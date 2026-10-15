using System;
using System.Collections.Generic;
using System.Linq;
using Apex.Core.Model;
using Apex.Core.Regime;
using Apex.Core.Scanner;

namespace Apex.Core.Engine
{
    /// <summary>Etap potoku decyzyjnego, do którego dotarła ocena.</summary>
    public enum DecisionStage
    {
        Data = 0,
        Indicators = 1,
        Regime = 2,
        Strategy = 3,
        Ranking = 4,
        Risk = 5,
        Execution = 6,
    }

    /// <summary>Ostateczna decyzja wynikająca z oceny (BUY/SELL tylko gdy pozycja została faktycznie otwarta).</summary>
    public enum DecisionVerdict
    {
        Wait = 0,
        Buy = 1,
        Sell = 2,
        /// <summary>Sygnał strategii czeka na ranking i kontrolę ryzyka (stan przejściowy).</summary>
        Pending = 3,
    }

    /// <summary>Pojedynczy punkt kontrolny potoku z rzeczywistym wynikiem.</summary>
    public sealed class DecisionGate
    {
        public string Name { get; init; } = "";
        public bool Passed { get; init; }
        public string Detail { get; init; } = "";
    }

    public sealed class RegimeSnapshot
    {
        public string Tf { get; init; } = "";
        public string Regime { get; init; } = "";
        public string RegimeName { get; init; } = "";
        public string Volatility { get; init; } = "";
        public int TrendDirection { get; init; }
        public double TrendStrength { get; init; }
        public double Adx { get; init; }
        public double PlusDi { get; init; }
        public double MinusDi { get; init; }
        public double EfficiencyRatio { get; init; }
        public double AtrPercentile { get; init; }
        public double Atr { get; init; }
        public double FastSlopeAtr { get; init; }
        public string Reason { get; init; } = "";

        public static RegimeSnapshot? From(RegimeAssessment? r, Tf? tf)
        {
            if (r == null || tf == null) return null;
            return new RegimeSnapshot
            {
                Tf = tf.Value.ToString(),
                Regime = r.Regime.ToString(),
                RegimeName = r.PolishName,
                Volatility = r.Volatility.ToString(),
                TrendDirection = r.TrendDirection,
                TrendStrength = Clean(r.TrendStrength),
                Adx = Clean(r.Adx),
                PlusDi = Clean(r.PlusDi),
                MinusDi = Clean(r.MinusDi),
                EfficiencyRatio = Clean(r.EfficiencyRatio),
                AtrPercentile = Clean(r.AtrPercentile),
                Atr = Clean(r.Atr),
                FastSlopeAtr = Clean(r.FastSlopeAtr),
                Reason = r.Reason,
            };
        }

        internal static double Clean(double v) => double.IsNaN(v) || double.IsInfinity(v) ? 0 : v;
    }

    /// <summary>
    /// Zapis jednej oceny instrumentu przez strategię – od danych do decyzji. Zawiera WYŁĄCZNIE wartości
    /// wyliczone przez silnik w trakcie normalnej pracy (nic nie jest symulowane dla wizualizacji).
    /// </summary>
    public sealed class DecisionEvent
    {
        public long Seq { get; set; }
        public DateTime TimeUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public string Symbol { get; set; } = "";
        public AssetClass AssetClass { get; set; }
        public StrategyId Strategy { get; set; }
        public string SignalTf { get; set; } = "";
        public string? FilterTf { get; set; }
        public DateTime BarTime { get; set; }
        public string? SignalId { get; set; }
        public DecisionStage Stage { get; set; }
        public DecisionVerdict Verdict { get; set; }
        public string? Direction { get; set; }
        public string Outcome { get; set; } = "";
        public string? StrategyReason { get; set; }
        public RegimeSnapshot? SignalRegime { get; set; }
        public RegimeSnapshot? FilterRegime { get; set; }
        public double? ReferencePrice { get; set; }
        public double? StopDistance { get; set; }
        public double? TakeProfitDistance { get; set; }
        public double? PlannedRewardRisk { get; set; }
        public ScoreBreakdown? Score { get; set; }
        public double? MinScore { get; set; }
        public List<string>? RiskFailures { get; set; }
        public double? Units { get; set; }
        public double? RiskAmount { get; set; }
        public double? CostToRisk { get; set; }
        public long? PositionId { get; set; }
        public List<DecisionGate> Gates { get; set; } = new();
    }

    /// <summary>Ostatni znany stan instrumentu w skanerze.</summary>
    public sealed class ScannerEntry
    {
        public string Symbol { get; set; } = "";
        public AssetClass AssetClass { get; set; }
        public DateTime LastEvaluationUtc { get; set; }
        public long LastSeq { get; set; }
        public DecisionVerdict LastVerdict { get; set; }
        public DecisionStage LastStage { get; set; }
        public string LastOutcome { get; set; } = "";
        public StrategyId LastStrategy { get; set; }
        public RegimeSnapshot? Regime { get; set; }
        public int Evaluations { get; set; }
    }

    public sealed class StrategyCounters
    {
        public int Evaluations { get; set; }
        public int NoSignal { get; set; }
        public int Skipped { get; set; }
        public int Signals { get; set; }
        public int Filtered { get; set; }
        public int RiskBlocked { get; set; }
        public int Executed { get; set; }
        public int ExecutedBuy { get; set; }
        public int ExecutedSell { get; set; }
        public int ExecutionFailed { get; set; }
    }

    /// <summary>
    /// Pasywny rejestrator decyzji silnika (dla AI Decision Center). Silnik zgłasza zdarzenia; rejestrator
    /// tylko je przechowuje (bufor cykliczny) – nie zwraca niczego do silnika i nie może wpłynąć na handel.
    /// Bezpieczny wątkowo: zapis z wątku silnika, odczyt z wątku API.
    /// </summary>
    public sealed class DecisionRecorder
    {
        private readonly object _lock = new();
        private readonly LinkedList<DecisionEvent> _events = new();
        private readonly Dictionary<string, DecisionEvent> _bySignal = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ScannerEntry> _scanner = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<StrategyId, StrategyCounters> _counters = new();
        private readonly int _capacity;
        private long _seq;

        public DecisionRecorder(int capacity = 600) => _capacity = capacity;

        public DateTime StartedUtc { get; } = DateTime.UtcNow;
        public DateTime LastCycleUtc { get; private set; }
        public long Cycles { get; private set; }

        internal void Cycle(DateTime utc)
        {
            lock (_lock)
            {
                LastCycleUtc = utc;
                Cycles++;
            }
        }

        private StrategyCounters C(StrategyId s)
        {
            if (!_counters.TryGetValue(s, out var c)) _counters[s] = c = new StrategyCounters();
            return c;
        }

        internal void Add(DecisionEvent e, bool skipped)
        {
            lock (_lock)
            {
                e.Seq = ++_seq;
                e.UpdatedUtc = e.TimeUtc;
                _events.AddLast(e);
                while (_events.Count > _capacity)
                {
                    var old = _events.First!.Value;
                    _events.RemoveFirst();
                    if (old.SignalId != null && _bySignal.TryGetValue(old.SignalId, out var x) && ReferenceEquals(x, old)) _bySignal.Remove(old.SignalId);
                }
                if (e.SignalId != null) _bySignal[e.SignalId] = e;
                var c = C(e.Strategy);
                c.Evaluations++;
                if (skipped) c.Skipped++;
                else if (e.Stage >= DecisionStage.Strategy && e.Verdict == DecisionVerdict.Pending) c.Signals++;
                else c.NoSignal++;
                Touch(e);
            }
        }

        /// <summary>Aktualizacja zdarzenia sygnału na dalszych etapach (ranking, ryzyko, wykonanie).</summary>
        internal void Update(string signalId, Action<DecisionEvent> change, Action<StrategyCounters>? counters, DateTime utc)
        {
            lock (_lock)
            {
                if (!_bySignal.TryGetValue(signalId, out var e)) return;
                var before = e.Verdict;
                change(e);
                if (before != e.Verdict && e.Verdict == DecisionVerdict.Buy) C(e.Strategy).ExecutedBuy++;
                if (before != e.Verdict && e.Verdict == DecisionVerdict.Sell) C(e.Strategy).ExecutedSell++;
                e.Seq = ++_seq;
                e.UpdatedUtc = utc;
                _events.Remove(e);
                _events.AddLast(e);
                if (counters != null) counters(C(e.Strategy));
                Touch(e);
            }
        }

        /// <summary>Zmienia opis wyniku tylko gdy się zmienił (bez sztucznego "odświeżania" zdarzeń).</summary>
        internal void SetOutcome(string signalId, string outcome, DateTime utc)
        {
            lock (_lock)
            {
                if (!_bySignal.TryGetValue(signalId, out var e) || e.Outcome == outcome) return;
            }
            Update(signalId, e => e.Outcome = outcome, null, utc);
        }

        private void Touch(DecisionEvent e)
        {
            if (!_scanner.TryGetValue(e.Symbol, out var s)) _scanner[e.Symbol] = s = new ScannerEntry { Symbol = e.Symbol, AssetClass = e.AssetClass };
            s.LastEvaluationUtc = e.UpdatedUtc;
            s.LastSeq = e.Seq;
            s.LastVerdict = e.Verdict;
            s.LastStage = e.Stage;
            s.LastOutcome = e.Outcome;
            s.LastStrategy = e.Strategy;
            if (e.FilterRegime != null || e.SignalRegime != null) s.Regime = e.FilterRegime ?? e.SignalRegime;
            if (e.Stage <= DecisionStage.Strategy) s.Evaluations++;
        }

        public (List<DecisionEvent> events, long lastSeq) Since(long seq, int max = 300)
        {
            lock (_lock)
            {
                var list = _events.Where(e => e.Seq > seq).OrderBy(e => e.Seq).ToList();
                if (list.Count > max) list = list.Skip(list.Count - max).ToList();
                return (list, _seq);
            }
        }

        public List<ScannerEntry> Scanner()
        {
            lock (_lock) return _scanner.Values.Select(s => (ScannerEntry)s.MemberwiseCloneEntry()).ToList();
        }

        public Dictionary<StrategyId, StrategyCounters> Counters()
        {
            lock (_lock) return _counters.ToDictionary(k => k.Key, v => new StrategyCounters
            {
                Evaluations = v.Value.Evaluations, NoSignal = v.Value.NoSignal, Skipped = v.Value.Skipped, Signals = v.Value.Signals,
                Filtered = v.Value.Filtered, RiskBlocked = v.Value.RiskBlocked, Executed = v.Value.Executed, ExecutionFailed = v.Value.ExecutionFailed,
                ExecutedBuy = v.Value.ExecutedBuy, ExecutedSell = v.Value.ExecutedSell,
            });
        }
    }

    internal static class ScannerEntryExtensions
    {
        public static ScannerEntry MemberwiseCloneEntry(this ScannerEntry s) => new()
        {
            Symbol = s.Symbol, AssetClass = s.AssetClass, LastEvaluationUtc = s.LastEvaluationUtc, LastSeq = s.LastSeq, LastVerdict = s.LastVerdict,
            LastStage = s.LastStage, LastOutcome = s.LastOutcome, LastStrategy = s.LastStrategy, Regime = s.Regime, Evaluations = s.Evaluations,
        };
    }
}
