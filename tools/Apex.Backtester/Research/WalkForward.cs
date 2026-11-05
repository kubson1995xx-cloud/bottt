using System;
using System.Collections.Generic;
using System.Linq;
using Apex.Core.Performance;

namespace Apex.Backtester.Research
{
    /// <summary>Stateless selection from outcomes available strictly before the IS cutoff.</summary>
    public static class WalkForward
    {
        public static IEnumerable<TradeRecord> Available(IEnumerable<TradeRecord> trades, DateTime from, DateTime to) =>
            trades.Where(t => t.EntryTimeUtc >= from && t.EntryTimeUtc < to &&
                t.ExitTimeUtc >= t.EntryTimeUtc && t.ExitTimeUtc < to &&
                t.InitialRiskMoney > 0 && double.IsFinite(t.RMultiple));

        public static Combo? Select(List<ComboTrades> training, DateTime from, DateTime to)
        {
            var objectives = training.ToDictionary(a => a.Combo.Key, a =>
            {
                var stats = ResearchPipeline.RStats(Available(a.Trades, from, to));
                return stats.n < 30 ? double.NegativeInfinity : stats.t;
            });
            Combo? best = null;
            double bestScore = double.NegativeInfinity;
            foreach (var candidate in training)
            {
                var c = candidate.Combo;
                double own = objectives[c.Key];
                if (!double.IsFinite(own)) continue; // neighbors cannot grant missing own evidence
                var neighbors = training.Where(a => a.Combo != c && a.Combo.Index.Length == c.Index.Length &&
                    a.Combo.Index.Zip(c.Index, (x, y) => Math.Abs(x - y)).Sum() == 1).ToList();
                var finite = neighbors.Select(a => objectives[a.Combo.Key]).Where(double.IsFinite).Prepend(own).ToList();
                int missing = neighbors.Count + 1 - finite.Count;
                // A missing outcome is worse than every observed score, including negative scores.
                double penalty = Math.Min(0, finite.Min()) - 1;
                double score = (finite.Sum() + missing * penalty) / (neighbors.Count + 1);
                if (score > bestScore || (score == bestScore && c.IsDefault)) { best = c; bestScore = score; }
            }
            return best; // no-data folds remain unselected, never an apparently validated default
        }
    }
}
