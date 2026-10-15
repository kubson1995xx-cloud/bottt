using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Apex.Core.Model;
using Apex.Core.Performance;
using Apex.Core.Strategies;

namespace Apex.Core.Adaptive
{
    /// <summary>Status walidacji strategii w profilu.</summary>
    public enum ValidationStatus
    {
        /// <summary>Spełnia wszystkie kryteria (walk-forward + holdout) – dozwolona na DEMO i (po odblokowaniu) LIVE.</summary>
        Validated,
        /// <summary>Częściowo potwierdzona – działa WYŁĄCZNIE na kontach DEMO.</summary>
        Experimental,
        /// <summary>Odrzucona przez testy – nie otwiera pozycji.</summary>
        Rejected,
    }

    /// <summary>Wpis profilu: strategia z parametrami dla danej klasy aktywów.</summary>
    public sealed class ProfileEntry
    {
        public AssetClass AssetClass { get; set; }
        public StrategyId Strategy { get; set; }
        public bool Enabled { get; set; } = true;
        public ValidationStatus Status { get; set; } = ValidationStatus.Validated;
        /// <summary>Uzasadnienie (np. odnośnik do wyników walk-forward).</summary>
        public string Evidence { get; set; } = "";
        public TrendPullbackParams? TrendPullback { get; set; }
        public DonchianBreakoutParams? Breakout { get; set; }
        public MeanReversionParams? MeanReversion { get; set; }
        public MomentumImpulseParams? Momentum { get; set; }
        public IndexDipBuyParams? DipBuy { get; set; }
        public DonchianBreakoutParams? DailyTrend { get; set; }
    }

    /// <summary>
    /// Wersjonowany profil strategii. Zmiana konfiguracji = nowa wersja profilu (po pozytywnym teście out-of-sample),
    /// poprzednie wersje pozostają dostępne do przywrócenia. Bot NIE modyfikuje parametrów samodzielnie.
    /// </summary>
    public sealed class StrategyProfile
    {
        public string Version { get; set; } = "";
        public string Description { get; set; } = "";
        public string Validation { get; set; } = "";
        public List<ProfileEntry> Entries { get; set; } = new();

        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
            PropertyNameCaseInsensitive = true,
        };

        public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

        public static StrategyProfile FromJson(string json) =>
            JsonSerializer.Deserialize<StrategyProfile>(json, JsonOptions) ?? throw new JsonException("Pusty profil.");
    }

    /// <summary>Zbiór aktywnych instancji strategii pogrupowanych po klasie aktywów.</summary>
    public sealed class StrategyBook
    {
        private readonly Dictionary<AssetClass, List<IStrategy>> _byClass = new();

        private readonly HashSet<(StrategyId, AssetClass)> _experimental = new();

        /// <param name="liveAccount">Dla kont LIVE strategie eksperymentalne są pomijane.</param>
        public StrategyBook(StrategyProfile profile, bool liveAccount = false)
        {
            Profile = profile;
            foreach (var e in profile.Entries)
            {
                if (!e.Enabled || e.Status == ValidationStatus.Rejected || e.AssetClass == AssetClass.Crypto || e.AssetClass == AssetClass.Unknown) continue;
                if (e.Status == ValidationStatus.Experimental)
                {
                    if (liveAccount) continue;
                    _experimental.Add((e.Strategy, e.AssetClass));
                }
                var s = Create(e);
                if (s == null || !s.AppliesTo(e.AssetClass)) continue;
                if (!_byClass.TryGetValue(e.AssetClass, out var list)) _byClass[e.AssetClass] = list = new List<IStrategy>();
                list.Add(s);
            }
        }

        public StrategyProfile Profile { get; }

        public IReadOnlyList<IStrategy> For(AssetClass c) =>
            _byClass.TryGetValue(c, out var list) ? list : (IReadOnlyList<IStrategy>)Array.Empty<IStrategy>();

        public IEnumerable<AssetClass> Classes => _byClass.Keys;

        public bool IsExperimental(StrategyId id, AssetClass c) => _experimental.Contains((id, c));

        public IEnumerable<IStrategy> All => _byClass.Values.SelectMany(x => x);

        /// <summary>Instancja strategii do oceny reguł wyjścia (także gdy strategia została wyłączona dla nowych wejść).</summary>
        public IStrategy ExitStrategyFor(StrategyId id, AssetClass c)
        {
            var s = For(c).FirstOrDefault(x => x.Id == id);
            if (s != null) return s;
            return id switch
            {
                StrategyId.TrendPullback => new TrendPullbackStrategy(),
                StrategyId.DonchianBreakout => new DonchianBreakoutStrategy(),
                StrategyId.MeanReversion => new MeanReversionStrategy(),
                StrategyId.MomentumImpulse => new MomentumImpulseStrategy(),
                StrategyId.DailyTrend => new DailyTrendStrategy(),
                _ => new IndexDipBuyStrategy(),
            };
        }

        public static IStrategy? Create(ProfileEntry e)
        {
            string variant = $"{e.AssetClass}";
            return e.Strategy switch
            {
                StrategyId.TrendPullback => new TrendPullbackStrategy(e.TrendPullback ?? new TrendPullbackParams(), variant),
                StrategyId.DonchianBreakout => new DonchianBreakoutStrategy(e.Breakout ?? new DonchianBreakoutParams(), variant),
                StrategyId.MeanReversion => new MeanReversionStrategy(e.MeanReversion ?? new MeanReversionParams(), variant),
                StrategyId.MomentumImpulse => new MomentumImpulseStrategy(e.Momentum ?? new MomentumImpulseParams(), variant),
                StrategyId.IndexDipBuy => new IndexDipBuyStrategy(e.DipBuy ?? new IndexDipBuyParams(), variant),
                StrategyId.DailyTrend => new DailyTrendStrategy(e.DailyTrend ?? DailyTrendStrategy.Defaults, variant),
                _ => null,
            };
        }

        /// <summary>Walidacja zakresów parametrów – odrzuca profile z wartościami spoza bezpiecznych granic.</summary>
        public static List<string> Validate(StrategyProfile p)
        {
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(p.Version)) errors.Add("brak wersji profilu");
            foreach (var e in p.Entries)
            {
                string w = $"{e.AssetClass}/{e.Strategy}: ";
                if (e.AssetClass == AssetClass.Crypto) errors.Add(w + "kryptowaluty są całkowicie wykluczone");
                CheckExit(w, GetExit(e), errors);
                switch (e.Strategy)
                {
                    case StrategyId.TrendPullback when e.TrendPullback != null:
                        Range(w + "StopAtr", e.TrendPullback.StopAtr, 0.5, 6, errors);
                        Range(w + "EmaFast", e.TrendPullback.EmaFast, 5, 100, errors);
                        Range(w + "EmaMid", e.TrendPullback.EmaMid, 10, 300, errors);
                        break;
                    case StrategyId.DonchianBreakout when e.Breakout != null:
                        Range(w + "StopAtr", e.Breakout.StopAtr, 0.5, 6, errors);
                        Range(w + "Channel", e.Breakout.Channel, 5, 200, errors);
                        break;
                    case StrategyId.MeanReversion when e.MeanReversion != null:
                        Range(w + "StopAtr", e.MeanReversion.StopAtr, 0.5, 6, errors);
                        Range(w + "BbK", e.MeanReversion.BbK, 1, 4, errors);
                        break;
                    case StrategyId.MomentumImpulse when e.Momentum != null:
                        Range(w + "MaxStopAtr", e.Momentum.MaxStopAtr, 0.5, 6, errors);
                        break;
                    case StrategyId.DailyTrend when e.DailyTrend != null:
                        Range(w + "StopAtr", e.DailyTrend.StopAtr, 0.5, 6, errors);
                        Range(w + "Channel", e.DailyTrend.Channel, 10, 300, errors);
                        break;
                    case StrategyId.IndexDipBuy when e.DipBuy != null:
                        Range(w + "StopAtr", e.DipBuy.StopAtr, 0.5, 8, errors);
                        if (e.AssetClass != AssetClass.Index && e.AssetClass != AssetClass.Etf) errors.Add(w + "strategia tylko dla indeksów/ETF");
                        break;
                }
            }
            return errors;
        }

        private static ExitPlan? GetExit(ProfileEntry e) => e.Strategy switch
        {
            StrategyId.TrendPullback => e.TrendPullback?.Exit,
            StrategyId.DonchianBreakout => e.Breakout?.Exit,
            StrategyId.MeanReversion => e.MeanReversion?.Exit,
            StrategyId.MomentumImpulse => e.Momentum?.Exit,
            StrategyId.IndexDipBuy => e.DipBuy?.Exit,
            StrategyId.DailyTrend => e.DailyTrend?.Exit,
            _ => null,
        };

        private static void CheckExit(string w, ExitPlan? x, List<string> errors)
        {
            if (x == null) return;
            if (x.TakeProfitR.HasValue) Range(w + "TakeProfitR", x.TakeProfitR.Value, 0.3, 20, errors);
            if (x.PartialFraction < 0 || x.PartialFraction >= 1) errors.Add(w + "PartialFraction poza zakresem [0;1)");
            if (x.TrailAtrMultiple < 0 || x.TrailAtrMultiple > 10) errors.Add(w + "TrailAtrMultiple poza zakresem");
            if (x.MaxBarsHeld < 0) errors.Add(w + "MaxBarsHeld ujemne");
        }

        private static void Range(string name, double v, double min, double max, List<string> errors)
        {
            if (double.IsNaN(v) || v < min || v > max) errors.Add($"{name}={v} poza zakresem [{min};{max}]");
        }
    }

    /// <summary>
    /// Monitor kondycji strategii: wstrzymuje (nie modyfikuje!) parę strategia×klasa aktywów, gdy na żywo/demo
    /// po co najmniej N transakcjach górna granica jednostronnego przedziału ufności średniego R jest ujemna.
    /// Ponowne włączenie wyłącznie ręczne (panel). Zapobiega to dalszemu handlowaniu strategią, która przestała działać,
    /// bez dopasowywania parametrów do kilku ostatnich transakcji.
    /// </summary>
    public sealed class StrategyHealthMonitor
    {
        public int MinTrades { get; set; } = 30;
        public int Window { get; set; } = 60;
        public double Z { get; set; } = 1.645;

        public static string Key(StrategyId s, AssetClass c) => $"{s}:{c}";

        public List<string> Evaluate(IReadOnlyList<TradeRecord> trades, ISet<string> suspended)
        {
            var newly = new List<string>();
            foreach (var g in trades.GroupBy(t => Key(t.Strategy, t.AssetClass)))
            {
                if (suspended.Contains(g.Key)) continue;
                var recent = g.OrderBy(t => t.ExitTimeUtc).TakeLast(Window).Select(t => t.RMultiple).ToList();
                if (recent.Count < MinTrades) continue;
                double mean = recent.Average();
                double sd = Math.Sqrt(recent.Sum(r => (r - mean) * (r - mean)) / (recent.Count - 1));
                double upper = mean + Z * sd / Math.Sqrt(recent.Count);
                if (upper < 0)
                {
                    suspended.Add(g.Key);
                    newly.Add($"{g.Key}: średnio {mean:F2}R z {recent.Count} transakcji (górna granica {upper:F2}R < 0) – strategia wstrzymana.");
                }
            }
            return newly;
        }
    }
}
