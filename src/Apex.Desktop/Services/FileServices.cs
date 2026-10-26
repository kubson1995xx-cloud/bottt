using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Apex.Core.Engine;
using Apex.Core.Management;
using Apex.Core.Performance;

namespace Apex.Desktop.Services
{
    /// <summary>Trwały magazyn stanu silnika: jeden plik JSON na klucz, zapis atomowy.</summary>
    public sealed class FileStore : IKeyValueStore
    {
        private readonly string _dir;
        private readonly object _lock = new();

        private volatile bool _hasFault;
        public bool HasFault => _hasFault;

        public FileStore(string dir)
        {
            _dir = dir;
            Directory.CreateDirectory(dir);
        }

        private string PathFor(string key)
        {
            var sb = new StringBuilder();
            foreach (char c in key) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return Path.Combine(_dir, sb + ".json");
        }

        public string? Get(string key)
        {
            lock (_lock)
            {
                try
                {
                    string p = PathFor(key);
                    if (File.Exists(p)) return File.ReadAllText(p);
                    // Stara kopia może nie zawierać Emergency Stop lub niepewnego zlecenia.
                    if (File.Exists(p + ".bak") || Directory.Exists(p))
                        throw new IOException("Brak autorytatywnego pliku stanu: " + p);
                    return null;
                }
                catch { _hasFault = true; throw; }
            }
        }

        public void Set(string key, string value)
        {
            lock (_lock)
            {
                try { AtomicFile.Write(PathFor(key), value); }
                catch { _hasFault = true; throw; }
            }
        }

        public void Flush() { }
    }

    /// <summary>Dziennik transakcji i zdarzeń w plikach CSV/tekstowych (osobny katalog dla każdego konta).</summary>
    public sealed class FileJournal : IRecoveryJournal
    {
        private readonly string _dir;
        private readonly object _lock = new();
        private readonly List<TradeRecord> _recent = new();
        private readonly Dictionary<long, TradeRecord> _closed = new();
        public IReadOnlyList<TradeRecord> ClosedRecords { get { lock (_lock) return _closed.Values.ToList(); } }
        private readonly LinkedList<string> _events = new();

        public FileJournal(string dir)
        {
            _dir = dir;
            Directory.CreateDirectory(dir);
            string trades = Path.Combine(dir, "transakcje.csv");
            if (!File.Exists(trades)) AtomicFile.Write(trades, TradeRecord.CsvHeader + Environment.NewLine);
            LoadRecent(trades);
        }

        private void LoadRecent(string path)
        {
            foreach (var line in File.ReadLines(path).Skip(1))
            {
                var r = Parse(line) ?? throw new IOException("Uszkodzony dziennik transakcji: " + path);
                if (_closed.ContainsKey(r.PositionId)) throw new IOException("Powtórzony klucz starego dziennika – wymagane uzgodnienie: " + r.PositionId);
                _closed.Add(r.PositionId, r);
                _recent.Add(r);
                if (_recent.Count > 500) _recent.RemoveAt(0);
            }
        }

        private static TradeRecord? Parse(string line)
        {
            var p = line.Split(';');
            if (p.Length < 25) return null;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            try
            {
                return new TradeRecord
                {
                    PositionId = long.Parse(p[0], ci),
                    SignalId = p[1],
                    Symbol = p[2],
                    AssetClass = Enum.TryParse<Apex.Core.Model.AssetClass>(p[3], out var ac) ? ac : default,
                    Strategy = Enum.TryParse<Apex.Core.Model.StrategyId>(p[4], out var st) ? st : default,
                    Variant = p[5],
                    TimeFrame = Enum.TryParse<Apex.Core.Model.Tf>(p[6], out var tf) ? tf : default,
                    Direction = p[7] == "Short" ? Apex.Core.Model.TradeDirection.Short : Apex.Core.Model.TradeDirection.Long,
                    EntryTimeUtc = DateTime.Parse(p[8], ci, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal),
                    ExitTimeUtc = DateTime.Parse(p[9], ci, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal),
                    EntryPrice = double.Parse(p[10], ci),
                    ExitPrice = double.Parse(p[11], ci),
                    Volume = double.Parse(p[12], ci),
                    InitialRiskMoney = double.Parse(p[13], ci),
                    GrossProfit = double.Parse(p[14], ci),
                    Commission = double.Parse(p[15], ci),
                    Swap = double.Parse(p[16], ci),
                    NetProfit = double.Parse(p[17], ci),
                    BarsHeld = int.Parse(p[19], ci),
                    Score = double.Parse(p[20], ci),
                    Regime = p.Length > 21 ? p[21] : "",
                    EntryReason = p.Length > 22 ? p[22] : "",
                    ExitReason = p.Length > 23 ? p[23] : "",
                    Parameters = p.Length > 24 ? p[24] : "",
                };
            }
            catch (Exception ex) when (ex is FormatException || ex is OverflowException) { return null; }
        }

        private void Append(string file, string line)
        {
            lock (_lock)
            {
                try { File.AppendAllText(Path.Combine(_dir, file), line + Environment.NewLine, Encoding.UTF8); }
                catch (IOException) { }
            }
        }

        public void TradeOpened(TradePlan plan, double volume, string details) =>
            Event($"OTWARCIE #{plan.PositionId} {plan.Symbol} {plan.Direction} wol. {volume} {plan.Strategy} wejście {plan.EntryPrice} SL {plan.InitialStop} ryzyko {plan.RiskAmount:F2} | {plan.EntryReason} | {details}");

        public void TradeClosed(TradeRecord record)
        {
            lock (_lock)
            {
                if (_closed.ContainsKey(record.PositionId)) return;
                // Atomic publication avoids a torn CSV tail and a separate dedupe-index crash gap.
                // This is append semantics, not retention/compaction: every accepted row is retained.
                string path = Path.Combine(_dir, "transakcje.csv");
                AtomicFile.Write(path, File.ReadAllText(path) + record.ToCsv() + Environment.NewLine);
                _closed.Add(record.PositionId, record);
                _recent.Add(record);
                if (_recent.Count > 500) _recent.RemoveAt(0);
            }
            Event($"ZAMKNIĘCIE #{record.PositionId} {record.Symbol} {record.NetProfit:F2} ({record.RMultiple:F2}R) – {record.ExitReason}");
        }

        public void Signal(string line) => Append("sygnaly.log", $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} {line}");

        public void Event(string line)
        {
            string l = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} {line}";
            Append("zdarzenia.log", l);
            lock (_lock)
            {
                _events.AddFirst(l);
                while (_events.Count > 100) _events.RemoveLast();
            }
        }

        public List<TradeRecord> RecentTrades(int n)
        {
            lock (_lock) return _recent.TakeLast(n).Reverse().ToList();
        }

        public List<TradeRecord> AllRecent()
        {
            lock (_lock) return _recent.ToList();
        }

        public List<string> RecentEvents(int n)
        {
            lock (_lock) return _events.Take(n).ToList();
        }
    }

    /// <summary>Log aplikacji: plik dzienny + bufor ostatnich wpisów dla panelu.</summary>
    public sealed class AppLog : IEngineLog
    {
        private readonly string _dir;
        private readonly string _prefix;
        private readonly object _lock = new();
        private readonly LinkedList<(DateTime t, string level, string msg)> _ring = new();

        public AppLog(string dir, string prefix = "")
        {
            _dir = dir;
            _prefix = prefix;
            Directory.CreateDirectory(dir);
        }

        public AppLog For(string prefix) => new(_dir, prefix) { };

        private void Write(string level, string msg)
        {
            var now = DateTime.UtcNow;
            string line = $"{now:yyyy-MM-dd HH:mm:ss} [{level}] {(_prefix.Length > 0 ? "[" + _prefix + "] " : "")}{msg}";
            lock (_lock)
            {
                _ring.AddFirst((now, level, msg));
                while (_ring.Count > 300) _ring.RemoveLast();
                try { File.AppendAllText(Path.Combine(_dir, $"apex-{now:yyyy-MM-dd}.log"), line + Environment.NewLine, Encoding.UTF8); }
                catch (IOException) { }
            }
            Console.WriteLine(line);
        }

        public void Info(string message) => Write("INFO", message);
        public void Warn(string message) => Write("UWAGA", message);
        public void Error(string message) => Write("BŁĄD", message);

        public List<object> Recent(int n)
        {
            lock (_lock) return _ring.Take(n).Select(x => (object)new { time = x.t, level = x.level, message = x.msg }).ToList();
        }
    }
}
