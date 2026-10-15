using System;
using Apex.Core.Indicators;
using Apex.Core.Model;

namespace Apex.Core.Regime
{
    public sealed class RegimeSettings
    {
        public int AdxPeriod { get; init; } = 14;
        public int EmaFast { get; init; } = 50;
        public int EmaSlow { get; init; } = 200;
        public int AtrPeriod { get; init; } = 14;
        public int ErPeriod { get; init; } = 20;
        /// <summary>Okno do wyznaczenia percentyla ATR (reżim zmienności).</summary>
        public int AtrPercentileLookback { get; init; } = 120;
        public double StrongTrendAdx { get; init; } = 25;
        public double TrendAdx { get; init; } = 20;
        public double RangeAdxMax { get; init; } = 20;
        public double LowVolPercentile { get; init; } = 0.15;
        public double HighVolPercentile { get; init; } = 0.90;
        /// <summary>Świeca o zasięgu większym niż X*ATR = warunki chaotyczne.</summary>
        public double ChaosBarAtrMultiple { get; init; } = 4.0;
        /// <summary>Gwałtowny skok zmienności: ATR(5)/ATR(50) powyżej progu = warunki chaotyczne.</summary>
        public double ChaosAtrRatio { get; init; } = 2.2;
        public int SlopeBars { get; init; } = 5;

        public int RequiredBars => Math.Max(EmaSlow + 10, Math.Max(AtrPercentileLookback + AtrPeriod + 5, 60));
    }

    /// <summary>Wynik oceny reżimu rynku na danym interwale.</summary>
    public sealed class RegimeAssessment
    {
        public MarketRegime Regime { get; init; }
        public VolatilityState Volatility { get; init; }
        /// <summary>+1 trend wzrostowy, -1 spadkowy, 0 brak.</summary>
        public int TrendDirection { get; init; }
        /// <summary>Siła trendu 0..1 (na podstawie ADX).</summary>
        public double TrendStrength { get; init; }
        public double Adx { get; init; }
        public double PlusDi { get; init; }
        public double MinusDi { get; init; }
        public double EfficiencyRatio { get; init; }
        public double AtrPercentile { get; init; }
        public double Atr { get; init; }
        public double EmaFast { get; init; }
        public double EmaSlow { get; init; }
        /// <summary>Nachylenie EMA szybkiej w jednostkach ATR na świecę.</summary>
        public double FastSlopeAtr { get; init; }
        public string Reason { get; init; } = "";

        public bool IsTrendUp => Regime == MarketRegime.StrongUptrend || Regime == MarketRegime.WeakUptrend;
        public bool IsTrendDown => Regime == MarketRegime.StrongDowntrend || Regime == MarketRegime.WeakDowntrend;
        public bool IsTradeable => Regime != MarketRegime.Unknown && Regime != MarketRegime.Chaotic;

        public static RegimeAssessment Unknown(string reason) => new() { Regime = MarketRegime.Unknown, Reason = reason };

        public string PolishName => Regime switch
        {
            MarketRegime.StrongUptrend => "Silny trend wzrostowy",
            MarketRegime.StrongDowntrend => "Silny trend spadkowy",
            MarketRegime.WeakUptrend => "Słaby trend wzrostowy",
            MarketRegime.WeakDowntrend => "Słaby trend spadkowy",
            MarketRegime.Range => "Konsolidacja",
            MarketRegime.Chaotic => "Warunki chaotyczne",
            _ => "Nieznany / niekorzystny",
        };
    }

    /// <summary>
    /// Klasyfikator reżimu rynku. Reguły są jawne i deterministyczne:
    /// 1) chaos (skok zmienności / ekstremalna świeca) ma pierwszeństwo – brak handlu,
    /// 2) kierunek trendu z ułożenia ceny i EMA50/EMA200,
    /// 3) siła trendu z ADX oraz zgodności DI i nachylenia EMA,
    /// 4) konsolidacja przy niskim ADX,
    /// 5) reżim zmienności z percentyla ATR.
    /// </summary>
    public static class MarketRegimeDetector
    {
        public static RegimeAssessment Assess(FeatureSet f, int i, RegimeSettings s)
        {
            if (i < 0 || i >= f.Count) return RegimeAssessment.Unknown("brak świecy");
            if (i + 1 < s.RequiredBars) return RegimeAssessment.Unknown($"za mało danych ({i + 1}/{s.RequiredBars})");

            var adxR = f.Adx(s.AdxPeriod);
            double adx = adxR.Adx[i], pdi = adxR.PlusDi[i], mdi = adxR.MinusDi[i];
            double emaF = f.Ema(s.EmaFast)[i];
            double emaS = f.Ema(s.EmaSlow)[i];
            double emaFPrev = Ta.At(f.Ema(s.EmaFast), i - s.SlopeBars);
            var atrArr = f.Atr(s.AtrPeriod);
            double atr = atrArr[i];
            double er = f.Er(s.ErPeriod)[i];
            double close = f.Close[i];
            double atr5 = f.Atr(5)[i];
            double atr50 = f.Atr(50)[i];
            double tr = f.TrueRange()[i];

            if (!Ta.AllFinite(adx, pdi, mdi, emaF, emaS, emaFPrev, atr, er, close, atr5, atr50, tr) || atr <= 0)
                return RegimeAssessment.Unknown("niepełne dane wskaźników");

            double pct = Ta.PercentRank(atrArr, i, s.AtrPercentileLookback);
            var vol = double.IsNaN(pct) ? VolatilityState.Unknown
                : pct < s.LowVolPercentile ? VolatilityState.Low
                : pct > s.HighVolPercentile ? VolatilityState.High
                : VolatilityState.Normal;

            double slope = (emaF - emaFPrev) / (s.SlopeBars * atr);
            int dir = 0;
            if (close > emaS && emaF > emaS) dir = 1;
            else if (close < emaS && emaF < emaS) dir = -1;
            double strength = Math.Max(0, Math.Min(1, (adx - 15.0) / 20.0));

            bool chaosBar = tr > s.ChaosBarAtrMultiple * atr50;
            bool chaosVol = atr50 > 0 && atr5 / atr50 > s.ChaosAtrRatio;
            MarketRegime regime;
            string reason;
            if (chaosBar || chaosVol)
            {
                regime = MarketRegime.Chaotic;
                reason = chaosBar ? $"świeca {tr / atr50:F1}x ATR50" : $"ATR5/ATR50={atr5 / atr50:F2}";
            }
            else if (dir != 0 && adx >= s.StrongTrendAdx && Math.Sign(slope) == dir && Math.Sign(pdi - mdi) == dir)
            {
                regime = dir > 0 ? MarketRegime.StrongUptrend : MarketRegime.StrongDowntrend;
                reason = $"ADX={adx:F1}, EMA{s.EmaFast}/{s.EmaSlow} zgodne, nachylenie={slope:F3}";
            }
            else if (dir != 0 && adx >= s.TrendAdx && Math.Sign(slope) == dir)
            {
                regime = dir > 0 ? MarketRegime.WeakUptrend : MarketRegime.WeakDowntrend;
                reason = $"ADX={adx:F1}, trend umiarkowany";
            }
            else if (adx < s.RangeAdxMax)
            {
                regime = MarketRegime.Range;
                reason = $"ADX={adx:F1} < {s.RangeAdxMax}, ER={er:F2}";
            }
            else
            {
                // ADX wysoki, ale kierunek niejednoznaczny (np. przejście trendu) – traktujemy jako słaby trend tylko gdy jest kierunek.
                regime = dir > 0 ? MarketRegime.WeakUptrend : dir < 0 ? MarketRegime.WeakDowntrend : MarketRegime.Range;
                reason = $"ADX={adx:F1}, kierunek niejednoznaczny";
            }

            return new RegimeAssessment
            {
                Regime = regime,
                Volatility = vol,
                TrendDirection = dir,
                TrendStrength = strength,
                Adx = adx,
                PlusDi = pdi,
                MinusDi = mdi,
                EfficiencyRatio = er,
                AtrPercentile = pct,
                Atr = atr,
                EmaFast = emaF,
                EmaSlow = emaS,
                FastSlopeAtr = slope,
                Reason = reason,
            };
        }
    }
}
