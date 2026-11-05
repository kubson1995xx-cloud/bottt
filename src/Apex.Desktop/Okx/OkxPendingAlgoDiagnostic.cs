using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
namespace Apex.Desktop.Okx;

internal sealed record OkxPendingAlgoFailure(OkxAlgoOrderFilter Filter,string ErrorCode,OkxObservationDiagnostic? Diagnostic);
internal sealed class OkxPendingAlgoDiagnostic
{
    public string Code { get; internal set; }="PENDING_DIAGNOSE_COMPLETE";
    public IReadOnlyList<OkxPendingAlgoFailure> Failures=>failures;
    private readonly List<OkxPendingAlgoFailure> failures=new(3);
    internal void Add(OkxAlgoOrderFilter filter,OkxObservationRead<OkxExecutionAlgoDto> read)
    {
        if(read.Status==OkxObservationStatus.Observed||failures.Count==3)return;
        string error=SafeError(read.ErrorCode);
        var d=read.Diagnostic;
        // Payload is the existing parser-owned closed schema/type/count projection;
        // never copy the read DTO (query, digests, rows, scope or private identity).
        failures.Add(new(filter,error,d==null?null:d with{Phase=SafePhase(d.Phase),OkxCode=NumericCode(d.OkxCode)?d.OkxCode:null}));
        Code="PENDING_DIAGNOSE_INCOMPLETE";
    }
    private static bool NumericCode(string? code)=>code=="0"||code is {Length:>=5 and <=8}&&code[0] is >= '1' and <= '9'&&code.All(c=>c is >= '0' and <= '9');
    private static string SafeError(string? error)=>NumericCode(error)?error!:error switch
    {
        "InvalidPayload" or "Cardinality" or "InvalidObservationRequest" or "ExpiredOrStopped" or "UnsafeHttpDiagnostics" or "ReadFenceDenied" or
        "BudgetBeforeQuota" or "QuotaWaitIncomplete" or "CredentialUnavailable" or "CredentialBindingMismatch" or "EndpointDenied" or
        "RedirectDenied" or "HttpFailure" or "BodyLimit" or "CanceledOrTimeout" or "TransportFailure"=>error,
        _=>"UNCLASSIFIED_READ_FAILURE"
    };
    private static string SafePhase(string phase)=>phase switch
    {
        "PARSE_UTF8" or "PARSE_JSON" or "PARSE_UNIQUE" or "PARSE_ENVELOPE" or "PARSE_SCHEMA" or "PARSE_VALUES" or "VENUE_REFUSAL"=>phase,
        _=>SafeError(phase)=="UNCLASSIFIED_READ_FAILURE"?"UNCLASSIFIED_READ_FAILURE":SafeError(phase)
    };
}
internal sealed partial class OkxOperationalTradingService
{
    // Shared exact pending loop: operational reconciliation retains its existing
    // strict classification; the diagnostic callback only projects failed reads.
    private static async Task CollectPendingAlgosAsync(OkxExecutionObservationClient reads,OkxObservationContext context,Action<OkxAlgoOrderFilter,OkxObservationRead<OkxExecutionAlgoDto>> observe,CancellationToken ct)
    {
        foreach(var filter in new[]{OkxAlgoOrderFilter.ConditionalAndOco,OkxAlgoOrderFilter.Trigger,OkxAlgoOrderFilter.TrailingStop})
        {
            var read=await reads.ReadOperationalPendingAlgosAsync(new(OkxAlgoObservationSurface.Pending,filter,null,null,null,null),context,ct).ConfigureAwait(false);
            observe(filter,read);
        }
    }
    // CONNECT constructs a dormant writer. This STOP/unarmed branch never invokes
    // it, admission, CollectEconomics, ApplyEconomics, balance/funding or Prepare.
    internal async Task<OkxPendingAlgoDiagnostic> DiagnosePendingAlgosAsync(CancellationToken ct)
    {
        var result=new OkxPendingAlgoDiagnostic();
        await lane.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if(!SourceCurrent||!OrdinaryDemoPolicy||FiniteDemo||armed||session.Coordinator.Snapshot().Mode!="STOP")
            {result.Code="PENDING_DIAGNOSE_REFUSED_STOP_UNARMED_ORDINARY_REQUIRED";return result;}
            await CollectPendingAlgosAsync(account.ObservationClient!,account.ObservationContext!,result.Add,ct).ConfigureAwait(false);
            return result;
        }
        catch(Exception e) when(e is System.IO.IOException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException or System.Net.Http.HttpRequestException or OperationCanceledException)
        {result.Code="PENDING_DIAGNOSE_INCOMPLETE";return result;}
        finally{lane.Release();}
    }
}
