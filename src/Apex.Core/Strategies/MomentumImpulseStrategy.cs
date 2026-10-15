using System;
using Apex.Core.Indicators;
using Apex.Core.Model;

namespace Apex.Core.Strategies
{
    public sealed class MomentumImpulseParams
    {
        public Tf SignalTf { get; init; } = Tf.H1;
        public Tf FilterTf { get; init; } = Tf.H4;
        public int AtrPeriod { get; init; } = 14;
        /// <summary>Świeca impulsu: zasięg co najmniej X * ATR (z poprzedniej świecy).</summary>
        public double ImpulseAtr { get; init; } = 1.8;
        /// <summary>Położenie zamknięcia w zasięgu świecy (0..1) – np. 0.75 = zamknięcie w górnych 25%.</summary>
        public double CloseLocation { get; init; } = 0.75;
        /// <summary>Zamknięcie musi ustanowić nowe ekstremum z N świec (kontynuacja ruchu).</summary>
        public int BreakLookback { get; init; } = 10;
        public double MinStopAtr { get; init; } = 1.0;
        public double MaxStopAtr { get; init; } = 3.0;
        public int MomentumEma { get; init; } = 10;
        public ExitPlan Exit { get; init; } = new() { TakeProfitR = null, BreakEvenAtR = 1.0, TrailStartR = 1.0, TrailAtrMultiple = 2.5, MaxBarsHeld = 48 };
    }

    /// <summary>
    /// Momentum: silna świeca kierunkowa (przyspieszenie ceny, wzrost zmienności) zamykająca się blisko ekstremum
    /// i ustanawiająca nowe maksimum/minimum z N świec, zgodnie z trendem wyższego interwału.
    /// SL za świecą impulsu (ograniczony przedziałem ATR). Wyjście: trailing lub utrata momentum (zamknięcie za EMA10).
    /// </summary>
    public sealed class MomentumImpulseStrategy : StrategyBase
    {
        private readonly MomentumImpulseParams _p;

        public MomentumImpulseStrategy() : this(new MomentumImpulseParams()) { }

        public MomentumImpulseStrategy(MomentumImpulseParams p, string variant = "default")
            : base(p.SignalTf, p.FilterTf, p.Exit, variant)
        {
            _p = p;
        }

        public MomentumImpulseParams Params => _p;
        public override StrategyId Id => StrategyId.MomentumImpulse;
        public override int RequiredSignalBars => 120;

        public override bool AppliesTo(AssetClass c) => c != AssetClass.Crypto && c != AssetClass.Unknown;

        public override SignalCandidate? Evaluate(StrategyInput input)
        {
            if (!BasicDataOk(input, RequiredSignalBars)) return null;
            var fr = input.FilterRegime;
            // Chaos na interwale sygnałowym (np. świeca newsowa > 4 ATR) wyklucza wejście.
            if (!fr.IsTradeable || !input.SignalRegime.IsTradeable || fr.TrendDirection == 0) return null;

            var f = input.Signal;
            int i = input.SignalIndex;
            double atrPrev = f.Atr(_p.AtrPeriod)[i - 1];
            double atr = f.Atr(_p.AtrPeriod)[i];
            if (!Ta.AllFinite(atrPrev, atr) || atrPrev <= 0) return null;

            double high = f.High[i], low = f.Low[i], close = f.Close[i], open = f.Open[i];
            double range = high - low;
            if (range < _p.ImpulseAtr * atrPrev || range <= 0) return null;

            var d = fr.TrendDirection > 0 ? TradeDirection.Long : TradeDirection.Short;
            int s = (int)d;
            if (s * (close - open) <= 0) return null;
            double loc = d == TradeDirection.Long ? (close - low) / range : (high - close) / range;
            if (loc < _p.CloseLocation) return null;

            double prevExtreme = d == TradeDirection.Long ? f.HighestHigh(_p.BreakLookback)[i - 1] : f.LowestLow(_p.BreakLookback)[i - 1];
            if (double.IsNaN(prevExtreme) || s * (close - prevExtreme) <= 0) return null;

            double rawStop = d == TradeDirection.Long ? close - low : high - close;
            double stop = Math.Max(_p.MinStopAtr * atr, Math.Min(_p.MaxStopAtr * atr, rawStop + 0.2 * atr));
            string reason = $"Impuls {range / atrPrev:F1}x ATR, nowe ekstremum {_p.BreakLookback} świec, trend {FilterTf}: {fr.PolishName}";
            return Build(input, d, stop, TpFromPlan(stop), atr, PlannedRr(2.0), reason, MomentumZ(f, i, 10, atr, d), DefaultValidity(SignalTf), 0.6);
        }

        public override StrategyExit CheckExit(StrategyInput input, TradeDirection direction, double entryPrice, int barsHeld)
        {
            var f = input.Signal;
            int i = input.SignalIndex;
            if (f == null || i < 1 || barsHeld < 2) return StrategyExit.None;
            double ema = f.Ema(_p.MomentumEma)[i];
            if (double.IsNaN(ema)) return StrategyExit.None;
            if ((int)direction * (f.Close[i] - ema) < 0)
                return new StrategyExit(true, $"Utrata momentum (zamknięcie za EMA{_p.MomentumEma})");
            return StrategyExit.None;
        }
    }
}
