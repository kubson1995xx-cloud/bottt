using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Apex.Core.Model;

namespace Apex.Core.Performance
{
    /// <summary>Rekord zamkniętej transakcji – wiersz dziennika.</summary>
    public sealed class TradeRecord
    {
        public long PositionId { get; set; }
        public string SignalId { get; set; } = "";
        public string Symbol { get; set; } = "";
        public AssetClass AssetClass { get; set; }
        public StrategyId Strategy { get; set; }
        public string Variant { get; set; } = "";
        public Tf TimeFrame { get; set; }
        public TradeDirection Direction { get; set; }
        public DateTime EntryTimeUtc { get; set; }
        public DateTime ExitTimeUtc { get; set; }
        public double EntryPrice { get; set; }
        public double ExitPrice { get; set; }
        public double Volume { get; set; }
        public double InitialRiskMoney { get; set; }
        public double GrossProfit { get; set; }
        public double Commission { get; set; }
        public double Swap { get; set; }
        public double NetProfit { get; set; }
        /// <summary>Wynik netto w wielokrotnościach ryzyka początkowego.</summary>
        public double RMultiple => InitialRiskMoney > 0 && double.IsFinite(InitialRiskMoney) ? NetProfit / InitialRiskMoney : double.NaN;
        public string EntryReason { get; set; } = "";
        public string ExitReason { get; set; } = "";
        public string Regime { get; set; } = "";
        public double Score { get; set; }
        public string Parameters { get; set; } = "";
        public int BarsHeld { get; set; }

        public static string CsvHeader =>
            "position_id;signal_id;symbol;asset_class;strategy;variant;timeframe;direction;entry_time_utc;exit_time_utc;entry_price;exit_price;" +
            "volume;initial_risk;gross_profit;commission;swap;net_profit;r_multiple;bars_held;score;regime;entry_reason;exit_reason;parameters";

        public string ToCsv()
        {
            var ci = CultureInfo.InvariantCulture;
            return string.Join(";", new[]
            {
                PositionId.ToString(ci), SignalId, Symbol, AssetClass.ToString(), Strategy.ToString(), Csv(Variant), TimeFrame.ToString(),
                Direction.ToString(), EntryTimeUtc.ToString("yyyy-MM-dd HH:mm:ss", ci), ExitTimeUtc.ToString("yyyy-MM-dd HH:mm:ss", ci),
                EntryPrice.ToString("G10", ci), ExitPrice.ToString("G10", ci), Volume.ToString("G10", ci), InitialRiskMoney.ToString("F2", ci),
                GrossProfit.ToString("F2", ci), Commission.ToString("F2", ci), Swap.ToString("F2", ci), NetProfit.ToString("F2", ci),
                double.IsFinite(RMultiple) ? RMultiple.ToString("F3", ci) : "", BarsHeld.ToString(ci), Score.ToString("F1", ci), Csv(Regime), Csv(EntryReason), Csv(ExitReason), Csv(Parameters),
            });
        }

        private static string Csv(string s) => (s ?? "").Replace(";", ",").Replace("\r", " ").Replace("\n", " ");
    }

    public sealed class PerformanceStats
    {
        public string Name { get; init; } = "";
        public int Trades { get; init; }
        public int Wins { get; init; }
        public double WinRate { get; init; }
        public double NetProfit { get; init; }
        public double GrossWin { get; init; }
        public double GrossLoss { get; init; }
        public double ProfitFactor { get; init; }
        public double ExpectancyMoney { get; init; }
        public double ExpectancyR { get; init; }
        /// <summary>Błąd standardowy średniego R (do oceny istotności).</summary>
        public double StdErrR { get; init; }
        public double AvgWin { get; init; }
        public double AvgLoss { get; init; }
        public double PayoffRatio { get; init; }
        public double MaxDrawdownMoney { get; init; }
        public double MaxDrawdownPct { get; init; }
        public double Sharpe { get; init; }
        public double Sqn { get; init; }
        public double TotalCommission { get; init; }
        public double TotalSwap { get; init; }
        public double AvgBarsHeld { get; init; }

        /// <summary>t-statystyka średniego R.</summary>
        public double TStat => StdErrR > 0 ? ExpectancyR / StdErrR : 0;

        public string ToLine() => string.Format(CultureInfo.InvariantCulture,
            "{0,-28} n={1,5} WR={2,5:P1} PF={3,5:F2} E={4,7:F3}R (t={5,5:F2}) netto={6,12:F2} DD={7,6:F2}% Sharpe={8,5:F2} AvgW/AvgL={9,5:F2}",
            Name, Trades, WinRate, ProfitFactor, ExpectancyR, TStat, NetProfit, MaxDrawdownPct, Sharpe, PayoffRatio);
    }

    /// <summary>
    /// Analiza wyników: Win Rate, Profit Factor, Expectancy (pieniądze i R), Max DD, Sharpe (z dziennych wyników),
    /// średni zysk/strata, SQN; grupowanie po strategii, instrumencie, klasie aktywów, reżimie i przedziale oceny.
    /// </summary>
    public static class PerformanceAnalyzer
    {
        public static PerformanceStats Compute(string name, IReadOnlyList<TradeRecord> trades, double startingEquity)
        {
            if (trades.Count == 0) return new PerformanceStats { Name = name };
            var ordered = trades.OrderBy(t => t.ExitTimeUtc).ToList();
            int n = ordered.Count;
            var wins = ordered.Where(t => t.NetProfit > 0).ToList();
            var losses = ordered.Where(t => t.NetProfit <= 0).ToList();
            double grossWin = wins.Sum(t => t.NetProfit);
            double grossLoss = -losses.Sum(t => t.NetProfit);
            double net = grossWin - grossLoss;
            var rs = ordered.Select(t => t.RMultiple).Where(double.IsFinite).ToList();
            double meanR = rs.Count > 0 ? rs.Average() : 0;
            double sdR = rs.Count > 1 ? Math.Sqrt(rs.Sum(r => (r - meanR) * (r - meanR)) / (rs.Count - 1)) : 0;

            double equity = startingEquity, peak = startingEquity, maxDd = 0, maxDdPct = 0;
            foreach (var t in ordered)
            {
                equity += t.NetProfit;
                if (equity > peak) peak = equity;
                double dd = peak - equity;
                if (dd > maxDd) maxDd = dd;
                if (peak > 0 && dd / peak > maxDdPct) maxDdPct = dd / peak;
            }

            double sharpe = DailySharpe(ordered, startingEquity);
            double avgWin = wins.Count > 0 ? grossWin / wins.Count : 0;
            double avgLoss = losses.Count > 0 ? grossLoss / losses.Count : 0;
            return new PerformanceStats
            {
                Name = name,
                Trades = n,
                Wins = wins.Count,
                WinRate = (double)wins.Count / n,
                NetProfit = net,
                GrossWin = grossWin,
                GrossLoss = grossLoss,
                ProfitFactor = grossLoss > 0 ? grossWin / grossLoss : (grossWin > 0 ? double.PositiveInfinity : 0),
                ExpectancyMoney = net / n,
                ExpectancyR = meanR,
                StdErrR = n > 1 ? sdR / Math.Sqrt(n) : 0,
                AvgWin = avgWin,
                AvgLoss = avgLoss,
                PayoffRatio = avgLoss > 0 ? avgWin / avgLoss : 0,
                MaxDrawdownMoney = maxDd,
                MaxDrawdownPct = maxDdPct * 100.0,
                Sharpe = sharpe,
                Sqn = sdR > 0 ? Math.Sqrt(Math.Min(n, 100)) * meanR / sdR : 0,
                TotalCommission = ordered.Sum(t => t.Commission),
                TotalSwap = ordered.Sum(t => t.Swap),
                AvgBarsHeld = ordered.Average(t => t.BarsHeld),
            };
        }

        /// <summary>
        /// Sharpe z dziennych stóp zwrotu (wynik zamkniętych transakcji przypisany do dnia zamknięcia, dni bez transakcji = 0),
        /// annualizowany √252. Przy rzadkich transakcjach jest to przybliżenie – raport podaje też t-statystykę R.
        /// </summary>
        public static double DailySharpe(IReadOnlyList<TradeRecord> ordered, double startingEquity)
        {
            if (ordered.Count < 2 || startingEquity <= 0) return 0;
            var first = ordered.Min(t => t.EntryTimeUtc).Date;
            var last = ordered.Max(t => t.ExitTimeUtc).Date;
            var byDay = ordered.GroupBy(t => t.ExitTimeUtc.Date).ToDictionary(g => g.Key, g => g.Sum(t => t.NetProfit));
            var rets = new List<double>();
            double eq = startingEquity;
            for (var d = first; d <= last; d = d.AddDays(1))
            {
                if (d.DayOfWeek == DayOfWeek.Saturday || d.DayOfWeek == DayOfWeek.Sunday) { if (byDay.TryGetValue(d, out var wk)) eq += wk; continue; }
                byDay.TryGetValue(d, out var pnl);
                rets.Add(eq > 0 ? pnl / eq : 0);
                eq += pnl;
            }
            if (rets.Count < 2) return 0;
            double mean = rets.Average();
            double sd = Math.Sqrt(rets.Sum(r => (r - mean) * (r - mean)) / (rets.Count - 1));
            return sd > 0 ? mean / sd * Math.Sqrt(252) : 0;
        }

        public static List<PerformanceStats> GroupBy(IReadOnlyList<TradeRecord> trades, Func<TradeRecord, string> key, double startingEquity) =>
            trades.GroupBy(key).OrderBy(g => g.Key).Select(g => Compute(g.Key, g.ToList(), startingEquity)).ToList();

        public static string ScoreBucket(double score) =>
            score < 50 ? "ocena <50" : score < 60 ? "ocena 50-60" : score < 70 ? "ocena 60-70" : score < 80 ? "ocena 70-80" : "ocena 80+";

        public static string Report(IReadOnlyList<TradeRecord> trades, double startingEquity)
        {
            var sb = new StringBuilder();
            sb.AppendLine(Compute("RAZEM", trades, startingEquity).ToLine());
            sb.AppendLine("-- wg strategii --");
            foreach (var s in GroupBy(trades, t => t.Strategy.ToString(), startingEquity)) sb.AppendLine(s.ToLine());
            sb.AppendLine("-- wg klasy aktywów --");
            foreach (var s in GroupBy(trades, t => t.AssetClass.ToString(), startingEquity)) sb.AppendLine(s.ToLine());
            sb.AppendLine("-- wg instrumentu --");
            foreach (var s in GroupBy(trades, t => t.Symbol, startingEquity)) sb.AppendLine(s.ToLine());
            sb.AppendLine("-- wg reżimu przy wejściu --");
            foreach (var s in GroupBy(trades, t => t.Regime, startingEquity)) sb.AppendLine(s.ToLine());
            sb.AppendLine("-- wg oceny okazji --");
            foreach (var s in GroupBy(trades, t => ScoreBucket(t.Score), startingEquity)) sb.AppendLine(s.ToLine());
            return sb.ToString();
        }
    }
}
