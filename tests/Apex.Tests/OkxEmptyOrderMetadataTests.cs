using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Xunit;
namespace Apex.Tests;

// Explicit parent LOCAL policy. Public vocabulary is not an endpoint contract.
// Removing conditional admission or dropping the field from either digest breaks these controls.
public sealed class OkxEmptyOrderMetadataTests
{
    private static string Endpoint(bool algo)=>algo?"/api/v5/trade/order-algo":"/api/v5/trade/order";
    private static JsonNode Wire(bool algo)
    {
        var body=OkxOwnedExitDiagnosticTests.Oracle(algo?"stream-algo-schema.json":"recovery-schema.json",algo?"detail":"order");
        body["data"]![0]!["instType"]="SPOT";body["data"]![0]!["tdMode"]="cash";return body;
    }
    private static object Parse(bool algo,byte[] bytes,string? endpoint=null)=>algo?
        Assert.Single(OkxObservationJson.Parse<OkxExecutionAlgoDto>(bytes,endpoint??Endpoint(true),100)):
        Assert.Single(OkxObservationJson.Parse<OkxExecutionOrderDto>(bytes,endpoint??Endpoint(false),100));
    private static JsonObject Selected(object row)
    {var selected=JsonSerializer.SerializeToNode(row)!.AsObject();selected.Remove("PayloadDigest");return selected;}
    private static byte[] Bytes(JsonNode node)=>Encoding.UTF8.GetBytes(node.ToJsonString());
    private sealed class Fence:IOkxObservationReadFence {public bool CanObserve(OkxExecutionScope s,OkxObservationContext c,DateTimeOffset t)=>true;}
    private sealed class Peer(byte[] body,string expected):HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
        {Assert.Equal(HttpMethod.Get,r.Method);Assert.Equal(expected,r.RequestUri!.AbsolutePath);Calls++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(body)});}
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Absent_and_exact_empty_keep_every_selected_operand_but_both_digests_distinct(bool algo)
    {
        var wire=Wire(algo);byte[] absent=Bytes(wire);var old=Parse(algo,absent);
        wire["data"]![0]!["slippage"]="";byte[] empty=Bytes(wire);var added=Parse(algo,empty);
        Assert.True(JsonNode.DeepEquals(Selected(old),Selected(added)),"Selected DTO changed");
        string Digest(object row)=>row.GetType().GetProperty("PayloadDigest")!.GetValue(row)!.ToString()!;
        Assert.NotEqual(Digest(old),Digest(added));Assert.NotEqual(SHA256.HashData(absent),SHA256.HashData(empty));
        await using var fixture=new OperationalPeer();
        var scope=new OkxExecutionScope("Demo",fixture.Binding.Account.AccountId,fixture.Binding.MainUid,"USDC","key");
        using var credentials=new Apex.Okx.DemoAcceptance.EphemeralCredentials(OperationalPeer.Key.ToArray(),OperationalPeer.Secret.ToArray(),OperationalPeer.Pass.ToArray());
        var digests=new List<string>();
        foreach(var bytes in new[]{absent,empty})
        {
            var peer=new Peer(bytes,Endpoint(algo));
            await using var reads=OkxExecutionObservationClient.ForTests(peer,fixture.Clock,scope,credentials.ForObservation(scope),credentials.Fingerprint,new Fence(),(t,c)=>{fixture.Clock.Advance(t);return Task.CompletedTask;});
            var context=new OkxObservationContext(Guid.NewGuid(),Guid.NewGuid());
            if(algo){var read=await reads.ReadAlgoAsync(new("123",null),context,default);Assert.Equal(OkxObservationStatus.Observed,read.Status);digests.Add(read.ResponseDigest!);}
            else{var read=await reads.ReadOrderAsync(new("BTC-USDC","987654","Ab9"),context,default);Assert.Equal(OkxObservationStatus.Observed,read.Status);digests.Add(read.ResponseDigest!);}
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),digests.Last());Assert.Equal(1,peer.Calls);
        }
        Assert.NotEqual(digests[0],digests[1]);
    }
    public static IEnumerable<object[]> Negatives()=>new[]{false,true}.SelectMany(a=>new[]{"nonempty","whitespace","null","number","boolean","array","object","nonspot","noncash","missingtype","missingmode","case","duplicate","foldedduplicate","unicodevalue","unicodename","unknown","nested","envelope"}.Select(n=>new object[]{a,n}));
    [Theory][MemberData(nameof(Negatives))]
    public void Refuse_before_projection_with_closed_reason_and_no_value(bool algo,string change)
    {
        var wire=Wire(algo);var row=wire["data"]![0]!;row["slippage"]="";
        string reason="EXACT_CASH_SPOT_EMPTY_SLIPPAGE_V1",path="$.data[].slippage",expected="EmptyStringOnExactCashSpot",actual="String";
        switch(change)
        {
            case "nonempty":row["slippage"]="PRIVATE_EXTENSION_VALUE";break;
            case "whitespace":row["slippage"]=" \t";break;
            case "null":row["slippage"]=null;actual="Null";break;
            case "number":row["slippage"]=0;actual="Number";break;
            case "boolean":row["slippage"]=false;actual="Boolean";break;
            case "array":row["slippage"]=new JsonArray();actual="Array";break;
            case "object":row["slippage"]=new JsonObject();actual="Object";break;
            case "nonspot":row["instType"]="MARGIN";break;
            case "noncash":row["tdMode"]="cross";break;
            case "missingtype":row.AsObject().Remove("instType");break;
            case "missingmode":row.AsObject().Remove("tdMode");break;
            case "case":row.AsObject().Remove("slippage");row["Slippage"]="";reason="PROPERTY_CASE";expected="ExactProperty";break;
            case "duplicate":case "foldedduplicate":reason="DUPLICATE_PROPERTY";expected="UniqueProperty";break;
            case "unicodevalue":reason="INVALID_STRING";expected="UnicodeString";break;
            case "unicodename":row.AsObject().Remove("slippage");row["slippag\u0435"]="";goto case "unknown";
            case "unknown":row["unrecognizedExtension"]="PRIVATE_EXTENSION_VALUE";reason="UNSUPPORTED_PROPERTY";path="$.data[].<unknown>";expected="KnownProperty";break;
            case "nested":row.AsObject().Remove("slippage");row["attachAlgoOrds"]=new JsonArray(new JsonObject{["slippage"]=""});reason="UNSUPPORTED_PROPERTY";path="$.data[].attachAlgoOrds[].<unknown>";expected="KnownProperty";break;
            case "envelope":row.AsObject().Remove("slippage");wire["slippage"]="";reason="UNSUPPORTED_PROPERTY";path="$.<unknown>";expected="KnownProperty";break;
        }
        string text=wire.ToJsonString();
        if(change is "duplicate" or "foldedduplicate")text=text.Replace("\"slippage\":\"\"","\"slippage\":\"\",\""+(change=="duplicate"?"slippage":"Slippage")+"\":\"\"");
        if(change=="unicodevalue")text=text.Replace("\"slippage\":\"\"","\"slippage\":\"\\uD800\"");
        var error=Assert.Throws<OkxObservationJson.PayloadException>(()=>Parse(algo,Encoding.UTF8.GetBytes(text)));
        Assert.Equal(reason,error.Failure.Reason);Assert.Equal(path,error.Failure.Path);Assert.Equal(expected,error.Failure.Expected);Assert.Equal(actual,error.Failure.Actual);
        Assert.DoesNotContain("PRIVATE_EXTENSION_VALUE",JsonSerializer.Serialize(error.Failure));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void Extension_cannot_mask_selected_economics_or_state(bool algo)
    {
        var wire=Wire(algo);var absent=Selected(Parse(algo,Bytes(wire)));wire["data"]![0]!["slippage"]="";
        wire["data"]![0]!["state"]="LOCAL_STATE_SENTINEL";
        var changed=Selected(Parse(algo,Bytes(wire)));Assert.False(JsonNode.DeepEquals(absent,changed));Assert.Equal("LOCAL_STATE_SENTINEL",changed["State"]!.GetValue<string>());
        wire["data"]![0]![algo?"sz":"fee"]="PRIVATE_BAD_NUMBER";
        Assert.Equal("NUMBER",Assert.Throws<OkxObservationJson.PayloadException>(()=>Parse(algo,Bytes(wire))).Failure.Reason);
    }
    [Theory]
    [InlineData(false,"/api/v5/trade/orders-pending")]
    [InlineData(false,"/api/v5/trade/orders-history")]
    [InlineData(false,"/api/v5/trade/orders-history-archive")]
    [InlineData(true,"/api/v5/trade/orders-algo-pending")]
    [InlineData(true,"/api/v5/trade/orders-algo-history")]
    public void Other_registered_endpoints_remain_strict(bool algo,string endpoint)
    {var wire=Wire(algo);Parse(algo,Bytes(wire),endpoint);wire["data"]![0]!["slippage"]="";Assert.ThrowsAny<JsonException>(()=>Parse(algo,Bytes(wire),endpoint));}
    [Theory][InlineData(false)][InlineData(true)]
    public void Absent_extension_does_not_change_legacy_product_mode_admission(bool algo)
    {var wire=Wire(algo);wire["data"]![0]!["instType"]="MARGIN";wire["data"]![0]!["tdMode"]="cross";Parse(algo,Bytes(wire));}
}
