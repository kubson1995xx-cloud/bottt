using System.Globalization;
using Apex.Backtester.Naval;
using Apex.Core.Naval;
using Xunit;

namespace Apex.Backtester.Tests;

// Controlled synthetic bars exercising the actual evaluator, fills and sticky risk latch.
public class NavalRiskMarkingTests
{
    static readonly DateTimeOffset Start = DateTimeOffset.Parse("2023-01-01T00:00:00Z");
    static NavalRunResult Run(NavalFixture f, string[]? assets = null) => NavalBacktestRunner.Run(new(
        NavalDataset.Load(f.Manifest), NavalFleet.Alpha, f.From, f.To, new(), SpotCostModel.Named("base"), Assets: assets));

    static NavalRunResult Gap(decimal open, bool continueAfterGap = false)
    {
        using var f = new NavalFixture(continueAfterGap ? 214 : 213, Start, i => i switch
        {
            212 => new(Start.AddDays(i), open, open + 1, open - 1, open, 1000000, true),
            213 => new(Start.AddDays(i), open, open + .6m, open - .6m, open + .5m, 1000000, true),
            _ => NavalFixture.Trend(Start, i)
        }, all: false);
        return Run(f, new[] { "BTC" });
    }

    [Fact]
    public void Reviewer_TP_gaps_320_and_400_have_identical_account_and_no_unrealizable_peak_halt()
    {
        var near = Gap(320); var far = Gap(400);
        Assert.Equal(10098.186520404112m, near.Account.Cash);
        Assert.Equal(near.Account.Cash, far.Account.Cash);
        Assert.Equal(near.Account.Cashflows, far.Account.Cashflows);
        Assert.Equal(near.Account.Trades, far.Account.Trades);
        Assert.Equal(near.Equity, far.Equity);
        Assert.Equal("TakeProfitGap", Assert.Single(far.Account.Trades).ExitReason);
        Assert.Equal(318.44m, far.Account.Cashflows.Last().Fill.ReferencePrice);
        Assert.False(near.Halted);
        Assert.False(far.Halted);
    }

    [Theory]
    [InlineData(320)]
    [InlineData(400)]
    public void Conservative_TP_gap_leaves_next_eligible_entry_available(int open)
    {
        var r = Gap(open, continueAfterGap: true);
        var buys = r.Account.Cashflows.Where(c => c.Fill.Side == SpotSide.Buy).ToArray();
        Assert.Equal(2, buys.Length);
        Assert.Equal(Start.AddDays(213).AddTicks(1), buys[1].Fill.TimeUtc);
        Assert.Equal((decimal)open, buys[1].Fill.ReferencePrice);
        Assert.DoesNotContain(r.Refusals, x => x.Reason == "RiskHalt");
        Assert.False(r.Halted);
    }

    [Theory]
    [InlineData("BTC")]
    [InlineData("ETH")]
    public void Simultaneous_open_protective_gaps_do_not_peak_on_the_other_pending_TP_gap(string stopAsset)
    {
        using var f = new NavalFixture(213, Start, all: false);
        foreach (var asset in new[] { "BTC", "ETH" })
            SetBars(f, asset, 213, i => i != 212 ? NavalFixture.Trend(Start, i)
                : asset == stopAsset
                    ? new(Start.AddDays(i), 304, 305, 303, 304, 1000000, true)
                    : new(Start.AddDays(i), 400, 401, 399, 400, 1000000, true));
        var r = Run(f);
        Assert.Equal(2, r.Account.Trades.Count);
        Assert.Contains(r.Account.Trades, t => t.ExitReason == "StopGap");
        Assert.Contains(r.Account.Trades, t => t.ExitReason == "TakeProfitGap");
        Assert.InRange(r.Account.Cash, 9940m, 10000m);
        Assert.False(r.Halted);
    }

    static NavalBar ReviewBar(string asset, string stopAsset, int i, decimal otherHigh, decimal otherClose)
        => i switch
        {
            212 when asset == stopAsset => new(Start.AddDays(i), 311.5m, 318.43m, 311, 318.43m, 1000000, true),
            212 => new(Start.AddDays(i), 311.5m, 312.2m, 311, 312, 1000000, true),
            213 when asset == stopAsset => new(Start.AddDays(i), 318.43m, 318.43m, 300, 311.5m, 1000000, true),
            213 => new(Start.AddDays(i), 311.5m, otherHigh, 311, otherClose, 1000000, true),
            _ => NavalFixture.Trend(Start, i)
        };

    static void SetBars(NavalFixture f, string asset, int days, Func<int, NavalBar> bar)
    {
        static string D(decimal x) => x.ToString(CultureInfo.InvariantCulture);
        var csv = "timestamp,open,high,low,close,volume\n" + string.Join("\n", Enumerable.Range(0, days).Select(i =>
        {
            var b = bar(i);
            return string.Join(",", NavalFixture.Stamp(b.OpenUtc), D(b.Open), D(b.High), D(b.Low), D(b.Close), D(b.Volume));
        })) + "\n";
        File.WriteAllText(Path.Combine(f.Root, asset + "-USDT.csv"), csv);
        f.Item(asset == "BTC" ? 0 : 1)["sha256"] = NavalFixture.Hash(csv);
        f.Save();
    }

    static NavalRunResult ReviewClose(string stopAsset = "BTC", decimal otherHigh = 312.2m, decimal otherClose = 312m, int days = 214)
    {
        using var f = new NavalFixture(days, Start, all: false);
        foreach (var asset in new[] { "BTC", "ETH" })
            SetBars(f, asset, days, i => ReviewBar(asset, stopAsset, i, otherHigh, otherClose));
        return Run(f);
    }

    [Fact]
    public void Reviewer_close_stop_and_survivor_close_mark_form_one_risk_observation()
    {
        var r = ReviewClose();
        var buys = r.Account.Cashflows.Where(c => c.Fill.Side == SpotSide.Buy).ToArray();
        Assert.Equal(new[] { 15.414m, 15.389m }, buys.Select(c => c.Fill.Quantity));
        Assert.Equal(10097.123163206514m, r.Equity[^2].LiquidationEquity);
        Assert.Equal(9949.092459964638m, r.Account.Cash);
        Assert.InRange(r.Equity.Max(e => e.EquityDrawdownPct), 1.46m, 1.47m);
        Assert.Equal(new[] { "Stop", "Boundary" }, r.Account.Trades.Select(t => t.ExitReason));
        Assert.False(r.Halted);
    }

    [Fact]
    public void Coherent_close_stop_does_not_refuse_later_eligible_entry_as_RiskHalt()
    {
        var r = ReviewClose(days: 240);
        Assert.Contains(r.Account.Cashflows, c => c.Fill.Side == SpotSide.Buy && c.Fill.TimeUtc > Start.AddDays(214));
        Assert.DoesNotContain(r.Refusals, x => x.Reason == "RiskHalt");
        Assert.False(r.Halted);
    }

    [Theory]
    [InlineData("BTC")]
    [InlineData("ETH")]
    public void Reviewer_simultaneous_close_stop_and_TP_do_not_create_ordinal_risk_states(string stopAsset)
    {
        var r = ReviewClose(stopAsset, otherHigh: 330m, otherClose: 311.5m);
        Assert.Equal(2, r.Account.Trades.Count);
        Assert.Contains(r.Account.Trades, t => t.Instrument == stopAsset + "-USDT" && t.ExitReason == "Stop");
        Assert.Contains(r.Account.Trades, t => t.Instrument != stopAsset + "-USDT" && t.ExitReason == "TakeProfit");
        Assert.All(r.Account.Trades, t => Assert.Equal(Start.AddDays(214), t.ExitUtc));
        Assert.InRange(r.Equity.Max(e => e.EquityDrawdownPct), 0m, 1.49m);
        Assert.False(r.Halted);
    }

    [Fact]
    public void Simultaneous_close_stops_do_not_peak_on_other_already_stopped_inventory_at_close()
    {
        NavalRunResult BothStops(decimal close)
        {
            using var f = new NavalFixture(213, Start, i => i == 212
                ? new(Start.AddDays(i), 311.5m, 401, 300, close, 1000000, true)
                : NavalFixture.Trend(Start, i), all: false);
            return Run(f);
        }
        var lowClose = BothStops(311.5m); var highClose = BothStops(400m);
        Assert.Equal(lowClose.Account.Cashflows, highClose.Account.Cashflows);
        Assert.Equal(lowClose.Equity, highClose.Equity);
        Assert.Equal(2, highClose.Account.Trades.Count);
        Assert.All(highClose.Account.Trades, t => Assert.Equal("Stop", t.ExitReason));
        Assert.InRange(highClose.Account.Cash, 9900m, 9901m);
        Assert.False(lowClose.Halted);
        Assert.False(highClose.Halted);
    }

    [Fact]
    public void Genuine_open_only_daily_breach_stays_latched_after_close_recovery_and_TP_exits()
    {
        const int days = 216;
        using var f = new NavalFixture(days, Start, all: false);
        foreach (var asset in new[] { "BTC", "ETH" })
            SetBars(f, asset, days, i => i switch
            {
                213 => new(Start.AddDays(i), 309, asset == "BTC" ? 318.43m : 312.2m, 309,
                    asset == "BTC" ? 318.43m : 312m, 1000000, true),
                214 => new(Start.AddDays(i), asset == "BTC" ? 318.43m : 312m, 331, 311, 330, 1000000, true),
                215 => new(Start.AddDays(i), 330, 331, 329, 330.5m, 1000000, true),
                _ => ReviewBar(asset, "BTC", i, 312.2m, 312m)
            });
        var r = Run(f);
        // At open 213 both marks=309: equity=9905.9275948758, a real 1.8935% daily loss.
        // Neither stop is touched; close recovers. Only the open observation can latch here.
        Assert.True(r.Halted);
        Assert.InRange(r.Equity.Max(e => e.EquityDrawdownPct), 0m, 1.49m);
        Assert.Equal(2, r.Account.Cashflows.Count(c => c.Fill.Side == SpotSide.Buy));
        Assert.Equal(2, r.Account.Trades.Count);
        Assert.All(r.Account.Trades, t => Assert.Equal("TakeProfit", t.ExitReason));
        Assert.Contains(r.Refusals, x => x.TimeUtc == Start.AddDays(215).AddTicks(1) && x.Reason == "RiskHalt");
        Assert.Empty(r.Account.Positions);
    }

    [Fact]
    public void Genuine_coherent_close_daily_breach_stays_latched_and_all_exits_remain_available()
    {
        var r = ReviewClose(otherClose: 311.5m, days: 240);
        Assert.True(r.Halted);
        Assert.InRange(r.Equity.Max(e => e.EquityDrawdownPct), 1.5m, 4.99m);
        Assert.Equal(2, r.Account.Cashflows.Count(c => c.Fill.Side == SpotSide.Buy));
        Assert.Equal(2, r.Account.Trades.Count);
        Assert.Contains(r.Account.Trades, t => t.ExitReason == "Stop");
        Assert.Contains(r.Account.Trades, t => t.ExitReason == "TakeProfit");
        Assert.Contains(r.Refusals, x => x.Reason == "RiskHalt");
        Assert.Empty(r.Account.Positions);
    }

    [Fact]
    public void Genuine_cumulative_DD_latches_even_when_every_daily_loss_is_below_daily_limit()
    {
        using var f = new NavalFixture(225, Start, i => i >= 211
            ? new(Start.AddDays(i), 100 + i, 100 + i + .6m, 100 + i - 20, 100 + i + .5m, 1000000, true)
            : NavalFixture.Trend(Start, i), all: false);
        var r = Run(f);
        Assert.True(r.Halted);
        Assert.True(r.Equity.Max(e => e.EquityDrawdownPct) >= 5m);
        Assert.All(r.Equity.Zip(r.Equity.Skip(1)), pair =>
            Assert.True(pair.Second.LiquidationEquity > pair.First.LiquidationEquity * .985m));
        Assert.All(r.Account.Trades, t => Assert.Equal("Stop", t.ExitReason));
        Assert.Contains(r.Refusals, x => x.Reason == "RiskHalt");
        Assert.Empty(r.Account.Positions);
    }

    [Theory]
    [InlineData(304, false)]
    [InlineData(100, true)]
    public void Genuine_protective_gap_daily_or_DD_breach_latches_before_later_admission(int open, bool exceedsFivePercent)
    {
        using var f = new NavalFixture(240, Start, i => i == 212
            ? new(Start.AddDays(i), open, open + 1, open - 1, open, 1000000, true)
            : NavalFixture.Trend(Start, i), all: false);
        var r = Run(f);
        Assert.True(r.Halted);
        var dd = r.Equity.Max(e => e.EquityDrawdownPct);
        Assert.True(dd >= 1.5m);
        Assert.Equal(exceedsFivePercent, dd >= 5m);
        Assert.Equal(2, r.Account.Cashflows.Count(c => c.Fill.Side == SpotSide.Buy));
        Assert.Equal(2, r.Account.Trades.Count);
        Assert.All(r.Account.Trades, t => Assert.Equal("StopGap", t.ExitReason));
        Assert.Contains(r.Refusals, x => x.Reason == "RiskHalt");
        Assert.Empty(r.Account.Positions);
    }
}
