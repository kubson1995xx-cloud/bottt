using System.Collections.Immutable;
namespace Apex.Adapters.Okx.Execution;

public enum OkxStreamLane { PrivateOrders, BusinessAlgos }
public enum OkxStreamObservationKind { Connecting, Connected, LoginAccepted, SubscriptionAccepted, Buffering, OrdersUpdate, AlgoUpdate, Gap, Closed }
public interface IOkxObservationStreamFence : IOkxObservationReadFence
{
    void OnGap(OkxExecutionScope scope,OkxObservationContext context,Guid epoch,string reason);
}

public sealed record OkxExecutionOrderUpdateDto
{
    public string InstType { get; }
    public string InstId { get; }
    public string OrdId { get; }
    public string ClOrdId { get; }
    public string? State { get; }
    public string? OrdType { get; }
    public string? Side { get; }
    public string? PosSide { get; }
    public string? TdMode { get; }
    public string? TgtCcy { get; }
    public string? Ccy { get; }
    public string? Tag { get; }
    public string? TradeId { get; }
    public string? ExecType { get; }
    public string? FeeCcy { get; }
    public string? RebateCcy { get; }
    public string? FillFeeCcy { get; }
    public string? AlgoId { get; }
    public string? AlgoClOrdId { get; }
    public string? AttachAlgoClOrdId { get; }
    public string? TradeQuoteCcy { get; }
    public string? Source { get; }
    public string? Category { get; }
    public string? CancelSource { get; }
    public string? AmendSource { get; }
    public string? ReqId { get; }
    public string? AmendResult { get; }
    public string? Code { get; }
    public string? ReduceOnly { get; }
    public string? IsTpLimit { get; }
    public string? TpTriggerPxType { get; }
    public string? SlTriggerPxType { get; }
    public string? StpMode { get; }
    public string? Outcome { get; }
    public OkxDecimal Px { get; }
    public OkxDecimal Sz { get; }
    public OkxDecimal AccFillSz { get; }
    public OkxDecimal AvgPx { get; }
    public OkxDecimal Pnl { get; }
    public OkxDecimal FillPx { get; }
    public OkxDecimal FillSz { get; }
    public OkxDecimal FillPnl { get; }
    public OkxDecimal FillFee { get; }
    public OkxDecimal Fee { get; }
    public OkxDecimal Rebate { get; }
    public OkxDecimal TpTriggerPx { get; }
    public OkxDecimal TpOrdPx { get; }
    public OkxDecimal SlTriggerPx { get; }
    public OkxDecimal SlOrdPx { get; }
    public OkxDecimal LastPx { get; }
    public OkxTimestamp CTime { get; }
    public OkxTimestamp UTime { get; }
    public OkxTimestamp FillTime { get; }
    public OkxExecutionLinkedAlgoDto? LinkedAlgoOrd { get; }
    public ImmutableArray<OkxExecutionAttachmentDto>? AttachAlgoOrds { get; }
    public string PayloadDigest { get; }
    internal OkxExecutionOrderUpdateDto(string instType,string instId,string ordId,string clOrdId,string? state,string? ordType,string? side,string? posSide,string? tdMode,string? tgtCcy,string? ccy,string? tag,string? tradeId,string? execType,string? feeCcy,string? rebateCcy,string? fillFeeCcy,string? algoId,string? algoClOrdId,string? attachAlgoClOrdId,string? tradeQuoteCcy,string? source,string? category,string? cancelSource,string? amendSource,string? reqId,string? amendResult,string? code,string? reduceOnly,string? isTpLimit,string? tpTriggerPxType,string? slTriggerPxType,string? stpMode,string? outcome,OkxDecimal px,OkxDecimal sz,OkxDecimal accFillSz,OkxDecimal avgPx,OkxDecimal pnl,OkxDecimal fillPx,OkxDecimal fillSz,OkxDecimal fillPnl,OkxDecimal fillFee,OkxDecimal fee,OkxDecimal rebate,OkxDecimal tpTriggerPx,OkxDecimal tpOrdPx,OkxDecimal slTriggerPx,OkxDecimal slOrdPx,OkxDecimal lastPx,OkxTimestamp cTime,OkxTimestamp uTime,OkxTimestamp fillTime,OkxExecutionLinkedAlgoDto? linkedAlgoOrd,ImmutableArray<OkxExecutionAttachmentDto>? attachAlgoOrds,string payloadDigest)
    {
        InstType=instType;
        InstId=instId;
        OrdId=ordId;
        ClOrdId=clOrdId;
        State=state;
        OrdType=ordType;
        Side=side;
        PosSide=posSide;
        TdMode=tdMode;
        TgtCcy=tgtCcy;
        Ccy=ccy;
        Tag=tag;
        TradeId=tradeId;
        ExecType=execType;
        FeeCcy=feeCcy;
        RebateCcy=rebateCcy;
        FillFeeCcy=fillFeeCcy;
        AlgoId=algoId;
        AlgoClOrdId=algoClOrdId;
        AttachAlgoClOrdId=attachAlgoClOrdId;
        TradeQuoteCcy=tradeQuoteCcy;
        Source=source;
        Category=category;
        CancelSource=cancelSource;
        AmendSource=amendSource;
        ReqId=reqId;
        AmendResult=amendResult;
        Code=code;
        ReduceOnly=reduceOnly;
        IsTpLimit=isTpLimit;
        TpTriggerPxType=tpTriggerPxType;
        SlTriggerPxType=slTriggerPxType;
        StpMode=stpMode;
        Outcome=outcome;
        Px=px;
        Sz=sz;
        AccFillSz=accFillSz;
        AvgPx=avgPx;
        Pnl=pnl;
        FillPx=fillPx;
        FillSz=fillSz;
        FillPnl=fillPnl;
        FillFee=fillFee;
        Fee=fee;
        Rebate=rebate;
        TpTriggerPx=tpTriggerPx;
        TpOrdPx=tpOrdPx;
        SlTriggerPx=slTriggerPx;
        SlOrdPx=slOrdPx;
        LastPx=lastPx;
        CTime=cTime;
        UTime=uTime;
        FillTime=fillTime;
        LinkedAlgoOrd=linkedAlgoOrd;
        AttachAlgoOrds=attachAlgoOrds;
        PayloadDigest=payloadDigest;
    }
    public override string ToString()=>"OKX OkxExecutionOrderUpdateDto [REDACTED]";
}

public sealed record OkxExecutionAlgoUpdateDto
{
    public OkxExecutionAlgoDto Algo { get; }
    public string? ReqId { get; }
    public string? AmendResult { get; }
    public OkxDecimal NotionalUsd { get; }
    public string PayloadDigest { get; }
    internal OkxExecutionAlgoUpdateDto(OkxExecutionAlgoDto algo,string? reqId,string? amendResult,OkxDecimal notionalUsd,string payloadDigest)
    {
        Algo=algo;
        ReqId=reqId;
        AmendResult=amendResult;
        NotionalUsd=notionalUsd;
        PayloadDigest=payloadDigest;
    }
    public override string ToString()=>"OKX OkxExecutionAlgoUpdateDto [REDACTED]";
}

public sealed record OkxStreamObservation
{
    public OkxStreamObservationKind Kind { get; }
    public OkxExecutionScope RequestedScope { get; }
    public OkxObservationContext Context { get; }
    public Guid Epoch { get; }
    public long Sequence { get; }
    public OkxStreamLane? Lane { get; }
    public string? ConnId { get; }
    public string? Channel { get; }
    public DateTimeOffset ReceiveUtc { get; }
    public long ReceiveMonotonic { get; }
    public string? MessageDigest { get; }
    public string? ErrorCode { get; }
    public ImmutableArray<OkxExecutionOrderUpdateDto> Orders { get; }
    public ImmutableArray<OkxExecutionAlgoUpdateDto> Algos { get; }
    internal OkxStreamObservation(OkxStreamObservationKind kind,OkxExecutionScope requestedScope,OkxObservationContext context,Guid epoch,long sequence,OkxStreamLane? lane,string? connId,string? channel,DateTimeOffset receiveUtc,long receiveMonotonic,string? messageDigest,string? errorCode,ImmutableArray<OkxExecutionOrderUpdateDto> orders,ImmutableArray<OkxExecutionAlgoUpdateDto> algos)
    {
        Kind=kind;
        RequestedScope=requestedScope;
        Context=context;
        Epoch=epoch;
        Sequence=sequence;
        Lane=lane;
        ConnId=connId;
        Channel=channel;
        ReceiveUtc=receiveUtc;
        ReceiveMonotonic=receiveMonotonic;
        MessageDigest=messageDigest;
        ErrorCode=errorCode;
        Orders=orders;
        Algos=algos;
    }
    public override string ToString()=>"OKX OkxStreamObservation [REDACTED]";
}

public sealed record OkxStreamSnapshot
{
    public OkxObservationContext? Context { get; }
    public Guid? Epoch { get; }
    public long Sequence { get; }
    public bool PairedSubscribed { get; }
    public bool HasGap { get; }
    public string? ErrorCode { get; }
    public bool Closed { get; }
    public ImmutableArray<string> Limitations { get; }
    internal OkxStreamSnapshot(OkxObservationContext? context,Guid? epoch,long sequence,bool pairedSubscribed,bool hasGap,string? errorCode,bool closed,ImmutableArray<string> limitations)
    {
        Context=context;
        Epoch=epoch;
        Sequence=sequence;
        PairedSubscribed=pairedSubscribed;
        HasGap=hasGap;
        ErrorCode=errorCode;
        Closed=closed;
        Limitations=limitations;
    }
    public override string ToString()=>"OKX OkxStreamSnapshot [REDACTED]";
}
