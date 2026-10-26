using Apex.Backtester.Data;
using Apex.Backtester.Sim;
using Apex.Core.Model;
using Apex.Core.Engine;
using Apex.Core.Risk;
using Xunit;

namespace Apex.Backtester.Tests;

public class AccountingFxTests
{
    internal static readonly DateTime T = new(2024, 1, 2, 12, 0, 0, DateTimeKind.Utc);
    internal static BarSeries Bars(string symbol, params (DateTime t, double p)[] rows) =>
        BarSeries.FromBars(symbol, Tf.H1, rows.Select(x => new Bar(x.t, x.p, x.p, x.p, x.p, 1)).ToList());
    internal static SimWorld World(CostScenario cost, string ccy = "EUR")
    {
        var d = new InstrumentDef { Symbol = "TEST", QuoteCcy = ccy, PipSize = 1, LotSize = 100,
            CommissionUsdPerLot = 35, VolumeMin = 1, VolumeStep = 1, Spread = 0, FinancingRate = 0 };
        var fx = new FxRates();
        fx.Add("EURUSD", Bars("EURUSD", (T.AddHours(-1), 2), (T, 2)));
        var w = new SimWorld(new[] { new SymbolData(d, Bars("TEST", (T, 100), (T.AddHours(1), 110)), null) }, fx, cost, 10000);
        w.SetTime(T);
        return w;
    }
    internal static long Open(SimWorld w, string symbol = "TEST", double units = 10) =>
        w.OpenMarket(new OrderRequest { Symbol = symbol, Direction = TradeDirection.Long, VolumeUnits = units,
            StopLossPips = 50, TakeProfitPips = 100, Label = "APEX" }).PositionId!.Value;

    // Catches one-side equity vs roundtrip PositionInfo and entry fees delayed until exit.
    [Theory][InlineData(1)][InlineData(1.5)][InlineData(2)]
    public void EquityEqualsBalancePlusPositionNetForCommissionAndFx(double multiplier)
    {
        var w = World(new CostScenario { CommissionMult = multiplier, SlippageMult = 0 });
        Open(w);
        Assert.Equal(10000 - 3.5 * multiplier, w.Balance, 8);
        Assert.Equal(w.Balance + w.GetPositions().Sum(p => p.NetProfit), w.GetAccount().Equity, 8);
        Assert.Equal(10000 - 7 * multiplier, w.GetAccount().Equity, 8);
    }

    // Independent arithmetic: entry=3.5; close4 gross80 fee1.4; close6 gross120 fee2.1.
    [Fact]
    public void PartialThenFullCashflowsAgreeWithSingleFinalRecord()
    {
        var w = World(new CostScenario { SlippageMult = 0 });
        var closed = new List<ClosedPositionInfo>(); w.OnClosed = closed.Add;
        long id = Open(w); w.SetTime(T.AddHours(1)); w.ClosePosition(id, 4);
        Assert.Equal(10075.1, w.Balance, 8);
        Assert.Empty(closed);
        Assert.Equal(w.Balance + w.GetPositions().Single().NetProfit, w.GetAccount().Equity, 8);
        w.ClosePosition(id, null);
        Assert.Equal(10193, w.Balance, 8);
        var record = Assert.Single(closed);
        Assert.Equal(193, record.NetProfit, 8); Assert.Equal(-7, record.Commission, 8);
        Assert.Equal(200, record.GrossProfit, 8);
        Assert.Equal(w.Balance, w.GetAccount().Equity, 8);
    }

    [Theory][InlineData(1)][InlineData(1.5)][InlineData(2)]
    public void CatalogFloorIsSharedBySizingEntryAndEveryReduction(double multiplier)
    {
        var d = Catalog.Get("EURUSD");
        var w = new SimWorld(new[] { new SymbolData(d, Bars("EURUSD", (T, 1)), null) }, new FxRates(),
            new CostScenario { CommissionMult = multiplier, SpreadMult = 0, SlippageMult = 0 }, 10000);
        w.SetTime(T);
        var spec = w.GetSpec("EURUSD")!;
        Assert.Equal(0.5 * multiplier, spec.CommissionOneSide(1000, 1), 8);
        var size = PositionSizer.Size(spec, 20, 0.01, 1, 0, 0);
        Assert.True(size.Ok); Assert.True(size.RiskAmount <= 20);
        Assert.Equal(multiplier, size.CommissionRoundTrip, 8);
        var closed = new List<ClosedPositionInfo>(); w.OnClosed = closed.Add;
        long id = Open(w, "EURUSD", 2000);
        Assert.Equal(10000 - 0.5 * multiplier, w.Balance, 8);
        w.ClosePosition(id, 1000); w.ClosePosition(id, null);
        Assert.Equal(10000 - 1.5 * multiplier, w.Balance, 8);
        Assert.Equal(-1.5 * multiplier, Assert.Single(closed).Commission, 8);
    }

    [Theory][InlineData("missing")][InlineData("future")][InlineData("stale")][InlineData("nan")][InlineData("infinity")][InlineData("zero")]
    public void InvalidFxRejectsInsteadOfInventingOne(string kind)
    {
        var fx = new FxRates();
        if (kind != "missing") fx.Add("EURUSD", Bars("EURUSD",
            (kind == "future" ? T : kind == "stale" ? T.AddDays(-10) : T.AddHours(-1),
                kind == "nan" ? double.NaN : kind == "infinity" ? double.PositiveInfinity : kind == "zero" ? 0 : 2)));
        Assert.ThrowsAny<InvalidOperationException>(() => fx.ToUsd("EUR", T));
    }
    [Fact] public void SameCurrencyRequiresNoExternalRate() => Assert.Equal(1, new FxRates().ToUsd("USD", T));
    [Theory][InlineData(double.NaN)][InlineData(double.PositiveInfinity)][InlineData(-1)][InlineData(0)][InlineData(1.5)]
    public void InvalidOpenVolumeDoesNotCorruptCashOrPositions(double volume)
    {
        var w = World(CostScenario.Base, "USD");
        var outcome = w.OpenMarket(new OrderRequest { Symbol="TEST", Direction=TradeDirection.Long, VolumeUnits=volume, StopLossPips=50, TakeProfitPips=100 });
        Assert.Equal(OrderStatus.Rejected,outcome.Status); Assert.Empty(w.GetPositions()); Assert.Equal(10000,w.Balance);
    }
    [Theory][InlineData(double.NaN)][InlineData(-1)][InlineData(0)][InlineData(double.PositiveInfinity)]
    public void InvalidCostAssumptionsCannotCreateProfits(double multiplier) =>
        Assert.ThrowsAny<ArgumentException>(() => World(new CostScenario { CommissionMult=multiplier }));

    [Fact] public void ConvertedFxMustRemainFiniteAndPositive()
    {
        var fx = new FxRates(); fx.Add("USDCAD", Bars("USDCAD", (T.AddHours(-1), double.Epsilon)));
        Assert.ThrowsAny<InvalidOperationException>(() => fx.ToUsd("CAD",T));
    }
    [Theory][InlineData("base",9998.955)][InlineData("elevated",9998.41)][InlineData("double",9997.85)]
    public void WholeCostScenariosMatchExecutableLiquidationAndFinalCash(string scenario,double expected)
    {
        var cost = scenario == "base" ? CostScenario.Base : scenario == "elevated" ? CostScenario.Elevated : CostScenario.Double;
        var w = new SimWorld(new[] { new SymbolData(Catalog.Get("EURUSD"),Bars("EURUSD",(T,1)),null) },new FxRates(),cost,10000);
        w.SetTime(T); long id=Open(w,"EURUSD",1000);
        Assert.Equal(expected,w.GetAccount().Equity,8);
        w.ClosePosition(id,null); Assert.Equal(expected,w.Balance,8);
    }
    [Fact] public void SwapIsAllocatedOnceAcrossPartialAndFinalCashflow()
    {
        var d=new InstrumentDef { Symbol="TEST", QuoteCcy="USD", PipSize=1, CommissionUsdPerLot=35, LotSize=100,
            VolumeMin=1, VolumeStep=1, FinancingRate=0.365 };
        var w=new SimWorld(new[] { new SymbolData(d,Bars("TEST",(T,100),(T.AddDays(1),100)),null) },new FxRates(),new CostScenario { SlippageMult=0 },10000);
        ClosedPositionInfo? closed=null; w.OnClosed=x=>closed=x;
        w.SetTime(T); long id=Open(w); w.ApplyFinancing(T); w.SetTime(T.AddDays(1)); w.ApplyFinancing(w.Now); w.ApplyFinancing(w.Now);
        w.ClosePosition(id,4); Assert.Equal(9994.7,w.Balance,8);
        Assert.Equal(w.Balance+w.GetPositions().Single().NetProfit,w.GetAccount().Equity,8);
        w.ClosePosition(id,null); Assert.Equal(9992,w.Balance,8); Assert.Equal(-1,closed!.Swap,8); Assert.Equal(-8,closed.NetProfit,8);
    }
    [Fact] public void SimulatorCannotOpenWithMissingFx()
    {
        var d = new InstrumentDef { Symbol = "TEST", QuoteCcy = "EUR", PipSize = 1, VolumeMin = 1 };
        var w = new SimWorld(new[] { new SymbolData(d, Bars("TEST", (T, 100)), null) }, new FxRates(), CostScenario.Base, 10000);
        w.SetTime(T);
        Assert.ThrowsAny<InvalidOperationException>(() => Open(w));
        Assert.Equal(0, w.OpenCount); Assert.Equal(10000, w.Balance);
    }
}
