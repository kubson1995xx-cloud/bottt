using System;
using System.Collections.Generic;
using System.Linq;
using Apex.Core.Execution;
using Apex.Core.Indicators;
using Apex.Core.Management;
using Apex.Core.Model;
using Apex.Core.Performance;
using Apex.Core.Regime;
using Apex.Core.Risk;
using Apex.Core.Scanner;

namespace Apex.Core.Engine
{
    public sealed class EngineConfig
    {
        /// <summary>Etykieta pozycji bota – po niej bot rozpoznaje WYŁĄCZNIE własne pozycje.</summary>
        public string Label { get; set; } = "APEX";
        public RiskSettings Risk { get; set; } = new();
        public ScoringSettings Scoring { get; set; } = new();
        public RegimeSettings Regime { get; set; } = new();
        public NewsSettings News { get; set; } = new();
        /// <summary>Okno zbierania sygnałów z wielu instrumentów przed rankingiem (sekundy).</summary>
        public int CollectionWindowSeconds { get; set; } = 20;
        /// <summary>Czas na weryfikację stanu po niepewnej odpowiedzi serwera.</summary>
        public int VerificationTimeoutSeconds { get; set; } = 90;
        /// <summary>Liczba kolejnych błędów brokera, po której nowe wejścia są wstrzymywane.</summary>
        public int MaxConsecutiveErrors { get; set; } = 5;
        /// <summary>Brak nowych wejść, gdy do zamknięcia sesji zostało mniej minut niż ta wartość.</summary>
        public int NoEntryMinutesBeforeClose { get; set; } = 30;
        public Tf CorrelationTf { get; set; } = Tf.H4;
        public int CorrelationLookback { get; set; } = 120;
        /// <summary>Maksymalny poślizg przy wejściu (zlecenie market-range) w ATR. 0 = bez limitu.</summary>
        public double MaxEntrySlippageAtr { get; set; } = 0.25;
        /// <summary>Maksymalny wiek notowania (sekundy) – starsze uznajemy za brak danych.</summary>
        public int MaxQuoteAgeSeconds { get; set; } = 180;
        public bool IsBacktest { get; set; }
        public bool UseHealthMonitor { get; set; } = true;
        /// <summary>Maksymalna liczba nowych pozycji otwieranych w jednym cyklu rankingu.</summary>
        public int MaxNewPositionsPerCycle { get; set; } = 2;

        public string? Validate()
        {
            if (string.IsNullOrWhiteSpace(Label) || Label.Length > 20) return "Etykieta bota musi mieć 1–20 znaków.";
            var r = Risk.Validate();
            if (r != null) return r;
            if (Scoring.MinScore < 0 || Scoring.MinScore > 100) return "Minimalna ocena okazji musi być w zakresie 0–100.";
            return null;
        }
    }

    /// <summary>Stan silnika zapisywany trwale (odporność na restart).</summary>
    public sealed class EngineState
    {
        public int SchemaVersion { get; set; } = 2;
        public long AccountNumber { get; set; }
        public string Broker { get; set; } = "";
        public string Currency { get; set; } = "";
        public string Label { get; set; } = "";
        public EngineMode Mode { get; set; } = EngineMode.Stopped;
        public string ModeReason { get; set; } = "";
        public RiskState Risk { get; set; } = new();
        public List<LedgerEntry> Ledger { get; set; } = new();
        public List<TradePlan> Plans { get; set; } = new();
        public List<StrategyId> DisabledStrategies { get; set; } = new();
        public List<AssetClass> DisabledClasses { get; set; } = new();
        public double? RiskPerTradeOverride { get; set; }
        public List<string> Suspended { get; set; } = new();
        public List<TradeRecord> RecentTrades { get; set; } = new();
        public HashSet<long> AccountedPositions { get; set; } = new();
        public List<ClosedPositionInfo> ClosingDeals { get; set; } = new();
        public List<TradeRecord> ClosedRecords { get; set; } = new();
        public List<TradeRecord> ClosedOutbox { get; set; } = new();
        public Dictionary<long, CloseIntent> PendingCloses { get; set; } = new();
        public double RealizedTotal { get; set; }
        public int TradesTotal { get; set; }
        public DateTime SavedUtc { get; set; }
    }

    public sealed class CloseIntent
    {
        public string Reason { get; set; } = "";
        public int Attempts { get; set; }
        public DateTime NextTryUtc { get; set; }
    }

    /// <summary>Śledzenie typowego spreadu każdego instrumentu (mediana z ostatnich próbek).</summary>
    public sealed class SpreadTracker
    {
        private readonly Dictionary<string, Queue<double>> _samples = new(StringComparer.OrdinalIgnoreCase);
        private readonly int _capacity;

        public SpreadTracker(int capacity = 240) => _capacity = capacity;

        public void Add(string symbol, double spread)
        {
            if (double.IsNaN(spread) || spread < 0) return;
            if (!_samples.TryGetValue(symbol, out var q)) _samples[symbol] = q = new Queue<double>();
            q.Enqueue(spread);
            while (q.Count > _capacity) q.Dequeue();
        }

        public int Count(string symbol) => _samples.TryGetValue(symbol, out var q) ? q.Count : 0;

        public double Median(string symbol) => _samples.TryGetValue(symbol, out var q) ? Ta.Median(q) : double.NaN;
    }

    public static class CorrelationCalculator
    {
        /// <summary>Korelacja logarytmicznych stóp zwrotu dwóch instrumentów wyrównanych po czasie świec.</summary>
        public static double Compute(FeatureSet a, int ia, FeatureSet b, int ib, int lookback)
        {
            if (ia < 2 || ib < 2) return double.NaN;
            var ra = a.LogReturns();
            var rb = b.LogReturns();
            var mapB = new Dictionary<DateTime, double>();
            for (int j = Math.Max(1, ib - lookback * 2); j <= ib; j++) mapB[b.Bars.Times[j]] = rb[j];
            var xs = new List<double>(lookback);
            var ys = new List<double>(lookback);
            for (int j = ia; j >= 1 && xs.Count < lookback; j--)
            {
                if (mapB.TryGetValue(a.Bars.Times[j], out var y))
                {
                    xs.Add(ra[j]);
                    ys.Add(y);
                }
            }
            return Ta.Correlation(xs, ys, Math.Min(30, lookback / 2));
        }
    }

    /// <summary>Migawka stanu dla panelu kontrolnego.</summary>
    public sealed class EngineStatus
    {
        public bool HistoryReady { get; init; }
        public string HistoryMessage { get; init; } = "";
        public EngineMode Mode { get; init; }
        public string ModeReason { get; init; } = "";
        public bool IsLive { get; init; }
        public bool GuardAllowed { get; init; }
        public string GuardMessage { get; init; } = "";
        public bool Connected { get; init; }
        public long AccountNumber { get; init; }
        public string Broker { get; init; } = "";
        public string Currency { get; init; } = "";
        public double Balance { get; init; }
        public double Equity { get; init; }
        public double DailyPnl { get; init; }
        public double DailyPnlPct { get; init; }
        public double TotalPnl { get; init; }
        public double TotalPnlPct { get; init; }
        public double DrawdownPct { get; init; }
        public double RiskPerTradePct { get; init; }
        public double MaxDailyLossPct { get; init; }
        public double MaxDrawdownPct { get; init; }
        public double EffectiveLeverage { get; init; }
        public double MaxEffectiveLeverage { get; init; }
        public bool DailyLimitHit { get; init; }
        public bool MaxDrawdownHit { get; init; }
        public IReadOnlyList<string> OpenPositions { get; init; } = Array.Empty<string>();
        public int OwnPositionCount { get; init; }
        public int MaxPositions { get; init; }
        public IReadOnlyDictionary<AssetClass, int> UniverseByClass { get; init; } = new Dictionary<AssetClass, int>();
        public IReadOnlyList<string> RecentSignals { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> RecentErrors { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> ActiveStrategies { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Suspended { get; init; } = Array.Empty<string>();
        public IReadOnlyCollection<StrategyId> DisabledStrategies { get; init; } = Array.Empty<StrategyId>();
        public IReadOnlyCollection<AssetClass> DisabledClasses { get; init; } = Array.Empty<AssetClass>();
        public string NewsStatus { get; init; } = "";
        public int TradesTotal { get; init; }
        public double RealizedTotal { get; init; }
    }

    /// <summary>Bufor cykliczny ostatnich komunikatów.</summary>
    public sealed class RingLog
    {
        private readonly LinkedList<string> _items = new();
        private readonly int _cap;
        public RingLog(int cap) => _cap = cap;
        public void Add(string s)
        {
            _items.AddFirst(s);
            while (_items.Count > _cap) _items.RemoveLast();
        }
        public IReadOnlyList<string> Items => _items.ToList();
    }
}
