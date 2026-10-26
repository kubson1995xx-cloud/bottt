using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Apex.Core.Execution;
using Apex.Core.Management;
using Apex.Core.Model;
using Apex.Core.Risk;

namespace Apex.Core.Engine;

public sealed partial class TradingEngine
{
    private static readonly ConditionalWeakTable<RecoveryBaseline, object> GeneratedProposals = new();
    private ScopedOpenPermit? _scopedPermit;
    private ScopedReceiptAuthority? _scopedAuthority;
    private ScopedReceipt? _lastScopedReceipt;
    private TimeSpan _lastScopedDeadline;
    private bool ScopedProjectionHealthy => _projectionReconciled && _journal is IRecoveryJournal { IsHealthy: true };
    internal RecoveryBaseline? ScopedBaseline => _state.Baseline;
    internal IReadOnlyList<OpenAdmission> ScopedAdmissions => _state.OpenAdmissions.AsReadOnly();
    internal long LastScopedObservationSequence => _state.OpenAdmissions.Select(x => x.LastObservationSequence).DefaultIfEmpty(-1).Max();
    public ScopedRecoveryStatus ScopedStatus { get; private set; }
    public string ScopedMessage { get; private set; } = "Unestablished: runtime producer not integrated";

    internal static RecoveryBaseline CreateBaselineProposal(RecoveryIdentity identity)
    {
        var proposal = new RecoveryBaseline { BaselineId = Guid.NewGuid(), ScopeVersion = ScopedRecoveryPolicy.ScopeVersion,
            Identity = identity with { Label = "A" + Convert.ToHexString(RandomNumberGenerator.GetBytes(9)) } };
        if (!proposal.Identity.Valid) throw new ArgumentException("Incomplete DEMO/hedged scope identity");
        GeneratedProposals.Add(proposal, new object());
        return proposal;
    }

    private bool MatchesAccount(RecoveryIdentity identity) => identity.Valid && identity.AccountNumber == _state.AccountNumber
        && identity.Broker == _state.Broker && identity.Currency == _state.Currency && identity.Label == _cfg.Label
        && _broker.GetAccount() is var a && a.Number == identity.AccountNumber && a.BrokerName == identity.Broker
        && a.Currency == identity.Currency && a.IsLive == identity.IsLive && a.IsHedging == identity.IsHedging;

    private EngineState ScopeCandidate()
    {
        var next = JsonSerializer.Deserialize<EngineState>(JsonSerializer.Serialize(_state, StateJson), StateJson)!;
        next.Plans = JsonSerializer.Deserialize<List<TradePlan>>(JsonSerializer.Serialize(_plans.Values, StateJson), StateJson)!;
        next.Ledger = JsonSerializer.Deserialize<List<LedgerEntry>>(JsonSerializer.Serialize(_ledger.Entries, StateJson), StateJson)!;
        next.Risk = JsonSerializer.Deserialize<RiskState>(JsonSerializer.Serialize(_risk.State, StateJson), StateJson)!;
        next.PendingCloses = JsonSerializer.Deserialize<Dictionary<long, CloseIntent>>(JsonSerializer.Serialize(_pendingCloses, StateJson), StateJson)!;
        return next;
    }

    // All scope transitions, including rejection and conflict tombstones, use the same authority.
    // Ambiguous publication freezes this process; never save the old in-memory revision over it.
    private bool CommitScope(EngineState next, DateTime utc)
    {
        _scopedPermit = null;
        if (_stateReadFault || !_persistenceHealthy) return false;
        try
        {
            ValidateFinancialState(next); ValidateScopeState(next);
            next.DurableRevision = checked(_state.DurableRevision + 1); next.SavedUtc = utc;
            _store.Set(StoreKey(next.AccountNumber), JsonSerializer.Serialize(next, StateJson)); _store.Flush();
            if (next.ClosedRecords.Count != _state.ClosedRecords.Count) _projectionReconciled = false;
            _state = next; _ledger.Restore(next.Ledger);
            _plans.Clear(); foreach (var p in next.Plans) _plans.Add(p.PositionId, p);
            _risk = new RiskManager(_cfg.Risk, next.Risk);
            _pendingCloses.Clear(); foreach (var p in next.PendingCloses) _pendingCloses.Add(p.Key, p.Value);
            _dirty = false; _lastSaveUtc = utc;
            return true;
        }
        catch (Exception ex)
        {
            _stateReadFault = true; _persistenceHealthy = false;
            ScopedStatus = ScopedRecoveryStatus.AwaitingEvidence; ScopedMessage = "ambiguous scope persistence: " + ex.Message;
            HistoryReady = false;
            return false;
        }
    }

    internal bool ReserveBaseline(RecoveryBaseline proposal, DateTime utc)
    {
        if (!GeneratedProposals.TryGetValue(proposal, out _) || !MatchesAccount(proposal.Identity) || proposal.Stage != BaselineStage.Proposed
            || _state.Baseline != null || _state.LegacyScopeQuarantine || _stateReadFault || !_persistenceHealthy || !ScopedProjectionHealthy
            || _plans.Count != 0 || _ledger.Entries.Count != 0 || _pendingCloses.Count != 0 || _state.ClosingDeals.Count != 0
            || _state.OpenAdmissions.Count != 0 || _state.ClosedRecords.Count != 0 || _state.ClosedOutbox.Count != 0) return false;
        var next = ScopeCandidate(); next.Baseline = proposal; next.Label = proposal.Identity.Label;
        return CommitScope(next, utc) && ScopedProjectionHealthy;
    }

    private bool ReceiptValid(ScopedReceipt r, ScopedReceiptAuthority a, DateTime utc, bool current)
    {
        var b = _state.Baseline;
        return ReferenceEquals(a.Owner, this) && b != null && b.Identity == r.Identity && b.BaselineId == r.BaselineId
            && b.ScopeVersion == ScopedRecoveryPolicy.ScopeVersion && MatchesAccount(r.Identity)
            && r.TransportComplete && r.BrokerConnected && !string.IsNullOrWhiteSpace(r.TransportGeneration)
            && !string.IsNullOrWhiteSpace(r.BrokerGeneration) && !string.IsNullOrWhiteSpace(r.RequestId) && !string.IsNullOrWhiteSpace(r.ReceiptId)
            && r.Positions != null && r.PendingOrders != null && r.Closed != null && r.OpeningObservations != null
            && r.Sequence >= 0 && r.PositionRevision >= 0 && r.PendingOrderRevision >= 0
            && r.CapturedUtc.Kind == DateTimeKind.Utc && r.ReceivedUtc.Kind == DateTimeKind.Utc
            && r.CapturedUtc <= r.ReceivedUtc && r.ReceivedUtc <= utc && r.ReceivedUtc - r.CapturedUtc <= TimeSpan.FromSeconds(30)
            && (!current || _broker.IsConnected && utc - r.CapturedUtc <= TimeSpan.FromSeconds(30) && a.IsCurrent(r));
    }

    internal bool EstablishBaseline(ScopedReceipt receipt, ScopedReceiptAuthority authority, DateTime utc)
    {
        // Pin before account/fence reads, candidate serialization or durable work. Publication
        // must never grant a fresh lifetime from the unchanged entry-time UTC after a stall.
        var expires = ReceiptDeadline(receipt, authority, utc);
        _scopedPermit = null;
        if (_state.Baseline?.Stage != BaselineStage.Proposed || _stateReadFault || _state.LegacyScopeQuarantine
            || !ScopedProjectionHealthy
            || !ReceiptValid(receipt, authority, utc, true) || receipt.Positions!.Count != 0 || receipt.PendingOrders!.Count != 0
            || receipt.Closed!.Any(x => x.Label == _cfg.Label) || receipt.OpeningObservations!.Any(x => x.Label == _cfg.Label)) return false;
        var next = ScopeCandidate(); next.Baseline = next.Baseline! with { Stage = BaselineStage.Established, EstablishedUtc = utc,
            EstablishingReceiptId = receipt.ReceiptId, EstablishingRevision = checked(next.DurableRevision + 1) };
        if (!CommitScope(next, utc)) return false;
        return PublishScoped(receipt, authority, utc, true, expires);
    }

    private static TimeSpan ReceiptDeadline(ScopedReceipt receipt, ScopedReceiptAuthority authority, DateTime utc) =>
        authority.MonotonicNow() + TimeSpan.FromSeconds(30) - (utc - receipt.CapturedUtc);

    private bool CurrentScopedReceipt(ScopedReceipt receipt, ScopedReceiptAuthority authority, DateTime utc, TimeSpan expires) =>
        ReceiptValid(receipt, authority, utc, true) && ScopedProjectionHealthy && authority.MonotonicNow() < expires;

    private bool PublishScoped(ScopedReceipt receipt, ScopedReceiptAuthority authority, DateTime utc, bool balanced, TimeSpan expires)
    {
        _scopedPermit = null;
        if (_state.ScopeConflict.Length != 0) { ScopedStatus = ScopedRecoveryStatus.Conflict; ScopedMessage = _state.ScopeConflict; return false; }
        ScopedStatus = ScopedRecoveryStatus.AwaitingEvidence;
        ScopedMessage = "AwaitingEvidence: obligations, current receipt or durable export";
        if (!balanced || !_persistenceHealthy || _stateReadFault || _state.ClosedOutbox.Count != 0 || !ScopedProjectionHealthy) return false;
        // Freeze the caller's collections. Authority validates exactly this frozen receipt at use.
        var frozen = JsonSerializer.Deserialize<ScopedReceipt>(JsonSerializer.Serialize(receipt, StateJson), StateJson)!;
        if (!CurrentScopedReceipt(frozen, authority, utc, expires)) return false;
        _scopedAuthority = authority;
        _lastScopedReceipt = frozen; _lastScopedDeadline = expires;
        _scopedPermit = new ScopedOpenPermit(this, frozen, _state.DurableRevision, expires);
        ScopedStatus = ScopedRecoveryStatus.Ready;
        ScopedMessage = "ScopedReady: dedicated/exclusive application epoch only; broker CoverageUnknown";
        return true;
    }

    internal ScopedOpenPermit? GetScopedPermit(ScopedReceiptAuthority authority, DateTime utc) =>
        ValidateScopedPermit(_scopedPermit, authority, utc) ? _scopedPermit : null;

    internal bool ValidateScopedPermit(ScopedOpenPermit? permit, ScopedReceiptAuthority authority, DateTime utc) =>
        permit != null && ReferenceEquals(permit, _scopedPermit) && ReferenceEquals(permit.Owner, this)
        && ReferenceEquals(authority, _scopedAuthority) && permit.DurableRevision == _state.DurableRevision
        && !_stateReadFault && _persistenceHealthy && _state.ScopeConflict.Length == 0 && _state.Baseline?.Stage == BaselineStage.Established
        && _state.ClosedOutbox.Count == 0 && CurrentScopedReceipt(permit.Receipt, authority, utc, permit.Expires);

    private static OpeningRequestEvidence RequestEvidence(OrderRequest r, string op) => new()
    { ExecutionId = op, SignalId = r.SignalId, Symbol = r.Symbol, Direction = r.Direction, VolumeUnits = r.VolumeUnits,
        Label = r.Label, Comment = r.Comment, StopLossPips = r.StopLossPips, TakeProfitPips = r.TakeProfitPips, MaxSlippagePips = r.MaxSlippagePips };

    // Called by BOTH strategy and technical-test dispatch. It does not bypass their risk gates.
    // Production without Phase C cannot obtain _scopedAuthority/permit from this internal API.
    internal OrderOutcome SendAdmittedOpen(OrderRequest request, TradePlan plan, DateTime utc)
    {
        // CommitScope consumes the issued object. Retain its ORIGINAL deadline locally for
        // the final send fence; a durable admission is not permission to send stale evidence.
        var permit = _scopedPermit;
        var authority = _scopedAuthority;
        // The offline simulator owns no broker risk and preserves its isolated aggregate callback
        // contract. Its authority is structural and immutable, never EngineConfig/JSON alone.
        if (_simulation && _state.Baseline == null && CanOperate(out _)) return _broker.OpenMarket(request);
        if (_state.Baseline == null && !_trustedSimulation)
            return OrderOutcome.Rejected(BrokerErrorKind.NoTradingPermission, "unestablished production scope");
        if (_state.Baseline != null && (_scopedAuthority == null || GetScopedPermit(_scopedAuthority, utc) == null))
            return OrderOutcome.Rejected(BrokerErrorKind.NoTradingPermission, "scoped permit required");
        if (!CanOperate(out _) || _stateReadFault || !_persistenceHealthy || _state.LegacyScopeQuarantine && _state.Baseline != null
            || _state.OpenAdmissions.Any(x => x.Request.SignalId == request.SignalId) || _state.OpenAdmissions.Any(x => x.Stage == AdmissionStage.OutcomeUnknown)
            || string.IsNullOrWhiteSpace(request.SignalId) || string.IsNullOrWhiteSpace(request.Symbol) || string.IsNullOrWhiteSpace(request.Comment)
            || request.Label != _cfg.Label || request.Direction is not (TradeDirection.Long or TradeDirection.Short)
            || !(request.VolumeUnits > 0) || !double.IsFinite(request.VolumeUnits) || !(request.StopLossPips > 0) || !double.IsFinite(request.StopLossPips)
            || request.TakeProfitPips is not > 0 || !double.IsFinite(request.TakeProfitPips.Value)
            || !double.IsFinite(plan.RiskAmount) || plan.RiskAmount < 0)
            return OrderOutcome.Rejected(BrokerErrorKind.NoTradingPermission, "admission invalid or unresolved");
        var spec = _market.GetSpec(request.Symbol);
        if (spec == null || spec.Validate() != null) return OrderOutcome.Rejected(BrokerErrorKind.InvalidRequest, "missing instrument risk/quantum inputs");
        try { ScopedRecoveryPolicy.Units(request.VolumeUnits, spec.VolumeStep); }
        catch (Exception) { return OrderOutcome.Rejected(BrokerErrorKind.InvalidRequest, "non-normalized requested volume"); }
        string op = Guid.NewGuid().ToString("N"); var evidence = RequestEvidence(request, op);
        var receipt = permit?.Receipt;
        var identity = _state.Baseline?.Identity ?? new RecoveryIdentity { AccountNumber = _state.AccountNumber, Broker = _state.Broker,
            Currency = _state.Currency, Label = _cfg.Label, IsLive = false, IsHedging = _broker.GetAccount().IsHedging };
        var admission = new OpenAdmission { BaselineId = _state.Baseline?.BaselineId ?? Guid.Empty, Identity = identity, OperationId = op,
            Request = evidence, RequestFingerprint = ScopedRecoveryPolicy.Fingerprint(evidence), PlanJson = JsonSerializer.Serialize(plan, StateJson),
            RiskAmount = plan.RiskAmount, RequestedVolumeStep = spec.VolumeStep,
            RiskInputsJson = JsonSerializer.Serialize(new { Account = _broker.GetAccount(), Instrument = spec, Quote = _market.GetQuote(request.Symbol),
                Settings = _cfg.Risk, RiskState = _risk.State, Positions = _broker.GetPositions() }, StateJson),
            ReservedUtc = utc, ReceiptId = receipt?.ReceiptId ?? "legacy-complete-compatibility",
            BrokerGeneration = receipt?.BrokerGeneration ?? "unknown", TransportGeneration = receipt?.TransportGeneration ?? _historyGeneration,
            PositionRevision = receipt?.PositionRevision ?? 0, PendingOrderRevision = receipt?.PendingOrderRevision ?? 0 };
        var next = ScopeCandidate(); next.OpenAdmissions.Add(admission);
        if (!CommitScope(next, utc)) return OrderOutcome.Rejected(BrokerErrorKind.NoTradingPermission, "pre-send durability failed; not sent");
        var send = new OrderRequest { ExecutionId = op, SignalId = evidence.SignalId, Symbol = evidence.Symbol, Direction = evidence.Direction,
            VolumeUnits = evidence.VolumeUnits, StopLossPips = evidence.StopLossPips, TakeProfitPips = evidence.TakeProfitPips,
            Label = evidence.Label, Comment = evidence.Comment, MaxSlippagePips = evidence.MaxSlippagePips };
        OrderOutcome result;
        if (receipt != null && (authority == null || !CurrentScopedReceipt(receipt, authority, utc, permit!.Expires)))
        {
            RecoverOpeningOutcome(op, OrderOutcome.Rejected(BrokerErrorKind.NoTradingPermission, "scope expired or fenced before send: not sent"), utc);
            return OrderOutcome.Rejected(BrokerErrorKind.NoTradingPermission, "scope expired or fenced before send");
        }
        try
        {
            result = receipt != null && _broker is IScopedDispatchGateway scopedGateway
                ? scopedGateway.OpenScoped(send, new ScopedDispatchAuthorization(receipt, r =>
                    r.ExecutionId == op && RequestEvidence(r, op) == evidence && authority != null
                    && !_stateReadFault && _persistenceHealthy && _state.ScopeConflict.Length == 0
                    && _state.OpenAdmissions.Any(a => a.OperationId == op && a.Request == evidence && a.Stage == AdmissionStage.OutcomeUnknown)
                    && CurrentScopedReceipt(receipt, authority, utc, permit!.Expires)))
                : _broker.OpenMarket(send);
        }
        catch (Exception ex) { result = OrderOutcome.Uncertain(BrokerErrorKind.TechnicalError, ex.Message); }
        if (!RecoverOpeningOutcome(op, result, utc))
            return new OrderOutcome { Status = OrderStatus.Uncertain, PositionId = result.PositionId, OpeningFill = result.OpeningFill,
                FillPrice = result.FillPrice, Error = BrokerErrorKind.TechnicalError, Message = "opening evidence unresolved or persistence ambiguous" };
        return result;
    }

    private static void ValidateFill(OpenAdmission a, OpeningFillEvidence f)
    {
        if (f.Request != a.Request || f.VolumeStep != a.RequestedVolumeStep || f.PositionId <= 0 || f.EntryPrice <= 0 || !double.IsFinite(f.EntryPrice) || f.EntryTimeUtc.Kind != DateTimeKind.Utc
            || f.EntryTimeUtc == default || f.InitialVolumeUnits <= 0 || f.InitialVolumeUnits > a.Request.VolumeUnits || f.OpeningDeals.Count != 1)
            throw new InvalidOperationException("opening request/actual witness mismatch");
        ScopedRecoveryPolicy.Units(a.Request.VolumeUnits, f.VolumeStep);
        var initial = ScopedRecoveryPolicy.Units(f.InitialVolumeUnits, f.VolumeStep);
        var d = f.OpeningDeals[0];
        if (d.DealId <= 0 || d.OrderId <= 0 || d.PositionId != f.PositionId || d.Price != f.EntryPrice || d.TimeUtc != f.EntryTimeUtc
            || d.Symbol != a.Request.Symbol || d.Label != a.Request.Label || d.Comment != a.Request.Comment || d.Direction != a.Request.Direction
            || ScopedRecoveryPolicy.Units(d.VolumeUnits, f.VolumeStep) != initial) throw new InvalidOperationException("opening graph mismatch");
    }

    internal bool RecoverOpeningOutcome(string operationId, OrderOutcome outcome, DateTime utc)
    {
        if (_stateReadFault) return false;
        var next = ScopeCandidate(); int i = next.OpenAdmissions.FindIndex(x => x.OperationId == operationId);
        if (i < 0) return false;
        var a = next.OpenAdmissions[i];
        try
        {
            if (outcome.OpeningFill is { } fill)
            {
                ValidateFill(a, fill);
                if (outcome.PositionId != fill.PositionId || a.Stage == AdmissionStage.Rejected
                    || next.OpenAdmissions.Any(x => x.OperationId != a.OperationId && x.Fill?.PositionId == fill.PositionId))
                    throw new InvalidOperationException("opening outcome identity collision");
                if (a.Fill != null && JsonSerializer.Serialize(a.Fill, StateJson) != JsonSerializer.Serialize(fill, StateJson))
                    throw new InvalidOperationException("immutable opening evidence conflict");
                if (a.Fill != null) return true;
                next.OpenAdmissions[i] = a with { Fill = fill, Stage = AdmissionStage.Bound };
                var plan = JsonSerializer.Deserialize<TradePlan>(a.PlanJson, StateJson)!;
                plan.PositionId = fill.PositionId; plan.SignalId = a.Request.SignalId; plan.Symbol = a.Request.Symbol; plan.Direction = a.Request.Direction;
                plan.InitialVolume = fill.InitialVolumeUnits; plan.EntryPrice = fill.EntryPrice; plan.EntryTimeUtc = fill.EntryTimeUtc;
                plan.RiskAmount = a.RiskAmount; plan.Reconstructed = false;
                if (plan.InitialRisk > 0) plan.InitialStop = fill.EntryPrice - (int)a.Request.Direction * plan.InitialRisk;
                if (next.Plans.All(x => x.PositionId != fill.PositionId)) next.Plans.Add(plan);
                var ledger = next.Ledger.FirstOrDefault(x => x.SignalId == a.Request.SignalId);
                if (ledger != null) { ledger.State = SignalState.Executed; ledger.PositionId = fill.PositionId; }
            }
            else if (outcome.Status == OrderStatus.Rejected && a.Fill == null)
            {
                next.OpenAdmissions[i] = a with { Stage = AdmissionStage.Rejected, OutcomeMessage = outcome.Message };
                var ledger = next.Ledger.FirstOrDefault(x => x.SignalId == a.Request.SignalId);
                if (ledger != null) { ledger.State = SignalState.Consumed; ledger.Note = outcome.Message; }
            }
            // A Filled status without a witness remains OutcomeUnknown, including fast disappearance.
            return CommitScope(next, utc);
        }
        catch (Exception ex) { return LatchScopeConflict(ex.Message, utc); }
    }

    private List<PositionInfo> ScopedManagedPositions(List<PositionInfo> positions, DateTime utc)
    {
        if (_state.Baseline == null) return positions;
        var managed = new List<PositionInfo>();
        foreach (var p in positions)
        {
            var a = _state.OpenAdmissions.FirstOrDefault(x => x.Fill?.PositionId == p.Id);
            if (a?.Fill is { } f && p.Label == a.Request.Label && p.Symbol == a.Request.Symbol && p.Direction == a.Request.Direction
                && p.Comment == a.Request.Comment && p.EntryPrice == f.EntryPrice && p.EntryTimeUtc == f.EntryTimeUtc && a.Stage == AdmissionStage.Bound)
                managed.Add(p);
            else if (_state.ScopeConflict.Length == 0) LatchScopeConflict("LabelCollision/entry mutation: automatic management quarantined", utc);
        }
        return managed;
    }

    private bool LatchScopeConflict(string message, DateTime utc)
    {
        _scopedPermit = null; var next = ScopeCandidate(); next.ScopeConflict = message;
        if (next.Mode != EngineMode.EmergencyStopped) { next.Mode = EngineMode.Stopped; next.ModeReason = message; }
        CommitScope(next, utc); ScopedStatus = ScopedRecoveryStatus.Conflict; ScopedMessage = message; HistoryReady = false;
        return false;
    }

    internal bool ReconcileScoped(ScopedReceipt receipt, ScopedReceiptAuthority authority, DateTime utc, TimeSpan? originalDeadline = null)
    {
        var expires = ReceiptDeadline(receipt, authority, utc);
        if (originalDeadline.HasValue && originalDeadline.Value < expires) expires = originalDeadline.Value;
        _scopedPermit = null;
        if (_stateReadFault || _state.Baseline?.Stage != BaselineStage.Established || _state.LegacyScopeQuarantine
            || !ReceiptValid(receipt, authority, utc, false)) return false;
        if (_state.ScopeConflict.Length != 0) { ScopedStatus = ScopedRecoveryStatus.Conflict; return false; }
        try
        {
            var next = ScopeCandidate();
            bool balanced = !next.Ledger.Any(e => e.State == SignalState.PendingVerification
                && next.OpenAdmissions.All(a => a.Request.SignalId != e.SignalId || a.Stage == AdmissionStage.OutcomeUnknown));
            var bound = next.OpenAdmissions.Where(x => x.Fill != null).ToDictionary(x => x.Fill!.PositionId);
            if (next.Plans.Any(p => !bound.ContainsKey(p.PositionId)) || next.ClosingDeals.Any(d => !bound.ContainsKey(d.PositionId))
                || next.AccountedPositions.Any(id => !bound.ContainsKey(id))) throw new InvalidOperationException("unmanifested legacy accounting/plan obligation");
            if (receipt.PendingOrders!.Count > 0) throw new InvalidOperationException("dedicated account pending order / label collision");
            if (receipt.Positions!.GroupBy(x => x.Id).Any(x => x.Count() != 1)) throw new InvalidOperationException("duplicate position identity");
            foreach (var p in receipt.Positions!)
            {
                if (!bound.TryGetValue(p.Id, out var a)) throw new InvalidOperationException("unknown/foreign position or label collision");
                var f = a.Fill!;
                if (p.Label != a.Request.Label || p.Symbol != a.Request.Symbol || p.Direction != a.Request.Direction || p.Comment != a.Request.Comment
                    || p.EntryPrice != f.EntryPrice || p.EntryTimeUtc != f.EntryTimeUtc
                    || a.Stage == AdmissionStage.Terminal && receipt.Sequence > a.LastObservationSequence)
                    throw new InvalidOperationException("position entry identity/external mutation");
            }
            foreach (var d in receipt.OpeningObservations!)
            {
                if (!bound.TryGetValue(d.PositionId, out var a) || !a.Fill!.OpeningDeals.Contains(d))
                    throw new InvalidOperationException("unsupported opening addition/identity mutation");
            }
            var known = next.ClosingDeals.ToDictionary(DealKey);
            foreach (var d in receipt.Closed!)
            {
                if (d.Label != _cfg.Label) continue;
                if (!ValidDeal(d) || !bound.TryGetValue(d.PositionId, out var a)) throw new InvalidOperationException("unknown closing deal / label collision");
                var f = a.Fill!;
                if (d.Symbol != a.Request.Symbol || d.Direction != a.Request.Direction || d.Comment != a.Request.Comment
                    || d.EntryPrice != f.EntryPrice || d.EntryTimeUtc != f.EntryTimeUtc) throw new InvalidOperationException("closing entry identity mutation");
                ScopedRecoveryPolicy.Units(d.Volume, f.VolumeStep);
                if (known.TryGetValue(DealKey(d), out var prior)) { if (!SameDeal(prior, d)) throw new InvalidOperationException("conflicting duplicate closing deal"); continue; }
                if (a.Stage == AdmissionStage.Terminal || next.AccountedPositions.Contains(d.PositionId)) throw new InvalidOperationException("new closing deal after finalization");
                next.ClosingDeals.Add(d); known.Add(DealKey(d), d); next.RealizedTotal += d.NetProfit;
            }
            for (int i = 0; i < next.OpenAdmissions.Count; i++)
            {
                var a = next.OpenAdmissions[i]; if (a.Stage == AdmissionStage.Rejected) continue;
                if (a.Fill == null) { balanced = false; continue; }
                var f = a.Fill; ValidateFill(a, f);
                var deals = next.ClosingDeals.Where(x => x.PositionId == f.PositionId).OrderBy(x => x.ExitTimeUtc).ThenBy(x => x.ClosingDealId).ToList();
                var p = receipt.Positions.FirstOrDefault(x => x.Id == f.PositionId);
                decimal c = 0; foreach (var d in deals) c = checked(c + ScopedRecoveryPolicy.Units(d.Volume, f.VolumeStep));
                bool olderObservation = receipt.Sequence < a.LastObservationSequence;
                double observedRemaining = olderObservation ? a.LastRemainingUnits!.Value : p?.VolumeUnits ?? 0;
                decimal o = ScopedRecoveryPolicy.Units(observedRemaining, f.VolumeStep), v = ScopedRecoveryPolicy.Units(f.InitialVolumeUnits, f.VolumeStep);
                if (a.LastObservationSequence < receipt.Sequence)
                {
                    if (a.LastRemainingUnits is double prior && o > ScopedRecoveryPolicy.Units(prior, f.VolumeStep)) throw new InvalidOperationException("observed external volume growth");
                    a = a with { LastObservationSequence = receipt.Sequence, LastRemainingUnits = p?.VolumeUnits ?? 0 };
                    next.OpenAdmissions[i] = a;
                }
                if (c + o > v) throw new InvalidOperationException("closing plus remaining exceeds original quantum volume");
                if (c + o < v) { balanced = false; continue; }
                if (olderObservation) { balanced = false; continue; }
                if (p != null || a.Stage == AdmissionStage.Terminal) continue;
                if (deals.Count == 0) { balanced = false; continue; }
                var first = deals[0]; var last = deals[deals.Count - 1];
                var aggregate = new ClosedPositionInfo { PositionId = f.PositionId, Label = a.Request.Label, Comment = a.Request.Comment,
                    Symbol = a.Request.Symbol, Direction = a.Request.Direction, Volume = f.InitialVolumeUnits, EntryPrice = f.EntryPrice, EntryTimeUtc = f.EntryTimeUtc,
                    ExitPrice = FiniteSum(deals.Select(x => x.ExitPrice * x.Volume)) / f.InitialVolumeUnits, ExitTimeUtc = last.ExitTimeUtc,
                    GrossProfit = FiniteSum(deals.Select(x => x.GrossProfit)), Commission = FiniteSum(deals.Select(x => x.Commission)),
                    Swap = FiniteSum(deals.Select(x => x.Swap)), NetProfit = FiniteSum(deals.Select(x => x.NetProfit)), PlatformReason = last.PlatformReason };
                var record = BuildClosedRecord(aggregate);
                next.AccountedPositions.Add(f.PositionId); next.ClosedRecords.Add(record); next.ClosedOutbox.Add(record); next.RecentTrades.Add(record); next.TradesTotal++;
                next.OpenAdmissions[i] = a with { Stage = AdmissionStage.Terminal };
                next.Plans.RemoveAll(x => x.PositionId == f.PositionId); next.PendingCloses.Remove(f.PositionId);
            }
            if (!CommitScope(next, utc)) return false;
            EvaluateHealth(); if (!ExportClosed(utc)) return false;
            return PublishScoped(receipt, authority, utc, balanced, expires);
        }
        catch (Exception ex) { return LatchScopeConflict(ex.Message, utc); }
    }

    private static void ValidateScopeDocument(JsonElement root)
    {
        static void Required<T>(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object || typeof(T).GetProperties().Any(p => !value.TryGetProperty(p.Name, out _)))
                throw new JsonException("missing explicit " + typeof(T).Name + " field");
        }
        var baseline = root.GetProperty("Baseline");
        if (baseline.ValueKind != JsonValueKind.Null) { Required<RecoveryBaseline>(baseline); Required<RecoveryIdentity>(baseline.GetProperty("Identity")); }
        foreach (var a in root.GetProperty("OpenAdmissions").EnumerateArray())
        {
            Required<OpenAdmission>(a); Required<RecoveryIdentity>(a.GetProperty("Identity")); Required<OpeningRequestEvidence>(a.GetProperty("Request"));
            var fill = a.GetProperty("Fill");
            if (fill.ValueKind != JsonValueKind.Null)
            {
                Required<OpeningFillEvidence>(fill); Required<OpeningRequestEvidence>(fill.GetProperty("Request"));
                foreach (var d in fill.GetProperty("OpeningDeals").EnumerateArray()) Required<OpeningDealEvidence>(d);
            }
        }
    }

    private static void ValidateScopeState(EngineState s)
    {
        if (s.OpenAdmissions == null || s.ScopeConflict == null || s.DurableRevision < 0) throw new JsonException("missing scope state");
        if (s.Baseline is { } b && (!b.Identity.Valid || b.BaselineId == Guid.Empty || b.ScopeVersion != ScopedRecoveryPolicy.ScopeVersion
            || !Enum.IsDefined(typeof(BaselineStage), b.Stage) || b.Stage == BaselineStage.Established && (b.EstablishedUtc == default || b.EstablishingReceiptId.Length == 0 || b.EstablishingRevision <= 0)))
            throw new JsonException("invalid baseline identity/evidence");
        if (s.OpenAdmissions.GroupBy(x => x.OperationId).Any(g => g.Count() != 1) || s.OpenAdmissions.GroupBy(x => x.Request.SignalId).Any(g => g.Count() != 1))
            throw new JsonException("duplicate open admission");
        foreach (var a in s.OpenAdmissions)
        {
            if (a.OperationId.Length == 0 || a.OperationId != a.Request.ExecutionId || a.RequestFingerprint != ScopedRecoveryPolicy.Fingerprint(a.Request)
                || a.PlanJson.Length == 0 || a.RiskInputsJson.Length == 0 || a.ReceiptId.Length == 0 || a.ReservedUtc == default
                || !double.IsFinite(a.RiskAmount) || a.RiskAmount < 0 || !Enum.IsDefined(typeof(AdmissionStage), a.Stage)
                || s.Baseline != null && (a.BaselineId != s.Baseline.BaselineId || a.Identity != s.Baseline.Identity)
                || a.Stage is AdmissionStage.Bound or AdmissionStage.Terminal && a.Fill == null)
                throw new JsonException("invalid immutable admission");
            ScopedRecoveryPolicy.Units(a.Request.VolumeUnits, a.RequestedVolumeStep);
            if (a.Fill != null) ValidateFill(a, a.Fill);
        }
    }
}
