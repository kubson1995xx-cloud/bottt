using System;
using System.Collections.Generic;

namespace Apex.Core.Model
{
    /// <summary>Pojedyncza świeca (ceny BID w cTrader).</summary>
    public readonly struct Bar
    {
        public Bar(DateTime openTime, double open, double high, double low, double close, double volume = 0)
        {
            OpenTime = openTime;
            Open = open;
            High = high;
            Low = low;
            Close = close;
            Volume = volume;
        }

        public DateTime OpenTime { get; }
        public double Open { get; }
        public double High { get; }
        public double Low { get; }
        public double Close { get; }
        public double Volume { get; }

        public bool IsValid =>
            !double.IsNaN(Open) && !double.IsNaN(High) && !double.IsNaN(Low) && !double.IsNaN(Close) &&
            !double.IsInfinity(Open) && !double.IsInfinity(High) && !double.IsInfinity(Low) && !double.IsInfinity(Close) &&
            Open > 0 && High > 0 && Low > 0 && Close > 0 &&
            High >= Low && High >= Open && High >= Close && Low <= Open && Low <= Close;
    }

    /// <summary>
    /// Niezmienny zbiór ZAMKNIĘTYCH świec jednego instrumentu na jednym interwale.
    /// Ostatni element (indeks Count-1) to ostatnia zamknięta świeca – silnik nigdy nie widzi świecy w trakcie tworzenia,
    /// co eliminuje look-ahead bias zarówno w handlu na żywo, jak i w backteście.
    /// </summary>
    public sealed class BarSeries
    {
        public BarSeries(string symbol, Tf timeFrame, DateTime[] times, double[] open, double[] high, double[] low, double[] close, double[] volume)
        {
            if (times.Length != open.Length || open.Length != high.Length || high.Length != low.Length || low.Length != close.Length || close.Length != volume.Length)
                throw new ArgumentException("Tablice świec mają różne długości.");
            Symbol = symbol;
            TimeFrame = timeFrame;
            Times = times;
            Open = open;
            High = high;
            Low = low;
            Close = close;
            Volume = volume;
        }

        public string Symbol { get; }
        public Tf TimeFrame { get; }
        public DateTime[] Times { get; }
        public double[] Open { get; }
        public double[] High { get; }
        public double[] Low { get; }
        public double[] Close { get; }
        public double[] Volume { get; }
        public int Count => Close.Length;
        public int Last => Count - 1;

        public DateTime LastOpenTime => Count > 0 ? Times[Count - 1] : DateTime.MinValue;

        /// <summary>Czas zamknięcia ostatniej świecy (= otwarcie + długość interwału).</summary>
        public DateTime LastCloseTime => Count > 0 ? Times[Count - 1] + TimeFrame.Duration() : DateTime.MinValue;

        public Bar this[int index] => new Bar(Times[index], Open[index], High[index], Low[index], Close[index], Volume[index]);

        public static BarSeries FromBars(string symbol, Tf tf, IReadOnlyList<Bar> bars)
        {
            int n = bars.Count;
            var t = new DateTime[n];
            var o = new double[n];
            var h = new double[n];
            var l = new double[n];
            var c = new double[n];
            var v = new double[n];
            for (int i = 0; i < n; i++)
            {
                var b = bars[i];
                t[i] = b.OpenTime;
                o[i] = b.Open;
                h[i] = b.High;
                l[i] = b.Low;
                c[i] = b.Close;
                v[i] = b.Volume;
            }
            return new BarSeries(symbol, tf, t, o, h, l, c, v);
        }

        /// <summary>Zwraca widok ostatnich <paramref name="count"/> świec, kończący się na indeksie <paramref name="endInclusive"/>.</summary>
        public BarSeries Slice(int endInclusive, int count)
        {
            if (endInclusive >= Count) endInclusive = Count - 1;
            int start = Math.Max(0, endInclusive - count + 1);
            int n = endInclusive - start + 1;
            if (n <= 0) return new BarSeries(Symbol, TimeFrame, Array.Empty<DateTime>(), Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>());
            var t = new DateTime[n];
            var o = new double[n];
            var h = new double[n];
            var l = new double[n];
            var c = new double[n];
            var v = new double[n];
            Array.Copy(Times, start, t, 0, n);
            Array.Copy(Open, start, o, 0, n);
            Array.Copy(High, start, h, 0, n);
            Array.Copy(Low, start, l, 0, n);
            Array.Copy(Close, start, c, 0, n);
            Array.Copy(Volume, start, v, 0, n);
            return new BarSeries(Symbol, TimeFrame, t, o, h, l, c, v);
        }

        /// <summary>
        /// Walidacja danych: wszystkie świece poprawne, czasy rosnące.
        /// Zwraca null jeśli dane są poprawne, w przeciwnym razie opis problemu (po polsku).
        /// </summary>
        public string? Validate(int minBars)
        {
            if (Count < minBars) return $"za mało świec ({Count} < {minBars})";
            for (int i = 0; i < Count; i++)
            {
                if (!this[i].IsValid) return $"niepoprawna świeca {Times[i]:yyyy-MM-dd HH:mm}";
                if (i > 0 && Times[i] <= Times[i - 1]) return $"nierosnące czasy świec przy {Times[i]:yyyy-MM-dd HH:mm}";
            }
            return null;
        }
    }
}
