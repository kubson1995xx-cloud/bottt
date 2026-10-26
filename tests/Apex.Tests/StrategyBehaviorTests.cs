using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Apex.Core.Adaptive;
using Apex.Core.Engine;
using Apex.Core.Indicators;
using Apex.Core.Management;
using Apex.Core.Model;
using Apex.Core.Regime;
using Apex.Core.Safety;
using Apex.Core.Strategies;
using Xunit;

namespace Apex.Tests;

// Controlled unit fixtures only, NEVER market research data. Indicators are calculated
// by real FeatureSet, not patched arrays. Mutations caught: direction inversion, >=/>
// threshold changes, current-bar channel inclusion, wrong ATR/TP, future indexing,
// and dropping real strategy exits before the manager closes the position.
public abstract class TwoSidedStrategyBehaviorSuite
{
    protected abstract StrategyId Family { get; }
    protected IStrategy Strategy => StrategyFixtures.Create(Family);

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void Positive_BuySell_ActualIndicatorsAndProtection(int side)
    {
        var s = Strategy;
        var input = StrategyFixtures.Input(s, side);
        var c = Assert.IsType<SignalCandidate>(s.Evaluate(input));
        Assert.Equal((TradeDirection)side, c.Direction);
        Assert.Equal(Family, c.Strategy);
        Assert.Equal(input.Signal.Close[input.SignalIndex], c.ReferencePrice);
        Assert.Equal(input.DecisionTimeUtc, c.DecisionTimeUtc);
        Assert.Equal(input.Signal.Bars.Times[input.SignalIndex], c.SignalBarTime);
        double atr = input.Signal.Atr(14)[input.SignalIndex];
        double expectedStop = s switch
        {
            TrendPullbackStrategy t => t.Params.StopAtr * atr,
            DonchianBreakoutStrategy d => d.Params.StopAtr * atr,
            MeanReversionStrategy m => m.Params.StopAtr * atr,
            MomentumImpulseStrategy m => Math.Max(m.Params.MinStopAtr * atr,
                Math.Min(m.Params.MaxStopAtr * atr, Math.Abs(c.ReferencePrice -
                    (side > 0 ? input.Signal.Low[input.SignalIndex] : input.Signal.High[input.SignalIndex])) + .2 * atr)),
            _ => throw new InvalidOperationException()
        };
        Assert.Equal(expectedStop, c.StopDistance, 10);
        if (s is MeanReversionStrategy mr)
            Assert.Equal(Math.Abs(input.Signal.Bollinger(mr.Params.BbPeriod, mr.Params.BbK).Middle[input.SignalIndex] - c.ReferencePrice), c.TakeProfitDistance!.Value, 10);
        else if (((StrategyBase)s).Exit.TakeProfitR is double tp)
            Assert.Equal(tp * expectedStop, c.TakeProfitDistance!.Value, 10);
        else Assert.Null(c.TakeProfitDistance); // Core, not strategy, supplies protective 3R.
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void Negative_UnknownChaosInsufficientAndNoPattern(int side)
    {
        var s = Strategy;
        var bars = StrategyFixtures.Pattern(Family, side, s.SignalTf);
        Assert.Null(s.Evaluate(StrategyFixtures.Input(s, side, bars, regime: MarketRegime.Chaotic)));
        Assert.Null(s.Evaluate(StrategyFixtures.Input(s, side, bars, regime: MarketRegime.Unknown)));
        Assert.Null(s.Evaluate(StrategyFixtures.Input(s, side, bars, index: s.RequiredSignalBars - 2)));
        Assert.Null(s.Evaluate(StrategyFixtures.Input(s, side, StrategyFixtures.Flat(s.SignalTf))));
        Assert.False(s.AppliesTo(AssetClass.Crypto));
        Assert.False(s.AppliesTo(AssetClass.Unknown));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void Boundary_ExactAdmissionThreshold(int side)
    {
        var s = Strategy;
        if (s is TrendPullbackStrategy t)
        {
            Assert.NotNull(s.Evaluate(StrategyFixtures.Input(s, side, adx: t.Params.FilterAdxMin)));
            Assert.Null(s.Evaluate(StrategyFixtures.Input(s, side, adx: Math.BitDecrement(t.Params.FilterAdxMin))));
        }
        else if (s is MeanReversionStrategy m)
        {
            Assert.NotNull(s.Evaluate(StrategyFixtures.Input(s, side, adx: Math.BitDecrement(m.Params.FilterAdxMax))));
            Assert.Null(s.Evaluate(StrategyFixtures.Input(s, side, adx: m.Params.FilterAdxMax)));
        }
        else if (s is DonchianBreakoutStrategy)
        {
            var bars = StrategyFixtures.Pattern(Family, side, s.SignalTf);
            // Exact prior-channel equality must not break out; current candle is excluded.
            double level = side > 0 ? bars.Take(bars.Count - 1).TakeLast(55).Max(b => b.High)
                                    : bars.Take(bars.Count - 1).TakeLast(55).Min(b => b.Low);
            bars[^1] = StrategyFixtures.Candle(bars[^1].OpenTime, 100, level);
            Assert.Null(s.Evaluate(StrategyFixtures.Input(s, side, bars)));
        }
        else if (s is MomentumImpulseStrategy m2)
        {
            var input = StrategyFixtures.Input(s, side);
            int i = input.SignalIndex;
            double ratio = (input.Signal.High[i] - input.Signal.Low[i]) / input.Signal.Atr(14)[i - 1];
            var exact = new MomentumImpulseStrategy(new MomentumImpulseParams { ImpulseAtr = ratio });
            var beyond = new MomentumImpulseStrategy(new MomentumImpulseParams { ImpulseAtr = ratio + 1e-9 });
            Assert.NotNull(exact.Evaluate(input));
            Assert.Null(beyond.Evaluate(input));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void Exit_RealRuleFeedsManager_NotEntryPattern(int side)
    {
        var s = Strategy;
        var noExit = StrategyFixtures.Input(s, side);
        Assert.False(s.CheckExit(noExit, (TradeDirection)side, 100, 2).ShouldExit);
        var bars = StrategyFixtures.ExitPattern(Family, side, s.SignalTf);
        var input = StrategyFixtures.Input(s, side, bars);
        var exit = s.CheckExit(input, (TradeDirection)side, 100, 2);
        Assert.True(exit.ShouldExit);
        Assert.NotEmpty(exit.Reason);
        if (s is MomentumImpulseStrategy)
            Assert.False(s.CheckExit(input, (TradeDirection)side, 100, 1).ShouldExit);
        var plan = new TradePlan { Direction = (TradeDirection)side, EntryPrice = 100,
            InitialRisk = 2, InitialStop = 100 - side * 2, BestPrice = 100,
            Exit = ((StrategyBase)s).Exit, Strategy = Family };
        var actions = PositionManagerRules.Evaluate(new ManagementInput { Plan = plan,
            Position = new PositionInfo { Direction = (TradeDirection)side, EntryPrice = 100, StopLoss = 100 - side * 2 },
            BarClose = input.Signal.Close[input.SignalIndex], BarHigh = input.Signal.High[input.SignalIndex],
            BarLow = input.Signal.Low[input.SignalIndex], BarTime = input.DecisionTimeUtc,
            CurrentAtr = 1, StrategyExit = exit });
        var action = Assert.Single(actions);
        Assert.Equal(ManagementActionKind.Close, action.Kind);
        Assert.Equal(exit.Reason, action.Reason);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void FuturePerturbation_EntryAndExitInvariant(int side)
    {
        var s = Strategy;
        foreach (var bars in new[] { StrategyFixtures.Pattern(Family, side, s.SignalTf), StrategyFixtures.ExitPattern(Family, side, s.SignalTf) })
        {
            var original = StrategyFixtures.Input(s, side, bars);
            var future = bars.ToList();
            for (int j = 1; j <= 25; j++) future.Add(StrategyFixtures.Candle(bars[^1].OpenTime + TimeSpan.FromTicks(s.SignalTf.Duration().Ticks * j), 1000 + j, 2000 - j));
            var changed = StrategyFixtures.Input(s, side, future, index: bars.Count - 1);
            Assert.Equal(JsonSerializer.Serialize(s.Evaluate(original)), JsonSerializer.Serialize(s.Evaluate(changed)));
            Assert.Equal(s.CheckExit(original, (TradeDirection)side, 100, 2).ShouldExit,
                s.CheckExit(changed, (TradeDirection)side, 100, 2).ShouldExit);
            Assert.Equal(original.SignalIndex, changed.Signal.IndexAtOrBefore(original.DecisionTimeUtc));
            Assert.Equal(original.SignalIndex - 1, changed.Signal.IndexAtOrBefore(original.DecisionTimeUtc.AddTicks(-1)));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void ExitBoundary_EqualityDoesNotCrossExceptMeanTarget(int side)
    {
        var s = Strategy;
        var input = StrategyFixtures.Input(s, side, StrategyFixtures.Flat(s.SignalTf, wick: 0));
        Assert.Equal(Family == StrategyId.MeanReversion,
            s.CheckExit(input, (TradeDirection)side, 100, 2).ShouldExit);
    }
}

public sealed class TrendPullbackBehaviorTests : TwoSidedStrategyBehaviorSuite { protected override StrategyId Family => StrategyId.TrendPullback; }
public sealed class DonchianBreakoutBehaviorTests : TwoSidedStrategyBehaviorSuite { protected override StrategyId Family => StrategyId.DonchianBreakout; }
public sealed class MeanReversionBehaviorTests : TwoSidedStrategyBehaviorSuite { protected override StrategyId Family => StrategyId.MeanReversion; }
public sealed class MomentumImpulseBehaviorTests : TwoSidedStrategyBehaviorSuite { protected override StrategyId Family => StrategyId.MomentumImpulse; }
public sealed class DailyTrendBehaviorTests : TwoSidedStrategyBehaviorSuite { protected override StrategyId Family => StrategyId.DailyTrend; }

public sealed class IndexDipBuyBehaviorTests
{
    private static IndexDipBuyStrategy Production() => Assert.IsType<IndexDipBuyStrategy>(
        Assert.Single(new StrategyBook(DefaultProfile.Load()).For(AssetClass.Index)));

    [Fact]
    public void ActualDefaultProfile_BuyOnly_Sma100Rsi2Below5Stop3AtrTenBars()
    {
        var s = Production();
        Assert.Equal(100, s.Params.TrendSma); Assert.Equal(2, s.Params.RsiPeriod);
        Assert.Equal(5, s.Params.RsiEntry); Assert.Equal(5, s.Params.ExitSma);
        Assert.Equal(3, s.Params.StopAtr); Assert.Equal(10, s.Params.Exit.MaxBarsHeld);
        var input = StrategyFixtures.Input(s, 1);
        var c = Assert.IsType<SignalCandidate>(s.Evaluate(input));
        Assert.Equal(TradeDirection.Long, c.Direction);
        Assert.Equal(3 * input.Signal.Atr(14)[input.SignalIndex], c.StopDistance, 10);
        Assert.Null(c.TakeProfitDistance);
        Assert.Null(s.Evaluate(StrategyFixtures.Input(s, -1)));
        Assert.False(s.AppliesTo(AssetClass.Forex)); Assert.False(s.AppliesTo(AssetClass.Metal));
        Assert.True(s.AppliesTo(AssetClass.Etf));
        Assert.Null(s.Evaluate(StrategyFixtures.Input(s, 1, index: 118)));
        Assert.Null(s.Evaluate(StrategyFixtures.Input(s, 1, regime: MarketRegime.Chaotic)));
    }

    [Fact]
    public void RsiEqualityRejected_TrendAndExitSmaEqualityRejected()
    {
        var s = Production(); var input = StrategyFixtures.Input(s, 1);
        double rsi = input.Signal.Rsi(2)[input.SignalIndex];
        var equal = new IndexDipBuyStrategy(new IndexDipBuyParams { TrendSma = 100, RsiEntry = rsi });
        var above = new IndexDipBuyStrategy(new IndexDipBuyParams { TrendSma = 100, RsiEntry = Math.BitIncrement(rsi) });
        Assert.Null(equal.Evaluate(input)); Assert.NotNull(above.Evaluate(input));
        Assert.Null(s.Evaluate(StrategyFixtures.Input(s, 1, StrategyFixtures.Flat(Tf.D1))));
        Assert.False(s.CheckExit(StrategyFixtures.Input(s, 1, StrategyFixtures.Flat(Tf.D1)), TradeDirection.Long, 100, 1).ShouldExit);
    }

    [Fact]
    public void FuturePerturbation_ProductionEntryAndExitUnchanged()
    {
        var s = Production(); var bars = StrategyFixtures.Pattern(s.Id, 1, Tf.D1);
        var a = StrategyFixtures.Input(s, 1, bars);
        var future = bars.Concat(Enumerable.Range(1, 20).Select(j => StrategyFixtures.Candle(bars[^1].OpenTime.AddDays(j), 1000, j))).ToList();
        var b = StrategyFixtures.Input(s, 1, future, index: bars.Count - 1);
        Assert.NotNull(s.Evaluate(a));
        Assert.Equal(JsonSerializer.Serialize(s.Evaluate(a)), JsonSerializer.Serialize(s.Evaluate(b)));
        Assert.Equal(s.CheckExit(a, TradeDirection.Long, 100, 2).ShouldExit, s.CheckExit(b, TradeDirection.Long, 100, 2).ShouldExit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoreDispatch_ActualProductionProfile_StopAndProtectiveTp3R_ThenManagerExit(bool timeExit)
    {
        var rig = new EngineRig(); // Reuse trusted in-process authority; replace fixture profile engine, not its safety gates.
        var cfg = new EngineConfig { CollectionWindowSeconds = 0 }; cfg.News.Enabled = false;
        var engine = new TradingEngine(cfg, new StrategyBook(DefaultProfile.Load()), rig.Broker, rig.Broker, rig.Bars,
            new MemoryStore(), new NullLog(), new NullJournal());
        engine.SetUniverse(new[] { new UniverseEntry { Symbol = "US500", AssetClass = AssetClass.Index, BaseCurrency = "US500" } });
        engine.SetGuard(new AccountGuardResult { Allowed = true, IsLive = false, Reason = "trusted unit fixture" });
        engine.Initialize(rig.Now); engine.CmdStart();
        rig.Broker.Now = rig.Now.AddSeconds(2); engine.OnTimer(rig.Broker.Now);
        var order = Assert.Single(rig.Broker.Orders);
        Assert.Equal(TradeDirection.Long, order.Direction);
        Assert.Equal(3 * order.StopLossPips, order.TakeProfitPips!.Value, 10);
        var pos = Assert.Single(rig.Broker.Positions); var plan = Assert.Single(engine.Plans.Values);
        Assert.Equal(10, plan.Exit.MaxBarsHeld);
        Assert.Equal(pos.EntryPrice - order.StopLossPips * .1, pos.StopLoss!.Value, 8);
        Assert.Equal(pos.EntryPrice + order.TakeProfitPips.Value * .1, pos.TakeProfit!.Value, 8);
        Assert.True(rig.Bars.TryGet("US500", Tf.D1, rig.Now, out var features, out var lastIndex));
        var last = features.Bars[lastIndex];
        double close = timeExit ? last.Close - 1 : last.Close + 200;
        if (timeExit) plan.BarsHeld = 9;
        rig.Bars.Merge("US500", Tf.D1, new[] { StrategyFixtures.Candle(last.OpenTime.AddDays(1), last.Close, close) }, false);
        rig.Broker.Now = rig.Now.AddDays(1).AddSeconds(5);
        // Keep quote at entry: this checks strategy/manager dispatch, not a fake broker TP fill.
        engine.OnTimer(rig.Broker.Now);
        Assert.Contains(pos.Id, rig.Broker.Closes);
        Assert.Empty(rig.Broker.Positions);
    }
}

internal static class StrategyFixtures
{
    internal static IStrategy Create(StrategyId id) => id switch
    {
        StrategyId.TrendPullback => new TrendPullbackStrategy(),
        StrategyId.DonchianBreakout => new DonchianBreakoutStrategy(),
        StrategyId.MeanReversion => new MeanReversionStrategy(),
        StrategyId.MomentumImpulse => new MomentumImpulseStrategy(),
        StrategyId.DailyTrend => new DailyTrendStrategy(),
        _ => new IndexDipBuyStrategy(DefaultProfile.Load().Entries.Single(e => e.Strategy == StrategyId.IndexDipBuy && e.AssetClass == AssetClass.Index).DipBuy!)
    };
    internal static Bar Candle(DateTime time, double open, double close, double wick = .4) =>
        new(time, open, Math.Max(open, close) + wick, Math.Min(open, close) - wick, close, 100);
    internal static List<Bar> Flat(Tf tf, double wick = .4) => Enumerable.Range(0, 320)
        .Select(j => Candle(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) + TimeSpan.FromTicks(tf.Duration().Ticks * j), 100, 100, wick)).ToList();
    internal static List<Bar> Pattern(StrategyId id, int side, Tf tf)
    {
        var bars = Flat(tf);
        if (id == StrategyId.TrendPullback || id == StrategyId.IndexDipBuy)
        {
            for (int j = 0; j < bars.Count; j++)
            {
                double c = 100 + .5 * j;
                if (id == StrategyId.TrendPullback && j >= 315 && j <= 318) c -= new[] { 5.0, 7, 7, 5 }[j - 315];
                if (id == StrategyId.IndexDipBuy && j >= 314) c = 100 + .5 * 313 - 2 * (j - 313);
                bars[j] = Candle(bars[j].OpenTime, j == 0 ? c : bars[j - 1].Close, c);
            }
        }
        else if (id == StrategyId.MeanReversion)
        {
            bars[^2] = Candle(bars[^2].OpenTime, 100, 90, .2);
            bars[^1] = Candle(bars[^1].OpenTime, 90, 95, .2);
        }
        else bars[^1] = Candle(bars[^1].OpenTime, 100, 103, .1);
        if (side < 0) bars = bars.Select(b => new Bar(b.OpenTime, 400 - b.Open, 400 - b.Low, 400 - b.High, 400 - b.Close, b.Volume)).ToList();
        return bars;
    }
    internal static List<Bar> ExitPattern(StrategyId id, int side, Tf tf)
    {
        var bars = Flat(tf);
        if (id == StrategyId.TrendPullback)
            for (int j = 0; j < bars.Count; j++) bars[j] = Candle(bars[j].OpenTime, 200 - .2 * j, 200 - .2 * j);
        else bars[^1] = Candle(bars[^1].OpenTime, 100, id == StrategyId.MeanReversion || id == StrategyId.IndexDipBuy ? 105 : 95);
        if (side < 0) bars = bars.Select(b => new Bar(b.OpenTime, 400 - b.Open, 400 - b.Low, 400 - b.High, 400 - b.Close, b.Volume)).ToList();
        return bars;
    }
    internal static StrategyInput Input(IStrategy s, int side, List<Bar>? bars = null, int? index = null,
        double? adx = null, MarketRegime? regime = null)
    {
        bars ??= Pattern(s.Id, side, s.SignalTf);
        var f = new FeatureSet(new BarSeries("US500", s.SignalTf, bars.Select(b => b.OpenTime).ToArray(), bars.Select(b => b.Open).ToArray(),
            bars.Select(b => b.High).ToArray(), bars.Select(b => b.Low).ToArray(), bars.Select(b => b.Close).ToArray(), bars.Select(b => b.Volume).ToArray()));
        int i = index ?? bars.Count - 1;
        var r = new RegimeAssessment { Regime = regime ?? (s.Id == StrategyId.MeanReversion ? MarketRegime.Range : side > 0 ? MarketRegime.StrongUptrend : MarketRegime.StrongDowntrend),
            TrendDirection = side, Adx = adx ?? (s.Id == StrategyId.MeanReversion ? 10 : 30), Volatility = VolatilityState.Normal };
        return new StrategyInput { Symbol = "US500", Spec = new InstrumentSpec { Name = "US500", AssetClass = AssetClass.Index },
            Signal = f, SignalIndex = i, Filter = f, FilterIndex = i, SignalRegime = r, FilterRegime = r,
            DecisionTimeUtc = bars[i].OpenTime + s.SignalTf.Duration() };
    }
}
