using System;
using Apex.Core.Indicators;
using Apex.Core.Model;

namespace Apex.Core.Strategies
{
    public sealed class MeanReversionParams
    {
        public Tf SignalTf { get; init; } = Tf.H1;
        public Tf FilterTf { get; init; } = Tf.H4;
        public int BbPeriod { get; init; } = 20;
        public double BbK { get; init; } = 2.0;
        public int RsiPeriod { get; init; } = 14;
        public double RsiOversold { get; init; } = 30;
        public double RsiOverbought { get; init; } = 70;
        public int AtrPeriod { get; init; } = 14;
        /// <summary>Maksymalny ADX na interwale filtrującym – strategia działa tylko w konsolidacji.</summary>
        public double FilterAdxMax { get; init; } = 20;
        /// <summary>Maksymalny ADX na interwale sygnałowym.</summary>
        public double SignalAdxMax { get; init; } = 25;
        public double StopAtr { get; init; } = 1.5;
        /// <summary>Minimalny stosunek odległości do średniej (cel) do ryzyka.</summary>
        public double MinRewardRisk { get; init; } = 0.8;
        public ExitPlan Exit { get; init; } = new() { TakeProfitR = null, MaxBarsHeld = 24 };
    }

    /// <summary>
    /// Powrót do średniej – stosowany WYŁĄCZNIE w konsolidacji (niski ADX na interwale wyższym i sygnałowym,
    /// zmienność nie-wysoka). Wejście po powrocie ceny do wnętrza wstęgi Bollingera po wyjściu poza nią,
    /// przy RSI w strefie wyprzedania/wykupienia. Cel: środkowa wstęga (TP liczony przy wejściu). Limit czasu.
    /// </summary>
    public sealed class MeanReversionStrategy : StrategyBase
    {
        private readonly MeanReversionParams _p;

        public MeanReversionStrategy() : this(new MeanReversionParams()) { }

        public MeanReversionStrategy(MeanReversionParams p, string variant = "default")
            : base(p.SignalTf, p.FilterTf, p.Exit, variant)
        {
            _p = p;
        }

        public MeanReversionParams Params => _p;
        public override StrategyId Id => StrategyId.MeanReversion;
        public override int RequiredSignalBars => Math.Max(_p.BbPeriod * 3, 120);

        public override bool AppliesTo(AssetClass c) => c != AssetClass.Crypto && c != AssetClass.Unknown;

        public override SignalCandidate? Evaluate(StrategyInput input)
        {
            if (!BasicDataOk(input, RequiredSignalBars)) return null;
            var fr = input.FilterRegime;
            var sr = input.SignalRegime;
            if (!fr.IsTradeable || !sr.IsTradeable) return null;
            if (fr.Adx >= _p.FilterAdxMax || fr.Volatility == VolatilityState.High) return null;
            if (sr.Adx >= _p.SignalAdxMax) return null;

            var f = input.Signal;
            int i = input.SignalIndex;
            var bb = f.Bollinger(_p.BbPeriod, _p.BbK);
            var rsi = f.Rsi(_p.RsiPeriod);
            double atr = f.Atr(_p.AtrPeriod)[i];
            if (!Ta.AllFinite(bb.Lower[i], bb.Upper[i], bb.Middle[i], bb.Lower[i - 1], bb.Upper[i - 1], rsi[i - 1], atr) || atr <= 0) return null;

            double close = f.Close[i];
            TradeDirection? dir = null;
            if (f.Close[i - 1] < bb.Lower[i - 1] && close > bb.Lower[i] && close < bb.Middle[i] && rsi[i - 1] < _p.RsiOversold)
                dir = TradeDirection.Long;
            else if (f.Close[i - 1] > bb.Upper[i - 1] && close < bb.Upper[i] && close > bb.Middle[i] && rsi[i - 1] > _p.RsiOverbought)
                dir = TradeDirection.Short;
            if (dir == null) return null;
            var d = dir.Value;

            double stop = _p.StopAtr * atr;
            double target = Math.Abs(bb.Middle[i] - close);
            double rr = target / stop;
            if (rr < _p.MinRewardRisk) return null;

            string reason = $"Konsolidacja ({fr.PolishName}, ADX {fr.Adx:F0}), powrót do wstęgi Bollingera, RSI {rsi[i - 1]:F0}";
            return Build(input, d, stop, target, atr, rr, reason, MomentumZ(f, i, 10, atr, d), DefaultValidity(SignalTf), 0.3);
        }

        public override StrategyExit CheckExit(StrategyInput input, TradeDirection direction, double entryPrice, int barsHeld)
        {
            var f = input.Signal;
            int i = input.SignalIndex;
            if (f == null || i < 1) return StrategyExit.None;
            var bb = f.Bollinger(_p.BbPeriod, _p.BbK);
            if (double.IsNaN(bb.Middle[i])) return StrategyExit.None;
            int s = (int)direction;
            if (s * (f.Close[i] - bb.Middle[i]) >= 0)
                return new StrategyExit(true, "Cena wróciła do średniej (cel osiągnięty na zamknięciu)");
            // Konsolidacja zamieniła się w trend przeciwko pozycji -> przewaga strategii zniknęła.
            var fr = input.FilterRegime;
            if (fr.IsTradeable && fr.Adx >= _p.FilterAdxMax + 5 && fr.TrendDirection == -s)
                return new StrategyExit(true, "Konsolidacja przeszła w trend przeciwny");
            return StrategyExit.None;
        }
    }
}
