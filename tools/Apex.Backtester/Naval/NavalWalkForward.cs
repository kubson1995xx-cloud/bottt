using System;
using System.Collections.Generic;
using System.Linq;
using Apex.Core.Naval;

namespace Apex.Backtester.Naval;
public sealed record NavalFold(DateTimeOffset TrainFrom, DateTimeOffset TrainTo, DateTimeOffset OosFrom, DateTimeOffset OosTo);
public sealed record NavalCandidateScore(string Key, int NaturalTrades, decimal? Score);
public sealed record NavalSelection(string? Key, string Status, IReadOnlyList<NavalCandidateScore> Candidates);
public sealed record NavalFoldResult(NavalFold Fold, NavalSelection Selection, IReadOnlyList<NavalRunResult> Training, NavalRunResult? Oos);
public sealed record NavalWalkForwardResult(IReadOnlyList<NavalFoldResult> Folds, int RunCount);
public sealed class NavalRunBudget
{
    readonly int maximum;
    public int Count { get; private set; }
    public NavalRunBudget(int maximum = 150)
    {
        if (maximum < 1 || maximum > 150) throw new ArgumentException("Research budget must be 1..150");
        this.maximum = maximum;
    }
    public void Take()
    {
        if (Count >= maximum) throw new InvalidOperationException("Research run budget exhausted; no silent partial completion");
        Count++;
    }
}
public static class NavalWalkForward
{
    public static IReadOnlyList<NavalSettings> Candidates { get; } = Array.AsReadOnly(new[] { new NavalSettings(40, 30), new NavalSettings(32, 24), new NavalSettings(48, 36) });
    static DateTimeOffset Date(string s) => NavalDataset.Utc(s + "T00:00:00Z");
    public static IReadOnlyList<NavalFold> FrozenFolds { get; } = Array.AsReadOnly(new[]
    {
        new NavalFold(Date("2022-01-01"), Date("2024-01-01"), Date("2024-01-01"), Date("2025-01-01")),
        new NavalFold(Date("2023-01-01"), Date("2025-01-01"), Date("2025-01-01"), Date("2026-01-01")),
        new NavalFold(Date("2024-01-01"), Date("2026-01-01"), Date("2026-01-01"), Date("2026-10-29"))
    });
    public static string Key(NavalSettings settings) => FormattableString.Invariant($"{settings.EntryDays}-{settings.ExitDays}-{settings.BtcSmaDays}");
    public static NavalSelection Select(IReadOnlyList<NavalRunResult> training, DateTimeOffset cutoff)
    {
        if (training.Count != Candidates.Count || training.Select(r => Key(r.Spec.Settings)).Distinct().Count() != Candidates.Count
            || training.Any(r => r.Spec.To != cutoff || r.Spec.Cost != SpotCostModel.Named("base")
                || !Candidates.Contains(r.Spec.Settings))) throw new ArgumentException("Invalid training evidence");
        var scores = training.Select(r =>
        {
            var natural = r.Account.Trades.Where(t => !t.Boundary && t.ExitUtc < cutoff && t.EntryUtc >= r.Spec.From && t.R.HasValue).ToArray();
            return new NavalCandidateScore(Key(r.Spec.Settings), natural.Length, natural.Length >= 10 ? natural.Average(t => t.R!.Value) : null);
        }).ToArray();
        var winner = scores.Where(s => s.Score.HasValue).OrderByDescending(s => s.Score)
            .ThenBy(s => s.Key == "40-30-200" ? 0 : 1).ThenBy(s => s.Key, StringComparer.Ordinal).FirstOrDefault();
        return new(winner?.Key, winner == null ? "NoSelection" : "Selected", Array.AsReadOnly(scores));
    }
    public static NavalWalkForwardResult Run(NavalRunSpec template, IReadOnlyList<NavalFold>? folds = null,
        Action<string, NavalRunResult>? onRun = null, NavalRunBudget? budget = null)
    {
        folds ??= FrozenFolds; budget ??= new(); int before = budget.Count;
        if (folds.Count < 1 || folds.Count > 3 || folds.Distinct().Count() != folds.Count || folds.Any(f => f.TrainFrom < template.From
            || f.OosTo > template.To || f.TrainFrom >= f.TrainTo || f.TrainTo != f.OosFrom || f.OosFrom >= f.OosTo))
            throw new ArgumentException("Invalid WF periods");
        var assets = template.Assets ?? NavalFleetProfiles.Get(template.Fleet).Assets;
        // Preflight OOS także przy NoSelection: brak danych nie staje się zerowym wynikiem.
        foreach (var f in folds) template.Dataset.ValidateAssets(assets, f.TrainFrom, f.OosTo);
        var results = new List<NavalFoldResult>(); int index = 0;
        foreach (var fold in folds)
        {
            index++; var training = new List<NavalRunResult>();
            foreach (var candidate in Candidates)
            {
                budget.Take();
                var run = NavalBacktestRunner.Run(template with { From = fold.TrainFrom, To = fold.TrainTo, Settings = candidate, Cost = SpotCostModel.Named("base") });
                training.Add(run); onRun?.Invoke("fold-" + index + "-is-" + Key(candidate), run);
            }
            var selection = Select(training, fold.TrainTo); NavalRunResult? oos = null;
            if (selection.Key != null)
            {
                budget.Take();
                oos = NavalBacktestRunner.Run(template with { From = fold.OosFrom, To = fold.OosTo, Settings = Candidates.Single(c => Key(c) == selection.Key) });
                onRun?.Invoke("fold-" + index + "-oos", oos);
            }
            results.Add(new(fold, selection, training.AsReadOnly(), oos));
        }
        return new(results.AsReadOnly(), budget.Count - before);
    }
}
