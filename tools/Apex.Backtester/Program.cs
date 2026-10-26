using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Apex.Backtester.Data;
using Apex.Backtester.Research;
using Apex.Backtester.Sim;
using Apex.Core.Adaptive;
using Apex.Core.Model;

namespace Apex.Backtester
{
    public static class Program
    {
        public const string SemanticsVersion = "backtest-v2-cashflow-purged";
        public static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] == "help" || args[0] == "--help")
            {
                Console.WriteLine("Offline DEMO research only. smoke|research|portfolio --data DIR --manifest FILE --out DIR --from YYYY-MM-DD --to YYYY-MM-DD --warmup-from YYYY-MM-DD [--symbol EURUSD | --symbols EURUSD,GBPUSD] [--cost base|elevated|double] [--intrabar h1|m15] [--fx-max-age-hours 2] [--only Strategy|Class] [--tag NAME] [--profiles FILE,...] [--is-years 4] [--max-combos 100] [--max-runs 1000] [--parallel 1]. See docs/BACKTESTER.md. No implicit holdout or promotion.");
                return 0;
            }
            string? outDir = GetArg(args, "--out");
            DataLoader? loader = null;
            object? config = null;
            try
            {
                var known = new HashSet<string>(new[] { "--data", "--manifest", "--out", "--from", "--to", "--warmup-from", "--symbol", "--symbols", "--cost", "--intrabar", "--fx-max-age-hours", "--only", "--tag", "--profiles", "--is-years", "--max-combos", "--max-runs", "--parallel" });
                var options = new Dictionary<string, string>();
                for (int i = 1; i < args.Length; i += 2)
                    if (!known.Contains(args[i]) || i + 1 >= args.Length || args[i + 1].StartsWith("--") || !options.TryAdd(args[i], args[i + 1]))
                        throw new ArgumentException("Unknown, duplicate or missing option: " + args[i]);
                string Required(string key) => options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException("Required: " + key);
                string Opt(string key, string fallback) => options.TryGetValue(key, out var value) ? value : fallback;
                DateTime Date(string key) => DateTime.ParseExact(Required(key), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                int Int(string key, int fallback, int max)
                {
                    int n = int.Parse(Opt(key, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
                    return n >= 1 && n <= max ? n : throw new ArgumentException("Out of bounds: " + key);
                }
                outDir = Required("--out"); Directory.CreateDirectory(outDir);
                var from = Date("--from"); var to = Date("--to"); var warmup = Date("--warmup-from");
                if (warmup >= from || from >= to || (to - warmup).TotalDays > 366 * 50) throw new ArgumentException("Require warmup < evaluation from < to, at most 50 years");
                string costName = Opt("--cost", "base");
                var cost = costName switch { "base" => CostScenario.Base, "elevated" => CostScenario.Elevated, "double" => CostScenario.Double, _ => throw new ArgumentException("Unknown cost scenario") };
                string intrabar = Opt("--intrabar", "h1");
                if (intrabar != "h1" && intrabar != "m15") throw new ArgumentException("intrabar must be h1 or m15");
                var fxAge = TimeSpan.FromHours(Int("--fx-max-age-hours", 2, 168));
                string[] symbols = Opt("--symbols", Opt("--symbol", "EURUSD")).Split(',');
                if (symbols.Distinct().Count() != symbols.Length || symbols.Any(s => !Catalog.All.Any(d => d.Symbol == s))) throw new ArgumentException("Unknown/duplicate symbol");
                string tag = Opt("--tag", "");
                if (tag.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')) throw new ArgumentException("Unsafe report tag");
                int isYears = Int("--is-years", 4, 20), maxCombos = Int("--max-combos", 100, 100), maxRuns = Int("--max-runs", 1000, 100000), parallel = Int("--parallel", 1, 64);
                config = new { command = args[0], from, to, warmupFrom = warmup, symbols, instruments = symbols.Select(Catalog.Get).ToArray(), engineConfig = args[0] == "portfolio" ? ResearchPipeline.PortfolioConfig(50) : ResearchPipeline.IsolatedConfig(), cost, intrabar, fxMaxAgeHours = fxAge.TotalHours, isYears, maxCombos, maxRuns, parallel,
                    costAssumption = "Catalog assumptions, not verified Pepperstone fees; FX/metals minimum USD0.50/operation; CFDs zero assumed; USD account",
                    selection = "independent fresh engines per IS/OOS; strict exit purge; no forced boundary outcomes; no profile promotion", sourceArguments = args };
                loader = new DataLoader(Required("--data"), Required("--manifest"), warmup, from, to, fxAge, intrabar == "m15");
                foreach (var symbol in symbols) _ = loader.Load(symbol);
                // Validate the ENTIRE requested interval even when a no-evidence IS fold skips OOS trading.
                BacktestRunner.ValidateFxCoverage(symbols.Select(loader.Load), loader.Fx, from, to);
                object result;
                if (args[0] == "smoke")
                {
                    var profile = new StrategyProfile { Version = "bounded-smoke", Entries = symbols.Select(s => new ProfileEntry { AssetClass = Catalog.Get(s).AssetClass, Strategy = StrategyId.TrendPullback }).DistinctBy(e => e.AssetClass).ToList() };
                    var r = BacktestRunner.Run(new RunSpec { Symbols = symbols.Select(loader.Load).ToList(), Profile = profile,
                        ConfigFactory = ResearchPipeline.IsolatedConfig, From = from, To = to, StartEquity = 1000000, Fx = loader.Fx, Cost = cost });
                    result = new { cycles = r.Cycles, trades = r.Trades.Count, r.StartEquity, r.EndEquity, r.MaxEquityDrawdownPct,
                        r.EffectiveStartUtc, r.LastExecutableOpenUtc, r.EffectiveEndUtc,
                        gross = r.Trades.Sum(t => t.GrossProfit), commission = r.Trades.Sum(t => t.Commission), swap = r.Trades.Sum(t => t.Swap),
                        net = r.Trades.Sum(t => t.NetProfit), boundaryLiquidations = r.BoundaryPositionIds.Count, events = r.Events,
                        warmup = "features only before from; no orders/account/risk state carried from warmup; insufficient indicators may yield zero signals" };
                    File.WriteAllLines(Path.Combine(outDir, "trades.csv"), new[] { Apex.Core.Performance.TradeRecord.CsvHeader }.Concat(r.Trades.Select(t => t.ToCsv())));
                }
                else if (args[0] == "research" || args[0] == "portfolio")
                {
                    if (args[0] == "research" && (from.Month != 1 || from.Day != 1 || to.Month != 1 || to.Day != 1 || to.Year - from.Year <= isYears))
                        throw new ArgumentException("Research requires Jan1 bounds and at least one complete yearly OOS after IS");
                    var pipeline = new ResearchPipeline(loader, outDir, GetArg(args,"--only"), tag, from, to)
                    { BaseCost = cost, TrainingYears = isYears, MaxCombos = maxCombos, MaxRuns = maxRuns, Parallelism = parallel, RequestedSymbols = symbols };
                    if (args[0] == "research") pipeline.Run();
                    else pipeline.RunPortfolioOnly(Required("--profiles").Split(','));
                    result = new { runs = pipeline.RunCount, results = pipeline.MachineResults, warning = "research candidate report only; no production profile written or enabled; no untouched holdout claimed" };
                }
                else throw new ArgumentException("Unknown command: " + args[0]);
                WriteReport(outDir, new { success = true, semantics = SemanticsVersion, source = SourceHashes(), config, dataSource = loader.Source, data = loader.Evidence, result });
                Console.WriteLine(Path.Combine(outDir, "run.json")); return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.Message);
                if (!string.IsNullOrWhiteSpace(outDir))
                {
                    Directory.CreateDirectory(outDir);
                    WriteReport(outDir, new { success = false, semantics = SemanticsVersion, source = SourceHashes(), config, data = loader?.Evidence, error = error.ToString() });
                }
                return 2;
            }
        }
        static object SourceHashes() => new { backtester = Hash(typeof(Program).Assembly.Location), core = Hash(typeof(InstrumentSpec).Assembly.Location) };
        static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        static void WriteReport(string dir, object report) => File.WriteAllText(Path.Combine(dir,"run.json"), JsonSerializer.Serialize(report, DataManifest.JsonOptions));
        public static string? GetArg(string[] args, string name)
        {
            int i = Array.IndexOf(args,name); return i >= 0 && i + 1 < args.Length ? args[i+1] : null;
        }
        public static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName,".git")) && !File.Exists(Path.Combine(dir.FullName,".git"))) dir = dir.Parent;
            return dir?.FullName ?? Directory.GetCurrentDirectory();
        }
    }
}
