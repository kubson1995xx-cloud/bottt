using Apex.Desktop.Okx;
using System.Text;
using Xunit;
namespace Apex.ControllerClient.Tests;
public sealed class SafetyTests
{
    static async Task Open(SessionTests.Peer p,OkxControllerClient c){var boot=p.Bootstrap();await c.ReconnectAsync(p.Client);await boot;}
    [Fact(Timeout=10000)] public async Task Stop_is_sent_while_passive_receipt_is_blocked_and_late_status_cannot_override_it()
    {
        await using var p=new SessionTests.Peer();await using var c=new OkxControllerClient(TimeSpan.FromSeconds(2),TimeSpan.FromSeconds(5));await Open(p,c);
        var poll=c.CommandAsync("STATUS");var old=await p.Read();
        var stop=c.CommandAsync("STOP");var command=await p.Read().WaitAsync(TimeSpan.FromSeconds(1));Assert.Equal("STOP",command.Action);Assert.NotEqual(old.CommandId,command.CommandId);
        Assert.Equal("pending-or-unknown",c.View.Outcome);await p.Reply(command);Assert.Equal("confirmed",(await stop).Outcome);
        await p.Reply(old,"RUNNING");await poll;Assert.Equal("STOP",c.View.Mode);Assert.Equal("confirmed",c.View.Outcome);
    }
    [Fact(Timeout=10000)] public async Task Late_start_receipt_does_not_leave_a_confirmed_newer_stop_permanently_pending()
    {
        await using var p=new SessionTests.Peer();await using var c=new OkxControllerClient(TimeSpan.FromSeconds(2),TimeSpan.FromSeconds(5));await Open(p,c);
        var start=c.CommandAsync("START");var old=await p.Read();var stop=c.CommandAsync("STOP");var command=await p.Read();await p.Reply(command);await stop;
        await p.Reply(old,"RUNNING");await start;Assert.Equal("STOP",c.View.Mode);Assert.Equal("confirmed",c.View.Outcome);
    }
    [Theory(Timeout=10000)][InlineData("EOF")][InlineData("TIMEOUT")][InlineData("UNKNOWN")]
    public async Task Mutation_uncertainty_retains_stale_snapshot_and_never_replays(string cause)
    {
        await using var p=new SessionTests.Peer();await using var c=new OkxControllerClient(TimeSpan.FromMilliseconds(300),TimeSpan.FromSeconds(5));await Open(p,c);
        var before=c.View.Snapshot;var mutation=c.CommandAsync("START");var command=await p.Read();
        if(cause=="EOF")await p.Server.DisposeAsync();if(cause=="UNKNOWN")await p.Reply(command,outcome:"pending-or-unknown");
        var result=await mutation;Assert.Equal("pending-or-unknown",result.Outcome);Assert.True(c.View.SnapshotStale);Assert.Same(before,c.View.Snapshot);
        Assert.Equal("MANUAL_RECONNECT_REQUIRED",(await c.CommandAsync("START")).Code);Assert.Equal(0,c.View.Pending);
        if(cause!="EOF")Assert.Null(await p.Server.ReadAsync(1024,default));
    }
    [Theory(Timeout=10000)][InlineData("Epoch")][InlineData("Session")][InlineData("CommandId")]
    public async Task Foreign_receipt_refuses_session_without_accepting_remote_state(string field)
    {
        await using var p=new SessionTests.Peer();await using var c=new OkxControllerClient(TimeSpan.FromSeconds(2),TimeSpan.FromSeconds(5));await Open(p,c);
        var mutation=c.CommandAsync("START");var command=await p.Read();
        var r=new OkxControllerReceipt(OkxControllerProtocol.Version,field=="Epoch"?Guid.NewGuid().ToString("N"):p.Epoch,field=="Session"?Guid.NewGuid().ToString("N"):p.Session,field=="CommandId"?Guid.NewGuid().ToString("N"):command.CommandId,"confirmed","SYNTHETIC","RUNNING",false,null);
        await p.Server.WriteAsync(OkxControllerProtocol.EncodeReceipt(r),default);Assert.Equal("pending-or-unknown",(await mutation).Outcome);Assert.NotEqual("RUNNING",c.View.Mode);Assert.True(c.View.SnapshotStale);
    }
    [Fact(Timeout=10000)] public async Task Disposal_of_confirmed_running_closes_only_channel()
    {
        await using var p=new SessionTests.Peer();var c=new OkxControllerClient(TimeSpan.FromSeconds(2),TimeSpan.FromSeconds(5));await Open(p,c);
        var start=c.CommandAsync("START");await p.Reply(await p.Read(),"RUNNING");await start;Assert.Equal("RUNNING",c.View.Mode);
        await c.DisposeAsync();Assert.Null(await p.Server.ReadAsync(1024,default));Assert.Equal("RUNNING",c.View.Mode);Assert.True(c.View.SnapshotStale);
    }
    [Fact(Timeout=10000)] public async Task Manual_reconnect_uses_new_session_and_queries_status_without_replaying_mutation()
    {
        await using var p=new SessionTests.Peer();await using var c=new OkxControllerClient(TimeSpan.FromMilliseconds(300),TimeSpan.FromSeconds(5));await Open(p,c);
        var start=c.CommandAsync("START");var old=await p.Read();await p.Server.DisposeAsync();await start;var oldSnapshot=c.View.Snapshot;
        await using var fresh=new SessionTests.Peer();await fresh.Hello();var reconnect=c.ReconnectAsync(fresh.Client);
        var status=await fresh.Read();Assert.Equal("STATUS",status.Action);Assert.NotEqual(old.Session,status.Session);Assert.NotEqual(old.CommandId,status.CommandId);Assert.Same(oldSnapshot,c.View.Snapshot);Assert.True(c.View.SnapshotStale);
        await fresh.Reply(status,"RUNNING");var snap=await fresh.Read();Assert.Equal("SNAPSHOT",snap.Action);await fresh.Reply(snap,snapshot:OkxControllerSnapshotCodec.Disconnected(DateTimeOffset.UtcNow));await reconnect;
        Assert.False(c.View.SnapshotStale);
    }
    [Fact(Timeout=10000)] public async Task Session_budget_is_below_server_limit_and_reserves_stop_without_automatic_reconnect()
    {
        await using var p=new SessionTests.Peer();await using var c=new OkxControllerClient(TimeSpan.FromSeconds(2),TimeSpan.FromSeconds(5));await Open(p,c);
        var seen=new HashSet<string>();
        for(int i=2;i<OkxControllerClient.NormalBudget;i++){var task=c.CommandAsync("STATUS");var command=await p.Read();Assert.True(seen.Add(command.CommandId));await p.Reply(command);Assert.Equal("confirmed",(await task).Outcome);}
        Assert.Equal("NEW_SESSION_REQUIRED",(await c.CommandAsync("STATUS")).Code);
        for(int i=OkxControllerClient.NormalBudget;i<OkxControllerClient.SessionBudget;i++){var task=c.CommandAsync("STOP");var command=await p.Read();Assert.Equal("STOP",command.Action);await p.Reply(command);await task;}
        Assert.Equal("NEW_SESSION_REQUIRED",(await c.CommandAsync("EMERGENCY")).Code);Assert.True(OkxControllerClient.SessionBudget<256);
    }
    sealed class Clock:TimeProvider
    {
        internal long Stamp;public override long TimestampFrequency=>1000;public override long GetTimestamp()=>Stamp;
        public override DateTimeOffset GetUtcNow()=>DateTimeOffset.UnixEpoch;
    }
    [Fact(Timeout=10000)] public async Task Freshness_uses_monotonic_receive_time_and_does_not_rejuvenate_financial_clocks()
    {
        var clock=new Clock();await using var p=new SessionTests.Peer();await using var c=new OkxControllerClient(TimeSpan.FromSeconds(2),TimeSpan.FromSeconds(1),clock);await Open(p,c);
        var generated=c.View.Snapshot!.GeneratedUtc;clock.Stamp=1001;Assert.False(c.View.TransportFresh);Assert.True(c.View.SnapshotStale);
        var status=c.CommandAsync("STATUS");await p.Reply(await p.Read());await status;Assert.True(c.View.TransportFresh);Assert.True(c.View.SnapshotStale);Assert.Equal(generated,c.View.Snapshot.GeneratedUtc);
    }
    [Fact(Timeout=10000)] public async Task Passive_polls_are_serial_but_stop_and_emergency_remain_admitted()
    {
        await using var p=new SessionTests.Peer();await using var c=new OkxControllerClient(TimeSpan.FromSeconds(2),TimeSpan.FromSeconds(5));await Open(p,c);
        var poll=c.CommandAsync("STATUS");var first=await p.Read();Assert.Equal("POLL_ALREADY_PENDING",(await c.CommandAsync("SNAPSHOT")).Code);
        var stop=c.CommandAsync("STOP");var emergency=c.CommandAsync("EMERGENCY");var a=await p.Read();var b=await p.Read();Assert.Equal(new[]{"EMERGENCY","STOP"},new[]{a.Action,b.Action}.Order().ToArray());
        await p.Reply(b);await p.Reply(a);await p.Reply(first);await Task.WhenAll(stop,emergency,poll);Assert.True(c.View.Pending<=8);
    }
    [Fact(Timeout=10000)] public async Task Snapshot_is_consumed_by_unchanged_strict_codec_not_lenient_json()
    {
        await using var p=new SessionTests.Peer();await using var c=new OkxControllerClient(TimeSpan.FromSeconds(2),TimeSpan.FromSeconds(5));await Open(p,c);
        var task=c.CommandAsync("SNAPSHOT");var command=await p.Read();
        var r=new OkxControllerReceipt(OkxControllerProtocol.Version,p.Epoch,p.Session,command.CommandId,"confirmed","SYNTHETIC","STOP",false,OkxControllerSnapshotCodec.Disconnected(DateTimeOffset.UtcNow));
        var line=OkxControllerProtocol.EncodeReceipt(r).Replace("\"GeneratedUtc\":","\"UnexpectedField\":1,\"GeneratedUtc\":",StringComparison.Ordinal);
        await p.Server.WriteAsync(line,default);Assert.Equal("pending-or-unknown",(await task).Outcome);Assert.True(c.View.SnapshotStale);
    }
    [Fact(Timeout=10000)] public async Task Commands_cannot_be_arbitrary_remote_strings()
    {
        await using var c=new OkxControllerClient(TimeSpan.FromSeconds(2),TimeSpan.FromSeconds(5));await Assert.ThrowsAsync<ArgumentException>(()=>c.CommandAsync("START; shell"));
    }
}
public sealed class FrameTests
{
    [Theory][InlineData("\r\n")][InlineData("abcd\n")][InlineData("abc")]
    public async Task Frame_bounds_cr_and_truncated_eof_refuse(string bytes)
    {
        await using var f=new OkxControllerClientFrames(new MemoryStream(Encoding.UTF8.GetBytes(bytes)),new MemoryStream());
        await Assert.ThrowsAsync<InvalidDataException>(()=>f.ReadAsync(3,default));
    }
    [Fact] public async Task Strict_utf8_refuses_invalid_encoding()
    {
        await using var f=new OkxControllerClientFrames(new MemoryStream(new byte[]{255,10}),new MemoryStream());await Assert.ThrowsAsync<DecoderFallbackException>(()=>f.ReadAsync(3,default));
    }
    [Fact] public async Task Buffered_duplex_frames_preserve_boundaries_and_exact_cap()
    {
        using var output=new MemoryStream();await using var f=new OkxControllerClientFrames(new MemoryStream(Encoding.UTF8.GetBytes("abc\nx\n")),output);
        Assert.Equal("abc",await f.ReadAsync(3,default));Assert.Equal("x",await f.ReadAsync(3,default));Assert.Null(await f.ReadAsync(3,default));await f.WriteAsync("hello",default);Assert.Equal("hello\n",Encoding.UTF8.GetString(output.ToArray()));
    }
}
