using System.Text;
using System.Text.Json.Nodes;
using Apex.Adapters.Okx.Execution;
using Xunit;
namespace Apex.Adapters.Okx.Tests;
public sealed class OkxExecutionStreamTests
{
    [Fact] public void Stream_RealLoginVectorAndMaximumEncoding()
    {
        var scope=new OkxExecutionScope("Demo","123","123","USD","signer");
        foreach(var maximum in new[]{false,true})
        {
            var key=maximum?Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("\\\"",2048))):Encoding.ASCII.GetBytes("observation-synthetic-key");var pass=maximum?key.ToArray():Encoding.ASCII.GetBytes("observation-synthetic-pass");
            using var m=new OkxSigningMaterial(scope,"DemoTradingApi",key,Encoding.ASCII.GetBytes("observation-synthetic-secret"),pass);var dest=Enumerable.Repeat((byte)0xa5,65536).ToArray();var n=m.WriteObservationLogin(dest,"1538054050");Assert.True(n>0);
            var oracle="{\"op\":\"login\",\"args\":[{\"apiKey\":"+System.Text.Json.JsonSerializer.Serialize(Encoding.ASCII.GetString(key),new System.Text.Json.JsonSerializerOptions{Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping})+",\"passphrase\":"+System.Text.Json.JsonSerializer.Serialize(Encoding.ASCII.GetString(pass),new System.Text.Json.JsonSerializerOptions{Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping})+",\"timestamp\":\"1538054050\",\"sign\":\"a47Jh9oGPiptdGej/hsWal0p1UuB1OJcWHW25Zif+80=\"}]}";
            Assert.Equal(Encoding.UTF8.GetBytes(oracle),dest[..n]);System.Security.Cryptography.CryptographicOperations.ZeroMemory(dest);
            var partial=Enumerable.Repeat((byte)0xa5,10).ToArray();Assert.Throws<ArgumentException>(()=>m.WriteObservationLogin(partial,"1538054050"));Assert.All(partial,b=>Assert.Equal(0,b));
        }
    }
    [Theory][InlineData("Demo")][InlineData("Live")]
    public async Task Stream_PairedLoginAndPopulatedChannels(string environment)
    {
        await using var f=new OkxExecutionWebSocketFixture(environment);await using var s=f.Stream();var events=new List<OkxStreamObservation>();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach(var e in s.ObserveAsync(f.Context,timeout.Token))
        {
            events.Add(e);
            if(e.Kind==OkxStreamObservationKind.Buffering){await f.Push(OkxStreamLane.PrivateOrders,f.Update("orders").ToJsonString());await f.Push(OkxStreamLane.BusinessAlgos,f.Update("algos").ToJsonString());}
            if(events.Any(x=>x.Kind==OkxStreamObservationKind.OrdersUpdate)&&events.Any(x=>x.Kind==OkxStreamObservationKind.AlgoUpdate))break;
        }
        Assert.Contains(events,e=>e.Kind==OkxStreamObservationKind.Buffering);Assert.Equal(2,f.Connects.Count);Assert.Equal(2,f.Logins.Count);Assert.Equal(2,f.Subscriptions.Count);
        Assert.Equal(new[]{"private-conn","business-conn"}.Order(),events.Where(x=>x.Kind==OkxStreamObservationKind.LoginAccepted).Select(x=>x.ConnId).Order());
        var order=Assert.Single(Assert.Single(events,e=>e.Kind==OkxStreamObservationKind.OrdersUpdate).Orders);Assert.Equal("SPOT",order.InstType);Assert.Equal("BTC-USDT",order.InstId);
        var algo=Assert.Single(Assert.Single(events,e=>e.Kind==OkxStreamObservationKind.AlgoUpdate).Algos);Assert.Equal("USDT",algo.Algo.TradeQuoteCcy);Assert.Equal("",algo.Algo.CloseFraction.Raw);Assert.Equal("",algo.Algo.QuickMgnType);
        Assert.Single(events.Select(e=>e.Epoch).Distinct());Assert.Equal(events.Select(e=>e.Sequence).Order(),events.Select(e=>e.Sequence));Assert.Equal(events.Count,events.Select(e=>e.Sequence).Distinct().Count());Assert.True(s.ReadState().HasGap);
    }
    private static async Task Until(Func<bool> predicate)
    {using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));while(!predicate()){timeout.Token.ThrowIfCancellationRequested();await Task.Yield();}}
    private static async Task Buffering(IAsyncEnumerator<OkxStreamObservation> e)
    {while(await e.MoveNextAsync()){if(e.Current.Kind==OkxStreamObservationKind.Buffering)return;}Assert.Fail("No paired Buffering");}
    [Fact] public async Task Stream_LatePongAtIndependentWallDeadlineCannotResurrect()
    {
        var clock=new StreamTestClock();await using var f=new OkxExecutionWebSocketFixture(clock:clock);f.AutoPong=false;await using var s=f.Stream();await using var e=s.ObserveAsync(f.Context,default).GetAsyncEnumerator();
        var opening=Buffering(e);for(int tick=0;!opening.IsCompleted&&tick<30;tick++){clock.Advance(TimeSpan.FromMilliseconds(100));await Task.Delay(5);}await opening;
        clock.Advance(TimeSpan.FromSeconds(15));await Until(()=>Volatile.Read(ref f.Pings)==2);
        clock.Advance(TimeSpan.FromSeconds(5),wall:true,monotonic:false);
        await f.Push(OkxStreamLane.PrivateOrders,"pong");
        // Receipt itself, not the next periodic tick, must enforce the independent pending-pong deadline.
        await Until(()=>s.ReadState().HasGap||!PendingPing(s,0));Assert.True(s.ReadState().HasGap);Assert.Equal("HeartbeatTimeout",s.ReadState().ErrorCode);
        await s.DisposeAsync();Assert.Equal(0,clock.TimerCount);
    }
    private static bool PendingPing(OkxObservationStream s,int index)
    {
        var lanes=(Array)typeof(OkxObservationStream).GetField("lanes",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(s)!;var lane=lanes.GetValue(index)!;return (bool)lane.GetType().GetField("PingPending",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(lane)!;
    }
    [Theory][InlineData("count")][InlineData("bytes")]
    public async Task Stream_BackpressureCannotHideGap(string cap)
    {
        await using var f=new OkxExecutionWebSocketFixture();await using var s=f.Stream();await using var e=s.ObserveAsync(f.Context,default).GetAsyncEnumerator();await Buffering(e);
        var wire=f.Update("orders");if(cap=="bytes")wire["data"]![0]!["msg"]=new string('x',900000);var text=wire.ToJsonString();int expected=cap=="count"?64:4;
        for(int n=0;n<expected;n++){var seq=s.ReadState().Sequence;await f.Push(OkxStreamLane.PrivateOrders,text);await Until(()=>s.ReadState().Sequence>seq);Assert.False(s.ReadState().HasGap);}
        await f.Push(OkxStreamLane.PrivateOrders,text);await f.Gap.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal("BufferOverflow",s.ReadState().ErrorCode);Assert.False(s.ReadState().PairedSubscribed);
        var events=new List<OkxStreamObservation>();while(await e.MoveNextAsync())events.Add(e.Current);
        Assert.Equal(expected,events.Count(x=>x.Kind==OkxStreamObservationKind.OrdersUpdate));Assert.Equal(new[]{OkxStreamObservationKind.Gap,OkxStreamObservationKind.Closed},events.TakeLast(2).Select(x=>x.Kind));Assert.Single(f.Gaps);Assert.Equal(2,f.Connects.Count);
    }
    [Theory][InlineData("valid")][InlineData("binary")][InlineData("utf")][InlineData("duplicate")][InlineData("surrogate")][InlineData("rows")][InlineData("links")][InlineData("attachments")][InlineData("bytes")][InlineData("fragments")]
    public async Task Stream_FragmentationUsesOneBoundedMessage(string variant)
    {
        await using var f=new OkxExecutionWebSocketFixture();await using var s=f.Stream();await using var e=s.ObserveAsync(f.Context,default).GetAsyncEnumerator();await Buffering(e);
        var w=f.Update("orders");w["data"]![0]!["msg"]="zażółć😀";if(variant=="rows"){var row=w["data"]![0]!.DeepClone();for(int i=1;i<101;i++)w["data"]!.AsArray().Add(row.DeepClone());}
        if(variant=="attachments")w["data"]![0]!["attachAlgoOrds"]=new JsonArray(Enumerable.Range(0,33).Select(_=>(JsonNode)new JsonObject()).ToArray());
        if(variant=="links"){w=f.Update("algos");w["data"]![0]!["ordIdList"]=new JsonArray(Enumerable.Repeat((JsonNode?)JsonValue.Create("1"),257).Select(x=>x!.DeepClone()).ToArray());}
        if(variant=="bytes")w["data"]![0]!["msg"]=new string('x',1048576);
        if(variant=="surrogate")w["data"]![0]!["msg"]="LONE_SENTINEL";string text=w.ToJsonString();if(variant=="duplicate")text=text.Replace("\"channel\":","\"channel\":\"orders\",\"channel\":");if(variant=="surrogate")text=text.Replace("LONE_SENTINEL","\\uD800");
        var bytes=variant=="utf"?new byte[]{0xff,0xfe}:Encoding.UTF8.GetBytes(text);var lane=variant=="links"?OkxStreamLane.BusinessAlgos:OkxStreamLane.PrivateOrders;
        try{await f.PushBytes(lane,bytes,variant=="binary"?System.Net.WebSockets.WebSocketMessageType.Binary:System.Net.WebSockets.WebSocketMessageType.Text,variant=="fragments"?1:variant=="valid"?7:65536);}catch(System.Net.WebSockets.WebSocketException) when(variant!="valid"){}
        var seen=new List<OkxStreamObservation>();while(await e.MoveNextAsync()){seen.Add(e.Current);if(variant=="valid"&&e.Current.Kind==OkxStreamObservationKind.OrdersUpdate)break;}
        if(variant=="valid"){Assert.Single(seen,x=>x.Kind==OkxStreamObservationKind.OrdersUpdate);Assert.False(s.ReadState().HasGap);}else{Assert.DoesNotContain(seen,x=>x.Kind is OkxStreamObservationKind.OrdersUpdate or OkxStreamObservationKind.AlgoUpdate);Assert.True(s.ReadState().HasGap);}
    }
    [Theory][InlineData("original-algo")][InlineData("uid")][InlineData("lane")][InlineData("count-error")][InlineData("unsubscribe")][InlineData("notice64008")][InlineData("notice64010")][InlineData("unknown")][InlineData("close")]
    public async Task Stream_ControlTerminationRevokesPair(string variant)
    {
        await using var f=new OkxExecutionWebSocketFixture();await using var s=f.Stream();await using var e=s.ObserveAsync(f.Context,default).GetAsyncEnumerator();await Buffering(e);var lane=OkxStreamLane.BusinessAlgos;var w=f.Update("algos");
        string text=w.ToJsonString();if(variant=="original-algo")text=f.Update("algos",true).ToJsonString();else if(variant=="uid"){w["arg"]!["uid"]="wrong";text=w.ToJsonString();}else if(variant=="lane")lane=OkxStreamLane.PrivateOrders;else if(variant=="close")await f.Peers[lane].CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,"",default);else if(variant.StartsWith("notice"))text="{\"event\":\"notice\",\"code\":\""+variant[6..]+"\",\"msg\":\"PRIVATE_SENTINEL\"}";else text="{\"event\":\""+(variant=="count-error"?"channel-conn-count-error":variant)+"\",\"channel\":\"orders-algo\",\"connId\":\"business-conn\"}";
        if(variant!="close")await f.Push(lane,text);await f.Gap.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.True(s.ReadState().HasGap);Assert.False(s.ReadState().PairedSubscribed);Assert.DoesNotContain("PRIVATE_SENTINEL",s.ReadState().ToString());var events=new List<OkxStreamObservation>();while(await e.MoveNextAsync())events.Add(e.Current);Assert.Single(events,x=>x.Kind==OkxStreamObservationKind.Gap);Assert.Equal(2,f.Connects.Count);
    }
    [Fact] public async Task Stream_OrdersAreCurrentUpdatesNotRestFills()
    {
        await using var f=new OkxExecutionWebSocketFixture();await using var s=f.Stream();await using var e=s.ObserveAsync(f.Context,default).GetAsyncEnumerator();await Buffering(e);var w=f.Update("orders");var row=w["data"]![0]!;row["accFillSz"]="4";row["fee"]="-0.4";row["fillSz"]="1";row["fillFee"]="-0.1";
        for(int n=0;n<3;n++){if(n==1)row["uTime"]="1654084353265";if(n==2){row["fillSz"]="0";row["tradeId"]="";}await f.Push(OkxStreamLane.PrivateOrders,w.ToJsonString());Assert.True(await e.MoveNextAsync());var dto=Assert.Single(e.Current.Orders);Assert.Equal(4m,dto.AccFillSz.Value);Assert.Equal(-0.4m,dto.Fee.Value);Assert.Equal(n==2?0m:1m,dto.FillSz.Value);Assert.Equal(-0.1m,dto.FillFee.Value);Assert.Equal(n==2?"":"242589207",dto.TradeId);}
        Assert.DoesNotContain(typeof(OkxExecutionOrderUpdateDto).GetProperties(),p=>p.Name is "BillId" or "Receipt" or "IsFinal");
    }
    private static byte[][] OwnedBuffers(OkxObservationStream s)
    {
        var lanes=(Array)typeof(OkxObservationStream).GetField("lanes",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(s)!;
        return lanes.Cast<object>().SelectMany(l=>new[]{(byte[])l.GetType().GetField("Login",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(l)!,(byte[])l.GetType().GetField("Message",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(l)!}).ToArray();
    }
    [Theory][InlineData("utc")][InlineData("mono")]
    public async Task Stream_FinalBoundaryClockChangeAfterEncodingNeverSends(string change)
    {
        var clock=new StreamTestClock();await using var f=new OkxExecutionWebSocketFixture(clock:clock);await using var s=f.Stream();var buffers=OwnedBuffers(s);int checkpoints=0,sends=0;
        f.OnFence=()=>{if(buffers.Any(b=>b.Length==65536&&b[0]==(byte)'{')&&Interlocked.Increment(ref checkpoints)==2)clock.Advance(TimeSpan.FromSeconds(5),wall:change=="utc",monotonic:change=="mono");};
        f.OnSend=b=>{Interlocked.Increment(ref sends);return Task.FromException(new IOException("InjectedForbiddenSend"));};var run=Task.Run(async()=>{await foreach(var item in s.ObserveAsync(f.Context,default)){} });
        for(int tick=0;!run.IsCompleted&&tick<40;tick++){clock.Advance(TimeSpan.FromMilliseconds(100));await Task.Delay(5);}await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0,sends);Assert.True(checkpoints>=2);Assert.True(s.ReadState().HasGap);Assert.All(buffers,b=>Assert.True(b.All(x=>x==0)));Assert.Equal(0,clock.TimerCount);
    }
    [Theory][InlineData("success")][InlineData("send-fault")][InlineData("provider-fault")]
    public async Task Stream_DisposeDrainsAllOwnedResources(string variant)
    {
        await using var f=new OkxExecutionWebSocketFixture();f.KeyBytes=Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("\\\"",2048)));f.PassBytes=f.KeyBytes.ToArray();f.SecretBytes=Enumerable.Repeat((byte)'s',4096).ToArray();await using var s=f.Stream();var buffers=OwnedBuffers(s);foreach(var b in buffers)Array.Fill(b,(byte)0xa5);byte[]? actual=null;
        f.OnSend=b=>{actual=b.Array;return variant=="send-fault"?Task.FromException(new IOException("PRIVATE_SEND_SENTINEL")):null;};if(variant=="provider-fault")f.ProviderError=new Exception("PRIVATE_PROVIDER_SENTINEL");
        await foreach(var item in s.ObserveAsync(f.Context,default)){if(variant=="success"&&item.Kind==OkxStreamObservationKind.Buffering)break;}
        var first=s.DisposeAsync().AsTask();var second=s.DisposeAsync().AsTask();Assert.Same(first,second);await first;
        Assert.True(s.ReadState().HasGap);Assert.True(s.ReadState().Closed);Assert.Single(f.Gaps);Assert.All(buffers,b=>Assert.True(b.All(x=>x==0)));if(variant!="provider-fault"){Assert.NotNull(actual);Assert.Equal(65536,actual!.Length);Assert.True(actual.All(x=>x==0));}else Assert.Empty(f.Logins);
        Assert.DoesNotContain("PRIVATE_",s.ReadState().ErrorCode??"");
        using var material=new OkxSigningMaterial(f.Scope,"DemoTradingApi",f.KeyBytes,f.SecretBytes,f.PassBytes);var partial=Enumerable.Repeat((byte)0xa5,5000).ToArray();Assert.Throws<ArgumentException>(()=>material.WriteObservationLogin(partial,"1538054050"));Assert.True(partial.All(x=>x==0));
    }
    [Fact] public async Task Stream_ProgrammingErrorsOutsideParserAndProviderRemainObservable()
    {
        await using var f=new OkxExecutionWebSocketFixture();var s=OkxObservationStream.ForTests((uri,ct)=>throw new InvalidOperationException("ProgrammingBoundary"),TimeProvider.System,f.Scope,f,f.Fingerprint,f);
        var failure=await Assert.ThrowsAsync<InvalidOperationException>(async()=>{await foreach(var item in s.ObserveAsync(f.Context,default)){} });Assert.Equal("ProgrammingBoundary",failure.Message);Assert.True(s.ReadState().HasGap);Assert.Equal(0,f.Opens);await Assert.ThrowsAsync<InvalidOperationException>(()=>s.DisposeAsync().AsTask());
    }
    [Fact] public async Task Stream_OriginalPairDeadlineExpiresWhileGlobalGateIsHeld()
    {
        var clock=new StreamTestClock();await using var f=new OkxExecutionWebSocketFixture(clock:clock);await using var s=f.Stream();
        var gate=(SemaphoreSlim)typeof(OkxObservationStream).GetField("ConnectGate",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;await gate.WaitAsync();
        var run=Task.Run(async()=>{await foreach(var item in s.ObserveAsync(f.Context,default)){} });
        try{await Until(()=>s.ReadState().Epoch is not null);clock.Advance(TimeSpan.FromSeconds(20));Assert.True(s.ReadState().HasGap);Assert.Equal("HandshakeTimeout",s.ReadState().ErrorCode);Assert.Empty(f.Connects);Assert.Equal(0,f.Opens);}
        finally{gate.Release();await s.DisposeAsync();await run;}
        Assert.Equal(0,clock.TimerCount);
    }
    [Fact] public async Task Stream_TimerFenceExceptionIsObservableAfterDrainNotThrownOnTimer()
    {
        var clock=new StreamTestClock();await using var f=new OkxExecutionWebSocketFixture(clock:clock);f.GapAction=()=>throw new InvalidOperationException("FenceCallbackBoundary");var s=f.Stream();
        var gate=(SemaphoreSlim)typeof(OkxObservationStream).GetField("ConnectGate",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;await gate.WaitAsync();var run=Task.Run(async()=>{await foreach(var item in s.ObserveAsync(f.Context,default)){} });
        Exception? timerError;
        try{await Until(()=>s.ReadState().Epoch is not null);timerError=Record.Exception(()=>clock.Advance(TimeSpan.FromSeconds(20)));}
        finally{gate.Release();}
        try{await run;}catch(InvalidOperationException){}
        try{await s.DisposeAsync();}catch(InvalidOperationException){}
        Assert.Null(timerError);Assert.True(s.ReadState().Closed);Assert.Equal(0,clock.TimerCount);Assert.True(run.IsFaulted);Assert.Contains("FenceCallbackBoundary",run.Exception!.ToString());
    }
    [Theory][InlineData("orders")][InlineData("algos")]
    public async Task Stream_AllOriginalTablePathsAreIndependentSchemas(string name)
    {
        await using var f=new OkxExecutionWebSocketFixture();await using var s=f.Stream();await using var e=s.ObserveAsync(f.Context,default).GetAsyncEnumerator();await Buffering(e);
        var w=OkxExecutionAlgoObservationTests.PopulateTableOnly(name,f.Update(name));await f.Push(name=="orders"?OkxStreamLane.PrivateOrders:OkxStreamLane.BusinessAlgos,w.ToJsonString());Assert.True(await e.MoveNextAsync());Assert.Equal(name=="orders"?OkxStreamObservationKind.OrdersUpdate:OkxStreamObservationKind.AlgoUpdate,e.Current.Kind);Assert.False(s.ReadState().HasGap);
    }
    [Theory][InlineData("orders","linkedAlgoOrd","{\"algoId\":{}}")][InlineData("orders","linkedAlgoOrd","{\"algoId\":null}")][InlineData("orders","linkedAlgoOrd","{\"algoId\":0}")][InlineData("orders","attachAlgoOrds","[{\"failCode\":\"0\"}]")][InlineData("algos","closeFraction","0")][InlineData("algos","quickMgnType","false")][InlineData("algos","triggerParams","[]")]
    public void Stream_RatifiedTypesDoNotBroadenChannels(string name,string field,string value)
    {
        var w=OkxExecutionAlgoObservationTests.Control(name);w["arg"]!["uid"]="123";w["arg"]!["instType"]="SPOT";w["arg"]!["instId"]=w["data"]![0]!["instId"]!.GetValue<string>();w["data"]![0]![field]=JsonNode.Parse(value);
        Assert.ThrowsAny<Exception>(()=>OkxObservationStreamJson.Parse(Encoding.UTF8.GetBytes(w.ToJsonString()),name=="orders"?OkxStreamLane.PrivateOrders:OkxStreamLane.BusinessAlgos,"123","conn",2));
    }
    [Fact] public async Task Stream_RestDelayPreservesNewerEventsWithoutCoverage()
    {
        var clock=new ObservationTestClock();clock.Utc=DateTimeOffset.UtcNow+TimeSpan.FromSeconds(2);
        using var http=new OkxExecutionObservationHttpFixture(clock);await using var f=new OkxExecutionWebSocketFixture(clock:clock,scope:http.Scope,context:http.Context);f.InspectLogin=_=>{clock.Advance(TimeSpan.FromSeconds(1));return Task.CompletedTask;};await using var stream=f.Stream();await using var e=stream.ObserveAsync(f.Context,default).GetAsyncEnumerator();await Buffering(e);
        var order=f.Update("orders");order["data"]![0]!["uTime"]="20";var before=stream.ReadState().Sequence;await f.Push(OkxStreamLane.PrivateOrders,order.ToJsonString());await Until(()=>stream.ReadState().Sequence>before);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var head=JsonNode.Parse("{\"code\":\"0\",\"msg\":\"\",\"data\":[{\"instType\":\"SPOT\",\"instId\":\"BTC-USDT\",\"ordId\":\"452197707845865472\",\"clOrdId\":\"\",\"cTime\":\"1\",\"uTime\":\"1\"}]}")!;
        const string path="/api/v5/trade/orders-pending?instType=SPOT&limit=1";
        http.Steps.Enqueue((path,async ct=>{entered.SetResult();await release.Task.WaitAsync(ct);return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new StringContent(head.ToJsonString())};}));
        OkxExecutionAlgoObservationTests.Add(http,"/api/v5/trade/orders-pending?after=452197707845865472&instType=SPOT&limit=1",JsonNode.Parse("{\"code\":\"0\",\"msg\":\"\",\"data\":[]}")!);OkxExecutionAlgoObservationTests.Add(http,path,head);
        await using var client=http.Client();var rest=new OkxObservationPager(client).CollectAsync(new(OkxObservationSurface.Pending,new(null,null,null,null,null,1),http.Context),default);var firstBoundary=await Task.WhenAny(rest,entered.Task).WaitAsync(TimeSpan.FromSeconds(5));if(firstBoundary==rest){var refused=await rest;Assert.Fail("REST refused before held handler: "+refused.ErrorCode+":"+string.Join(",",refused.Trace.Select(x=>x.ErrorCode)));}await entered.Task;
        order["data"]![0]!["uTime"]="30";await f.Push(OkxStreamLane.PrivateOrders,order.ToJsonString());var algo=f.Update("algos");algo["data"]![0]!["triggerTime"]="31";await f.Push(OkxStreamLane.BusinessAlgos,algo.ToJsonString());release.SetResult();var r=await rest;Assert.True(r.Status==OkxObservationCollectionStatus.Collected,r.ErrorCode+":"+string.Join(",",r.Trace.Select(x=>x.ErrorCode)));Assert.Equal("1",Assert.Single(r.Orders).UTime.Raw);
        order["data"]![0]!["uTime"]="40";await f.Push(OkxStreamLane.PrivateOrders,order.ToJsonString());var observed=new List<OkxStreamObservation>();while(observed.Count<4){Assert.True(await e.MoveNextAsync());observed.Add(e.Current);}
        Assert.Equal(new[]{"20","30","40"},observed.SelectMany(x=>x.Orders).Select(x=>x.UTime.Raw));Assert.Equal("31",Assert.Single(observed.SelectMany(x=>x.Algos)).Algo.TriggerTime.Raw);Assert.True(observed.All(x=>x.Context==http.Context));Assert.Single(observed.Select(x=>x.Epoch).Distinct());Assert.True(stream.ReadState().PairedSubscribed);Assert.False(stream.ReadState().HasGap);http.Done();
    }
    [Fact] public async Task Stream_ExplicitReplacementHasFreshEpochNotAuthority()
    {
        await using var old=new OkxExecutionWebSocketFixture();Guid? previous;
        await using(var first=old.Stream()){await using var e=first.ObserveAsync(old.Context,default).GetAsyncEnumerator();await Buffering(e);previous=first.ReadState().Epoch;await old.Push(OkxStreamLane.BusinessAlgos,"{\"event\":\"error\"}");await old.Gap.Task.WaitAsync(TimeSpan.FromSeconds(5));while(await e.MoveNextAsync()){}Assert.True(first.ReadState().HasGap);}
        await using(var revoked=old.Stream()){await foreach(var item in revoked.ObserveAsync(old.Context,default)){}Assert.Equal(2,old.Connects.Count);Assert.Equal(2,old.Opens);}
        await using var fresh=new OkxExecutionWebSocketFixture(scope:old.Scope);await using var replacement=fresh.Stream();await using var next=replacement.ObserveAsync(fresh.Context,default).GetAsyncEnumerator();await Buffering(next);Assert.NotEqual(old.Context.ConnectionId,fresh.Context.ConnectionId);Assert.NotEqual(previous,replacement.ReadState().Epoch);Assert.True(replacement.ReadState().PairedSubscribed);Assert.Equal(2,fresh.Logins.Count);
        using var http=new OkxExecutionObservationHttpFixture();http.Scope=old.Scope;OkxExecutionAlgoObservationTests.Add(http,"/api/v5/trade/orders-algo-pending?instType=SPOT&limit=100&ordType=conditional",OkxExecutionAlgoObservationTests.Rows());OkxExecutionAlgoObservationTests.Add(http,"/api/v5/trade/orders-algo-pending?instType=SPOT&limit=100&ordType=conditional",OkxExecutionAlgoObservationTests.Rows());await using var rest=http.Client();Assert.Equal(OkxObservationCollectionStatus.Collected,(await new OkxObservationPager(rest).CollectAlgosAsync(OkxExecutionAlgoObservationTests.Page(),http.Context,default)).Status);http.Done();
        Assert.DoesNotContain(typeof(OkxStreamSnapshot).GetProperties(),p=>p.Name is "Ready" or "Complete" or "CanStart");
    }
    [Theory][InlineData("success")][InlineData("silent")][InlineData("data-not-pong")][InlineData("rollback")]
    public async Task Stream_TextPingPongIsBounded(string variant)
    {
        var clock=new StreamTestClock();await using var f=new OkxExecutionWebSocketFixture(clock:clock);f.AutoPong=variant=="success";await using var s=f.Stream();await using var e=s.ObserveAsync(f.Context,default).GetAsyncEnumerator();var opening=Buffering(e);for(int tick=0;!opening.IsCompleted&&tick<50;tick++){clock.Advance(TimeSpan.FromMilliseconds(100));await Task.Delay(5);}await opening;
        clock.Advance(TimeSpan.FromSeconds(15));await Until(()=>Volatile.Read(ref f.Pings)==2);
        if(variant=="success"){await Until(()=>!PendingPing(s,0)&&!PendingPing(s,1));Assert.False(s.ReadState().HasGap);clock.Advance(TimeSpan.FromSeconds(15));await Until(()=>Volatile.Read(ref f.Pings)==4);await Until(()=>!PendingPing(s,0)&&!PendingPing(s,1));Assert.True(s.ReadState().PairedSubscribed);}
        else {if(variant=="data-not-pong"){var before=s.ReadState().Sequence;await f.Push(OkxStreamLane.PrivateOrders,f.Update("orders").ToJsonString());await Until(()=>s.ReadState().Sequence>before);Assert.True(PendingPing(s,0));}if(variant=="rollback"){clock.Advance(-TimeSpan.FromSeconds(1),wall:true,monotonic:false);clock.Advance(TimeSpan.FromMilliseconds(250),wall:false);}else clock.Advance(TimeSpan.FromSeconds(5));await f.Gap.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(variant=="rollback"?"ClockRollback":"HeartbeatTimeout",s.ReadState().ErrorCode);}
        await s.DisposeAsync();Assert.Equal(0,clock.TimerCount);
    }
    [Theory][InlineData("wall")][InlineData("mono")]
    public async Task Stream_SlowTrickleAssemblyHasIndependentDeadline(string clockSide)
    {
        var clock=new StreamTestClock();await using var f=new OkxExecutionWebSocketFixture(clock:clock);await using var s=f.Stream();await using var e=s.ObserveAsync(f.Context,default).GetAsyncEnumerator();var opening=Buffering(e);for(int tick=0;!opening.IsCompleted&&tick<50;tick++){clock.Advance(TimeSpan.FromMilliseconds(100));await Task.Delay(5);}await opening;
        var bytes=Encoding.UTF8.GetBytes(f.Update("orders").ToJsonString());await f.Peers[OkxStreamLane.PrivateOrders].SendAsync(new ArraySegment<byte>(bytes,0,1),System.Net.WebSockets.WebSocketMessageType.Text,false,default);await Until(()=>clock.TimerCount>=3);
        clock.Advance(TimeSpan.FromSeconds(5),wall:clockSide=="wall",monotonic:clockSide=="mono");if(clockSide=="wall")await f.Peers[OkxStreamLane.PrivateOrders].SendAsync(new ArraySegment<byte>(bytes,1,bytes.Length-1),System.Net.WebSockets.WebSocketMessageType.Text,true,default);
        await f.Gap.Task.WaitAsync(TimeSpan.FromSeconds(5));var observed=new List<OkxStreamObservation>();while(await e.MoveNextAsync())observed.Add(e.Current);Assert.DoesNotContain(observed,x=>x.Kind==OkxStreamObservationKind.OrdersUpdate);Assert.True(s.ReadState().HasGap);Assert.Equal(0,clock.TimerCount);
    }
    [Fact] public async Task Stream_MaximumMessageAndInformationalCountAreAdmitted()
    {
        await using var f=new OkxExecutionWebSocketFixture();await using var s=f.Stream();await using var e=s.ObserveAsync(f.Context,default).GetAsyncEnumerator();await Buffering(e);
        await f.Push(OkxStreamLane.PrivateOrders,"{\"event\":\"channel-conn-count\",\"channel\":\"orders\",\"connCount\":\"2\",\"connId\":\"private-conn\"}");var w=f.Update("orders");w["data"]![0]!["msg"]="";var overhead=Encoding.UTF8.GetByteCount(w.ToJsonString());w["data"]![0]!["msg"]=new string('x',1048576-overhead);var bytes=Encoding.UTF8.GetBytes(w.ToJsonString());Assert.Equal(1048576,bytes.Length);await f.PushBytes(OkxStreamLane.PrivateOrders,bytes);Assert.True(await e.MoveNextAsync());Assert.Equal(OkxStreamObservationKind.OrdersUpdate,e.Current.Kind);Assert.False(s.ReadState().HasGap);
    }
    [Theory][InlineData("stop")][InlineData("cancel")][InlineData("dispose")][InlineData("fingerprint")]
    public async Task Stream_FinalBoundaryStopWins(string variant)
    {
        await using var f=new OkxExecutionWebSocketFixture();await using var s=f.Stream();using var cancel=new CancellationTokenSource();Task? disposal=null;int reached=0,sends=0;var buffers=OwnedBuffers(s);
        f.OnFence=()=>{if(buffers.Any(b=>b.Length==65536&&b[0]==(byte)'{')&&Interlocked.Exchange(ref reached,1)==0){if(variant=="stop")f.Allowed=false;else if(variant=="cancel")cancel.Cancel();else if(variant=="dispose")disposal=s.DisposeAsync().AsTask();}};
        if(variant=="fingerprint")f.OnOpen=()=>f.KeyBytes=Encoding.ASCII.GetBytes("different-key");f.OnSend=b=>{Interlocked.Increment(ref sends);return null;};
        await foreach(var item in s.ObserveAsync(f.Context,cancel.Token)){}if(disposal is not null)await disposal;
        Assert.Equal(variant=="fingerprint"?0:1,reached);Assert.Equal(0,sends);Assert.True(s.ReadState().HasGap);Assert.True(buffers.All(b=>b.All(x=>x==0)));
    }
    [Fact] public async Task Stream_ActiveUidSlotIsSharedAcrossCredentialBindings()
    {
        await using var a=new OkxExecutionWebSocketFixture();await using var first=a.Stream();await using var e=first.ObserveAsync(a.Context,default).GetAsyncEnumerator();await Buffering(e);
        var sameUid=new OkxExecutionScope(a.Scope.Environment,a.Scope.Uid,a.Scope.MainUid,"USD","different-binding");await using var b=new OkxExecutionWebSocketFixture(scope:sameUid);await using var second=b.Stream();await foreach(var item in second.ObserveAsync(b.Context,default)){}Assert.Equal("StreamAlreadyActive",second.ReadState().ErrorCode);Assert.Empty(b.Connects);Assert.Equal(0,b.Opens);Assert.True(first.ReadState().PairedSubscribed);
    }
    [Theory][InlineData("split-utf8")][InlineData("changed-type")][InlineData("zero-fragments")]
    public async Task Stream_RawBclFramesPreserveUtf8AndBoundEmptyFragments(string variant)
    {
        await using var f=new OkxExecutionWebSocketFixture();await using var s=f.Stream();await using var e=s.ObserveAsync(f.Context,default).GetAsyncEnumerator();await Buffering(e);var lane=OkxStreamLane.PrivateOrders;
        if(variant=="zero-fragments")
        {try{for(int n=0;n<1025;n++)await f.RawFrame(lane,n==0?(byte)1:(byte)0,false,ReadOnlyMemory<byte>.Empty);}catch(IOException){}catch(System.Threading.Channels.ChannelClosedException){}await f.Gap.Task.WaitAsync(TimeSpan.FromSeconds(5));}
        else
        {
            var w=f.Update("orders");w["data"]![0]!["msg"]="é";var bytes=Encoding.UTF8.GetBytes(w.ToJsonString(new System.Text.Json.JsonSerializerOptions{Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));int split=Array.IndexOf(bytes,(byte)0xc3)+1;Assert.True(split>0);await f.RawFrame(lane,1,false,bytes.AsMemory(0,split));await f.RawFrame(lane,variant=="changed-type"?(byte)2:(byte)0,true,bytes.AsMemory(split));
        }
        var observations=new List<OkxStreamObservation>();while(await e.MoveNextAsync()){observations.Add(e.Current);if(variant=="split-utf8"&&e.Current.Kind==OkxStreamObservationKind.OrdersUpdate)break;}
        if(variant=="split-utf8"){Assert.Single(observations,x=>x.Kind==OkxStreamObservationKind.OrdersUpdate);Assert.False(s.ReadState().HasGap);}else{Assert.True(s.ReadState().HasGap);Assert.DoesNotContain(observations,x=>x.Kind==OkxStreamObservationKind.OrdersUpdate);}
    }
    [Theory][InlineData("connect")][InlineData("provider")][InlineData("send")][InlineData("login")][InlineData("subscribe")][InlineData("receive")]
    public async Task Stream_DisposalCancelsReachedOwnedPhase(string phase)
    {
        await using var f=new OkxExecutionWebSocketFixture();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if(phase=="provider")f.OpenOverride=async ct=>{entered.TrySetResult();await Task.Delay(Timeout.Infinite,ct);throw new InvalidOperationException();};
        if(phase is "login" or "subscribe")f.BeforeReply=async (name,ct)=>{if(name==phase){entered.TrySetResult();await Task.Delay(Timeout.Infinite,ct);}};
        if(phase=="send")f.BlockSend=true;
        async Task<System.Net.WebSockets.WebSocket> Connect(Uri uri,CancellationToken ct){if(phase=="connect"){entered.TrySetResult();await Task.Delay(Timeout.Infinite,ct);}return await f.Connect(uri,ct);}
        await using var s=OkxObservationStream.ForTests(Connect,TimeProvider.System,f.Scope,f,f.Fingerprint,f);var buffers=OwnedBuffers(s);
        var run=Task.Run(async()=>{await foreach(var item in s.ObserveAsync(f.Context,default)){if(item.Kind==OkxStreamObservationKind.Buffering)entered.TrySetResult();}});
        await (phase=="send"?f.SendEntered.Task:entered.Task).WaitAsync(TimeSpan.FromSeconds(10));var calls=f.Opens;var connects=f.Connects.Count;
        var first=s.DisposeAsync().AsTask();var second=s.DisposeAsync().AsTask();Assert.Same(first,second);await first.WaitAsync(TimeSpan.FromSeconds(5));await run.WaitAsync(TimeSpan.FromSeconds(5));Assert.True(s.ReadState().Closed);Assert.True(s.ReadState().HasGap);Assert.Equal("Disposed",s.ReadState().ErrorCode);Assert.Single(f.Gaps);Assert.Equal(calls,f.Opens);Assert.Equal(connects,f.Connects.Count);Assert.True(buffers.All(b=>b.All(x=>x==0)));
        var running=(Task)typeof(OkxObservationStream).GetField("running",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(s)!;Assert.True(running.IsCompleted);var active=(HashSet<string>)typeof(OkxObservationStream).GetField("Active",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!.GetValue(null)!;Assert.DoesNotContain(f.Scope.Environment+":"+f.Scope.Uid,active);var closing=(CancellationTokenSource)typeof(OkxObservationStream).GetField("stopping",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(s)!;Assert.Throws<ObjectDisposedException>(()=>{_=closing.Token;});
    }
    [Fact] public async Task Stream_FenceFaultStillDiscardsQueuedPrivateReferences()
    {
        await using var f=new OkxExecutionWebSocketFixture();var s=f.Stream();var e=s.ObserveAsync(f.Context,default).GetAsyncEnumerator();await Buffering(e);var seq=s.ReadState().Sequence;await f.Push(OkxStreamLane.PrivateOrders,f.Update("orders").ToJsonString());await Until(()=>s.ReadState().Sequence>seq);f.GapAction=()=>throw new InvalidOperationException("FenceDrainBoundary");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>s.DisposeAsync().AsTask());await Assert.ThrowsAsync<InvalidOperationException>(()=>e.DisposeAsync().AsTask());
        var queue=typeof(OkxObservationStream).GetField("queue",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(s)!;Assert.Equal(0,(int)queue.GetType().GetProperty("Count")!.GetValue(queue)!);Assert.True(OwnedBuffers(s).All(b=>b.All(x=>x==0)));Assert.True(s.ReadState().Closed);
    }
    [Fact] public async Task Stream_LazyConstructionAndSingleUse()
    {
        await using var f=new OkxExecutionWebSocketFixture();await using var s=f.Stream();var enumerable=s.ObserveAsync(f.Context,default);Assert.Equal(0,f.Opens);Assert.Empty(f.Connects);Assert.False(s.ReadState().PairedSubscribed);
        await foreach(var e in enumerable){if(e.Kind==OkxStreamObservationKind.Buffering)break;}
        Assert.Equal(2,f.Opens);await Assert.ThrowsAsync<InvalidOperationException>(async()=>{await foreach(var e in s.ObserveAsync(f.Context,default)){} });Assert.Equal(2,f.Connects.Count);
    }
}
