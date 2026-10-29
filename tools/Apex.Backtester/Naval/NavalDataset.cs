using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Apex.Core.Naval;

namespace Apex.Backtester.Naval;
public sealed record NavalInstrument(NavalSeries Series, decimal LotSize, decimal MinSize, decimal TickSize,
    DateTimeOffset ListTime, string Sha256, string MetadataSha256, string CsvSnapshot);

// Lokalny, fail-closed snapshot. Brak sieci, interpolacji i uprawnień wykonawczych.
public sealed class NavalDataset
{
    public string Source { get; private init; } = "";
    public string QuoteAsset { get; private init; } = "";
    public string ManifestSha256 { get; private init; } = "";
    public DateTimeOffset CutoffUtc { get; private init; }
    public IReadOnlyDictionary<string, NavalInstrument> Instruments { get; private init; } = new Dictionary<string, NavalInstrument>();
    public IReadOnlyDictionary<string, string> Unavailable { get; private init; } = new Dictionary<string, string>();
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static NavalDataset Load(string manifest)
    {
        try { return Read(manifest); }
        catch (Exception e) when (e is not InvalidDataException) { throw new InvalidDataException("Dataset refused: " + e.Message, e); }
    }
    static NavalDataset Read(string manifest)
    {
        manifest = Path.GetFullPath(manifest);
        var root = Path.GetDirectoryName(manifest)!;
        var bytes = File.ReadAllBytes(SafePath(root, Path.GetFileName(manifest)));
        using var document = Parse(bytes);
        var m = document.RootElement;
        if (Text(m, "schemaVersion") != "naval-market-data-v1" || Text(m, "priceBasis") != "TRADE"
            || !m.GetProperty("confirmedOnly").GetBoolean()) throw new InvalidDataException("Schema/TRADE/confirmed1 required");
        var source = Text(m, "source"); var quote = Text(m, "quoteAsset");
        if (quote != "USDT" && quote != "USDC") throw new InvalidDataException("Unknown quote; no implicit FX: " + quote);
        var cutoff = Utc(Text(m, "cutoffUtc"));
        var loaded = new Dictionary<string, NavalInstrument>(StringComparer.Ordinal);
        var missing = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in m.GetProperty("instruments").EnumerateArray())
        {
            var asset = Text(item, "baseAsset"); var symbol = Text(item, "instrument");
            if (!NavalFleetProfiles.Get(NavalFleet.Bravo).Assets.Contains(asset) || symbol != asset + "-" + quote || !seen.Add(asset))
                throw new InvalidDataException("Unknown/duplicate instrument: " + symbol);
            if (item.TryGetProperty("quoteAsset", out var iq) && iq.GetString() != quote) throw new InvalidDataException("Mixed quote");
            var status = Text(item, "status");
            if (status == "unavailable") { missing.Add(asset, Text(item, "unavailableReason")); continue; }
            if (status != "available") throw new InvalidDataException("Unknown availability");
            // EndUtc jest ostatnim opening timestamp, nie końcem exclusive.
            var start = Utc(Text(item, "startUtc")); var end = Utc(Text(item, "endUtc"));
            var listingToken = Text(item, "listTime");
            var listing = Regex.IsMatch(listingToken, "^[0-9]+$")
                ? DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(listingToken, CultureInfo.InvariantCulture)) : Utc(listingToken, false);
            var lot = Positive(Text(item, "lotSize")); var min = Positive(Text(item, "minSize")); var tick = Positive(Text(item, "tickSize"));
            if (item.GetProperty("gaps").GetArrayLength() != 0) throw new InvalidDataException("Declared data gaps: " + symbol);
            var metadataBytes = File.ReadAllBytes(SafePath(root, Text(item, "metadataPath")));
            using var metadata = Parse(metadataBytes);
            var md = metadata.RootElement.GetProperty("instrumentMetadata");
            if (Text(md, "instId") != symbol || Text(md, "baseCcy") != asset || Text(md, "quoteCcy") != quote
                || Positive(Text(md, "lotSz")) != lot || Positive(Text(md, "minSz")) != min || Positive(Text(md, "tickSz")) != tick)
                throw new InvalidDataException("Missing/conflicting CURRENT instrument metadata: " + symbol);
            var csvBytes = File.ReadAllBytes(SafePath(root, Text(item, "csvPath")));
            var hash = Text(item, "sha256");
            if (!Regex.IsMatch(hash, "^[a-f0-9]{64}$") || Hash(csvBytes) != hash) throw new InvalidDataException("CSV hash mismatch: " + symbol);
            var csv = new UTF8Encoding(false, true).GetString(csvBytes);
            var lines = csv.Split('\n');
            if (lines[0].TrimEnd('\r') != "timestamp,open,high,low,close,volume") throw new InvalidDataException("Exact CSV header required");
            var bars = new List<NavalBar>();
            for (int i = 1; i < lines.Length; i++)
            {
                if (i == lines.Length - 1 && lines[i] == "") continue;
                var fields = lines[i].TrimEnd('\r').Split(',');
                if (fields.Length != 6) throw new InvalidDataException("Malformed CSV row: " + i);
                var t = Utc(fields[0]); var o = Positive(fields[1]); var h = Positive(fields[2]);
                var l = Positive(fields[3]); var c = Positive(fields[4]); var v = Number(fields[5]);
                if (v < 0 || l > o || l > c || h < o || h < c || l > h || t >= cutoff
                    || (bars.Count > 0 && t != bars[^1].OpenUtc.AddDays(1))) throw new InvalidDataException("Invalid OHLCV/continuity/cutoff: " + symbol + ":" + i);
                bars.Add(new(t, o, h, l, c, v, true));
            }
            if (bars.Count == 0 || bars.Count != item.GetProperty("rows").GetInt32()
                || bars[0].OpenUtc != start || bars[^1].OpenUtc != end) throw new InvalidDataException("CSV coverage mismatch: " + symbol);
            loaded.Add(asset, new(new(symbol, asset, quote, source, "TRADE", bars.AsReadOnly()), lot, min, tick, listing, hash, Hash(metadataBytes), csv));
        }
        if (seen.Count == 0) throw new InvalidDataException("Empty instrument manifest");
        return new() { Source = source, QuoteAsset = quote, ManifestSha256 = Hash(bytes), CutoffUtc = cutoff,
            Instruments = new ReadOnlyDictionary<string, NavalInstrument>(loaded), Unavailable = new ReadOnlyDictionary<string, string>(missing) };
    }
    public void ValidateCoverage(NavalFleet fleet, DateTimeOffset from, DateTimeOffset to, int warmupDays = 200)
        => ValidateAssets(NavalFleetProfiles.Get(fleet).Assets, from, to, warmupDays);
    public void ValidateAssets(IEnumerable<string> assets, DateTimeOffset from, DateTimeOffset to, int warmupDays = 200)
    {
        if (from.Offset != TimeSpan.Zero || to.Offset != TimeSpan.Zero || from.TimeOfDay != TimeSpan.Zero
            || to.TimeOfDay != TimeSpan.Zero || from >= to || to > CutoffUtc || warmupDays < 200) throw new InvalidDataException("Invalid UTC half-open period/warmup");
        foreach (var asset in assets.Append("BTC").Distinct(StringComparer.Ordinal))
        {
            if (!Instruments.TryGetValue(asset, out var i)) throw new InvalidDataException("Unavailable: " + asset + ": " + (Unavailable.TryGetValue(asset, out var reason) ? reason : "missing member"));
            if (i.Series.Bars[0].OpenUtc > from.AddDays(-warmupDays) || i.ListTime > from.AddDays(-warmupDays)
                || i.Series.Bars[^1].OpenUtc < to.AddDays(-1)) throw new InvalidDataException("Incomplete coverage/listing/warmup: " + asset);
        }
    }
    internal static DateTimeOffset Utc(string token, bool daily = true)
    {
        if (!token.EndsWith('Z') || !DateTimeOffset.TryParseExact(token, new[] { "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'" }, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) || (daily && t.TimeOfDay != TimeSpan.Zero))
            throw new InvalidDataException("UTC Z timestamp required: " + token);
        return t;
    }
    static decimal Positive(string s) { var d = Number(s); return d > 0 ? d : throw new InvalidDataException("Positive metadata/OHLC required"); }
    static decimal Number(string s)
    {
        string Canonical(string t) { var p = t.Split('.'); var whole = p[0].TrimStart('0'); if (whole == "") whole = "0"; var frac = p.Length > 1 ? p[1].TrimEnd('0') : ""; return whole + (frac == "" ? "" : "." + frac); }
        if (!Regex.IsMatch(s, "^[0-9]+(?:\\.[0-9]+)?$") || !decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d)
            || Canonical(s) != Canonical(d.ToString(CultureInfo.InvariantCulture))) throw new InvalidDataException("Exact invariant decimal required: " + s);
        return d;
    }
    static string Text(JsonElement e, string key)
    {
        var value = e.GetProperty(key).GetString();
        return !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl) ? value : throw new InvalidDataException("Missing text: " + key);
    }
    static JsonDocument Parse(byte[] bytes)
    {
        var doc = JsonDocument.Parse(bytes);
        void Check(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in element.EnumerateObject()) { if (!names.Add(p.Name)) throw new InvalidDataException("Duplicate JSON key"); Check(p.Value); }
            }
            else if (element.ValueKind == JsonValueKind.Array) foreach (var e in element.EnumerateArray()) Check(e);
        }
        try { Check(doc.RootElement); return doc; } catch { doc.Dispose(); throw; }
    }
    static string SafePath(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(s => s is ".." or "." or "")) throw new InvalidDataException("Unsafe dataset path");
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidDataException("Dataset path escape");
        for (FileSystemInfo? p = new FileInfo(full); p != null; p = p is FileInfo f ? f.Directory : ((DirectoryInfo)p).Parent)
            if (p.LinkTarget != null) throw new InvalidDataException("Dataset symlinks refused");
        return full;
    }
}
