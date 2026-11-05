using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Apex.Adapters.Okx.Execution;
using Xunit;
namespace Apex.Adapters.Okx.Tests;
public sealed class OkxExecutionAdditionalTests
{
    [Fact] public async Task OrderIdPrecedenceRetainsObservedClientIdentityWithoutAttribution()
    {
        using var f=new OkxExecutionHttpFixture(); f.Add("GET","/api/v5/trade/order?clOrdId=Ab9&instId=BTC-USDC&ordId=987654",null,OkxExecutionHttpFixture.Detail("DifferentClient"));
        await using var c=f.Client(); var o=await c.LookupAsync("BTC-USDC","987654","Ab9",default);
        Assert.Equal("Observed",o.Reason); Assert.Equal("Ab9",o.OriginalClientId);
        using var json=JsonDocument.Parse(JsonSerializer.Serialize(o));
        Assert.True(json.RootElement.TryGetProperty("ObservedClientId",out var observed)); Assert.Equal("DifferentClient",observed.GetString()); f.Done();
    }
    [Fact] public async Task CredentialProviderExceptionsAreSanitizedBeforeSend()
    {
        using var f=new OkxExecutionHttpFixture(); f.OnOpen=()=>throw new CryptographicException("PRIVATE_PROVIDER_ERROR"); await using var c=f.Client();
        var o=await c.SubmitAsync(f.Request(),default); Assert.Equal(OkxWriteDisposition.ProvenNotSent,o.Disposition); Assert.DoesNotContain("PRIVATE_PROVIDER_ERROR",JsonSerializer.Serialize(o)); Assert.Empty(f.Sent);
    }
    [Theory][InlineData("transfer")][InlineData("withdrawal")][InlineData("borrow")][InlineData("amend-order")][InlineData("cancel-all-after")]
    public void ForbiddenOperationsCannotBeRestored(string operation)
    {
        using var f=new OkxExecutionHttpFixture(); var input=Encoding.UTF8.GetBytes("{\"path\":\"/api/v5/"+operation+"\",\"method\":\"POST\",\"body\":{}}");
        Assert.Throws<ArgumentException>(()=>OkxPreparedRequest.Restore(f.Scope,OkxExecutionOperation.Submit,"BTC-USDC","Ab9",null,f.Clock.Now.AddSeconds(20),input));
        Assert.Throws<ArgumentException>(()=>OkxPreparedRequest.Restore(f.Scope,(OkxExecutionOperation)999,"BTC-USDC","Ab9",null,f.Clock.Now.AddSeconds(20),Encoding.UTF8.GetBytes(OkxExecutionHttpFixture.SubmitBody)));
    }
    [Fact] public async Task CancellationAfterHandlerReturningAckRetainsUnknown()
    {
        using var f=new OkxExecutionHttpFixture(); using var ct=new CancellationTokenSource();
        f.Steps.Enqueue(("POST","/api/v5/trade/order",OkxExecutionHttpFixture.SubmitBody,token=>{ ct.Cancel(); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent(OkxExecutionHttpFixture.Ack()) }); }));
        await using var c=f.Client(); Assert.Equal(OkxWriteDisposition.Unknown,(await c.SubmitAsync(f.Request(),ct.Token)).Disposition); Assert.Single(f.Sent); f.Done();
    }
    [Theory][InlineData("instType","MARGIN")][InlineData("tdMode","cross")][InlineData("ordId","123456")][InlineData("clOrdId","ab9")][InlineData("accFillSz","0.00000000000000000000000000001")][InlineData("tradeQuoteCcy","USD")]
    public async Task LookupInvalidOrCrossScopeObservationsStayUnknown(string field,string value)
    {
        using var f=new OkxExecutionHttpFixture(); var body=System.Text.Json.Nodes.JsonNode.Parse(OkxExecutionHttpFixture.Detail())!; body["data"]![0]![field]=value;
        f.Add("GET","/api/v5/trade/order?clOrdId=Ab9&instId=BTC-USDC&ordId=987654",null,body.ToJsonString());
        // With ordId precedence a different clOrdId is a preserved observation, not a matching intent.
        await using var c=f.Client(); var o=await c.LookupAsync("BTC-USDC","987654","Ab9",default);
        Assert.Equal(field=="clOrdId"?"Observed":"Unknown",o.Reason); f.Done();
    }
    [Fact] public async Task DeadlineAfterSecretOpenDoesNotReachPeer()
    {
        using var f=new OkxExecutionHttpFixture(); f.OnOpen=()=>f.Clock.Now+=TimeSpan.FromSeconds(2); await using var c=f.Client();
        var o=await c.SubmitAsync(f.Request(deadline:f.Clock.Now.AddSeconds(1)),default); Assert.Equal(OkxWriteDisposition.ProvenNotSent,o.Disposition); Assert.Empty(f.Sent);
    }
    [Fact] public async Task ScopeFingerprintMismatchRefusesAndOpenedMaterialIsDisposed()
    {
        using var f=new OkxExecutionHttpFixture(); var provider=new RetainedProvider(f.Scope);
        await using var c=OkxExecutionClient.ForTests(f,f.Clock,f.Scope,provider,new string('a',64),f,f.Clock.Delay);
        Assert.Equal(OkxWriteDisposition.ProvenNotSent,(await c.SubmitAsync(f.Request(),default)).Disposition);
        Assert.NotNull(provider.Material); Assert.All(provider.Material!.Key,b=>Assert.Equal(0,b)); Assert.Empty(f.Sent);
    }
    private sealed class RetainedProvider(OkxExecutionScope scope) : IOkxSigningMaterialProvider
    {
        internal OkxSigningMaterial? Material;
        public ValueTask<OkxSigningMaterial> OpenAsync(CancellationToken ct) { Material=new(scope,"DemoTradingApi",Encoding.UTF8.GetBytes("OTHER_SYNTHETIC_KEY"),Encoding.UTF8.GetBytes("secret"),Encoding.UTF8.GetBytes("pass")); return ValueTask.FromResult(Material); }
    }
}
