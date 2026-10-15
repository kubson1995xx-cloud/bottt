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
            return ScheduleMath.NextDueUtc(c.Schedule, _store.LastScheduledRunUtc ?? c.Schedule.EnabledSinceUtc, nowUtc);
        }

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

            if (!c.AutomationEnabled || _runner.RunningJobId != null) return;

            // Harmonogram.
            var due = NextScheduledUtc(nowUtc);
            if (due.HasValue && nowUtc >= due.Value)
            {
                _store.MarkScheduledRun(nowUtc);
                var id = _runner.Start(TriggerKind.Schedule, "zaplanowana analiza", runAgent: false, provider: null);
                LastMessage = id != null ? $"Zaplanowana analiza rozpoczęta ({id})." : "Zaplanowana analiza pominięta – inne zadanie w toku.";
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
                _store.MarkTrigger(key, nowUtc);
                var id = _runner.Start(f.Kind, f.Title + " – " + f.Detail, runAgent: true, provider: null);
                LastMessage = id != null ? $"Wyzwalacz „{f.Title}” – rozpoczęto zadanie {id}." : "Wyzwalacz pominięty – inne zadanie w toku.";
                return;
            }
        }
    }
}
