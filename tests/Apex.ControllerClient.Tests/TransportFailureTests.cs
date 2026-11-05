using Apex.Desktop.Okx;
using Xunit;
namespace Apex.ControllerClient.Tests;
public sealed class TransportFailureTests
{
    sealed class Intercept:IOkxControllerFrameTransport
    {
        readonly IOkxControllerFrameTransport inner;
        internal bool FailAfterWrite,Hold;
        internal readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<string> Written=new();
        internal Intercept(IOkxControllerFrameTransport inner){this.inner=inner;}
        public Task<string?> ReadAsync(int cap,CancellationToken ct)=>inner.ReadAsync(cap,ct);
        public async Task WriteAsync(string line,CancellationToken ct)
        {
            var command=OkxControllerProtocol.DecodeCommand(line);lock(Written)Written.Add(command.Action);
            await inner.WriteAsync(line,ct);
            if(FailAfterWrite)throw new IOException("INJECTED_AFTER_POSSIBLE_SEND");
            if(Hold){Entered.TrySetResult();await Release.Task.WaitAsync(ct);}
        }
        public ValueTask DisposeAsync()=>inner.DisposeAsync();
    }
    static async Task Open(SessionTests.Peer p,OkxControllerClient c,IOkxControllerFrameTransport t){var boot=p.Bootstrap();await c.ReconnectAsync(t);await boot;}
    [Fact(Timeout=10000)] public async Task Write_failure_after_possible_mutation_send_is_unknown_with_no_retry_or_stop()
    {
        await using var p=new SessionTests.Peer();var wire=new Intercept(p.Client);await using var c=new OkxControllerClient(TimeSpan.FromSeconds(2),TimeSpan.FromSeconds(5));await Open(p,c,wire);
        wire.FailAfterWrite=true;var task=c.CommandAsync("START");Assert.Equal("START",(await p.Read()).Action);Assert.Equal("pending-or-unknown",(await task).Outcome);
        Assert.True(c.View.SnapshotStale);Assert.Equal(new[]{"STATUS","SNAPSHOT","START"},wire.Written);Assert.Null(await p.Server.ReadAsync(1024,default));
    }
    [Fact(Timeout=10000)] public async Task Canceled_sent_mutation_is_unknown_without_implicit_remote_stop()
    {
        await using var p=new SessionTests.Peer();await using var c=new OkxControllerClient(TimeSpan.FromSeconds(2),TimeSpan.FromSeconds(5));await Open(p,c,p.Client);
        using var cancel=new CancellationTokenSource();var task=c.CommandAsync("ARM",cancel.Token);await p.Read();cancel.Cancel();Assert.Equal("pending-or-unknown",(await task).Outcome);Assert.Null(await p.Server.ReadAsync(1024,default));
    }
    [Fact(Timeout=10000)] public async Task Stop_supersedes_queued_start_and_queue_keeps_reserved_emergency_slots()
    {
        await using var p=new SessionTests.Peer();var wire=new Intercept(p.Client);await using var c=new OkxControllerClient(TimeSpan.FromSeconds(3),TimeSpan.FromSeconds(5));await Open(p,c,wire);
        wire.Hold=true;var poll=c.CommandAsync("STATUS");var held=await p.Read();await wire.Entered.Task;
        var queued=new List<Task<OkxControllerClientResult>>();for(int i=0;i<5;i++)queued.Add(c.CommandAsync("START"));
        Assert.Equal("CLIENT_QUEUE_FULL",(await c.CommandAsync("ARM")).Code);Assert.Equal(6,c.View.Pending);
        var stop=c.CommandAsync("STOP");Assert.All(await Task.WhenAll(queued),x=>Assert.Equal("SUPERSEDED_BEFORE_SEND",x.Code));
        wire.Hold=false;wire.Release.SetResult();var command=await p.Read();Assert.Equal("STOP",command.Action);await p.Reply(command);await stop;await p.Reply(held);await poll;
        Assert.DoesNotContain("START",wire.Written);Assert.Equal("confirmed",c.View.Outcome);
    }
    [Fact(Timeout=10000)] public async Task Repeated_hello_session_is_rejected_before_any_new_command()
    {
        await using var p=new SessionTests.Peer();await using var c=new OkxControllerClient(TimeSpan.FromSeconds(2),TimeSpan.FromSeconds(5));await Open(p,c,p.Client);
        await using var next=new SessionTests.Peer{Epoch=p.Epoch,Session=p.Session};await next.Hello();await Assert.ThrowsAsync<InvalidDataException>(()=>c.ReconnectAsync(next.Client));Assert.Null(await next.Server.ReadAsync(1024,default));Assert.True(c.View.SnapshotStale);
    }
    [Fact(Timeout=10000)] public async Task Real_connected_snapshot_preserves_excluded_balances_and_authoritative_capital_separately()
    {
        await using var p=new SessionTests.Peer();await using var c=new OkxControllerClient(TimeSpan.FromSeconds(2),TimeSpan.FromSeconds(5));await Open(p,c,p.Client);
        var utc=DateTimeOffset.UtcNow;var synthetic=OkxControllerSnapshotCodec.Disconnected(utc) with{Connected=true,Revision=3,Capital=new("EUR",10m,2m,8m,1m,null),Observation=new("BASELINE",utc.AddHours(-1),null,null,null,false,false),Balances=new(1,false,new[]{new OkxControllerBalance("BTC",123m,100m,23m,utc.AddHours(-1),true)})};
        var task=c.CommandAsync("SNAPSHOT");await p.Reply(await p.Read(),snapshot:synthetic);await task;var view=c.View.Snapshot!;
        Assert.Equal(10m,view.Capital!.Cash);Assert.Equal(8m,view.Capital.Free);Assert.True(view.Balances.Items.Single().Excluded);Assert.Equal(123m,view.Balances.Items.Single().Total);Assert.Equal(utc.AddHours(-1),view.Observation.CaptureUtc);
    }
    [Fact] public void Ssh_paths_refuse_secondary_expansion_and_control_characters()
    {
        foreach(var path in new[]{"/owned/%h","/owned/${HOME}","/owned/key\nProxyCommand evil"})
            Assert.Throws<ArgumentException>(()=>OkxControllerSshLaunchSpec.Build(new("vps.example",22,"apex",path,"/owned/hosts"),"/usr/bin/ssh","/dev/null"));
    }
}
