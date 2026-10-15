using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Apex.AiLab.Lab;

namespace Apex.AiLab.Pipeline
{
    /// <summary>Niezależna weryfikacja kopii roboczej: kompilacja, testy regresyjne, backtest portfela – wykonywana przez AI Lab, nie przez agenta.</summary>
    public sealed class Verification
    {
        private readonly RepoSettings _repo;
        private readonly Action<string, string> _log;

        public Verification(RepoSettings repo, Action<string, string> log)
        {
            _repo = repo;
            _log = log;
        }

        public static CommandSpec? Dotnet() => ExecutableLocator.Find("dotnet");

        private static CommandSpec Need() => Dotnet() ?? throw new InvalidOperationException("Nie znaleziono .NET SDK (dotnet). Zainstaluj: winget install Microsoft.DotNet.SDK.8");

        /// <summary>Przywraca pakiety NuGet (poza piaskownicą agenta – agent kompiluje potem z --no-restore).</summary>
        public async Task<bool> RestoreAsync(string dir, CancellationToken ct)
        {
            var r = await ProcessRunner.RunAsync(Need(), new[] { "restore", "tests/Apex.Tests/Apex.Tests.csproj", "-nologo" }, dir, timeout: TimeSpan.FromMinutes(_repo.BuildTimeoutMinutes), ct: ct).ConfigureAwait(false);
            if (!r.Ok) _log("warning", "dotnet restore: " + r.Tail(5));
            return r.Ok;
        }

        public async Task<BuildResult> BuildAsync(string dir, CancellationToken ct)
        {
            var res = new BuildResult { Ok = true };
            var steps = new List<(string name, string[] args)>
            {
                ("Silnik, APEX Desktop, symulator i testy", new[] { "build", "tests/Apex.Tests/Apex.Tests.csproj", "-c", "Release", "-nologo" }),
                ("Backtester", new[] { "build", "tools/Apex.Backtester/Apex.Backtester.csproj", "-c", "Release", "-nologo" }),
            };
            foreach (var sln in new[] { "src/Platforms/cTrader/ApexBridge/ApexBridge.sln", "src/Platforms/cTrader/ApexTradingAI/ApexTradingAI.sln" })
                if (File.Exists(Path.Combine(dir, sln))) steps.Add(("cBot " + Path.GetFileNameWithoutExtension(sln), new[] { "build", sln, "-c", "Release", "-nologo" }));
            foreach (var (name, args) in steps)
            {
                _log("command", "▶ dotnet " + string.Join(" ", args));
                var r = await ProcessRunner.RunAsync(Need(), args, dir, timeout: TimeSpan.FromMinutes(_repo.BuildTimeoutMinutes), ct: ct).ConfigureAwait(false);
                res.DurationSec += r.DurationSec;
                var errs = r.Output.Concat(r.Errors).Where(l => l.Contains(": error ", StringComparison.Ordinal)).Select(l => l.Trim()).Distinct().Take(30).ToList();
                int warnings = r.Output.Count(l => l.Contains(": warning ", StringComparison.Ordinal));
                bool ok = r.Ok;
                res.Steps.Add($"{name}: {(ok ? "OK" : r.TimedOut ? "PRZEKROCZONY CZAS" : "BŁĄD")} ({r.DurationSec:F0} s{(warnings > 0 ? $", ostrzeżeń {warnings}" : "")})");
                if (!ok)
                {
                    res.Ok = false;
                    res.Errors.AddRange(errs.Count > 0 ? errs : new List<string> { r.Tail(10) });
                    break;
                }
            }
            return res;
        }

        public async Task<TestResult> TestAsync(string dir, string resultsDir, CancellationToken ct)
        {
            Directory.CreateDirectory(resultsDir);
            string trx = Path.Combine(resultsDir, "wyniki.trx");
            if (File.Exists(trx)) File.Delete(trx);
            var args = new[] { "test", "tests/Apex.Tests/Apex.Tests.csproj", "-c", "Release", "--no-build", "-nologo", "--logger", "trx;LogFileName=wyniki.trx", "--results-directory", resultsDir };
            _log("command", "▶ dotnet " + string.Join(" ", args.Take(6)) + " …");
            var r = await ProcessRunner.RunAsync(Need(), args, dir, timeout: TimeSpan.FromMinutes(_repo.TestTimeoutMinutes), ct: ct).ConfigureAwait(false);
            var res = ParseTrx(trx);
            res.DurationSec = r.DurationSec;
            if (!res.Ran)
            {
                res.FailedNames.Add(r.TimedOut ? "Przekroczony limit czasu testów." : "Brak wyników testów: " + r.Tail(6));
                res.Failed = Math.Max(1, res.Failed);
            }
            return res;
        }

        public static TestResult ParseTrx(string path)
        {
            var res = new TestResult();
            if (!File.Exists(path)) return res;
            try
            {
                var doc = XDocument.Load(path);
                XNamespace ns = doc.Root?.Name.Namespace ?? "";
                var counters = doc.Descendants(ns + "Counters").FirstOrDefault();
                if (counters != null)
                {
                    res.Ran = true;
                    res.Total = Int(counters, "total");
                    res.Passed = Int(counters, "passed");
                    res.Failed = Int(counters, "failed") + Int(counters, "error") + Int(counters, "timeout") + Int(counters, "aborted");
                    res.Skipped = Int(counters, "notExecuted");
                }
                res.FailedNames = doc.Descendants(ns + "UnitTestResult")
                    .Where(e => (string?)e.Attribute("outcome") is "Failed" or "Error" or "Timeout" or "Aborted")
                    .Select(e => (string?)e.Attribute("testName") ?? "?").Take(50).ToList();
            }
            catch (System.Xml.XmlException) { }
            return res;

            static int Int(XElement e, string name) => int.TryParse((string?)e.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
        }

        /// <summary>Backtest portfela na profilu z kopii roboczej (ten sam silnik, te same koszty). Zwraca null, gdy brak danych.</summary>
        public async Task<(List<BacktestMetrics>? metrics, string message)> BacktestAsync(string dir, string outDir, CancellationToken ct)
        {
            if (!_repo.RunBacktests) return (null, "Backtest wyłączony w ustawieniach.");
            string data = _repo.BacktestDataDir;
            if (string.IsNullOrWhiteSpace(data) || !Directory.Exists(data) || Directory.GetFiles(data, "*_H1.csv").Length == 0)
                return (null, "Brak danych historycznych (ustaw katalog danych z plikami *_H1.csv / *_M15.csv – patrz tools/data/prepare_data.py).");
            string dll = Path.Combine(dir, "tools", "Apex.Backtester", "bin", "Release", "net8.0", "Apex.Backtester.dll");
            if (!File.Exists(dll)) return (null, "Backtester nie został zbudowany.");
            Directory.CreateDirectory(outDir);
            string summary = Path.Combine(outDir, "podsumowanie.json");
            var args = new[] { dll, "portfolio", "--data", data, "--profiles", Path.Combine(dir, "config", "profile_default.json"), "--out", outDir, "--summary", summary };
            _log("command", "▶ backtest portfela (okres badawczy 2005–2017 i odłożony 2018–2020.05)");
            var r = await ProcessRunner.RunAsync(Need(), args, dir, timeout: TimeSpan.FromMinutes(_repo.BacktestTimeoutMinutes), ct: ct).ConfigureAwait(false);
            if (!r.Ok) return (null, r.TimedOut ? "Przekroczony czas backtestu." : "Backtest nieudany: " + Short(r.Tail(3), 300));
            if (!File.Exists(summary)) return (new List<BacktestMetrics>(), "Profil bez aktywnych strategii – portfel pusty.");
            return (ParseSummary(File.ReadAllText(summary)), "OK");
        }

        private static string Short(string s, int max)
        {
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        public static List<BacktestMetrics> ParseSummary(string json)
        {
            var list = new List<BacktestMetrics>();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array) return list;
            foreach (var r in runs.EnumerateArray())
            {
                double D(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
                list.Add(new BacktestMetrics
                {
                    Period = r.TryGetProperty("period", out var p) ? p.GetString() ?? "" : "",
                    MinScore = D("minScore"),
                    Trades = (int)D("trades"),
                    WinRate = D("winRate"),
                    ExpectancyR = D("expectancyR"),
                    TStat = D("tStat"),
                    ProfitFactor = D("profitFactor"),
                    NetProfit = D("netProfit"),
                    MaxEquityDrawdownPct = D("maxEquityDrawdownPct"),
                    Sharpe = D("sharpe"),
                });
            }
            return list;
        }

        /// <summary>
        /// Porównanie z wersją bazową (minimalna ocena 50, jak w handlu). Reguły ustalone z góry:
        /// gorszy, gdy E[R] badawczy spada o &gt; 0,01R, E[R] odłożony o &gt; 0,02R albo obsunięcie rośnie o &gt; 0,5 pp.
        /// </summary>
        public static void Compare(BacktestComparison c)
        {
            var b = c.Baseline.Where(x => x.MinScore == 50).ToDictionary(x => x.Period);
            var n = c.Candidate.Where(x => x.MinScore == 50).ToDictionary(x => x.Period);
            if (!b.TryGetValue("research", out var br) || !n.TryGetValue("research", out var nr))
            {
                bool bothEmpty = c.Baseline.Count == 0 && c.Candidate.Count == 0;
                c.Verdict = bothEmpty ? "bez zmian" : "niejednoznaczny";
                c.Notes.Add(bothEmpty ? "Brak aktywnych strategii w obu wersjach." : "Brak porównywalnych wyników.");
                return;
            }
            b.TryGetValue("holdout", out var bh);
            n.TryGetValue("holdout", out var nh);
            bool identical = Same(br, nr) && (bh == null || nh == null || Same(bh, nh));
            if (identical) { c.Verdict = "bez zmian"; c.Notes.Add("Zmiana nie wpływa na wyniki strategii w backteście."); return; }
            double dRes = nr.ExpectancyR - br.ExpectancyR;
            double dHold = (nh?.ExpectancyR ?? 0) - (bh?.ExpectancyR ?? 0);
            double dDdRes = nr.MaxEquityDrawdownPct - br.MaxEquityDrawdownPct;
            double dDdHold = (nh?.MaxEquityDrawdownPct ?? 0) - (bh?.MaxEquityDrawdownPct ?? 0);
            c.Notes.Add($"Okres badawczy: E[R] {br.ExpectancyR:+0.000;-0.000} → {nr.ExpectancyR:+0.000;-0.000} ({dRes:+0.000;-0.000}), transakcje {br.Trades} → {nr.Trades}, maks. DD {br.MaxEquityDrawdownPct:F2}% → {nr.MaxEquityDrawdownPct:F2}%.");
            if (bh != null && nh != null)
                c.Notes.Add($"Okres odłożony: E[R] {bh.ExpectancyR:+0.000;-0.000} → {nh.ExpectancyR:+0.000;-0.000} ({dHold:+0.000;-0.000}), transakcje {bh.Trades} → {nh.Trades}, maks. DD {bh.MaxEquityDrawdownPct:F2}% → {nh.MaxEquityDrawdownPct:F2}%.");
            if (dRes < -0.01 || dHold < -0.02 || dDdRes > 0.5 || dDdHold > 0.5) c.Verdict = "gorszy";
            else if (dRes > 0.01 && dHold >= 0 && dDdRes <= 0.25 && dDdHold <= 0.25) c.Verdict = "lepszy";
            else c.Verdict = "niejednoznaczny";
            if (c.Verdict == "lepszy")
                c.Notes.Add("Lepszy wynik backtestu NIE jest dowodem przyszłej skuteczności – każda kolejna próba zwiększa ryzyko dopasowania do historii.");
        }

        private static bool Same(BacktestMetrics a, BacktestMetrics b) =>
            a.Trades == b.Trades && Math.Abs(a.ExpectancyR - b.ExpectancyR) < 1e-9 && Math.Abs(a.MaxEquityDrawdownPct - b.MaxEquityDrawdownPct) < 1e-9;
    }

    /// <summary>Wyniki wersji bazowej (testy + backtest) zapamiętywane per commit – liczone raz.</summary>
    public sealed class BaselineCache
    {
        private readonly string _dir;

        public BaselineCache(string labDir)
        {
            _dir = Path.Combine(labDir, "bazowe");
            Directory.CreateDirectory(_dir);
        }

        public sealed class Entry
        {
            public string Commit { get; set; } = "";
            public DateTime TimeUtc { get; set; }
            public bool BuildOk { get; set; }
            public TestResult? Tests { get; set; }
            public List<BacktestMetrics>? Backtest { get; set; }
            public string BacktestMessage { get; set; } = "";
            public string DataDir { get; set; } = "";
        }

        public Entry? Get(string commit, string dataDir)
        {
            var e = LabStore.ReadJson<Entry>(Path.Combine(_dir, Safe(commit) + ".json"));
            return e != null && string.Equals(e.DataDir, dataDir ?? "", StringComparison.OrdinalIgnoreCase) ? e : null;
        }

        public void Put(Entry e) => LabStore.WriteJson(Path.Combine(_dir, Safe(e.Commit) + ".json"), e);

        private static string Safe(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray());
    }
}
