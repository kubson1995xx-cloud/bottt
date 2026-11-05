using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
namespace Apex.Desktop.Okx;

internal enum OkxOrderObservationOrigin { Exact, Recent, Archive, Pending, Child }
internal sealed record OkxOrderDisagreement(OkxOrderObservationOrigin LeftOrigin,OkxOrderObservationOrigin RightOrigin,OkxOrderDifferences Difference);
internal sealed class OkxOrderDiagnostic
{
    public string Code { get; internal set; }="DIAGNOSE_COMPLETE";
    public List<OkxOrderDisagreement> Disagreements { get; }=new();
    public bool Truncated { get; internal set; }
    internal void Add(OkxExecutionOrderDto a,OkxExecutionOrderDto b,OkxOrderObservationOrigin left,OkxOrderObservationOrigin right)
    {
        if(Disagreements.Count==32){Truncated=true;return;}
        Disagreements.Add(new(left,right,OkxOrderConsistency.Differences(a,b)));
    }
}
internal sealed partial class OkxOperationalTradingService
{
    // The existing service has a dormant writer. This branch never uses it,
    // ReconcileAsync, ApplyEconomics, Prepare, arming or ID allocation.
    internal async Task<OkxOrderDiagnostic> DiagnoseOrderDifferencesAsync(CancellationToken ct)
    {
        var result=new OkxOrderDiagnostic();
        await lane.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if(!SourceCurrent||!OrdinaryDemoPolicy||FiniteDemo||armed||session.Coordinator.Snapshot().Mode!="STOP")
            {result.Code="DIAGNOSE_REFUSED_STOP_UNARMED_ORDINARY_REQUIRED";return result;}
            await CollectEconomicsAsync(ct,result).ConfigureAwait(false);
            return result;
        }
        catch(Exception e) when(e is System.IO.IOException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException or System.Net.Http.HttpRequestException or OperationCanceledException)
        {result.Code="DIAGNOSE_INCOMPLETE";return result;}
        finally{lane.Release();}
    }
}
