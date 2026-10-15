using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apex.AiLab.Analysis;
using Apex.AiLab.Deploy;
using Apex.AiLab.Lab;
using Apex.AiLab.Pipeline;
using Apex.AiLab.Providers;
using Apex.AiLab.Scheduling;
using Apex.Core.Model;
using Apex.Core.Performance;
using Xunit;

namespace Apex.Tests
{
    /// <summary>Audyt zmian AI – reguły egzekwujące wymagania: RiskEngine/limity/aktywne strategie tylko z potwierdzeniem, LIVE i krypto nietykalne.</summary>
    public class ChangeAuditTests
    {
        private static readonly PolicySettings Policy = new();

        private static string Diff(string file, params string[] lines) =>
            $"diff --git a/{file} b/{file}\n--- a/{file}\n+++ b/{file}\n@@ -1,1 +1,{lines.Length} @@\n" + string.Join("\n", lines) + "\n";

        private static List<AuditFinding> Audit(string file, string status, string diff, params string[] active) =>
            ChangeAudit.Audit(new List<ChangedFile> { new() { Path = file, Added = 1, Status = status } }, diff, Policy, active);

        [Fact]
        public void RiskEngineChange_IsProtected()
        {
            var f = Audit("src/Apex.Core/Risk/RiskManager.cs", "M", Diff("src/Apex.Core/Risk/RiskManager.cs", "+        public double X { get; set; } = 1;"));
            Assert.Contains(f, x => x.Severity == FindingSeverity.Protected);
            Assert.DoesNotContain(f, x => x.Severity == FindingSeverity.Blocking);
        }

        [Fact]
        public void ActiveStrategyFile_IsProtected_InactiveIsNot()
        {
            var active = Audit("src/Apex.Core/Strategies/IndexDipBuyStrategy.cs", "M", Diff("src/Apex.Core/Strategies/IndexDipBuyStrategy.cs", "+ int a = 1;"), "IndexDipBuy");
            Assert.Contains(active, x => x.Severity == FindingSeverity.Protected);
            var inactive = Audit("src/Apex.Core/Strategies/TrendPullbackStrategy.cs", "M", Diff("src/Apex.Core/Strategies/TrendPullbackStrategy.cs", "+ int a = 1;"), "IndexDipBuy");
            Assert.DoesNotContain(inactive, x => x.Severity == FindingSeverity.Protected);
        }

        [Fact]
        public void ProfileChange_IsProtected()
        {
            var f = Audit("config/profile_default.json", "M", Diff("config/profile_default.json", "+ \"Status\": \"Validated\""));
            Assert.Contains(f, x => x.Severity == FindingSeverity.Protected);
        }

        [Theory]
        [InlineData("src/Apex.AiLab/Pipeline/ChangeAudit.cs")]
        [InlineData("tools/windows/Zbuduj-APEX.ps1")]
        public void ForbiddenPaths_AreBlocking(string path)
        {
            Assert.Contains(Audit(path, "M", Diff(path, "+ x")), x => x.Severity == FindingSeverity.Blocking);
        }

        [Theory]
        [InlineData("+            if (cfg.LiveTradingFeature) allowed = true;")]
        [InlineData("-            if (!settings.AllowLive)")]
        [InlineData("+        var g = AccountGuard.Evaluate(acc, s);")]
        [InlineData("+    public bool AppliesTo(AssetClass c) => c == AssetClass.Crypto;")]
        [InlineData("+        volume *= 2; // martingale")]
        [InlineData("+        units *= 2;")]
        [InlineData("+        // ok\n+        AverageDown(position);")]
        [InlineData("+    [Robot(AccessRights = AccessRights.FullAccess)]")]
        public void SafetyViolations_AreBlocking(string lines)
        {
            const string file = "src/Apex.Core/Strategies/MeanReversionStrategy.cs";
            Assert.Contains(Audit(file, "M", Diff(file, lines.Split('\n'))), x => x.Severity == FindingSeverity.Blocking);
        }

        [Fact]
        public void KeywordsInCommentsAndDocs_AreNotBlocking()
        {
            const string code = "src/Apex.Core/Strategies/MeanReversionStrategy.cs";
            Assert.DoesNotContain(Audit(code, "M", Diff(code, "+        // Nigdy nie stosujemy martingale ani LiveTradingFeature.")), x => x.Severity == FindingSeverity.Blocking);
            Assert.DoesNotContain(Audit("docs/X.md", "M", Diff("docs/X.md", "+ Martingale i AssetClass.Crypto są zabronione.")), x => x.Severity == FindingSeverity.Blocking);
        }

        [Fact]
        public void SkippingOrDeletingTests_IsBlocking()
        {
            const string t = "tests/Apex.Tests/EngineTests.cs";
            Assert.Contains(Audit(t, "M", Diff(t, "+        [Fact(Skip = \"niestabilny\")]")), x => x.Rule == "Wyłączenie testu");
            Assert.Contains(Audit(t, "D", ""), x => x.Rule == "Usunięcie testu" && x.Severity == FindingSeverity.Blocking);
        }

        [Fact]
        public void NetworkInEngine_IsBlocking_ButNotInDesktop()
        {
            const string core = "src/Apex.Core/Indicators/Ta.cs";
            Assert.Contains(Audit(core, "M", Diff(core, "+ var c = new HttpClient();")), x => x.Rule == "Silnik bez sieci i procesów");
            const string desk = "src/Apex.Desktop/Services/NewsService.cs";
            Assert.DoesNotContain(Audit(desk, "M", Diff(desk, "+ var c = new HttpClient();")), x => x.Severity == FindingSeverity.Blocking);
        }

        [Fact]
        public void CodeWithoutTests_IsWarning()
        {
            const string core = "src/Apex.Core/Indicators/Ta.cs";
            Assert.Contains(Audit(core, "M", Diff(core, "+ int a = 1;")), x => x.Rule == "Brak nowych testów" && x.Severity == FindingSeverity.Warning);
        }

        [Fact]
        public void DefaultProtectedAndForbiddenPaths_CannotBeRemovedViaConfigFile()
        {
            string dir = Path.Combine(Path.GetTempPath(), "apex-cfg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "ailab.config.json"), "{\"Policy\":{\"ProtectedPaths\":[],\"ForbiddenPaths\":[]}}");
                var store = new LabConfigStore(dir);
                Assert.Contains("src/Apex.Core/Risk/", store.Config.Policy.ProtectedPaths);
                Assert.Contains("src/Apex.AiLab/", store.Config.Policy.ForbiddenPaths);
                Assert.False(store.Config.AutomationEnabled);
                Assert.True(store.Config.ApiToken.Length >= 32);
            }
            finally { Directory.Delete(dir, true); }
        }
    }

    public class ProviderParsingTests
    {
        [Fact]
        public void CodexEvents_AreParsed()
        {
            Assert.Contains("wątek", CodexEvents.Parse("{\"type\":\"thread.started\",\"thread_id\":\"abc\"}")!.Text);
            var cmd = CodexEvents.Parse("{\"type\":\"item.completed\",\"item\":{\"id\":\"1\",\"type\":\"command_execution\",\"command\":\"dotnet build\",\"aggregated_output\":\"OK\",\"exit_code\":0,\"status\":\"completed\"}}")!;
            Assert.Equal("command", cmd.Kind);
            Assert.Contains("dotnet build", cmd.Text);
            var file = CodexEvents.Parse("{\"type\":\"item.completed\",\"item\":{\"type\":\"file_change\",\"changes\":[{\"path\":\"src/a.cs\",\"kind\":\"update\"}]}}")!;
            Assert.Single(file.Files);
            var msg = CodexEvents.Parse("{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"Gotowe\"}}")!;
            Assert.Equal("Gotowe", msg.AgentMessage);
            var usage = CodexEvents.Parse("{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":1200,\"cached_input_tokens\":1000,\"output_tokens\":300}}")!;
            Assert.Equal(1200, usage.Usage!.InputTokens);
            Assert.Equal(1000, usage.Usage.CachedInputTokens);
            Assert.Equal(300, usage.Usage.OutputTokens);
            var fail = CodexEvents.Parse("{\"type\":\"turn.failed\",\"error\":{\"message\":\"You've hit your usage limit. Try again at 3:45 PM.\"}}")!;
            Assert.True(fail.TurnFailed);
            Assert.True(LimitDetector.IsLimit(fail.Error));
            var reconnect = CodexEvents.Parse("{\"type\":\"error\",\"message\":\"Reconnecting... 2/5\"}")!;
            Assert.Equal("warning", reconnect.Kind);
            Assert.Null(reconnect.Error);
            Assert.Null(CodexEvents.Parse("not json"));
        }

        [Fact]
        public void ClaudeStreamJson_IsParsed()
        {
            var edit = ClaudeEvents.Parse("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Poprawiam\"},{\"type\":\"tool_use\",\"name\":\"Edit\",\"input\":{\"file_path\":\"/w/src/a.cs\"}},{\"type\":\"tool_use\",\"name\":\"Bash\",\"input\":{\"command\":\"dotnet test\"}}]}}");
            Assert.Equal(3, edit.Count);
            Assert.Contains(edit, e => e.Kind == "file" && e.Files.Contains("/w/src/a.cs"));
            Assert.Contains(edit, e => e.Kind == "command" && e.Text.Contains("dotnet test"));
            var ok = ClaudeEvents.Parse("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"Tytuł\\nopis\",\"total_cost_usd\":0.42,\"usage\":{\"input_tokens\":100,\"cache_read_input_tokens\":900,\"cache_creation_input_tokens\":0,\"output_tokens\":50}}").Single();
            Assert.True(ok.IsResult);
            Assert.False(ok.IsError);
            Assert.Equal(1000, ok.Usage!.InputTokens);
            Assert.Equal(0.42, ok.Usage.CostUsd!.Value, 6);
            var lim = ClaudeEvents.Parse("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":true,\"result\":\"Claude AI usage limit reached|1760000000\"}").Single();
            Assert.True(lim.IsError);
            Assert.True(LimitDetector.IsLimit(lim.ResultText));
        }

        [Fact]
        public void LimitResetTimes_AreRecognised()
        {
            var now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1760000000).UtcDateTime, LimitDetector.ResetTime("Claude AI usage limit reached|1760000000", now));
            Assert.Equal(now.AddHours(2).AddMinutes(15), LimitDetector.ResetTime("Rate limit. Try again in 2 hours 15 minutes.", now));
            var at = LimitDetector.ResetTime("You've hit your usage limit. Try again at Oct 16th, 2026 3:45 PM.", now);
            Assert.NotNull(at);
            Assert.Equal(new DateTime(2026, 10, 16, 15, 45, 0, DateTimeKind.Local).ToUniversalTime(), at);
            Assert.Null(LimitDetector.ResetTime("zwykły błąd", now));
            Assert.False(LimitDetector.IsLimit("Build failed: CS1002"));
            Assert.True(LimitDetector.IsLimit("HTTP 429 rate_limit_error"));
        }

        [Fact]
        public void FileBlocks_ParseFencesAndSummary()
        {
            string text = "Wstęp\n=== PLIK: src/a.cs ===\n```csharp\nclass A {}\n```\n=== KONIEC PLIKU ===\n=== PLIK: tests/b.cs ===\nclass B {}\n=== KONIEC PLIKU ===\n=== PLIK: niedokonczony.cs ===\nx\n=== PODSUMOWANIE ===\nTytuł\nopis\n=== KONIEC ===";
            var r = FileBlocks.Parse(text);
            Assert.Equal(2, r.Files.Count);
            Assert.Equal("class A {}\n", r.Files[0].Content);
            Assert.Single(r.Problems);
        }

        [Fact]
        public void FileBlocks_SafePath_RejectsEscapesAndForbidden()
        {
            string root = Path.Combine(Path.GetTempPath(), "apex-root-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var forbidden = new[] { "src/Apex.AiLab/", ".git/" };
                Assert.Null(FileBlocks.SafePath(root, "../poza.cs", forbidden, out _));
                Assert.Null(FileBlocks.SafePath(root, "/etc/passwd", forbidden, out _));
                Assert.Null(FileBlocks.SafePath(root, "C:\\Windows\\x.dll", forbidden, out _));
                Assert.Null(FileBlocks.SafePath(root, "src/Apex.AiLab/Program.cs", forbidden, out _));
                Assert.Null(FileBlocks.SafePath(root, ".git/config", forbidden, out _));
                Assert.Equal("src/Apex.Core/X.cs", FileBlocks.SafePath(root, "src\\Apex.Core\\X.cs", forbidden, out _));
                var parsed = FileBlocks.Parse("=== PLIK: ../evil.cs ===\nx\n=== KONIEC PLIKU ===\n=== PLIK: docs/ok.md ===\ntekst\n=== KONIEC PLIKU ===");
                var problems = new List<string>();
                var applied = FileBlocks.Apply(root, parsed, forbidden, problems);
                Assert.Equal(new[] { "docs/ok.md" }, applied);
                Assert.Single(problems);
                Assert.False(File.Exists(Path.Combine(root, "..", "evil.cs")));
            }
            finally { Directory.Delete(root, true); }
        }
    }

    public class LabSchedulingTests
    {
        [Fact]
        public void Schedule_NextDue()
        {
            var now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
            Assert.Null(ScheduleMath.NextDueUtc(new ScheduleSettings { Kind = ScheduleKind.Off }, null, now));
            Assert.Equal(now.AddHours(6), ScheduleMath.NextDueUtc(new ScheduleSettings { Kind = ScheduleKind.EveryHours, EveryHours = 6 }, now, now));
            var daily = ScheduleMath.NextDueUtc(new ScheduleSettings { Kind = ScheduleKind.Daily, Hour = 6, Minute = 30 }, now, now)!.Value.ToLocalTime();
            Assert.Equal((6, 30), (daily.Hour, daily.Minute));
            Assert.True(daily > now.ToLocalTime() && daily <= now.ToLocalTime().AddDays(1));
            var weekly = ScheduleMath.NextDueUtc(new ScheduleSettings { Kind = ScheduleKind.Weekly, WeeklyDay = DayOfWeek.Saturday, Hour = 8 }, now, now)!.Value.ToLocalTime();
            Assert.Equal(DayOfWeek.Saturday, weekly.DayOfWeek);
        }

        private static (LabStore store, LabConfig cfg, Budget budget, string dir) NewBudget()
        {
            string dir = Path.Combine(Path.GetTempPath(), "apex-lab-" + Guid.NewGuid().ToString("N"));
            var store = new LabStore(dir);
            var cfg = new LabConfig();
            return (store, cfg, new Budget(store, () => cfg), dir);
        }

        [Fact]
        public void Budget_BlocksWhenLimitsExceeded()
        {
            var (store, cfg, budget, dir) = NewBudget();
            try
            {
                var now = DateTime.UtcNow;
                Assert.Null(budget.CanRun(ProviderId.Codex, now, manual: true));
                cfg.Budget.MaxAgentRunsPerDay = 1;
                budget.Record(ProviderId.Codex, "j1", "agent", new UsageInfo { InputTokens = 10, OutputTokens = 5 }, now);
                Assert.Contains("dzienny limit uruchomień", budget.CanRun(ProviderId.Codex, now, true));
                cfg.Budget.MaxAgentRunsPerDay = 10;
                cfg.Budget.MaxTokensPerDay = 10;
                Assert.Contains("tokenów", budget.CanRun(ProviderId.Codex, now, true));
                cfg.Budget.MaxTokensPerDay = 0;
                cfg.Budget.MaxUsdPerMonth = 1;
                budget.Record(ProviderId.AnthropicApi, "j2", "review", new UsageInfo { CostUsd = 1.5 }, now);
                Assert.Contains("kosztów API", budget.CanRun(ProviderId.AnthropicApi, now, true));
                Assert.Null(budget.CanRun(ProviderId.Codex, now, true)); // subskrypcja nie liczy się do limitu kwotowego API
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Budget_SubscriptionLimitPausesProvider_AndMinIntervalAppliesToAutomaticOnly()
        {
            var (store, cfg, budget, dir) = NewBudget();
            try
            {
                var now = DateTime.UtcNow;
                budget.Record(ProviderId.Codex, "j", "agent", new UsageInfo { LimitReached = true, LimitResetUtc = now.AddHours(2), LimitMessage = "usage limit" }, now);
                Assert.Contains("limit dostawcy", budget.CanRun(ProviderId.Codex, now, true));
                Assert.Null(budget.CanRun(ProviderId.Codex, now.AddHours(3), true) is string s && s.Contains("limit dostawcy") ? "x" : null);
                store.MarkAgentRun(now);
                Assert.Contains("odstęp", budget.CanRun(ProviderId.ClaudeCode, now.AddHours(1), manual: false));
                Assert.Null(budget.CanRun(ProviderId.ClaudeCode, now.AddHours(1), manual: true));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void ConfigValidation_RejectsAbsurdValues()
        {
            var c = new LabConfig();
            Assert.Empty(c.Validate());
            c.Schedule.EveryHours = 0;
            c.Budget.MaxUsdPerMonth = -1;
            c.Provider(ProviderId.OpenAiApi).Model = "gpt; rm -rf /";
            var e = c.Validate();
            Assert.Equal(3, e.Count);
        }

        [Fact]
        public void Secrets_RoundTripAndMask()
        {
            string key = "sk-test-0123456789abcdef";
            string stored = Secrets.Protect(key);
            Assert.DoesNotContain(key, stored);
            Assert.Equal(key, Secrets.Unprotect(stored));
            Assert.Equal("sk-…cdef", Secrets.Mask(key));
            Assert.Null(Secrets.Unprotect("bzdura"));
        }

        [Fact]
        public void RequiredPhrase_DependsOnProtectionAndCompleteness()
        {
            Assert.Null(Deployer.RequiredPhrase(new LabJob { Status = JobStatus.ReadyForApproval }));
            Assert.Equal(Deployer.WarningPhrase, Deployer.RequiredPhrase(new LabJob { Status = JobStatus.NeedsAttention }));
            Assert.Equal(Deployer.ProtectedPhrase, Deployer.RequiredPhrase(new LabJob { Status = JobStatus.NeedsAttention, ProtectedTouched = true }));
        }
    }

    public class LabVerificationTests
    {
        private static BacktestMetrics M(string period, int n, double er, double dd) => new() { Period = period, MinScore = 50, Trades = n, ExpectancyR = er, MaxEquityDrawdownPct = dd };

        [Fact]
        public void Compare_Verdicts()
        {
            var same = new BacktestComparison { Baseline = { M("research", 113, 0.036, 1.6), M("holdout", 21, -0.115, 0.93) }, Candidate = { M("research", 113, 0.036, 1.6), M("holdout", 21, -0.115, 0.93) } };
            Verification.Compare(same);
            Assert.Equal("bez zmian", same.Verdict);
            var worse = new BacktestComparison { Baseline = { M("research", 113, 0.036, 1.6), M("holdout", 21, -0.115, 0.93) }, Candidate = { M("research", 120, 0.010, 1.7), M("holdout", 22, -0.12, 0.9) } };
            Verification.Compare(worse);
            Assert.Equal("gorszy", worse.Verdict);
            var ddWorse = new BacktestComparison { Baseline = { M("research", 113, 0.036, 1.6), M("holdout", 21, -0.115, 0.93) }, Candidate = { M("research", 113, 0.040, 2.4), M("holdout", 21, -0.10, 0.93) } };
            Verification.Compare(ddWorse);
            Assert.Equal("gorszy", ddWorse.Verdict);
            var better = new BacktestComparison { Baseline = { M("research", 113, 0.036, 1.6), M("holdout", 21, -0.115, 0.93) }, Candidate = { M("research", 110, 0.060, 1.5), M("holdout", 20, -0.05, 0.9) } };
            Verification.Compare(better);
            Assert.Equal("lepszy", better.Verdict);
            Assert.Contains(better.Notes, n => n.Contains("NIE jest dowodem"));
        }

        [Fact]
        public void ParseTrxAndSummary()
        {
            string dir = Path.Combine(Path.GetTempPath(), "apex-trx-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string trx = Path.Combine(dir, "w.trx");
                File.WriteAllText(trx, "<?xml version=\"1.0\"?><TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results>" +
                    "<UnitTestResult testName=\"A.B\" outcome=\"Passed\"/><UnitTestResult testName=\"A.C\" outcome=\"Failed\"/></Results>" +
                    "<ResultSummary outcome=\"Failed\"><Counters total=\"2\" executed=\"2\" passed=\"1\" failed=\"1\" error=\"0\" timeout=\"0\" aborted=\"0\" notExecuted=\"0\"/></ResultSummary></TestRun>");
                var r = Verification.ParseTrx(trx);
                Assert.True(r.Ran);
                Assert.Equal(2, r.Total);
                Assert.Equal(1, r.Failed);
                Assert.Equal(new[] { "A.C" }, r.FailedNames);
                Assert.False(Verification.ParseTrx(Path.Combine(dir, "brak.trx")).Ran);
            }
            finally { Directory.Delete(dir, true); }
            var s = Verification.ParseSummary("{\"runs\":[{\"period\":\"holdout\",\"minScore\":50,\"trades\":21,\"expectancyR\":-0.115,\"profitFactor\":0.71,\"maxEquityDrawdownPct\":0.93}]}");
            Assert.Equal(21, Assert.Single(s).Trades);
        }

        [Fact]
        public void JournalCsv_RoundTripsFromEngineFormat()
        {
            var rec = new TradeRecord { PositionId = 7, Symbol = "US500", AssetClass = AssetClass.Index, Strategy = Apex.Core.Model.StrategyId.IndexDipBuy, ExitTimeUtc = new DateTime(2026, 10, 1, 20, 0, 0), InitialRiskMoney = 50, NetProfit = 25, ExitReason = "SL; test" };
            var parsed = JournalTrade.ParseCsv(TradeRecord.CsvHeader + "\n" + rec.ToCsv() + "\n");
            var t = Assert.Single(parsed);
            Assert.Equal("IndexDipBuy", t.Strategy);
            Assert.Equal("Index", t.AssetClass);
            Assert.Equal(0.5, t.R, 6);
            Assert.Equal(25, t.NetProfit, 6);
        }

        [Fact]
        public void MarketChange_IsDetectedFromSnapshots()
        {
            string dir = Path.Combine(Path.GetTempPath(), "apex-mkt-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new LabStore(dir);
                var now = DateTime.UtcNow;
                string[] syms = { "US500", "NAS100", "GER40", "UK100" };
                for (int d = 20; d >= 6; d--)
                    store.AddSnapshot(new MarketSnapshot { TimeUtc = now.AddDays(-d), Account = "acc", Entries = syms.Select(s => new MarketSnapshotEntry { Symbol = s, Regime = "StrongUptrend", AtrPercentile = 0.3 }).ToList() });
                store.AddSnapshot(new MarketSnapshot { TimeUtc = now, Account = "acc", Entries = syms.Select((s, i) => new MarketSnapshotEntry { Symbol = s, Regime = i < 3 ? "Chaotic" : "StrongUptrend", AtrPercentile = 0.95 }).ToList() });
                var analyzer = new Analyzer(new DesktopClient(dir), store);
                var report = new AnalysisReport();
                analyzer.AnalyzeMarket(report);
                Assert.Contains(report.Findings, f => f.Kind == TriggerKind.MarketChange && f.Title.Contains("reżim"));
                Assert.Contains(report.Findings, f => f.Kind == TriggerKind.MarketChange && f.Title.Contains("zmienności"));
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }
    }

    /// <summary>Izolowane środowisko pracy na prawdziwym git: kopia, gałąź zadania, zmiany, wdrożenie, eksport – repozytorium źródłowe pozostaje nietknięte.</summary>
    public class GitWorkspaceTests
    {
        private static void Git(string dir, params string[] args)
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, "git " + string.Join(" ", args) + ": " + p.StandardError.ReadToEnd());
        }

        [Fact]
        public async Task Workspace_IsolatesChanges_AndDeploysOnlyOnRequest()
        {
            if (GitWorkspace.Git() == null) return; // brak git – test nie ma sensu
            string root = Path.Combine(Path.GetTempPath(), "apex-git-" + Guid.NewGuid().ToString("N"));
            string src = Path.Combine(root, "zrodlo");
            Directory.CreateDirectory(src);
            try
            {
                Git(src, "init", "-q", "-b", "main");
                Git(src, "config", "user.email", "t@t");
                Git(src, "config", "user.name", "t");
                File.WriteAllText(Path.Combine(src, "a.txt"), "jeden\n");
                Git(src, "add", "-A");
                Git(src, "commit", "-q", "-m", "start");

                var ws = new GitWorkspace(Path.Combine(root, "lab"));
                var (baseCommit, baseRef) = await ws.PrepareAsync(src, "", CancellationToken.None);
                Assert.Equal("origin/main", baseRef);
                string wt = await ws.CreateWorktreeAsync("j1", baseCommit, CancellationToken.None);
                File.WriteAllText(Path.Combine(wt, "a.txt"), "jeden\ndwa\n");
                Directory.CreateDirectory(Path.Combine(wt, ".ailab"));
                File.WriteAllText(Path.Combine(wt, ".ailab", "ZADANIE.md"), "zadanie");
                var files = await ws.CollectChangesAsync(wt, baseCommit, CancellationToken.None);
                var f = Assert.Single(files); // .ailab nie trafia do zmian
                Assert.Equal(("a.txt", 1, 0), (f.Path, f.Added, f.Removed));
                Assert.Contains("+dwa", await ws.DiffAsync(wt, baseCommit, CancellationToken.None));
                await ws.CommitAsync(wt, "zmiana", CancellationToken.None);

                Assert.Equal("jeden\n", File.ReadAllText(Path.Combine(src, "a.txt"))); // źródło nietknięte
                var (ok, commit) = await ws.MergeForDeployAsync("j1", baseCommit, CancellationToken.None);
                Assert.True(ok, commit);
                Assert.Equal("jeden\ndwa\n", File.ReadAllText(Path.Combine(ws.DeployDir, "a.txt")));
                string patch = await ws.ExportPatchAsync("j1", baseCommit, Path.Combine(root, "z.patch"), CancellationToken.None);
                Assert.Contains("+dwa", File.ReadAllText(patch));
                var (pushed, _) = await ws.PushBranchToSourceAsync("j1", CancellationToken.None);
                Assert.True(pushed);
                Assert.Equal("jeden\n", File.ReadAllText(Path.Combine(src, "a.txt"))); // tylko nowa gałąź, pliki źródła bez zmian
                Git(src, "rev-parse", "--verify", "ai/j1");
                await ws.RemoveWorktreeAsync("j1", CancellationToken.None);
                Assert.False(Directory.Exists(wt));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (Exception) { /* pliki git tylko do odczytu na Windows */ }
            }
        }
    }
}
