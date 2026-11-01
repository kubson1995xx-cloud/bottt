using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
namespace Apex.Adapters.Okx.Execution;

/// <summary>Single-use raw pair. No initial snapshot, replay continuity, financial authority or automatic reconnect.</summary>
public sealed class OkxObservationStream : IAsyncDisposable
{
    private const int MessageCap=1024*1024,QueueCap=64,ByteCap=4*1024*1024;
    private static readonly ImmutableArray<string> Limitations=["NoInitialSnapshot","NoExchangeReplaySequence","NoAtomicSnapshot","ObservationNotAuthority","TrailingStreamNotSubscribed"];
    private static readonly object RegistrySync=new();private static readonly HashSet<string> Active=[];
    private static readonly SemaphoreSlim ConnectGate=new(1,1);
    private static DateTimeOffset nextConnect;private static TimeProvider? nextClock;private static long nextMono;
    private readonly object sync=new();
    private readonly OkxExecutionScope scope;private readonly IOkxSigningMaterialProvider provider;private readonly string fingerprint;private readonly IOkxObservationStreamFence fence;private readonly TimeProvider clock;
    private readonly Func<Uri,CancellationToken,Task<WebSocket>> connect;
    private readonly ProductionSockets? production;
    private readonly CancellationTokenSource stopping=new();
    private readonly TaskCompletionSource gapNotified=new(TaskCreationOptions.RunContinuationsAsynchronously),startAssigned=new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Signal contains no private data. Two reserved terminal slots never compete with the bounded data queue.
    private readonly Channel<bool> signal=Channel.CreateBounded<bool>(new BoundedChannelOptions(1){FullMode=BoundedChannelFullMode.DropWrite});
    private readonly Queue<(OkxStreamObservation Event,int Charge)> queue=new();
    private OkxStreamObservation? gapEvent,closedEvent;
    private readonly Lane[] lanes=[new(OkxStreamLane.PrivateOrders),new(OkxStreamLane.BusinessAlgos)];
    private OkxObservationContext? context;private Guid? epoch;private long sequence;private int used,enumerated;private bool disposed,closed,gapped,paired;private string? error;
    private System.Runtime.ExceptionServices.ExceptionDispatchInfo? callbackFailure;
    private ITimer? handshakeTimer;
    private Task? running,disposal;private DateTimeOffset startUtc,lastUtc;private long startMono,lastMono;
    private sealed class Lane(OkxStreamLane kind)
    {
        internal readonly OkxStreamLane Kind=kind;internal WebSocket? Socket;internal readonly SemaphoreSlim SendGate=new(1,1);
        internal readonly byte[] Login=new byte[65536],Message=new byte[MessageCap+1];
        internal string? ConnId;internal int Phase;internal DateTimeOffset IdleUtc,PingUtc;internal long IdleMono,PingMono;internal bool PingPending;
    }
    private OkxObservationStream(Func<Uri,CancellationToken,Task<WebSocket>>? connect,TimeProvider clock,OkxExecutionScope scope,IOkxSigningMaterialProvider provider,string fingerprint,IOkxObservationStreamFence fence)
    {
        ArgumentNullException.ThrowIfNull(clock);ArgumentNullException.ThrowIfNull(scope);ArgumentNullException.ThrowIfNull(provider);ArgumentNullException.ThrowIfNull(fence);
        if(!Grammar.Match(fingerprint,"[0-9a-f]{64}"))throw new ArgumentException("InvalidObservationBinding");
        this.clock=clock;this.scope=scope;this.provider=provider;this.fingerprint=fingerprint;this.fence=fence;
        if(connect is null){production=new();this.connect=production.Connect;}else this.connect=connect;
    }
    public static OkxObservationStream Create(OkxExecutionScope scope,IOkxSigningMaterialProvider provider,string expectedFingerprint,IOkxObservationStreamFence fence)=>new(null,TimeProvider.System,scope,provider,expectedFingerprint,fence);
    internal static OkxObservationStream ForTests(Func<Uri,CancellationToken,Task<WebSocket>> connect,TimeProvider clock,OkxExecutionScope scope,IOkxSigningMaterialProvider provider,string expectedFingerprint,IOkxObservationStreamFence fence)=>new(connect,clock,scope,provider,expectedFingerprint,fence);
    public OkxStreamSnapshot ReadState(){lock(sync)return new(context,epoch,sequence,paired&&!gapped&&!closed,gapped,error,closed,Limitations);}
    public async IAsyncEnumerable<OkxStreamObservation> ObserveAsync(OkxObservationContext context,[EnumeratorCancellation] CancellationToken ct)
    {
        if(Interlocked.Exchange(ref enumerated,1)!=0)throw new InvalidOperationException("StreamSingleUse");
        lock(sync)
        {
            if(disposed){startAssigned.TrySetResult();throw new ObjectDisposedException(nameof(OkxObservationStream));}
            this.context=context??new(Guid.Empty,Guid.Empty);startUtc=lastUtc=clock.GetUtcNow();startMono=lastMono=clock.GetTimestamp();
            handshakeTimer=clock.CreateTimer(_=>{bool expire;lock(sync)expire=!paired&&!closed;if(expire)Gap("HandshakeTimeout");},null,TimeSpan.FromSeconds(20),Timeout.InfiniteTimeSpan);
            epoch=Guid.NewGuid();
        }
        using var cancel=ct.Register(()=>Gap("Canceled"));
        running=Run();startAssigned.TrySetResult();
        try
        {
            while(true)
            {
                OkxStreamObservation? item=null;bool finished;
                lock(sync)
                {
                    if(queue.TryDequeue(out var entry)){used-=entry.Charge;item=entry.Event;}
                    else if(gapEvent is not null){item=gapEvent;gapEvent=null;}
                    else if(closedEvent is not null){item=closedEvent;closedEvent=null;}
                    finished=closed&&item is null;
                }
                if(item is not null){yield return item;continue;}
                if(finished)break;
                await signal.Reader.ReadAsync().ConfigureAwait(false);
            }
            await running.ConfigureAwait(false); // programming/strict peer errors remain observable
        }
        finally
        {
            try {Gap(disposed?"Disposed":"Canceled");await running.ConfigureAwait(false);}
            finally {stopping.Dispose();lock(sync){queue.Clear();used=0;gapEvent=null;closedEvent=null;}}
        }
    }
    private OkxStreamObservation Event(OkxStreamObservationKind kind,Lane? lane=null,string? digest=null,string? failure=null,ImmutableArray<OkxExecutionOrderUpdateDto> orders=default,ImmutableArray<OkxExecutionAlgoUpdateDto> algos=default)=>new(kind,scope,context!,epoch!.Value,++sequence,lane?.Kind,lane?.ConnId,lane is null?null:lane.Kind==OkxStreamLane.PrivateOrders?"orders":"orders-algo",clock.GetUtcNow(),clock.GetTimestamp(),digest,failure,orders.IsDefault?[]:orders,algos.IsDefault?[]:algos);
    private void Signal()=>signal.Writer.TryWrite(true);
    private bool Enqueue(OkxStreamObservation item,int charge)
    {
        // sync is held; never await the reader and never drop old private evidence.
        if(queue.Count>=QueueCap||charge>ByteCap-used)return false;
        queue.Enqueue((item,charge));used+=charge;Signal();return true;
    }
    private void Publish(OkxStreamObservationKind kind,Lane? lane=null)
    {bool overflow;lock(sync){if(gapped)return;overflow=!Enqueue(Event(kind,lane),0);}if(overflow)Gap("BufferOverflow");}
    private void Gap(string reason)
    {
        bool notify;
        lock(sync)
        {
            notify=!gapped; if(!notify)return;
            gapped=true;paired=false;error=reason;
            if(context is not null&&epoch is not null)gapEvent=Event(OkxStreamObservationKind.Gap,failure:reason);
            Signal();
        }
        try {if(context is not null&&epoch is not null)fence.OnGap(scope,context,epoch.Value,reason);}
        catch(Exception e){lock(sync)callbackFailure??=System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e);}
        finally {try{stopping.Cancel();}finally{gapNotified.TrySetResult();}}
    }
    private string? Check(DateTimeOffset? phaseUtc=null,long phaseMono=0,TimeSpan? budget=null)
    {
        string? ClockAdmission()
        {
            lock(sync)
            {
                if(disposed)return "Disposed";if(gapped||stopping.IsCancellationRequested)return error??"Canceled";
                var now=clock.GetUtcNow();var mono=clock.GetTimestamp();
                if(now<lastUtc||mono<lastMono)return "ClockRollback";
                lastUtc=now;lastMono=mono;
                if(!paired&&(now-startUtc>=TimeSpan.FromSeconds(20)||clock.GetElapsedTime(startMono,mono)>=TimeSpan.FromSeconds(20)))return "HandshakeTimeout";
                if(phaseUtc is not null&&(now-phaseUtc>=budget!.Value||clock.GetElapsedTime(phaseMono,mono)>=budget.Value))return "PhaseTimeout";
                return null;
            }
        }
        var denied=ClockAdmission();if(denied is not null)return denied;
        if(ExecutionDiagnostics.Unsafe)return "UnsafeHttpDiagnostics";
        if(context is null||context.ConnectionId==Guid.Empty||context.CaptureId==Guid.Empty||!fence.CanObserve(scope,context,clock.GetUtcNow()))return "ReadFenceDenied";
        // A fence/provider can advance either independent clock. Never renew the original deadline.
        return ClockAdmission();
    }
    private void Require(DateTimeOffset? phaseUtc=null,long phaseMono=0,TimeSpan? budget=null)
    {var denied=Check(phaseUtc,phaseMono,budget);if(denied is not null){Gap(denied);throw new OperationCanceledException(stopping.Token);}}
    private async Task Run()
    {
        bool slot=false;string key=scope.Environment+":"+scope.Uid;
        try
        {
            Require();lock(RegistrySync)slot=Active.Add(key);
            if(!slot){Gap("StreamAlreadyActive");return;}
            await Task.WhenAll(lanes.Select(Work)).ConfigureAwait(false);
        }
        catch(OperationCanceledException) when(stopping.IsCancellationRequested||Volatile.Read(ref gapped)||Volatile.Read(ref disposed)) { }
        finally
        {
            try{Gap(disposed?"Disposed":"TransportClosed");}
            finally
            {
                await gapNotified.Task.ConfigureAwait(false);
                foreach(var lane in lanes)
                {
                    lane.Socket?.Abort();lane.Socket?.Dispose();CryptographicOperations.ZeroMemory(lane.Login);CryptographicOperations.ZeroMemory(lane.Message);
                }
                handshakeTimer?.Dispose();production?.Dispose();if(slot)lock(RegistrySync)Active.Remove(key);
                lock(sync){closed=true;paired=false;closedEvent=Event(OkxStreamObservationKind.Closed,failure:error);Signal();}
                callbackFailure?.Throw();
            }
        }
    }
    private async Task Work(Lane lane)
    {
        Task? heartbeat=null;
        try
        {
            Publish(OkxStreamObservationKind.Connecting,lane);
            await ConnectGate.WaitAsync(stopping.Token).ConfigureAwait(false);
            try
            {
                Require();
                while(true)
                {
                    var wait=nextConnect-clock.GetUtcNow();
                    if(ReferenceEquals(nextClock,clock)){var m=clock.GetElapsedTime(clock.GetTimestamp(),nextMono);if(m>wait)wait=m;}
                    if(wait<=TimeSpan.Zero)break;
                    // Timers may wake early. Wait again against the same original budget; this is not a connection retry.
                    await Task.Delay(wait<TimeSpan.FromMilliseconds(1)?TimeSpan.FromMilliseconds(1):wait,clock,stopping.Token).ConfigureAwait(false);Require();
                }
                Require();
                var utc=clock.GetUtcNow();var mono=clock.GetTimestamp();
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5),clock);using var linked=CancellationTokenSource.CreateLinkedTokenSource(stopping.Token,timeout.Token);
                var uri=new Uri("wss://"+(scope.Environment=="Demo"?"wseeapap.okx.com":"wseea.okx.com")+"/ws/v5/"+(lane.Kind==OkxStreamLane.PrivateOrders?"private":"business"));
                Require(utc,mono,TimeSpan.FromSeconds(5));Task<WebSocket> invocation;
                lock(sync)
                {
                    if(gapped||disposed||linked.IsCancellationRequested)throw new OperationCanceledException(linked.Token);
                    nextConnect=clock.GetUtcNow()+TimeSpan.FromSeconds(1);nextClock=clock;nextMono=checked(clock.GetTimestamp()+clock.TimestampFrequency);
                    invocation=connect(uri,linked.Token);
                }
                lane.Socket=await invocation.ConfigureAwait(false);Require(utc,mono,TimeSpan.FromSeconds(5));
            }
            finally {ConnectGate.Release();}
            Publish(OkxStreamObservationKind.Connected,lane);
            var loginUtc=clock.GetUtcNow();var loginMono=clock.GetTimestamp();
            using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5),clock))
            using(var linked=CancellationTokenSource.CreateLinkedTokenSource(stopping.Token,timeout.Token))
            {
                OkxSigningMaterial? material;
                try{material=await provider.OpenAsync(linked.Token).ConfigureAwait(false);}catch(OperationCanceledException){throw;}catch{Gap("CredentialUnavailable");return;}
                using(material)
                {
                    Require(loginUtc,loginMono,TimeSpan.FromSeconds(5));
                    if(material is null||material.Scope!=scope||material.Fingerprint!=fingerprint||material.Provenance!=(scope.Environment=="Demo"?"DemoTradingApi":"LiveAccountApi")){Gap("CredentialBindingMismatch");return;}
                    await Send(lane,null,material,loginUtc,loginMono,linked.Token).ConfigureAwait(false);
                }
                await Receive(lane,loginUtc,loginMono,linked.Token).ConfigureAwait(false);
            }
            var subUtc=clock.GetUtcNow();var subMono=clock.GetTimestamp();
            using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5),clock))
            using(var linked=CancellationTokenSource.CreateLinkedTokenSource(stopping.Token,timeout.Token))
            {
                var subscription=lane.Kind==OkxStreamLane.PrivateOrders?"{\"id\":\"orders1\",\"op\":\"subscribe\",\"args\":[{\"channel\":\"orders\",\"instType\":\"SPOT\"}]}":"{\"id\":\"algos1\",\"op\":\"subscribe\",\"args\":[{\"channel\":\"orders-algo\",\"instType\":\"SPOT\"}]}";
                await Send(lane,subscription,null,subUtc,subMono,linked.Token).ConfigureAwait(false);
                await Receive(lane,subUtc,subMono,linked.Token).ConfigureAwait(false);
            }
            lock(sync){lane.IdleUtc=clock.GetUtcNow();lane.IdleMono=clock.GetTimestamp();}
            heartbeat=Heartbeat(lane);
            while(!stopping.IsCancellationRequested)await Receive(lane,null,0,stopping.Token).ConfigureAwait(false);
        }
        catch(OperationCanceledException){Gap(disposed?"Disposed":stopping.IsCancellationRequested?(error??"Canceled"):"PhaseTimeout");}
        catch(Exception e) when(e is WebSocketException or IOException){Gap("TransportFailure");}
        catch(InvalidStreamPayload){Gap("InvalidPayload");}
        catch {Gap("CaptureFailure");throw;}
        finally
        {
            // Workers are canceled pair-wide first; bounded close-output is best effort, never a reconnect.
            try
            {
                if(lane.Socket is not null&&lane.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var close=new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    bool held=false;
                    try{await lane.SendGate.WaitAsync(close.Token).ConfigureAwait(false);held=true;await lane.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure,"",close.Token).ConfigureAwait(false);}catch(Exception e) when(e is OperationCanceledException or WebSocketException or IOException){}finally{if(held)lane.SendGate.Release();}
                }
            }
            finally {lane.Socket?.Abort();if(heartbeat is not null)await heartbeat.ConfigureAwait(false);CryptographicOperations.ZeroMemory(lane.Login);CryptographicOperations.ZeroMemory(lane.Message);}
        }
    }
    private async Task Send(Lane lane,string? text,OkxSigningMaterial? material,DateTimeOffset phaseUtc,long phaseMono,CancellationToken parent)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5),clock);using var linked=CancellationTokenSource.CreateLinkedTokenSource(parent,timeout.Token);bool held=false;
        var sendUtc=clock.GetUtcNow();var sendMono=clock.GetTimestamp();
        try
        {
            await lane.SendGate.WaitAsync(linked.Token).ConfigureAwait(false);held=true;Require(phaseUtc,phaseMono,TimeSpan.FromSeconds(5));
            int count=material is not null?material.WriteObservationLogin(lane.Login,clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)):Encoding.ASCII.GetBytes(text!,lane.Login);
            Require(phaseUtc,phaseMono,TimeSpan.FromSeconds(5));Require(sendUtc,sendMono,TimeSpan.FromSeconds(5));
            Task sending;
            lock(sync)
            {
                if(gapped||disposed||linked.IsCancellationRequested)throw new OperationCanceledException(linked.Token);
                sending=lane.Socket!.SendAsync(new ArraySegment<byte>(lane.Login,0,count),WebSocketMessageType.Text,true,linked.Token);
            }
            await sending.ConfigureAwait(false);Require(phaseUtc,phaseMono,TimeSpan.FromSeconds(5));
        }
        finally {CryptographicOperations.ZeroMemory(lane.Login);if(held)lane.SendGate.Release();}
    }
    private sealed class InvalidStreamPayload : Exception { }
    private OkxObservationStreamJson.Message ParseMessage(Lane lane,int used)
    {
        try{return OkxObservationStreamJson.Parse(lane.Message.AsMemory(0,used),lane.Kind,scope.Uid,lane.ConnId,lane.Phase);}
        catch(Exception e) when(e is JsonException or OkxObservationJson.CardinalityException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or DecoderFallbackException){throw new InvalidStreamPayload();}
    }
    private async Task Receive(Lane lane,DateTimeOffset? phaseUtc,long phaseMono,CancellationToken ct)
    {
        int used=0,fragments=0;DateTimeOffset? firstUtc=null;long firstMono=0;CancellationTokenSource? assembly=null,linked=null;
        try
        {
            while(true)
            {
                var r=await lane.Socket!.ReceiveAsync(new ArraySegment<byte>(lane.Message,used,lane.Message.Length-used),linked?.Token??ct).ConfigureAwait(false);
                if(firstUtc is null){firstUtc=clock.GetUtcNow();firstMono=clock.GetTimestamp();assembly=new(TimeSpan.FromSeconds(5),clock);linked=CancellationTokenSource.CreateLinkedTokenSource(ct,assembly.Token);}
                Require(firstUtc,firstMono,TimeSpan.FromSeconds(5));if(phaseUtc is not null)Require(phaseUtc,phaseMono,TimeSpan.FromSeconds(5));
                if(r.MessageType==WebSocketMessageType.Close){Gap("PeerClosed");return;}
                if(r.MessageType!=WebSocketMessageType.Text)throw new InvalidStreamPayload();
                used+=r.Count;if(++fragments>1024||used>MessageCap)throw new InvalidStreamPayload();
                if(!r.EndOfMessage)continue;
                Require();bool overflow=false;bool pongExpired;
                lock(sync)pongExpired=lane.PingPending&&(clock.GetUtcNow()-lane.PingUtc>=TimeSpan.FromSeconds(5)||clock.GetElapsedTime(lane.PingMono,clock.GetTimestamp())>=TimeSpan.FromSeconds(5));
                if(pongExpired){Gap("HeartbeatTimeout");return;}
                lock(sync)
                {
                    if(gapped||disposed)return;
                    if(used==4&&lane.Message.AsSpan(0,4).SequenceEqual("pong"u8))
                    {if(!lane.PingPending)throw new InvalidStreamPayload();lane.PingPending=false;lane.IdleUtc=clock.GetUtcNow();lane.IdleMono=clock.GetTimestamp();return;}
                    var parsed=ParseMessage(lane,used);
                    if(parsed.Event=="login"){lane.ConnId=parsed.ConnId;lane.Phase=1;overflow=!Enqueue(Event(OkxStreamObservationKind.LoginAccepted,lane,parsed.Digest),0);}
                    else if(parsed.Event=="subscribe")
                    {
                        lane.Phase=2;overflow=!Enqueue(Event(OkxStreamObservationKind.SubscriptionAccepted,lane,parsed.Digest),0);
                        if(!overflow&&lanes.All(x=>x.Phase==2)){paired=true;handshakeTimer?.Dispose();overflow=!Enqueue(Event(OkxStreamObservationKind.Buffering),0);}
                    }
                    else if(parsed.Event=="data")overflow=!Enqueue(Event(lane.Kind==OkxStreamLane.PrivateOrders?OkxStreamObservationKind.OrdersUpdate:OkxStreamObservationKind.AlgoUpdate,lane,parsed.Digest,orders:parsed.Orders,algos:parsed.Algos),used);
                    if(!lane.PingPending){lane.IdleUtc=clock.GetUtcNow();lane.IdleMono=clock.GetTimestamp();}
                }
                if(overflow)Gap("BufferOverflow");return;
            }
        }
        finally {CryptographicOperations.ZeroMemory(lane.Message);linked?.Dispose();assembly?.Dispose();}
    }
    private async Task Heartbeat(Lane lane)
    {
        try
        {
            while(!stopping.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250),clock,stopping.Token).ConfigureAwait(false);Require();bool ping=false,expired=false;DateTimeOffset now;long mono;
                lock(sync)
                {
                    now=clock.GetUtcNow();mono=clock.GetTimestamp();
                    if(lane.PingPending)expired=now-lane.PingUtc>=TimeSpan.FromSeconds(5)||clock.GetElapsedTime(lane.PingMono,mono)>=TimeSpan.FromSeconds(5);
                    else if(now-lane.IdleUtc>=TimeSpan.FromSeconds(15)||clock.GetElapsedTime(lane.IdleMono,mono)>=TimeSpan.FromSeconds(15)){ping=true;lane.PingPending=true;lane.PingUtc=now;lane.PingMono=mono;}
                }
                if(expired){Gap("HeartbeatTimeout");return;}
                if(ping)await Send(lane,"ping",null,now,mono,stopping.Token).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException) when(stopping.IsCancellationRequested){}
        catch(OperationCanceledException){Gap("HeartbeatTimeout");}
        catch(Exception e) when(e is IOException or WebSocketException){Gap("TransportFailure");}
    }
    public ValueTask DisposeAsync()
    {
        Task task;
        lock(sync){disposed=true;task=disposal??=Drain();}
        Gap("Disposed");return new(task);
    }
    private async Task Drain()
    {
        // Yield prevents invoking callbacks under the caller's lifetime monitor.
        await Task.Yield();
        try
        {
            Gap("Disposed");if(Volatile.Read(ref enumerated)!=0)await startAssigned.Task.ConfigureAwait(false);var task=running;if(task is not null)
            {
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try{await task.WaitAsync(timeout.Token).ConfigureAwait(false);}
                catch(OperationCanceledException){foreach(var lane in lanes)lane.Socket?.Abort();await task.ConfigureAwait(false);}
            }
            else {production?.Dispose();foreach(var lane in lanes){CryptographicOperations.ZeroMemory(lane.Login);CryptographicOperations.ZeroMemory(lane.Message);}lock(sync)closed=true;}
        }
        finally {stopping.Dispose();lock(sync){queue.Clear();used=0;gapEvent=null;closedEvent=null;}}
    }
    private sealed class ProductionSockets : IDisposable
    {
        private readonly HttpMessageInvoker invoker=new(new SocketsHttpHandler{AllowAutoRedirect=false,UseProxy=false,UseCookies=false,AutomaticDecompression=DecompressionMethods.None,ActivityHeadersPropagator=null,ConnectTimeout=TimeSpan.FromSeconds(5)},true);
        internal async Task<WebSocket> Connect(Uri uri,CancellationToken ct)
        {
            var ws=new ClientWebSocket();ws.Options.KeepAliveInterval=Timeout.InfiniteTimeSpan;ws.Options.HttpVersion=HttpVersion.Version11;ws.Options.HttpVersionPolicy=HttpVersionPolicy.RequestVersionExact;
            try{await ws.ConnectAsync(uri,invoker,ct).ConfigureAwait(false);return ws;}catch{ws.Dispose();throw;}
        }
        public void Dispose()=>invoker.Dispose();
    }
}
