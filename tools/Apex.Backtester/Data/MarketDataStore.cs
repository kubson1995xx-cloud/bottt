using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Apex.Core.Indicators;
using Apex.Core.Model;

namespace Apex.Backtester.Data
{
    /// <summary>Wczytywanie świec z CSV przygotowanych przez tools/data/prepare_data.py (czas UTC, ceny MID).</summary>
    public static class BarCsv
    {
        public static BarSeries Load(string path, string symbol, Tf tf, DateTime? from = null, DateTime? to = null)
        {
            var bars = new List<Bar>(120_000);
            using var sr = new StreamReader(path);
            string? line = sr.ReadLine(); // nagłówek
            var ci = CultureInfo.InvariantCulture;
            while ((line = sr.ReadLine()) != null)
            {
                var p = line.Split(',');
                if (p.Length < 6) continue;
                var t = DateTime.ParseExact(p[0], "yyyy-MM-dd HH:mm", ci, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                if (from.HasValue && t < from.Value) continue;
                if (to.HasValue && t >= to.Value) break;
                var b = new Bar(t, double.Parse(p[1], ci), double.Parse(p[2], ci), double.Parse(p[3], ci), double.Parse(p[4], ci), double.Parse(p[5], ci));
                if (b.IsValid) bars.Add(b);
            }
            return BarSeries.FromBars(symbol, tf, bars);
        }
    }

    /// <summary>
    /// Agregacja H1 → H4/D1 z granicami jak u brokerów cTrader (czas serwera = Nowy Jork + 7h,
    /// dzień handlowy kończy się o 17:00 czasu nowojorskiego). Brak look-ahead: świeca D1 jest "zamknięta"
    /// dopiero po upływie pełnej doby od swojego początku.
    /// </summary>
    public static class Aggregator
    {
        private static readonly TimeZoneInfo Ny = FindNy();

        private static TimeZoneInfo FindNy()
        {
            foreach (var id in new[] { "America/New_York", "Eastern Standard Time" })
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
                catch (TimeZoneNotFoundException) { }
                catch (InvalidTimeZoneException) { }
            }
            throw new InvalidOperationException("Brak strefy czasowej America/New_York.");
        }

        public static DateTime ToServer(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, Ny).AddHours(7);

        public static DateTime ServerToUtc(DateTime server)
        {
            var ny = DateTime.SpecifyKind(server.AddHours(-7), DateTimeKind.Unspecified);
            if (Ny.IsInvalidTime(ny)) ny = ny.AddHours(1);
            return TimeZoneInfo.ConvertTimeToUtc(ny, Ny);
        }

        public static BarSeries Aggregate(BarSeries h1, Tf target)
        {
            if (target != Tf.H4 && target != Tf.D1) throw new ArgumentException("Obsługiwane: H4, D1");
            var result = new List<Bar>(h1.Count / (target == Tf.H4 ? 4 : 24) + 10);
            DateTime? curKey = null;
            double o = 0, h = 0, l = 0, c = 0, v = 0;
            for (int i = 0; i < h1.Count; i++)
            {
                var server = ToServer(h1.Times[i]);
                DateTime key = target == Tf.D1 ? server.Date : server.Date.AddHours(server.Hour / 4 * 4);
                if (curKey != key)
                {
                    if (curKey.HasValue) result.Add(new Bar(ServerToUtc(curKey.Value), o, h, l, c, v));
                    curKey = key;
                    o = h1.Open[i]; h = h1.High[i]; l = h1.Low[i]; c = h1.Close[i]; v = h1.Volume[i];
                }
                else
                {
                    if (h1.High[i] > h) h = h1.High[i];
                    if (h1.Low[i] < l) l = h1.Low[i];
                    c = h1.Close[i];
                    v += h1.Volume[i];
                }
            }
            if (curKey.HasValue) result.Add(new Bar(ServerToUtc(curKey.Value), o, h, l, c, v));
            // Usunięcie ewentualnych duplikatów czasu (zmiana czasu) – zachowujemy pierwszą świecę.
            var clean = new List<Bar>(result.Count);
            foreach (var b in result)
                if (clean.Count == 0 || b.OpenTime > clean[^1].OpenTime) clean.Add(b);
            return BarSeries.FromBars(h1.Symbol, target, clean);
        }
    }

    /// <summary>Kursy przeliczeniowe walut na USD na dany moment (ostatnie zamknięcie H1 ≤ czas).</summary>
    public sealed class FxRates
    {
        private readonly Dictionary<string, BarSeries> _series = new();

        public void Add(string pair, BarSeries h1) => _series[pair] = h1;

        private double Close(string pair, DateTime utc)
        {
            if (!_series.TryGetValue(pair, out var s) || s.Count == 0) return double.NaN;
            int lo = 0, hi = s.Count - 1, ans = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (s.Times[mid].AddHours(1) <= utc) { ans = mid; lo = mid + 1; } else hi = mid - 1;
            }
            return ans < 0 ? s.Open[0] : s.Close[ans];
        }

        /// <summary>Ile USD jest wart 1 jednostka waluty <paramref name="ccy"/>.</summary>
        public double ToUsd(string ccy, DateTime utc)
        {
            switch (ccy)
            {
                case "USD": return 1.0;
                case "EUR": return Close("EURUSD", utc);
                case "GBP": return Close("GBPUSD", utc);
                case "AUD": return Close("AUDUSD", utc);
                case "CAD": { double x = Close("USDCAD", utc); return x > 0 ? 1.0 / x : double.NaN; }
                case "JPY":
                    {
                        double ej = Close("EURJPY", utc), eu = Close("EURUSD", utc);
                        return ej > 0 && eu > 0 ? eu / ej : double.NaN;
                    }
                default: return double.NaN;
            }
        }
    }

    /// <summary>Komplet danych instrumentu w backteście: świece i wskaźniki dla wszystkich interwałów.</summary>
    public sealed class SymbolData
    {
        public SymbolData(InstrumentDef def, BarSeries h1, BarSeries? m15)
        {
            Def = def;
            H1 = h1;
            M15 = m15;
            H4 = Aggregator.Aggregate(h1, Tf.H4);
            D1 = Aggregator.Aggregate(h1, Tf.D1);
            Features = new Dictionary<Tf, FeatureSet>
            {
                [Tf.H1] = new FeatureSet(H1),
                [Tf.H4] = new FeatureSet(H4),
                [Tf.D1] = new FeatureSet(D1),
            };
            if (m15 != null) Features[Tf.M15] = new FeatureSet(m15);
            IndexByOpen = new Dictionary<DateTime, int>(h1.Count);
            for (int i = 0; i < h1.Count; i++) IndexByOpen[h1.Times[i]] = i;
            // Koniec ciągłej sesji dla każdej świecy H1 (pierwsza luka > 1h) – odpowiednik godzin handlu.
            SessionEnd = new DateTime[h1.Count];
            DateTime end = h1.Count > 0 ? h1.Times[^1].AddHours(1) : DateTime.MinValue;
            for (int i = h1.Count - 1; i >= 0; i--)
            {
                if (i < h1.Count - 1 && h1.Times[i + 1] - h1.Times[i] > TimeSpan.FromHours(1)) end = h1.Times[i].AddHours(1);
                SessionEnd[i] = end;
            }
            if (m15 != null)
            {
                M15IndexByOpen = new Dictionary<DateTime, int>(m15.Count);
                for (int i = 0; i < m15.Count; i++) M15IndexByOpen[m15.Times[i]] = i;
            }
        }

        public InstrumentDef Def { get; }
        public BarSeries H1 { get; }
        public BarSeries? M15 { get; }
        public BarSeries H4 { get; }
        public BarSeries D1 { get; }
        public Dictionary<Tf, FeatureSet> Features { get; }
        public Dictionary<DateTime, int> IndexByOpen { get; }
        public Dictionary<DateTime, int>? M15IndexByOpen { get; }
        public DateTime[] SessionEnd { get; }
    }
}
