using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Naval;
namespace Apex.Desktop.Okx;
internal sealed partial class OkxOperationalTradingService
{
    private DateTimeOffset Deadline()=>DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().AddSeconds(10).ToUnixTimeMilliseconds());
    private async Task<NavalRiskResult> DispatchMutation(string id,string reservation,OkxPreparedRequest request,bool reduction,CancellationToken ct)
    {
        var state=session.ReadState();var x=state.NavalBroker!.Operational!;
        if(x.Intents.Any(i=>i.Id==id))return Refuse("ORIGINAL_MUTATION_ALREADY_PREPARED_NO_RETRY");
        var i=new NavalBrokerIntent(id,reservation,Enum.Parse<NavalExecutionOperation>(request.Operation.ToString()),request.Instrument,request.OriginalClientId,request.OrderId,request.CopyBody(),request.Digest,request.Deadline,
            SessionId,state.Naval!.Generation,state.Naval.Sequence,x.StopEpoch,state.DurableRevision+1,NavalExecutionStage.Prepared){Reduction=reduction};
        var stored=session.Prepare(i);if(stored.Status!=NavalMutationStatus.Applied)return stored;
        current=id;
        var outcome=request.Operation switch
        {
            OkxExecutionOperation.Submit=>await client.SubmitAsync(request,ct).ConfigureAwait(false),
            OkxExecutionOperation.Cancel=>await client.CancelAsync(request,ct).ConfigureAwait(false),
            OkxExecutionOperation.CancelProtection=>await client.CancelProtectionAsync(request,ct).ConfigureAwait(false),
            _=>throw new InvalidOperationException("CLOSED_MUTATION_REQUIRED")
        };
        var disposition=outcome.Disposition==OkxWriteDisposition.Acknowledged?NavalDispatchDisposition.Acknowledged:outcome.Disposition==OkxWriteDisposition.ProvenNotSent?NavalDispatchDisposition.NotSent:NavalDispatchDisposition.Unknown;
        return session.Outcome(id,new(disposition,outcome.OriginalClientId,outcome.OrderId,disposition==NavalDispatchDisposition.Acknowledged?"TRANSPORT_ACK":disposition==NavalDispatchDisposition.NotSent?"TRANSPORT_NOT_SENT":"TRANSPORT_UNKNOWN",outcome.Code,outcome.SCode,outcome.SubCode));
    }
    internal async Task<NavalRiskResult> CancelEntryAsync(string reservationId,CancellationToken ct)
    {
        await lane.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if(!CanExecute)return Refuse("EXECUTE_ARMING_REQUIRED");
            var state=session.ReadState();var parent=state.NavalBroker!.Operational?.Intents.SingleOrDefault(i=>i.ReservationId==reservationId&&!i.Reduction&&i.Operation==NavalExecutionOperation.Submit&&i.DispatchRevision!=null);
            if(parent==null)return Refuse("EXACT_OWNED_PARENT_REQUIRED");
            return await DispatchMutation("cancel-"+parent.Id,reservationId,OkxPreparedRequest.Cancel(account.ExecutionScope!,parent.Instrument,parent.Outcome?.OrderId,parent.ClientId,Deadline()),false,ct).ConfigureAwait(false);
        }
        finally{current=null;lane.Release();}
    }
    internal async Task<NavalRiskResult> ExitAsync(string reservationId,CancellationToken ct)
    {
        await lane.WaitAsync(ct).ConfigureAwait(false);
        try{return await ExitOwned(reservationId,ct).ConfigureAwait(false);}
        finally{current=null;lane.Release();}
    }
    private async Task<NavalRiskResult> ExitOwned(string reservationId,CancellationToken ct)
    {
        if(!CanExecute||protection==null)return Refuse("EXECUTE_PROTECTION_APPROVAL_REQUIRED");
        var state=session.ReadState();var r=state.Naval!.Reservations.SingleOrDefault(r=>r.Id==reservationId);
        if(r==null||r.Inventory<=0)return Refuse("EXACT_OWNED_INVENTORY_REQUIRED");
        var x=state.NavalBroker!.Operational!;
        if(x.Intents.Any(i=>i.ReservationId==r.Id&&i.Reduction))return Refuse("ORIGINAL_EXIT_ALREADY_PREPARED_NO_RETRY");
        var requested=session.RequestExit(reservationId);if(requested.Status==NavalMutationStatus.Refused)return requested;
        foreach(var p in x.Protection.Where(p=>p.Instrument==r.Intent.Instrument).GroupBy(p=>p.AlgoId).Select(g=>g.Last()).Where(p=>p.State=="live"))
        {
            var cancel=OkxPreparedRequest.CancelProtection(account.ExecutionScope!,p.Instrument,p.AlgoId,p.AttachmentClientId,Deadline());
            var result=await DispatchMutation("cancel-protection-"+p.AlgoId,r.Id,cancel,false,ct).ConfigureAwait(false);
            if(result.Status!=NavalMutationStatus.Applied)return result;
        }
        // Cancellation ACK is never used as cancellation, inventory or fee proof.
        await CollectEconomicsAsync(ct).ConfigureAwait(false);state=session.ReadState();x=state.NavalBroker!.Operational!;r=state.Naval!.Reservations.Single(r=>r.Id==reservationId);
        if(x.Obligations.Any(o=>!o.StartsWith("PROTECTION_GAP:",StringComparison.Ordinal))||x.Protection.Where(p=>p.Instrument==r.Intent.Instrument).GroupBy(p=>p.AlgoId).Any(g=>g.Last().State!="canceled"))return Refuse("EXIT_COMPETING_OR_UNKNOWN_OBLIGATIONS");
        var definition=NavalBrokerValidation.Definition(state.NavalBroker.Baseline);var spec=definition.Catalog.Single(i=>i.Instrument==r.Intent.Instrument);var quote=await feed.ReadQuoteAsync(spec,ct).ConfigureAwait(false);
        decimal quantity=decimal.Floor(r.Inventory/(definition.Fees.ExitInBase?1+definition.Fees.ExitRate:1)/spec.Quantum)*spec.Quantum;
        if(quantity<spec.MinimumQuantity)return Refuse("OWNED_DUST_RETAINED");
        string command="exit-"+r.Id;var ids=session.ReserveIds(r.Id,command,true);if(ids.Result.Status is not (NavalMutationStatus.Applied or NavalMutationStatus.Duplicate))return ids.Result;
        decimal price=decimal.Floor(quote.Bid/spec.TickSize)*spec.TickSize;
        var request=OkxPreparedRequest.Submit(account.ExecutionScope!,spec.Instrument,"sell","fok",N(quantity),N(price),ids.Ids[0].ClientId,Deadline());
        return await DispatchMutation(command,r.Id,request,true,ct).ConfigureAwait(false);
    }
}
