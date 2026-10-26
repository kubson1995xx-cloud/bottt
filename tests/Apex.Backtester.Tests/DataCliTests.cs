using System.Security.Cryptography;
using System.Text.Json;
using Apex.Backtester.Data;
using Apex.Backtester.Sim;
using Apex.Core.Model;
using Xunit;

namespace Apex.Backtester.Tests;

public class DataCliTests : IDisposable
{
    readonly string dir = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR")!, "task3-test-" + Guid.NewGuid().ToString("N"));
    public DataCliTests() => Directory.CreateDirectory(dir);
    public void Dispose() => Directory.Delete(dir, true);
    string Csv(string rows)
    {
        string p = Path.Combine(dir, "EURUSD_H1.csv");
        File.WriteAllText(p, "time,open,high,low,close,volume\n" + rows); return p;
    }
    static readonly string Valid = "2024-01-02 12:00,1,1.1,0.9,1,1\n";
    [Theory]
    [InlineData("2024-01-02 13:00,1,0.9,0.8,1,1\n")]
    [InlineData("2024-01-02 12:00,1,1.1,0.9,1,1\n")]
    [InlineData("2024-01-02 11:00,1,1.1,0.9,1,1\n")]
    [InlineData("2024-01-02 12:30,1,1.1,0.9,1,1\n")]
    [InlineData("2024-01-02 13:00,1,1.1,0.9,1,NaN\n")]
    [InlineData("broken,line\n")]
    public void CorruptOhlcTimestampOrderAndRowsAreNotSilentlySkipped(string row) =>
        Assert.ThrowsAny<Exception>(() => BarCsv.Load(Csv(Valid + row), "EURUSD", Tf.H1));

    string Manifest(string basis, object[]? gaps = null)
    {
        string p = Path.Combine(dir, "manifest.json");
        File.WriteAllText(p, JsonSerializer.Serialize(new { source = "deterministic regression fixture (not market data)", timezone = "UTC",
            files = new[] { new { file = "EURUSD_H1.csv", sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, "EURUSD_H1.csv")))).ToLowerInvariant(), quoteBasis = basis, sessionGaps = gaps ?? Array.Empty<object>() } } }));
        return p;
    }
    string[] Args(string manifest, string command = "smoke") => new[] { command, "--data", dir, "--out", Path.Combine(dir,"out"),
        "--manifest", manifest, "--symbol", "EURUSD", "--from", "2024-01-02", "--to", "2024-01-03", "--warmup-from", "2024-01-01", "--cost", "base" };

    [Fact] public void CliRefusesMissingProvenanceInsteadOfDefaultHistoricalRun()
    {
        Csv(Valid);
        Assert.NotEqual(0, Program.Main(Args(Path.Combine(dir,"absent.json"))));
        Assert.True(File.Exists(Path.Combine(dir,"out","run.json")));
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir,"out","run.json")));
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
    }
    [Fact] public void CliRejectsUnknownOptionsAndHashMismatch()
    {
        Csv(Valid); string manifest = Manifest("MID"); File.AppendAllText(Path.Combine(dir,"EURUSD_H1.csv"), "\n");
        Assert.NotEqual(0, Program.Main(Args(manifest)));
        Assert.NotEqual(0, Program.Main(Args(manifest).Concat(new[] { "--invented", "1" }).ToArray()));
    }
    [Fact] public void PartialM15CannotHideAnH1Stop()
    {
        var h1 = AccountingFxTests.Bars("EURUSD", (AccountingFxTests.T, 1));
        var m15 = BarSeries.FromBars("EURUSD", Tf.M15, new[] { new Bar(AccountingFxTests.T,1,1,1,1) });
        Assert.ThrowsAny<InvalidOperationException>(() => new SymbolData(Catalog.Get("EURUSD"), h1, m15));
    }
    [Fact] public void BoundedBidRunRecordsBasisWarmupAndZeroTradesHonestly()
    {
        var rows = Enumerable.Range(0,48).Select(i => $"{new DateTime(2024,1,1).AddHours(i):yyyy-MM-dd HH:mm},1,1.1,0.9,1,1\n");
        Csv(string.Concat(rows));
        Assert.Equal(0, Program.Main(Args(Manifest("BID"))));
        string report = Path.Combine(dir,"out","run.json"); Assert.True(File.Exists(report));
        using var doc = JsonDocument.Parse(File.ReadAllText(report));
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(24, doc.RootElement.GetProperty("result").GetProperty("cycles").GetInt32());
        Assert.Equal("BID", doc.RootElement.GetProperty("data")[0].GetProperty("quoteBasis").GetString());
    }
    [Fact] public void UndeclaredMissingIntervalsAreAnErrorNotSessions()
    {
        Csv(Valid + "2024-01-02 14:00,1,1.1,0.9,1,1\n");
        Assert.NotEqual(0, Program.Main(Args(Manifest("MID"))));
    }
    [Fact] public void BidConversionUsesDeclaredModeledSpreadNotSilentMid()
    {
        Csv(string.Concat(Enumerable.Range(0,48).Select(i => $"{new DateTime(2024,1,1).AddHours(i):yyyy-MM-dd HH:mm},1,1.1,0.9,1,1\n")));
        var loader = new DataLoader(dir,Manifest("BID"),new DateTime(2024,1,1,0,0,0,DateTimeKind.Utc),new DateTime(2024,1,2,0,0,0,DateTimeKind.Utc),new DateTime(2024,1,3,0,0,0,DateTimeKind.Utc),TimeSpan.FromHours(2));
        var sd=loader.Load("EURUSD");
        Assert.Equal(1.000015,sd.H1.Open[36],10);
        Assert.Equal(1.000045,sd.H1.Open[21],10); // explicit rollover model
        Assert.Equal(24,Assert.Single(loader.Evidence).WarmupBars);
    }
    [Fact] public void DeclaredSessionGapIsReportedNotFilledOrMislabelledContinuous()
    {
        var origin=new DateTime(2024,1,1,0,0,0,DateTimeKind.Utc);
        Csv(string.Concat(Enumerable.Range(0,48).Where(i=>i!=37).Select(i => $"{origin.AddHours(i):yyyy-MM-dd HH:mm},1,1.1,0.9,1,1\n")));
        string manifest=Manifest("MID",new object[] { new { from=origin.AddHours(37), to=origin.AddHours(38), reason="declared fixture session closure, not observed market calendar" } });
        var loader=new DataLoader(dir,manifest,origin,origin.AddDays(1),origin.AddDays(2),TimeSpan.FromHours(2));
        Assert.Equal(47,loader.Load("EURUSD").H1.Count);
        Assert.Single(Assert.Single(loader.Evidence).SessionGaps);
    }
    [Fact] public void FxCoverageIsCheckedEvenWhenProfileCannotTrade()
    {
        var d=new InstrumentDef { Symbol="TEST", QuoteCcy="EUR", PipSize=1 };
        var data=new SymbolData(d,AccountingFxTests.Bars("TEST",(AccountingFxTests.T,100)),null);
        Assert.ThrowsAny<InvalidOperationException>(()=>BacktestRunner.Run(new RunSpec { Symbols=new() { data }, From=AccountingFxTests.T,To=AccountingFxTests.T.AddHours(1) }));
    }
    [Fact] public void NoDataIsAnErrorNotSuccessfulZeroEquity()
    {
        var d = new SymbolData(Catalog.Get("EURUSD"), AccountingFxTests.Bars("EURUSD", (AccountingFxTests.T,1)), null);
        Assert.ThrowsAny<InvalidOperationException>(() => BacktestRunner.Run(new RunSpec { Symbols = new() { d }, From = AccountingFxTests.T.AddDays(1), To = AccountingFxTests.T.AddDays(2) }));
    }
}
