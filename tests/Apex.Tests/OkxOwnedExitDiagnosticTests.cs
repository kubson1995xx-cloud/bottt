using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Engine;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.Tests;

public sealed class OkxOwnedExitDiagnosticTests
{
    internal static JsonNode Oracle(string file,string name)
    {
        var dir=new DirectoryInfo(AppContext.BaseDirectory);
        while(dir!=null&&!Directory.Exists(Path.Combine(dir.FullName,"tests/Apex.Adapters.Okx.Tests/Fixtures")))dir=dir.Parent;
        return JsonNode.Parse(File.ReadAllText(Path.Combine(dir!.FullName,"tests/Apex.Adapters.Okx.Tests/Fixtures/ExecutionObservations",file)))!["endpoints"]![name]!["control"]!.DeepClone();
    }
    internal static async Task<int> Native(string scenario)
    {
        bool retrospective=scenario=="retrospective-nested";
        string reportDirectory=retrospective?"owned-exit-diagnostic-reports":"order-protection-reports";
        string root=Path.Combine("/home/ubuntu/apex-deliveries/current-market-funding-synthetic","owned-diagnostic-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root,(UnixFileMode)448);
        var binding=OkxConfiguration.Bind(new OkxNavalOptions{Schema="okx-naval-config-v1",Scopes=new[]{new OkxScopeOptions{ScopeId="finite-demo",Environment="OkxDemo",Region="EEA",ExpectedUid="99123456789",ExpectedMainUid="99123456789",QuoteCurrency="USDC",CredentialBindingId="foreground-demo",SafetyVersion="okx-cash-v1"}}})[0];
        await using var f=new OperationalPeer(root,binding,preserve:true,finite:true);using var peer=new FiniteDemoPeer(f){BaseCurrency="ADA",OwnFreezes=true};
        f.Mutate=(p,b)=>
        {
            if(p=="/api/v5/trade/order-algo")
            {
                var current=JsonSerializer.Deserialize<EngineState>(File.ReadAllText(f.Target))!;var reservation=current.Naval!.Reservations.Single();var a=Oracle("stream-algo-schema.json","detail")["data"]![0]!.DeepClone();
                a["instId"]=peer.Instrument;a["algoId"]="889";a["algoClOrdId"]=peer.Attachment;a["side"]="sell";a["tdMode"]="cash";a["tradeQuoteCcy"]="USDC";a["state"]="live";a["sz"]=(peer.BuyQuantity-peer.BuyFee).ToString(System.Globalization.CultureInfo.InvariantCulture);a["slTriggerPx"]=reservation.Intent.StopPrice.ToString(System.Globalization.CultureInfo.InvariantCulture);a["tpTriggerPx"]=reservation.Intent.TakeProfitPrice.ToString(System.Globalization.CultureInfo.InvariantCulture);a["ordIdList"]=new JsonArray();b["data"]=new JsonArray(a);return;
            }
            OkxCurrentMarketFundingTests.Shape(f,peer,p,b);
            if(p=="/api/v5/trade/order"&&peer.Canceled){b["data"]![0]!["state"]="filled";b["data"]![0]!["attachAlgoOrds"]=new JsonArray(new JsonObject{["attachAlgoId"]="888",["attachAlgoClOrdId"]=peer.Attachment,["failCode"]=""});}
        };
        var vault=new Apex.Okx.DemoAcceptance.LinuxDemoCredentialVault(Path.Combine(root,"vault"));await vault.SaveNewAsync(OperationalPeer.Key,OperationalPeer.Secret,OperationalPeer.Pass);
        f.Runtime.FiniteFundingPremise=((NavalFiniteOperatorPremise)OkxCurrentMarketFundingTests.Premise(f)) with{CredentialRecordDigest=await vault.RecordIdentityAsync()};
        f.BaselineOverride=new Apex.Okx.DemoAcceptance.SignedFiniteBaseline(f.Binding,f.Request.CredentialFingerprint,()=>f.Service);
        await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        await using var loop=OkxOperationalTradingService.Create(f.Service,f.Clock,(s,k,h,x)=>OkxExecutionClient.ForTests(peer,f.Clock,s,k,h,x),peer);loop.BindFiniteSafety(()=>true);
        Assert.True(loop.ArmFiniteDemo());Assert.Equal(NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);Assert.Equal(NavalMutationStatus.Applied,loop.PlanFiniteDemo(peer.Instrument,Guid.NewGuid().ToString("N")).Status);Assert.Equal(NavalMutationStatus.Applied,loop.Start().Status);
        f.WriteBoundary=b=>{if(peer.Sends.Count>0&&b.ToString()=="BeforeTempCreate")throw new IOException("synthetic-outcome-write-fault");};
        Assert.Equal("STORAGE_FROZEN",(await loop.SubmitFiniteDemoAsync(true,default)).Code);f.WriteBoundary=null;
        var sent=JsonSerializer.Deserialize<EngineState>(File.ReadAllText(f.Target))!;Assert.Equal(NavalExecutionStage.Dispatching,Assert.Single(sent.NavalBroker!.Operational!.Intents).Stage);Assert.Null(sent.NavalBroker.Operational.Intents[0].Outcome);
        peer.Canceled=true;peer.FillPending=true;peer.BuyQuantity=sent.Naval!.Reservations.Single().Quantity;peer.BuyFee=peer.BuyQuantity*peer.FeeRate;peer.BuyPrice=sent.Naval.Reservations.Single().Intent.EntryPriceCeiling;peer.Attachment=sent.NavalBroker.Operational.IdAllocations.SelectMany(a=>a.Ids).Single(i=>i.Role==NavalClientIdRole.ProtectionAlgo).ClientId;
        await loop.DisposeAsync();await f.Service.DisconnectAsync();
        foreach(string dir in Directory.EnumerateDirectories(root,"*",SearchOption.AllDirectories))File.SetUnixFileMode(dir,(UnixFileMode)448);
        foreach(string file in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories))File.SetUnixFileMode(file,(UnixFileMode)384);
        File.WriteAllText(Path.Combine(root,"last-report.json"),"ORIGINAL_PRIVATE_REPORT");File.SetUnixFileMode(Path.Combine(root,"last-report.json"),(UnixFileMode)384);
        f.Clock.Advance(TimeSpan.FromMinutes(11));bool locked=false;
        var composition=new Apex.Okx.DemoAcceptance.FiniteEntryTestComposition(f.Clock,(s,k,h,x)=>{Assert.Equal("STOP",JsonSerializer.Deserialize<EngineState>(File.ReadAllText(f.Target))!.Naval!.Mode);try{using var other=new FileStream(Path.Combine(root,".run.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None);}catch(IOException){locked=true;}Assert.True(locked);return OkxExecutionObservationClient.ForTests(f,f.Clock,s,k,h,x,(t,ct)=>{f.Clock.Advance(t);return Task.CompletedTask;});},(s,k,h,x)=>throw new Exception("NO_STREAM"),(s,k,h,x)=>throw new Exception("NO_WRITER"),peer);
        // Establish the actual-shaped existing fill/net state using the accepted
        // observation path, not hand-written seals or a second financial ledger.
        Assert.Equal(0,await Apex.Okx.DemoAcceptance.Program.RunFiniteForTestsAsync(new[]{"--recover-observe"},root,vault,(_,_)=>throw new Exception("NO_DISCOVERY"),composition));
        var before=JsonSerializer.Deserialize<EngineState>(File.ReadAllText(f.Target))!;Assert.Single(before.Naval!.Fills);Assert.True(before.Naval.Reservations.Single().Inventory>0);
        string baseline=NavalJson.Serialize(before.NavalBroker!.Baseline),economic=NavalJson.Serialize(before.Naval.Reservations),intents=NavalJson.Serialize(before.NavalBroker.Operational!.Intents),funding=NavalJson.Serialize(before.NavalBroker.Operational.LastCurrentMarketFunding);
        var mutate=f.Mutate!;f.Mutate=(p,b)=>{mutate(p,b);if(p=="/api/v5/trade/order"&&scenario is "unknown-order" or "secret-name")b["data"]![0]![scenario=="secret-name"?System.Text.Encoding.ASCII.GetString(OperationalPeer.Secret):"privateUnrecognized"]="VALUE_MUST_NOT_LEAK";if(p=="/api/v5/trade/order-algo"&&scenario=="unknown-algo")b["data"]![0]!["privateUnrecognized"]="VALUE_MUST_NOT_LEAK";if(p=="/api/v5/account/config"&&scenario=="account-refused")b["data"]![0]!["uid"]="99999999";if(p=="/api/v5/trade/order"&&scenario=="unknown-envelope")b["privateUnrecognized"]="VALUE_MUST_NOT_LEAK";};
        int originalReads=0;
        if(scenario=="dual-refusal")
        {
            var prior=f.Mutate!;f.Mutate=(p,b)=>{prior(p,b);if(p=="/api/v5/trade/order"){b["data"]![0]!["slippage"]="";b["data"]![0]![originalReads++==0?"originalUnknown":"laterUnknown"]="VALUE_MUST_NOT_LEAK";}if(p=="/api/v5/trade/order-algo"){b["data"]![0]!["slippage"]="";b["data"]![0]!["protectionUnknown"]="VALUE_MUST_NOT_LEAK";}};
        }
        if(retrospective)
        {
            var prior=f.Mutate!;f.Mutate=(p,b)=>
            {
                prior(p,b);if(p is not ("/api/v5/trade/order" or "/api/v5/trade/order-algo"))return;
                var row=b["data"]![0]!;bool algo=p.EndsWith("order-algo",StringComparison.Ordinal);
                if(algo){row["aggressiveness"]="opaque";row["lmtOrderNumber"]="opaque";row["triggerParams"]=new JsonArray();}
                else row["slippage"]="synthetic-nonempty-opaque";
                row["attachAlgoOrds"]=new JsonArray(new JsonObject{[algo?"slOrdPx":"failCode"]="",[algo?"protectionUnknown":originalReads++==0?"originalUnknown":"laterUnknown"]="VALUE_MUST_NOT_LEAK"});
            };
        }
        string fills=NavalJson.Serialize(before.Naval.Fills),allocations=NavalJson.Serialize(before.NavalBroker.Operational!.IdAllocations),protection=NavalJson.Serialize(before.NavalBroker.Operational.Protection);
        var protectedFiles=Directory.GetFiles(root,"*",SearchOption.AllDirectories).Where(p=>p!=f.Target).ToDictionary(p=>p,p=>File.ReadAllBytes(p));
        f.Paths.Clear();int sends=peer.Sends.Count;
        int code=await Apex.Okx.DemoAcceptance.Program.RunFiniteForTestsAsync(new[]{retrospective?"--diagnose-owned-exit":"--recover-diagnose"},root,vault,(_,_)=>throw new Exception("NO_DISCOVERY"),composition);
        Assert.True(code==(scenario=="account-refused"?7:5),"Missing GET-only diagnostic entry; exit="+code);
        var after=JsonSerializer.Deserialize<EngineState>(File.ReadAllText(f.Target))!;Assert.Equal("STOP",after.Naval!.Mode);Assert.Equal(baseline,NavalJson.Serialize(after.NavalBroker!.Baseline));Assert.Equal(economic,NavalJson.Serialize(after.Naval.Reservations));Assert.Equal(intents,NavalJson.Serialize(after.NavalBroker.Operational!.Intents));Assert.Equal(funding,NavalJson.Serialize(after.NavalBroker.Operational.LastCurrentMarketFunding));Assert.Equal(before.NavalBroker.Namespace,after.NavalBroker.Namespace);Assert.Equal(before.NavalBroker.HighWater,after.NavalBroker.HighWater);Assert.Equal(sends,peer.Sends.Count);Assert.Equal("ORIGINAL_PRIVATE_REPORT",File.ReadAllText(Path.Combine(root,"last-report.json")));
        string path=Directory.EnumerateFiles(Path.Combine(root,reportDirectory),"*.json").Single();using var report=JsonDocument.Parse(File.ReadAllBytes(path));Assert.True(report.RootElement.GetProperty("complete").GetBoolean());Assert.False(report.RootElement.GetProperty("armed").GetBoolean());Assert.False(report.RootElement.GetProperty("schedulerStarted").GetBoolean());
        if(scenario=="account-refused")Assert.Equal(new[]{"/api/v5/account/config"},f.Paths);
        else
        {
            Assert.Equal(4,f.Paths.Count);Assert.Equal("/api/v5/account/config",f.Paths[0]);Assert.Contains("clOrdId="+peer.PendingClient,f.Paths[1]);Assert.Contains("ordId=776",f.Paths[2]);Assert.Contains("algoClOrdId="+peer.Attachment,f.Paths[3]);
            var observation=report.RootElement.GetProperty("transport").GetProperty("observation");Assert.False(observation.GetProperty("financialClearance").GetBoolean());var reads=observation.GetProperty("reads");Assert.Equal(3,reads.GetArrayLength());
            if(scenario=="unknown-order"){Assert.Equal(JsonValueKind.Null,reads[0].GetProperty("observation").ValueKind);Assert.Equal("UNSUPPORTED_PROPERTY",reads[0].GetProperty("diagnostic").GetProperty("Payload").GetProperty("Reason").GetString());}
        }
        var inventories=Directory.GetFiles(Path.Combine(root,reportDirectory),"order-protection-identifiers.json",SearchOption.AllDirectories);
        bool inventory=retrospective||scenario is "unknown-order" or "unknown-algo" or "unknown-envelope" or "dual-refusal";Assert.Equal(retrospective||scenario=="dual-refusal"?2:inventory?1:0,inventories.Length);
        if(retrospective||scenario=="dual-refusal")
        {
            foreach(string file in inventories)
            {
                using var meta=JsonDocument.Parse(File.ReadAllBytes(file));var m=meta.RootElement;bool algo=m.GetProperty("endpoint").GetString()=="/api/v5/trade/order-algo";
                Assert.Equal(algo?"protection":"order",Path.GetFileName(Path.GetDirectoryName(file)));
                Assert.Equal(retrospective?"$.data[].attachAlgoOrds[].<unknown>":"$.data[].<unknown>",m.GetProperty("path").GetString());var groups=m.GetProperty("groups");var g=Assert.Single(groups.EnumerateArray());
                Assert.Equal(algo?"protectionUnknown":"originalUnknown",g.GetProperty("name").GetString());Assert.Equal("String",g.GetProperty("kind").GetString());Assert.Equal(1,g.GetProperty("count").GetInt32());
                Assert.DoesNotContain("laterUnknown",m.GetRawText());Assert.DoesNotContain("VALUE_MUST_NOT_LEAK",m.GetRawText());
            }
            var observed=report.RootElement.GetProperty("transport").GetProperty("observation").GetProperty("reads");
            foreach(var r in observed.EnumerateArray()){Assert.Equal(JsonValueKind.Null,r.GetProperty("observation").ValueKind);Assert.Equal("UNSUPPORTED_PROPERTY",r.GetProperty("diagnostic").GetProperty("Payload").GetProperty("Reason").GetString());}
        }
        else if(inventory){string text=File.ReadAllText(inventories[0]);Assert.Contains("privateUnrecognized",text);Assert.DoesNotContain("VALUE_MUST_NOT_LEAK",text);Assert.DoesNotContain(binding.Account.AccountId,text);}
        Assert.Equal(fills,NavalJson.Serialize(after.Naval.Fills));Assert.Equal(allocations,NavalJson.Serialize(after.NavalBroker.Operational.IdAllocations));Assert.Equal(protection,NavalJson.Serialize(after.NavalBroker.Operational.Protection));
        foreach(var pair in protectedFiles)Assert.True(pair.Value.AsSpan().SequenceEqual(File.ReadAllBytes(pair.Key)),"Original protected file changed");
        int diagnosticGets=f.Paths.Count;
        if(retrospective)await OkxOwnedAttachmentNativeControls.Check(f,peer,root,vault,composition,before,mutate,sends);
        File.WriteAllText(Path.Combine(root,"diagnostic-summary.json"),JsonSerializer.Serialize(new{scenario,code,mode=after.Naval.Mode,getCount=diagnosticGets,inventory,lockHeld=locked,noNewSubmitOrCancel=peer.Sends.Count==sends,originalReportPreserved=true,baselinePreserved=true,netInventoryPreserved=true,originalDispatchingPreserved=true}));Console.WriteLine("SYNTHETIC_OWNED_DIAGNOSTIC_PASS; root="+root);return 0;
    }
    private sealed class Fence:IOkxObservationReadFence { public bool CanObserve(OkxExecutionScope s,OkxObservationContext c,DateTimeOffset t)=>true; }
    // Removing diagnostic propagation (without changing strict admission) makes
    // these tests fail. Synthetic values are never asserted as actual broker data.
    [Theory]
    [InlineData(false,"type","TYPE","$.data[].fee","String","Number",null)]
    [InlineData(false,"unknown","UNSUPPORTED_PROPERTY","$.data[].<unknown>","KnownProperty","String",null)]
    [InlineData(false,"number","NUMBER","$.data[].fee","ExactDecimal","String",null)]
    [InlineData(false,"timestamp","TIMESTAMP","$.data[].uTime","UnixMilliseconds","String",null)]
    [InlineData(false,"empty","CARDINALITY","$.data","ExactlyOne","Empty",0)]
    [InlineData(false,"many","CARDINALITY","$.data","ExactlyOne","Multiple",2)]
    [InlineData(false,"nested-type","TYPE","$.data[].attachAlgoOrds[].failCode","String","Boolean",null)]
    [InlineData(false,"nested-count","CARDINALITY","$.data[].attachAlgoOrds","AtMost32","OverLimit",33)]
    [InlineData(false,"identity","IDENTIFIER","$.data[].ordId","OrderId","String",null)]
    [InlineData(false,"duplicate","DUPLICATE_PROPERTY","$.data[].fee","UniqueProperty","String",null)]
    [InlineData(true,"type","TYPE","$.data[].sz","String","Number",null)]
    [InlineData(true,"unknown","UNSUPPORTED_PROPERTY","$.data[].<unknown>","KnownProperty","String",null)]
    [InlineData(true,"number","NUMBER","$.data[].sz","ExactDecimal","String",null)]
    [InlineData(true,"timestamp","TIMESTAMP","$.data[].uTime","UnixMilliseconds","String",null)]
    [InlineData(true,"empty","CARDINALITY","$.data","ExactlyOne","Empty",0)]
    [InlineData(true,"many","CARDINALITY","$.data","ExactlyOne","Multiple",2)]
    [InlineData(true,"nested-type","TYPE","$.data[].attachAlgoOrds[].slOrdPx","String","Boolean",null)]
    [InlineData(true,"nested-count","CARDINALITY","$.data[].ordIdList","AtMost256","OverLimit",257)]
    [InlineData(true,"identity","IDENTIFIER","$.data[].algoId","OrderId","String",null)]
    public async Task Exact_order_and_protection_retain_responsible_guard(bool algo,string change,string reason,string path,string expected,string actual,int? count)
    {
        await using var f=new OperationalPeer();
        var body=Oracle(algo?"stream-algo-schema.json":"recovery-schema.json",algo?"detail":"order");var row=body["data"]![0]!;
        switch(change)
        {
            case "type":row[algo?"sz":"fee"]=7;break;
            case "unknown":row["privateUnrecognized"]="VALUE_MUST_NOT_LEAK";break;
            case "number":row[algo?"sz":"fee"]="PRIVATE_NUMBER_SENTINEL";break;
            case "timestamp":row["uTime"]="PRIVATE_TIME_SENTINEL";break;
            case "empty":body["data"]=new JsonArray();break;
            case "many":body["data"]!.AsArray().Add(row.DeepClone());break;
            case "nested-type":row["attachAlgoOrds"]=new JsonArray(new JsonObject{[algo?"slOrdPx":"failCode"]=false});break;
            case "nested-count":var a=new JsonArray();for(int n=0;n<(algo?257:33);n++)a.Add(algo?JsonValue.Create("123"):new JsonObject());row[algo?"ordIdList":"attachAlgoOrds"]=a;break;
            case "identity":row[algo?"algoId":"ordId"]="PRIVATE_BAD_ID";break;
            case "duplicate":row["Fee"]="0";break;
        }
        f.Mutate=(p,b)=>{b["data"]=body["data"]!.DeepClone();};
        var scope=new OkxExecutionScope("Demo",f.Binding.Account.AccountId,f.Binding.MainUid,"USDC","key");
        using var credentials=new Apex.Okx.DemoAcceptance.EphemeralCredentials(OperationalPeer.Key.ToArray(),OperationalPeer.Secret.ToArray(),OperationalPeer.Pass.ToArray());
        var context=new OkxObservationContext(Guid.NewGuid(),Guid.NewGuid());
        await using var reads=OkxExecutionObservationClient.ForTests(f,f.Clock,scope,credentials.ForObservation(scope),credentials.Fingerprint,new Fence(),(t,ct)=>{f.Clock.Advance(t);return Task.CompletedTask;});
        JsonElement doc;
        if(algo){var r=await reads.ReadAlgoAsync(new("123",null),context,default);Assert.Equal(OkxObservationStatus.Failed,r.Status);Assert.Empty(r.Rows);doc=JsonSerializer.SerializeToElement(r);}
        else{var r=await reads.ReadOrderAsync(new("BTC-USDC","987654","Ab9"),context,default);Assert.Equal(OkxObservationStatus.Failed,r.Status);Assert.Empty(r.Rows);doc=JsonSerializer.SerializeToElement(r);}
        Assert.True(doc.TryGetProperty("Diagnostic",out var d),"Missing closed exact-order/protection parser diagnostic");
        Assert.Equal(200,d.GetProperty("HttpStatus").GetInt32());
        // A recursive duplicate fails before validated envelope code exists.
        if(change!="duplicate")Assert.Equal("0",d.GetProperty("OkxCode").GetString());
        var failure=d.GetProperty("Payload");Assert.Equal(reason,failure.GetProperty("Reason").GetString());Assert.Equal(path,failure.GetProperty("Path").GetString());Assert.Equal(expected,failure.GetProperty("Expected").GetString());Assert.Equal(actual,failure.GetProperty("Actual").GetString());
        if(count.HasValue)Assert.Equal(count.Value,failure.GetProperty("ActualCount").GetInt32());
        string text=d.GetRawText();foreach(string value in new[]{"VALUE_MUST_NOT_LEAK","PRIVATE_NUMBER_SENTINEL","PRIVATE_TIME_SENTINEL","PRIVATE_BAD_ID","privateUnrecognized"})Assert.DoesNotContain(value,text);
        Assert.Single(f.Paths);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Populated_official_exact_endpoint_is_already_compatible_and_identity_is_not_relabelled(bool algo)
    {
        await using var f=new OperationalPeer();var wire=Oracle(algo?"stream-algo-schema.json":"recovery-schema.json",algo?"detail":"order");
        f.Mutate=(p,b)=>b["data"]=wire["data"]!.DeepClone();
        var scope=new OkxExecutionScope("Demo",f.Binding.Account.AccountId,f.Binding.MainUid,"USDC","key");
        using var credentials=new Apex.Okx.DemoAcceptance.EphemeralCredentials(OperationalPeer.Key.ToArray(),OperationalPeer.Secret.ToArray(),OperationalPeer.Pass.ToArray());
        await using var reads=OkxExecutionObservationClient.ForTests(f,f.Clock,scope,credentials.ForObservation(scope),credentials.Fingerprint,new Fence());var context=new OkxObservationContext(Guid.NewGuid(),Guid.NewGuid());
        if(algo){var read=await reads.ReadAlgoAsync(new("123","Ab9"),context,default);Assert.Equal(OkxObservationStatus.Observed,read.Status);Assert.NotEqual("Ab9",Assert.Single(read.Rows).AlgoClOrdId);}
        else{var read=await reads.ReadOrderAsync(new("BTC-USDC","987654","Ab9"),context,default);Assert.Equal(OkxObservationStatus.Observed,read.Status);Assert.NotEqual("Ab9",Assert.Single(read.Rows).ClOrdId);}
    }
}
