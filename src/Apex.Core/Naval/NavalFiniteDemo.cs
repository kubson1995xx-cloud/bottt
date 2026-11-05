using System;
using System.Linq;
namespace Apex.Core.Naval;

// This is a durable, single-use acceptance command plan, NOT a strategy signal or
// Desktop activation. Reopening the file cannot recover its process/session authority.
public sealed record NavalFiniteDemoPlan(string RunId,Guid Session,string Instrument,DateTimeOffset CreatedUtc,DateTimeOffset ExpiresUtc)
{
    public string PendingCommand=>"demo-pending-"+RunId;
    public string EntryCommand=>"demo-entry-"+RunId;
}
internal interface INavalFiniteDemoAuthority { bool FiniteDemo { get; } }
internal sealed partial class NavalBrokerSession
{
    internal NavalRiskResult PlanFiniteDemo(NavalFiniteDemoPlan plan)=>Coordinator.PlanFiniteDemo(this,plan);
}
public sealed partial class NavalPortfolioCoordinator
{
    internal NavalRiskResult PlanFiniteDemo(NavalBrokerSession session,NavalFiniteDemoPlan plan)
    {
        lock(_owner)
        {
            var e=_owner.Read();var s=e.Naval!;var x=e.NavalBroker!.Operational??new();
            if(!BrokerSource(session)||_brokerAuthority is not INavalFiniteDemoAuthority{FiniteDemo:true}||!FinitePlanShape(e.NavalBroker.Baseline,plan)||
                plan.Session!=_brokerAuthority.SessionId||plan.CreatedUtc>_clock.UtcNow||_clock.UtcNow>=plan.ExpiresUtc||
                x.DemoAcceptance!=null||s.Commands.Count!=0||s.Reservations.Count!=0||s.Fills.Count!=0||x.Intents.Count!=0||x.Obligations.Count!=0)
                return Result(s,NavalMutationStatus.Refused,"FINITE_DEMO_PLAN_DENIED");
            e.NavalBroker=e.NavalBroker with{Operational=x with{DemoAcceptance=plan}};
            return Commit(e,s,NavalMutationStatus.Applied,"FINITE_DEMO_PLANNED",Maintenance());
        }
    }
    internal static bool FinitePlanShape(NavalBrokerBaseline b,NavalFiniteDemoPlan p)=>b.Binding.Account.Environment==NavalEnvironment.OkxDemo&&
        b.Binding.Account.QuoteCurrency is "EUR" or "USDC"&&Guid.TryParseExact(p.RunId,"N",out _)&&p.Session!=Guid.Empty&&
        b.Observations.Catalog.Any(i=>i.Instrument==p.Instrument)&&p.CreatedUtc.Offset==TimeSpan.Zero&&p.ExpiresUtc>p.CreatedUtc&&p.ExpiresUtc-p.CreatedUtc<=TimeSpan.FromMinutes(3);
    private NavalSignal? FiniteDemoSignal(Apex.Core.Engine.EngineState e,NavalEntryIntent intent)
    {
        var p=e.NavalBroker?.Operational?.DemoAcceptance;
        if(p==null||_brokerAuthority is not INavalFiniteDemoAuthority{FiniteDemo:true}||p.Session!=_brokerAuthority.SessionId||_clock.UtcNow>=p.ExpiresUtc||
            intent.SignalId!=intent.CommandId||intent.Instrument!=p.Instrument||intent.CommandId!=p.PendingCommand&&intent.CommandId!=p.EntryCommand||
            NavalCashRiskPolicy.BuyDebit(intent.Quantity,intent.EntryPriceCeiling,_definition.Fees)>10)return null;
        if(intent.CommandId==p.EntryCommand)
        {
            var x=e.NavalBroker!.Operational!;
            var pending=e.Naval!.Reservations.SingleOrDefault(r=>r.Intent.CommandId==p.PendingCommand);
            var parent=x.Intents.SingleOrDefault(i=>i.ReservationId==pending?.Id&&i.Operation==NavalExecutionOperation.Submit&&!i.Reduction);
            if(pending==null||parent==null||pending.Inventory!=0||pending.Filled!=0||pending.Pending!=0||!pending.TerminalCoverage||
                !x.SettledIntents.Contains(parent.Id)||!x.Intents.Any(i=>i.ReservationId==pending.Id&&i.Operation==NavalExecutionOperation.Cancel&&i.DispatchRevision!=null)||
                e.Naval.Fills.Count!=0)return null;
        }
        else if(e.Naval!.Reservations.Count!=0)return null;
        return new(intent.SignalId,e.NavalBroker!.Baseline.Binding.Account,NavalFleet.Alpha,p.Instrument,"FINITE-DEMO-ACCEPTANCE-NOT-STRATEGY",p.CreatedUtc,p.ExpiresUtc);
    }
    private bool FiniteDemoRequest(Apex.Core.Engine.EngineState e,NavalBrokerIntent i,NavalReservation r)
    {
        var p=e.NavalBroker!.Operational?.DemoAcceptance;
        return p!=null&&_brokerAuthority is INavalFiniteDemoAuthority{FiniteDemo:true}&&p.Session==_brokerAuthority.SessionId&&i.Session==p.Session&&
            _clock.UtcNow<p.ExpiresUtc&&r.Intent.Instrument==p.Instrument&&r.Intent.SignalId==r.Intent.CommandId&&
            r.Intent.CommandId==p.PendingCommand&&NavalCashRiskPolicy.BuyDebit(r.Quantity,r.Intent.EntryPriceCeiling,_definition.Fees)<=10;
    }
}
