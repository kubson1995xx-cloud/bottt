using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Engine;
using Apex.Core.Naval;
using Xunit;

namespace Apex.Tests;

public class NavalCashCoordinatorTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public void NavalCash_ConcurrentFleetReservations_FourDistinctRaceHasOnlyThreeSlots(bool twoAccounts)
    {
        using var f=new NavalCashFixture(twoAccounts:twoAccounts);f.Run();using var barrier=new Barrier(4);
        var results=new NavalRiskResult[4];string[] symbols={"BTC","ETH","SOL","XRP"};
        Parallel.For(0,4,n=>{barrier.SignalAndWait();results[n]=f.Reserve(symbols[n],n<2?NavalFleet.Alpha:NavalFleet.Bravo,1,twoAccounts&&n>=2?1:0);});
        Assert.Equal(3,results.Count(r=>r.Status==NavalMutationStatus.Applied));Assert.Equal(3,f.View.ActiveCount);Assert.Equal(300.690m,f.View.Held);
    }
    [Fact] public void NavalCash_ConcurrentFleetReservations_BravoCannotUseThirdSlot()
    {
        using var f=new NavalCashFixture();f.Run();Assert.Equal(NavalMutationStatus.Applied,f.Reserve(fleet:NavalFleet.Bravo,q:1).Status);
        Assert.Equal(NavalMutationStatus.Applied,f.Reserve("ETH",NavalFleet.Bravo,1).Status);
        Assert.Equal(NavalMutationStatus.Refused,f.Reserve("SOL",NavalFleet.Bravo,1).Status);Assert.Equal(2,f.View.ActiveCount);
    }
    [Fact] public void NavalCash_SharedExposureAndFees_MarketBreachInOneAccountCannotHideBehindOtherCash()
    {
        using var f=new NavalCashFixture(twoAccounts:true,fees:new NavalFeeSchedule(0,false,.001m,true));f.Run();
        var r=f.C.ForFleet(f.A[0],NavalFleet.Alpha).TryReserve(f.Intent(q:40) with{StopPrice=99.99m});
        Assert.Equal(NavalMutationStatus.Applied,r.Status);f.C.Confirm(f.Receipt(NavalEvidenceKind.Confirm,fills:new[]{f.Fill(r,40,100,0)}));
        f.Marks["BTC"]=30000;f.Reconcile();Assert.Equal(NavalMutationStatus.Refused,f.Start().Status);
        Assert.Equal("STOP",f.View.Mode);Assert.Equal("ACCOUNT_NOTIONAL_BREACH",f.View.Reason);
    }
    [Fact] public void NavalCash_SharedExposureAndFees_CentralNotionalAndRounding()
    {
        using var f=new NavalCashFixture(twoAccounts:true,limits:new NavalCashLimits(MaxLeverage:.5m));f.Run();
        Assert.Equal(NavalMutationStatus.Applied,f.C.ForFleet(f.A[0],NavalFleet.Alpha).TryReserve(f.Intent(q:20) with{StopPrice=99.9m}).Status);
        Assert.Equal("CENTRAL_NOTIONAL",f.C.ForFleet(f.A[1],NavalFleet.Bravo).TryReserve(f.Intent("ETH",NavalFleet.Bravo,40,1) with{StopPrice=99.9m}).Code);
        using var g=new NavalCashFixture();g.Run();Assert.Equal(NavalMutationStatus.Applied,g.Reserve(q:4.0009m).Status);Assert.Equal(4,g.View.Reservations[0].Quantity);
    }
    [Fact] public void NavalCash_TamperedIdentityAndLimits_SignalTimingAndWrongFleetCannotPoisonSignal()
    {
        using var f=new NavalCashFixture();f.Run();var i=f.Intent();
        Assert.Equal("SIGNAL_BINDING",f.C.ForFleet(f.A[0],NavalFleet.Bravo).TryReserve(i).Code);
        Assert.Equal(NavalMutationStatus.Applied,f.C.ForFleet(f.A[0],NavalFleet.Alpha).TryReserve(i).Status);
        using var g=new NavalCashFixture();g.Clock.Now=g.Clock.Now.AddDays(6);g.Run();Assert.Equal("SIGNAL_TIME",g.Reserve().Code);
    }
    [Fact] public void NavalCash_SharedExposureAndFees_CombinedBudgetNotMultipliedAcrossAccounts()
    {
        using var f=new NavalCashFixture(twoAccounts:true,limits:new NavalCashLimits(DailyPct:.8m));f.Run();
        Assert.Equal(NavalMutationStatus.Applied,f.Reserve().Status);
        Assert.Equal(NavalMutationStatus.Refused,f.Reserve("ETH",NavalFleet.Bravo,4,1).Status);
        Assert.False(f.View.DailyLatch);Assert.Equal(1,f.View.ActiveCount);
    }
    [Theory] [InlineData("gap")] [InlineData("rollback")] [InlineData("oldGeneration")]
    public void NavalCash_EvidenceOrderingAndFinality_SourceWatermarksCannotRefresh(string problem)
    {
        using var f=new NavalCashFixture();f.Run();var r=f.Reserve();var s=f.Owner.Read().Naval!;
        var e=new NavalEvidenceReceipt(f.C.OwnerToken,f.D.Id,"bad",problem=="oldGeneration"?0:f.Generation,
            problem=="gap"?s.Sequence+2:s.Sequence,f.Clock.Now,f.Clock.Now,NavalEvidenceKind.Reject,true,true,f.Marks,
            cash:s.Accounts,observations:new[]{new NavalObservation(r.ReservationId!,0,0)},terminalIds:new[]{r.ReservationId!});
        Assert.Equal(NavalMutationStatus.Stopped,f.C.Reject(e).Status);Assert.Equal(400.920m,f.View.Held);Assert.False(f.View.Ready);
        Assert.Equal(s.Sequence,f.Owner.Read().Naval!.Sequence);
    }
    [Fact] public void NavalCash_LatchesAndCoherentEquity_DrawdownSurvivesRestartDayAndReductions()
    {
        using var f=new NavalCashFixture();f.Run();var intent=f.Intent(q:90) with{StopPrice=99.9m};var r=f.C.ForFleet(f.A[0],NavalFleet.Alpha).TryReserve(intent);
        Assert.Equal(NavalMutationStatus.Applied,r.Status);f.C.Confirm(f.Receipt(NavalEvidenceKind.Confirm,fills:new[]{f.Fill(r,90,100,9)}));
        f.Marks["BTC"]=94;f.Reconcile();Assert.True(f.View.DrawdownLatch);Assert.True(f.View.DailyLatch);
        f.Restart();f.Clock.Now=f.Clock.Now.AddDays(1);f.Marks["BTC"]=100;f.Reconcile();Assert.False(f.View.DailyLatch);Assert.True(f.View.DrawdownLatch);
        Assert.Equal(NavalMutationStatus.Refused,f.Start().Status);
        Assert.Equal(NavalMutationStatus.Applied,f.C.Confirm(f.Receipt(NavalEvidenceKind.Confirm,fills:new[]{f.Fill(r,90,100,9,true,"reduce")})).Status);
        Assert.Equal(9982,f.View.Cash);
    }
    [Fact] public void NavalCash_LatchesAndCoherentEquity_NoHybridPhantomPeak()
    {
        using var f=new NavalCashFixture();f.Run();var r=f.C.ForFleet(f.A[0],NavalFleet.Alpha).TryReserve(f.Intent(q:40) with{StopPrice=99.9m});
        var t=f.C.ForFleet(f.A[0],NavalFleet.Bravo).TryReserve(f.Intent("ETH",NavalFleet.Bravo,40) with{StopPrice=99.9m});
        f.C.Confirm(f.Receipt(NavalEvidenceKind.Confirm,fills:new[]{f.Fill(r,40,100,4),f.Fill(t,40,100,4,trade:"buy-eth")}));
        f.Marks["BTC"]=150;f.Marks["ETH"]=50;Assert.Equal(NavalMutationStatus.Applied,f.Reconcile().Status);
        Assert.False(f.View.DrawdownLatch);Assert.False(f.View.DailyLatch);Assert.Equal(10000,f.Owner.Read().Naval!.PeakEquity);
    }
    [Fact] public void NavalCash_EvidenceOrderingAndFinality_ConflictingEvidenceIsUnknownAndRetained()
    {
        using var f=new NavalCashFixture();f.Run();var r=f.Reserve();
        var fill=f.Fill(r,5,101,.505m);
        Assert.Equal(NavalMutationStatus.Stopped,f.C.Confirm(f.Receipt(NavalEvidenceKind.Confirm,fills:new[]{fill})).Status);
        Assert.Equal("UNKNOWN",f.View.Quality);Assert.Null(f.View.LiquidationEquity);
        Assert.Contains("101",string.Join(" ",f.Owner.Read().Naval!.Conflicts));Assert.Equal(400.920m,f.View.Held);
    }
    // Catches an always-refuse implementation and cash/cost-basis double posting.
    [Fact] public void NavalCash_SharedExposureAndFees_ExactNumericalRoundTrip()
    {
        using var f = new NavalCashFixture(); f.Run();
        var r = f.Reserve(); Assert.Equal(NavalMutationStatus.Applied,r.Status);
        Assert.Equal(400.400m,f.View.Held - .520m); Assert.Equal(.520m,f.View.Reservations.Single().CloseFeeHold);
        Assert.Equal(40.760m,f.View.Reservations.Single().RiskHold);
        var fill = f.Fill(r,4,100,.400m); var e = f.Receipt(NavalEvidenceKind.Confirm,fills:new[]{fill});
        Assert.Equal(NavalMutationStatus.Applied,f.C.Confirm(e).Status);
        Assert.Equal(9599.600m,f.View.Cash); Assert.Equal(9599.080m,f.View.Free);
        Assert.Equal(NavalMutationStatus.Duplicate,f.C.Confirm(e).Status);
        Assert.Equal(9599.600m,f.View.Cash);
        var close = f.Fill(r,4,110,.440m,true,"sell");
        Assert.Equal(NavalMutationStatus.Applied,f.C.Confirm(f.Receipt(NavalEvidenceKind.Confirm,fills:new[]{close})).Status);
        Assert.Equal(10039.160m,f.View.Cash);
        Assert.Equal(1,f.View.ActiveCount); // Missing terminal coverage still owns slot.
        Assert.Equal(NavalMutationStatus.Applied,f.C.Terminal(f.Receipt(NavalEvidenceKind.Terminal,final:true,complete:true,terminal:new[]{r.ReservationId!})).Status);
        Assert.Equal(0,f.View.ActiveCount);
        var alpha=Assert.Single(f.View.Fleets,v=>v.Fleet==NavalFleet.Alpha);
        Assert.Equal(39.160m,alpha.NetQuoteCashflow);Assert.Equal(39.160m,alpha.NetContribution);Assert.Equal(0,alpha.AllocatedCost);Assert.Equal(2,alpha.Fills.Count);
        Assert.Equal(0,Assert.Single(f.View.Fleets,v=>v.Fleet==NavalFleet.Bravo).NetQuoteCashflow);
    }
    [Fact] public void NavalCash_ConcurrentFleetReservations_OneExactSymbolOwner()
    {
        using var f = new NavalCashFixture(); f.Run(); using var barrier = new Barrier(2);
        var results = new NavalRiskResult[2];
        Parallel.Invoke(()=>{barrier.SignalAndWait();results[0]=f.Reserve();},
            ()=>{barrier.SignalAndWait();results[1]=f.Reserve(fleet:NavalFleet.Bravo);});
        Assert.Single(results,x=>x.Status==NavalMutationStatus.Applied); Assert.Equal(1,f.View.ActiveCount);
        Assert.Equal(400.920m,f.View.Held);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void NavalCash_ConcurrentFleetReservations_GlobalAndBravoSlots(bool twoAccounts)
    {
        using var f = new NavalCashFixture(twoAccounts:twoAccounts); f.Run();
        var a=f.Reserve(q:1); var b=f.Reserve("ETH",NavalFleet.Bravo,1,twoAccounts?1:0);
        var c=f.Reserve("SOL",NavalFleet.Bravo,1,twoAccounts?1:0);
        Assert.All(new[]{a,b,c},x=>Assert.Equal(NavalMutationStatus.Applied,x.Status));
        f.C.MarkUncertain(b.ReservationId!,"timeout"); f.Run();
        Assert.Equal(NavalMutationStatus.Refused,f.Reserve("XRP",NavalFleet.Bravo,1,twoAccounts?1:0).Status);
        Assert.Equal(3,f.View.ActiveCount);
    }
    [Fact] public void NavalCash_SharedExposureAndFees_AccountLocalCannotBorrow()
    {
        using var f=new NavalCashFixture(twoAccounts:true,firstAllocation:400.400m); f.Run();
        Assert.Equal(NavalMutationStatus.Refused,f.Reserve().Status); Assert.Equal(0,f.View.Held);
        Assert.Equal(NavalMutationStatus.Applied,f.Reserve(account:1).Status);
    }
    [Fact] public void NavalCash_SharedExposureAndFees_ExactLotAndTombstones()
    {
        using var f=new NavalCashFixture(twoAccounts:true,firstAllocation:400.920m); f.Run();
        var i=f.Intent(); var r=f.C.ForFleet(f.A[0],NavalFleet.Alpha).TryReserve(i);
        Assert.Equal(NavalMutationStatus.Applied,r.Status); Assert.Equal(0,f.View.Accounts[0].Free);
        Assert.Equal(NavalMutationStatus.Duplicate,f.C.ForFleet(f.A[0],NavalFleet.Alpha).TryReserve(i).Status);
        Assert.Equal(NavalMutationStatus.Refused,f.C.ForFleet(f.A[0],NavalFleet.Alpha).TryReserve(i with{CommandId="new"}).Status);
        using var g=new NavalCashFixture(twoAccounts:true,firstAllocation:400.920m);g.Run();
        Assert.Equal(NavalMutationStatus.Refused,g.Reserve(q:4.001m).Status);
    }
    [Fact] public void NavalCash_SharedExposureAndFees_BaseFeesAndOversell()
    {
        using var f=new NavalCashFixture(fees:new NavalFeeSchedule(.001m,true,.001m,true));f.Run(); var r=f.Reserve();
        Assert.Equal(NavalMutationStatus.Applied,f.C.Confirm(f.Receipt(NavalEvidenceKind.Confirm,
            fills:new[]{f.Fill(r,4,100,.004m,feeCurrency:"BTC")})).Status);
        Assert.Equal(9600,f.View.Cash);Assert.Equal(3.996m,f.View.Reservations[0].Inventory);
        Assert.Equal(NavalMutationStatus.Stopped,f.C.Confirm(f.Receipt(NavalEvidenceKind.Confirm,
            fills:new[]{f.Fill(r,3.996m,110,.003996m,true,"bad","BTC")})).Status);
        Assert.Equal(3.996m,f.View.Reservations[0].Inventory);
    }
    [Fact] public void NavalCash_EvidenceOrderingAndFinality_TimeoutAndPartialCancellation()
    {
        using var f=new NavalCashFixture();f.Run();var r=f.Reserve();
        Assert.Equal(NavalMutationStatus.Refused,f.C.Confirm(f.Receipt(NavalEvidenceKind.Acknowledgement)).Status);
        f.C.MarkUncertain(r.ReservationId!,"timeout");Assert.Equal(400.920m,f.View.Held);
        Assert.Equal(NavalMutationStatus.Refused,f.Reserve("ETH").Status);
        f.Run();var fill=f.Fill(r,2,100,.2m);
        Assert.Equal(NavalMutationStatus.Applied,f.C.Confirm(f.Receipt(NavalEvidenceKind.Confirm,fills:new[]{fill})).Status);
        Assert.Equal(9799.8m,f.View.Cash);Assert.Equal(2,f.View.Reservations[0].Pending);
        Assert.Equal(NavalMutationStatus.Stopped,f.C.Terminal(f.Receipt(NavalEvidenceKind.Terminal,final:true,
            terminal:new[]{r.ReservationId!})).Status);
        Assert.Equal(2,f.View.Reservations[0].Pending);
        Assert.Equal(NavalMutationStatus.Applied,f.C.Terminal(f.Receipt(NavalEvidenceKind.Terminal,final:true,complete:true,
            terminal:new[]{r.ReservationId!})).Status);
        Assert.Equal(0,f.View.Reservations[0].Pending);Assert.Equal(2,f.View.Reservations[0].Inventory);Assert.Equal(1,f.View.ActiveCount);
    }
    [Fact] public void NavalCash_EvidenceOrderingAndFinality_ReplayConflictAndFreshness()
    {
        using var f=new NavalCashFixture();f.Run();var r=f.Reserve();var fill=f.Fill(r,1,100,.1m);
        var receipt=f.Receipt(NavalEvidenceKind.Confirm,fills:new[]{fill});f.C.Confirm(receipt);
        f.Clock.Now=f.Clock.Now.AddSeconds(31);Assert.False(f.View.Ready);
        Assert.Equal(NavalMutationStatus.Duplicate,f.C.Confirm(receipt).Status);Assert.False(f.View.Ready);
        f.Clock.Now=f.Clock.Now.AddSeconds(-31);
        Assert.Equal(NavalMutationStatus.Stopped,f.C.Confirm(f.Receipt(NavalEvidenceKind.Confirm,fills:new[]{fill with{Price=101}})).Status);
        Assert.Equal(9899.9m,f.View.Cash);Assert.True(f.View.EmergencyLatch);
        Assert.Contains("\"Price\":101",string.Join(" ",f.Owner.Read().Naval!.Conflicts));
        f.Reconcile();Assert.Equal("UNKNOWN",f.View.Quality);Assert.Null(f.View.LiquidationEquity);
    }
    [Theory] [InlineData(-31)] [InlineData(1)]
    public void NavalCash_EvidenceOrderingAndFinality_StaleFutureCannotRelease(int seconds)
    {
        using var f=new NavalCashFixture();f.Run();var r=f.Reserve();
        var e=f.Receipt(NavalEvidenceKind.Reject,complete:true,final:true,terminal:new[]{r.ReservationId!},capture:f.Clock.Now.AddSeconds(seconds));
        Assert.Equal(NavalMutationStatus.Stopped,f.C.Reject(e).Status);Assert.Equal(400.920m,f.View.Held);
    }
    [Fact] public void NavalCash_EvidenceOrderingAndFinality_ZeroFillRejectReleasesOnce()
    {
        using var f=new NavalCashFixture();f.Run();var r=f.Reserve();
        var e=f.Receipt(NavalEvidenceKind.Reject,complete:true,final:true,terminal:new[]{r.ReservationId!});
        Assert.Equal(NavalMutationStatus.Applied,f.C.Reject(e).Status);Assert.Equal(0,f.View.Held);
        Assert.Equal(NavalMutationStatus.Duplicate,f.C.Reject(e).Status);Assert.Equal(10000,f.View.Cash);
    }
    [Fact] public void NavalCash_TamperedIdentityAndLimits_DenyAuthorityAndLimits()
    {
        foreach(var l in new[]{new NavalCashLimits(TradePct:.5001m),new NavalCashLimits(DailyPct:1.5001m),new NavalCashLimits(DrawdownPct:5.0001m),
            new NavalCashLimits(MaxLeverage:1.0001m),new NavalCashLimits(CentralMax:4),new NavalCashLimits(BravoMax:3)})
            Assert.ThrowsAny<Exception>(()=>{using var f=new NavalCashFixture(limits:l);});
        foreach(var env in new[]{NavalEnvironment.OkxDemo,NavalEnvironment.Live,(NavalEnvironment)999})
            Assert.ThrowsAny<Exception>(()=>{using var f=new NavalCashFixture(environment:env);});
        using var g=new NavalCashFixture();g.Run();g.Reserve();
        Assert.ThrowsAny<Exception>(()=>g.C.ForFleet(g.A[0] with{AccountId="alias"},NavalFleet.Alpha));
        Assert.ThrowsAny<Exception>(()=>g.C.ForFleet(g.A[0],(NavalFleet)4));Assert.Equal(400.920m,g.View.Held);
    }
    [Fact] public void NavalCash_LatchesAndCoherentEquity_ConfirmationUsesWholeReceiptMarks()
    {
        using var f=new NavalCashFixture(fees:new NavalFeeSchedule(0,false,0,false));f.Run();
        var r=f.C.ForFleet(f.A[0],NavalFleet.Alpha).TryReserve(f.Intent(q:50) with{StopPrice=99.9m});
        f.Marks["BTC"]=97;
        Assert.Equal(NavalMutationStatus.Applied,f.C.Confirm(f.Receipt(NavalEvidenceKind.Confirm,fills:new[]{f.Fill(r,50,100,0)})).Status);
        Assert.True(f.View.DailyLatch);Assert.Equal(9850,f.View.LiquidationEquity);
    }
    [Fact] public void NavalCash_LatchesAndCoherentEquity_ExactDailyAndDrawdownThresholds()
    {
        using var f=new NavalCashFixture(fees:new NavalFeeSchedule(0,false,0,false));f.Run();
        var r=f.C.ForFleet(f.A[0],NavalFleet.Alpha).TryReserve(f.Intent(q:50) with{StopPrice=99.9m});
        Assert.Equal(NavalMutationStatus.Applied,r.Status);f.C.Confirm(f.Receipt(NavalEvidenceKind.Confirm,fills:new[]{f.Fill(r,50,100,0)}));
        f.Marks["BTC"]=97;f.Reconcile();Assert.Equal(9850,f.View.LiquidationEquity);Assert.True(f.View.DailyLatch);Assert.False(f.View.DrawdownLatch);
        f.Marks["BTC"]=90;f.Reconcile();Assert.Equal(9500,f.View.LiquidationEquity);Assert.True(f.View.DrawdownLatch);
    }
    [Fact] public void NavalCash_SharedExposureAndFees_BaseSellFeeConsumesInventoryNotQuote()
    {
        using var f=new NavalCashFixture(fees:new NavalFeeSchedule(.001m,true,.001m,true));f.Run();var r=f.Reserve();
        f.C.Confirm(f.Receipt(NavalEvidenceKind.Confirm,fills:new[]{f.Fill(r,4,100,.004m,feeCurrency:"BTC")}));
        f.C.Confirm(f.Receipt(NavalEvidenceKind.Confirm,fills:new[]{f.Fill(r,3,110,.003m,true,"sell-base","BTC")}));
        Assert.Equal(9930,f.View.Cash);Assert.Equal(.993m,f.View.Reservations[0].Inventory);Assert.Equal(0,f.View.Reservations[0].CloseFeeHold);
    }
    [Fact] public void NavalCash_LatchesAndCoherentEquity_DailyAndDrawdownSticky()
    {
        using var f=new NavalCashFixture();f.Run();var r=f.Reserve(q:4);f.C.Confirm(f.Receipt(NavalEvidenceKind.Confirm,fills:new[]{f.Fill(r,4,100,.4m)}));
        f.Marks["BTC"]=60;f.Reconcile();Assert.True(f.View.DailyLatch);Assert.False(f.View.DrawdownLatch);
        f.Marks["BTC"]=100;f.Reconcile();Assert.True(f.View.DailyLatch);Assert.Equal(NavalMutationStatus.Refused,f.Start().Status);
        f.Clock.Now=f.Clock.Now.AddDays(1);f.Reconcile();Assert.False(f.View.DailyLatch);
        f.Marks["BTC"]=1;f.Reconcile();Assert.True(f.View.DailyLatch);
    }
    [Fact] public void NavalCash_EvidenceOrderingAndFinality_LateFillRequiresCompleteReconciliation()
    {
        using var f=new NavalCashFixture();f.Run();var r=f.Reserve();var fill=f.Fill(r,1,100,.1m) with{TradeUtc=f.Clock.Now.AddDays(-1)};
        Assert.Equal(NavalMutationStatus.Stopped,f.C.Confirm(f.Receipt(NavalEvidenceKind.Confirm,fills:new[]{fill})).Status);
        Assert.Equal(10000,f.View.Cash);Assert.Equal(400.920m,f.View.Held);
        var e=f.Receipt(NavalEvidenceKind.Reconcile,complete:true,fills:new[]{fill},cash:new[]{new NavalCashAccount(f.A[0],10000,9899.9m)},observations:new[]{new NavalObservation(r.ReservationId!,1,3)});
        Assert.Equal(NavalMutationStatus.Applied,f.C.Reconcile(e).Status);Assert.Equal(9899.9m,f.View.Cash);Assert.False(f.View.EmergencyLatch);
    }
    [Fact] public void NavalCash_EvidenceOrderingAndFinality_LateFillRecoveredOnceAfterNewGeneration()
    {
        using var f=new NavalCashFixture();f.Run();var r=f.Reserve();var fill=f.Fill(r,1,100,.1m) with{TradeUtc=f.Clock.Now.AddDays(-1)};
        f.Generation++;f.Sequence=0;
        var e=f.Receipt(NavalEvidenceKind.Reconcile,complete:true,fills:new[]{fill},
            cash:new[]{new NavalCashAccount(f.A[0],10000,9899.9m)},observations:new[]{new NavalObservation(r.ReservationId!,1,3)});
        Assert.Equal(NavalMutationStatus.Applied,f.C.Reconcile(e).Status);Assert.Equal(9899.9m,f.View.Cash);
        Assert.Equal(NavalMutationStatus.Duplicate,f.C.Reconcile(e).Status);Assert.Equal(9899.9m,f.View.Cash);
    }
}

internal sealed class NavalTestClock : INavalClock { internal DateTimeOffset Now=new(2026,1,1,12,0,0,TimeSpan.Zero);public DateTimeOffset UtcNow=>Now; }
internal sealed class NavalTestStore : IKeyValueStore
{
    internal string? Value;internal string? Fault; public string? Get(string key)=>Value;
    public void Set(string key,string value){if(Fault=="before")throw new IOException("before");Value=value;if(Fault=="after")throw new IOException("after");}
    public void Flush(){if(Fault=="flush")throw new IOException("flush");}
}
internal sealed class NavalCashFixture : IDisposable
{
    internal string DirectoryPath=Path.Combine(Environment.GetEnvironmentVariable("TMPDIR")??Path.GetTempPath(),"naval-"+Guid.NewGuid().ToString("N"));
    internal NavalTestStore Store=new(); internal NavalTestClock Clock=new();internal NavalCoreStateOwner Owner=null!;
    internal NavalPortfolioCoordinator C=null!;internal NavalPortfolioDefinition D;
    internal NavalAccountId[] A;internal long Generation=1,Sequence;internal int Command;
    internal Dictionary<string,decimal> Marks=new(){{"BTC",100},{"ETH",100},{"SOL",100},{"XRP",100}};
    internal NavalPortfolioView View=>C.Snapshot();
    internal NavalCashFixture(bool twoAccounts=false,decimal firstAllocation=5000,NavalFeeSchedule? fees=null,NavalCashLimits? limits=null,NavalEnvironment environment=NavalEnvironment.Offline)
    {
        A=Enumerable.Range(0,twoAccounts?2:1).Select(i=>new NavalAccountId("SYNTHETIC",environment,"account-"+i,"USD")).ToArray();
        var signals=from a in A from fleet in new[]{NavalFleet.Alpha,NavalFleet.Bravo} from symbol in Marks.Keys from n in Enumerable.Range(0,10)
            select new NavalSignal($"s-{a.AccountId}-{fleet}-{symbol}-{n}",a,fleet,symbol,"synthetic-rule",Clock.Now.AddDays(-2),Clock.Now.AddDays(5));
        D=new(new("p","USD"),limits??new(),A.Select((a,i)=>new NavalSeed(a,twoAccounts?(i==0?firstAllocation:10000-firstAllocation):10000)),
            Marks.Keys.Select(s=>new NavalInstrument(s,s,"USD",.001m,.001m,.01m)),fees??new(),signals);
        Owner=new(DirectoryPath,Store,D); C=new(Owner,D,Clock);
    }
    internal NavalEntryIntent Intent(string symbol="BTC",NavalFleet fleet=NavalFleet.Alpha,decimal q=4,int account=0,int signal=0)=>
        new("c-"+Interlocked.Increment(ref Command),$"s-{A[account].AccountId}-{fleet}-{symbol}-{signal}",symbol,q,100,90,130);
    internal NavalRiskResult Reserve(string symbol="BTC",NavalFleet fleet=NavalFleet.Alpha,decimal q=4,int account=0,int signal=0)=>C.ForFleet(A[account],fleet).TryReserve(Intent(symbol,fleet,q,account,signal));
    internal NavalEvidenceReceipt Receipt(NavalEvidenceKind kind,bool complete=false,bool final=false,IEnumerable<NavalFill>? fills=null,IEnumerable<string>? terminal=null,
        DateTimeOffset? capture=null,IEnumerable<NavalCashAccount>? cash=null,IEnumerable<NavalObservation>? observations=null)
    {
        var state=Owner.Read().Naval ?? new NavalPortfolioState { Accounts=D.Members.Select(m=>new NavalCashAccount(m.Account,m.Allocation,m.Allocation)).ToArray() };var ids=(terminal??Array.Empty<string>()).ToArray();
        return new(C.OwnerToken,D.Id,"e-"+Generation+"-"+(++Sequence),Generation,Generation>state.Generation?1:state.Sequence+1,capture??Clock.Now,Clock.Now,kind,complete,final,Marks,fills,
            cash??state.Accounts,observations??state.Reservations.Where(NavalActive).Select(r=>new NavalObservation(r.Id,r.Inventory,ids.Contains(r.Id)?0:r.Pending)),ids);
    }
    internal static bool NavalActive(NavalReservation r)=>r.Inventory>0||r.Pending>0||!r.TerminalCoverage;
    internal NavalFill Fill(NavalRiskResult r,decimal q,decimal price,decimal fee,bool reduction=false,string trade="buy",string feeCurrency="USD")
    {var reservation=View.Reservations.Single(x=>x.Id==r.ReservationId);return new(reservation.Account,reservation.Intent.Instrument,trade,r.ReservationId!,reduction,q,price,fee,feeCurrency,Clock.Now);}
    internal NavalRiskResult Reconcile()=>C.Reconcile(Receipt(NavalEvidenceKind.Reconcile,complete:true));
    internal NavalRiskResult Start()=>C.Start(new(C.OwnerToken,View.DurableRevision,Generation,true));
    internal void Run(){Assert.Equal(NavalMutationStatus.Applied,Reconcile().Status);Assert.Equal(NavalMutationStatus.Applied,Start().Status);}
    internal void Restart(){Owner.Dispose();Owner=new(DirectoryPath,Store,D);C=new(Owner,D,Clock);Generation++;Sequence=0;}
    public void Dispose(){Owner?.Dispose();if(Directory.Exists(DirectoryPath))Directory.Delete(DirectoryPath,true);}
}
