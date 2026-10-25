using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Apex.Core.Model;

namespace Apex.Core.Risk
{
    public sealed class RiskSettings
    {
        public double RiskPerTradePct { get; set; } = 0.5;
        public double MaxDailyLossPct { get; set; } = 1.5;
        public double MaxDrawdownPct { get; set; } = 5.0;
        public int MaxOpenPositions { get; set; } = 3;
        public double MaxEffectiveLeverage { get; set; } = 5.0;
        /// <summary>Maksymalna korelacja (ze znakiem kierunku) nowej pozycji z otwartą pozycją.</summary>
        public double MaxCorrelation { get; set; } = 0.75;
        public int MaxPositionsPerAssetClass { get; set; } = 2;
        /// <summary>Maksymalna liczba pozycji z tą samą ekspozycją (kierunkiem) na tę samą walutę/aktywo.</summary>
        public int MaxSameCurrencyExposure { get; set; } = 2;
        /// <summary>Margin nowej pozycji nie może przekroczyć tej części wolnego marginu.</summary>
        public double MaxMarginUseOfFree { get; set; } = 0.5;
        /// <summary>Minimalny poziom marginu (%) po otwarciu pozycji.</summary>
        public double MinMarginLevelPct { get; set; } = 300;
        /// <summary>Uwzględnij najgorszy scenariusz (wszystkie otwarte pozycje + nowa trafiają SL) w limitach dziennym i DD.</summary>
        public bool ProjectWorstCase { get; set; } = true;
        public bool CloseAllOnMaxDrawdown { get; set; } = true;
        public bool CloseAllOnDailyLimit { get; set; } = false;
        /// <summary>Maksymalny koszt transakcji jako ułamek ryzyka (SL).</summary>
        public double MaxCostToRisk { get; set; } = 0.20;
        /// <summary>Maksymalny spread jako ułamek ATR interwału sygnałowego.</summary>
        public double MaxSpreadToAtr { get; set; } = 0.25;
        /// <summary>Spread większy niż X × mediana obserwowanego spreadu = anomalia, brak wejścia.</summary>
        public double SpreadSpikeMultiple { get; set; } = 2.5;
        /// <summary>Zapas na poślizg na SL w ATR (wliczany do ryzyka i kosztu).</summary>
        public double SlippageAllowanceAtr { get; set; } = 0.05;
        /// <summary>Godzina UTC rozpoczęcia nowego dnia handlowego dla limitu dziennego.</summary>
        public int DayResetHourUtc { get; set; } = 0;

        public RiskSettings Clone() => (RiskSettings)MemberwiseClone();

        /// <summary>Walidacja zakresów – chroni przed absurdalnymi ustawieniami (np. ryzyko 50%).</summary>
        public string? Validate()
        {
            if (new[] { RiskPerTradePct, MaxDailyLossPct, MaxDrawdownPct, MaxEffectiveLeverage, MaxCorrelation,
                MaxMarginUseOfFree, MinMarginLevelPct, MaxCostToRisk, MaxSpreadToAtr, SpreadSpikeMultiple, SlippageAllowanceAtr }.Any(x => !double.IsFinite(x)))
                return "Parametry ryzyka muszą być skończonymi liczbami.";
            if (RiskPerTradePct <= 0 || RiskPerTradePct > 2.0) return "Ryzyko na transakcję musi być w zakresie (0; 2]%.";
            if (MaxDailyLossPct <= 0 || MaxDailyLossPct > 10) return "Maksymalna dzienna strata musi być w zakresie (0; 10]%.";
            if (MaxDrawdownPct <= 0 || MaxDrawdownPct > 30) return "Maksymalne obsunięcie musi być w zakresie (0; 30]%.";
            if (MaxOpenPositions < 1 || MaxOpenPositions > 20) return "Maksymalna liczba pozycji musi być w zakresie 1–20.";
            if (MaxEffectiveLeverage <= 0 || MaxEffectiveLeverage > 30) return "Maksymalna dźwignia efektywna musi być w zakresie (0; 30].";
            if (RiskPerTradePct > MaxDailyLossPct) return "Ryzyko na transakcję nie może przekraczać dziennego limitu straty.";
            if (MaxCorrelation <= 0 || MaxCorrelation > 1) return "Maksymalna korelacja musi być w zakresie (0; 1].";
            if (DayResetHourUtc < 0 || DayResetHourUtc > 23) return "Godzina resetu dnia musi być w zakresie 0–23.";
            return null;
        }
    }

    /// <summary>Trwały stan modułu ryzyka (zapisywany, aby limity działały poprawnie po restarcie).</summary>
    public sealed class RiskState
    {
        public string DayKey { get; set; } = "";
        public double DayStartEquity { get; set; }
        public double PeakEquity { get; set; }
        public bool DailyLimitHit { get; set; }
        public bool MaxDrawdownHit { get; set; }
        public double InitialEquity { get; set; }
        public DateTime StartedUtc { get; set; }
    }

    /// <summary>Ryzyko otwartej pozycji użyte w kontrolach portfela.</summary>
    public sealed class OpenRisk
    {
        public string Symbol { get; init; } = "";
        public AssetClass AssetClass { get; init; }
        public TradeDirection Direction { get; init; }
        /// <summary>Strata od bieżącej ceny do SL (0 gdy SL zabezpiecza zysk), w walucie konta.</summary>
        public double WorstCaseLoss { get; init; }
        public double Notional { get; init; }
        public string? BaseCurrency { get; init; }
        public string? QuoteCurrency { get; init; }
        public bool HasStopLoss { get; init; }
    }

    public sealed class TradeProposal
    {
        public string Symbol { get; init; } = "";
        public AssetClass AssetClass { get; init; }
        public TradeDirection Direction { get; init; }
        public double RiskAmount { get; init; }
        public double Notional { get; init; }
        public double EstimatedMargin { get; init; }
        public string? BaseCurrency { get; init; }
        public string? QuoteCurrency { get; init; }
        /// <summary>Maksymalna korelacja ze znakiem kierunku względem otwartych pozycji (NaN = brak danych/brak pozycji).</summary>
        public double MaxSignedCorrelation { get; init; } = double.NaN;
        public string? MaxCorrelationWith { get; init; }
    }

    public sealed class RiskCheckResult
    {
        public bool Allowed => Failures.Count == 0;
        public List<string> Failures { get; } = new();
        public override string ToString() => Allowed ? "OK" : string.Join("; ", Failures);
    }

    /// <summary>
    /// Zarządzanie ryzykiem portfela: limity dzienne, maksymalne obsunięcie, liczba pozycji, dźwignia efektywna,
    /// margin, koncentracja walutowa i korelacja. Wszystkie kontrole są deterministyczne i testowalne.
    /// </summary>
    public sealed class RiskManager
    {
        private readonly RiskSettings _settings;

        public RiskManager(RiskSettings settings, RiskState? state = null)
        {
            _settings = settings;
            State = state ?? new RiskState();
        }

        public RiskSettings Settings => _settings;
        public RiskState State { get; }

        public double DailyLossPct { get; private set; }
        public double DrawdownPct { get; private set; }
        public double LastEquity { get; private set; }

        public static string DayKeyFor(DateTime utc, int resetHourUtc) =>
            utc.AddHours(-resetHourUtc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        /// <summary>
        /// Aktualizacja stanu na podstawie bieżącego kapitału. Zwraca listę zdarzeń (np. przekroczenie limitu) do zalogowania.
        /// </summary>
        public List<string> Update(AccountSnapshot acc, DateTime utc)
        {
            var events = new List<string>();
            if (!acc.IsValid) return events;
            double equity = acc.Equity;
            LastEquity = equity;

            if (State.InitialEquity <= 0)
            {
                State.InitialEquity = equity;
                State.StartedUtc = utc;
            }

            string key = DayKeyFor(utc, _settings.DayResetHourUtc);
            if (State.DayKey != key)
            {
                if (!string.IsNullOrEmpty(State.DayKey) && State.DailyLimitHit)
                    events.Add("Nowy dzień handlowy – zdjęto blokadę dziennego limitu straty.");
                State.DayKey = key;
                State.DayStartEquity = equity;
                State.DailyLimitHit = false;
            }
            if (State.DayStartEquity <= 0) State.DayStartEquity = equity;
            if (equity > State.PeakEquity) State.PeakEquity = equity;

            DailyLossPct = Math.Max(0, (State.DayStartEquity - equity) / State.DayStartEquity * 100.0);
            DrawdownPct = State.PeakEquity > 0 ? Math.Max(0, (State.PeakEquity - equity) / State.PeakEquity * 100.0) : 0;

            if (!State.DailyLimitHit && DailyLossPct >= _settings.MaxDailyLossPct)
            {
                State.DailyLimitHit = true;
                events.Add($"PRZEKROCZONO DZIENNY LIMIT STRATY: {DailyLossPct:F2}% ≥ {_settings.MaxDailyLossPct:F2}% – nowe transakcje zablokowane do końca dnia.");
            }
            if (!State.MaxDrawdownHit && DrawdownPct >= _settings.MaxDrawdownPct)
            {
                State.MaxDrawdownHit = true;
                events.Add($"PRZEKROCZONO MAKSYMALNE OBSUNIĘCIE: {DrawdownPct:F2}% ≥ {_settings.MaxDrawdownPct:F2}% – handel zatrzymany, wymagany ręczny reset.");
            }
            return events;
        }

        /// <summary>Ręczny reset blokady maksymalnego obsunięcia (panel) – nowy szczyt = bieżący kapitał.</summary>
        public void ResetDrawdownLatch(double equity)
        {
            State.MaxDrawdownHit = false;
            State.PeakEquity = equity;
        }

        public double RiskBudget(AccountSnapshot acc) =>
            Math.Min(acc.Balance, acc.Equity) * _settings.RiskPerTradePct / 100.0;

        public bool NewEntriesBlocked(out string reason)
        {
            if (State.MaxDrawdownHit) { reason = "przekroczone maksymalne obsunięcie"; return true; }
            if (State.DailyLimitHit) { reason = "przekroczony dzienny limit straty"; return true; }
            reason = "";
            return false;
        }

        public RiskCheckResult EvaluateNewTrade(TradeProposal p, AccountSnapshot acc, IReadOnlyList<OpenRisk> open)
        {
            var r = new RiskCheckResult();
            if (!acc.IsValid) { r.Failures.Add("nieprawidłowe dane konta"); return r; }
            if (NewEntriesBlocked(out var blocked)) r.Failures.Add(blocked);
            if (!(p.RiskAmount > 0) || !double.IsFinite(p.RiskAmount) || !(p.Notional > 0) || !double.IsFinite(p.Notional))
                r.Failures.Add("nieprawidłowe ryzyko lub wartość nominalna transakcji");

            if (open.Count >= _settings.MaxOpenPositions)
                r.Failures.Add($"osiągnięto limit pozycji ({open.Count}/{_settings.MaxOpenPositions})");

            if (open.Any(o => string.Equals(o.Symbol, p.Symbol, StringComparison.OrdinalIgnoreCase)))
                r.Failures.Add("na tym instrumencie jest już pozycja bota");

            int sameClass = open.Count(o => o.AssetClass == p.AssetClass);
            if (sameClass >= _settings.MaxPositionsPerAssetClass)
                r.Failures.Add($"limit pozycji w klasie {p.AssetClass.PolishName()} ({sameClass}/{_settings.MaxPositionsPerAssetClass})");

            if (open.Any(o => !o.HasStopLoss))
                r.Failures.Add("istnieje pozycja bota bez SL – najpierw zabezpieczenie");

            double budget = RiskBudget(acc);
            if (p.RiskAmount > budget * 1.000001)
                r.Failures.Add($"ryzyko transakcji {p.RiskAmount:F2} > budżet {budget:F2}");

            double openWorst = open.Sum(o => Math.Max(0, o.WorstCaseLoss));
            double dayStart = State.DayStartEquity > 0 ? State.DayStartEquity : acc.Equity;
            double currentDayLoss = Math.Max(0, dayStart - acc.Equity);
            double dailyLimit = dayStart * _settings.MaxDailyLossPct / 100.0;
            if (_settings.ProjectWorstCase)
            {
                double projected = currentDayLoss + openWorst + p.RiskAmount;
                if (projected > dailyLimit * 1.000001)
                    r.Failures.Add($"najgorszy scenariusz dnia {projected:F2} > limit dzienny {dailyLimit:F2}");
                double peak = Math.Max(State.PeakEquity, acc.Equity);
                double ddLimit = peak * _settings.MaxDrawdownPct / 100.0;
                double projectedDd = Math.Max(0, peak - acc.Equity) + openWorst + p.RiskAmount;
                if (projectedDd > ddLimit * 1.000001)
                    r.Failures.Add($"najgorszy scenariusz obsunięcia {projectedDd:F2} > limit {ddLimit:F2}");
            }

            double totalNotional = open.Sum(o => Math.Abs(o.Notional)) + Math.Abs(p.Notional);
            double leverage = totalNotional / acc.Equity;
            if (leverage > _settings.MaxEffectiveLeverage)
                r.Failures.Add($"dźwignia efektywna {leverage:F2}x > {_settings.MaxEffectiveLeverage:F2}x");

            if (!(p.EstimatedMargin >= 0) || double.IsNaN(p.EstimatedMargin))
                r.Failures.Add("nie można oszacować wymaganego depozytu");
            else
            {
                if (p.EstimatedMargin > acc.FreeMargin * _settings.MaxMarginUseOfFree)
                    r.Failures.Add($"depozyt {p.EstimatedMargin:F2} > {_settings.MaxMarginUseOfFree:P0} wolnego marginu {acc.FreeMargin:F2}");
                double newMargin = acc.Margin + p.EstimatedMargin;
                if (newMargin > 0)
                {
                    double level = acc.Equity / newMargin * 100.0;
                    if (level < _settings.MinMarginLevelPct)
                        r.Failures.Add($"poziom marginu po otwarciu {level:F0}% < {_settings.MinMarginLevelPct:F0}%");
                }
            }

            foreach (var ccy in Exposures(p.BaseCurrency, p.QuoteCurrency, p.Direction))
            {
                int same = open.Count(o => Exposures(o.BaseCurrency, o.QuoteCurrency, o.Direction).Contains(ccy));
                if (same >= _settings.MaxSameCurrencyExposure)
                    r.Failures.Add($"koncentracja ekspozycji {ccy} ({same} pozycji)");
            }

            if (!double.IsNaN(p.MaxSignedCorrelation) && p.MaxSignedCorrelation > _settings.MaxCorrelation)
                r.Failures.Add($"korelacja {p.MaxSignedCorrelation:F2} z {p.MaxCorrelationWith} > {_settings.MaxCorrelation:F2}");

            return r;
        }

        /// <summary>Ekspozycje walutowe ze znakiem, np. long EURUSD = {"+EUR", "-USD"}.</summary>
        public static HashSet<string> Exposures(string? baseCcy, string? quoteCcy, TradeDirection d)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string plus = d == TradeDirection.Long ? "+" : "-";
            string minus = d == TradeDirection.Long ? "-" : "+";
            if (!string.IsNullOrWhiteSpace(baseCcy)) set.Add(plus + baseCcy!.ToUpperInvariant());
            if (!string.IsNullOrWhiteSpace(quoteCcy)) set.Add(minus + quoteCcy!.ToUpperInvariant());
            return set;
        }

        /// <summary>Najgorsza strata od bieżącej ceny do SL dla pozycji (0 gdy SL zabezpiecza zysk lub brak danych o SL → pełna wartość ostrzegawcza).</summary>
        public static double WorstCaseLoss(InstrumentSpec spec, PositionInfo pos, double currentExitPrice)
        {
            if (!pos.StopLoss.HasValue) return double.PositiveInfinity;
            int s = (int)pos.Direction;
            double dist = s * (currentExitPrice - pos.StopLoss.Value);
            return Math.Max(0, dist) * pos.VolumeUnits * spec.ValuePerPricePerUnit;
        }
    }
}
