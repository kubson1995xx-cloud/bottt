using System;
using System.Collections.Generic;

namespace Apex.AiLab.Lab
{
    public enum JobStage
    {
        Analysis,
        Workspace,
        Baseline,
        Agent,
        Audit,
        Build,
        Tests,
        Backtest,
        Review,
    }

    public enum StageStatus
    {
        Pending,
        Running,
        Ok,
        Warning,
        Failed,
        Skipped,
    }

    public enum JobStatus
    {
        Running,
        /// <summary>Tylko analiza – agent nie był potrzebny lub nie został uruchomiony.</summary>
        AnalysisOnly,
        /// <summary>Agent nie zaproponował zmian.</summary>
        NoChanges,
        /// <summary>Wszystkie kontrole zaliczone – czeka na decyzję użytkownika.</summary>
        ReadyForApproval,
        /// <summary>Kontrole zaliczone, ale weryfikacja niepełna (np. brak danych do backtestu) – decyzja użytkownika z ostrzeżeniem.</summary>
        NeedsAttention,
        /// <summary>Odrzucona automatycznie przez audyt, kompilację, testy lub backtest.</summary>
        RejectedByChecks,
        Failed,
        Cancelled,
        /// <summary>Wstrzymana przez limity (budżet, limit subskrypcji).</summary>
        Throttled,
        Deploying,
        /// <summary>Wydanie przygotowane – aktywne po ponownym uruchomieniu APEX Desktop.</summary>
        Deployed,
        RejectedByUser,
        RolledBack,
    }

    public enum TriggerKind
    {
        Manual,
        Schedule,
        StrategyDegradation,
        MarketChange,
        ExecutionErrors,
    }

    public sealed class StageInfo
    {
        public JobStage Stage { get; set; }
        public StageStatus Status { get; set; } = StageStatus.Pending;
        public DateTime? StartedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }
        public string Summary { get; set; } = "";
    }

    public sealed class ChangedFile
    {
        public string Path { get; set; } = "";
        public int Added { get; set; }
        public int Removed { get; set; }
        public bool Protected { get; set; }
        public string Status { get; set; } = "M";
    }

    public enum FindingSeverity
    {
        Info,
        Warning,
        /// <summary>Zmiana chroniona – dozwolona tylko po dodatkowym, wpisanym potwierdzeniu.</summary>
        Protected,
        /// <summary>Blokuje wdrożenie bezwarunkowo.</summary>
        Blocking,
    }

    public sealed class AuditFinding
    {
        public FindingSeverity Severity { get; set; }
        public string Rule { get; set; } = "";
        public string File { get; set; } = "";
        public string Detail { get; set; } = "";
    }

    public sealed class BuildResult
    {
        public bool Ok { get; set; }
        public double DurationSec { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> Steps { get; set; } = new();
        /// <summary>Przekroczony limit czasu kompilacji (wynik niemiarodajny – nie trafia do pamięci wersji bazowej).</summary>
        public bool TimedOut { get; set; }
        /// <summary>Kompilacja przerwana na żądanie użytkownika.</summary>
        public bool Cancelled { get; set; }
    }

    public sealed class TestResult
    {
        public bool Ran { get; set; }
        public int Total { get; set; }
        public int Passed { get; set; }
        public int Failed { get; set; }
        public int Skipped { get; set; }
        public List<string> FailedNames { get; set; } = new();
        public double DurationSec { get; set; }
        public int? BaselineTotal { get; set; }
        public int? BaselinePassed { get; set; }
        /// <summary>Wynik każdego testu z pliku TRX: pełna nazwa testu → wynik (Passed, Failed, NotExecuted…). Porównanie z wersją bazową po nazwach.</summary>
        public Dictionary<string, string> Outcomes { get; set; } = new();
        /// <summary>Przekroczony limit czasu testów.</summary>
        public bool TimedOut { get; set; }
        /// <summary>Testy przerwane na żądanie użytkownika.</summary>
        public bool Cancelled { get; set; }
    }

    public sealed class BacktestMetrics
    {
        public string Period { get; set; } = "";
        public double MinScore { get; set; }
        public int Trades { get; set; }
        public double WinRate { get; set; }
        public double ExpectancyR { get; set; }
        public double TStat { get; set; }
        public double ProfitFactor { get; set; }
        public double NetProfit { get; set; }
        public double MaxEquityDrawdownPct { get; set; }
        public double Sharpe { get; set; }
    }

    public sealed class BacktestComparison
    {
        public bool Ran { get; set; }
        public string Reason { get; set; } = "";
        public List<BacktestMetrics> Baseline { get; set; } = new();
        public List<BacktestMetrics> Candidate { get; set; } = new();
        /// <summary>lepszy / gorszy / bez zmian / niejednoznaczny</summary>
        public string Verdict { get; set; } = "";
        public List<string> Notes { get; set; } = new();
        /// <summary>Ile razy okres odłożony (holdout) był już użyty do oceny zmian – im więcej, tym mniej wiarygodny.</summary>
        public int HoldoutUses { get; set; }
    }

    public sealed class UsageInfo
    {
        public long InputTokens { get; set; }
        public long CachedInputTokens { get; set; }
        public long OutputTokens { get; set; }
        /// <summary>Koszt w USD – z narzędzia (Claude Code) lub z cen wpisanych przez użytkownika; null = nieznany.</summary>
        public double? CostUsd { get; set; }
        public double DurationSec { get; set; }
        public bool LimitReached { get; set; }
        public DateTime? LimitResetUtc { get; set; }
        public string? LimitMessage { get; set; }

        public long TotalTokens => InputTokens + OutputTokens;

        public void Add(UsageInfo o)
        {
            InputTokens += o.InputTokens;
            CachedInputTokens += o.CachedInputTokens;
            OutputTokens += o.OutputTokens;
            if (o.CostUsd.HasValue) CostUsd = (CostUsd ?? 0) + o.CostUsd.Value;
            DurationSec += o.DurationSec;
            LimitReached |= o.LimitReached;
            LimitResetUtc ??= o.LimitResetUtc;
            LimitMessage ??= o.LimitMessage;
        }
    }

    public sealed class DeploymentInfo
    {
        public DateTime ApprovedUtc { get; set; }
        public string Version { get; set; } = "";
        public string ReleasePath { get; set; } = "";
        public string ExePath { get; set; } = "";
        public string MergeCommit { get; set; } = "";
        public bool ProtectedConfirmed { get; set; }
        public List<string> Notes { get; set; } = new();
    }

    /// <summary>Zadanie AI Lab: analiza → (agent → audyt → kompilacja → testy → backtest → przegląd) → propozycja zmiany.</summary>
    public sealed class LabJob
    {
        public string Id { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }
        public TriggerKind Trigger { get; set; }
        public string TriggerDetail { get; set; } = "";
        public bool RunAgent { get; set; }
        public ProviderId? Provider { get; set; }
        public string Model { get; set; } = "";
        public JobStatus Status { get; set; } = JobStatus.Running;
        public string StatusText { get; set; } = "";
        public List<StageInfo> Stages { get; set; } = new();
        public string? AnalysisId { get; set; }
        public string AnalysisSummary { get; set; } = "";
        public string Title { get; set; } = "";
        public string AgentSummary { get; set; } = "";
        public string Branch { get; set; } = "";
        public string BaseCommit { get; set; } = "";
        public string HeadCommit { get; set; } = "";
        public List<ChangedFile> Files { get; set; } = new();
        public List<AuditFinding> Findings { get; set; } = new();
        public bool ProtectedTouched { get; set; }
        public BuildResult? Build { get; set; }
        public TestResult? Tests { get; set; }
        public BacktestComparison? Backtest { get; set; }
        public string? Review { get; set; }
        public ProviderId? ReviewProvider { get; set; }
        public UsageInfo Usage { get; set; } = new();
        public DeploymentInfo? Deployment { get; set; }
        public string? UserDecisionNote { get; set; }
        public DateTime? DecisionUtc { get; set; }
    }

    public sealed class LabEvent
    {
        public long Seq { get; set; }
        public DateTime TimeUtc { get; set; }
        public string JobId { get; set; } = "";
        /// <summary>stage | agent | command | file | plan | message | warning | error | usage</summary>
        public string Kind { get; set; } = "";
        public string Text { get; set; } = "";
    }

    // ------------------------------------------------------------------ analiza

    public sealed class AnalysisFinding
    {
        public TriggerKind Kind { get; set; }
        /// <summary>info | warning | critical</summary>
        public string Level { get; set; } = "info";
        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";
    }

    public sealed class StrategyLiveStats
    {
        public string Account { get; set; } = "";
        public string Strategy { get; set; } = "";
        public string AssetClass { get; set; } = "";
        public string Status { get; set; } = "";
        /// <summary>Liczba transakcji w oknie, na którym liczone są wszystkie miary (ostatnie ≤ 60).</summary>
        public int Trades { get; set; }
        public double WinRate { get; set; }
        public double ExpectancyR { get; set; }
        public double UpperBoundR { get; set; }
        /// <summary>Wynik netto w tym samym oknie co pozostałe miary.</summary>
        public double NetProfit { get; set; }
        public double? BacktestExpectancyR { get; set; }
        public bool Suspended { get; set; }
        /// <summary>Wszystkie zamknięte transakcje strategii w dzienniku (także spoza okna).</summary>
        public int TotalTrades { get; set; }
        /// <summary>Wynik netto wszystkich transakcji w dzienniku.</summary>
        public double TotalNetProfit { get; set; }
    }

    public sealed class MarketSnapshotEntry
    {
        public string Symbol { get; set; } = "";
        public string AssetClass { get; set; } = "";
        public string Tf { get; set; } = "D1";
        public string Regime { get; set; } = "";
        public int TrendDirection { get; set; }
        public double? Adx { get; set; }
        public double? AtrPercentile { get; set; }
        public double? EfficiencyRatio { get; set; }
    }

    public sealed class MarketSnapshot
    {
        public DateTime TimeUtc { get; set; }
        public string Account { get; set; } = "";
        public List<MarketSnapshotEntry> Entries { get; set; } = new();
    }

    public sealed class AnalysisReport
    {
        public string Id { get; set; } = "";
        public DateTime TimeUtc { get; set; }
        public bool DesktopReachable { get; set; }
        public string DataNote { get; set; } = "";
        public List<string> Accounts { get; set; } = new();
        public List<StrategyLiveStats> Strategies { get; set; } = new();
        public List<AnalysisFinding> Findings { get; set; } = new();
        public List<string> MarketSummary { get; set; } = new();
        public List<string> RecentErrors { get; set; } = new();
        public int TotalTrades { get; set; }
        public string Markdown { get; set; } = "";

        public bool HasActionableFindings => Findings.Exists(f => f.Level != "info");
    }

    // ------------------------------------------------------------------ zużycie

    public sealed class UsageRecord
    {
        public DateTime TimeUtc { get; set; }
        public ProviderId Provider { get; set; }
        public string JobId { get; set; } = "";
        /// <summary>
        /// agent | agent-limit | repair | review | test. Do limitu uruchomień agenta liczy się tylko "agent";
        /// "agent-limit" = próba odrzucona przez limit dostawcy (bez pracy), "repair" = runda poprawek w ramach tego samego zadania.
        /// </summary>
        public string Kind { get; set; } = "agent";
        public long InputTokens { get; set; }
        public long CachedInputTokens { get; set; }
        public long OutputTokens { get; set; }
        public double? CostUsd { get; set; }
        public bool LimitReached { get; set; }
        /// <summary>
        /// Rozliczenie za tokeny (API) w chwili uruchomienia: dostawcy API zawsze, Codex/Claude Code przy logowaniu kluczem API / kontem Console.
        /// null = zapis sprzed tej zmiany (rozliczenie ustalane wg rodzaju dostawcy).
        /// </summary>
        public bool? ApiBilled { get; set; }
    }

    public sealed class ProviderLimitState
    {
        public ProviderId Provider { get; set; }
        public DateTime? LimitedUntilUtc { get; set; }
        public string Message { get; set; } = "";
        public DateTime DetectedUtc { get; set; }
    }
}
