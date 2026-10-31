using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Naval;
namespace Apex.Desktop.Okx;

// Explicit friend-only Offline join. Never composed by ordinary runtime startup.
internal sealed class OkxNavalExecutionDispatcher : IAsyncDisposable,IOkxSerializedDispatchFence
{
    private readonly NavalExecutionTransactions transactions;
    private readonly NavalOfflineExecutionSession session;
    private readonly OkxExecutionClient client;
    private readonly SemaphoreSlim lane=new(1,1);
    private string? current;
    internal OkxNavalExecutionDispatcher(NavalExecutionTransactions transactions,Func<IOkxSerializedDispatchFence,OkxExecutionClient> offlineClientFactory)
    {
        this.transactions=transactions;session=transactions.OpenOfflineSession();
        client=offlineClientFactory(this);
    }
    public bool CanDispatch(OkxPreparedRequest request,DateTimeOffset utcNow)=>false;
    public IDisposable? TryEnterDispatch(OkxPreparedRequest request,DateTimeOffset utcNow)
        =>current is null?null:transactions.TryEnterDispatch(current,request.Digest,session,utcNow);
    internal async Task<NavalRiskResult> DispatchAsync(string intentId,CancellationToken ct)
    {
        await lane.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var i=transactions.Read(intentId);
            if(i is null)return new(NavalMutationStatus.Refused,"EXECUTION_UNKNOWN_INTENT",null,0);
            if(i.Stage!=NavalExecutionStage.Prepared)return new(NavalMutationStatus.Duplicate,"EXECUTION_ALREADY_CLAIMED",null,i.DispatchRevision??i.PreparedRevision);
            var claim=transactions.ClaimDispatch(intentId);if(claim.Status!=NavalMutationStatus.Applied)return claim;
            var b=i.Binding;var scope=new OkxExecutionScope(b.TransportEnvironment,b.Uid,b.MainUid,b.QuoteCurrency,b.CredentialBindingId);
            var r=OkxPreparedRequest.Restore(scope,Enum.Parse<OkxExecutionOperation>(i.Operation.ToString()),i.Instrument,i.OriginalClientId,i.OrderId,i.Deadline,i.Body);
            current=intentId;
            OkxWriteOutcome o=i.Operation==NavalExecutionOperation.Submit?await client.SubmitAsync(r,ct).ConfigureAwait(false):await client.CancelAsync(r,ct).ConfigureAwait(false);
            var attempt=transactions.Read(intentId)!.DispatchRevision;
            return transactions.RecordOutcome(intentId,new(attempt,MapOutcome(i,o)));
        }
        finally{current=null;lane.Release();}
    }
    internal static NavalExecutionOutcome MapOutcome(NavalExecutionIntent i,OkxWriteOutcome o)
    {
        bool identity=o.OriginalClientId==i.OriginalClientId&&(i.OrderId==null||o.OrderId==i.OrderId);
        var disposition=!identity?NavalDispatchDisposition.Unknown:o.Disposition switch{OkxWriteDisposition.ProvenNotSent=>NavalDispatchDisposition.NotSent,OkxWriteDisposition.Acknowledged when o.OrderId!=null&&o.Code=="0"&&o.SCode=="0"&&string.IsNullOrEmpty(o.SubCode)=>NavalDispatchDisposition.Acknowledged,_=>NavalDispatchDisposition.Unknown};
        string? Code(string? value)=>value=="0"||value!=null&&System.Text.RegularExpressions.Regex.IsMatch(value,"\\A[1-9][0-9]{4,7}\\z",System.Text.RegularExpressions.RegexOptions.CultureInvariant)?value:null;
        string reason=disposition switch{NavalDispatchDisposition.Acknowledged=>"TRANSPORT_ACK",NavalDispatchDisposition.NotSent=>"TRANSPORT_NOT_SENT",_=>"TRANSPORT_UNKNOWN"};
        return new(disposition,i.OriginalClientId,identity?o.OrderId:i.OrderId,reason,Code(o.Code),Code(o.SCode),Code(o.SubCode));
    }
    public async ValueTask DisposeAsync(){session.Revoke();await client.DisposeAsync().ConfigureAwait(false);}
}
