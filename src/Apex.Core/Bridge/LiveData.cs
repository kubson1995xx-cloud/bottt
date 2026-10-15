using System;
using System.Collections.Generic;
using System.Linq;
using Apex.Core.Engine;
using Apex.Core.Indicators;
using Apex.Core.Model;
using Apex.Core.Safety;

namespace Apex.Core.Bridge
{
    /// <summary>
    /// Magazyn ZAMKNIĘTYCH świec otrzymywanych z platformy (most lub natywny cBot) + dostawca wskaźników dla silnika.
    /// Bezpieczny wątkowo. FeatureSet przebudowywany tylko gdy pojawi się nowa świeca.
    /// </summary>
    public sealed class BarStore : IFeatureProvider
    {
        private readonly object _lock = new();
        private readonly Dictionary<(string, Tf), List<Bar>> _bars = new();
        private readonly Dictionary<(string, Tf), (DateTime last, int count, FeatureSet fs)> _features = new();
        private readonly int _maxBars;

        public BarStore(int maxBars = 2000) => _maxBars = maxBars;

        private static (string, Tf) K(string symbol, Tf tf) => (symbol.ToUpperInvariant(), tf);

        /// <summary>Dodaje świece. snapshot=true zastępuje historię. Nieprawidłowe i niechronologiczne świece są odrzucane.</summary>
        public int Merge(string symbol, Tf tf, IEnumerable<Bar> bars, bool snapshot)
        {
            int added = 0;
            lock (_lock)
            {
                var key = K(symbol, tf);
                if (snapshot || !_bars.TryGetValue(key, out var list))
                {
                    list = new List<Bar>();
                    _bars[key] = list;
                }
                foreach (var b in bars.OrderBy(x => x.OpenTime))
                {
                    if (!b.IsValid) continue;
                    if (list.Count > 0)
                    {
                        var last = list[^1];
                        if (b.OpenTime < last.OpenTime) continue;
                        if (b.OpenTime == last.OpenTime) { list[^1] = b; continue; }
                    }
                    list.Add(b);
                    added++;
                }
                if (list.Count > _maxBars) list.RemoveRange(0, list.Count - _maxBars);
            }
            return added;
        }

        public int Count(string symbol, Tf tf)
        {
            lock (_lock) return _bars.TryGetValue(K(symbol, tf), out var l) ? l.Count : 0;
        }

        public DateTime? LastClosedBarTime(string symbol, Tf tf, DateTime utc)
        {
            lock (_lock)
            {
                if (!_bars.TryGetValue(K(symbol, tf), out var l) || l.Count == 0) return null;
                return l[^1].OpenTime;
            }
        }

        public bool TryGet(string symbol, Tf tf, DateTime utc, out FeatureSet features, out int index)
        {
            features = null!;
            index = -1;
            lock (_lock)
            {
                var key = K(symbol, tf);
                if (!_bars.TryGetValue(key, out var l) || l.Count == 0) return false;
                var last = l[^1].OpenTime;
                if (_features.TryGetValue(key, out var cached) && cached.last == last && cached.count == l.Count)
                {
                    features = cached.fs;
                }
                else
                {
                    features = new FeatureSet(BarSeries.FromBars(symbol, tf, l));
                    _features[key] = (last, l.Count, features);
                }
                index = features.Count - 1;
                return true;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _bars.Clear();
                _features.Clear();
            }
        }
    }

    public static class TfNames
    {
        public static bool TryParse(string? s, out Tf tf)
        {
            switch ((s ?? "").ToUpperInvariant())
            {
                case "M5": tf = Tf.M5; return true;
                case "M15": tf = Tf.M15; return true;
                case "M30": tf = Tf.M30; return true;
                case "H1": tf = Tf.H1; return true;
                case "H4": tf = Tf.H4; return true;
                case "D1": tf = Tf.D1; return true;
                default: tf = Tf.H1; return false;
            }
        }
    }

    /// <summary>Ręczne ustawienie prowizji (np. MT5 nie udostępnia prowizji przez API).</summary>
    public sealed class CommissionOverride
    {
        public string Kind { get; set; } = "UsdPerLot";
        public double Value { get; set; }
    }

    public static class SpecMapper
    {
        public static CommissionKind ParseCommission(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
        {
            "none" => CommissionKind.None,
            "usdpermillionusdvolume" => CommissionKind.UsdPerMillionUsdVolume,
            "usdperlot" or "usdperonelot" => CommissionKind.UsdPerLot,
            "percentofvolume" or "percentageoftradingvolume" => CommissionKind.PercentOfVolume,
            "quotecurrencyperlot" or "quotecurrencyperonelot" => CommissionKind.QuoteCurrencyPerLot,
            _ => CommissionKind.Unknown,
        };

        public static InstrumentSpec ToSpec(SymbolDto s, double? currentPipValue, CommissionOverride? commission)
        {
            var cls = InstrumentClassifier.Classify(s.Name, s.Description, s.BaseAsset, s.QuoteAsset);
            var kind = commission != null ? ParseCommission(commission.Kind) : ParseCommission(s.CommissionKind);
            double value = commission != null ? commission.Value : s.CommissionValue;
            return new InstrumentSpec
            {
                Name = s.Name,
                Description = s.Description,
                AssetClass = cls,
                BaseAsset = s.BaseAsset,
                QuoteAsset = s.QuoteAsset,
                Digits = s.Digits,
                PipSize = s.PipSize,
                TickSize = s.TickSize,
                PipValuePerUnit = currentPipValue is > 0 ? currentPipValue.Value : s.PipValue,
                LotSize = s.LotSize,
                VolumeMin = s.VolumeMin,
                VolumeStep = s.VolumeStep,
                VolumeMax = s.VolumeMax,
                MinStopDistancePips = s.MinStopPips,
                TradingEnabled = s.TradingEnabled,
                CloseOnly = s.CloseOnly,
                CommissionKind = kind,
                CommissionValue = value,
                UsdToAccount = s.UsdToAccount > 0 ? s.UsdToAccount : 1,
                QuoteToAccount = s.QuoteToAccount > 0 ? s.QuoteToAccount : 1,
            };
        }
    }

    public sealed class UniverseSettings
    {
        /// <summary>auto = wybór automatyczny; manual = tylko instrumenty z listy <see cref="Manual"/>.</summary>
        public string Mode { get; set; } = "auto";
        public List<string> Manual { get; set; } = new();
        public List<string> Blacklist { get; set; } = new();
        public int MaxTotal { get; set; } = 30;
        public Dictionary<AssetClass, int> MaxPerClass { get; set; } = new()
        {
            [AssetClass.Forex] = 10,
            [AssetClass.Metal] = 2,
            [AssetClass.Energy] = 2,
            [AssetClass.Index] = 8,
            [AssetClass.Commodity] = 4,
            [AssetClass.Bond] = 4,
            [AssetClass.Stock] = 5,
            [AssetClass.Etf] = 3,
        };
    }

    public sealed class UniverseCandidate
    {
        public string Symbol { get; init; } = "";
        public AssetClass AssetClass { get; init; }
        public bool Selected { get; init; }
        public string Reason { get; init; } = "";
        public string Description { get; init; } = "";
    }

    /// <summary>
    /// Wybór instrumentów do skanowania. Kryptowaluty i instrumenty nierozpoznane są ZAWSZE odrzucane.
    /// W trybie automatycznym preferowane są płynne instrumenty z listy (nazwy typowe dla brokerów cTrader/MT5),
    /// z limitami na klasę aktywów, aby nie subskrybować setek symboli.
    /// </summary>
    public static class UniverseSelector
    {
        public static readonly Dictionary<AssetClass, string[]> Preferred = new()
        {
            [AssetClass.Forex] = new[] { "EURUSD", "GBPUSD", "USDJPY", "AUDUSD", "USDCAD", "USDCHF", "NZDUSD", "EURJPY", "GBPJPY", "EURGBP", "AUDJPY", "EURAUD", "EURCHF", "CADJPY" },
            [AssetClass.Metal] = new[] { "XAUUSD", "XAGUSD", "GOLD", "SILVER" },
            [AssetClass.Energy] = new[] { "SPOTCRUDE", "XTIUSD", "WTI", "USOIL", "SPOTBRENT", "XBRUSD", "UKOIL", "NATGAS", "XNGUSD" },
            [AssetClass.Index] = new[] { "US500", "SPX500", "US30", "NAS100", "USTEC", "US2000", "GER40", "DE40", "UK100", "JPN225", "JP225", "AUS200", "FRA40", "EUSTX50", "NETH25" },
            [AssetClass.Commodity] = new[] { "CORN", "WHEAT", "SOYBEANS", "SOYBEAN", "SUGAR", "COFFEE", "COCOA", "COTTON" },
            [AssetClass.Bond] = new[] { "USB10Y", "UST10Y", "DE10YB", "BUND", "UK10YB", "GILT", "USB02Y" },
            [AssetClass.Stock] = new[] { "AAPL.US", "MSFT.US", "NVDA.US", "AMZN.US", "GOOGL.US", "META.US", "TSLA.US" },
            [AssetClass.Etf] = new[] { "SPY.US", "QQQ.US", "IWM.US", "GLD.US" },
        };

        public static List<UniverseCandidate> Select(IEnumerable<SymbolDto> symbols, UniverseSettings s, ISet<AssetClass> classesWithStrategies, ISet<AssetClass> disabledClasses)
        {
            var all = new List<(SymbolDto dto, AssetClass cls)>();
            foreach (var x in symbols)
                all.Add((x, InstrumentClassifier.Classify(x.Name, x.Description, x.BaseAsset, x.QuoteAsset)));

            var result = new List<UniverseCandidate>();
            var black = new HashSet<string>(s.Blacklist.Select(InstrumentClassifier.Normalize));
            var manual = new HashSet<string>(s.Manual.Select(InstrumentClassifier.Normalize));
            var chosen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var perClass = new Dictionary<AssetClass, int>();

            string? Reject((SymbolDto dto, AssetClass cls) e)
            {
                if (e.cls == AssetClass.Crypto) return "kryptowaluta – trwale wykluczona";
                if (e.cls == AssetClass.Unknown) return "nierozpoznana klasa – pominięty (zasada fail-closed)";
                if (e.dto.IsCustom) return "symbol niestandardowy";
                if (!e.dto.TradingEnabled || e.dto.CloseOnly) return "handel wyłączony u brokera";
                if (black.Contains(InstrumentClassifier.Normalize(e.dto.Name))) return "na liście wykluczeń";
                if (disabledClasses.Contains(e.cls)) return "klasa wyłączona przez użytkownika";
                if (!classesWithStrategies.Contains(e.cls)) return "brak zweryfikowanej strategii dla tej klasy";
                return null;
            }

            bool manualMode = string.Equals(s.Mode, "manual", StringComparison.OrdinalIgnoreCase);
            if (!manualMode)
            {
                // 1) instrumenty preferowane, w kolejności listy
                foreach (var cls in Preferred.Keys)
                {
                    foreach (var pref in Preferred[cls])
                    {
                        string pn = InstrumentClassifier.Normalize(pref);
                        foreach (var e in all.Where(a => a.cls == cls && Matches(InstrumentClassifier.Normalize(a.dto.Name), pn)))
                            TryAdd(e, "preferowany płynny instrument");
                    }
                }
            }
            foreach (var e in all)
            {
                string norm = InstrumentClassifier.Normalize(e.dto.Name);
                if (manualMode)
                {
                    if (manual.Contains(norm)) TryAdd(e, "wybrany ręcznie");
                }
            }
            foreach (var e in all)
            {
                if (chosen.Contains(e.dto.Name)) continue;
                result.Add(new UniverseCandidate { Symbol = e.dto.Name, AssetClass = e.cls, Selected = false, Description = e.dto.Description, Reason = Reject(e) ?? (manualMode ? "nie wybrany ręcznie" : "poza listą preferowanych / limit") });
            }
            return result.OrderByDescending(r => r.Selected).ThenBy(r => r.AssetClass).ThenBy(r => r.Symbol).ToList();

            void TryAdd((SymbolDto dto, AssetClass cls) e, string reason)
            {
                if (chosen.Contains(e.dto.Name)) return;
                var rej = Reject(e);
                if (rej != null) return;
                if (chosen.Count >= s.MaxTotal) return;
                perClass.TryGetValue(e.cls, out int n);
                int max = s.MaxPerClass.TryGetValue(e.cls, out var m) ? m : 3;
                if (n >= max) return;
                perClass[e.cls] = n + 1;
                chosen.Add(e.dto.Name);
                result.Add(new UniverseCandidate { Symbol = e.dto.Name, AssetClass = e.cls, Selected = true, Description = e.dto.Description, Reason = reason });
            }
        }

        /// <summary>Dopasowanie nazwy brokera do nazwy preferowanej (sufiksy typu ".cash", "m", ".r", "-ECN").</summary>
        public static bool Matches(string normBroker, string normPreferred)
        {
            if (normBroker == normPreferred) return true;
            if (!normBroker.StartsWith(normPreferred, StringComparison.Ordinal)) return false;
            string rest = normBroker.Substring(normPreferred.Length);
            return rest is "CASH" or "M" or "R" or "ECN" or "PRO" or "RAW" or "SPOT" or "I" or "C" or "STD";
        }
    }
}
