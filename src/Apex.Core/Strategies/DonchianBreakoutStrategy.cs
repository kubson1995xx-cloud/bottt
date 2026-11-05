using System;
using Apex.Core.Indicators;
using Apex.Core.Model;

namespace Apex.Core.Strategies
{
    public sealed class DonchianBreakoutParams
    {
        public Tf SignalTf { get; init; } = Tf.H4;
        public Tf FilterTf { get; init; } = Tf.D1;
        public int Channel { get; init; } = 20;
        public int ExitChannel { get; init; } = 10;
        public int AtrPeriod { get; init; } = 14;
        /// <summary>Wymagane domknięcie poza kanałem o co najmniej X*ATR (ochrona przed fałszywym wybiciem).</summary>
        public double MinCloseBeyondAtr { get; init; } = 0.1;
        /// <summary>Minimalny udział korpusu w zasięgu świecy wybicia (silne zamknięcie).</summary>
        public double MinBodyFraction { get; init; } = 0.5;
        /// <summary>0 = bez filtra, 1 = nie przeciw trendowi wyższego TF, 2 = tylko zgodnie z trendem wyższego TF.</summary>
        public int FilterMode { get; init; } = 2;
        /// <summary>Wymagana kompresja zmienności przed wybiciem (ATR ostatnich świec poniżej X*ATR długiego).</summary>
        public double MaxPreBreakoutAtrRatio { get; init; } = 0;
        public double StopAtr { get; init; } = 2.5;
        /// <summary>Okno, w którym poprzednie nieudane wybicie w tym samym kierunku blokuje sygnał.</summary>
        public int FailedBreakoutLookback { get; init; } = 10;
        public ExitPlan Exit { get; init; } = new() { TakeProfitR = null, TrailStartR = 1.0, TrailAtrMultiple = 3.0, ExitOnTrendReversal = false };
    }

    /// <summary>
    /// Wybicie kanału Donchiana (klasyczny trend following typu "Turtle"):
    /// zamknięcie powyżej maksimum z N poprzednich świec (bez bieżącej), pierwsze zamknięcie poza kanałem,
    /// silny korpus, zgodność z trendem wyższego TF. Fałszywe wybicia odfiltrowane przez wymóg domknięcia o X*ATR
    /// oraz blokadę po niedawnym nieudanym wybiciu. Wyjście: trailing ATR lub przebicie kanału wyjścia (M świec).
    /// </summary>
    public class DonchianBreakoutStrategy : StrategyBase
    {
        private readonly DonchianBreakoutParams _p;

        public DonchianBreakoutStrategy() : this(new DonchianBreakoutParams()) { }

        public DonchianBreakoutStrategy(DonchianBreakoutParams p, string variant = "default")
            : base(p.SignalTf, p.FilterTf, p.Exit, variant)
        {
            _p = p;
        }

        public DonchianBreakoutParams Params => _p;
        public override StrategyId Id => StrategyId.DonchianBreakout;
        public override int RequiredSignalBars => Math.Max(_p.Channel + _p.FailedBreakoutLookback + 60, 120);

        public override bool AppliesTo(AssetClass c) => c != AssetClass.Crypto && c != AssetClass.Unknown;

        public override SignalCandidate? Evaluate(StrategyInput input)
        {
            if (!BasicDataOk(input, RequiredSignalBars)) return null;
            if (!input.SignalRegime.IsTradeable) return null;
            if (_p.FilterMode > 0 && !input.FilterRegime.IsTradeable) return null;

            var f = input.Signal;
            int i = input.SignalIndex;
            var hh = f.HighestHigh(_p.Channel);
            var ll = f.LowestLow(_p.Channel);
            var atrA = f.Atr(_p.AtrPeriod);
            double atr = atrA[i];
            double upper = hh[i - 1], lower = ll[i - 1];
            double upperPrev = hh[i - 2], lowerPrev = ll[i - 2];
            if (!Ta.AllFinite(atr, upper, lower, upperPrev, lowerPrev) || atr <= 0) return null;

            double close = f.Close[i], open = f.Open[i], high = f.High[i], low = f.Low[i];
            double range = high - low;
            if (range <= 0) return null;

            TradeDirection? dir = null;
            if (close > upper + _p.MinCloseBeyondAtr * atr && f.Close[i - 1] <= upperPrev) dir = TradeDirection.Long;
            else if (close < lower - _p.MinCloseBeyondAtr * atr && f.Close[i - 1] >= lowerPrev) dir = TradeDirection.Short;
            if (dir == null) return null;
            var d = dir.Value;
            int s = (int)d;

            double body = s * (close - open);
            if (body <= 0 || body / range < _p.MinBodyFraction) return null;

            int ftd = input.FilterRegime.TrendDirection;
            if (_p.FilterMode == 1 && ftd == -s) return null;
            if (_p.FilterMode == 2 && ftd != s) return null;

            if (_p.MaxPreBreakoutAtrRatio > 0)
            {
                double atrLong = Ta.At(f.Atr(50), i - 1);
                double atrShort = Ta.At(f.Atr(5), i - 1);
                if (!Ta.AllFinite(atrLong, atrShort) || atrLong <= 0 || atrShort / atrLong > _p.MaxPreBreakoutAtrRatio) return null;
            }

            // Blokada po nieudanym wybiciu: w ostatnich M świecach cena zamknęła poza kanałem i wróciła do środka.
            for (int j = i - _p.FailedBreakoutLookback; j < i - 1; j++)
            {
                if (j < 2) continue;
                double lvl = s > 0 ? hh[j - 1] : ll[j - 1];
                if (double.IsNaN(lvl)) continue;
                bool brokeOut = s * (f.Close[j] - lvl) > 0;
                if (!brokeOut) continue;
                bool failed = false;
                for (int k = j + 1; k < i; k++)
                {
                    if (s * (f.Close[k] - lvl) < 0) { failed = true; break; }
                }
                if (failed) return null;
            }

            double stop = _p.StopAtr * atr;
            string reason = $"Wybicie kanału {_p.Channel} świec ({(d == TradeDirection.Long ? "górą" : "dołem")}), filtr {FilterTf}: {input.FilterRegime.PolishName}";
            return Build(input, d, stop, TpFromPlan(stop), atr, PlannedRr(2.5), reason, MomentumZ(f, i, 10, atr, d), DefaultValidity(SignalTf));
        }

        public override StrategyExit CheckExit(StrategyInput input, TradeDirection direction, double entryPrice, int barsHeld)
        {
            if (_p.ExitChannel <= 0) return StrategyExit.None;
            var f = input.Signal;
            int i = input.SignalIndex;
            if (f == null || i < _p.ExitChannel + 1) return StrategyExit.None;
            double lvl = direction == TradeDirection.Long ? f.LowestLow(_p.ExitChannel)[i - 1] : f.HighestHigh(_p.ExitChannel)[i - 1];
            if (double.IsNaN(lvl)) return StrategyExit.None;
            int s = (int)direction;
            if (s * (f.Close[i] - lvl) < 0)
                return new StrategyExit(true, $"Zamknięcie poza kanałem wyjścia {_p.ExitChannel} świec");
            return StrategyExit.None;
        }
    }

    /// <summary>
    /// Trend dzienny (runda 2 badań): wybicie kanału Donchiana na świecach D1 – klasyczny trend following
    /// w stylu "Turtle" / time-series momentum, z najsilniejszym udokumentowaniem w literaturze dla wielu klas aktywów.
    /// Ta sama logika co <see cref="DonchianBreakoutStrategy"/>, inny interwał i domyślne parametry; osobny identyfikator,
    /// aby wyniki i włączanie były niezależne.
    /// </summary>
    public sealed class DailyTrendStrategy : DonchianBreakoutStrategy
    {
        public static DonchianBreakoutParams Defaults => new()
        {
            SignalTf = Tf.D1,
            FilterTf = Tf.D1,
            Channel = 55,
            ExitChannel = 20,
            StopAtr = 2.0,
            FilterMode = 1,
            FailedBreakoutLookback = 0,
            MinBodyFraction = 0.3,
            Exit = new ExitPlan { TakeProfitR = null, TrailStartR = 1.0, TrailAtrMultiple = 3.0 },
        };

        public DailyTrendStrategy() : this(Defaults) { }

        public DailyTrendStrategy(DonchianBreakoutParams p, string variant = "default") : base(p, variant) { }

        public override StrategyId Id => StrategyId.DailyTrend;
    }
}
