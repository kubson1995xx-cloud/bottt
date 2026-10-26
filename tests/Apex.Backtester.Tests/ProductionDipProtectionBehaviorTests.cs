using Apex.Backtester.Data;
using Apex.Backtester.Sim;
using Apex.Core.Adaptive;
using Apex.Core.Engine;
using Apex.Core.Model;
using Apex.Core.Safety;
using Xunit;

namespace Apex.Backtester.Tests;

// Controlled execution fixture, not market research. Uses actual DefaultProfile,
// Core dispatch, USD catalog costs and SimWorld H1 protection execution.
public sealed class ProductionDipProtectionBehaviorTests
{
    [Theory]
    [InlineData("TP")]
    [InlineData("SL")]
    [InlineData("both")]
    [InlineData("gap")]
    public void ProductionDefault_CoreBracketIs3R_ActualProtectiveFill(string hit)
    {
        var origin = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var bars = new List<Bar>(); double price = 4000;
        var rng = new Random(3);
        for (int day = 0; day < 302; day++)
        {
            double open = price;
            double close = day is >= 297 and <= 299 ? open - 45 : open + 4 + (rng.NextDouble() - .5) * 20;
            for (int hour = 0; hour < 24; hour++)
            {
                double o = open + (close - open) * hour / 24, c = open + (close - open) * (hour + 1) / 24;
                bars.Add(new Bar(origin.AddDays(day).AddHours(hour), o, Math.Max(o, c) + 8, Math.Min(o, c) - 8, c, 1000));
            }
            price = close;
        }
        var sd = new SymbolData(Catalog.Get("US500"), BarSeries.FromBars("US500", Tf.H1, bars), null);
        var world = new SimWorld(new[] { sd }, new FxRates(), CostScenario.Base, 10_000);
        var cfg = new EngineConfig { CollectionWindowSeconds = 0, IsBacktest = true }; cfg.News.Enabled = false;
        var engine = new TradingEngine(cfg, new StrategyBook(DefaultProfile.Load()), world, world, world,
            new MemoryStore(), new NullLog(), new NullJournal());
        var closed = new List<ClosedPositionInfo>();
        world.OnClosed = p => { closed.Add(p); engine.OnPositionClosed(p); };
        engine.SetUniverse(new[] { new UniverseEntry { Symbol = "US500", AssetClass = AssetClass.Index, BaseCurrency = "US500" } });
        engine.SetGuard(new AccountGuardResult { Allowed = true, IsLive = false, Reason = "trusted unit simulation" });
        var now = origin.AddDays(300);
        engine.Decisions = new DecisionRecorder();
        world.SetTime(now); engine.Initialize(now); engine.CmdStart(); engine.OnTimer(now);
        Assert.True(world.OpenCount == 1, System.Text.Json.JsonSerializer.Serialize(engine.Decisions.Since(0).events));
        var pos = Assert.Single(world.GetPositions());
        var plan = Assert.Single(engine.Plans.Values);
        Assert.Equal(TradeDirection.Long, pos.Direction);
        Assert.Equal(10, plan.Exit.MaxBarsHeld);
        double risk = pos.EntryPrice - pos.StopLoss!.Value;
        Assert.True(risk > 0);
        Assert.Equal(3 * risk, pos.TakeProfit!.Value - pos.EntryPrice, 6);
        Assert.InRange(plan.RiskAmount, double.Epsilon, 50.000001);
        // Change only the execution bar (not any closed indicator bar). This proves
        // TP/SL fills rather than merely checking that a request contains a number.
        int i = sd.IndexByOpen[now];
        sd.H1.Open[i] = hit == "gap" ? pos.StopLoss.Value - 10 : pos.EntryPrice;
        sd.H1.High[i] = hit is "TP" or "both" ? pos.TakeProfit.Value + 10 : pos.EntryPrice + 1;
        sd.H1.Low[i] = hit is "SL" or "both" or "gap" ? sd.H1.Open[i] < pos.StopLoss.Value ? sd.H1.Open[i] - 1 : pos.StopLoss.Value - 10 : pos.EntryPrice - 1;
        sd.H1.Close[i] = sd.H1.Open[i];
        world.ProcessIntrabar(now, now.AddHours(1));
        var result = Assert.Single(closed);
        Assert.Empty(world.GetPositions());
        Assert.Equal(hit == "TP" ? "TakeProfit" : "StopLoss", result.PlatformReason);
        Assert.Equal(now.AddHours(1), result.ExitTimeUtc);
        if (hit == "TP") Assert.True(result.NetProfit > 0);
        else Assert.True(result.NetProfit < 0);
        if (hit == "gap") Assert.True(result.ExitPrice < pos.StopLoss.Value - 9);
        Assert.Equal(10_000 + world.Cashflows.Sum(c => c.Delta), world.Balance, 7);
        Assert.Equal(10_000 + result.NetProfit, world.Balance, 7);
    }
}
