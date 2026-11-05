using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Apex.AiLab.Lab;

namespace Apex.AiLab.Pipeline
{
    /// <summary>Przyczyna wyniku backtestu – odróżnia pominięcie z powodu ustawień/danych od błędu kodu, przekroczenia czasu i przerwania.</summary>
    public enum BacktestOutcome
    {
        Ok,
        /// <summary>Backtest wyłączony w ustawieniach.</summary>
        Disabled,
        /// <summary>Brak katalogu danych lub plików *_H1.csv.</summary>
        NoData,
        /// <summary>Brak zbudowanego backtestera (mimo udanej kompilacji).</summary>
        NotBuilt,
        Failed,
        TimedOut,
        Cancelled,
    }

    public sealed class BacktestRun
    {
        public List<BacktestMetrics>? Metrics { get; set; }
        public string Message { get; set; } = "";
        public BacktestOutcome Outcome { get; set; }

        /// <summary>Pominięty wyłącznie z powodu ustawień lub braku danych (nie jest to błąd kodu ani wynik niepełny).</summary>
        public bool IsConfigSkip => Outcome is BacktestOutcome.Disabled or BacktestOutcome.NoData;
    }

    /// <summary>Weryfikacja kopii roboczej (interfejs – podmiana w testach potoku zadań).</summary>
    public interface IVerification
    {
        Task<bool> RestoreAsync(string dir, CancellationToken ct);
        Task<BuildResult> BuildAsync(string dir, CancellationToken ct);
        Task<TestResult> TestAsync(string dir, string resultsDir, CancellationToken ct);
        Task<BacktestRun> RunBacktestAsync(string dir, string outDir, CancellationToken ct);
    }

    /// <summary>
    /// Niezależna weryfikacja kopii roboczej: kompilacja, testy regresyjne, backtest portfela – wykonywana przez AI Lab, nie przez agenta.
    /// Procesy weryfikacji (restore/build/test/backtest) dostają oczyszczone środowisko: bez zmiennych z kluczami, tokenami i hasłami,
    /// z APEX_DATA_DIR wskazującym na katalog tymczasowy zadania (kod kandydata nie widzi danych działającego APEX).
    /// </summary>
    public sealed class Verification : IVerification
    {
        private readonly RepoSettings _repo;
        private readonly Action<string, string> _log;
        private readonly Dictionary<string, string?> _env;

        /// <param name="isolationDir">Katalog tymczasowy zadania (APEX_DATA_DIR procesów weryfikacji); null = wspólny katalog tymczasowy.</param>
        public Verification(RepoSettings repo, Action<string, string> log, string? isolationDir = null)
        {
            _repo = repo;
            _log = log;
            string dataDir = Path.Combine(isolationDir ?? Path.Combine(Path.GetTempPath(), "apex-ailab-weryfikacja"), "dane-apex");
            Directory.CreateDirectory(dataDir);
            _env = ScrubbedEnvironment(dataDir);
        }

        /// <summary>Zmiany środowiska procesów weryfikacji (null = zmienna usunięta).</summary>
        public IReadOnlyDictionary<string, string?> IsolatedEnvironment => _env;

        public static CommandSpec? Dotnet() => ExecutableLocator.Find("dotnet");

        private static CommandSpec Need() => Dotnet() ?? throw new InvalidOperationException("Nie znaleziono .NET SDK (dotnet). Zainstaluj: winget install Microsoft.DotNet.SDK.8");

        /// <summary>Czy nazwa zmiennej środowiskowej wskazuje na sekret (klucz API, token, hasło).</summary>
        public static bool IsSecretName(string name)
        {
            string n = name.ToUpperInvariant();
            return n.Contains("API_KEY", StringComparison.Ordinal) || n.Contains("APIKEY", StringComparison.Ordinal) || n.Contains("ACCESS_KEY", StringComparison.Ordinal)
                   || n.Contains("TOKEN", StringComparison.Ordinal) || n.Contains("SECRET", StringComparison.Ordinal)
                   || n.Contains("PASSWORD", StringComparison.Ordinal) || n.Contains("PASSWD", StringComparison.Ordinal);
        }

        /// <summary>
        /// Środowisko procesów weryfikacji: usunięte zmienne z sekretami (*_API_KEY, *TOKEN*, *SECRET*, *PASSWORD*…),
        /// APEX_DATA_DIR = katalog tymczasowy zadania. current: zmienne bieżącego procesu (podmiana w testach).
        /// </summary>
        public static Dictionary<string, string?> ScrubbedEnvironment(string apexDataDir, IDictionary? current = null)
        {
            var env = new Dictionary<string, string?>(StringComparer.Ordinal);
            current ??= Environment.GetEnvironmentVariables();
            foreach (DictionaryEntry kv in current)
            {
                string name = kv.Key?.ToString() ?? "";
                if (name.Length > 0 && IsSecretName(name)) env[name] = null;
            }
            env["APEX_DATA_DIR"] = apexDataDir;
            return env;
        }

        /// <summary>Uruchamia polecenie w oczyszczonym środowisku weryfikacji.</summary>
        public Task<ProcessResult> RunIsolatedAsync(CommandSpec cmd, IEnumerable<string> args, string dir, TimeSpan timeout, CancellationToken ct) =>
            ProcessRunner.RunAsync(cmd, args, dir, env: _env, timeout: timeout, ct: ct);

        /// <summary>Przywraca pakiety NuGet (poza piaskownicą agenta – agent kompiluje potem z --no-restore).</summary>
        public async Task<bool> RestoreAsync(string dir, CancellationToken ct)
        {
            var r = await RunIsolatedAsync(Need(), new[] { "restore", "tests/Apex.Tests/Apex.Tests.csproj", "-nologo" }, dir, TimeSpan.FromMinutes(_repo.BuildTimeoutMinutes), ct).ConfigureAwait(false);
            if (!r.Ok && !r.Cancelled) _log("warning", "dotnet restore: " + r.Tail(5));
            return r.Ok;
        }

        /// <summary>
        /// Kroki kompilacji weryfikacyjnej. Każdy krok dostaje właściwość globalną -p:AlgoPublish=false – nadpisuje ona csproj oraz
        /// Directory.Build.props/targets (także w projektach zależnych), więc kompilacja przed decyzją użytkownika NIGDY nie instaluje
        /// pliku .algo w cTrader (Dokumenty\cAlgo\Sources\Robots).
        /// </summary>
        public static List<(string name, string[] args)> BuildSteps(string dir)
        {
            var steps = new List<(string name, string[] args)>
            {
                ("Silnik, APEX Desktop, symulator i testy", new[] { "build", "tests/Apex.Tests/Apex.Tests.csproj", "-c", "Release", "-nologo" }),
                ("Backtester", new[] { "build", "tools/Apex.Backtester/Apex.Backtester.csproj", "-c", "Release", "-nologo" }),
            };
            foreach (var sln in new[] { "src/Platforms/cTrader/ApexBridge/ApexBridge.sln", "src/Platforms/cTrader/ApexTradingAI/ApexTradingAI.sln" })
                if (File.Exists(Path.Combine(dir, sln))) steps.Add(("cBot " + Path.GetFileNameWithoutExtension(sln), new[] { "build", sln, "-c", "Release", "-nologo" }));
            return steps.Select(s => (s.name, s.args.Concat(new[] { "-p:AlgoPublish=false" }).ToArray())).ToList();
        }

        public async Task<BuildResult> BuildAsync(string dir, CancellationToken ct)
        {
            var res = new BuildResult { Ok = true };
            foreach (var (name, args) in BuildSteps(dir))
            {
                _log("command", "▶ dotnet " + string.Join(" ", args));
                var r = await RunIsolatedAsync(Need(), args, dir, TimeSpan.FromMinutes(_repo.BuildTimeoutMinutes), ct).ConfigureAwait(false);
                res.DurationSec += r.DurationSec;
                var errs = r.Output.Concat(r.Errors).Where(l => l.Contains(": error ", StringComparison.Ordinal)).Select(l => l.Trim()).Distinct().Take(30).ToList();
                int warnings = r.Output.Count(l => l.Contains(": warning ", StringComparison.Ordinal));
                bool ok = r.Ok;
                res.Steps.Add($"{name}: {(ok ? "OK" : r.TimedOut ? "PRZEKROCZONY CZAS" : r.Cancelled ? "PRZERWANO" : "BŁĄD")} ({r.DurationSec:F0} s{(warnings > 0 ? $", ostrzeżeń {warnings}" : "")})");
                if (!ok)
                {
                    res.Ok = false;
                    res.TimedOut = r.TimedOut;
                    res.Cancelled = r.Cancelled;
                    res.Errors.AddRange(errs.Count > 0 ? errs : new List<string> { r.TimedOut ? "Przekroczony limit czasu kompilacji." : r.Cancelled ? "Przerwano." : r.Tail(10) });
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
            var r = await RunIsolatedAsync(Need(), args, dir, TimeSpan.FromMinutes(_repo.TestTimeoutMinutes), ct).ConfigureAwait(false);
            var res = ParseTrx(trx);
            res.DurationSec = r.DurationSec;
            res.TimedOut = r.TimedOut;
            res.Cancelled = r.Cancelled;
            if (!res.Ran || r.TimedOut || r.Cancelled)
            {
                // Przekroczenie czasu lub przerwanie = wynik niemiarodajny, nawet gdy plik TRX powstał.
                res.FailedNames.Add(r.TimedOut ? "Przekroczony limit czasu testów." : r.Cancelled ? "Testy przerwane." : "Brak wyników testów: " + r.Tail(6));
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
                var results = doc.Descendants(ns + "UnitTestResult").ToList();
                res.FailedNames = results
                    .Where(e => IsFailure((string?)e.Attribute("outcome")))
                    .Select(e => (string?)e.Attribute("testName") ?? "?").Take(50).ToList();

                // Pełne nazwy testów: klasa z definicji testu (TestMethod className), gdy nazwa wyniku jej nie zawiera.
                var classById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var ut in doc.Descendants(ns + "UnitTest"))
                {
                    string? id = (string?)ut.Attribute("id");
                    string? cls = (string?)ut.Element(ns + "TestMethod")?.Attribute("className");
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(cls)) classById[id!] = cls!;
                }
                foreach (var e in results)
                {
                    string name = ((string?)e.Attribute("testName") ?? "").Trim();
                    if (name.Length == 0) continue;
                    if (classById.TryGetValue((string?)e.Attribute("testId") ?? "", out var cls) && !name.StartsWith(cls + ".", StringComparison.Ordinal))
                        name = cls + "." + name;
                    // Skrócone nazwy przypadków testów parametrycznych mogą się powtarzać – kolejne wystąpienia numerujemy.
                    string key = name;
                    for (int i = 2; res.Outcomes.ContainsKey(key); i++) key = name + " [#" + i.ToString(CultureInfo.InvariantCulture) + "]";
                    res.Outcomes[key] = (string?)e.Attribute("outcome") ?? "";
                }
            }
            catch (System.Xml.XmlException) { }
            return res;

            static int Int(XElement e, string name) => int.TryParse((string?)e.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
        }

        private static bool IsFailure(string? outcome) => outcome is "Failed" or "Error" or "Timeout" or "Aborted";

        /// <summary>
        /// Porównanie testów kandydata z wersją bazową po pełnych nazwach (niezależnie od liczby testów): każdy test bazowy musi nadal
        /// istnieć, test zaliczany w wersji bazowej nie może zostać pominięty ani niezaliczony. Naruszenie = ustalenie blokujące.
        /// Chroni przed zastąpieniem testu strażniczego nowym, trywialnym testem albo wyłączeniem go (Skip w innej linii).
        /// </summary>
        public static List<AuditFinding> CompareTests(TestResult? baseline, TestResult? candidate)
        {
            var findings = new List<AuditFinding>();
            if (baseline == null || candidate == null || baseline.Outcomes.Count == 0 || !candidate.Ran) return findings;
            var missing = new List<string>();
            var skipped = new List<string>();
            var broken = new List<string>();
            foreach (var kv in baseline.Outcomes.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (!candidate.Outcomes.TryGetValue(kv.Key, out var now)) { missing.Add(kv.Key); continue; }
                if (kv.Value != "Passed" || now == "Passed") continue;
                if (IsFailure(now)) broken.Add(kv.Key);
                else skipped.Add(kv.Key); // NotExecuted, Inconclusive itp.
            }
            void Add(List<string> names, string rule, string what)
            {
                if (names.Count == 0) return;
                findings.Add(new AuditFinding
                {
                    Severity = FindingSeverity.Blocking,
                    Rule = rule,
                    File = "tests/",
                    Detail = $"{what} ({names.Count}): " + string.Join("; ", names.Take(10)) + (names.Count > 10 ? $"; … i {names.Count - 10} więcej" : ""),
                });
            }
            Add(missing, "Brak testów z wersji bazowej", "testy wersji bazowej nieobecne w wynikach kandydata");
            Add(skipped, "Wyłączenie testu", "testy zaliczane w wersji bazowej są teraz pomijane");
            Add(broken, "Test bazowy niezaliczony", "testy zaliczane w wersji bazowej są teraz niezaliczone");
            return findings;
        }

        /// <summary>Backtest portfela na profilu z kopii roboczej (ten sam silnik, te same koszty). Zwraca null, gdy brak danych.</summary>
        public async Task<(List<BacktestMetrics>? metrics, string message)> BacktestAsync(string dir, string outDir, CancellationToken ct)
        {
            var r = await RunBacktestAsync(dir, outDir, ct).ConfigureAwait(false);
            return (r.Metrics, r.Message);
        }

        /// <summary>Backtest portfela z rozróżnieniem przyczyny braku wyników (ustawienia/dane vs. błąd, czas, przerwanie).</summary>
        public async Task<BacktestRun> RunBacktestAsync(string dir, string outDir, CancellationToken ct)
        {
            if (!_repo.RunBacktests) return new BacktestRun { Outcome = BacktestOutcome.Disabled, Message = "Backtest wyłączony w ustawieniach." };
            string data = _repo.BacktestDataDir;
            if (string.IsNullOrWhiteSpace(data) || !Directory.Exists(data) || Directory.GetFiles(data, "*_H1.csv").Length == 0)
                return new BacktestRun { Outcome = BacktestOutcome.NoData, Message = "Brak danych historycznych (ustaw katalog danych z plikami *_H1.csv / *_M15.csv – patrz tools/data/prepare_data.py)." };
            string dll = Path.Combine(dir, "tools", "Apex.Backtester", "bin", "Release", "net8.0", "Apex.Backtester.dll");
            if (!File.Exists(dll)) return new BacktestRun { Outcome = BacktestOutcome.NotBuilt, Message = "Backtester nie został zbudowany." };
            Directory.CreateDirectory(outDir);
            string summary = Path.Combine(outDir, "podsumowanie.json");
            if (File.Exists(summary)) File.Delete(summary);
            var args = new[] { dll, "portfolio", "--data", data, "--profiles", Path.Combine(dir, "config", "profile_default.json"), "--out", outDir, "--summary", summary };
            _log("command", "▶ backtest portfela (okres badawczy 2005–2017 i odłożony 2018–2020.05)");
            var r = await RunIsolatedAsync(Need(), args, dir, TimeSpan.FromMinutes(_repo.BacktestTimeoutMinutes), ct).ConfigureAwait(false);
            if (r.Cancelled) return new BacktestRun { Outcome = BacktestOutcome.Cancelled, Message = "Backtest przerwany." };
            if (r.TimedOut) return new BacktestRun { Outcome = BacktestOutcome.TimedOut, Message = "Przekroczony czas backtestu." };
            if (!r.Ok) return new BacktestRun { Outcome = BacktestOutcome.Failed, Message = "Backtest nieudany: " + Short(r.Tail(3), 300) };
            if (!File.Exists(summary)) return new BacktestRun { Outcome = BacktestOutcome.Ok, Metrics = new List<BacktestMetrics>(), Message = "Profil bez aktywnych strategii – portfel pusty." };
            try
            {
                return new BacktestRun { Outcome = BacktestOutcome.Ok, Metrics = ParseSummary(File.ReadAllText(summary)), Message = "OK" };
            }
            catch (JsonException ex)
            {
                return new BacktestRun { Outcome = BacktestOutcome.Failed, Message = "Nieczytelne podsumowanie backtestu: " + Short(ex.Message, 200) };
            }
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

    /// <summary>
    /// Wyniki wersji bazowej (testy + backtest) zapamiętywane per commit – liczone raz. Zapisywany jest WYŁĄCZNIE wynik pełny
    /// i wiarygodny (kompilacja OK, testy wykonane bez przekroczenia czasu i przerwania, backtest wykonany albo pominięty z powodu
    /// ustawień/danych). Klucz: commit + katalog danych + włączenie backtestu + odcisk plików danych (nazwy, rozmiary, daty zmian).
    /// </summary>
    public sealed class BaselineCache
    {
        /// <summary>Wersja formatu wpisu – starsze wpisy (sprzed poprawek, także zapisane błędnie) są ignorowane.</summary>
        public const int CurrentVersion = 2;

        private readonly string _dir;

        public BaselineCache(string labDir)
        {
            _dir = Path.Combine(labDir, "bazowe");
            Directory.CreateDirectory(_dir);
        }

        public sealed class Entry
        {
            public int Version { get; set; }
            public string Commit { get; set; } = "";
            public DateTime TimeUtc { get; set; }
            public bool BuildOk { get; set; }
            public bool BuildTimedOut { get; set; }
            public TestResult? Tests { get; set; }
            public List<BacktestMetrics>? Backtest { get; set; }
            public string BacktestMessage { get; set; } = "";
            /// <summary>Backtest pominięty wyłącznie z powodu ustawień lub braku danych (dopuszczalne w pamięci podręcznej).</summary>
            public bool BacktestConfigSkip { get; set; }
            public string DataDir { get; set; } = "";
            public bool RunBacktests { get; set; } = true;
            public string DataFingerprint { get; set; } = "";
        }

        /// <summary>Nowy (pusty) wpis z kluczem wyliczonym teraz – przed uruchomieniem kompilacji, testów i backtestu.</summary>
        public static Entry NewEntry(string commit, string? dataDir, bool runBacktests) => new()
        {
            Version = CurrentVersion,
            Commit = commit,
            TimeUtc = DateTime.UtcNow,
            DataDir = dataDir ?? "",
            RunBacktests = runBacktests,
            DataFingerprint = Fingerprint(dataDir),
        };

        public Entry? Get(string commit, string dataDir, bool runBacktests = true)
        {
            var e = LabStore.ReadJson<Entry>(PathFor(commit));
            if (e == null || e.Version != CurrentVersion) return null;
            if (!string.Equals(e.DataDir, dataDir ?? "", StringComparison.OrdinalIgnoreCase) || e.RunBacktests != runBacktests) return null;
            if (!string.Equals(e.DataFingerprint, Fingerprint(dataDir), StringComparison.Ordinal)) return null;
            return IsComplete(e, out _) ? e : null;
        }

        /// <summary>Zapisuje wpis tylko, gdy jest pełny. Zwraca false, gdy wynik niepełny (nie zapisano).</summary>
        public bool Put(Entry e)
        {
            if (e.Version != CurrentVersion || !IsComplete(e, out _)) return false;
            LabStore.WriteJson(PathFor(e.Commit), e);
            return true;
        }

        /// <summary>Czy wynik wersji bazowej jest pełny i wiarygodny (może trafić do pamięci podręcznej). reason – przyczyna (PL).</summary>
        public static bool IsComplete(Entry e, out string reason)
        {
            reason = "";
            if (!e.BuildOk) reason = e.BuildTimedOut ? "przekroczony czas kompilacji" : "kompilacja nieudana";
            else if (e.Tests == null || !e.Tests.Ran) reason = "testy nie zostały wykonane";
            else if (e.Tests.TimedOut) reason = "przekroczony czas testów";
            else if (e.Tests.Cancelled) reason = "testy przerwane";
            else if (e.Backtest == null && !e.BacktestConfigSkip) reason = "backtest nieudany" + (e.BacktestMessage.Length > 0 ? " (" + e.BacktestMessage + ")" : "");
            return reason.Length == 0;
        }

        /// <summary>Usuwa wszystkie zapamiętane wyniki wersji bazowej. Zwraca liczbę usuniętych wpisów.</summary>
        public int Clear()
        {
            int n = 0;
            if (!Directory.Exists(_dir)) return 0;
            foreach (var f in Directory.GetFiles(_dir, "*.json").Concat(Directory.GetFiles(_dir, "*.tmp")))
            {
                try { File.Delete(f); if (f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) n++; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return n;
        }

        /// <summary>Odcisk danych do backtestu: nazwy, rozmiary i daty zmian plików CSV (zmiana danych = nowy wynik bazowy).</summary>
        public static string Fingerprint(string? dataDir)
        {
            if (string.IsNullOrWhiteSpace(dataDir) || !Directory.Exists(dataDir)) return "brak";
            var sb = new StringBuilder();
            try
            {
                foreach (var f in new DirectoryInfo(dataDir).GetFiles("*.csv").OrderBy(f => f.Name, StringComparer.Ordinal))
                    sb.Append(f.Name).Append('|').Append(f.Length.ToString(CultureInfo.InvariantCulture)).Append('|')
                      .Append(f.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
            catch (IOException) { return "błąd-odczytu-" + Guid.NewGuid().ToString("N"); }
            catch (UnauthorizedAccessException) { return "błąd-odczytu-" + Guid.NewGuid().ToString("N"); }
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).Substring(0, 32);
        }

        private string PathFor(string commit) => Path.Combine(_dir, Safe(commit) + ".json");

        private static string Safe(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray());
    }

    /// <summary>
    /// Zabezpieczenie zainstalowanych cBotów cTrader (Dokumenty\cAlgo\Sources\Robots\*.algo): skróty i kopia przed etapem agenta,
    /// porównanie po weryfikacji. Każda zmiana jest cofana z kopii; zmienione i nowe pliki trafiają do kwarantanny w katalogu
    /// zadania (nic nie jest bezpowrotnie usuwane). Foldery można podać (testy na Linuksie).
    /// </summary>
    public sealed class AlgoTripwire
    {
        private readonly List<string> _dirs;
        private readonly string _backupDir;
        private readonly Dictionary<string, (string hash, string backup)> _snapshot = new(StringComparer.Ordinal);

        public AlgoTripwire(IEnumerable<string> robotsDirs, string backupDir)
        {
            _dirs = robotsDirs.Where(d => !string.IsNullOrWhiteSpace(d)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _backupDir = backupDir;
        }

        /// <summary>Czy wykonano migawkę (są foldery do pilnowania).</summary>
        public bool Armed { get; private set; }

        public IReadOnlyList<string> Dirs => _dirs;

        /// <summary>Domyślne foldery cBotów cTrader – tylko Windows (Dokumenty użytkownika, także przekierowane np. do OneDrive).</summary>
        public static IEnumerable<string> DefaultRobotsDirs()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return Array.Empty<string>();
            var docs = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents"),
            };
            return docs.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => Path.Combine(d, "cAlgo", "Sources", "Robots"))
                       .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Migawka: skróty SHA-256 i kopia plików *.algo. Zwraca liczbę zabezpieczonych plików.</summary>
        public int Snapshot()
        {
            _snapshot.Clear();
            for (int i = 0; i < _dirs.Count; i++)
            {
                if (!Directory.Exists(_dirs[i])) continue;
                string backup = Path.Combine(_backupDir, i.ToString(CultureInfo.InvariantCulture));
                Directory.CreateDirectory(backup);
                foreach (var f in Directory.GetFiles(_dirs[i], "*.algo", SearchOption.TopDirectoryOnly))
                {
                    string copy = Path.Combine(backup, Path.GetFileName(f));
                    try
                    {
                        File.Copy(f, copy, true);
                        _snapshot[f] = (Hash(copy), copy);
                    }
                    catch (IOException)
                    {
                        // Plik zablokowany do kopiowania – zmianę nadal wykryjemy (skrót), ale bez możliwości przywrócenia.
                        _snapshot[f] = (TryHash(f), "");
                    }
                }
            }
            Armed = _dirs.Count > 0;
            return _snapshot.Count;
        }

        /// <summary>
        /// Porównuje stan z migawką i przywraca kopię. Zwraca listę zmian (PL); pusta = bez zmian. Zmienione i nowe pliki
        /// są przenoszone do quarantineDir (do wglądu), usunięte i zmienione – odtwarzane z kopii.
        /// </summary>
        public List<string> CheckAndRestore(string quarantineDir)
        {
            var changes = new List<string>();
            if (!Armed) return changes;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dir in _dirs)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, "*.algo", SearchOption.TopDirectoryOnly))
                {
                    seen.Add(f);
                    string name = Path.GetFileName(f);
                    if (!_snapshot.TryGetValue(f, out var snap))
                    {
                        changes.Add($"nowy plik {name}" + Quarantine(f, quarantineDir, move: true));
                        continue;
                    }
                    string now = TryHash(f);
                    if (now.Length > 0 && now == snap.hash) continue;
                    string q = Quarantine(f, quarantineDir, move: false);
                    changes.Add($"zmieniony plik {name}" + q + Restore(snap.backup, f));
                }
            }
            foreach (var kv in _snapshot)
                if (!seen.Contains(kv.Key))
                    changes.Add($"usunięty plik {Path.GetFileName(kv.Key)}" + Restore(kv.Value.backup, kv.Key));
            return changes;
        }

        private static string Quarantine(string file, string quarantineDir, bool move)
        {
            try
            {
                Directory.CreateDirectory(quarantineDir);
                string target = Path.Combine(quarantineDir, DateTime.UtcNow.ToString("HHmmssfff", CultureInfo.InvariantCulture) + "-" + Path.GetFileName(file));
                if (move) File.Move(file, target, true); else File.Copy(file, target, true);
                return move ? " (przeniesiony do kwarantanny zadania)" : "";
            }
            catch (IOException ex) { return " (nie udało się przenieść: " + ex.Message + ")"; }
            catch (UnauthorizedAccessException ex) { return " (nie udało się przenieść: " + ex.Message + ")"; }
        }

        private static string Restore(string backup, string target)
        {
            if (backup.Length == 0) return " – NIE udało się przywrócić (brak kopii – plik był zablokowany w chwili migawki)";
            try
            {
                File.Copy(backup, target, true);
                return " – przywrócono poprzednią wersję";
            }
            catch (IOException ex) { return " – NIE udało się przywrócić (" + ex.Message + "); kopia: " + backup; }
            catch (UnauthorizedAccessException ex) { return " – NIE udało się przywrócić (" + ex.Message + "); kopia: " + backup; }
        }

        private static string Hash(string path)
        {
            // Odczyt współdzielony – cTrader może mieć plik otwarty.
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(s));
        }

        /// <summary>Skrót pliku albo "" (nieczytelny – traktowany jak zmiana).</summary>
        private static string TryHash(string path)
        {
            try { return Hash(path); }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }
    }
}
