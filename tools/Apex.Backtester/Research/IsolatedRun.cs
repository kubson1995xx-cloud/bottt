using System;
using System.Collections.Generic;
using System.Linq;
using Apex.Backtester.Sim;
using Apex.Core.Adaptive;
using Apex.Core.Performance;

namespace Apex.Backtester.Research
{
    /// <summary>One fresh account, with natural outcomes kept separate from complete money evidence.</summary>
    public sealed class IsolatedRun
    {
        public int RunId { get; init; }
        public string Symbol { get; init; } = "";
        public string ComboKey { get; init; } = "";
        public string ComboLabel { get; init; } = "";
        public ProfileEntry Entry { get; init; } = null!;
        public string Purpose { get; init; } = "isolated";
        public int? FoldYear { get; init; }
        public DateTime From { get; init; }
        public DateTime To { get; init; }
        public CostScenario Cost { get; init; } = null!;
        public BacktestResult Account { get; init; } = null!;
        public string EvidenceFile { get; init; } = "";
        public List<TradeRecord> NaturalTrades => Account.Trades.Where(t => !Account.BoundaryPositionIds.Contains(t.PositionId)).ToList();

        public static object TradeStats(IReadOnlyList<TradeRecord> trades, double startEquity)
        {
            var stats = PerformanceAnalyzer.Compute("natural outcomes only", trades, startEquity);
            return new { trades = trades.Count, net = stats.NetProfit, gross = trades.Sum(t => t.GrossProfit),
                commission = trades.Sum(t => t.Commission), swap = trades.Sum(t => t.Swap),
                meanR = stats.ExpectancyR, t = stats.TStat, winRate = stats.WinRate,
                profitFactor = double.IsFinite(stats.ProfitFactor) ? (double?)stats.ProfitFactor : null,
                closedTradeMaxDrawdownPct = stats.MaxDrawdownPct };
        }

        // No compounding or pooled portfolio DD: each symbol/fold is a separately funded account.
        public static object AccountTotals(IEnumerable<IsolatedRun> source)
        {
            var runs = source.ToList();
            var trades = runs.SelectMany(r => r.Account.Trades).ToList();
            return new { basis = "sum of independent accounts; not a compounded portfolio", runs = runs.Count,
                runIds = runs.Select(r => r.RunId).ToArray(),
                startEquity = runs.Sum(r => r.Account.StartEquity), endEquity = runs.Sum(r => r.Account.EndEquity),
                net = trades.Sum(t => t.NetProfit), gross = trades.Sum(t => t.GrossProfit),
                commission = trades.Sum(t => t.Commission), swap = trades.Sum(t => t.Swap), trades = trades.Count,
                boundaryLiquidations = runs.Sum(r => r.Account.BoundaryPositionIds.Count),
                sampledDrawdownByRun = runs.Select(r => new { r.RunId, r.Account.MaxEquityDrawdownPct }).ToArray() };
        }

        public object ToReport() => new { RunId, Symbol, ComboKey, ComboLabel, strategy = Entry.Strategy.ToString(),
            assetClass = Entry.AssetClass.ToString(), Entry, Purpose, FoldYear, From, To, Cost, EvidenceFile,
            naturalOutcomes = TradeStats(NaturalTrades, Account.StartEquity),
            account = new { Account.StartEquity, Account.EndEquity, Account.MaxEquityDrawdownPct,
                Account.EffectiveStartUtc, Account.LastExecutableOpenUtc, Account.EffectiveEndUtc,
                Account.Cycles, net = Account.Trades.Sum(t => t.NetProfit), gross = Account.Trades.Sum(t => t.GrossProfit),
                commission = Account.Trades.Sum(t => t.Commission), swap = Account.Trades.Sum(t => t.Swap),
                Account.BoundaryPositionIds, Account.Trades, Account.Cashflows,
                dailyEquity = Account.DailyEquity.Select(e => new { time = e.Time, equity = e.Equity }).ToArray() } };
    }
}
