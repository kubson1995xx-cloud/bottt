using Apex.Backtester.Naval;
using Apex.Core.Naval;
using Xunit;

namespace Apex.Backtester.Tests;

public class NavalProtectionEvidenceTests
{
    [Fact]
    public void Larger_asof_breakout_distance_precedes_ordinal_symbol_tie_break()
    {
        using var f = new NavalFixture();
        var path = Path.Combine(f.Root, "ETH-USDT.csv");
        var text = File.ReadAllText(path).Replace("310,310.6,309.4,310.5", "310,311.1,309.4,311.0");
        File.WriteAllText(path, text); f.Item(1)["sha256"] = NavalFixture.Hash(text); f.Save();
        var r = NavalBacktestRunner.Run(new(NavalDataset.Load(f.Manifest), NavalFleet.Alpha, f.From, f.To, new(), SpotCostModel.Named("base")));
        Assert.Equal("ETH-USDT", r.Account.Cashflows[0].Fill.Instrument);
        var first = r.Decisions.Where(x => x.Decision.Action == NavalAction.EntrySignal).Take(2).ToArray();
        Assert.True(first.Single(x => x.Decision.Instrument == "ETH-USDT").Rank > first.Single(x => x.Decision.Instrument == "BTC-USDT").Rank);
    }

    [Fact]
    public void Simultaneous_low_stress_includes_positions_before_protective_liquidation()
    {
        var start = DateTimeOffset.Parse("2023-01-01T00:00:00Z");
        using var f = new NavalFixture(213, start, i => i == 211 ? new(start.AddDays(i), 311, 400, 290, 311, 1000000, true) : NavalFixture.Trend(start, i));
        var r = NavalBacktestRunner.Run(new(NavalDataset.Load(f.Manifest), NavalFleet.Alpha, f.From, f.To, new(), SpotCostModel.Named("base"), Assets: new[] { "BTC" }));
        Assert.Equal("Stop", r.Account.Trades[0].ExitReason);
        var day = r.Equity.Single(e => e.TimeUtc == start.AddDays(212));
        Assert.True(day.IntrabarLowStress < day.LiquidationEquity, $"Low stress {day.IntrabarLowStress} must include pre-stop inventory, close equity {day.LiquidationEquity}");
    }
    [Fact]
    public void Entry_drift_is_refused_without_using_execution_day_high_or_volume()
    {
        var start = DateTimeOffset.Parse("2023-01-01T00:00:00Z");
        using var f = new NavalFixture(212, start, i => i == 211 ? new(start.AddDays(i), 320, 400, 1, 320, 1000000, true) : NavalFixture.Trend(start, i));
        var r = NavalBacktestRunner.Run(new(NavalDataset.Load(f.Manifest), NavalFleet.Alpha, f.From, f.To, new(), SpotCostModel.Named("base"), Assets: new[] { "BTC" }));
        Assert.Empty(r.Account.Cashflows); Assert.Contains(r.Refusals, x => x.Reason == "EntryDriftOrAtr");
    }
    [Fact]
    public void Daily_limit_can_latch_below_five_percent_DD_and_prevent_later_entries()
    {
        var start = DateTimeOffset.Parse("2023-01-01T00:00:00Z");
        using var f = new NavalFixture(240, start, i => i == 212 ? new(start.AddDays(i), 304, 305, 303, 304, 1000000, true) : NavalFixture.Trend(start, i));
        var r = NavalBacktestRunner.Run(new(NavalDataset.Load(f.Manifest), NavalFleet.Alpha, f.From, f.To, new(), SpotCostModel.Named("base")));
        Assert.True(r.Halted);
        Assert.InRange(r.Equity.Max(e => e.EquityDrawdownPct), 1.5m, 4.99m);
        Assert.Equal(2, r.Account.Cashflows.Count(c => c.Fill.Side == SpotSide.Buy));
        Assert.Equal(2, r.Account.Trades.Count); Assert.Contains(r.Refusals, x => x.Reason == "RiskHalt");
    }
}
