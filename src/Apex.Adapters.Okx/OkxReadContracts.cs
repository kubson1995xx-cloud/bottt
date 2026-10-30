using System.Collections.Immutable;
using System.Text.Json;
namespace Apex.Adapters.Okx;
public sealed record OkxReadScope(string CredentialBindingId, string ExpectedUid);
public sealed record OkxOrderRef(string InstId, string OrdId);
public enum OkxHistoryWindow { Recent, Archive }
public sealed record OkxPageRequest(string? After = null, int Limit = 100, DateTimeOffset? BeginUtc = null, DateTimeOffset? EndUtc = null);
public sealed record OkxSnapshotRequest(DateTimeOffset FromUtc, DateTimeOffset ToUtc, IReadOnlyList<OkxOrderRef> UnresolvedOrders);
public enum OkxReadStatus { Observed, Failed }
public enum OkxCollectionStatus { Collected, Incomplete, Failed }
public enum OkxCoverage { Unknown, Gap, Conflict }
public sealed class OkxRead<T>
{
    public OkxReadStatus Status { get; }
    public ImmutableArray<T> Rows { get; }
    public string? ErrorCode { get; }
    public string Endpoint { get; }
    public DateTimeOffset RequestUtc { get; }
    public DateTimeOffset ReceiveUtc { get; }
    public bool IsPrivate { get; }
    public string? ResponseSha256 { get; }
    internal OkxRead(OkxReadStatus status, IEnumerable<T> rows, string? error, string endpoint, DateTimeOffset request, DateTimeOffset receive, bool isPrivate, string? hash = null)
    { Status = status; Rows = status == OkxReadStatus.Observed ? rows.ToImmutableArray() : ImmutableArray<T>.Empty; ErrorCode = error; Endpoint = endpoint; RequestUtc = request; ReceiveUtc = receive; IsPrivate = isPrivate; ResponseSha256 = hash; }
    public override string ToString() => $"OKX {Endpoint} {Status} {ErrorCode} rows={Rows.Length}";
}
public sealed record OkxPageTrace(string Endpoint, OkxPageRequest Query, int PageIndex, int RowCount, string? ResponseSha256, DateTimeOffset? EarliestTs, DateTimeOffset? LatestTs, DateTimeOffset? EarliestFillTime, DateTimeOffset? LatestFillTime, bool Exhausted, bool BoundaryReached, string? ErrorCode);
public sealed record OkxCurrencyDiagnostic(string Currency, decimal? CashBefore, decimal? CashAfter, decimal? BillChange, decimal? CashDifference, bool? ChainConsistent);
public sealed record OkxFillCashflowDiagnostic(string InstId, string TradeId, string BaseCurrency, string QuoteCurrency, decimal? BaseChange, decimal? QuoteChange, decimal? SignedFee, string FeeCurrency, bool BillLinked);
[System.Text.Json.Serialization.JsonConverter(typeof(OkxSnapshotExportGuard))]
public sealed class OkxReadSnapshot
{
    internal OkxReadSnapshot() { }
    public string Schema => "okx-demo-read-snapshot-v1";
    public string Region => "EEA";
    public string Environment => "DEMO_REQUESTED / READ_ONLY / NO_BROKER_AUTHORITY";
    public Guid Generation { get; internal init; } = Guid.NewGuid();
    public string? CredentialBindingId { get; internal init; }
    public string? Uid { get; internal init; }
    public DateTimeOffset StartUtc { get; internal init; }
    public DateTimeOffset EndUtc { get; internal init; }
    public DateTimeOffset FromUtc { get; internal init; }
    public DateTimeOffset ToUtc { get; internal init; }
    public OkxRead<OkxAccountConfigDto>? ConfigBefore { get; internal init; }
    public OkxRead<OkxAccountConfigDto>? ConfigAfter { get; internal init; }
    public OkxRead<OkxBalanceDto>? BalanceBefore { get; internal init; }
    public OkxRead<OkxBalanceDto>? BalanceAfter { get; internal init; }
    public ImmutableArray<OkxOrderDto> PendingBefore { get; internal init; } = [];
    public ImmutableArray<OkxOrderDto> PendingAfter { get; internal init; } = [];
    public ImmutableArray<OkxInstrumentDto> Instruments { get; internal init; } = [];
    public ImmutableArray<OkxOrderDto> OrderDetails { get; internal init; } = [];
    public ImmutableArray<OkxFillDto> Fills { get; internal init; } = [];
    public ImmutableArray<OkxBillDto> Bills { get; internal init; } = [];
    public ImmutableArray<OkxPageTrace> Trace { get; internal init; } = [];
    public ImmutableArray<OkxFillCashflowDiagnostic> FillCashflows { get; internal init; } = [];
    public ImmutableArray<OkxCurrencyDiagnostic> CurrencyDiagnostics { get; internal init; } = [];
    public ImmutableArray<string> Reasons { get; internal init; } = [];
    public OkxCollectionStatus CollectionStatus { get; internal init; }
    public OkxCoverage Coverage { get; internal init; }
    public string ExportRedactedDiagnostics(bool optIn) => optIn ? JsonSerializer.Serialize(new { Schema, Region, Environment, Generation, StartUtc, EndUtc, FromUtc, ToUtc, CollectionStatus, Coverage, Reasons, FillCount = Fills.Length, BillCount = Bills.Length, PageCount = Trace.Length }) : throw new InvalidOperationException("ExportOptInRequired");
    public override string ToString() => $"{Schema} {Generation} {CollectionStatus} {Coverage}";
}
internal sealed class OkxSnapshotExportGuard : System.Text.Json.Serialization.JsonConverter<OkxReadSnapshot>
{
    public override OkxReadSnapshot Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new JsonException("SnapshotImportDenied");
    public override void Write(Utf8JsonWriter writer, OkxReadSnapshot value, JsonSerializerOptions options) => throw new JsonException("UseExplicitRedactedExport");
}
