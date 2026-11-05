using System.Net;
using System.Text;
using System.Text.Json;
using Apex.Adapters.Okx.Execution;
using Xunit;
namespace Apex.Adapters.Okx.Tests;

public sealed class OkxExecutionBoundaryTests
{
    private static int uid=700000;
    private static void Unique(OkxExecutionHttpFixture f) { string id=Interlocked.Increment(ref uid).ToString(System.Globalization.CultureInfo.InvariantCulture); f.Scope=new("Demo",id,id,"USDC","test-a"); }
    private static TaskCompletionSource<bool> Signal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Accept(OkxExecutionHttpFixture f,string id="Ab9") => f.Add("POST","/api/v5/trade/order",OkxExecutionHttpFixture.SubmitBody.Replace("Ab9",id),OkxExecutionHttpFixture.Ack(id));

    [Theory][InlineData("stop")][InlineData("cancel")][InlineData("expire")][InlineData("dispose")]
    public async Task RealSendLockQueuedRequestCannotEscapeFence(string action)
    {
        using var f=new OkxExecutionHttpFixture(); Unique(f); var entered=Signal(); var release=Signal();
        f.Steps.Enqueue(("POST","/api/v5/trade/order",OkxExecutionHttpFixture.SubmitBody,async ct=>{ entered.SetResult(true); await release.Task.WaitAsync(ct); return new(HttpStatusCode.OK) { Content=new StringContent(OkxExecutionHttpFixture.Ack()) }; }));
        var c=f.Client(); using var queuedToken=new CancellationTokenSource();
        var first=c.SubmitAsync(f.Request(),default); await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second=c.SubmitAsync(f.Request("B2",f.Clock.Now.AddSeconds(1)),queuedToken.Token);
        Assert.False(second.IsCompleted); Task? disposing=null;
        if(action=="stop") f.Allowed=false;
        if(action=="cancel") queuedToken.Cancel();
        if(action=="expire") f.Clock.Now+=TimeSpan.FromSeconds(2);
        if(action=="dispose") disposing=c.DisposeAsync().AsTask();
        release.TrySetResult(true);
        var outcomes=await Task.WhenAll(first,second).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(OkxWriteDisposition.ProvenNotSent,outcomes[1].Disposition); Assert.Single(f.Sent); f.Done();
        if(disposing is not null) await disposing.WaitAsync(TimeSpan.FromSeconds(2));
        await c.DisposeAsync(); Assert.Equal(OkxWriteDisposition.ProvenNotSent,(await c.SubmitAsync(f.Request("C3"),default)).Disposition);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task RealUidRateGateCrossBindingFinalFenceAndValidControl(bool stop)
    {
        using var firstPeer=new OkxExecutionHttpFixture(); Unique(firstPeer);
        using var secondPeer=new OkxExecutionHttpFixture(); secondPeer.Scope=new("Demo",firstPeer.Scope.Uid,firstPeer.Scope.MainUid,"USDC","test-b");
        var entered=Signal(); var release=Signal();
        firstPeer.Steps.Enqueue(("POST","/api/v5/trade/order",OkxExecutionHttpFixture.SubmitBody,async ct=>{ entered.SetResult(true); await release.Task.WaitAsync(ct); return new(HttpStatusCode.OK) { Content=new StringContent(OkxExecutionHttpFixture.Ack()) }; }));
        await using var one=firstPeer.Client(); await using var two=secondPeer.Client();
        var a=one.SubmitAsync(firstPeer.Request(),default); await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if(!stop) Accept(secondPeer,"B2");
        var b=two.SubmitAsync(secondPeer.Request("B2"),default); Assert.False(b.IsCompleted);
        secondPeer.Allowed=!stop; release.SetResult(true);
        Assert.Equal(OkxWriteDisposition.Acknowledged,(await a.WaitAsync(TimeSpan.FromSeconds(2))).Disposition);
        Assert.Equal(stop?OkxWriteDisposition.ProvenNotSent:OkxWriteDisposition.Acknowledged,(await b.WaitAsync(TimeSpan.FromSeconds(2))).Disposition);
        Assert.Equal(stop?0:1,secondPeer.Sent.Count);
        if(!stop) Assert.True(secondPeer.Sent[0].At-firstPeer.Sent[0].At>=TimeSpan.FromMilliseconds(40));
        firstPeer.Done(); secondPeer.Done();
    }
    [Theory][InlineData("stop")][InlineData("expire")][InlineData("cancel")][InlineData("dispose")][InlineData("valid")]
    public async Task FenceDeadlineAndLifetimeRecheckedAfterActualRateWait(string action)
    {
        using var f=new OkxExecutionHttpFixture(); Unique(f); var c=f.Client(); Accept(f);
        Assert.Equal(OkxWriteDisposition.Acknowledged,(await c.SubmitAsync(f.Request(),default)).Disposition);
        var entered=Signal(); var release=Signal();
        // A second client shares the process quota, not its send lock.
        using var p=new OkxExecutionHttpFixture(); p.Scope=f.Scope;
        p.Delay=async (span,ct)=>{ entered.TrySetResult(true); await release.Task.WaitAsync(ct); p.Clock.Now+=span; };
        var second=p.Client(); using var token=new CancellationTokenSource();
        if(action=="valid") Accept(p,"B2");
        var queued=second.SubmitAsync(p.Request("B2",p.Clock.Now.AddSeconds(1)),token.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if(action=="stop") p.Allowed=false;
        if(action=="expire") p.Clock.Now+=TimeSpan.FromSeconds(2);
        if(action=="cancel") token.Cancel();
        var dispose=action=="dispose"?second.DisposeAsync().AsTask():null;
        release.TrySetResult(true);
        var o=await queued.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(action=="valid"?OkxWriteDisposition.Acknowledged:OkxWriteDisposition.ProvenNotSent,o.Disposition);
        Assert.Equal(action=="valid"?1:0,p.Sent.Count); p.Done(); f.Done();
        if(dispose is not null) await dispose; await second.DisposeAsync(); await c.DisposeAsync();
    }
    [Fact] public async Task DisposingOneClientDoesNotResetQuotaAndUnrelatedOperationsRemainIndependent()
    {
        using var f=new OkxExecutionHttpFixture(); Unique(f); var c=f.Client(); Accept(f); Assert.Equal(OkxWriteDisposition.Acknowledged,(await c.SubmitAsync(f.Request(),default)).Disposition); await c.DisposeAsync();
        using var p=new OkxExecutionHttpFixture(); p.Scope=f.Scope; bool waited=false; p.Delay=(d,ct)=>{ waited=true; return p.Clock.Delay(d,ct); }; Accept(p,"B2"); await using var two=p.Client();
        Assert.Equal(OkxWriteDisposition.Acknowledged,(await two.SubmitAsync(p.Request("B2"),default)).Disposition); Assert.True(waited);
        bool cancelWaited=false; p.Delay=(d,ct)=>{ cancelWaited=true; return p.Clock.Delay(d,ct); };
        using var q=new OkxExecutionHttpFixture(); q.Scope=f.Scope; q.Delay=p.Delay;
        q.Add("POST","/api/v5/trade/cancel-order","""{"instId":"BTC-USDC","ordId":"987654","clOrdId":"Ab9"}""",OkxExecutionHttpFixture.Ack());
        await using var three=q.Client(); Assert.Equal(OkxWriteDisposition.Acknowledged,(await three.CancelAsync(OkxPreparedRequest.Cancel(q.Scope,"BTC-USDC","987654","Ab9",q.Clock.Now.AddSeconds(10)),default)).Disposition); Assert.False(cancelWaited); q.Done(); p.Done(); f.Done();
    }
    [Fact] public async Task AbsoluteDeadlineAfterSecretWaitAndBudgetAfterSecretWaitPreventSend()
    {
        using var f=new OkxExecutionHttpFixture(); Unique(f); f.OnOpen=()=>f.Clock.Now+=TimeSpan.FromSeconds(11); Accept(f);
        await using var c=f.Client(); var o=await c.SubmitAsync(f.Request(),default);
        Assert.Equal(OkxWriteDisposition.ProvenNotSent,o.Disposition); Assert.Empty(f.Sent);
    }
    [Theory]
    [InlineData("ts",null)][InlineData("ts","null")][InlineData("ts","123")]
    [InlineData("sMsg",null)][InlineData("ordId",null)][InlineData("clOrdId","null")]
    [InlineData("sCode","0")]
    public async Task RequiredItemFieldTypesCannotAcknowledge(string field,string? replacement)
    {
        using var f=new OkxExecutionHttpFixture(); var node=System.Text.Json.Nodes.JsonNode.Parse(OkxExecutionHttpFixture.Ack())!;
        var row=node["data"]![0]!.AsObject(); if(replacement is null) row.Remove(field); else row[field]=System.Text.Json.Nodes.JsonNode.Parse(replacement);
        f.SubmitReply(node.ToJsonString()); await using var c=f.Client(); Assert.Equal(OkxWriteDisposition.Unknown,(await c.SubmitAsync(f.Request(),default)).Disposition); f.Done();
    }
    [Theory][InlineData("duplicate")][InlineData("case")][InlineData("cardinality")][InlineData("topfail")][InlineData("unsafeCode")][InlineData("unknownField")]
    public async Task MalformedOrContradictoryAckIsNeverSuccess(string mutation)
    {
        using var f=new OkxExecutionHttpFixture(); var ack=OkxExecutionHttpFixture.Ack();
        var body=mutation switch {
            "duplicate"=>ack.Replace("\"sCode\":\"0\"","\"sCode\":\"0\",\"sCode\":\"0\""),
            "case"=>ack.Replace("\"sCode\":\"0\"","\"sCode\":\"0\",\"Scode\":\"0\""),
            "cardinality"=>ack.Replace("}]}","},{\"sCode\":\"0\"}]}"),
            "topfail"=>OkxExecutionHttpFixture.Ack(top:"1"),
            "unsafeCode"=>OkxExecutionHttpFixture.Ack(code:"PRIVATE_ERROR_KEY"),
            _=>ack.Replace("\"sCode\":\"0\"","\"sCode\":\"0\",\"secret\":\"PRIVATE_ERROR_KEY\"") };
        f.SubmitReply(body); await using var c=f.Client(); var o=await c.SubmitAsync(f.Request(),default); Assert.Equal(OkxWriteDisposition.Unknown,o.Disposition); Assert.DoesNotContain("PRIVATE_ERROR",JsonSerializer.Serialize(o)); f.Done();
    }
    [Fact] public async Task BodyBoundAndPrivateMessageSanitization()
    {
        using var f=new OkxExecutionHttpFixture(); f.SubmitReply(new string('X',65537)); await using var c=f.Client(); var o=await c.SubmitAsync(f.Request(),default); Assert.Equal(OkxWriteDisposition.Unknown,o.Disposition); Assert.Equal("BodyLimit",o.Reason); f.Done();
        using var p=new OkxExecutionHttpFixture(); p.SubmitReply(OkxExecutionHttpFixture.Ack(code:"59113",top:"1",order:"").Replace("\"sMsg\":\"\"","\"sMsg\":\"PRIVATE_BODY_SENTINEL\"")); await using var d=p.Client(); var e=await d.SubmitAsync(p.Request(),default); Assert.Equal(OkxWriteDisposition.Unknown,e.Disposition); Assert.Equal("59113",e.SCode); Assert.DoesNotContain("PRIVATE_BODY",JsonSerializer.Serialize(e)); p.Done();
    }
    [Fact] public async Task AttachedProtectionIsExactWireButNotCoverage()
    {
        using var f=new OkxExecutionHttpFixture(); var r=OkxPreparedRequest.Submit(f.Scope,"BTC-USDC","buy","fok","0.01","90000","Ab9",f.Clock.Now.AddSeconds(20),new("Attached9","100000","80000"));
        const string body="""{"instId":"BTC-USDC","tdMode":"cash","side":"buy","ordType":"fok","sz":"0.01","px":"90000","clOrdId":"Ab9","tradeQuoteCcy":"USDC","attachAlgoOrds":[{"attachAlgoClOrdId":"Attached9","tpTriggerPx":"100000","tpOrdPx":"-1","tpTriggerPxType":"last","slTriggerPx":"80000","slOrdPx":"-1","slTriggerPxType":"last"}]}""";
        f.Add("POST","/api/v5/trade/order",body,OkxExecutionHttpFixture.Ack()); await using var c=f.Client(); var o=await c.SubmitAsync(r,default); Assert.Equal(OkxWriteDisposition.Acknowledged,o.Disposition); f.Done();
        Assert.Equal(body,Encoding.UTF8.GetString(OkxPreparedRequest.Restore(f.Scope,OkxExecutionOperation.Submit,"BTC-USDC","Ab9",null,r.Deadline,Encoding.UTF8.GetBytes(body)).CopyBody()));
    }
    [Theory][InlineData("conditional",null,"80000")][InlineData("conditional","100000",null)]
    public async Task SingleLegConditionalProtectionHasNoInventedBracket(string type,string? tp,string? sl)
    {
        using var f=new OkxExecutionHttpFixture(); var r=OkxPreparedRequest.PlaceProtection(f.Scope,"BTC-USDC",type,"0.01","Algo9",tp,sl,f.Clock.Now.AddSeconds(20));
        var expected="{\"instId\":\"BTC-USDC\",\"tdMode\":\"cash\",\"side\":\"sell\",\"ordType\":\"conditional\",\"sz\":\"0.01\",\"algoClOrdId\":\"Algo9\",\"tradeQuoteCcy\":\"USDC\","+(tp is null?"\"slTriggerPx\":\"80000\",\"slOrdPx\":\"-1\",\"slTriggerPxType\":\"last\"}":"\"tpTriggerPx\":\"100000\",\"tpOrdPx\":\"-1\",\"tpTriggerPxType\":\"last\"}");
        f.Add("POST","/api/v5/trade/order-algo",expected,"""{"code":"0","msg":"","data":[{"algoId":"765432","algoClOrdId":"Algo9","sCode":"0","sMsg":""}]}""");
        await using var c=f.Client(); Assert.Equal(OkxWriteDisposition.Acknowledged,(await c.PlaceProtectionAsync(r,default)).Disposition); f.Done();
    }
}
