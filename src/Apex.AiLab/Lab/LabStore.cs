using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Apex.AiLab.Lab
{
    /// <summary>
    /// Trwały stan AI Lab: zadania (jobs/&lt;id&gt;/), analizy, migawki rynku, zużycie limitów, stan limitów dostawców,
    /// licznik użyć okresu odłożonego. Zapis atomowy. Strumień zdarzeń (postęp agentów) w pamięci + plik per zadanie.
    /// </summary>
    public sealed class LabStore
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, LabJob> _jobs = new(StringComparer.Ordinal);
        private readonly List<LabEvent> _events = new();
        private readonly List<UsageRecord> _usage = new();
        private readonly Dictionary<ProviderId, ProviderLimitState> _limits = new();
        private readonly List<MarketSnapshot> _snapshots = new();
        private long _seq;
        private LabCounters _counters = new();

        private sealed class LabCounters
        {
            public int HoldoutUses { get; set; }
            public DateTime? LastAgentRunUtc { get; set; }
            public DateTime? LastScheduledRunUtc { get; set; }
            public DateTime? LastSnapshotUtc { get; set; }
            public Dictionary<string, DateTime> LastTriggerUtc { get; set; } = new();
        }

        private sealed class UsageFile
        {
            public List<UsageRecord> Records { get; set; } = new();
            public List<ProviderLimitState> Limits { get; set; } = new();
        }

        public LabStore(string labDir)
        {
            Dir = labDir;
            JobsDir = Path.Combine(labDir, "zadania");
            AnalysesDir = Path.Combine(labDir, "analizy");
            Directory.CreateDirectory(JobsDir);
            Directory.CreateDirectory(AnalysesDir);
            Load();
        }

        public string Dir { get; }
        public string JobsDir { get; }
        public string AnalysesDir { get; }

        public string JobDir(string id) => Path.Combine(JobsDir, id);

        private void Load()
        {
            foreach (var d in Directory.GetDirectories(JobsDir))
            {
                var job = ReadJson<LabJob>(Path.Combine(d, "zadanie.json"));
                if (job == null || string.IsNullOrEmpty(job.Id)) continue;
                // Zadanie przerwane zamknięciem aplikacji nie może wisieć jako "w toku".
                if (job.Status == JobStatus.Running || job.Status == JobStatus.Deploying)
                {
                    job.Status = JobStatus.Failed;
                    job.StatusText = "Przerwane – AI Lab został zamknięty w trakcie zadania.";
                    job.FinishedUtc ??= DateTime.UtcNow;
                    foreach (var s in job.Stages.Where(s => s.Status == StageStatus.Running)) { s.Status = StageStatus.Failed; s.Summary = "przerwane"; }
                    WriteJson(Path.Combine(d, "zadanie.json"), job);
                }
                _jobs[job.Id] = job;
            }
            var usage = ReadJson<UsageFile>(Path.Combine(Dir, "zuzycie.json"));
            if (usage != null)
            {
                _usage.AddRange(usage.Records);
                foreach (var l in usage.Limits) _limits[l.Provider] = l;
            }
            _counters = ReadJson<LabCounters>(Path.Combine(Dir, "liczniki.json")) ?? new LabCounters();
            string snaps = Path.Combine(Dir, "rynek.json");
            var list = ReadJson<List<MarketSnapshot>>(snaps);
            if (list != null) _snapshots.AddRange(list);
        }

        // ------------------------------------------------------------------ zadania

        public IReadOnlyList<LabJob> Jobs()
        {
            lock (_lock) return _jobs.Values.OrderByDescending(j => j.CreatedUtc).ToList();
        }

        public LabJob? Job(string id)
        {
            lock (_lock) return _jobs.TryGetValue(id, out var j) ? j : null;
        }

        public void SaveJob(LabJob job)
        {
            lock (_lock)
            {
                _jobs[job.Id] = job;
                Directory.CreateDirectory(JobDir(job.Id));
                WriteJson(Path.Combine(JobDir(job.Id), "zadanie.json"), job);
            }
        }

        public bool AnyRunning()
        {
            lock (_lock) return _jobs.Values.Any(j => j.Status == JobStatus.Running || j.Status == JobStatus.Deploying);
        }

        // ------------------------------------------------------------------ zdarzenia (postęp)

        public LabEvent AddEvent(string jobId, string kind, string text)
        {
            if (text.Length > 4000) text = text.Substring(0, 4000) + " …";
            LabEvent e;
            lock (_lock)
            {
                e = new LabEvent { Seq = ++_seq, TimeUtc = DateTime.UtcNow, JobId = jobId, Kind = kind, Text = text };
                _events.Add(e);
                if (_events.Count > 3000) _events.RemoveRange(0, _events.Count - 3000);
            }
            if (!string.IsNullOrEmpty(jobId))
            {
                try
                {
                    Directory.CreateDirectory(JobDir(jobId));
                    File.AppendAllText(Path.Combine(JobDir(jobId), "zdarzenia.jsonl"), JsonSerializer.Serialize(e, LabConfigStore.Json).Replace("\n", " ").Replace("\r", "") + "\n");
                }
                catch (IOException) { }
            }
            return e;
        }

        public (List<LabEvent> events, long last) EventsSince(long since, string? jobId = null, int max = 400)
        {
            lock (_lock)
            {
                var q = _events.Where(e => e.Seq > since && (jobId == null || e.JobId == jobId)).Take(max).ToList();
                return (q, q.Count > 0 ? q[^1].Seq : Math.Max(since, _seq));
            }
        }

        /// <summary>Pełny dziennik zdarzeń zadania z dysku (także po restarcie AI Lab).</summary>
        public List<LabEvent> JobEvents(string jobId, int max = 2000)
        {
            string p = Path.Combine(JobDir(jobId), "zdarzenia.jsonl");
            var list = new List<LabEvent>();
            if (!File.Exists(p)) return list;
            foreach (var line in File.ReadLines(p))
            {
                try { var e = JsonSerializer.Deserialize<LabEvent>(line, LabConfigStore.Json); if (e != null) list.Add(e); }
                catch (JsonException) { }
            }
            return list.Count > max ? list.Skip(list.Count - max).ToList() : list;
        }

        // ------------------------------------------------------------------ analizy

        public void SaveAnalysis(AnalysisReport r) => WriteJson(Path.Combine(AnalysesDir, r.Id + ".json"), r);

        public AnalysisReport? Analysis(string id) => ReadJson<AnalysisReport>(Path.Combine(AnalysesDir, id + ".json"));

        public List<AnalysisReport> RecentAnalyses(int n)
        {
            return new DirectoryInfo(AnalysesDir).GetFiles("*.json").OrderByDescending(f => f.Name).Take(n)
                .Select(f => ReadJson<AnalysisReport>(f.FullName)).Where(r => r != null).Select(r => r!).ToList();
        }

        // ------------------------------------------------------------------ migawki rynku

        public void AddSnapshot(MarketSnapshot s)
        {
            lock (_lock)
            {
                _snapshots.Add(s);
                var cutoff = DateTime.UtcNow.AddDays(-45);
                _snapshots.RemoveAll(x => x.TimeUtc < cutoff);
                _counters.LastSnapshotUtc = s.TimeUtc;
                WriteJson(Path.Combine(Dir, "rynek.json"), _snapshots);
                WriteJson(Path.Combine(Dir, "liczniki.json"), _counters);
            }
        }

        public List<MarketSnapshot> Snapshots()
        {
            lock (_lock) return _snapshots.ToList();
        }

        public DateTime? LastSnapshotUtc { get { lock (_lock) return _counters.LastSnapshotUtc; } }

        // ------------------------------------------------------------------ liczniki

        public int HoldoutUses { get { lock (_lock) return _counters.HoldoutUses; } }

        public int RegisterHoldoutUse()
        {
            lock (_lock)
            {
                _counters.HoldoutUses++;
                WriteJson(Path.Combine(Dir, "liczniki.json"), _counters);
                return _counters.HoldoutUses;
            }
        }

        public DateTime? LastAgentRunUtc { get { lock (_lock) return _counters.LastAgentRunUtc; } }
        public DateTime? LastScheduledRunUtc { get { lock (_lock) return _counters.LastScheduledRunUtc; } }

        public void MarkAgentRun(DateTime utc) { lock (_lock) { _counters.LastAgentRunUtc = utc; WriteJson(Path.Combine(Dir, "liczniki.json"), _counters); } }
        public void MarkScheduledRun(DateTime utc) { lock (_lock) { _counters.LastScheduledRunUtc = utc; WriteJson(Path.Combine(Dir, "liczniki.json"), _counters); } }

        public DateTime? LastTrigger(string key) { lock (_lock) return _counters.LastTriggerUtc.TryGetValue(key, out var t) ? t : null; }
        public void MarkTrigger(string key, DateTime utc) { lock (_lock) { _counters.LastTriggerUtc[key] = utc; WriteJson(Path.Combine(Dir, "liczniki.json"), _counters); } }

        // ------------------------------------------------------------------ zużycie i limity

        public void AddUsage(UsageRecord r)
        {
            lock (_lock)
            {
                _usage.Add(r);
                var cutoff = DateTime.UtcNow.AddDays(-120);
                _usage.RemoveAll(x => x.TimeUtc < cutoff);
                SaveUsage();
            }
        }

        public List<UsageRecord> Usage(DateTime fromUtc)
        {
            lock (_lock) return _usage.Where(u => u.TimeUtc >= fromUtc).ToList();
        }

        public ProviderLimitState? Limit(ProviderId p)
        {
            lock (_lock) return _limits.TryGetValue(p, out var l) ? l : null;
        }

        public void SetLimit(ProviderLimitState? state, ProviderId p)
        {
            lock (_lock)
            {
                if (state == null) _limits.Remove(p); else _limits[p] = state;
                SaveUsage();
            }
        }

        private void SaveUsage() => WriteJson(Path.Combine(Dir, "zuzycie.json"), new UsageFile { Records = _usage, Limits = _limits.Values.ToList() });

        // ------------------------------------------------------------------ pliki

        public static T? ReadJson<T>(string path) where T : class
        {
            try
            {
                if (!File.Exists(path)) return null;
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), LabConfigStore.Json);
            }
            catch (JsonException) { return null; }
            catch (IOException) { return null; }
        }

        public static void WriteJson<T>(string path, T value)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, LabConfigStore.Json));
            File.Move(tmp, path, true);
        }
    }
}
