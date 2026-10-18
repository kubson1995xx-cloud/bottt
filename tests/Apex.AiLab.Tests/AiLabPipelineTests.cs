using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apex.AiLab;
using Apex.AiLab.Analysis;
using Apex.AiLab.Lab;
using Apex.AiLab.Pipeline;
using Apex.AiLab.Providers;
using Apex.AiLab.Scheduling;
using Xunit;

namespace Apex.Tests
{
    // ------------------------------------------------------------------ pomocnicze

    /// <summary>Katalog tymczasowy usuwany po teście.</summary>
    internal sealed class LabTempDir : IDisposable
    {
        public LabTempDir(string prefix)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Sub(string name)
        {
            string p = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(p);
            return p;
        }

        public void Dispose()
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(Path, true);
            }
            catch (Exception) { /* pliki git tylko do odczytu na Windows / proces jeszcze trzyma plik */ }
        }
    }

    /// <summary>Dostawca AI do testów potoku (bez sieci i bez procesów).</summary>
    internal sealed class FakeProvider : IAiProvider
    {
        private readonly Func<AgentRequest, CancellationToken, AgentResult> _run;

        public FakeProvider(ProviderId id, Func<AgentRequest, CancellationToken, AgentResult> run, bool executesCommands = true)
        {
            Id = id;
            _run = run;
            ExecutesCommands = executesCommands;
        }

        public ProviderId Id { get; }
        public string Name => "Fake " + Id;
        public bool ExecutesCommands { get; }
        public bool UsesSubscription { get; set; } = true;
        public int Calls;
        public int StatusCalls;
        public Func<CancellationToken, ProviderStatus>? StatusOverride { get; set; }

        public Task<ProviderStatus> StatusAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref StatusCalls);
            if (StatusOverride != null) return Task.FromResult(StatusOverride(ct));
            return Task.FromResult(new ProviderStatus { Id = Id, Name = Name, Installed = true, LoggedIn = true, UsesSubscription = UsesSubscription, ExecutesCommands = ExecutesCommands });
        }

        public Task<AgentResult> RunAsync(AgentRequest req, AgentProgress progress, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(_run(req, ct));
        }

        /// <summary>Agent, który dopisuje plik dokumentacji (zmiana przechodzi audyt).</summary>
        public static AgentResult WriteDoc(AgentRequest req)
        {
            Directory.CreateDirectory(Path.Combine(req.WorkDir, "docs"));
            File.WriteAllText(Path.Combine(req.WorkDir, "docs", "notatka-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".md"), "Wniosek z analizy.\n");
            return new AgentResult { Ok = true, Summary = "Notatka z analizy\nOpis zmiany.", Usage = new UsageInfo { InputTokens = 1000, OutputTokens = 100 } };
        }

        public static AgentResult Limit() => new()
        {
            Ok = false,
            Error = "Limit użycia: You've hit your usage limit.",
            Usage = new UsageInfo { LimitReached = true, LimitMessage = "You've hit your usage limit.", LimitResetUtc = DateTime.UtcNow.AddHours(3) },
        };
    }

    /// <summary>Weryfikacja do testów potoku: domyślnie wszystko zaliczone; zachowanie podmieniane delegatami.</summary>
    internal sealed class FakeVerification : IVerification
    {
        public int Builds, Tests, Backtests;
        public Func<int, string, BuildResult>? OnBuild;            // (numer wywołania, katalog)
        public Func<int, string, TestResult>? OnTest;              // (numer wywołania, katalog wyników)
        public Func<int, string, BacktestRun>? OnBacktest;         // (numer wywołania, katalog wyjściowy)

        public static TestResult Passing(params string[] names)
        {
            if (names.Length == 0) names = new[] { "Apex.Tests.A.Jeden", "Apex.Tests.A.Dwa" };
            var t = new TestResult { Ran = true, Total = names.Length, Passed = names.Length };
            foreach (var n in names) t.Outcomes[n] = "Passed";
            return t;
        }

        public static List<BacktestMetrics> Metrics() => new()
        {
            new BacktestMetrics { Period = "research", MinScore = 50, Trades = 100, ExpectancyR = 0.05, MaxEquityDrawdownPct = 2 },
            new BacktestMetrics { Period = "holdout", MinScore = 50, Trades = 20, ExpectancyR = 0.01, MaxEquityDrawdownPct = 1 },
        };

        public Task<bool> RestoreAsync(string dir, CancellationToken ct) => Task.FromResult(true);

        public Task<BuildResult> BuildAsync(string dir, CancellationToken ct)
        {
            int n = Interlocked.Increment(ref Builds);
            return Task.FromResult(OnBuild?.Invoke(n, dir) ?? new BuildResult { Ok = true });
        }

        public Task<TestResult> TestAsync(string dir, string resultsDir, CancellationToken ct)
        {
            int n = Interlocked.Increment(ref Tests);
            return Task.FromResult(OnTest?.Invoke(n, resultsDir) ?? Passing());
        }

        public Task<BacktestRun> RunBacktestAsync(string dir, string outDir, CancellationToken ct)
        {
            int n = Interlocked.Increment(ref Backtests);
            return Task.FromResult(OnBacktest?.Invoke(n, outDir) ?? new BacktestRun { Outcome = BacktestOutcome.Ok, Metrics = Metrics(), Message = "OK" });
        }
    }

    /// <summary>Kompletne środowisko AI Lab na prawdziwym git z podstawionymi dostawcami i weryfikacją.</summary>
    internal sealed class LabHarness : IDisposable
    {
        private readonly LabTempDir _tmp = new("apex-pipe");

        public LabHarness()
        {
            Source = _tmp.Sub("zrodlo");
            Git(Source, "init", "-q", "-b", "main");
            Git(Source, "config", "user.email", "t@t");
            Git(Source, "config", "user.name", "t");
            File.WriteAllText(Path.Combine(Source, "README.md"), "APEX\n");
            Git(Source, "add", "-A");
            Git(Source, "commit", "-q", "-m", "start");

            LabDir = _tmp.Sub("lab");
            Cfg = new LabConfigStore(LabDir);
            Cfg.Update(c => c.Repo.SourceRepoPath = Source);
            Store = new LabStore(LabDir);
            Budget = new Budget(Store, () => Cfg.Config);
            Providers = new ProviderRegistry(Cfg);
            Baseline = new BaselineCache(LabDir);
            Verify = new FakeVerification();
            Runner = new JobRunner(Cfg, Store, new Analyzer(new DesktopClient(LabDir), Store), new GitWorkspace(LabDir), Budget, Providers, Baseline,
                (repo, jobDir, log) => Verify)
            {
                AlgoRobotsDirs = () => Array.Empty<string>(),
            };
        }

        public static bool GitAvailable => GitWorkspace.Git() != null;

        public string Source { get; }
        public string LabDir { get; }
        public LabConfigStore Cfg { get; }
        public LabStore Store { get; }
        public Budget Budget { get; }
        public ProviderRegistry Providers { get; }
        public BaselineCache Baseline { get; }
        public FakeVerification Verify { get; }
        public JobRunner Runner { get; }
        public string Sub(string name) => _tmp.Sub(name);

        public FakeProvider Provider(ProviderId id, Func<AgentRequest, CancellationToken, AgentResult> run, bool executesCommands = true)
        {
            var p = new FakeProvider(id, run, executesCommands);
            Providers.Override(p);
            return p;
        }

        public async Task<LabJob> RunAsync(TriggerKind trigger, bool runAgent = true)
        {
            string? id = Runner.Start(trigger, "test", runAgent, null);
            Assert.NotNull(id);
            var sw = Stopwatch.StartNew();
            while (Runner.RunningJobId != null)
            {
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(90), "Zadanie nie zakończyło się w czasie.");
                await Task.Delay(50);
            }
            return Store.Job(id!)!;
        }

        private static void Git(string dir, params string[] args)
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, "git " + string.Join(" ", args) + ": " + p.StandardError.ReadToEnd());
        }

        public void Dispose() => _tmp.Dispose();
    }

    // ------------------------------------------------------------------ potok zadań (JobRunner)

    /// <summary>Potok zadania: wersja bazowa, dostawca zapasowy, przegląd, przerwanie, backtest kandydata, zabezpieczenie cBotów.</summary>
    public class AiLabPipelineTests
    {
        [Fact]
        public async Task Fallback_RunsForAutomaticJob_AfterPrimaryLimit()
        {
            if (!LabHarness.GitAvailable) return;
            using var h = new LabHarness();
            var codex = h.Provider(ProviderId.Codex, (req, ct) => FakeProvider.Limit());
            var claude = h.Provider(ProviderId.ClaudeCode, (req, ct) => FakeProvider.WriteDoc(req));
            var job = await h.RunAsync(TriggerKind.StrategyDegradation);
            Assert.Equal(1, codex.Calls);
            Assert.Equal(1, claude.Calls); // wcześniej: blokada „minimalny odstęp” tuż po oznaczeniu uruchomienia
            Assert.Equal(ProviderId.ClaudeCode, job.Provider);
            Assert.Equal(JobStatus.ReadyForApproval, job.Status);
            var usage = h.Store.Usage(DateTime.UtcNow.AddHours(-1));
            Assert.Contains(usage, u => u.Provider == ProviderId.Codex && u.Kind == "agent-limit");
            Assert.Equal(1, h.Budget.Compute(DateTime.UtcNow).RunsToday); // próba odrzucona limitem nie zajmuje dziennego uruchomienia
            Assert.NotNull(h.Store.LastAgentRunUtc);
        }

        [Fact]
        public async Task Fallback_ApiProviderWithoutModel_IsNotUsed()
        {
            if (!LabHarness.GitAvailable) return;
            using var h = new LabHarness();
            h.Cfg.Update(c =>
            {
                c.FallbackProvider = ProviderId.OpenAiApi;
                c.Provider(ProviderId.OpenAiApi).Enabled = true;
                c.Provider(ProviderId.OpenAiApi).Model = "";
            });
            h.Provider(ProviderId.Codex, (req, ct) => FakeProvider.Limit());
            var api = h.Provider(ProviderId.OpenAiApi, (req, ct) => FakeProvider.WriteDoc(req), executesCommands: false);
            var job = await h.RunAsync(TriggerKind.Manual);
            Assert.Equal(0, api.Calls);
            Assert.Equal(JobStatus.Throttled, job.Status); // limit – a nie „Failed” po nieudanej próbie zapasowej
        }

        [Fact]
        public async Task Review_IsNotBlockedByAgentRunCounters()
        {
            if (!LabHarness.GitAvailable) return;
            using var h = new LabHarness();
            h.Cfg.Update(c =>
            {
                c.Budget.MaxAgentRunsPerDay = 1;
                c.ReviewProvider = ProviderId.AnthropicApi;
                c.Provider(ProviderId.AnthropicApi).Enabled = true;
            });
            h.Provider(ProviderId.Codex, (req, ct) => FakeProvider.WriteDoc(req));
            var reviewer = h.Provider(ProviderId.AnthropicApi, (req, ct) => new AgentResult { Ok = true, Summary = "Zmiana poprawna.\nWERDYKT: AKCEPTUJ" }, executesCommands: false);
            var job = await h.RunAsync(TriggerKind.Manual);
            Assert.Equal(1, reviewer.Calls);
            Assert.Equal(StageStatus.Ok, job.Stages.First(s => s.Stage == JobStage.Review).Status);
            Assert.Equal(JobStatus.ReadyForApproval, job.Status);
            Assert.Contains(h.Store.Usage(DateTime.UtcNow.AddHours(-1)), u => u.Kind == "review" && u.ApiBilled == true);
        }

        [Fact]
        public async Task Review_Reject_EndsAsNeedsAttention()
        {
            if (!LabHarness.GitAvailable) return;
            using var h = new LabHarness();
            h.Cfg.Update(c => c.ReviewProvider = ProviderId.ClaudeCode);
            h.Provider(ProviderId.Codex, (req, ct) => FakeProvider.WriteDoc(req));
            h.Provider(ProviderId.ClaudeCode, (req, ct) => new AgentResult { Ok = true, Summary = "Zmiana luzuje filtr SL.\n\n**WERDYKT: ODRZUĆ**" });
            var job = await h.RunAsync(TriggerKind.Manual);
            Assert.Equal(JobStatus.NeedsAttention, job.Status);
            Assert.Contains("ODRZUĆ", job.StatusText);
            Assert.DoesNotContain("wszystkie kontrole zaliczone", job.StatusText);
            Assert.Equal(StageStatus.Warning, job.Stages.First(s => s.Stage == JobStage.Review).Status);
        }

        [Fact]
        public async Task Review_SkippedWhileConfigured_EndsAsNeedsAttention()
        {
            if (!LabHarness.GitAvailable) return;
            using var h = new LabHarness();
            h.Cfg.Update(c => c.ReviewProvider = ProviderId.ClaudeCode);
            h.Provider(ProviderId.Codex, (req, ct) => FakeProvider.WriteDoc(req));
            var claude = h.Provider(ProviderId.ClaudeCode, (req, ct) => new AgentResult { Ok = true, Summary = "WERDYKT: AKCEPTUJ" });
            claude.StatusOverride = _ => new ProviderStatus { Id = ProviderId.ClaudeCode, Installed = true, LoggedIn = false };
            var job = await h.RunAsync(TriggerKind.Manual);
            Assert.Equal(0, claude.Calls);
            Assert.Equal(JobStatus.NeedsAttention, job.Status);
            Assert.Contains("przegląd AI pominięty", job.StatusText);
        }

        [Theory]
        [InlineData("Nie widzę powodu do odrzucenia.\nWERDYKT: AKCEPTUJ", ReviewVerdict.Accept)]
        [InlineData("Zastrzeżenia: brak testu.\n- **Werdykt:** wymaga poprawek", ReviewVerdict.NeedsChanges)]
        [InlineData("Werdykt: AKCEPTUJ / ODRZUĆ / WYMAGA POPRAWEK\n...\nWERDYKT: ODRZUC", ReviewVerdict.Reject)]
        [InlineData("Ogólnie dobrze, ale WYMAGA POPRAWEK w pliku X.", ReviewVerdict.NeedsChanges)]
        [InlineData("Ocena bez słowa kluczowego.", ReviewVerdict.Unknown)]
        [InlineData("", ReviewVerdict.Unknown)]
        public void ReviewVerdict_PrefersExplicitLine(string text, ReviewVerdict expected)
        {
            Assert.Equal(expected, JobRunner.ParseReviewVerdict(text));
        }

        [Fact]
        public async Task Baseline_BuildFailure_IsNotCached_AndAgentNotRun()
        {
            if (!LabHarness.GitAvailable) return;
            using var h = new LabHarness();
            var codex = h.Provider(ProviderId.Codex, (req, ct) => FakeProvider.WriteDoc(req));
            h.Verify.OnBuild = (n, dir) => new BuildResult { Ok = false, Errors = { "CS0000: chwilowy błąd" } };
            var job = await h.RunAsync(TriggerKind.Manual);
            Assert.Equal(JobStatus.Failed, job.Status);
            Assert.Contains("Wersja bazowa się nie kompiluje", job.StatusText);
            Assert.Equal(0, codex.Calls);
            Assert.Null(h.Baseline.Get(job.BaseCommit, h.Cfg.Config.Repo.BacktestDataDir, h.Cfg.Config.Repo.RunBacktests));

            // Następne zadanie na tym samym commicie liczy wersję bazową od nowa (a nie powtarza zapisanego błędu).
            h.Verify.OnBuild = null;
            int before = h.Verify.Builds;
            var second = await h.RunAsync(TriggerKind.Manual);
            Assert.Equal(JobStatus.ReadyForApproval, second.Status);
            Assert.Equal(before + 2, h.Verify.Builds); // baza + kandydat
            Assert.NotNull(h.Baseline.Get(second.BaseCommit, h.Cfg.Config.Repo.BacktestDataDir, h.Cfg.Config.Repo.RunBacktests));
        }

        [Fact]
        public async Task Baseline_TestTimeout_IsNotCached()
        {
            if (!LabHarness.GitAvailable) return;
            using var h = new LabHarness();
            var codex = h.Provider(ProviderId.Codex, (req, ct) => FakeProvider.WriteDoc(req));
            h.Verify.OnTest = (n, dir) => new TestResult { Ran = true, Total = 0, Failed = 1, TimedOut = true };
            var job = await h.RunAsync(TriggerKind.Manual);
            Assert.Equal(JobStatus.Failed, job.Status);
            Assert.Contains("Testy wersji bazowej nie zostały wykonane", job.StatusText);
            Assert.Equal(0, codex.Calls);
            Assert.Null(h.Baseline.Get(job.BaseCommit, h.Cfg.Config.Repo.BacktestDataDir, h.Cfg.Config.Repo.RunBacktests));
        }

        [Fact]
        public async Task Baseline_CancelDuringBuild_EndsCancelled_AndIsNotCached()
        {
            if (!LabHarness.GitAvailable) return;
            using var h = new LabHarness();
            var codex = h.Provider(ProviderId.Codex, (req, ct) => FakeProvider.WriteDoc(req));
            h.Verify.OnBuild = (n, dir) =>
            {
                h.Runner.Cancel(h.Runner.RunningJobId!);
                return new BuildResult { Ok = false, Cancelled = true };
            };
            var job = await h.RunAsync(TriggerKind.Manual);
            Assert.Equal(JobStatus.Cancelled, job.Status);
            Assert.Equal(0, codex.Calls);
            Assert.Null(h.Baseline.Get(job.BaseCommit, h.Cfg.Config.Repo.BacktestDataDir, h.Cfg.Config.Repo.RunBacktests));
        }

        [Fact]
        public async Task Baseline_BacktestFailure_IsNotCached_AndJobStopsBeforeAgent()
        {
            if (!LabHarness.GitAvailable) return;
            using var h = new LabHarness();
            var codex = h.Provider(ProviderId.Codex, (req, ct) => FakeProvider.WriteDoc(req));
            h.Verify.OnBacktest = (n, dir) => new BacktestRun { Outcome = BacktestOutcome.TimedOut, Message = "Przekroczony czas backtestu." };
            var job = await h.RunAsync(TriggerKind.Manual);
            Assert.Equal(JobStatus.Failed, job.Status);
            Assert.Contains("Backtest wersji bazowej nieudany", job.StatusText);
            Assert.Equal(0, codex.Calls);
            Assert.Null(h.Baseline.Get(job.BaseCommit, h.Cfg.Config.Repo.BacktestDataDir, h.Cfg.Config.Repo.RunBacktests));
        }

        [Fact]
        public async Task CandidateBacktestFailure_IsRejected_ConfigSkipNeedsAttention()
        {
            if (!LabHarness.GitAvailable) return;
            using var h = new LabHarness();
            h.Provider(ProviderId.Codex, (req, ct) => FakeProvider.WriteDoc(req));
            // Wersja bazowa OK, kandydat – awaria backtestu.
            h.Verify.OnBacktest = (n, dir) => dir.EndsWith("backtest-baza", StringComparison.Ordinal)
                ? new BacktestRun { Outcome = BacktestOutcome.Ok, Metrics = FakeVerification.Metrics(), Message = "OK" }
                : new BacktestRun { Outcome = BacktestOutcome.Failed, Message = "Backtest nieudany: IndexOutOfRangeException" };
            var job = await h.RunAsync(TriggerKind.Manual);
            Assert.Equal(JobStatus.RejectedByChecks, job.Status);
            Assert.Contains("backtest kandydata nieudany", job.StatusText);

            // Pominięcie z powodu braku danych (ustawienia) – propozycja z ostrzeżeniem.
            using var h2 = new LabHarness();
            h2.Provider(ProviderId.Codex, (req, ct) => FakeProvider.WriteDoc(req));
            h2.Verify.OnBacktest = (n, dir) => new BacktestRun { Outcome = BacktestOutcome.NoData, Message = "Brak danych historycznych." };
            var job2 = await h2.RunAsync(TriggerKind.Manual);
            Assert.Equal(JobStatus.NeedsAttention, job2.Status);
            Assert.Contains("weryfikacja niepełna", job2.StatusText);
        }

        [Fact]
        public async Task CancelDuringCandidateTests_EndsCancelled_WithoutCommit()
        {
            if (!LabHarness.GitAvailable) return;
            using var h = new LabHarness();
            h.Cfg.Update(c => { c.PrimaryProvider = ProviderId.AnthropicApi; c.Provider(ProviderId.AnthropicApi).Enabled = true; });
            var api = h.Provider(ProviderId.AnthropicApi, (req, ct) => FakeProvider.WriteDoc(req), executesCommands: false);
            h.Verify.OnTest = (n, dir) =>
            {
                if (n == 1) return FakeVerification.Passing();
                h.Runner.Cancel(h.Runner.RunningJobId!);
                return new TestResult { Ran = false, Failed = 1, Cancelled = true };
            };
            var job = await h.RunAsync(TriggerKind.Manual);
            Assert.Equal(JobStatus.Cancelled, job.Status);
            Assert.Equal("", job.HeadCommit);
            Assert.Equal(1, api.Calls); // bez rundy poprawek po przerwaniu
            Assert.DoesNotContain(h.Store.Usage(DateTime.UtcNow.AddHours(-1)), u => u.Kind == "repair");
        }

        [Fact]
        public async Task CandidateMissingBaselineTest_IsBlocking()
        {
            if (!LabHarness.GitAvailable) return;
            using var h = new LabHarness();
            h.Provider(ProviderId.Codex, (req, ct) => FakeProvider.WriteDoc(req));
            // Kandydat: ta sama liczba testów, ale test strażniczy zastąpiony innym.
            h.Verify.OnTest = (n, dir) => n == 1 ? FakeVerification.Passing("Apex.Tests.A.Straznik", "Apex.Tests.A.Dwa") : FakeVerification.Passing("Apex.Tests.A.Trywialny", "Apex.Tests.A.Dwa");
            var job = await h.RunAsync(TriggerKind.Manual);
            Assert.Equal(JobStatus.RejectedByChecks, job.Status);
            Assert.Contains(job.Findings, f => f.Severity == FindingSeverity.Blocking && f.Rule == "Brak testów z wersji bazowej" && f.Detail.Contains("Straznik"));
        }

        [Fact]
        public async Task Tripwire_RestoresCtraderAlgo_ChangedByCandidateBuild()
        {
            if (!LabHarness.GitAvailable) return;
            using var h = new LabHarness();
            string robots = h.Sub("Robots");
            string algo = Path.Combine(robots, "ApexTradingAI.algo");
            File.WriteAllText(algo, "zatwierdzona wersja");
            h.Runner.AlgoRobotsDirs = () => new[] { robots };
            h.Provider(ProviderId.Codex, (req, ct) => FakeProvider.WriteDoc(req));
            h.Verify.OnBuild = (n, dir) =>
            {
                if (n == 2) File.WriteAllText(algo, "niezatwierdzony kandydat"); // kompilacja kandydata „publikuje” cBota
                return new BuildResult { Ok = true };
            };
            var job = await h.RunAsync(TriggerKind.Manual);
            Assert.Equal(JobStatus.RejectedByChecks, job.Status);
            Assert.Contains(job.Findings, f => f.Severity == FindingSeverity.Blocking && f.Rule == "Kod kandydata zmienił pliki cBotów w cTrader");
            Assert.Equal("zatwierdzona wersja", File.ReadAllText(algo));
        }
    }

    // ------------------------------------------------------------------ elementy potoku

    public class AiLabPipelineUnitTests
    {
        [Fact]
        public void BaselineCache_StoresOnlyCompleteEntries_AndKeyIncludesDataAndBacktestFlag()
        {
            using var tmp = new LabTempDir("apex-base");
            string data = tmp.Sub("dane");
            File.WriteAllText(Path.Combine(data, "US500_H1.csv"), "a\n");
            var cache = new BaselineCache(tmp.Sub("lab"));

            var incomplete = BaselineCache.NewEntry("abc123", data, true);
            incomplete.BuildOk = true;
            incomplete.Tests = new TestResult { Ran = true, Total = 5, Passed = 5, TimedOut = true };
            incomplete.Backtest = FakeVerification.Metrics();
            Assert.False(cache.Put(incomplete));
            incomplete.Tests.TimedOut = false;
            incomplete.Backtest = null;
            incomplete.BacktestMessage = "Przekroczony czas backtestu.";
            Assert.False(cache.Put(incomplete)); // awaria backtestu ≠ pominięcie z powodu ustawień
            Assert.Null(cache.Get("abc123", data, true));

            var full = BaselineCache.NewEntry("abc123", data, true);
            full.BuildOk = true;
            full.Tests = FakeVerification.Passing();
            full.Backtest = FakeVerification.Metrics();
            Assert.True(cache.Put(full));
            Assert.NotNull(cache.Get("abc123", data, true));
            Assert.Null(cache.Get("abc123", data, false)); // inne ustawienie backtestu = inny klucz

            File.WriteAllText(Path.Combine(data, "NAS100_H1.csv"), "nowe dane\n"); // dodane pliki danych
            Assert.Null(cache.Get("abc123", data, true));

            var skip = BaselineCache.NewEntry("def456", "", true);
            skip.BuildOk = true;
            skip.Tests = FakeVerification.Passing();
            skip.BacktestConfigSkip = true;
            Assert.True(cache.Put(skip)); // brak danych (ustawienia) – wynik pełny
            Assert.NotNull(cache.Get("def456", "", true));
            Assert.Equal(2, cache.Clear());
            Assert.Null(cache.Get("def456", "", true));
        }

        [Fact]
        public void BaselineCache_IgnoresLegacyEntries()
        {
            using var tmp = new LabTempDir("apex-base-old");
            string lab = tmp.Sub("lab");
            var cache = new BaselineCache(lab);
            // Wpis sprzed poprawek (zapisany także po nieudanej kompilacji) – bez wersji i odcisku danych.
            File.WriteAllText(Path.Combine(lab, "bazowe", "abc.json"), "{\"Commit\":\"abc\",\"BuildOk\":false,\"DataDir\":\"\"}");
            Assert.Null(cache.Get("abc", "", true));
        }

        [Fact]
        public void TrxComparison_ByFullName()
        {
            using var tmp = new LabTempDir("apex-trx2");
            string Trx(string results) =>
                "<?xml version=\"1.0\"?><TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results>" + results + "</Results>" +
                "<TestDefinitions><UnitTest name=\"Krotka\" id=\"id-k\"><TestMethod className=\"Apex.Tests.K\" name=\"Krotka\"/></UnitTest></TestDefinitions>" +
                "<ResultSummary outcome=\"Completed\"><Counters total=\"3\" executed=\"3\" passed=\"3\" failed=\"0\" error=\"0\" timeout=\"0\" aborted=\"0\" notExecuted=\"0\"/></ResultSummary></TestRun>";
            string basePath = Path.Combine(tmp.Path, "baza.trx");
            File.WriteAllText(basePath, Trx("<UnitTestResult testId=\"x1\" testName=\"Apex.Tests.A.Straznik\" outcome=\"Passed\"/>" +
                                            "<UnitTestResult testId=\"x2\" testName=\"Apex.Tests.A.Pomijany\" outcome=\"Passed\"/>" +
                                            "<UnitTestResult testId=\"id-k\" testName=\"Krotka\" outcome=\"Passed\"/>"));
            var baseline = Verification.ParseTrx(basePath);
            Assert.Equal("Passed", baseline.Outcomes["Apex.Tests.K.Krotka"]); // nazwa uzupełniona o klasę
            Assert.Equal(3, baseline.Outcomes.Count);

            string candPath = Path.Combine(tmp.Path, "kandydat.trx");
            File.WriteAllText(candPath, Trx("<UnitTestResult testId=\"x3\" testName=\"Apex.Tests.A.Nowy\" outcome=\"Passed\"/>" +
                                            "<UnitTestResult testId=\"x2\" testName=\"Apex.Tests.A.Pomijany\" outcome=\"NotExecuted\"/>" +
                                            "<UnitTestResult testId=\"id-k\" testName=\"Krotka\" outcome=\"Passed\"/>"));
            var cand = Verification.ParseTrx(candPath);
            var f = Verification.CompareTests(baseline, cand);
            Assert.All(f, x => Assert.Equal(FindingSeverity.Blocking, x.Severity));
            Assert.Contains(f, x => x.Rule == "Brak testów z wersji bazowej" && x.Detail.Contains("Apex.Tests.A.Straznik"));
            Assert.Contains(f, x => x.Rule == "Wyłączenie testu" && x.Detail.Contains("Apex.Tests.A.Pomijany"));
            Assert.Empty(Verification.CompareTests(baseline, baseline));
        }

        [Fact]
        public void VerificationBuild_NeverPublishesAlgo()
        {
            using var tmp = new LabTempDir("apex-steps");
            foreach (var sln in new[] { "src/Platforms/cTrader/ApexBridge/ApexBridge.sln", "src/Platforms/cTrader/ApexTradingAI/ApexTradingAI.sln" })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(tmp.Path, sln))!);
                File.WriteAllText(Path.Combine(tmp.Path, sln), "");
            }
            var steps = Verification.BuildSteps(tmp.Path);
            Assert.Equal(4, steps.Count);
            Assert.All(steps, s => Assert.Contains("-p:AlgoPublish=false", s.args));
        }

        [Fact]
        public void VerificationEnvironment_RemovesSecrets_AndIsolatesDataDir()
        {
            var current = new Hashtable
            {
                ["OPENAI_API_KEY"] = "sk-x", ["ANTHROPIC_API_KEY"] = "sk-y", ["GITHUB_TOKEN"] = "t", ["MY_SECRET_VALUE"] = "s",
                ["DB_PASSWORD"] = "p", ["PATH"] = "/usr/bin", ["APEX_DATA_DIR"] = "/prawdziwe/dane",
            };
            var env = Verification.ScrubbedEnvironment("/tmp/izolacja", current);
            foreach (var k in new[] { "OPENAI_API_KEY", "ANTHROPIC_API_KEY", "GITHUB_TOKEN", "MY_SECRET_VALUE", "DB_PASSWORD" })
            {
                Assert.True(env.ContainsKey(k), k);
                Assert.Null(env[k]);
            }
            Assert.False(env.ContainsKey("PATH"));
            Assert.Equal("/tmp/izolacja", env["APEX_DATA_DIR"]);
        }

        [Fact]
        public async Task VerificationProcess_SeesNoSecrets()
        {
            bool win = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            string exe = win ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : "/usr/bin/env";
            if (!File.Exists(exe)) return;
            var cmd = new CommandSpec { FileName = exe };
            string name = "APEX_TEST_" + Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant() + "_API_KEY";
            using var tmp = new LabTempDir("apex-env");
            Environment.SetEnvironmentVariable(name, "tajne");
            try
            {
                var v = new Verification(new RepoSettings(), (k, t) => { }, tmp.Path);
                var r = await v.RunIsolatedAsync(cmd, win ? new[] { "/c", "set" } : Array.Empty<string>(), tmp.Path, TimeSpan.FromSeconds(30), CancellationToken.None);
                Assert.True(r.Ok, $"{cmd.FileName}: kod {r.ExitCode}, start {r.StartFailed} {r.StartError}, czas {r.TimedOut}: {r.Tail(5)}");
                Assert.DoesNotContain(r.Output, l => l.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(r.Output, l => l.StartsWith("APEX_DATA_DIR=", StringComparison.OrdinalIgnoreCase) && l.Contains(tmp.Path));
            }
            finally { Environment.SetEnvironmentVariable(name, null); }
        }

        [Fact]
        public void Tripwire_DetectsAndRestores()
        {
            using var tmp = new LabTempDir("apex-trip");
            string robots = tmp.Sub("Robots");
            File.WriteAllText(Path.Combine(robots, "A.algo"), "A1");
            File.WriteAllText(Path.Combine(robots, "B.algo"), "B1");
            var trip = new AlgoTripwire(new[] { robots, Path.Combine(tmp.Path, "brak") }, Path.Combine(tmp.Path, "kopia"));
            Assert.Equal(2, trip.Snapshot());
            Assert.Empty(trip.CheckAndRestore(Path.Combine(tmp.Path, "kwarantanna")));

            File.WriteAllText(Path.Combine(robots, "A.algo"), "A2");  // zmieniony
            File.Delete(Path.Combine(robots, "B.algo"));              // usunięty
            File.WriteAllText(Path.Combine(robots, "C.algo"), "C1");  // nowy
            var changes = trip.CheckAndRestore(Path.Combine(tmp.Path, "kwarantanna"));
            Assert.Equal(3, changes.Count);
            Assert.Equal("A1", File.ReadAllText(Path.Combine(robots, "A.algo")));
            Assert.Equal("B1", File.ReadAllText(Path.Combine(robots, "B.algo")));
            Assert.False(File.Exists(Path.Combine(robots, "C.algo")));
            var quarantined = Directory.GetFiles(Path.Combine(tmp.Path, "kwarantanna")).Select(File.ReadAllText).ToList();
            Assert.Contains("A2", quarantined); // nic nie jest bezpowrotnie usuwane
            Assert.Contains("C1", quarantined);
            Assert.Empty(trip.CheckAndRestore(Path.Combine(tmp.Path, "kwarantanna")));
        }

        [Fact]
        public void Analyzer_UsesOneWindowForAllStatistics()
        {
            var trades = new List<JournalTrade>();
            var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < 240; i++) trades.Add(new JournalTrade { Strategy = "MeanReversion", AssetClass = "Index", ExitUtc = t0.AddHours(i), R = 1, NetProfit = 10 });
            for (int i = 240; i < 300; i++) trades.Add(new JournalTrade { Strategy = "MeanReversion", AssetClass = "Index", ExitUtc = t0.AddHours(i), R = i % 2 == 0 ? -1.0 : -0.5, NetProfit = -5 });
            var (st, finding) = Analyzer.EvaluateStrategy("acc", "MeanReversion", "Index", trades, 0.05, "Validated", false, 15);
            Assert.Equal(60, st.Trades);
            Assert.Equal(300, st.TotalTrades);
            Assert.Equal(0, st.WinRate, 6);          // to samo okno co E[R]
            Assert.Equal(-300, st.NetProfit, 6);
            Assert.Equal(2400 - 300, st.TotalNetProfit, 6);
            Assert.True(st.ExpectancyR < 0);
            Assert.NotNull(finding);
            Assert.Contains("60 ostatnich transakcji (z 300 łącznie)", finding!.Detail);
        }

        [Fact]
        public void Budget_CountsTokensWithoutCache_AndApiBilledCliForMonthlyCap()
        {
            using var tmp = new LabTempDir("apex-bud");
            var store = new LabStore(tmp.Path);
            var cfg = new LabConfig();
            var budget = new Budget(store, () => cfg);
            var now = DateTime.UtcNow;
            cfg.Budget.MaxAgentRunsPerDay = 10;
            budget.Record(ProviderId.ClaudeCode, "j1", "agent", new UsageInfo { InputTokens = 1_000_000, CachedInputTokens = 900_000, OutputTokens = 10_000, CostUsd = 3 }, now, apiBilled: false);
            var s = budget.Compute(now);
            Assert.Equal(110_000, s.TokensToday);
            Assert.Equal(0, s.CostMonthUsd, 6); // subskrypcja – koszt szacunkowy, nie rozliczenie API

            cfg.Budget.MaxUsdPerMonth = 20;
            budget.Record(ProviderId.Codex, "j2", "agent", new UsageInfo { InputTokens = 10, OutputTokens = 10, CostUsd = 25 }, now, apiBilled: true);
            Assert.Equal(25, budget.Compute(now).CostMonthUsd, 6);
            Assert.Contains("kosztów API", budget.CanRun(ProviderId.Codex, now, manual: true, apiBilled: true));
            Assert.Null(budget.CanRun(ProviderId.ClaudeCode, now, manual: true, apiBilled: false));
        }

        [Fact]
        public void Budget_ReviewAndRepair_IgnoreAgentRunCounters()
        {
            using var tmp = new LabTempDir("apex-bud2");
            var store = new LabStore(tmp.Path);
            var cfg = new LabConfig();
            var budget = new Budget(store, () => cfg);
            var now = DateTime.UtcNow;
            cfg.Budget.MaxAgentRunsPerDay = 2;
            budget.Record(ProviderId.AnthropicApi, "j", "agent", new UsageInfo { InputTokens = 10 }, now);
            budget.Record(ProviderId.AnthropicApi, "j", "repair", new UsageInfo { InputTokens = 10 }, now);
            Assert.Equal(1, budget.Compute(now).RunsToday); // runda poprawek nie zajmuje uruchomienia
            budget.Record(ProviderId.AnthropicApi, "k", "agent", new UsageInfo { InputTokens = 10 }, now);
            Assert.Contains("dzienny limit uruchomień", budget.CanRun(ProviderId.Codex, now, true));
            Assert.Null(budget.CanReview(ProviderId.Codex, now));
            cfg.Budget.MaxTokensPerDay = 5;
            Assert.Contains("tokenów", budget.CanReview(ProviderId.Codex, now));
            cfg.Budget.MaxTokensPerDay = 0;
            budget.Record(ProviderId.ClaudeCode, "k", "review", new UsageInfo { LimitReached = true, LimitResetUtc = now.AddHours(1), LimitMessage = "usage limit" }, now);
            Assert.Contains("limit dostawcy", budget.CanReview(ProviderId.ClaudeCode, now));
        }

        [Fact]
        public void ConfigPatch_AutomationOff_IsNeverBlockedByUnrelatedPaths()
        {
            using var tmp = new LabTempDir("apex-cfgp");
            var store = new LabConfigStore(tmp.Path);
            string missing = Path.Combine(tmp.Path, "odlaczony-dysk");
            store.Update(c =>
            {
                c.AutomationEnabled = true;
                c.Repo.SourceRepoPath = missing;
                c.Repo.BacktestDataDir = missing;
                c.Budget.MaxUsdPerMonth = -5; // np. ręcznie zepsuty plik – nie może blokować wyłącznika
            });
            JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

            var (ok, msg) = Apex.AiLab.Program.SaveConfigPatch(store, J("{\"automationEnabled\":false}"));
            Assert.True(ok, msg);
            Assert.False(store.Config.AutomationEnabled);

            store.Update(c => { c.AutomationEnabled = true; c.Budget.MaxUsdPerMonth = 20; });
            (ok, msg) = Apex.AiLab.Program.SaveConfigPatch(store, J("{\"schedule\":{\"kind\":\"Off\"}}"));
            Assert.True(ok, msg);
            Assert.Equal(ScheduleKind.Off, store.Config.Schedule.Kind);

            // Zapis harmonogramu i budżetu (bez sekcji repo) – ścieżki repo nie są sprawdzane.
            (ok, msg) = Apex.AiLab.Program.SaveConfigPatch(store, J("{\"schedule\":{\"kind\":\"Daily\",\"hour\":7,\"minute\":15,\"everyHours\":24,\"minHoursBetweenAgentRuns\":24,\"marketSnapshotMinutes\":60},\"budget\":{\"maxAgentRunsPerDay\":1,\"maxAgentRunsPerWeek\":5,\"maxTokensPerDay\":1000000,\"maxUsdPerMonth\":10}}"));
            Assert.True(ok, msg);
            Assert.Equal(7, store.Config.Schedule.Hour);

            // Sekcja repo nadal jest walidowana.
            Assert.NotNull(Apex.AiLab.Program.ApplyPatch(new LabConfig(), J("{\"repo\":{\"sourceRepoPath\":\"" + missing.Replace("\\", "\\\\") + "\"}}")));
            Assert.Null(Apex.AiLab.Program.ApplyPatch(new LabConfig { Repo = { SourceRepoPath = missing } }, J("{\"automationEnabled\":true}")));
        }

        [Fact]
        public async Task StatusCache_AbortedRequest_DoesNotCacheFalseStatus()
        {
            using var tmp = new LabTempDir("apex-stc");
            var cfg = new LabConfigStore(tmp.Path);
            var reg = new ProviderRegistry(cfg);
            var request = new CancellationTokenSource();
            FakeProvider? first = null;
            foreach (var id in Enum.GetValues<ProviderId>())
            {
                var p = new FakeProvider(id, (r, c) => new AgentResult());
                // Panel zamyka połączenie w trakcie sprawdzania; sprawdzenie z tokenem zapytania dałoby „niezalogowany”.
                p.StatusOverride = ct =>
                {
                    request.Cancel();
                    return new ProviderStatus { Id = id, Installed = true, LoggedIn = !ct.IsCancellationRequested };
                };
                reg.Override(p);
                first ??= p;
            }
            var cache = new StatusCache(reg);
            var (statuses, _, _) = await cache.GetAsync(true, request.Token);
            Assert.All(statuses, s => Assert.True(s.LoggedIn));
            int calls = first!.StatusCalls;
            var (again, _, _) = await cache.GetAsync(false, CancellationToken.None);
            Assert.Equal(calls, first.StatusCalls); // z pamięci podręcznej
            Assert.All(again, s => Assert.True(s.LoggedIn));
        }

        [Fact]
        public async Task StatusCache_RefreshesAfterDeviceLoginFinished()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return; // atrapa programu codex jako skrypt powłoki
            using var tmp = new LabTempDir("apex-login");
            string script = Path.Combine(tmp.Path, "codex");
            File.WriteAllText(script, "#!/bin/sh\necho 'Otwórz https://auth.example.test/device i wpisz ABCD-12345'\nexit 0\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var cfg = new LabConfigStore(tmp.Sub("lab"));
            cfg.Update(c => c.Provider(ProviderId.Codex).ExecutablePath = script);
            var reg = new ProviderRegistry(cfg);
            var fakes = Enum.GetValues<ProviderId>().Select(id => new FakeProvider(id, (r, c) => new AgentResult())).ToList();
            foreach (var f in fakes) reg.Override(f);
            var cache = new StatusCache(reg);
            await cache.GetAsync(false, CancellationToken.None);
            Assert.Equal(1, fakes[0].StatusCalls);

            reg.Codex.StartDeviceLogin(); // bez Invalidate – odświeżenie ma wynikać z zakończenia logowania
            var sw = Stopwatch.StartNew();
            while (reg.Codex.Login is { Running: true } && sw.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(20);
            Assert.False(reg.Codex.Login!.Running);
            Assert.True(cache.LoginFinishedSinceCheck());
            await cache.GetAsync(false, CancellationToken.None);
            Assert.Equal(2, fakes[0].StatusCalls);
            await cache.GetAsync(false, CancellationToken.None);
            Assert.Equal(2, fakes[0].StatusCalls); // jednorazowo
        }
    }

    // ------------------------------------------------------------------ harmonogram

    public class AiLabSchedulerTests
    {
        private sealed class SchedulerHarness : IDisposable
        {
            private readonly LabTempDir _tmp = new("apex-sched");

            public SchedulerHarness()
            {
                Cfg = new LabConfigStore(_tmp.Path);
                Store = new LabStore(_tmp.Path);
                var analyzer = new Analyzer(new DesktopClient(_tmp.Path), Store);
                Runner = new JobRunner(Cfg, Store, analyzer, new GitWorkspace(_tmp.Path), new Budget(Store, () => Cfg.Config), new ProviderRegistry(Cfg), new BaselineCache(_tmp.Path),
                    (r, d, l) => new FakeVerification())
                {
                    AlgoRobotsDirs = () => Array.Empty<string>(),
                };
                Scheduler = new Scheduler(Cfg, Store, analyzer, Runner);
            }

            public LabConfigStore Cfg { get; }
            public LabStore Store { get; }
            public JobRunner Runner { get; }
            public Scheduler Scheduler { get; }

            public async Task WaitIdle()
            {
                var sw = Stopwatch.StartNew();
                while (Runner.RunningJobId != null && sw.Elapsed < TimeSpan.FromSeconds(60)) await Task.Delay(50);
            }

            public void Dispose() => _tmp.Dispose();
        }

        [Fact]
        public async Task ScheduledSlot_IsMarkedOnlyAfterSuccessfulStart()
        {
            using var h = new SchedulerHarness();
            var now = DateTime.UtcNow;
            h.Cfg.Update(c =>
            {
                c.AutomationEnabled = true;
                c.Schedule.Kind = ScheduleKind.EveryHours;
                c.Schedule.EveryHours = 1;
                c.Schedule.EnabledSinceUtc = now.AddHours(-3);
                c.Schedule.TriggerOnStrategyDegradation = c.Schedule.TriggerOnMarketChange = c.Schedule.TriggerOnExecutionErrors = false;
            });
            // Trwa wdrożenie (status ustawia Deployer – JobRunner nie ma zadania w toku).
            var deploying = new LabJob { Id = "wdrozenie", CreatedUtc = now, Status = JobStatus.Deploying };
            h.Store.SaveJob(deploying);
            await h.Scheduler.TickAsync(now, CancellationToken.None);
            Assert.Null(h.Store.LastScheduledRunUtc);
            Assert.Null(h.Runner.RunningJobId);

            deploying.Status = JobStatus.Deployed;
            h.Store.SaveJob(deploying);
            await h.Scheduler.TickAsync(now, CancellationToken.None);
            Assert.Equal(now, h.Store.LastScheduledRunUtc);
            await h.WaitIdle();
            Assert.Contains(h.Store.Jobs(), j => j.Trigger == TriggerKind.Schedule && j.Status == JobStatus.AnalysisOnly);
        }

        [Fact]
        public void ReEnablingAutomation_DoesNotFireOverdueSlot()
        {
            using var h = new SchedulerHarness();
            var lastRun = new DateTime(2026, 10, 5, 4, 30, 0, DateTimeKind.Utc);
            var enabled = new DateTime(2026, 10, 12, 13, 0, 0, DateTimeKind.Utc);
            h.Store.MarkScheduledRun(lastRun);
            h.Cfg.Update(c =>
            {
                c.AutomationEnabled = true;
                c.Schedule.Kind = ScheduleKind.Daily;
                c.Schedule.Hour = 6;
                c.Schedule.Minute = 30;
                c.Schedule.EnabledSinceUtc = enabled;
            });
            var now = enabled.AddMinutes(1);
            var next = h.Scheduler.NextScheduledUtc(now);
            Assert.NotNull(next);
            Assert.True(next!.Value > now, "termin sprzed wyłączenia automatyzacji nie może uruchomić analizy od razu");
            Assert.Equal(enabled, Scheduler.Anchor(lastRun, enabled));
            Assert.Equal(lastRun, Scheduler.Anchor(lastRun, null));
            Assert.Equal(enabled, Scheduler.Anchor(null, enabled));
        }
    }
}
