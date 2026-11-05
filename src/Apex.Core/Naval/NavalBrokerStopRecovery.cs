using System;
using System.Linq;
namespace Apex.Core.Naval;

// Cleanup authority is not trading authority. It cannot satisfy CanExecute,
// finite START, ID allocation or Submit admission. Only an existing exact parent
// with a fresh signed live observation may acquire this cancellation fence.
internal interface INavalBrokerStopRecoveryAuthority : INavalBrokerTradingAuthority
{
    bool CanCancel(NavalBrokerIntent parent,DateTimeOffset now);
}
internal sealed partial class NavalBrokerSession
{
    internal NavalRiskResult PrepareStopCancel(NavalBrokerIntent intent)=>Coordinator.PrepareStopCancel(this,intent);
    internal IDisposable? EnterStopCancel(string id,string digest,DateTimeOffset now)=>Coordinator.EnterStopCancel(this,id,digest,now);
}
public sealed partial class NavalPortfolioCoordinator
{
    private bool StopCancelAllowed(NavalBrokerSession session,Apex.Core.Engine.EngineState e,NavalBrokerIntent i)
    {
        var x=e.NavalBroker!.Operational!;var s=e.Naval!;
        var parents=x.Intents.Where(p=>p.Operation==NavalExecutionOperation.Submit&&!p.Reduction&&p.DispatchRevision!=null&&p.ReservationId==i.ReservationId&&p.ClientId==i.ClientId&&p.Instrument==i.Instrument).ToArray();
        return e.NavalBroker.Baseline.Binding.Account.Environment==NavalEnvironment.OkxDemo&&s.Mode=="STOP"&&
            BrokerSource(session)&&_brokerAuthority is INavalBrokerStopRecoveryAuthority a&&!a.CanExecute&&a.ProtectionProfile==null&&
            i.Operation==NavalExecutionOperation.Cancel&&!i.Reduction&&parents.Length==1&&i.Id=="cancel-"+parents[0].Id&&i.OrderId==parents[0].Outcome?.OrderId&&
            i.Session==a.SessionId&&i.Generation==s.Generation&&i.Sequence==s.Sequence&&i.StopEpoch==x.StopEpoch&&a.CanCancel(parents[0],_clock.UtcNow)&&
            !x.Intents.Any(p=>p.Id!=i.Id&&p.Operation==NavalExecutionOperation.Cancel&&p.ReservationId==i.ReservationId)&&BrokerWorkflow(e,i,s.Reservations.Single(r=>r.Id==i.ReservationId));
    }
    internal NavalRiskResult PrepareStopCancel(NavalBrokerSession session,NavalBrokerIntent i)
    {
        lock(_owner)
        {
            var e=_owner.Read();var s=e.Naval!;var b=e.NavalBroker!;var x=b.Operational!;
            if(x.Intents.Any(p=>p.Id==i.Id))return Result(s,NavalMutationStatus.Refused,"RECOVERY_CANCEL_ALREADY_PREPARED_NO_RETRY");
            if(_frozen||!StopCancelAllowed(session,e,i)||i.Stage!=NavalExecutionStage.Prepared||i.Outcome!=null||i.DispatchRevision!=null||i.PreparedRevision!=s.Revision+1||i.Deadline<=_clock.UtcNow)
                return Result(s,NavalMutationStatus.Refused,"RECOVERY_CANCEL_DENIED");
            e.NavalBroker=b with{Operational=x with{Intents=x.Intents.Append(NavalJson.Clone(i)).ToArray()}};
            return Commit(e,s,NavalMutationStatus.Applied,"RECOVERY_CANCEL_PREPARED",Maintenance(),i.ReservationId);
        }
    }
    internal IDisposable? EnterStopCancel(NavalBrokerSession session,string id,string digest,DateTimeOffset now)
    {
        System.Threading.Monitor.Enter(_owner);bool held=false;
        try
        {
            var e=_owner.Read();var s=e.Naval!;var b=e.NavalBroker!;var x=b.Operational!;var i=x.Intents.SingleOrDefault(i=>i.Id==id);
            if(i==null||_frozen||!StopCancelAllowed(session,e,i)||i.Stage!=NavalExecutionStage.Prepared||i.RequestDigest!=digest||i.PreparedRevision!=s.Revision||_clock.UtcNow>=i.Deadline||now>=i.Deadline)return null;
            e.NavalBroker=b with{Operational=x with{Intents=x.Intents.Select(p=>p.Id==id?p with{Stage=NavalExecutionStage.Dispatching,DispatchRevision=s.Revision+1}:p).ToArray()}};
            if(Commit(e,s,NavalMutationStatus.Applied,"RECOVERY_CANCEL_DISPATCHING",Maintenance()).Status!=NavalMutationStatus.Applied||!StopCancelAllowed(session,_owner.Read(),i)||_clock.UtcNow>=i.Deadline)return null;
            held=true;return new DispatchGuard(_owner);
        }
        finally{if(!held)System.Threading.Monitor.Exit(_owner);}
    }
}
