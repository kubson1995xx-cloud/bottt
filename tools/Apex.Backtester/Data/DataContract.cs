using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Apex.Core.Model;

namespace Apex.Backtester.Data
{
    public sealed class SessionGap
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public string Reason { get; set; } = "";
    }
    public sealed class DataFile
    {
        public string File { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public string QuoteBasis { get; set; } = "";
        public List<SessionGap> SessionGaps { get; set; } = new();
    }
    public sealed class DataManifest
    {
        public string Source { get; set; } = "";
        public string Timezone { get; set; } = "";
        public List<DataFile> Files { get; set; } = new();
        public static DataManifest Read(string path)
        {
            var m = JsonSerializer.Deserialize<DataManifest>(System.IO.File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("Empty data manifest");
            if (string.IsNullOrWhiteSpace(m.Source) || m.Timezone != "UTC" || m.Files.Count == 0 ||
                m.Files.Select(f => f.File).Distinct(StringComparer.OrdinalIgnoreCase).Count() != m.Files.Count)
                throw new InvalidDataException("Manifest requires source, UTC timezone and unique files");
            foreach (var f in m.Files)
            {
                if (Path.GetFileName(f.File) != f.File || f.File.Contains('\\') || f.File.Contains('/') ||
                    f.Sha256.Length != 64 || !f.Sha256.All(Uri.IsHexDigit) ||
                    (f.QuoteBasis != "MID" && f.QuoteBasis != "BID" && f.QuoteBasis != "ASK") ||
                    f.SessionGaps.Any(g => g.From.Kind != DateTimeKind.Utc || g.To.Kind != DateTimeKind.Utc || g.From >= g.To || string.IsNullOrWhiteSpace(g.Reason)))
                    throw new InvalidDataException($"Invalid manifest entry: {f.File}");
            }
            return m;
        }
        public static JsonSerializerOptions JsonOptions => new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };
    }

    public sealed class DataEvidence
    {
        public string File { get; init; } = "";
        public string Sha256 { get; init; } = "";
        public string QuoteBasis { get; init; } = "";
        public string Conversion { get; init; } = "";
        public DateTime First { get; init; }
        public DateTime EndExclusive { get; init; }
        public DateTime RequestedFrom { get; init; }
        public DateTime RequestedTo { get; init; }
        public int Bars { get; init; }
        public int WarmupBars { get; init; }
        public List<SessionGap> SessionGaps { get; init; } = new();
    }

    /// <summary>Loads only hashed, explicit-basis data. Gaps are caller-declared sessions, never inferred continuity.</summary>
    public sealed class DataLoader
    {
        private readonly string _dir;
        private readonly DataManifest _manifest;
        private readonly DateTime _warmup, _from, _to;
        private readonly bool _m15;
        private readonly Dictionary<string, SymbolData> _cache = new();
        private readonly object _lock = new();
        public DataLoader(string dir, string manifest, DateTime warmup, DateTime from, DateTime to, TimeSpan fxMaxAge, bool m15 = false)
        {
            _dir = dir; _manifest = DataManifest.Read(manifest); _warmup = warmup; _from = from; _to = to; _m15 = m15;
            Fx = new FxRates(fxMaxAge);
            // Load only conversion pairs required by selected instrument quote currencies, lazily in Load.
        }
        public FxRates Fx { get; }
        public string Source => _manifest.Source;
        public List<DataEvidence> Evidence { get; } = new();
        public bool Exists(string symbol) => _manifest.Files.Any(f => f.File == symbol + "_H1.csv");

        private BarSeries Read(string symbol, Tf tf)
        {
            string name = symbol + "_" + tf + ".csv";
            var entry = _manifest.Files.SingleOrDefault(f => f.File == name) ?? throw new InvalidDataException($"Missing manifest entry {name}");
            string path = Path.Combine(_dir, name);
            // Hash and parse the same immutable byte snapshot (prevents hash/read TOCTOU).
            byte[] bytes = System.IO.File.ReadAllBytes(path);
            string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!hash.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Hash mismatch: {name}");
            using var reader = new StringReader(System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\ufeff'));
            // Inspect full timestamps to anchor declarations on both sides of a clipped calendar edge.
            // Out-of-range prices are never returned to the simulator.
            var all = BarCsv.Load(reader, name, symbol, tf);
            var raw = BarSeries.FromBars(symbol, tf, Enumerable.Range(0, all.Count)
                .Where(i => all.Times[i] >= _warmup && all.Times[i] < _to).Select(i => all[i]).ToList());
            if (raw.Count == 0)
                throw new InvalidDataException($"{name}: no candles in [{_warmup:o},{_to:o}) including warmup");
            var gaps = new List<SessionGap>();
            void RequireGap(DateTime start, DateTime end)
            {
                if (start >= end) return;
                var matches = entry.SessionGaps.Where(g =>
                    (g.From < _warmup ? _warmup : g.From) == start && (g.To > _to ? _to : g.To) == end).ToList();
                if (matches.Count != 1) throw new InvalidDataException($"{name}: missing intervals [{start:o},{end:o}); require one exact declared session gap");
                var gap = matches[0];
                int next = Array.BinarySearch(all.Times, gap.From);
                if (next >= 0) throw new InvalidDataException($"{name}: session declaration overlaps a candle");
                next = ~next;
                if (gap.From.Ticks % tf.Duration().Ticks != 0 || gap.To.Ticks % tf.Duration().Ticks != 0 ||
                    (next > 0 && all.Times[next - 1] + tf.Duration() != gap.From) ||
                    (next < all.Count && all.Times[next] != gap.To))
                    throw new InvalidDataException($"{name}: session declaration must match actual adjacent candle endpoints");
                gaps.Add(gap);
            }
            RequireGap(_warmup, raw.Times[0]);
            for (int i = 1; i < raw.Count; i++) RequireGap(raw.Times[i - 1] + tf.Duration(), raw.Times[i]);
            RequireGap(raw.LastCloseTime, _to);
            Evidence.RemoveAll(e => e.File == name);
            Evidence.Add(new DataEvidence { File = name, Sha256 = hash, QuoteBasis = entry.QuoteBasis,
                Conversion = entry.QuoteBasis == "MID" ? "none" : "modeled MID = source " + (entry.QuoteBasis == "BID" ? "+" : "-") + " base catalog spread/2 (rollover x3); not observed MID",
                First = raw.Times[0], EndExclusive = raw.LastCloseTime, RequestedFrom = _warmup, RequestedTo = _to, Bars = raw.Count,
                WarmupBars = raw.Times.Count(t => t < _from), SessionGaps = gaps });
            var d = Catalog.Get(symbol);
            var normalized = new List<Bar>(raw.Count);
            for (int i = 0; i < raw.Count; i++)
            {
                double shift = entry.QuoteBasis == "MID" ? 0 : d.Spread / 2 * (raw.Times[i].Hour == 21 || raw.Times[i].Hour == 22 ? 3 : 1) * (entry.QuoteBasis == "BID" ? 1 : -1);
                var b = raw[i];
                var converted = new Bar(b.OpenTime, b.Open + shift, b.High + shift, b.Low + shift, b.Close + shift, b.Volume);
                if (!converted.IsValid) throw new InvalidDataException($"{name}: invalid modeled MID");
                normalized.Add(converted);
            }
            return BarSeries.FromBars(symbol, tf, normalized);
        }
        public SymbolData Load(string symbol)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(symbol, out var cached)) return cached;
                var d = Catalog.Get(symbol);
                var pairs = d.QuoteCcy switch { "USD" => Array.Empty<string>(), "EUR" => new[] { "EURUSD" }, "GBP" => new[] { "GBPUSD" }, "AUD" => new[] { "AUDUSD" }, "CAD" => new[] { "USDCAD" }, "JPY" => new[] { "EURUSD", "EURJPY" }, _ => throw new InvalidDataException("Unsupported quote currency") };
                foreach (var pair in pairs) Fx.Add(pair, Read(pair, Tf.H1));
                var sd = new SymbolData(d, Read(symbol, Tf.H1), _m15 ? Read(symbol, Tf.M15) : null);
                _cache.Add(symbol, sd); return sd;
            }
        }
    }
}
