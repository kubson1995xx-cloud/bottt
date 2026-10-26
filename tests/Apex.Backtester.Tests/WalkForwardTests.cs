using System.Reflection;
using Apex.Backtester.Data;
using Apex.Backtester.Research;
using Apex.Backtester.Sim;
using Apex.Core.Engine;
using Apex.Core.Model;
using Apex.Core.Performance;
using Xunit;

namespace Apex.Backtester.Tests;

public class WalkForwardTests
{
    static readonly DateTime Start = AccountingFxTests.T;
    static readonly DateTime Cut = Start.AddHours(65);
    static Combo C(int index, bool def = false) => new() { Strategy = StrategyId.DonchianBreakout, Index = new[] { index }, IsDefault = def };
    // Outcomes come from actual simulator fills/cashflows, never fabricated responses or archived CSVs.
    static ComboTrades Trades(Combo c, double change, bool crossing = false)
    {
        var ct = new ComboTrades { Combo = c };
        for (int i = 0; i < 32; i++)
        {
            var t = Start.AddHours(i * 2);
            var end = crossing ? Cut.AddHours(1 + i) : t.AddHours(1);
            var d = new InstrumentDef { Symbol = "TEST", QuoteCcy = "USD", PipSize = 1, Spread = 0, VolumeMin = 1 };
            var bars = AccountingFxTests.Bars("TEST", (t, 100), (end, 100 + change + (i % 2 == 0 ? 0.1 : -0.1)));
            var w = new SimWorld(new[] { new SymbolData(d, bars, null) }, new FxRates(), new CostScenario { SlippageMult = 0 }, 10000);
            w.OnClosed = x => ct.Trades.Add(new TradeRecord { PositionId = x.PositionId, Symbol = x.Symbol,
                EntryTimeUtc = x.EntryTimeUtc, ExitTimeUtc = x.ExitTimeUtc, NetProfit = x.NetProfit,
                InitialRiskMoney = 10 });
            w.SetTime(t); long id = AccountingFxTests.Open(w); w.SetTime(end); w.ClosePosition(id, null);
        }
        return ct;
    }
    static Combo? Select(List<ComboTrades> all) 
    {
        return WalkForward.Select(all, Start, Cut);
    }
    [Fact] public void IsEntryWithOosExitCannotInfluenceSelection()
    {
        var a = C(0, true); var b = C(5);
        var clean = Trades(a, 1); var crossing = Trades(b, 100, true);
        Assert.Same(a, Select(new() { clean, crossing }));
        crossing.Trades.ForEach(t => { t.NetProfit = -9990; });
        Assert.Same(a, Select(new() { clean, crossing }));
    }
    [Fact] public void OosOnlyCandidateCannotBecomeTrainingOrDefaultFallback()
    {
        var a = C(0, true); var ct = Trades(a, 100, true);
        ct.Trades.ForEach(t => t.EntryTimeUtc = Cut);
        Assert.Null(Select(new() { ct }));
    }
    [Fact] public void MissingNeighborsNeverRewardANegativeObjective()
    {
        var a = C(0); var missing = new ComboTrades { Combo = C(1) };
        var b = C(5, true);
        Assert.Same(b, Select(new() { Trades(a, -2), missing, Trades(b, -1.5) }));
    }
    [Fact] public void ReusingSelectorAfterLaterFoldCannotCarryFutureOutcomes()
    {
        var a = C(0, true); var b = C(5);
        Assert.Same(a, Select(new() { Trades(a, 2), Trades(b, 1) }));
        Assert.Same(b, Select(new() { Trades(a, -1), Trades(b, 4) }));
        Assert.Same(a, Select(new() { Trades(a, 2), Trades(b, 1) }));
    }
}
