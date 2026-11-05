using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Xunit;
namespace Apex.Tests;

// Break caught: only the approved retrospective exact parent attachment may
// discard the named bounded string. All financial fields/digests remain original.
public sealed class OkxOwnedLotCompatibilityTests
{
    private sealed class Fence:IOkxObservationReadFence
    { public bool CanObserve(OkxExecutionScope s,OkxObservationContext c,DateTimeOffset t)=>true; }
    private sealed class RawPeer(string wire):System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage r,System.Threading.CancellationToken ct)
        { Assert.Equal(System.Net.Http.HttpMethod.Get,r.Method);return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new System.Net.Http.StringContent(wire,System.Text.Encoding.UTF8,"application/json")}); }
    }
    [Theory][InlineData("absent")][InlineData("empty")][InlineData("nonempty")][InlineData("boundary")][InlineData("unicode")]
    public async Task Only_owned_exact_parent_discards_bounded_nested_string_and_preserves_selected_fields_and_digests(string variant)
    {
        await using var f=new OperationalPeer();var body=OkxOwnedExitDiagnosticTests.Oracle("recovery-schema.json","order");
        var row=body["data"]![0]!;row["instType"]="SPOT";row["tdMode"]="cash";
        var child=new JsonObject{["attachAlgoId"]="888",["attachAlgoClOrdId"]="OwnProtection1",["failCode"]=""};row["attachAlgoOrds"]=new JsonArray(child);
        f.Mutate=(_,b)=>b["data"]=body["data"]!.DeepClone();
        var s=new OkxExecutionScope("Demo",f.Binding.Account.AccountId,f.Binding.MainUid,"USDC","key");
        using var k=new Apex.Okx.DemoAcceptance.EphemeralCredentials(OperationalPeer.Key.ToArray(),OperationalPeer.Secret.ToArray(),OperationalPeer.Pass.ToArray());
        await using var c=OkxExecutionObservationClient.ForTests(f,f.Clock,s,k.ForObservation(s),k.Fingerprint,new Fence(),(t,ct)=>{f.Clock.Advance(t);return Task.CompletedTask;});var x=new OkxObservationContext(Guid.NewGuid(),Guid.NewGuid());var q=new OkxObservationOrderQuery("BTC-USDC","987654","Ab9");
        var before=await c.ReadOwnedRecoveryOrderAsync(q,x,default);Assert.Equal(OkxObservationStatus.Observed,before.Status);
        if(variant!="absent")child["percent"]=variant switch{"empty"=>"","boundary"=>new string('x',128),"unicode"=>"Zażółć-\U0001F680",_=>"opaque-not-financial"};
        var after=await c.ReadOwnedRecoveryOrderAsync(q,x,default);
        Assert.True(after.Status==OkxObservationStatus.Observed,"Approved exact nested String refused: "+variant);Assert.Single(after.Rows);
        var a=after.Rows.Single();var b=before.Rows.Single();
        var selectedBefore=JsonSerializer.SerializeToNode(b)!.AsObject();var selectedAfter=JsonSerializer.SerializeToNode(a)!.AsObject();selectedBefore.Remove("PayloadDigest");selectedAfter.Remove("PayloadDigest");Assert.True(JsonNode.DeepEquals(selectedBefore,selectedAfter));
        if(variant!="absent"){Assert.NotEqual(b.PayloadDigest,a.PayloadDigest);Assert.NotEqual(before.ResponseDigest,after.ResponseDigest);}
        Assert.DoesNotContain("percent",JsonSerializer.Serialize(a));Assert.DoesNotContain("opaque-not-financial",JsonSerializer.Serialize(a));
        // Ordinary parser and original diagnostic profile are still strict.
        if(variant!="absent")
        {Assert.Equal(OkxObservationStatus.Failed,(await c.ReadOrderAsync(q,x,default)).Status);Assert.Equal(OkxObservationStatus.Failed,(await c.ReadOrderSchemaAsync(q,x,default,(_,_,_)=>{})).Status);}
    }
    [Theory]
    [InlineData("number")][InlineData("null")][InlineData("bool")][InlineData("array")][InlineData("object")]
    [InlineData("overlimit")][InlineData("case")][InlineData("root")][InlineData("linked")][InlineData("deeper")][InlineData("otherunknown")]
    [InlineData("duplicate")][InlineData("unicode")][InlineData("margin")][InlineData("swap")][InlineData("cross")][InlineData("algo")][InlineData("pending")]
    public async Task Named_metadata_does_not_broaden_wrong_kind_path_product_duplicate_unicode_or_other_unknown(string fault)
    {
        await using var f=new OperationalPeer();bool algo=fault=="algo";var body=OkxOwnedExitDiagnosticTests.Oracle(algo?"stream-algo-schema.json":"recovery-schema.json",algo?"detail":"order");var row=body["data"]![0]!;row["instType"]="SPOT";row["tdMode"]="cash";
        var child=new JsonObject{[algo?"slOrdPx":"failCode"]="",["percent"]="opaque"};row["attachAlgoOrds"]=new JsonArray(child);
        switch(fault)
        {
            case "number":child["percent"]=1;break;case "null":child["percent"]=null;break;case "bool":child["percent"]=false;break;case "array":child["percent"]=new JsonArray();break;case "object":child["percent"]=new JsonObject();break;
            case "overlimit":child["percent"]=new string('x',129);break;case "case":child["Percent"]="opaque";break;
            case "root":child.Remove("percent");row["percent"]="opaque";break;
            case "linked":child.Remove("percent");row["linkedAlgoOrd"]=new JsonObject{["algoId"]="123",["percent"]="opaque"};break;
            case "deeper":child.Remove("percent");child["attachAlgoOrds"]=new JsonArray(new JsonObject{["percent"]="opaque"});break;
            case "otherunknown":child["unknownSynthetic"]="opaque";break;
            case "margin":row["instType"]="MARGIN";break;case "swap":row["instType"]="SWAP";break;case "cross":row["tdMode"]="cross";break;
        }
        string wire=body.ToJsonString();if(fault=="duplicate")wire=wire.Replace("\"percent\":\"opaque\"","\"percent\":\"opaque\",\"percent\":\"opaque\"",StringComparison.Ordinal);
        if(fault=="unicode")wire=wire.Replace("\"percent\":\"opaque\"","\"percent\":\"\\uD800\"",StringComparison.Ordinal);
        using var handler=new RawPeer(wire);
        var s=new OkxExecutionScope("Demo",f.Binding.Account.AccountId,f.Binding.MainUid,"USDC","key");using var k=new Apex.Okx.DemoAcceptance.EphemeralCredentials(OperationalPeer.Key.ToArray(),OperationalPeer.Secret.ToArray(),OperationalPeer.Pass.ToArray());
        await using var c=OkxExecutionObservationClient.ForTests(handler,f.Clock,s,k.ForObservation(s),k.Fingerprint,new Fence(),(t,ct)=>{f.Clock.Advance(t);return Task.CompletedTask;});var x=new OkxObservationContext(Guid.NewGuid(),Guid.NewGuid());
        if(algo){var r=await c.ReadOwnedRecoveryAlgoAsync(new("123",null),x,default);Assert.Equal(OkxObservationStatus.Failed,r.Status);Assert.Empty(r.Rows);}
        else if(fault=="pending"){var r=await c.ReadPendingAsync(new(null,null,null,null,null),x,default);Assert.Equal(OkxObservationStatus.Failed,r.Status);Assert.Empty(r.Rows);}
        else{var r=await c.ReadOwnedRecoveryOrderAsync(new("BTC-USDC","987654","Ab9"),x,default);Assert.Equal(OkxObservationStatus.Failed,r.Status);Assert.Empty(r.Rows);}
    }
}
