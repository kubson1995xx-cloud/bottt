using System;
using System.Collections.Generic;
using System.Linq;
using Apex.Core.Engine;
namespace Apex.Core.Naval;

public sealed record NavalBrokerSignal(NavalDecision Decision,DateTimeOffset ExpiresUtc);
public sealed record NavalBrokerOperationalState
{
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public NavalOrdinaryCatalog? OrdinaryCatalog { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public NavalOrdinaryDemoAdmission? OrdinaryDemo { get; init; }
    public string Schema { get; init; }="naval-broker-operational-v1";
    public IReadOnlyList<NavalBrokerSignal> Signals { get; init; }=Array.Empty<NavalBrokerSignal>();
    public IReadOnlyList<NavalBrokerIntent> Intents { get; init; }=Array.Empty<NavalBrokerIntent>();
    public IReadOnlyList<NavalExecutionIdAllocation> IdAllocations { get; init; }=Array.Empty<NavalExecutionIdAllocation>();
    public IReadOnlyList<string> ExitRequested { get; init; }=Array.Empty<string>();
    public IReadOnlyList<string> SettledIntents { get; init; }=Array.Empty<string>();
    public ulong StopEpoch { get; init; }
    public IReadOnlyList<string> Obligations { get; init; }=Array.Empty<string>();
    public IReadOnlyList<NavalBrokerSurface> Evidence { get; init; }=Array.Empty<NavalBrokerSurface>();
    public IReadOnlyList<NavalBrokerRawFill> FillEvidence { get; init; }=Array.Empty<NavalBrokerRawFill>();
    public IReadOnlyList<NavalBrokerRawBill> Bills { get; init; }=Array.Empty<NavalBrokerRawBill>();
    public IReadOnlyList<NavalBrokerOrderProof> Orders { get; init; }=Array.Empty<NavalBrokerOrderProof>();
    public IReadOnlyList<NavalBrokerProtection> Protection { get; init; }=Array.Empty<NavalBrokerProtection>();
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public NavalFiniteDemoPlan? DemoAcceptance { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<NavalBrokerBalance>? LastBalances { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? LastBalanceCaptureUtc { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public CurrentMarketFundingEvidenceV1? LastCurrentMarketFunding { get; init; }
}

public sealed partial class NavalPortfolioCoordinator
{
    internal NavalRiskResult RegisterBrokerSignal(NavalEvaluationRequest request)
    {
        lock(_owner)
        {
            var e=_owner.Read();var s=e.Naval!;
            if(!_broker||_frozen)return Result(s,NavalMutationStatus.Refused,"BROKER_REQUIRED");
            var instrument=_definition.Catalog.SingleOrDefault(i=>i.Instrument==request.Instrument.Instrument);
            if(instrument==null||request.Instrument.BaseAsset!=instrument.BaseCurrency||request.Instrument.QuoteAsset!=instrument.QuoteCurrency||
                request.Settings!=null&&request.Settings!=NavalFleetProfiles.Get(request.Fleet).Settings||request.CutoffUtc>_clock.UtcNow||
                request.CutoffUtc!=new DateTimeOffset(_clock.UtcNow.UtcDateTime.Date,TimeSpan.Zero)||
                request.HasLong!=s.Reservations.Any(r=>r.Fleet==request.Fleet&&r.Intent.Instrument==instrument.Instrument&&r.Inventory>0&&r.QuarantineMark==null))
                return Result(s,NavalMutationStatus.Refused,"SIGNAL_SOURCE_INVALID");
            var decision=new NavalD1Evaluator().Evaluate(request);
            if(decision.Action is not (NavalAction.EntrySignal or NavalAction.ExitSignal))return Result(s,NavalMutationStatus.Refused,decision.Reason);
            var x=e.NavalBroker!.Operational??new();
            if(x.Signals.Any(p=>p.Decision.StableId==decision.StableId))return Result(s,NavalMutationStatus.Duplicate,"SIGNAL_DUPLICATE");
            if(x.Signals.Count>=4096)return StopCandidate(e,"SIGNAL_CAPACITY");
            e.NavalBroker=e.NavalBroker with{Operational=x with{Signals=x.Signals.Append(new NavalBrokerSignal(decision,request.CutoffUtc.AddDays(1))).ToArray()}};
            return Commit(e,s,NavalMutationStatus.Applied,"SIGNAL_REGISTERED",Maintenance());
        }
    }
    internal static void ValidateBrokerOperational(EngineState e)
    {
        var x=e.NavalBroker!.Operational;
        if(x==null)return;
        NavalOrdinaryDemoPolicy.Validate(e);
        NavalOrdinaryCatalogPolicy.Validate(e.NavalBroker);
        if(x.DemoAcceptance is {} finite&&!FinitePlanShape(e.NavalBroker.Baseline,finite))throw NavalBrokerValidation.Invalid();
        if(x.Schema!="naval-broker-operational-v1"||x.Signals.Count>4096||x.Signals.Select(p=>p.Decision.StableId).Distinct().Count()!=x.Signals.Count||
            x.Signals.Any(p=>p.Decision.Action is not (NavalAction.EntrySignal or NavalAction.ExitSignal)||p.ExpiresUtc!=p.Decision.CutoffUtc.AddDays(1)))throw NavalBrokerValidation.Invalid();
    }
}
