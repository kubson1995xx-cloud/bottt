using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Apex.Backtester.Data;
using Apex.Backtester.Sim;
using Apex.Core.Adaptive;
using Apex.Core.Engine;
using Apex.Core.Model;
using Apex.Core.Performance;
using Apex.Core.Strategies;

namespace Apex.Backtester.Research
{
    /// <summary>Kombinacja parametrów strategii w siatce (indeksy wymiarów pozwalają wyznaczyć sąsiadów).</summary>
    public sealed class Combo
    {
        public StrategyId Strategy { get; init; }
        public int[] Index { get; init; } = Array.Empty<int>();
        public string Label { get; init; } = "";
        public Func<AssetClass, ProfileEntry> Build { get; init; } = null!;
        public bool IsDefault { get; init; }
        public string Key => Strategy + ":" + string.Join(",", Index);
    }

    public sealed class ComboTrades
    {
        public Combo Combo { get; init; } = null!;
        public AssetClass AssetClass { get; init; }
        public List<TradeRecord> Trades { get; } = new();
        public List<IsolatedRun> Runs { get; } = new();
    }

    /// <summary>
    /// Offline bounded research with independent IS/OOS engines; see docs/BACKTESTER.md.
    /// Historical results and previously reused holdout periods do not validate these semantics.
    ///     Wybór parametrów w IS: t-statystyka średniego R wygładzona po sąsiadach w siatce (preferencja "płaskowyżu").
    ///  4. Kryteria włączenia (wszystkie muszą być spełnione): n OOS ≥ 60, E[R] OOS ≥ +0,05, t OOS ≥ 1,65,
    ///     ≥ 55% zyskownych lat OOS, ≥ 60% kombinacji siatki z dodatnim E[R] w całym okresie badawczym,
    ///     E[R] &gt; 0 przy kosztach ×1,5 dla wybranych parametrów.
    ///  5. Portfel z wybranymi strategiami i pełnym zarządzaniem ryzykiem: okres badawczy + jednorazowy test holdout.
    ///  6. Monte Carlo (bootstrap transakcji) dla obsunięć.
    /// </summary>
    public sealed class ResearchPipeline
    {
        public DateTime ResearchFrom { get; }
        public DateTime ResearchTo { get; }
        public int TrainingYears { get; init; } = 4;
        public int MaxCombos { get; init; } = 100;
        public int MaxRuns { get; init; } = 1000;
        public int Parallelism { get; init; } = 1;
        public string[] RequestedSymbols { get; init; } = Array.Empty<string>();
        private int _runCount;
        public int RunCount => _runCount;
        public List<object> MachineResults { get; } = new();
        private readonly ConcurrentBag<IsolatedRun> _isolatedRuns = new();
        public IReadOnlyList<IsolatedRun> IsolatedRuns => _isolatedRuns.OrderBy(r => r.RunId).ToArray();
        private IEnumerable<int> OosYears => Enumerable.Range(ResearchFrom.Year + TrainingYears, ResearchTo.Year - ResearchFrom.Year - TrainingYears);
        private const double IsolatedEquity = 1_000_000;
        public CostScenario BaseCost { get; init; } = CostScenario.Base;

        private readonly DataLoader _loader;
        private readonly string _outDir;
        private readonly string? _only;
        private readonly string _tag;
        private readonly StringBuilder _log = new();

        public ResearchPipeline(DataLoader loader, string outDir, string? only, string? tag, DateTime from, DateTime to)
        {
            ResearchFrom = from; ResearchTo = to;
            _loader = loader;
            _outDir = outDir;
            _only = only;
            _tag = string.IsNullOrEmpty(tag) ? "" : "_" + tag;
        }

        public static EngineConfig IsolatedConfig()
        {
            var c = new EngineConfig { Label = "APEX", IsBacktest = true, UseHealthMonitor = false, MaxNewPositionsPerCycle = 5 };
            c.Risk.RiskPerTradePct = 0.1;
            c.Risk.MaxDailyLossPct = 10;
            c.Risk.MaxDrawdownPct = 30;
            c.Risk.MaxOpenPositions = 20;
            c.Risk.MaxPositionsPerAssetClass = 20;
            c.Risk.MaxSameCurrencyExposure = 20;
            c.Risk.MaxEffectiveLeverage = 30;
            c.Risk.ProjectWorstCase = false;
            c.Risk.CloseAllOnMaxDrawdown = false;
            c.Risk.MaxCorrelation = 1.0;
            c.Scoring = new Apex.Core.Scanner.ScoringSettings { MinScore = 0 };
            return c;
        }

        public static EngineConfig PortfolioConfig(double minScore)
        {
            var c = new EngineConfig { Label = "APEX", IsBacktest = true, UseHealthMonitor = true };
            c.Scoring = new Apex.Core.Scanner.ScoringSettings { MinScore = minScore };
            return c;
        }

        private CostScenario Stress(CostScenario factor) => new()
        {
            Name = BaseCost.Name + " * " + factor.Name, SpreadMult = BaseCost.SpreadMult * factor.SpreadMult,
            CommissionMult = BaseCost.CommissionMult * factor.CommissionMult,
            SlippageMult = BaseCost.SlippageMult * factor.SlippageMult, FinancingMult = BaseCost.FinancingMult * factor.FinancingMult,
        };

        private void Log(string s)
        {
            Console.WriteLine(s);
            lock (_log) _log.AppendLine(s);
        }

        // ------------------------------------------------------------------ siatki parametrów

        public static List<Combo> Grid(StrategyId id)
        {
            var list = new List<Combo>();
            var ci = CultureInfo.InvariantCulture;
            switch (id)
            {
                case StrategyId.TrendPullback:
                    {
                        double[] stops = { 1.5, 2.0, 2.5 };
                        double[] tps = { 1.5, 2.0, 3.0 };
                        int[] looks = { 4, 6, 8 };
                        for (int a = 0; a < 3; a++) for (int b = 0; b < 3; b++) for (int c = 0; c < 3; c++)
                        {
                            double st = stops[a], tp = tps[b]; int lk = looks[c];
                            list.Add(new Combo
                            {
                                Strategy = id, Index = new[] { a, b, c }, IsDefault = a == 1 && b == 1 && c == 1,
                                Label = string.Format(ci, "SL {0}ATR, TP {1}R, korekta {2}", st, tp, lk),
                                Build = cls => new ProfileEntry
                                {
                                    AssetClass = cls, Strategy = id,
                                    TrendPullback = new TrendPullbackParams
                                    {
                                        StopAtr = st, PullbackLookback = lk,
                                        Exit = new ExitPlan { TakeProfitR = tp, BreakEvenAtR = 1.0, ExitOnTrendReversal = true, MaxBarsHeld = 120 },
                                    },
                                },
                            });
                        }
                        break;
                    }
                case StrategyId.DonchianBreakout:
                    {
                        int[] ch = { 20, 40, 55 };
                        double[] stops = { 2.0, 3.0 };
                        double[] trails = { 2.5, 3.5 };
                        int[] modes = { 1, 2 };
                        for (int a = 0; a < 3; a++) for (int b = 0; b < 2; b++) for (int c = 0; c < 2; c++) for (int d = 0; d < 2; d++)
                        {
                            int chn = ch[a]; double st = stops[b], tr = trails[c]; int md = modes[d];
                            list.Add(new Combo
                            {
                                Strategy = id, Index = new[] { a, b, c, d }, IsDefault = a == 0 && b == 1 && c == 1 && d == 1,
                                Label = string.Format(ci, "kanał {0}, SL {1}ATR, trailing {2}ATR, filtr {3}", chn, st, tr, md),
                                Build = cls => new ProfileEntry
                                {
                                    AssetClass = cls, Strategy = id,
                                    Breakout = new DonchianBreakoutParams
                                    {
                                        Channel = chn, ExitChannel = Math.Max(10, chn / 2), StopAtr = st, FilterMode = md,
                                        Exit = new ExitPlan { TakeProfitR = null, TrailStartR = 1.0, TrailAtrMultiple = tr },
                                    },
                                },
                            });
                        }
                        break;
                    }
                case StrategyId.MeanReversion:
                    {
                        double[] ks = { 2.0, 2.5 };
                        double[] stops = { 1.0, 1.5, 2.0 };
                        double[] rsis = { 25, 30 };
                        int[] bars = { 12, 24 };
                        for (int a = 0; a < 2; a++) for (int b = 0; b < 3; b++) for (int c = 0; c < 2; c++) for (int d = 0; d < 2; d++)
                        {
                            double k = ks[a], st = stops[b], rl = rsis[c]; int mb = bars[d];
                            list.Add(new Combo
                            {
                                Strategy = id, Index = new[] { a, b, c, d }, IsDefault = a == 0 && b == 1 && c == 1 && d == 1,
                                Label = string.Format(ci, "BB {0}, SL {1}ATR, RSI {2}/{3}, max {4} świec", k, st, rl, 100 - rl, mb),
                                Build = cls => new ProfileEntry
                                {
                                    AssetClass = cls, Strategy = id,
                                    MeanReversion = new MeanReversionParams
                                    {
                                        BbK = k, StopAtr = st, RsiOversold = rl, RsiOverbought = 100 - rl,
                                        Exit = new ExitPlan { TakeProfitR = null, MaxBarsHeld = mb },
                                    },
                                },
                            });
                        }
                        break;
                    }
                case StrategyId.MomentumImpulse:
                    {
                        double[] imp = { 1.5, 1.8, 2.2 };
                        double[] trails = { 2.0, 3.0 };
                        int[] bars = { 24, 48 };
                        for (int a = 0; a < 3; a++) for (int b = 0; b < 2; b++) for (int c = 0; c < 2; c++)
                        {
                            double im = imp[a], tr = trails[b]; int mb = bars[c];
                            list.Add(new Combo
                            {
                                Strategy = id, Index = new[] { a, b, c }, IsDefault = a == 1 && b == 0 && c == 1,
                                Label = string.Format(ci, "impuls {0}ATR, trailing {1}ATR, max {2} świec", im, tr, mb),
                                Build = cls => new ProfileEntry
                                {
                                    AssetClass = cls, Strategy = id,
                                    Momentum = new MomentumImpulseParams
                                    {
                                        ImpulseAtr = im,
                                        Exit = new ExitPlan { TakeProfitR = null, BreakEvenAtR = 1.0, TrailStartR = 1.0, TrailAtrMultiple = tr, MaxBarsHeld = mb },
                                    },
                                },
                            });
                        }
                        break;
                    }
                case StrategyId.DailyTrend:
                    {
                        int[] ch = { 20, 55, 100 };
                        double[] stops = { 2.0, 3.0 };
                        double[] trails = { 3.0, 5.0 };
                        int[] modes = { 0, 1 };
                        for (int a = 0; a < 3; a++) for (int b = 0; b < 2; b++) for (int c = 0; c < 2; c++) for (int d = 0; d < 2; d++)
                        {
                            int chn = ch[a]; double st = stops[b], tr = trails[c]; int md = modes[d];
                            list.Add(new Combo
                            {
                                Strategy = id, Index = new[] { a, b, c, d }, IsDefault = a == 1 && b == 0 && c == 0 && d == 1,
                                Label = string.Format(ci, "D1 kanał {0}, SL {1}ATR, trailing {2}ATR, filtr {3}", chn, st, tr, md),
                                Build = cls =>
                                {
                                    var p = DailyTrendStrategy.Defaults;
                                    return new ProfileEntry
                                    {
                                        AssetClass = cls, Strategy = id,
                                        DailyTrend = new DonchianBreakoutParams
                                        {
                                            SignalTf = Tf.D1, FilterTf = Tf.D1, Channel = chn, ExitChannel = Math.Max(10, chn * 2 / 5), StopAtr = st, FilterMode = md,
                                            FailedBreakoutLookback = 0, MinBodyFraction = p.MinBodyFraction,
                                            Exit = new ExitPlan { TakeProfitR = null, TrailStartR = 1.0, TrailAtrMultiple = tr },
                                        },
                                    };
                                },
                            });
                        }
                        break;
                    }
                case StrategyId.IndexDipBuy:
                    {
                        double[] rsi = { 5, 10, 15 };
                        int[] sma = { 100, 200 };
                        double[] stops = { 2.0, 3.0 };
                        int[] bars = { 5, 10 };
                        for (int a = 0; a < 3; a++) for (int b = 0; b < 2; b++) for (int c = 0; c < 2; c++) for (int d = 0; d < 2; d++)
                        {
                            double r = rsi[a]; int sm = sma[b]; double st = stops[c]; int mb = bars[d];
                            list.Add(new Combo
                            {
                                Strategy = id, Index = new[] { a, b, c, d }, IsDefault = a == 1 && b == 1 && c == 1 && d == 0,
                                Label = string.Format(ci, "RSI2<{0}, SMA{1}, SL {2}ATR, max {3} dni", r, sm, st, mb),
                                Build = cls => new ProfileEntry
                                {
                                    AssetClass = cls, Strategy = id,
                                    DipBuy = new IndexDipBuyParams { RsiEntry = r, TrendSma = sm, StopAtr = st, Exit = new ExitPlan { TakeProfitR = null, MaxBarsHeld = mb } },
                                },
                            });
                        }
                        break;
                    }
            }
            return list;
        }

        public static IEnumerable<AssetClass> ClassesFor(StrategyId id) => id == StrategyId.IndexDipBuy
            ? new[] { AssetClass.Index }
            : new[] { AssetClass.Forex, AssetClass.Metal, AssetClass.Energy, AssetClass.Index, AssetClass.Bond, AssetClass.Commodity };

        // ------------------------------------------------------------------ uruchamianie izolowane

        private List<string> SymbolsOf(AssetClass c) => Catalog.ByClass(c).Select(d => d.Symbol).Where(s => _loader.Exists(s) && (RequestedSymbols.Length == 0 || RequestedSymbols.Contains(s))).ToList();

        public IsolatedRun RunIsolated(ProfileEntry entry, string symbol, DateTime from, DateTime to, CostScenario cost,
            Combo? combo = null, string purpose = "isolated", int? foldYear = null)
        {
            int runId = System.Threading.Interlocked.Increment(ref _runCount);
            if (runId > MaxRuns)
                throw new InvalidOperationException($"Research run budget exceeded ({MaxRuns}); no successful report");
            var sd = _loader.Load(symbol);
            var res = BacktestRunner.Run(new RunSpec
            {
                Symbols = new List<SymbolData> { sd },
                Profile = new StrategyProfile { Version = "research", Entries = new List<ProfileEntry> { entry } },
                ConfigFactory = IsolatedConfig,
                Cost = cost,
                From = from,
                To = to,
                StartEquity = IsolatedEquity,
                Fx = _loader.Fx,
            });
            var run = new IsolatedRun { RunId = runId, Symbol = symbol, Entry = entry,
                ComboKey = combo?.Key ?? entry.Strategy.ToString(), ComboLabel = combo?.Label ?? "explicit entry (see entry parameters)",
                From = from, To = to, Cost = cost, Purpose = purpose, FoldYear = foldYear, Account = res,
                EvidenceFile = $"isolated{_tag}/run-{runId:D6}.json" };
            // Persist every completed account, including IS diagnostics and forced/partial boundary cashflows.
            Directory.CreateDirectory(Path.Combine(_outDir, $"isolated{_tag}"));
            var jsonOptions = DataManifest.JsonOptions;
            jsonOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals;
            File.WriteAllText(Path.Combine(_outDir, run.EvidenceFile), System.Text.Json.JsonSerializer.Serialize(run.ToReport(), jsonOptions));
            _isolatedRuns.Add(run);
            return run;
        }

        private ComboTrades RunComboClass(Combo combo, AssetClass cls, DateTime from, DateTime to, CostScenario cost,
            string purpose, int? foldYear = null)
        {
            var ct = new ComboTrades { Combo = combo, AssetClass = cls };
            var bag = new ConcurrentBag<IsolatedRun>();
            Parallel.ForEach(SymbolsOf(cls), new ParallelOptions { MaxDegreeOfParallelism = Parallelism }, sym =>
            {
                bag.Add(RunIsolated(combo.Build(cls), sym, from, to, cost, combo, purpose, foldYear));
            });
            ct.Runs.AddRange(bag.OrderBy(r => r.RunId));
            ct.Trades.AddRange(ct.Runs.SelectMany(r => r.NaturalTrades).OrderBy(t => t.EntryTimeUtc));
            return ct;
        }

        // ------------------------------------------------------------------ statystyki pomocnicze

        public static (int n, double mean, double t) RStats(IEnumerable<TradeRecord> trades)
        {
            var r = trades.Select(t => t.RMultiple).ToList();
            int n = r.Count;
            if (n < 2) return (n, n == 1 ? r[0] : 0, 0);
            double m = r.Average();
            double sd = Math.Sqrt(r.Sum(x => (x - m) * (x - m)) / (n - 1));
            return (n, m, sd > 0 ? m / (sd / Math.Sqrt(n)) : 0);
        }


        // ------------------------------------------------------------------ główny proces

        public sealed class StrategyClassResult
        {
            public StrategyId Strategy;
            public AssetClass AssetClass;
            public int GridSize;
            public double PctCombosPositive;
            public double MedianComboR;
            public (int n, double mean, double t) Wf;
            public (int n, double mean, double t) DefaultOos;
            public int OosYearsPositive;
            public int OosYearsTotal;
            public Combo Chosen = null!;
            public (int n, double mean, double t) ChosenFull;
            public (int n, double mean, double t) ChosenElevated;
            public (int n, double mean, double t) ChosenDouble;
            public PerformanceStats WfStats = new();
            public bool Enabled;
            public List<string> FailedCriteria = new();
            public Dictionary<int, (int n, double mean)> OosByYear = new();
            public List<string> WfChoices = new();
            public List<IsolatedRun> SelectedOosRuns = new();
        }

        public int Run()
        {
            var sw = Stopwatch.StartNew();
            Log($"# APEX – badanie strategii ({DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC)");
            Log($"Evaluation [{ResearchFrom:o}, {ResearchTo:o}); {TrainingYears} years IS then independent yearly OOS. No untouched holdout claimed. Cost assumptions, not broker fees.");
            var results = new List<StrategyClassResult>();
            var strategies = new[] { StrategyId.TrendPullback, StrategyId.DonchianBreakout, StrategyId.MeanReversion, StrategyId.MomentumImpulse, StrategyId.IndexDipBuy, StrategyId.DailyTrend };
            foreach (var sid in strategies)
            {
                foreach (var cls in ClassesFor(sid))
                {
                    if (_only != null && !_only.Split(',').Contains(cls.ToString()) && !_only.Split(',').Contains(sid.ToString())) continue;
                    if (SymbolsOf(cls).Count == 0) continue;
                    var r = EvaluateStrategyClass(sid, cls);
                    results.Add(r);
                    Log($"[{sw.Elapsed:hh\\:mm\\:ss}] {sid}/{cls}: WF OOS n={r.Wf.n} E={r.Wf.mean:F3}R t={r.Wf.t:F2} | lata+ {r.OosYearsPositive}/{r.OosYearsTotal} | siatka+ {r.PctCombosPositive:P0} | x1.5 E={r.ChosenElevated.mean:F3} => {(r.Enabled ? "offline criteria met (no activation)" : "rejected: " + string.Join("; ", r.FailedCriteria))}");
                }
            }
            if (results.Count == 0) throw new InvalidOperationException("No applicable strategy/class for requested symbols/filter");
            WriteResearchReport(results);
            var profile = BuildProfile(results);
            File.WriteAllText(Path.Combine(_outDir, $"research_candidates{_tag}.json"), profile.ToJson());
            Log($"Profil zapisany: {profile.Entries.Count(e => e.Enabled)} aktywnych par strategia/klasa.");
            // No automatic portfolio/holdout sweep or production promotion. Explicit portfolio command only.
            File.WriteAllText(Path.Combine(_outDir, $"research_log{_tag}.txt"), _log.ToString());
            Log($"Zakończono w {sw.Elapsed}.");
            return 0;
        }

        /// <summary>Portfel z połączenia profili z kilku rund (wpisy włączone w którejkolwiek rundzie).</summary>
        /// <summary>Opcjonalny plik JSON z podsumowaniem portfela (do automatycznych porównań, np. moduł AI Lab).</summary>
        public string? SummaryPath { get; set; }
        private readonly List<object> _portfolioSummary = new();

        public int RunPortfolioOnly(IEnumerable<string> profilePaths)
        {
            var merged = new StrategyProfile { Version = "merged", Entries = new List<ProfileEntry>() };
            foreach (var path in profilePaths)
            {
                string text = File.ReadAllText(path);
                var p = StrategyProfile.FromJson(text);
                MachineResults.Add(new { profilePath = path, sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant(), profile = p });
                merged.Entries.AddRange(p.Entries);
                Log($"Profil {Path.GetFileName(path)}: {p.Entries.Count(e => e.Enabled)} włączonych wpisów.");
            }
            RunPortfolio(merged);
            MachineResults.AddRange(_portfolioSummary);
            File.WriteAllText(Path.Combine(_outDir, $"portfolio_log{_tag}.txt"), _log.ToString());
            if (!string.IsNullOrWhiteSpace(SummaryPath))
                File.WriteAllText(SummaryPath!, System.Text.Json.JsonSerializer.Serialize(new { profile = merged.Version, enabledEntries = merged.Entries.Count(e => e.Enabled), runs = _portfolioSummary },
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
            return 0;
        }

        private StrategyClassResult EvaluateStrategyClass(StrategyId sid, AssetClass cls)
        {
            var grid = Grid(sid).OrderByDescending(c => c.IsDefault).Take(MaxCombos).ToList();
            var res = new StrategyClassResult { Strategy = sid, AssetClass = cls, GridSize = grid.Count };
            var defaultOos = new List<TradeRecord>();

            // Walk-forward.
            var oos = new List<TradeRecord>();
            var chosenCount = new Dictionary<string, int>();
            foreach (int y in OosYears)
            {
                var isFrom = DateTime.SpecifyKind(new DateTime(y - TrainingYears, 1, 1), DateTimeKind.Utc);
                var isTo = DateTime.SpecifyKind(new DateTime(y, 1, 1), DateTimeKind.Utc);
                var oosTo = DateTime.SpecifyKind(new DateTime(y + 1, 1, 1), DateTimeKind.Utc);
                // Each call creates a fresh world/engine/store. No full-history trades or OOS state enter IS.
                var training = grid.Select(c => RunComboClass(c, cls, isFrom, isTo, BaseCost, "training-is", y)).ToList();
                var best = WalkForward.Select(training, isFrom, isTo);
                if (best == null)
                {
                    res.WfChoices.Add($"{y}: no eligible IS evidence; OOS not selected");
                    res.OosByYear[y] = (0, 0);
                    continue;
                }
                chosenCount[best.Key] = chosenCount.TryGetValue(best.Key, out var cc) ? cc + 1 : 1;
                res.WfChoices.Add($"{y}: {best.Label}; independent IS [{isFrom:o},{isTo:o}), OOS [{isTo:o},{oosTo:o})");
                var selected = RunComboClass(best, cls, isTo, oosTo, BaseCost, "selected-oos", y);
                res.SelectedOosRuns.AddRange(selected.Runs);
                var yearTrades = selected.Trades;
                defaultOos.AddRange(RunComboClass(grid.First(c => c.IsDefault), cls, isTo, oosTo, BaseCost, "default-oos", y).Trades);
                oos.AddRange(yearTrades);
                var ys = RStats(yearTrades);
                res.OosByYear[y] = (ys.n, ys.mean);
            }
            res.Wf = RStats(oos);
            res.WfStats = PerformanceAnalyzer.Compute($"{sid}/{cls} WF-OOS natural outcomes ONLY", oos, IsolatedEquity);
            res.OosYearsTotal = res.OosByYear.Count(kv => kv.Value.n > 0);
            res.OosYearsPositive = res.OosByYear.Count(kv => kv.Value.n > 0 && kv.Value.mean > 0);
            var def = grid.First(c => c.IsDefault);
            res.DefaultOos = RStats(defaultOos);
            // Retrospective grid diagnostics are explicitly NOT inputs to fold selection.
            var all = grid.Select(c => RunComboClass(c, cls, ResearchFrom, ResearchTo, BaseCost, "retrospective-grid")).ToList();
            var byKey = all.ToDictionary(a => a.Combo.Key);
            var fullMeans = all.Select(a => RStats(a.Trades)).ToList();
            res.PctCombosPositive = fullMeans.Count(x => x.n >= 30 && x.mean > 0) / (double)grid.Count;
            res.MedianComboR = Apex.Core.Indicators.Ta.Median(fullMeans.Where(x => x.n > 0).Select(x => x.mean));

            // Parametry docelowe: najczęściej wybierane w walk-forward (remis → domyślne).
            string chosenKey = chosenCount.OrderByDescending(kv => kv.Value).ThenByDescending(kv => kv.Key == def.Key).Select(kv => kv.Key).FirstOrDefault() ?? def.Key;
            res.Chosen = grid.First(c => c.Key == chosenKey);
            res.ChosenFull = RStats(byKey[chosenKey].Trades);
            res.ChosenElevated = RStats(RunComboClass(res.Chosen, cls, ResearchFrom, ResearchTo, Stress(CostScenario.Elevated), "retrospective-stress").Trades);
            res.ChosenDouble = RStats(RunComboClass(res.Chosen, cls, ResearchFrom, ResearchTo, Stress(CostScenario.Double), "retrospective-stress").Trades);

            // Kryteria włączenia (ustalone z góry).
            if (res.Wf.n < 60) res.FailedCriteria.Add($"za mało transakcji OOS ({res.Wf.n} < 60)");
            if (res.Wf.mean < 0.05) res.FailedCriteria.Add($"E[R] OOS {res.Wf.mean:F3} < 0,05");
            if (res.Wf.t < 1.65) res.FailedCriteria.Add($"t OOS {res.Wf.t:F2} < 1,65");
            if (res.OosYearsTotal == 0 || res.OosYearsPositive / (double)res.OosYearsTotal < 0.55) res.FailedCriteria.Add($"zyskowne lata OOS {res.OosYearsPositive}/{res.OosYearsTotal} < 55%");
            if (res.PctCombosPositive < 0.60) res.FailedCriteria.Add($"dodatnie kombinacje siatki {res.PctCombosPositive:P0} < 60%");
            if (res.ChosenElevated.mean <= 0) res.FailedCriteria.Add($"E[R] przy kosztach x1,5 = {res.ChosenElevated.mean:F3} ≤ 0");
            res.Enabled = res.FailedCriteria.Count == 0;

            var wfEvidence = ExportWalkForwardEvidence(sid, cls, res.SelectedOosRuns);
            MachineResults.Add(new { strategy = sid.ToString(), assetClass = cls.ToString(), gridSize = grid.Count,
                selectedFolds = res.WfChoices, naturalOosByYear = res.OosByYear.ToDictionary(k => k.Key, v => new { trades = v.Value.n, meanR = v.Value.mean }),
                naturalOos = wfEvidence.NaturalOos, selectedOosAccounts = wfEvidence.SelectedOosAccounts,
                researchCriteriaMet = res.Enabled, productionEnabled = false, failures = res.FailedCriteria });
            return res;
        }

        public sealed record WalkForwardEvidence(object NaturalOos, object SelectedOosAccounts);

        /// <summary>Export frozen selected OOS runs without conflating natural statistics and account money.</summary>
        public WalkForwardEvidence ExportWalkForwardEvidence(StrategyId sid, AssetClass cls, IReadOnlyList<IsolatedRun> selectedOosRuns)
        {
            var natural = selectedOosRuns.SelectMany(r => r.NaturalTrades).OrderBy(t => t.EntryTimeUtc).ToList();
            Directory.CreateDirectory(Path.Combine(_outDir, "trades"));
            File.WriteAllLines(Path.Combine(_outDir, "trades", $"wf_natural_{sid}_{cls}.csv"),
                new[] { TradeRecord.CsvHeader }.Concat(natural.Select(t => t.ToCsv())));
            File.WriteAllLines(Path.Combine(_outDir, "trades", $"wf_account_{sid}_{cls}.csv"),
                new[] { "run_id;boundary_liquidation;" + TradeRecord.CsvHeader }.Concat(selectedOosRuns.SelectMany(r =>
                    r.Account.Trades.Select(t => $"{r.RunId};{r.Account.BoundaryPositionIds.Contains(t.PositionId)};{t.ToCsv()}"))));
            return new(IsolatedRun.TradeStats(natural, IsolatedEquity), IsolatedRun.AccountTotals(selectedOosRuns));
        }

        private StrategyProfile BuildProfile(List<StrategyClassResult> results)
        {
            var p = new StrategyProfile
            {
                Version = "1.0.0-" + DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                Description = $"OFFLINE CANDIDATES ONLY [{ResearchFrom:o},{ResearchTo:o}); no production validation or activation.",
                Validation = "reports/RAPORT_BADAN.md",
            };
            foreach (var r in results)
            {
                var e = r.Chosen.Build(r.AssetClass);
                e.Enabled = false; // research never enables or promotes production strategies
                e.Evidence = string.Format(CultureInfo.InvariantCulture, "Independent WF OOS: n={0}, E={1:F3}R, t={2:F2}; years+ {3}/{4}; {5}",
                    r.Wf.n, r.Wf.mean, r.Wf.t, r.OosYearsPositive, r.OosYearsTotal, r.Enabled ? "SPEŁNIA kryteria" : "NIE spełnia: " + string.Join("; ", r.FailedCriteria));
                p.Entries.Add(e);
            }
            return p;
        }

        // ------------------------------------------------------------------ portfel, holdout, Monte Carlo

        private void RunPortfolio(StrategyProfile profile)
        {
            var enabled = profile.Entries.Where(e => e.Enabled).ToList();
            Log("");
            Log("## Portfel (pełne zarządzanie ryzykiem: 0,5%/transakcję, limit dzienny 1,5%, max DD 5%, max 3 pozycje, dźwignia ≤ 5)");
            if (enabled.Count == 0)
            {
                Log("Żadna strategia nie spełniła kryteriów – portfel pusty. Bot w tej konfiguracji pozostaje bez pozycji.");
                return;
            }
            Directory.CreateDirectory(Path.Combine(_outDir, "trades"));
            var classes = enabled.Select(e => e.AssetClass).Distinct().ToList();
            var symbols = classes.SelectMany(SymbolsOf).Distinct().Select(_loader.Load).ToList();
            foreach (var (name, from, to) in new[] { ("explicit bounded evaluation (not untouched holdout)", ResearchFrom, ResearchTo) })
            {
                foreach (double minScore in new[] { 0.0, 50.0 })
                {
                    var res = BacktestRunner.Run(new RunSpec
                    {
                        Symbols = symbols,
                        Profile = new StrategyProfile { Version = profile.Version, Entries = enabled },
                        ConfigFactory = () => PortfolioConfig(minScore),
                        From = from,
                        To = to,
                        StartEquity = 10_000,
                        Cost = BaseCost,
                        Fx = _loader.Fx,
                    });
                    double years = (to - from).TotalDays / 365.25;
                    double cagr = Math.Pow(Math.Max(1e-9, res.EndEquity / res.StartEquity), 1 / years) - 1;
                    var st = PerformanceAnalyzer.Compute($"portfel {name} minScore {minScore}", res.Trades, res.StartEquity);
                    double sharpe = DailyEquitySharpe(res.DailyEquity);
                    Log($"### Portfel – okres {name}, minimalna ocena {minScore}");
                    Log($"Kapitał {res.StartEquity:F0} → {res.EndEquity:F0} USD; CAGR {cagr:P2}; max DD (equity) {res.MaxEquityDrawdownPct:F2}%; Sharpe (dzienny equity) {sharpe:F2}");
                    Log("```");
                    Log(PerformanceAnalyzer.Report(res.Trades, res.StartEquity));
                    Log("```");
                    foreach (var ev in res.Events.Where(e => e.Contains("PRZEKROCZONO") || e.Contains("Tryb")).Take(10)) Log("  zdarzenie: " + ev);
                    string tag = (name.StartsWith("HOLD") ? "holdout" : "research") + "_min" + minScore + _tag;
                    File.WriteAllLines(Path.Combine(_outDir, "trades", $"portfolio_{tag}.csv"), new[] { TradeRecord.CsvHeader }.Concat(res.Trades.Select(t => t.ToCsv())));
                    File.WriteAllLines(Path.Combine(_outDir, "trades", $"equity_{tag}.csv"), new[] { "date,equity" }.Concat(res.DailyEquity.Select(e => $"{e.Time:yyyy-MM-dd},{e.Equity.ToString("F2", CultureInfo.InvariantCulture)}")));
                    _portfolioSummary.Add(new
                    {
                        period = name.StartsWith("HOLD") ? "holdout" : "research",
                        minScore,
                        trades = st.Trades,
                        winRate = st.WinRate,
                        expectancyR = st.ExpectancyR,
                        tStat = st.TStat,
                        profitFactor = double.IsInfinity(st.ProfitFactor) ? 999 : st.ProfitFactor,
                        netProfit = st.NetProfit,
                        endEquity = res.EndEquity,
                        maxEquityDrawdownPct = res.MaxEquityDrawdownPct,
                        sharpe,
                    });
                    if (minScore == 50.0) MonteCarlo(res.Trades, name);
                }
            }
        }

        public static double DailyEquitySharpe(List<(DateTime Time, double Equity)> eq)
        {
            if (eq.Count < 3) return 0;
            var r = new List<double>();
            for (int i = 1; i < eq.Count; i++) if (eq[i - 1].Equity > 0) r.Add(eq[i].Equity / eq[i - 1].Equity - 1);
            double m = r.Average();
            double sd = Math.Sqrt(r.Sum(x => (x - m) * (x - m)) / (r.Count - 1));
            return sd > 0 ? m / sd * Math.Sqrt(252) : 0;
        }

        private void MonteCarlo(List<TradeRecord> trades, string name)
        {
            if (trades.Count < 20) { Log($"Monte Carlo ({name}): za mało transakcji ({trades.Count})."); return; }
            var rng = new Random(12345);
            var pct = trades.OrderBy(t => t.ExitTimeUtc).Select(t => t.InitialRiskMoney > 0 ? t.RMultiple * 0.5 / 100.0 : 0).ToArray();
            int sims = 10_000;
            var dds = new double[sims];
            var finals = new double[sims];
            for (int s = 0; s < sims; s++)
            {
                double eq = 1, peak = 1, mdd = 0;
                for (int k = 0; k < pct.Length; k++)
                {
                    eq *= 1 + pct[rng.Next(pct.Length)];
                    if (eq > peak) peak = eq;
                    mdd = Math.Max(mdd, (peak - eq) / peak);
                }
                dds[s] = mdd * 100;
                finals[s] = (eq - 1) * 100;
            }
            Array.Sort(dds);
            Array.Sort(finals);
            Log($"Monte Carlo ({name}, bootstrap {sims}× {pct.Length} transakcji, ryzyko 0,5% na R): " +
                $"max DD mediana {dds[sims / 2]:F1}%, 95. percentyl {dds[(int)(sims * 0.95)]:F1}%, 99. percentyl {dds[(int)(sims * 0.99)]:F1}%; " +
                $"wynik końcowy 5./50./95. percentyl: {finals[(int)(sims * 0.05)]:F1}% / {finals[sims / 2]:F1}% / {finals[(int)(sims * 0.95)]:F1}%; " +
                $"P(DD ≥ 5%) = {dds.Count(d => d >= 5) / (double)sims:P1}");
        }

        // ------------------------------------------------------------------ porównanie interwałów

        private void RunTimeframeComparison()
        {
            Log("");
            Log("## Porównanie interwałów (Trend–korekta, parametry domyślne, okres badawczy, koszty bazowe)");
            var combos = new (string name, Tf sig, Tf filt)[] { ("M15/H1", Tf.M15, Tf.H1), ("H1/H4", Tf.H1, Tf.H4), ("H4/D1", Tf.H4, Tf.D1) };
            foreach (var cls in new[] { AssetClass.Forex, AssetClass.Index, AssetClass.Metal })
            {
                foreach (var (name, sig, filt) in combos)
                {
                    var entry = new ProfileEntry { AssetClass = cls, Strategy = StrategyId.TrendPullback, TrendPullback = new TrendPullbackParams { SignalTf = sig, FilterTf = filt } };
                    var bag = new ConcurrentBag<TradeRecord>();
                    Parallel.ForEach(SymbolsOf(cls), sym => { foreach (var t in RunIsolated(entry, sym, ResearchFrom, ResearchTo, CostScenario.Base, purpose: "timeframe-diagnostic").NaturalTrades) bag.Add(t); });
                    var st = RStats(bag);
                    Log($"{cls,-8} {name,-7} n={st.n,6} E={st.mean,7:F3}R t={st.t,6:F2}");
                }
            }
        }

        private void WriteResearchReport(List<StrategyClassResult> results)
        {
            var sb = new StringBuilder();
            var ci = CultureInfo.InvariantCulture;
            sb.AppendLine("Natural-outcome statistics only below (boundary outcomes excluded from selection). Complete selected OOS money, costs, boundary trades and sampled equity DD: separate independent accounts, not a compounded portfolio.");
            sb.AppendLine($"Every isolated IS/OOS/diagnostic account: isolated{_tag}/run-*.json; WF CSV: wf_natural_* vs complete wf_account_* (run_id joins identity/cost/range).\n");
            sb.AppendLine("| Strategia | Klasa | n OOS | E[R] OOS | t OOS | PF OOS | lata+ | siatka+ | domyślne OOS E[R] | wybrane parametry | E[R] x1,5 | E[R] x2 | decyzja |");
            sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---|---:|---:|---|");
            foreach (var r in results)
            {
                sb.AppendLine(string.Format(ci, "| {0} | {1} | {2} | {3:F3} | {4:F2} | {5:F2} | {6}/{7} | {8:P0} | {9:F3} | {10} | {11:F3} | {12:F3} | {13} |",
                    r.Strategy, r.AssetClass, r.Wf.n, r.Wf.mean, r.Wf.t, r.WfStats.ProfitFactor, r.OosYearsPositive, r.OosYearsTotal, r.PctCombosPositive,
                    r.DefaultOos.mean, r.Chosen.Label, r.ChosenElevated.mean, r.ChosenDouble.mean, r.Enabled ? "criteria met (offline candidate ONLY)" : "rejected"));
            }
            sb.AppendLine();
            foreach (var r in results)
            {
                sb.AppendLine($"### {r.Strategy} / {r.AssetClass}");
                sb.AppendLine(string.Format(ci, "Selected OOS COMPLETE independent accounts: start {0:F8}, end {1:F8}, net {2:F8}, commission {3:F8}, swap {4:F8}, boundary liquidations {5}. Per-account sampled DD and cashflows in isolated JSON.",
                    r.SelectedOosRuns.Sum(x => x.Account.StartEquity), r.SelectedOosRuns.Sum(x => x.Account.EndEquity),
                    r.SelectedOosRuns.Sum(x => x.Account.Trades.Sum(t => t.NetProfit)), r.SelectedOosRuns.Sum(x => x.Account.Trades.Sum(t => t.Commission)),
                    r.SelectedOosRuns.Sum(x => x.Account.Trades.Sum(t => t.Swap)), r.SelectedOosRuns.Sum(x => x.Account.BoundaryPositionIds.Count)));
                sb.AppendLine("Wybory walk-forward: " + string.Join("; ", r.WfChoices));
                sb.AppendLine("Wyniki OOS wg lat: " + string.Join(", ", r.OosByYear.Select(kv => string.Format(ci, "{0}: n={1} E={2:F2}R", kv.Key, kv.Value.n, kv.Value.mean))));
                if (!r.Enabled) sb.AppendLine("Powody odrzucenia: " + string.Join("; ", r.FailedCriteria));
                sb.AppendLine();
            }
            File.WriteAllText(Path.Combine(_outDir, $"research_table{_tag}.md"), sb.ToString());
            Log("");
            Log(sb.ToString());
        }
    }
}
