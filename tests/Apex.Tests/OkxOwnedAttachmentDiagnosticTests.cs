using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Xunit;
namespace Apex.Tests;

// Break caught: the retrospective exact reader can reach nested rejection but
// previously could not pass the accepted identifier-only observer to that guard.
public sealed class OkxOwnedAttachmentDiagnosticTests
{
    private sealed class Fence : IOkxObservationReadFence
    { public bool CanObserve(OkxExecutionScope s,OkxObservationContext c,DateTimeOffset t)=>true; }

    // Preserve the three-argument old API and the exact same tester on old/new
    // DLLs. No production Main fallback, root override or new public API.
    private static async Task<JsonElement> Read(OkxExecutionObservationClient reads,bool algo,bool observe,List<(string? Name,string Kind,int Count)> groups)
    {
        string name=algo?"ReadOwnedRecoveryAlgoAsync":"ReadOwnedRecoveryOrderAsync";
        object query=algo?new OkxObservationAlgoQuery("123",null):new OkxObservationOrderQuery("BTC-USDC","987654","Ab9");
        var context=new OkxObservationContext(Guid.NewGuid(),Guid.NewGuid());
        Action<string?,string,int> observer=(n,k,c)=>groups.Add((n,k,c));
        var type=typeof(OkxExecutionObservationClient);
        var method=(observe?type.GetMethod(algo?"ReadOwnedRecoveryAlgoSchemaAsync":"ReadOwnedRecoveryOrderSchemaAsync",BindingFlags.NonPublic|BindingFlags.Instance):null)??type.GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance)!;
        object[] args=method.GetParameters().Length==4?new object[]{query,context,CancellationToken.None,observer}:new object[]{query,context,CancellationToken.None};
        var task=(Task)method.Invoke(reads,args)!;await task;
        return JsonSerializer.SerializeToElement(task.GetType().GetProperty("Result")!.GetValue(task));
    }
    [Theory]
    [InlineData(false,"unknown")][InlineData(true,"unknown")]
    [InlineData(false,"object")][InlineData(true,"object")]
    [InlineData(false,"first")][InlineData(true,"first")]
    [InlineData(false,"malformed")][InlineData(true,"malformed")]
    [InlineData(false,"groups-cap")][InlineData(true,"groups-cap")]
    [InlineData(false,"properties-cap")][InlineData(true,"properties-cap")]
    public async Task Retrospective_observer_reaches_first_rejected_nested_object_without_admission(bool algo,string variant)
    {
        await using var f=new OperationalPeer();var body=OkxOwnedExitDiagnosticTests.Oracle(algo?"stream-algo-schema.json":"recovery-schema.json",algo?"detail":"order");
        var row=body["data"]![0]!;row["instType"]="SPOT";row["tdMode"]="cash";
        if(algo){row["aggressiveness"]="synthetic-opaque";row["lmtOrderNumber"]="synthetic-opaque";row["triggerParams"]=new JsonArray();}
        else row["slippage"]="synthetic-nonempty-opaque";
        var nested=new JsonObject{[algo?"slOrdPx":"failCode"]=""};
        if(variant=="malformed")nested["not-identifier"]="PRIVATE_VALUE_SENTINEL";
        else if(variant is "groups-cap" or "properties-cap")
        {for(int i=0;i<(variant=="groups-cap"?33:257);i++)nested["syntheticUnknown"+(char)('A'+i/26)+(char)('A'+i%26)]="PRIVATE_VALUE_SENTINEL";}
        else if(variant=="object")nested["syntheticUnknown"]=new JsonObject{["noRecursiveCapture"]="PRIVATE_VALUE_SENTINEL"};
        else nested["syntheticUnknown"]="PRIVATE_VALUE_SENTINEL";
        row["attachAlgoOrds"]=new JsonArray(nested);
        if(variant=="first")row["attachAlgoOrds"]!.AsArray().Add(new JsonObject{["laterUnknown"]="PRIVATE_VALUE_SENTINEL"});
        f.Mutate=(_,b)=>b["data"]=body["data"]!.DeepClone();
        var scope=new OkxExecutionScope("Demo",f.Binding.Account.AccountId,f.Binding.MainUid,"USDC","key");
        using var credentials=new Apex.Okx.DemoAcceptance.EphemeralCredentials(OperationalPeer.Key.ToArray(),OperationalPeer.Secret.ToArray(),OperationalPeer.Pass.ToArray());
        await using var reads=OkxExecutionObservationClient.ForTests(f,f.Clock,scope,credentials.ForObservation(scope),credentials.Fingerprint,new Fence(),(t,ct)=>{f.Clock.Advance(t);return Task.CompletedTask;});
        var groups=new List<(string? Name,string Kind,int Count)>();var result=await Read(reads,algo,true,groups);
        Assert.Equal((int)OkxObservationStatus.Failed,result.GetProperty("Status").GetInt32());Assert.Equal(0,result.GetProperty("Rows").GetArrayLength());
        var diagnostic=result.GetProperty("Diagnostic");Assert.Equal(200,diagnostic.GetProperty("HttpStatus").GetInt32());Assert.Equal("0",diagnostic.GetProperty("OkxCode").GetString());Assert.Equal("PARSE_SCHEMA",diagnostic.GetProperty("Phase").GetString());
        var failure=diagnostic.GetProperty("Payload");Assert.Equal("UNSUPPORTED_PROPERTY",failure.GetProperty("Reason").GetString());Assert.Equal("$.data[].attachAlgoOrds[].<unknown>",failure.GetProperty("Path").GetString());
        Assert.Equal(variant=="object"?"Object":"String",failure.GetProperty("Actual").GetString());
        Assert.True(groups.Count>0,"Missing retrospective nested identifier observer at the retained strict rejection");
        if(variant is "malformed" or "groups-cap" or "properties-cap"){Assert.Single(groups);Assert.Null(groups[0].Name);Assert.Equal(0,groups[0].Count);}
        else Assert.Equal(new (string?,string,int)[]{("syntheticUnknown",variant=="object"?"Object":"String",1)},groups);
        string text=diagnostic.GetRawText();foreach(string value in new[]{"PRIVATE_VALUE_SENTINEL","syntheticUnknown","noRecursiveCapture","laterUnknown"})Assert.DoesNotContain(value,text);
        Assert.Single(f.Paths);
    }

    [Theory]
    [InlineData(false,"parsed")][InlineData(true,"parsed")]
    [InlineData(false,"type")][InlineData(true,"type")]
    [InlineData(false,"root-bound")][InlineData(true,"root-bound")]
    [InlineData(false,"without-observer")][InlineData(true,"without-observer")]
    [InlineData(false,"ordinary")][InlineData(true,"ordinary")]
    public async Task Retrospective_observer_never_replaces_strict_schema_or_earlier_refusal(bool algo,string variant)
    {
        await using var f=new OperationalPeer();var body=OkxOwnedExitDiagnosticTests.Oracle(algo?"stream-algo-schema.json":"recovery-schema.json",algo?"detail":"order");var row=body["data"]![0]!;row["instType"]="SPOT";row["tdMode"]="cash";
        if(algo){row["aggressiveness"]="opaque";row["lmtOrderNumber"]="opaque";row["triggerParams"]=new JsonArray();}else row["slippage"]="opaque";
        if(variant=="root-bound")row[algo?"aggressiveness":"slippage"]=new string('x',129);
        row["attachAlgoOrds"]=new JsonArray(new JsonObject{[algo?"slOrdPx":"failCode"]=variant=="type"?JsonValue.Create(false):JsonValue.Create("")});
        if(variant is "without-observer" or "ordinary")row["attachAlgoOrds"]![0]!["syntheticUnknown"]="PRIVATE_VALUE_SENTINEL";
        f.Mutate=(_,b)=>b["data"]=body["data"]!.DeepClone();var scope=new OkxExecutionScope("Demo",f.Binding.Account.AccountId,f.Binding.MainUid,"USDC","key");
        using var credentials=new Apex.Okx.DemoAcceptance.EphemeralCredentials(OperationalPeer.Key.ToArray(),OperationalPeer.Secret.ToArray(),OperationalPeer.Pass.ToArray());
        await using var reads=OkxExecutionObservationClient.ForTests(f,f.Clock,scope,credentials.ForObservation(scope),credentials.Fingerprint,new Fence(),(t,ct)=>{f.Clock.Advance(t);return Task.CompletedTask;});
        var groups=new List<(string? Name,string Kind,int Count)>();JsonElement result;
        if(variant=="ordinary")
        {
            var context=new OkxObservationContext(Guid.NewGuid(),Guid.NewGuid());Action<string?,string,int> observer=(n,k,c)=>groups.Add((n,k,c));
            result=algo?JsonSerializer.SerializeToElement(await reads.ReadAlgoSchemaAsync(new("123",null),context,default,observer)):JsonSerializer.SerializeToElement(await reads.ReadOrderSchemaAsync(new("BTC-USDC","987654","Ab9"),context,default,observer));
        }
        else result=await Read(reads,algo,variant!="without-observer",groups);
        Assert.Equal((int)(variant=="parsed"?OkxObservationStatus.Observed:OkxObservationStatus.Failed),result.GetProperty("Status").GetInt32());
        if(variant=="parsed")Assert.Equal(1,result.GetProperty("Rows").GetArrayLength());else Assert.Equal(0,result.GetProperty("Rows").GetArrayLength());
        if(variant=="ordinary"&&algo){Assert.DoesNotContain(groups,g=>g.Name=="syntheticUnknown");Assert.Equal("$.data[].<unknown>",result.GetProperty("Diagnostic").GetProperty("Payload").GetProperty("Path").GetString());}
        else Assert.Empty(groups);
        Assert.Single(f.Paths);
    }
}
