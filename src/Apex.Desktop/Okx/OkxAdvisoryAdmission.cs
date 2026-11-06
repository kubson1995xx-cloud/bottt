using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Naval;
using Apex.Desktop.Services;
namespace Apex.Desktop.Okx;
internal sealed partial class OkxOperationalTradingService
{
    private WindowsOkxAdvisoryStore? advisoryStore;
    private sealed record EntryAdvice(Guid Session,string Decision,string Dataset,DateTimeOffset Cutoff,OkxAdvisorySnapshot Snapshot,string? Intent=null,string? RequestDigest=null);
    private EntryAdvice? entryAdvice;
    private string advisoryEntryCode="NOT_EVALUATED";
    internal string AdvisoryEntryCode=>advisoryEntryCode;
    private bool AdmitAdvice(NavalEvaluationRequest request,string decision,DateTimeOffset cutoff)
    {
        entryAdvice=null;
        if(!account.WindowsAdvisoryRequired)return true; // Preserves finite/VPS editions. Not a config switch.
        try
        {
            byte[] canonical=OkxAdvisoryCanonicalData.Serialize(request.Instrument,cutoff);
            string digest=OkxAdvisorySnapshot.Hash(canonical);
            advisoryStore??=new(account.AdvisoryReportRoot);
            using var lease=advisoryStore.Acquire(request.Instrument.Instrument,digest,cutoff,clock.GetUtcNow());
            if(lease==null){advisoryEntryCode="AI_ENTRY_VETO_MISSING_STALE_INVALID_OR_DATA_CHANGED";return false;}
            if(!lease.Snapshot.AllowsEntry){advisoryEntryCode="AI_ENTRY_VETO_DUAL_BUY_REQUIRED";return false;}
            entryAdvice=new(SessionId,decision,digest,cutoff,lease.Snapshot);advisoryEntryCode="DUAL_BUY_CURRENT_ENTRY_CANDIDATE";return true;
        }
        catch(Exception e) when(e is IOException or WindowsDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {advisoryEntryCode="AI_ENTRY_VETO_STORE_OR_DATA_UNAVAILABLE";return false;}
    }
    private void BindAdvice(string decision,string intent,OkxPreparedRequest request)
    {
        if(entryAdvice is {} a&&a.Session==SessionId&&a.Decision==decision&&a.Snapshot.Instrument==request.Instrument)entryAdvice=a with{Intent=intent,RequestDigest=request.Digest};
    }
    private IDisposable? EnterAdvisoryDispatch(OkxPreparedRequest request,DateTimeOffset now)
    {
        if(current==null||!PreparedAdmission(request))return null;
        var intent=session.ReadState().NavalBroker!.Operational!.Intents.SingleOrDefault(i=>i.Id==current);
        bool ordinaryBuy=account.WindowsAdvisoryRequired&&intent is {Operation:NavalExecutionOperation.Submit,Reduction:false};
        if(!ordinaryBuy)return session.EnterDispatch(current,request.Digest,now);
        var a=entryAdvice;entryAdvice=null; // Single dispatch attempt, never persisted or resurrected.
        if(a==null||a.Session!=SessionId||a.Intent!=current||a.RequestDigest!=request.Digest||a.Snapshot.Instrument!=request.Instrument||advisoryStore==null)return null;
        var reports=advisoryStore.Acquire(request.Instrument,a.Dataset,a.Cutoff,clock.GetUtcNow());
        if(reports==null)return null;
        if(reports.Snapshot!=a.Snapshot||!reports.Snapshot.AllowsEntry){reports.Dispose();return null;}
        var broker=session.EnterDispatch(current,request.Digest,clock.GetUtcNow());
        if(broker==null){reports.Dispose();return null;}
        return new AdvisoryDispatchLease(broker,reports);
    }
    private sealed class AdvisoryDispatchLease(IDisposable broker,IDisposable reports):IDisposable
    {public void Dispose(){try{broker.Dispose();}finally{reports.Dispose();}}}
}
