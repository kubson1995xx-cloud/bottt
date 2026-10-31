using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
namespace Apex.Adapters.Okx.Execution;

/// <summary>Infrastructure only. Construction makes no requests and does not grant Demo/Live activation.
/// No Desktop caller exists. Caller must supply protected identity/readiness and durable dispatch authority.</summary>
public sealed class OkxExecutionClient : IAsyncDisposable
{
    private sealed class Lane { internal readonly SemaphoreSlim Gate=new(1,1); internal DateTimeOffset Next; }
    // UID/operation/instrument quotas outlive every client and credential binding. Separate from reader quotas.
    private static readonly ConcurrentDictionary<string,Lane> Lanes=new(StringComparer.Ordinal);
    private readonly OkxExecutionScope scope;
    private readonly IOkxSigningMaterialProvider provider;
    private readonly IOkxFinalDispatchFence fence;
    private readonly string fingerprint;
    private readonly HttpMessageInvoker http;
    private readonly TimeProvider clock;
    private readonly Func<TimeSpan,CancellationToken,Task> delay;
    private readonly SemaphoreSlim sendLock=new(1,1);
    private readonly object lifetime=new();
    private readonly CancellationTokenSource closing=new();
    private Task? disposal;
    private bool disposed;
    private OkxExecutionClient(HttpMessageHandler handler,TimeProvider clock,OkxExecutionScope scope,IOkxSigningMaterialProvider provider,string fingerprint,IOkxFinalDispatchFence fence,Func<TimeSpan,CancellationToken,Task>? delay)
    {
        ArgumentNullException.ThrowIfNull(scope); ArgumentNullException.ThrowIfNull(provider); ArgumentNullException.ThrowIfNull(fence);
        if(!Grammar.Match(fingerprint,"[0-9a-f]{64}")) throw Grammar.Invalid();
        this.scope=scope; this.provider=provider; this.fingerprint=fingerprint; this.fence=fence; this.clock=clock;
        this.delay=delay??((t,ct)=>Task.Delay(t,clock,ct)); http=new(handler,true);
    }
    private static SocketsHttpHandler ProductionHandler()=>new() { AllowAutoRedirect=false,UseProxy=false,UseCookies=false,ConnectTimeout=TimeSpan.FromSeconds(5),ActivityHeadersPropagator=null,AutomaticDecompression=DecompressionMethods.None };
    public static OkxExecutionClient Create(OkxExecutionScope scope,IOkxSigningMaterialProvider provider,string expectedFingerprint,IOkxFinalDispatchFence fence) => new(ProductionHandler(),TimeProvider.System,scope,provider,expectedFingerprint,fence,null);
    internal static OkxExecutionClient ForTests(HttpMessageHandler handler,TimeProvider clock,OkxExecutionScope scope,IOkxSigningMaterialProvider provider,string fingerprint,IOkxFinalDispatchFence fence,Func<TimeSpan,CancellationToken,Task>? delay=null)=>new(handler,clock,scope,provider,fingerprint,fence,delay);
    public Task<OkxWriteOutcome> SubmitAsync(OkxPreparedRequest request,CancellationToken ct)=>Write(request,OkxExecutionOperation.Submit,ct);
    public Task<OkxWriteOutcome> CancelAsync(OkxPreparedRequest request,CancellationToken ct)=>Write(request,OkxExecutionOperation.Cancel,ct);
    public Task<OkxWriteOutcome> PlaceProtectionAsync(OkxPreparedRequest request,CancellationToken ct)=>Write(request,OkxExecutionOperation.PlaceProtection,ct);
    public Task<OkxWriteOutcome> CancelProtectionAsync(OkxPreparedRequest request,CancellationToken ct)=>Write(request,OkxExecutionOperation.CancelProtection,ct);
    private async Task<OkxWriteOutcome> Write(OkxPreparedRequest r,OkxExecutionOperation operation,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        if(r.Operation!=operation || r.Scope!=scope) return Outcome(r,OkxWriteDisposition.ProvenNotSent,"RequestBindingMismatch");
        var result=await Send(r,ct).ConfigureAwait(false);
        if(result.Bytes is null) return Outcome(r,result.Invoked?OkxWriteDisposition.Unknown:OkxWriteDisposition.ProvenNotSent,result.Reason);
        try { return ParseWrite(r,result.Bytes); }
        catch(Exception e) when(e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException) { return Outcome(r,OkxWriteDisposition.Unknown,"InvalidPayload"); }
        finally { CryptographicOperations.ZeroMemory(result.Bytes); }
    }
    public async Task<OkxOrderObservation> LookupAsync(string instrument,string? ordId,string originalClOrdId,CancellationToken ct)
    {
        // Invalid caller data fails locally and cannot become arbitrary signed paths.
        // This deadline is library-owned: floor to milliseconds, never extend the ten-second budget.
        // Caller-owned mutation/replay deadlines retain their exact strict constructor invariant.
        var deadline=clock.GetUtcNow().AddSeconds(10);
        deadline=new DateTimeOffset(deadline.Ticks-deadline.Ticks%TimeSpan.TicksPerMillisecond,TimeSpan.Zero);
        var r=OkxPreparedRequest.Lookup(scope,instrument,ordId,originalClOrdId,deadline);
        var result=await Send(r,ct).ConfigureAwait(false);
        if(result.Bytes is null) return UnknownObservation(r);
        try { return ParseLookup(r,result.Bytes); }
        catch(Exception e) when(e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException) { return UnknownObservation(r); }
        finally { CryptographicOperations.ZeroMemory(result.Bytes); }
    }
    private sealed record Response(bool Invoked,string Reason,byte[]? Bytes=null);
    private async Task<Response> Send(OkxPreparedRequest r,CancellationToken caller)
    {
        bool invoked=false,held=false,laneHeld=false; Lane? lane=null;
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10),clock);
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(caller,closing.Token,timeout.Token);
        var ct=linked.Token;
        var budgetEnd=clock.GetUtcNow().AddSeconds(10);
        bool Admitted()=>!Volatile.Read(ref disposed) && !ct.IsCancellationRequested && clock.GetUtcNow()<r.Deadline && clock.GetUtcNow()<budgetEnd;
        try
        {
            if(!Admitted()) return new(false,"ExpiredOrStopped");
            if(ExecutionDiagnostics.Unsafe) return new(false,"UnsafeHttpDiagnostics");
            await sendLock.WaitAsync(ct).ConfigureAwait(false); held=true;
            if(!Admitted()) return new(false,"ExpiredOrStopped");
            lane=Lanes.GetOrAdd(scope.Environment+":"+scope.Uid+":"+r.Operation+":"+r.Instrument,_=>new());
            await lane.Gate.WaitAsync(ct).ConfigureAwait(false); laneHeld=true;
            var wait=lane.Next-clock.GetUtcNow();
            if(wait>TimeSpan.Zero)
            {
                if(clock.GetUtcNow()+wait>=r.Deadline || clock.GetUtcNow()+wait>=budgetEnd) return new(false,"DeadlineBeforeQuota");
                await delay(wait,ct).ConfigureAwait(false);
            }
            // No preflight GET, account inference or authority bootstrap occurs here.
            if(!Admitted()) return new(false,"ExpiredOrStopped");
            if(ExecutionDiagnostics.Unsafe) return new(false,"UnsafeHttpDiagnostics");
            using var material=await OpenMaterial(ct).ConfigureAwait(false);
            if(material is null || material.Scope!=scope || material.Fingerprint!=fingerprint) return new(false,"CredentialBindingMismatch");
            if(ExecutionDiagnostics.Unsafe) return new(false,"UnsafeHttpDiagnostics");
            using var request=new HttpRequestMessage(r.Method=="POST"?HttpMethod.Post:HttpMethod.Get,new Uri("https://eea.okx.com:443"+r.Path));
            if(request.RequestUri!.PathAndQuery!=r.Path || request.RequestUri.Scheme!="https" || request.RequestUri.Host!="eea.okx.com" || request.RequestUri.Port!=443 || request.RequestUri.Fragment.Length!=0) return new(false,"EndpointDenied");
            if(r.Method=="POST") { request.Content=new ByteArrayContent(r.CopyBody()); request.Content.Headers.ContentType=new("application/json"); }
            request.Headers.Add("x-simulated-trading",scope.Environment=="Demo"?"1":"0");
            // expTime is documented for general place/amend, not cancel or standalone algo placement.
            if(r.Operation==OkxExecutionOperation.Submit) request.Headers.Add("expTime",r.Deadline.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
            material.Authenticate(request,OkxRequestSigner.Timestamp(clock.GetUtcNow()),r);
            Task<HttpResponseMessage> sending;
            lock(lifetime)
            {
                if(!Admitted()) return new(false,"ExpiredOrStopped");
                if(ExecutionDiagnostics.Unsafe) return new(false,"UnsafeHttpDiagnostics");
                IDisposable? guard=null;
                try
                {
                    if(fence is IOkxSerializedDispatchFence serialized)
                    {
                        guard=serialized.TryEnterDispatch(r,clock.GetUtcNow());
                        if(guard is null) return new(false,"FinalFenceDenied");
                    }
                    else if(!fence.CanDispatch(r,clock.GetUtcNow())) return new(false,"FinalFenceDenied");
                    if(!Admitted()) return new(false,"ExpiredOrStopped");
                    // Guard remains held through synchronous invocation, never across response await.
                    lane.Next=clock.GetUtcNow()+TimeSpan.FromMilliseconds(r.Operation is OkxExecutionOperation.PlaceProtection or OkxExecutionOperation.CancelProtection?110:40);
                    invoked=true;
                    sending=http.SendAsync(request,ct);
                }
                finally { guard?.Dispose(); }
            }
            using var response=await sending.ConfigureAwait(false);
            if(!response.IsSuccessStatusCode) return new(true,(int)response.StatusCode is >=300 and <=399?"RedirectDenied":"HttpUncertain");
            var bytes=await ReadBounded(response,ct).ConfigureAwait(false);
            if(ct.IsCancellationRequested || clock.GetUtcNow()>=r.Deadline || clock.GetUtcNow()>=budgetEnd) { CryptographicOperations.ZeroMemory(bytes); return new(true,"ResponseExpired"); }
            return new(true,"Received",bytes);
        }
        catch(OperationCanceledException) { return new(invoked,"CanceledOrTimeout"); }
        catch(HttpRequestException) { return new(invoked,"TransportFailure"); }
        catch(IOException) { return new(invoked,"TransportFailure"); }
        catch(BodyLimitException) { return new(invoked,"BodyLimit"); }
        catch(Exception e) when(e is ArgumentException or ObjectDisposedException or InvalidOperationException or NotSupportedException) { return new(invoked,"LocalFailure"); }
        finally { if(laneHeld) lane!.Gate.Release(); if(held) sendLock.Release(); }
    }

    private static OkxWriteOutcome Outcome(OkxPreparedRequest r,OkxWriteDisposition d,string reason,string? order=null,string? code=null,string? scode=null,string? sub=null)=>new(d,r.OriginalClientId,order??r.OrderId,reason,code,scode,sub);
    private static JsonDocument Envelope(byte[] bytes)
    {
        var doc=JsonDocument.Parse(bytes,new JsonDocumentOptions { MaxDepth=16 });
        try
        {
            var e=doc.RootElement; Grammar.Unique(e);
            Fields(e,"code msg data inTime outTime",false);
            _=Grammar.String(e,"msg"); string top=Grammar.String(e,"code"); if(top is not ("1" or "2") && !Code(top)) throw new JsonException();
            if(e.GetProperty("data").ValueKind!=JsonValueKind.Array) throw new JsonException();
            return doc;
        }
        catch { doc.Dispose(); throw; }
    }
    private static bool Code(string s)=>s=="0" || Grammar.Match(s,"[1-9][0-9]{4,7}");
    private static string? Sub(JsonElement row)
    { if(!row.TryGetProperty("subCode",out var value)) return null; var s=value.GetString()!; if(s!="" && !Code(s)) throw new JsonException(); return s; }
    private static void Fields(JsonElement e,string allowed,bool allStrings=true)
    {
        if(e.ValueKind!=JsonValueKind.Object) throw new JsonException();
        var names=allowed.Split(' ').ToHashSet(StringComparer.Ordinal);
        foreach(var p in e.EnumerateObject()) if(!names.Contains(p.Name) || allStrings && p.Value.ValueKind!=JsonValueKind.String) throw new JsonException();
        if(!allStrings) foreach(var p in e.EnumerateObject()) if(p.Name!="data" && p.Value.ValueKind!=JsonValueKind.String) throw new JsonException();
    }
    private static OkxWriteOutcome ParseWrite(OkxPreparedRequest r,byte[] bytes)
    {
        using var doc=Envelope(bytes); var root=doc.RootElement; string code=Grammar.String(root,"code"); var rows=root.GetProperty("data");
        // Empty/error-only envelopes lack matching per-item identity, so never prove rejection.
        if(rows.GetArrayLength()!=1) return Outcome(r,OkxWriteDisposition.Unknown,"UnmatchedResult",code:code);
        var row=rows[0]; bool algo=r.Operation is OkxExecutionOperation.PlaceProtection or OkxExecutionOperation.CancelProtection;
        Fields(row,algo?"algoId algoClOrdId clOrdId tag sCode sMsg subCode ts":"ordId clOrdId tag ts sCode sMsg subCode");
        if(!algo && !Grammar.Match(Grammar.String(row,"ts"),"[0-9]{13}")) throw new JsonException();
        string scode=Grammar.String(row,"sCode"); if(!Code(scode)) throw new JsonException();
        _=Grammar.String(row,"sMsg"); string? sub=Sub(row);
        string id=Grammar.String(row,algo?"algoId":"ordId");
        if(id!="" && !Grammar.OrderId(id)) throw new JsonException();
        // Algo cancellation may return a blank deprecated algoClOrdId; a matching original algoId is required then.
        string client=Grammar.String(row,algo?"algoClOrdId":"clOrdId");
        bool match=(r.OrderId is null || id==r.OrderId) && (client==r.OriginalClientId || r.Operation==OkxExecutionOperation.CancelProtection && r.OrderId is not null && client=="");
        if(!match) return Outcome(r,OkxWriteDisposition.Unknown,"IdentityMismatch",code:code,scode:scode,sub:sub);
        if(code=="0" && scode=="0" && id!="" && string.IsNullOrEmpty(sub)) return Outcome(r,OkxWriteDisposition.Acknowledged,"RequestAcknowledged",id,code,scode,sub);
        // 51000's parameter-error description does not establish an endpoint-specific safe rejection shape.
        // Until that contract is independently validated, even a matching empty-ID error stays Unknown.
        // This transport emits no release-capable Rejected outcome; retain the enum for contract compatibility.
        return Outcome(r,OkxWriteDisposition.Unknown,"ProtocolUncertain",code:code,scode:scode,sub:sub);
    }
    private static OkxOrderObservation UnknownObservation(OkxPreparedRequest r)=>new(r.Scope,r.Instrument,r.OrderId,r.OriginalClientId,null,null,null,null,null,"Unknown");
    private static OkxOrderObservation ParseLookup(OkxPreparedRequest r,byte[] bytes)
    {
        using var doc=Envelope(bytes); var root=doc.RootElement;
        if(Grammar.String(root,"code")!="0" || root.GetProperty("data").GetArrayLength()!=1) return UnknownObservation(r);
        var row=root.GetProperty("data")[0];
        if(row.ValueKind!=JsonValueKind.Object) throw new JsonException();
        // Selected required schema plus documented optional fields (EEA R:7645-7679).
        // Attachment fields are string-typed/duplicate-checked, then discarded, including private failReason.
        // Parent-only observation does not report attachment success/failure or protection coverage.
        const string fields="instType instId tdMode ordId clOrdId side state sz accFillSz avgPx tradeQuoteCcy algoClOrdId algoId attachAlgoClOrdId cTime cancelSource cancelSourceReason category ccy fee feeCcy fillPx fillSz fillTime isTpLimit lever ordType pnl posSide px pxType pxUsd pxVol quickMgnType rebate rebateCcy reduceOnly slOrdPx slTriggerPx slTriggerPxType source stpId stpMode tag tgtCcy tpOrdPx tpTriggerPx tpTriggerPxType tradeId uTime";
        foreach(var p in row.EnumerateObject())
        {
            if(p.Name=="attachAlgoOrds") { if(p.Value.ValueKind!=JsonValueKind.Array || p.Value.GetArrayLength()>1) throw new JsonException(); foreach(var a in p.Value.EnumerateArray()) Fields(a,"attachAlgoId attachAlgoClOrdId tpOrdKind tpTriggerPx tpOrdPx tpTriggerPxType slTriggerPx slOrdPx slTriggerPxType failCode failReason"); }
            else if(p.Name=="linkedAlgoOrd") Fields(p.Value,"algoId");
            else if(!fields.Split(' ').Contains(p.Name,StringComparer.Ordinal) || p.Value.ValueKind!=JsonValueKind.String) throw new JsonException();
        }
        string S(string name)=>Grammar.String(row,name);
        string order=S("ordId"),client=S("clOrdId"),state=S("state"),size=S("sz"),filled=S("accFillSz"),avg=S("avgPx");
        if(S("instType")!="SPOT" || S("tdMode")!="cash" || S("instId")!=r.Instrument || S("tradeQuoteCcy")!=r.Scope.QuoteCurrency ||
            !Grammar.OrderId(order) || !Grammar.ClientId(client) || (r.OrderId is null?client!=r.OriginalClientId:order!=r.OrderId) ||
            S("side") is not ("buy" or "sell") || state is not ("live" or "partially_filled" or "filled" or "canceled" or "mmp_canceled") ||
            !Grammar.Decimal(size) || !Grammar.Decimal(filled,true) || avg!="" && !Grammar.Decimal(avg)) return UnknownObservation(r);
        return new(r.Scope,r.Instrument,order,r.OriginalClientId,state,size,filled,avg,S("tradeQuoteCcy"),"Observed",client);
    }
    private async ValueTask<OkxSigningMaterial?> OpenMaterial(CancellationToken ct)
    {
        try { return await provider.OpenAsync(ct).ConfigureAwait(false); }
        catch(OperationCanceledException) { throw; }
        catch { return null; } // Never expose store/provider exception messages or inner exceptions.
    }
    private sealed class BodyLimitException : Exception { }
    private static async Task<byte[]> ReadBounded(HttpResponseMessage response,CancellationToken ct)
    {
        const int max=65536;
        if(response.Content.Headers.ContentLength>max) throw new BodyLimitException();
        byte[] buffer=new byte[max+1];
        try
        {
            await using var stream=await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            int used=0,n;
            while((n=await stream.ReadAsync(buffer.AsMemory(used,buffer.Length-used),ct).ConfigureAwait(false))>0)
            { used+=n; if(used>max) throw new BodyLimitException(); }
            return buffer.AsSpan(0,used).ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }
    public ValueTask DisposeAsync()
    {
        lock(lifetime)
        {
            if(disposal is not null) return new(disposal);
            Volatile.Write(ref disposed,true); closing.Cancel();
            disposal=Drain(); return new(disposal);
        }
    }
    private async Task Drain()
    {
        await sendLock.WaitAsync().ConfigureAwait(false);
        try { http.Dispose(); }
        finally { sendLock.Release(); } // semaphore remains alive for callers already queued; no Dispose race.
    }
}
internal sealed class ExecutionDiagnostics : IObserver<System.Diagnostics.DiagnosticListener>
{
    private static readonly ExecutionDiagnostics Instance=new();
    private static readonly IDisposable Subscription=System.Diagnostics.DiagnosticListener.AllListeners.Subscribe(Instance);
    private readonly List<WeakReference<System.Diagnostics.DiagnosticListener>> listeners=[];
    public void OnNext(System.Diagnostics.DiagnosticListener value) { if(value.Name=="HttpHandlerDiagnosticListener") lock(listeners) listeners.Add(new(value)); }
    public void OnError(Exception error) { }
    public void OnCompleted() { }
    internal static bool Unsafe
    {
        get { GC.KeepAlive(Subscription); lock(Instance.listeners) if(Instance.listeners.Any(w=>w.TryGetTarget(out var l)&&l.IsEnabled())) return true; return System.Diagnostics.Tracing.EventSource.GetSources().Any(s=>s.Name=="System.Net.Http"&&s.IsEnabled()); }
    }
}
