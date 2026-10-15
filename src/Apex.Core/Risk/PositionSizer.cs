using System;
using Apex.Core.Model;

namespace Apex.Core.Risk
{
    public sealed class SizingResult
    {
        public bool Ok { get; init; }
        public string Reason { get; init; } = "";
        /// <summary>Wolumen w jednostkach, znormalizowany W DÓŁ do kroku brokera.</summary>
        public double Units { get; init; }
        /// <summary>Rzeczywista maksymalna strata przy SL (z prowizją i zapasem na poślizg), w walucie konta.</summary>
        public double RiskAmount { get; init; }
        public double RiskBudget { get; init; }
        public double StopPips { get; init; }
        public double CommissionRoundTrip { get; init; }
        /// <summary>Koszt transakcji (spread + prowizja + poślizg) jako ułamek ryzyka do SL.</summary>
        public double CostToRisk { get; init; }
        /// <summary>Wartość nominalna pozycji w walucie konta.</summary>
        public double Notional { get; init; }

        public static SizingResult Reject(string reason, double budget = 0, double costToRisk = double.NaN) =>
            new() { Ok = false, Reason = reason, RiskBudget = budget, CostToRisk = costToRisk };
    }

    /// <summary>
    /// Wyznaczanie wielkości pozycji tak, aby strata na SL (łącznie z prowizją i zapasem na poślizg) nie przekroczyła budżetu.
    /// Zasady: wolumen zaokrąglany WYŁĄCZNIE w dół; jeśli minimalny wolumen brokera przekracza budżet – transakcja odrzucona
    /// (nigdy nie zwiększamy ryzyka, aby "zmieścić się" w minimalnym wolumenie).
    /// </summary>
    public static class PositionSizer
    {
        /// <param name="spec">Specyfikacja instrumentu.</param>
        /// <param name="riskBudget">Budżet ryzyka w walucie konta.</param>
        /// <param name="stopDistance">Odległość SL od ceny wejścia (cena).</param>
        /// <param name="entryPrice">Przewidywana cena wejścia.</param>
        /// <param name="spread">Bieżący spread (cena).</param>
        /// <param name="slippageAllowance">Zakładany niekorzystny poślizg na wyjściu przez SL (cena).</param>
        public static SizingResult Size(InstrumentSpec spec, double riskBudget, double stopDistance, double entryPrice, double spread, double slippageAllowance)
        {
            var specError = spec.Validate();
            if (specError != null) return SizingResult.Reject("Specyfikacja: " + specError, riskBudget);
            if (!(riskBudget > 0) || double.IsInfinity(riskBudget)) return SizingResult.Reject("nieprawidłowy budżet ryzyka", riskBudget);
            if (!(stopDistance > 0) || double.IsInfinity(stopDistance)) return SizingResult.Reject("nieprawidłowa odległość SL", riskBudget);
            if (!(entryPrice > 0)) return SizingResult.Reject("nieprawidłowa cena wejścia", riskBudget);
            if (double.IsNaN(spread) || spread < 0) return SizingResult.Reject("nieprawidłowy spread", riskBudget);
            if (double.IsNaN(slippageAllowance) || slippageAllowance < 0) slippageAllowance = 0;

            double stopPips = spec.PriceToPips(stopDistance);
            if (spec.MinStopDistancePips > 0 && stopPips < spec.MinStopDistancePips)
                return SizingResult.Reject($"SL {stopPips:F1} pips poniżej minimum brokera {spec.MinStopDistancePips:F1}", riskBudget);

            double vpp = spec.ValuePerPricePerUnit;
            double commPerUnitRt = 2.0 * spec.CommissionOneSide(1.0, entryPrice);
            if (double.IsNaN(commPerUnitRt) || commPerUnitRt < 0) return SizingResult.Reject("nie można zweryfikować prowizji", riskBudget);

            double lossPerUnit = (stopDistance + slippageAllowance) * vpp + commPerUnitRt;
            if (!(lossPerUnit > 0)) return SizingResult.Reject("nie można policzyć straty na jednostkę", riskBudget);

            // Koszt: spread + prowizja (w cenie) + poślizg na wejściu i wyjściu, względem odległości SL.
            double commInPrice = vpp > 0 ? commPerUnitRt / vpp : double.NaN;
            double costToRisk = (spread + commInPrice + 2 * slippageAllowance) / stopDistance;

            double rawUnits = riskBudget / lossPerUnit;
            double units = spec.NormalizeVolumeDown(rawUnits);
            if (units < spec.VolumeMin - 1e-9)
                return SizingResult.Reject($"minimalny wolumen {spec.VolumeMin} przekracza dopuszczalne ryzyko (wyliczono {rawUnits:F2})", riskBudget, costToRisk);

            double commission = 2.0 * spec.CommissionOneSide(units, entryPrice);
            double risk = units * (stopDistance + slippageAllowance) * vpp + commission;
            if (risk > riskBudget * 1.000001)
                return SizingResult.Reject($"ryzyko po zaokrągleniu {risk:F2} > budżet {riskBudget:F2}", riskBudget, costToRisk);

            return new SizingResult
            {
                Ok = true,
                Units = units,
                RiskAmount = risk,
                RiskBudget = riskBudget,
                StopPips = stopPips,
                CommissionRoundTrip = commission,
                CostToRisk = costToRisk,
                Notional = units * entryPrice * vpp,
            };
        }

        /// <summary>Wartość nominalna pozycji w walucie konta (jednostki × cena × wartość zmiany ceny o 1.0 na jednostkę).</summary>
        public static double Notional(InstrumentSpec spec, double units, double price) => units * price * spec.ValuePerPricePerUnit;
    }
}
