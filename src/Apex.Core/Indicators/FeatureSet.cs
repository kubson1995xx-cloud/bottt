using System;
using System.Collections.Generic;
using Apex.Core.Model;

namespace Apex.Core.Indicators
{
    /// <summary>
    /// Leniwie liczony i buforowany zestaw wskaźników dla jednej serii świec (bezpieczny wątkowo – backtest równoległy).
    /// Na żywo: seria = okno ostatnich N zamkniętych świec, oceniany indeks = ostatni.
    /// W backteście: seria = cała historia, oceniany indeks = bieżący krok symulacji (nigdy dalej – brak look-ahead).
    /// </summary>
    public sealed class FeatureSet
    {
        private readonly Dictionary<string, double[]> _cache = new();
        private readonly Dictionary<int, Ta.AdxResult> _adx = new();
        private readonly Dictionary<string, Ta.Bands> _bands = new();

        public FeatureSet(BarSeries bars)
        {
            Bars = bars ?? throw new ArgumentNullException(nameof(bars));
        }

        public BarSeries Bars { get; }
        public int Count => Bars.Count;
        public double[] Open => Bars.Open;
        public double[] High => Bars.High;
        public double[] Low => Bars.Low;
        public double[] Close => Bars.Close;

        private readonly object _lock = new();

        private double[] Get(string key, Func<double[]> factory)
        {
            lock (_lock)
            {
                if (!_cache.TryGetValue(key, out var arr))
                {
                    arr = factory();
                    _cache[key] = arr;
                }
                return arr;
            }
        }

        public double[] Ema(int period) => Get("ema" + period, () => Ta.Ema(Bars.Close, period));
        public double[] Sma(int period) => Get("sma" + period, () => Ta.Sma(Bars.Close, period));
        public double[] Atr(int period) => Get("atr" + period, () => Ta.Atr(Bars.High, Bars.Low, Bars.Close, period));
        public double[] Rsi(int period) => Get("rsi" + period, () => Ta.Rsi(Bars.Close, period));
        public double[] Roc(int period) => Get("roc" + period, () => Ta.Roc(Bars.Close, period));
        public double[] Er(int period) => Get("er" + period, () => Ta.EfficiencyRatio(Bars.Close, period));
        public double[] HighestHigh(int period) => Get("hh" + period, () => Ta.Highest(Bars.High, period));
        public double[] LowestLow(int period) => Get("ll" + period, () => Ta.Lowest(Bars.Low, period));
        public double[] HighestClose(int period) => Get("hc" + period, () => Ta.Highest(Bars.Close, period));
        public double[] LowestClose(int period) => Get("lc" + period, () => Ta.Lowest(Bars.Close, period));
        public double[] TrueRange() => Get("tr", () => Ta.TrueRange(Bars.High, Bars.Low, Bars.Close));
        public double[] LogReturns() => Get("lr", () => Ta.LogReturns(Bars.Close));

        public Ta.AdxResult Adx(int period)
        {
            lock (_lock)
            {
                if (!_adx.TryGetValue(period, out var r))
                {
                    r = Ta.Adx(Bars.High, Bars.Low, Bars.Close, period);
                    _adx[period] = r;
                }
                return r;
            }
        }

        public Ta.Bands Bollinger(int period, double k)
        {
            string key = period + ":" + k.ToString(System.Globalization.CultureInfo.InvariantCulture);
            lock (_lock)
            {
                if (!_bands.TryGetValue(key, out var b))
                {
                    b = Ta.Bollinger(Bars.Close, period, k);
                    _bands[key] = b;
                }
                return b;
            }
        }

        /// <summary>Indeks ostatniej świecy, której czas ZAMKNIĘCIA jest &lt;= <paramref name="timeUtc"/>. -1 gdy brak.</summary>
        public int IndexAtOrBefore(DateTime timeUtc)
        {
            var times = Bars.Times;
            var dur = Bars.TimeFrame.Duration();
            int lo = 0, hi = times.Length - 1, ans = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (times[mid] + dur <= timeUtc)
                {
                    ans = mid;
                    lo = mid + 1;
                }
                else hi = mid - 1;
            }
            return ans;
        }
    }
}
