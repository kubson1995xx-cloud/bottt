using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Engine;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Apex.Okx.DemoAcceptance;
using Xunit;
namespace Apex.Tests;

// Every financial value in this file is synthetic. Never invoke production Main.
[TestCaseOrderer("Apex.Tests.ControllerSnapshotOrderer","Apex.Tests")]
public sealed class OkxControllerSnapshotTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper log;
    public OkxControllerSnapshotTests(Xunit.Abstractions.ITestOutputHelper log){this.log=log;}
    private void ReportConsumedAssemblies()
    {
        foreach(var assembly in new[]{typeof(OkxControllerSnapshotTests).Assembly,typeof(LinuxDemoHost).Assembly,typeof(OkxNavalRuntime).Assembly,typeof(EngineState).Assembly,typeof(OkxExecutionClient).Assembly})
        {
            Assert.Equal(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,Path.GetFileName(assembly.Location))),Path.GetFullPath(assembly.Location));
            log.WriteLine("CONSUMED "+assembly.GetName().Name+" "+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(assembly.Location)))+" "+assembly.Location);
        }
    }
    private static Type Codec()=>typeof(OkxNavalRuntime).Assembly.GetType("Apex.Desktop.Okx.OkxControllerSnapshotCodec")
        ??throw new Xunit.Sdk.XunitException("Shared portable controller codec is missing");
    private static object Decode(string line)=>Codec().GetMethod("Decode",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new object[]{line})!;
    private static JsonObject Data(string line){Decode(line);return JsonNode.Parse(line["CONTROLLER_SNAPSHOT ".Length..])!.AsObject();}
    private static string Wire(JsonNode node)=>"CONTROLLER_SNAPSHOT "+node.ToJsonString();
    private static JsonArray Items(JsonNode node,string name)=>node[name]!["Items"]!.AsArray();
    [Fact]
    public async Task Disconnected_snapshot_has_no_finances_and_opens_no_root_vault_or_discovery()
    {
        ReportConsumedAssemblies();
        string root=Path.Combine(Environment.GetEnvironmentVariable("TMPDIR")!,"snapshot-absent-"+Guid.NewGuid().ToString("N"));
        using var input=new Input();using var output=new Output();
        var run=LinuxDemoHost.RunForTestsAsync(root,new LinuxDemoCredentialVault(Path.Combine(root,"vault")),input,output,(_,_)=>throw new Exception("DISCOVERY_FORBIDDEN"),null,default);
        try
        {
            await output.Expect("STOP_DISCONNECTED_UNARMED");input.Send("SNAPSHOT");var line=await output.Snapshot();var d=Data(line);
            Assert.False(d["Connected"]!.GetValue<bool>());Assert.Equal("STOP",d["Mode"]!.GetValue<string>());Assert.False(d["Armed"]!.GetValue<bool>());
            Assert.Null(d["Revision"]);Assert.Null(d["Capital"]);Assert.Null(d["Observation"]!["CaptureUtc"]);Assert.Empty(Items(d,"Balances"));Assert.Empty(Items(d,"History"));Assert.Empty(Items(d,"Catalog"));
            Assert.DoesNotContain(root,line);Assert.DoesNotContain("Uid",line);Assert.DoesNotContain("Fingerprint",line);Assert.False(Directory.Exists(root));
            input.Send("START");Assert.Equal("ACTION_CANCELLED_STOP",await output.Expect("ACTION_CANCELLED_STOP"));input.Send("STOP");await output.Expect("STOP_DRAINED_UNARMED");input.End();
            Assert.Equal(0,await run.WaitAsync(TimeSpan.FromSeconds(10)));Assert.Contains("HOST_DRAINED_CREDENTIALS_CLOSED",output.ToString());Assert.False(Directory.Exists(root));
        }
        finally{input.End();await run.WaitAsync(TimeSpan.FromSeconds(10));}
    }
    [Fact]
    public async Task Connected_host_projects_same_mixed_owner_pinned_dust_history_and_original_clocks_without_reads()
    {
        ReportConsumedAssemblies();
        await using var f=new OperationalPeer(finite:true);using var wire=new FiniteDemoPeer(f){BaseCurrency="SOL"};
        f.Mutate=(p,b)=>OkxOrdinaryMixedAccountTests.Shape(f,wire,p,b);f.Runtime.FiniteFundingPremise=(NavalFiniteOperatorPremise)OkxCurrentMarketFundingTests.Premise(f);
        await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        await using(var finite=OkxOperationalTradingService.Create(f.Service,f.Clock,(s,k,h,x)=>OkxExecutionClient.ForTests(wire,f.Clock,s,k,h,x),wire))
        {finite.BindFiniteSafety(()=>true);Assert.Equal("COMPLETED_STOP_WITH_EXACT_DUST",await OkxFiniteDemoSequence.RunAsync(finite,f.Service,Guid.NewGuid().ToString("N"),_=>{},default));}
        await f.Service.DisconnectAsync();
        await using var runtime=OkxNavalRuntime.ComposeLinuxDemo(f.Root,f.Binding,f,f,()=>true);
        await using var account=OkxAccountConnectionService.ForTests(runtime,f.Root,f.Binding,f,f.Clock,
            (s,k,h,x)=>OkxExecutionObservationClient.ForTests(f,f.Clock,s,k,h,x,(t,c)=>{f.Clock.Advance(t);return Task.CompletedTask;}),
            (s,k,h,x)=>OkxObservationStream.ForTests((uri,c)=>Task.FromResult<System.Net.WebSockets.WebSocket>(new OperationalSocket(uri)),f.Clock,s,k,h,x));
        var request=f.Request with{Session=Guid.NewGuid()};var lease=await runtime.Activation.RequestAsync(request,default);Assert.True(await account.ConnectAsync(request,lease!,default),account.Status);
        var loop=OkxOperationalTradingService.Create(account,f.Clock,(s,k,h,x)=>OkxExecutionClient.ForTests(wire,f.Clock,s,k,h,x),wire);
        Assert.Equal("ORDINARY_DEMO_PREPARED",(await loop.PrepareOrdinaryDemoAsync(default)).Code);
        var before=account.BrokerSession!.ReadState();int reads=f.Paths.Count,opens=f.SecretOpens,posts=wire.Sends.Count;var view=account.Coordinator!.Snapshot();
        Decode(OkxControllerSnapshotCodec.Encode(loop.ReadControllerSnapshot()));
        using var input=new Input();using var output=new Output();var run=ConnectedHost(f.Root,account,loop,input,output);
        try
        {
            await output.Expect("STOP_DISCONNECTED_UNARMED");input.Send("SNAPSHOT");var d=Data(await output.Snapshot());
            Assert.True(d["Connected"]!.GetValue<bool>());Assert.Equal(before.DurableRevision,d["Revision"]!.GetValue<long>());
            Assert.Equal(view.Cash,d["Capital"]!["Cash"]!.GetValue<decimal>());Assert.Equal(view.Held,d["Capital"]!["Held"]!.GetValue<decimal>());Assert.Equal(view.Free,d["Capital"]!["Free"]!.GetValue<decimal>());
            Assert.Equal("USDC",d["Capital"]!["Quote"]!.GetValue<string>());Assert.Equal(6,Items(d,"Balances").Count);
            Assert.Contains(Items(d,"Balances"),b=>b!["Currency"]!.GetValue<string>()=="BTC"&&b["Excluded"]!.GetValue<bool>());
            Assert.Contains(Items(d,"Balances"),b=>b!["Currency"]!.GetValue<string>()=="ETH"&&b["Excluded"]!.GetValue<bool>());
            var expected=before.NavalBroker!.Operational!;Assert.Equal(expected.LastBalanceCaptureUtc,DateTimeOffset.Parse(d["Observation"]!["CaptureUtc"]!.GetValue<string>()));
            foreach(var row in expected.LastBalances!)
            {var b=Items(d,"Balances").Single(v=>v!["Currency"]!.GetValue<string>()==row.Currency)!;Assert.Equal(row.Cash,b["Total"]?.GetValue<decimal>());Assert.Equal(row.Available,b["Available"]?.GetValue<decimal>());Assert.Equal(row.Frozen,b["Frozen"]?.GetValue<decimal>());Assert.Equal(row.SourceUtc,b["SourceUtc"]==null?null:DateTimeOffset.Parse(b["SourceUtc"]!.GetValue<string>()));}
            Assert.Equal(before.Naval!.ReconciledUtc,DateTimeOffset.Parse(d["Observation"]!["EconomicUtc"]!.GetValue<string>()));
            var balanceEvidence=expected.Evidence.Last(e=>e.Endpoint=="/api/v5/account/balance");Assert.Equal(balanceEvidence.RequestUtc,DateTimeOffset.Parse(d["Observation"]!["RequestUtc"]!.GetValue<string>()));Assert.Equal(balanceEvidence.ReceiveUtc,DateTimeOffset.Parse(d["Observation"]!["ReceiveUtc"]!.GetValue<string>()));
            Assert.Equal(new[]{"SOL-USDC"},Items(d,"Catalog").Select(i=>i!["Instrument"]!.GetValue<string>()));
            var dust=Items(d,"Inventory").Single(i=>i!["Quantity"]!.GetValue<decimal>()>0)!;Assert.Equal("UNPROTECTED_QUARANTINED_FULL_LOSS_RESERVED",dust["Protection"]!.GetValue<string>());Assert.True(dust["RiskHold"]!.GetValue<decimal>()>0);
            Assert.Equal(2,Items(d,"History").Count);Assert.Contains(Items(d,"History"),h=>h!["FeeCurrency"]!.GetValue<string>()=="SOL");Assert.Contains(Items(d,"History"),h=>h!["FeeCurrency"]!.GetValue<string>()=="USDC");Assert.NotEmpty(Items(d,"Orders"));Assert.NotEmpty(Items(d,"Protection"));
            Assert.Equal(0,d["Evaluation"]!["Completed"]!.GetValue<long>());
            input.Send("SNAPSHOT");var second=Data(await output.Snapshot());Assert.Equal(d["Observation"]!.ToJsonString(),second["Observation"]!.ToJsonString());Assert.Equal(before.DurableRevision,account.BrokerSession.ReadState().DurableRevision);
            Assert.Equal(reads,f.Paths.Count);Assert.Equal(opens,f.SecretOpens);Assert.Equal(posts,wire.Sends.Count);
            VerifyDecoder(d);VerifyBounds(before,loop.ReadStatus(),view,f.Clock.GetUtcNow());
            foreach(string forbidden in new[]{f.Root,f.Binding.Account.AccountId,f.Request.CredentialFingerprint,Encoding.ASCII.GetString(OperationalPeer.Key),Encoding.ASCII.GetString(OperationalPeer.Secret),Encoding.ASCII.GetString(OperationalPeer.Pass)})Assert.DoesNotContain(forbidden,d.ToJsonString());
            input.Send("STOP");await output.Expect("STOP_DRAINED_UNARMED");input.Send("DISCONNECT");await output.Expect("STOP_DISCONNECTED_UNARMED");input.Send("SNAPSHOT");var disconnected=Data(await output.Snapshot());Assert.False(disconnected["Connected"]!.GetValue<bool>());Assert.Null(disconnected["Capital"]);Assert.Empty(Items(disconnected,"Balances"));Assert.Empty(Items(disconnected,"History"));input.End();Assert.Equal(0,await run.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally{input.End();await run.WaitAsync(TimeSpan.FromSeconds(10));}
    }
    private static void VerifyDecoder(JsonObject good)
    {
        Decode(Wire(good));
        foreach(Action<JsonObject> mutate in new Action<JsonObject>[]{d=>d["Schema"]="future",d=>d.Remove("Connected"),d=>d["Unexpected"]="x",d=>d["Connected"]="true",d=>d["Mode"]="UNKNOWN",d=>d["Balances"]!["Total"]=1,d=>d["Balances"]!["Truncated"]=true,d=>d["Evaluation"]!["Completed"]=-1,d=>d["Ready"]=true})
        {var bad=(JsonObject)good.DeepClone();mutate(bad);Assert.Throws<TargetInvocationException>(()=>Decode(Wire(bad)));}
        foreach(string bad in new[]{"{}","CONTROLLER_SNAPSHOT null","CONTROLLER_SNAPSHOT {",Wire(good)+"\n",Wire(good).Replace("\"Connected\":true","\"Connected\":true,\"Connected\":false"),"CONTROLLER_SNAPSHOT "+new string(' ',262145)})Assert.Throws<TargetInvocationException>(()=>Decode(bad));
    }
    private static void VerifyBounds(EngineState original,OkxOperationalTradingStatus status,NavalPortfolioView view,DateTimeOffset now)
    {
        var e=JsonSerializer.Deserialize<EngineState>(JsonSerializer.Serialize(original))!;var b=e.NavalBroker!;var x=b.Operational!;
        var template=x.Intents.First(i=>i.Operation==NavalExecutionOperation.Submit&&!i.Reduction&&i.DispatchRevision!=null);
        var order=x.Orders.First(o=>o.ClientId==template.ClientId);var protection=x.Protection.First();var allocation=x.IdAllocations.First(a=>a.Ids.Any(i=>i.Role==NavalClientIdRole.ProtectionAlgo));
        var reservations=Enumerable.Range(0,70).Select(n=>e.Naval!.Reservations.First(r=>r.QuarantineMark!=null) with{Id="r"+n,RiskHold=n+1,Intent=templateInstrument(n)}).ToArray();
        NavalEntryIntent templateInstrument(int n)=>e.Naval!.Reservations.First(r=>r.QuarantineMark!=null).Intent with{CommandId="command-"+(100-n)};
        var intents=Enumerable.Range(0,70).Select(n=>template with{Id="intent"+n,ReservationId="r"+n,ClientId="client"+n,PreparedRevision=n+1,DispatchRevision=n+2,Outcome=null}).ToArray();
        var orders=Enumerable.Range(0,70).Select(n=>order with{OrderId=(1000-n).ToString(),ClientId="client"+n,Quantity=n+1,Accumulated=0}).ToArray();
        var protections=Enumerable.Range(0,70).Select(n=>protection with{ParentOrderId=(1000-n).ToString(),AlgoId=(2000-n).ToString(),AttachmentClientId="attachment"+n,Quantity=n+1}).ToArray();
        var allocations=Enumerable.Range(0,70).Select(n=>allocation with{ReservationId="r"+n,Ids=new[]{allocation.Ids.First(i=>i.Role==NavalClientIdRole.ProtectionAlgo) with{ReservationId="r"+n,ClientId="attachment"+n}}}).ToArray();
        // Projection-only overflow/sparse operands cloned from the admitted synthetic owner.
        var fills=Enumerable.Range(0,70).Select(n=>e.Naval!.Fills[0] with{Fill=e.Naval.Fills[0].Fill with{TradeId=n.ToString(),TradeUtc=now.AddSeconds(-100+n)}}).ToArray();e.Naval=e.Naval! with{Fills=fills,Reservations=reservations};
        var balances=x.LastBalances!.Select(v=>v.Currency=="BTC"?v with{Cash=null,Available=null,Frozen=null,SourceUtc=null}:v).ToArray();e.NavalBroker=b with{Operational=x with{LastBalances=balances,Intents=intents,Orders=orders,Protection=protections,IdAllocations=allocations}};
        var projector=typeof(OkxNavalRuntime).Assembly.GetType("Apex.Desktop.Okx.OkxControllerSnapshotProjector")!;
        var snapshot=projector.GetMethod("Project",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new object[]{e,status,view,now})!;
        string line=(string)Codec().GetMethod("Encode",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new[]{snapshot})!;var d=Data(line);
        Assert.InRange(Encoding.UTF8.GetByteCount(line),1,262144);Assert.Equal(70,d["History"]!["Total"]!.GetValue<int>());Assert.True(d["History"]!["Truncated"]!.GetValue<bool>());Assert.Equal(32,Items(d,"History").Count);
        Assert.Equal(now.AddSeconds(-31),DateTimeOffset.Parse(Items(d,"History")[0]!["TradeUtc"]!.GetValue<string>()));Assert.Equal(now.AddSeconds(-62),DateTimeOffset.Parse(Items(d,"History")[31]!["TradeUtc"]!.GetValue<string>()));
        foreach(string section in new[]{"Inventory","Orders","Protection"}){Assert.Equal(70,d[section]!["Total"]!.GetValue<int>());Assert.True(d[section]!["Truncated"]!.GetValue<bool>());Assert.Equal(32,Items(d,section).Count);}
        Assert.Equal(70m,Items(d,"Inventory")[0]!["RiskHold"]!.GetValue<decimal>());Assert.Equal(70m,Items(d,"Orders")[0]!["Quantity"]!.GetValue<decimal>());Assert.Equal(70m,Items(d,"Protection")[0]!["Quantity"]!.GetValue<decimal>());
        var btc=Items(d,"Balances").Single(v=>v!["Currency"]!.GetValue<string>()=="BTC")!;Assert.Null(btc["Total"]);Assert.Null(btc["Available"]);Assert.Null(btc["Frozen"]);Assert.Null(btc["SourceUtc"]);
        e.Naval=e.Naval with{Fills=Array.Empty<NavalAcceptedFill>(),Reservations=Array.Empty<NavalReservation>()};e.NavalBroker=b with{Operational=new NavalBrokerOperationalState()};
        var empty=OkxControllerSnapshotProjector.Project(e,status,view,now);Assert.False(empty.Observation.HistoryAvailable);Assert.Equal(b.Baseline.Observations.CaptureUtc,empty.Observation.CaptureUtc);Assert.Equal("BASELINE",empty.Observation.Origin);
    }
    [Fact]
    public void Decoder_preserves_source_update_during_collection_and_refuses_inconsistent_or_missing_operands()
    {
        var now=DateTimeOffset.Parse("2026-11-05T12:00:10Z");
        var d=OkxControllerSnapshotCodec.Disconnected(now) with{Connected=true,Revision=12,Capital=new("EUR",10,2,8,1,null),
            Observation=new("OPERATIONAL",now.AddSeconds(-5),now.AddSeconds(-4),now.AddSeconds(-1),now.AddSeconds(-5),true,false),
            Balances=new(1,false,new[]{new OkxControllerBalance("EUR",10,null,2,now.AddSeconds(-2),false)}),
            Catalog=new(1,false,new[]{new OkxControllerInstrument("SOL-EUR","SOL","EUR",.001m,.001m,.01m)})};
        var decoded=OkxControllerSnapshotCodec.Decode(OkxControllerSnapshotCodec.Encode(d));
        Assert.Equal(now.AddSeconds(-2),decoded.Balances.Items[0].SourceUtc);Assert.Null(decoded.Balances.Items[0].Available);Assert.Null(decoded.Capital!.LiquidationEquity);
        Assert.Throws<InvalidDataException>(()=>OkxControllerSnapshotCodec.Encode(d with{Balances=d.Balances with{Items=new[]{d.Balances.Items[0] with{SourceUtc=now}}}}));
        var json=Data(OkxControllerSnapshotCodec.Encode(d));
        foreach(Action<JsonObject> mutate in new Action<JsonObject>[]{j=>j["Observation"]!.AsObject().Remove("ReceiveUtc"),j=>j["Balances"]!["Items"]![0]!.AsObject().Remove("Available"),j=>j["Catalog"]!["Items"]![0]!["Tick"]=0,j=>j["Evaluation"]!["Code"]="private/path",j=>j["Observation"]!["Origin"]="FRESH_BY_POLL",j=>j["Capital"]!["Free"]=10,j=>j["Balances"]!["Items"]![0]!["Available"]="0",j=>j["Catalog"]!["Total"]=257})
        {var bad=(JsonObject)json.DeepClone();mutate(bad);Assert.Throws<TargetInvocationException>(()=>Decode(Wire(bad)));}
    }
    [Theory]
    [InlineData("STOP")][InlineData("EOF")][InlineData("CANCEL")]
    public async Task Stop_EOF_and_cancellation_revoke_armed_owner_while_snapshot_output_is_blocked(string veto)
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f);
        var loop=OkxOperationalTradingService.ForTests(f.Service,(s,k,h,x)=>OkxExecutionClient.ForTests(peer,f.Clock,s,k,h,x),peer,f.Clock);
        var request=f.Request with{Purpose=OkxActivationPurpose.Execute,Session=f.Service.TradingSessionId};var lease=await f.Runtime.Activation.RequestAsync(request,default);Assert.True(loop.Arm(request,lease!));
        using var input=new Input();using var output=new Output{BlockSnapshot=true};using var cancel=new CancellationTokenSource();var run=ConnectedHost(f.Root,f.Service,loop,input,output,cancel.Token);
        try
        {
            await output.Expect("STOP_DISCONNECTED_UNARMED");int reads=f.Paths.Count,opens=f.SecretOpens;input.Send("SNAPSHOT");await output.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if(veto=="EOF")input.End();else if(veto=="CANCEL")cancel.Cancel();else input.Send("STOP");
            for(int n=0;n<500&&loop.ReadStatus().Armed;n++)await Task.Delay(10);
            Assert.False(loop.ReadStatus().Armed);Assert.Equal("STOP",loop.ReadStatus().Mode);Assert.False(run.IsCompleted);Assert.Equal(reads,f.Paths.Count);Assert.Equal(opens,f.SecretOpens);Assert.Equal(0,peer.Posts);
            output.Release.Set();if(veto=="STOP"){await output.Expect("STOP_DRAINED_UNARMED");input.End();}
            Assert.Equal(0,await run.WaitAsync(TimeSpan.FromSeconds(10)));Assert.Contains("HOST_DRAINED_CREDENTIALS_CLOSED",output.ToString());
        }
        finally{output.Release.Set();input.End();await run.WaitAsync(TimeSpan.FromSeconds(10));}
    }
    [Theory]
    [InlineData("snapshot")][InlineData("SNAPSHOT ")][InlineData("SNAPSHOT ../private")][InlineData("SNAPSHOT CONNECT")]
    public async Task Only_exact_SNAPSHOT_is_admitted_without_opening_any_root(string command)
    {
        using var input=new Input();using var output=new Output();var root=Path.Combine(Environment.GetEnvironmentVariable("TMPDIR")!,"snapshot-unused-"+Guid.NewGuid().ToString("N"));
        var run=ConnectedHost(root,null!,null!,input,output);
        try{await output.Expect("STOP_DISCONNECTED_UNARMED");input.Send(command);await output.Expect("COMMAND_REFUSED");input.End();Assert.Equal(0,await run.WaitAsync(TimeSpan.FromSeconds(10)));Assert.False(Directory.Exists(root));Assert.DoesNotContain("CONTROLLER_SNAPSHOT ",output.ToString());}
        finally{input.End();await run.WaitAsync(TimeSpan.FromSeconds(10));}
    }
    private static Task<int> ConnectedHost(string root,OkxAccountConnectionService account,OkxOperationalTradingService loop,TextReader input,TextWriter output,CancellationToken cancellation=default)
    {
        var type=typeof(LinuxDemoHost);var constructor=type.GetConstructors(BindingFlags.Instance|BindingFlags.NonPublic).Single();
        Func<CancellationToken,ValueTask<string?>> read=input.ReadLineAsync;Func<EphemeralCredentials,CancellationToken,Task<OkxEnrollmentDiscovery>> discover=(_,_)=>throw new Exception("SNAPSHOT_DISCOVERY_FORBIDDEN");
        var host=constructor.Invoke(new object?[]{root,null,read,output,discover,null,(Func<bool>)(()=>true),cancellation});
        type.GetField("account",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(host,account);type.GetField("loop",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(host,loop);
        return (Task<int>)type.GetMethod("RunCoreAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(host,null)!;
    }
    private sealed class Input:TextReader
    {
        private readonly Channel<string> lines=Channel.CreateUnbounded<string>();
        internal void Send(string line)=>lines.Writer.TryWrite(line);internal void End()=>lines.Writer.TryComplete();
        public override async ValueTask<string?> ReadLineAsync(CancellationToken ct)=>await lines.Reader.WaitToReadAsync(ct)?await lines.Reader.ReadAsync(ct):null;
    }
    private sealed class Output:StringWriter
    {
        private readonly Channel<string> lines=Channel.CreateUnbounded<string>();
        internal bool BlockSnapshot;internal readonly TaskCompletionSource Blocked=new(TaskCreationOptions.RunContinuationsAsynchronously);internal readonly ManualResetEventSlim Release=new(false);
        public override void WriteLine(string? value){if(BlockSnapshot&&value?.StartsWith("CONTROLLER_SNAPSHOT ",StringComparison.Ordinal)==true){Blocked.TrySetResult();if(!Release.Wait(TimeSpan.FromSeconds(15)))throw new IOException("SYNTHETIC_OUTPUT_TIMEOUT");}base.WriteLine(value);lines.Writer.TryWrite(value??"");}
        internal async Task<string> Expect(string prefix)
        {using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(10));while(await lines.Reader.WaitToReadAsync(ct.Token)){var line=await lines.Reader.ReadAsync(ct.Token);if(line.StartsWith(prefix,StringComparison.Ordinal))return line;if(line=="COMMAND_REFUSED")throw new Xunit.Sdk.XunitException("SNAPSHOT command refused by production host");}throw new Xunit.Sdk.XunitException("Host output ended");}
        internal Task<string> Snapshot()=>Expect("CONTROLLER_SNAPSHOT ");
    }
}
// The historical finite peer advances its clock. Run that fixture last rather
// than reset/bypass production process-wide WebSocket quotas or spawn a child.
public sealed class ControllerSnapshotOrderer:Xunit.Sdk.ITestCaseOrderer
{
    public System.Collections.Generic.IEnumerable<T> OrderTestCases<T>(System.Collections.Generic.IEnumerable<T> cases) where T:Xunit.Abstractions.ITestCase
        =>cases.OrderBy(c=>c.TestMethod.Method.Name.StartsWith("Connected_",StringComparison.Ordinal)?1:0).ThenBy(c=>c.DisplayName,StringComparer.Ordinal);
}
