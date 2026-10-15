using System;
using Apex.Core.Indicators;
using Apex.Core.Model;

namespace Apex.Core.Strategies
{
    public sealed class IndexDipBuyParams
    {
        public Tf SignalTf { get; init; } = Tf.D1;
        public int TrendSma { get; init; } = 200;
        public int RsiPeriod { get; init; } = 2;
        public double RsiEntry { get; init; } = 10;
        public int ExitSma { get; init; } = 5;
        public int AtrPeriod { get; init; } = 14;
        /// <summary>Szeroki stop katastroficzny – strategia wychodzi głównie regułą (powrót powyżej SMA5) lub limitem czasu.</summary>
        public double StopAtr { get; init; } = 3.0;
        public ExitPlan Exit { get; init; } = new() { TakeProfitR = null, MaxBarsHeld = 7 };
    }

    /// <summary>
    /// Krótkoterminowy powrót do średniej na indeksach akcyjnych i ETF (tylko LONG): w długoterminowym trendzie wzrostowym
    /// (zamknięcie &gt; SMA200 D1) kupno silnego krótkoterminowego wyprzedania (RSI(2) &lt; 10), wyjście po zamknięciu powyżej SMA5
    /// lub po limicie czasu. Szeroki SL ogranicza ryzyko katastroficzne.
    /// </summary>
    public sealed class IndexDipBuyStrategy : StrategyBase
    {
        private readonly IndexDipBuyParams _p;

        public IndexDipBuyStrategy() : this(new IndexDipBuyParams()) { }

        public IndexDipBuyStrategy(IndexDipBuyParams p, string variant = "default")
            : base(p.SignalTf, null, p.Exit, variant)
        {
            _p = p;
        }

        public IndexDipBuyParams Params => _p;
        public override StrategyId Id => StrategyId.IndexDipBuy;
        public override int RequiredSignalBars => _p.TrendSma + 20;
        public override int RequiredFilterBars => 0;

        public override bool AppliesTo(AssetClass c) => c == AssetClass.Index || c == AssetClass.Etf;

        public override SignalCandidate? Evaluate(StrategyInput input)
        {
            if (!BasicDataOk(input, RequiredSignalBars)) return null;
            if (input.SignalRegime.Regime == MarketRegime.Chaotic) return null;
            var f = input.Signal;
            int i = input.SignalIndex;
            double sma = f.Sma(_p.TrendSma)[i];
            double rsi = f.Rsi(_p.RsiPeriod)[i];
            double atr = f.Atr(_p.AtrPeriod)[i];
            double exitSma = f.Sma(_p.ExitSma)[i];
            if (!Ta.AllFinite(sma, rsi, atr, exitSma) || atr <= 0) return null;
            double close = f.Close[i];
            if (close <= sma || rsi >= _p.RsiEntry || close >= exitSma) return null;

            double stop = _p.StopAtr * atr;
            double expected = Math.Max(0.2, (exitSma - close) / stop);
            string reason = $"Indeks w trendzie wzrostowym (nad SMA{_p.TrendSma}), RSI({_p.RsiPeriod})={rsi:F1} – krótkoterminowe wyprzedanie";
            return Build(input, TradeDirection.Long, stop, null, atr, expected, reason, MomentumZ(f, i, 5, atr, TradeDirection.Long), DefaultValidity(SignalTf), 0.75);
        }

        public override StrategyExit CheckExit(StrategyInput input, TradeDirection direction, double entryPrice, int barsHeld)
        {
            var f = input.Signal;
            int i = input.SignalIndex;
            if (f == null || i < 1) return StrategyExit.None;
            double exitSma = f.Sma(_p.ExitSma)[i];
            if (double.IsNaN(exitSma)) return StrategyExit.None;
            if (f.Close[i] > exitSma)
                return new StrategyExit(true, $"Zamknięcie powyżej SMA{_p.ExitSma} – odbicie zrealizowane");
            return StrategyExit.None;
        }
    }
}
