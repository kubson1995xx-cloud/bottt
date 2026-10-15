using System;
using System.Globalization;
using Apex.Core.Model;
using Apex.Core.Regime;
using Apex.Core.Strategies;

namespace Apex.Core.Scanner
{
    public sealed class ScoringSettings
    {
        public double WeightMtfAlignment { get; init; } = 20;
        public double WeightTrendStrength { get; init; } = 15;
        public double WeightMomentum { get; init; } = 10;
        public double WeightVolatility { get; init; } = 10;
        public double WeightCost { get; init; } = 20;
        public double WeightRewardRisk { get; init; } = 10;
        public double WeightRegimeFit { get; init; } = 15;
        /// <summary>Koszt (spread+prowizja+poślizg) jako ułamek odległości SL, przy którym składowa kosztu = 0.</summary>
        public double MaxCostToRisk { get; init; } = 0.20;
        /// <summary>Korelacja ze stratą punktów – próg i siła kary.</summary>
        public double CorrelationPenaltyStart { get; init; } = 0.5;
        public double CorrelationPenaltyPerUnit { get; init; } = 60;
        public double MinScore { get; init; } = 50;

        public double TotalWeight => WeightMtfAlignment + WeightTrendStrength + WeightMomentum + WeightVolatility + WeightCost + WeightRewardRisk + WeightRegimeFit;
    }

    /// <summary>Rozbicie oceny okazji na składowe – zapisywane w dzienniku, aby ranking był audytowalny.</summary>
    public sealed class ScoreBreakdown
    {
        public double MtfAlignment { get; init; }
        public double TrendStrength { get; init; }
        public double Momentum { get; init; }
        public double Volatility { get; init; }
        public double Cost { get; init; }
        public double RewardRisk { get; init; }
        public double RegimeFit { get; init; }
        public double CorrelationPenalty { get; init; }
        public double CostToRisk { get; init; }
        public double Total { get; init; }

        public string Describe() => string.Format(CultureInfo.InvariantCulture,
            "MTF {0:F2} | Trend {1:F2} | Mom {2:F2} | Zmienn {3:F2} | Koszt {4:F2} ({8:P0} R) | RR {5:F2} | Reżim {6:F2} | Korel -{7:F1} => {9:F1}",
            MtfAlignment, TrendStrength, Momentum, Volatility, Cost, RewardRisk, RegimeFit, CorrelationPenalty, CostToRisk, Total);
    }

    /// <summary>
    /// Ocena okazji 0–100 jako ważona suma jawnych składowych (każda 0..1).
    /// UWAGA: wynik jest wyłącznie RANKINGIEM jakości warunków według reguł – NIE jest prawdopodobieństwem zysku.
    /// Czy wyższy wynik przekłada się na lepsze rezultaty, weryfikuje backtest (wyniki wg przedziałów oceny).
    /// </summary>
    public static class OpportunityScorer
    {
        public static double Clamp01(double x) => double.IsNaN(x) ? 0 : Math.Max(0, Math.Min(1, x));

        /// <param name="costToRisk">Koszt transakcji (round-trip) / odległość SL.</param>
        /// <param name="maxSignedCorrelation">Maksymalna korelacja (ze znakiem kierunku) z otwartymi pozycjami, NaN gdy brak.</param>
        public static ScoreBreakdown Score(SignalCandidate c, double costToRisk, double maxSignedCorrelation, ScoringSettings s)
        {
            int dir = (int)c.Direction;
            var fr = c.FilterRegime;
            var sr = c.SignalRegime;
            bool meanRev = c.Strategy == StrategyId.MeanReversion || c.Strategy == StrategyId.IndexDipBuy;

            double mtf;
            if (c.Strategy == StrategyId.MeanReversion)
                mtf = fr.Regime == MarketRegime.Range ? 1.0 : 0.3;
            else if (c.Strategy == StrategyId.IndexDipBuy)
                mtf = sr.TrendDirection > 0 ? 1.0 : 0.5;
            else
                mtf = fr.TrendDirection == dir ? 1.0 : fr.TrendDirection == 0 ? 0.5 : 0.0;

            double trend = c.Strategy == StrategyId.MeanReversion
                ? Clamp01((30.0 - sr.Adx) / 15.0)
                : c.Strategy == StrategyId.IndexDipBuy ? (sr.TrendDirection > 0 ? 1.0 : 0.5)
                : Clamp01((sr.Adx - 15.0) / 20.0);

            double mom = meanRev
                ? Clamp01(0.5 - c.MomentumZ / 3.0)
                : Clamp01(0.5 + c.MomentumZ / 3.0);

            var vs = c.Strategy == StrategyId.IndexDipBuy ? sr.Volatility : fr.Volatility;
            double vol = vs switch
            {
                VolatilityState.Normal => 1.0,
                VolatilityState.Low => c.Strategy == StrategyId.DonchianBreakout || c.Strategy == StrategyId.DailyTrend ? 0.8 : 0.5,
                VolatilityState.High => 0.4,
                _ => 0.3,
            };

            double cost = Clamp01(1.0 - costToRisk / s.MaxCostToRisk);
            double rr = Clamp01((c.PlannedRewardRisk - 0.8) / 2.2);
            double fit = RegimeFit(c.Strategy, dir, fr, sr);

            double raw = s.WeightMtfAlignment * mtf + s.WeightTrendStrength * trend + s.WeightMomentum * mom +
                         s.WeightVolatility * vol + s.WeightCost * cost + s.WeightRewardRisk * rr + s.WeightRegimeFit * fit;
            double total = s.TotalWeight > 0 ? raw / s.TotalWeight * 100.0 : 0;

            double penalty = 0;
            if (!double.IsNaN(maxSignedCorrelation) && maxSignedCorrelation > s.CorrelationPenaltyStart)
                penalty = (maxSignedCorrelation - s.CorrelationPenaltyStart) * s.CorrelationPenaltyPerUnit;
            total = Math.Max(0, Math.Min(100, total - penalty));

            return new ScoreBreakdown
            {
                MtfAlignment = mtf,
                TrendStrength = trend,
                Momentum = mom,
                Volatility = vol,
                Cost = cost,
                RewardRisk = rr,
                RegimeFit = fit,
                CorrelationPenalty = penalty,
                CostToRisk = costToRisk,
                Total = total,
            };
        }

        public static double RegimeFit(StrategyId id, int dir, RegimeAssessment fr, RegimeAssessment sr)
        {
            switch (id)
            {
                case StrategyId.TrendPullback:
                case StrategyId.MomentumImpulse:
                    if (fr.TrendDirection != dir) return 0.0;
                    return fr.Regime == MarketRegime.StrongUptrend || fr.Regime == MarketRegime.StrongDowntrend ? 1.0 : 0.7;
                case StrategyId.DonchianBreakout:
                case StrategyId.DailyTrend:
                    if (fr.TrendDirection == -dir) return 0.0;
                    if (fr.Regime == MarketRegime.Range && fr.Volatility == VolatilityState.Low) return 1.0;
                    return fr.TrendDirection == dir ? 0.8 : 0.5;
                case StrategyId.MeanReversion:
                    return fr.Regime == MarketRegime.Range ? 1.0 : 0.2;
                case StrategyId.IndexDipBuy:
                    return sr.TrendDirection > 0 ? 1.0 : 0.4;
                default:
                    return 0;
            }
        }
    }
}
