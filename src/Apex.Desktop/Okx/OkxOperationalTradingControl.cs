using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Naval;
namespace Apex.Desktop.Okx;
internal sealed record OkxOperationalTradingStatus(string Mode,string Code,bool Connected,bool Armed,bool ProductionProfileApproved,bool Ready,
    long Revision,decimal Cash,decimal Held,IReadOnlyList<string> Instruments,IReadOnlyList<string> Obligations);
internal sealed partial class OkxOperationalTradingService
{
    internal OkxOperationalTradingStatus ReadStatus()
    {
        if(!ReferenceEquals(account.BrokerSession,session))return new("STOP","FRESH_CONNECTION_REQUIRED",false,false,false,false,0,0,0,Array.Empty<string>(),new[]{"OWNER_NOT_OPEN"});
        var state=session.ReadState();var view=session.Coordinator.Snapshot();var b=state.NavalBroker!;
        return new(view.Mode,Status,SourceCurrent,armed,protection!=null&&protection!="SYNTHETIC-PROTOCOL-ONLY-NOT-PRODUCTION",view.Ready&&CanExecute&&protection!=null&&protection!="SYNTHETIC-PROTOCOL-ONLY-NOT-PRODUCTION",
            state.DurableRevision,view.Cash,view.Held,b.Baseline.Observations.Catalog.Select(i=>i.Instrument).ToArray(),b.Operational?.Obligations??Array.Empty<string>());
    }
    internal void EmergencyStop(){RevokeArming();session.EmergencyStop();}
    // Explicit host-owned lifetime; no task/network/secret access is started by construction/startup.
    // Reconciliation continues while stopped, but neither this loop nor a successful refresh STARTs trading.
    internal async Task RunAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested&&SourceCurrent)
        {
            await ReconcileAsync(ct).ConfigureAwait(false);
            if(session.Coordinator.Snapshot().Mode=="RUNNING"&&CanExecute)
            foreach(var fleet in new[]{NavalFleet.Alpha,NavalFleet.Bravo})
            foreach(var i in session.ReadState().NavalBroker!.Baseline.Observations.Catalog.Where(i=>NavalFleetProfiles.Get(fleet).Assets.Contains(i.BaseCurrency)))
            {
                if(session.Coordinator.Snapshot().Mode!="RUNNING"||!CanExecute)break;
                await RunOnceAsync(fleet,i.Instrument,ct).ConfigureAwait(false);
            }
            await Task.Delay(TimeSpan.FromSeconds(2),clock,ct).ConfigureAwait(false);
        }
    }
}
