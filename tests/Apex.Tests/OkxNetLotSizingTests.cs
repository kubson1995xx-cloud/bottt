using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Apex.Core.Naval;
using Xunit;
namespace Apex.Tests;

public sealed partial class OkxConditionalTradingTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper sizingOutput;
    public OkxConditionalTradingTests(Xunit.Abstractions.ITestOutputHelper output){sizingOutput=output;}
    [Theory]
    [InlineData("0.001")]
    [InlineData("0.0008")]
    public async Task Ordinary_scheduled_base_fee_selects_exact_net_lots_and_completes_strategy_cycle(string rateText)
    {
        decimal rate=decimal.Parse(rateText,CultureInfo.InvariantCulture);
        await using var f=new OperationalPeer();
        f.Mutate=(p,b)=>
        {
            if(p=="/api/v5/account/config")b["data"]![0]!["feeType"]="0";
            if(p=="/api/v5/account/trade-fee")b["data"]![0]!["feeGroup"]![0]!["taker"]="-"+rateText;
        };
        await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        using var peer=new TradingPeer(f);await using var loop=Production(f,peer);await Start(f,loop);
        var entry=await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);
        Assert.True(entry.Status==NavalMutationStatus.Applied,entry.Code);Assert.Equal(1,peer.Posts);Assert.True(peer.SawDispatching);
        var before=State(f);var spec=before.NavalBroker!.Baseline.Observations.Catalog.Single();
        decimal fee=peer.Size*rate,net=peer.Size-fee;
        Assert.Equal(0,peer.Size%spec.Quantum);
        Assert.True(net%spec.Quantum==0,$"Reached posted gross {peer.Size}, scheduled net {net}, quantum {spec.Quantum}, nonaligned remainder {net%spec.Quantum}");
        // Independent original risk/cash/depth ceiling for this unchanged ALPHA candle fixture.
        var definition=NavalBrokerValidation.Definition(before.NavalBroker.Baseline);
        var r=before.Naval!.Reservations.Single();
        var unit=NavalCashRiskPolicy.Holds(r with{Quantity=1,Pending=1,Inventory=0},definition,before.Naval.Marks);
        decimal old=decimal.Floor(Math.Min(100m,Math.Min(1234.56m*.005m/unit.RiskHold,1234.56m/NavalCashRiskPolicy.Held(unit)))/spec.Quantum)*spec.Quantum;
        Assert.True(peer.Size<=old);Assert.True(old*(1-rate)%spec.Quantum!=0);Assert.True(net>=spec.MinimumQuantity);
        Assert.True(r.RiskHold<=1234.56m*.005m);Assert.True(NavalCashRiskPolicy.Held(r)<=1234.56m);
        peer.ExpectedSellSize=net;InstallOrdinaryPair(f,peer,fee);var entryPair=f.Mutate!;
        f.Mutate=(p,b)=>{entryPair(p,b);if(p=="/api/v5/account/trade-fee")b["data"]![0]!["feeGroup"]![0]!["taker"]="-"+rateText;};
        var observed=await loop.ReconcileAsync(default);
        Assert.True(observed.Status==NavalMutationStatus.Applied,observed.Code+" | "+string.Join(",",loop.ReadStatus().Obligations));
        Assert.True(loop.ReadStatus().Ready);Assert.Equal("RUNNING",loop.ReadStatus().Mode);
        Assert.Equal(net,State(f).Naval!.Reservations.Single().Inventory);Assert.Equal(2,State(f).NavalBroker!.Operational!.Bills.Count);
        Assert.Equal(1234.56m-peer.Size*110,loop.ReadStatus().Cash);
        peer.ExitSignal=true;var exit=await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);
        Assert.True(exit.Status==NavalMutationStatus.Applied,exit.Code);Assert.Equal(3,peer.Posts);Assert.True(peer.ProtectionCanceled);Assert.NotEmpty(peer.ExitClient);
        InstallOrdinarySellPair(f,peer,0,rate);
        var settled=await loop.ReconcileAsync(default);
        Assert.True(settled.Status==NavalMutationStatus.Applied,settled.Code+" | "+string.Join(",",loop.ReadStatus().Obligations));
        var state=State(f);Assert.Equal(0,state.Naval!.Reservations.Single().Inventory);Assert.False(state.Naval.Reservations.Single().ReductionUnsettled);
        Assert.Equal(2,state.Naval.Fills.Count);Assert.Equal(4,state.NavalBroker!.Operational!.Bills.Count);
        decimal cash=1234.56m-peer.Size*110+net*109.99m*(1-rate);
        Assert.Equal(cash,loop.ReadStatus().Cash);Assert.Equal(0,loop.ReadStatus().Held);Assert.Empty(loop.ReadStatus().Obligations);
        Assert.True(loop.ReadStatus().Ready);
        sizingOutput.WriteLine($"rate={rate}; oldGross={old}; postedGross={peer.Size}; net={net}; posts={peer.Posts}; fills={state.Naval.Fills.Count}; bills={state.NavalBroker.Operational.Bills.Count}; finalCash={cash}; flat={state.Naval.Reservations.Single().Inventory==0}; ready={loop.ReadStatus().Ready}; mode={loop.ReadStatus().Mode}; code={loop.ReadStatus().Code}");
        // Bounded continuation of this already deliberately STARTed ordinary session.
        Assert.Equal("RUNNING",loop.ReadStatus().Mode);
        await loop.ReconcileAsync(default);await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);
        Assert.Equal(3,peer.Posts);Assert.Equal(2,State(f).Naval!.Fills.Count);Assert.Equal(cash,loop.ReadStatus().Cash);Assert.True(loop.ReadStatus().Ready);
    }
    [Theory]
    [InlineData("risk")]
    [InlineData("depth")]
    [InlineData("cash")]
    [InlineData("quote")]
    public async Task Ordinary_net_lot_sizing_preserves_risk_cash_depth_and_quote_fee_quantity(string scenario)
    {
        decimal rate=.001m;
        await using var f=new OperationalPeer();
        f.Mutate=(p,b)=>
        {
            if(p=="/api/v5/account/config")b["data"]![0]!["feeType"]=scenario=="quote"?"1":"0";
            if(p=="/api/v5/account/trade-fee")b["data"]![0]!["feeGroup"]![0]!["taker"]=(-rate).ToString(CultureInfo.InvariantCulture);
        };
        await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        using var peer=new TradingPeer(f);
        if(scenario=="depth")peer.AskDepth=.34567891m;
        if(scenario=="cash"){peer.AskPrice=10000m;peer.BidPrice=9999.99m;}
        await using var loop=Production(f,peer);await Start(f,loop);
        var result=await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);Assert.True(result.Status==NavalMutationStatus.Applied,result.Code);Assert.Equal(1,peer.Posts);
        var state=State(f);var r=state.Naval!.Reservations.Single();var definition=NavalBrokerValidation.Definition(state.NavalBroker!.Baseline);var spec=definition.Catalog.Single();
        var unit=NavalCashRiskPolicy.Holds(r with{Quantity=1,Pending=1,Inventory=0},definition,state.Naval.Marks);
        decimal riskCap=1234.56m*.005m/unit.RiskHold,cashCap=1234.56m/NavalCashRiskPolicy.Held(unit),cap=Math.Min(peer.AskDepth,Math.Min(riskCap,cashCap));
        if(scenario=="risk")Assert.True(riskCap<cashCap&&riskCap<peer.AskDepth);
        if(scenario=="cash")Assert.True(cashCap<riskCap&&cashCap<peer.AskDepth);
        if(scenario=="depth")Assert.True(peer.AskDepth<cashCap&&peer.AskDepth<riskCap);
        decimal step=scenario=="quote"?spec.Quantum:.00001m;
        Assert.Equal(decimal.Floor(cap/step)*step,peer.Size);Assert.True(peer.Size<=cap);Assert.True(peer.Size>=spec.MinimumQuantity);
        Assert.True(r.RiskHold<=1234.56m*.005m);Assert.True(NavalCashRiskPolicy.Held(r)<=1234.56m);Assert.Equal(1234.56m,loop.ReadStatus().Cash);
        sizingOutput.WriteLine($"bound={scenario}; gross={peer.Size}; riskCap={riskCap}; cashCap={cashCap}; depth={peer.AskDepth}");
    }
    [Theory]
    [InlineData("effective-min","NET_LOT_MINIMUM")]
    [InlineData("net-min","NET_LOT_MINIMUM")]
    [InlineData("quantum-overflow","NET_LOT_SIZING_UNREPRESENTABLE")]
    [InlineData("precision-overflow","NET_LOT_SIZING_UNREPRESENTABLE")]
    public async Task Ordinary_net_lot_sizing_refuses_before_reservation_or_POST_when_minimum_or_exact_arithmetic_fails(string scenario,string code)
    {
        await using var f=new OperationalPeer();
        f.Mutate=(p,b)=>
        {
            if(p=="/api/v5/account/config")b["data"]![0]!["feeType"]="0";
            if(p=="/api/v5/account/trade-fee")b["data"]![0]!["feeGroup"]![0]!["taker"]=scenario=="quantum-overflow"?"-0.0000000000000000000000000001":scenario=="precision-overflow"?"-0.0008":"-0.001";
            if(p=="/api/v5/account/instruments")
            {
                if(scenario=="effective-min")b["data"]![0]!["lotSz"]="0.001";
                if(scenario=="net-min")b["data"]![0]!["minSz"]="0.9934";
                if(scenario=="quantum-overflow")b["data"]![0]!["lotSz"]="10";
                if(scenario=="precision-overflow")b["data"]![0]!["lotSz"]="0.1234567890123456789012345679";
            }
        };
        await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f);await using var loop=Production(f,peer);await Start(f,loop);
        var result=await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);
        Assert.Equal(NavalMutationStatus.Refused,result.Status);Assert.Equal(code,result.Code);Assert.Equal(0,peer.Posts);Assert.Empty(State(f).Naval!.Reservations);Assert.Equal(0,loop.ReadStatus().Held);
    }
    [Fact]
    public async Task Ordinary_net_lot_sizing_preserves_existing_zero_taker_fee_connection_refusal()
    {
        await using var f=new OperationalPeer();
        f.Mutate=(p,b)=>
        {
            if(p=="/api/v5/account/config")b["data"]![0]!["feeType"]="0";
            if(p=="/api/v5/account/trade-fee")b["data"]![0]!["feeGroup"]![0]!["taker"]="0";
        };
        var lease=await f.Runtime.Activation.RequestAsync(f.Request,default);Assert.NotNull(lease);
        Assert.False(await f.Service.ConnectAsync(f.Request,lease!,default));Assert.False(f.Service.IsConnected);Assert.False(System.IO.File.Exists(f.Target));
    }
    [Fact]
    public async Task Ordinary_net_lot_sizing_does_not_hide_unexpected_actual_base_fee_dust()
    {
        await using var f=new OperationalPeer();f.Mutate=(p,b)=>{if(p=="/api/v5/account/config")b["data"]![0]!["feeType"]="0";};
        await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f);await using var loop=Production(f,peer);await Start(f,loop);
        Assert.Equal(NavalMutationStatus.Applied,(await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default)).Status);
        decimal quantum=State(f).NavalBroker!.Baseline.Observations.Catalog.Single().Quantum;
        Assert.Equal(0,(peer.Size*(1-.001m))%quantum);
        decimal fee=peer.Size*.001m-quantum/10,net=peer.Size-fee,lot=decimal.Floor(net/quantum)*quantum;
        Assert.True(net%quantum>0);InstallOrdinaryPair(f,peer,fee);var pair=f.Mutate!;
        f.Mutate=(p,b)=>{pair(p,b);if(p is "/api/v5/trade/order-algo" or "/api/v5/trade/orders-algo-pending")foreach(var row in b["data"]!.AsArray())row!["sz"]=lot.ToString(CultureInfo.InvariantCulture);};
        var observed=await loop.ReconcileAsync(default);
        Assert.Equal(NavalMutationStatus.Stopped,observed.Status);Assert.Equal("STOP",loop.ReadStatus().Mode);Assert.False(loop.ReadStatus().Ready);
        Assert.Single(State(f).Naval!.Fills);Assert.Equal(net,State(f).Naval!.Reservations.Single().Inventory);
        Assert.Contains(loop.ReadStatus().Obligations,o=>o.StartsWith("PROTECTION_GAP:",StringComparison.Ordinal));
        Assert.Equal(NavalMutationStatus.Refused,loop.Start().Status);await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);Assert.Equal(1,peer.Posts);
    }
    [Fact]
    public void Net_lot_sizing_refuses_when_exact_scheduled_net_cannot_be_represented()
    {
        Assert.Throws<OverflowException>(()=>Apex.Desktop.Okx.OkxOperationalTradingService.NetLotQuantity(decimal.MaxValue,.00000001m,.001m));
    }
    [Theory]
    [InlineData("0.99434655","0.00000001","0.001","0.99434")]
    [InlineData("1.00104868","0.00000001","0.0008","1.0010375")]
    [InlineData("1.00104868","0.00000001","0.0008000","1.0010375")]
    [InlineData("1.2","0.0003","0.0008","1.125")]
    [InlineData("0.00000999","0.00000001","0.001","0")]
    [InlineData("0.123456789","0.00000001","0","0.12345678")]
    [InlineData("1","0.0000000000000000000000000001","0.0000000000000000000000000001","1")]
    public void Net_lot_sizing_is_exact_maximal_downward_rational_rounding(string capText,string quantumText,string rateText,string expectedText)
    {
        decimal P(string value)=>decimal.Parse(value,CultureInfo.InvariantCulture);
        decimal cap=P(capText),quantum=P(quantumText),rate=P(rateText);
        decimal actual=Apex.Desktop.Okx.OkxOperationalTradingService.NetLotQuantity(cap,quantum,rate);
        Assert.Equal(P(expectedText),actual);Assert.InRange(actual,0,cap);
        Assert.Equal(0,actual%quantum);Assert.Equal(0,(actual*(1-rate))%quantum);
    }
}
