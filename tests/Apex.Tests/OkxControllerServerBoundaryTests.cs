using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Desktop.Okx;
using Apex.Okx.DemoAcceptance;
using Xunit;
namespace Apex.Tests;
public sealed partial class OkxControllerServerTests
{
    private static string FreshRoot(){var root=Path.Combine("/home/ubuntu/apex-deliveries","cs-"+Guid.NewGuid().ToString("N")[..12]);Directory.CreateDirectory(root,(UnixFileMode)448);return root;}
    private static LinuxControllerServer NewServer(string root)=>LinuxControllerServer.ForTests(root,Path.Combine(root,"financial"),new LinuxDemoCredentialVault(Path.Combine(root,"vault")),(_,_)=>throw new Exception("NO_DISCOVERY"));
    private static OkxControllerCommand Command(OkxControllerHello h,string action)=>new(OkxControllerProtocol.Version,h.Epoch,h.Session,Guid.NewGuid().ToString("N"),action);
    [Fact]
    public async Task Session_replay_eviction_foreign_epoch_and_simultaneous_clients_are_closed()
    {
        string root=FreshRoot();string oldEpoch;
        await using(var server=NewServer(root))
        {
            using var stop=new CancellationTokenSource();var running=server.RunAsync(stop.Token);
            try
            {
                using var a=await LinuxControllerRelay.ConnectForTestsAsync(root,default);var ah=await a.HelloAsync(default);oldEpoch=ah.Epoch;
                using var b=await LinuxControllerRelay.ConnectForTestsAsync(root,default);var bh=await b.HelloAsync(default);Assert.NotEqual(ah.Session,bh.Session);
                Assert.Equal("FOREIGN_SESSION",(await b.CommandAsync(Command(ah,"START"),default)).Code);
                var first=Command(ah,"STOP");Assert.Equal("confirmed",(await a.CommandAsync(first,default)).Outcome);
                for(int n=0;n<66;n++)Assert.Equal("confirmed",(await a.CommandAsync(Command(ah,"STATUS"),default)).Outcome);
                Assert.Equal("RECEIPT_EXPIRED_SESSION_LIMIT",(await a.CommandAsync(first,default)).Code);
                Assert.Equal("refused",(await a.CommandAsync(Command(ah,"START"),default)).Outcome);
                Assert.Equal("refused",(await a.CommandAsync(Command(ah,"CONNECT"),default)).Outcome);
                Assert.False(Directory.Exists(Path.Combine(root,"vault")));
                Assert.Equal("STOP",(await b.CommandAsync(Command(bh,"STATUS"),default)).Mode);
            }
            finally{stop.Cancel();await running.WaitAsync(TimeSpan.FromSeconds(10));}
        }
        await using(var server=NewServer(root))
        {
            using var stop=new CancellationTokenSource();var running=server.RunAsync(stop.Token);
            try{using var relay=await LinuxControllerRelay.ConnectForTestsAsync(root,default);var h=await relay.HelloAsync(default);Assert.NotEqual(oldEpoch,h.Epoch);Assert.Equal("FOREIGN_SESSION",(await relay.CommandAsync(Command(h,"START") with{Epoch=oldEpoch},default)).Code);Assert.Equal("STOP",(await relay.CommandAsync(Command(h,"SNAPSHOT"),default)).Mode);}
            finally{stop.Cancel();await running.WaitAsync(TimeSpan.FromSeconds(10));}
        }
        Directory.Delete(root,true);
    }
    [Fact]
    public void Strict_shared_codec_rejects_fields_versions_duplicates_and_command_injection()
    {
        var h=new OkxControllerHello(OkxControllerProtocol.Version,Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"));var c=Command(h,"STOP");string good=OkxControllerProtocol.EncodeCommand(c);
        foreach(string bad in new[]{good.Replace("\"Action\":\"STOP\"","\"Action\":\"STOP\",\"extra\":true"),good.Replace("\"Action\":\"STOP\"","\"Action\":\"STOP\",\"Action\":\"START\""),good.Replace("apex-controller-v1","v2"),good+"\n",good.Replace("STOP","BASELINE"),good.Replace("STOP","STOP START"),good.Replace("STOP","STOP\\nSTART"),new string(' ',1025)})Assert.Throws<InvalidDataException>(()=>OkxControllerProtocol.DecodeCommand(bad));
        foreach(string action in new[]{"--finite-demo","LIVE","QUIT","DIAGNOSE","BASELINE","ADVISORY-STATUS","DISCONNECT ../"})Assert.Throws<InvalidDataException>(()=>OkxControllerProtocol.EncodeCommand(c with{Action=action}));
    }
    [Fact]
    public async Task Native_admission_refuses_bad_permissions_symlinks_replacement_old_socket_and_second_server()
    {
        string root=FreshRoot();
        await using(var server=NewServer(root))
        {
            Assert.Throws<IOException>(()=>NewServer(root));
            Assert.Equal((UnixFileMode)384,File.GetUnixFileMode(Path.Combine(root,"controller.sock")));
            File.Move(Path.Combine(root,"controller.sock"),Path.Combine(root,"owned.sock"));File.WriteAllText(Path.Combine(root,"controller.sock"),"unexplained");File.SetUnixFileMode(Path.Combine(root,"controller.sock"),(UnixFileMode)384);
            Assert.Throws<IOException>(()=>LinuxControllerAdmission.Connect(root));
        }
        Assert.Equal("unexplained",File.ReadAllText(Path.Combine(root,"controller.sock")));Directory.Delete(root,true);
        root=FreshRoot();File.SetUnixFileMode(root,(UnixFileMode)488);Assert.Throws<IOException>(()=>NewServer(root));Directory.Delete(root,true);
        root=FreshRoot();string target=FreshRoot();Directory.Delete(root);Directory.CreateSymbolicLink(root,target);Assert.Throws<IOException>(()=>NewServer(root));Directory.Delete(root);Directory.Delete(target);
        root=FreshRoot();using(var socket=new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix,System.Net.Sockets.SocketType.Stream,System.Net.Sockets.ProtocolType.Unspecified)){socket.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(Path.Combine(root,"controller.sock")));Assert.Throws<IOException>(()=>NewServer(root));Assert.True(File.Exists(Path.Combine(root,"controller.sock")));}Directory.Delete(root,true);
    }
    private static LinuxDemoHost Inject(LinuxControllerServer server,OperationalPeer f,OkxOperationalTradingService loop)
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var attachment=typeof(LinuxControllerServer).GetField("host",flags)!.GetValue(server)!;
        var host=(LinuxDemoHost)attachment.GetType().GetField("host",flags)!.GetValue(attachment)!;
        typeof(LinuxDemoHost).GetField("account",flags)!.SetValue(host,f.Service);typeof(LinuxDemoHost).GetField("loop",flags)!.SetValue(host,loop);return host;
    }
    [Fact]
    public async Task Disconnect_before_START_revokes_ARM_but_confirmed_START_outlives_controller_then_STOP_drains()
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f){ExitSignal=true};
        var loop=OkxOperationalTradingService.ForTests(f.Service,(s,k,h,x)=>OkxExecutionClient.ForTests(peer,f.Clock,s,k,h,x),peer,f.Clock);
        string root=FreshRoot();await using var server=NewServer(root);using var stop=new CancellationTokenSource();var running=server.RunAsync(stop.Token);
        async Task Arm(){var r=f.Request with{Purpose=OkxActivationPurpose.Execute,Session=f.Service.TradingSessionId};var l=await f.Runtime.Activation.RequestAsync(r,default);Assert.True(loop.Arm(r,l!));Assert.Equal(Apex.Core.Naval.NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);}
        try
        {
            using(var a=await LinuxControllerRelay.ConnectForTestsAsync(root,default)){var h=await a.HelloAsync(default);Inject(server,f,loop);await Arm();Assert.True(loop.ReadStatus().Armed);}
            for(int n=0;n<100&&loop.ReadStatus().Armed;n++)await Task.Delay(10);Assert.False(loop.ReadStatus().Armed);
            using(var a=await LinuxControllerRelay.ConnectForTestsAsync(root,default))
            {
                var h=await a.HelloAsync(default);await Arm();var r=await a.CommandAsync(Command(h,"START"),default);Assert.Equal("confirmed",r.Outcome);Assert.Equal("RUNNING",r.Mode);
                var host=Inject(server,f,loop);const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;var field=typeof(LinuxDemoHost).GetField("output",flags)!;var block=new BlockOutput((TextWriter)field.GetValue(host)!);field.SetValue(host,block);
                var projection=a.CommandAsync(Command(h,"SNAPSHOT"),default);await block.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                try{a.Dispose();await Task.Delay(100);Assert.Equal("RUNNING",loop.ReadStatus().Mode);}
                finally{block.Release.Set();}
                try{await projection;}catch(Exception e) when(e is IOException or ObjectDisposedException){}
            }
            await Task.Delay(100);Assert.Equal("RUNNING",loop.ReadStatus().Mode);
            using(var b=await LinuxControllerRelay.ConnectForTestsAsync(root,default)){var h=await b.HelloAsync(default);var r=await b.CommandAsync(Command(h,"STOP"),default);Assert.Equal("confirmed",r.Outcome);Assert.False(r.Armed);Assert.Equal("STOP",r.Mode);}
            Assert.Equal(0,peer.Posts);
        }
        finally{stop.Cancel();await running.WaitAsync(TimeSpan.FromSeconds(10));}
        Directory.Delete(root,true);
    }
}
