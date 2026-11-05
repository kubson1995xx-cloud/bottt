using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Desktop.Okx;
using Apex.Okx.DemoAcceptance;
using Xunit;
namespace Apex.Tests;
public sealed partial class OkxControllerServerTests
{
    [Theory]
    [InlineData("STOP")][InlineData("EMERGENCY")]
    public async Task Accepted_STOP_revokes_armed_host_during_blocked_output_and_rejects_queued_START(string action)
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f){ExitSignal=true};
        var loop=OkxOperationalTradingService.ForTests(f.Service,(s,k,h,x)=>OkxExecutionClient.ForTests(peer,f.Clock,s,k,h,x),peer,f.Clock);
        string root=FreshRoot();await using var server=NewServer(root);using var stop=new CancellationTokenSource();var running=server.RunAsync(stop.Token);BlockOutput? block=null;
        try
        {
            using var a=await LinuxControllerRelay.ConnectForTestsAsync(root,default);var ah=await a.HelloAsync(default);var host=Inject(server,f,loop);
            var r=f.Request with{Purpose=OkxActivationPurpose.Execute,Session=f.Service.TradingSessionId};var lease=await f.Runtime.Activation.RequestAsync(r,default);Assert.True(loop.Arm(r,lease!));
            const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;var field=typeof(LinuxDemoHost).GetField("output",flags)!;block=new BlockOutput((TextWriter)field.GetValue(host)!);field.SetValue(host,block);
            var pending=a.CommandAsync(Command(ah,"SNAPSHOT"),default);await block.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var queued=await LinuxControllerRelay.ConnectForTestsAsync(root,default);var qh=await queued.HelloAsync(default);var queuedStart=queued.CommandAsync(Command(qh,"START"),default);
            var channel=typeof(LinuxControllerServer).GetField("actions",flags)!.GetValue(server)!;var reader=channel.GetType().GetProperty("Reader")!.GetValue(channel)!;
            for(int n=0;n<100&&(int)reader.GetType().GetProperty("Count")!.GetValue(reader)!<1;n++)await Task.Delay(10);
            Assert.True((int)reader.GetType().GetProperty("Count")!.GetValue(reader)!>=1);
            using var b=await LinuxControllerRelay.ConnectForTestsAsync(root,default);var bh=await b.HelloAsync(default);
            var veto=b.CommandAsync(Command(bh,action),default);
            for(int n=0;n<100&&loop.ReadStatus().Armed;n++)await Task.Delay(10);
            Assert.False(loop.ReadStatus().Armed);Assert.Equal("STOP",loop.ReadStatus().Mode);Assert.False(veto.IsCompleted);Assert.Equal(0,peer.Posts);
            block.Release.Set();var projected=await pending.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(projected.Armed);Assert.Equal("refused",(await queuedStart.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);Assert.Equal("confirmed",(await veto.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);
            Assert.Equal("refused",(await b.CommandAsync(Command(bh,"START"),default)).Outcome);Assert.Equal(0,peer.Posts);
        }
        finally{block?.Release.Set();stop.Cancel();await running.WaitAsync(TimeSpan.FromSeconds(10));}
        Directory.Delete(root,true);
    }
    [Fact]
    public async Task Lost_controller_during_unacknowledged_START_is_stopped_and_fresh_session_does_not_replay()
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f){ExitSignal=true};
        var loop=OkxOperationalTradingService.ForTests(f.Service,(s,k,h,x)=>OkxExecutionClient.ForTests(peer,f.Clock,s,k,h,x),peer,f.Clock);
        string root=FreshRoot();await using var server=NewServer(root);using var stop=new CancellationTokenSource();var running=server.RunAsync(stop.Token);BlockOutput? block=null;
        try
        {
            var a=await LinuxControllerRelay.ConnectForTestsAsync(root,default);var ah=await a.HelloAsync(default);var host=Inject(server,f,loop);
            var r=f.Request with{Purpose=OkxActivationPurpose.Execute,Session=f.Service.TradingSessionId};var lease=await f.Runtime.Activation.RequestAsync(r,default);Assert.True(loop.Arm(r,lease!));Assert.Equal(Apex.Core.Naval.NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);
            const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;var field=typeof(LinuxDemoHost).GetField("output",flags)!;block=new BlockOutput((TextWriter)field.GetValue(host)!,"SCHEDULER_STARTED");field.SetValue(host,block);
            var uncertain=a.CommandAsync(Command(ah,"START"),default);await block.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));a.Dispose();
            for(int n=0;n<100&&loop.ReadStatus().Armed;n++)await Task.Delay(10);Assert.False(loop.ReadStatus().Armed);Assert.Equal("STOP",loop.ReadStatus().Mode);block.Release.Set();
            try{await uncertain.WaitAsync(TimeSpan.FromSeconds(10));Assert.True(false,"A missing acknowledgement must not become success");}catch(Exception e) when(e is IOException or ObjectDisposedException){}
            using var b=await LinuxControllerRelay.ConnectForTestsAsync(root,default);var bh=await b.HelloAsync(default);var status=await b.CommandAsync(Command(bh,"STATUS"),default);Assert.Equal("STOP",status.Mode);Assert.False(status.Armed);Assert.Equal(0,peer.Posts);
        }
        finally{block?.Release.Set();stop.Cancel();await running.WaitAsync(TimeSpan.FromSeconds(10));}
        Directory.Delete(root,true);
    }
    private sealed class BlockOutput(TextWriter inner,string prefix="CONTROLLER_SNAPSHOT "):TextWriter
    {
        public override Encoding Encoding=>Encoding.UTF8;
        internal readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release=new(false);
        public override void WriteLine(string? value)
        {if(value?.StartsWith(prefix,StringComparison.Ordinal)==true){Entered.TrySetResult();if(!Release.Wait(TimeSpan.FromSeconds(15)))throw new IOException("FIXTURE_TIMEOUT");}inner.WriteLine(value);}
    }
}
