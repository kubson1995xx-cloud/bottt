using Apex.Backtester.Naval;
using Apex.Core.Naval;
using Xunit;

namespace Apex.Backtester.Tests;

public class NavalWalkForwardTests
{
    static readonly DateTimeOffset Start = DateTimeOffset.Parse("2020-01-01T00:00:00Z");
    static readonly DateTimeOffset TrainFrom = DateTimeOffset.Parse("2022-01-01T00:00:00Z");
    static readonly DateTimeOffset Cutoff = DateTimeOffset.Parse("2024-01-01T00:00:00Z");
    static readonly DateTimeOffset End = DateTimeOffset.Parse("2025-01-01T00:00:00Z");
    static readonly NavalFold Fold = new(TrainFrom, Cutoff, Cutoff, End);
    static NavalRunSpec Spec(NavalFixture f) => new(NavalDataset.Load(f.Manifest), NavalFleet.Alpha, TrainFrom, End, new(), SpotCostModel.Named("double"), Assets: new[] { "BTC" });

    [Fact]
    public void Actual_traded_WF_uses_fresh_IS_OOS_base_training_and_future_independent_selection()
    {
        using var a = new NavalFixture((int)(End - Start).TotalDays, Start);
        using var b = new NavalFixture((int)(End - Start).TotalDays, Start, i => Start.AddDays(i) < Cutoff ? NavalFixture.Trend(Start, i) : new(Start.AddDays(i), 100, 101, 99, 100, 1000000, true));
        var x = NavalWalkForward.Run(Spec(a), new[] { Fold });
        var y = NavalWalkForward.Run(Spec(b), new[] { Fold });
        var fx = Assert.Single(x.Folds); var fy = Assert.Single(y.Folds);
        Assert.Equal(4, x.RunCount); Assert.Equal(fx.Selection.Key, fy.Selection.Key);
        Assert.NotNull(fx.Selection.Key);
        Assert.Equal(fx.Selection.Candidates, fy.Selection.Candidates);
        Assert.All(fx.Selection.Candidates, c => Assert.True(c.NaturalTrades >= 10));
        Assert.All(fx.Training, c => Assert.Equal(SpotCostModel.Named("base"), c.Spec.Cost));
        var oos = Assert.IsType<NavalRunResult>(fx.Oos);
        Assert.Equal(SpotCostModel.Named("double"), oos.Spec.Cost);
        Assert.Equal(10000m, oos.Equity[0].Cash);
        Assert.True(oos.Account.Cashflows[0].Fill.TimeUtc > Cutoff);
        Assert.NotSame(fx.Training[0].Account, oos.Account);
        Assert.All(fx.Training, t => {
            Assert.Contains(t.Account.Trades, z => !z.Boundary && z.ExitUtc < Cutoff);
            Assert.Empty(t.Account.Positions);
            Assert.All(t.Account.Trades, z => Assert.True(z.ExitUtc <= Cutoff));
            Assert.Equal(t.Account.Cash, 10000m + t.Account.Cashflows.Sum(z => z.QuoteDelta));
        });
        foreach (var c in fx.Selection.Candidates)
        {
            var run = fx.Training.Single(t => NavalWalkForward.Key(t.Spec.Settings) == c.Key);
            var eligible = run.Account.Trades.Where(t => !t.Boundary && t.ExitUtc < Cutoff).ToArray();
            Assert.Equal(eligible.Length, c.NaturalTrades);
            Assert.Equal(eligible.Average(t => t.R!.Value), c.Score);
        }
        // Celowy ledger-only stress selektora: kosztowne wyniki boundary PRZED cutoff i natural NA cutoff.
        // Nie eksportujemy tego zmodyfikowanego konta jako krzywej rzeczywistego runnera.
        foreach (var run in fx.Training)
        {
            foreach (var boundary in new[] { true, false })
            {
                var id = boundary ? "selector-boundary" : "selector-at-cutoff";
                var entry = Cutoff.AddMinutes(boundary ? -2 : -1);
                var exit = boundary ? Cutoff.AddSeconds(-2) : Cutoff;
                run.Account.Apply(new(id + "-buy", id, "BTC-USDT", "BTC", "USDT", entry, SpotSide.Buy, 1, 1000, 1000, 1, "USDT", InitialRisk: 1));
                run.Account.Apply(new(id + "-sell", id, "BTC-USDT", "BTC", "USDT", exit, SpotSide.Sell, 1, 1, 1, 0, "USDT", "SelectorStress", boundary));
            }
        }
        var purged = NavalWalkForward.Select(fx.Training, Cutoff);
        Assert.Equal(fx.Selection.Key, purged.Key);
        Assert.Equal(fx.Selection.Candidates, purged.Candidates);
    }
    [Fact]
    public void Fewer_than_ten_natural_trades_is_NoSelection_without_fallback()
    {
        using var f = new NavalFixture((int)(End - Start).TotalDays, Start, i => new(Start.AddDays(i), 100, 101, 99, 100, 1000000, true));
        var result = NavalWalkForward.Run(Spec(f), new[] { Fold });
        var fold = Assert.Single(result.Folds);
        Assert.Null(fold.Selection.Key); Assert.Equal("NoSelection", fold.Selection.Status);
        Assert.Null(fold.Oos); Assert.Equal(3, result.RunCount);
    }
    [Fact]
    public void Budget_refuses_before_overshooting_and_folds_match_frozen_periods()
    {
        Assert.Equal(3, NavalWalkForward.FrozenFolds.Count);
        Assert.Equal(DateTimeOffset.Parse("2026-10-29T00:00:00Z"), NavalWalkForward.FrozenFolds[^1].OosTo);
        var budget = new NavalRunBudget(1); budget.Take();
        Assert.Throws<InvalidOperationException>(() => budget.Take()); Assert.Equal(1, budget.Count);
        Assert.Throws<ArgumentException>(() => new NavalRunBudget(151));
    }
}
