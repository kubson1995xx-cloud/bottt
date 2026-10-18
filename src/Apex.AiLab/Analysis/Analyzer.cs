using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Apex.AiLab.Lab;

namespace Apex.AiLab.Analysis
{
    /// <summary>
    /// Deterministyczna analiza (bez AI): skuteczność strategii na żywo vs. backtest, zmiany warunków rynkowych
    /// (reżim i zmienność D1), błędy wykonania. Wynik jest kontekstem dla agenta i podstawą wyzwalaczy.
    /// </summary>
    public sealed class Analyzer
    {
        private readonly DesktopClient _desktop;
        private readonly LabStore _store;

        public Analyzer(DesktopClient desktop, LabStore store)
        {
            _desktop = desktop;
            _store = store;
        }

        public int MinTradesForDegradation { get; set; } = 15;

        // ------------------------------------------------------------------ migawki rynku

        /// <summary>Zapisuje migawkę reżimów D1 dla skanowanych instrumentów wszystkich kont.</summary>
        public async Task<int> TakeSnapshotAsync(CancellationToken ct)
        {
            using var status = await _desktop.StatusAsync(ct).ConfigureAwait(false);
            if (status == null) return 0;
            int saved = 0;
            foreach (var acc in Accounts(status.RootElement))
            {
                string key = Str(acc, "key");
                var snap = new MarketSnapshot { TimeUtc = DateTime.UtcNow, Account = key };
                if (!acc.TryGetProperty("universe", out var uni) || uni.ValueKind != JsonValueKind.Array) continue;
                foreach (var u in uni.EnumerateArray().Where(x => x.TryGetProperty("selected", out var s) && s.ValueKind == JsonValueKind.True).Take(40))
                {
                    string sym = Str(u, "symbol");
                    using var detail = await _desktop.InstrumentAsync(key, sym, ct).ConfigureAwait(false);
                    if (detail == null) continue;
                    if (!detail.RootElement.TryGetProperty("d1", out var d1) || !d1.TryGetProperty("regime", out var reg) || reg.ValueKind != JsonValueKind.Object) continue;
                    snap.Entries.Add(new MarketSnapshotEntry
                    {
                        Symbol = sym,
                        AssetClass = Str(u, "assetClass"),
                        Tf = "D1",
                        Regime = Str(reg, "regime"),
                        TrendDirection = reg.TryGetProperty("trendDirection", out var td) && td.TryGetInt32(out int t) ? t : 0,
                        Adx = Num(reg, "adx"),
                        AtrPercentile = Num(reg, "atrPercentile"),
                        EfficiencyRatio = Num(reg, "efficiencyRatio"),
                    });
                }
                if (snap.Entries.Count > 0)
                {
                    _store.AddSnapshot(snap);
                    saved += snap.Entries.Count;
                }
            }
            return saved;
        }

        // ------------------------------------------------------------------ analiza

        public async Task<AnalysisReport> AnalyzeAsync(ScheduleSettings sched, CancellationToken ct)
        {
            var r = new AnalysisReport { Id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture), TimeUtc = DateTime.UtcNow };
            using var status = await _desktop.StatusAsync(ct).ConfigureAwait(false);
            r.DesktopReachable = status != null;
            if (status != null)
            {
                foreach (var acc in Accounts(status.RootElement))
                {
                    string key = Str(acc, "key");
                    r.Accounts.Add(key);
                    var engine = acc.TryGetProperty("engine", out var e) && e.ValueKind == JsonValueKind.Object ? e : default;
                    var expectations = Expectations(acc);
                    var suspended = new HashSet<string>(Strings(engine, "suspended"), StringComparer.OrdinalIgnoreCase); // klucze "Strategia:Klasa"
                    string csv = await _desktop.JournalCsvAsync(key, ct).ConfigureAwait(false) ?? "";
                    var trades = JournalTrade.ParseCsv(csv);
                    r.TotalTrades += trades.Count;
                    foreach (var g in trades.GroupBy(t => (t.Strategy, t.AssetClass)))
                    {
                        expectations.TryGetValue(g.Key.Strategy + ":" + g.Key.AssetClass, out var exp);
                        var (st, finding) = EvaluateStrategy(key, g.Key.Strategy, g.Key.AssetClass, g, exp.er, exp.status,
                            suspended.Contains(g.Key.Strategy + ":" + g.Key.AssetClass), MinTradesForDegradation);
                        r.Strategies.Add(st);
                        if (finding != null) r.Findings.Add(finding);
                    }
                    foreach (var s in Strings(engine, "suspended"))
                        r.Findings.Add(new AnalysisFinding { Kind = TriggerKind.StrategyDegradation, Level = "warning", Title = "Monitor wyników wstrzymał strategię", Detail = $"[{key}] {s}" });

                    var errors = Strings(engine, "recentErrors").ToList();
                    r.RecentErrors.AddRange(errors.Select(x => $"[{key}] {x}"));
                    int serious = errors.Count(x => !x.Contains("kalendarz", StringComparison.OrdinalIgnoreCase));
                    if (serious >= 3)
                        r.Findings.Add(new AnalysisFinding
                        {
                            Kind = TriggerKind.ExecutionErrors,
                            Level = "warning",
                            Title = "Powtarzające się błędy silnika lub wykonania",
                            Detail = $"[{key}] {serious} ostatnich błędów, np.: " + string.Join(" | ", errors.Take(3)),
                        });
                    if (Bool(engine, "maxDrawdownHit"))
                        r.Findings.Add(new AnalysisFinding { Kind = TriggerKind.StrategyDegradation, Level = "critical", Title = "Przekroczone maksymalne obsunięcie", Detail = $"[{key}] handel zatrzymany przez RiskEngine." });
                    else if (Bool(engine, "dailyLimitHit"))
                        r.Findings.Add(new AnalysisFinding { Kind = TriggerKind.StrategyDegradation, Level = "info", Title = "Dzienny limit straty osiągnięty", Detail = $"[{key}] nowe wejścia zablokowane do końca dnia." });
                }
            }
            else r.DataNote = "APEX Desktop jest niedostępny – analiza oparta wyłącznie na zapisanych migawkach rynku.";

            AnalyzeMarket(r);
            if (r.DesktopReachable && r.TotalTrades == 0)
                r.DataNote = "Brak zamkniętych transakcji – ocena skuteczności strategii na żywo nie jest jeszcze możliwa.";
            r.Markdown = BuildMarkdown(r);
            _store.SaveAnalysis(r);
            return r;
        }

        /// <summary>Liczba ostatnich transakcji, na których liczone są wszystkie miary strategii na żywo.</summary>
        public const int StatsWindow = 60;

        /// <summary>
        /// Statystyki strategii na żywo – liczba transakcji, skuteczność, E[R], górna granica i wynik netto liczone na TYM SAMYM oknie
        /// ostatnich ≤ 60 transakcji (łączna liczba i wynik podawane osobno). Zwraca też wniosek o pogorszeniu (albo null).
        /// </summary>
        public static (StrategyLiveStats stats, AnalysisFinding? finding) EvaluateStrategy(string account, string strategy, string assetClass,
            IEnumerable<JournalTrade> trades, double? backtestEr, string? status, bool suspended, int minTrades)
        {
            var all = trades.OrderBy(t => t.ExitUtc).ToList();
            var window = all.Count > StatsWindow ? all.GetRange(all.Count - StatsWindow, StatsWindow) : all;
            var rs = window.Select(t => t.R).ToList();
            double mean = rs.Count > 0 ? rs.Average() : 0;
            double sd = rs.Count > 1 ? Math.Sqrt(rs.Sum(x => (x - mean) * (x - mean)) / (rs.Count - 1)) : 0;
            double upper = rs.Count > 1 ? mean + 1.645 * sd / Math.Sqrt(rs.Count) : double.NaN;
            var st = new StrategyLiveStats
            {
                Account = account,
                Strategy = strategy,
                AssetClass = assetClass,
                Status = status ?? "",
                Trades = window.Count,
                WinRate = window.Count > 0 ? window.Count(t => t.NetProfit > 0) / (double)window.Count : 0,
                ExpectancyR = mean,
                UpperBoundR = upper,
                NetProfit = window.Sum(t => t.NetProfit),
                BacktestExpectancyR = backtestEr,
                Suspended = suspended,
                TotalTrades = all.Count,
                TotalNetProfit = all.Sum(t => t.NetProfit),
            };
            string sample = st.TotalTrades > st.Trades ? $"{st.Trades} ostatnich transakcji (z {st.TotalTrades} łącznie)" : $"{st.Trades} transakcji";
            AnalysisFinding? finding = null;
            if (st.Trades >= minTrades && !double.IsNaN(upper) && upper < 0)
                finding = new AnalysisFinding
                {
                    Kind = TriggerKind.StrategyDegradation,
                    Level = "critical",
                    Title = $"Strategia {st.Strategy}/{st.AssetClass} traci na żywo",
                    Detail = $"{sample}, średnio {mean:F3}R (górna granica 95%: {upper:F3}R < 0), skuteczność {st.WinRate:P0}" + (backtestEr.HasValue ? $"; backtest OOS {backtestEr.Value:F3}R" : "") + ".",
                };
            else if (st.Trades >= minTrades && backtestEr.HasValue && sd > 0 && mean < backtestEr.Value - 2 * sd / Math.Sqrt(rs.Count))
                finding = new AnalysisFinding
                {
                    Kind = TriggerKind.StrategyDegradation,
                    Level = "warning",
                    Title = $"Strategia {st.Strategy}/{st.AssetClass} wyraźnie poniżej backtestu",
                    Detail = $"Na żywo {mean:F3}R vs backtest OOS {backtestEr.Value:F3}R ({sample}).",
                };
            return (st, finding);
        }

        /// <summary>Porównanie ostatniej migawki z migawkami sprzed 5–30 dni: zmiana rodziny reżimu i zmienności.</summary>
        public void AnalyzeMarket(AnalysisReport r)
        {
            var snaps = _store.Snapshots();
            if (snaps.Count == 0) { r.MarketSummary.Add("Brak migawek rynku – historia warunków rynkowych dopiero się zbiera."); return; }
            foreach (var acc in snaps.Select(s => s.Account).Distinct())
            {
                var mine = snaps.Where(s => s.Account == acc).OrderBy(s => s.TimeUtc).ToList();
                var latest = mine[^1];
                var dist = latest.Entries.GroupBy(e => Family(e.Regime)).Select(g => $"{g.Key}: {g.Count()}").ToList();
                double? medAtr = Median(latest.Entries.Select(e => e.AtrPercentile));
                r.MarketSummary.Add($"[{acc}] {latest.TimeUtc:yyyy-MM-dd HH:mm} UTC – D1: {string.Join(", ", dist)}; mediana percentyla ATR {(medAtr.HasValue ? medAtr.Value.ToString("P0", CultureInfo.InvariantCulture) : "—")}");
                var reference = mine.Where(s => s.TimeUtc <= latest.TimeUtc.AddDays(-5) && s.TimeUtc >= latest.TimeUtc.AddDays(-30)).ToList();
                if (reference.Count == 0) { r.MarketSummary.Add($"[{acc}] Za krótka historia migawek (potrzeba ≥ 5 dni) do wykrywania zmian."); continue; }
                int compared = 0, changed = 0;
                var changedList = new List<string>();
                foreach (var e in latest.Entries)
                {
                    var past = reference.SelectMany(s => s.Entries.Where(x => x.Symbol == e.Symbol)).ToList();
                    if (past.Count == 0) continue;
                    compared++;
                    string pastFamily = past.GroupBy(x => Family(x.Regime)).OrderByDescending(g => g.Count()).First().Key;
                    if (pastFamily != Family(e.Regime)) { changed++; changedList.Add($"{e.Symbol}: {pastFamily} → {Family(e.Regime)}"); }
                }
                double? pastAtr = Median(reference.SelectMany(s => s.Entries).Select(x => x.AtrPercentile));
                if (compared >= 3 && changed / (double)compared >= 0.3)
                    r.Findings.Add(new AnalysisFinding
                    {
                        Kind = TriggerKind.MarketChange,
                        Level = "warning",
                        Title = "Zmiana warunków rynkowych (reżim D1)",
                        Detail = $"[{acc}] {changed}/{compared} instrumentów zmieniło reżim względem ostatnich 5–30 dni: " + string.Join("; ", changedList.Take(8)),
                    });
                if (medAtr.HasValue && pastAtr.HasValue && medAtr.Value - pastAtr.Value >= 0.3)
                    r.Findings.Add(new AnalysisFinding
                    {
                        Kind = TriggerKind.MarketChange,
                        Level = "warning",
                        Title = "Wyraźny wzrost zmienności",
                        Detail = $"[{acc}] mediana percentyla ATR(14) D1: {pastAtr.Value:P0} → {medAtr.Value:P0}.",
                    });
            }
        }

        public static string Family(string regime) => regime switch
        {
            "StrongUptrend" or "WeakUptrend" => "trend wzrostowy",
            "StrongDowntrend" or "WeakDowntrend" => "trend spadkowy",
            "Range" => "konsolidacja",
            "Chaotic" => "chaos",
            _ => "nieokreślony",
        };

        private static string BuildMarkdown(AnalysisReport r)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Analiza APEX {r.TimeUtc:yyyy-MM-dd HH:mm} UTC");
            if (r.DataNote.Length > 0) sb.AppendLine().AppendLine("> " + r.DataNote);
            sb.AppendLine().AppendLine("## Wnioski");
            if (r.Findings.Count == 0) sb.AppendLine("- Brak istotnych problemów wykrytych automatycznie.");
            foreach (var f in r.Findings) sb.AppendLine($"- **[{f.Level}] {f.Title}** – {f.Detail}");
            sb.AppendLine().AppendLine($"## Strategie na żywo (miary z ostatnich ≤ {StatsWindow} transakcji)");
            if (r.Strategies.Count == 0) sb.AppendLine("- Brak zamkniętych transakcji.");
            foreach (var s in r.Strategies)
                sb.AppendLine($"- {s.Strategy}/{s.AssetClass} [{s.Account}] status {s.Status}: n={s.Trades}" + (s.TotalTrades > s.Trades ? $" (z {s.TotalTrades} łącznie)" : "") +
                              $", WR {s.WinRate:P0}, E={s.ExpectancyR:F3}R, górna granica {s.UpperBoundR:F3}R, wynik {s.NetProfit:F2}" +
                              (s.TotalTrades > s.Trades ? $" (łącznie {s.TotalNetProfit:F2})" : "") +
                              (s.BacktestExpectancyR.HasValue ? $", backtest OOS {s.BacktestExpectancyR.Value:F3}R" : "") + (s.Suspended ? " – WSTRZYMANA" : ""));
            sb.AppendLine().AppendLine("## Rynek");
            foreach (var m in r.MarketSummary) sb.AppendLine("- " + m);
            if (r.RecentErrors.Count > 0)
            {
                sb.AppendLine().AppendLine("## Ostatnie błędy");
                foreach (var e in r.RecentErrors.Take(15)) sb.AppendLine("- " + e);
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ pomocnicze JSON

        private static IEnumerable<JsonElement> Accounts(JsonElement root)
        {
            if (root.TryGetProperty("data", out var data) && data.TryGetProperty("accounts", out var accs) && accs.ValueKind == JsonValueKind.Array)
                foreach (var a in accs.EnumerateArray()) yield return a;
        }

        /// <summary>Oczekiwany wynik z backtestu (E OOS) wg strategii i klasy – z pola "evidence" profilu.</summary>
        private static Dictionary<string, (double? er, string? status)> Expectations(JsonElement acc)
        {
            var d = new Dictionary<string, (double?, string?)>(StringComparer.OrdinalIgnoreCase);
            if (!acc.TryGetProperty("profile", out var prof) || prof.ValueKind != JsonValueKind.Array) return d;
            foreach (var p in prof.EnumerateArray())
            {
                string key = Str(p, "strategy") + ":" + Str(p, "assetClass");
                var m = Regex.Match(Str(p, "evidence"), @"E=([-+]?\d+(?:\.\d+)?)R");
                double? er = m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
                d[key] = (er, Str(p, "status"));
            }
            return d;
        }

        private static string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";

        private static double? Num(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

        private static bool Bool(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        private static IEnumerable<string> Strings(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) yield break;
            foreach (var x in v.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) yield return x.GetString() ?? "";
        }

        private static double? Median(IEnumerable<double?> values)
        {
            var l = values.Where(v => v.HasValue && !double.IsNaN(v.Value)).Select(v => v!.Value).OrderBy(v => v).ToList();
            if (l.Count == 0) return null;
            return l.Count % 2 == 1 ? l[l.Count / 2] : (l[l.Count / 2 - 1] + l[l.Count / 2]) / 2;
        }
    }
}
