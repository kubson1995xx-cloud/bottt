using System;
using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Engine;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Apex.Okx.DemoAcceptance;
using Apex.ControllerPanel.Tests;
using Xunit;
namespace Apex.Tests;

public sealed partial class OkxConditionalTradingTests
{
    internal static string? PanelCapture;
    // Reuse the existing partial fixture's sizingOutput constructor.
    [Fact]
    public async Task Panel_explicit_CONNECT_closes_fresh_server_dead_end_and_preserves_mixed_owner()
    {
        Assert.Equal(typeof(PanelConnectEntry),GetType().Assembly.EntryPoint?.DeclaringType);
        var info=new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")??"dotnet"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
        info.ArgumentList.Add(GetType().Assembly.Location);info.ArgumentList.Add("panel-connect");info.ArgumentList.Add(Environment.GetEnvironmentVariable("CONTROLLER_PANEL_CONNECT_CAPTURE")??"");
        info.Environment.Clear();foreach(string key in new[]{"HOME","PATH","DOTNET_ROOT","LANG","TMPDIR"})if(Environment.GetEnvironmentVariable(key) is {} value)info.Environment[key]=value;
        info.Environment["DOTNET_EnableDiagnostics"]="0";info.Environment["COMPlus_EnableDiagnostics"]="0";
        using var child=System.Diagnostics.Process.Start(info)!;
        var stdout=child.StandardOutput.ReadToEndAsync();var stderr=child.StandardError.ReadToEndAsync();
        try{await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(100));string text=await stdout+await stderr;sizingOutput.WriteLine(text);Assert.True(child.ExitCode==0,"Synthetic panel child exit="+child.ExitCode+"\n"+text);}
        finally{if(!child.HasExited){child.Kill(true);await child.WaitForExitAsync();}}
    }
    internal async Task PanelScenario()
    {
        // Same historical mixed-owner preparation as ControllerMultiScenario; no preconnected/prearmed wire peer.
        LinuxInput.Harden();
        string root=Path.Combine("/home/ubuntu/apex-deliveries","pc-"+Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root,(UnixFileMode)448);
        string financial=Path.Combine(root,"financial"),controlRoot=Path.Combine(root,"control");Directory.CreateDirectory(controlRoot,(UnixFileMode)448);
        var binding=OkxConfiguration.Bind(new OkxNavalOptions{Schema="okx-naval-config-v1",Scopes=new[]{new OkxScopeOptions{ScopeId="finite-demo",Environment="OkxDemo",Region="EEA",ExpectedUid="99225511",ExpectedMainUid="99225511",QuoteCurrency="EUR",CredentialBindingId="foreground-demo",SafetyVersion="okx-cash-v1"}}})[0];
        var f=new OperationalPeer(financial,binding,finite:true);var history=new FiniteDemoPeer(f){BaseCurrency="SOL"};
        await using var fixture=(MultiFixture)Activator.CreateInstance(typeof(MultiFixture),BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{f,history},null)!;
        bool benchmark=false;
        f.Mutate=(path,body)=>
        {
            OkxOrdinaryMixedAccountTests.Shape(f,history,path,body);
            if(path=="/api/v5/account/instruments")
            {
                body["data"]![0]!["minSz"]="0.01";
                if(benchmark){var btc=body["data"]![0]!.DeepClone();btc["instId"]="BTC-USDC";btc["baseCcy"]="BTC";btc["lotSz"]="0.00000001";btc["minSz"]="0.0001";btc["tickSz"]="0.1";body["data"]!.AsArray().Add(btc);var eth=btc.DeepClone();eth["instId"]="ETH-USDC";eth["baseCcy"]="ETH";eth["lotSz"]="0.000001";eth["minSz"]="0.001";eth["tickSz"]="0.01";body["data"]!.AsArray().Add(eth);}
            }
            typeof(MultiFixture).GetMethod("Denominate",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,new object[]{body});
            if(benchmark)MultiOrdinary(fixture,path,body);
            if(benchmark&&path is "/api/v5/trade/order" or "/api/v5/trade/orders-history" or "/api/v5/trade/orders-history-archive")foreach(var row in body["data"]!.AsArray().Where(row=>row!["state"]!.GetValue<string>()=="filled")){row!["posSide"]=path=="/api/v5/trade/order"?"net":"";row["tradeQuoteCcy"]=path=="/api/v5/trade/order"?"":"EUR";}
        };
        f.Runtime.FiniteFundingPremise=(NavalFiniteOperatorPremise)OkxCurrentMarketFundingTests.Premise(f);await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        await using(var finite=OkxOperationalTradingService.Create(f.Service,f.Clock,(s,k,d,x)=>OkxExecutionClient.ForTests(history,f.Clock,s,k,d,x),history))
        {
            finite.BindFiniteSafety(()=>true);
            string result=await OkxFiniteDemoSequence.RunAsync(finite,f.Service,Guid.NewGuid().ToString("N"),stage=>{if(stage=="FILLED_BUY_SUBMIT_RESULT")f.WriteBoundary=b=>{if(history.SellClient!=""&&b==Apex.Desktop.Services.AtomicWriteBoundary.BeforeTempCreate)throw new IOException("synthetic-post-send-commit-fault");};},default);
            Assert.Equal("OWNED_EXIT_INCOMPLETE_CHECK_RESIDUAL_PROTECTION",result);f.WriteBoundary=null;
        }
        var failed=f.Service.BrokerSession!.ReadState();Assert.Equal(NavalExecutionStage.Dispatching,failed.NavalBroker!.Operational!.Intents.Last().Stage);Assert.Null(failed.NavalBroker.Operational.Intents.Last().Outcome);
        await f.Service.DisconnectAsync();benchmark=true;
        var seed=JsonSerializer.Deserialize<EngineState>(File.ReadAllText(f.Target),NavalJson.Options)!;
        var buy=seed.NavalBroker!.Operational!.Intents.First(i=>i.Operation==NavalExecutionOperation.Submit&&!i.Reduction) with{Stage=NavalExecutionStage.Dispatching,Outcome=null};
        seed.NavalBroker=NavalBrokerValidation.Seal(seed.NavalBroker with{Operational=seed.NavalBroker.Operational with{Intents=seed.NavalBroker.Operational.Intents.Select(i=>i.Id==buy.Id?buy:i).ToArray()}});File.WriteAllText(f.Target,NavalJson.Serialize(seed));fixture.Seed=NavalJson.Clone(seed);
        Assert.Null(seed.NavalBroker.Operational.OrdinaryDemo);Assert.Null(seed.NavalBroker.Operational.OrdinaryCatalog);Assert.Single(seed.Naval!.Fills);
        foreach(string p in Directory.GetDirectories(financial,"*",SearchOption.AllDirectories))File.SetUnixFileMode(p,(UnixFileMode)448);foreach(string p in Directory.GetFiles(financial,"*",SearchOption.AllDirectories))File.SetUnixFileMode(p,(UnixFileMode)384);File.SetUnixFileMode(financial,(UnixFileMode)448);
        var encrypted=OkxControllerServerTests.AcceptanceVault(Path.Combine(root,"vault"));using var cryptor=encrypted.Cryptor;var vault=encrypted.Vault;await vault.SaveNewAsync(OperationalPeer.Key,OperationalPeer.Secret,OperationalPeer.Pass);
        byte[] cipher=File.ReadAllBytes(Path.Combine(root,"vault",LinuxDemoCredentialVault.RecordName));
        var syntheticTransport=new FiniteEntryTestComposition(f.Clock,(s,k,d,x)=>OkxExecutionObservationClient.ForTests(f,f.Clock,s,k,d,x,(t,c)=>{f.Clock.Advance(t);return Task.CompletedTask;}),(s,k,d,x)=>OkxObservationStream.ForTests((u,c)=>Task.FromResult<WebSocket>(new OperationalSocket(u)),f.Clock,s,k,d,x),(s,k,d,x)=>OkxExecutionClient.ForTests(fixture.Peer,f.Clock,s,k,d,x),fixture.Market);
        int discoveries=0,factories=0;
        string Body(string id)=>JsonSerializer.Serialize(new{commandId=id});
        async Task<JsonElement> Json(System.Net.Http.HttpResponseMessage r){using var d=JsonDocument.Parse(await r.Content.ReadAsStringAsync());return d.RootElement.Clone();}
        async Task Run(bool reopen)
        {
            await using var server=LinuxControllerServer.ForTests(controlRoot,financial,vault,(k,c)=>{discoveries++;return Task.FromResult(new OkxEnrollmentDiscovery(new(binding.Account.AccountId,binding.MainUid,"Demo","DemoTradingApi",k.Fingerprint,DateTimeOffset.UtcNow,ImmutableArray<OkxExecutionBalanceDetailDto>.Empty,ImmutableArray<OkxExecutionInstrumentDto>.Empty),"SYNTHETIC"));},syntheticTransport);
            using var stop=new CancellationTokenSource();var running=server.RunAsync(stop.Token);
            try
            {
                // Product default composition has no transport, and browser cannot create one.
                await using(var unpaired=await Host.Open())
                {
                    var u=await Json(await unpaired.Send("status"));Assert.Equal("NOT_CONFIGURED",u.GetProperty("availability").GetString());
                    Assert.Equal("NOT_CONFIGURED",(await Json(await unpaired.Send("connect","operator",Body("unpaired")))).GetProperty("code").GetString());
                }
                int beforeDiscovery=discoveries;string beforeBoot=File.ReadAllText(f.Target);
                RelayFrames? observed=null;
                await using var remote=new OkxRemoteControllerControl(async ct=>{factories++;return observed=await RelayFrames.Open(controlRoot,ct);});
                await using var http=await Host.Open(remote);
                Assert.Equal(beforeDiscovery,discoveries);
                var channel=await Json(await http.Send("connect","operator",Body("channel")));Assert.Equal("confirmed",channel.GetProperty("outcome").GetString());
                Assert.Equal(beforeDiscovery,discoveries);Assert.Equal(beforeBoot,File.ReadAllText(f.Target));Assert.Equal(new[]{"STATUS","SNAPSHOT"},observed!.Actions.ToArray());
                var status=await Json(await http.Send("status"));Assert.False(status.GetProperty("snapshot").GetProperty("connected").GetBoolean());Assert.False(status.GetProperty("canPrepare").GetBoolean());
                // This reaches HTTP 404 with original panel product, not a setup/platform refusal.
                var connection=await http.Send("connect-account","operator",Body("broker"));Assert.Equal(HttpStatusCode.OK,connection.StatusCode);
                var connected=await Json(connection);Assert.Equal("confirmed",connected.GetProperty("outcome").GetString());Assert.Equal("STOP",connected.GetProperty("confirmedMode").GetString());Assert.Equal(beforeDiscovery+1,discoveries);
                var replay=await Json(await http.Send("connect-account","operator",Body("broker")));Assert.Equal(connected.GetRawText(),replay.GetRawText());Assert.Single(observed.Actions.Where(a=>a=="CONNECT"));
                status=await Json(await http.Send("status"));Assert.True(status.GetProperty("stale").GetBoolean());Assert.False(status.GetProperty("canPrepare").GetBoolean());Assert.False(status.GetProperty("canConnectAccount").GetBoolean());
                const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
                var attachment=typeof(LinuxControllerServer).GetField("host",flags)!.GetValue(server)!;var host=(LinuxDemoHost)attachment.GetType().GetField("host",flags)!.GetValue(attachment)!;
                fixture.Account=(OkxAccountConnectionService)typeof(LinuxDemoHost).GetField("account",flags)!.GetValue(host)!;fixture.Loop=(OkxOperationalTradingService)typeof(LinuxDemoHost).GetField("loop",flags)!.GetValue(host)!;
                Assert.False(fixture.Loop.ReadStatus().Armed);Assert.Equal("STOP",fixture.Loop.ReadStatus().Mode);
                async Task<JsonElement> Command(string action,string id){var r=await Json(await http.Send(action,"operator",Body(id)));Console.WriteLine("PANEL "+action+" "+r.GetRawText());return r;}
                async Task Refresh(string id)
                {
                    var response=await Command("refresh",id);
                    Assert.True(response.GetProperty("outcome").GetString()=="confirmed"||response.GetProperty("code").GetString()=="POLL_ALREADY_PENDING",response.GetRawText());
                    // A real background poll may already own the single read slot. Observe its completion;
                    // do not retry a mutation or weaken readiness to make the fixture advance.
                    for(int n=0;n<200;n++)
                    {
                        var view=await Json(await http.Send("status"));
                        if(!view.GetProperty("stale").GetBoolean()&&view.GetProperty("pending").GetInt32()==0)return;
                        await Task.Delay(20);
                    }
                    Assert.Fail("Fresh snapshot did not complete after explicit refresh/background poll");
                }
                await Refresh("after-connect");status=await Json(await http.Send("status"));Assert.True(status.GetProperty("canPrepare").GetBoolean());Assert.False(status.GetProperty("canConnectAccount").GetBoolean());
                Assert.Equal("FRESH_READINESS_REQUIRED",(await Command("start","not-armed")).GetProperty("code").GetString());
                await Refresh("after-refused");
                var initial=fixture.Account.BrokerSession!.ReadState();Assert.Equal(6,initial.NavalBroker!.Operational!.LastBalances!.Count);Assert.Equal(reopen?2:1,initial.Naval!.Fills.Count);
                if(!reopen)
                {
                    Assert.Equal("confirmed",(await Command("prepare","prepare-1")).GetProperty("outcome").GetString());await Refresh("after-prepare-1");
                    var prepared=fixture.Account.BrokerSession!.ReadState();Assert.NotNull(prepared.NavalBroker!.Operational!.OrdinaryDemo);Assert.NotNull(prepared.NavalBroker.Operational.OrdinaryCatalog);
                    Assert.Equal(2,prepared.Naval!.Fills.Count);initial=prepared; // PREPARE reconciles the retained historical sell; preserve cash after that legitimate recovery.
                    Assert.Equal("confirmed",(await Command("prepare","prepare-2")).GetProperty("outcome").GetString());await Refresh("after-prepare-2");
                    var twice=fixture.Account.BrokerSession!.ReadState();Assert.Equal(NavalJson.Serialize(prepared.NavalBroker.Operational.OrdinaryDemo),NavalJson.Serialize(twice.NavalBroker!.Operational!.OrdinaryDemo));Assert.Equal(NavalJson.Serialize(prepared.NavalBroker.Operational.OrdinaryCatalog),NavalJson.Serialize(twice.NavalBroker.Operational.OrdinaryCatalog));
                    Assert.Equal(new[]{"BTC-EUR","ETH-EUR","SOL-EUR"},fixture.Loop.ReadStatus().Instruments.OrderBy(i=>i,StringComparer.Ordinal));
                    Assert.Equal("confirmed",(await Command("arm","arm")).GetProperty("outcome").GetString());await Refresh("after-arm");
                    Assert.Equal("RUNNING",(await Command("start","start")).GetProperty("confirmedMode").GetString());await Refresh("running");status=await Json(await http.Send("status"));Assert.Equal("RUNNING",status.GetProperty("snapshot").GetProperty("mode").GetString());
                    // Channel detach never means broker DISCONNECT/STOP; reopen the channel explicitly.
                    Assert.Equal("CHANNEL_CLOSED_VPS_MAY_CONTINUE",(await Command("disconnect","detach")).GetProperty("code").GetString());Assert.Equal("RUNNING",fixture.Loop.ReadStatus().Mode);
                    Assert.Equal("confirmed",(await Command("connect","reattach")).GetProperty("outcome").GetString());Assert.Equal(beforeDiscovery+1,discoveries);
                }
                Assert.Equal("STOP",(await Command("stop","stop")).GetProperty("confirmedMode").GetString());await Refresh("stopped");Assert.False(fixture.Loop.ReadStatus().Armed);
                var after=fixture.Account.BrokerSession!.ReadState();
                Assert.Equal(NavalJson.Serialize(seed.NavalBroker.Baseline),NavalJson.Serialize(after.NavalBroker!.Baseline));Assert.Equal(seed.NavalBroker.Namespace,after.NavalBroker.Namespace);Assert.Equal(seed.Naval.ConfigDigest,after.Naval!.ConfigDigest);Assert.Equal(seed.Naval.Accounts.Single().Seed,after.Naval.Accounts.Single().Seed);
                foreach(var intent in seed.NavalBroker.Operational.Intents)Assert.Equal(NavalJson.Serialize(intent),NavalJson.Serialize(after.NavalBroker.Operational!.Intents.Single(v=>v.Id==intent.Id)));
                Assert.Equal(initial.Naval.Accounts.Single().Cash,after.Naval.Accounts.Single().Cash);Assert.Equal(0,fixture.Peer.Posts);Assert.Equal(2,after.Naval.Fills.Count);
                var dust=Assert.Single(after.Naval.Reservations,r=>r.QuarantineMark!=null);Assert.Equal(initial.Naval.Reservations.Single(r=>r.Id==dust.Id).Inventory,dust.Inventory);
                string? evidence=PanelCapture;if(evidence!=null){Directory.CreateDirectory(evidence);File.WriteAllText(Path.Combine(evidence,reopen?"reopened-owner.json":"stopped-owner.json"),NavalJson.Serialize(after));File.WriteAllText(Path.Combine(evidence,reopen?"reopened-wire.json":"startup-wire.json"),JsonSerializer.Serialize(observed.Actions.ToArray()));}
            }
            finally{stop.Cancel();await running.WaitAsync(TimeSpan.FromSeconds(15));}
        }
        await Run(false);await Run(true);Assert.Equal(2,discoveries);Assert.Equal(3,factories);Assert.True(cipher.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(root,"vault",LinuxDemoCredentialVault.RecordName))));
        foreach(var assembly in new[]{typeof(OkxRemoteControllerControl).Assembly,typeof(LinuxControllerServer).Assembly,typeof(NavalBrokerSession).Assembly,typeof(OkxExecutionClient).Assembly,GetType().Assembly})Console.WriteLine("CONSUMED "+assembly.GetName().Name+" "+Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)))+" "+assembly.Location);
        Console.WriteLine("PANEL_REAL_UDS_STARTUP_PASS CONNECT_explicit=True PREPARE_twice=True ARM_START_STOP=True retainedOwner=True reopenedSTOP=True brokerWrites=0");
    }
}

// Test-only stdio adapter around the real relay and actual OS-admitted Unix socket.
// It observes outgoing actions but never manufactures HELLO, receipts, snapshots or financial state.
internal sealed class RelayFrames:IOkxControllerFrameTransport
{
    readonly Pipe up=new(),down=new();readonly CancellationTokenSource life=new();readonly LinuxControllerRelay relay;readonly OkxControllerClientFrames frames;readonly Task<int> forwarding;
    internal readonly ConcurrentQueue<string> Actions=new();
    RelayFrames(LinuxControllerRelay relay){this.relay=relay;frames=new(down.Reader.AsStream(),up.Writer.AsStream());forwarding=relay.ForwardAsync(up.Reader.AsStream(),down.Writer.AsStream(),life.Token);}
    internal static async Task<RelayFrames> Open(string root,CancellationToken ct)=>new(await LinuxControllerRelay.ConnectForTestsAsync(root,ct));
    public Task<string?> ReadAsync(int cap,CancellationToken ct)=>frames.ReadAsync(cap,ct);
    public Task WriteAsync(string line,CancellationToken ct){Actions.Enqueue(OkxControllerProtocol.DecodeCommand(line).Action);return frames.WriteAsync(line,ct);}
    public async ValueTask DisposeAsync(){life.Cancel();relay.Dispose();await frames.DisposeAsync();try{await forwarding;}catch(Exception e) when(e is IOException or OperationCanceledException or ObjectDisposedException){}life.Dispose();}
}
