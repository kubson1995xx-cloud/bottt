using System;
using System.Collections.Generic;
using System.Linq;
using Apex.AiLab.Lab;

namespace Apex.AiLab.Scheduling
{
    /// <summary>Kontrola zużycia: liczba uruchomień agenta, tokeny, koszty API, limity subskrypcji zgłoszone przez narzędzia.</summary>
    public sealed class Budget
    {
        private readonly LabStore _store;
        private readonly Func<LabConfig> _cfg;

        public Budget(LabStore store, Func<LabConfig> cfg)
        {
            _store = store;
            _cfg = cfg;
        }

        public static DateTime DayStartUtc(DateTime nowUtc)
        {
            var local = nowUtc.ToLocalTime().Date;
            return DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();
        }

        public static DateTime WeekStartUtc(DateTime nowUtc)
        {
            var local = nowUtc.ToLocalTime().Date;
            int diff = ((int)local.DayOfWeek + 6) % 7; // poniedziałek
            return DateTime.SpecifyKind(local.AddDays(-diff), DateTimeKind.Local).ToUniversalTime();
        }

        public static DateTime MonthStartUtc(DateTime nowUtc)
        {
            var local = nowUtc.ToLocalTime();
            return DateTime.SpecifyKind(new DateTime(local.Year, local.Month, 1), DateTimeKind.Local).ToUniversalTime();
        }

        public sealed class Summary
        {
            public int RunsToday { get; set; }
            public int RunsWeek { get; set; }
            public long TokensToday { get; set; }
            public long TokensWeek { get; set; }
            public long TokensMonth { get; set; }
            public double CostMonthUsd { get; set; }
            public Dictionary<string, long> TokensByProviderMonth { get; set; } = new();
            public Dictionary<string, int> RunsByProviderMonth { get; set; } = new();
            public List<(DateTime day, long tokens, int runs)> Daily { get; set; } = new();
        }

        public Summary Compute(DateTime nowUtc)
        {
            var month = MonthStartUtc(nowUtc);
            var from = month < nowUtc.AddDays(-30) ? month : nowUtc.AddDays(-30);
            var recs = _store.Usage(from);
            var day = DayStartUtc(nowUtc);
            var week = WeekStartUtc(nowUtc);
            var s = new Summary
            {
                RunsToday = recs.Count(r => r.Kind == "agent" && r.TimeUtc >= day),
                RunsWeek = recs.Count(r => r.Kind == "agent" && r.TimeUtc >= week),
                TokensToday = recs.Where(r => r.TimeUtc >= day).Sum(CountedTokens),
                TokensWeek = recs.Where(r => r.TimeUtc >= week).Sum(CountedTokens),
                TokensMonth = recs.Where(r => r.TimeUtc >= month).Sum(CountedTokens),
                CostMonthUsd = recs.Where(r => r.TimeUtc >= month && r.CostUsd.HasValue && IsApiBilled(r)).Sum(r => r.CostUsd!.Value),
            };
            foreach (var g in recs.Where(r => r.TimeUtc >= month).GroupBy(r => r.Provider))
            {
                s.TokensByProviderMonth[g.Key.ToString()] = g.Sum(CountedTokens);
                s.RunsByProviderMonth[g.Key.ToString()] = g.Count(r => r.Kind == "agent");
            }
            for (int i = 13; i >= 0; i--)
            {
                var d0 = DayStartUtc(nowUtc.AddDays(-i));
                var d1 = d0.AddDays(1);
                var dayRecs = recs.Where(r => r.TimeUtc >= d0 && r.TimeUtc < d1).ToList();
                s.Daily.Add((d0, dayRecs.Sum(CountedTokens), dayRecs.Count(r => r.Kind == "agent")));
            }
            return s;
        }

        /// <summary>
        /// Tokeny liczone do limitów: wejście bez części odczytanej z pamięci podręcznej modelu + wyjście. Agent CLI wielokrotnie
        /// czyta ten sam kontekst – pełne liczenie tokenów z pamięci podręcznej wyczerpywałoby dzienny limit jednym uruchomieniem.
        /// </summary>
        public static long CountedTokens(UsageRecord r) => Math.Max(0, r.InputTokens - r.CachedInputTokens) + r.OutputTokens;

        /// <summary>Czy koszt jest realnym rozliczeniem API (a nie szacunkiem w ramach subskrypcji) – wg rodzaju dostawcy.</summary>
        public static bool IsApiBilled(ProviderId p) => p is ProviderId.OpenAiApi or ProviderId.AnthropicApi;

        /// <summary>Rozliczenie API zapisane przy uruchomieniu (Codex/Claude Code z kluczem API też); starsze zapisy – wg rodzaju dostawcy.</summary>
        public static bool IsApiBilled(UsageRecord r) => r.ApiBilled ?? IsApiBilled(r.Provider);

        /// <summary>
        /// Sprawdza, czy można uruchomić agenta danego dostawcy. Zwraca null albo powód blokady (PL).
        /// apiBilled: bieżący sposób rozliczenia dostawcy (np. Codex zalogowany kluczem API) – null = wg rodzaju dostawcy.
        /// </summary>
        public string? CanRun(ProviderId provider, DateTime nowUtc, bool manual, bool? apiBilled = null)
        {
            var cfg = _cfg();
            var b = cfg.Budget;
            string? limit = ProviderLimit(provider, nowUtc);
            if (limit != null) return limit;
            var s = Compute(nowUtc);
            if (b.MaxAgentRunsPerDay >= 0 && s.RunsToday >= b.MaxAgentRunsPerDay) return $"dzienny limit uruchomień agenta ({s.RunsToday}/{b.MaxAgentRunsPerDay})";
            if (b.MaxAgentRunsPerWeek >= 0 && s.RunsWeek >= b.MaxAgentRunsPerWeek) return $"tygodniowy limit uruchomień agenta ({s.RunsWeek}/{b.MaxAgentRunsPerWeek})";
            string? spend = Spend(s, apiBilled ?? IsApiBilled(provider));
            if (spend != null) return spend;
            if (!manual)
            {
                var last = _store.LastAgentRunUtc;
                if (last.HasValue && (nowUtc - last.Value).TotalHours < cfg.Schedule.MinHoursBetweenAgentRuns)
                    return $"minimalny odstęp między automatycznymi uruchomieniami ({cfg.Schedule.MinHoursBetweenAgentRuns} h)";
            }
            return null;
        }

        /// <summary>
        /// Kontynuacja TEGO SAMEGO zadania (niezależny przegląd drugim modelem, runda poprawek): liczniki uruchomień agenta
        /// i minimalny odstęp dotyczą rozpoczynania zadań, więc ich tu nie stosujemy. Obowiązują: limit zgłoszony przez dostawcę,
        /// dzienny limit tokenów i miesięczny limit kosztów (przy rozliczeniu API). Rundy poprawek są ograniczone osobno
        /// ustawieniem MaxRepairRounds i zapisywane jako "repair" (nie zajmują dziennych uruchomień agenta).
        /// </summary>
        public string? CanContinueJob(ProviderId provider, DateTime nowUtc, bool? apiBilled = null) =>
            ProviderLimit(provider, nowUtc) ?? Spend(Compute(nowUtc), apiBilled ?? IsApiBilled(provider));

        /// <summary>Czy można wykonać niezależny przegląd – patrz <see cref="CanContinueJob"/>.</summary>
        public string? CanReview(ProviderId provider, DateTime nowUtc, bool? apiBilled = null) => CanContinueJob(provider, nowUtc, apiBilled);

        /// <summary>Limit zgłoszony przez dostawcę (subskrypcja / API) – do czasu odnowienia.</summary>
        private string? ProviderLimit(ProviderId provider, DateTime nowUtc)
        {
            var lim = _store.Limit(provider);
            if (lim?.LimitedUntilUtc != null && lim.LimitedUntilUtc > nowUtc && _cfg().Budget.PauseOnSubscriptionLimit)
                return $"limit dostawcy {provider} wyczerpany do {lim.LimitedUntilUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm} ({lim.Message})";
            return null;
        }

        /// <summary>Dzienny limit tokenów i miesięczny limit kosztów (ten drugi tylko przy rozliczeniu API).</summary>
        private string? Spend(Summary s, bool apiBilled)
        {
            var b = _cfg().Budget;
            if (b.MaxTokensPerDay > 0 && s.TokensToday >= b.MaxTokensPerDay) return $"dzienny limit tokenów ({s.TokensToday:N0}/{b.MaxTokensPerDay:N0})";
            if (apiBilled && b.MaxUsdPerMonth > 0 && s.CostMonthUsd >= b.MaxUsdPerMonth) return $"miesięczny limit kosztów API ({s.CostMonthUsd:F2}/{b.MaxUsdPerMonth:F2} USD)";
            return null;
        }

        /// <summary>Zapis zużycia. apiBilled: rozliczenie za tokeny wg bieżącego logowania dostawcy (null = wg rodzaju dostawcy).</summary>
        public void Record(ProviderId p, string jobId, string kind, UsageInfo u, DateTime nowUtc, bool? apiBilled = null)
        {
            _store.AddUsage(new UsageRecord
            {
                TimeUtc = nowUtc,
                Provider = p,
                JobId = jobId,
                Kind = kind,
                InputTokens = u.InputTokens,
                CachedInputTokens = u.CachedInputTokens,
                OutputTokens = u.OutputTokens,
                CostUsd = u.CostUsd,
                LimitReached = u.LimitReached,
                ApiBilled = apiBilled ?? IsApiBilled(p),
            });
            if (u.LimitReached)
                _store.SetLimit(new ProviderLimitState
                {
                    Provider = p,
                    DetectedUtc = nowUtc,
                    LimitedUntilUtc = u.LimitResetUtc ?? nowUtc.AddHours(1),
                    Message = u.LimitMessage ?? "limit użycia",
                }, p);
        }
    }

    /// <summary>Wyznaczanie terminu kolejnej zaplanowanej analizy (czas lokalny użytkownika).</summary>
    public static class ScheduleMath
    {
        public static DateTime? NextDueUtc(ScheduleSettings s, DateTime? lastUtc, DateTime nowUtc)
        {
            switch (s.Kind)
            {
                case ScheduleKind.Off:
                    return null;
                case ScheduleKind.EveryHours:
                    return (lastUtc ?? nowUtc).AddHours(Math.Max(1, s.EveryHours));
                case ScheduleKind.Daily:
                case ScheduleKind.Weekly:
                    {
                        var fromLocal = (lastUtc ?? nowUtc).ToLocalTime();
                        var cand = new DateTime(fromLocal.Year, fromLocal.Month, fromLocal.Day, s.Hour, s.Minute, 0, DateTimeKind.Local);
                        for (int i = 0; i < 15; i++)
                        {
                            if (cand > fromLocal && (s.Kind == ScheduleKind.Daily || cand.DayOfWeek == s.WeeklyDay)) return cand.ToUniversalTime();
                            cand = cand.AddDays(1);
                        }
                        return null;
                    }
                default:
                    return null;
            }
        }
    }
}
