using System;
using Apex.Core.Indicators;
using Apex.Core.Model;

namespace Apex.Core.Strategies
{
    public sealed class TrendPullbackParams
    {
        public Tf SignalTf { get; init; } = Tf.H1;
        public Tf FilterTf { get; init; } = Tf.H4;
        public int EmaFast { get; init; } = 20;
        public int EmaMid { get; init; } = 50;
        public int AtrPeriod { get; init; } = 14;
        public int RsiPeriod { get; init; } = 14;
        public int PullbackLookback { get; init; } = 6;
        /// <summary>Korekta nie może zejść głębiej niż EMA średnia minus X*ATR (trend nienaruszony).</summary>
        public double MaxPullbackBelowMidAtr { get; init; } = 1.0;
        public double FilterAdxMin { get; init; } = 20;
        public double StopAtr { get; init; } = 2.0;
        public ExitPlan Exit { get; init; } = new() { TakeProfitR = 2.0, BreakEvenAtR = 1.0, ExitOnTrendReversal = true, MaxBarsHeld = 120 };
    }

    /// <summary>
    /// Trend following z wejściem na korekcie:
    /// filtr: trend na interwale wyższym (EMA50/EMA200 + ADX),
    /// sygnał: EMA20 &gt; EMA50 na interwale sygnałowym, w ostatnich N świecach zamknięcie poniżej EMA20 (korekta),
    /// wyzwalacz: zamknięcie powyżej EMA20 i powyżej maksimum poprzedniej świecy oraz RSI &gt; 50 (wznowienie momentum).
    /// </summary>
    public sealed class TrendPullbackStrategy : StrategyBase
    {
        private readonly TrendPullbackParams _p;

        public TrendPullbackStrategy() : this(new TrendPullbackParams()) { }

        public TrendPullbackStrategy(TrendPullbackParams p, string variant = "default")
            : base(p.SignalTf, p.FilterTf, p.Exit, variant)
        {
            _p = p;
        }

        public TrendPullbackParams Params => _p;
        public override StrategyId Id => StrategyId.TrendPullback;
        public override int RequiredSignalBars => Math.Max(_p.EmaMid * 3, 160);

        public override bool AppliesTo(AssetClass c) => c != AssetClass.Crypto && c != AssetClass.Unknown;

        public override SignalCandidate? Evaluate(StrategyInput input)
        {
            if (!BasicDataOk(input, RequiredSignalBars)) return null;
            var fr = input.FilterRegime;
            if (!fr.IsTradeable || !input.SignalRegime.IsTradeable) return null;
            if (fr.TrendDirection == 0 || fr.Adx < _p.FilterAdxMin) return null;

            var f = input.Signal;
            int i = input.SignalIndex;
            var emaF = f.Ema(_p.EmaFast);
            var emaM = f.Ema(_p.EmaMid);
            var atrA = f.Atr(_p.AtrPeriod);
            var rsi = f.Rsi(_p.RsiPeriod);
            double atr = atrA[i];
            if (!Ta.AllFinite(emaF[i], emaM[i], atr, rsi[i], emaF[i - _p.PullbackLookback]) || atr <= 0) return null;

            var d = fr.TrendDirection > 0 ? TradeDirection.Long : TradeDirection.Short;
            int s = (int)d;
            double close = f.Close[i];

            if (s * (emaF[i] - emaM[i]) <= 0) return null;

            bool pulledBack = false;
            for (int j = i - _p.PullbackLookback; j < i; j++)
            {
                if (s * (f.Close[j] - emaF[j]) < 0) { pulledBack = true; break; }
            }
            if (!pulledBack) return null;

            // Korekta nie może być zbyt głęboka: ekstremum korekty nie dalej niż EMA50 -/+ X*ATR.
            for (int j = i - _p.PullbackLookback; j <= i; j++)
            {
                double extreme = d == TradeDirection.Long ? f.Low[j] : f.High[j];
                if (s * (extreme - (emaM[i] - s * _p.MaxPullbackBelowMidAtr * atr)) < 0) return null;
            }

            bool trigger = s * (close - emaF[i]) > 0
                && (d == TradeDirection.Long ? close > f.High[i - 1] : close < f.Low[i - 1])
                && (d == TradeDirection.Long ? rsi[i] > 50 : rsi[i] < 50);
            if (!trigger) return null;

            double stop = _p.StopAtr * atr;
            string reason = $"Trend {fr.PolishName} na {FilterTf}, korekta do EMA{_p.EmaFast} i wznowienie (RSI {rsi[i]:F0})";
            return Build(input, d, stop, TpFromPlan(stop), atr, PlannedRr(2.0), reason, MomentumZ(f, i, 10, atr, d), DefaultValidity(SignalTf));
        }

        public override StrategyExit CheckExit(StrategyInput input, TradeDirection direction, double entryPrice, int barsHeld)
        {
            // Utrata przewagi: EMA20 przecina EMA50 przeciwko pozycji na interwale sygnałowym.
            var f = input.Signal;
            int i = input.SignalIndex;
            if (f == null || i < 1) return StrategyExit.None;
            var emaF = f.Ema(_p.EmaFast);
            var emaM = f.Ema(_p.EmaMid);
            if (!Ta.AllFinite(emaF[i], emaM[i])) return StrategyExit.None;
            int s = (int)direction;
            if (s * (emaF[i] - emaM[i]) < 0)
                return new StrategyExit(true, $"EMA{_p.EmaFast} przecięła EMA{_p.EmaMid} przeciw pozycji");
            return StrategyExit.None;
        }
    }
}
