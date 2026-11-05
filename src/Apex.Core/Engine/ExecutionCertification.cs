using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Apex.Core.Bridge;
using Apex.Core.Execution;
using Apex.Core.Model;

namespace Apex.Core.Engine;

public enum ExecutionTestOutcome { InProgress, Interrupted, Failed, Passed }

/// <summary>Engine-owned evidence, never a client assertion. Admission/fill and raw deals remain authoritative.</summary>
public sealed record ExecutionTestCertificate
{
    public int RecordVersion { get; init; }
    public int ProtocolVersion { get; init; }
    public string TestVersion { get; init; } = "";
    public string ScopeVersion { get; init; } = "";
    public RecoveryIdentity Identity { get; init; } = new();
    public Guid BaselineId { get; init; }
    public string OperationId { get; init; } = "";
    public ExecutionTestOutcome Outcome { get; init; }
    public DateTime StartedUtc { get; init; }
    public long PositionId { get; init; }
    public double InitialStop { get; init; }
    public double TakeProfit { get; init; }
    public DateTime ProtectionObservedUtc { get; init; }
    public double TightenedStop { get; init; }
    public DateTime ModifyRequestedUtc { get; init; }
    public DateTime ModifyObservedUtc { get; init; }
    public DateTime CloseRequestedUtc { get; init; }
    public DateTime CloseConfirmedUtc { get; init; }
    public DateTime CompletedUtc { get; init; }
    public long[] ClosingDealIds { get; init; } = Array.Empty<long>();
    public double NetProfit { get; init; }
    public string Message { get; init; } = "";
}

public sealed record ExecutionReadiness(bool Valid, bool CanStart, string Status, string Message,
    RecoveryIdentity? Identity, Guid BaselineId, int ProtocolVersion, string TestVersion);

public sealed partial class TradingEngine
{
    public const string ExecutionTestVersion = "demo-execution-v1";
    private bool CertificateMatchesCurrent() => !_trustedSimulation && !_stateReadFault && _persistenceHealthy
        && _state.ExecutionTest is { Outcome: ExecutionTestOutcome.Passed } c
        && c.RecordVersion == 1 && c.ProtocolVersion == BridgeProtocol.Version && c.TestVersion == ExecutionTestVersion
        && c.ScopeVersion == ScopedRecoveryPolicy.ScopeVersion && _state.Baseline is { Stage: BaselineStage.Established } b
        && c.BaselineId == b.BaselineId && c.Identity == b.Identity && MatchesAccount(c.Identity)
        && c.Identity.Platform == "cTrader" && c.Identity.ExecutionPath == RecoveryExecutionPath.DesktopBridge;

    public ExecutionReadiness GetExecutionReadiness(DateTime? ownerUtc = null)
    {
        bool valid = CertificateMatchesCurrent();
        bool ready = GuardAllowed && !GuardIsLive && _initialized && HistoryReady && _persistenceHealthy && !_stateReadFault
            && CanOperate(out _) && (_trustedSimulation || _scopedAuthority != null && GetScopedPermit(_scopedAuthority, ownerUtc ?? _broker.GetAccount().TimeUtc) != null)
            && _state.Mode != EngineMode.EmergencyStopped && !_risk.NewEntriesBlocked(out _) && _test == null;
        string status = _trustedSimulation ? "SimulationOnly" : valid ? "PASS" : _state.ExecutionTest?.Outcome.ToString() ?? "Missing";
        string message = _trustedSimulation ? "Zaufana symulacja strukturalna; bez certyfikatu produkcyjnego."
            : !valid ? _state.ExecutionTest?.Outcome == ExecutionTestOutcome.InProgress ? "Test trwa. Czekaj na potwierdzenie SL/TP, zmiany SL, pełnego close i trwałego rozliczenia ekonomicznego. START zablokowany."
                : _state.ExecutionTest == null ? "Brak trwałego PASS. Osobno potwierdź Test wykonania DEMO."
                : $"Brak ważnego PASS ({status}): {_state.ExecutionTest.Message}. Sprawdź zakres i osobno ponów test po uzgodnieniu."
            : !ready ? "PASS zapisany. Wymagana świeża synchronizacja/połączenie i usunięcie blokad ryzyka przed jawnym START."
            : "PASS zgodny z kontem, zakresem, ścieżką i wersją. Możesz osobno potwierdzić START.";
        return new(valid, ready && (valid || _trustedSimulation), status, message, _state.ExecutionTest?.Identity,
            _state.ExecutionTest?.BaselineId ?? Guid.Empty, _state.ExecutionTest?.ProtocolVersion ?? BridgeProtocol.Version,
            _state.ExecutionTest?.TestVersion ?? ExecutionTestVersion);
    }

    private ExecutionTestCertificate NewExecutionCertificate(string operation, DateTime utc) => new()
    {
        RecordVersion = 1, ProtocolVersion = BridgeProtocol.Version, TestVersion = ExecutionTestVersion,
        ScopeVersion = ScopedRecoveryPolicy.ScopeVersion, Identity = _state.Baseline!.Identity,
        BaselineId = _state.Baseline.BaselineId, OperationId = operation, StartedUtc = utc,
        Outcome = ExecutionTestOutcome.InProgress, Message = "Test rozpoczęty świadomym poleceniem; oczekiwanie na dowody."
    };

    private bool SaveExecutionEvidence(Func<ExecutionTestCertificate, ExecutionTestCertificate> update, DateTime utc)
    {
        if (_trustedSimulation || _state.Baseline == null) return true; // isolated tests cannot mint production PASS
        if (_state.ExecutionTest == null) return false;
        var next = ScopeCandidate(); next.ExecutionTest = update(next.ExecutionTest!);
        return CommitScope(next, utc);
    }

    private void InterruptExecutionTest(string reason, DateTime utc)
    {
        if (_test == null && _state.ExecutionTest?.Outcome != ExecutionTestOutcome.InProgress) return;
        SaveExecutionEvidence(c => c with { Outcome = ExecutionTestOutcome.Interrupted, CompletedUtc = utc, Message = reason }, utc);
        if (_test != null) { _test.Log.Add("TEST PRZERWANY – " + reason + "; bez automatycznego wznowienia/PASS"); LastExecutionTest = _test.Log.ToArray(); }
        _test = null;
    }

    private bool StopMatches(string symbol, double? observed, double expected)
    {
        var spec = _market.GetSpec(symbol);
        return spec != null && observed is > 0 && double.IsFinite(observed.Value) && double.IsFinite(expected)
            && Math.Abs(observed.Value - expected) <= spec.TickSize * 0.500001 + 1e-10;
    }

    private bool TechnicalPositionMatches(PositionInfo p, ExecTest t, bool originalStop)
    {
        if (p.Id != t.PositionId || p.Direction != t.Direction || p.Label != _cfg.Label || p.Symbol != t.Symbol
            || !ExecutionLedger.TryParseComment(p.Comment, out var strategy, out var signal) || strategy != StrategyId.ExecutionTest || signal != t.SignalId
            || p.StopLoss is not > 0 || !HasValidTarget(p) || p.StopLoss >= p.EntryPrice) return false;
        if (_trustedSimulation) return true;
        var a = _state.OpenAdmissions.SingleOrDefault(x => x.OperationId == _state.ExecutionTest?.OperationId);
        if (a?.Fill is not { } f || a.Stage != AdmissionStage.Bound || f.PositionId != p.Id || f.EntryPrice != p.EntryPrice || f.EntryTimeUtc != p.EntryTimeUtc
            || ScopedRecoveryPolicy.Units(p.VolumeUnits, f.VolumeStep) != ScopedRecoveryPolicy.Units(f.InitialVolumeUnits, f.VolumeStep)) return false;
        var spec = _market.GetSpec(p.Symbol); if (spec == null) return false;
        return StopMatches(p.Symbol, p.TakeProfit, f.EntryPrice + a.Request.TakeProfitPips!.Value * spec.PipSize)
            && (!originalStop || StopMatches(p.Symbol, p.StopLoss, f.EntryPrice - a.Request.StopLossPips * spec.PipSize));
    }

    // Observe before generic protection repair, so missing initial protection cannot be repaired into PASS.
    private void ObserveTechnicalProtection(IReadOnlyList<PositionInfo> positions)
    {
        var t = _test; if (t == null || t.Step is < 1 or > 4) return;
        var p = positions.FirstOrDefault(x => x.Id == t.PositionId);
        if (p != null && !TechnicalPositionMatches(p, t, t.Step <= 2)) t.Failed = true;
    }

    private bool PublishExecutionPass(DateTime utc)
    {
        var c = _state.ExecutionTest;
        if (c == null || c.Outcome != ExecutionTestOutcome.InProgress || !_persistenceHealthy || _stateReadFault
            || !ScopedProjectionHealthy || _state.ClosedOutbox.Count != 0 || _state.ScopeConflict.Length != 0) return false;
        var a = _state.OpenAdmissions.SingleOrDefault(x => x.OperationId == c.OperationId);
        if (a?.Stage != AdmissionStage.Terminal || a.Fill?.PositionId != c.PositionId || !_state.AccountedPositions.Contains(c.PositionId)
            || _state.PendingCloses.ContainsKey(c.PositionId) || _broker.GetPositions().Any(p => p.Id == c.PositionId)) return false;
        var deals = _state.ClosingDeals.Where(d => d.PositionId == c.PositionId).ToArray();
        return SaveExecutionEvidence(x => x with { Outcome = ExecutionTestOutcome.Passed, CompletedUtc = utc,
            ClosingDealIds = deals.Select(d => d.ClosingDealId!.Value).OrderBy(id => id).ToArray(), NetProfit = deals.Sum(d => d.NetProfit),
            Message = "Pełny łańcuch open/SL/TP/modify/requested-close oraz trwałe rozliczenie ekonomiczne." }, utc);
    }

    private static void ValidateExecutionDocument(JsonElement root)
    {
        if (!root.TryGetProperty("ExecutionTest", out var c)) throw new JsonException("missing v4 ExecutionTest");
        if (c.ValueKind == JsonValueKind.Null) return;
        if (c.ValueKind != JsonValueKind.Object || typeof(ExecutionTestCertificate).GetProperties().Any(p => !c.TryGetProperty(p.Name, out _)))
            throw new JsonException("missing explicit execution evidence field");
        var identity = c.GetProperty("Identity");
        if (identity.ValueKind != JsonValueKind.Object || typeof(RecoveryIdentity).GetProperties().Any(p => !identity.TryGetProperty(p.Name, out _)))
            throw new JsonException("missing explicit execution identity field");
    }

    private static void ValidateExecutionState(EngineState s)
    {
        var c = s.ExecutionTest; if (c == null) return;
        if (c.RecordVersion != 1 || c.ProtocolVersion <= 0 || string.IsNullOrWhiteSpace(c.TestVersion) || string.IsNullOrWhiteSpace(c.ScopeVersion)
            || c.Identity == null || !c.Identity.Valid || c.BaselineId == Guid.Empty || c.StartedUtc.Kind != DateTimeKind.Utc || c.StartedUtc == default
            || !Enum.IsDefined(typeof(ExecutionTestOutcome), c.Outcome) || c.Message == null || c.ClosingDealIds == null
            || new[] { c.InitialStop, c.TakeProfit, c.TightenedStop, c.NetProfit }.Any(v => !double.IsFinite(v))) throw new JsonException("invalid execution certificate");
        var a = s.OpenAdmissions.SingleOrDefault(x => x.OperationId == c.OperationId);
        if (a == null || a.Identity != c.Identity || a.BaselineId != c.BaselineId
            || !ExecutionLedger.TryParseComment(a.Request.Comment, out var strategy, out var signal) || strategy != StrategyId.ExecutionTest || signal != a.Request.SignalId)
            throw new JsonException("execution admission mismatch");
        if (c.Outcome != ExecutionTestOutcome.Passed) return;
        var f = a.Fill;
        var deals = s.ClosingDeals.Where(d => d.PositionId == c.PositionId).ToArray();
        using var riskInputs = JsonDocument.Parse(a.RiskInputsJson);
        var instrument = riskInputs.RootElement.GetProperty("Instrument");
        double pip = instrument.GetProperty("PipSize").GetDouble(), tick = instrument.GetProperty("TickSize").GetDouble();
        bool price(double actual, double expected) => double.IsFinite(pip) && pip > 0 && double.IsFinite(tick) && tick > 0
            && Math.Abs(actual - expected) <= tick * 0.500001 + 1e-10;
        bool utc(DateTime t) => t.Kind == DateTimeKind.Utc && t != default;
        if (a.Stage != AdmissionStage.Terminal || f == null || f.PositionId != c.PositionId || c.Identity != s.Baseline?.Identity || c.BaselineId != s.Baseline?.BaselineId
            || !s.AccountedPositions.Contains(c.PositionId) || !s.ClosedRecords.Any(r => r.PositionId == c.PositionId && r.SignalId == a.Request.SignalId)
            || s.PendingCloses.ContainsKey(c.PositionId) || c.InitialStop <= 0 || c.InitialStop >= f.EntryPrice || c.TakeProfit <= f.EntryPrice
            || c.TightenedStop <= c.InitialStop || c.TightenedStop >= f.EntryPrice
            || !price(c.InitialStop, f.EntryPrice - a.Request.StopLossPips * pip) || !price(c.TakeProfit, f.EntryPrice + a.Request.TakeProfitPips!.Value * pip)
            || !utc(c.ProtectionObservedUtc) || !utc(c.ModifyRequestedUtc) || !utc(c.ModifyObservedUtc) || !utc(c.CloseRequestedUtc) || !utc(c.CloseConfirmedUtc) || !utc(c.CompletedUtc)
            || c.StartedUtc > c.ProtectionObservedUtc || c.ProtectionObservedUtc > c.ModifyRequestedUtc || c.ModifyRequestedUtc > c.ModifyObservedUtc
            || c.ModifyObservedUtc > c.CloseRequestedUtc || c.CloseRequestedUtc > c.CloseConfirmedUtc || c.CloseConfirmedUtc > c.CompletedUtc
            || deals.Length == 0 || deals.Any(d => d.ExitTimeUtc < c.CloseRequestedUtc || d.ExitTimeUtc > c.CompletedUtc || d.Label != c.Identity.Label || d.Comment != a.Request.Comment)
            || !c.ClosingDealIds.SequenceEqual(deals.Select(d => d.ClosingDealId!.Value).OrderBy(id => id))
            || deals.Sum(d => ScopedRecoveryPolicy.Units(d.Volume, f.VolumeStep)) != ScopedRecoveryPolicy.Units(f.InitialVolumeUnits, f.VolumeStep)
            || Math.Abs(c.NetProfit - deals.Sum(d => d.NetProfit)) > 1e-7)
            throw new JsonException("incomplete terminal execution evidence");
    }
}
