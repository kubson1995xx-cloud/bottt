using System;
using System.Collections.Generic;

namespace Apex.Core.Indicators
{
    /// <summary>
    /// Wskaźniki techniczne liczone na tablicach zamkniętych świec.
    /// Implementacja własna (a nie wbudowane wskaźniki cTrader), aby backtest offline, testy jednostkowe
    /// i bot na żywo liczyły DOKŁADNIE te same wartości. Okres rozgrzewki = NaN.
    /// Wygładzanie Wildera zgodne z definicjami J. Wellesa Wildera (ATR, RSI, ADX).
    /// </summary>
    public static class Ta
    {
        public static double[] NaNArray(int n)
        {
            var a = new double[n];
            for (int i = 0; i < n; i++) a[i] = double.NaN;
            return a;
        }

        public static double[] Sma(double[] src, int period)
        {
            if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
            int n = src.Length;
            var r = NaNArray(n);
            double sum = 0;
            int valid = 0;
            for (int i = 0; i < n; i++)
            {
                double v = src[i];
                if (double.IsNaN(v)) { sum = 0; valid = 0; continue; }
                sum += v;
                valid++;
                if (valid > period)
                {
                    sum -= src[i - period];
                    valid = period;
                }
                if (valid == period) r[i] = sum / period;
            }
            return r;
        }

        /// <summary>EMA inicjalizowana średnią prostą z pierwszych <paramref name="period"/> wartości.</summary>
        public static double[] Ema(double[] src, int period)
        {
            if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
            return SeededSmoothing(src, period, 2.0 / (period + 1));
        }

        /// <summary>Średnia Wildera (RMA), alpha = 1/period.</summary>
        public static double[] Rma(double[] src, int period)
        {
            if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
            return SeededSmoothing(src, period, 1.0 / period);
        }

        private static double[] SeededSmoothing(double[] src, int period, double alpha)
        {
            int n = src.Length;
            var r = NaNArray(n);
            int first = 0;
            while (first < n && double.IsNaN(src[first])) first++;
            if (first + period > n) return r;
            double sum = 0;
            for (int i = first; i < first + period; i++)
            {
                if (double.IsNaN(src[i])) return r; // luka w danych w okresie rozgrzewki
                sum += src[i];
            }
            double prev = sum / period;
            r[first + period - 1] = prev;
            for (int i = first + period; i < n; i++)
            {
                double v = src[i];
                if (double.IsNaN(v)) { r[i] = prev; continue; }
                prev = prev + alpha * (v - prev);
                r[i] = prev;
            }
            return r;
        }

        public static double[] TrueRange(double[] high, double[] low, double[] close)
        {
            int n = close.Length;
            var tr = new double[n];
            for (int i = 0; i < n; i++)
            {
                double hl = high[i] - low[i];
                if (i == 0) { tr[i] = hl; continue; }
                double hc = Math.Abs(high[i] - close[i - 1]);
                double lc = Math.Abs(low[i] - close[i - 1]);
                tr[i] = Math.Max(hl, Math.Max(hc, lc));
            }
            return tr;
        }

        public static double[] Atr(double[] high, double[] low, double[] close, int period)
        {
            var tr = TrueRange(high, low, close);
            // Pierwszy TR (bez poprzedniego zamknięcia) jest pomijany, jak w klasycznej definicji.
            if (tr.Length > 0) tr[0] = double.NaN;
            return Rma(tr, period);
        }

        public static double[] Rsi(double[] close, int period)
        {
            int n = close.Length;
            var r = NaNArray(n);
            if (n <= period) return r;
            double gain = 0, loss = 0;
            for (int i = 1; i <= period; i++)
            {
                double d = close[i] - close[i - 1];
                if (d > 0) gain += d; else loss -= d;
            }
            double avgGain = gain / period;
            double avgLoss = loss / period;
            r[period] = RsiValue(avgGain, avgLoss);
            for (int i = period + 1; i < n; i++)
            {
                double d = close[i] - close[i - 1];
                double g = d > 0 ? d : 0;
                double l = d < 0 ? -d : 0;
                avgGain = (avgGain * (period - 1) + g) / period;
                avgLoss = (avgLoss * (period - 1) + l) / period;
                r[i] = RsiValue(avgGain, avgLoss);
            }
            return r;
        }

        private static double RsiValue(double avgGain, double avgLoss)
        {
            if (avgLoss <= 0 && avgGain <= 0) return 50;
            if (avgLoss <= 0) return 100;
            double rs = avgGain / avgLoss;
            return 100 - 100 / (1 + rs);
        }

        public readonly struct AdxResult
        {
            public AdxResult(double[] adx, double[] plusDi, double[] minusDi)
            {
                Adx = adx;
                PlusDi = plusDi;
                MinusDi = minusDi;
            }

            public double[] Adx { get; }
            public double[] PlusDi { get; }
            public double[] MinusDi { get; }
        }

        /// <summary>ADX / DMI Wildera.</summary>
        public static AdxResult Adx(double[] high, double[] low, double[] close, int period)
        {
            int n = close.Length;
            var adx = NaNArray(n);
            var pdi = NaNArray(n);
            var mdi = NaNArray(n);
            if (n <= period * 2) return new AdxResult(adx, pdi, mdi);

            var tr = TrueRange(high, low, close);
            double trS = 0, pS = 0, mS = 0;
            for (int i = 1; i <= period; i++)
            {
                (double p, double m) = Dm(high, low, i);
                trS += tr[i];
                pS += p;
                mS += m;
            }
            var dx = NaNArray(n);
            SetDi(period, trS, pS, mS, pdi, mdi, dx);
            for (int i = period + 1; i < n; i++)
            {
                (double p, double m) = Dm(high, low, i);
                trS = trS - trS / period + tr[i];
                pS = pS - pS / period + p;
                mS = mS - mS / period + m;
                SetDi(i, trS, pS, mS, pdi, mdi, dx);
            }
            // ADX = średnia Wildera z DX, startuje po kolejnych `period` wartościach DX.
            int start = period;
            double sum = 0;
            for (int i = start; i < start + period; i++) sum += dx[i];
            double prev = sum / period;
            adx[start + period - 1] = prev;
            for (int i = start + period; i < n; i++)
            {
                prev = (prev * (period - 1) + dx[i]) / period;
                adx[i] = prev;
            }
            return new AdxResult(adx, pdi, mdi);
        }

        private static (double plus, double minus) Dm(double[] high, double[] low, int i)
        {
            double up = high[i] - high[i - 1];
            double down = low[i - 1] - low[i];
            double p = up > down && up > 0 ? up : 0;
            double m = down > up && down > 0 ? down : 0;
            return (p, m);
        }

        private static void SetDi(int i, double trS, double pS, double mS, double[] pdi, double[] mdi, double[] dx)
        {
            if (trS <= 0)
            {
                pdi[i] = 0;
                mdi[i] = 0;
                dx[i] = 0;
                return;
            }
            double p = 100 * pS / trS;
            double m = 100 * mS / trS;
            pdi[i] = p;
            mdi[i] = m;
            double s = p + m;
            dx[i] = s <= 0 ? 0 : 100 * Math.Abs(p - m) / s;
        }

        /// <summary>Odchylenie standardowe populacji w oknie kroczącym.</summary>
        public static double[] StdDev(double[] src, int period)
        {
            int n = src.Length;
            var r = NaNArray(n);
            for (int i = period - 1; i < n; i++)
            {
                double mean = 0;
                bool ok = true;
                for (int j = i - period + 1; j <= i; j++)
                {
                    if (double.IsNaN(src[j])) { ok = false; break; }
                    mean += src[j];
                }
                if (!ok) continue;
                mean /= period;
                double ss = 0;
                for (int j = i - period + 1; j <= i; j++)
                {
                    double d = src[j] - mean;
                    ss += d * d;
                }
                r[i] = Math.Sqrt(ss / period);
            }
            return r;
        }

        public readonly struct Bands
        {
            public Bands(double[] middle, double[] upper, double[] lower)
            {
                Middle = middle;
                Upper = upper;
                Lower = lower;
            }

            public double[] Middle { get; }
            public double[] Upper { get; }
            public double[] Lower { get; }
        }

        public static Bands Bollinger(double[] close, int period, double k)
        {
            var mid = Sma(close, period);
            var sd = StdDev(close, period);
            int n = close.Length;
            var up = NaNArray(n);
            var lo = NaNArray(n);
            for (int i = 0; i < n; i++)
            {
                if (double.IsNaN(mid[i]) || double.IsNaN(sd[i])) continue;
                up[i] = mid[i] + k * sd[i];
                lo[i] = mid[i] - k * sd[i];
            }
            return new Bands(mid, up, lo);
        }

        /// <summary>Najwyższe maksimum w oknie [i-period+1, i] (włącznie z bieżącą świecą).</summary>
        public static double[] Highest(double[] src, int period)
        {
            int n = src.Length;
            var r = NaNArray(n);
            for (int i = period - 1; i < n; i++)
            {
                double m = double.MinValue;
                for (int j = i - period + 1; j <= i; j++) if (src[j] > m) m = src[j];
                r[i] = m;
            }
            return r;
        }

        /// <summary>Najniższe minimum w oknie [i-period+1, i] (włącznie z bieżącą świecą).</summary>
        public static double[] Lowest(double[] src, int period)
        {
            int n = src.Length;
            var r = NaNArray(n);
            for (int i = period - 1; i < n; i++)
            {
                double m = double.MaxValue;
                for (int j = i - period + 1; j <= i; j++) if (src[j] < m) m = src[j];
                r[i] = m;
            }
            return r;
        }

        /// <summary>Rate of change w procentach.</summary>
        public static double[] Roc(double[] close, int period)
        {
            int n = close.Length;
            var r = NaNArray(n);
            for (int i = period; i < n; i++)
            {
                double prev = close[i - period];
                if (prev > 0) r[i] = (close[i] / prev - 1.0) * 100.0;
            }
            return r;
        }

        /// <summary>
        /// Kaufman Efficiency Ratio: |zmiana netto| / suma |zmian| w oknie. 1 = idealny trend, ~0 = szum/konsolidacja.
        /// </summary>
        public static double[] EfficiencyRatio(double[] close, int period)
        {
            int n = close.Length;
            var r = NaNArray(n);
            for (int i = period; i < n; i++)
            {
                double net = Math.Abs(close[i] - close[i - period]);
                double path = 0;
                for (int j = i - period + 1; j <= i; j++) path += Math.Abs(close[j] - close[j - 1]);
                r[i] = path > 0 ? net / path : 0;
            }
            return r;
        }

        /// <summary>Ranga procentowa wartości src[i] względem poprzednich <paramref name="lookback"/> wartości (0..1).</summary>
        public static double PercentRank(double[] src, int i, int lookback)
        {
            if (i < lookback || double.IsNaN(src[i])) return double.NaN;
            int below = 0, count = 0;
            for (int j = i - lookback; j < i; j++)
            {
                if (double.IsNaN(src[j])) continue;
                count++;
                if (src[j] < src[i]) below++;
            }
            return count == 0 ? double.NaN : (double)below / count;
        }

        /// <summary>Logarytmiczne stopy zwrotu (pierwszy element NaN).</summary>
        public static double[] LogReturns(double[] close)
        {
            int n = close.Length;
            var r = NaNArray(n);
            for (int i = 1; i < n; i++)
                if (close[i] > 0 && close[i - 1] > 0) r[i] = Math.Log(close[i] / close[i - 1]);
            return r;
        }

        /// <summary>Korelacja Pearsona dwóch serii (pomija pary z NaN). NaN gdy za mało danych.</summary>
        public static double Correlation(IReadOnlyList<double> a, IReadOnlyList<double> b, int minPairs = 20)
        {
            int n = Math.Min(a.Count, b.Count);
            double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
            int k = 0;
            for (int i = 0; i < n; i++)
            {
                double x = a[i], y = b[i];
                if (double.IsNaN(x) || double.IsNaN(y)) continue;
                sa += x; sb += y; saa += x * x; sbb += y * y; sab += x * y; k++;
            }
            if (k < minPairs) return double.NaN;
            double cov = sab / k - (sa / k) * (sb / k);
            double va = saa / k - (sa / k) * (sa / k);
            double vb = sbb / k - (sb / k) * (sb / k);
            if (va <= 0 || vb <= 0) return double.NaN;
            return Math.Max(-1, Math.Min(1, cov / Math.Sqrt(va * vb)));
        }

        public static double Last(double[] a) => a.Length == 0 ? double.NaN : a[a.Length - 1];

        public static double At(double[] a, int i) => i >= 0 && i < a.Length ? a[i] : double.NaN;

        public static bool AllFinite(params double[] values)
        {
            foreach (var v in values)
                if (double.IsNaN(v) || double.IsInfinity(v)) return false;
            return true;
        }

        /// <summary>Mediana (kopiuje dane).</summary>
        public static double Median(IEnumerable<double> values)
        {
            var list = new List<double>();
            foreach (var v in values) if (!double.IsNaN(v)) list.Add(v);
            if (list.Count == 0) return double.NaN;
            list.Sort();
            int m = list.Count / 2;
            return list.Count % 2 == 1 ? list[m] : (list[m - 1] + list[m]) / 2.0;
        }
    }
}
