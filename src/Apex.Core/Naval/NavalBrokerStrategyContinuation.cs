using System;
using System.Collections.Generic;
using System.Linq;
using Apex.Core.Engine;
namespace Apex.Core.Naval;

public sealed partial class NavalPortfolioCoordinator
{
    // Process-only capability owned by this coordinator; never part of durable state.
    private sealed record StrategyContinuation(NavalBrokerSession Session,INavalBrokerTradingAuthority Authority,Guid SessionId,
        string Baseline,string Reservation,string Parent,string Signal,DateTimeOffset ExpiresUtc,long Generation,long ArmingEpoch,ulong StopEpoch,string[] Cancels);
    private StrategyContinuation? _brokerContinuation;
    internal void RevokeBrokerStrategyContinuation(){lock(_owner)_brokerContinuation=null;}
    private bool OrdinaryStrategySource(EngineState e)=>e.NavalBroker!.Baseline.Binding.Account.Environment==NavalEnvironment.OkxDemo&&
        (e.NavalBroker.Operational?.OrdinaryDemo!=null||e.NavalBroker.Baseline.Observations.FiniteAllocation==null&&e.NavalBroker.Operational?.DemoAcceptance==null)&&
        _brokerAuthority is INavalFiniteDemoAuthority{FiniteDemo:false}&&_brokerAuthority.ContinuationEpoch>=0&&_brokerAuthority.ProtectionProfile!=null;
    private bool StrategyBinding(NavalBrokerSession session,EngineState e,StrategyContinuation c)=>
        ReferenceEquals(c.Session,session)&&ReferenceEquals(c.Authority,_brokerAuthority)&&BrokerSource(session)&&OrdinaryStrategySource(e)&&
        _brokerAuthority!.CanExecute&&_brokerAuthority.SessionId==c.SessionId&&_brokerAuthority.ContinuationEpoch==c.ArmingEpoch&&
        e.NavalBroker!.Baseline.Digest==c.Baseline&&e.Naval!.Generation==c.Generation&&e.NavalBroker.Operational!.StopEpoch==c.StopEpoch&&
        _clock.UtcNow<c.ExpiresUtc&&!e.Naval.EmergencyLatch&&!e.Naval.DailyLatch&&!e.Naval.DrawdownLatch&&e.Naval.Conflicts.Count==0&&
        e.NavalBroker.Operational.Signals.Any(s=>s.Decision.StableId==c.Signal&&s.Decision.Action==NavalAction.ExitSignal)&&
        e.NavalBroker.Operational.Intents.Any(i=>i.Id==c.Parent&&i.ReservationId==c.Reservation&&!i.Reduction&&i.Operation==NavalExecutionOperation.Submit&&i.Session==c.SessionId&&e.NavalBroker.Operational.SettledIntents.Contains(i.Id))&&
        e.NavalBroker.Operational.Intents.All(i=>NavalOrdinaryDemoPolicy.Historical(e.NavalBroker.Operational,i)||i.Stage==NavalExecutionStage.Accepted&&
            (e.NavalBroker.Operational.SettledIntents.Contains(i.Id)||i.ReservationId==c.Reservation&&(c.Cancels.Contains(i.Id)||i.Reduction&&i.Id=="exit-"+c.Reservation)));
    internal NavalRiskResult RequestBrokerStrategyExit(NavalBrokerSession session,string reservation,string signal)
    {
        lock(_owner)
        {
            _brokerContinuation=null;
            var e=_owner.Read();var s=e.Naval!;var x=e.NavalBroker!.Operational!;
            if(!OrdinaryStrategySource(e))return RequestBrokerExit(session,reservation);
            var r=s.Reservations.SingleOrDefault(r=>r.Id==reservation);
            var registered=x.Signals.SingleOrDefault(p=>p.Decision.StableId==signal);
            var parent=x.Intents.SingleOrDefault(i=>i.ReservationId==reservation&&!i.Reduction&&i.Operation==NavalExecutionOperation.Submit);
            if(r==null||registered==null||registered.Decision.Action!=NavalAction.ExitSignal||registered.Decision.Fleet!=r.Fleet||registered.Decision.Instrument!=r.Intent.Instrument||
                registered.Decision.CutoffUtc!=new DateTimeOffset(_clock.UtcNow.UtcDateTime.Date,TimeSpan.Zero)||registered.ExpiresUtc<=_clock.UtcNow)
                return Result(s,NavalMutationStatus.Refused,"REGISTERED_CURRENT_STRATEGY_EXIT_REQUIRED");
            if(!OrdinaryStrategySource(e)||!BrokerSource(session)||_brokerAuthority!.CanExecute!=true||!_started||s.Mode!="RUNNING"||!Fresh(s)||!BrokerFresh()||
                s.EmergencyLatch||s.DailyLatch||s.DrawdownLatch||s.Conflicts.Count!=0||x.Obligations.Any(o=>NavalOrdinaryDemoPolicy.Blocking(e,o))||r.Inventory<=0||r.Pending!=0||!r.TerminalCoverage||r.ReductionUnsettled||
                parent==null||parent.Session!=_brokerAuthority.SessionId||parent.Stage!=NavalExecutionStage.Accepted||!x.SettledIntents.Contains(parent.Id)||x.ExitRequested.Contains(reservation)||
                x.Intents.Any(i=>!NavalOrdinaryDemoPolicy.Historical(x,i)&&(i.Stage!=NavalExecutionStage.Accepted||!x.SettledIntents.Contains(i.Id)))||
                s.Reservations.Any(p=>p.Id!=reservation&&!NavalOrdinaryDemoPolicy.Pinned(e,p)&&(p.Inventory!=0||p.Pending!=0||p.ReductionUnsettled)))
                return RequestBrokerExit(session,reservation); // No continuation must not disable the existing safe reduction.
            var protection=NavalBrokerProtectionOwnership.Competing(e,r);
            if(protection.Length==0||protection.Any(p=>p.State!="live"||p.ChildOrderIds.Count!=0||NavalBrokerProtectionOwnership.Reservation(x,p)!=reservation))
                return RequestBrokerExit(session,reservation);
            var capability=new StrategyContinuation(session,_brokerAuthority,_brokerAuthority.SessionId,e.NavalBroker.Baseline.Digest,reservation,parent.Id,signal,registered.ExpiresUtc,s.Generation,
                _brokerAuthority.ContinuationEpoch,x.StopEpoch,protection.Select(p=>"cancel-protection-"+p.AlgoId).ToArray());
            var result=RequestBrokerExit(session,reservation);
            if(result.Status==NavalMutationStatus.Stopped&&result.Code=="EXACT_OWNED_EXIT_REQUESTED")
                _brokerContinuation=capability with{StopEpoch=_owner.Read().NavalBroker!.Operational!.StopEpoch};
            return result;
        }
    }
    private bool StrategyProtectionPause(NavalBrokerSession session,EngineState e,ISet<string> obligations)
    {
        var c=_brokerContinuation;
        if(c==null||!StrategyBinding(session,e,c)||obligations.Count(o=>NavalOrdinaryDemoPolicy.Blocking(e,o))!=1||!obligations.Contains("PROTECTION_GAP:"+c.Reservation))return false;
        var x=e.NavalBroker!.Operational!;var r=e.Naval!.Reservations.Single(p=>p.Id==c.Reservation);
        var competing=NavalBrokerProtectionOwnership.Competing(e,r);
        return r.Inventory>0&&r.Pending==0&&r.TerminalCoverage&&!e.Naval.Fills.Any(f=>f.Fill.ReservationId==r.Id&&f.Fill.Reduction)&&
            !x.Intents.Any(i=>i.ReservationId==r.Id&&i.Reduction&&x.SettledIntents.Contains(i.Id))&&competing.Length>0&&
            competing.All(p=>p.State=="canceled"&&p.ChildOrderIds.Count==0&&NavalBrokerProtectionOwnership.Reservation(x,p)==c.Reservation)&&
            c.Cancels.All(id=>x.Intents.Any(i=>i.Id==id&&i.Operation==NavalExecutionOperation.CancelProtection&&i.ReservationId==c.Reservation)&&x.SettledIntents.Contains(id));
    }
    internal NavalRiskResult ContinueBrokerStrategy(NavalBrokerSession session)
    {
        lock(_owner)
        {
            var e=_owner.Read();var s=e.Naval!;var c=_brokerContinuation;
            if(c==null)return Result(s,NavalMutationStatus.Refused,"NO_STRATEGY_CONTINUATION");
            if(!StrategyBinding(session,e,c)){_brokerContinuation=null;return Result(s,NavalMutationStatus.Refused,"STRATEGY_CONTINUATION_REVOKED");}
            var x=e.NavalBroker!.Operational!;
            // Pending settlement may be observed more than once; it never grants entry authority.
            if(s.Reservations.Any(r=>!NavalOrdinaryDemoPolicy.Pinned(e,r)&&(r.Inventory!=0||r.Pending!=0||r.ReductionUnsettled))||x.Obligations.Any(o=>NavalOrdinaryDemoPolicy.Blocking(e,o)))return Result(s,NavalMutationStatus.Refused,"STRATEGY_EXIT_NOT_FLAT");
            _brokerContinuation=null; // Single-use even if the final current proof or commit fails.
            if(_frozen||s.Mode!="STOP"||!Fresh(s)||!BrokerFresh()||s.Reservations.Any(r=>!NavalOrdinaryDemoPolicy.Pinned(e,r)&&(r.Stage!=NavalReservationStage.Terminal||!r.TerminalCoverage))||
                x.Intents.Any(i=>!NavalOrdinaryDemoPolicy.Historical(x,i)&&!x.SettledIntents.Contains(i.Id))||!x.Intents.Any(i=>i.Id=="exit-"+c.Reservation&&i.Reduction&&i.Session==c.SessionId)||
                c.Cancels.Any(id=>!x.SettledIntents.Contains(id))||
                NavalBrokerProtectionOwnership.Competing(e,s.Reservations.Single(r=>r.Id==c.Reservation)).Any(p=>!NavalBrokerProtectionOwnership.Settled(p)))
                return Result(s,NavalMutationStatus.Refused,"STRATEGY_CONTINUATION_FINAL_PROOF_REQUIRED");
            var result=Commit(e,s with{Mode="RUNNING",Reason="BROKER_STRATEGY_CONTINUED"},NavalMutationStatus.Applied,"BROKER_STRATEGY_CONTINUED",Maintenance());
            if(result.Status==NavalMutationStatus.Applied)_started=true;
            return result;
        }
    }
}
