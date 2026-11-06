using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Apex.Desktop.Services;
using Xunit;
namespace Apex.Tests;

// Test surface replacing ONLY the native dialogs/CredUI. Lives in the test assembly.
internal sealed class SyntheticLocalUi(string[]? lines,bool approve=true):IWindowsLocalEnrollmentUi
{
    internal static string[] Exact=>NavalCurrentMarketFunding.Statement.Split('\n').Append(WindowsOkxLocalEnrollment.ConfirmationWord).ToArray();
    internal readonly List<WindowsLocalOwnerOffer> Offers=new();
    internal int Keys,Approvals;internal string? ApprovalText;
    public async Task<OkxNativeForm> KeysAsync(Func<byte[][],CancellationToken,Task<string?>> submit,CancellationToken ct)
    {
        Keys++;var copy=new[]{OperationalPeer.Key.ToArray(),OperationalPeer.Secret.ToArray(),OperationalPeer.Pass.ToArray()};
        return await submit(copy,ct)==null?new(copy,"ACCEPTED"):new(null,"CANCELLED");
    }
    public Task<string[]?> ConfirmAsync(WindowsLocalOwnerOffer offer,CancellationToken ct){Offers.Add(offer);return Task.FromResult(lines);}
    public Task<OkxWindowsApproval> ProtectedApprovalAsync(string message,CancellationToken ct){Approvals++;ApprovalText=message;return Task.FromResult(new OkxWindowsApproval(approve,approve?"APPROVAL_TEST_ADAPTER":"APPROVAL_USER_CANCELLED"));}
}

public sealed class OkxWindowsLocalProvenanceTests
{
    private static NavalFiniteOperatorPremise P(string provenance,DateTimeOffset now,string? statement=null,string env="OkxDemo",string? digest=null,int minutes=20)=>
        new("finite-operator-premise-v1","current-market-funding-v1",NavalCurrentMarketFunding.TestId,digest??new string('a',64),statement??NavalCurrentMarketFunding.Statement,provenance,now.AddMinutes(-1),now.AddMinutes(minutes),env);
    [Fact]
    [Trait("Gate","WindowsLocalExecution")]
    public void Premise_accepts_exactly_parent_and_windows_local_provenance_with_all_other_checks_unchanged()
    {
        var now=DateTimeOffset.UtcNow;
        Assert.True(NavalCurrentMarketFunding.Premise(P("parent-confirmed-user",now),now));
        Assert.True(NavalCurrentMarketFunding.Premise(P("windows-local-user-confirmed",now),now));
        foreach(string bad in new[]{"","windows-local-user","Windows-local-user-confirmed","WINDOWS-LOCAL-USER-CONFIRMED"," windows-local-user-confirmed","windows-local-user-confirmed ","windows-local-user-confirmed\n","\twindows-local-user-confirmed","windows local user confirmed","windows-local-user-confirmed2","Parent-confirmed-user","parent-confirmed-user ","PARENT-CONFIRMED-USER","linux-local-user-confirmed","user-confirmed"})
            Assert.False(NavalCurrentMarketFunding.Premise(P(bad,now),now),bad);
        Assert.False(NavalCurrentMarketFunding.Premise(P(null!,now),now));
        foreach(string provenance in new[]{"parent-confirmed-user","windows-local-user-confirmed"})
        {
            Assert.False(NavalCurrentMarketFunding.Premise(P(provenance,now,statement:NavalCurrentMarketFunding.Statement.Replace("\n"," ")),now));
            Assert.False(NavalCurrentMarketFunding.Premise(P(provenance,now,env:"Live"),now));
            Assert.False(NavalCurrentMarketFunding.Premise(P(provenance,now,digest:new string('A',64)),now));
            Assert.False(NavalCurrentMarketFunding.Premise(P(provenance,now,minutes:30),now)); // window > 30 min (issued -1 min)
            Assert.False(NavalCurrentMarketFunding.Premise(P(provenance,now),now.AddMinutes(21))); // expired
            Assert.False(NavalCurrentMarketFunding.Premise(P(provenance,now) with{Schema="finite-operator-premise-v2"},now));
            Assert.False(NavalCurrentMarketFunding.Premise(P(provenance,now) with{IssuedUtc=now.AddMinutes(-1).ToOffset(TimeSpan.FromHours(1))},now));
        }
    }
    [Fact]
    [Trait("Gate","WindowsLocalExecution")]
    public void Only_the_native_Windows_enrollment_references_the_windows_local_provenance()
    {
        var dir=new DirectoryInfo(AppContext.BaseDirectory);while(dir!=null&&!Directory.Exists(Path.Combine(dir.FullName,"src","Apex.Core")))dir=dir.Parent;
        Assert.NotNull(dir);
        string Rel(string p)=>Path.GetRelativePath(dir!.FullName,p).Replace('\\','/');
        var files=new[]{"src","tools"}.SelectMany(d=>Directory.EnumerateFiles(Path.Combine(dir!.FullName,d),"*",SearchOption.AllDirectories))
            .Where(p=>!p.Contains(Path.DirectorySeparatorChar+"bin"+Path.DirectorySeparatorChar)&&!p.Contains(Path.DirectorySeparatorChar+"obj"+Path.DirectorySeparatorChar)&&!p.Contains("node_modules")&&(p.EndsWith(".cs")||p.EndsWith(".js")||p.EndsWith(".json")||p.EndsWith(".ps1")||p.EndsWith(".py")))
            .ToArray();
        var literal=files.Where(p=>File.ReadAllText(p).Contains("windows-local-user-confirmed",StringComparison.Ordinal)).Select(Rel).ToArray();
        Assert.Equal(new[]{"src/Apex.Core/Naval/CurrentMarketFundingEvidenceV1.cs"},literal);
        var constant=files.Where(p=>File.ReadAllText(p).Contains("WindowsLocalProvenance",StringComparison.Ordinal)).Select(Rel).OrderBy(p=>p,StringComparer.Ordinal).ToArray();
        // Core only checks it (premise validation, fresh-local admission); only the native enrollment produces it.
        Assert.Equal(new[]{"src/Apex.Core/Naval/CurrentMarketFundingEvidenceV1.cs","src/Apex.Core/Naval/NavalOrdinaryDemoPolicy.cs","src/Apex.Desktop/Okx/WindowsOkxLocalEnrollment.cs"},constant);
        var producers=files.Where(p=>File.ReadAllText(p).Contains("new NavalFiniteOperatorPremise(",StringComparison.Ordinal)).Select(Rel).ToArray();
        Assert.Equal(new[]{"src/Apex.Desktop/Okx/WindowsOkxLocalEnrollment.cs"},producers);
        string policy=File.ReadAllText(Path.Combine(dir!.FullName,"src/Apex.Core/Naval/NavalOrdinaryDemoPolicy.cs"));
        Assert.Equal(1,policy.Split("WindowsLocalProvenance").Length-1);Assert.Contains("Provenance==NavalCurrentMarketFunding.WindowsLocalProvenance",policy);
    }
    [Fact]
    [Trait("Gate","WindowsLocalExecution")]
    public async Task Http_and_controller_wire_cannot_carry_provenance_premise_or_confirmation()
    {
        Assert.True(OkxControlEndpoints.IsPath("/api/naval/okx/create-local"));
        Assert.DoesNotContain(OkxControllerProtocol.Actions,a=>a.Contains("CREATE",StringComparison.OrdinalIgnoreCase)||a.Contains("PREMISE",StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(OkxRemoteControllerEndpoints.Actions,a=>a.Contains("create",StringComparison.OrdinalIgnoreCase));
        string id=Guid.NewGuid().ToString("N");
        Assert.Throws<InvalidDataException>(()=>OkxControllerProtocol.DecodeCommand(JsonSerializer.Serialize(new{Schema=OkxControllerProtocol.Version,Epoch=id,Session=id,CommandId=id,Action="CREATE-LOCAL"})));
        Assert.Throws<InvalidDataException>(()=>OkxControllerProtocol.DecodeCommand(JsonSerializer.Serialize(new{Schema=OkxControllerProtocol.Version,Epoch=id,Session=id,CommandId=id,Action="PREPARE",Provenance="windows-local-user-confirmed"})));
        await using var h=new NavalProductionHost();await h.Start();
        foreach(string body in new[]{"{\"commandId\":\"x\",\"provenance\":\"windows-local-user-confirmed\"}","{\"commandId\":\"x\",\"premise\":{\"Provenance\":\"windows-local-user-confirmed\"}}","{\"commandId\":\"x\",\"confirmation\":\"TWORZĘ LOKALNEGO BOTA\"}","{\"provenance\":\"windows-local-user-confirmed\"}"})
            foreach(string path in new[]{"/api/naval/okx/create-local","/api/naval/okx/configure","/api/naval/okx/local-demo/prepare"})
            {
                using var request=h.Request(path,"operator","POST");request.Headers.Add("X-Apex-Local","1");
                request.Content=new System.Net.Http.StringContent(body,Encoding.UTF8,"application/json");
                Assert.Equal(System.Net.HttpStatusCode.BadRequest,(await h.Client.SendAsync(request)).StatusCode);
            }
        Assert.False(Directory.Exists(Path.Combine(h.DirectoryPath,"okx")),"no store may appear from a wire request");
    }
}

public sealed class OkxLinuxCliProvenanceTests
{
    private static string Json(string provenance,string digest,DateTimeOffset now)=>JsonSerializer.Serialize(new NavalFiniteOperatorPremise("finite-operator-premise-v1","current-market-funding-v1",NavalCurrentMarketFunding.TestId,digest,NavalCurrentMarketFunding.Statement,provenance,now.AddMinutes(-1),now.AddMinutes(20),"OkxDemo"));
    [Fact]
    public async Task Linux_cli_receipt_cannot_provision_or_load_windows_local_provenance()
    {
        if(!OperatingSystem.IsLinux())return; // Linux CLI only; the source scan above covers all OS.
        string root=Path.Combine(Environment.GetEnvironmentVariable("TMPDIR")??Path.GetTempPath(),"cli-provenance-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        try
        {
            var vault=new Apex.Okx.DemoAcceptance.LinuxDemoCredentialVault(Path.Combine(root,"vault"));var now=DateTimeOffset.UtcNow;
            async Task<Exception?> Provision(string provenance)
            {try{await Apex.Okx.DemoAcceptance.FiniteOperatorReceipt.ProvisionAsync(root,vault,new MemoryStream(Encoding.UTF8.GetBytes(Json(provenance,"PARENT_BIND_CURRENT_ENCRYPTED_RECORD",now))),default);return null;}catch(Exception e){return e;}}
            var refused=Assert.IsType<OkxFiniteAccountRefusal>(await Provision("windows-local-user-confirmed"));
            Assert.Equal("FINITE_OPERATOR_PREMISE_INPUT_REFUSED",refused.Code);
            Assert.False(File.Exists(Path.Combine(root,Apex.Okx.DemoAcceptance.FiniteOperatorReceipt.Name)));
            // Control: the parent value passes this guard and only then reaches the (absent) vault.
            Assert.IsNotType<OkxFiniteAccountRefusal>(await Provision("parent-confirmed-user"));
            Assert.False(File.Exists(Path.Combine(root,Apex.Okx.DemoAcceptance.FiniteOperatorReceipt.Name)));
            string file=Path.Combine(root,Apex.Okx.DemoAcceptance.FiniteOperatorReceipt.Name);
            async Task<Exception?> Load(string provenance)
            {
                File.WriteAllText(file,Json(provenance,new string('a',64),now));File.SetUnixFileMode(file,UnixFileMode.UserRead|UnixFileMode.UserWrite);
                try{await Apex.Okx.DemoAcceptance.FiniteOperatorReceipt.LoadAsync(root,vault,default);return null;}catch(Exception e){return e;}finally{File.Delete(file);}
            }
            var loadRefused=Assert.IsType<OkxFiniteAccountRefusal>(await Load("windows-local-user-confirmed"));
            Assert.Equal("FINITE_OPERATOR_PREMISE_INVALID_OR_WRONG_RECORD",loadRefused.Code);
            Assert.IsNotType<OkxFiniteAccountRefusal>(await Load("parent-confirmed-user"));
        }
        finally{Directory.Delete(root,true);}
    }
}

public sealed partial class OkxConditionalTradingTests
{
    private static JsonElement Status(OkxOperatorControl c)=>JsonSerializer.SerializeToElement(c.ReadStatus());
    private static string PowerRequests()
    {
        using var p=Process.Start(new ProcessStartInfo("powercfg","/requests"){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false})!;
        string o=p.StandardOutput.ReadToEnd();p.WaitForExit();Assert.True(p.ExitCode==0,"powercfg /requests failed: "+p.StandardError.ReadToEnd());
        int s=o.IndexOf("SYSTEM:",StringComparison.Ordinal),a=o.IndexOf("AWAYMODE:",StringComparison.Ordinal);Assert.True(s>=0&&a>s,o);
        string section=o[s..a];string? evidence=Environment.GetEnvironmentVariable("RUNNER_TEMP");
        if(evidence!=null)File.AppendAllText(Path.Combine(evidence,"controller-windows-source","power-requests.txt"),DateTimeOffset.UtcNow.ToString("O")+"\n"+o+"\n----\n");
        return section;
    }
    [Fact]
    [Trait("Gate","WindowsLocalExecution")]
    public async Task Windows_local_first_run_creates_owner_from_multi_currency_DEMO_then_full_cycle_keep_awake_STOP_reopen()
    {
        Assert.True(OperatingSystem.IsWindows(),"Native Windows gate must not silently skip");
        await using var fixture=await MultiFixture.Create(windowsLocalCreate:true);
        var f=fixture.F;
        fixture.Runtime=OkxNavalRuntime.Compose(f.Root,Array.Empty<OkxScopeBinding>(),f,f);
        var ui=new SyntheticLocalUi(SyntheticLocalUi.Exact);var config=new ConfigStore(f.Root);int loops=0;
        var control=new OkxOperatorControl(fixture.Runtime,config,account=>
        {
            loops++;return fixture.Loop=OkxOperationalTradingService.Create(account,f.Clock,(s,k,d,x)=>OkxExecutionClient.ForTests(fixture.Peer,f.Clock,s,k,d,x),fixture.Market);
        },fixture.LocalEnrollment(ui));
        async Task<string> Command(string action)=>await control.ExecuteAsync(f.Binding.ScopeId,action,Guid.NewGuid().ToString("N"),default);
        string image=Path.GetFileName(Environment.ProcessPath!);
        try
        {
            var first=Status(control);
            Assert.Equal("NOT_SELECTED",first.GetProperty("executionMode").GetString());Assert.True(first.GetProperty("localCreateAvailable").GetBoolean());Assert.False(first.GetProperty("keepAwake").GetBoolean());
            Assert.Equal("METADATA_ONLY_NO_KEY",WindowsOkxProvisioning.PassiveKeyState(f.Root,f.Binding));
            int requests=f.Methods.Count;
            Assert.Equal("LOCAL_OWNER_CREATED_STOP_ATTACH_NEXT",await control.ExecuteAsync("","create-local","create",default));
            Assert.All(f.Methods.Skip(requests),m=>Assert.Equal(System.Net.Http.HttpMethod.Get,m));Assert.Equal(0,fixture.Peer.Posts); // no test trade
            var offer=Assert.Single(ui.Offers);Assert.Equal(1,ui.Approvals);Assert.Equal(1,ui.Keys);
            Assert.True(offer.EurCash>0);Assert.Contains("BTC",offer.ExcludedCurrencies);Assert.Contains("SOL",offer.ExcludedCurrencies);Assert.DoesNotContain("ETH",offer.ExcludedCurrencies);Assert.DoesNotContain("EUR",offer.ExcludedCurrencies);
            string text=offer.Text();
            foreach(string required in new[]{"NOWĄ lokalną historię","oddzielną od historii bota na VPS","bot na VPS MUSI pozostać zatrzymany (STOP)","Kapitał bota = całe saldo EUR na tym koncie DEMO","są wykluczone i nie będą sprzedawane","Brak automatycznego START"})Assert.Contains(required,text);
            Assert.Contains("Kapitał bota = całe saldo EUR",ui.ApprovalText);
            Assert.Equal(f.Binding,Assert.Single(fixture.Runtime.Bindings));
            Assert.Equal("CIPHERTEXT_PRESENT_NOT_VERIFIED",WindowsOkxProvisioning.PassiveKeyState(f.Root,f.Binding));
            Assert.True(WindowsOkxProvisioning.SavedBindingMatches(config,f.Binding));
            Apex.Core.Engine.EngineState created;
            using(var store=OkxNavalStore.OpenNative(f.Root,f.Binding))created=JsonSerializer.Deserialize<Apex.Core.Engine.EngineState>(store.Get(NavalBrokerValidation.StateKey)!,NavalJson.Options)!;
            var obs=created.NavalBroker!.Baseline.Observations;
            Assert.Equal("windows-local-user-confirmed",obs.OperatorPremise!.Provenance);Assert.NotNull(obs.CurrentMarketFunding);
            Assert.Equal("EUR",obs.FiniteAllocation!.Quote);
            Assert.Equal(obs.Balances.Where(b=>b.Currency!="EUR").Select(b=>b.Currency).OrderBy(c=>c,StringComparer.Ordinal),obs.FiniteAllocation.Excluded.Select(b=>b.Currency).OrderBy(c=>c,StringComparer.Ordinal));
            Assert.True(obs.FiniteAllocation.Excluded.Count>=2);
            Assert.Equal("ETH-EUR",Assert.Single(obs.Catalog).Instrument); // unheld finite-origin anchor
            decimal heldBtc=obs.FiniteAllocation.Excluded.Single(b=>b.Currency=="BTC").Cash!.Value,heldSol=obs.FiniteAllocation.Excluded.Single(b=>b.Currency=="SOL").Cash!.Value;
            Assert.True(heldBtc>0);Assert.Equal(decimal.Parse(MultiFixture.HeldSol,System.Globalization.CultureInfo.InvariantCulture),heldSol);
            Assert.Equal("STOP",created.Naval!.Mode);Assert.Empty(created.Naval.Fills);Assert.Equal(offer.EurCash,created.Naval.Accounts.Single().Cash);
            Assert.Empty(created.NavalBroker.Operational?.Intents??Array.Empty<NavalBrokerIntent>());
            var second=Status(control);
            Assert.Equal("LOCAL",second.GetProperty("executionMode").GetString());Assert.False(second.GetProperty("localCreateAvailable").GetBoolean());
            Assert.Equal("LOCAL_SCOPE_ALREADY_CONFIGURED_USE_ATTACH",await control.ExecuteAsync("","create-local","again",default));Assert.Equal(1,ui.Keys);
            fixture.EnterBenchmark();
            var approval=new WindowsOkxOrdinaryApprovalProvider(()=>fixture.Account);
            fixture.Account=OkxAccountConnectionService.ForTests(fixture.Runtime,f.Root,f.Binding,approval,f.Clock,
                (s,k,d,x)=>OkxExecutionObservationClient.ForTests(f,f.Clock,s,k,d,x,(t,c)=>{f.Clock.Advance(t);return Task.CompletedTask;}),
                (s,k,d,x)=>OkxObservationStream.ForTests((u,c)=>Task.FromResult<WebSocket>(new OperationalSocket(u)),f.Clock,s,k,d,x),()=>OkxNavalStore.OpenNative(f.Root,f.Binding));
            Assert.Equal("CONNECT_REQUIRED",await Command("start"));Assert.False(control.KeepAwakeActive);
            Assert.Equal("LOCAL_EXISTING_DEMO_ATTACHED_STOP",await Command("attach-local"));
            Assert.True(fixture.Account.OrdinaryDemoPolicy);
            Assert.Equal("CONNECTED_STOP_RECONCILIATION_REQUIRED",await Command("connect"));
            Assert.Equal("ORDINARY_DEMO_PREPARED",await Command("prepare"));
            Assert.Equal("ORDINARY_DEMO_ALREADY_PREPARED",await Command("prepare"));
            Assert.Equal(new[]{"BTC-EUR","ETH-EUR","SOL-EUR"},fixture.Loop.ReadStatus().Instruments.OrderBy(i=>i,StringComparer.Ordinal));
            var before=fixture.Account.BrokerSession!.ReadState();Assert.Empty(before.Naval!.Fills);
            Assert.NotEqual("RUNNING",await Command("start"));Assert.False(fixture.Loop.ReadStatus().Armed);Assert.False(control.KeepAwakeActive);
            fixture.Peer.BeforeResponse=()=>
            {
                var peer=fixture.Peer;Assert.InRange(peer.Size*110,0m,10m);peer.ExpectedSellSize=peer.Size*.999m;peer.ProtectionCanceled=false;
                int n=fixture.Cycles.Count;peer.EntryOrderId=(1777+n*10).ToString();peer.ExitOrderId=(1778+n*10).ToString();peer.GeneratedAlgoId=(1889+n*10).ToString();
                string instrument=fixture.Account.BrokerSession!.ReadState().NavalBroker!.Operational!.Intents.Last().Instrument;
                fixture.Cycles.Add(new(instrument,peer.Client,peer.Attachment,peer.EntryOrderId,peer.ExitOrderId,peer.GeneratedAlgoId,peer.Size,f.Clock.GetUtcNow()));
            };
            fixture.Peer.BeforeMutationResponse=kind=>{var c=fixture.Cycles.Last();if(kind=="cancel")c.Canceled=true;if(kind=="sell"){c.ExitClient=fixture.Peer.ExitClient;c.Sold=true;c.SellUtc=f.Clock.GetUtcNow();if(fixture.Cycles.Count(v=>v.Sold)==2)fixture.Market.Wait=true;}}; // exactly two cycles: flat candles after the 2nd exit
            Assert.Equal("EXECUTE_ARMED_START_SEPARATE_CONDITIONAL_RISK",await Command("arm"));
            fixture.Market.Wait=false;
            await Command("start");Assert.Equal("RUNNING",fixture.Loop.ReadStatus().Mode);
            Assert.True(control.KeepAwakeActive);Assert.Equal(WindowsLocalRunGuard.EsContinuous|WindowsLocalRunGuard.EsSystemRequired,control.KeepAwakeFlags);
            Assert.True(Status(control).GetProperty("keepAwake").GetBoolean());
            Assert.Contains(image,PowerRequests(),StringComparison.OrdinalIgnoreCase);
            for(int n=0;n<1200;n++)
            {
                var state=fixture.Account.BrokerSession!.ReadState();if(state.Naval!.Fills.Count==4&&fixture.Loop.ReadStatus().Mode=="RUNNING")break;
                Assert.True(n<1199,"Native scheduler stalled "+fixture.Loop.ReadStatus().Code+" last="+Status(control).GetProperty("lastResult").GetString()+" "+string.Join(",",fixture.Loop.ReadStatus().Obligations)+" posts="+fixture.Peer.Posts+" fills="+state.Naval.Fills.Count);await Task.Delay(25);
            }
            Assert.Equal("OPERATOR_STOP",await Command("stop"));
            for(int n=0;n<200&&control.KeepAwakeActive;n++)await Task.Delay(25);
            Assert.False(control.KeepAwakeActive);Assert.Equal(WindowsLocalRunGuard.EsContinuous,control.KeepAwakeFlags);
            Assert.DoesNotContain(image,PowerRequests(),StringComparison.OrdinalIgnoreCase);
            await control.DisposeAsync();
            Apex.Core.Engine.EngineState after;
            using(var store=OkxNavalStore.OpenNative(f.Root,f.Binding))after=JsonSerializer.Deserialize<Apex.Core.Engine.EngineState>(store.Get(NavalBrokerValidation.StateKey)!,NavalJson.Options)!;
            Assert.Equal("STOP",after.Naval!.Mode);
            Assert.Equal(new[]{"BTC-EUR","ETH-EUR"},fixture.Cycles.Select(c=>c.Instrument).OrderBy(i=>i,StringComparer.Ordinal));Assert.All(fixture.Cycles,c=>Assert.True(c.Sold&&c.Canceled));Assert.Equal(1,loops);
            Assert.Equal(NavalJson.Serialize(created.NavalBroker.Baseline),NavalJson.Serialize(after.NavalBroker!.Baseline));
            Assert.Equal(before.Naval.Accounts.Single().Cash+fixture.Cycles.Sum(c=>-c.Size*110+c.Size*.999m*c.SellPrice*.999m),after.Naval.Accounts.Single().Cash);
            Assert.All(after.Naval.Reservations.Where(r=>r.QuarantineMark==null),r=>Assert.True(r.Inventory==0&&r.Pending==0&&!r.ReductionUnsettled&&r.TerminalCoverage));
            Assert.Equal("windows-local-user-confirmed",after.NavalBroker.Baseline.Observations.OperatorPremise!.Provenance);
            // Excluded holdings: only own fills are ever sold or protected; pre-existing BTC/SOL never counted or consumed.
            var afterX=after.NavalBroker.Operational!;
            Assert.Equal(NavalOrdinaryDemoPolicy.FreshLocalId,afterX.OrdinaryDemo!.Policy);Assert.Null(afterX.OrdinaryDemo.Dust);Assert.Empty(afterX.OrdinaryDemo.HistoricalIntents);
            Assert.Equal(new[]{"BTC-EUR","ETH-EUR","SOL-EUR"},afterX.OrdinaryCatalog!.Instruments.Select(i=>i.Instrument).OrderBy(i=>i,StringComparer.Ordinal));
            Assert.DoesNotContain(after.Naval.Fills,a=>a.Fill.Instrument=="SOL-EUR");
            foreach(string instrument in new[]{"BTC-EUR","ETH-EUR"})
            {
                decimal bought=after.Naval.Fills.Where(a=>a.Fill.Instrument==instrument&&!a.Fill.Reduction).Sum(a=>a.Fill.Quantity),sold=after.Naval.Fills.Where(a=>a.Fill.Instrument==instrument&&a.Fill.Reduction).Sum(a=>a.Fill.Quantity);
                Assert.True(bought>0&&sold>0&&sold<=bought,instrument+" own sell exceeded own buy");
                Assert.All(afterX.Protection.Where(q=>q.Instrument==instrument&&q.Quantity!=null),q=>Assert.True(q.Quantity<=bought,instrument+" protection sized beyond own fills"));
            }
            Assert.True(after.Naval.Fills.Where(a=>a.Fill.Instrument=="BTC-EUR").Sum(a=>a.Fill.Quantity)<heldBtc,"fixture must keep own BTC far below held BTC");
            Assert.Equal(created.Naval.Accounts.Single().Cash,offer.EurCash);
            Assert.Equal(NavalJson.Serialize(obs.FiniteAllocation.Excluded),NavalJson.Serialize(after.NavalBroker.Baseline.Observations.FiniteAllocation!.Excluded));
            int paths=f.Paths.Count;
            await using var restarted=OkxNavalRuntime.Compose(f.Root,new[]{f.Binding},f,f);
            await using var reopened=new OkxOperatorControl(restarted,new ConfigStore(f.Root),null,fixture.LocalEnrollment(new SyntheticLocalUi(SyntheticLocalUi.Exact)));
            Assert.False(restarted.IsWindowsOrdinary(f.Binding));Assert.Equal("CONNECT_REQUIRED",await reopened.ExecuteAsync(f.Binding.ScopeId,"start","restart-start",default));Assert.Equal(paths,f.Paths.Count);
            Assert.False(reopened.KeepAwakeActive);Assert.Equal("LOCAL",Status(reopened).GetProperty("executionMode").GetString());
            Console.WriteLine("WINDOWS_LOCAL_CREATE_PASS nativeDPAPI=True provenance=windows-local-user-confirmed heldBTC="+heldBtc+" heldSOL="+heldSol+" excluded="+obs.FiniteAllocation.Excluded.Count+" noTestTrade=True BTC_ETH_cycles=2 keepAwakeSetCleared=True restartSTOP=True");
        }
        finally{await control.DisposeAsync();}
    }
    [Fact]
    [Trait("Gate","WindowsLocalExecution")]
    public async Task Windows_local_create_refuses_LIVE_mismatch_cancel_and_VPS_profile_without_saving_anything()
    {
        Assert.True(OperatingSystem.IsWindows(),"Native Windows gate must not silently skip");
        await using var fixture=await MultiFixture.Create(windowsLocalCreate:true);
        var f=fixture.F;var inner=f.Mutate;
        fixture.Runtime=OkxNavalRuntime.Compose(f.Root,Array.Empty<OkxScopeBinding>(),f,f);
        var config=new ConfigStore(f.Root);
        async Task<(string Code,SyntheticLocalUi Ui)> Create(SyntheticLocalUi ui)
        {
            await using var control=new OkxOperatorControl(fixture.Runtime,config,null,fixture.LocalEnrollment(ui));
            return (await control.ExecuteAsync("","create-local",Guid.NewGuid().ToString("N"),default),ui);
        }
        void Nothing()
        {
            Assert.Equal("METADATA_ONLY_NO_KEY",WindowsOkxProvisioning.PassiveKeyState(f.Root,f.Binding));Assert.Empty(fixture.Runtime.Bindings);
            Assert.Empty(OkxConfiguration.Bind(new ConfigStore(f.Root).Config.OkxNaval));Assert.False(Directory.Exists(OkxConfiguration.Paths(f.Root,f.Binding).Authority)&&File.Exists(Path.Combine(OkxConfiguration.Paths(f.Root,f.Binding).Authority,NavalBrokerValidation.Marker)));
            Assert.All(f.Methods,m=>Assert.Equal(System.Net.Http.HttpMethod.Get,m));
        }
        string remote=Path.Combine(f.Root,"controller-connection.json");File.WriteAllText(remote,"{}");
        var vps=await Create(new SyntheticLocalUi(SyntheticLocalUi.Exact));Assert.Equal("LOCAL_CREATE_REFUSED_VPS_CONTROLLER_PROFILE_PRESENT",vps.Code);Assert.Equal(0,vps.Ui.Keys);File.Delete(remote);Nothing();
        // Real OKX answer for a LIVE key on the DEMO endpoint (50101), through the real discovery client.
        f.Mutate=(p,b)=>{inner?.Invoke(p,b);if(p=="/api/v5/account/config"){b["code"]="50101";b["msg"]="APIKey does not match current environment.";b["data"]=new JsonArray();}};
        var live=await Create(new SyntheticLocalUi(SyntheticLocalUi.Exact));f.Mutate=inner;
        Assert.StartsWith("LOCAL_DEMO_KEYS_REFUSED_",live.Code);Assert.Empty(live.Ui.Offers);Assert.Equal(0,live.Ui.Approvals);Nothing();
        foreach(var lines in new[]{SyntheticLocalUi.Exact.Select((l,i)=>i==0?l.ToUpperInvariant():l).ToArray(),SyntheticLocalUi.Exact.Select((l,i)=>i==2?"TWORZE LOKALNEGO BOTA":l).ToArray(),SyntheticLocalUi.Exact.Take(2).ToArray(),null})
        {
            var mismatch=await Create(new SyntheticLocalUi(lines));Assert.Equal("LOCAL_CONFIRMATION_REFUSED_NOTHING_SAVED",mismatch.Code);Assert.Single(mismatch.Ui.Offers);Assert.Equal(0,mismatch.Ui.Approvals);Nothing();
        }
        var cancelled=await Create(new SyntheticLocalUi(SyntheticLocalUi.Exact,approve:false));Assert.Equal("APPROVAL_USER_CANCELLED",cancelled.Code);Assert.Equal(1,cancelled.Ui.Approvals);Nothing();
    }
}
