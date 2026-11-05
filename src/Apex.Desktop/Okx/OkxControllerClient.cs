using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
namespace Apex.Desktop.Okx;

internal interface IOkxControllerFrameTransport:IAsyncDisposable
{
    Task<string?> ReadAsync(int cap,CancellationToken cancellation);
    Task WriteAsync(string line,CancellationToken cancellation);
}
// Owns only controller stdin/stdout. No broker/session-disconnect command is emitted on disposal.
internal sealed class OkxControllerClientFrames:IOkxControllerFrameTransport
{
    private readonly Stream input,output;
    private readonly byte[] buffer=new byte[4096];
    private int position,length,disposed;
    private readonly SemaphoreSlim writes=new(1,1);
    internal OkxControllerClientFrames(Stream input,Stream output){this.input=input;this.output=output;}
    public async Task<string?> ReadAsync(int cap,CancellationToken cancellation)
    {
        if(cap<1||cap>OkxControllerProtocol.ReceiptLimit)throw new ArgumentOutOfRangeException(nameof(cap));
        byte[] frame=new byte[cap];int count=0;
        while(true)
        {
            if(position==length){length=await input.ReadAsync(buffer,cancellation);position=0;if(length==0){if(count==0)return null;throw new InvalidDataException("FRAME_TRUNCATED");}}
            byte b=buffer[position++];if(b==10)return new UTF8Encoding(false,true).GetString(frame,0,count);
            if(b==13||count==cap)throw new InvalidDataException("FRAME_REFUSED");frame[count++]=b;
        }
    }
    public async Task WriteAsync(string line,CancellationToken cancellation)
    {
        if(line==null||line.IndexOfAny(new[]{'\r','\n'})>=0||Encoding.UTF8.GetByteCount(line)>OkxControllerProtocol.ReceiptLimit)throw new InvalidDataException("FRAME_REFUSED");
        await writes.WaitAsync(cancellation);
        try{byte[] bytes=new UTF8Encoding(false,true).GetBytes(line+"\n");await output.WriteAsync(bytes,cancellation);await output.FlushAsync(cancellation);}
        finally{writes.Release();}
    }
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        try{await input.DisposeAsync();}finally{if(!ReferenceEquals(input,output))await output.DisposeAsync();}
    }
}
internal sealed record OkxControllerClientResult(string Outcome,string Code,OkxControllerReceipt? Receipt);
internal sealed record OkxControllerClientView(string Outcome,string Code,string? Mode,bool? Armed,bool SnapshotStale,OkxControllerSnapshot? Snapshot,bool TransportFresh=false,int Pending=0);

// A portable controller, never an executor. A supplied transport is not native SSH admission.
internal sealed class OkxControllerClient:IAsyncDisposable
{
    internal const int SessionBudget=240,NormalBudget=236,MaxPending=8;
    private readonly object gate=new();
    private readonly SemaphoreSlim lifecycle=new(1,1);
    private readonly TimeSpan timeout,freshness;
    private readonly TimeProvider clock;
    private Connection? current;
    private string? previousSession;
    private bool disposed,stale=true;
    private string outcome="pending-or-unknown",code="NOT_CONNECTED";
    private string? mode;
    private bool? armed;
    private OkxControllerSnapshot? snapshot;
    private sealed class Pending
    {
        internal readonly OkxControllerCommand Command;
        internal readonly int Sequence;
        internal bool Sent;
        internal readonly TaskCompletionSource<OkxControllerClientResult> Done=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Pending(OkxControllerCommand command,int sequence){Command=command;Sequence=sequence;}
    }
    private sealed class Connection
    {
        internal readonly IOkxControllerFrameTransport Transport;
        internal readonly CancellationTokenSource Life=new();
        internal readonly SemaphoreSlim Signal=new(0);
        internal readonly Queue<Pending> Urgent=new(),Normal=new();
        internal readonly Dictionary<string,Pending> Pending=new(StringComparer.Ordinal);
        internal OkxControllerHello? Hello;
        internal Task Reader=Task.CompletedTask,Writer=Task.CompletedTask;
        internal bool Active=true,Ready,PollPending;
        internal int Issued,Applied;
        internal long Received,SnapshotReceived;
        internal OkxControllerReceipt? AppliedReceipt;
        internal Connection(IOkxControllerFrameTransport transport){Transport=transport;}
    }
    internal OkxControllerClient(TimeSpan timeout,TimeSpan freshness,TimeProvider? clock=null)
    {
        if(timeout<TimeSpan.FromMilliseconds(20)||timeout>TimeSpan.FromMinutes(2)||freshness<=TimeSpan.Zero||freshness>TimeSpan.FromMinutes(5))throw new ArgumentOutOfRangeException(nameof(timeout));
        this.timeout=timeout;this.freshness=freshness;this.clock=clock??TimeProvider.System;
    }
    internal OkxControllerClientView View
    {
        get{lock(gate){long now=clock.GetTimestamp();bool fresh=current is {Active:true,Hello:not null} c&&clock.GetElapsedTime(c.Received,now)<=freshness;bool snapshotExpired=current==null||clock.GetElapsedTime(current.SnapshotReceived,now)>freshness;return new(outcome,code,mode,armed,stale||!fresh||snapshotExpired,snapshot,fresh,current?.Pending.Count??0);}}
    }
    // Explicit human reconnect only. Old snapshots remain stale until a new strict SNAPSHOT arrives.
    internal async Task ReconnectAsync(IOkxControllerFrameTransport transport,CancellationToken cancellation=default)
    {
        ArgumentNullException.ThrowIfNull(transport);await lifecycle.WaitAsync(cancellation);
        Connection? c=null;
        try
        {
            if(disposed)throw new ObjectDisposedException(nameof(OkxControllerClient));
            await CloseCurrent("MANUAL_RECONNECT_QUERY_STATE");
            c=new(transport);lock(gate){current=c;stale=true;outcome="pending-or-unknown";code="HELLO_PENDING";}
            using var budget=CancellationTokenSource.CreateLinkedTokenSource(cancellation,c.Life.Token);budget.CancelAfter(timeout);
            var hello=OkxControllerProtocol.DecodeHello(await transport.ReadAsync(OkxControllerProtocol.CommandLimit,budget.Token)??throw new IOException("HELLO_EOF"));
            lock(gate)
            {
                string id=hello.Epoch+hello.Session;if(id==previousSession)throw new InvalidDataException("NEW_SESSION_REQUIRED");previousSession=id;
                c.Hello=hello;c.Received=clock.GetTimestamp();
            }
            c.Reader=ReadLoop(c);c.Writer=WriteLoop(c);
            var status=await Send(c,"STATUS",true,cancellation);
            if(status.Outcome!="confirmed")throw new IOException("AUTHORITATIVE_STATUS_UNAVAILABLE");
            var snap=await Send(c,"SNAPSHOT",true,cancellation);
            if(snap.Outcome!="confirmed"||snap.Receipt?.Snapshot==null)throw new IOException("AUTHORITATIVE_SNAPSHOT_UNAVAILABLE");
            lock(gate){if(c.Active)c.Ready=true;else throw new IOException("SESSION_LOST");}
        }
        catch{if(c!=null){Fail(c,"RECONNECT_REFUSED_QUERY_STATE");await Retire(c);}else await transport.DisposeAsync();throw;}
        finally{lifecycle.Release();}
    }
    internal Task<OkxControllerClientResult> CommandAsync(string action,CancellationToken cancellation=default)
    {
        if(!OkxControllerProtocol.Actions.Contains(action))throw new ArgumentException("CONTROLLER_ACTION_REFUSED",nameof(action));
        lock(gate){if(disposed||current is not {Active:true,Ready:true} c)return Task.FromResult(Local("refused","MANUAL_RECONNECT_REQUIRED"));return Send(c,action,false,cancellation);}
    }
    private static bool Urgent(string action)=>action is "STOP" or "EMERGENCY";
    private static OkxControllerClientResult Local(string result,string reason)=>new(result,reason,null);
    private async Task<OkxControllerClientResult> Send(Connection c,string action,bool bootstrap,CancellationToken cancellation)
    {
        Pending p;
        lock(gate)
        {
            if(cancellation.IsCancellationRequested)return Local("refused","CANCELED_BEFORE_SEND");
            if(!c.Active||current!=c||!bootstrap&&!c.Ready)return Local("refused","MANUAL_RECONNECT_REQUIRED");
            bool urgent=Urgent(action),passive=!OkxControllerProtocol.Mutation(action);
            if(c.Issued>=(urgent?SessionBudget:NormalBudget))return Local("refused","NEW_SESSION_REQUIRED");
            if(passive&&c.PollPending)return Local("refused","POLL_ALREADY_PENDING");
            if(c.Pending.Count>=MaxPending||!urgent&&c.Pending.Values.Count(x=>!Urgent(x.Command.Action))>=MaxPending-2)return Local("refused","CLIENT_QUEUE_FULL");
            if(urgent)
            {
                // A queued START/ARM/etc must not follow a later emergency request.
                foreach(var old in c.Normal.Where(x=>!x.Sent&&OkxControllerProtocol.Mutation(x.Command.Action)).ToArray())
                {c.Pending.Remove(old.Command.CommandId);old.Done.TrySetResult(Local("refused","SUPERSEDED_BEFORE_SEND"));}
            }
            var h=c.Hello!;p=new(new(OkxControllerProtocol.Version,h.Epoch,h.Session,Guid.NewGuid().ToString("N"),action),++c.Issued);
            c.Pending.Add(p.Command.CommandId,p);if(passive)c.PollPending=true;
            (urgent?c.Urgent:c.Normal).Enqueue(p);
            if(OkxControllerProtocol.Mutation(action)){outcome="pending-or-unknown";code="COMMAND_PENDING";stale=true;}
            c.Signal.Release();
        }
        try{return await p.Done.Task.WaitAsync(timeout,clock,cancellation);}
        catch(Exception e) when(e is TimeoutException or OperationCanceledException)
        {
            lock(gate)
            {
                if(p.Done.Task.IsCompletedSuccessfully)return p.Done.Task.Result;
                if(!p.Sent)
                {
                    c.Pending.Remove(p.Command.CommandId);if(!OkxControllerProtocol.Mutation(action))c.PollPending=false;
                    var local=Local("refused","CANCELED_BEFORE_SEND");p.Done.TrySetResult(local);return local;
                }
                Fail(c,"ACKNOWLEDGEMENT_UNKNOWN_NO_REPLAY");
                return p.Done.Task.Result;
            }
        }
    }
    private async Task WriteLoop(Connection c)
    {
        try
        {
            while(true)
            {
                await c.Signal.WaitAsync(c.Life.Token);Pending? p=null;
                lock(gate)
                {
                    if(!c.Active)return;
                    while(c.Urgent.Count+c.Normal.Count>0)
                    {var next=c.Urgent.Count>0?c.Urgent.Dequeue():c.Normal.Dequeue();if(c.Pending.ContainsKey(next.Command.CommandId)){p=next;p.Sent=true;break;}}
                }
                if(p!=null)await c.Transport.WriteAsync(OkxControllerProtocol.EncodeCommand(p.Command),c.Life.Token);
            }
        }
        catch(Exception e) when(e is IOException or OperationCanceledException or ObjectDisposedException or InvalidDataException or InvalidOperationException){Fail(c,"WRITE_FAILED_UNKNOWN_NO_REPLAY");}
    }
    private async Task ReadLoop(Connection c)
    {
        try
        {
            while(true)
            {
                var line=await c.Transport.ReadAsync(OkxControllerProtocol.ReceiptLimit,c.Life.Token);
                if(line==null)throw new IOException("SESSION_EOF");
                var r=OkxControllerProtocol.DecodeReceipt(line);
                lock(gate)
                {
                    if(!c.Active||current!=c)return;
                    if(r.Epoch!=c.Hello!.Epoch||r.Session!=c.Hello.Session||!c.Pending.TryGetValue(r.CommandId,out var p)||!p.Sent)throw new InvalidDataException("RECEIPT_CORRELATION_REFUSED");
                    if(r.Snapshot!=null&&p.Command.Action!="SNAPSHOT"||p.Command.Action=="SNAPSHOT"&&r.Outcome=="confirmed"&&r.Snapshot==null)throw new InvalidDataException("SNAPSHOT_RECEIPT_REFUSED");
                    c.Pending.Remove(r.CommandId);if(!OkxControllerProtocol.Mutation(p.Command.Action))c.PollPending=false;c.Received=clock.GetTimestamp();
                    if(r.Outcome=="pending-or-unknown")
                    {p.Done.TrySetResult(new(r.Outcome,r.Code,r));Fail(c,"REMOTE_UNKNOWN_QUERY_STATE");return;}
                    if(p.Sequence>=c.Applied)
                    {
                        c.Applied=p.Sequence;c.AppliedReceipt=r;mode=r.Mode;armed=r.Armed;outcome=r.Outcome;code=r.Code;
                        if(r.Snapshot!=null){snapshot=r.Snapshot;stale=false;c.SnapshotReceived=c.Received;}
                        else if(snapshot!=null&&(snapshot.Mode!=r.Mode||snapshot.Armed!=r.Armed))stale=true;
                        // STATUS updates status, never financial capture timestamps or stale financial content.
                    }
                    if(c.Pending.Values.Any(x=>OkxControllerProtocol.Mutation(x.Command.Action))){outcome="pending-or-unknown";code="COMMAND_PENDING";stale=true;}
                    else if(c.AppliedReceipt is {} latest){outcome=latest.Outcome;code=latest.Code;}
                    p.Done.TrySetResult(new(r.Outcome,r.Code,r));
                }
            }
        }
        catch(Exception e) when(e is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException or InvalidOperationException or DecoderFallbackException or ArgumentException){Fail(c,"CHANNEL_LOST_QUERY_STATE_NO_REPLAY");}
    }
    private void Fail(Connection c,string reason)
    {
        lock(gate)
        {
            if(!c.Active)return;c.Active=false;c.Ready=false;c.Life.Cancel();
            if(current==c){stale=true;outcome="pending-or-unknown";code=reason;}
            foreach(var p in c.Pending.Values)p.Done.TrySetResult(Local(p.Sent?"pending-or-unknown":"refused",p.Sent?reason:"NOT_SENT_SESSION_CLOSED"));
            c.Pending.Clear();c.PollPending=false;c.Normal.Clear();c.Urgent.Clear();
        }
        // Abort blocked stream operations. No reconnect, replay, STOP or DISCONNECT is sent.
        _=Abort(c);
    }
    private static async Task Abort(Connection c){try{await c.Transport.DisposeAsync();}catch(Exception e) when(e is IOException or ObjectDisposedException){}}
    private static async Task Retire(Connection c)
    {await Abort(c);await Task.WhenAll(c.Reader,c.Writer);}
    private async Task CloseCurrent(string reason)
    {Connection? c;lock(gate){c=current;if(c!=null)Fail(c,reason);}if(c!=null)await Retire(c);}
    internal async Task PollAsync(TimeSpan interval,CancellationToken cancellation)
    {
        if(interval<TimeSpan.FromMilliseconds(250)||interval>TimeSpan.FromMinutes(1))throw new ArgumentOutOfRangeException(nameof(interval));
        while(!cancellation.IsCancellationRequested)
        {
            var r=await CommandAsync("SNAPSHOT",cancellation);
            if(r.Code is "NEW_SESSION_REQUIRED" or "MANUAL_RECONNECT_REQUIRED"||r.Outcome=="pending-or-unknown")return;
            await Task.Delay(interval,clock,cancellation);
        }
    }
    public async ValueTask DisposeAsync()
    {
        await lifecycle.WaitAsync();try{if(disposed)return;disposed=true;await CloseCurrent("CONTROLLER_CHANNEL_CLOSED_QUERY_STATE");}finally{lifecycle.Release();}
    }
}
