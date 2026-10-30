using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
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

        internal static async Task<int> Run(string[] args, bool trustedSyntheticComposition)
        {
            Console.OutputEncoding = Encoding.UTF8;
            string dataDir = GetArg(args, "--data") ?? ConfigStore.DefaultDataDir();
            Directory.CreateDirectory(dataDir);

            // Jedna instancja na katalog danych – dwa procesy APEX nie mogą zarządzać tymi samymi kontami.
            FileStream lockFile;
            try { lockFile = new FileStream(Path.Combine(dataDir, "apex.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
                Console.Error.WriteLine("APEX Desktop już działa (katalog danych jest zablokowany). Zamknij drugą instancję.");
                return 2;
            }

            ConfigStore config;
            NavalRuntimeOptions? naval;
            try
            {
                config = new ConfigStore(dataDir) { TrustedSyntheticComposition = trustedSyntheticComposition };
                naval = NavalAccessPolicy.Load(config.Config.NavalRead);
            }
            catch (NavalAccessException ex)
            {
                Console.Error.WriteLine(ex.Code);
                lockFile.Dispose();
                return 2;
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or UnauthorizedAccessException or IOException)
            {
                Console.Error.WriteLine("HOST_CONFIG_INVALID");
                lockFile.Dispose();
                return 2;
            }
            var log = new AppLog(Path.Combine(dataDir, "logi"));
            var news = new NewsCalendar();
            var newsService = new NewsService(news, config, log);
            var accounts = new AccountManager(config, news, log);
            var bridge = new BridgeServer(accounts, config, log);
            int port = int.TryParse(GetArg(args, "--port"), out var p) ? p : config.Config.HttpPort;

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory, WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot") });
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Warning);
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));
            builder.Services.ConfigureHttpJsonOptions(o =>
            {
                o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
                o.SerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals;
            });
            var app = builder.Build();
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
                NavalReadEndpoints.MapNavalReadEndpoints(app, new NavalReadService(new NavalPublicationReader(naval.PublicationPath, naval.Scope), naval.Scope), naval.Access);

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

            using var cts = new CancellationTokenSource();
            bridge.StartTcp(cts.Token);
            newsService.Start(cts.Token);

            string url = $"http://127.0.0.1:{port}/";
            log.Info($"APEX Desktop {AccountManager.HostVersion} uruchomiony: {url}  (dane: {dataDir})");
            if (naval == null) log.Info($"Klucz parowania mostów: {config.Config.PairingKey}  | WebSocket: ws://127.0.0.1:{port}/bridge | TCP: 127.0.0.1:{config.Config.BridgeTcpPort}");
            else log.Info("Bridge configured.");
            log.Info("Handel na kontach LIVE: TRWALE WYŁĄCZONY (wersja DEMO).");
            if (config.Config.OpenBrowserOnStart && !args.Contains("--no-browser")) OpenBrowser(url);

            app.Lifetime.ApplicationStopping.Register(() =>
            {
                cts.Cancel();
                bridge.Stop();
                accounts.Dispose();
                log.Info("APEX Desktop zatrzymany.");
            });
            await app.RunAsync();
            lockFile.Dispose();
            return 0;
        }

        private static void OpenBrowser(string url)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // Tryb aplikacji przeglądarki Edge (okno bez pasków – wygląda jak osobna aplikacja); w razie braku – domyślna przeglądarka.
                    try { Process.Start(new ProcessStartInfo("msedge", $"--app={url}") { UseShellExecute = true }); }
                    catch (System.ComponentModel.Win32Exception) { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                }
            }
            catch (Exception) { /* brak przeglądarki nie zatrzymuje bota */ }
        }

        private static string? GetArg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
