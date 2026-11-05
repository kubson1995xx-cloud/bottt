using System.Text.Json;
using Apex.Backtester;
using Apex.Backtester.Naval;
using Apex.Core.Naval;
using Xunit;

namespace Apex.Backtester.Tests;

public class NavalReportsTests
{
    static string[] Args(NavalFixture f, string output) => new[] { "naval", "--manifest", f.Manifest, "--out", output, "--from", f.From.ToString("yyyy-MM-dd"), "--to", f.To.ToString("yyyy-MM-dd"), "--fleet", "Alpha", "--cost", "base", "--mode", "fixed" };

    [Fact]
    public void Actual_Program_dispatch_exports_complete_account_provenance_csv_and_zero_loss_PF_null()
    {
        using var f = new NavalFixture(); var dir = Path.Combine(f.Root, "results");
        Assert.Equal(0, Program.Main(Args(f, dir)));
        using var top = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "run.json")));
        Assert.True(top.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(3, top.RootElement.GetProperty("runCount").GetInt32()); // one fleet + two isolated symbols
        var accountDir = Path.Combine(dir, "Alpha-fleet");
        using var account = JsonDocument.Parse(File.ReadAllText(Path.Combine(accountDir, "account.json")));
        var a = account.RootElement;
        Assert.Equal(NavalDataset.Load(f.Manifest).ManifestSha256, a.GetProperty("provenance").GetProperty("manifestSha256").GetString());
        Assert.Equal(64, a.GetProperty("provenance").GetProperty("backtesterSha256").GetString()!.Length);
        Assert.True(a.GetProperty("trades").GetArrayLength() > 0);
        Assert.True(a.GetProperty("cashflows").GetArrayLength() > 0);
        var m = a.GetProperty("metrics");
        Assert.True(m.GetProperty("fees").GetDecimal() > 0);
        Assert.Equal(m.GetProperty("net").GetDecimal(), a.GetProperty("endCash").GetDecimal() - 10000m);
        Assert.Equal(2, a.GetProperty("perSymbol").GetArrayLength());
        Assert.NotEmpty(a.GetProperty("years").EnumerateArray());
        foreach (var file in new[] { "trades.csv", "cashflows.csv", "equity.csv", "refusals.csv", "decisions.csv" })
            Assert.True(File.ReadLines(Path.Combine(accountDir, file)).Count() > 0);
        var solo = NavalBacktestRunner.Run(new(NavalDataset.Load(f.Manifest), NavalFleet.Alpha, f.From, f.To, new(), SpotCostModel.Named("base"), Assets: new[] { "BTC" }));
        Assert.Null(NavalReports.Statistics(solo.Account.Trades).ProfitFactor);
        var original = File.ReadAllBytes(Path.Combine(dir, "run.json"));
        Assert.Equal(2, Program.Main(Args(f, dir)));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(dir, "run.json")));
    }
    [Theory]
    [InlineData("missingmetadata")] [InlineData("unknownquote")] [InlineData("incomplete")]
    public void Cli_failure_is_not_zero_performance_and_has_success_false(string kind)
    {
        using var f = new NavalFixture();
        switch (kind)
        {
            case "missingmetadata": File.Delete(Path.Combine(f.Root, "BTC-USDT.json")); break;
            case "unknownquote": f.Document["quoteAsset"] = "??"; f.Save(); break;
            case "incomplete": f.Item(1)["status"] = "unavailable"; f.Item(1)["unavailableReason"] = "synthetic unavailable"; f.Save(); break;
        }
        var dir = Path.Combine(f.Root, "failed");
        Assert.Equal(2, NavalResearchCommand.Run(Args(f, dir)[1..]));
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "run.json")));
        Assert.False(report.RootElement.GetProperty("success").GetBoolean());
        Assert.False(report.RootElement.TryGetProperty("metrics", out _));
    }
    [Fact]
    public void Cli_rejects_unknown_duplicate_keys_and_unregistered_variant()
    {
        using var f = new NavalFixture();
        foreach (var extra in new[] { new[] { "--cost", "double" }, new[] { "--unknown", "x" }, new[] { "--entry-days", "2" } })
            Assert.Equal(2, NavalResearchCommand.Run(Args(f, Path.Combine(f.Root, Guid.NewGuid().ToString("N")))[1..].Concat(extra).ToArray()));
    }
    [Fact]
    public void Boundary_loss_and_all_fees_exported_even_when_boundary_is_selection_ineligible()
    {
        using var f = new NavalFixture(212);
        var r = NavalBacktestRunner.Run(new(NavalDataset.Load(f.Manifest), NavalFleet.Alpha, f.From, f.To, new(), SpotCostModel.Named("base"), Assets: new[] { "BTC" }));
        // Replace only final candle close with equal-open reference: entry fee+impact => boundary loss.
        using var loss = new NavalFixture(212, f.Start, i => i == 211 ? NavalFixture.Trend(f.Start, i) with { Close = 311m } : NavalFixture.Trend(f.Start, i));
        r = NavalBacktestRunner.Run(new(NavalDataset.Load(loss.Manifest), NavalFleet.Alpha, loss.From, loss.To, new(), SpotCostModel.Named("base"), Assets: new[] { "BTC" }));
        var t = Assert.Single(r.Account.Trades); Assert.True(t.Boundary); Assert.True(t.Net < 0);
        var dir = Path.Combine(f.Root, "boundary"); NavalReports.WriteAccount(dir, r);
        using var a = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "account.json")));
        Assert.Equal(t.Net, a.RootElement.GetProperty("metrics").GetProperty("net").GetDecimal());
        Assert.True(a.RootElement.GetProperty("cashflows")[0].GetProperty("feeQuote").GetDecimal() > 0);
        Assert.True(a.RootElement.GetProperty("trades")[0].GetProperty("boundary").GetBoolean());
        Assert.Contains("Boundary", File.ReadAllText(Path.Combine(dir, "trades.csv")));
    }
}
