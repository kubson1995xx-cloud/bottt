using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
namespace Apex.Adapters.Okx;
internal sealed class OkxParseException : Exception { internal OkxParseException() : base("InvalidPayload") { } }
internal static class OkxJson
{
    internal static (string Code, ImmutableArray<T> Rows) Parse<T>(byte[] bytes, int? maxRows, CancellationToken ct, bool publicCatalog = false, bool singleton = false)
    {
        using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        Check(doc.RootElement, ct);
        var root = doc.RootElement;
        string code = Str(root, "code", true)!;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) throw new OkxParseException();
        if (code != "0") return (code, []);
        if (maxRows.HasValue && data.GetArrayLength() > maxRows.Value) throw new OkxParseException();
        if (singleton && data.GetArrayLength() != 1) throw new OkxParseException();
        var rows = ImmutableArray.CreateBuilder<T>();
        foreach (var row in data.EnumerateArray()) { ct.ThrowIfCancellationRequested(); rows.Add((T)Row(typeof(T), row, publicCatalog)); }
        return (code, rows.ToImmutable());
    }
    private static void Check(JsonElement value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (value.ValueKind == JsonValueKind.Object) { var names = new HashSet<string>(StringComparer.Ordinal); foreach (var p in value.EnumerateObject()) { if (!names.Add(DecodeName(p))) throw new OkxParseException(); Check(p.Value, ct); } }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Check(item, ct);
        else if (value.ValueKind == JsonValueKind.String) _ = DecodeString(value);
    }
    // JsonDocument defers escaped UTF-16 validation until decoding. Convert only
    // decoder failures, not arbitrary InvalidOperationException in transport/caller code.
    private static string DecodeString(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) throw new OkxParseException();
        try { return value.GetString()!; }
        catch (InvalidOperationException) { throw new OkxParseException(); }
    }
    private static string DecodeName(JsonProperty property)
    {
        try { return property.Name; }
        catch (InvalidOperationException) { throw new OkxParseException(); }
    }
    private static string? Str(JsonElement row, string key, bool required = false)
    {
        if (row.ValueKind != JsonValueKind.Object) throw new OkxParseException();
        if (!row.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) { if (required) throw new OkxParseException(); return null; }
        if (v.ValueKind != JsonValueKind.String) throw new OkxParseException();
        var s = DecodeString(v); if (required && string.IsNullOrEmpty(s)) throw new OkxParseException();
        if (s?.Any(char.IsControl) == true) throw new OkxParseException();
        return s;
    }
    private static bool Bool(JsonElement row, string key)
    { if (!row.TryGetProperty(key, out var v) || v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new OkxParseException(); return v.GetBoolean(); }
    private static OkxDecimal Num(JsonElement row, string key, bool required = false)
    {
        var raw = Str(row, key, required); if (string.IsNullOrEmpty(raw)) return new(raw, null);
        int start = raw[0] is '+' or '-' ? 1 : 0, dot = -1;
        if (start == raw.Length) throw new OkxParseException();
        for (int i = start; i < raw.Length; i++) { char c = raw[i]; if (c == '.' && dot < 0) dot = i; else if (c < '0' || c > '9') throw new OkxParseException(); }
        if (dot == start || dot == raw.Length - 1) throw new OkxParseException();
        string digits = raw[start..].Replace(".", "", StringComparison.Ordinal);
        int scale = dot < 0 ? 0 : raw.Length - dot - 1;
        while (scale > 0 && digits.EndsWith('0')) { digits = digits[..^1]; scale--; }
        // Bound parsing work before BigInteger allocation: decimal has at most 29 significant digits.
        digits = digits.TrimStart('0'); if (digits.Length == 0) digits = "0";
        if (scale > 28 || digits.Length > 29) throw new OkxParseException();
        var n = BigInteger.Parse(digits, CultureInfo.InvariantCulture);
        if (n > ((BigInteger.One << 96) - 1)) throw new OkxParseException();
        uint lo = (uint)(n & uint.MaxValue), mid = (uint)((n >> 32) & uint.MaxValue), hi = (uint)((n >> 64) & uint.MaxValue);
        var value = new decimal(unchecked((int)lo), unchecked((int)mid), unchecked((int)hi), raw[0] == '-', (byte)scale);
        return new(raw, value);
    }
    private static OkxTimestamp Ts(JsonElement row, string key, bool required = false)
    {
        var raw = Str(row, key, required); if (string.IsNullOrEmpty(raw)) return new(raw, null);
        if (raw.Any(c => c < '0' || c > '9') || !long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var ms)) throw new OkxParseException();
        try { return new(raw, DateTimeOffset.FromUnixTimeMilliseconds(ms)); } catch (ArgumentOutOfRangeException) { throw new OkxParseException(); }
    }
    private static ImmutableArray<string> Strings(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Array) throw new OkxParseException();
        return a.EnumerateArray().Select(v => { var s = DecodeString(v); return !string.IsNullOrEmpty(s) ? s : throw new OkxParseException(); }).ToImmutableArray();
    }
    private static object Row(Type type, JsonElement r, bool publicCatalog)
    {
        string S(string k) => Str(r, k, true)!; string? O(string k) => Str(r, k); OkxDecimal N(string k, bool req = false) => Num(r, k, req); OkxTimestamp T(string k, bool req = false) => Ts(r, k, req);
        if (type == typeof(OkxTimeDto)) return new OkxTimeDto(T("ts", true));
        if (type == typeof(OkxAccountConfigDto)) return new OkxAccountConfigDto(S("uid"), S("mainUid"), S("acctLv"), S("perm"), S("ip"), S("feeType"), Bool(r, "autoLoan"), Bool(r, "enableSpotBorrow"));
        if (type == typeof(OkxBalanceDto))
        {
            if (!r.TryGetProperty("details", out var d) || d.ValueKind != JsonValueKind.Array) throw new OkxParseException();
            var details = d.EnumerateArray().Select(v => new OkxBalanceDetailDto(Str(v, "ccy", true)!, Num(v, "cashBal", true), Num(v, "availBal"), Num(v, "frozenBal"), Num(v, "ordFrozen"), Num(v, "eq"), Num(v, "liab"))).ToImmutableArray();
            if (details.Select(v => v.Ccy).Distinct(StringComparer.Ordinal).Count() != details.Length) throw new OkxParseException();
            return new OkxBalanceDto(T("uTime", true), details);
        }
        if (type == typeof(OkxInstrumentDto)) return new OkxInstrumentDto(S("instId"), S("instType"), S("baseCcy"), S("quoteCcy"), publicCatalog && !r.TryGetProperty("tradeQuoteCcyList", out _) ? [] : Strings(r, "tradeQuoteCcyList"), S("state"), N("tickSz", true), N("lotSz", true), N("minSz", true), T("listTime"), T("expTime"), O("groupId"), r.EnumerateObject().Where(p => p.Name.StartsWith("max", StringComparison.Ordinal)).ToImmutableDictionary(p => p.Name, p => Str(r, p.Name), StringComparer.Ordinal));
        if (type == typeof(OkxOrderDto)) return new OkxOrderDto(S("instType"), S("instId"), S("ordId"), O("clOrdId"), S("state"), S("side"), S("tdMode"), N("sz", true), N("accFillSz", true), N("avgPx"), T("cTime", true), T("uTime", true), O("tradeQuoteCcy"));
        if (type == typeof(OkxFillDto)) return new OkxFillDto(S("instType"), S("instId"), S("ordId"), O("clOrdId"), S("tradeId"), S("billId"), S("side"), N("fillPx", true), N("fillSz", true), N("fee", true), S("feeCcy"), N("feeRate"), O("execType"), T("fillTime", true), T("ts", true), O("tradeQuoteCcy"));
        if (type == typeof(OkxBillDto)) return new OkxBillDto(S("billId"), S("ccy"), N("bal", true), N("balChg", true), N("fee"), O("ordId"), O("tradeId"), O("instId"), S("type"), S("subType"), T("ts", true));
        throw new OkxParseException();
    }
}
