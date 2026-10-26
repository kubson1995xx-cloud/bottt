using Apex.Backtester.Data;
using Apex.Backtester.Research;
using Apex.Backtester.Sim;
using Apex.Core.Adaptive;
using Apex.Core.Model;
using Xunit;

namespace Apex.Backtester.Tests;

public class RunnerIsolationTests
{
    static readonly DateTime Origin = new(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    static List<Bar> History(bool hostileFuture)
    {
        var rows = new List<Bar>(); double price = 4000;
        var rng = new Random(3);
        for (int day = 0; day < 315; day++)
        {
            double open = price;
            double close = day >= 297 && day <= 299 ? open - 45 : open + 4 + (rng.NextDouble() - 0.5) * 20;
            if (day >= 307 && hostileFuture) close = open * 0.9;
            for (int hour = 0; hour < 24; hour++)
            {
                double o = open + (close - open) * hour / 24, c = open + (close - open) * (hour + 1) / 24;
                rows.Add(new Bar(Origin.AddDays(day).AddHours(hour), o, Math.Max(o,c) + 8, Math.Min(o,c) - 8, c, 1000));
            }
            price = close;
        }
        return rows;
    }
    static BacktestResult Run(List<Bar> bars, int from, int to)
    {
        var d = new SymbolData(Catalog.Get("US500"), BarSeries.FromBars("US500",Tf.H1,bars),null);
        return BacktestRunner.Run(new RunSpec { Symbols = new() { d }, From = Origin.AddDays(from), To = Origin.AddDays(to),
            Profile = new StrategyProfile { Entries = new() { new ProfileEntry { AssetClass = AssetClass.Index, Strategy = StrategyId.IndexDipBuy } } },
            ConfigFactory = ResearchPipeline.IsolatedConfig, StartEquity = 1000000 });
    }
    [Fact] public void FinalEvaluationBarIsProcessedBeforeBoundaryLiquidation()
    {
        var r = Run(History(false),300,301);
        Assert.NotEmpty(r.Trades);
        Assert.NotEmpty(r.BoundaryPositionIds);
        Assert.Equal(Origin.AddDays(301), r.Trades.Last().ExitTimeUtc);
    }
    [Fact] public void ActualRunnerUsesTrustedEngineAndLaterFoldCannotMutateEarlierResult()
    {
        var first = Run(History(false),300,307);
        Assert.NotEmpty(first.Trades); // non-vacuous actual StrategyBook→risk→SimWorld→journal execution
        var later = Run(History(true),307,312);
        var again = Run(History(false),300,307);
        var withFutureChanged = Run(History(true),300,307);
        Assert.Equal(first.Trades.Select(t => t.ToCsv()), again.Trades.Select(t => t.ToCsv()));
        Assert.Equal(first.Trades.Select(t => t.ToCsv()), withFutureChanged.Trades.Select(t => t.ToCsv()));
        Assert.Equal(first.EndEquity, withFutureChanged.EndEquity,8);
        Assert.Equal(first.StartEquity + first.Trades.Sum(t => t.NetProfit), first.EndEquity,6);
        Assert.Equal(168, first.Cycles);
    }
}
