using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Engine;
using Apex.Desktop.Bridge;
using Apex.Desktop.Runtime;
using Apex.Desktop.Services;
using Apex.Desktop.Naval;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Apex.Desktop
{
    public static class Program
    {
        public static Task<int> Main(string[] args) => Run(args, trustedSyntheticComposition: false);

        internal static Task<int> Run(string[] args, bool trustedSyntheticComposition) => Run(args, trustedSyntheticComposition, null);

        // Source-only native prompt adapter; production entry never supplies one.
        internal static async Task<int> Run(string[] args, bool trustedSyntheticComposition, Func<string, string, bool>? localAccessPrompt)
            => await Run(args, trustedSyntheticComposition, localAccessPrompt, null, CancellationToken.None, null);

        // Per-call source seams only; Main never supplies adapters or observations.
        internal static async Task<int> Run(string[] args, bool trustedSyntheticComposition,
            Func<string, string, bool>? localAccessPrompt, Action<ProcessStartInfo>? browserLauncher,
            CancellationToken cancellation, Action<string, object?>? startupObserver)
        {
            Console.OutputEncoding = Encoding.UTF8;
            string dataDir;
            WindowsPrivateDataLease? privateData = null;
            try
            {
                dataDir = GetArg(args, "--data") ?? ConfigStore.DefaultDataDir();
                if (OperatingSystem.IsWindows())
                {
                    var executableDirectory = new DirectoryInfo(AppContext.BaseDirectory);
                    string releaseRoot = executableDirectory.Name.Equals("APEX-Desktop", StringComparison.OrdinalIgnoreCase) ? executableDirectory.Parent!.FullName : executableDirectory.FullName;
                    privateData = WindowsPrivateData.Open(dataDir, releaseRoot);
                    dataDir = privateData.Path;
                }
                else Directory.CreateDirectory(dataDir);
            }
            catch (WindowsDataException ex)
            {
                Console.Error.WriteLine(ex.Code + ": Wybierz prywatny katalog na lokalnym dysku poza paczką; sprawdź właściciela i uprawnienia. Istniejących danych nie naprawiono ani nie usunięto.");
                return 2;
            }
            using var privateDataLease = privateData;

            // Jedna instancja na katalog danych – dwa procesy APEX nie mogą zarządzać tymi samymi kontami.
            FileStream lockFile;
            // Windows admission must still inspect the existing empty lock's ACL.
            // Permit reads only: a second ReadWrite owner and delete/replace remain
            // denied. Unix keeps FileShare.None for its exclusive flock semantics.
            try { lockFile = new FileStream(Path.Combine(dataDir, "apex.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, OperatingSystem.IsWindows() ? FileShare.Read : FileShare.None); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 or 11)
            {
                Console.Error.WriteLine("HOST_ALREADY_RUNNING: APEX już działa dla tego katalogu danych. Zamknij drugą instancję; nie otwarto przeglądarki.");
                return 2;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                Console.Error.WriteLine("HOST_DATA_UNAVAILABLE: Nie można otworzyć blokady prywatnego katalogu. Sprawdź uprawnienia, dysk i drugą instancję APEX; zachowaj istniejące dane.");
                return 2;
            }

            using var startupLock = lockFile;
            ConfigStore config;
            NavalRuntimeOptions? naval;
            bool desktopLocal = false;
            try
            {
                if (args.Contains("--show-local-access"))
                {
                    if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("HOST_LOCAL_SETUP_INCOMPLETE: Dostęp lokalny wymaga prywatnego profilu Windows."); return 2; }
                    privateData?.ValidateDirectories();
                    return WindowsLocalAccess.Show(dataDir, localAccessPrompt ?? WindowsLocalAccessPrompt.Show) ? 0 : 2;
                }
                if (OperatingSystem.IsWindows())
                {
                    privateData?.ValidateDirectories();
                    var setup = WindowsLocalSetup.Ensure(dataDir, Path.Combine(AppContext.BaseDirectory, "presentation"));
                    config = setup.Store;
                    privateData?.ValidateDirectories();
                    // Runtime-only capability from complete validated Windows presentation
                    // state; never inferred from a client header or arbitrary custom config.
                    desktopLocal = setup.DesktopLocal;
                }
                else config = new ConfigStore(dataDir);
                config.TrustedSyntheticComposition = trustedSyntheticComposition;
                naval = NavalAccessPolicy.Load(config.Config.NavalRead, desktopLocal);
            }
            catch (WindowsDataException ex)
            {
                Console.Error.WriteLine(ex.Code + ": Sprawdź prywatny katalog i uprawnienia; zachowaj istniejące dane.");
                return 2;
            }
            catch (NavalAccessException ex)
            {
                Console.Error.WriteLine(ex.Code);
                return 2;
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or UnauthorizedAccessException or IOException)
            {
                Console.Error.WriteLine("HOST_CONFIG_INVALID");
                return 2;
            }
            int port = config.Config.HttpPort;
            int portIndex = Array.IndexOf(args, "--port");
            if ((portIndex >= 0 && !int.TryParse(GetArg(args, "--port"), out port)) || port is < 1 or > 65535)
            {
                Console.Error.WriteLine("HOST_HTTP_PORT_INVALID: Port HTTP musi być liczbą od 1 do 65535. Nie uruchomiono serwera ani przeglądarki.");
                return 2;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            NewsService? newsService = null;
            AccountManager? accounts = null;
            BridgeServer? bridge = null;
            WebApplication? app = null;
            CancellationTokenRegistration stopping = default;
            void StopBackground()
            {
                try { cts.Cancel(); }
                finally { bridge?.Stop(); }
            }
            try
            {
                cancellation.ThrowIfCancellationRequested();
                var log = new AppLog(Path.Combine(dataDir, "logi"));
                var news = new NewsCalendar();
                newsService = new NewsService(news, config, log);
                accounts = new AccountManager(config, news, log);
                startupObserver?.Invoke("accounts", accounts);
                bridge = new BridgeServer(accounts, config, log);
                startupObserver?.Invoke("bridge", bridge);

                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory, WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot") });
                builder.Logging.ClearProviders();
                builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Warning);
                // Start failures are translated below; never dump native exception details.
                builder.Logging.AddFilter("Microsoft.Extensions.Hosting.Internal.Host", LogLevel.None);
                builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));
                builder.Services.ConfigureHttpJsonOptions(o =>
                {
                    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
                    o.SerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals;
                });
                app = builder.Build();
                stopping = app.Lifetime.ApplicationStopping.Register(() =>
                {
                    try { StopBackground(); }
                    finally { accounts.Dispose(); }
                });
                startupObserver?.Invoke("app", app);
                app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

                string token = config.Config.ApiToken;
                string[] allowedHosts = { $"127.0.0.1:{port}", $"localhost:{port}" };

                // Bezpieczeństwo: tylko lokalny host (ochrona przed DNS rebinding), token API, nagłówki ochronne.
                app.Use(async (ctx, next) =>
                {
                    var h = ctx.Response.Headers;
                    h["X-Content-Type-Options"] = "nosniff";
                    h["X-Frame-Options"] = "DENY";
                    h["Referrer-Policy"] = "no-referrer";
                    h["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
                    if (naval != null) h["Cache-Control"] = "no-store";
                    string host = ctx.Request.Host.Value ?? "";
                    if (!allowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
                    {
                        await NavalAccessPolicy.Error(ctx, 403);
                        return;
                    }
                    if (naval != null && !await naval.Access.Enter(ctx)) return;
                    if (ctx.Request.Path.StartsWithSegments("/api") && (naval == null || !ctx.Request.Path.StartsWithSegments("/api/naval")))
                    {
                        string got = ctx.Request.Headers["X-Apex-Token"].ToString();
                        var a = Encoding.UTF8.GetBytes(got);
                        var b = Encoding.UTF8.GetBytes(token);
                        if (naval == null && (a.Length != b.Length || !CryptographicOperations.FixedTimeEquals(a, b)))
                        {
                            ctx.Response.StatusCode = 401;
                            return;
                        }
                        if (HttpMethods.IsPost(ctx.Request.Method) && !(ctx.Request.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                        {
                            ctx.Response.StatusCode = 415;
                            return;
                        }
                        h["Cache-Control"] = "no-store";
                    }
                    await next();
                });

                app.Map("/bridge", async ctx =>
                {
                    if (naval != null && ctx.Request.Path.Value != "/bridge") { ctx.Response.StatusCode = 404; return; }
                    await bridge.HandleWebSocketAsync(ctx);
                });

                string indexPath = Path.Combine(app.Environment.WebRootPath, "index.html");
                async Task RenderIndex(HttpContext ctx)
                {
                    // Enter has already applied the unchanged Operator document policy.
                    // Compare the literal nonsecret query, never reflect a target from input.
                    if (naval != null && ctx.Request.Path.Value == "/" && ctx.Request.QueryString.Value == "?view=naval")
                    { ctx.Response.Redirect("/naval/"); return; }
                    string html = await File.ReadAllTextAsync(indexPath);
                    html = html.Replace("__APEX_TOKEN__", naval == null ? token : "");
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    ctx.Response.Headers["Cache-Control"] = "no-store";
                    await ctx.Response.WriteAsync(html);
                }
                app.MapGet("/", RenderIndex);
                if (naval != null) app.MapGet("/index.html", RenderIndex);
                app.UseStaticFiles();
                if (naval != null)
                {
                    NavalReadEndpoints.MapNavalReadEndpoints(app, new NavalReadService(new NavalPublicationReader(naval.PublicationPath, naval.Scope), naval.Scope), naval.Access);
                    NavalFrontendEndpoints.MapNavalFrontendEndpoints(app);
                }

                app.MapGet("/api/status", () => Results.Json(new
                {
                    host = new
                    {
                        version = AccountManager.HostVersion,
                        time = DateTime.UtcNow,
                        dataDir,
                        pairingKey = config.Config.PairingKey,
                        wsUrl = $"ws://127.0.0.1:{port}/bridge",
                        tcpPort = config.Config.BridgeTcpPort,
                        liveFeature = false,
                        label = config.Config.Label,
                        news = new
                        {
                            enabled = config.Config.News.Enabled,
                            updated = news.LastUpdateUtc == DateTime.MinValue ? (DateTime?)null : news.LastUpdateUtc,
                            count = news.Events.Count,
                            error = newsService.LastError,
                            upcoming = news.Upcoming(DateTime.UtcNow, 8, config.Config.News.MinImpact).Select(e => new { e.TimeUtc, e.Currency, e.Title, impact = e.Impact.ToString() }).ToList(),
                        },
                    },
                    data = accounts.Snapshot(),
                    log = log.Recent(40),
                }));

                app.MapPost("/api/accounts/{key}/command", async (string key, HttpContext ctx) =>
                {
                    var rt = accounts.Get(key);
                    if (rt == null) return Results.NotFound(new { message = "Nieznane konto." });
                    JsonDocument doc;
                    try { doc = await JsonDocument.ParseAsync(ctx.Request.Body); }
                    catch (JsonException) { return Results.BadRequest(new { message = "Nieprawidłowe dane." }); }
                    using (doc)
                    {
                        string action = doc.RootElement.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
                        var argsEl = doc.RootElement.TryGetProperty("args", out var ar) ? ar.Clone() : default;
                        try
                        {
                            string msg = await rt.Command(e => rt.ExecuteAction(e, action, argsEl), ctx.RequestAborted);
                            log.Info($"[{key}] polecenie '{action}': {msg}");
                            return Results.Json(new { message = msg });
                        }
                        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
                        {
                            return Results.StatusCode(499);
                        }
                        catch (TimeoutException)
                        {
                            return Results.Json(new { message = "Polecenie anulowane przed wykonaniem – przekroczono czas oczekiwania." }, statusCode: 504);
                        }
                    }
                });

                // AI Decision Center – wyłącznie odczyt (brak jakichkolwiek poleceń handlowych).
                app.MapGet("/api/accounts/{key}/decisions", (string key, long? since) =>
                {
                    var rt = accounts.Get(key);
                    return rt == null ? Results.NotFound() : Results.Json(rt.DecisionsSnapshot(since ?? 0));
                });
                app.MapGet("/api/accounts/{key}/instrument/{symbol}", (string key, string symbol) =>
                {
                    var rt = accounts.Get(key);
                    var d = rt?.InstrumentDetail(symbol);
                    return d == null ? Results.NotFound() : Results.Json(d);
                });

                app.MapPost("/api/pairing/regenerate", () =>
                {
                    config.Update(c => c.PairingKey = ConfigStore.NewPairingKey());
                    log.Warn("Wygenerowano nowy klucz parowania – istniejące połączenia pozostają aktywne, nowe wymagają nowego klucza.");
                    return Results.Json(new { message = "Nowy klucz parowania: " + config.Config.PairingKey });
                });

                app.MapPost("/api/news/refresh", async () =>
                {
                    await newsService.RefreshAsync(CancellationToken.None);
                    return Results.Json(new { message = string.IsNullOrEmpty(newsService.LastError) ? $"Kalendarz: {news.Events.Count} wydarzeń." : "Błąd: " + newsService.LastError });
                });

                app.MapGet("/api/accounts/{key}/journal.csv", (string key) =>
                {
                    var rt = accounts.Get(key);
                    if (rt == null) return Results.NotFound();
                    var lines = new[] { Apex.Core.Performance.TradeRecord.CsvHeader }.Concat(rt.Journal.AllRecent().Select(t => t.ToCsv()));
                    return Results.Text(string.Join("\n", lines), "text/csv; charset=utf-8");
                });

                try { await app.StartAsync(cancellation); }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { return 0; }
                catch (Exception)
                {
                    Console.Error.WriteLine("HOST_HTTP_BIND_FAILED: Nie można uruchomić lokalnego HTTP. Sprawdź port i drugą instancję APEX. Nie otwarto przeglądarki ani innego portu.");
                    return 2;
                }
                startupObserver?.Invoke("http", app);
                cts.Token.ThrowIfCancellationRequested();
                bridge.StartTcp(cts.Token);
                newsService.Start(cts.Token);
                startupObserver?.Invoke("services", newsService);
                cts.Token.ThrowIfCancellationRequested();

                var uri = DesktopBrowser.BuildUri(port, naval != null);
                log.Info($"APEX Desktop {AccountManager.HostVersion} uruchomiony: {uri.AbsoluteUri}  (dane: {dataDir})");
                log.Info($"Bridge configured. WebSocket: ws://127.0.0.1:{port}/bridge | TCP: 127.0.0.1:{config.Config.BridgeTcpPort}");
                log.Info("Handel na kontach LIVE: TRWALE WYŁĄCZONY (wersja DEMO).");
                if (OperatingSystem.IsWindows() && config.Config.OpenBrowserOnStart && !args.Contains("--no-browser") && !cts.IsCancellationRequested)
                {
                    if (!DesktopBrowser.TryOpen(uri, browserLauncher))
                        Console.Error.WriteLine($"Nie można otworzyć przeglądarki. Serwer działa; otwórz ręcznie {uri.AbsoluteUri}");
                }
                await app.WaitForShutdownAsync(cancellation);
                log.Info("APEX Desktop zatrzymany. Zamknięcie aplikacji nie zastępuje finansowego STOP.");
                return 0;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { return 0; }
            catch (Exception)
            {
                Console.Error.WriteLine("HOST_START_FAILED: Nie można uruchomić aplikacji. Zachowano konfigurację; sprawdź prywatny katalog, zasoby i porty.");
                return 2;
            }
            finally
            {
                try { StopBackground(); }
                finally
                {
                    try { if (app != null) await app.StopAsync(CancellationToken.None); }
                    finally
                    {
                        try { accounts?.Dispose(); }
                        finally
                        {
                            try { if (newsService != null) await newsService.StopAsync(); }
                            finally
                            {
                                try { if (app != null) await app.DisposeAsync(); }
                                finally { stopping.Dispose(); }
                            }
                        }
                    }
                }
            }
        }

        private static string? GetArg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
