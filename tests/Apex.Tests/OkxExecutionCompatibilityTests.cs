using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Apex.Core.Engine;
using Apex.Core.Naval;
using Apex.Desktop.Services;
using Apex.Adapters.Okx.Execution;
using Xunit;
namespace Apex.Tests;
public sealed class OkxExecutionCompatibilityTests
{
    internal static NavalExecutionIntent Prepared(OkxExecutionFixture f)
    {
        f.Run();var r=f.Reserve();Assert.Equal(NavalMutationStatus.Applied,r.Status);f.Init();
        var ids=f.Transactions.ReserveIds(r.ReservationId!,"execution-0",new[]{NavalClientIdRole.Parent,NavalClientIdRole.ProtectionAlgo});
        Assert.Equal(NavalMutationStatus.Applied,ids.Result.Status);
        var wire=OkxPreparedRequest.Submit(f.Scope,"BTC-USD","buy","fok","4","100",ids.Ids[0].ClientId,f.Clock.Now.AddSeconds(5),new(ids.Ids[1].ClientId,"130","90"));
        var input=f.Intent(r,wire);Assert.Equal(NavalMutationStatus.Applied,f.Transactions.Prepare(r.ReservationId!,input).Status);
        return f.Transactions.Read("execution-0")!;
    }
    internal static void RefusesUnchanged(OkxExecutionFixture f,string raw)
    {
        f.Owner.Dispose();File.WriteAllText(f.Target,raw);
        var before=Directory.GetFiles(f.DirectoryPath).ToDictionary(p=>Path.GetFileName(p)!,p=>File.ReadAllBytes(p));
        var ex=Record.Exception(()=>{using var owner=new NavalCoreStateOwner(f.DirectoryPath,new FileStore(f.DirectoryPath),f.Definition);});
        Assert.True(ex!=null,"Malformed state reopened: "+raw.Substring(0,Math.Min(80,raw.Length)));
        Assert.DoesNotContain("fixture-key",ex!.Message);Assert.DoesNotContain("123456",ex.Message);
        var after=Directory.GetFiles(f.DirectoryPath).ToDictionary(p=>Path.GetFileName(p)!,p=>File.ReadAllBytes(p));
        Assert.Equal(before.Keys.OrderBy(k=>k),after.Keys.OrderBy(k=>k));foreach(var k in before.Keys)Assert.Equal(before[k],after[k]);
    }
    private static IEnumerable<string[]> Fields(JsonNode node,string[]? prefix=null)
    {
        prefix??=Array.Empty<string>();
        if(node is JsonObject obj)foreach(var item in obj)
        {var path=prefix.Append(item.Key).ToArray();yield return path;if(item.Value is {} child)foreach(var nested in Fields(child,path))yield return nested;}
        else if(node is JsonArray array)for(int n=0;n<array.Count;n++)if(array[n] is {} child)foreach(var nested in Fields(child,prefix.Append(n.ToString()).ToArray()))yield return nested;
    }
    private static JsonNode At(JsonNode node,IEnumerable<string> path)
    {foreach(string key in path)node=node is JsonArray array?array[int.Parse(key)]!:node[key]!;return node;}
    internal static string Rehash(JsonObject root)
    {
        var x=root["NavalExecution"]!.Deserialize<NavalExecutionState>(NavalJson.Options)!;
        root["NavalExecution"]=JsonSerializer.SerializeToNode(NavalExecutionValidation.Seal(x,x.FinancialRevision),NavalJson.Options);return root.ToJsonString();
    }
    // Break caught: defaults, unknown/duplicate fields or JSON coercion bypass integrity on actual reopen.
    [Fact] public void OkxExecution_StrictCompatibilityEveryRequiredField()
    {
        using var f=new OkxExecutionFixture();Prepared(f);var valid=JsonNode.Parse(File.ReadAllText(f.Target))!.AsObject();
        var paths=Fields(valid["NavalExecution"]!).ToArray();Assert.NotEmpty(paths);
        foreach(var path in paths)
        {
            var mutant=valid.DeepClone().AsObject();var parent=At(mutant["NavalExecution"]!,path.Take(path.Length-1)).AsObject();
            Assert.True(parent.Remove(path.Last()));RefusesUnchanged(f,mutant.ToJsonString());
        }
        foreach(string key in new[]{"Future","stopEpoch","integrityDigest"})
        {var mutant=valid.DeepClone().AsObject();mutant["NavalExecution"]![key]=0;RefusesUnchanged(f,mutant.ToJsonString());}
        string json=valid.ToJsonString();
        RefusesUnchanged(f,json.Insert(1,"\"NavalExecution\":null,"));
        RefusesUnchanged(f,json.Insert(1,"\"navalExecution\":null,"));
        RefusesUnchanged(f,json.Insert(1,"\"NAVALExecution\":{},"));
        f.Owner.Dispose();File.WriteAllText(f.Target,json);f.Open();f.Holds();
    }
    [Fact] public void OkxExecution_StrictCompatibilityRehashedSemanticMutants()
    {
        using var f=new OkxExecutionFixture();Prepared(f);var valid=JsonNode.Parse(File.ReadAllText(f.Target))!.AsObject();
        Action<JsonObject>[] changes={
            x=>x["NavalExecution"]!["Schema"]="future-v2",
            x=>x["NavalExecution"]!["FinancialRevision"]=0,
            x=>x["NavalExecution"]!["Namespace"]="FFFFFFFFFFFFFFFF",
            x=>x["NavalExecution"]!["HighWater"]=3,
            x=>x["NavalExecution"]!["Binding"]!["TransportEnvironment"]="Offline",
            x=>x["NavalExecution"]!["Binding"]!["Uid"]="999",
            x=>x["NavalExecution"]!["Binding"]!["Account"]!["Environment"]=1,
            x=>x["NavalExecution"]!["Intents"]![0]!["SignalId"]="foreign",
            x=>x["NavalExecution"]!["Intents"]![0]!["Path"]="/api/v5/asset/transfer",
            x=>x["NavalExecution"]!["Intents"]![0]!["Body"]=Convert.ToBase64String(new byte[]{123,125}),
            x=>x["NavalExecution"]!["Intents"]![0]!["RequestDigest"]=new string('a',64),
            x=>x["NavalExecution"]!["Intents"]![0]!["Stage"]="Accepted",
            x=>x["NavalExecution"]!["Intents"]![0]!["PreparedRevision"]=999,
            x=>x["NavalExecution"]!["IdAllocations"]![0]!["Ids"]![0]!["Counter"]=2,
            x=>x["NavalExecution"]!["IdAllocations"]![0]!["Ids"]![0]!["ClientId"]=new string('A',32),
            x=>x["NavalExecution"]!["Intents"]![0]!["AllocationKeys"]=new JsonArray("foreign"),
            x=>x["NavalExecution"]!["Intents"]![0]!["StopEpoch"]=1,
            x=>x["NavalExecution"]!["Intents"]![0]!["PreparationSessionId"]="not-a-session"
        };
        foreach(var change in changes){var m=valid.DeepClone().AsObject();change(m);RefusesUnchanged(f,Rehash(m));}
        foreach(var value in new JsonNode?[]{JsonValue.Create(0),JsonValue.Create("prepared"),JsonValue.Create("Lookup"),null})
        {var m=valid.DeepClone().AsObject();m["NavalExecution"]!["Intents"]![0]!["Operation"]=value;RefusesUnchanged(f,m.ToJsonString());}
        var oversized=valid.DeepClone().AsObject();oversized["NavalExecution"]!["Intents"]![0]!["Body"]=Convert.ToBase64String(new byte[16385]);RefusesUnchanged(f,oversized.ToJsonString());
        var cfd=valid.DeepClone().AsObject();cfd["AccountNumber"]=7;RefusesUnchanged(f,cfd.ToJsonString());
        var noNaval=valid.DeepClone().AsObject();noNaval["Naval"]=null;RefusesUnchanged(f,noNaval.ToJsonString());
        f.Owner.Dispose();File.WriteAllText(f.Target,Rehash(valid));f.Open();f.Holds();
    }
    [Fact] public void OkxExecution_MalformedCanonicalBodyHasSanitizedPreserveUpgradeDiagnostic()
    {
        using var f=new OkxExecutionFixture();Prepared(f);var e=f.Owner.Read();var x=e.NavalExecution!;var original=x.Intents.Single();
        var malformed=original with{Body=System.Text.Encoding.UTF8.GetBytes("{}")};malformed=malformed with{RequestDigest=NavalExecutionValidation.RequestDigest(malformed)};
        e.NavalExecution=NavalExecutionValidation.Seal(x with{Intents=new[]{malformed}},e.DurableRevision);string raw=NavalJson.Serialize(e);
        RefusesUnchanged(f,raw);
        var error=Record.Exception(()=>f.Open());Assert.IsType<InvalidDataException>(error);Assert.Contains("preserve",error!.Message);Assert.Contains("upgrade",error.Message);
    }
    [Fact] public void OkxExecution_MissingOrCorruptAuthorityPreservesMarkerAndCandidates()
    {
        using var f=new OkxExecutionFixture();Prepared(f);f.Owner.Dispose();string valid=File.ReadAllText(f.Target);
        string candidate=f.Target+".orphan.tmp";File.WriteAllText(candidate,valid);File.WriteAllText(f.Target+".bak",valid);
        RefusesUnchanged(f,"{corrupt");RefusesUnchanged(f,valid.Replace("\"IntegrityDigest\":\"","\"IntegrityDigest\":\"BAD"));
        File.Delete(f.Target);var before=Directory.GetFiles(f.DirectoryPath).ToDictionary(p=>p,p=>File.ReadAllBytes(p));
        Assert.ThrowsAny<IOException>(()=>f.Open());Assert.False(File.Exists(f.Target));foreach(var item in before)Assert.Equal(item.Value,File.ReadAllBytes(item.Key));
        File.WriteAllText(f.Target,valid);File.Delete(Path.Combine(f.DirectoryPath,".naval-initialized"));
        Assert.Throws<InvalidDataException>(()=>f.Open());Assert.Equal(valid,File.ReadAllText(f.Target));Assert.Equal(valid,File.ReadAllText(candidate));
    }
    [Fact] public void OkxExecution_StrictTypesAndNestedDuplicatesAreNotCoerced()
    {
        using var f=new OkxExecutionFixture();Prepared(f);var valid=JsonNode.Parse(File.ReadAllText(f.Target))!.AsObject();
        foreach(var path in new[]{new[]{"HighWater"},new[]{"Binding","Uid"},new[]{"Binding","Account","Environment"},new[]{"Intents","0","PreparedRevision"},new[]{"Intents","0","Deadline"}})
        {
            foreach(JsonNode? value in new JsonNode?[]{JsonValue.Create("0"),JsonValue.Create(0),null})
            {
                var m=valid.DeepClone().AsObject();var parent=At(m["NavalExecution"]!,path.Take(path.Length-1));
                // Keep the valid numeric-zero boundary as a control rather than classifying it malformed.
                if(path.Last()=="Environment"&&value?.ToJsonString()=="0")continue;
                parent[path.Last()]=value?.DeepClone();RefusesUnchanged(f,m.ToJsonString());
            }
        }
        string raw=valid.ToJsonString();RefusesUnchanged(f,raw.Replace("\"StopEpoch\":0","\"StopEpoch\":0,\"stopEpoch\":0"));
        RefusesUnchanged(f,raw.Replace("\"Uid\":\"123456\"","\"Uid\":\"123456\",\"Uid\":\"123456\""));
        var nested=valid.DeepClone().AsObject();nested["NavalExecution"]!["Intents"]![0]!["Binding"]!["Future"]=true;RefusesUnchanged(f,nested.ToJsonString());
    }
    [Fact] public void OkxExecution_StrictCompatibilityPreservesV1()
    {
        using var f=new OkxExecutionFixture();
        string Legacy(string name)
        {using var stream=typeof(OkxExecutionCompatibilityTests).Assembly.GetManifestResourceStream("Apex.Tests.Fixtures.okx-old-v1-"+name+".json")!;using var reader=new StreamReader(stream);return reader.ReadToEnd();}
        string noJournal=Legacy("no-journal"),journal=Legacy("journal");
        Assert.Equal(noJournal,File.ReadAllText(f.Target));Assert.DoesNotContain("NavalExecution",noJournal);Assert.DoesNotContain("NavalExecution",journal);
        foreach(string legacy in new[]{noJournal,journal})foreach(bool explicitNull in new[]{false,true})
        {
            f.Owner.Dispose();string raw=explicitNull?legacy.Insert(1,"\"NavalExecution\":null,"):legacy;File.WriteAllText(f.Target,raw);
            var marker=File.ReadAllBytes(Path.Combine(f.DirectoryPath,".naval-initialized"));f.Open();Assert.Null(f.Owner.Read().NavalExecution);
            Assert.Equal(raw,File.ReadAllText(f.Target));Assert.Equal(marker,File.ReadAllBytes(Path.Combine(f.DirectoryPath,".naval-initialized")));
        }
        long revision=f.Owner.Read().DurableRevision;string financialSeal=f.Owner.Read().Naval!.StateDigest;
        f.Init();var e=f.Owner.Read();Assert.Equal(revision+1,e.DurableRevision);Assert.Equal(e.DurableRevision,e.NavalExecution!.FinancialRevision);
        Assert.Empty(e.NavalReadJournal!.Batches.Last().Facts);Assert.Equal(e.Naval!.Generation,e.NavalReadJournal.Batches.Last().SourceGeneration);
        Assert.NotEqual(financialSeal,e.Naval.StateDigest);Assert.Equal("naval-cash-risk-v1",e.Naval.Schema);Assert.Equal(5,e.SchemaVersion);
        Assert.Equal(NavalMutationStatus.Stopped,f.Coordinator.Stop("private reason").Status);
        e=f.Owner.Read();Assert.Equal(e.DurableRevision,e.NavalExecution!.FinancialRevision);Assert.Equal(e.DurableRevision,e.NavalReadJournal!.Checkpoint.FinancialRevision);
        f.Owner.Dispose();var missing=JsonNode.Parse(journal)!.AsObject();missing["Naval"]!["Reservations"]![0]!.AsObject().Remove("ReductionUnsettled");RefusesUnchanged(f,missing.ToJsonString());
    }
}
