using System.Collections.Immutable;
namespace Apex.Adapters.Okx.Execution;

public enum OkxObservationStatus { Observed, Failed }
public sealed record OkxObservationContext(Guid ConnectionId, Guid CaptureId);
public sealed record OkxObservationDiagnostic(string Phase,int? HttpStatus,string? OkxCode,OkxEnrollmentPayloadFailure? Payload);
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
    public OkxObservationDiagnostic? Diagnostic { get; internal init; }
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


public enum OkxObservationWindow { Recent, Archive }
public enum OkxObservationSurface { Pending, History, HistoryArchive, Fills, FillsArchive, Bills, BillsArchive }
public enum OkxObservationCursorDirection { After, Before }
public sealed record OkxObservationCursor
{
    public OkxObservationSurface Surface { get; }
    public string? Instrument { get; }
    public OkxObservationCursorDirection Direction { get; }
    public string Value { get; }
    public OkxObservationCursor(OkxObservationSurface Surface,string? Instrument,OkxObservationCursorDirection Direction,string Value)
    {
        if(!Enum.IsDefined(Surface)||!Enum.IsDefined(Direction)||Instrument is not null&&!Grammar.Instrument(Instrument)||!Grammar.OrderId(Value))throw RecoveryRequest.Invalid();
        this.Surface=Surface;this.Instrument=Instrument;this.Direction=Direction;this.Value=Value;
    }
    public override string ToString()=>"OKX observation cursor";
}
public sealed record OkxObservationOrderQuery
{
    public string Instrument { get; }
    public string? OrderId { get; }
    public string? ClientOrderId { get; }
    public OkxObservationOrderQuery(string Instrument,string? OrderId,string? ClientOrderId)
    {
        if(!Grammar.Instrument(Instrument)||OrderId is null&&ClientOrderId is null||OrderId is not null&&!Grammar.OrderId(OrderId)||ClientOrderId is not null&&!Grammar.ClientId(ClientOrderId))throw RecoveryRequest.Invalid();
        this.Instrument=Instrument;this.OrderId=OrderId;this.ClientOrderId=ClientOrderId;
    }
    public override string ToString()=>"OKX order query";
}
public sealed record OkxObservationPageQuery
{
    public string? Instrument { get; }
    public string? OrderId { get; }
    public DateTimeOffset? BeginUtc { get; }
    public DateTimeOffset? EndUtc { get; }
    public OkxObservationCursor? Cursor { get; }
    public int Limit { get; }
    public OkxObservationPageQuery(string? Instrument,string? OrderId,DateTimeOffset? BeginUtc,DateTimeOffset? EndUtc,OkxObservationCursor? Cursor,int Limit=100)
    {
        if(Instrument is not null&&!Grammar.Instrument(Instrument)||OrderId is not null&&!Grammar.OrderId(OrderId)||Limit is <1 or >100||
            BeginUtc.HasValue!=EndUtc.HasValue||BeginUtc.HasValue&&(!RecoveryRequest.Time(BeginUtc.Value)||!RecoveryRequest.Time(EndUtc!.Value)||BeginUtc>=EndUtc))throw RecoveryRequest.Invalid();
        this.Instrument=Instrument;this.OrderId=OrderId;this.BeginUtc=BeginUtc;this.EndUtc=EndUtc;this.Cursor=Cursor;this.Limit=Limit;
    }
    public override string ToString()=>"OKX page query";
}
internal static class RecoveryRequest
{
    internal static string Endpoint(OkxObservationSurface surface)=>surface switch
    {
        OkxObservationSurface.Pending=>"/api/v5/trade/orders-pending",
        OkxObservationSurface.History=>"/api/v5/trade/orders-history",
        OkxObservationSurface.HistoryArchive=>"/api/v5/trade/orders-history-archive",
        OkxObservationSurface.Fills=>"/api/v5/trade/fills",
        OkxObservationSurface.FillsArchive=>"/api/v5/trade/fills-history",
        OkxObservationSurface.Bills=>"/api/v5/account/bills",
        OkxObservationSurface.BillsArchive=>"/api/v5/account/bills-archive",
        _=>""
    };
    internal static bool Valid(OkxObservationSurface surface,OkxObservationPageQuery? p)
    {
        if(!Enum.IsDefined(surface)||p is null)return false;
        bool pending=surface==OkxObservationSurface.Pending, bills=surface is OkxObservationSurface.Bills or OkxObservationSurface.BillsArchive,
            fills=surface is OkxObservationSurface.Fills or OkxObservationSurface.FillsArchive;
        return (pending?p.BeginUtc is null&&p.EndUtc is null:p.BeginUtc.HasValue&&p.EndUtc.HasValue)&&
            (!bills||p.Instrument is null)&& (fills||p.OrderId is null)&&
            (p.Cursor is null||p.Cursor.Surface==surface&&p.Cursor.Instrument==p.Instrument);
    }
    internal static string Query(OkxObservationSurface surface,OkxObservationPageQuery p)
    {
        var pairs=new SortedDictionary<string,string>(StringComparer.Ordinal);
        if(p.Cursor is not null)pairs.Add(p.Cursor.Direction==OkxObservationCursorDirection.After?"after":"before",p.Cursor.Value);
        if(p.BeginUtc.HasValue){pairs.Add("begin",p.BeginUtc.Value.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));pairs.Add("end",p.EndUtc!.Value.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));}
        if(p.Instrument is not null)pairs.Add("instId",p.Instrument);
        if(surface is not (OkxObservationSurface.Bills or OkxObservationSurface.BillsArchive))pairs.Add("instType","SPOT");
        pairs.Add("limit",p.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if(p.OrderId is not null)pairs.Add("ordId",p.OrderId);
        return "?"+string.Join("&",pairs.Select(x=>x.Key+"="+x.Value));
    }
    internal static ArgumentException Invalid()=>new("InvalidObservationRequest");
    internal static bool Time(DateTimeOffset t)=>t.Offset==TimeSpan.Zero&&t.Ticks%TimeSpan.TicksPerMillisecond==0&&t.ToUnixTimeMilliseconds()>0;
}

public sealed record OkxExecutionOrderDto
{
    public string InstType { get; }
    public string InstId { get; }
    public string OrdId { get; }
    public string ClOrdId { get; }
    public OkxDecimal Px { get; }
    public OkxDecimal Sz { get; }
    public OkxDecimal Pnl { get; }
    public OkxDecimal AccFillSz { get; }
    public OkxDecimal FillPx { get; }
    public OkxDecimal FillSz { get; }
    public OkxDecimal AvgPx { get; }
    public OkxDecimal TpTriggerPx { get; }
    public OkxDecimal TpOrdPx { get; }
    public OkxDecimal SlTriggerPx { get; }
    public OkxDecimal SlOrdPx { get; }
    public OkxDecimal Fee { get; }
    public OkxDecimal Rebate { get; }
    public OkxTimestamp FillTime { get; }
    public OkxTimestamp UTime { get; }
    public OkxTimestamp CTime { get; }
    public string? TgtCcy { get; }
    public string? Ccy { get; }
    public string? Tag { get; }
    public string? OrdType { get; }
    public string? Side { get; }
    public string? PosSide { get; }
    public string? TdMode { get; }
    public string? TradeId { get; }
    public string? State { get; }
    public string? StpMode { get; }
    public string? AttachAlgoClOrdId { get; }
    public string? TpTriggerPxType { get; }
    public string? SlTriggerPxType { get; }
    public string? FeeCcy { get; }
    public string? RebateCcy { get; }
    public string? Source { get; }
    public string? Category { get; }
    public string? ReduceOnly { get; }
    public string? IsTpLimit { get; }
    public string? CancelSource { get; }
    public string? AlgoClOrdId { get; }
    public string? AlgoId { get; }
    public string? TradeQuoteCcy { get; }
    public string? Outcome { get; }
    public ImmutableArray<OkxExecutionAttachmentDto>? AttachAlgoOrds { get; }
    public OkxExecutionLinkedAlgoDto? LinkedAlgoOrd { get; }
    public string PayloadDigest { get; }
    // Parsed presence/value of admitted operands not exposed in the financial DTO.
    // Null means no parser proof; it must never corroborate another observation.
    internal ImmutableDictionary<string,string>? ConsistencyExtras { get; init; }
    internal OkxExecutionOrderDto(string instType,string instId,string ordId,string clOrdId,OkxDecimal px,OkxDecimal sz,OkxDecimal pnl,OkxDecimal accFillSz,OkxDecimal fillPx,OkxDecimal fillSz,OkxDecimal avgPx,OkxDecimal tpTriggerPx,OkxDecimal tpOrdPx,OkxDecimal slTriggerPx,OkxDecimal slOrdPx,OkxDecimal fee,OkxDecimal rebate,OkxTimestamp fillTime,OkxTimestamp uTime,OkxTimestamp cTime,string? tgtCcy,string? ccy,string? tag,string? ordType,string? side,string? posSide,string? tdMode,string? tradeId,string? state,string? stpMode,string? attachAlgoClOrdId,string? tpTriggerPxType,string? slTriggerPxType,string? feeCcy,string? rebateCcy,string? source,string? category,string? reduceOnly,string? isTpLimit,string? cancelSource,string? algoClOrdId,string? algoId,string? tradeQuoteCcy,string? outcome,ImmutableArray<OkxExecutionAttachmentDto>? attachAlgoOrds,OkxExecutionLinkedAlgoDto? linkedAlgoOrd,string payloadDigest)
    {
        InstType=instType;
        InstId=instId;
        OrdId=ordId;
        ClOrdId=clOrdId;
        Px=px;
        Sz=sz;
        Pnl=pnl;
        AccFillSz=accFillSz;
        FillPx=fillPx;
        FillSz=fillSz;
        AvgPx=avgPx;
        TpTriggerPx=tpTriggerPx;
        TpOrdPx=tpOrdPx;
        SlTriggerPx=slTriggerPx;
        SlOrdPx=slOrdPx;
        Fee=fee;
        Rebate=rebate;
        FillTime=fillTime;
        UTime=uTime;
        CTime=cTime;
        TgtCcy=tgtCcy;
        Ccy=ccy;
        Tag=tag;
        OrdType=ordType;
        Side=side;
        PosSide=posSide;
        TdMode=tdMode;
        TradeId=tradeId;
        State=state;
        StpMode=stpMode;
        AttachAlgoClOrdId=attachAlgoClOrdId;
        TpTriggerPxType=tpTriggerPxType;
        SlTriggerPxType=slTriggerPxType;
        FeeCcy=feeCcy;
        RebateCcy=rebateCcy;
        Source=source;
        Category=category;
        ReduceOnly=reduceOnly;
        IsTpLimit=isTpLimit;
        CancelSource=cancelSource;
        AlgoClOrdId=algoClOrdId;
        AlgoId=algoId;
        TradeQuoteCcy=tradeQuoteCcy;
        Outcome=outcome;
        AttachAlgoOrds=attachAlgoOrds;
        LinkedAlgoOrd=linkedAlgoOrd;
        PayloadDigest=payloadDigest;
    }
    public override string ToString()=>"OKX order observation";
}

public sealed record OkxExecutionAttachmentDto
{
    public string? AttachAlgoId { get; }
    public string? AttachAlgoClOrdId { get; }
    public string? TpOrdKind { get; }
    public string? TpTriggerPxType { get; }
    public string? SlTriggerPxType { get; }
    public string? AmendPxOnTriggerType { get; }
    public string? FailCode { get; }
    public OkxDecimal TpTriggerPx { get; }
    public OkxDecimal TpTriggerRatio { get; }
    public OkxDecimal TpOrdPx { get; }
    public OkxDecimal SlTriggerPx { get; }
    public OkxDecimal SlTriggerRatio { get; }
    public OkxDecimal SlOrdPx { get; }
    public OkxDecimal Sz { get; }
    public OkxDecimal CallbackRatio { get; }
    public OkxDecimal CallbackSpread { get; }
    public OkxDecimal ActivePx { get; }
    internal string PayloadDigest { get; }
    internal ImmutableDictionary<string,string>? ConsistencyExtras { get; init; }
    internal OkxExecutionAttachmentDto(string? attachAlgoId,string? attachAlgoClOrdId,string? tpOrdKind,string? tpTriggerPxType,string? slTriggerPxType,string? amendPxOnTriggerType,string? failCode,OkxDecimal tpTriggerPx,OkxDecimal tpTriggerRatio,OkxDecimal tpOrdPx,OkxDecimal slTriggerPx,OkxDecimal slTriggerRatio,OkxDecimal slOrdPx,OkxDecimal sz,OkxDecimal callbackRatio,OkxDecimal callbackSpread,OkxDecimal activePx,string payloadDigest)
    {
        AttachAlgoId=attachAlgoId;
        AttachAlgoClOrdId=attachAlgoClOrdId;
        TpOrdKind=tpOrdKind;
        TpTriggerPxType=tpTriggerPxType;
        SlTriggerPxType=slTriggerPxType;
        AmendPxOnTriggerType=amendPxOnTriggerType;
        FailCode=failCode;
        TpTriggerPx=tpTriggerPx;
        TpTriggerRatio=tpTriggerRatio;
        TpOrdPx=tpOrdPx;
        SlTriggerPx=slTriggerPx;
        SlTriggerRatio=slTriggerRatio;
        SlOrdPx=slOrdPx;
        Sz=sz;
        CallbackRatio=callbackRatio;
        CallbackSpread=callbackSpread;
        ActivePx=activePx;
        PayloadDigest=payloadDigest;
    }
    public override string ToString()=>"OKX attachment observation";
}

public sealed record OkxExecutionLinkedAlgoDto
{
    public string? AlgoId { get; }
    internal OkxExecutionLinkedAlgoDto(string? algoId)
    {
        AlgoId=algoId;
    }
    public override string ToString()=>"OKX linkedalgo observation";
}

public sealed record OkxExecutionFillDto
{
    public string InstType { get; }
    public string InstId { get; }
    public string BillId { get; }
    public string TradeId { get; }
    public string OrdId { get; }
    public string ClOrdId { get; }
    public OkxDecimal FillPx { get; }
    public OkxDecimal FillSz { get; }
    public OkxDecimal FillPnl { get; }
    public OkxDecimal Fee { get; }
    public OkxDecimal FeeRate { get; }
    public OkxTimestamp Ts { get; }
    public OkxTimestamp FillTime { get; }
    public string? SubType { get; }
    public string? Tag { get; }
    public string? Side { get; }
    public string? PosSide { get; }
    public string? ExecType { get; }
    public string? FeeCcy { get; }
    public string? TradeQuoteCcy { get; }
    public string PayloadDigest { get; }
    internal OkxExecutionFillDto(string instType,string instId,string billId,string tradeId,string ordId,string clOrdId,OkxDecimal fillPx,OkxDecimal fillSz,OkxDecimal fillPnl,OkxDecimal fee,OkxDecimal feeRate,OkxTimestamp ts,OkxTimestamp fillTime,string? subType,string? tag,string? side,string? posSide,string? execType,string? feeCcy,string? tradeQuoteCcy,string payloadDigest)
    {
        InstType=instType;
        InstId=instId;
        BillId=billId;
        TradeId=tradeId;
        OrdId=ordId;
        ClOrdId=clOrdId;
        FillPx=fillPx;
        FillSz=fillSz;
        FillPnl=fillPnl;
        Fee=fee;
        FeeRate=feeRate;
        Ts=ts;
        FillTime=fillTime;
        SubType=subType;
        Tag=tag;
        Side=side;
        PosSide=posSide;
        ExecType=execType;
        FeeCcy=feeCcy;
        TradeQuoteCcy=tradeQuoteCcy;
        PayloadDigest=payloadDigest;
    }
    public override string ToString()=>"OKX fill observation";
}

public sealed record OkxExecutionBillDto
{
    public string BillId { get; }
    public string Ccy { get; }
    public string Type { get; }
    public string SubType { get; }
    public OkxDecimal BalChg { get; }
    public OkxDecimal PosBalChg { get; }
    public OkxDecimal Bal { get; }
    public OkxDecimal PosBal { get; }
    public OkxDecimal Sz { get; }
    public OkxDecimal Px { get; }
    public OkxDecimal Pnl { get; }
    public OkxDecimal Fee { get; }
    public OkxDecimal EarnAmt { get; }
    public OkxDecimal EarnApr { get; }
    public OkxDecimal Interest { get; }
    public OkxTimestamp Ts { get; }
    public OkxTimestamp FillTime { get; }
    public string? InstType { get; }
    public string? MgnMode { get; }
    public string? InstId { get; }
    public string? OrdId { get; }
    public string? ExecType { get; }
    public string? From { get; }
    public string? To { get; }
    public string? Tag { get; }
    public string? TradeId { get; }
    public string? ClOrdId { get; }
    public string PayloadDigest { get; }
    internal OkxExecutionBillDto(string billId,string ccy,string valueType,string subType,OkxDecimal balChg,OkxDecimal posBalChg,OkxDecimal bal,OkxDecimal posBal,OkxDecimal sz,OkxDecimal px,OkxDecimal pnl,OkxDecimal fee,OkxDecimal earnAmt,OkxDecimal earnApr,OkxDecimal interest,OkxTimestamp ts,OkxTimestamp fillTime,string? instType,string? mgnMode,string? instId,string? ordId,string? execType,string? from,string? to,string? tag,string? tradeId,string? clOrdId,string payloadDigest)
    {
        BillId=billId;
        Ccy=ccy;
        Type=valueType;
        SubType=subType;
        BalChg=balChg;
        PosBalChg=posBalChg;
        Bal=bal;
        PosBal=posBal;
        Sz=sz;
        Px=px;
        Pnl=pnl;
        Fee=fee;
        EarnAmt=earnAmt;
        EarnApr=earnApr;
        Interest=interest;
        Ts=ts;
        FillTime=fillTime;
        InstType=instType;
        MgnMode=mgnMode;
        InstId=instId;
        OrdId=ordId;
        ExecType=execType;
        From=from;
        To=to;
        Tag=tag;
        TradeId=tradeId;
        ClOrdId=clOrdId;
        PayloadDigest=payloadDigest;
    }
    public override string ToString()=>"OKX bill observation";
}

public enum OkxObservationCollectionStatus { Collected, Incomplete, Conflict }
public sealed record OkxObservationCollectionQuery
{
    public OkxObservationSurface Surface { get; }
    public OkxObservationPageQuery Page { get; }
    public OkxObservationContext Context { get; }
    public OkxObservationCollectionQuery(OkxObservationSurface Surface,OkxObservationPageQuery Page,OkxObservationContext Context)
    {
        if(!RecoveryRequest.Valid(Surface,Page)||Page.Cursor is not null||Context is null||Context.ConnectionId==Guid.Empty||Context.CaptureId==Guid.Empty)throw RecoveryRequest.Invalid();
        this.Surface=Surface;this.Page=Page;this.Context=Context;
    }
    public override string ToString()=>"OKX collection query";
}

public sealed record OkxObservationPageTrace
{
    public OkxObservationSurface Surface { get; }
    public string Endpoint { get; }
    public string ExactQuery { get; }
    public OkxObservationContext Context { get; }
    public int PageIndex { get; }
    public bool IsBookend { get; }
    public bool IsLocalStop { get; }
    public OkxObservationStatus Status { get; }
    public int RowCount { get; }
    public string? ResponseDigest { get; }
    public DateTimeOffset RequestUtc { get; }
    public DateTimeOffset ReceiveUtc { get; }
    public long RequestMonotonic { get; }
    public long ReceiveMonotonic { get; }
    public OkxObservationCursor? RequestedCursor { get; }
    public OkxObservationCursor? NextCursor { get; }
    public string? ObservedTimeField { get; }
    public DateTimeOffset? MinimumObservedTime { get; }
    public DateTimeOffset? MaximumObservedTime { get; }
    public DateTimeOffset? MinimumFillTime { get; }
    public DateTimeOffset? MaximumFillTime { get; }
    public bool EmptyTerminal { get; }
    public string? ErrorCode { get; }
    internal OkxObservationPageTrace(OkxObservationSurface surface,string endpoint,string exactQuery,OkxObservationContext context,int pageIndex,bool isBookend,bool isLocalStop,OkxObservationStatus status,int rowCount,string? responseDigest,DateTimeOffset requestUtc,DateTimeOffset receiveUtc,long requestMonotonic,long receiveMonotonic,OkxObservationCursor? requestedCursor,OkxObservationCursor? nextCursor,string? observedTimeField,DateTimeOffset? minimumObservedTime,DateTimeOffset? maximumObservedTime,DateTimeOffset? minimumFillTime,DateTimeOffset? maximumFillTime,bool emptyTerminal,string? errorCode)
    {
        Surface=surface;
        Endpoint=endpoint;
        ExactQuery=exactQuery;
        Context=context;
        PageIndex=pageIndex;
        IsBookend=isBookend;
        IsLocalStop=isLocalStop;
        Status=status;
        RowCount=rowCount;
        ResponseDigest=responseDigest;
        RequestUtc=requestUtc;
        ReceiveUtc=receiveUtc;
        RequestMonotonic=requestMonotonic;
        ReceiveMonotonic=receiveMonotonic;
        RequestedCursor=requestedCursor;
        NextCursor=nextCursor;
        ObservedTimeField=observedTimeField;
        MinimumObservedTime=minimumObservedTime;
        MaximumObservedTime=maximumObservedTime;
        MinimumFillTime=minimumFillTime;
        MaximumFillTime=maximumFillTime;
        EmptyTerminal=emptyTerminal;
        ErrorCode=errorCode;
    }
    public override string ToString()=>"OKX OkxObservationPageTrace";
}

public sealed record OkxObservationCollection
{
    public OkxObservationCollectionStatus Status { get; }
    public OkxExecutionScope RequestedScope { get; }
    public OkxObservationCollectionQuery Query { get; }
    public DateTimeOffset RequestUtc { get; }
    public DateTimeOffset ReceiveUtc { get; }
    public long RequestMonotonic { get; }
    public long ReceiveMonotonic { get; }
    public IReadOnlyList<OkxObservationPageTrace> Trace { get; }
    public IReadOnlyList<OkxExecutionOrderDto> Orders { get; }
    public IReadOnlyList<OkxExecutionFillDto> Fills { get; }
    public IReadOnlyList<OkxExecutionBillDto> Bills { get; }
    public IReadOnlyList<string> Limitations { get; }
    public string? ErrorCode { get; }
    internal OkxObservationCollection(OkxObservationCollectionStatus status,OkxExecutionScope requestedScope,OkxObservationCollectionQuery query,DateTimeOffset requestUtc,DateTimeOffset receiveUtc,long requestMonotonic,long receiveMonotonic,IReadOnlyList<OkxObservationPageTrace> trace,IReadOnlyList<OkxExecutionOrderDto> orders,IReadOnlyList<OkxExecutionFillDto> fills,IReadOnlyList<OkxExecutionBillDto> bills,IReadOnlyList<string> limitations,string? errorCode)
    {
        Status=status;
        RequestedScope=requestedScope;
        Query=query;
        RequestUtc=requestUtc;
        ReceiveUtc=receiveUtc;
        RequestMonotonic=requestMonotonic;
        ReceiveMonotonic=receiveMonotonic;
        Trace=trace.ToImmutableArray();
        Orders=orders.ToImmutableArray();
        Fills=fills.ToImmutableArray();
        Bills=bills.ToImmutableArray();
        Limitations=limitations.ToImmutableArray();
        ErrorCode=errorCode;
    }
    public override string ToString()=>"OKX OkxObservationCollection";
}
