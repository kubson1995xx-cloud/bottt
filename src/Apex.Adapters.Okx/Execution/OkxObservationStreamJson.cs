using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using static Apex.Adapters.Okx.Execution.OkxObservationJson;
namespace Apex.Adapters.Okx.Execution;

// Independent channel schemas, not REST Parse<Order> or fills. R1/R2 are explicit local precedence.
internal static class OkxObservationStreamJson
{
    internal sealed record Message(string Event,string? ConnId,string Digest,ImmutableArray<OkxExecutionOrderUpdateDto> Orders,ImmutableArray<OkxExecutionAlgoUpdateDto> Algos);
    private const string OrderStrings="instType instId tgtCcy ccy ordId clOrdId tag px pxUsd pxVol pxType sz notionalUsd ordType side posSide tdMode fillPx tradeId fillSz fillPnl fillTime fillFee fillFeeCcy fillPxVol fillPxUsd fillMarkVol fillFwdPx fillMarkPx fillIdxPx execType accFillSz fillNotionalUsd avgPx state lever attachAlgoClOrdId tpTriggerPx tpTriggerPxType tpOrdPx slTriggerPx slTriggerPxType slOrdPx stpId stpMode feeCcy fee rebateCcy rebate pnl source cancelSource amendSource category isTpLimit uTime cTime reqId amendResult reduceOnly quickMgnType algoClOrdId algoId lastPx code msg tradeQuoteCcy outcome riskBypassResult";
    private const string AlgoStrings="instType instId ccy ordId algoId clOrdId sz ordType side posSide tdMode tgtCcy lever state tpTriggerPx tpTriggerPxType tpOrdPx slTriggerPx slTriggerPxType slOrdPx triggerPx triggerPxType ordPx advanceOrdType last actualSz actualPx notionalUsd tag actualSide triggerTime reduceOnly failCode algoClOrdId reqId amendResult amendPxOnTriggerType cTime uTime isTradeBorrowMode chaseType chaseVal maxChaseType maxChaseVal tradeQuoteCcy closeFraction quickMgnType";
    internal static Message Parse(ReadOnlyMemory<byte> bytes,OkxStreamLane lane,string uid,string? connId,int phase)
    {
        _=new UTF8Encoding(false,true).GetCharCount(bytes.Span);
        using var doc=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=32});var root=doc.RootElement;Grammar.Unique(root);
        string channel=lane==OkxStreamLane.PrivateOrders?"orders":"orders-algo",id=lane==OkxStreamLane.PrivateOrders?"orders1":"algos1";
        Message Result(string name,string? conn=null,ImmutableArray<OkxExecutionOrderUpdateDto> orders=default,ImmutableArray<OkxExecutionAlgoUpdateDto> algos=default)=>new(name,conn,PayloadDigest(root),orders.IsDefault?[]:orders,algos.IsDefault?[]:algos);
        if(root.TryGetProperty("event",out var evt))
        {
            string kind=evt.GetString()!;
            if(kind=="login")
            {
                Shape(root,"event code msg connId");if(phase!=0||S(root,"code")!="0"||S(root,"connId").Length==0)throw new JsonException();return Result("login",S(root,"connId"));
            }
            if(kind=="subscribe")
            {
                ObjectEnvelope(root,"id event connId code msg","arg");Shape(root.GetProperty("arg"),"channel instType");
                var arg=root.GetProperty("arg");if(phase!=1||S(root,"id")!=id||S(root,"connId")!=connId||S(arg,"channel")!=channel||S(arg,"instType")!="SPOT"||Optional(root,"code") is not (null or "0"))throw new JsonException();return Result("subscribe",connId);
            }
            if(kind=="channel-conn-count")
            {
                Shape(root,"event channel connCount connId");if(phase!=2||S(root,"channel")!=channel||S(root,"connId")!=connId)throw new JsonException();_=S(root,"connCount");return Result("count",connId);
            }
            // error/unsubscribe/count-error/notices (including 64008/64010)/unknown are terminal.
            throw new JsonException();
        }
        if(phase!=2)throw new JsonException();ObjectEnvelope(root,"","arg",arrays:"data");var routing=root.GetProperty("arg");Shape(routing,"channel uid instType instFamily instId");
        if(S(routing,"channel")!=channel||S(routing,"uid")!=uid||S(routing,"instType")!="SPOT")throw new JsonException();
        var data=root.GetProperty("data");if(data.GetArrayLength() is 0 or >100)throw new CardinalityException();
        var orders=ImmutableArray.CreateBuilder<OkxExecutionOrderUpdateDto>();var algos=ImmutableArray.CreateBuilder<OkxExecutionAlgoUpdateDto>();
        foreach(var r in data.EnumerateArray())
        {
            if(S(r,"instType")!="SPOT"||S(r,"instId").Length is 0 or >128||Optional(routing,"instId") is string inst&&inst!=S(r,"instId"))throw new JsonException();
            if(lane==OkxStreamLane.PrivateOrders){ValidateOrder(r);orders.Add(OrderRow(r));}
            else {ValidateAlgoShape(r,AlgoStrings,false);algos.Add(new(AlgoRow(r),Optional(r,"reqId"),Optional(r,"amendResult"),Number(r,"notionalUsd"),PayloadDigest(r)));}
        }
        return Result("data",connId,orders.ToImmutable(),algos.ToImmutable());
    }
    private static void ObjectEnvelope(JsonElement root,string strings,string objects,string arrays="")
    {
        if(root.ValueKind!=JsonValueKind.Object)throw new JsonException();
        foreach(var p in root.EnumerateObject())
        {
            if(objects.Split(' ').Contains(p.Name,StringComparer.Ordinal)){if(p.Value.ValueKind!=JsonValueKind.Object)throw new JsonException();}
            else if(arrays.Split(' ').Contains(p.Name,StringComparer.Ordinal)){if(p.Value.ValueKind!=JsonValueKind.Array)throw new JsonException();}
            else if(!strings.Split(' ').Contains(p.Name,StringComparer.Ordinal)||p.Value.ValueKind!=JsonValueKind.String)throw new JsonException();
        }
    }
    private static void ValidateOrder(JsonElement r)
    {
        if(r.ValueKind!=JsonValueKind.Object)throw new JsonException();
        foreach(var p in r.EnumerateObject())
        {
            if(p.Name=="linkedAlgoOrd")Shape(p.Value,"algoId"); // R1 String only, table Object retained in oracle.
            else if(p.Name=="attachAlgoOrds")
            {
                if(p.Value.ValueKind!=JsonValueKind.Array)throw new JsonException();if(p.Value.GetArrayLength()>32)throw new CardinalityException();
                foreach(var a in p.Value.EnumerateArray())Shape(a,"attachAlgoId attachAlgoClOrdId tpOrdKind tpTriggerPx tpTriggerRatio tpTriggerPxType tpOrdPx slTriggerPx slTriggerRatio slTriggerPxType slOrdPx sz amendPxOnTriggerType callbackRatio callbackSpread activePx");
            }
            else if(!OrderStrings.Split(' ').Contains(p.Name,StringComparer.Ordinal)||p.Value.ValueKind!=JsonValueKind.String)throw new JsonException();
        }
        if(!Grammar.OrderId(S(r,"ordId")))throw new JsonException();_=S(r,"clOrdId");
    }
    private static OkxExecutionOrderUpdateDto OrderRow(JsonElement r)=>new(S(r,"instType"),
        S(r,"instId"),
        S(r,"ordId"),
        S(r,"clOrdId"),
        Optional(r,"state"),
        Optional(r,"ordType"),
        Optional(r,"side"),
        Optional(r,"posSide"),
        Optional(r,"tdMode"),
        Optional(r,"tgtCcy"),
        Optional(r,"ccy"),
        Optional(r,"tag"),
        Optional(r,"tradeId"),
        Optional(r,"execType"),
        Optional(r,"feeCcy"),
        Optional(r,"rebateCcy"),
        Optional(r,"fillFeeCcy"),
        Optional(r,"algoId"),
        Optional(r,"algoClOrdId"),
        Optional(r,"attachAlgoClOrdId"),
        Optional(r,"tradeQuoteCcy"),
        Optional(r,"source"),
        Optional(r,"category"),
        Optional(r,"cancelSource"),
        Optional(r,"amendSource"),
        Optional(r,"reqId"),
        Optional(r,"amendResult"),
        Optional(r,"code"),
        Optional(r,"reduceOnly"),
        Optional(r,"isTpLimit"),
        Optional(r,"tpTriggerPxType"),
        Optional(r,"slTriggerPxType"),
        Optional(r,"stpMode"),
        Optional(r,"outcome"),
        Number(r,"px"),
        Number(r,"sz"),
        Number(r,"accFillSz"),
        Number(r,"avgPx"),
        Number(r,"pnl"),
        Number(r,"fillPx"),
        Number(r,"fillSz"),
        Number(r,"fillPnl"),
        Number(r,"fillFee"),
        Number(r,"fee"),
        Number(r,"rebate"),
        Number(r,"tpTriggerPx"),
        Number(r,"tpOrdPx"),
        Number(r,"slTriggerPx"),
        Number(r,"slOrdPx"),
        Number(r,"lastPx"),
        Timestamp(r,"cTime"),
        Timestamp(r,"uTime"),
        Timestamp(r,"fillTime"),
        r.TryGetProperty("linkedAlgoOrd",out var linked)?new OkxExecutionLinkedAlgoDto(Optional(linked,"algoId")):null,
        r.TryGetProperty("attachAlgoOrds",out var attachments)?attachments.EnumerateArray().Select(Attachment).ToImmutableArray():null,
        PayloadDigest(r));
    private static OkxExecutionAttachmentDto Attachment(JsonElement r)=>new(
        Optional(r,"attachAlgoId"),Optional(r,"attachAlgoClOrdId"),Optional(r,"tpOrdKind"),Optional(r,"tpTriggerPxType"),Optional(r,"slTriggerPxType"),Optional(r,"amendPxOnTriggerType"),null,
        Number(r,"tpTriggerPx"),Number(r,"tpTriggerRatio"),Number(r,"tpOrdPx"),Number(r,"slTriggerPx"),Number(r,"slTriggerRatio"),Number(r,"slOrdPx"),Number(r,"sz"),Number(r,"callbackRatio"),Number(r,"callbackSpread"),Number(r,"activePx"),PayloadDigest(r));
}
