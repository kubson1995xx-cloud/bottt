using System.Collections.Immutable;
namespace Apex.Adapters.Okx.Execution;

public enum OkxObservationStatus { Observed, Failed }
public sealed record OkxObservationContext(Guid ConnectionId, Guid CaptureId);
/// <summary>Raw collection only; no authentication, financial authority, readiness or coverage claim.</summary>
public sealed record OkxObservationRead<T>
{
    public OkxObservationStatus Status { get; }
    public OkxExecutionScope RequestedScope { get; }
    public OkxObservationContext Context { get; }
    public string Endpoint { get; }
    public string ExactQuery { get; }
    public DateTimeOffset RequestUtc { get; }
    public DateTimeOffset ReceiveUtc { get; }
    public long RequestMonotonic { get; }
    public long ReceiveMonotonic { get; }
    public string? ResponseDigest { get; }
    public IReadOnlyList<T> Rows { get; }
    public string? ErrorCode { get; }
    internal OkxObservationRead(OkxExecutionScope scope, OkxObservationContext context, string endpoint, string query,
        DateTimeOffset requestUtc, DateTimeOffset receiveUtc, long requestMono, long receiveMono,
        string? digest, IEnumerable<T> rows, string? error)
    {
        RequestedScope=scope; Context=context; Endpoint=endpoint; ExactQuery=query;
        RequestUtc=requestUtc; ReceiveUtc=receiveUtc; RequestMonotonic=requestMono; ReceiveMonotonic=receiveMono;
        ResponseDigest=digest; ErrorCode=error; Status=error is null?OkxObservationStatus.Observed:OkxObservationStatus.Failed;
        Rows=error is null?rows.ToImmutableArray():ImmutableArray<T>.Empty;
    }
    public override string ToString()=>$"OKX observation {Status} {Endpoint} {ErrorCode}";
}
/// <summary>Lifetime synchronization only; cannot issue account or financial authority.</summary>
public interface IOkxObservationReadFence
{
    bool CanObserve(OkxExecutionScope requestedScope, OkxObservationContext context, DateTimeOffset utcNow);
}
public sealed record OkxExecutionAccountDto(string Uid, string MainUid, string AccountLevel, string Permissions,
    string Ip, string FeeType, bool AutoLoan, bool EnableSpotBorrow, bool SpotBorrowAutoRepay,
    string AccountType, string SpotRoleType, string KycLevel)
{ public override string ToString()=>"OKX account observation [REDACTED]"; }
public sealed record OkxExecutionBalanceDto(OkxTimestamp UTime, ImmutableArray<OkxExecutionBalanceDetailDto> Details);
public sealed record OkxExecutionBalanceDetailDto(string Currency, OkxDecimal Cash, OkxDecimal Available,
    OkxDecimal Frozen, OkxDecimal OrderFrozen, OkxDecimal Equity, OkxDecimal Liability, OkxTimestamp UTime,
    OkxDecimal Interest, OkxDecimal CrossLiability, OkxDecimal IsolatedLiability, OkxDecimal StrategyEquity, OkxDecimal FixedBalance);
public sealed record OkxObservationMaximum(OkxDecimal Amount, string Unit);
public sealed record OkxExecutionInstrumentDto(string Instrument, string InstrumentType, string BaseCurrency,
    string CatalogQuote, ImmutableArray<string> TradeQuoteCurrencies, string? GroupId, string State,
    OkxDecimal TickSize, OkxDecimal LotSize, OkxDecimal MinimumSize, OkxTimestamp ListTime, OkxTimestamp ExpiryTime,
    ImmutableDictionary<string,OkxObservationMaximum> MaxValues);
public sealed record OkxExecutionFeeDto(string InstrumentType, OkxTimestamp Timestamp,
    ImmutableDictionary<string,OkxExecutionFeeGroupDto> Groups);
public sealed record OkxExecutionFeeGroupDto(string GroupId, OkxDecimal Maker, OkxDecimal Taker,
    OkxDecimal RpiMaker, OkxDecimal ElpMaker);
