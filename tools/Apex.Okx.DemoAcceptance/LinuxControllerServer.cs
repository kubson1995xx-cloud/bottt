using Apex.Adapters.Okx.Execution;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Apex.Desktop.Okx;
namespace Apex.Okx.DemoAcceptance;

// Only this native admission owner can construct the process-local capability.
// Same UID and administrator are trusted; this is not isolation from that actor.
internal sealed class LinuxControllerAdmission:IDisposable
{
    internal const string ProductionRoot="/home/ubuntu/apex-private/okx-demo-controller";
    private readonly string root,path,lockPath;
    private readonly byte[] directory,lockIdentity;
    private byte[]? socketIdentity;
    private int fd=-1;
    private bool disposed;
    private int listener=-1;
    internal Task<Socket> AcceptAsync(CancellationToken ct)=>Task.Run(()=>
    {
        while(true)
        {
            ct.ThrowIfCancellationRequested();var p=new Poll{fd=listener,events=1};int ready=poll(ref p,1,100);
            if(ready<0){if(Marshal.GetLastPInvokeError()==4)continue;throw Refused();}if(ready==0)continue;
            int client=accept4(listener,IntPtr.Zero,IntPtr.Zero,0x80000|0x800);if(client<0){if(Marshal.GetLastPInvokeError() is 4 or 11)continue;throw Refused();}
            return new Socket(new SafeSocketHandle((IntPtr)client,true));
        }
    },ct);
    internal sealed class LocalControllerCapability
    {
        private readonly LinuxControllerAdmission owner;
        private LocalControllerCapability(LinuxControllerAdmission owner){this.owner=owner;}
        internal bool Current=>!owner.disposed&&owner.Current();
        internal static LocalControllerCapability Admit(LinuxControllerAdmission owner,Socket peer)
        {owner.CheckPeer(peer);return new(owner);}
    }
    internal LinuxControllerAdmission(string root)
    {
        this.root=root;path=Path.Combine(root,"controller.sock");lockPath=Path.Combine(root,".controller.lock");
        OkxLinuxPrivate.AdmitDirectory(root,true);directory=Stat(root);Check(directory,0x4000,0x1c0,false);
        fd=open(lockPath,2|0x40|0x20000|0x80000|0x800,0x180);
        try
        {
            if(fd<0||flock(fd,2|4)!=0)throw Refused();lockIdentity=Fstat(fd);Check(lockIdentity,0x8000,0x180,true);
            if(!Same(lockIdentity,Stat(lockPath)))throw Refused();
            // Never remove unexplained/stale sockets, including symlinks.
            if(lstat(path,new byte[144])==0||Marshal.GetLastPInvokeError()!=2)throw Refused();
            listener=socket(1,1|0x80000|0x800,0);if(listener<0)throw Refused();
            try
            {
                byte[] name=Encoding.UTF8.GetBytes(path);if(name.Length>107)throw Refused();var address=new byte[110];address[0]=1;name.CopyTo(address,2);
                if(bind(listener,address,(uint)(name.Length+3))!=0)throw Refused();socketIdentity=Stat(path);
                CheckSocketOwner(socketIdentity);File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite);
                var admitted=Stat(path);if(!Same(socketIdentity,admitted))throw Refused();Check(admitted,0xc000,0x180,true);socketIdentity=admitted;
                if(listen(listener,8)!=0||!Current())throw Refused();
            }
            catch{close(listener);listener=-1;throw;}
        }
        catch{if(fd>=0){close(fd);fd=-1;}throw;}
    }
    private static IOException Refused()=>new("LOCAL_CONTROLLER_ADMISSION_REFUSED");
    private static byte[] Stat(string p){var s=new byte[144];if(lstat(p,s)!=0)throw Refused();return s;}
    private static byte[] Fstat(int f){var s=new byte[144];if(fstat(f,s)!=0)throw Refused();return s;}
    private static bool Same(byte[] a,byte[] b)=>a.AsSpan(0,16).SequenceEqual(b.AsSpan(0,16));
    private static void CheckSocketOwner(byte[] s){if((BitConverter.ToUInt32(s,24)&0xf000)!=0xc000||BitConverter.ToUInt32(s,28)!=getuid()||BitConverter.ToUInt64(s,16)!=1)throw Refused();}
    private static void Check(byte[] s,uint type,uint permissions,bool oneLink)
    {uint m=BitConverter.ToUInt32(s,24);if((m&0xf000)!=type||(m&0xfff)!=permissions||BitConverter.ToUInt32(s,28)!=getuid()||oneLink&&BitConverter.ToUInt64(s,16)!=1)throw Refused();}
    internal bool Current()
    {try{OkxLinuxPrivate.AdmitDirectory(root,false);var d=Stat(root);Check(d,0x4000,0x1c0,false);var l=Stat(lockPath);Check(l,0x8000,0x180,true);var s=Stat(path);Check(s,0xc000,0x180,true);return Same(d,directory)&&Same(l,lockIdentity)&&Same(Fstat(fd),lockIdentity)&&socketIdentity!=null&&Same(s,socketIdentity);}catch{return false;}}
    internal void CheckPeer(Socket peer){if(!Current())throw Refused();Peer(peer);}
    internal static void Peer(Socket peer)
    {uint size=12;var u=new byte[12];if(getsockopt(peer.Handle.ToInt32(),1,17,u,ref size)!=0||size!=12||BitConverter.ToUInt32(u,4)!=getuid())throw Refused();}
    internal static Socket Connect(string root)
    {
        OkxLinuxPrivate.AdmitDirectory(root,false);var d=Stat(root);Check(d,0x4000,0x1c0,false);string p=Path.Combine(root,"controller.sock");var s=Stat(p);Check(s,0xc000,0x180,true);
        var socket=new Socket(AddressFamily.Unix,SocketType.Stream,ProtocolType.Unspecified);
        try{socket.Connect(new UnixDomainSocketEndPoint(p));Peer(socket);if(!Same(s,Stat(p))||!Same(d,Stat(root)))throw Refused();return socket;}catch{socket.Dispose();throw;}
    }
    public void Dispose()
    {
        if(disposed)return;
        try{if(listener>=0){close(listener);listener=-1;}if(socketIdentity!=null&&Current())File.Delete(path);}finally{disposed=true;if(fd>=0){close(fd);fd=-1;}}
    }
    [StructLayout(LayoutKind.Sequential)]private struct Poll{public int fd;public short events,revents;}
    [DllImport("libc",SetLastError=true)]private static extern int socket(int domain,int type,int protocol);
    [DllImport("libc",SetLastError=true)]private static extern int bind(int fd,byte[] address,uint length);
    [DllImport("libc",SetLastError=true)]private static extern int listen(int fd,int backlog);
    [DllImport("libc",SetLastError=true)]private static extern int accept4(int fd,IntPtr address,IntPtr length,int flags);
    [DllImport("libc",SetLastError=true)]private static extern int poll(ref Poll p,nuint count,int timeout);
    [DllImport("libc",SetLastError=true)]private static extern int lstat(string p,[Out]byte[] s);
    [DllImport("libc",SetLastError=true)]private static extern int fstat(int f,[Out]byte[] s);
    [DllImport("libc",SetLastError=true)]private static extern int open(string p,int flags,uint mode);
    [DllImport("libc",SetLastError=true)]private static extern int flock(int fd,int op);
    [DllImport("libc")]private static extern int close(int fd);
    [DllImport("libc")]private static extern uint getuid();
    [DllImport("libc",SetLastError=true)]private static extern int getsockopt(int fd,int level,int option,[Out]byte[] value,ref uint size);
}

internal static class ControllerFrames
{
    internal static async Task<string?> Read(Stream s,int cap,CancellationToken ct)
    {
        byte[] bytes=new byte[cap];byte[] one=new byte[1];int n=0;
        while(true)
        {
            if(await s.ReadAsync(one,ct)==0){if(n==0)return null;throw new InvalidDataException("FRAME_TRUNCATED");}
            if(one[0]==10)return new UTF8Encoding(false,true).GetString(bytes,0,n);
            if(one[0]==13||n==cap)throw new InvalidDataException("FRAME_REFUSED");bytes[n++]=one[0];
        }
    }
    internal static async Task Write(Stream s,string line,CancellationToken ct)
    {byte[] bytes=Encoding.UTF8.GetBytes(line+"\n");await s.WriteAsync(bytes,ct);await s.FlushAsync(ct);}
}
internal sealed class LinuxControllerRelay:IDisposable
{
    private readonly NetworkStream stream;
    private LinuxControllerRelay(Socket socket){stream=new(socket,true);}
    internal static Task<LinuxControllerRelay> ConnectForTestsAsync(string root,CancellationToken ct){ct.ThrowIfCancellationRequested();return Task.FromResult(new LinuxControllerRelay(LinuxControllerAdmission.Connect(root)));}
    internal async Task<OkxControllerHello> HelloAsync(CancellationToken ct)=>OkxControllerProtocol.DecodeHello(await ControllerFrames.Read(stream,1024,ct)??throw new IOException("SESSION_LOST"));
    internal async Task<OkxControllerReceipt> CommandAsync(OkxControllerCommand c,CancellationToken ct)
    {await ControllerFrames.Write(stream,OkxControllerProtocol.EncodeCommand(c),ct);return OkxControllerProtocol.DecodeReceipt(await ControllerFrames.Read(stream,OkxControllerProtocol.ReceiptLimit,ct)??throw new IOException("ACKNOWLEDGEMENT_UNKNOWN_NO_REPLAY"));}
    internal static async Task<int> RunAsync()
    {
        try{using var relay=new LinuxControllerRelay(LinuxControllerAdmission.Connect(LinuxControllerAdmission.ProductionRoot));return await relay.ForwardAsync(Console.OpenStandardInput(),Console.OpenStandardOutput(),default);}
        catch(Exception e) when(e is IOException or SocketException or UnauthorizedAccessException){Console.Error.WriteLine("CONTROLLER_RELAY_UNAVAILABLE_NO_REPLAY");return 2;}
    }
    internal async Task<int> ForwardAsync(Stream input,Stream output,CancellationToken ct)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task Up(){while(await ControllerFrames.Read(input,OkxControllerProtocol.CommandLimit,linked.Token) is {} line){OkxControllerProtocol.DecodeCommand(line);await ControllerFrames.Write(stream,line,linked.Token);}}
        async Task Down(){var hello=await HelloAsync(linked.Token);await ControllerFrames.Write(output,OkxControllerProtocol.EncodeHello(hello),linked.Token);while(await ControllerFrames.Read(stream,OkxControllerProtocol.ReceiptLimit,linked.Token) is {} line){OkxControllerProtocol.DecodeReceipt(line);await ControllerFrames.Write(output,line,linked.Token);}}
        var up=Up();var down=Down();await Task.WhenAny(up,down);linked.Cancel();stream.Dispose();try{await Task.WhenAll(up,down);}catch(OperationCanceledException){}return 0;
    }
    public void Dispose()=>stream.Dispose();
}

internal sealed class LinuxControllerServer:IAsyncDisposable
{
    private readonly LinuxControllerAdmission admission;
    private readonly string financial;
    private readonly LinuxDemoCredentialVault vault;
    private readonly Func<EphemeralCredentials,CancellationToken,Task<OkxEnrollmentDiscovery>> discover;
    private readonly string epoch=Guid.NewGuid().ToString("N");
    private readonly Channel<Work> actions=Channel.CreateBounded<Work>(8);
    private readonly HashSet<Session> sessions=new();
    private readonly object gate=new();
    private CancellationTokenSource? lifetime;
    private LinuxDemoHost.ControllerAttachment? host;
    private Session? activeOwner;
    private string? activeAction;
    private bool confirmedRunning;
    private readonly bool synthetic;
    private readonly FiniteEntryTestComposition? transport;
    private long actionEpoch;
    private sealed record Work(Session Owner,OkxControllerCommand Command,long Epoch,TaskCompletionSource<OkxControllerReceipt> Done);
    private sealed class Session
    {
        internal readonly string Id=Guid.NewGuid().ToString("N");
        internal readonly Dictionary<string,(string Action,TaskCompletionSource<OkxControllerReceipt> Done)> Receipts=new();
        internal readonly HashSet<string> Seen=new();
        internal readonly Queue<string> Order=new();
        internal readonly Channel<string> Output=Channel.CreateBounded<string>(8);
        internal bool Active=true;
        internal int Publishers;
    }
    private LinuxControllerServer(string root,string financial,LinuxDemoCredentialVault vault,Func<EphemeralCredentials,CancellationToken,Task<OkxEnrollmentDiscovery>> discover,bool synthetic=false,FiniteEntryTestComposition? transport=null)
    {admission=new(root);this.financial=financial;this.vault=vault;this.discover=discover;this.synthetic=synthetic;this.transport=transport;}
    internal static LinuxControllerServer ForTests(string root,string financial,LinuxDemoCredentialVault vault,Func<EphemeralCredentials,CancellationToken,Task<OkxEnrollmentDiscovery>> discover)=>new(root,financial,vault,discover,true);
    internal static LinuxControllerServer ForTests(string root,string financial,LinuxDemoCredentialVault vault,Func<EphemeralCredentials,CancellationToken,Task<OkxEnrollmentDiscovery>> discover,FiniteEntryTestComposition? transport)=>new(root,financial,vault,discover,true,transport);
    internal static async Task<int> RunProductionAsync()
    {
        try{LinuxInput.Harden();await using var server=new LinuxControllerServer(LinuxControllerAdmission.ProductionRoot,Program.Root,new LinuxDemoCredentialVault(),(k,c)=>k.Discover(c));return await server.RunAsync(default);}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or SocketException){Console.Error.WriteLine("CONTROLLER_SERVER_ADMISSION_REFUSED_NO_CREDENTIALS_OPENED");return 2;}
    }
    internal async Task<int> RunAsync(CancellationToken ct)
    {
        lifetime=CancellationTokenSource.CreateLinkedTokenSource(ct);var token=lifetime.Token;var clients=new List<Task>();
        var signals=new List<PosixSignalRegistration>();
        foreach(var signal in new[]{PosixSignal.SIGINT,PosixSignal.SIGTERM,PosixSignal.SIGHUP,PosixSignal.SIGQUIT})signals.Add(PosixSignalRegistration.Create(signal,c=>{c.Cancel=true;lifetime.Cancel();}));
        var lane=Lane(token);
        try
        {
            while(true)
            {
                var peer=await admission.AcceptAsync(token);lock(gate){clients.RemoveAll(t=>t.IsCompleted);if(clients.Count>=8){peer.Dispose();continue;}clients.Add(Client(peer,token));}
            }
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested){}
        finally{lifetime.Cancel();actions.Writer.TryComplete();await Task.WhenAll(clients);await lane;foreach(var signal in signals)signal.Dispose();admission.Dispose();}
        return 0;
    }
    private async Task Client(Socket peer,CancellationToken ct)
    {
        var session=new Session();using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct);using var stream=new NetworkStream(peer,true);
        Task? writer=null;
        try
        {
            var capability=LinuxControllerAdmission.LocalControllerCapability.Admit(admission,peer);
            lock(gate){host??=new(capability,financial,vault,discover,transport,synthetic,ct);sessions.Add(session);}
            writer=Task.Run(async()=>{try{await foreach(var line in session.Output.Reader.ReadAllAsync(linked.Token))await ControllerFrames.Write(stream,line,linked.Token);}finally{linked.Cancel();}},CancellationToken.None);
            session.Output.Writer.TryWrite(OkxControllerProtocol.EncodeHello(new(OkxControllerProtocol.Version,epoch,session.Id)));
            while(await ControllerFrames.Read(stream,OkxControllerProtocol.CommandLimit,linked.Token) is {} line)
            {
                if(!capability.Current)throw new IOException("ADMISSION_LOST");var command=OkxControllerProtocol.DecodeCommand(line);
                if(command.Epoch!=epoch||command.Session!=session.Id){Send(session,Receipt(command,"refused","FOREIGN_SESSION"));continue;}
                TaskCompletionSource<OkxControllerReceipt>? done;bool fresh=false;
                lock(gate)
                {
                    if(session.Receipts.TryGetValue(command.CommandId,out var old))
                    {if(old.Action!=command.Action){Send(session,Receipt(command,"refused","COMMAND_CONFLICT"));continue;}done=old.Done;}
                    else
                    {
                        if(session.Seen.Contains(command.CommandId)||session.Seen.Count>=256){Send(session,Receipt(command,"refused","RECEIPT_EXPIRED_SESSION_LIMIT"));continue;}
                        done=new(TaskCreationOptions.RunContinuationsAsynchronously);session.Seen.Add(command.CommandId);session.Receipts.Add(command.CommandId,(command.Action,done));session.Order.Enqueue(command.CommandId);fresh=true;
                        while(session.Order.Count>64)session.Receipts.Remove(session.Order.Dequeue());
                    }
                }
                lock(gate)
                {
                    if(fresh&&command.Action is "STOP" or "EMERGENCY" or "DISCONNECT"){actionEpoch++;confirmedRunning=false;host?.Revoke();}
                    if(fresh&&!actions.Writer.TryWrite(new(session,command,actionEpoch,done)))done.TrySetResult(Receipt(command,"refused","QUEUE_FULL"));
                }
                if(Interlocked.Increment(ref session.Publishers)>16)throw new IOException("OUTPUT_BACKPRESSURE");
                _=Publish(session,done.Task,linked);
            }
        }
        catch(Exception e) when(e is IOException or SocketException or OperationCanceledException or ObjectDisposedException or DecoderFallbackException){}
        finally{lock(gate){session.Active=false;sessions.Remove(session);host?.ControllerLost(confirmedRunning&&(activeOwner!=session||activeAction is not ("CONNECT" or "PREPARE" or "ARM" or "START")));}linked.Cancel();session.Output.Writer.TryComplete();if(writer!=null)try{await writer;}catch(Exception e) when(e is IOException or SocketException or OperationCanceledException or ObjectDisposedException){} }
    }
    private async Task Publish(Session s,Task<OkxControllerReceipt> task,CancellationTokenSource linked)
    {try{var r=await task.WaitAsync(linked.Token);if(!Send(s,r))linked.Cancel();}catch(OperationCanceledException){}finally{Interlocked.Decrement(ref s.Publishers);}}
    private bool Send(Session s,OkxControllerReceipt r)=>s.Output.Writer.TryWrite(OkxControllerProtocol.EncodeReceipt(r));
    private OkxControllerReceipt Receipt(OkxControllerCommand c,string outcome,string code,OkxControllerSnapshot? snapshot=null)
    {var state=host?.State()??("STOP",false);if(snapshot!=null&&(snapshot.Mode!=state.Item1||snapshot.Armed!=state.Item2)){snapshot=null;outcome="refused";code="SNAPSHOT_STATE_CHANGED";}return new(OkxControllerProtocol.Version,epoch,c.Session,c.CommandId,outcome,code,state.Item1,state.Item2,snapshot);}
    private async Task Lane(CancellationToken ct)
    {
        try
        {
            await foreach(var work in actions.Reader.ReadAllAsync(ct))
            {
                Task<(string Outcome,string Code,OkxControllerSnapshot? Snapshot)> executing;
                lock(gate)
                {
                    if(!work.Owner.Active||host==null||work.Epoch!=actionEpoch&&work.Command.Action is "CONNECT" or "PREPARE" or "ARM" or "START"){work.Done.TrySetResult(Receipt(work.Command,"refused","SESSION_LOST"));continue;}
                    activeOwner=work.Owner;activeAction=work.Command.Action;executing=host.Execute(work.Command.Action);
                }
                try
                {
                    var result=await executing;
                    lock(gate)
                    {
                        if(work.Command.Action=="START")confirmedRunning=work.Owner.Active&&result.Outcome=="confirmed";
                        if(!work.Owner.Active&&OkxControllerProtocol.Mutation(work.Command.Action))
                        {if(work.Command.Action=="START")host!.Revoke();work.Done.TrySetResult(Receipt(work.Command,"pending-or-unknown","SESSION_LOST_QUERY_STATE"));}
                        else work.Done.TrySetResult(Receipt(work.Command,result.Outcome,result.Code,work.Command.Action=="SNAPSHOT"?result.Snapshot:null));
                        activeOwner=null;activeAction=null;
                    }
                }
                catch(OperationCanceledException){work.Done.TrySetResult(Receipt(work.Command,"pending-or-unknown","ACTION_INTERRUPTED_QUERY_STATE"));}
                catch(Exception e) when(e is IOException or InvalidOperationException)
                {lock(gate){confirmedRunning=false;host!.Revoke();activeOwner=null;activeAction=null;}work.Done.TrySetResult(Receipt(work.Command,"pending-or-unknown","ACTION_UNKNOWN_QUERY_STATE"));}
            }
        }
        catch(OperationCanceledException){}
        finally{if(host!=null)await host.DisposeAsync();while(actions.Reader.TryRead(out var w))w.Done.TrySetResult(Receipt(w.Command,"pending-or-unknown","SERVER_SHUTDOWN"));}
    }
    public ValueTask DisposeAsync(){lifetime?.Cancel();admission.Dispose();lifetime?.Dispose();return ValueTask.CompletedTask;}
}
