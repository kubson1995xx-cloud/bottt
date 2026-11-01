using System.Collections.Immutable;
using System.Globalization;
namespace Apex.Adapters.Okx.Execution;

public enum OkxAlgoObservationSurface { Pending, History }
public enum OkxAlgoOrderFilter { Conditional, Oco, ConditionalAndOco, Trigger, TrailingStop }
public enum OkxAlgoHistoryState { Effective, Canceled, OrderFailed }
public sealed record OkxObservationAlgoQuery
{
    public string? AlgoId { get; }
    public string? AlgoClientOrderId { get; }
    public OkxObservationAlgoQuery(string? AlgoId,string? AlgoClientOrderId)
    {
        if(AlgoId is null&&AlgoClientOrderId is null||AlgoId is not null&&!Grammar.OrderId(AlgoId)||AlgoClientOrderId is not null&&!Grammar.ClientId(AlgoClientOrderId))throw RecoveryRequest.Invalid();
        this.AlgoId=AlgoId;this.AlgoClientOrderId=AlgoClientOrderId;
    }
    public override string ToString()=>"OKX algo query";
}
public sealed record OkxObservationAlgoCursor
{
    public OkxAlgoObservationSurface Surface { get; }
    public OkxAlgoOrderFilter OrderType { get; }
    public OkxAlgoHistoryState? State { get; }
    public string? AlgoIdFilter { get; }
    public string? Instrument { get; }
    public OkxObservationCursorDirection Direction { get; }
    public string Value { get; }
    public OkxObservationAlgoCursor(OkxAlgoObservationSurface Surface,OkxAlgoOrderFilter OrderType,OkxAlgoHistoryState? State,string? AlgoIdFilter,string? Instrument,OkxObservationCursorDirection Direction,string Value)
    {
        if(!AlgoRequest.Filters(Surface,OrderType,State,AlgoIdFilter,Instrument)||!Enum.IsDefined(Direction)||!Grammar.OrderId(Value))throw RecoveryRequest.Invalid();
        this.Surface=Surface;this.OrderType=OrderType;this.State=State;this.AlgoIdFilter=AlgoIdFilter;this.Instrument=Instrument;this.Direction=Direction;this.Value=Value;
    }
    public override string ToString()=>"OKX algo cursor";
}
public sealed record OkxObservationAlgoPageQuery
{
    public OkxAlgoObservationSurface Surface { get; }
    public OkxAlgoOrderFilter OrderType { get; }
    public OkxAlgoHistoryState? State { get; }
    public string? AlgoId { get; }
    public string? Instrument { get; }
    public OkxObservationAlgoCursor? Cursor { get; }
    public int Limit { get; }
    public OkxObservationAlgoPageQuery(OkxAlgoObservationSurface Surface,OkxAlgoOrderFilter OrderType,OkxAlgoHistoryState? State,string? AlgoId,string? Instrument,OkxObservationAlgoCursor? Cursor,int Limit=100)
    {
        if(!AlgoRequest.Filters(Surface,OrderType,State,AlgoId,Instrument)||Limit is <1 or >100||Cursor is not null&&(Cursor.Surface!=Surface||Cursor.OrderType!=OrderType||Cursor.State!=State||Cursor.AlgoIdFilter!=AlgoId||Cursor.Instrument!=Instrument))throw RecoveryRequest.Invalid();
        this.Surface=Surface;this.OrderType=OrderType;this.State=State;this.AlgoId=AlgoId;this.Instrument=Instrument;this.Cursor=Cursor;this.Limit=Limit;
    }
    public override string ToString()=>"OKX algo page query";
}
internal static class AlgoRequest
{
    internal static bool Filters(OkxAlgoObservationSurface s,OkxAlgoOrderFilter o,OkxAlgoHistoryState? state,string? id,string? inst)=>Enum.IsDefined(s)&&Enum.IsDefined(o)&&(!state.HasValue||Enum.IsDefined(state.Value))&&(s==OkxAlgoObservationSurface.Pending?state is null:state is not null||id is not null)&&(id is null||Grammar.OrderId(id))&&(inst is null||Grammar.Instrument(inst));
    internal static string Endpoint(OkxAlgoObservationSurface s)=>s==OkxAlgoObservationSurface.Pending?"/api/v5/trade/orders-algo-pending":"/api/v5/trade/orders-algo-history";
    internal static string Query(OkxObservationAlgoPageQuery p)
    {
        var q=new SortedDictionary<string,string>(StringComparer.Ordinal);
        if(p.AlgoId is not null)q.Add("algoId",p.AlgoId);
        if(p.Cursor is not null)q.Add(p.Cursor.Direction==OkxObservationCursorDirection.After?"after":"before",p.Cursor.Value);
        if(p.Instrument is not null)q.Add("instId",p.Instrument);
        q.Add("instType","SPOT");q.Add("limit",p.Limit.ToString(CultureInfo.InvariantCulture));
        q.Add("ordType",p.OrderType switch {OkxAlgoOrderFilter.Conditional=>"conditional",OkxAlgoOrderFilter.Oco=>"oco",OkxAlgoOrderFilter.ConditionalAndOco=>"conditional,oco",OkxAlgoOrderFilter.Trigger=>"trigger",OkxAlgoOrderFilter.TrailingStop=>"move_order_stop",_=>throw RecoveryRequest.Invalid()});
        if(p.State is not null)q.Add("state",p.State switch {OkxAlgoHistoryState.Effective=>"effective",OkxAlgoHistoryState.Canceled=>"canceled",OkxAlgoHistoryState.OrderFailed=>"order_failed",_=>throw RecoveryRequest.Invalid()});
        return "?"+string.Join("&",q.Select(x=>x.Key+"="+x.Value));
    }
}

public sealed record OkxExecutionAlgoDto
{
    public string InstType { get; }
    public string InstId { get; }
    public string AlgoId { get; }
    public string? AlgoClOrdId { get; }
    public string? ClOrdId { get; }
    public string? OrdId { get; }
    public string? State { get; }
    public string? OrdType { get; }
    public string? Side { get; }
    public string? PosSide { get; }
    public string? TdMode { get; }
    public string? TgtCcy { get; }
    public string? Ccy { get; }
    public string? Tag { get; }
    public string? ActualSide { get; }
    public string? TpTriggerPxType { get; }
    public string? SlTriggerPxType { get; }
    public string? TriggerPxType { get; }
    public string? FailCode { get; }
    public string? ReduceOnly { get; }
    public string? IsTradeBorrowMode { get; }
    public string? AmendPxOnTriggerType { get; }
    public string? TradeQuoteCcy { get; }
    public string? AdvanceOrdType { get; }
    public string? ChaseType { get; }
    public string? MaxChaseType { get; }
    public string? QuickMgnType { get; }
    public OkxDecimal Sz { get; }
    public OkxDecimal CloseFraction { get; }
    public OkxDecimal ActualSz { get; }
    public OkxDecimal ActualPx { get; }
    public OkxDecimal TpTriggerPx { get; }
    public OkxDecimal TpOrdPx { get; }
    public OkxDecimal SlTriggerPx { get; }
    public OkxDecimal SlOrdPx { get; }
    public OkxDecimal TriggerPx { get; }
    public OkxDecimal OrdPx { get; }
    public OkxDecimal Last { get; }
    public OkxDecimal CallbackRatio { get; }
    public OkxDecimal CallbackSpread { get; }
    public OkxDecimal ActivePx { get; }
    public OkxDecimal MoveTriggerPx { get; }
    public OkxDecimal ChaseVal { get; }
    public OkxDecimal MaxChaseVal { get; }
    public OkxTimestamp CTime { get; }
    public OkxTimestamp UTime { get; }
    public OkxTimestamp TriggerTime { get; }
    public ImmutableArray<string>? OrdIdList { get; }
    public ImmutableArray<string>? SubAlgoIdList { get; }
    public ImmutableArray<OkxAlgoAttachmentDto>? AttachAlgoOrds { get; }
    public ImmutableArray<OkxAlgoChaseDto>? AdvChaseParams { get; }
    public OkxAlgoLinkedOrderDto? LinkedOrd { get; }
    public string PayloadDigest { get; }
    internal OkxExecutionAlgoDto(string instType,string instId,string algoId,string? algoClOrdId,string? clOrdId,string? ordId,string? state,string? ordType,string? side,string? posSide,string? tdMode,string? tgtCcy,string? ccy,string? tag,string? actualSide,string? tpTriggerPxType,string? slTriggerPxType,string? triggerPxType,string? failCode,string? reduceOnly,string? isTradeBorrowMode,string? amendPxOnTriggerType,string? tradeQuoteCcy,string? advanceOrdType,string? chaseType,string? maxChaseType,string? quickMgnType,OkxDecimal sz,OkxDecimal closeFraction,OkxDecimal actualSz,OkxDecimal actualPx,OkxDecimal tpTriggerPx,OkxDecimal tpOrdPx,OkxDecimal slTriggerPx,OkxDecimal slOrdPx,OkxDecimal triggerPx,OkxDecimal ordPx,OkxDecimal last,OkxDecimal callbackRatio,OkxDecimal callbackSpread,OkxDecimal activePx,OkxDecimal moveTriggerPx,OkxDecimal chaseVal,OkxDecimal maxChaseVal,OkxTimestamp cTime,OkxTimestamp uTime,OkxTimestamp triggerTime,ImmutableArray<string>? ordIdList,ImmutableArray<string>? subAlgoIdList,ImmutableArray<OkxAlgoAttachmentDto>? attachAlgoOrds,ImmutableArray<OkxAlgoChaseDto>? advChaseParams,OkxAlgoLinkedOrderDto? linkedOrd,string payloadDigest)
    {
        InstType=instType;
        InstId=instId;
        AlgoId=algoId;
        AlgoClOrdId=algoClOrdId;
        ClOrdId=clOrdId;
        OrdId=ordId;
        State=state;
        OrdType=ordType;
        Side=side;
        PosSide=posSide;
        TdMode=tdMode;
        TgtCcy=tgtCcy;
        Ccy=ccy;
        Tag=tag;
        ActualSide=actualSide;
        TpTriggerPxType=tpTriggerPxType;
        SlTriggerPxType=slTriggerPxType;
        TriggerPxType=triggerPxType;
        FailCode=failCode;
        ReduceOnly=reduceOnly;
        IsTradeBorrowMode=isTradeBorrowMode;
        AmendPxOnTriggerType=amendPxOnTriggerType;
        TradeQuoteCcy=tradeQuoteCcy;
        AdvanceOrdType=advanceOrdType;
        ChaseType=chaseType;
        MaxChaseType=maxChaseType;
        QuickMgnType=quickMgnType;
        Sz=sz;
        CloseFraction=closeFraction;
        ActualSz=actualSz;
        ActualPx=actualPx;
        TpTriggerPx=tpTriggerPx;
        TpOrdPx=tpOrdPx;
        SlTriggerPx=slTriggerPx;
        SlOrdPx=slOrdPx;
        TriggerPx=triggerPx;
        OrdPx=ordPx;
        Last=last;
        CallbackRatio=callbackRatio;
        CallbackSpread=callbackSpread;
        ActivePx=activePx;
        MoveTriggerPx=moveTriggerPx;
        ChaseVal=chaseVal;
        MaxChaseVal=maxChaseVal;
        CTime=cTime;
        UTime=uTime;
        TriggerTime=triggerTime;
        OrdIdList=ordIdList;
        SubAlgoIdList=subAlgoIdList;
        AttachAlgoOrds=attachAlgoOrds;
        AdvChaseParams=advChaseParams;
        LinkedOrd=linkedOrd;
        PayloadDigest=payloadDigest;
    }
    public override string ToString()=>"OKX OkxExecutionAlgoDto [REDACTED]";
}

public sealed record OkxAlgoAttachmentDto
{
    public string? AttachAlgoClOrdId { get; }
    public string? TpTriggerPxType { get; }
    public string? SlTriggerPxType { get; }
    public OkxDecimal TpTriggerPx { get; }
    public OkxDecimal TpTriggerRatio { get; }
    public OkxDecimal TpOrdPx { get; }
    public OkxDecimal SlTriggerPx { get; }
    public OkxDecimal SlTriggerRatio { get; }
    public OkxDecimal SlOrdPx { get; }
    public OkxDecimal CallbackRatio { get; }
    public OkxDecimal CallbackSpread { get; }
    public OkxDecimal ActivePx { get; }
    public string PayloadDigest { get; }
    internal OkxAlgoAttachmentDto(string? attachAlgoClOrdId,string? tpTriggerPxType,string? slTriggerPxType,OkxDecimal tpTriggerPx,OkxDecimal tpTriggerRatio,OkxDecimal tpOrdPx,OkxDecimal slTriggerPx,OkxDecimal slTriggerRatio,OkxDecimal slOrdPx,OkxDecimal callbackRatio,OkxDecimal callbackSpread,OkxDecimal activePx,string payloadDigest)
    {
        AttachAlgoClOrdId=attachAlgoClOrdId;
        TpTriggerPxType=tpTriggerPxType;
        SlTriggerPxType=slTriggerPxType;
        TpTriggerPx=tpTriggerPx;
        TpTriggerRatio=tpTriggerRatio;
        TpOrdPx=tpOrdPx;
        SlTriggerPx=slTriggerPx;
        SlTriggerRatio=slTriggerRatio;
        SlOrdPx=slOrdPx;
        CallbackRatio=callbackRatio;
        CallbackSpread=callbackSpread;
        ActivePx=activePx;
        PayloadDigest=payloadDigest;
    }
    public override string ToString()=>"OKX OkxAlgoAttachmentDto [REDACTED]";
}

public sealed record OkxAlgoChaseDto
{
    public string? ChaseType { get; }
    public string? MaxChaseType { get; }
    public OkxDecimal ChaseVal { get; }
    public OkxDecimal MaxChaseVal { get; }
    public string PayloadDigest { get; }
    internal OkxAlgoChaseDto(string? chaseType,string? maxChaseType,OkxDecimal chaseVal,OkxDecimal maxChaseVal,string payloadDigest)
    {
        ChaseType=chaseType;
        MaxChaseType=maxChaseType;
        ChaseVal=chaseVal;
        MaxChaseVal=maxChaseVal;
        PayloadDigest=payloadDigest;
    }
    public override string ToString()=>"OKX OkxAlgoChaseDto [REDACTED]";
}

public sealed record OkxAlgoLinkedOrderDto
{
    public string? OrdId { get; }
    public string PayloadDigest { get; }
    internal OkxAlgoLinkedOrderDto(string? ordId,string payloadDigest)
    {
        OrdId=ordId;
        PayloadDigest=payloadDigest;
    }
    public override string ToString()=>"OKX OkxAlgoLinkedOrderDto [REDACTED]";
}

public sealed record OkxAlgoObservationPageTrace
{
    public OkxObservationAlgoPageQuery Query { get; }
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
    public OkxObservationAlgoCursor? RequestedCursor { get; }
    public OkxObservationAlgoCursor? NextCursor { get; }
    public bool EmptyTerminal { get; }
    public string? ErrorCode { get; }
    internal OkxAlgoObservationPageTrace(OkxObservationAlgoPageQuery query,string endpoint,string exactQuery,OkxObservationContext context,int pageIndex,bool isBookend,bool isLocalStop,OkxObservationStatus status,int rowCount,string? responseDigest,DateTimeOffset requestUtc,DateTimeOffset receiveUtc,long requestMonotonic,long receiveMonotonic,OkxObservationAlgoCursor? requestedCursor,OkxObservationAlgoCursor? nextCursor,bool emptyTerminal,string? errorCode)
    {
        Query=query;
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
        EmptyTerminal=emptyTerminal;
        ErrorCode=errorCode;
    }
    public override string ToString()=>"OKX OkxAlgoObservationPageTrace [REDACTED]";
}

public sealed record OkxAlgoObservationCollection
{
    public OkxObservationCollectionStatus Status { get; }
    public OkxExecutionScope RequestedScope { get; }
    public OkxObservationAlgoPageQuery Query { get; }
    public OkxObservationContext Context { get; }
    public DateTimeOffset RequestUtc { get; }
    public DateTimeOffset ReceiveUtc { get; }
    public long RequestMonotonic { get; }
    public long ReceiveMonotonic { get; }
    public ImmutableArray<OkxExecutionAlgoDto> Rows { get; }
    public ImmutableArray<OkxAlgoObservationPageTrace> Trace { get; }
    public ImmutableArray<string> Limitations { get; }
    public string? ErrorCode { get; }
    internal OkxAlgoObservationCollection(OkxObservationCollectionStatus status,OkxExecutionScope requestedScope,OkxObservationAlgoPageQuery query,OkxObservationContext context,DateTimeOffset requestUtc,DateTimeOffset receiveUtc,long requestMonotonic,long receiveMonotonic,ImmutableArray<OkxExecutionAlgoDto> rows,ImmutableArray<OkxAlgoObservationPageTrace> trace,ImmutableArray<string> limitations,string? errorCode)
    {
        Status=status;
        RequestedScope=requestedScope;
        Query=query;
        Context=context;
        RequestUtc=requestUtc;
        ReceiveUtc=receiveUtc;
        RequestMonotonic=requestMonotonic;
        ReceiveMonotonic=receiveMonotonic;
        Rows=rows;
        Trace=trace;
        Limitations=limitations;
        ErrorCode=errorCode;
    }
    public override string ToString()=>"OKX OkxAlgoObservationCollection [REDACTED]";
}
