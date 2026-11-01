using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Apex.Adapters.Okx.Execution;
using Xunit;
namespace Apex.Adapters.Okx.Tests;

// Real paired BCL framing over bounded owned bytes, no listener/TLS/HTTP upgrade claim.
internal sealed class OkxExecutionWebSocketFixture : IAsyncDisposable,IOkxSigningMaterialProvider,IOkxObservationStreamFence
{
    internal static DateTimeOffset LastPeerConnectUtc;
    internal readonly TimeProvider Clock;
    internal readonly OkxExecutionScope Scope;
    internal readonly OkxObservationContext Context=new(Guid.NewGuid(),Guid.NewGuid());
    internal readonly List<Uri> Connects=[];
    internal readonly List<string> Logins=[],Subscriptions=[];
    internal readonly Dictionary<OkxStreamLane,Stream> Wires=[];
    internal readonly Dictionary<OkxStreamLane,WebSocket> Peers=[];
    internal readonly List<Task> Scripts=[];
    internal readonly CancellationTokenSource Closing=new();
    internal readonly TaskCompletionSource BothSubscribed=new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource Gap=new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly List<string> Gaps=[];
    internal int Opens,Pings,ActiveSockets;
    internal bool Allowed=true,AutoPong=true;
    internal Action? OnOpen;
    internal Action? OnFence,GapAction;
    internal Func<ArraySegment<byte>,Task?>? OnSend;
    internal byte[] KeyBytes=Encoding.ASCII.GetBytes(OkxExecutionObservationHttpFixture.Key),PassBytes=Encoding.ASCII.GetBytes(OkxExecutionObservationHttpFixture.Pass),SecretBytes=Encoding.ASCII.GetBytes(OkxExecutionObservationHttpFixture.Secret);
    internal Func<CancellationToken,ValueTask<OkxSigningMaterial>>? OpenOverride;
    internal Func<string,CancellationToken,Task>? BeforeReply;
    internal bool BlockSend;
    internal readonly TaskCompletionSource SendEntered=new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Exception? ProviderError;
    internal Func<byte[],Task>? InspectLogin;
    internal readonly List<OkxSigningMaterial> Materials=[];
    internal string Fingerprint=>Convert.ToHexString(SHA256.HashData(KeyBytes)).ToLowerInvariant();
    internal OkxExecutionWebSocketFixture(string environment="Demo",TimeProvider? clock=null,OkxExecutionScope? scope=null,OkxObservationContext? context=null)
    {Clock=clock??TimeProvider.System;var id=OkxExecutionObservationHttpFixture.NextUid();Scope=scope??new(environment,id,id,"USD","stream");if(context is not null)Context=context;}
    internal OkxObservationStream Stream()=>OkxObservationStream.ForTests(Connect,Clock,Scope,this,Fingerprint,this);
    public ValueTask<OkxSigningMaterial> OpenAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref Opens);OnOpen?.Invoke();if(OpenOverride is not null)return OpenOverride(ct);if(ProviderError is not null)throw ProviderError;var m=new OkxSigningMaterial(Scope,Scope.Environment=="Demo"?"DemoTradingApi":"LiveAccountApi",KeyBytes,SecretBytes,PassBytes);lock(Materials)Materials.Add(m);return ValueTask.FromResult(m);
    }
    public bool CanObserve(OkxExecutionScope scope,OkxObservationContext context,DateTimeOffset utc){OnFence?.Invoke();return Allowed&&scope==Scope&&context==Context;}
    public void OnGap(OkxExecutionScope scope,OkxObservationContext context,Guid epoch,string reason)
    {lock(Gaps)Gaps.Add(reason);Allowed=false;Gap.TrySetResult();GapAction?.Invoke();}
    internal Task<WebSocket> Connect(Uri uri,CancellationToken ct)
    {
        Assert.Equal("wss",uri.Scheme);Assert.Equal(Scope.Environment=="Demo"?"wseeapap.okx.com":"wseea.okx.com",uri.Host);Assert.Equal(443,uri.Port);Assert.Empty(uri.Query);Assert.Empty(uri.Fragment);Assert.Empty(uri.UserInfo);
        var lane=uri.AbsolutePath switch {"/ws/v5/private"=>OkxStreamLane.PrivateOrders,"/ws/v5/business"=>OkxStreamLane.BusinessAlgos,_=>throw new Xunit.Sdk.XunitException("Unexpected path")};
        lock(Connects){Assert.True(Connects.Count<2);Assert.DoesNotContain(Connects,u=>u.AbsolutePath==uri.AbsolutePath);Connects.Add(uri);}
        LastPeerConnectUtc=Clock.GetUtcNow();
        var (clientStream,serverStream)=Duplex.Pair();var client=WebSocket.CreateFromStream(clientStream,false,null,Timeout.InfiniteTimeSpan);var server=WebSocket.CreateFromStream(serverStream,true,null,Timeout.InfiniteTimeSpan);
        lock(Wires)Wires.Add(lane,serverStream);
        Interlocked.Add(ref ActiveSockets,2);lock(Peers)Peers.Add(lane,server);lock(Scripts)Scripts.Add(Script(server,lane));
        return Task.FromResult<WebSocket>(new CountedSocket(client,()=>Interlocked.Decrement(ref ActiveSockets),(b,token)=>{if(BlockSend){SendEntered.TrySetResult();return Task.Delay(Timeout.Infinite,token);}return OnSend?.Invoke(b);}));
    }
    private async Task Script(WebSocket peer,OkxStreamLane lane)
    {
        bool handshakeComplete=false;
        try
        {
            var bytes=await Receive(peer,Closing.Token);var text=Encoding.UTF8.GetString(bytes);lock(Logins)Logins.Add(text);
            var login=JsonNode.Parse(bytes)!.AsObject();Assert.Equal(new[]{"op","args"},login.Select(x=>x.Key));Assert.Equal("login",login["op"]!.GetValue<string>());var a=Assert.Single(login["args"]!.AsArray())!.AsObject();
            Assert.Equal(new[]{"apiKey","passphrase","timestamp","sign"},a.Select(x=>x.Key));Assert.Equal(Encoding.ASCII.GetString(KeyBytes),a["apiKey"]!.GetValue<string>());Assert.Equal(Encoding.ASCII.GetString(PassBytes),a["passphrase"]!.GetValue<string>());
            var seconds=a["timestamp"]!.GetValue<string>();Assert.Matches("^[0-9]{1,12}$",seconds);Assert.InRange(Clock.GetUtcNow().ToUnixTimeSeconds()-long.Parse(seconds,System.Globalization.CultureInfo.InvariantCulture),0,5);
            var hmac=Convert.ToBase64String(HMACSHA256.HashData(SecretBytes,Encoding.ASCII.GetBytes(seconds+"GET/users/self/verify")));Assert.Equal(hmac,a["sign"]!.GetValue<string>());
            if(InspectLogin is not null)await InspectLogin(bytes);CryptographicOperations.ZeroMemory(bytes);
            if(BeforeReply is not null)await BeforeReply("login",Closing.Token);
            string conn=lane==OkxStreamLane.PrivateOrders?"private-conn":"business-conn";
            await Send(peer,"{\"event\":\"login\",\"code\":\"0\",\"msg\":\"\",\"connId\":\""+conn+"\"}",Closing.Token);
            var sub=Encoding.UTF8.GetString(await Receive(peer,Closing.Token));lock(Subscriptions)Subscriptions.Add(sub);
            string channel=lane==OkxStreamLane.PrivateOrders?"orders":"orders-algo",id=lane==OkxStreamLane.PrivateOrders?"orders1":"algos1";
            Assert.Equal("{\"id\":\""+id+"\",\"op\":\"subscribe\",\"args\":[{\"channel\":\""+channel+"\",\"instType\":\"SPOT\"}]}",sub);
            if(BeforeReply is not null)await BeforeReply("subscribe",Closing.Token);
            await Send(peer,"{\"id\":\""+id+"\",\"event\":\"subscribe\",\"arg\":{\"channel\":\""+channel+"\",\"instType\":\"SPOT\"},\"connId\":\""+conn+"\"}",Closing.Token);
            handshakeComplete=true;
            lock(Subscriptions)if(Subscriptions.Count==2)BothSubscribed.TrySetResult();
            while(!Closing.IsCancellationRequested)
            {
                var message=Encoding.UTF8.GetString(await Receive(peer,Closing.Token));Assert.Equal("ping",message);Interlocked.Increment(ref Pings);if(AutoPong)await Send(peer,"pong",Closing.Token);
            }
        }
        catch(OperationCanceledException) when(Closing.IsCancellationRequested) { }
        catch(WebSocketException) when(Closing.IsCancellationRequested||!Allowed||handshakeComplete) { }
        catch(EndOfStreamException) when(Closing.IsCancellationRequested||!Allowed||handshakeComplete) { }
        finally {peer.Dispose();Interlocked.Decrement(ref ActiveSockets);}
    }
    internal static async Task<byte[]> Receive(WebSocket ws,CancellationToken ct)
    {
        var b=new byte[65536];int used=0;WebSocketReceiveResult r;
        do{r=await ws.ReceiveAsync(new ArraySegment<byte>(b,used,b.Length-used),ct);if(r.MessageType==WebSocketMessageType.Close)throw new EndOfStreamException();Assert.Equal(WebSocketMessageType.Text,r.MessageType);used+=r.Count;}while(!r.EndOfMessage);
        var result=b.AsSpan(0,used).ToArray();CryptographicOperations.ZeroMemory(b);return result;
    }
    internal static Task Send(WebSocket ws,string text,CancellationToken ct)=>ws.SendAsync(Encoding.UTF8.GetBytes(text),WebSocketMessageType.Text,true,ct);
    internal Task Push(OkxStreamLane lane,string text)=>Send(Peers[lane],text,Closing.Token);
    internal async Task RawFrame(OkxStreamLane lane,byte opcode,bool final,ReadOnlyMemory<byte> payload)
    {
        Assert.InRange(payload.Length,0,1048577);var frame=new byte[payload.Length+10];int header;
        frame[0]=(byte)((final?128:0)|opcode);
        if(payload.Length<126){frame[1]=(byte)payload.Length;header=2;}
        else if(payload.Length<=65535){frame[1]=126;System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2),(ushort)payload.Length);header=4;}
        else{frame[1]=127;System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(2),(ulong)payload.Length);header=10;}
        payload.CopyTo(frame.AsMemory(header));try{await Wires[lane].WriteAsync(frame.AsMemory(0,header+payload.Length),Closing.Token);}finally{CryptographicOperations.ZeroMemory(frame);}
    }
    internal async Task PushBytes(OkxStreamLane lane,byte[] bytes,WebSocketMessageType type=WebSocketMessageType.Text,int chunk=65536)
    {
        var peer=Peers[lane];if(bytes.Length==0){await peer.SendAsync(Array.Empty<byte>(),type,true,Closing.Token);return;}
        for(int at=0;at<bytes.Length;){int count=Math.Min(chunk,bytes.Length-at);await peer.SendAsync(new ArraySegment<byte>(bytes,at,count),type,at+count==bytes.Length,Closing.Token);at+=count;}
    }
    internal JsonNode Update(string name,bool original=false)
    {
        var body=OkxExecutionAlgoObservationTests.Control(name);
        if(!original){body["arg"]!["uid"]=Scope.Uid;body["arg"]!["instType"]="SPOT";body["arg"]!["instId"]=body["data"]![0]!["instId"]!.GetValue<string>();body["arg"]!.AsObject().Remove("instFamily");}
        return body;
    }
    public async ValueTask DisposeAsync()
    {
        Closing.Cancel();lock(Peers)foreach(var peer in Peers.Values)peer.Abort();Task[] scripts;lock(Scripts)scripts=Scripts.ToArray();await Task.WhenAll(scripts).WaitAsync(TimeSpan.FromSeconds(5));Closing.Dispose();
        Assert.Equal(0,ActiveSockets);foreach(var m in Materials){Assert.All(m.Key,b=>Assert.Equal(0,b));Assert.All(m.Passphrase,b=>Assert.Equal(0,b));Assert.All(m.Secret,b=>Assert.Equal(0,b));}
    }
    private sealed class CountedSocket(WebSocket inner,Action disposed,Func<ArraySegment<byte>,CancellationToken,Task?> onSend):WebSocket
    {
        private int done;
        public override WebSocketCloseStatus? CloseStatus=>inner.CloseStatus;public override string? CloseStatusDescription=>inner.CloseStatusDescription;public override WebSocketState State=>inner.State;public override string? SubProtocol=>inner.SubProtocol;
        public override void Abort()=>inner.Abort();public override Task CloseAsync(WebSocketCloseStatus s,string? d,CancellationToken ct)=>inner.CloseAsync(s,d,ct);public override Task CloseOutputAsync(WebSocketCloseStatus s,string? d,CancellationToken ct)=>inner.CloseOutputAsync(s,d,ct);
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b,CancellationToken ct)=>inner.ReceiveAsync(b,ct);public override Task SendAsync(ArraySegment<byte> b,WebSocketMessageType t,bool e,CancellationToken ct)=>onSend(b,ct)??inner.SendAsync(b,t,e,ct);
        public override void Dispose(){if(Interlocked.Exchange(ref done,1)==0){inner.Dispose();disposed();}}
    }
    private sealed class Duplex(Channel<byte[]> input,Channel<byte[]> output):Stream
    {
        private byte[]? current;private int offset;private bool disposed;
        internal static (Stream,Stream) Pair(){Channel<byte[]> Make()=>Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4){FullMode=BoundedChannelFullMode.Wait});var a=Make();var b=Make();return(new Duplex(a,b),new Duplex(b,a));}
        public override bool CanRead=>true;public override bool CanWrite=>true;public override bool CanSeek=>false;public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override async ValueTask<int> ReadAsync(Memory<byte> b,CancellationToken ct=default)
        {if(current is null){try{current=await input.Reader.ReadAsync(ct);}catch(ChannelClosedException){return 0;}offset=0;}int n=Math.Min(b.Length,current.Length-offset);current.AsMemory(offset,n).CopyTo(b);offset+=n;if(offset==current.Length){CryptographicOperations.ZeroMemory(current);current=null;}return n;}
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> b,CancellationToken ct=default)
        {while(!b.IsEmpty){int n=Math.Min(65536,b.Length);var copy=b[..n].ToArray();try{await output.Writer.WriteAsync(copy,ct);}catch{CryptographicOperations.ZeroMemory(copy);throw;}b=b[n..];}}
        public override Task<int> ReadAsync(byte[] b,int o,int n,CancellationToken ct)=>ReadAsync(b.AsMemory(o,n),ct).AsTask();public override Task WriteAsync(byte[] b,int o,int n,CancellationToken ct)=>WriteAsync(b.AsMemory(o,n),ct).AsTask();
        protected override void Dispose(bool disposing){if(disposed)return;disposed=true;output.Writer.TryComplete();input.Writer.TryComplete();if(current is not null)CryptographicOperations.ZeroMemory(current);while(input.Reader.TryRead(out var b))CryptographicOperations.ZeroMemory(b);base.Dispose(disposing);}
        public override void Flush(){}public override int Read(byte[] b,int o,int n)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int n)=>throw new NotSupportedException();public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();public override void SetLength(long l)=>throw new NotSupportedException();
    }
}

// Explicit timers; timestamp-only overrides do not control Task.Delay/CTS.
internal sealed class StreamTestClock : TimeProvider
{
    private readonly object sync=new();private DateTimeOffset utc=DateTimeOffset.UtcNow;private long mono;private readonly List<Timer> timers=[];
    internal StreamTestClock(){var earliest=OkxExecutionWebSocketFixture.LastPeerConnectUtc+TimeSpan.FromSeconds(1);if(earliest>utc)utc=earliest;}
    public override DateTimeOffset GetUtcNow(){lock(sync)return utc;}public override long GetTimestamp(){lock(sync)return mono;}public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
    internal int TimerCount{get{lock(sync)return timers.Count(t=>!t.Disposed);}}
    public override ITimer CreateTimer(TimerCallback cb,object? state,TimeSpan due,TimeSpan period){lock(sync){var t=new Timer(this,cb,state);timers.Add(t);t.Change(due,period);return t;}}
    internal void Advance(TimeSpan delta, bool wall=true,bool monotonic=true)
    {
        List<(TimerCallback,object?)> fire=[];lock(sync){if(wall)utc+=delta;if(monotonic)mono+=delta.Ticks;foreach(var t in timers.ToArray())if(!t.Disposed&&t.Due<=mono){fire.Add((t.Callback,t.State));t.Due=t.Period==Timeout.InfiniteTimeSpan?long.MaxValue:mono+t.Period.Ticks;}}
        foreach(var (cb,s) in fire)cb(s);
    }
    private sealed class Timer(StreamTestClock clock,TimerCallback cb,object? state):ITimer
    {
        internal bool Disposed;internal long Due=long.MaxValue;internal TimeSpan Period;internal TimerCallback Callback=cb;internal object? State=state;
        public bool Change(TimeSpan due,TimeSpan period){lock(clock.sync){if(Disposed)return false;Due=due==Timeout.InfiniteTimeSpan?long.MaxValue:clock.mono+due.Ticks;Period=period;return true;}}
        public void Dispose(){lock(clock.sync){Disposed=true;clock.timers.Remove(this);}}public ValueTask DisposeAsync(){Dispose();return ValueTask.CompletedTask;}
    }
}
