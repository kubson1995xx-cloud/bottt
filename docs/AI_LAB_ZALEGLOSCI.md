# AI Lab – zaległości po przeglądzie (dla Codex)

Moduł **AI & Auto-Updates (APEX AI Lab)** jest odłożony na później decyzją użytkownika. Ten plik zbiera to, co zostało do zrobienia,
gdy praca nad modułem zostanie wznowiona. Teksty ustaleń pochodzą z automatycznego przeglądu (po angielsku, z dokładnymi ścieżkami).

## Stan grup poprawek

| Grupa | Stan |
|---|---|
| deploy (wydania, wycofania, dostęp do plików) | scalona, 14 nowych testów (`AiLabDeployTests.cs`) |
| audit (audyt zmian, zakres chroniony, dostawcy, procesy) | scalona, 99 nowych testów (`AiLabAuditTests.cs`) |
| jobs (przebieg zadań, weryfikacja, harmonogram, limity, API) | scalona **częściowo** – agent przerwany przez limit sesji; kod kompiluje się, 34 nowe testy (`AiLabPipelineTests.cs`) przechodzą, ale nie wiadomo, które ustalenia są w pełni zamknięte – sprawdź każde poniżej |
| ui (zakładka w panelu) | **niezrobiona** |
| win (skrypt budowania, końce linii w testach) | **niezrobiona** (część dotycząca bota – parsowanie skryptu w PowerShell 5.1 – naprawiana osobno) |

## Zmiany integracyjne zgłoszone przez grupy deploy i audit (do wykonania)

- **[deploy]** REQUIRED – src/Apex.AiLab/Program.cs (GET /api/jobs/{id}, line ~261): replace `requiredPhrase = Deployer.RequiredPhrase(job)` with `requiredPhrase = deployer.RequiredPhraseFor(job)`. Optionally also add `requiredPhraseReason = deployer.ConfirmationFor(job).reason`. Until this is done, a job whose deploy base differs from its BaseCommit (a release is active and the job was based on the source commit) gets the warning-phrase demand from ApproveAsync, but the dialog shows no phrase field. The approve call then returns 'Wymagane potwierdzenie: wpisz dokładnie "ZATWIERDZAM MIMO OSTRZEŻEŃ"' and the user cannot type it.
- **[deploy]** RECOMMENDED – src/Apex.AiLab/Program.cs line ~317: `app.MapPost("/api/releases/rollback", async (CancellationToken ct) => Results.Json(new { message = await deployer.RollbackAsync(ct) }));`. The synchronous Rollback() still works: it wraps RollbackAsync and blocks a thread-pool thread during the git reset.
- **[deploy]** pipeline-8 glue – src/Apex.AiLab/Pipeline/JobRunner.cs: add `public Func<string?>? ActiveReleaseCommit { get; set; }`. In ExecuteAsync, right after `var (baseCommit, baseRef) = await _ws.PrepareAsync(...)`, add: `string? rel = ActiveReleaseCommit?.Invoke(); if (!string.IsNullOrEmpty(rel) && await _ws.ResolveCommitAsync(rel, ct).ConfigureAwait(false) is string relSha) { baseCommit = relSha; baseRef = GitWorkspace.DeployBranch + " (aktywne wydanie)"; }`. Wire it in src/Apex.AiLab/Program.cs after the deployer is created: `runner.ActiveReleaseCommit = deployer.ActiveReleaseCommit;`. Side effect: while a release is active, new source-repo commits do not reach new jobs. The release lineage is authoritative. A job built on a release that is later rolled back is refused at approval with a clear message.
- **[deploy]** win-1 (other group, Verification.cs): Deployer now runs Verification.BuildAsync on DeployDir before publish, so the cBot steps in BuildAsync must also get -p:AlgoPublish=false (and may use -p:AlgoBuild=false). Otherwise a cBot build during deploy could copy an .algo into cTrader if the approved tree's csproj enables AlgoPublish.
- **[deploy]** src/Apex.AiLab/Lab/LabStore.cs Load() (not mine): on restart it turns a Deploying job into Failed. Safer: if the history contains a release with JobId == job.Id, set Deployed. Otherwise restore job.StatusBeforeDeploy (ReadyForApproval or NeedsAttention) so the user can retry. This is safe now because each deploy resets DeployDir to the deploy base.
- **[deploy]** src/Apex.AiLab/Lab/LabConfig.cs Save() (not mine): add the same File.Move retry as LabStore.WriteJson / HostConfig.Save. Retry on UnauthorizedAccessException or IOException other than FileNotFound/DirectoryNotFound, up to 5 attempts with 25*n^2 ms sleeps.
- **[deploy]** UI glue (src/Apex.Desktop/wwwroot/ai-lab.js, not mine): (1) show `withdrawn` on releases (a 'wycofane' badge). /api/state already serialises ReleaseInfo.Withdrawn, WithdrawnUtc and BaseCommit. (2) In the approve dialog, show `requiredPhraseReason` when present instead of the fixed 'Weryfikacja jest niepełna…' text. (3) Optionally show job.shippedFindings and zadania/<id>/wdrozenie.diff, which is the diff actually shipped relative to the active release. job.deployment now also has deployBase, mergedBuildSteps and mergedTests.
- **[deploy]** API additions (all source-compatible): GitWorkspace.MergeForDeployAsync(jobId, baseCommit, ct, string? deployBase = null, string? auditedCommit = null) now always resets DeployDir to deployBase ?? baseCommit before merging. New GitWorkspace members: ResetDeployAsync, ResolveCommitAsync, IsAncestorAsync, ChangesBetweenAsync, ShowFileAsync, MarkReleaseAsync. Deployer ctor has an optional IReleaseBuilder (default DotnetReleaseBuilder). New Deployer members: RequiredPhraseFor, ConfirmationFor, ActiveReleaseCommit, RollbackAsync; the static RequiredPhrase is kept. New LabJob members: StatusBeforeDeploy, ShippedAuditBase, ShippedFindings, ShippedProtected. New DeploymentInfo members: DeployBase, MergedBuildSteps, MergedTests. These were appended at the end of LabJob/DeploymentInfo, so expect trivial merge conflicts if another group appends there too.
- **[deploy]** GitWorkspace: CollectChangesAsync and DiffAsync were not touched. My re-audit uses its own ChangesBetweenAsync (diff -z --no-renames, full output, --no-ext-diff/--no-textconv). Note for the agent fixing DiffAsync: RunGit/GitOut keep only the last 400 output lines (ProcessRunner keepLines default), so large diffs are truncated.
- **[audit]** REQUIRED (win-6 / sec-2 full effect): in src/Apex.AiLab/Pipeline/JobRunner.cs line ~276, pass the worktree: `job.Findings = ChangeAudit.Audit(job.Files, diff, cfg.Policy, active, worktree);`. Without it, the per-file UTF-16/NUL/invalid-UTF-8 check and the full-file lexing of added lines do not run in production. The binary flag, the diff-based control-character check and all path rules work without it.
- **[audit]** JobRunner: CollectChangesAsync can now return extra entries with Status="I" (ignored files that affect the build). They are Blocking in the audit and also count in `pending.Count == 0` before switching to the fallback provider, which is the correct behaviour. CollectChangesAsync/DiffAsync now throw InvalidOperationException ('Kopia robocza została naruszona…') if the worktree .git pointer was replaced. The existing catch in ExecuteAsync turns this into Failed.
- **[audit]** win-1 (not my files): add the global property `-p:AlgoPublish=false` to every cBot build in Verification.BuildAsync (optionally also -p:AlgoBuild=false there) and in Deployer.ApproveAsync (keep AlgoBuild so the .algo still lands in the release folder).
- **[audit]** sec-7 (not my files): in JobRunner/Verification, compare TRX results by fully qualified test name against the baseline. Every baseline test must still exist and pass; a missing or newly skipped name should be Blocking. Optionally run the backtester built from the BASE commit. tools/Apex.Backtester/ is now forbidden for AI changes, so the candidate's backtester equals the base one.
- **[audit]** Verification hardening (not my files, found during the review): clean bin/ and obj/ in the worktree before the candidate build, or build with --no-incremental. Otherwise binaries planted by the agent with newer timestamps (obj/*.dll) could be used by tests and the backtest instead of the audited source. The audit only flags non-NuGet obj/*.props|targets and does not inspect stale binaries.
- **[audit]** provider-11 glue (not my files): in src/Apex.AiLab/Program.cs (GET config, ~line 355), add `kv.Value.PriceCachedInputPerMTok` to the provider object. In src/Apex.Desktop/wwwroot/ai-lab.js (~line 702), add `field('Cena wejścia z pamięci podręcznej (USD / 1 mln tokenów)', num(p, 'priceCachedInputPerMTok', { min: 0, step: '0.01' }), '0 = 10% ceny wejścia (typowy cennik).')`. PUT already deserialises the whole ProviderSettings.
- **[audit]** Optional UI text (ai-lab.js ~line 700 and 821): the cost-limit hint can say that the limit is skipped for subscription logins. The 'Zawsze blokowane' list can add: pliki projektów/skrypty, pliki binarne i kodowanie inne niż UTF-8, refleksja/unsafe/global using w src/, backtester, AccountGuard.cs/HostConfig.cs.
- **[audit]** Docs (not my files): update docs/AI_AUTO_UPDATES.md ('Zmiany w src/Apex.AiLab/, tools/windows/ i .git/ są zawsze odrzucane' → add tools/Apex.Backtester/, AccountGuard.cs, HostConfig.cs, build/script files; describe the Claude Code tools as exact build/test commands). Update docs/PRZEGLAD_BEZPIECZENSTWA.md: Protected now = all of Apex.Core (with the two exceptions), Apex.Desktop except wwwroot, config/, TestSupport.cs, simulator, changes to existing tests. Also state there that AI-written tests and MSBuild code run with the user's privileges during verification (sec-3 residual). Update the test counts in docs/WYNIKI_TESTOW.md (253 tests, new class AiLabAuditTests with 99).
- **[audit]** Behaviour change for the E2E rerun: many more changes now require the phrase ZATWIERDZAM ZMIANY CHRONIONE. That covers any edit under src/Apex.Core/ except the two exceptions, src/Apex.Desktop/ outside wwwroot, config/, and edits of existing test files. The E2E scenario 'new file tests/Apex.Tests/IndicatorEdgeCaseTests.cs' still gives no Blocking and no Protected. RiskManager.cs is still Protected, not Blocking. HostConfig.cs LiveTradingFeature is still Blocking. All three are covered in E2EScenarios_StillBehaveAsBefore.
- **[audit]** New public helpers others may reuse: ProcessRunner.CaptureAsync/CaptureResult (exact stdout), ProcessRunner.Utf8NoBom, ProcessRunner.BuildInteractiveArguments, ChangeAudit.IsBuildOrScriptFile, GitChangeParsing (ParseChanges/HiddenBuildInputs/CheckWorktreePointer/Unquote), SourceScanner, ClaudeCodeProvider.BuildCommand/TestCommand/BudgetApplies/LastStatus, AnthropicApiProvider.StopReasonError, OpenAiApiProvider.OpenAiResponseError, FileBlocks.AllowedRoots. All existing public signatures are unchanged; ChangeAudit.Audit only gained an optional parameter.

## Ustalenia do sprawdzenia lub naprawy

### Grupa jobs

#### pipeline-1 (high) – A cancelled, timed-out or transiently failed baseline is cached forever for that commit
- Plik: `src/Apex.AiLab/Pipeline/JobRunner.cs:214`
- Scenariusz: (a) The user clicks Przerwij during 'Przygotowanie izolowanej kopii' or the first baseline build of a new source commit. RestoreAsync and BuildAsync return Cancelled and Put() stores BuildOk=false. From then on every job on that commit, including after restarts, stops at once with 'Wersja bazowa się nie kompiluje – najpierw napraw repozytorium źródłowe', although the repo compiles. (b) The baseline test run times out: Tests.Total=0 is cached, so the 'Mniej testów niż w wersji bazowej' guard (JobRunner.cs:297) can never fire for that commit. (c) The baseline backtest times out, or the data folder is still empty. The cached Backtest=null makes every later job skip the backtest and end as NeedsAttention, even after the user raises the timeout or adds the CSV files.
- Proponowana poprawka: Put() only a complete baseline: BuildOk && Tests.Ran && (Backtest != null || the skip reason is a pure configuration skip). Never Put when ct.IsCancellationRequested or any step TimedOut; when cancelled, throw OperationCanceledException so the job ends Cancelled. Add RunBacktests and a data-folder fingerprint (file names and mtimes of *_H1.csv/*_M15.csv) to the key, or store no entry when Backtest==null. Also add a 'clear baseline cache' action.

#### pipeline-5 (medium) – Candidate backtest that crashes, times out or is cancelled still produces an approvable proposal
- Plik: `src/Apex.AiLab/Pipeline/JobRunner.cs:368`
- Scenariusz: (1) The agent changes src/Apex.Core/Indicators/Ta.cs (not protected) so that it throws IndexOutOfRange on some bars. Build and unit tests pass. The backtester exits with an unhandled exception, giving 'Backtest nieudany: …'. The job becomes NeedsAttention and can be deployed with 'ZATWIERDZAM MIMO OSTRZEŻEŃ' (or with no phrase at all, see pipeline-7), so the crash reaches the live engine. (2) The user presses Przerwij during the 30-minute candidate backtest. The job does not become Cancelled; it becomes an approvable NeedsAttention proposal (any review also runs with the cancelled token).
- Proponowana poprawka: In the cand==null branch: if ct.IsCancellationRequested, throw OperationCanceledException. Otherwise Finish(job, JobStatus.RejectedByChecks, "Odrzucona: backtest kandydata nieudany – " + msg) and return. Keep NeedsAttention only for the case baseline.Backtest == null (a configuration or data skip).

#### pipeline-6 (medium) – Fallback after a usage limit is dead for automatic jobs: MarkAgentRun is set just before the fallback budget check
- Plik: `src/Apex.AiLab/Pipeline/JobRunner.cs:233`
- Scenariusz: A degradation trigger starts the agent. Codex reports 'usage limit reached'. FallbackAsync returns null because of the min-gap rule, and the job ends Throttled. The fallback provider (Claude Code) is never tried, and the next automatic attempt is blocked for another 24 h.
- Proponowana poprawka: In FallbackAsync, skip the min-gap rule (pass manual:true or a dedicated flag), because it continues the same job and the run-count, token and cost limits still apply. Better: move MarkAgentRun to after the final agent result. Optionally record the last automatic run separately from manual runs.

#### provider-1 (medium) – Fallback provider can never run for automatic jobs: MarkAgentRun is stamped before FallbackAsync checks the minimum-interval rule
- Plik: `src/Apex.AiLab/Pipeline/JobRunner.cs:233`
- Scenariusz: Automation is on, primary=Codex and fallback=ClaudeCode. A StrategyDegradation or Schedule trigger fires, and Codex returns "You've hit your usage limit…" with a clean worktree. MarkAgentRun(now) runs, then FallbackAsync -> CanRun(ClaudeCode, now, manual:false) returns "minimalny odstęp między automatycznymi uruchomieniami (24 h)". Fallback is null and the job finishes Throttled. The configured fallback only ever works for manual runs, never for the fully automatic path it is meant for.
- Proponowana poprawka: Move `_store.MarkAgentRun(DateTime.UtcNow)` to after the fallback block (after line 249). In FallbackAsync, skip the min-interval rule, because it was already enforced in PickProviderAsync for this job: call `_budget.CanRun(id.Value, DateTime.UtcNow, manual: true)`. Optionally record a limit-hit attempt under a separate kind (e.g. "agent-limit") that RunsToday does not count. Add a test: non-manual trigger, a primary fake provider returning LimitReached with a clean worktree, and an assertion that the fallback provider ran.

#### provider-5 (medium) – Independent review is skipped because its budget check counts the current job's own agent and repair runs
- Plik: `src/Apex.AiLab/Pipeline/JobRunner.cs:466`
- Scenariusz: Primary=AnthropicApi, review=Codex, defaults. This is the first job of the day: the agent run is recorded (RunsToday=1), the build fails, and a repair round is recorded (RunsToday=2). Tests then pass and ReviewAsync calls CanRun, which returns "dzienny limit uruchomień agenta (2/2)". The stage shows "Przegląd pominięty", and the job still finishes "Gotowa do Twojej decyzji – wszystkie kontrole zaliczone" with no second-model review.
- Proponowana poprawka: In ReviewAsync, do not apply the agent-run counters. Check only the reviewer's limit state (LimitedUntilUtc), the USD cap for API-billed reviewers, and optionally token headroom. Introduce e.g. `Budget.CanReview(ProviderId, DateTime)` for this. Record repair rounds as kind "repair" so they don't silently consume agent-run slots (or count them deliberately and document it).

#### provider-7 (low) – Fallback can pick an API provider with no model configured; the job then fails instead of being throttled
- Plik: `src/Apex.AiLab/Pipeline/JobRunner.cs:454`
- Scenariusz: Primary=Codex, fallback=OpenAiApi with a key saved but no model. This is a manual run. Codex hits its usage limit, the fallback is selected, and ApiProviderBase returns "OpenAI API: nie wybrano modelu". That attempt is recorded as another "agent" run, and the job is marked Failed (LimitReached=false) instead of Throttled.
- Proponowana poprawka: In FallbackAsync, add `if (!p.ExecutesCommands && string.IsNullOrWhiteSpace(cfg.Provider(id.Value).Model)) return null;`, or factor out a shared `IsUsable(id)` helper used by both PickProviderAsync and FallbackAsync.

#### provider-9 (low) – Final job status claims all checks passed even when the reviewer says ODRZUĆ or the review failed
- Plik: `src/Apex.AiLab/Pipeline/JobRunner.cs:369`
- Scenariusz: The second model answers "Werdykt: ODRZUĆ – zmiana luzuje filtr SL…". The proposal list shows a ReadyForApproval job with the status text "wszystkie kontrole zaliczone", which hides a negative independent review from a user deciding quickly.
- Proponowana poprawka: After ReviewAsync, if the Review stage is Warning or Failed, finish with NeedsAttention (or at least use a status text such as 'Kontrole automatyczne zaliczone, ale przegląd AI zgłasza zastrzeżenia'). If it is Skipped while a reviewer is configured, mention that in the text. Ask the reviewer for a dedicated last line ('WERDYKT: AKCEPTUJ|WYMAGA POPRAWEK|ODRZUĆ') and parse that line instead of matching substrings.

#### pipeline-10 (low) – User cancellation becomes Failed, 'Commit nieudany' or RejectedByChecks instead of Cancelled, and can consume agent quota
- Plik: `src/Apex.AiLab/Pipeline/JobRunner.cs:316`
- Scenariusz: The user clicks Przerwij while dotnet test is running. The history shows 'Błąd: Commit nieudany: …' with status Failed, not Cancelled. With an API provider and MaxRepairRounds=1, the aborted repair call is counted as one of today's MaxAgentRunsPerDay.
- Proponowana poprawka: Call ct.ThrowIfCancellationRequested() after each provider, build, test and backtest call and before the repair round. Do not Record usage when the result is a pre-call cancel with zero tokens. Run CommitAsync with CancellationToken.None, or skip it when cancelled.

#### pipeline-12 (low) – Scheduler marks the schedule slot or trigger as used before the job start succeeds
- Plik: `src/Apex.AiLab/Scheduling/Scheduler.cs:98`
- Scenariusz: A weekly analysis comes due while a release is being published (a few minutes of Deploying). Start returns null, but LastScheduledRunUtc is already set, so the weekly run is skipped until the next week. Or a critical 'Strategia X traci na żywo' finding is detected while the user starts a manual analysis-only job. The trigger key is consumed and Start fails, so the agent is not triggered for MinHoursBetweenAgentRuns (24 h).
- Proponowana poprawka: Call `var id = _runner.Start(...)` first and call MarkScheduledRun / MarkTrigger only when id != null. Also check _store.AnyRunning() at line 68.

#### pipeline-13 (low) – Re-enabling automation fires a scheduled run immediately because the old LastScheduledRunUtc overrides EnabledSinceUtc
- Plik: `src/Apex.AiLab/Scheduling/Scheduler.cs:38`
- Scenariusz: Daily schedule at 06:30, last run on day 5. The user disables automation, then re-enables it on day 12 at 15:00. The next tick sees due = day 6 06:30 < now and starts an analysis (and the agent, if there are findings) right away instead of at 06:30 the next day.
- Proponowana poprawka: Use the later of the two as the anchor: `var anchor = Max(LastScheduledRunUtc, EnabledSinceUtc)` (ignoring nulls) and pass it to ScheduleMath.NextDueUtc.

#### pipeline-14 (low) – Analyzer reports the wrong sample size and win rate next to statistics computed on the last 60 trades
- Plik: `src/Apex.AiLab/Analysis/Analyzer.cs:103`
- Scenariusz: A strategy has 300 closed trades. The finding says '300 transakcji, średnio -0.12R (górna granica 95%: …)', but the mean and the bound come from only 60 trades, and the win rate shown covers all 300. The agent and the user get statistics that look far stronger and inconsistent.
- Proponowana poprawka: Compute WinRate and NetProfit on the same last-60 window and report both counts (e.g. Trades=rs.Count, TotalTrades=g.Count()), and use rs.Count in the finding text.

#### provider-2 (low) – Monthly USD cost cap ignores Codex and Claude Code even when they are billed per token (API-key or Console login)
- Plik: `src/Apex.AiLab/Scheduling/Budget.cs:84`
- Scenariusz: The user logs Codex in with an API key, enters prices, and sets MaxUsdPerMonth=20. Daily scheduled or trigger runs keep calling CanRun(Codex), which never evaluates the USD limit, so spending continues past 20 USD/month. The panel's "koszt miesięcznie" also stays at 0 for these runs.
- Proponowana poprawka: Store a `ApiBilled` flag on UsageRecord when recording, taken from the provider's cached ProviderStatus (API providers: always; Codex/ClaudeCode: `!status.UsesSubscription`). Use that flag instead of the static provider check in CostMonthUsd and in the MaxUsdPerMonth check, and evaluate the cap for CLI providers whose current login is API-billed. Update the UI labels to match.

#### provider-6 (low) – Daily token budget counts cached input tokens at full weight, so one CLI agent run can exhaust MaxTokensPerDay
- Plik: `src/Apex.AiLab/Providers/ProviderModel.cs:309`
- Scenariusz: A Claude Code run of about 40 tool calls at about 70k context reports roughly 2.8M cache_read + 0.2M other input tokens. TokensToday reaches about 3M. The review in the same job and any trigger-based job later that day are refused with "dzienny limit tokenów (3 000 000/3 000 000)", although the real billed or limited usage was a small fraction of that.
- Proponowana poprawka: In Budget.Compute, count `(r.InputTokens - r.CachedInputTokens) + r.OutputTokens` (or weight cached tokens at about 0.1) for TokensToday/Week/Month and the daily chart. Since CachedInputTokens holds only cache_read for Claude, cache_creation stays counted.

#### sec-7 (medium) – The backtest and test gates are judged by code the candidate itself can change
- Plik: `src/Apex.AiLab/Pipeline/Verification.cs:119`
- Scenariusz: A change that degrades the strategy also edits the backtester's metric calculation, or replaces a guard test's [Fact] with a new trivial test. The backtest verdict is not 'gorszy' and the tests show N/N passed, so the job reaches ReadyForApproval with every check marked OK. The panel and docs present these as 'Weryfikacja niezależna od agenta'.
- Proponowana poprawka: Run the candidate's profile and engine through a backtester binary built from the base commit (build it in the baseline stage and reuse it), or make tools/Apex.Backtester/ and tests/Apex.Tests/TestSupport.cs protected. Compare TRX results by fully-qualified test name: every baseline test must still exist and pass, and any missing or newly skipped name is Blocking. Flag removed or changed [Fact]/[Theory] attributes and Skip arguments across lines. Show changed test files as Protected so they need the typed phrase.

#### win-1 (high) – Pre-approval verification build can auto-install an unapproved cBot .algo into the live cTrader (AlgoPublish)
- Plik: `src/Apex.AiLab/Pipeline/Verification.cs:47`
- Scenariusz: The agent removes <AlgoPublish>False</AlgoPublish> from ApexTradingAI.csproj, or adds Directory.Build.targets with <AlgoPublish>true</AlgoPublish>, or runs 'dotnet build ... -p:AlgoPublish=true'. AI Lab's Build stage (or the agent's own build) overwrites Documents\cAlgo\Sources\Robots\ApexTradingAI.algo with the unapproved engine, including RiskEngine changes. The next cTrader or cBot restart runs it, even if the proposal is later rejected or blocked. This violates 'deployment only after explicit approval' and 'never change RiskEngine/active strategies without approval'.
- Proponowana poprawka: In Verification.BuildAsync, pass '-p:AlgoPublish=false' as a global property to every cBot build; '-p:AlgoBuild=false' is also fine there because verification only needs to compile. In Deployer.ApproveAsync (Deployer.cs:112), pass '-p:AlgoPublish=false' and keep AlgoBuild so the .algo still lands in the release folder. Global properties override both the csproj and Directory.Build.props/targets. In ChangeAudit, treat changes to *.csproj, *.sln, *.props, *.targets, Directory.Build.*, Directory.Packages.props, NuGet.config, global.json and .gitattributes as Blocking, or at least Protected with a dedicated rule, and run content rules on them (for example flag AlgoPublish, Exec and UsingTask). Narrow Claude's 'Bash(dotnet build:*)' and 'Bash(dotnet test:*)' patterns to the exact tests/Apex.Tests commands.

#### sec-3 (medium) – AI-produced build and test code runs unsandboxed with the user's privileges before any approval
- Plik: `src/Apex.AiLab/Pipeline/Verification.cs:43`
- Scenariusz: An agent, possibly steered by injected data (see sec-10), adds a test or build target with side effects outside the worktree. AI Lab's own Tests stage runs it automatically while the job is still Running. The side effects (changed launcher pointer, AI Lab state or Desktop config) take effect without any user decision and are never shown in the audited diff.
- Proponowana poprawka: Run the agent CLIs and Verification (restore, build, test, backtest) under an isolation boundary with no access to the APEX data directory: a separate low-privilege local account, AppContainer or Windows Sandbox, or a container. At minimum, mark *.csproj, *.props, *.targets, Directory.Build.*, NuGet.config, global.json and tools/Apex.Backtester/ as protected. Remove the dotnet Bash grants from Claude Code unless an OS sandbox is active. Update PRZEGLAD_BEZPIECZENSTWA.md to state that verification executes AI-written code with user privileges.

#### ui-2 (medium) – Automation kill-switch (and Schedule 'Off') rejected when unrelated repo/backtest paths are invalid
- Plik: `src/Apex.AiLab/Program.cs:398`
- Scenariusz: The user configured BacktestDataDir (or SourceRepoPath) on a drive that is later unplugged, or deletes the folder. The scheduler keeps starting automatic agent runs: with BacktestDataDir missing the agent still runs and uses subscription limits (NeedsAttention). The user clicks 'Automatyzacja' OFF and gets a 400 'Katalog danych do backtestu nie istnieje.', and the switch flips back to ON on the next refresh. Setting Schedule to 'Wyłączone' fails the same way. Automation cannot be stopped from the panel until the path is fixed or cleared in another sub-tab.
- Proponowana poprawka: In ApplyPatch, run the repo path checks only when body has the 'repo' property, and never reject a patch that only sets automationEnabled=false (or schedule kind Off). Optionally also add a server-side short-circuit in /api/config: if body is exactly {automationEnabled:false}, apply it without validation.

#### ui-6 (low) – /api/state caches aborted provider checks as 'not installed / not logged in' for 5 minutes
- Plik: `src/Apex.AiLab/Program.cs:498`
- Scenariusz: The panel's /api/state call is running the 5-minute re-check (several seconds of codex/claude/git/dotnet/node launches). The user then reloads the panel or closes APEX Desktop to start the newly approved release, which aborts the proxied request. For the next 5 minutes the dashboard shows 'Git – brak', '.NET 8 SDK – brak', 'Node.js – brak' and Codex/Claude 'niezalogowany', with 'Wyloguj' disabled. In reality everything is installed and logged in.
- Proponowana poprawka: Run the refresh with a cache-owned token (CancellationToken.None plus its own timeout) instead of the request token, or after the loop do `if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);` before assigning _statuses/_prereq/_checked. Treat ProcessResult.Cancelled as unknown instead of false.

#### ui-9 (low) – Codex device-login URL/code never appears while the Providers form is dirty or an input has focus
- Plik: `src/Apex.Desktop/wwwroot/ai-lab.js:182`
- Scenariusz: The user changes the Codex model field (form now dirty) and clicks 'Logowanie kodem urządzenia'. The toast says the address and code will appear in the panel, but they never render until the user saves or reverts. The device code can expire (15 min) without the user seeing it. After a successful login the callout says 'Zalogowano.' while the badge says 'niezalogowany' and 'Wyloguj' is disabled.
- Proponowana poprawka: Render the Codex login callout into a dedicated container (like liveUsage) that the dirty-path branch also updates. In the device-login completion handler, invalidate the status cache, e.g. pass a callback from Program into CodexCliProvider, or have /api/state force a refresh when Login.Result changed since the last check.

### Grupa ui

#### ui-1 (low) – AI Lab timers keep running after navigating from AI & Auto-Updates to AI Decision Center
- Plik: `src/Apex.Desktop/wwwroot/app.js:183`
- Scenariusz: Open AI & Auto-Updates, then click AI Decision Center. The panel keeps calling /api/ai-module every 3 s (an HTTP health check from the Desktop), /api/ai/state every 3 s (on the lab this re-runs provider checks every 5 min, spawning codex/claude/git/dotnet/node) and, while a job is live, /api/ai/jobs/{id}/events every 1.5 s. All of this goes through the Desktop process while the user is on a different view, on top of the Decision Center's own polling and animations. It stops only when the user opens a third view such as Pulpit.
- Proponowana poprawka: In render(), stop every module whose view is not active before any branch returns, e.g. at the top: `if (S.view !== 'ai' && window.ApexAI) window.ApexAI.stop(); if (S.view !== 'decisions' && window.ApexDC) window.ApexDC.stop();` Then remove the later stop() calls.

#### ui-3 (low) – Proposal detail panel is never refreshed: wrong status and actions after the job finishes
- Plik: `src/Apex.Desktop/wwwroot/ai-lab.js:418`
- Scenariusz: The agent finishes and the user clicks 'Szczegóły zmiany' while tests and backtest are running. The detail shows 'W toku', a frozen stepper and a 'Przerwij' button. When the job reaches ReadyForApproval, the table row above shows 'Gotowa do decyzji' but the detail never shows 'Zatwierdź'/'Odrzuć', the backtest card or the test results. Clicking 'Przerwij' returns 'Zadanie nie jest w toku.' The user has to close and reopen the proposal by hand.
- Proponowana poprawka: In refresh(), when A.detail is set, find the matching st.jobs entry. If its status or stage string differs from A.detail.job (or A.detail.job.status is Running or Deploying), re-fetch jobs/{id} and assign A.detail before renderBody.

#### ui-4 (low) – Proposals view is rebuilt periodically, collapsing open diffs and resetting scroll during review
- Plik: `src/Apex.Desktop/wwwroot/ai-lab.js:190`
- Scenariusz: A user is reading the expanded diff of a protected-module proposal before typing the approval phrase. Within 5 minutes, or at the next stage change of any running analysis, all expanded files collapse and the page jumps. This interrupts the review-before-approval workflow.
- Proponowana poprawka: Keep the open state across rebuilds: before rebuilding, collect the names of open details (and whether the report is open) from A.els.detail, then re-open them and draw their content in diffCard. Better: rebuild the detail section only when A.detail changes, and leave providersCheckedUtc out of the signature unless A.tab === 'providers'.

#### ui-5 (low) – pollEvents has no in-flight or job-identity guard: duplicate events or another job's log
- Plik: `src/Apex.Desktop/wwwroot/ai-lab.js:281`
- Scenariusz: (a) The user clicks 'Pokaż zadanie' for job B and then quickly for job C. B's larger disk log returns after C's, so A.events becomes B's events under C's header. Because finished jobs are not re-polled once events exist (line 278), C keeps showing B's agent log. (b) During a live run on a CPU-saturated machine (dotnet build/test), one poll takes >1.5 s. The next poll is sent with the same `since`, both responses are pushed, and every line in that range appears twice.
- Proponowana poprawka: Add `if (A.evBusy) return; A.evBusy = true; const job = A.eventsJob; ... finally { A.evBusy = false; }`. After the await, `if (A.eventsJob !== job) return;`. When appending, push only `r.events.filter(e => e.seq > A.lastEvent)`. Allow a reset call to override a pending non-reset call, e.g. with a request counter.

#### ui-7 (low) – Error responses without a JSON message show success-looking or blank toasts
- Plik: `src/Apex.Desktop/wwwroot/ai-lab.js:36`
- Scenariusz: 'Eksportuj łatkę' when `git format-patch` fails: GitWorkspace.GitOut throws InvalidOperationException, the lab returns 500 with an empty body, and the user sees a blank toast. If ApproveAsync throws, the user is told 'Gotowe.' although nothing was deployed. A config save that fails with 500 shows no message at all.
- Proponowana poprawka: In lab(): `if (!r.ok && !data.message) data.message = 'Błąd AI Lab (HTTP ' + r.status + ')';`. In the callers, use the success fallbacks only when r.ok, e.g. `toast(r.message || (r.ok ? 'Gotowe.' : 'Błąd.'))`. Optionally add app.UseExceptionHandler in AI Lab that returns JSON {message}.

#### ui-8 (low) – A.pinned is never cleared, so automatic runs stop being followed live
- Plik: `src/Apex.Desktop/wwwroot/ai-lab.js:214`
- Scenariusz: The user once inspects an older job from the picker. Later the scheduler or a trigger (degradation/market change) starts an agent job. The 'Postęp pracy agentów' card still shows the old finished job's log and stepper, with no live progress, even though the hero says 'Zadanie w toku'. Only manual 'Uruchom agenta teraz' runs set A.selected explicitly.
- Proponowana poprawka: Track the last seen running id: in viewOverview, `if (runningId && runningId !== A.lastRunning) { A.lastRunning = runningId; A.pinned = false; }` before the selection rule. Also clear A.pinned when the user picks the running job.

#### ui-9 (low) – Codex device-login URL/code never appears while the Providers form is dirty or an input has focus
- Plik: `src/Apex.Desktop/wwwroot/ai-lab.js:182`
- Scenariusz: The user changes the Codex model field (form now dirty) and clicks 'Logowanie kodem urządzenia'. The toast says the address and code will appear in the panel, but they never render until the user saves or reverts. The device code can expire (15 min) without the user seeing it. After a successful login the callout says 'Zalogowano.' while the badge says 'niezalogowany' and 'Wyloguj' is disabled.
- Proponowana poprawka: Render the Codex login callout into a dedicated container (like liveUsage) that the dirty-path branch also updates. In the device-login completion handler, invalidate the status cache, e.g. pass a callback from Program into CodexCliProvider, or have /api/state force a refresh when Login.Result changed since the last check.

#### ui-10 (low) – Unsaved Schedule/Settings edits become unreachable after a hero action switches the tab
- Plik: `src/Apex.Desktop/wwwroot/ai-lab.js:156`
- Scenariusz: The user changes the schedule, does not save, and clicks 'Uruchom agenta teraz' to test. The view jumps to 'Przegląd'. Clicking 'Harmonogram i limity' to get back prompts 'Masz niezapisane zmiany… Porzucić je?'. OK discards the edits, Cancel stays on the overview, so the unsaved edits cannot be reached or saved.
- Proponowana poprawka: In runNow (and openProposal), do the same check before switching: `if (A.cfgDirty && !confirm(...)) { skip tab switch } else { A.cfgDirty=false; A.cfg=null; }`. Alternatively, let the tab handler skip the prompt when switching to a tab whose sections own A.cfg.

#### ui-11 (low) – Hero is rebuilt every 3 s, dropping keyboard focus and occasionally clicks
- Plik: `src/Apex.Desktop/wwwroot/ai-lab.js:134`
- Scenariusz: A keyboard user tabs to 'Uruchom agenta teraz' (or the Automatyzacja switch). Within 3 s the element is replaced, focus falls back to <body>, and the next Tab starts from the top of the page. A mouse click whose mousedown and mouseup straddle a rebuild does not fire, so the button press is silently lost.
- Proponowana poprawka: Compute a hero signature (running, version, installed, automation, nextRunUtc, running job id/title, A.starting) and skip replaceChildren when it is unchanged, or update badge text and disabled flags in place.

#### ui-12 (low) – Live log (role=log, aria-live) is fully replaced every 1.5 s, re-announcing hundreds of lines
- Plik: `src/Apex.Desktop/wwwroot/ai-lab.js:291`
- Scenariusz: With NVDA or Narrator while an agent job runs, the screen reader re-reads the whole visible log every 1.5 s. The tab becomes unusable with assistive technology for the length of the job.
- Proponowana poprawka: In pollEvents, return before drawing when r.events is empty and this is not a reset. In drawEvents, append only the new rows and remove the oldest ones above the 400 cap, rebuilding fully only on reset.

#### ui-13 (low) – AI Lab modals never move focus into the dialog; Enter re-opens a second stacked modal
- Plik: `src/Apex.Desktop/wwwroot/ai-lab.js:498`
- Scenariusz: The user clicks 'Zatwierdź i przygotuj wdrożenie' and the modal opens with focus still on that button. Pressing Enter (for example to submit) activates the button again and stacks a second approval modal. Tab moves through the page behind the overlay, not into the phrase field.
- Proponowana poprawka: In confirmModal: `const first = extra && extra.querySelector('input,select,textarea,button'); (first || ok).focus();`. Add a focus trap (keep Tab inside .modal) and blur or inert the background while the modal is open.

#### ui-14 (low) – Agent log jumps to the bottom on every overview re-render, despite the 'keeps scroll' intent
- Plik: `src/Apex.Desktop/wwwroot/ai-lab.js:290`
- Scenariusz: During a live agent run the user scrolls up in 'Dziennik pracy agenta' to read an earlier command or plan. When the job moves from Agent to Audit, or the 5-minute provider re-check happens, the log jumps to the end and the user loses their place.
- Proponowana poprawka: In renderBody, before body.replaceChildren, save `const keep = A.els.log && { top: A.els.log.scrollTop, bottom: A.els.log.scrollHeight - A.els.log.scrollTop - A.els.log.clientHeight < 40 }`. After attaching, restore scrollTop, or scroll to the bottom only if keep.bottom was true. In viewOverview, skip the atBottom measurement on a detached node (check log.isConnected).

### Grupa win

#### win-3 (high) – Zbuduj-APEX.ps1 is UTF-8 without BOM and has en dashes inside double-quoted strings, so Windows PowerShell 5.1 fails to parse it
- Plik: `tools/windows/Zbuduj-APEX.ps1:53`
- Scenariusz: A user on Polish Windows 11 runs the documented build command. PowerShell 5.1 reports parse errors such as 'Unexpected token' or 'The string is missing the terminator' before any step runs. No dist\APEX-AiLab\ApexAiLab.exe and no new 'Uruchom APEX.cmd' launcher (the one that reads aktualne.txt) are produced, so the AI Lab module cannot be installed.
- Proponowana poprawka: Store tools/windows/Zbuduj-APEX.ps1 as UTF-8 with BOM (EF BB BF). Git keeps the BOM in the blob, so no .gitattributes trick is needed. Alternatively, or as well, use only ASCII in code and strings: replace '–' with '-' and drop the diacritics in Write-Host and throw text. Optionally document 'pwsh' as an alternative. Add a check, run on Windows CI or as a test, that parses the script with [System.Management.Automation.Language.Parser]::ParseFile under Windows PowerShell 5.1.

#### win-7 (medium) – Generated launcher always prefers the AI release in aktualne.txt, so rebuilding the package from newer sources is silently ignored
- Plik: `tools/windows/Zbuduj-APEX.ps1:92`
- Scenariusz: The user approves AI release A, then later pulls an upstream fix (for example a RiskEngine correction) and rebuilds dist. Double-clicking 'Uruchom APEX.cmd' (or the shell:startup shortcut recommended in the docs) still runs release A without the fix, and nothing indicates that the freshly built package is not the one running.
- Proponowana poprawka: In Zbuduj-APEX.ps1, after a successful build, check whether %LOCALAPPDATA%\APEX Trading AI\wydania\aktualne.txt (or the APEX_DATA_DIR equivalent) exists. If it does, rename it to aktualne.txt.poprzednie, print a clear warning that the AI release was deactivated in favour of the new package, and add a -ZachowajWydanieAI switch to keep it. Alternatively, write a build stamp into dist and have the launcher prefer the package when its ApexDesktop.exe is newer than the release, comparing with 'for %%F in (...) do' timestamps or a stamp file. Also show the running build or version in the Desktop panel, and update the Aktualizacja section of INSTALACJA_WINDOWS.md.

#### win-4 (medium) – GitWorkspace test assumes LF checkout and fails with Git for Windows' default core.autocrlf=true, which aborts the package build
- Plik: `tests/Apex.Tests/AiLabTests.cs:438`
- Scenariusz: On a default Windows 11 + Git for Windows install, Workspace_IsolatesChanges_AndDeploysOnlyOnRequest fails at line 438 (expected "jeden\ndwa\n", actual "jeden\r\ndwa\r\n"). The build script stops and no package is produced unless the user knows to pass -PominTesty.
- Proponowana poprawka: In GitWorkspace.PrepareAsync, clone with 'git clone -c core.autocrlf=false --no-hardlinks ...' and also run 'git config core.autocrlf false' for existing lab repos, before any worktree is created. Worktrees, diffs and the audit then behave the same on Windows and Linux. In the test, either set 'git config core.autocrlf false' in the source repo, or normalise line endings with .Replace("\r\n","\n") before the content assertions at lines 432, 438 and 443.

