using Apex.Backtester.Naval;
using Apex.Core.Naval;
using Xunit;

namespace Apex.Backtester.Tests;

public class NavalDatasetTests
{
    [Fact]
    public void One_snapshot_hash_exact_decimals_and_halfopen_coverage()
    {
        using var f = new NavalFixture();
        f.EditCsv(s => s.Replace("100,100.6,99.4,100.5", "100.00000000,100.60000000,99.40000000,100.50000000"));
        var d = NavalDataset.Load(f.Manifest);
        d.ValidateCoverage(NavalFleet.Alpha, f.From, f.To);
        var btc = d.Instruments["BTC"];
        Assert.Contains("100.00000000", btc.CsvSnapshot);
        Assert.Equal(f.Item()["sha256"]!.GetValue<string>(), btc.Sha256);
        f.EditCsv(s => s.Replace("100.00000000", "999.00000000"));
        Assert.Equal(100m, btc.Series.Bars[0].Open);
        Assert.Equal(f.To.AddDays(-1), btc.Series.Bars[^1].OpenUtc);
        Assert.Throws<InvalidDataException>(() => d.ValidateCoverage(NavalFleet.Alpha, f.From, f.To.AddDays(1)));
    }
    [Theory]
    [InlineData("hash")] [InlineData("ohlc")] [InlineData("duplicate")] [InlineData("gap")]
    [InlineData("quote")] [InlineData("metadata")] [InlineData("traversal")] [InlineData("symlink")]
    [InlineData("utc")] [InlineData("decimal")] [InlineData("confirmed")] [InlineData("listing")]
    public void Invalid_dataset_fails_closed(string kind)
    {
        using var f = new NavalFixture();
        switch (kind)
        {
            case "hash": f.EditCsv(s => s + "\n", false); break;
            case "ohlc": f.EditCsv(s => s.Replace("100,100.6,99.4,100.5", "100,99,99.4,100.5")); break;
            case "duplicate": f.EditCsv(s => s.Replace("2023-01-02", "2023-01-01")); break;
            case "gap": f.EditCsv(s => string.Join("\n", s.Split('\n').Where(x => !x.StartsWith("2023-01-02")))); break;
            case "quote": f.Document["quoteAsset"] = "UNKNOWN"; break;
            case "metadata": File.Delete(Path.Combine(f.Root, "BTC-USDT.json")); break;
            case "traversal": f.Item()["csvPath"] = "../outside.csv"; break;
            case "symlink": File.CreateSymbolicLink(Path.Combine(f.Root, "link.csv"), Path.Combine(f.Root, "BTC-USDT.csv")); f.Item()["csvPath"] = "link.csv"; break;
            case "utc": f.EditCsv(s => s.Replace("T00:00:00Z", "T00:00:00+01:00")); break;
            case "decimal": f.EditCsv(s => s.Replace("100,100.6", "1e2,100.6")); break;
            case "confirmed": f.Document["confirmedOnly"] = false; break;
            case "listing": f.Item()["listTime"] = f.From.ToUnixTimeMilliseconds().ToString(); break;
        }
        f.Save();
        Assert.Throws<InvalidDataException>(() => NavalDataset.Load(f.Manifest).ValidateCoverage(NavalFleet.Alpha, f.From, f.To));
    }
    [Fact]
    public void Unavailable_fleet_member_is_not_a_zero_result()
    {
        using var f = new NavalFixture();
        f.Item(1)["status"] = "unavailable"; f.Item(1)["unavailableReason"] = "controlled unavailable"; f.Save();
        var d = NavalDataset.Load(f.Manifest);
        Assert.Throws<InvalidDataException>(() => d.ValidateCoverage(NavalFleet.Alpha, f.From, f.To));
    }
}
