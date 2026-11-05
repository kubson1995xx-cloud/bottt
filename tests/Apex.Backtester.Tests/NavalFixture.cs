using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Apex.Core.Naval;

namespace Apex.Backtester.Tests;

// Jawne dane syntetyczne; żadnej interpretacji wyników jako evidence rynkowego.
internal sealed class NavalFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "naval-" + Guid.NewGuid().ToString("N"));
    public string Manifest => Path.Combine(Root, "dataset.json");
    public DateTimeOffset Start { get; }
    public DateTimeOffset From => Start.AddDays(210);
    public DateTimeOffset To { get; }
    public JsonObject Document { get; }
    public NavalFixture(int days = 240, DateTimeOffset? start = null, Func<int, NavalBar>? bar = null, bool all = true)
    {
        Start = start ?? DateTimeOffset.Parse("2023-01-01T00:00:00Z"); To = Start.AddDays(days);
        Directory.CreateDirectory(Root);
        var instruments = new JsonArray();
        foreach (var asset in (all ? NavalFleetProfiles.Get(NavalFleet.Bravo).Assets : NavalFleetProfiles.Get(NavalFleet.Alpha).Assets))
        {
            var symbol = asset + "-USDT";
            var bars = Enumerable.Range(0, days).Select(i => bar?.Invoke(i) ?? Trend(Start, i)).ToArray();
            var csv = "timestamp,open,high,low,close,volume\n" + string.Join("\n", bars.Select(b => string.Join(",", b.OpenUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), D(b.Open), D(b.High), D(b.Low), D(b.Close), D(b.Volume)))) + "\n";
            File.WriteAllText(Path.Combine(Root, symbol + ".csv"), csv, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(Root, symbol + ".json"), JsonSerializer.Serialize(new { baseAsset = asset, quoteAsset = "USDT", instrument = symbol, instrumentMetadata = new { instId = symbol, baseCcy = asset, quoteCcy = "USDT", lotSz = "0.001", minSz = "0.001", tickSz = "0.01" } }));
            instruments.Add(new JsonObject { ["baseAsset"] = asset, ["instrument"] = symbol, ["status"] = "available", ["csvPath"] = symbol + ".csv", ["sha256"] = Hash(csv), ["startUtc"] = Stamp(Start), ["endUtc"] = Stamp(To.AddDays(-1)), ["rows"] = days, ["gaps"] = new JsonArray(), ["listTime"] = Start.AddYears(-1).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), ["metadataPath"] = symbol + ".json", ["lotSize"] = "0.001", ["minSize"] = "0.001", ["tickSize"] = "0.01", ["unavailableReason"] = null });
        }
        Document = new JsonObject { ["schemaVersion"] = "naval-market-data-v1", ["source"] = "SYNTHETIC-CONTROLLED", ["quoteAsset"] = "USDT", ["cutoffUtc"] = Stamp(To), ["priceBasis"] = "TRADE", ["confirmedOnly"] = true, ["instruments"] = instruments };
        Save();
    }
    public static NavalBar Trend(DateTimeOffset start, int i)
    {
        var p = 100m + i;
        return new(start.AddDays(i), p, p + .6m, p - .6m, p + .5m, 1000000m, true);
    }
    public JsonObject Item(int index = 0) => (JsonObject)Document["instruments"]![index]!;
    public void Save() => File.WriteAllText(Manifest, Document.ToJsonString());
    public void EditCsv(Func<string, string> edit, bool rehash = true)
    {
        var path = Path.Combine(Root, Item()["csvPath"]!.GetValue<string>());
        var text = edit(File.ReadAllText(path)); File.WriteAllText(path, text);
        if (rehash) Item()["sha256"] = Hash(text); Save();
    }
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    public static string Stamp(DateTimeOffset t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    static string D(decimal d) => d.ToString(CultureInfo.InvariantCulture);
    public void Dispose() => Directory.Delete(Root, true);
}
