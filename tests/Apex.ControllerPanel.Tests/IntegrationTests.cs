using System.IO.Pipelines;
using System.Text.Json;
using System.Net;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.ControllerPanel.Tests;
public sealed class IntegrationTests
{
    static string Body(string id)=>JsonSerializer.Serialize(new{commandId=id});
    static async Task<JsonElement> Json(HttpResponseMessage r){using var d=JsonDocument.Parse(await r.Content.ReadAsStringAsync());return d.RootElement.Clone();}
    [Fact] public async Task Strict_stream_snapshot_is_projected_and_all_commands_are_exact_and_replay_safe()
    {
        await using var peer=new StrictPeer();int factories=0;
        var control=new OkxRemoteControllerControl(ct=>{Interlocked.Increment(ref factories);return Task.FromResult<IOkxControllerFrameTransport>(peer.Client);});
        await using var h=await Host.Open(control);Assert.Equal(0,factories);
        for(int i=0;i<4;i++)await h.Send("status");Assert.Empty(peer.Actions);
        var connected=await Json(await h.Send("connect","operator",Body("connect")));Assert.Equal("confirmed",connected.GetProperty("outcome").GetString());Assert.Equal(1,factories);
        var s=await Json(await h.Send("status","reader"));Assert.Equal(121.5m,s.GetProperty("snapshot").GetProperty("balances").GetProperty("items")[0].GetProperty("total").GetDecimal());Assert.Equal(17m,s.GetProperty("snapshot").GetProperty("capital").GetProperty("cash").GetDecimal());
        Assert.True(s.GetProperty("canStart").GetBoolean());
        foreach(string action in new[]{"prepare","arm","start","stop","emergency","refresh"}){
            var r=await Json(await h.Send(action,"operator",Body(action)));Assert.Equal("confirmed",r.GetProperty("outcome").GetString());
            var replay=await Json(await h.Send(action,"operator",Body(action)));Assert.Equal(r.GetRawText(),replay.GetRawText());
            if(action is "prepare" or "arm")await h.Send("refresh","operator",Body("after-"+action));
        }
        Assert.Equal(new[]{"STATUS","SNAPSHOT","PREPARE","SNAPSHOT","ARM","SNAPSHOT","START","STOP","EMERGENCY","SNAPSHOT"},peer.Actions.ToArray());
        var conflict=await Json(await h.Send("start","operator",Body("stop")));Assert.Equal("COMMAND_ID_CONFLICT",conflict.GetProperty("code").GetString());
        s=await Json(await h.Send("status"));
        var evidence=Environment.GetEnvironmentVariable("CONTROLLER_PANEL_EVIDENCE");if(evidence!=null){Directory.CreateDirectory(evidence);await File.WriteAllTextAsync(Path.Combine(evidence,"render-snapshot.json"),s.GetRawText());}
        await h.Send("disconnect","operator",Body("close"));Assert.Equal(10,peer.Actions.Count);s=await Json(await h.Send("status"));Assert.True(s.GetProperty("stale").GetBoolean());Assert.False(s.GetProperty("canStart").GetBoolean());Assert.Equal("EMERGENCY:STOP",s.GetProperty("lastConfirmedControl").GetString());
    }
    [Fact] public async Task STOP_does_not_wait_for_a_blocked_passive_snapshot_and_late_snapshot_does_not_restore_running()
    {
        await using var peer=new StrictPeer();var control=new OkxRemoteControllerControl(ct=>Task.FromResult<IOkxControllerFrameTransport>(peer.Client));await using var h=await Host.Open(control);
        await h.Send("connect","operator",Body("c"));await h.Send("start","operator",Body("go"));
        peer.BlockSnapshots=true;var refresh=h.Send("refresh","operator",Body("refresh"));await peer.SnapshotBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stop=await Json(await h.Send("stop","operator",Body("stop")));Assert.Equal("STOP",stop.GetProperty("confirmedMode").GetString());Assert.False(refresh.IsCompleted);
        var s=await Json(await h.Send("status"));Assert.Equal("STOP",s.GetProperty("mode").GetString());Assert.True(s.GetProperty("stale").GetBoolean());
        await peer.ReleaseBlocked();await refresh;s=await Json(await h.Send("status"));Assert.Equal("STOP",s.GetProperty("mode").GetString());Assert.True(s.GetProperty("stale").GetBoolean());
    }
    [Fact] public async Task Dropped_START_receipt_remains_unknown_stale_and_identical_request_never_retries()
    {
        await using var peer=new StrictPeer();var control=new OkxRemoteControllerControl(ct=>Task.FromResult<IOkxControllerFrameTransport>(peer.Client),()=>new(TimeSpan.FromMilliseconds(300),TimeSpan.FromSeconds(10)));await using var h=await Host.Open(control);
        await h.Send("connect","operator",Body("connect"));peer.DropStart=true;
        var r=await Json(await h.Send("start","operator",Body("start")));Assert.Equal("pending-or-unknown",r.GetProperty("outcome").GetString());Assert.Equal(JsonValueKind.Null,r.GetProperty("confirmedMode").ValueKind);
        var retry=await Json(await h.Send("start","operator",Body("start")));Assert.Equal(r.GetRawText(),retry.GetRawText());Assert.Single(peer.Actions.Where(a=>a=="START"));
        var s=await Json(await h.Send("status"));Assert.True(s.GetProperty("stale").GetBoolean());Assert.False(s.GetProperty("canStart").GetBoolean());Assert.Equal(JsonValueKind.Null,s.GetProperty("lastConfirmedControl").ValueKind);
    }
    [Fact] public async Task STOP_does_not_wait_for_a_synchronously_blocked_transport_factory()
    {
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        await using var control=new OkxRemoteControllerControl(ct=>{entered.Set();release.Wait(TimeSpan.FromSeconds(2));throw new IOException("SYNTHETIC_FACTORY_REFUSED");});
        var clock=System.Diagnostics.Stopwatch.StartNew();var connect=control.ExecuteAsync("connect","connect");
        try{Assert.True(clock.Elapsed<TimeSpan.FromMilliseconds(500),"Factory work held the command admission gate");Assert.True(entered.Wait(TimeSpan.FromSeconds(1)));Assert.Equal("MANUAL_RECONNECT_REQUIRED",(await control.ExecuteAsync("stop","stop").WaitAsync(TimeSpan.FromSeconds(1))).Code);}
        finally{release.Set();await connect;}
    }
    [Fact] public async Task Passive_background_poll_only_follows_connect_and_stops_on_disconnect()
    {
        await using var peer=new StrictPeer();var control=new OkxRemoteControllerControl(ct=>Task.FromResult<IOkxControllerFrameTransport>(peer.Client));await using var h=await Host.Open(control);
        await Task.Delay(250);Assert.Empty(peer.Actions);await h.Send("connect","operator",Body("connect"));await Task.Delay(2300);Assert.True(peer.Actions.Count(a=>a=="SNAPSHOT")>=2);
        await h.Send("disconnect","operator",Body("disconnect"));int count=peer.Actions.Count;await Task.Delay(2300);Assert.Equal(count,peer.Actions.Count);Assert.DoesNotContain("STOP",peer.Actions);
    }
    [Fact] public async Task Closed_payload_origin_host_reader_and_literal_route_denials_do_not_invoke_factory()
    {
        int factories=0;var control=new OkxRemoteControllerControl(ct=>{factories++;throw new IOException("PRIVATE_HOST_KEY_PATH_AND_PROVIDER_TEXT");});await using var h=await Host.Open(control);
        foreach(string action in OkxRemoteControllerEndpoints.Actions){Assert.Equal(HttpStatusCode.Forbidden,(await h.Send(action,"reader",Body(action))).StatusCode);Assert.Equal(HttpStatusCode.Forbidden,(await h.Send(action,"operator",Body(action),"https://evil.test")).StatusCode);Assert.Equal(HttpStatusCode.Forbidden,(await h.Send(action,"operator",Body(action),csrf:false)).StatusCode);}
        foreach(string body in new[]{"{}","[]","null","{\"commandId\":\"a\",\"commandId\":\"a\"}","{\"commandId\":\"a\",\"path\":\"private\"}","{\"commandId\":22}","{\"commandId\":\"../x\"}"})Assert.Equal(HttpStatusCode.BadRequest,(await h.Send("connect","operator",body)).StatusCode);
        foreach(string path in new[]{"START","start/","arbitrary","start?x=1","/../other"})Assert.NotEqual(HttpStatusCode.OK,(await h.Send(path,"operator",Body("x"))).StatusCode);
        Assert.Equal(0,factories);var r=await h.Send("connect","operator",Body("good"));Assert.DoesNotContain("PRIVATE",await r.Content.ReadAsStringAsync());Assert.Equal(1,factories);
        foreach(string asset in new[]{"remote-controller.js","remote-controller.css"}){
            using var req=new HttpRequestMessage(HttpMethod.Get,"/"+asset);Assert.Equal(HttpStatusCode.Unauthorized,(await h.Client.SendAsync(req)).StatusCode);
        }
    }
}
internal sealed class StrictPeer:IAsyncDisposable
{
    readonly Pipe up=new(),down=new();
    internal readonly OkxControllerClientFrames Client,Server;
    readonly CancellationTokenSource life=new();
    readonly string epoch=Guid.NewGuid().ToString("N"),session=Guid.NewGuid().ToString("N");
    readonly Task loop;
    internal readonly System.Collections.Concurrent.ConcurrentQueue<string> Actions=new();
    internal bool BlockSnapshots,DropStart;
    internal readonly TaskCompletionSource SnapshotBlocked=new(TaskCreationOptions.RunContinuationsAsynchronously);
    OkxControllerCommand? blocked;string mode="STOP";
    internal StrictPeer(){Client=new(down.Reader.AsStream(),up.Writer.AsStream());Server=new(up.Reader.AsStream(),down.Writer.AsStream());loop=Run();}
    internal static OkxControllerSnapshot Snapshot(string mode="STOP")
    {
        var now=DateTimeOffset.UtcNow;
        var s=OkxControllerSnapshotCodec.Disconnected(now) with{Connected=true,Mode=mode,Armed=true,Ready=true,Revision=42,Capital=new("EUR",17m,2m,15m,3m,null),Observation=new("OPERATIONAL",now,now,now,now,true,true),
            Balances=new(2,false,new[]{new OkxControllerBalance("EUR",121.5m,120m,1.5m,now,false),new OkxControllerBalance("SOL",null,null,null,null,true)}),
            Catalog=new(1,false,new[]{new OkxControllerInstrument("SOL-EUR","SOL","EUR",.01m,.01m,.01m)}),
            Inventory=new(1,false,new[]{new OkxControllerInventory("SOL-EUR",.03m,0,3m,3m,false,"UNPROTECTED_QUARANTINED_FULL_LOSS_RESERVED")}),
            Orders=new(1,false,new[]{new OkxControllerOrder("SOL-EUR","filled",.03m,.03m,-.01m,"EUR")}),
            Protection=new(1,false,new[]{new OkxControllerProtection("SOL-EUR","canceled",.03m,90m,120m)}),
            History=new(1,false,new[]{new OkxControllerFill("SOL-EUR",false,.03m,100m,.01m,"EUR",now)})};
        return OkxControllerSnapshotCodec.Decode(OkxControllerSnapshotCodec.Encode(s));
    }
    async Task Run(){try{await Server.WriteAsync(OkxControllerProtocol.EncodeHello(new(OkxControllerProtocol.Version,epoch,session)),life.Token);while(true){var line=await Server.ReadAsync(1024,life.Token);if(line==null)return;var c=OkxControllerProtocol.DecodeCommand(line);Actions.Enqueue(c.Action);if(c.Action=="SNAPSHOT"&&BlockSnapshots){blocked=c;SnapshotBlocked.TrySetResult();continue;}if(c.Action=="START"){mode="RUNNING";if(DropStart)continue;}if(c.Action is "STOP" or "EMERGENCY")mode="STOP";await Reply(c);}}catch(Exception e) when(e is OperationCanceledException or ObjectDisposedException or IOException){}}
    Task Reply(OkxControllerCommand c,string? replyMode=null){string m=replyMode??mode;return Server.WriteAsync(OkxControllerProtocol.EncodeReceipt(new(OkxControllerProtocol.Version,epoch,session,c.CommandId,"confirmed","SYNTHETIC_FIXTURE",m,true,c.Action=="SNAPSHOT"?Snapshot(m):null)),life.Token);}
    internal Task ReleaseBlocked()=>Reply(blocked!,"RUNNING");
    public async ValueTask DisposeAsync(){life.Cancel();await Client.DisposeAsync();await Server.DisposeAsync();await loop;life.Dispose();}
}
