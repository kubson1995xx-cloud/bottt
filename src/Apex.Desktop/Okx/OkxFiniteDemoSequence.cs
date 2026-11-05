using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Naval;
namespace Apex.Desktop.Okx;

// Finite command orchestrator. It never calls RunOnce/strategy evaluation, retries a
// mutation or replaces an Unknown order. REST collections may be repeated twice.
internal static class OkxFiniteDemoSequence
{
    internal static async Task<string> RunAsync(OkxOperationalTradingService loop,OkxAccountConnectionService account,string runId,Action<string> checkpoint,CancellationToken ct)
    {
        var session=account.BrokerSession??throw new InvalidOperationException("FINITE_OWNER_REQUIRED");
        bool Applied(NavalRiskResult r)=>r.Status==NavalMutationStatus.Applied;
        async Task Observe()
        {for(int n=0;n<2;n++){var observed=await loop.ReconcileAsync(ct).ConfigureAwait(false);checkpoint("OBSERVED:"+observed.Code);if(account.BrokerSession!.ReadState().NavalBroker!.Operational!.Obligations.Count==0)return;if(n==0)await Task.Delay(250,ct).ConfigureAwait(false);}}
        try
        {
            if(!loop.ArmFiniteDemo())return "FINITE_ARMING_REFUSED";
            if(!Applied(await loop.ReconcileAsync(ct).ConfigureAwait(false)))return "PREFLIGHT_RECONCILIATION_REFUSED";
            var selected=await loop.SelectFiniteInstrumentAsync(ct).ConfigureAwait(false);
            var planned=loop.PlanFiniteDemo(selected.Instrument,runId);if(!Applied(planned))return "FINITE_PLAN_REFUSED:"+planned.Code;
            var started=loop.Start();if(!Applied(started))return "FINITE_START_REFUSED:"+started.Code;
            checkpoint("PLANNED");
            var pending=await loop.SubmitFiniteDemoAsync(true,ct).ConfigureAwait(false);checkpoint("PENDING_SUBMIT_RESULT");
            if(!Applied(pending)){await Observe().ConfigureAwait(false);return "PENDING_SUBMIT_NOT_ACKNOWLEDGED_NO_RETRY:"+pending.Code;}
            await loop.ReconcileAsync(ct).ConfigureAwait(false);checkpoint("PENDING_OBSERVED");
            var canceled=await loop.CancelEntryAsync(pending.ReservationId!,ct).ConfigureAwait(false);checkpoint("PENDING_CANCEL_RESULT");
            if(!Applied(canceled)){await Observe().ConfigureAwait(false);return "PENDING_CANCEL_NOT_ACKNOWLEDGED_NO_NEW_RISK";}
            await Observe().ConfigureAwait(false);
            var state=session.ReadState();var p=state.Naval!.Reservations.Single(r=>r.Id==pending.ReservationId);
            if(p.Inventory!=0||p.Filled!=0||p.Pending!=0||!p.TerminalCoverage||state.NavalBroker!.Operational!.Obligations.Count!=0)return "PENDING_NOT_PROVEN_ZERO_FILL_CANCELED_NO_SECOND_BUY";
            if(!Applied(loop.Start()))return "POST_CANCEL_START_REFUSED";
            var bought=await loop.SubmitFiniteDemoAsync(false,ct).ConfigureAwait(false);checkpoint("FILLED_BUY_SUBMIT_RESULT");
            if(!Applied(bought)){await Observe().ConfigureAwait(false);return "BUY_NOT_ACKNOWLEDGED_NO_RETRY";}
            await Observe().ConfigureAwait(false);state=session.ReadState();var r=state.Naval!.Reservations.Single(r=>r.Id==bought.ReservationId);
            if(r.Inventory<=0||!r.TerminalCoverage)return "BUY_FILL_OR_FEE_FINALITY_UNPROVEN";
            if(state.NavalBroker!.Operational!.Obligations.Any(o=>!o.StartsWith("PROTECTION_GAP:",StringComparison.Ordinal)))return "BUY_ECONOMICS_OR_OWNERSHIP_UNPROVEN";
            // Existing cancellation-to-sale workflow: exact own protection is observed
            // canceled before sell; a failed/Unknown sale can leave unprotected inventory.
            var sold=await loop.ExitAsync(r.Id,ct).ConfigureAwait(false);checkpoint("OWNED_EXIT_RESULT");
            if(!Applied(sold)){await Observe().ConfigureAwait(false);return "OWNED_EXIT_INCOMPLETE_CHECK_RESIDUAL_PROTECTION";}
            await Observe().ConfigureAwait(false);state=session.ReadState();r=state.Naval!.Reservations.Single(r=>r.Id==bought.ReservationId);
            var spec=state.NavalBroker!.Baseline.Observations.Catalog.Single(i=>i.Instrument==r.Intent.Instrument);
            if(r.ReductionUnsettled||!state.Naval.Fills.Any(f=>f.Fill.ReservationId==r.Id&&f.Fill.Reduction)||r.Inventory>=spec.MinimumQuantity||
                state.NavalBroker.Operational!.Intents.Any(i=>i.Reduction&&!state.NavalBroker.Operational.SettledIntents.Contains(i.Id)))return "SELL_OR_BALANCE_FINALITY_UNPROVEN";
            return r.Inventory==0?"COMPLETED_STOP_FLAT":"COMPLETED_STOP_WITH_EXACT_DUST";
        }
        finally{loop.Stop();checkpoint("STOP_UNARMED_SCHEDULER_NEVER_STARTED");}
    }
}
