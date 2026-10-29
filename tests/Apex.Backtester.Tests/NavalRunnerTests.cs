using Apex.Backtester.Naval;
using Apex.Core.Naval;
using Xunit;

namespace Apex.Backtester.Tests;

public class NavalRunnerTests
{
    static NavalRunSpec Spec(NavalFixture f, string[]? assets = null) => new(NavalDataset.Load(f.Manifest), NavalFleet.Alpha, f.From, f.To, new(), SpotCostModel.Named("base"), Assets: assets);

    [Fact]
    public void Actual_evaluator_entry_close_next_open_and_boundary_account_complete()
    {
        using var f = new NavalFixture();
        var r = NavalBacktestRunner.Run(Spec(f, new[] { "BTC" }));
        Assert.Contains(r.Decisions, x => x.Decision.Action == NavalAction.EntrySignal);
        Assert.NotEmpty(r.Account.Trades);
        var buy = r.Account.Cashflows.First(x => x.Fill.Side == SpotSide.Buy).Fill;
        Assert.Equal(f.From.AddDays(1).AddTicks(1), buy.TimeUtc);
        Assert.Equal(311m, buy.ReferencePrice);
        Assert.True(buy.ExecutedPrice > buy.ReferencePrice);
        Assert.True(buy.InitialRisk <= 50m);
        Assert.Empty(r.Account.Positions);
        Assert.Equal(r.Account.Cash - 10000m, r.Account.Trades.Sum(t => t.Net));
        Assert.Equal(r.Account.Cash, 10000m + r.Account.Cashflows.Sum(c => c.QuoteDelta));
        Assert.Contains(r.Account.Trades, t => t.Boundary);
        Assert.Equal(f.To, r.Equity[^1].TimeUtc);
    }

    [Theory]
    [InlineData("stopgap", "StopGap", 290)]
    [InlineData("tpgap", "TakeProfitGap", 0)]
    [InlineData("both", "Stop", 0)]
    [InlineData("newboth", "Stop", 0)]
    public void Gaps_both_hits_and_new_entry_protection(string kind, string reason, int price)
    {
        var start = DateTimeOffset.Parse("2023-01-01T00:00:00Z");
        using var f = new NavalFixture(215, start, i =>
        {
            if (i == (kind == "newboth" ? 211 : 212)) return kind switch
            {
                "stopgap" => new(start.AddDays(i), 290, 291, 289, 290, 1000000, true),
                "tpgap" => new(start.AddDays(i), 400, 401, 399, 400, 1000000, true),
                _ => new(start.AddDays(i), 311, 400, 290, 311, 1000000, true)
            };
            return NavalFixture.Trend(start, i);
        });
        var r = NavalBacktestRunner.Run(Spec(f, new[] { "BTC" }));
        var t = r.Account.Trades[0]; Assert.Equal(reason, t.ExitReason);
        var exit = r.Account.Cashflows.First(x => x.Fill.Side == SpotSide.Sell).Fill;
        var buy = r.Account.Cashflows[0].Fill;
        if (price > 0) Assert.Equal((decimal)price, exit.ReferencePrice);
        if (kind == "tpgap") Assert.True(exit.ReferencePrice < 400m);
        if (kind == "newboth") Assert.Equal(buy.TimeUtc.Date, exit.TimeUtc.AddTicks(-1).Date);
        Assert.True(exit.ExecutedPrice < exit.ReferencePrice);
    }

    [Fact]
    public void Future_ohlcv_does_not_change_prior_entry_ranking_or_signal_identity()
    {
        using var a = new NavalFixture();
        var start = a.Start;
        using var b = new NavalFixture(bar: i => i < 220 ? NavalFixture.Trend(start, i) : new(start.AddDays(i), 50, 500, 1, 55, 0, true));
        var x = NavalBacktestRunner.Run(Spec(a)); var y = NavalBacktestRunner.Run(Spec(b));
        var before = start.AddDays(220);
        Assert.Contains(x.Account.Cashflows, c => c.Fill.TimeUtc < before);
        Assert.Equal(x.Account.Cashflows.Where(c => c.Fill.TimeUtc < before), y.Account.Cashflows.Where(c => c.Fill.TimeUtc < before));
        Assert.Equal(x.Decisions.Where(d => d.Decision.CutoffUtc < before), y.Decisions.Where(d => d.Decision.CutoffUtc < before));
    }

    [Fact]
    public void Fleet_shared_cash_max_two_rank_tie_symbol_and_past_turnover()
    {
        using var f = new NavalFixture();
        var spec = new NavalRunSpec(NavalDataset.Load(f.Manifest), NavalFleet.Bravo, f.From, f.To, new(), SpotCostModel.Named("base"));
        var r = NavalBacktestRunner.Run(spec);
        var buys = r.Account.Cashflows.Where(c => c.Fill.Side == SpotSide.Buy).Take(2).ToArray();
        Assert.Equal(new[] { "ADA-USDT", "AVAX-USDT" }, buys.Select(b => b.Fill.Instrument));
        Assert.Equal(2, r.MaxConcurrentPositions); Assert.All(r.Equity, e => Assert.True(e.Cash >= 0));
        Assert.Contains(r.Refusals, x => x.Reason == "MaxPositions");
        var start = f.Start;
        using var lowVolume = new NavalFixture(bar: i => NavalFixture.Trend(start, i) with { Volume = i < 211 ? 0 : 1000000 });
        var no = NavalBacktestRunner.Run(Spec(lowVolume));
        Assert.Contains(no.Refusals, x => x.TimeUtc < lowVolume.From.AddDays(2) && x.Reason == "Liquidity");
    }

    [Fact]
    public void Daily_and_drawdown_latch_and_exits_remain_available()
    {
        var start = DateTimeOffset.Parse("2023-01-01T00:00:00Z");
        using var f = new NavalFixture(240, start, i => i == 212 ? new(start.AddDays(i), 100, 101, 99, 100, 1000000, true) : NavalFixture.Trend(start, i));
        var r = NavalBacktestRunner.Run(Spec(f));
        Assert.True(r.Halted); Assert.Equal(2, r.Account.Trades.Count);
        Assert.All(r.Account.Trades, t => Assert.Equal("StopGap", t.ExitReason));
        Assert.Equal(2, r.Account.Cashflows.Count(x => x.Fill.Side == SpotSide.Buy));
        Assert.Contains(r.Refusals, x => x.Reason == "RiskHalt");
        Assert.Empty(r.Account.Positions);
    }

    [Fact]
    public void Channel_exit_uses_real_evaluator_and_is_not_gated_by_btc_filter()
    {
        var start = DateTimeOffset.Parse("2023-01-01T00:00:00Z");
        // Szerokie wcześniejsze bary dają odległy SL; jednobarowy kanał przebity blisko wejścia.
        using var f = new NavalFixture(216, start, i =>
        {
            if (i < 210) return NavalFixture.Trend(start, i) with { High = 110 + i, Low = 90 + i };
            return i switch {
                210 => new(start.AddDays(i), 351, 352, 350, 351.5m, 1000000, true),
                211 => new(start.AddDays(i), 351.5m, 352, 351, 351.5m, 1000000, true),
                212 => new(start.AddDays(i), 351, 351, 349, 349, 1000000, true),
                _ => new(start.AddDays(i), 349, 350, 348, 349, 1000000, true)
            };
        });
        var btcPath = Path.Combine(f.Root, "BTC-USDT.csv");
        var btcCsv = File.ReadAllText(btcPath).Replace("351,351,349,349,1000000", "1,1,1,1,1000000");
        File.WriteAllText(btcPath, btcCsv); f.Item()["sha256"] = NavalFixture.Hash(btcCsv); f.Save();
        var r = NavalBacktestRunner.Run(Spec(f, new[] { "ETH" }) with { Settings = new(40, 1, 200) });
        Assert.Contains(r.Decisions, x => x.Decision.Action == NavalAction.ExitSignal && x.Decision.Instrument == "ETH-USDT");
        Assert.Contains(r.Account.Trades, x => x.ExitReason == "Channel");
        Assert.Equal(f.Start.AddDays(213).AddTicks(1), r.Account.Trades[0].ExitUtc);
    }
}
