using System;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Apex.Core.Naval;
using Xunit;
namespace Apex.Tests;

public sealed partial class OkxConditionalTradingTests
{
    [Fact]
    public async Task Ordinary_base_fee_paired_bills_post_individual_fill_once()
    {
        await using var f=new OperationalPeer();
        f.Mutate=(p,b)=>{if(p=="/api/v5/account/config")b["data"]![0]!["feeType"]="0";};
        await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        using var peer=new TradingPeer(f);await using var loop=Production(f,peer);await Start(f,loop);
        var entry=await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);Assert.True(entry.Status==NavalMutationStatus.Applied,entry.Code);Assert.Equal(1,peer.Posts);
        InstallOrdinaryPair(f,peer,.0001m);
        var result=await loop.ReconcileAsync(default);
        Assert.True(result.Status==NavalMutationStatus.Applied,result.Code+" | "+string.Join(",",loop.ReadStatus().Obligations));
        var state=State(f);Assert.Single(state.Naval!.Fills);Assert.Equal(peer.Size-.0001m,state.Naval.Reservations.Single().Inventory);
        Assert.Equal(2,state.NavalBroker!.Operational!.Bills.Count);Assert.Equal(1234.56m-peer.Size*110,loop.ReadStatus().Cash);
        await loop.ReconcileAsync(default);await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);
        Assert.Single(State(f).Naval!.Fills);Assert.Equal(1,peer.Posts);
    }
    [Fact]
    public async Task Ordinary_retrospective_metadata_reaches_same_owned_economics()
    {
        await using var f=new OperationalPeer();f.Mutate=(p,b)=>{if(p=="/api/v5/account/config")b["data"]![0]!["feeType"]="0";};
        await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f);await using var loop=Production(f,peer);await Start(f,loop);
        await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);InstallOrdinaryPair(f,peer,.0001m);var basis=f.Mutate!;
        f.Mutate=(p,b)=>
        {
            basis(p,b);
            if(p is "/api/v5/trade/order" or "/api/v5/trade/orders-history" or "/api/v5/trade/orders-history-archive")foreach(var row in b["data"]!.AsArray()){row!["slippage"]="0";row["attachAlgoOrds"]![0]!["percent"]="100";}
            if(p is "/api/v5/trade/order-algo" or "/api/v5/trade/orders-algo-pending")foreach(var row in b["data"]!.AsArray()){row!["aggressiveness"]="0";row["lmtOrderNumber"]="1";row["triggerParams"]=new JsonArray();}
        };
        var result=await loop.ReconcileAsync(default);Assert.True(result.Status==NavalMutationStatus.Applied,result.Code+" | "+string.Join(",",loop.ReadStatus().Obligations));Assert.Single(State(f).Naval!.Fills);Assert.Equal(1,peer.Posts);
    }
    [Theory]
    [InlineData("unknown-child")]
    [InlineData("pending-live")]
    [InlineData("pending-child")]
    [InlineData("pending-size")]
    public async Task Ordinary_protection_conflict_vetoes_sale(string fault)
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f);await using var loop=Production(f,peer);await Start(f,loop);
        await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);TradingEconomicsFixture.Install(f,peer,peer.Size,peer.Size*.11m,true);LivePending(f,peer,peer.GeneratedAlgoId);
        Assert.Equal(NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);var basis=f.Mutate!;
        f.Mutate=(p,b)=>
        {
            basis(p,b);
            if(fault=="unknown-child"&&p=="/api/v5/trade/order-algo")b["data"]![0]!.AsObject().Remove("ordIdList");
            if(p=="/api/v5/trade/orders-algo-pending"&&f.Paths.Last().Contains("ordType=conditional,oco",StringComparison.Ordinal))
            {
                // A later pending read contradicts exact cancellation, but shares its IDs.
                if(fault is "pending-live" or "pending-child" or "pending-size"){basis("/api/v5/trade/order-algo",b);b["data"]![0]!["state"]="live";}
                if(fault=="pending-child")b["data"]![0]!["ordIdList"]=new JsonArray("999");
                if(fault=="pending-size"&&b["data"]!.AsArray().Count>0)b["data"]![0]!["sz"]="999";
            }
        };
        if(fault=="pending-size")await loop.ReconcileAsync(default);
        var exit=await loop.ExitAsync(State(f).Naval!.Reservations.Single().Id,default);
        Assert.Empty(peer.ExitClient);Assert.NotEqual(NavalMutationStatus.Applied,exit.Status);Assert.NotEmpty(loop.ReadStatus().Obligations);Assert.Equal("STOP",loop.ReadStatus().Mode);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_fee0_owned_cycle_settles_sell_pair_once_and_retains_dust_STOP(bool dust)
    {
        await using var f=new OperationalPeer();f.Mutate=(p,b)=>{if(p=="/api/v5/account/config")b["data"]![0]!["feeType"]="0";};
        await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f);await using var loop=Production(f,peer);await Start(f,loop);
        await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);Assert.Equal(1,peer.Posts);
        decimal quantum=State(f).NavalBroker!.Baseline.Observations.Catalog.Single().Quantum;
        decimal entryFee=dust ? quantum/10 : quantum,net=peer.Size-entryFee,lot=decimal.Floor(net/quantum)*quantum,residual=net-lot;
        Assert.Equal(dust,residual>0);peer.ExpectedSellSize=lot;InstallOrdinaryPair(f,peer,entryFee);var pair=f.Mutate!;
        f.Mutate=(p,b)=>{pair(p,b);if(p is "/api/v5/trade/order-algo" or "/api/v5/trade/orders-algo-pending")foreach(var row in b["data"]!.AsArray())row!["sz"]=lot.ToString(CultureInfo.InvariantCulture);};
        var entryEconomics=await loop.ReconcileAsync(default);var before=State(f);Assert.Single(before.Naval!.Fills);Assert.Equal(net,before.Naval.Reservations.Single().Inventory);Assert.True(before.Naval.Reservations.Single().TerminalCoverage);
        if(dust)
        {
            Assert.Equal(NavalMutationStatus.Stopped,entryEconomics.Status);Assert.Equal("STOP",loop.ReadStatus().Mode);Assert.Contains(loop.ReadStatus().Obligations,o=>o.StartsWith("PROTECTION_GAP:",StringComparison.Ordinal));
            Assert.Equal(NavalMutationStatus.Refused,loop.Start().Status);await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);Assert.Equal(1,peer.Posts);
            // Exact lot is not full-net protection: retain the STOP. Existing explicit
            // own reduction is independent of scheduler START and does not sell dust.
            var exit=await loop.ExitAsync(before.Naval.Reservations.Single().Id,default);Assert.True(exit.Status==NavalMutationStatus.Applied,exit.Code);
        }
        else
        {
            Assert.True(entryEconomics.Status==NavalMutationStatus.Applied,entryEconomics.Code);Assert.Equal("RUNNING",loop.ReadStatus().Mode);
            peer.ExitSignal=true;var exit=await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);Assert.True(exit.Status==NavalMutationStatus.Applied,exit.Code);
        }
        Assert.Equal(3,peer.Posts);Assert.True(peer.ProtectionCanceled);Assert.NotEmpty(peer.ExitClient);
        InstallOrdinarySellPair(f,peer,residual);
        var settled=await loop.ReconcileAsync(default);Assert.True(settled.Status==NavalMutationStatus.Applied||dust&&settled.Status==NavalMutationStatus.Stopped,settled.Code+" | "+string.Join(",",loop.ReadStatus().Obligations));
        var state=State(f);var r=state.Naval!.Reservations.Single();Assert.Equal(residual,r.Inventory);Assert.False(r.ReductionUnsettled);Assert.Equal(2,state.Naval.Fills.Count);Assert.Equal(4,state.NavalBroker!.Operational!.Bills.Count);
        Assert.Contains(state.NavalBroker.Operational.SettledIntents,id=>state.NavalBroker.Operational.Intents.Any(i=>i.Id==id&&i.Reduction));
        decimal cash=1234.56m-peer.Size*110+lot*109.99m*(1-.001m);Assert.Equal(cash,loop.ReadStatus().Cash);
        await loop.ReconcileAsync(default);await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);Assert.Equal(3,peer.Posts);Assert.Equal(2,State(f).Naval!.Fills.Count);Assert.Equal(cash,loop.ReadStatus().Cash);
        loop.Stop();await loop.ExitAsync(r.Id,default);Assert.Equal(3,peer.Posts);
        await f.Service.DisconnectAsync();Assert.True(f.Service.OpenExisting());Assert.Equal("STOP",f.Service.Coordinator!.Snapshot().Mode);Assert.Equal(residual,State(f).Naval!.Reservations.Single().Inventory);
    }
    [Fact]
    public async Task Ordinary_RunAsync_obeys_STOP_after_first_dispatch_without_duplicate()
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f);await using var loop=Production(f,peer);await Start(f,loop);
        peer.HoldResponse=new(TaskCreationOptions.RunContinuationsAsynchronously);using var stop=new System.Threading.CancellationTokenSource();
        var running=loop.RunAsync(stop.Token);await peer.Invoked.Task.WaitAsync(TimeSpan.FromSeconds(10));loop.Stop();stop.Cancel();peer.HoldResponse.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>running);Assert.Equal(1,peer.Posts);Assert.False(loop.ReadStatus().Armed);Assert.Equal("STOP",loop.ReadStatus().Mode);
        await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);Assert.Equal(1,peer.Posts);
    }
    [Theory]
    [InlineData("unknown")]
    [InlineData("percent-type")]
    [InlineData("percent-bound")]
    [InlineData("trigger-schedule")]
    public async Task Ordinary_retrospective_profile_still_refuses_unsupported_operands(string fault)
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f);await using var loop=Production(f,peer);await Start(f,loop);
        await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);TradingEconomicsFixture.Install(f,peer,peer.Size,peer.Size*.11m,true);var basis=f.Mutate!;
        f.Mutate=(p,b)=>
        {
            basis(p,b);
            if(p is "/api/v5/trade/order" or "/api/v5/trade/orders-history" or "/api/v5/trade/orders-history-archive")foreach(var row in b["data"]!.AsArray())
            {
                if(fault=="unknown")row!["unsupportedNewField"]="";
                if(fault=="percent-type")row!["attachAlgoOrds"]![0]!["percent"]=0;
                if(fault=="percent-bound")row!["attachAlgoOrds"]![0]!["percent"]=new string('x',129);
            }
            if(fault=="trigger-schedule"&&p=="/api/v5/trade/order-algo")b["data"]![0]!["triggerParams"]=new JsonArray(new JsonObject());
        };
        await loop.ReconcileAsync(default);Assert.Equal("STOP",loop.ReadStatus().Mode);Assert.False(loop.ReadStatus().Ready);await loop.ExitAsync(State(f).Naval!.Reservations.Single().Id,default);Assert.Empty(peer.ExitClient);Assert.Equal(1,peer.Posts);
    }
    [Theory]
    [InlineData("foreign-fill")]
    [InlineData("fill-correction")]
    [InlineData("unlinked-bill")]
    public async Task Ordinary_foreign_and_correction_evidence_remain_vetoes_after_clean_pass(string fault)
    {
        await using var f=new OperationalPeer();f.Mutate=(p,b)=>{if(p=="/api/v5/account/config")b["data"]![0]!["feeType"]="0";};
        await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f);await using var loop=Production(f,peer);await Start(f,loop);
        await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);InstallOrdinaryPair(f,peer,.0001m);Assert.Equal(NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);var basis=f.Mutate!;
        f.Mutate=(p,b)=>
        {
            basis(p,b);var rows=b["data"]!.AsArray();
            if(p.StartsWith("/api/v5/trade/fills",StringComparison.Ordinal)&&rows.Count>0)
            {
                if(fault=="fill-correction")rows[0]!["fee"]="-0.0002";
                if(fault=="foreign-fill"){var foreign=rows[0]!.DeepClone();foreign["billId"]="906";foreign["tradeId"]="907";foreign["ordId"]="999";foreign["clOrdId"]="Foreign1";rows.Insert(0,foreign);}
            }
            if(fault=="unlinked-bill"&&p.StartsWith("/api/v5/account/bills",StringComparison.Ordinal)&&rows.Count>0){var foreign=rows[0]!.DeepClone();foreign["billId"]="906";foreign["ordId"]="999";foreign["clOrdId"]="Foreign1";rows.Insert(0,foreign);}
        };
        await loop.ReconcileAsync(default);f.Mutate=basis;await loop.ReconcileAsync(default);
        string marker=fault=="foreign-fill"?"FOREIGN_FILL:":fault=="fill-correction"?"FILL_CORRECTION:":"UNLINKED_BILL:";
        Assert.Contains(loop.ReadStatus().Obligations,o=>o.StartsWith(marker,StringComparison.Ordinal));Assert.Single(State(f).Naval!.Fills);await loop.ExitAsync(State(f).Naval!.Reservations.Single().Id,default);Assert.Empty(peer.ExitClient);
    }
    private static void InstallOrdinarySellPair(OperationalPeer f,TradingPeer peer,decimal residual,decimal exitRate=.001m)
    {
        var basis=f.Mutate!;string now=f.Clock.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        decimal size=peer.ExpectedSellSize!.Value,fee=size*109.99m*exitRate,cash=1234.56m-peer.Size*110+size*109.99m-fee;
        string N(decimal n)=>n.ToString("0.############################",CultureInfo.InvariantCulture);
        f.Mutate=(p,b)=>
        {
            basis(p,b);var rows=b["data"]!.AsArray();
            if((p.StartsWith("/api/v5/trade/fills",StringComparison.Ordinal)||p.StartsWith("/api/v5/account/bills",StringComparison.Ordinal))&&rows.Count>0)
            {
                var sell=rows[0]!.DeepClone();sell["ordId"]=peer.ExitOrderId;sell["clOrdId"]=peer.ExitClient;sell["tradeId"]="903";sell["billId"]="904";sell["fee"]=N(-fee);sell["ts"]=now;sell["fillTime"]=now;
                if(p.Contains("fills",StringComparison.Ordinal)){sell["side"]="sell";sell["fillPx"]="109.99";sell["fillSz"]=N(size);sell["feeCcy"]="USDC";rows.Insert(0,sell);}
                else{sell["ccy"]="USDC";sell["sz"]=N(size);sell["px"]="109.99";sell["balChg"]=N(size*109.99m-fee);var counter=sell.DeepClone();counter["billId"]="905";counter["ccy"]="BTC";counter["fee"]="0";counter["balChg"]=N(-size);rows.Insert(0,sell);rows.Insert(0,counter);}
            }
            if(p is "/api/v5/trade/order" or "/api/v5/trade/orders-history" or "/api/v5/trade/orders-history-archive"&&rows.Count>0)
            {
                var sell=rows[0]!.DeepClone();sell["ordId"]=peer.ExitOrderId;sell["clOrdId"]=peer.ExitClient;sell["side"]="sell";sell["tradeId"]="903";sell["sz"]=N(size);sell["accFillSz"]=N(size);sell["fillSz"]=N(size);sell["px"]="109.99";sell["avgPx"]="109.99";sell["fillPx"]="109.99";sell["fee"]=N(-fee);sell["feeCcy"]="USDC";sell["cTime"]=now;sell["uTime"]=now;sell["fillTime"]=now;sell["attachAlgoOrds"]=new JsonArray();
                if(p!="/api/v5/trade/order")rows.Insert(0,sell);
                else if(f.Paths.Last().Contains("clOrdId="+peer.ExitClient,StringComparison.Ordinal)||f.Paths.Last().Contains("ordId="+peer.ExitOrderId,StringComparison.Ordinal))b["data"]=new JsonArray(sell);
            }
            if(p=="/api/v5/account/balance")
            {
                var quote=b["data"]![0]!["details"]![0]!;foreach(string field in new[]{"cashBal","availBal","eq"})quote[field]=N(cash);
                var details=new JsonArray(quote.DeepClone());if(residual>0){var inventory=quote.DeepClone();inventory["ccy"]="BTC";foreach(string field in new[]{"cashBal","availBal","eq"})inventory[field]=N(residual);details.Add(inventory);}b["data"]![0]!["details"]=details;
            }
        };
    }
    private static void InstallOrdinaryPair(OperationalPeer f,TradingPeer peer,decimal fee)
    {
        TradingEconomicsFixture.Install(f,peer,peer.Size,fee,true,true);LivePending(f,peer,peer.GeneratedAlgoId);var basis=f.Mutate!;
        string N(decimal n)=>n.ToString("0.############################",CultureInfo.InvariantCulture);
        f.Mutate=(p,b)=>
        {
            basis(p,b);
            if(p=="/api/v5/account/config")b["data"]![0]!["feeType"]="0";
            if(p.StartsWith("/api/v5/account/bills",StringComparison.Ordinal)&&b["data"]!.AsArray().Count>0)
            {
                var anchor=b["data"]![0]!;anchor["balChg"]=N(peer.Size-fee);
                var counter=anchor.DeepClone();counter["billId"]="902";counter["ccy"]="USDC";counter["fee"]="0";counter["balChg"]=N(-peer.Size*110);
                b["data"]!.AsArray().Insert(0,counter);
            }
        };
    }
}
