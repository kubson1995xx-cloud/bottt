using System;
using System.Collections.Generic;
using Apex.Core.Model;
using Apex.Core.Strategies;

namespace Apex.Core.Management
{
    /// <summary>
    /// Trwały plan transakcji – zapisywany przy otwarciu, aby po restarcie bot kontynuował zarządzanie pozycją
    /// z tymi samymi regułami (R, plan wyjścia, stan break-even/częściowego zamknięcia).
    /// </summary>
    public sealed class TradePlan
    {
        public long PositionId { get; set; }
        public string SignalId { get; set; } = "";
        public string Symbol { get; set; } = "";
        public StrategyId Strategy { get; set; }
        public string Variant { get; set; } = "";
        public TradeDirection Direction { get; set; }
        public Tf SignalTf { get; set; }
        public double EntryPrice { get; set; }
        public double InitialStop { get; set; }
        /// <summary>R – początkowa odległość SL w jednostkach ceny.</summary>
        public double InitialRisk { get; set; }
        public double InitialVolume { get; set; }
        public double RiskAmount { get; set; }
        public DateTime EntryTimeUtc { get; set; }
        public double EntryAtr { get; set; }
        public double Score { get; set; }
        public string EntryReason { get; set; } = "";
        public string Regime { get; set; } = "";
        public bool BreakEvenDone { get; set; }
        public bool PartialDone { get; set; }
        /// <summary>Najkorzystniejsza cena od wejścia (maksimum dla long, minimum dla short) – z ekstremów zamkniętych świec.</summary>
        public double BestPrice { get; set; }
        public int BarsHeld { get; set; }
        public DateTime LastManagedBarTime { get; set; }
        public ExitPlan Exit { get; set; } = new();
        /// <summary>Plan odtworzony bez pełnych danych (np. pozycja sprzed aktualizacji). Zarządzanie konserwatywne.</summary>
        public bool Reconstructed { get; set; }

        public double MfeR => InitialRisk > 0 ? (int)Direction * (BestPrice - EntryPrice) / InitialRisk : 0;
    }

    public enum ManagementActionKind
    {
        ModifyStop,
        PartialClose,
        Close,
    }

    public sealed class ManagementAction
    {
        public ManagementActionKind Kind { get; init; }
        public double? NewStop { get; init; }
        public double CloseUnits { get; init; }
        public string Reason { get; init; } = "";

        public override string ToString() => Kind switch
        {
            ManagementActionKind.ModifyStop => $"SL→{NewStop} ({Reason})",
            ManagementActionKind.PartialClose => $"częściowe zamknięcie {CloseUnits} ({Reason})",
            _ => $"zamknięcie ({Reason})",
        };
    }

    /// <summary>Dane dla jednej oceny zarządzania pozycją (na zamknięciu świecy interwału sygnałowego).</summary>
    public sealed class ManagementInput
    {
        public TradePlan Plan { get; init; } = new();
        public PositionInfo Position { get; init; } = new();
        public InstrumentSpec Spec { get; init; } = new();
        public Quote Quote { get; init; }
        /// <summary>Zamknięcie ostatniej świecy sygnałowej, maksimum i minimum tej świecy.</summary>
        public double BarClose { get; init; }
        public double BarHigh { get; init; }
        public double BarLow { get; init; }
        public DateTime BarTime { get; init; }
        public double CurrentAtr { get; init; }
        /// <summary>Wynik reguły wyjścia strategii (utrata przewagi).</summary>
        public StrategyExit StrategyExit { get; init; }
        /// <summary>Kierunek trendu na interwale filtrującym (+1/-1/0), NaN-bezpieczny.</summary>
        public int FilterTrendDirection { get; init; }
        public bool FilterRegimeStrong { get; init; }
    }

    /// <summary>
    /// Reguły zarządzania pozycją – czysta funkcja: stan + dane rynkowe → lista akcji.
    /// Działa niezależnie od generowania sygnałów. SL wolno wyłącznie zacieśniać (nigdy nie oddalamy SL).
    /// </summary>
    public static class PositionManagerRules
    {
        public static List<ManagementAction> Evaluate(ManagementInput m)
        {
            var actions = new List<ManagementAction>();
            var p = m.Plan;
            var pos = m.Position;
            var exit = p.Exit ?? new ExitPlan();
            int s = (int)p.Direction;

            if (p.InitialRisk <= 0 || double.IsNaN(p.InitialRisk))
                return actions;

            // Aktualizacja stanu na podstawie nowej zamkniętej świecy (tylko świece po wejściu).
            if (m.BarTime > p.LastManagedBarTime)
            {
                p.LastManagedBarTime = m.BarTime;
                p.BarsHeld++;
                double favorable = p.Direction == TradeDirection.Long ? m.BarHigh : m.BarLow;
                if (p.BestPrice <= 0 || s * (favorable - p.BestPrice) > 0) p.BestPrice = favorable;
            }

            double closeR = s * (m.BarClose - p.EntryPrice) / p.InitialRisk;

            // 1) Reguła strategii (utrata przewagi sygnału).
            if (m.StrategyExit.ShouldExit)
            {
                actions.Add(new ManagementAction { Kind = ManagementActionKind.Close, Reason = m.StrategyExit.Reason });
                return actions;
            }

            // 2) Limit czasu.
            if (exit.MaxBarsHeld > 0 && p.BarsHeld >= exit.MaxBarsHeld)
            {
                actions.Add(new ManagementAction { Kind = ManagementActionKind.Close, Reason = $"limit czasu ({p.BarsHeld} świec)" });
                return actions;
            }

            // 3) Odwrócenie trendu na interwale wyższym.
            if (exit.ExitOnTrendReversal && m.FilterTrendDirection == -s && m.FilterRegimeStrong)
            {
                actions.Add(new ManagementAction { Kind = ManagementActionKind.Close, Reason = "odwrócenie trendu na interwale wyższym" });
                return actions;
            }

            // 4) Częściowe zamknięcie.
            if (!p.PartialDone && exit.PartialAtR > 0 && exit.PartialFraction > 0 && closeR >= exit.PartialAtR)
            {
                double target = m.Spec.NormalizeVolumeDown(pos.VolumeUnits * exit.PartialFraction);
                double remaining = pos.VolumeUnits - target;
                if (target >= m.Spec.VolumeMin && remaining >= m.Spec.VolumeMin)
                {
                    actions.Add(new ManagementAction { Kind = ManagementActionKind.PartialClose, CloseUnits = target, Reason = $"częściowa realizacja przy {closeR:F2}R" });
                }
                p.PartialDone = true; // także gdy wolumen nie pozwala – nie próbujemy w kółko
            }

            // 5) Nowy SL: break-even i trailing – wybieramy najciaśniejszy, tylko jeśli poprawia obecny SL.
            double? candidate = null;
            string reason = "";
            double mfeR = p.MfeR;
            if (exit.BreakEvenAtR > 0 && mfeR >= exit.BreakEvenAtR)
            {
                double be = p.EntryPrice + s * exit.BreakEvenOffsetR * p.InitialRisk;
                candidate = be;
                reason = $"break-even (MFE {mfeR:F2}R)";
                p.BreakEvenDone = true;
            }
            if (exit.TrailAtrMultiple > 0 && mfeR >= exit.TrailStartR && m.CurrentAtr > 0)
            {
                double trail = p.BestPrice - s * exit.TrailAtrMultiple * m.CurrentAtr;
                if (candidate == null || s * (trail - candidate.Value) > 0)
                {
                    candidate = trail;
                    reason = $"trailing {exit.TrailAtrMultiple}xATR";
                }
            }

            if (candidate.HasValue)
            {
                double newStop = m.Spec.RoundPrice(candidate.Value);
                double current = pos.StopLoss ?? p.InitialStop;
                double minStep = Math.Max(m.Spec.TickSize, 0.05 * Math.Max(m.CurrentAtr, p.EntryAtr));
                bool improves = s * (newStop - current) > minStep;
                // SL musi być po właściwej stronie bieżącej ceny wyjścia z zachowaniem minimalnej odległości.
                double exitPrice = m.Quote.IsValid ? m.Quote.ExitPrice(p.Direction) : m.BarClose;
                double minDist = Math.Max(m.Spec.MinStopDistancePips * m.Spec.PipSize, m.Spec.TickSize * 2);
                bool valid = s * (exitPrice - newStop) > minDist;
                if (improves && valid)
                    actions.Add(new ManagementAction { Kind = ManagementActionKind.ModifyStop, NewStop = newStop, Reason = reason });
            }

            return actions;
        }

        /// <summary>Awaryjny SL dla pozycji bota bez zabezpieczenia: z planu lub 2×ATR od bieżącej ceny.</summary>
        public static double EmergencyStop(TradePlan? plan, PositionInfo pos, InstrumentSpec spec, Quote q, double atr)
        {
            int s = (int)pos.Direction;
            double exitPrice = q.IsValid ? q.ExitPrice(pos.Direction) : pos.CurrentPrice;
            double candidate = plan != null && plan.InitialStop > 0 ? plan.InitialStop : exitPrice - s * 2.0 * Math.Max(atr, spec.PipSize * 10);
            double minDist = Math.Max(spec.MinStopDistancePips * spec.PipSize, spec.TickSize * 2);
            // Jeśli SL z planu jest już po złej stronie ceny, ustawiamy minimalnie odsunięty od ceny bieżącej.
            if (s * (exitPrice - candidate) <= minDist) candidate = exitPrice - s * Math.Max(minDist * 2, 0.5 * Math.Max(atr, spec.PipSize * 10));
            return spec.RoundPrice(candidate);
        }
    }
}
