using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Apex.Adapters.Okx.Execution;

/// <summary>Closed REST observation surfaces. Construction does not open credentials or invoke HTTP.
/// Observed establishes shape only, never account acceptance, readiness, coverage or financial authority.</summary>
public sealed class OkxExecutionObservationClient : IAsyncDisposable
{
    private sealed class Lane
    {
        internal readonly SemaphoreSlim Gate=new(1,1);
        internal DateTimeOffset NextUtc;
        internal TimeProvider? Clock;
        internal long NextMonotonic;
    }
    // Conservative process-lifetime observation quotas, independent of credentials and returned identity.
    private static readonly ConcurrentDictionary<string,Lane> PrivateLanes=new(StringComparer.Ordinal);
    private static readonly Lane PublicLane=new();
    private readonly OkxExecutionScope scope;
    private readonly TimeProvider clock;
    private readonly IOkxSigningMaterialProvider provider;
    private readonly string fingerprint;
    private readonly IOkxObservationReadFence fence;
    private readonly HttpMessageInvoker http;
    private readonly Func<TimeSpan,CancellationToken,Task> delay;
    private readonly SemaphoreSlim sendLock=new(1,1);
    private readonly object lifetime=new();
    private readonly CancellationTokenSource closing=new();
    private bool disposed;
    private Task? disposal;
    private static readonly TimeSpan Budget=TimeSpan.FromSeconds(10);
    private const int BodyCap=1024*1024;
    private OkxExecutionObservationClient(HttpMessageHandler handler,TimeProvider clock,OkxExecutionScope scope,IOkxSigningMaterialProvider provider,string expectedFingerprint,IOkxObservationReadFence fence,Func<TimeSpan,CancellationToken,Task>? delay)
    {
        ArgumentNullException.ThrowIfNull(scope);ArgumentNullException.ThrowIfNull(provider);ArgumentNullException.ThrowIfNull(fence);ArgumentNullException.ThrowIfNull(clock);
        if(!Grammar.Match(expectedFingerprint,"[0-9a-f]{64}"))throw new ArgumentException("InvalidObservationBinding");
        this.scope=scope;this.clock=clock;this.provider=provider;fingerprint=expectedFingerprint;this.fence=fence;
        this.delay=delay??((t,ct)=>Task.Delay(t,clock,ct));http=new(handler,true);
    }
    private static SocketsHttpHandler ProductionHandler()=>new()
    { AllowAutoRedirect=false,UseProxy=false,UseCookies=false,ConnectTimeout=TimeSpan.FromSeconds(5),AutomaticDecompression=DecompressionMethods.None,ActivityHeadersPropagator=null };
    public static OkxExecutionObservationClient Create(OkxExecutionScope scope,IOkxSigningMaterialProvider provider,string expectedFingerprint,IOkxObservationReadFence fence)=>new(ProductionHandler(),TimeProvider.System,scope,provider,expectedFingerprint,fence,null);
    internal static OkxExecutionObservationClient ForTests(HttpMessageHandler handler,TimeProvider clock,OkxExecutionScope scope,IOkxSigningMaterialProvider provider,string expectedFingerprint,IOkxObservationReadFence fence,Func<TimeSpan,CancellationToken,Task>? delay=null)=>new(handler,clock,scope,provider,expectedFingerprint,fence,delay);
    public Task<OkxObservationRead<OkxTimeDto>> ReadTimeAsync(OkxObservationContext context,CancellationToken ct)=>Read<OkxTimeDto>("/api/v5/public/time","",context,false,true,ct);
    public Task<OkxObservationRead<OkxExecutionAccountDto>> ReadAccountAsync(OkxObservationContext context,CancellationToken ct)=>Read<OkxExecutionAccountDto>("/api/v5/account/config","",context,true,true,ct);
    public Task<OkxObservationRead<OkxExecutionBalanceDto>> ReadBalancesAsync(OkxObservationContext context,CancellationToken ct)=>Read<OkxExecutionBalanceDto>("/api/v5/account/balance","",context,true,true,ct);
    public Task<OkxObservationRead<OkxExecutionInstrumentDto>> ReadInstrumentsAsync(OkxObservationContext context,CancellationToken ct)=>Read<OkxExecutionInstrumentDto>("/api/v5/account/instruments","?instType=SPOT",context,true,true,ct);
    public Task<OkxObservationRead<OkxExecutionFeeDto>> ReadFeesAsync(string instrument,OkxObservationContext context,CancellationToken ct)
    {
        bool valid=Grammar.Instrument(instrument);
        return Read<OkxExecutionFeeDto>("/api/v5/account/trade-fee",valid?"?instId="+instrument+"&instType=SPOT":"",context,true,valid,ct);
    }
    internal OkxExecutionScope RequestedObservationScope=>scope;
    internal TimeProvider ObservationClock=>clock;
    internal bool CanContinueObservation(OkxObservationContext context)=>!Volatile.Read(ref disposed)&&!ExecutionDiagnostics.Unsafe&&context is not null&&context.ConnectionId!=Guid.Empty&&context.CaptureId!=Guid.Empty&&fence.CanObserve(scope,context,clock.GetUtcNow());
    public Task<OkxObservationRead<OkxExecutionOrderDto>> ReadOrderAsync(OkxObservationOrderQuery query,OkxObservationContext context,CancellationToken ct)
    {
        var q=query is null?"":(query.ClientOrderId is null?"?":"?clOrdId="+query.ClientOrderId+"&")+"instId="+query.Instrument+(query.OrderId is null?"":"&ordId="+query.OrderId);
        return Read<OkxExecutionOrderDto>("/api/v5/trade/order",q,context,true,query is not null,ct,detailInstrument:query?.Instrument);
    }
    public Task<OkxObservationRead<OkxExecutionOrderDto>> ReadPendingAsync(OkxObservationPageQuery page,OkxObservationContext context,CancellationToken ct)=>Page<OkxExecutionOrderDto>(OkxObservationSurface.Pending,page,context,true,ct);
    public Task<OkxObservationRead<OkxExecutionOrderDto>> ReadHistoryAsync(OkxObservationWindow window,OkxObservationPageQuery page,OkxObservationContext context,CancellationToken ct)=>Page<OkxExecutionOrderDto>(window==OkxObservationWindow.Archive?OkxObservationSurface.HistoryArchive:OkxObservationSurface.History,page,context,Enum.IsDefined(window),ct);
    public Task<OkxObservationRead<OkxExecutionFillDto>> ReadFillsAsync(OkxObservationWindow window,OkxObservationPageQuery page,OkxObservationContext context,CancellationToken ct)=>Page<OkxExecutionFillDto>(window==OkxObservationWindow.Archive?OkxObservationSurface.FillsArchive:OkxObservationSurface.Fills,page,context,Enum.IsDefined(window),ct);
    public Task<OkxObservationRead<OkxExecutionBillDto>> ReadBillsAsync(OkxObservationWindow window,OkxObservationPageQuery page,OkxObservationContext context,CancellationToken ct)=>Page<OkxExecutionBillDto>(window==OkxObservationWindow.Archive?OkxObservationSurface.BillsArchive:OkxObservationSurface.Bills,page,context,Enum.IsDefined(window),ct);
    private Task<OkxObservationRead<T>> Page<T>(OkxObservationSurface surface,OkxObservationPageQuery page,OkxObservationContext context,bool valid,CancellationToken ct)
    {
        valid=valid&&RecoveryRequest.Valid(surface,page);
        return Read<T>(RecoveryRequest.Endpoint(surface),valid?RecoveryRequest.Query(surface,page):"",context,true,valid,ct,valid?page.Limit:null);
    }
    private async Task<OkxObservationRead<T>> Read<T>(string endpoint,string query,OkxObservationContext context,bool isPrivate,bool valid,CancellationToken caller,int? pageLimit=null,string? detailInstrument=null)
    {
        context??=new(Guid.Empty,Guid.Empty);
        var startUtc=clock.GetUtcNow();var startMono=clock.GetTimestamp();var lastUtc=startUtc;var lastMono=startMono;
        bool held=false,laneHeld=false,detailHeld=false;Lane? lane=null;OkxExecutionQuota.Lane? detail=null;byte[]? buffer=null;string? digest=null;
        // Timer bounds real asynchronous waits; UTC and independent monotonic checks also bound scripted/provider work.
        using var timeout=new CancellationTokenSource(Budget,clock);
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(caller,closing.Token,timeout.Token);
        var ct=linked.Token;
        OkxObservationRead<T> Result(string? error,IEnumerable<T>? rows=null)=>new(scope,context,endpoint,query,startUtc,clock.GetUtcNow(),startMono,clock.GetTimestamp(),digest,rows??[],error);
        bool Admitted()
        {
            var utc=clock.GetUtcNow();var mono=clock.GetTimestamp();
            bool okay=!Volatile.Read(ref disposed)&&!ct.IsCancellationRequested&&utc>=lastUtc&&mono>=lastMono&&utc-startUtc<Budget&&clock.GetElapsedTime(startMono,mono)<Budget&&OkxObservationPager.WithinCollectionBudget(caller);
            lastUtc=utc;lastMono=mono;return okay;
        }
        string? Check()
        {
            if(!Admitted())return "ExpiredOrStopped";
            if(ExecutionDiagnostics.Unsafe)return "UnsafeHttpDiagnostics";
            if(!fence.CanObserve(scope,context,clock.GetUtcNow()))return "ReadFenceDenied";
            return Admitted()?null:"ExpiredOrStopped";
        }
        try
        {
            if(!valid || context.ConnectionId==Guid.Empty || context.CaptureId==Guid.Empty)return Result("InvalidObservationRequest");
            string? denied=Check();if(denied is not null)return Result(denied);
            await sendLock.WaitAsync(ct).ConfigureAwait(false);held=true;
            denied=Check();if(denied is not null)return Result(denied);
            lane=isPrivate?PrivateLanes.GetOrAdd(scope.Environment+":"+scope.Uid,_=>new()):PublicLane;
            await lane.Gate.WaitAsync(ct).ConfigureAwait(false);laneHeld=true;
            var wait=lane.NextUtc-clock.GetUtcNow();
            if(ReferenceEquals(lane.Clock,clock))
            {
                var monoWait=clock.GetElapsedTime(clock.GetTimestamp(),lane.NextMonotonic);
                if(monoWait>wait)wait=monoWait;
            }
            if(wait>TimeSpan.Zero)
            {
                if(clock.GetUtcNow()-startUtc+wait>=Budget || clock.GetElapsedTime(startMono,clock.GetTimestamp())+wait>=Budget)return Result("BudgetBeforeQuota");
                await delay(wait,ct).ConfigureAwait(false);
                // A custom delay must not allow an early start. Production Task.Delay may also wake early.
                if(clock.GetUtcNow()<lane.NextUtc || ReferenceEquals(lane.Clock,clock)&&clock.GetTimestamp()<lane.NextMonotonic)return Result("QuotaWaitIncomplete");
            }
            denied=Check();if(denied is not null)return Result(denied);
            if(detailInstrument is not null)
            {
                // Fixed lock order: client -> private observation -> shared detail. Old Lookup never takes private.
                detail=OkxExecutionQuota.OrderDetail(scope,detailInstrument);
                await detail.Gate.WaitAsync(ct).ConfigureAwait(false);detailHeld=true;
                var detailWait=detail.Next-clock.GetUtcNow();
                if(ReferenceEquals(detail.Clock,clock))
                {
                    var monoWait=clock.GetElapsedTime(clock.GetTimestamp(),detail.NextMonotonic);
                    if(monoWait>detailWait)detailWait=monoWait;
                }
                if(detailWait>TimeSpan.Zero)
                {
                    if(clock.GetUtcNow()-startUtc+detailWait>=Budget||clock.GetElapsedTime(startMono,clock.GetTimestamp())+detailWait>=Budget)return Result("BudgetBeforeQuota");
                    await delay(detailWait,ct).ConfigureAwait(false);
                    if(clock.GetUtcNow()<detail.Next||ReferenceEquals(detail.Clock,clock)&&clock.GetTimestamp()<detail.NextMonotonic)return Result("QuotaWaitIncomplete");
                }
                denied=Check();if(denied is not null)return Result(denied);
            }
            using var material=isPrivate?await OpenMaterial(ct).ConfigureAwait(false):null;
            if(isPrivate && material is null)return Result("CredentialUnavailable");
            if(isPrivate && (material!.Scope!=scope || material.Fingerprint!=fingerprint || material.Provenance!=(scope.Environment=="Demo"?"DemoTradingApi":"LiveAccountApi")))return Result("CredentialBindingMismatch");
            denied=Check();if(denied is not null)return Result(denied);
            var exact=endpoint+query;
            using var request=new HttpRequestMessage(HttpMethod.Get,new Uri("https://eea.okx.com:443"+exact));
            var uri=request.RequestUri!;
            if(uri.Scheme!="https" || uri.Host!="eea.okx.com" || uri.Port!=443 || uri.PathAndQuery!=exact || uri.Fragment.Length!=0)return Result("EndpointDenied");
            request.Headers.Add("x-simulated-trading",scope.Environment=="Demo"?"1":"0");
            if(isPrivate)
            {
                try{material!.AuthenticateRead(request,OkxRequestSigner.Timestamp(clock.GetUtcNow()),exact);}
                catch(Exception e) when(e is ArgumentException or ObjectDisposedException){return Result("CredentialUnavailable");}
            }
            Task<HttpResponseMessage> sending;
            lock(lifetime)
            {
                denied=Check();if(denied is not null)return Result(denied);
                // Lifetime lock spans only synchronous invocation; disposal revokes before draining.
                var spacing=TimeSpan.FromMilliseconds(isPrivate?600:250);
                lane.NextUtc=clock.GetUtcNow()+spacing;lane.Clock=clock;
                lane.NextMonotonic=checked(clock.GetTimestamp()+(long)(spacing.TotalSeconds*clock.TimestampFrequency));
                if(detail is not null)
                {
                    detail.Next=clock.GetUtcNow()+TimeSpan.FromMilliseconds(40);detail.Clock=clock;
                    detail.NextMonotonic=checked(clock.GetTimestamp()+(long)(0.040*clock.TimestampFrequency));
                }
                sending=http.SendAsync(request,ct);
            }
            using var response=await sending.ConfigureAwait(false);
            if(!response.IsSuccessStatusCode)return Result((int)response.StatusCode is >=300 and <=399?"RedirectDenied":"HttpFailure");
            if(!Admitted())return Result("ExpiredOrStopped");
            if(response.Content.Headers.ContentLength>BodyCap)return Result("BodyLimit");
            // One non-growing owned private buffer. All capacity cleared, including on stream/parse failures.
            buffer=new byte[BodyCap+1];int used=0;
            await using(var stream=await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            {
                int n;while((n=await stream.ReadAsync(buffer.AsMemory(used,buffer.Length-used),ct).ConfigureAwait(false))>0)
                {used+=n;if(used>BodyCap)return Result("BodyLimit");}
            }
            if(!Admitted())return Result("ExpiredOrStopped");
            digest=Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0,used))).ToLowerInvariant();
            IReadOnlyList<T> rows;
            try{rows=OkxObservationJson.Parse<T>(buffer.AsMemory(0,used),endpoint,pageLimit);}
            catch(OkxObservationJson.CardinalityException){return Result("Cardinality");}
            catch(OkxObservationJson.VenueException e){return Result(e.Code);}
            catch(Exception e) when(e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or DecoderFallbackException){return Result("InvalidPayload");}
            denied=Check();return denied is null?Result(null,rows):Result(denied);
        }
        catch(OperationCanceledException){return Result("CanceledOrTimeout");}
        catch(HttpRequestException){return Result("TransportFailure");}
        catch(IOException){return Result("TransportFailure");}
        finally
        {
            if(buffer is not null)CryptographicOperations.ZeroMemory(buffer);
            if(detailHeld)detail!.Gate.Release();if(laneHeld)lane!.Gate.Release();if(held)sendLock.Release();
        }
    }
    private async ValueTask<OkxSigningMaterial?> OpenMaterial(CancellationToken ct)
    {try{return await provider.OpenAsync(ct).ConfigureAwait(false);}catch(OperationCanceledException){throw;}catch{return null;}}
    public ValueTask DisposeAsync()
    {
        lock(lifetime)
        {
            if(disposal is not null)return new(disposal);
            Volatile.Write(ref disposed,true);closing.Cancel();disposal=Drain();return new(disposal);
        }
    }
    private async Task Drain()
    {await sendLock.WaitAsync().ConfigureAwait(false);try{http.Dispose();}finally{sendLock.Release();}}
}
