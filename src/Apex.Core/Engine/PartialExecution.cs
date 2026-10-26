using System;
using System.Linq;
using System.Text.Json;
using Apex.Core.Management;
using Apex.Core.Model;

namespace Apex.Core.Engine;

public sealed partial class TradingEngine
{
    private static bool WitnessMatches(PartialCloseIntent p, ReductionFillEvidence? f) => f != null
        && f.ActionId == p.ActionId && f.PositionId == p.PositionId && f.SignalId == p.SignalId && f.Label == p.Label && f.Symbol == p.Symbol
        && f.BeforeUnits == p.ObservedUnits && f.ClosedUnits == p.RequestedUnits && f.RemainingUnits == p.ExpectedRemaining;

    private static void ValidatePartialState(EngineState s)
    {
        foreach (var plan in s.Plans)
        {
            var p = plan.Partial;
            if (p == null) { if (plan.PartialDone) throw new JsonException("partial completion without evidence"); continue; }
            if (!Enum.IsDefined(typeof(PartialCloseStage), p.Stage) || p.PositionId != plan.PositionId || p.SignalId != plan.SignalId
                || p.Symbol != plan.Symbol || p.Label != s.Label || string.IsNullOrWhiteSpace(p.ActionId) || p.Attempt < 1)
                throw new JsonException("partial action identity");
            if (p.Stage == PartialCloseStage.LegacyUnknown) { if (plan.PartialDone) throw new JsonException("legacy partial claimed complete"); continue; }
            if (p.ReservedUtc.Kind != DateTimeKind.Utc || p.RetryAfterUtc.Kind != DateTimeKind.Utc || p.ObservationSequence < 0)
                throw new JsonException("partial action timestamp");
            var original = s.OpenAdmissions.SingleOrDefault(a => a.Fill?.PositionId == plan.PositionId);
            if (original?.Fill == null || original.Fill.VolumeStep != p.VolumeStep || original.Request.SignalId != p.SignalId
                || original.Request.Label != p.Label || original.Request.Symbol != p.Symbol) throw new JsonException("partial original binding");
            var before = ScopedRecoveryPolicy.Units(p.ObservedUnits, p.VolumeStep);
            if (before + p.ClosedBefore != ScopedRecoveryPolicy.Units(original.Fill.InitialVolumeUnits, p.VolumeStep)) throw new JsonException("partial original conservation");
            if (p.Stage != PartialCloseStage.Skipped)
            {
                var requested = ScopedRecoveryPolicy.Units(p.RequestedUnits, p.VolumeStep);
                var after = ScopedRecoveryPolicy.Units(p.ExpectedRemaining, p.VolumeStep);
                if (requested <= 0 || after <= 0 || before != requested + after || p.ClosedBefore < 0) throw new JsonException("partial action volume");
            }
            if (p.Confirmation != null && !WitnessMatches(p, p.Confirmation) || plan.PartialDone != (p.Stage == PartialCloseStage.Confirmed)
                || p.Stage == PartialCloseStage.Confirmed && (p.Confirmation == null
                    || s.ClosingDeals.Where(d => d.PositionId == plan.PositionId).Sum(d => ScopedRecoveryPolicy.Units(d.Volume, p.VolumeStep))
                        < p.ClosedBefore + ScopedRecoveryPolicy.Units(p.RequestedUnits, p.VolumeStep))) throw new JsonException("partial confirmation");
        }
    }

    private static void MigrateAndValidatePartials(EngineState s, string json)
    {
        if (s.SchemaVersion < 5)
        {
            foreach (var plan in s.Plans)
            {
                if (plan.PartialDone || plan.Exit?.PartialAtR > 0)
                    plan.Partial = new PartialCloseIntent { ActionId = "legacy-" + plan.PositionId, PositionId = plan.PositionId,
                        SignalId = plan.SignalId, Label = s.Label, Symbol = plan.Symbol, Attempt = 1,
                        Stage = PartialCloseStage.LegacyUnknown, Reason = "legacy: execution unknown; no retry" };
                plan.PartialDone = false;
            }
            s.SchemaVersion = 5;
        }
        else
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var plan in doc.RootElement.GetProperty("Plans").EnumerateArray())
            {
                if (!plan.TryGetProperty("Partial", out var p)) throw new JsonException("missing v5 partial field");
                if (p.ValueKind == JsonValueKind.Null) continue;
                string[] fields = { "ActionId", "PositionId", "SignalId", "Label", "Symbol", "Attempt", "ObservedUnits", "RequestedUnits", "ExpectedRemaining",
                    "VolumeStep", "ClosedBefore", "ObservationSequence", "ReservedUtc", "RetryAfterUtc", "Stage", "Reason", "Confirmation" };
                if (fields.Any(n => !p.TryGetProperty(n, out _))) throw new JsonException("incomplete partial intent");
            }
        }
        ValidatePartialState(s);
    }

    // Invoked only following fresh management evaluation. Reservation is irrevocable before
    // calling the gateway. A crash at any later point can only LOOK UP, never re-send this ID.
    internal bool RequestPartialClose(PositionInfo observed, double units, DateTime utc)
    {
        if (!CanOperate(out _) || !IsOwn(observed) || !_plans.TryGetValue(observed.Id, out var plan) || plan.PartialDone
            || _pendingCloses.ContainsKey(observed.Id) || !HasValidTarget(observed) || observed.StopLoss is not > 0
            || !double.IsFinite(observed.StopLoss.Value)) return false;
        var previous = plan.Partial;
        if (previous != null && (previous.Stage != PartialCloseStage.Rejected || utc < previous.RetryAfterUtc)) return false;
        var spec = _market.GetSpec(observed.Symbol); var quote = _market.GetQuote(observed.Symbol);
        if (spec == null || quote == null || !QuoteFresh(quote.Value, utc)) return false;
        if (_simulation)
        {
            // Structural simulation has no production replay authority; confirm the actual simulated remainder.
            double after = observed.VolumeUnits - units;
            if (units < spec.VolumeMin || after < spec.VolumeMin)
            {
                plan.Partial = new PartialCloseIntent { ActionId = "simulation-skip", PositionId = plan.PositionId, SignalId = plan.SignalId,
                    Label = observed.Label, Symbol = observed.Symbol, Attempt = 1, Stage = PartialCloseStage.Skipped, Reason = "simulation minimum: not executed" };
                return false;
            }
            var result = _broker.ClosePosition(observed.Id, units);
            if (result.Status == OrderStatus.Filled && OwnPositions().SingleOrDefault(p => p.Id == observed.Id)?.VolumeUnits == after)
                plan.PartialDone = true;
            return result.Status == OrderStatus.Filled;
        }
        if (_broker is not IPartialCloseGateway || _broker is not IScopedPartialDispatchGateway gateway || _scopedAuthority == null) return false;
        var authority = _scopedAuthority;
        var permit = GetScopedPermit(authority, utc);
        var snapshot = permit?.Receipt.Positions?.SingleOrDefault(p => p.Id == observed.Id);
        var admission = _state.OpenAdmissions.SingleOrDefault(a => a.Fill?.PositionId == observed.Id);
        if (permit == null || snapshot == null || admission?.Fill == null || spec.VolumeStep != admission.Fill.VolumeStep || snapshot.VolumeUnits != observed.VolumeUnits
            || snapshot.Label != observed.Label || snapshot.Comment != observed.Comment || snapshot.EntryPrice != observed.EntryPrice
            || snapshot.Symbol != observed.Symbol || snapshot.Direction != observed.Direction || snapshot.EntryTimeUtc != observed.EntryTimeUtc || admission.Request.SignalId != plan.SignalId || snapshot.StopLoss != observed.StopLoss
            || snapshot.TakeProfit != observed.TakeProfit || previous != null && permit.Receipt.Sequence <= previous.ObservationSequence) return false;
        var next = ScopeCandidate(); var nextPlan = next.Plans.Single(p => p.PositionId == observed.Id);
        var intent = new PartialCloseIntent { ActionId = "partial-" + Guid.NewGuid().ToString("N"), PositionId = observed.Id, SignalId = plan.SignalId,
            Label = observed.Label, Symbol = observed.Symbol, Attempt = checked((previous?.Attempt ?? 0) + 1), ObservedUnits = observed.VolumeUnits,
            RequestedUnits = units, VolumeStep = admission.Fill.VolumeStep, ObservationSequence = permit.Receipt.Sequence,
            ReservedUtc = utc, RetryAfterUtc = utc.AddSeconds(15), Stage = PartialCloseStage.Reserved };
        try
        {
            intent.ClosedBefore = next.ClosingDeals.Where(d => d.PositionId == observed.Id).Sum(d => ScopedRecoveryPolicy.Units(d.Volume, intent.VolumeStep));
            decimal before = ScopedRecoveryPolicy.Units(observed.VolumeUnits, intent.VolumeStep), close = ScopedRecoveryPolicy.Units(units, intent.VolumeStep);
            intent.ExpectedRemaining = (double)(before - close);
            if (units < spec.VolumeMin || intent.ExpectedRemaining < spec.VolumeMin || close <= 0 || close >= before)
            { intent.Stage = PartialCloseStage.Skipped; intent.Reason = "minimum close/remainder: not executed"; }
        }
        catch (Exception) { intent.Stage = PartialCloseStage.Skipped; intent.Reason = "invalid exact volume/quantum: not executed"; }
        nextPlan.Partial = intent;
        if (!CommitScope(next, utc) || intent.Stage == PartialCloseStage.Skipped) return false;
        // Original absolute permit deadline survives the reservation; do not issue a new lifetime.
        if (!CurrentScopedReceipt(permit.Receipt, authority, utc, permit.Expires)
            || !OwnPositions().Any(p => p.Id == observed.Id && p.VolumeUnits == observed.VolumeUnits && p.Comment == observed.Comment
                && p.StopLoss == observed.StopLoss && p.TakeProfit == observed.TakeProfit)) return false;
        OrderOutcome outcome;
        var request = new PartialCloseRequest(intent.ActionId, observed.Id, plan.SignalId, observed.VolumeUnits, units);
        var reservedRevision = _state.DurableRevision;
        try
        {
            outcome = gateway.ClosePartialScoped(request, new ScopedPartialDispatchAuthorization(permit.Receipt, r =>
                r == request && ReferenceEquals(authority, _scopedAuthority) && _state.DurableRevision == reservedRevision
                && !_stateReadFault && _persistenceHealthy && _state.ScopeConflict.Length == 0
                && _state.Baseline?.Stage == BaselineStage.Established
                && _state.Plans.Any(p => p.PositionId == request.PositionId && p.Partial?.ActionId == request.ActionId
                    && p.Partial.Stage == PartialCloseStage.Reserved && p.Partial.SignalId == request.SignalId
                    && p.Partial.ObservedUnits == request.ObservedUnits && p.Partial.RequestedUnits == request.Units)
                && CurrentScopedReceipt(permit.Receipt, authority, utc, permit.Expires)
                && OwnPositions().Any(p => p.Id == observed.Id && p.VolumeUnits == observed.VolumeUnits && p.Comment == observed.Comment
                    && p.StopLoss == observed.StopLoss && p.TakeProfit == observed.TakeProfit)));
        }
        catch (Exception) { return true; } // durable Reserved remains uncertain
        var completed = ScopeCandidate(); var saved = completed.Plans.Single(p => p.PositionId == observed.Id).Partial!;
        ApplyPartialOutcome(saved, outcome);
        CommitScope(completed, utc);
        return true;
    }

    private static void ApplyPartialOutcome(PartialCloseIntent p, OrderOutcome outcome)
    {
        if (WitnessMatches(p, outcome.ReductionFill))
        { p.Confirmation = outcome.ReductionFill; p.Stage = PartialCloseStage.AwaitingEvidence; }
        else if (outcome.Status == OrderStatus.Rejected && !OrderOutcome.IsUncertainError(outcome.Error) && outcome.Error != BrokerErrorKind.InvalidRequest && outcome.ReductionFill == null)
        { p.Stage = PartialCloseStage.Rejected; p.Reason = outcome.Message; }
        else p.Stage = PartialCloseStage.AwaitingEvidence;
    }

    private void ReconcilePartials(EngineState next, ScopedReceipt receipt, ScopedReceiptAuthority authority, DateTime utc, TimeSpan expires)
    {
        foreach (var plan in next.Plans)
        {
            var p = plan.Partial;
            if (p == null || p.Stage is PartialCloseStage.Confirmed or PartialCloseStage.Skipped or PartialCloseStage.LegacyUnknown) continue;
            // Read only: never turn lookup absence or volume decrease into execution evidence.
            if (p.Confirmation == null && _broker is IPartialCloseGateway gateway)
            {
                var result = gateway.LookupPartial(p.ActionId);
                if (result != null) ApplyPartialOutcome(p, result);
            }
            if (!WitnessMatches(p, p.Confirmation) || receipt.Sequence <= p.ObservationSequence
                || !CurrentScopedReceipt(receipt, authority, utc, expires)) continue;
            var remaining = receipt.Positions!.SingleOrDefault(x => x.Id == plan.PositionId);
            var fill = next.OpenAdmissions.Single(a => a.Fill?.PositionId == plan.PositionId).Fill!;
            decimal closed = next.ClosingDeals.Where(d => d.PositionId == plan.PositionId).Sum(d => ScopedRecoveryPolicy.Units(d.Volume, p.VolumeStep));
            if (remaining?.VolumeUnits != p.ExpectedRemaining || closed - p.ClosedBefore != ScopedRecoveryPolicy.Units(p.RequestedUnits, p.VolumeStep)
                || closed + ScopedRecoveryPolicy.Units(remaining.VolumeUnits, p.VolumeStep) != ScopedRecoveryPolicy.Units(fill.InitialVolumeUnits, p.VolumeStep)) continue;
            p.Stage = PartialCloseStage.Confirmed; plan.PartialDone = true;
        }
    }
}
