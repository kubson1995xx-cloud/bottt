using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Naval;

namespace Apex.Desktop.Naval;
internal enum NavalPublicationAvailability { Available, NotConfigured, Invalid }
internal sealed record NavalPublicationRead(NavalPublicationAvailability Availability, NavalPublicationEnvelope? Envelope, string ReasonCode);
internal interface INavalPublicationReader { ValueTask<NavalPublicationRead> ReadAsync(CancellationToken cancellationToken); }
internal sealed record NavalPublicationProvenance(string InputSha256, string CoreSha256, string BacktesterSha256);
internal sealed record NavalPublicationContent(string Schema, string TemporalClassification, DateTimeOffset SourceClockUtc, string SourceFreshness, NavalPublicationProvenance Provenance, NavalReadPublication Publication);
internal sealed record NavalPublicationEnvelope(string Schema, string TemporalClassification, DateTimeOffset SourceClockUtc, string SourceFreshness, NavalPublicationProvenance Provenance, NavalReadPublication Publication, string ContentSha256);
internal sealed class NavalPublicationLineageException : Exception { }

// Passive capture only: never composes an owner, opens a writer lease or changes any file.
internal sealed class NavalPublicationReader : INavalPublicationReader
{
    internal const int MaximumBytes = 32 * 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        MaxDepth = 64,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new DecimalString(), new CounterString(), new JsonStringEnumConverter(null, false) }
    };
    private readonly string? path; private readonly NavalReadScope scope;
    internal NavalPublicationReader(string? path, NavalReadScope scope) { this.path = path; this.scope = scope; }
    public async ValueTask<NavalPublicationRead> ReadAsync(CancellationToken cancellationToken)
    {
        if (path == null) return new(NavalPublicationAvailability.NotConfigured, null, "NO_PUBLICATION_CONFIGURED");
        try
        {
            NavalPrivatePath.Check(path, false, true);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length > MaximumBytes) return Invalid("PUBLICATION_TOO_LARGE");
            using var memory = new MemoryStream(); var buffer = new byte[65536]; int count;
            while ((count = await file.ReadAsync(buffer, cancellationToken)) != 0)
            { if (memory.Length + count > MaximumBytes) return Invalid("PUBLICATION_TOO_LARGE"); memory.Write(buffer, 0, count); }
            cancellationToken.ThrowIfCancellationRequested();
            var envelope = Parse(memory.ToArray(), scope);
            return new(NavalPublicationAvailability.Available, envelope, "HISTORICAL_OFFLINE");
        }
        catch (OperationCanceledException) { throw; }
        catch (NavalPublicationLineageException) { return Invalid("PUBLICATION_LINEAGE_CHANGED"); }
        catch (NavalAccessException e) { return Invalid(e.Code == "NAVAL_PERMISSIONS_INVALID" ? "PUBLICATION_PERMISSIONS_INVALID" : "PUBLICATION_UNAVAILABLE"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Invalid("PUBLICATION_UNAVAILABLE"); }
        catch (Exception e) when (e is not OutOfMemoryException) { return Invalid("PUBLICATION_INVALID"); }
    }
    private static NavalPublicationRead Invalid(string code) => new(NavalPublicationAvailability.Invalid, null, code);
    internal static NavalPublicationEnvelope Parse(byte[] bytes, NavalReadScope scope)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException();
        using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        // Typed construction gives a canonical structural template, not trust. Compare every raw
        // property recursively, including getter-only fields, before admitting the typed object.
        // This uses no runtime reflection or producer-internal API.
        var e = JsonSerializer.Deserialize<NavalPublicationEnvelope>(bytes, Json) ?? throw new InvalidDataException();
        using var typed = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(e, Json));
        Shape(doc.RootElement, typed.RootElement, "", false);
        if (e.Schema != "naval-shared-publication-export-v1" || e.TemporalClassification != "HISTORICAL_OFFLINE_CHECKPOINT" || e.SourceFreshness != "UNKNOWN" || e.SourceClockUtc == default ||
            !NavalAccessPolicy.Hex(e.ContentSha256) || !NavalAccessPolicy.Hex(e.Provenance.InputSha256) || !NavalAccessPolicy.Hex(e.Provenance.CoreSha256) || !NavalAccessPolicy.Hex(e.Provenance.BacktesterSha256)) throw new InvalidDataException();
        var content = new NavalPublicationContent(e.Schema, e.TemporalClassification, e.SourceClockUtc, e.SourceFreshness, e.Provenance, e.Publication);
        byte[] digest = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(content, Json));
        if (!CryptographicOperations.FixedTimeEquals(digest, Convert.FromHexString(e.ContentSha256))) throw new InvalidDataException();
        var p = e.Publication; var j = p.Journal; var c = j.Checkpoint;
        if (j.Batches.Count > 512 || c.DurableMode is not ("STOP" or "RUNNING") || !CheckpointCodes.Contains(c.ReasonCode)) throw new InvalidDataException();
        var page = NavalReadJournal.ReadPage(p, new(j.Epoch, j.ScopeId, p.AsOfSequence), 100);
        if (page.Gap || page.ResetRequired) throw new InvalidDataException();
        if (j.ScopeId != scope.PublicationScopeId || c.PortfolioId != scope.PortfolioId || c.ConfigDigest != scope.ConfigDigest ||
            c.Accounts.Count != scope.Accounts.Count || c.Accounts.Select(a => a.Account).Distinct().Count() != c.Accounts.Count || !c.Accounts.Select(a => a.Account).ToHashSet().SetEquals(scope.Accounts)) throw new NavalPublicationLineageException();
        return e;
    }
    // Public checkpoint reason vocabulary of the accepted v1 projection (not a financial validator).
    private static readonly HashSet<string> CheckpointCodes = new(StringComparer.Ordinal)
    {"INITIAL_STOP","RECONCILED_EXPLICIT_START_REQUIRED","EXPLICIT_OFFLINE_START","EXECUTION_OVERRUN","COMMAND_CONFLICT","RECEIPT_CONFLICT",
     "SOURCE_ORDER_OR_TIME","FINAL_COVERAGE_REQUIRED","FILL_CONFLICT","LATE_FILL_RECONCILE_REQUIRED","UNOWNED_FILL","FEE_OVERRUN","OVERSELL",
     "ENTRY_OVERRUN","CASH_OVERRUN","TERMINAL_CONFLICT","INCOHERENT_MARKS","INCOHERENT_CASH_MARKS","INCOHERENT_ORDER_HOLDINGS","ECONOMIC_OVERFLOW",
     "EVIDENCE_APPLIED","STARTED","RESERVED","SIGNAL_CONSUMED","NOT_RUNNING_FRESH","SIGNAL_TIME","INVALID_LONG_PROTECTION_OR_QUANTUM",
     "EXECUTABLE_PRICE","DECIMAL_OVERFLOW","SYMBOL_OWNED","EXPLICIT_SYNTHETIC_STOP","DAILY_LATCH","DRAWDOWN_LATCH",
     "TRADE_RISK","ACCOUNT_CASH","ACCOUNT_NOTIONAL","CASH_BREACH","LEVERAGE_BREACH","ACCOUNT_NOTIONAL_BREACH","EQUITY_UNKNOWN",
     "SLOT_LIMIT","DAILY_PROJECTED","DRAWDOWN_PROJECTED","CENTRAL_NOTIONAL","UNCERTAIN","OFFLINE_STOP"};
    private static readonly HashSet<string> NullableFields = new(StringComparer.Ordinal) { "captureUtc", "receiveUtc", "reconciledUtc", "historicalLiquidationEquity", "brokerAccountId", "brokerEquity", "realizedPnl", "unrealizedPnl" };
    private static readonly HashSet<string> OptionalFactFields = new(StringComparer.Ordinal) { "account", "fleet", "reservationId", "fillKey", "reservation", "fill", "coverage", "operation", "intent" };
    private static void Shape(JsonElement raw, JsonElement canonical, string name, bool fact)
    {
        if (raw.ValueKind != canonical.ValueKind) throw new InvalidDataException();
        if (raw.ValueKind == JsonValueKind.Null)
        { if (!NullableFields.Contains(name) && !(fact && OptionalFactFields.Contains(name))) throw new InvalidDataException(); return; }
        if (raw.ValueKind == JsonValueKind.Object)
        {
            var properties = canonical.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal); var seen = new HashSet<string>(StringComparer.Ordinal);
            bool childFact = canonical.TryGetProperty("factId", out _);
            foreach (var property in raw.EnumerateObject())
            { if (!seen.Add(property.Name) || !properties.TryGetValue(property.Name, out var value)) throw new InvalidDataException(); Shape(property.Value, value, property.Name, childFact); }
            if (seen.Count != properties.Count) throw new InvalidDataException();
        }
        else if (raw.ValueKind == JsonValueKind.Array)
        { if (raw.GetArrayLength() != canonical.GetArrayLength()) throw new InvalidDataException(); for (int i = 0; i < raw.GetArrayLength(); i++) Shape(raw[i], canonical[i], name, false); }
        else if (raw.ValueKind == JsonValueKind.String)
        {
            // Literal/enum/getter-only values may not be normalized by deserialization.
            if (name is "currentQuality" or "runtimeReadiness" or "coverage" or "brokerState" or "kind" or "fleet" or "environment" or "stage")
                if (raw.GetString() != canonical.GetString()) throw new InvalidDataException();
        }
    }
    private sealed class CounterString : JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => r.TokenType == JsonTokenType.String && long.TryParse(r.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out long v) ? v : throw new JsonException();
        public override void Write(Utf8JsonWriter w, long v, JsonSerializerOptions o) => w.WriteStringValue(v.ToString(CultureInfo.InvariantCulture));
    }
    private sealed class DecimalString : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
        {
            if (r.TokenType != JsonTokenType.String) throw new JsonException();
            string text = r.GetString()!;
            if (!ExactlyRepresentable(text.AsSpan()) || !decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal value)) throw new JsonException();
            return value;
        }
        private static bool ExactlyRepresentable(ReadOnlySpan<char> text)
        {
            // Decimal.TryParse can round excess precision (even to zero) while returning true.
            // Validate the exact base-10 coefficient/scale first, without a growing big integer
            // or canonical-string equality. Work is linear in the already bounded input token.
            int start = text.Length > 0 && text[0] is '+' or '-' ? 1 : 0;
            int point = -1, digits = 0;
            for (int i = start; i < text.Length; i++)
            {
                if (text[i] is >= '0' and <= '9') digits++;
                else if (text[i] == '.' && point < 0) point = i;
                else return false;
            }
            if (digits == 0) return false;
            int end = text.Length;
            if (point >= 0)
            {
                // Only fractional trailing zeros can be discarded without changing the value.
                while (end > point + 1 && text[end - 1] == '0') end--;
                if (end - point - 1 > 28) return false;
            }
            decimal coefficient = 0;
            try
            {
                for (int i = start; i < end; i++)
                    if (i != point) coefficient = checked(coefficient * 10 + (text[i] - '0'));
            }
            catch (OverflowException) { return false; }
            // Integer-only arithmetic above is exact and enforces the 96-bit coefficient bound.
            return true;
        }
        public override void Write(Utf8JsonWriter w, decimal v, JsonSerializerOptions o) => w.WriteStringValue(v.ToString(CultureInfo.InvariantCulture));
    }
}
