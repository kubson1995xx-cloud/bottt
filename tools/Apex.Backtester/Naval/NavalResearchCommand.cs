using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Apex.Core.Naval;

namespace Apex.Backtester.Naval;
public static class NavalResearchCommand
{
    // args bez tokena "naval". Nie tworzy klienta API ani execution authority.
    public static int Run(string[] args)
    {
        string? ownedOutput = null; NavalDataset? dataset = null; var budget = new NavalRunBudget();
        var completed = new List<string>(); object? config = null;
        try
        {
            var allowed = new HashSet<string>(new[] { "--manifest", "--out", "--from", "--to", "--fleet", "--cost", "--mode", "--entry-days", "--exit-days" }, StringComparer.Ordinal);
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i += 2)
                if (!allowed.Contains(args[i]) || i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal)
                    || !options.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Unknown/duplicate/missing option: " + args[i]);
            string Required(string key) => options.TryGetValue(key, out var value) ? value : throw new ArgumentException("Required: " + key);
            var path = Path.GetFullPath(Required("--out"));
            if (Directory.Exists(path) || File.Exists(path)) throw new IOException("Output already exists; no overwrite: " + path);
            Directory.CreateDirectory(path);
            using (new FileStream(Path.Combine(path, ".naval-claim"), FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            ownedOutput = path;
            DateTimeOffset Date(string key) => new(DateTime.ParseExact(Required(key), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal), TimeSpan.Zero);
            var from = Date("--from"); var to = Date("--to");
            var costName = Required("--cost"); var cost = SpotCostModel.Named(costName);
            var fleetName = Required("--fleet");
            var fleets = fleetName switch { "Alpha" => new[] { NavalFleet.Alpha }, "Bravo" => new[] { NavalFleet.Bravo }, "Both" => new[] { NavalFleet.Alpha, NavalFleet.Bravo }, _ => throw new ArgumentException("Unknown fleet") };
            var mode = Required("--mode");
            if (mode != "fixed" && mode != "walk-forward") throw new ArgumentException("Unknown mode");
            int Days(string key, int fallback) => options.TryGetValue(key, out var v) ? int.Parse(v, NumberStyles.None, CultureInfo.InvariantCulture) : fallback;
            var settings = new NavalSettings(Days("--entry-days", 40), Days("--exit-days", 30));
            if (options.ContainsKey("--entry-days") != options.ContainsKey("--exit-days") || !NavalWalkForward.Candidates.Contains(settings))
                throw new ArgumentException("Only preregistered pairs 40/30, 32/24, 48/36 allowed");
            if (mode == "walk-forward" && (options.ContainsKey("--entry-days") || options.ContainsKey("--exit-days")
                || from != NavalWalkForward.FrozenFolds[0].TrainFrom || to != NavalWalkForward.FrozenFolds[^1].OosTo))
                throw new ArgumentException("WF requires frozen [2022-01-01,2026-10-29), no parameter override");
            config = new { from, to, fleet = fleetName, mode, costName, settings, sourceArguments = args, maxRuns = 150,
                independentAccounts = "10000 quote per fleet/isolated symbol/fold/candidate. Not a reinvested combined portfolio.",
                perSymbol = mode == "fixed" ? "Separate isolated runs plus fleet attribution" : "Fleet attribution in every IS/OOS; not standalone per-symbol WF accounts" };
            dataset = NavalDataset.Load(Required("--manifest"));
            foreach (var fleet in fleets) dataset.ValidateCoverage(fleet, from, to);
            var foldEvidence = new List<object>();
            void Export(string id, NavalRunResult run)
            {
                NavalReports.WriteAccount(Path.Combine(path, id), run); completed.Add(id);
            }
            foreach (var fleet in fleets)
            {
                var spec = new NavalRunSpec(dataset, fleet, from, to, settings, cost);
                if (mode == "fixed")
                {
                    budget.Take(); Export(fleet + "-fleet", NavalBacktestRunner.Run(spec));
                    foreach (var asset in NavalFleetProfiles.Get(fleet).Assets)
                    {
                        budget.Take(); Export(fleet + "-isolated-" + asset, NavalBacktestRunner.Run(spec with { Assets = new[] { asset } }));
                    }
                }
                else
                {
                    var result = NavalWalkForward.Run(spec, onRun: (id, run) => Export(fleet + "-" + id, run), budget: budget);
                    foldEvidence.Add(new { fleet, result.RunCount, folds = result.Folds.Select(f => new { f.Fold, f.Selection, oosStatus = f.Oos == null ? "NoSelection" : "Completed" }) });
                }
            }
            NavalReports.WriteJson(Path.Combine(path, "run.json"), new { success = true, semantics = NavalReports.SemanticsVersion,
                config, provenance = NavalReports.Provenance(dataset), runCount = budget.Count, accounts = completed, walkForward = foldEvidence,
                warning = "Historical research only; no strategy promotion, broker keys, LIVE/DEMO orders, certified untouched holdout or aggregated independent-account drawdown." });
            Console.WriteLine(Path.Combine(path, "run.json")); return 0;
        }
        catch (Exception e)
        {
            var failure = new { success = false, semantics = NavalReports.SemanticsVersion, config, runCount = budget.Count,
                completedAccounts = completed, manifestSha256 = dataset?.ManifestSha256, error = e.Message };
            Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(failure, NavalReports.Json));
            if (ownedOutput != null) NavalReports.WriteJson(Path.Combine(ownedOutput, "run.json"), failure);
            return 2;
        }
    }
}
