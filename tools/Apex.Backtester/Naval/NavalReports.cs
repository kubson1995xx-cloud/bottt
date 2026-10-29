using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Apex.Core.Naval;

namespace Apex.Backtester.Naval;
public sealed record NavalStatistics(int N, decimal GrossReference, decimal GrossExecution, decimal Net, decimal Fees,
    decimal ModeledPriceImpact, decimal? WinRate, decimal? ProfitFactor, decimal? AverageWin, decimal? AverageLoss);
public static class NavalReports
{
    public const string SemanticsVersion = "naval-cash-spot-v1";
    public static JsonSerializerOptions Json { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public static NavalStatistics Statistics(IEnumerable<SpotTrade> trades)
    {
        var all = trades.ToArray(); var wins = all.Where(t => t.Net > 0).ToArray(); var losses = all.Where(t => t.Net < 0).ToArray();
        return new(all.Length, all.Sum(t => t.GrossReference), all.Sum(t => t.GrossExecution), all.Sum(t => t.Net),
            all.Sum(t => t.Fees), all.Sum(t => t.PriceImpact), all.Length > 0 ? (decimal)wins.Length / all.Length : null,
            losses.Length > 0 ? wins.Sum(t => t.Net) / -losses.Sum(t => t.Net) : null,
            wins.Length > 0 ? wins.Average(t => t.Net) : null, losses.Length > 0 ? losses.Average(t => t.Net) : null);
    }
    public static object Config(NavalRunSpec s) => new { s.Fleet, s.From, s.To, s.Settings, s.Cost, s.InitialCash, assets = s.Assets ?? NavalFleetProfiles.Get(s.Fleet).Assets };
    public static object Provenance(NavalDataset d) => new
    {
        d.Source, d.QuoteAsset, d.CutoffUtc, d.ManifestSha256,
        backtesterSha256 = NavalDataset.Hash(File.ReadAllBytes(typeof(NavalReports).Assembly.Location)),
        coreSha256 = NavalDataset.Hash(File.ReadAllBytes(typeof(NavalD1Evaluator).Assembly.Location)),
        evaluatorVersion = NavalD1Evaluator.RuleVersion,
        instruments = d.Instruments.Values.Select(i => new { i.Series.Instrument, i.Sha256, i.MetadataSha256, i.ListTime, i.LotSize, i.MinSize, i.TickSize }),
        d.Unavailable,
        assumptions = "TRADE D1 UTC confirm1; modeled taker fees and directional spread/slippage, not verified account costs. CURRENT metadata frozen historically, not historical constraints. Quote units, no FX=1. Research only, no execution authority."
    };
    public static void WriteJson(string path, object value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, value, Json);
    }
    public static void WriteAccount(string directory, NavalRunResult r)
    {
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("Account output already exists");
        Directory.CreateDirectory(directory);
        var s = r.Spec; var a = r.Account;
        var delta = a.InitialCash + a.Cashflows.Sum(c => c.QuoteDelta) - a.Cash;
        if (Math.Abs(delta) > .000000000001m || a.Positions.Count != 0 || Math.Abs(a.Cash - a.InitialCash - a.Trades.Sum(t => t.Net)) > .000000000001m)
            throw new InvalidOperationException("Full-liquidation account reconciliation failed");
        var cost = s.Cost; var impactBps = cost.HalfSpreadBps + cost.SlippageBps;
        decimal Spread(decimal impact) => impactBps > 0 ? impact * cost.HalfSpreadBps / impactBps : 0;
        var operations = a.Cashflows.Select(c => new
        {
            c.Fill, c.QuoteDelta, c.BaseDelta, c.FeeQuote, c.PriceImpact,
            modeledSpread = Spread(c.PriceImpact), modeledSlippage = c.PriceImpact - Spread(c.PriceImpact)
        }).ToArray();
        var config = Config(s);
        WriteJson(Path.Combine(directory, "account.json"), new
        {
            success = true, semantics = SemanticsVersion, provenance = Provenance(s.Dataset), config,
            configSha256 = NavalDataset.Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(config, Json))),
            startCash = a.InitialCash, endCash = a.Cash, reconciliationResidual = delta,
            metrics = Statistics(a.Trades), modeledSpread = operations.Sum(c => c.modeledSpread), modeledSlippage = operations.Sum(c => c.modeledSlippage),
            maxCloseSampledEquityDrawdownPct = r.Equity.Max(e => e.EquityDrawdownPct),
            maxClosedBalanceDrawdownPct = r.Equity.Max(e => e.ClosedBalanceDrawdownPct),
            r.Halted, r.MaxConcurrentPositions,
            sampling = "D1 open/observed fills/close risk checks; sticky daily and DD halt to run end. Intrabar fills are observed at close, not claimed tick chronology. IntrabarLowStress is a non-synchronized conservative pre-intrabar-protection inventory bound, not historical synchronized DD.",
            perSymbol = (s.Assets ?? NavalFleetProfiles.Get(s.Fleet).Assets).Select(asset => new
            {
                asset, instrument = asset + "-" + a.QuoteAsset, attributionOnly = true,
                metrics = Statistics(a.Trades.Where(t => t.Instrument == asset + "-" + a.QuoteAsset)),
                quoteCashflow = a.Cashflows.Where(c => c.Fill.BaseAsset == asset).Sum(c => c.QuoteDelta)
            }),
            years = Enumerable.Range(s.From.Year, s.To.AddTicks(-1).Year - s.From.Year + 1).Select(year =>
            {
                var begin = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
                var end = begin.AddYears(1); if (begin < s.From) begin = s.From; if (end > s.To) end = s.To;
                var first = r.Equity.Last(e => e.TimeUtc <= begin); var last = r.Equity.Last(e => e.TimeUtc <= end);
                // Close-observed intrabar/boundary flows są przypisane do zakończonego D1.
                var flows = a.Cashflows.Where(c => OperationDay(c.Fill) >= begin && OperationDay(c.Fill) < end).ToArray();
                return new { year, from = begin, to = end, startLiquidationEquity = first.LiquidationEquity, endLiquidationEquity = last.LiquidationEquity,
                    netLiquidationEquityChange = last.LiquidationEquity - first.LiquidationEquity,
                    quoteCashflows = flows.Sum(c => c.QuoteDelta), fees = flows.Sum(c => c.FeeQuote),
                    metrics = Statistics(a.Trades.Where(t => OperationDay(t.ExitUtc, t.ExitReason) >= begin && OperationDay(t.ExitUtc, t.ExitReason) < end)) };
            }),
            trades = a.Trades, cashflows = operations, equity = r.Equity, refusals = r.Refusals, decisions = r.Decisions
        });
        Csv(Path.Combine(directory, "trades.csv"), a.Trades);
        Csv(Path.Combine(directory, "cashflows.csv"), a.Cashflows.Select(c => new
        {
            c.Fill.Id, c.Fill.PositionId, c.Fill.Instrument, c.Fill.TimeUtc, c.Fill.Side, c.Fill.Quantity,
            c.Fill.ReferencePrice, c.Fill.ExecutedPrice, c.Fill.Fee, c.Fill.FeeCurrency,
            c.QuoteDelta, c.BaseDelta, c.FeeQuote, c.PriceImpact,
            modeledSpread = Spread(c.PriceImpact), modeledSlippage = c.PriceImpact - Spread(c.PriceImpact), c.Fill.Reason, c.Fill.Boundary, c.Fill.InitialRisk
        }));
        Csv(Path.Combine(directory, "equity.csv"), r.Equity);
        Csv(Path.Combine(directory, "refusals.csv"), r.Refusals);
        Csv(Path.Combine(directory, "decisions.csv"), r.Decisions.Select(d => new
        {
            d.Decision.CutoffUtc, d.Decision.Instrument, d.Decision.Action, d.Decision.Reason, d.Decision.EarliestExecutionUtc,
            d.Decision.StableId, d.Decision.ConfigIdentity, d.Decision.AsOfIdentity, d.Decision.EntryHigh, d.Decision.ExitLow,
            d.Decision.BtcSma, d.Atr, d.Rank, d.Turnover
        }));
    }
    static DateTimeOffset OperationDay(SpotFill f) => OperationDay(f.TimeUtc, f.Reason);
    static DateTimeOffset OperationDay(DateTimeOffset t, string reason)
        => reason is "Stop" or "TakeProfit" or "Boundary" ? t.AddTicks(-1) : t;
    static void Csv<T>(string path, IEnumerable<T> rows)
    {
        var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        string Cell(object? value)
        {
            var text = value switch { null => "", DateTimeOffset d => d.ToString("O", CultureInfo.InvariantCulture),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture), _ => value.ToString() ?? "" };
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.WriteLine(string.Join(",", properties.Select(p => Cell(p.Name))));
        foreach (var row in rows) writer.WriteLine(string.Join(",", properties.Select(p => Cell(p.GetValue(row)))));
    }
}
