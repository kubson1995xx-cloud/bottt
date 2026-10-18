using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apex.AiLab.Analysis;
using Apex.AiLab.Lab;
using Apex.AiLab.Providers;
using Apex.AiLab.Scheduling;
using Apex.Core.Adaptive;

namespace Apex.AiLab.Pipeline
{
    public sealed class ProviderRegistry
    {
        private readonly Dictionary<ProviderId, IAiProvider> _all;

        public ProviderRegistry(LabConfigStore cfg)
        {
            Codex = new CodexCliProvider(() => cfg.Config.Provider(ProviderId.Codex));
            Claude = new ClaudeCodeProvider(() => cfg.Config.Provider(ProviderId.ClaudeCode));
            OpenAi = new OpenAiApiProvider(() => cfg.Config.Provider(ProviderId.OpenAiApi), () => cfg.Config.Policy.ForbiddenPaths);
            Anthropic = new AnthropicApiProvider(() => cfg.Config.Provider(ProviderId.AnthropicApi), () => cfg.Config.Policy.ForbiddenPaths);
            _all = new Dictionary<ProviderId, IAiProvider>
            {
                [ProviderId.Codex] = Codex,
                [ProviderId.ClaudeCode] = Claude,
                [ProviderId.OpenAiApi] = OpenAi,
                [ProviderId.AnthropicApi] = Anthropic,
            };
        }

        public CodexCliProvider Codex { get; }
        public ClaudeCodeProvider Claude { get; }
        public OpenAiApiProvider OpenAi { get; }
        public AnthropicApiProvider Anthropic { get; }

        /// <summary>Podmiana dostawcy (testy).</summary>
        public void Override(IAiProvider p) => _all[p.Id] = p;

        public IAiProvider Get(ProviderId id) => _all[id];
        public IEnumerable<IAiProvider> All => _all.Values;
    }

    /// <summary>Werdykt niezależnego przeglądu drugim modelem.</summary>
    public enum ReviewVerdict
    {
        /// <summary>Brak jednoznacznego werdyktu.</summary>
        Unknown,
        Accept,
        NeedsChanges,
        Reject,
    }

    /// <summary>
    /// Wykonanie zadania AI Lab. Kolejność: analiza → wybór dostawcy (limity) → izolowana kopia → wersja bazowa (testy, backtest)
    /// → agent → audyt → kompilacja → testy → backtest → przegląd → propozycja. Nic nie trafia do działającego silnika.
    /// </summary>
    public sealed class JobRunner
    {
        private readonly LabConfigStore _cfg;
        private readonly LabStore _store;
        private readonly Analyzer _analyzer;
        private readonly GitWorkspace _ws;
        private readonly Budget _budget;
        private readonly ProviderRegistry _providers;
        private readonly BaselineCache _baseline;
        private readonly Func<RepoSettings, string, Action<string, string>, IVerification> _verification;
        private readonly object _lock = new();
        private CancellationTokenSource? _cts;
        private string? _running;

        /// <param name="verificationFactory">Tworzenie weryfikacji dla zadania: (ustawienia repozytorium, katalog zadania, dziennik). Domyślnie <see cref="Verification"/>.</param>
        public JobRunner(LabConfigStore cfg, LabStore store, Analyzer analyzer, GitWorkspace ws, Budget budget, ProviderRegistry providers, BaselineCache baseline,
            Func<RepoSettings, string, Action<string, string>, IVerification>? verificationFactory = null)
        {
            _cfg = cfg;
            _store = store;
            _analyzer = analyzer;
            _ws = ws;
            _budget = budget;
            _providers = providers;
            _baseline = baseline;
            _verification = verificationFactory ?? ((repo, jobDir, log) => new Verification(repo, log, Path.Combine(jobDir, "izolacja")));
        }

        public string? RunningJobId { get { lock (_lock) return _running; } }

        /// <summary>Foldery zainstalowanych cBotów cTrader pilnowane podczas zadania (domyślnie tylko Windows; podmiana w testach).</summary>
        public Func<IEnumerable<string>> AlgoRobotsDirs { get; set; } = AlgoTripwire.DefaultRobotsDirs;

        /// <summary>Stan jednego wykonania zadania (statusy dostawców, uruchomienia agenta, zabezpieczenie cBotów).</summary>
        private sealed class RunContext
        {
            public int AgentCalls;
            public readonly Dictionary<ProviderId, ProviderStatus> Statuses = new();
            public AlgoTripwire? Tripwire;
        }

        /// <summary>Rozpoczyna zadanie. Zwraca identyfikator lub null, gdy inne zadanie jest w toku.</summary>
        public string? Start(TriggerKind trigger, string detail, bool runAgent, ProviderId? provider)
        {
            lock (_lock)
            {
                if (_running != null || _store.AnyRunning()) return null;
                var job = new LabJob
                {
                    Id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 4),
                    CreatedUtc = DateTime.UtcNow,
                    Trigger = trigger,
                    TriggerDetail = detail,
                    RunAgent = runAgent,
                    Provider = provider,
                    Stages = Enum.GetValues(typeof(JobStage)).Cast<JobStage>().Select(s => new StageInfo { Stage = s }).ToList(),
                };
                _store.SaveJob(job);
                _running = job.Id;
                _cts = new CancellationTokenSource();
                var ct = _cts.Token;
                _ = Task.Run(async () =>
                {
                    try { await ExecuteAsync(job, ct).ConfigureAwait(false); }
                    finally
                    {
                        lock (_lock) { _running = null; _cts?.Dispose(); _cts = null; }
                    }
                });
                return job.Id;
            }
        }

        public bool Cancel(string id)
        {
            lock (_lock)
            {
                if (_running != id || _cts == null) return false;
                _cts.Cancel();
                return true;
            }
        }

        private void Ev(LabJob job, string kind, string text) => _store.AddEvent(job.Id, kind, text);

        private StageInfo Stage(LabJob job, JobStage s) => job.Stages.First(x => x.Stage == s);

        private void Begin(LabJob job, JobStage s, string text)
        {
            var st = Stage(job, s);
            st.Status = StageStatus.Running;
            st.StartedUtc = DateTime.UtcNow;
            st.Summary = text;
            _store.SaveJob(job);
            Ev(job, "stage", text);
        }

        private void End(LabJob job, JobStage s, StageStatus status, string summary)
        {
            var st = Stage(job, s);
            st.Status = status;
            st.FinishedUtc = DateTime.UtcNow;
            st.Summary = summary;
            _store.SaveJob(job);
            Ev(job, status == StageStatus.Failed ? "error" : status == StageStatus.Warning ? "warning" : "stage", summary);
        }

        private void Finish(LabJob job, JobStatus status, string text)
        {
            job.Status = status;
            job.StatusText = text;
            job.FinishedUtc = DateTime.UtcNow;
            foreach (var s in job.Stages.Where(s => s.Status == StageStatus.Pending)) s.Status = StageStatus.Skipped;
            foreach (var s in job.Stages.Where(s => s.Status == StageStatus.Running)) { s.Status = StageStatus.Failed; s.FinishedUtc = DateTime.UtcNow; }
            _store.SaveJob(job);
            Ev(job, status is JobStatus.ReadyForApproval or JobStatus.AnalysisOnly or JobStatus.NoChanges ? "stage" : "warning", "Zakończono: " + text);
        }

        private async Task ExecuteAsync(LabJob job, CancellationToken ct)
        {
            var cfg = _cfg.Config;
            var ctx = new RunContext();
            string jobDir = _store.JobDir(job.Id);
            Directory.CreateDirectory(jobDir);
            try
            {
                // 1. Analiza (bez AI, tylko dane z APEX Desktop i migawki rynku).
                Begin(job, JobStage.Analysis, "Analiza skuteczności strategii i warunków rynkowych…");
                var analysis = await _analyzer.AnalyzeAsync(cfg.Schedule, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                job.AnalysisId = analysis.Id;
                job.AnalysisSummary = analysis.Findings.Count == 0 ? "Brak istotnych problemów." : string.Join("; ", analysis.Findings.Select(f => f.Title));
                End(job, JobStage.Analysis, analysis.HasActionableFindings ? StageStatus.Warning : StageStatus.Ok,
                    $"Analiza: {analysis.Findings.Count} wniosków, {analysis.TotalTrades} transakcji w dziennikach" + (analysis.DataNote.Length > 0 ? " – " + analysis.DataNote : "."));

                bool wantAgent = job.RunAgent
                                 || job.Trigger is TriggerKind.StrategyDegradation or TriggerKind.MarketChange or TriggerKind.ExecutionErrors
                                 || (job.Trigger == TriggerKind.Schedule && (!cfg.Schedule.AgentOnlyWithFindings || analysis.HasActionableFindings));
                if (!wantAgent)
                {
                    Finish(job, JobStatus.AnalysisOnly, analysis.HasActionableFindings ? "Analiza zakończona – wykryto problemy (agent nie był uruchamiany w tym trybie)." : "Analiza zakończona – brak powodów do uruchomienia agenta.");
                    return;
                }

                // 2. Wybór dostawcy z uwzględnieniem limitów.
                var (provider, reason) = await PickProviderAsync(job, ctx, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (provider == null)
                {
                    Finish(job, JobStatus.Throttled, "Agent nie został uruchomiony: " + reason);
                    return;
                }
                job.Provider = provider.Id;
                job.Model = cfg.Provider(provider.Id).Model;
                _store.SaveJob(job);

                // 3. Izolowana kopia robocza.
                // (Integracja: gdy Deployer udostępni commit aktywnego wydania, nowe zadania powinny startować od niego – tutaj, zamiast baseCommit z PrepareAsync.)
                Begin(job, JobStage.Workspace, "Przygotowanie izolowanej kopii repozytorium…");
                var (baseCommit, baseRef) = await _ws.PrepareAsync(cfg.Repo.SourceRepoPath, cfg.Repo.BaseBranch, ct).ConfigureAwait(false);
                job.BaseCommit = baseCommit;
                string worktree = await _ws.CreateWorktreeAsync(job.Id, baseCommit, ct).ConfigureAwait(false);
                job.Branch = "ai/" + job.Id;
                var verify = _verification(cfg.Repo, jobDir, (k, t) => Ev(job, k, t));
                bool restored = await verify.RestoreAsync(worktree, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                End(job, JobStage.Workspace, StageStatus.Ok, $"Kopia robocza na gałęzi {job.Branch} (baza {baseRef} @ {baseCommit.Substring(0, Math.Min(10, baseCommit.Length))}).");

                // 4. Wersja bazowa – raz na commit i dane (testy i backtest do porównania). Niepełny wynik kończy zadanie przed agentem.
                Begin(job, JobStage.Baseline, "Weryfikacja wersji bazowej (kompilacja, testy, backtest)…");
                var baseline = await BaselineAsync(job, verify, worktree, jobDir, baseCommit, cfg.Repo, ct).ConfigureAwait(false);
                string? baseProblem = BaselineProblem(baseline, restored);
                if (baseProblem != null)
                {
                    End(job, JobStage.Baseline, StageStatus.Failed, baseProblem);
                    Finish(job, JobStatus.Failed, baseProblem + " Agent nie został uruchomiony (limity nie zostały zużyte).");
                    return;
                }
                End(job, JobStage.Baseline, baseline.Tests is { Failed: 0 } && baseline.Backtest != null ? StageStatus.Ok : StageStatus.Warning,
                    $"Wersja bazowa: testy {baseline.Tests?.Passed}/{baseline.Tests?.Total}; backtest: {(baseline.Backtest != null ? "OK" : baseline.BacktestMessage)}");

                // Zabezpieczenie zainstalowanych cBotów cTrader – migawka przed agentem, porównanie po weryfikacji (także przy przerwaniu).
                ctx.Tripwire = new AlgoTripwire(AlgoRobotsDirs(), Path.Combine(jobDir, "cbot-kopia"));
                int guarded;
                try { guarded = ctx.Tripwire.Snapshot(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Bez migawki nie da się wykryć ani cofnąć zmian – agent nie jest uruchamiany.
                    throw new InvalidOperationException("Nie można zabezpieczyć plików cBotów cTrader (" + ex.Message + ") – agent nie został uruchomiony.", ex);
                }
                if (ctx.Tripwire.Armed) Ev(job, "stage", $"Zabezpieczono pliki cBotów cTrader ({guarded}) – każda zmiana zostanie wykryta i cofnięta.");

                // 5. Agent AI.
                Begin(job, JobStage.Agent, $"Agent {provider.Name}{(job.Model.Length > 0 ? " (" + job.Model + ")" : "")} pracuje w kopii roboczej…");
                string task = PromptBuilder.AgentTask(job, analysis, provider.ExecutesCommands);
                if (!provider.ExecutesCommands) task += "\n\n" + PromptBuilder.ApiContext(worktree, analysis, 180_000);
                Directory.CreateDirectory(Path.Combine(worktree, ".ailab"));
                File.WriteAllText(Path.Combine(worktree, ".ailab", "ZADANIE.md"), task);
                File.WriteAllText(Path.Combine(jobDir, "zadanie.md"), task);
                AgentResult agent;
                try
                {
                    agent = await RunAgentAsync(job, ctx, provider, worktree, jobDir, task, "agent", ct).ConfigureAwait(false);
                    if (!agent.Ok && agent.Usage.LimitReached)
                    {
                        // Limit wyczerpany: próba u dostawcy zapasowego, jeśli kopia robocza jest nietknięta.
                        var fallback = await FallbackAsync(job, ctx, provider, ct).ConfigureAwait(false);
                        var pending = await _ws.CollectChangesAsync(worktree, baseCommit, ct).ConfigureAwait(false);
                        if (fallback != null && pending.Count == 0)
                        {
                            Ev(job, "warning", $"Limit {provider.Name} wyczerpany – przełączam na {fallback.Name}.");
                            provider = fallback;
                            job.Provider = provider.Id;
                            job.Model = cfg.Provider(provider.Id).Model;
                            task = PromptBuilder.AgentTask(job, analysis, provider.ExecutesCommands);
                            if (!provider.ExecutesCommands) task += "\n\n" + PromptBuilder.ApiContext(worktree, analysis, 180_000);
                            File.WriteAllText(Path.Combine(worktree, ".ailab", "ZADANIE.md"), task);
                            agent = await RunAgentAsync(job, ctx, provider, worktree, jobDir, task, "agent", ct).ConfigureAwait(false);
                        }
                        else if (fallback != null)
                            Ev(job, "warning", $"Dostawca zapasowy {fallback.Name} pominięty – kopia robocza zawiera już zmiany przerwanego agenta.");
                    }
                }
                finally
                {
                    // Odstęp między automatycznymi uruchomieniami liczony od OSTATECZNEGO wyniku agenta (także po przełączeniu
                    // na dostawcę zapasowego) – oznaczenie przed próbą zapasową blokowało ją regułą minimalnego odstępu.
                    if (ctx.AgentCalls > 0) _store.MarkAgentRun(DateTime.UtcNow);
                }
                job.AgentSummary = agent.Summary;
                job.Title = Title(agent.Summary, job);
                if (!agent.Ok)
                {
                    End(job, JobStage.Agent, StageStatus.Failed, agent.Error);
                    Finish(job, agent.Usage.LimitReached ? JobStatus.Throttled : JobStatus.Failed, agent.Error);
                    return;
                }
                End(job, JobStage.Agent, StageStatus.Ok, $"Agent zakończył pracę ({agent.Usage.DurationSec:F0} s, {agent.Usage.TotalTokens:N0} tokenów).");

                // 6–8. Audyt, kompilacja, testy (dla dostawców API z rundami poprawek).
                int maxRepair = provider.ExecutesCommands ? 0 : cfg.Provider(provider.Id).MaxRepairRounds;
                var active = ActiveStrategies(worktree);
                string diff = "";
                for (int round = 0; ; round++)
                {
                    ct.ThrowIfCancellationRequested();
                    job.Files = await _ws.CollectChangesAsync(worktree, baseCommit, ct).ConfigureAwait(false);
                    if (job.Files.Count == 0)
                    {
                        Finish(job, JobStatus.NoChanges, "Agent nie zaproponował zmian w kodzie (wnioski w podsumowaniu).");
                        return;
                    }
                    diff = await _ws.DiffAsync(worktree, baseCommit, ct).ConfigureAwait(false);
                    File.WriteAllText(Path.Combine(jobDir, "zmiana.diff"), diff);
                    Begin(job, JobStage.Audit, "Audyt zmian…");
                    // (Integracja: ChangeAudit.Audit otrzyma opcjonalny parametr kopii roboczej – przekazać tu: worktree.)
                    job.Findings = ChangeAudit.Audit(job.Files, diff, cfg.Policy, active);
                    job.ProtectedTouched = job.Files.Any(f => f.Protected);
                    int blocking = job.Findings.Count(f => f.Severity == FindingSeverity.Blocking);
                    End(job, JobStage.Audit, blocking > 0 ? StageStatus.Failed : job.Findings.Any(f => f.Severity is FindingSeverity.Protected or FindingSeverity.Warning) ? StageStatus.Warning : StageStatus.Ok,
                        $"Audyt: {job.Files.Count} plików (+{job.Files.Sum(f => f.Added)}/−{job.Files.Sum(f => f.Removed)}), blokujących {blocking}, chronionych {job.Files.Count(f => f.Protected)}.");
                    if (blocking > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        await CommitAsync(job, worktree).ConfigureAwait(false);
                        Finish(job, JobStatus.RejectedByChecks, "Odrzucona przez audyt: " + string.Join("; ", job.Findings.Where(f => f.Severity == FindingSeverity.Blocking).Select(f => f.Rule).Distinct()));
                        return;
                    }

                    Begin(job, JobStage.Build, "Kompilacja…");
                    job.Build = await verify.BuildAsync(worktree, ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    End(job, JobStage.Build, job.Build.Ok ? StageStatus.Ok : StageStatus.Failed, string.Join("; ", job.Build.Steps));
                    if (job.Build.Ok)
                    {
                        Begin(job, JobStage.Tests, "Testy regresyjne (jednostkowe, silnika, integracyjne)…");
                        job.Tests = await verify.TestAsync(worktree, Path.Combine(jobDir, "testy"), ct).ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested();
                        job.Tests.BaselineTotal = baseline.Tests?.Total;
                        job.Tests.BaselinePassed = baseline.Tests?.Passed;
                        bool fewer = baseline.Tests != null && job.Tests.Total < baseline.Tests.Total;
                        if (fewer)
                            job.Findings.Add(new AuditFinding { Severity = FindingSeverity.Blocking, Rule = "Mniej testów niż w wersji bazowej", Detail = $"{job.Tests.Total} < {baseline.Tests!.Total}" });
                        // Porównanie po nazwach: każdy test bazowy musi istnieć i (jeśli był zaliczany) nadal być zaliczany.
                        var byName = Verification.CompareTests(baseline.Tests, job.Tests);
                        job.Findings.AddRange(byName);
                        End(job, JobStage.Tests, job.Tests.Failed == 0 && !fewer && byName.Count == 0 ? StageStatus.Ok : StageStatus.Failed,
                            $"Testy: {job.Tests.Passed}/{job.Tests.Total} zaliczonych (wersja bazowa {baseline.Tests?.Passed}/{baseline.Tests?.Total})" +
                            (byName.Count > 0 ? "; " + string.Join("; ", byName.Select(f => f.Rule)) + "." : "."));
                    }
                    bool failed = !job.Build.Ok || job.Tests == null || job.Tests.Failed > 0;
                    if (failed && round < maxRepair && Budget.IsApiBilled(provider.Id))
                    {
                        // Runda poprawek kontynuuje to samo zadanie: bez liczników uruchomień, z limitem tokenów/kosztów i limitem dostawcy.
                        string? block = _budget.CanContinueJob(provider.Id, DateTime.UtcNow, Billed(provider, ctx));
                        if (block != null)
                        {
                            Ev(job, "warning", "Runda poprawek pominięta: " + block);
                            break;
                        }
                        ct.ThrowIfCancellationRequested();
                        Ev(job, "stage", $"Runda poprawek {round + 1}/{maxRepair}: przekazuję błędy modelowi.");
                        string repair = PromptBuilder.RepairTask(job.AgentSummary, job.Build, job.Tests, worktree, job.Files.Select(f => f.Path)) + "\n\n" + PromptBuilder.ApiContext(worktree, analysis, 60_000);
                        var fix = await RunAgentAsync(job, ctx, provider, worktree, jobDir, repair, "repair", ct).ConfigureAwait(false);
                        if (fix.Ok && fix.Summary.Length > 0) job.AgentSummary = fix.Summary;
                        if (!fix.Ok) break;
                        job.Tests = null;
                        continue;
                    }
                    break;
                }
                ct.ThrowIfCancellationRequested();
                await CommitAsync(job, worktree).ConfigureAwait(false);
                // Kod kandydata został już wykonany (kompilacja, testy) – sprawdzenie plików cBotów przed decyzją.
                bool tampered = TripwireCheck(job, ctx);
                if (job.Build == null || !job.Build.Ok)
                {
                    Finish(job, JobStatus.RejectedByChecks, tampered ? TamperedText : "Odrzucona: błąd kompilacji.");
                    return;
                }
                if (tampered || job.Tests == null || job.Tests.Failed > 0 || job.Findings.Any(f => f.Severity == FindingSeverity.Blocking))
                {
                    Finish(job, JobStatus.RejectedByChecks, tampered ? TamperedText : TestsRejectText(job));
                    return;
                }

                // 9. Backtest – porównanie z wersją bazową.
                Begin(job, JobStage.Backtest, "Backtest portfela i porównanie z wersją bazową…");
                job.Backtest = new BacktestComparison { HoldoutUses = _store.HoldoutUses };
                if (baseline.Backtest == null)
                {
                    // Tu trafia wyłącznie pominięcie z powodu ustawień lub braku danych (błędy wersji bazowej kończą zadanie wcześniej).
                    job.Backtest.Reason = baseline.BacktestMessage;
                    End(job, JobStage.Backtest, StageStatus.Skipped, "Backtest pominięty: " + baseline.BacktestMessage);
                }
                else
                {
                    var cand = await verify.RunBacktestAsync(worktree, Path.Combine(jobDir, "backtest"), ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (cand.Metrics == null)
                    {
                        // Awaria, przekroczenie czasu albo brak backtestera po zmianie = kandydat odrzucony (nie "weryfikacja niepełna").
                        job.Backtest.Reason = cand.Message;
                        End(job, JobStage.Backtest, StageStatus.Failed, "Backtest kandydata nieudany: " + cand.Message);
                        Finish(job, JobStatus.RejectedByChecks, TripwireCheck(job, ctx) ? TamperedText : "Odrzucona: backtest kandydata nieudany – " + cand.Message);
                        return;
                    }
                    job.Backtest.Ran = true;
                    job.Backtest.Baseline = baseline.Backtest;
                    job.Backtest.Candidate = cand.Metrics;
                    Verification.Compare(job.Backtest);
                    if (job.Backtest.Verdict != "bez zmian")
                    {
                        job.Backtest.HoldoutUses = _store.RegisterHoldoutUse();
                        job.Backtest.Notes.Add($"Okres odłożony użyto już {job.Backtest.HoldoutUses} raz(y) do oceny zmian – jego wiarygodność maleje z każdą próbą.");
                    }
                    End(job, JobStage.Backtest, job.Backtest.Verdict == "gorszy" ? StageStatus.Failed : job.Backtest.Verdict == "niejednoznaczny" ? StageStatus.Warning : StageStatus.Ok,
                        "Backtest: " + job.Backtest.Verdict + ". " + string.Join(" ", job.Backtest.Notes.Take(2)));
                }
                if (TripwireCheck(job, ctx))
                {
                    Finish(job, JobStatus.RejectedByChecks, TamperedText);
                    return;
                }
                if (job.Backtest.Verdict == "gorszy")
                {
                    Finish(job, JobStatus.RejectedByChecks, "Odrzucona: backtest gorszy niż wersja bazowa.");
                    return;
                }

                // 10. Niezależny przegląd drugim modelem (opcjonalnie).
                string? reviewIssue = await ReviewAsync(job, ctx, worktree, jobDir, diff, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

                var issues = new List<string>();
                if (!job.Backtest.Ran) issues.Add("weryfikacja niepełna (" + job.Backtest.Reason + ")");
                if (reviewIssue != null) issues.Add(reviewIssue);
                if (issues.Count == 0) Finish(job, JobStatus.ReadyForApproval, "Gotowa do Twojej decyzji – wszystkie kontrole zaliczone.");
                else Finish(job, JobStatus.NeedsAttention, "Kontrole automatyczne zaliczone, ale " + string.Join("; ", issues) + " – decyzja wymaga ostrożności.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Finish(job, JobStatus.Cancelled, "Przerwano na żądanie użytkownika.");
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                // Przerwany proces (np. git) kończy się błędem – przyczyną jest jednak decyzja użytkownika.
                Finish(job, JobStatus.Cancelled, "Przerwano na żądanie użytkownika.");
            }
            catch (Exception ex)
            {
                Finish(job, JobStatus.Failed, "Błąd: " + ex.Message);
            }
            finally
            {
                try
                {
                    // Każda ścieżka zakończenia (błąd, przerwanie, odrzucenie) – sprawdzenie i ewentualne cofnięcie zmian w cBotach.
                    if (TripwireCheck(job, ctx) && job.Status is JobStatus.ReadyForApproval or JobStatus.NeedsAttention)
                    {
                        job.Status = JobStatus.RejectedByChecks;
                        job.StatusText = TamperedText;
                        _store.SaveJob(job);
                    }
                }
                catch (Exception ex) { Ev(job, "error", "Nie udało się sprawdzić plików cBotów cTrader: " + ex.Message); }
                try
                {
                    var keep = new HashSet<string>(_store.Jobs().Where(j => j.Status is JobStatus.ReadyForApproval or JobStatus.NeedsAttention).Select(j => j.Id));
                    await _ws.PruneWorktreesAsync(_cfg.Config.Repo.KeepWorktrees, keep, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception) { /* sprzątanie nie może zmienić wyniku zadania */ }
            }
        }

        private const string TamperedText = "Odrzucona: kod kandydata zmienił pliki cBotów w cTrader (przywrócono poprzednie wersje).";

        private static string TestsRejectText(LabJob job)
        {
            bool removed = job.Findings.Any(f => f.Rule.StartsWith("Mniej testów", StringComparison.Ordinal) || f.Rule == "Brak testów z wersji bazowej");
            bool disabled = job.Findings.Any(f => f.Rule == "Wyłączenie testu");
            return "Odrzucona: testy regresyjne niezaliczone" + (removed && disabled ? " (usunięto lub wyłączono testy)." : removed ? " (usunięto testy)." : disabled ? " (wyłączono testy)." : ".");
        }

        /// <summary>
        /// Porównanie plików cBotów z migawką; przy zmianie: cofnięcie, ustalenie blokujące i zdarzenie. Zwraca true, gdy wykryto zmianę
        /// (teraz lub wcześniej w tym zadaniu).
        /// </summary>
        private bool TripwireCheck(LabJob job, RunContext ctx)
        {
            const string rule = "Kod kandydata zmienił pliki cBotów w cTrader";
            if (ctx.Tripwire != null && ctx.Tripwire.Armed)
            {
                var changes = ctx.Tripwire.CheckAndRestore(Path.Combine(_store.JobDir(job.Id), "cbot-kwarantanna"));
                if (changes.Count > 0)
                {
                    job.Findings.Add(new AuditFinding
                    {
                        Severity = FindingSeverity.Blocking,
                        Rule = rule,
                        File = string.Join(", ", ctx.Tripwire.Dirs),
                        Detail = string.Join("; ", changes),
                    });
                    _store.SaveJob(job);
                    Ev(job, "error", rule + ": " + string.Join("; ", changes));
                }
            }
            return job.Findings.Any(f => f.Rule == rule);
        }

        /// <summary>Wynik wersji bazowej z pamięci (pełny) albo policzony teraz; do pamięci trafia tylko wynik pełny.</summary>
        private async Task<BaselineCache.Entry> BaselineAsync(LabJob job, IVerification verify, string worktree, string jobDir, string baseCommit, RepoSettings repo, CancellationToken ct)
        {
            var cached = _baseline.Get(baseCommit, repo.BacktestDataDir, repo.RunBacktests);
            if (cached != null)
            {
                Ev(job, "stage", $"Wersja bazowa z pamięci podręcznej (wynik z {cached.TimeUtc.ToLocalTime():yyyy-MM-dd HH:mm}).");
                return cached;
            }
            var e = BaselineCache.NewEntry(baseCommit, repo.BacktestDataDir, repo.RunBacktests);
            var bb = await verify.BuildAsync(worktree, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            e.BuildOk = bb.Ok;
            e.BuildTimedOut = bb.TimedOut;
            if (bb.Ok)
            {
                e.Tests = await verify.TestAsync(worktree, Path.Combine(jobDir, "testy-baza"), ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var bt = await verify.RunBacktestAsync(worktree, Path.Combine(jobDir, "backtest-baza"), ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                e.Backtest = bt.Metrics;
                e.BacktestMessage = bt.Message;
                e.BacktestConfigSkip = bt.Metrics == null && bt.IsConfigSkip;
            }
            if (!_baseline.Put(e))
            {
                BaselineCache.IsComplete(e, out string why);
                Ev(job, "warning", "Wynik wersji bazowej niepełny (" + why + ") – nie zapisano go w pamięci podręcznej; zostanie policzony ponownie.");
            }
            return e;
        }

        /// <summary>Powód, dla którego wersja bazowa nie pozwala na porównanie (null = można kontynuować).</summary>
        private static string? BaselineProblem(BaselineCache.Entry b, bool restored)
        {
            if (!b.BuildOk)
                return b.BuildTimedOut ? "Kompilacja wersji bazowej przekroczyła limit czasu – zwiększ limit w ustawieniach."
                     : restored ? "Wersja bazowa się nie kompiluje – najpierw napraw repozytorium źródłowe."
                     : "Wersja bazowa się nie kompiluje (przywracanie pakietów NuGet nie powiodło się – sprawdź połączenie z siecią).";
            if (b.Tests == null || !b.Tests.Ran || b.Tests.TimedOut || b.Tests.Cancelled)
                return "Testy wersji bazowej nie zostały wykonane" + (b.Tests is { TimedOut: true } ? " (przekroczony limit czasu – zwiększ limit w ustawieniach)" : "") + " – porównanie niemożliwe.";
            if (b.Backtest == null && !b.BacktestConfigSkip)
                return "Backtest wersji bazowej nieudany (" + b.BacktestMessage + ") – porównanie niemożliwe.";
            return null;
        }

        private async Task CommitAsync(LabJob job, string worktree)
        {
            if (!string.IsNullOrEmpty(job.HeadCommit)) return;
            string msg = "AI Lab: " + job.Title + "\n\nZadanie " + job.Id + " (" + PromptBuilder.TriggerName(job.Trigger) + "), dostawca " + job.Provider + ".\n\n" + job.AgentSummary;
            // Bez tokenu przerwania: zabicie git w trakcie commitu zostawiałoby blokadę indeksu w kopii roboczej.
            job.HeadCommit = await _ws.CommitAsync(worktree, msg, CancellationToken.None).ConfigureAwait(false);
            _store.SaveJob(job);
        }

        /// <summary>
        /// Uruchomienie dostawcy. kind: agent | repair | review. Przerwanie przed wywołaniem nie jest zapisywane w zużyciu;
        /// przerwanie w trakcie – zapisywane (zużycie mogło nastąpić), po czym zadanie kończy się jako przerwane.
        /// </summary>
        private async Task<AgentResult> RunAgentAsync(LabJob job, RunContext ctx, IAiProvider provider, string worktree, string jobDir, string task, string kind, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var settings = _cfg.Config.Provider(provider.Id);
            var req = new AgentRequest
            {
                JobId = job.Id,
                WorkDir = worktree,
                JobDir = jobDir,
                Prompt = task,
                Model = settings.Model,
                Settings = settings,
                ReadOnly = kind == "review",
                Timeout = TimeSpan.FromMinutes(settings.TimeoutMinutes),
            };
            if (kind == "agent") ctx.AgentCalls++;
            var sw = Stopwatch.StartNew();
            var result = await provider.RunAsync(req, (k, t) => Ev(job, k, t), ct).ConfigureAwait(false);
            bool cancelled = ct.IsCancellationRequested;
            if (cancelled && !result.Ok && result.Usage.TotalTokens == 0 && sw.Elapsed < TimeSpan.FromSeconds(1))
            {
                // Przerwano tuż przed wywołaniem – dostawca nic nie wykonał; bez wpisu w zużyciu i bez znacznika uruchomienia.
                if (kind == "agent") ctx.AgentCalls--;
                throw new OperationCanceledException(ct);
            }
            if (result.Usage.CostUsd == null) result.Usage.CostUsd = ApiProviderBase.Cost(result.Usage, settings);
            job.Usage.Add(result.Usage);
            // Próba odrzucona przez limit dostawcy (bez pracy) nie zajmuje dziennego uruchomienia – inaczej blokowałaby dostawcę zapasowego.
            string recKind = kind == "agent" && !result.Ok && result.Usage.LimitReached ? "agent-limit" : kind;
            _budget.Record(provider.Id, job.Id, recKind, result.Usage, DateTime.UtcNow, Billed(provider, ctx));
            _store.SaveJob(job);
            if (cancelled) throw new OperationCanceledException(ct);
            return result;
        }

        /// <summary>Rozliczenie za tokeny: dostawcy API zawsze; Codex/Claude Code, gdy bieżące logowanie nie jest subskrypcją (klucz API, Console).</summary>
        private static bool Billed(IAiProvider p, ProviderStatus? st) =>
            !p.ExecutesCommands || Budget.IsApiBilled(p.Id) || (st != null && st.LoggedIn && !st.UsesSubscription);

        private static bool Billed(IAiProvider p, RunContext ctx) => Billed(p, ctx.Statuses.TryGetValue(p.Id, out var st) ? st : null);

        /// <summary>
        /// Czy dostawca może teraz pracować: włączony, limity (manual=false – także minimalny odstęp automatycznych uruchomień),
        /// zainstalowany i zalogowany, a dla API – z wybranym modelem. Wspólne dla wyboru dostawcy i dostawcy zapasowego.
        /// </summary>
        private async Task<(IAiProvider? provider, string reason)> UsableAsync(ProviderId id, bool manual, RunContext ctx, CancellationToken ct)
        {
            var cfg = _cfg.Config;
            if (!cfg.Provider(id).Enabled) return (null, "wyłączony");
            // Najpierw limity niewymagające uruchamiania procesów.
            string? block = _budget.CanRun(id, DateTime.UtcNow, manual);
            if (block != null) return (null, block);
            var p = _providers.Get(id);
            var st = await p.StatusAsync(ct).ConfigureAwait(false);
            ctx.Statuses[id] = st;
            if (!st.Installed || !st.LoggedIn) return (null, st.Installed ? "brak logowania / klucza" : "nie zainstalowano");
            if (!p.ExecutesCommands && string.IsNullOrWhiteSpace(cfg.Provider(id).Model)) return (null, "nie wybrano modelu");
            // Codex / Claude Code zalogowane kluczem API (lub kontem Console) podlegają też miesięcznemu limitowi kosztów.
            if (Billed(p, st) && !Budget.IsApiBilled(id))
            {
                block = _budget.CanRun(id, DateTime.UtcNow, manual, apiBilled: true);
                if (block != null) return (null, block);
            }
            return (p, "");
        }

        private async Task<(IAiProvider? provider, string reason)> PickProviderAsync(LabJob job, RunContext ctx, CancellationToken ct)
        {
            var cfg = _cfg.Config;
            var order = new List<ProviderId>();
            if (job.Provider.HasValue) order.Add(job.Provider.Value);
            else
            {
                order.Add(cfg.PrimaryProvider);
                if (cfg.FallbackProvider.HasValue && cfg.FallbackProvider != cfg.PrimaryProvider) order.Add(cfg.FallbackProvider.Value);
            }
            var reasons = new List<string>();
            foreach (var id in order)
            {
                var (p, why) = await UsableAsync(id, job.Trigger == TriggerKind.Manual, ctx, ct).ConfigureAwait(false);
                if (p != null) return (p, "");
                reasons.Add($"{id}: {why}");
            }
            return (null, string.Join("; ", reasons));
        }

        /// <summary>
        /// Dostawca zapasowy po wyczerpaniu limitu – kontynuacja TEGO SAMEGO zadania: reguła minimalnego odstępu (sprawdzona przy wyborze
        /// dostawcy) nie obowiązuje; limity uruchomień, tokenów i kosztów oraz wymagania (logowanie, model) – tak.
        /// </summary>
        private async Task<IAiProvider?> FallbackAsync(LabJob job, RunContext ctx, IAiProvider current, CancellationToken ct)
        {
            var id = _cfg.Config.FallbackProvider;
            if (id == null || id == current.Id) return null;
            var (p, why) = await UsableAsync(id.Value, manual: true, ctx, ct).ConfigureAwait(false);
            if (p == null) Ev(job, "warning", $"Dostawca zapasowy {id} niedostępny: {why}.");
            return p;
        }

        /// <summary>
        /// Niezależny przegląd drugim modelem. Zwraca null, gdy przegląd zaakceptował zmianę lub nie jest skonfigurowany,
        /// albo opis zastrzeżenia (PL) – wtedy propozycja nie może być oznaczona jako „wszystkie kontrole zaliczone”.
        /// </summary>
        private async Task<string?> ReviewAsync(LabJob job, RunContext ctx, string worktree, string jobDir, string diff, CancellationToken ct)
        {
            var cfg = _cfg.Config;
            var rid = cfg.ReviewProvider;
            if (rid == null || !cfg.Provider(rid.Value).Enabled)
            {
                End(job, JobStage.Review, StageStatus.Skipped, "Przegląd drugim modelem wyłączony.");
                return null;
            }
            ct.ThrowIfCancellationRequested();
            var p = _providers.Get(rid.Value);
            var st = await p.StatusAsync(ct).ConfigureAwait(false);
            ctx.Statuses[rid.Value] = st;
            ct.ThrowIfCancellationRequested();
            string? block = !st.Installed ? "nie zainstalowano"
                          : !st.LoggedIn ? "brak logowania / klucza"
                          : !p.ExecutesCommands && string.IsNullOrWhiteSpace(cfg.Provider(rid.Value).Model) ? "nie wybrano modelu"
                          // Przegląd jest częścią tego samego zadania – bez liczników uruchomień agenta (tylko limit dostawcy, tokeny, koszty API).
                          : _budget.CanReview(rid.Value, DateTime.UtcNow, Billed(p, st));
            if (block != null)
            {
                End(job, JobStage.Review, StageStatus.Skipped, "Przegląd pominięty: " + block);
                return "niezależny przegląd AI pominięty (" + block + ")";
            }
            Begin(job, JobStage.Review, "Niezależny przegląd zmiany drugim modelem…");
            var r = await RunAgentAsync(job, ctx, p, worktree, jobDir, PromptBuilder.ReviewTask(job, diff), "review", ct).ConfigureAwait(false);
            job.ReviewProvider = rid;
            job.Review = r.Ok ? r.Summary : "Przegląd nieudany: " + r.Error;
            if (!r.Ok)
            {
                End(job, JobStage.Review, StageStatus.Warning, job.Review);
                return "niezależny przegląd AI nieudany";
            }
            var verdict = ParseReviewVerdict(r.Summary);
            string name = VerdictName(verdict);
            End(job, JobStage.Review, verdict == ReviewVerdict.Accept ? StageStatus.Ok : StageStatus.Warning,
                verdict == ReviewVerdict.Unknown ? "Przegląd zakończony bez jednoznacznego werdyktu – szczegóły w propozycji." : "Przegląd zakończony – werdykt: " + name + " (szczegóły w propozycji).");
            return verdict switch
            {
                ReviewVerdict.Accept => null,
                ReviewVerdict.Unknown => "niezależny przegląd AI bez jednoznacznego werdyktu",
                _ => "niezależny przegląd AI zgłasza zastrzeżenia (werdykt: " + name + ")",
            };
        }

        public static string VerdictName(ReviewVerdict v) => v switch
        {
            ReviewVerdict.Accept => "AKCEPTUJ",
            ReviewVerdict.NeedsChanges => "WYMAGA POPRAWEK",
            ReviewVerdict.Reject => "ODRZUĆ",
            _ => "brak",
        };

        /// <summary>
        /// Werdykt przeglądu: najpierw ostatnia linia „WERDYKT: AKCEPTUJ | WYMAGA POPRAWEK | ODRZUĆ” (dokładnie jedno słowo kluczowe),
        /// w przeciwnym razie przeszukanie całego tekstu jak dotąd (ODRZUĆ ma pierwszeństwo, potem WYMAGA POPRAWEK, potem AKCEPTUJ).
        /// </summary>
        public static ReviewVerdict ParseReviewVerdict(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return ReviewVerdict.Unknown;
            static string Clean(string s) => new string(s.Where(c => c is not ('*' or '_' or '`' or '#' or '>')).ToArray()).Trim().ToUpperInvariant();
            static List<ReviewVerdict> Found(string upper, bool verdictLine)
            {
                var l = new List<ReviewVerdict>();
                // W linii werdyktu dopuszczamy zapis bez polskich znaków ("ODRZUC"); w całym tekście tylko pełne słowo (np. "odrzucenia" to nie werdykt).
                if (upper.Contains("ODRZUĆ", StringComparison.Ordinal) || (verdictLine && upper.Contains("ODRZUC", StringComparison.Ordinal))) l.Add(ReviewVerdict.Reject);
                if (upper.Contains("WYMAGA POPRAWEK", StringComparison.Ordinal)) l.Add(ReviewVerdict.NeedsChanges);
                if (upper.Contains("AKCEPTUJ", StringComparison.Ordinal)) l.Add(ReviewVerdict.Accept);
                return l;
            }
            var lines = text.Replace("\r", "").Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = Clean(lines[i]).TrimStart('-', '–', ' ');
                if (!line.StartsWith("WERDYKT", StringComparison.Ordinal)) continue;
                var f = Found(line.Substring("WERDYKT".Length), verdictLine: true);
                if (f.Count == 1) return f[0];
            }
            var all = Found(Clean(text), verdictLine: false);
            return all.Count == 0 ? ReviewVerdict.Unknown : all[0];
        }

        private static List<string> ActiveStrategies(string worktree)
        {
            try
            {
                var p = StrategyProfile.FromJson(File.ReadAllText(Path.Combine(worktree, "config", "profile_default.json")));
                return p.Entries.Where(e => e.Enabled && e.Status != ValidationStatus.Rejected).Select(e => e.Strategy.ToString()).Distinct().ToList();
            }
            catch (Exception) { return ChangeAudit.StrategyFiles.Keys.ToList(); } // brak profilu = wszystkie traktujemy jako chronione
        }

        private static string Title(string summary, LabJob job)
        {
            string first = (summary ?? "").Split('\n').Select(l => l.Trim().TrimStart('#', '*', '-', ' ')).FirstOrDefault(l => l.Length > 0) ?? "";
            if (first.Length == 0) first = "Propozycja zmiany (" + PromptBuilder.TriggerName(job.Trigger) + ")";
            return first.Length > 110 ? first.Substring(0, 110) + "…" : first;
        }
    }
}
