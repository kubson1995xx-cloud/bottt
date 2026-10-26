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
    [Fact] public void ClosedSessionCutoffLiquidatesAtLastCloseUsingOnlyFxKnownThen()
    {
        var end = Origin.AddDays(301);
        BacktestResult ClosedRun(double futureFx)
        {
            var bars = History(false).Where(b => b.OpenTime < end || b.OpenTime >= Origin.AddDays(310)).ToList();
            var data = new SymbolData(Catalog.Get("GER40"),BarSeries.FromBars("GER40",Tf.H1,bars),null);
            var fx = new FxRates(TimeSpan.FromHours(2));
            fx.Add("EURUSD",BarSeries.FromBars("EURUSD",Tf.H1,bars.Select(b => new Bar(b.OpenTime,
                b.OpenTime < end ? 1.25 : futureFx,b.OpenTime < end ? 1.25 : futureFx,
                b.OpenTime < end ? 1.25 : futureFx,b.OpenTime < end ? 1.25 : futureFx)).ToList()));
            return BacktestRunner.Run(new RunSpec { Symbols=new() {data},From=Origin.AddDays(300),To=Origin.AddDays(307),Fx=fx,
                Profile=new StrategyProfile { Entries=new() {new ProfileEntry {AssetClass=AssetClass.Index,Strategy=StrategyId.IndexDipBuy}} },
                ConfigFactory=ResearchPipeline.IsolatedConfig,StartEquity=1000000 });
        }
        var first=ClosedRun(2); var hostile=ClosedRun(99);
        var boundary=Assert.Single(first.Trades, t => first.BoundaryPositionIds.Contains(t.PositionId));
        Assert.Equal(end,boundary.ExitTimeUtc);
        Assert.Equal(first.EndEquity,hostile.EndEquity,8);
        var report=System.Text.Json.JsonSerializer.SerializeToElement(first,DataManifest.JsonOptions);
        Assert.Equal(end,report.GetProperty("effectiveEndUtc").GetDateTime());
        Assert.Equal(end.AddHours(-1),report.GetProperty("lastExecutableOpenUtc").GetDateTime());
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
