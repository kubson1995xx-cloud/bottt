using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Apex.Backtester.Data;
using Apex.Backtester.Research;
using Apex.Backtester.Sim;
using Apex.Core.Adaptive;
using Apex.Core.Engine;
using Apex.Core.Model;
using Apex.Core.Performance;
using Apex.Core.Strategies;

namespace Apex.Backtester
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            string cmd = args.Length > 0 ? args[0] : "help";
            string dataDir = GetArg(args, "--data") ?? Path.Combine(FindRepoRoot(), "data", "bars");
            string outDir = GetArg(args, "--out") ?? Path.Combine(FindRepoRoot(), "reports");
            Directory.CreateDirectory(outDir);
            var loader = new DataLoader(dataDir);
            switch (cmd)
            {
                case "smoke":
                    return Smoke(loader, GetArg(args, "--symbol") ?? "EURUSD");
                case "research":
                    return new ResearchPipeline(loader, outDir, GetArg(args, "--only"), GetArg(args, "--tag")).Run();
                case "portfolio":
                    return new ResearchPipeline(loader, outDir, null, GetArg(args, "--tag"))
                        .RunPortfolioOnly((GetArg(args, "--profiles") ?? Path.Combine(outDir, "profile_validated.json")).Split(','));
                default:
                    Console.WriteLine("Użycie: Apex.Backtester smoke|research|portfolio [--data dir] [--out dir] [--symbol S] [--only Strategia|Klasa] [--tag r2] [--profiles a.json,b.json]");
                    return 1;
            }
        }

        private static int Smoke(DataLoader loader, string symbol)
        {
            var sw = Stopwatch.StartNew();
            var sd = loader.Load(symbol);
            Console.WriteLine($"Wczytano {symbol}: H1={sd.H1.Count} H4={sd.H4.Count} D1={sd.D1.Count} M15={sd.M15?.Count ?? 0} w {sw.Elapsed.TotalSeconds:F1}s");
            var profile = new StrategyProfile
            {
                Version = "smoke",
                Entries = new List<ProfileEntry>
                {
                    new() { AssetClass = sd.Def.AssetClass, Strategy = StrategyId.TrendPullback },
                },
            };
            sw.Restart();
            var res = BacktestRunner.Run(new RunSpec
            {
                Symbols = new List<SymbolData> { sd },
                Profile = profile,
                ConfigFactory = ResearchPipeline.IsolatedConfig,
                From = new DateTime(2005, 1, 1),
                To = new DateTime(2018, 1, 1),
                StartEquity = 1_000_000,
                Fx = loader.Fx,
            });
            Console.WriteLine($"Cykle: {res.Cycles}, transakcje: {res.Trades.Count}, czas {sw.Elapsed.TotalSeconds:F1}s");
            Console.WriteLine(PerformanceAnalyzer.Compute("TP " + symbol, res.Trades, 1_000_000).ToLine());
            foreach (var e in res.Events.Take(5)) Console.WriteLine("  zdarzenie: " + e);
            foreach (var g in res.Trades.GroupBy(t => t.ExitReason.Split('(')[0].Trim()).OrderByDescending(g => g.Count()))
                Console.WriteLine($"  wyjście '{g.Key}': n={g.Count()} średnio {g.Average(t => t.RMultiple):F3}R, min {g.Min(t => t.RMultiple):F2}R, max {g.Max(t => t.RMultiple):F2}R, śr. świec {g.Average(t => t.BarsHeld):F1}");
            var rs = res.Trades.Select(t => t.RMultiple).OrderBy(x => x).ToList();
            if (rs.Count > 0) Console.WriteLine($"  R percentyle 1/25/50/75/99: {rs[rs.Count / 100]:F2} {rs[rs.Count / 4]:F2} {rs[rs.Count / 2]:F2} {rs[rs.Count * 3 / 4]:F2} {rs[rs.Count * 99 / 100]:F2}");
            Console.WriteLine($"  prowizje {res.Trades.Sum(t => t.Commission):F0}, swap {res.Trades.Sum(t => t.Swap):F0}, brutto {res.Trades.Sum(t => t.GrossProfit):F0}");
            foreach (var t in res.Trades.Take(3)) Console.WriteLine("  " + t.ToCsv());
            return 0;
        }

        public static string? GetArg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        public static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git"))) dir = dir.Parent;
            return dir?.FullName ?? Directory.GetCurrentDirectory();
        }
    }

    /// <summary>Wczytywanie danych instrumentów z cache w pamięci.</summary>
    public sealed class DataLoader
    {
        private readonly string _dir;
        private readonly Dictionary<string, SymbolData> _cache = new();
        private readonly object _lock = new();

        public DataLoader(string dir)
        {
            _dir = dir;
            Fx = new FxRates();
            foreach (var pair in new[] { "EURUSD", "GBPUSD", "AUDUSD", "USDCAD", "EURJPY" })
            {
                string p = Path.Combine(dir, pair + "_H1.csv");
                if (File.Exists(p)) Fx.Add(pair, BarCsv.Load(p, pair, Tf.H1));
            }
        }

        public FxRates Fx { get; }

        public bool Exists(string symbol) => File.Exists(Path.Combine(_dir, symbol + "_H1.csv"));

        public SymbolData Load(string symbol)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(symbol, out var sd)) return sd;
                var def = Catalog.Get(symbol);
                var h1 = BarCsv.Load(Path.Combine(_dir, symbol + "_H1.csv"), symbol, Tf.H1);
                string m15p = Path.Combine(_dir, symbol + "_M15.csv");
                var m15 = File.Exists(m15p) ? BarCsv.Load(m15p, symbol, Tf.M15) : null;
                sd = new SymbolData(def, h1, m15);
                _cache[symbol] = sd;
                return sd;
            }
        }
    }
}
