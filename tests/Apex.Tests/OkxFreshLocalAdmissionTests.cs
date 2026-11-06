using System;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.Tests;

// Core PREPARE admission boundaries, driven through the real Linux ordinary host composition.
public sealed class OkxFreshLocalAdmissionTests
{
    private static async Task<string> Prepare(string provenance,bool finiteHistory)
    {
        await using var f=new OperationalPeer(finite:true);using var peer=new FiniteDemoPeer(f){BaseCurrency="SOL"};
        f.Mutate=(p,b)=>OkxOrdinaryMixedAccountTests.Shape(f,peer,p,b);
        f.Runtime.FiniteFundingPremise=((NavalFiniteOperatorPremise)OkxCurrentMarketFundingTests.Premise(f)) with{Provenance=provenance};
        await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default),f.Service.Status);
        Assert.Equal(provenance,f.Service.BrokerSession!.ReadState().NavalBroker!.Baseline.Observations.OperatorPremise!.Provenance);
        if(finiteHistory)
        {
            await using var loop=OkxOperationalTradingService.Create(f.Service,f.Clock,(s,k,h,x)=>OkxExecutionClient.ForTests(peer,f.Clock,s,k,h,x),peer);loop.BindFiniteSafety(()=>true);
            Assert.Equal("COMPLETED_STOP_WITH_EXACT_DUST",await OkxFiniteDemoSequence.RunAsync(loop,f.Service,Guid.NewGuid().ToString("N"),_=>{},default));
        }
        await f.Service.DisconnectAsync();f.Clock.Advance(TimeSpan.FromMinutes(31));
        await using var runtime=OkxNavalRuntime.ComposeLinuxDemo(f.Root,f.Binding,f,f,()=>true);
        await using var ordinary=OkxAccountConnectionService.ForTests(runtime,f.Root,f.Binding,f,f.Clock,
            (s,k,h,x)=>OkxExecutionObservationClient.ForTests(f,f.Clock,s,k,h,x,(t,c)=>{f.Clock.Advance(t);return Task.CompletedTask;}),
            (s,k,h,x)=>OkxObservationStream.ForTests((uri,c)=>Task.FromResult<System.Net.WebSockets.WebSocket>(new OperationalSocket(uri)),f.Clock,s,k,h,x),()=>OkxNavalStore.ForTests(f.Root,f.Binding));
        var request=f.Request with{Session=Guid.NewGuid()};var lease=await runtime.Activation.RequestAsync(request,default);Assert.NotNull(lease);Assert.True(await ordinary.ConnectAsync(request,lease!,default),ordinary.Status);
        await using var normal=OkxOperationalTradingService.Create(ordinary,f.Clock,(s,k,h,x)=>OkxExecutionClient.ForTests(peer,f.Clock,s,k,h,x),peer);
        var prepared=await normal.PrepareOrdinaryDemoAsync(default);
        Assert.Null(ordinary.BrokerSession!.ReadState().NavalBroker!.Operational?.OrdinaryDemo);
        return prepared.Code;
    }
    [Fact]
    public async Task Linux_parent_owner_without_dust_gap_history_is_still_refused_exactly_as_before()
    {
        if(!OperatingSystem.IsLinux())return; // Linux ordinary host composition only.
        Assert.Equal("ORDINARY_DEMO_UNIQUE_HISTORICAL_DUST_REQUIRED",await Prepare(NavalCurrentMarketFunding.ParentProvenance,finiteHistory:false));
    }
    [Fact]
    public async Task Windows_local_provenance_owner_carrying_history_is_refused_with_distinct_code()
    {
        if(!OperatingSystem.IsLinux())return;
        Assert.Equal("ORDINARY_DEMO_FRESH_LOCAL_HISTORY_PRESENT",await Prepare(NavalCurrentMarketFunding.WindowsLocalProvenance,finiteHistory:true));
    }
    [Fact]
    public async Task Windows_local_provenance_owner_outside_EUR_scope_is_refused_with_distinct_code()
    {
        if(!OperatingSystem.IsLinux())return; // this synthetic owner is USDC-quoted
        Assert.Equal("ORDINARY_DEMO_FRESH_LOCAL_SCOPE_INVALID",await Prepare(NavalCurrentMarketFunding.WindowsLocalProvenance,finiteHistory:false));
    }
}
