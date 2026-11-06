using System;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Apex.Desktop.Services;
using Xunit;
namespace Apex.Tests;
public sealed partial class OkxConditionalTradingTests
{
    [Fact]
    [Trait("Gate","WindowsLocalExecution")]
    public async Task Windows_local_controller_PREPARE_real_RunAsync_BTC_ETH_protection_settlement_STOP_reopen()
    {
        Assert.True(OperatingSystem.IsWindows());
        await using var fixture=await MultiFixture.Create(windowsLocalSeed:true);
        var f=fixture.F;var historical=fixture.Seed;
        fixture.Runtime=OkxNavalRuntime.Compose(f.Root,new[]{f.Binding},f,f);
        var approval=new WindowsOkxOrdinaryApprovalProvider(()=>fixture.Account);
        fixture.Account=OkxAccountConnectionService.ForTests(fixture.Runtime,f.Root,f.Binding,approval,f.Clock,
            (s,k,d,x)=>OkxExecutionObservationClient.ForTests(f,f.Clock,s,k,d,x,(t,c)=>{f.Clock.Advance(t);return Task.CompletedTask;}),
            (s,k,d,x)=>OkxObservationStream.ForTests((u,c)=>Task.FromResult<WebSocket>(new OperationalSocket(u)),f.Clock,s,k,d,x),()=>OkxNavalStore.OpenNative(f.Root,f.Binding));
        int loops=0;
        var control=new OkxOperatorControl(fixture.Runtime,new ConfigStore(f.Root),account=>
        {
            loops++;return fixture.Loop=OkxOperationalTradingService.Create(account,f.Clock,(s,k,d,x)=>OkxExecutionClient.ForTests(fixture.Peer,f.Clock,s,k,d,x),fixture.Market);
        });
        async Task<string> Command(string action)=>await control.ExecuteAsync(f.Binding.ScopeId,action,Guid.NewGuid().ToString("N"),default);
        try
        {
            Assert.False(fixture.Account.OrdinaryDemoPolicy);
            Assert.Equal("LOCAL_EXISTING_DEMO_ATTACHED_STOP",await Command("attach-local"));
            Assert.True(fixture.Account.OrdinaryDemoPolicy);Assert.False(fixture.Runtime.IsLinuxDemo);
            Assert.Equal("CONNECTED_STOP_RECONCILIATION_REQUIRED",await Command("connect"));
            Assert.Equal("ORDINARY_DEMO_PREPARED",await Command("prepare"));
            Assert.Equal("ORDINARY_DEMO_ALREADY_PREPARED",await Command("prepare"));
            Assert.Equal(new[]{"BTC-EUR","ETH-EUR","SOL-EUR"},fixture.Loop.ReadStatus().Instruments.OrderBy(i=>i,StringComparer.Ordinal));
            var before=fixture.Account.BrokerSession!.ReadState();Assert.Equal(2,before.Naval!.Fills.Count);
            Assert.NotEqual("RUNNING",await Command("start"));Assert.False(fixture.Loop.ReadStatus().Armed);
            fixture.Peer.BeforeResponse=()=>
            {
                var peer=fixture.Peer;Assert.InRange(peer.Size*110,0m,10m);peer.ExpectedSellSize=peer.Size*.999m;peer.ProtectionCanceled=false;
                int n=fixture.Cycles.Count;peer.EntryOrderId=(1777+n*10).ToString();peer.ExitOrderId=(1778+n*10).ToString();peer.GeneratedAlgoId=(1889+n*10).ToString();
                string instrument=fixture.Account.BrokerSession!.ReadState().NavalBroker!.Operational!.Intents.Last().Instrument;
                fixture.Cycles.Add(new(instrument,peer.Client,peer.Attachment,peer.EntryOrderId,peer.ExitOrderId,peer.GeneratedAlgoId,peer.Size,f.Clock.GetUtcNow()));
            };
            fixture.Peer.BeforeMutationResponse=kind=>{var c=fixture.Cycles.Last();if(kind=="cancel"){c.Canceled=true;if(fixture.Cycles.Count==2)foreach(string pair in new[]{"BTC-EUR","ETH-EUR","SOL-EUR"})File.Delete(Path.Combine(f.Root,"advisory","reports",pair,"CURRENT.json"));}if(kind=="sell"){c.ExitClient=fixture.Peer.ExitClient;c.Sold=true;c.SellUtc=f.Clock.GetUtcNow();}};
            Assert.Equal("EXECUTE_ARMED_START_SEPARATE_CONDITIONAL_RISK",await Command("arm"));
            Assert.Equal("ORDINARY_DEMO_UNARMED_STOP_REQUIRED",await Command("prepare"));
            fixture.Market.Wait=false;
            foreach(string pair in new[]{"BTC-EUR","ETH-EUR","SOL-EUR"})await SeedSyntheticAdvice(fixture,pair);
            string started=await Command("start");Assert.Equal("RUNNING",fixture.Loop.ReadStatus().Mode);
            for(int n=0;n<1200;n++)
            {
                var state=fixture.Account.BrokerSession!.ReadState();if(state.Naval!.Fills.Count==6&&fixture.Loop.ReadStatus().Mode=="RUNNING")break;
                Assert.True(n<1199,"Native scheduler stalled "+fixture.Loop.ReadStatus().Code+" "+string.Join(",",fixture.Loop.ReadStatus().Obligations)+" posts="+fixture.Peer.Posts+" fills="+state.Naval.Fills.Count);await Task.Delay(25);
            }
            Assert.Equal("OPERATOR_STOP",await Command("stop"));
            await control.DisposeAsync();
            using var store=OkxNavalStore.OpenNative(f.Root,f.Binding);
            var raw=store.Get(NavalBrokerValidation.StateKey)!;var after=System.Text.Json.JsonSerializer.Deserialize<Apex.Core.Engine.EngineState>(raw,NavalJson.Options)!;
            Assert.Equal("STOP",after.Naval!.Mode);
            Assert.Equal(new[]{"BTC-EUR","ETH-EUR"},fixture.Cycles.Select(c=>c.Instrument).OrderBy(i=>i,StringComparer.Ordinal));Assert.All(fixture.Cycles,c=>Assert.True(c.Sold&&c.Canceled));Assert.Equal(6,fixture.Peer.Posts);Assert.Equal(1,loops);
            Assert.Equal(NavalJson.Serialize(historical.NavalBroker!.Baseline),NavalJson.Serialize(after.NavalBroker!.Baseline));Assert.Equal(historical.NavalBroker.Namespace,after.NavalBroker.Namespace);Assert.Equal(historical.Naval!.Accounts.Single().Seed,after.Naval.Accounts.Single().Seed);
            foreach(var i in historical.NavalBroker.Operational!.Intents)Assert.Equal(NavalJson.Serialize(i),NavalJson.Serialize(after.NavalBroker.Operational!.Intents.Single(v=>v.Id==i.Id)));
            Assert.Equal(before.Naval.Accounts.Single().Cash+fixture.Cycles.Sum(c=>-c.Size*110+c.Size*.999m*c.SellPrice*.999m),after.Naval.Accounts.Single().Cash);
            Assert.All(after.Naval.Reservations.Where(r=>r.QuarantineMark==null),r=>Assert.True(r.Inventory==0&&r.Pending==0&&!r.ReductionUnsettled&&r.TerminalCoverage));
            var dust=Assert.Single(after.Naval.Reservations,r=>r.QuarantineMark!=null);Assert.Equal(before.Naval.Reservations.Single(r=>r.Id==dust.Id).Inventory,dust.Inventory);Assert.Equal(dust.Inventory*dust.QuarantineMark,dust.RiskHold);Assert.Single(after.NavalBroker.Operational!.Obligations);
            int paths=f.Paths.Count,opens=f.SecretOpens;
            await using var restarted=OkxNavalRuntime.Compose(f.Root,new[]{f.Binding},f,f);
            await using var reopenedControl=new OkxOperatorControl(restarted,new ConfigStore(f.Root));
            Assert.False(restarted.IsWindowsOrdinary(f.Binding));Assert.Equal("CONNECT_REQUIRED",await reopenedControl.ExecuteAsync(f.Binding.ScopeId,"start","restart-start",default));Assert.Equal(paths,f.Paths.Count);Assert.Equal(opens,f.SecretOpens);
            Console.WriteLine("WINDOWS_LOCAL_MULTI_PASS nativeDPAPI=True nativeFinancialOrigin=True syntheticOnly=True BTC_ETH_cycles=2 protectedSettled=True sharedCash=True pinnedDust=True restartSTOP=True");
        }
        finally{await control.DisposeAsync();}
    }
}
