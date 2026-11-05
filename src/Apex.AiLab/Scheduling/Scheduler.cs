using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apex.AiLab.Analysis;
using Apex.AiLab.Lab;
using Apex.AiLab.Pipeline;

namespace Apex.AiLab.Scheduling
{
    /// <summary>
    /// Pętla automatyzacji: migawki rynku (zawsze, gdy APEX Desktop działa), zaplanowane analizy i wyzwalacze
    /// (pogorszenie strategii, zmiana rynku, błędy wykonania). Działa tylko przy włączonej automatyzacji.
    /// </summary>
    public sealed class Scheduler
    {
        private readonly LabConfigStore _cfg;
        private readonly LabStore _store;
        private readonly Analyzer _analyzer;
        private readonly JobRunner _runner;
        private DateTime _lastTriggerCheck = DateTime.MinValue;

        public Scheduler(LabConfigStore cfg, LabStore store, Analyzer analyzer, JobRunner runner)
        {
            _cfg = cfg;
            _store = store;
            _analyzer = analyzer;
            _runner = runner;
        }

        public string LastMessage { get; private set; } = "";
        public int TriggerCheckMinutes { get; set; } = 60;

        public DateTime? NextScheduledUtc(DateTime nowUtc)
        {
            var c = _cfg.Config;
            if (!c.AutomationEnabled) return null;
            return ScheduleMath.NextDueUtc(c.Schedule, Anchor(_store.LastScheduledRunUtc, c.Schedule.EnabledSinceUtc), nowUtc);
        }

        /// <summary>
        /// Punkt odniesienia harmonogramu: późniejszy z (ostatnie zaplanowane uruchomienie, włączenie automatyzacji). Po ponownym
        /// włączeniu automatyzacji zaległy termin sprzed wyłączenia nie uruchamia analizy natychmiast.
        /// </summary>
        public static DateTime? Anchor(DateTime? lastScheduledUtc, DateTime? enabledSinceUtc)
        {
            if (lastScheduledUtc == null) return enabledSinceUtc;
            if (enabledSinceUtc == null) return lastScheduledUtc;
            return lastScheduledUtc.Value >= enabledSinceUtc.Value ? lastScheduledUtc : enabledSinceUtc;
        }

        /// <summary>Czy trwa zadanie AI Lab albo wdrożenie (Deploying ustawia Deployer, nie JobRunner).</summary>
        private bool Busy() => _runner.RunningJobId != null || _store.AnyRunning();

        public void Start(CancellationToken ct)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
                while (!ct.IsCancellationRequested)
                {
                    try { await TickAsync(DateTime.UtcNow, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { LastMessage = "Błąd harmonogramu: " + ex.Message; }
                    try { await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                }
            }, ct);
        }

        public async Task TickAsync(DateTime nowUtc, CancellationToken ct)
        {
            var c = _cfg.Config;
            // Migawki rynku – lekkie, tylko odczyt z APEX Desktop.
            var lastSnap = _store.LastSnapshotUtc;
            if (lastSnap == null || (nowUtc - lastSnap.Value).TotalMinutes >= c.Schedule.MarketSnapshotMinutes)
            {
                int n = await _analyzer.TakeSnapshotAsync(ct).ConfigureAwait(false);
                if (n == 0 && lastSnap == null) LastMessage = "Migawki rynku: APEX Desktop niedostępny lub brak instrumentów.";
                else if (n == 0) { /* Desktop chwilowo niedostępny – ponowimy przy następnym cyklu */ }
            }

            if (!c.AutomationEnabled || Busy()) return;

            // Harmonogram. Termin oznaczamy jako wykorzystany dopiero po udanym starcie zadania – inaczej przepadałby do następnego okresu.
            var due = NextScheduledUtc(nowUtc);
            if (due.HasValue && nowUtc >= due.Value)
            {
                var id = _runner.Start(TriggerKind.Schedule, "zaplanowana analiza", runAgent: false, provider: null);
                if (id != null)
                {
                    _store.MarkScheduledRun(nowUtc);
                    LastMessage = $"Zaplanowana analiza rozpoczęta ({id}).";
                }
                else LastMessage = "Zaplanowana analiza odłożona – inne zadanie lub wdrożenie w toku (ponowię próbę).";
                return;
            }

            // Wyzwalacze – analiza co godzinę; agent tylko przy nowych, istotnych wnioskach.
            if ((nowUtc - _lastTriggerCheck).TotalMinutes < TriggerCheckMinutes) return;
            _lastTriggerCheck = nowUtc;
            if (!(c.Schedule.TriggerOnStrategyDegradation || c.Schedule.TriggerOnMarketChange || c.Schedule.TriggerOnExecutionErrors)) return;
            var report = await _analyzer.AnalyzeAsync(c.Schedule, ct).ConfigureAwait(false);
            foreach (var f in report.Findings.Where(f => f.Level != "info"))
            {
                bool enabled = f.Kind switch
                {
                    TriggerKind.StrategyDegradation => c.Schedule.TriggerOnStrategyDegradation,
                    TriggerKind.MarketChange => c.Schedule.TriggerOnMarketChange,
                    TriggerKind.ExecutionErrors => c.Schedule.TriggerOnExecutionErrors,
                    _ => false,
                };
                if (!enabled) continue;
                string key = f.Kind + ":" + f.Title;
                var last = _store.LastTrigger(key);
                if (last.HasValue && (nowUtc - last.Value).TotalHours < c.Schedule.MinHoursBetweenAgentRuns) continue;
                // Analiza trwała – w międzyczasie mogło ruszyć zadanie ręczne lub wdrożenie. Wyzwalacz oznaczamy dopiero po udanym starcie.
                var id = Busy() ? null : _runner.Start(f.Kind, f.Title + " – " + f.Detail, runAgent: true, provider: null);
                if (id != null)
                {
                    _store.MarkTrigger(key, nowUtc);
                    LastMessage = $"Wyzwalacz „{f.Title}” – rozpoczęto zadanie {id}.";
                }
                else
                {
                    LastMessage = "Wyzwalacz odłożony – inne zadanie lub wdrożenie w toku (ponowię próbę po jego zakończeniu).";
                    _lastTriggerCheck = DateTime.MinValue; // ponowna analiza zaraz po zwolnieniu (pętla czeka, dopóki trwa inne zadanie)
                }
                return;
            }
        }
    }
}
