using System;
using System.IO;
using System.Linq;
using System.Net.Http;
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

public sealed class OkxDispatchRecoveryTests
{
    // The real response bytes were not retained for this worker. Blank subCode is
    // a demonstrated causal candidate, not an assertion about the private response.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Successful_ACK_with_optional_blank_subCode_persists_outcome_and_durable_STOP(bool blank)
    {
        await using var f=new OperationalPeer(finite:true);using var peer=new FiniteDemoPeer(f);f.Mutate=peer.Observe;
        await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        using var wire=new AckShape(peer,blank);
        await using var loop=OkxOperationalTradingService.Create(f.Service,f.Clock,(s,k,h,x)=>OkxExecutionClient.ForTests(wire,f.Clock,s,k,h,x),peer);
        loop.BindFiniteSafety(()=>true);Assert.True(loop.ArmFiniteDemo());Assert.Equal(NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);
        Assert.Equal(NavalMutationStatus.Applied,loop.PlanFiniteDemo("BTC-USDC",Guid.NewGuid().ToString("N")).Status);Assert.Equal(NavalMutationStatus.Applied,loop.Start().Status);
        var result=await loop.SubmitFiniteDemoAsync(true,default);
        var before=JsonSerializer.Deserialize<EngineState>(File.ReadAllText(f.Target))!;
        loop.Stop();var after=JsonSerializer.Deserialize<EngineState>(File.ReadAllText(f.Target))!;
        Assert.True(result.Status==NavalMutationStatus.Applied,"outcome="+result.Code+"; persisted="+before.NavalBroker!.Operational!.Intents.Single().Stage+"; stop="+after.Naval!.Mode);
        Assert.Equal(NavalExecutionStage.Accepted,Assert.Single(before.NavalBroker!.Operational!.Intents).Stage);
        Assert.Equal("STOP",after.Naval!.Mode);Assert.Single(peer.Sends);Assert.Equal(before.Naval.Reservations.Single().Pending,after.Naval.Reservations.Single().Pending);
    }
    [Theory]
    [InlineData("run")]
    [InlineData("unknown")]
    [InlineData("cancel-unknown")]
    [InlineData("observe")]
    [InlineData("already-terminal")]
    [InlineData("wrong-id")]
    [InlineData("lock")]
    [InlineData("corrupt")]
    [InlineData("not-found")]
    [InlineData("partial")]
    [InlineData("filled")]
    public async Task Existing_Dispatching_recovery_uses_same_ID_cancel_only_and_keeps_original_report(string action)
    {
        var psi=new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")??"/home/ubuntu/.dotnet/dotnet"){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false};
        psi.ArgumentList.Add(typeof(OkxDispatchRecoveryTests).Assembly.Location);psi.ArgumentList.Add("--okx-dispatch-recovery-native");psi.ArgumentList.Add(action);
        psi.Environment.Clear();foreach(string n in new[]{"HOME","PATH","DOTNET_ROOT","LANG","TMPDIR"})if(Environment.GetEnvironmentVariable(n) is {} v)psi.Environment[n]=v;
        psi.Environment["DOTNET_EnableDiagnostics"]="0";psi.Environment["COMPlus_EnableDiagnostics"]="0";
        using var process=System.Diagnostics.Process.Start(psi)!;var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();await process.WaitForExitAsync();Assert.True(process.ExitCode==0,(await stdout)+(await stderr));
    }
    internal static async Task<int> Native(string action)
    {
        string root=Path.Combine("/home/ubuntu/apex-deliveries/current-market-funding-synthetic","recovery-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root,(UnixFileMode)448);
        var binding=OkxConfiguration.Bind(new OkxNavalOptions{Schema="okx-naval-config-v1",Scopes=new[]{new OkxScopeOptions{ScopeId="finite-demo",Environment="OkxDemo",Region="EEA",ExpectedUid="99123456789",ExpectedMainUid="99123456789",QuoteCurrency="USDC",CredentialBindingId="foreground-demo",SafetyVersion="okx-cash-v1"}}})[0];
        await using var f=new OperationalPeer(root,binding,preserve:true,finite:true);using var peer=new FiniteDemoPeer(f){BaseCurrency="ADA",OwnFreezes=true};f.Mutate=(p,b)=>OkxCurrentMarketFundingTests.Shape(f,peer,p,b);
        var vault=new Apex.Okx.DemoAcceptance.LinuxDemoCredentialVault(Path.Combine(root,"vault"));await vault.SaveNewAsync(OperationalPeer.Key,OperationalPeer.Secret,OperationalPeer.Pass);
        f.Runtime.FiniteFundingPremise=((NavalFiniteOperatorPremise)OkxCurrentMarketFundingTests.Premise(f)) with{CredentialRecordDigest=await vault.RecordIdentityAsync()};
        f.BaselineOverride=new Apex.Okx.DemoAcceptance.SignedFiniteBaseline(f.Binding,f.Request.CredentialFingerprint,()=>f.Service);
        await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        await using var loop=OkxOperationalTradingService.Create(f.Service,f.Clock,(s,k,h,x)=>OkxExecutionClient.ForTests(peer,f.Clock,s,k,h,x),peer);loop.BindFiniteSafety(()=>true);
        Assert.True(loop.ArmFiniteDemo());Assert.Equal(NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);Assert.Equal(NavalMutationStatus.Applied,loop.PlanFiniteDemo(peer.Instrument,Guid.NewGuid().ToString("N")).Status);Assert.Equal(NavalMutationStatus.Applied,loop.Start().Status);
        f.WriteBoundary=b=>{if(peer.Sends.Count>0&&b.ToString()=="BeforeTempCreate")throw new IOException("synthetic-outcome-write-fault");};
        var result=await loop.SubmitFiniteDemoAsync(true,default);f.WriteBoundary=null;Assert.Equal("STORAGE_FROZEN",result.Code);loop.Stop();
        var original=JsonSerializer.Deserialize<EngineState>(File.ReadAllText(f.Target))!;Assert.Equal("RUNNING",original.Naval!.Mode);Assert.Equal(6,original.NavalBroker!.Baseline.Observations.Balances.Count);Assert.Equal(NavalExecutionStage.Dispatching,Assert.Single(original.NavalBroker!.Operational!.Intents).Stage);
        string client=peer.PendingClient,baseline=NavalJson.Serialize(original.NavalBroker.Baseline);await loop.DisposeAsync();await f.Service.DisconnectAsync();
        foreach(string dir in Directory.EnumerateDirectories(root,"*",SearchOption.AllDirectories))File.SetUnixFileMode(dir,(UnixFileMode)448);
        foreach(string file in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories))File.SetUnixFileMode(file,(UnixFileMode)384);
        File.WriteAllText(Path.Combine(root,"last-report.json"),"ORIGINAL_PRIVATE_REPORT");File.SetUnixFileMode(Path.Combine(root,"last-report.json"),(UnixFileMode)384);
        f.Clock.Advance(TimeSpan.FromMinutes(11),true,true);f.Paths.Clear();int readFactories=0;bool lockHeld=false;
        if(action=="already-terminal")peer.Canceled=true;if(action=="cancel-unknown")peer.UnknownCancel=true;if(action=="partial")peer.FillPending=true;
        if(action=="filled"){peer.Canceled=true;peer.FillPending=true;peer.BuyQuantity=original.Naval.Reservations.Single().Quantity;peer.BuyFee=peer.BuyQuantity*peer.FeeRate;peer.BuyPrice=original.Naval.Reservations.Single().Intent.EntryPriceCeiling;}
        var originalMutate=f.Mutate!;f.Mutate=(p,b)=>{originalMutate(p,b);if(p=="/api/v5/trade/order"&&action=="unknown")b["data"]=new JsonArray();if(p=="/api/v5/trade/order"&&action=="not-found"){b["code"]="51400";b["data"]=new JsonArray();}if(p=="/api/v5/trade/order"&&action=="filled")b["data"]![0]!["state"]="filled";if(p=="/api/v5/trade/order"&&action=="wrong-id"&&b["data"]!.AsArray().Count>0)b["data"]![0]!["clOrdId"]="FOREIGN999";};
        var composition=new Apex.Okx.DemoAcceptance.FiniteEntryTestComposition(f.Clock,(s,k,h,x)=>{readFactories++;Assert.Equal("STOP",JsonSerializer.Deserialize<EngineState>(File.ReadAllText(f.Target))!.Naval!.Mode);try{using var attempted=new FileStream(Path.Combine(root,".run.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None);}catch(IOException){lockHeld=true;}Assert.True(lockHeld);return OkxExecutionObservationClient.ForTests(f,f.Clock,s,k,h,x);},(s,k,h,x)=>throw new InvalidOperationException("RECOVERY_NO_STREAM_REQUIRED"),(s,k,h,x)=>OkxExecutionClient.ForTests(peer,f.Clock,s,k,h,x),peer);
        int CallExpected()=>action is "unknown" or "wrong-id" or "observe" or "not-found"?5:action=="lock"?9:action=="corrupt"?7:0;
        if(action=="corrupt")File.WriteAllText(f.Target,"{}");string beforeRecovery=File.ReadAllText(f.Target);
        var deniedCrypto=new NoDecrypt();var chosenVault=action is "lock" or "corrupt"?new Apex.Okx.DemoAcceptance.LinuxDemoCredentialVault(Path.Combine(root,"vault"),null,deniedCrypto):vault;
        using var held=action=="lock"?new FileStream(Path.Combine(root,".run.lock"),new FileStreamOptions{Mode=FileMode.OpenOrCreate,Access=FileAccess.ReadWrite,Share=FileShare.None,UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite}):null;
        int code=await Apex.Okx.DemoAcceptance.Program.RunFiniteForTestsAsync(new[]{action=="observe"?"--recover-observe":"--recover-stop"},root,chosenVault,(_,_)=>throw new InvalidOperationException("RECOVERY_NO_DISCOVERY"),composition);
        Assert.True(code==CallExpected(),"recovery exit="+code+" scenario="+action);
        if(action is "lock" or "corrupt"){Assert.Equal(0,deniedCrypto.Calls);Assert.Equal(0,readFactories);Assert.Equal(beforeRecovery,File.ReadAllText(f.Target));Console.WriteLine("SYNTHETIC_RECOVERY_PREDECRYPT_REFUSAL "+action+"; root="+root);return 0;}
        var recovered=JsonSerializer.Deserialize<EngineState>(File.ReadAllText(f.Target))!;Assert.Equal("STOP",recovered.Naval!.Mode);Assert.Equal(client,recovered.NavalBroker!.Operational!.Intents[0].ClientId);Assert.Equal(baseline,NavalJson.Serialize(recovered.NavalBroker.Baseline));Assert.Equal(original.NavalBroker.Namespace,recovered.NavalBroker.Namespace);Assert.Equal(original.NavalBroker.HighWater,recovered.NavalBroker.HighWater);Assert.Equal(NavalJson.Serialize(original.NavalBroker.Operational!.LastCurrentMarketFunding),NavalJson.Serialize(recovered.NavalBroker.Operational!.LastCurrentMarketFunding));
        Assert.Equal(action is "run" or "cancel-unknown" or "partial"?new[]{"/api/v5/trade/order","/api/v5/trade/cancel-order"}:new[]{"/api/v5/trade/order"},peer.Sends);Assert.Equal("ORIGINAL_PRIVATE_REPORT",File.ReadAllText(Path.Combine(root,"last-report.json")));Assert.Contains(f.Paths,p=>p.Contains("clOrdId="+client,StringComparison.Ordinal));
        if(action is "partial" or "filled"){Assert.Single(recovered.Naval.Fills);Assert.True(recovered.Naval.Reservations.Single().Inventory>0);Assert.Contains(recovered.NavalBroker.Operational.Obligations,o=>o.StartsWith("PROTECTION_GAP:",StringComparison.Ordinal));}
        else Assert.Equal(original.Naval.Reservations.Single().Pending,recovered.Naval.Reservations.Single().Pending);
        Assert.False(recovered.Naval.Reservations.Single().TerminalCoverage);Assert.True(recovered.Naval.Reservations.Single().EntryHold>0||recovered.Naval.Reservations.Single().Inventory>0);
        string reportPath=Directory.EnumerateFiles(Path.Combine(root,"recovery-reports"),"*.json").Single();byte[] reportBytes=File.ReadAllBytes(reportPath);using var report=JsonDocument.Parse(reportBytes);Assert.Equal("STOP",report.RootElement.GetProperty("mode").GetString());Assert.False(report.RootElement.GetProperty("armed").GetBoolean());Assert.False(report.RootElement.GetProperty("schedulerStarted").GetBoolean());
        using(var privateOwner=new Apex.Okx.DemoAcceptance.EphemeralCredentials(OperationalPeer.Key.ToArray(),OperationalPeer.Secret.ToArray(),OperationalPeer.Pass.ToArray()))Assert.False(privateOwner.ContainsSecret(reportBytes));
        if(action is "run" or "already-terminal" or "cancel-unknown")Assert.Contains(recovered.NavalBroker.Operational.Orders,o=>o.ClientId==client&&o.State=="canceled");
        int paths=f.Paths.Count;int refused=await Apex.Okx.DemoAcceptance.Program.RunFiniteForTestsAsync(Array.Empty<string>(),root,new Apex.Okx.DemoAcceptance.LinuxDemoCredentialVault(Path.Combine(root,"vault"),null,deniedCrypto),(_,_)=>throw new InvalidOperationException("DEFAULT_MUST_REFUSE"),composition);Assert.Equal(3,refused);Assert.Equal(0,deniedCrypto.Calls);Assert.Equal(paths,f.Paths.Count);
        File.WriteAllText(Path.Combine(root,"synthetic-summary.json"),JsonSerializer.Serialize(new{scenario=action,code,mode=recovered.Naval.Mode,sends=peer.Sends.Count,observedTerminal=recovered.NavalBroker.Operational.Orders.Any(o=>o.State=="canceled"),held=recovered.Naval.Reservations.Single().EntryHold>0,originalReportPreserved=true,lockHeld,baselinePreserved=true,rawNullablePreserved=true,privateAccess=false}));
        Console.WriteLine("SYNTHETIC_EXISTING_DISPATCHING_RECOVERY_PASS "+action+"; root="+root);return 0;
    }
    private sealed class NoDecrypt:Apex.Okx.DemoAcceptance.ICredentialCryptor
    {internal int Calls;public Task<Apex.Okx.DemoAcceptance.SensitiveBuffer> TransformAsync(bool encrypt,ReadOnlyMemory<byte> input,CancellationToken ct){Calls++;throw new InvalidOperationException("SYNTHETIC_DECRYPT_MUST_NOT_RUN");}}
    private sealed class AckShape(HttpMessageHandler peer,bool blank):HttpMessageHandler
    {
        private readonly HttpMessageInvoker inner=new(peer,false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var response=await inner.SendAsync(request,ct);var body=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
            if(blank)body["data"]![0]!["subCode"]="";
            response.Content.Dispose();response.Content=new StringContent(body.ToJsonString());return response;
        }
    }
}
