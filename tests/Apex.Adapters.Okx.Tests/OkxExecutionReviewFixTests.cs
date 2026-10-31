using System.Net;
using System.Text;
using System.Text.Json;
using Apex.Adapters.Okx.Execution;
using Xunit;
namespace Apex.Adapters.Okx.Tests;

public sealed class OkxExecutionReviewFixTests
{
    private const string LookupPath="/api/v5/trade/order?clOrdId=Ab9&instId=BTC-USDC";
    private sealed class InspectFence(Action<OkxPreparedRequest> inspect) : IOkxFinalDispatchFence
    {
        public bool CanDispatch(OkxPreparedRequest request,DateTimeOffset now) { inspect(request); return true; }
    }
    private static OkxExecutionHttpFixture Fixture()
    {
        var f=new OkxExecutionHttpFixture();
        // Independent quota lane per case, without resetting the production process-wide limiter.
        var uid=Interlocked.Increment(ref nextUid).ToString(System.Globalization.CultureInfo.InvariantCulture);
        f.Scope=new("Demo",uid,uid,"USDC","review-fix"); return f;
    }
    private static int nextUid=992000;

    // Catches the internally derived fractional deadline refusing normal high-resolution clocks.
    [Theory]
    [InlineData(null,0)][InlineData("987654",0)]
    [InlineData(null,1)][InlineData("987654",1)]
    [InlineData(null,9999)][InlineData("987654",9999)]
    public async Task LookupHighResolutionClockUsesNoLaterMillisecondDeadline(string? order,long ticks)
    {
        using var f=Fixture(); var aligned=f.Clock.Now; f.Clock.Now=aligned.AddTicks(ticks);
        DateTimeOffset? deadline=null;
        var fence=new InspectFence(r=>deadline=r.Deadline);
        f.Add("GET",LookupPath+(order is null?"":"&ordId=987654"),null,OkxExecutionHttpFixture.Detail());
        await using var c=OkxExecutionClient.ForTests(f,f.Clock,f.Scope,f,f.Fingerprint,fence,f.Clock.Delay);
        var o=await c.LookupAsync("BTC-USDC",order,"Ab9",default);
        Assert.Equal("Observed",o.Reason); Assert.Equal("987654",o.OrderId); Assert.Equal("Ab9",o.ObservedClientId);
        Assert.Equal(aligned.AddSeconds(10),deadline); Assert.True(deadline<=f.Clock.Now.AddSeconds(10));
        Assert.Single(f.Sent); Assert.Equal(1,f.Opens); f.Done();
    }

    // Catches rounding up, expiry equality bypass, and checking only before secret access.
    [Theory][InlineData(-1,true)][InlineData(0,false)][InlineData(1,false)][InlineData(9999,false)]
    public async Task LookupExpiryAfterMaterialAccessHonorsFlooredBoundary(long ticks,bool send)
    {
        using var f=Fixture(); var aligned=f.Clock.Now; f.Clock.Now=aligned.AddTicks(9999);
        f.OnOpen=()=>f.Clock.Now=aligned.AddSeconds(10).AddTicks(ticks);
        if(send) f.Add("GET",LookupPath,null,OkxExecutionHttpFixture.Detail());
        await using var c=f.Client(); var o=await c.LookupAsync("BTC-USDC",null,"Ab9",default);
        Assert.Equal(send?"Observed":"Unknown",o.Reason); Assert.Equal(send?1:0,f.Sent.Count); Assert.Equal(1,f.Opens); f.Done();
    }

    [Fact] public async Task LookupExpiryInsideFinalFencePreventsInvocation()
    {
        using var f=Fixture(); var aligned=f.Clock.Now; f.Clock.Now=aligned.AddTicks(1);
        var fence=new InspectFence(r=> { Assert.Equal(aligned.AddSeconds(10),r.Deadline); f.Clock.Now=r.Deadline; });
        await using var c=OkxExecutionClient.ForTests(f,f.Clock,f.Scope,f,f.Fingerprint,fence,f.Clock.Delay);
        Assert.Equal("Unknown",(await c.LookupAsync("BTC-USDC",null,"Ab9",default)).Reason);
        Assert.Empty(f.Sent); Assert.Equal(1,f.Opens);
    }

    [Fact] public async Task LookupQuotaWaitCannotRejuvenateTenSecondBudget()
    {
        using var f=Fixture(); f.Clock.Now=f.Clock.Now.AddTicks(1);
        f.Add("GET",LookupPath,null,OkxExecutionHttpFixture.Detail());
        f.Delay=(t,ct)=> { ct.ThrowIfCancellationRequested(); f.Clock.Now=f.Clock.Now.AddSeconds(10); return Task.CompletedTask; };
        await using var c=f.Client(); Assert.Equal("Observed",(await c.LookupAsync("BTC-USDC",null,"Ab9",default)).Reason);
        Assert.Equal("Unknown",(await c.LookupAsync("BTC-USDC",null,"Ab9",default)).Reason);
        Assert.Single(f.Sent); Assert.Equal(1,f.Opens); f.Done();
    }

    [Fact] public async Task LookupResponseAtDeadlineIsNotAnObservation()
    {
        using var f=Fixture(); var aligned=f.Clock.Now; f.Clock.Now=aligned.AddTicks(1);
        f.Steps.Enqueue(("GET",LookupPath,null,ct=> { f.Clock.Now=aligned.AddSeconds(10); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent(OkxExecutionHttpFixture.Detail()) }); }));
        await using var c=f.Client(); var o=await c.LookupAsync("BTC-USDC",null,"Ab9",default);
        Assert.Equal("Unknown",o.Reason); Assert.Null(o.State); Assert.Single(f.Sent); f.Done();
    }

    // Same parent/control and three variants as the unchanged reviewer AttachmentDiagnostic.cs.
    // Oracle: retained EEA R:7645-7679. Observing a parent never proves attachment protection.
    private const string AttachmentBase="\"attachAlgoId\":\"876543\",\"attachAlgoClOrdId\":\"Attached9\",\"tpTriggerPx\":\"100000\",\"tpOrdPx\":\"-1\",\"tpTriggerPxType\":\"last\",\"slTriggerPx\":\"80000\",\"slOrdPx\":\"-1\",\"slTriggerPxType\":\"last\"";
    private static string AttachedDetail(string metadata) =>
        """{"code":"0","msg":"","data":[{"instType":"SPOT","instId":"BTC-USDC","tdMode":"cash","ordId":"987654","clOrdId":"Ab9","side":"buy","state":"live","sz":"0.01","accFillSz":"0","avgPx":"","tradeQuoteCcy":"USDC","attachAlgoOrds":[{"""+AttachmentBase+metadata+"}]}]}";

    [Theory]
    [InlineData("")]
    [InlineData(",\"tpOrdKind\":\"condition\"")]
    [InlineData(",\"failCode\":\"\",\"failReason\":\"\"")]
    [InlineData(",\"failCode\":\"51020\",\"failReason\":\"SYNTHETIC_PROTECTION_ERROR\"")]
    public async Task DocumentedAttachmentMetadataPreservesOnlyParentObservation(string metadata)
    {
        using var f=Fixture(); f.Add("GET",LookupPath,null,AttachedDetail(metadata));
        await using var c=f.Client(); var o=await c.LookupAsync("BTC-USDC",null,"Ab9",default);
        Assert.Equal("Observed",o.Reason); Assert.Equal("live",o.State); Assert.Equal("0",o.AccFillSize);
        Assert.Equal("0.01",o.Size); Assert.Equal("",o.AveragePrice); Assert.Equal("987654",o.OrderId);
        Assert.Equal("Ab9",o.OriginalClientId); Assert.Equal("Ab9",o.ObservedClientId); Assert.Equal("USDC",o.TradeQuoteCurrency);
        var serialized=JsonSerializer.Serialize(o);
        Assert.DoesNotContain("SYNTHETIC_PROTECTION_ERROR",serialized); Assert.DoesNotContain("51020",serialized);
        Assert.DoesNotContain("Attached9",serialized); Assert.DoesNotContain("876543",serialized);
        Assert.Single(f.Sent); f.Done();
    }

    // Catches a permissive unknown-field/type bypass and recursive exact/case duplicate acceptance.
    [Theory]
    [InlineData(",\"tpOrdKind\":0")][InlineData(",\"tpOrdKind\":null")]
    [InlineData(",\"failCode\":51020")][InlineData(",\"failCode\":{}")]
    [InlineData(",\"failReason\":false")][InlineData(",\"failReason\":[]")]
    [InlineData(",\"tpOrdKind\":\"condition\",\"tpOrdKind\":\"limit\"")]
    [InlineData(",\"tpOrdKind\":\"condition\",\"TpOrdKind\":\"condition\"")]
    [InlineData(",\"failCode\":\"\",\"failCode\":\"51020\"")]
    [InlineData(",\"failCode\":\"\",\"FAILCODE\":\"\"")]
    [InlineData(",\"failReason\":\"\",\"failReason\":\"PRIVATE_ATTACHMENT_SENTINEL\"")]
    [InlineData(",\"failReason\":\"\",\"FailReason\":\"PRIVATE_ATTACHMENT_SENTINEL\"")]
    [InlineData(",\"unexpectedField\":\"PRIVATE_ATTACHMENT_SENTINEL\"")]
    public async Task InvalidAttachmentMetadataStaysUnknownWithoutPrivateText(string metadata)
    {
        using var f=Fixture(); f.Add("GET",LookupPath,null,AttachedDetail(metadata));
        await using var c=f.Client(); var o=await c.LookupAsync("BTC-USDC",null,"Ab9",default);
        Assert.Equal("Unknown",o.Reason); Assert.Null(o.State); Assert.Null(o.AccFillSize);
        Assert.DoesNotContain("PRIVATE_ATTACHMENT_SENTINEL",JsonSerializer.Serialize(o)); Assert.Single(f.Sent); f.Done();
    }

    [Fact] public async Task AttachmentFailureTextIsDiscardedNotProtectionCoverage()
    {
        using var f=Fixture();
        var metadata=",\"tpOrdKind\":\"condition\",\"failCode\":\"51020\",\"failReason\":"+JsonSerializer.Serialize("PRIVATE_ATTACHMENT_SENTINEL\r\n\"secret\":synthetic")+"";
        f.Add("GET",LookupPath,null,AttachedDetail(metadata));
        await using var c=f.Client(); var o=await c.LookupAsync("BTC-USDC",null,"Ab9",default);
        Assert.Equal("Observed",o.Reason); Assert.Equal("live",o.State); Assert.Equal("0",o.AccFillSize);
        Assert.DoesNotContain("PRIVATE_ATTACHMENT_SENTINEL",o.ToString()); Assert.DoesNotContain("secret",JsonSerializer.Serialize(o));
        Assert.Single(f.Sent); f.Done();
    }

    // HTTP400 is documented for 51000 (EEA R:27187), not these endpoint-specific
    // identity/ts combinations. These are conservative offline shape controls, not broker proof.
    [Theory]
    [InlineData(false,200,"0")][InlineData(false,200,"1")]
    [InlineData(true,200,"0")][InlineData(true,200,"1")]
    [InlineData(false,400,"0")][InlineData(false,400,"1")]
    [InlineData(true,400,"0")][InlineData(true,400,"1")]
    public async Task MatchingParameterErrorCannotProveReleaseForGeneralOrAlgo(bool algo,int status,string top)
    {
        using var f=Fixture();
        var row=algo?"\"algoId\":\"\",\"algoClOrdId\":\"Algo9\"":"\"ordId\":\"\",\"clOrdId\":\"Ab9\",\"ts\":\"1793448000000\"";
        var response="{\"code\":\""+top+"\",\"msg\":\"PRIVATE_PARAMETER_SENTINEL\",\"data\":[{"+row+",\"sCode\":\"51000\",\"sMsg\":\"PRIVATE_PARAMETER_SENTINEL\",\"subCode\":\"\"}]}";
        var request=algo?OkxPreparedRequest.PlaceProtection(f.Scope,"BTC-USDC","oco","0.01","Algo9","100000","80000",f.Clock.Now.AddSeconds(20)):f.Request();
        if(algo) f.Add("POST","/api/v5/trade/order-algo","""{"instId":"BTC-USDC","tdMode":"cash","side":"sell","ordType":"oco","sz":"0.01","algoClOrdId":"Algo9","tradeQuoteCcy":"USDC","tpTriggerPx":"100000","tpOrdPx":"-1","tpTriggerPxType":"last","slTriggerPx":"80000","slOrdPx":"-1","slTriggerPxType":"last"}""",response,(HttpStatusCode)status);
        else f.SubmitReply(response,(HttpStatusCode)status);
        await using var c=f.Client(); var o=algo?await c.PlaceProtectionAsync(request,default):await c.SubmitAsync(request,default);
        Assert.Equal(OkxWriteDisposition.Unknown,o.Disposition); Assert.Null(o.OrderId);
        Assert.Equal(algo?"Algo9":"Ab9",o.OriginalClientId);
        if(status==200) { Assert.Equal(top,o.Code); Assert.Equal("51000",o.SCode); }
        else { Assert.Null(o.Code); Assert.Null(o.SCode); } // HTTP failure details are deliberately not parsed, not evidence of rejection.
        Assert.DoesNotContain("PRIVATE_PARAMETER_SENTINEL",JsonSerializer.Serialize(o)); Assert.Single(f.Sent); f.Done();
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task Http400ParameterErrorEnvelopeWithoutItemCannotProveRelease(bool algo)
    {
        using var f=Fixture(); const string response="""{"code":"51000","msg":"PRIVATE_PARAMETER_SENTINEL","data":[]}""";
        var request=algo?OkxPreparedRequest.PlaceProtection(f.Scope,"BTC-USDC","oco","0.01","Algo9","100000","80000",f.Clock.Now.AddSeconds(20)):f.Request();
        if(algo) f.Add("POST","/api/v5/trade/order-algo","""{"instId":"BTC-USDC","tdMode":"cash","side":"sell","ordType":"oco","sz":"0.01","algoClOrdId":"Algo9","tradeQuoteCcy":"USDC","tpTriggerPx":"100000","tpOrdPx":"-1","tpTriggerPxType":"last","slTriggerPx":"80000","slOrdPx":"-1","slTriggerPxType":"last"}""",response,HttpStatusCode.BadRequest);
        else f.SubmitReply(response,HttpStatusCode.BadRequest);
        await using var c=f.Client(); var o=algo?await c.PlaceProtectionAsync(request,default):await c.SubmitAsync(request,default);
        Assert.Equal(OkxWriteDisposition.Unknown,o.Disposition); Assert.Null(o.OrderId); Assert.Null(o.Code); Assert.Null(o.SCode);
        Assert.DoesNotContain("PRIVATE_PARAMETER_SENTINEL",JsonSerializer.Serialize(o)); Assert.Single(f.Sent); f.Done();
    }

    // Lookup alignment must not silently repair caller-owned exact immutable mutation deadlines.
    [Theory][InlineData(1)][InlineData(9999)]
    public void CallerMutationAndReplayDeadlinesRemainStrict(long ticks)
    {
        using var f=Fixture(); var deadline=f.Clock.Now.AddSeconds(10).AddTicks(ticks);
        Assert.Throws<ArgumentException>(()=>f.Request(deadline:deadline));
        Assert.Throws<ArgumentException>(()=>OkxPreparedRequest.Cancel(f.Scope,"BTC-USDC","987654","Ab9",deadline));
        Assert.Throws<ArgumentException>(()=>OkxPreparedRequest.PlaceProtection(f.Scope,"BTC-USDC","oco","0.01","Algo9","100000","80000",deadline));
        Assert.Throws<ArgumentException>(()=>OkxPreparedRequest.CancelProtection(f.Scope,"BTC-USDC","765432","Algo9",deadline));
        Assert.Throws<ArgumentException>(()=>OkxPreparedRequest.Restore(f.Scope,OkxExecutionOperation.Submit,"BTC-USDC","Ab9",null,deadline,Encoding.UTF8.GetBytes(OkxExecutionHttpFixture.SubmitBody)));
    }
}
