using System;
using System.Collections.Generic;
using System.Globalization;
using Apex.Core.Indicators;
using Apex.Core.Model;
using Apex.Core.Regime;

namespace Apex.Core.Strategies
{
    /// <summary>
    /// Plan wyjścia z pozycji. Wartości w wielokrotnościach R (R = początkowa odległość SL).
    /// 0 = mechanizm wyłączony. Domyślne plany każdej strategii wynikają z testów (patrz docs/RAPORT_STRATEGII.md).
    /// </summary>
    public sealed class ExitPlan
    {
        /// <summary>Stały take profit w R. Null = brak TP (wyjście przez trailing/regułę strategii).</summary>
        public double? TakeProfitR { get; init; }
        public double BreakEvenAtR { get; init; }
        /// <summary>Przesunięcie SL ponad cenę wejścia przy break-even (pokrycie kosztów), w R.</summary>
        public double BreakEvenOffsetR { get; init; } = 0.05;
        public double TrailStartR { get; init; }
        /// <summary>Trailing typu chandelier: SL = ekstremum od wejścia ∓ X*ATR. 0 = wyłączony.</summary>
        public double TrailAtrMultiple { get; init; }
        public double PartialAtR { get; init; }
        public double PartialFraction { get; init; }
        /// <summary>Maksymalny czas utrzymywania pozycji w świecach interwału sygnałowego. 0 = bez limitu.</summary>
        public int MaxBarsHeld { get; init; }
        /// <summary>Zamknięcie, gdy trend na interwale filtrującym odwróci się przeciw pozycji.</summary>
        public bool ExitOnTrendReversal { get; init; }

        public string Describe()
        {
            var parts = new List<string>();
            parts.Add(TakeProfitR.HasValue ? $"TP {TakeProfitR.Value.ToString("0.##", CultureInfo.InvariantCulture)}R" : "bez TP");
            if (BreakEvenAtR > 0) parts.Add($"BE@{BreakEvenAtR.ToString("0.##", CultureInfo.InvariantCulture)}R");
            if (TrailAtrMultiple > 0) parts.Add($"trailing {TrailAtrMultiple.ToString("0.##", CultureInfo.InvariantCulture)}ATR od {TrailStartR.ToString("0.##", CultureInfo.InvariantCulture)}R");
            if (PartialAtR > 0 && PartialFraction > 0) parts.Add($"częściowe {PartialFraction:P0}@{PartialAtR.ToString("0.##", CultureInfo.InvariantCulture)}R");
            if (MaxBarsHeld > 0) parts.Add($"max {MaxBarsHeld} świec");
            if (ExitOnTrendReversal) parts.Add("wyjście przy odwróceniu trendu");
            return string.Join(", ", parts);
        }
    }

    /// <summary>Dane wejściowe do oceny strategii w jednym momencie (zamknięcie świecy sygnałowej).</summary>
    public sealed class StrategyInput
    {
        public string Symbol { get; init; } = "";
        public InstrumentSpec Spec { get; init; } = new();
        public FeatureSet Signal { get; init; } = null!;
        public int SignalIndex { get; init; }
        public FeatureSet? Filter { get; init; }
        public int FilterIndex { get; init; } = -1;
        public RegimeAssessment SignalRegime { get; init; } = RegimeAssessment.Unknown("brak");
        public RegimeAssessment FilterRegime { get; init; } = RegimeAssessment.Unknown("brak");
        /// <summary>Czas zamknięcia świecy sygnałowej (UTC).</summary>
        public DateTime DecisionTimeUtc { get; init; }
    }

    /// <summary>Kandydat na transakcję wygenerowany przez strategię (jeszcze przed oceną i kontrolą ryzyka).</summary>
    public sealed class SignalCandidate
    {
        public string Symbol { get; init; } = "";
        public AssetClass AssetClass { get; init; }
        public StrategyId Strategy { get; init; }
        public string StrategyVariant { get; init; } = "";
        public TradeDirection Direction { get; init; }
        public Tf SignalTf { get; init; }
        public Tf? FilterTimeFrame { get; init; }
        /// <summary>Czas otwarcia świecy, na której zamknięciu powstał sygnał.</summary>
        public DateTime SignalBarTime { get; init; }
        public DateTime DecisionTimeUtc { get; init; }
        /// <summary>Cena zamknięcia świecy sygnałowej (cena odniesienia).</summary>
        public double ReferencePrice { get; init; }
        /// <summary>Odległość SL od ceny wejścia (w jednostkach ceny).</summary>
        public double StopDistance { get; init; }
        /// <summary>Odległość TP (w jednostkach ceny). Null = brak stałego TP.</summary>
        public double? TakeProfitDistance { get; init; }
        public double Atr { get; init; }
        /// <summary>Oczekiwany stosunek zysku do ryzyka (dla wyjść bez TP – założenie strategii).</summary>
        public double PlannedRewardRisk { get; init; }
        public ExitPlan Exit { get; init; } = new();
        public string Reason { get; init; } = "";
        public RegimeAssessment SignalRegime { get; init; } = RegimeAssessment.Unknown("brak");
        public RegimeAssessment FilterRegime { get; init; } = RegimeAssessment.Unknown("brak");
        /// <summary>Znormalizowane momentum w kierunku transakcji (zmiana ceny / (ATR*sqrt(n))).</summary>
        public double MomentumZ { get; init; }
        /// <summary>Maksymalny czas ważności sygnału po zamknięciu świecy.</summary>
        public TimeSpan Validity { get; init; } = TimeSpan.FromMinutes(15);
        /// <summary>Maksymalne dopuszczalne odejście ceny od ceny odniesienia (w ATR), po którym sygnał się dezaktualizuje.</summary>
        public double MaxDriftAtr { get; init; } = 0.5;

        public string SignalId => SignalIdGenerator.Create(Symbol, Strategy, Direction, SignalBarTime);

        /// <summary>Główny reżim (interwał filtrujący, a gdy go brak – sygnałowy) – używany do grupowania wyników.</summary>
        public string PrimaryRegimeName => FilterTimeFrame.HasValue ? FilterRegime.PolishName : SignalRegime.PolishName;
    }

    /// <summary>Wynik sprawdzenia, czy strategia każe zamknąć pozycję (utrata przewagi sygnału).</summary>
    public readonly struct StrategyExit
    {
        public StrategyExit(bool shouldExit, string reason)
        {
            ShouldExit = shouldExit;
            Reason = reason;
        }

        public bool ShouldExit { get; }
        public string Reason { get; }
        public static StrategyExit None => new(false, "");
    }

    /// <summary>Wspólny interfejs strategii.</summary>
    public interface IStrategy
    {
        StrategyId Id { get; }
        string Variant { get; }
        Tf SignalTf { get; }
        Tf? FilterTf { get; }
        int RequiredSignalBars { get; }
        int RequiredFilterBars { get; }
        bool AppliesTo(AssetClass assetClass);
        SignalCandidate? Evaluate(StrategyInput input);

        /// <summary>Reguła wyjścia specyficzna dla strategii, oceniana na zamknięciu świecy sygnałowej.</summary>
        StrategyExit CheckExit(StrategyInput input, TradeDirection direction, double entryPrice, int barsHeld);
    }

    /// <summary>Deterministyczny identyfikator sygnału – ten sam sygnał po restarcie daje ten sam ID (ochrona przed duplikacją).</summary>
    public static class SignalIdGenerator
    {
        public static string Create(string symbol, StrategyId strategy, TradeDirection direction, DateTime signalBarTime)
        {
            string raw = $"{symbol.ToUpperInvariant()}|{(int)strategy}|{(int)direction}|{signalBarTime.ToUniversalTime():yyyyMMddHHmm}";
            return Fnv1a32(raw).ToString("x8", CultureInfo.InvariantCulture);
        }

        public static uint Fnv1a32(string s)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (char ch in s)
                {
                    h ^= ch;
                    h *= 16777619;
                }
                return h;
            }
        }
    }

    /// <summary>Wspólna logika strategii.</summary>
    public abstract class StrategyBase : IStrategy
    {
        protected StrategyBase(Tf signalTf, Tf? filterTf, ExitPlan exit, string variant)
        {
            SignalTf = signalTf;
            FilterTf = filterTf;
            Exit = exit;
            Variant = variant;
        }

        public abstract StrategyId Id { get; }
        public string Variant { get; }
        public Tf SignalTf { get; }
        public Tf? FilterTf { get; }
        public ExitPlan Exit { get; }
        public virtual int RequiredSignalBars => 260;
        public virtual int RequiredFilterBars => 260;

        public abstract bool AppliesTo(AssetClass assetClass);
        public abstract SignalCandidate? Evaluate(StrategyInput input);

        public virtual StrategyExit CheckExit(StrategyInput input, TradeDirection direction, double entryPrice, int barsHeld) => StrategyExit.None;

        protected static bool BasicDataOk(StrategyInput input, int requiredBars)
        {
            if (input.Signal == null) return false;
            int i = input.SignalIndex;
            return i >= requiredBars - 1 && i < input.Signal.Count;
        }

        protected static double MomentumZ(FeatureSet f, int i, int n, double atr, TradeDirection d)
        {
            if (i - n < 0 || atr <= 0) return 0;
            double ch = f.Close[i] - f.Close[i - n];
            return (int)d * ch / (atr * Math.Sqrt(n));
        }

        protected SignalCandidate Build(StrategyInput input, TradeDirection d, double stopDistance, double? tpDistance, double atr,
            double plannedRr, string reason, double momentumZ, TimeSpan validity, double maxDriftAtr = 0.5)
        {
            int i = input.SignalIndex;
            return new SignalCandidate
            {
                Symbol = input.Symbol,
                AssetClass = input.Spec.AssetClass,
                Strategy = Id,
                StrategyVariant = Variant,
                Direction = d,
                SignalTf = SignalTf,
                FilterTimeFrame = FilterTf,
                SignalBarTime = input.Signal.Bars.Times[i],
                DecisionTimeUtc = input.DecisionTimeUtc,
                ReferencePrice = input.Signal.Close[i],
                StopDistance = stopDistance,
                TakeProfitDistance = tpDistance,
                Atr = atr,
                PlannedRewardRisk = plannedRr,
                Exit = Exit,
                Reason = reason,
                SignalRegime = input.SignalRegime,
                FilterRegime = input.FilterRegime,
                MomentumZ = momentumZ,
                Validity = validity,
                MaxDriftAtr = maxDriftAtr,
            };
        }

        protected double? TpFromPlan(double stopDistance) => Exit.TakeProfitR.HasValue ? Exit.TakeProfitR.Value * stopDistance : (double?)null;

        protected double PlannedRr(double assumedForTrailing) => Exit.TakeProfitR ?? assumedForTrailing;

        protected static TimeSpan DefaultValidity(Tf tf) => tf switch
        {
            Tf.D1 => TimeSpan.FromHours(3),
            Tf.H4 => TimeSpan.FromMinutes(45),
            _ => TimeSpan.FromMinutes(15),
        };
    }
}
