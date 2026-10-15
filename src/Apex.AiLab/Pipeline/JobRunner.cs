using System;
using System.Collections.Generic;
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
        private readonly object _lock = new();
        private CancellationTokenSource? _cts;
        private string? _running;

        public JobRunner(LabConfigStore cfg, LabStore store, Analyzer analyzer, GitWorkspace ws, Budget budget, ProviderRegistry providers, BaselineCache baseline)
        {
            _cfg = cfg;
            _store = store;
            _analyzer = analyzer;
            _ws = ws;
            _budget = budget;
            _providers = providers;
            _baseline = baseline;
        }

        public string? RunningJobId { get { lock (_lock) return _running; } }

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
            string jobDir = _store.JobDir(job.Id);
            Directory.CreateDirectory(jobDir);
            try
            {
                // 1. Analiza (bez AI, tylko dane z APEX Desktop i migawki rynku).
                Begin(job, JobStage.Analysis, "Analiza skuteczności strategii i warunków rynkowych…");
                var analysis = await _analyzer.AnalyzeAsync(cfg.Schedule, ct).ConfigureAwait(false);
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
                var (provider, reason) = await PickProviderAsync(job, ct).ConfigureAwait(false);
                if (provider == null)
                {
                    Finish(job, JobStatus.Throttled, "Agent nie został uruchomiony: " + reason);
                    return;
                }
                job.Provider = provider.Id;
                job.Model = cfg.Provider(provider.Id).Model;
                _store.SaveJob(job);

                // 3. Izolowana kopia robocza.
                Begin(job, JobStage.Workspace, "Przygotowanie izolowanej kopii repozytorium…");
                var (baseCommit, baseRef) = await _ws.PrepareAsync(cfg.Repo.SourceRepoPath, cfg.Repo.BaseBranch, ct).ConfigureAwait(false);
                job.BaseCommit = baseCommit;
                string worktree = await _ws.CreateWorktreeAsync(job.Id, baseCommit, ct).ConfigureAwait(false);
                job.Branch = "ai/" + job.Id;
                var verify = new Verification(cfg.Repo, (k, t) => Ev(job, k, t));
                await verify.RestoreAsync(worktree, ct).ConfigureAwait(false);
                End(job, JobStage.Workspace, StageStatus.Ok, $"Kopia robocza na gałęzi {job.Branch} (baza {baseRef} @ {baseCommit.Substring(0, Math.Min(10, baseCommit.Length))}).");

                // 4. Wersja bazowa – raz na commit (testy i backtest do porównania).
                Begin(job, JobStage.Baseline, "Weryfikacja wersji bazowej (kompilacja, testy, backtest)…");
                var baseline = _baseline.Get(baseCommit, cfg.Repo.BacktestDataDir);
                if (baseline == null)
                {
                    baseline = new BaselineCache.Entry { Commit = baseCommit, TimeUtc = DateTime.UtcNow, DataDir = cfg.Repo.BacktestDataDir ?? "" };
                    var bb = await verify.BuildAsync(worktree, ct).ConfigureAwait(false);
                    baseline.BuildOk = bb.Ok;
                    if (bb.Ok)
                    {
                        baseline.Tests = await verify.TestAsync(worktree, Path.Combine(jobDir, "testy-baza"), ct).ConfigureAwait(false);
                        var (bt, msg) = await verify.BacktestAsync(worktree, Path.Combine(jobDir, "backtest-baza"), ct).ConfigureAwait(false);
                        baseline.Backtest = bt;
                        baseline.BacktestMessage = msg;
                    }
                    _baseline.Put(baseline);
                }
                if (!baseline.BuildOk)
                {
                    End(job, JobStage.Baseline, StageStatus.Failed, "Wersja bazowa się nie kompiluje – najpierw napraw repozytorium źródłowe.");
                    Finish(job, JobStatus.Failed, "Wersja bazowa się nie kompiluje.");
                    return;
                }
                End(job, JobStage.Baseline, baseline.Tests is { Failed: 0 } && baseline.Backtest != null ? StageStatus.Ok : StageStatus.Warning,
                    $"Wersja bazowa: testy {baseline.Tests?.Passed}/{baseline.Tests?.Total}; backtest: {(baseline.Backtest != null ? "OK" : baseline.BacktestMessage)}");

                // 5. Agent AI.
                Begin(job, JobStage.Agent, $"Agent {provider.Name}{(job.Model.Length > 0 ? " (" + job.Model + ")" : "")} pracuje w kopii roboczej…");
                string task = PromptBuilder.AgentTask(job, analysis, provider.ExecutesCommands);
                if (!provider.ExecutesCommands) task += "\n\n" + PromptBuilder.ApiContext(worktree, analysis, 180_000);
                Directory.CreateDirectory(Path.Combine(worktree, ".ailab"));
                File.WriteAllText(Path.Combine(worktree, ".ailab", "ZADANIE.md"), task);
                File.WriteAllText(Path.Combine(jobDir, "zadanie.md"), task);
                var agent = await RunAgentAsync(job, provider, worktree, jobDir, task, readOnly: false, ct).ConfigureAwait(false);
                _store.MarkAgentRun(DateTime.UtcNow);
                if (!agent.Ok && agent.Usage.LimitReached)
                {
                    // Limit wyczerpany: próba u dostawcy zapasowego, jeśli kopia robocza jest nietknięta.
                    var fallback = await FallbackAsync(job, provider, ct).ConfigureAwait(false);
                    var pending = await _ws.CollectChangesAsync(worktree, baseCommit, ct).ConfigureAwait(false);
                    if (fallback != null && pending.Count == 0)
                    {
                        Ev(job, "warning", $"Limit {provider.Name} wyczerpany – przełączam na {fallback.Name}.");
                        provider = fallback;
                        job.Provider = provider.Id;
                        job.Model = cfg.Provider(provider.Id).Model;
                        task = PromptBuilder.AgentTask(job, analysis, provider.ExecutesCommands);
                        if (!provider.ExecutesCommands) task += "\n\n" + PromptBuilder.ApiContext(worktree, analysis, 180_000);
                        agent = await RunAgentAsync(job, provider, worktree, jobDir, task, readOnly: false, ct).ConfigureAwait(false);
                    }
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
                    job.Findings = ChangeAudit.Audit(job.Files, diff, cfg.Policy, active);
                    job.ProtectedTouched = job.Files.Any(f => f.Protected);
                    int blocking = job.Findings.Count(f => f.Severity == FindingSeverity.Blocking);
                    End(job, JobStage.Audit, blocking > 0 ? StageStatus.Failed : job.Findings.Any(f => f.Severity is FindingSeverity.Protected or FindingSeverity.Warning) ? StageStatus.Warning : StageStatus.Ok,
                        $"Audyt: {job.Files.Count} plików (+{job.Files.Sum(f => f.Added)}/−{job.Files.Sum(f => f.Removed)}), blokujących {blocking}, chronionych {job.Files.Count(f => f.Protected)}.");
                    if (blocking > 0)
                    {
                        await CommitAsync(job, worktree, ct).ConfigureAwait(false);
                        Finish(job, JobStatus.RejectedByChecks, "Odrzucona przez audyt: " + string.Join("; ", job.Findings.Where(f => f.Severity == FindingSeverity.Blocking).Select(f => f.Rule).Distinct()));
                        return;
                    }

                    Begin(job, JobStage.Build, "Kompilacja…");
                    job.Build = await verify.BuildAsync(worktree, ct).ConfigureAwait(false);
                    End(job, JobStage.Build, job.Build.Ok ? StageStatus.Ok : StageStatus.Failed, string.Join("; ", job.Build.Steps));
                    if (job.Build.Ok)
                    {
                        Begin(job, JobStage.Tests, "Testy regresyjne (jednostkowe, silnika, integracyjne)…");
                        job.Tests = await verify.TestAsync(worktree, Path.Combine(jobDir, "testy"), ct).ConfigureAwait(false);
                        job.Tests.BaselineTotal = baseline.Tests?.Total;
                        job.Tests.BaselinePassed = baseline.Tests?.Passed;
                        bool fewer = baseline.Tests != null && job.Tests.Total < baseline.Tests.Total;
                        if (fewer)
                            job.Findings.Add(new AuditFinding { Severity = FindingSeverity.Blocking, Rule = "Mniej testów niż w wersji bazowej", Detail = $"{job.Tests.Total} < {baseline.Tests!.Total}" });
                        End(job, JobStage.Tests, job.Tests.Failed == 0 && !fewer ? StageStatus.Ok : StageStatus.Failed,
                            $"Testy: {job.Tests.Passed}/{job.Tests.Total} zaliczonych (wersja bazowa {baseline.Tests?.Passed}/{baseline.Tests?.Total}).");
                    }
                    bool failed = !job.Build.Ok || job.Tests == null || job.Tests.Failed > 0;
                    if (failed && round < maxRepair && Budget.IsApiBilled(provider.Id) && _budget.CanRun(provider.Id, DateTime.UtcNow, true) == null)
                    {
                        Ev(job, "stage", $"Runda poprawek {round + 1}/{maxRepair}: przekazuję błędy modelowi.");
                        string repair = PromptBuilder.RepairTask(job.AgentSummary, job.Build, job.Tests, worktree, job.Files.Select(f => f.Path)) + "\n\n" + PromptBuilder.ApiContext(worktree, analysis, 60_000);
                        var fix = await RunAgentAsync(job, provider, worktree, jobDir, repair, readOnly: false, ct).ConfigureAwait(false);
                        if (fix.Ok && fix.Summary.Length > 0) job.AgentSummary = fix.Summary;
                        if (!fix.Ok) break;
                        job.Tests = null;
                        continue;
                    }
                    break;
                }
                await CommitAsync(job, worktree, ct).ConfigureAwait(false);
                if (job.Build == null || !job.Build.Ok)
                {
                    Finish(job, JobStatus.RejectedByChecks, "Odrzucona: błąd kompilacji.");
                    return;
                }
                if (job.Tests == null || job.Tests.Failed > 0 || job.Findings.Any(f => f.Severity == FindingSeverity.Blocking))
                {
                    Finish(job, JobStatus.RejectedByChecks, "Odrzucona: testy regresyjne niezaliczone" + (job.Findings.Any(f => f.Rule.StartsWith("Mniej testów", StringComparison.Ordinal)) ? " (usunięto testy)." : "."));
                    return;
                }

                // 9. Backtest – porównanie z wersją bazową.
                Begin(job, JobStage.Backtest, "Backtest portfela i porównanie z wersją bazową…");
                job.Backtest = new BacktestComparison { HoldoutUses = _store.HoldoutUses };
                if (baseline.Backtest == null)
                {
                    job.Backtest.Reason = baseline.BacktestMessage;
                    End(job, JobStage.Backtest, StageStatus.Skipped, "Backtest pominięty: " + baseline.BacktestMessage);
                }
                else
                {
                    var (cand, msg) = await verify.BacktestAsync(worktree, Path.Combine(jobDir, "backtest"), ct).ConfigureAwait(false);
                    if (cand == null)
                    {
                        job.Backtest.Reason = msg;
                        End(job, JobStage.Backtest, StageStatus.Failed, "Backtest nieudany: " + msg);
                    }
                    else
                    {
                        job.Backtest.Ran = true;
                        job.Backtest.Baseline = baseline.Backtest;
                        job.Backtest.Candidate = cand;
                        Verification.Compare(job.Backtest);
                        if (job.Backtest.Verdict != "bez zmian")
                        {
                            job.Backtest.HoldoutUses = _store.RegisterHoldoutUse();
                            job.Backtest.Notes.Add($"Okres odłożony użyto już {job.Backtest.HoldoutUses} raz(y) do oceny zmian – jego wiarygodność maleje z każdą próbą.");
                        }
                        End(job, JobStage.Backtest, job.Backtest.Verdict == "gorszy" ? StageStatus.Failed : job.Backtest.Verdict == "niejednoznaczny" ? StageStatus.Warning : StageStatus.Ok,
                            "Backtest: " + job.Backtest.Verdict + ". " + string.Join(" ", job.Backtest.Notes.Take(2)));
                    }
                }
                if (job.Backtest.Verdict == "gorszy")
                {
                    Finish(job, JobStatus.RejectedByChecks, "Odrzucona: backtest gorszy niż wersja bazowa.");
                    return;
                }

                // 10. Niezależny przegląd drugim modelem (opcjonalnie).
                await ReviewAsync(job, worktree, jobDir, diff, ct).ConfigureAwait(false);

                bool complete = job.Backtest.Ran;
                Finish(job, complete ? JobStatus.ReadyForApproval : JobStatus.NeedsAttention,
                    complete ? "Gotowa do Twojej decyzji – wszystkie kontrole zaliczone." : "Kontrole zaliczone, ale weryfikacja niepełna (" + job.Backtest.Reason + ") – decyzja wymaga ostrożności.");
            }
            catch (OperationCanceledException)
            {
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
                    var keep = new HashSet<string>(_store.Jobs().Where(j => j.Status is JobStatus.ReadyForApproval or JobStatus.NeedsAttention).Select(j => j.Id));
                    await _ws.PruneWorktreesAsync(_cfg.Config.Repo.KeepWorktrees, keep, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception) { /* sprzątanie nie może zmienić wyniku zadania */ }
            }
        }

        private async Task CommitAsync(LabJob job, string worktree, CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(job.HeadCommit)) return;
            string msg = "AI Lab: " + job.Title + "\n\nZadanie " + job.Id + " (" + PromptBuilder.TriggerName(job.Trigger) + "), dostawca " + job.Provider + ".\n\n" + job.AgentSummary;
            job.HeadCommit = await _ws.CommitAsync(worktree, msg, ct).ConfigureAwait(false);
            _store.SaveJob(job);
        }

        private async Task<AgentResult> RunAgentAsync(LabJob job, IAiProvider provider, string worktree, string jobDir, string task, bool readOnly, CancellationToken ct)
        {
            var settings = _cfg.Config.Provider(provider.Id);
            var req = new AgentRequest
            {
                JobId = job.Id,
                WorkDir = worktree,
                JobDir = jobDir,
                Prompt = task,
                Model = settings.Model,
                Settings = settings,
                ReadOnly = readOnly,
                Timeout = TimeSpan.FromMinutes(settings.TimeoutMinutes),
            };
            var result = await provider.RunAsync(req, (k, t) => Ev(job, k, t), ct).ConfigureAwait(false);
            if (result.Usage.CostUsd == null) result.Usage.CostUsd = ApiProviderBase.Cost(result.Usage, settings);
            job.Usage.Add(result.Usage);
            _budget.Record(provider.Id, job.Id, readOnly ? "review" : "agent", result.Usage, DateTime.UtcNow);
            _store.SaveJob(job);
            return result;
        }

        private async Task<(IAiProvider? provider, string reason)> PickProviderAsync(LabJob job, CancellationToken ct)
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
                if (!cfg.Provider(id).Enabled) { reasons.Add($"{id}: wyłączony"); continue; }
                string? block = _budget.CanRun(id, DateTime.UtcNow, job.Trigger == TriggerKind.Manual);
                if (block != null) { reasons.Add($"{id}: {block}"); continue; }
                var p = _providers.Get(id);
                var st = await p.StatusAsync(ct).ConfigureAwait(false);
                if (!st.Installed || !st.LoggedIn) { reasons.Add($"{id}: {(st.Installed ? "brak logowania / klucza" : "nie zainstalowano")}"); continue; }
                if (!p.ExecutesCommands && string.IsNullOrWhiteSpace(cfg.Provider(id).Model)) { reasons.Add($"{id}: nie wybrano modelu"); continue; }
                return (p, "");
            }
            return (null, string.Join("; ", reasons));
        }

        private async Task<IAiProvider?> FallbackAsync(LabJob job, IAiProvider current, CancellationToken ct)
        {
            var cfg = _cfg.Config;
            var id = cfg.FallbackProvider;
            if (id == null || id == current.Id || !cfg.Provider(id.Value).Enabled) return null;
            if (_budget.CanRun(id.Value, DateTime.UtcNow, job.Trigger == TriggerKind.Manual) != null) return null;
            var p = _providers.Get(id.Value);
            var st = await p.StatusAsync(ct).ConfigureAwait(false);
            return st.Installed && st.LoggedIn ? p : null;
        }

        private async Task ReviewAsync(LabJob job, string worktree, string jobDir, string diff, CancellationToken ct)
        {
            var cfg = _cfg.Config;
            var rid = cfg.ReviewProvider;
            if (rid == null || !cfg.Provider(rid.Value).Enabled)
            {
                End(job, JobStage.Review, StageStatus.Skipped, "Przegląd drugim modelem wyłączony.");
                return;
            }
            string? block = _budget.CanRun(rid.Value, DateTime.UtcNow, true);
            if (block != null)
            {
                End(job, JobStage.Review, StageStatus.Skipped, "Przegląd pominięty: " + block);
                return;
            }
            Begin(job, JobStage.Review, "Niezależny przegląd zmiany drugim modelem…");
            var p = _providers.Get(rid.Value);
            var r = await RunAgentAsync(job, p, worktree, jobDir, PromptBuilder.ReviewTask(job, diff), readOnly: true, ct).ConfigureAwait(false);
            job.ReviewProvider = rid;
            job.Review = r.Ok ? r.Summary : "Przegląd nieudany: " + r.Error;
            string verdict = (job.Review ?? "").ToUpperInvariant();
            End(job, JobStage.Review, !r.Ok ? StageStatus.Warning : verdict.Contains("ODRZUĆ") || verdict.Contains("WYMAGA POPRAWEK") ? StageStatus.Warning : StageStatus.Ok,
                r.Ok ? "Przegląd zakończony – werdykt w szczegółach propozycji." : job.Review ?? "Przegląd nieudany.");
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
