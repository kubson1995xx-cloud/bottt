using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Apex.Core.Engine;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Apex.Adapters.Okx.Execution;
using Xunit;
namespace Apex.Tests;

public sealed class OkxFeeTypeZeroTests
{
    private static EngineState State(OperationalPeer f)=>JsonSerializer.Deserialize<EngineState>(File.ReadAllText(f.Target))!;
    [Theory]
    [InlineData("0",true)]
    [InlineData("1",false)]
    public async Task Production_policy_posts_actual_fee_currency_net_inventory_and_deduplicates_restart(string feeType,bool entryBase)
    {
        await using var f=new OperationalPeer();
        f.Mutate=(p,b)=>{if(p=="/api/v5/account/config")b["data"]![0]!["feeType"]=feeType;};
        await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        Assert.Equal(entryBase,State(f).NavalBroker!.Baseline.Observations.Fees.EntryInBase);
        using var peer=new TradingPeer(f);
        await using var loop=OkxOperationalTradingService.Create(f.Service,f.Clock,(s,k,h,x)=>OkxExecutionClient.ForTests(peer,f.Clock,s,k,h,x),peer);
        var request=f.Request with{Purpose=OkxActivationPurpose.Execute,Session=f.Service.TradingSessionId,ExpiresUtc=DateTimeOffset.UtcNow.AddSeconds(120)};
        var lease=await f.Runtime.Activation.RequestAsync(request,default);Assert.NotNull(lease);Assert.True(loop.Arm(request,lease!));
        Assert.NotNull(loop.ProtectionProfile);
        Assert.Equal(NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);
        Assert.Equal(NavalMutationStatus.Applied,loop.Start().Status);
        var sent=await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);Assert.True(sent.Status==NavalMutationStatus.Applied,sent.Code);
        decimal fee=peer.Size*(entryBase?.001m:.11m),net=peer.Size-(entryBase?fee:0);
        TradingEconomicsFixture.Install(f,peer,peer.Size,fee,true,entryBase);var economics=f.Mutate!;
        f.Mutate=(p,b)=>{economics(p,b);if(p=="/api/v5/account/config")b["data"]![0]!["feeType"]=feeType;};
        var applied=await loop.ReconcileAsync(default);Assert.True(applied.Status==NavalMutationStatus.Applied,applied.Code);
        Assert.Equal(net,State(f).Naval!.Reservations.Single().Inventory);
        Assert.Equal(1234.56m-peer.Size*110-(entryBase?0:fee),State(f).Naval!.Accounts.Single().Cash);
        Assert.Single(State(f).Naval!.Fills);
        await loop.ReconcileAsync(default);Assert.Single(State(f).Naval!.Fills);Assert.Equal(1,peer.Posts);
        await f.Service.DisconnectAsync();Assert.True(f.Service.OpenExisting());
        Assert.Equal(net,State(f).Naval!.Reservations.Single().Inventory);Assert.Single(State(f).Naval!.Fills);
        Assert.Equal("STOP",f.Service.Coordinator!.Snapshot().Mode);
    }
    [Theory]
    [InlineData("0",false,false)]
    [InlineData("1",true,false)]
    [InlineData("0",true,true)]
    public async Task Policy_refuses_fee_flags_that_disagree_with_authenticated_account(string feeType,bool entryBase,bool exitBase)
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        var b=State(f).NavalBroker!.Baseline;var observations=b.Observations with{Account=b.Observations.Account with{FeeType=feeType},Fees=b.Observations.Fees with{EntryInBase=entryBase,ExitInBase=exitBase}};
        Assert.Null(OkxConditionalCashSpotPolicy.Admit(b with{Observations=observations}));
    }
}
