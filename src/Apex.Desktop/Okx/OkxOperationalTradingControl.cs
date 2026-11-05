using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Naval;
namespace Apex.Desktop.Okx;
internal sealed record OkxOperationalTradingStatus(string Mode,string Code,bool Connected,bool Armed,bool ProductionProfileApproved,bool Ready,
    long Revision,decimal Cash,decimal Held,IReadOnlyList<string> Instruments,IReadOnlyList<string> Obligations,IReadOnlyList<OkxOwnedInventory>? Inventory=null)
{
    public OkxStrategyEvaluationStatus Evaluation { get; init; }=new(0,0,0,0,"NOT_EVALUATED");
}
internal sealed record OkxStrategyEvaluationStatus(long Completed,long Wait,long Entry,long Exit,string Code);
internal sealed record OkxOwnedInventory(string ReservationId,string Instrument,decimal Quantity,string State,bool ReductionUnsettled);
internal sealed partial class OkxOperationalTradingService
{
    // Process-only aggregate, one immutable atomic snapshot; no identifiers or provider text.
    private OkxStrategyEvaluationStatus evaluation=new(0,0,0,0,"NOT_EVALUATED");
    private void EvaluationCode(string code)=>Volatile.Write(ref evaluation,Volatile.Read(ref evaluation) with{Code=code});
    private void Evaluated(NavalAction action)
    {
        static long Next(long n)=>n==long.MaxValue?n:n+1;
        var previous=Volatile.Read(ref evaluation);
        Volatile.Write(ref evaluation,new(Next(previous.Completed),action is NavalAction.Wait or NavalAction.Hold?Next(previous.Wait):previous.Wait,
            action==NavalAction.EntrySignal?Next(previous.Entry):previous.Entry,action==NavalAction.ExitSignal?Next(previous.Exit):previous.Exit,
            action==NavalAction.EntrySignal?"EVALUATED_ENTRY":action==NavalAction.ExitSignal?"EVALUATED_EXIT":"EVALUATED_WAIT"));
    }
    internal OkxOperationalTradingStatus ReadStatus()
    {
        if(!ReferenceEquals(account.BrokerSession,session))return new("STOP","FRESH_CONNECTION_REQUIRED",false,false,false,false,0,0,0,Array.Empty<string>(),new[]{"OWNER_NOT_OPEN"});
        var state=session.ReadState();var view=session.Coordinator.Snapshot();var b=state.NavalBroker!;
        return new(view.Mode,Status,SourceCurrent,armed,protection!=null&&protection!="SYNTHETIC-PROTOCOL-ONLY-NOT-PRODUCTION",view.Ready&&CanExecute&&!EntryObservationPending&&protection!=null&&protection!="SYNTHETIC-PROTOCOL-ONLY-NOT-PRODUCTION",
            state.DurableRevision,view.Cash,view.Held,NavalOrdinaryCatalogPolicy.Catalog(b).Select(i=>i.Instrument).ToArray(),b.Operational?.Obligations??Array.Empty<string>(),
            state.Naval!.Reservations.Where(r=>r.Inventory>0).Select(r=>new OkxOwnedInventory(r.Id,r.Intent.Instrument,r.Inventory,r.QuarantineMark!=null?"UNPROTECTED_QUARANTINED_FULL_LOSS_RESERVED":r.Stage.ToString(),r.ReductionUnsettled)).ToArray()){Evaluation=Volatile.Read(ref evaluation)};
    }
    internal void EmergencyStop(){RevokeArming();session.EmergencyStop();}
    // Explicit host-owned lifetime; no task/network/secret access is started by construction/startup.
    // Refresh never grants a new START; only the scoped own-exit capability may continue the existing one.
    internal async Task RunAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested&&SourceCurrent)
        {
            var reconciled=await ReconcileAsync(ct).ConfigureAwait(false);
            if(reconciled.Status!=NavalMutationStatus.Applied)EvaluationCode("SKIPPED_RECONCILIATION");
            else if(!CanExecute)EvaluationCode("SKIPPED_CAN_EXECUTE");
            else if(session.Coordinator.Snapshot().Mode!="RUNNING")EvaluationCode("SKIPPED_NOT_RUNNING");
            if(session.Coordinator.Snapshot().Mode=="RUNNING"&&CanExecute)
            foreach(var fleet in new[]{NavalFleet.Alpha,NavalFleet.Bravo})
            foreach(var i in NavalOrdinaryCatalogPolicy.Catalog(session.ReadState().NavalBroker!).Where(i=>NavalFleetProfiles.Get(fleet).Assets.Contains(i.BaseCurrency)))
            {
                if(session.Coordinator.Snapshot().Mode!="RUNNING"||!CanExecute)break;
                await RunOnceAsync(fleet,i.Instrument,ct).ConfigureAwait(false);
            }
            await Task.Delay(TimeSpan.FromSeconds(2),clock,ct).ConfigureAwait(false);
        }
    }
}
