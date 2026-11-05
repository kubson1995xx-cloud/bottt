using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Apex.AiLab.Analysis;
using Apex.AiLab.Deploy;
using Apex.AiLab.Lab;
using Apex.AiLab.Pipeline;
using Apex.AiLab.Providers;
using Apex.AiLab.Scheduling;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Apex.AiLab
{
    /// <summary>
    /// APEX AI Lab – osobny proces nasłuchujący na 127.0.0.1 (domyślnie 5079). Panel znajduje się w APEX Desktop (zakładka
    /// „AI &amp; Auto-Updates”), który przekazuje zapytania z tokenem. AI Lab czyta dane z APEX Desktop wyłącznie w trybie odczytu.
    /// </summary>
    public static class Program
    {
        public const string Version = "1.0.0";

        public static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            string apexData = GetArg(args, "--data") ?? DefaultApexDataDir();
            string labDir = Path.Combine(apexData, "ailab");
            Directory.CreateDirectory(labDir);

            FileStream lockFile;
            try { lockFile = new FileStream(Path.Combine(labDir, "ailab.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
                Console.Error.WriteLine("APEX AI Lab już działa (katalog danych jest zablokowany).");
                return 2;
            }

            var cfg = new LabConfigStore(labDir);
            if (int.TryParse(GetArg(args, "--port"), out int portArg)) cfg.Update(c => c.HttpPort = portArg);
            int port = cfg.Config.HttpPort;
            // Ścieżka programu – APEX Desktop uruchamia AI Lab na żądanie z panelu.
            try { File.WriteAllText(Path.Combine(labDir, "ailab.path"), SelfPath()); } catch (IOException) { }

            // Opcjonalne adresy API (np. serwer pośredniczący firmy lub lokalny serwer testowy).
            string? openAiUrl = Environment.GetEnvironmentVariable("APEX_AILAB_OPENAI_URL");
            string? anthropicUrl = Environment.GetEnvironmentVariable("APEX_AILAB_ANTHROPIC_URL");
            if (!string.IsNullOrWhiteSpace(openAiUrl)) OpenAiApiProvider.Endpoint = openAiUrl!;
            if (!string.IsNullOrWhiteSpace(anthropicUrl)) AnthropicApiProvider.Endpoint = anthropicUrl!;

            var store = new LabStore(labDir);
            var desktop = new DesktopClient(apexData);
            var analyzer = new Analyzer(desktop, store);
            var ws = new GitWorkspace(labDir);
            var budget = new Budget(store, () => cfg.Config);
            var providers = new ProviderRegistry(cfg);
            var baseline = new BaselineCache(labDir);
            var runner = new JobRunner(cfg, store, analyzer, ws, budget, providers, baseline);
            var scheduler = new Scheduler(cfg, store, analyzer, runner);
            var deployer = new Deployer(cfg, store, ws, apexData);
            var statusCache = new StatusCache(providers);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Warning);
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));
            builder.Services.ConfigureHttpJsonOptions(o =>
            {
                o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
                o.SerializerOptions.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
            });
            var app = builder.Build();
            string[] allowedHosts = { $"127.0.0.1:{port}", $"localhost:{port}" };

            app.Use(async (ctx, next) =>
            {
                if (!allowedHosts.Contains(ctx.Request.Host.Value ?? "", StringComparer.OrdinalIgnoreCase)) { ctx.Response.StatusCode = 403; return; }
                ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
                ctx.Response.Headers["Cache-Control"] = "no-store";
                if (ctx.Request.Path.StartsWithSegments("/api") && !ctx.Request.Path.StartsWithSegments("/api/health"))
                {
                    var a = Encoding.UTF8.GetBytes(ctx.Request.Headers["X-Lab-Token"].ToString());
                    var b = Encoding.UTF8.GetBytes(cfg.Config.ApiToken);
                    if (a.Length != b.Length || !CryptographicOperations.FixedTimeEquals(a, b)) { ctx.Response.StatusCode = 401; return; }
                    if (HttpMethods.IsPost(ctx.Request.Method) && !(ctx.Request.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase)) { ctx.Response.StatusCode = 415; return; }
                }
                await next();
            });

            app.MapGet("/", () => Results.Text("APEX AI Lab " + Version + " działa. Panel: APEX Desktop → zakładka „AI & Auto-Updates”.", "text/plain; charset=utf-8"));
            app.MapGet("/api/health", () => Results.Json(new { ok = true, version = Version }));

            app.MapGet("/api/state", async (CancellationToken ct) =>
            {
                var now = DateTime.UtcNow;
                var c = cfg.Config;
                var st = await statusCache.GetAsync(false, ct);
                var usage = budget.Compute(now);
                var latest = store.RecentAnalyses(1).FirstOrDefault();
                var hist = deployer.History();
                return Results.Json(new
                {
                    version = Version,
                    time = now,
                    running = runner.RunningJobId,
                    schedulerMessage = scheduler.LastMessage,
                    nextRunUtc = scheduler.NextScheduledUtc(now),
                    lastScheduledRunUtc = store.LastScheduledRunUtc,
                    lastAgentRunUtc = store.LastAgentRunUtc,
                    lastSnapshotUtc = store.LastSnapshotUtc,
                    snapshotCount = store.Snapshots().Count,
                    holdoutUses = store.HoldoutUses,
                    config = PublicConfig(c),
                    providers = st.statuses,
                    providersCheckedUtc = st.checkedUtc,
                    prerequisites = st.prerequisites,
                    codexLogin = providers.Codex.Login,
                    usage = new
                    {
                        usage.RunsToday, usage.RunsWeek, usage.TokensToday, usage.TokensWeek, usage.TokensMonth, usage.CostMonthUsd,
                        usage.TokensByProviderMonth, usage.RunsByProviderMonth,
                        daily = usage.Daily.Select(d => new { day = d.day, d.tokens, d.runs }),
                    },
                    limits = Enum.GetValues(typeof(ProviderId)).Cast<ProviderId>().Select(store.Limit).Where(l => l != null && l.LimitedUntilUtc > now),
                    jobs = store.Jobs().Take(60).Select(JobSummary),
                    analysis = latest == null ? null : new { latest.Id, latest.TimeUtc, latest.DesktopReachable, latest.DataNote, latest.Findings, latest.MarketSummary, latest.TotalTrades, latest.Strategies },
                    releases = new { hist.Current, list = hist.Releases.OrderByDescending(r => r.CreatedUtc).Take(20) },
                    phrases = new { protectedChanges = Deployer.ProtectedPhrase, warnings = Deployer.WarningPhrase },
                    modelSuggestions = ModelSuggestions,
                });
            });

            app.MapPost("/api/providers/refresh", async (CancellationToken ct) =>
            {
                await statusCache.GetAsync(true, ct);
                return Results.Json(new { message = "Sprawdzono połączenia AI." });
            });

            app.MapPost("/api/providers/{id}/login", async (string id, HttpContext ctx) =>
            {
                var body = await ReadBody(ctx);
                string mode = Str(body, "mode");
                if (!Enum.TryParse<ProviderId>(id, true, out var pid)) return Results.BadRequest(new { message = "Nieznany dostawca." });
                string msg = pid switch
                {
                    ProviderId.Codex => mode == "device" ? providers.Codex.StartDeviceLogin() : providers.Codex.StartBrowserLogin(),
                    ProviderId.ClaudeCode => providers.Claude.StartLogin(mode == "console"),
                    _ => "Dla dostawców API wklej klucz API.",
                };
                statusCache.Invalidate();
                return Results.Json(new { message = msg });
            });

            app.MapPost("/api/providers/{id}/logout", async (string id, CancellationToken ct) =>
            {
                if (!Enum.TryParse<ProviderId>(id, true, out var pid)) return Results.BadRequest(new { message = "Nieznany dostawca." });
                string msg;
                switch (pid)
                {
                    case ProviderId.Codex: msg = await providers.Codex.LogoutAsync(ct); break;
                    case ProviderId.ClaudeCode: msg = await providers.Claude.LogoutAsync(ct); break;
                    default:
                        cfg.Update(c => c.Provider(pid).ApiKeyProtected = null);
                        msg = "Usunięto klucz API.";
                        break;
                }
                statusCache.Invalidate();
                return Results.Json(new { message = msg });
            });

            app.MapPost("/api/providers/{id}/apikey", async (string id, HttpContext ctx, CancellationToken ct) =>
            {
                var body = await ReadBody(ctx);
                string key = Str(body, "key").Trim();
                if (!Enum.TryParse<ProviderId>(id, true, out var pid)) return Results.BadRequest(new { message = "Nieznany dostawca." });
                if (key.Length < 20 || key.Length > 400 || key.Any(char.IsWhiteSpace)) return Results.BadRequest(new { message = "Nieprawidłowy klucz API." });
                string msg;
                if (pid == ProviderId.Codex) msg = await providers.Codex.LoginWithApiKeyAsync(key, ct);
                else if (pid == ProviderId.ClaudeCode) msg = "Claude Code: użyj „Zaloguj – Anthropic Console” (rozliczenie API) – klucz nie jest zapisywany przez AI Lab.";
                else
                {
                    cfg.Update(c => c.Provider(pid).ApiKeyProtected = Secrets.Protect(key));
                    msg = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
                        ? "Klucz zapisany (zaszyfrowany Windows DPAPI – tylko dla Twojego konta)."
                        : "Klucz zapisany w pliku konfiguracji z uprawnieniami tylko dla Twojego konta (szyfrowanie DPAPI dostępne tylko w Windows).";
                }
                statusCache.Invalidate();
                return Results.Json(new { message = msg });
            });

            app.MapPost("/api/config", async (HttpContext ctx) =>
            {
                var body = await ReadBody(ctx);
                if (body == null) return Results.BadRequest(new { message = "Nieprawidłowe dane." });
                var (ok, message) = SaveConfigPatch(cfg, body.Value);
                if (!ok) return Results.BadRequest(new { message });
                SetAutostartFlag(labDir, cfg.Config.Autostart);
                statusCache.Invalidate();
                return Results.Json(new { message });
            });

            app.MapPost("/api/baseline/clear", () =>
            {
                if (runner.RunningJobId != null) return Results.Json(new { message = "Zadanie jest w toku – wyczyść pamięć wersji bazowej po jego zakończeniu." }, statusCode: 409);
                int n = baseline.Clear();
                return Results.Json(new { message = $"Wyczyszczono pamięć wyników wersji bazowej ({n} wpisów) – kolejne zadanie policzy je od nowa." });
            });

            app.MapPost("/api/run", async (HttpContext ctx) =>
            {
                var body = await ReadBody(ctx);
                string mode = Str(body, "mode");
                ProviderId? pid = Enum.TryParse<ProviderId>(Str(body, "provider"), true, out var p) ? p : null;
                string? id = runner.Start(TriggerKind.Manual, mode == "agent" ? "ręczne uruchomienie agenta" : "ręczna analiza", mode == "agent", pid);
                return id == null
                    ? Results.Json(new { message = "Inne zadanie jest w toku – poczekaj na jego zakończenie." }, statusCode: 409)
                    : Results.Json(new { message = mode == "agent" ? "Rozpoczęto zadanie agenta." : "Rozpoczęto analizę.", id });
            });

            app.MapGet("/api/jobs/{id}", (string id) =>
            {
                var job = store.Job(id);
                if (job == null) return Results.NotFound();
                var analysis = job.AnalysisId != null ? store.Analysis(job.AnalysisId) : null;
                string diffPath = Path.Combine(store.JobDir(id), "zmiana.diff");
                string diff = File.Exists(diffPath) ? File.ReadAllText(diffPath) : "";
                bool truncated = diff.Length > 400_000;
                return Results.Json(new
                {
                    job,
                    analysisMarkdown = analysis?.Markdown,
                    diff = truncated ? diff.Substring(0, 400_000) : diff,
                    diffTruncated = truncated,
                    requiredPhrase = Deployer.RequiredPhrase(job),
                    task = File.Exists(Path.Combine(store.JobDir(id), "zadanie.md")) ? File.ReadAllText(Path.Combine(store.JobDir(id), "zadanie.md")) : null,
                });
            });

            app.MapGet("/api/jobs/{id}/events", (string id, long? since) =>
            {
                if (runner.RunningJobId == id)
                {
                    var (ev, last) = store.EventsSince(since ?? 0, id);
                    return Results.Json(new { events = ev, last, live = true });
                }
                // Zadanie zakończone: pełny dziennik z dysku przy pierwszym odczycie, potem nic nowego.
                var all = store.JobEvents(id);
                return Results.Json(new { events = since is null or 0 ? all : new List<LabEvent>(), last = all.Count > 0 ? all[^1].Seq : 0, live = false });
            });

            app.MapGet("/api/events", (long? since) =>
            {
                var (ev, last) = store.EventsSince(since ?? 0);
                return Results.Json(new { events = ev, last, running = runner.RunningJobId });
            });

            app.MapPost("/api/jobs/{id}/cancel", (string id) =>
                Results.Json(new { message = runner.Cancel(id) ? "Przerywanie zadania…" : "Zadanie nie jest w toku." }));

            app.MapPost("/api/jobs/{id}/approve", async (string id, HttpContext ctx, CancellationToken ct) =>
            {
                var body = await ReadBody(ctx);
                string msg = await deployer.ApproveAsync(id, Str(body, "confirmation"), Str(body, "note"), CancellationToken.None);
                return Results.Json(new { message = msg });
            });

            app.MapPost("/api/jobs/{id}/reject", async (string id, HttpContext ctx, CancellationToken ct) =>
            {
                var body = await ReadBody(ctx);
                return Results.Json(new { message = await deployer.RejectAsync(id, Str(body, "note"), ct) });
            });

            app.MapPost("/api/jobs/{id}/export", async (string id, CancellationToken ct) =>
            {
                var job = store.Job(id);
                if (job == null || string.IsNullOrEmpty(job.HeadCommit)) return Results.Json(new { message = "Brak zmiany do wyeksportowania." });
                string file = Path.Combine(store.JobDir(id), "zmiana.patch");
                await ws.ExportPatchAsync(id, job.BaseCommit, file, ct);
                return Results.Json(new { message = "Łatka zapisana: " + file, path = file });
            });

            app.MapPost("/api/jobs/{id}/push-source", async (string id, CancellationToken ct) =>
            {
                var job = store.Job(id);
                if (job == null || string.IsNullOrEmpty(job.HeadCommit)) return Results.Json(new { message = "Brak zmiany do wypchnięcia." });
                var (ok, msg) = await ws.PushBranchToSourceAsync(id, ct);
                return Results.Json(new { message = msg, ok });
            });

            app.MapPost("/api/releases/rollback", () => Results.Json(new { message = deployer.Rollback() }));

            app.MapGet("/api/analyses/{id}", (string id) =>
            {
                var a = store.Analysis(id);
                return a == null ? Results.NotFound() : Results.Json(a);
            });

            using var cts = new CancellationTokenSource();
            scheduler.Start(cts.Token);
            Console.WriteLine($"APEX AI Lab {Version}: http://127.0.0.1:{port}/  (dane: {labDir})");
            await app.RunAsync();
            cts.Cancel();
            lockFile.Dispose();
            return 0;
        }

        // ------------------------------------------------------------------ pomocnicze

        private static readonly Dictionary<string, string[]> ModelSuggestions = new()
        {
            // Codex: pusty = model domyślny narzędzia (zalecane). Nazwy z listy modeli Codex CLI – dostępność zależy od planu.
            ["Codex"] = new[] { "", "gpt-6.1-sol", "gpt-6-sol", "gpt-6-luna" },
            // Claude Code: aliasy narzędzia lub pełne nazwy modeli.
            ["ClaudeCode"] = new[] { "", "opus", "sonnet", "fable" },
            ["AnthropicApi"] = new[] { "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5", "claude-fable-5-1" },
            ["OpenAiApi"] = Array.Empty<string>(),
        };

        private static object PublicConfig(LabConfig c) => new
        {
            c.AutomationEnabled,
            c.Autostart,
            c.PrimaryProvider,
            c.FallbackProvider,
            c.ReviewProvider,
            providers = c.Providers.ToDictionary(kv => kv.Key.ToString(), kv => new
            {
                kv.Value.Enabled, kv.Value.Model, kv.Value.TimeoutMinutes, kv.Value.ExecutablePath, kv.Value.PriceInputPerMTok, kv.Value.PriceOutputPerMTok,
                kv.Value.MaxBudgetUsdPerRun, kv.Value.MaxOutputTokens, kv.Value.MaxRepairRounds, hasKey = !string.IsNullOrEmpty(kv.Value.ApiKeyProtected),
            }),
            c.Schedule,
            c.Budget,
            c.Repo,
            policy = new { c.Policy.ProtectedPaths, c.Policy.ForbiddenPaths },
        };

        private static object JobSummary(LabJob j) => new
        {
            j.Id, j.CreatedUtc, j.FinishedUtc, j.Trigger, j.TriggerDetail, j.Title, j.Status, j.StatusText, j.Provider, j.Model, j.ProtectedTouched,
            files = j.Files.Count,
            added = j.Files.Sum(f => f.Added),
            removed = j.Files.Sum(f => f.Removed),
            tests = j.Tests == null ? null : new { j.Tests.Passed, j.Tests.Total, j.Tests.Failed },
            backtest = j.Backtest?.Verdict,
            tokens = j.Usage.TotalTokens,
            cost = j.Usage.CostUsd,
            stages = j.Stages.Select(s => new { s.Stage, s.Status }),
            version = j.Deployment?.Version,
        };

        /// <summary>
        /// Zapis zmian ustawień z panelu. Wyłącznik bezpieczeństwa (tylko automationEnabled=false, ewentualnie z harmonogramem „Wyłączone”)
        /// jest zapisywany ZAWSZE – nie mogą go zablokować niezwiązane błędy walidacji (np. nieistniejący katalog danych).
        /// Zwraca (ok, komunikat PL).
        /// </summary>
        public static (bool ok, string message) SaveConfigPatch(LabConfigStore cfg, JsonElement body)
        {
            if (body.ValueKind != JsonValueKind.Object) return (false, "Nieprawidłowe dane.");
            if (IsSafetyOnlyPatch(body, out bool scheduleOff))
            {
                cfg.Update(c =>
                {
                    c.AutomationEnabled = false;
                    if (scheduleOff) c.Schedule.Kind = ScheduleKind.Off;
                });
                return (true, "Automatyzacja wyłączona.");
            }
            var copy = JsonSerializer.Deserialize<LabConfig>(JsonSerializer.Serialize(cfg.Config, LabConfigStore.Json), LabConfigStore.Json)!;
            string? err = ApplyPatch(copy, body);
            if (err != null) return (false, err);
            var errors = copy.Validate();
            if (errors.Count > 0) return (false, string.Join(" ", errors));
            bool enabling = copy.AutomationEnabled && !cfg.Config.AutomationEnabled;
            cfg.Update(c =>
            {
                c.AutomationEnabled = copy.AutomationEnabled;
                c.Autostart = copy.Autostart;
                c.PrimaryProvider = copy.PrimaryProvider;
                c.FallbackProvider = copy.FallbackProvider;
                c.ReviewProvider = copy.ReviewProvider;
                foreach (var kv in copy.Providers)
                {
                    var key = c.Provider(kv.Key).ApiKeyProtected; // klucz zmieniany tylko osobnym poleceniem
                    c.Providers[kv.Key] = kv.Value;
                    kv.Value.ApiKeyProtected = key;
                }
                var since = enabling ? DateTime.UtcNow : c.Schedule.EnabledSinceUtc;
                c.Schedule = copy.Schedule;
                c.Schedule.EnabledSinceUtc = since;
                c.Budget = copy.Budget;
                c.Repo = copy.Repo;
            });
            return (true, "Zapisano ustawienia AI Lab.");
        }

        /// <summary>
        /// Czy zmiana wyłącznie zatrzymuje automatyzację: { automationEnabled: false } i/lub { schedule: { kind: "Off" } } bez innych pól.
        /// Pozostałe pola harmonogramu nie są wtedy zmieniane.
        /// </summary>
        private static bool IsSafetyOnlyPatch(JsonElement body, out bool scheduleOff)
        {
            scheduleOff = false;
            bool any = false;
            foreach (var p in body.EnumerateObject())
            {
                if (p.NameEquals("automationEnabled") && p.Value.ValueKind == JsonValueKind.False) { any = true; continue; }
                if (p.NameEquals("schedule") && p.Value.ValueKind == JsonValueKind.Object && p.Value.EnumerateObject().All(x => x.NameEquals("kind")) &&
                    p.Value.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String && string.Equals(k.GetString(), "Off", StringComparison.OrdinalIgnoreCase))
                {
                    scheduleOff = true;
                    any = true;
                    continue;
                }
                return false;
            }
            return any;
        }

        /// <summary>
        /// Stosuje zmiany ustawień z panelu (tylko dozwolone sekcje; zasady chronione zmienia się wyłącznie w pliku).
        /// Ścieżki repozytorium i danych są sprawdzane tylko wtedy, gdy zmiana zawiera sekcję "repo" – zapis innych sekcji
        /// (np. wyłączenie automatyzacji, harmonogram) nie może być blokowany przez niezwiązany, np. odłączony, katalog.
        /// </summary>
        public static string? ApplyPatch(LabConfig c, JsonElement body)
        {
            try
            {
                if (body.TryGetProperty("automationEnabled", out var ae)) c.AutomationEnabled = ae.GetBoolean();
                if (body.TryGetProperty("autostart", out var au)) c.Autostart = au.GetBoolean();
                if (body.TryGetProperty("primaryProvider", out var pp)) c.PrimaryProvider = Enum.Parse<ProviderId>(pp.GetString()!, true);
                if (body.TryGetProperty("fallbackProvider", out var fp)) c.FallbackProvider = ParseOpt(fp);
                if (body.TryGetProperty("reviewProvider", out var rp)) c.ReviewProvider = ParseOpt(rp);
                if (body.TryGetProperty("providers", out var prov) && prov.ValueKind == JsonValueKind.Object)
                    foreach (var p in prov.EnumerateObject())
                    {
                        if (!Enum.TryParse<ProviderId>(p.Name, true, out var id)) continue;
                        var s = JsonSerializer.Deserialize<ProviderSettings>(p.Value.GetRawText(), LabConfigStore.Json);
                        if (s != null) c.Providers[id] = s;
                    }
                if (body.TryGetProperty("schedule", out var sc)) c.Schedule = JsonSerializer.Deserialize<ScheduleSettings>(sc.GetRawText(), LabConfigStore.Json) ?? c.Schedule;
                if (body.TryGetProperty("budget", out var bu)) c.Budget = JsonSerializer.Deserialize<BudgetSettings>(bu.GetRawText(), LabConfigStore.Json) ?? c.Budget;
                if (body.TryGetProperty("repo", out var re))
                {
                    c.Repo = JsonSerializer.Deserialize<RepoSettings>(re.GetRawText(), LabConfigStore.Json) ?? c.Repo;
                    if (!string.IsNullOrWhiteSpace(c.Repo.SourceRepoPath) && !Directory.Exists(Path.Combine(c.Repo.SourceRepoPath, ".git")))
                        return "Repozytorium źródłowe: wskazany folder nie zawiera katalogu .git.";
                    if (!string.IsNullOrWhiteSpace(c.Repo.BacktestDataDir) && !Directory.Exists(c.Repo.BacktestDataDir))
                        return "Katalog danych do backtestu nie istnieje.";
                    if (c.Repo.BaseBranch.Any(ch => char.IsWhiteSpace(ch) || ch is '~' or '^' or ':' or '?' or '*' or '['))
                        return "Nieprawidłowa nazwa gałęzi bazowej.";
                }
                return null;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or FormatException)
            {
                return "Nieprawidłowe ustawienia: " + ex.Message;
            }

            static ProviderId? ParseOpt(JsonElement e) =>
                e.ValueKind == JsonValueKind.String && Enum.TryParse<ProviderId>(e.GetString(), true, out var v) ? v : null;
        }

        private static void SetAutostartFlag(string labDir, bool on)
        {
            string flag = Path.Combine(labDir, "autostart");
            try
            {
                if (on) File.WriteAllText(flag, "1");
                else if (File.Exists(flag)) File.Delete(flag);
            }
            catch (IOException) { }
        }

        private static async Task<JsonElement?> ReadBody(HttpContext ctx)
        {
            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
                return doc.RootElement.Clone();
            }
            catch (JsonException) { return null; }
        }

        private static string Str(JsonElement? e, string name) =>
            e is { ValueKind: JsonValueKind.Object } v && v.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

        private static string? GetArg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        /// <summary>Ścieżka do uruchomienia AI Lab: plik exe albo (gdy uruchomiono przez "dotnet") plik dll.</summary>
        private static string SelfPath()
        {
            string? proc = Environment.ProcessPath;
            string name = Path.GetFileNameWithoutExtension(proc ?? "");
            if (proc == null || name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(AppContext.BaseDirectory, "ApexAiLab.dll");
            return proc;
        }

        public static string DefaultApexDataDir()
        {
            string? env = Environment.GetEnvironmentVariable("APEX_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(env)) return env!;
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDir)) baseDir = AppContext.BaseDirectory;
            return Path.Combine(baseDir, "APEX Trading AI");
        }
    }

    /// <summary>
    /// Pamięć podręczna stanu dostawców i narzędzi (sprawdzanie uruchamia procesy – nie przy każdym odświeżeniu panelu).
    /// Sprawdzanie ma własny token i limit czasu: przerwane zapytanie panelu (przeładowanie strony, zamknięcie APEX Desktop)
    /// nie zapisuje fałszywego „brak / niezalogowany”. Wynik niepewny (przekroczony czas) jest ponawiany po 30 s.
    /// </summary>
    public sealed class StatusCache
    {
        private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan RetryInconclusive = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan RefreshTimeout = TimeSpan.FromMinutes(2);

        private readonly ProviderRegistry _providers;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private List<ProviderStatus> _statuses = new();
        // Słownik po opublikowaniu nie jest zmieniany (odświeżenie tworzy nowy) – szybka ścieżka czyta go bez blokady.
        private Dictionary<string, object> _prereq = new(StringComparer.Ordinal);
        private DateTime _checked = DateTime.MinValue;
        private DateTime _validUntil = DateTime.MinValue;
        private LoginSession? _seenLogin;
        private bool _seenLoginRunning;

        public StatusCache(ProviderRegistry providers) => _providers = providers;

        public void Invalidate() => _validUntil = DateTime.MinValue;

        /// <summary>
        /// Czy logowanie kodem urządzenia (Codex) zakończyło się od ostatniego sprawdzenia – wtedy stan logowania trzeba odświeżyć,
        /// inaczej panel przez 5 minut pokazywałby „niezalogowany” obok komunikatu „Zalogowano.”.
        /// </summary>
        public bool LoginFinishedSinceCheck()
        {
            var s = _providers.Codex.Login;
            if (s == null || s.Running) return false;
            return !ReferenceEquals(s, _seenLogin) || _seenLoginRunning;
        }

        public async Task<(List<ProviderStatus> statuses, object prerequisites, DateTime checkedUtc)> GetAsync(bool force, CancellationToken ct)
        {
            force |= LoginFinishedSinceCheck();
            if (!force && DateTime.UtcNow < _validUntil) return Snapshot();
            await _gate.WaitAsync(ct);
            try
            {
                force |= LoginFinishedSinceCheck();
                if (!force && DateTime.UtcNow < _validUntil) return Snapshot();
                // Stan logowania zapamiętany PRZED sprawdzeniem – zakończenie logowania w trakcie wymusi kolejne odświeżenie.
                var login = _providers.Codex.Login;
                bool loginRunning = login?.Running ?? false;
                // Własny token: przerwanie zapytania HTTP nie przerywa sprawdzania (wynik trafia do pamięci dla kolejnych zapytań).
                using var cts = new CancellationTokenSource(RefreshTimeout);
                var list = new List<ProviderStatus>();
                foreach (var p in _providers.All)
                {
                    try { list.Add(await p.StatusAsync(cts.Token)); }
                    catch (Exception ex) { list.Add(new ProviderStatus { Id = p.Id, Name = p.Name, Detail = "Błąd sprawdzania: " + ex.Message }); }
                }
                bool conclusive = !cts.IsCancellationRequested;
                // Po przekroczeniu łącznego czasu wyniki są niepewne – zostawiamy poprzednie, jeśli były.
                if (conclusive || _statuses.Count == 0) _statuses = list;
                var prereq = new Dictionary<string, object>(_prereq, StringComparer.Ordinal);
                var tools = new (string key, string[] args)[] { ("git", new[] { "--version" }), ("dotnet", new[] { "--list-sdks" }), ("node", new[] { "--version" }) };
                foreach (var (key, args) in tools)
                {
                    var (value, sure) = await Version(key, args, cts.Token);
                    // Niepewny wynik (przekroczony czas / przerwanie) nie nadpisuje znanego stanu – „brak” tylko przy pewnym wyniku.
                    if (sure || !prereq.ContainsKey(key)) prereq[key] = value;
                    conclusive &= sure;
                }
                _prereq = prereq;
                var now = DateTime.UtcNow;
                _checked = now;
                _validUntil = now + (conclusive ? Fresh : RetryInconclusive);
                _seenLogin = login;
                _seenLoginRunning = loginRunning;
                return Snapshot();
            }
            finally
            {
                _gate.Release();
            }
        }

        private (List<ProviderStatus>, object, DateTime) Snapshot() => (_statuses, _prereq, _checked);

        /// <summary>Wersja narzędzia; sure=false, gdy sprawdzenie przekroczyło czas lub zostało przerwane (wynik nieznany, a nie „brak”).</summary>
        private static async Task<(object value, bool sure)> Version(string name, string[] args, CancellationToken ct)
        {
            var exe = ExecutableLocator.Find(name);
            if (exe == null) return (new { found = false, version = "" }, true);
            var r = await ProcessRunner.RunAsync(exe, args, Environment.CurrentDirectory, timeout: TimeSpan.FromSeconds(20), ct: ct);
            if (r.Cancelled || r.TimedOut) return (new { found = false, version = "", unknown = true }, false);
            string v = string.Join(" | ", r.Output.Where(l => l.Trim().Length > 0).Take(4));
            bool ok = r.Ok && (name != "dotnet" || r.Output.Any(l => l.StartsWith("8.", StringComparison.Ordinal) || l.StartsWith("9.", StringComparison.Ordinal) || l.StartsWith("10.", StringComparison.Ordinal)));
            return (new { found = ok, version = v }, true);
        }
    }
}
