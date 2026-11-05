using System.Collections.Immutable;
namespace Apex.Adapters.Okx.Execution;

// Safe output types hold code-owned names and classes only, never operand values.
internal sealed record OkxOrderFieldDifference(string Name,string LeftKind,string RightKind,int? LeftCount=null,int? RightCount=null);
internal sealed record OkxOrderDifferences(IReadOnlyList<OkxOrderFieldDifference> Fields,bool Truncated);
internal static class OkxOrderConsistency
{
    // One predicate supplies both equality and diagnostics. Bitwise AND evaluates all
    // operands for diagnostics. These raw entry points never use contextual allowances.
    internal static bool Equivalent(OkxExecutionOrderDto a,OkxExecutionOrderDto b)=>Compare(a,b,null);
    // Only the contextual owner may supply a corroborated economic quote. Raw
    // Equivalent/Differences remain strict and never consume this allowance.
    internal static bool EquivalentOwnCash(OkxExecutionOrderDto a,OkxExecutionOrderDto b,string quote)=>
        !string.IsNullOrEmpty(quote)&&a.InstType=="SPOT"&&b.InstType=="SPOT"&&a.TdMode=="cash"&&b.TdMode=="cash"&&
        a.PosSide is "net" or ""&&b.PosSide is "net" or ""&&
        (a.TradeQuoteCcy==quote||a.TradeQuoteCcy=="")&&(b.TradeQuoteCcy==quote||b.TradeQuoteCcy=="")&&Compare(a,b,null,true);
    internal static OkxOrderDifferences Differences(OkxExecutionOrderDto a,OkxExecutionOrderDto b)
    {var d=new DifferenceSink();Compare(a,b,d);return new(d.Fields.ToArray(),d.Truncated);}
    private static bool Compare(OkxExecutionOrderDto a,OkxExecutionOrderDto b,DifferenceSink? d,bool ownCash=false)=>
        Check(a.InstType==b.InstType,"InstType",Kind(a.InstType),Kind(b.InstType),d) &
        Check(a.InstId==b.InstId,"InstId",Kind(a.InstId),Kind(b.InstId),d) &
        Check(a.OrdId==b.OrdId,"OrdId",Kind(a.OrdId),Kind(b.OrdId),d) &
        Check(a.ClOrdId==b.ClOrdId,"ClOrdId",Kind(a.ClOrdId),Kind(b.ClOrdId),d) &
        Check(Number(a.Px,b.Px),"Px",Kind(a.Px),Kind(b.Px),d) &
        Check(Number(a.Sz,b.Sz),"Sz",Kind(a.Sz),Kind(b.Sz),d) &
        Check(Number(a.Pnl,b.Pnl),"Pnl",Kind(a.Pnl),Kind(b.Pnl),d) &
        Check(Number(a.AccFillSz,b.AccFillSz),"AccFillSz",Kind(a.AccFillSz),Kind(b.AccFillSz),d) &
        Check(Number(a.FillPx,b.FillPx),"FillPx",Kind(a.FillPx),Kind(b.FillPx),d) &
        Check(Number(a.FillSz,b.FillSz),"FillSz",Kind(a.FillSz),Kind(b.FillSz),d) &
        Check(Number(a.AvgPx,b.AvgPx),"AvgPx",Kind(a.AvgPx),Kind(b.AvgPx),d) &
        Check(Number(a.TpTriggerPx,b.TpTriggerPx),"TpTriggerPx",Kind(a.TpTriggerPx),Kind(b.TpTriggerPx),d) &
        Check(Number(a.TpOrdPx,b.TpOrdPx),"TpOrdPx",Kind(a.TpOrdPx),Kind(b.TpOrdPx),d) &
        Check(Number(a.SlTriggerPx,b.SlTriggerPx),"SlTriggerPx",Kind(a.SlTriggerPx),Kind(b.SlTriggerPx),d) &
        Check(Number(a.SlOrdPx,b.SlOrdPx),"SlOrdPx",Kind(a.SlOrdPx),Kind(b.SlOrdPx),d) &
        Check(Number(a.Fee,b.Fee),"Fee",Kind(a.Fee),Kind(b.Fee),d) &
        Check(Number(a.Rebate,b.Rebate),"Rebate",Kind(a.Rebate),Kind(b.Rebate),d) &
        Check(Time(a.FillTime,b.FillTime),"FillTime",Kind(a.FillTime),Kind(b.FillTime),d) &
        Check(Time(a.UTime,b.UTime),"UTime",Kind(a.UTime),Kind(b.UTime),d) &
        Check(Time(a.CTime,b.CTime),"CTime",Kind(a.CTime),Kind(b.CTime),d) &
        Check(a.TgtCcy==b.TgtCcy,"TgtCcy",Kind(a.TgtCcy),Kind(b.TgtCcy),d) &
        Check(a.Ccy==b.Ccy,"Ccy",Kind(a.Ccy),Kind(b.Ccy),d) &
        Check(a.OrdType==b.OrdType,"OrdType",Kind(a.OrdType),Kind(b.OrdType),d) &
        Check(a.Side==b.Side,"Side",Kind(a.Side),Kind(b.Side),d) &
        Check(ownCash||a.PosSide==b.PosSide,"PosSide",Kind(a.PosSide),Kind(b.PosSide),d) &
        Check(a.TdMode==b.TdMode,"TdMode",Kind(a.TdMode),Kind(b.TdMode),d) &
        Check(a.TradeId==b.TradeId,"TradeId",Kind(a.TradeId),Kind(b.TradeId),d) &
        Check(a.State==b.State,"State",Kind(a.State),Kind(b.State),d) &
        Check(a.StpMode==b.StpMode,"StpMode",Kind(a.StpMode),Kind(b.StpMode),d) &
        Check(a.AttachAlgoClOrdId==b.AttachAlgoClOrdId,"AttachAlgoClOrdId",Kind(a.AttachAlgoClOrdId),Kind(b.AttachAlgoClOrdId),d) &
        Check(a.TpTriggerPxType==b.TpTriggerPxType,"TpTriggerPxType",Kind(a.TpTriggerPxType),Kind(b.TpTriggerPxType),d) &
        Check(a.SlTriggerPxType==b.SlTriggerPxType,"SlTriggerPxType",Kind(a.SlTriggerPxType),Kind(b.SlTriggerPxType),d) &
        Check(a.FeeCcy==b.FeeCcy,"FeeCcy",Kind(a.FeeCcy),Kind(b.FeeCcy),d) &
        Check(a.RebateCcy==b.RebateCcy,"RebateCcy",Kind(a.RebateCcy),Kind(b.RebateCcy),d) &
        Check(a.Source==b.Source,"Source",Kind(a.Source),Kind(b.Source),d) &
        Check(a.Category==b.Category,"Category",Kind(a.Category),Kind(b.Category),d) &
        Check(a.ReduceOnly==b.ReduceOnly,"ReduceOnly",Kind(a.ReduceOnly),Kind(b.ReduceOnly),d) &
        Check(a.IsTpLimit==b.IsTpLimit,"IsTpLimit",Kind(a.IsTpLimit),Kind(b.IsTpLimit),d) &
        Check(a.CancelSource==b.CancelSource,"CancelSource",Kind(a.CancelSource),Kind(b.CancelSource),d) &
        Check(a.AlgoClOrdId==b.AlgoClOrdId,"AlgoClOrdId",Kind(a.AlgoClOrdId),Kind(b.AlgoClOrdId),d) &
        Check(a.AlgoId==b.AlgoId,"AlgoId",Kind(a.AlgoId),Kind(b.AlgoId),d) &
        Check(ownCash||a.TradeQuoteCcy==b.TradeQuoteCcy,"TradeQuoteCcy",Kind(a.TradeQuoteCcy),Kind(b.TradeQuoteCcy),d) &
        Check(a.Outcome==b.Outcome,"Outcome",Kind(a.Outcome),Kind(b.Outcome),d) &
        Extras(a.ConsistencyExtras,b.ConsistencyExtras,d,"ConsistencyExtras","pxUsd pxVol pxType stpId lever cancelSourceReason quickMgnType slippage") &
        Attachments(a.AttachAlgoOrds,b.AttachAlgoOrds,d) &
        Linked(a.LinkedAlgoOrd,b.LinkedAlgoOrd,d);
    // Descriptive Tag and raw PayloadDigest remain excluded exactly as before.
    private static bool Number(OkxDecimal a,OkxDecimal b)=>a.Value.HasValue&&b.Value.HasValue?a.Value==b.Value:a==b;
    private static bool Time(OkxTimestamp a,OkxTimestamp b)=>a.Utc.HasValue&&b.Utc.HasValue?a.Utc==b.Utc:a==b;
    private static string Kind(string? v)=>v==null?"Missing":v.Length==0?"Empty":"Known";
    private static string Kind(OkxDecimal v)=>Kind(v.Raw);
    private static string Kind(OkxTimestamp v)=>Kind(v.Raw);
    private static bool Check(bool equal,string name,string left,string right,DifferenceSink? d,int? lc=null,int? rc=null)
    {if(!equal)d?.Add(new(name,left,right,lc,rc));return equal;}
    private static bool Extras(ImmutableDictionary<string,string>? a,ImmutableDictionary<string,string>? b,DifferenceSink? d,string name,string whitelist)
    {
        bool equal=a!=null&&b!=null&&a.Count==b.Count&&a.All(p=>b.TryGetValue(p.Key,out var value)&&value==p.Value);
        if(equal||d==null)return equal;
        if(a==null||b==null){Check(false,name,a==null?"Missing":"Object",b==null?"Missing":"Object",d,a?.Count,b?.Count);return false;}
        string prefix=name=="ConsistencyExtras"?"": "AttachAlgoOrds[].";
        foreach(string key in whitelist.Split(' '))
        {
            bool ap=a.TryGetValue(key,out var av),bp=b.TryGetValue(key,out var bv);
            Check(ap==bp&&av==bv,prefix+key,Kind(av),Kind(bv),d);
        }
        // Unknown dictionary keys can veto equality, but can never become output names.
        if(a.Keys.Concat(b.Keys).Any(k=>!whitelist.Split(' ').Contains(k)))
            Check(false,name,"Object","Object",d,a.Count,b.Count);
        return false;
    }
    private static bool Linked(OkxExecutionLinkedAlgoDto? a,OkxExecutionLinkedAlgoDto? b,DifferenceSink? d)=>
        a==null||b==null?Check(a==b,"LinkedAlgoOrd",a==null?"Missing":"Object",b==null?"Missing":"Object",d):
        Check(a.AlgoId==b.AlgoId,"LinkedAlgoOrd.AlgoId",Kind(a.AlgoId),Kind(b.AlgoId),d);
    private static bool Attachments(ImmutableArray<OkxExecutionAttachmentDto>? a,ImmutableArray<OkxExecutionAttachmentDto>? b,DifferenceSink? d)
    {
        if(!a.HasValue||!b.HasValue)return Check(a.HasValue==b.HasValue,"AttachAlgoOrds",a.HasValue?"List":"Missing",b.HasValue?"List":"Missing",d,a?.Length,b?.Length);
        if(a.Value.Length!=b.Value.Length)return Check(false,"AttachAlgoOrds","List","List",d,a.Value.Length,b.Value.Length);
        bool equal=true;
        for(int i=0;i<a.Value.Length;i++)equal=Attachment(a.Value[i],b.Value[i],d)&equal;
        return equal;
    }
    private static bool Attachment(OkxExecutionAttachmentDto a,OkxExecutionAttachmentDto b,DifferenceSink? d)=>
        Check(a.AttachAlgoId==b.AttachAlgoId,"AttachAlgoOrds[].AttachAlgoId",Kind(a.AttachAlgoId),Kind(b.AttachAlgoId),d) &
        Check(a.AttachAlgoClOrdId==b.AttachAlgoClOrdId,"AttachAlgoOrds[].AttachAlgoClOrdId",Kind(a.AttachAlgoClOrdId),Kind(b.AttachAlgoClOrdId),d) &
        Check(a.TpOrdKind==b.TpOrdKind,"AttachAlgoOrds[].TpOrdKind",Kind(a.TpOrdKind),Kind(b.TpOrdKind),d) &
        Check(a.TpTriggerPxType==b.TpTriggerPxType,"AttachAlgoOrds[].TpTriggerPxType",Kind(a.TpTriggerPxType),Kind(b.TpTriggerPxType),d) &
        Check(a.SlTriggerPxType==b.SlTriggerPxType,"AttachAlgoOrds[].SlTriggerPxType",Kind(a.SlTriggerPxType),Kind(b.SlTriggerPxType),d) &
        Check(a.AmendPxOnTriggerType==b.AmendPxOnTriggerType,"AttachAlgoOrds[].AmendPxOnTriggerType",Kind(a.AmendPxOnTriggerType),Kind(b.AmendPxOnTriggerType),d) &
        Check(a.FailCode==b.FailCode,"AttachAlgoOrds[].FailCode",Kind(a.FailCode),Kind(b.FailCode),d) &
        Check(Number(a.TpTriggerPx,b.TpTriggerPx),"AttachAlgoOrds[].TpTriggerPx",Kind(a.TpTriggerPx),Kind(b.TpTriggerPx),d) &
        Check(Number(a.TpTriggerRatio,b.TpTriggerRatio),"AttachAlgoOrds[].TpTriggerRatio",Kind(a.TpTriggerRatio),Kind(b.TpTriggerRatio),d) &
        Check(Number(a.TpOrdPx,b.TpOrdPx),"AttachAlgoOrds[].TpOrdPx",Kind(a.TpOrdPx),Kind(b.TpOrdPx),d) &
        Check(Number(a.SlTriggerPx,b.SlTriggerPx),"AttachAlgoOrds[].SlTriggerPx",Kind(a.SlTriggerPx),Kind(b.SlTriggerPx),d) &
        Check(Number(a.SlTriggerRatio,b.SlTriggerRatio),"AttachAlgoOrds[].SlTriggerRatio",Kind(a.SlTriggerRatio),Kind(b.SlTriggerRatio),d) &
        Check(Number(a.SlOrdPx,b.SlOrdPx),"AttachAlgoOrds[].SlOrdPx",Kind(a.SlOrdPx),Kind(b.SlOrdPx),d) &
        Check(Number(a.Sz,b.Sz),"AttachAlgoOrds[].Sz",Kind(a.Sz),Kind(b.Sz),d) &
        Check(Number(a.CallbackRatio,b.CallbackRatio),"AttachAlgoOrds[].CallbackRatio",Kind(a.CallbackRatio),Kind(b.CallbackRatio),d) &
        Check(Number(a.CallbackSpread,b.CallbackSpread),"AttachAlgoOrds[].CallbackSpread",Kind(a.CallbackSpread),Kind(b.CallbackSpread),d) &
        Check(Number(a.ActivePx,b.ActivePx),"AttachAlgoOrds[].ActivePx",Kind(a.ActivePx),Kind(b.ActivePx),d) &
        Extras(a.ConsistencyExtras,b.ConsistencyExtras,d,"AttachAlgoOrds[].ConsistencyExtras","percent failReason");
    private sealed class DifferenceSink
    {
        internal readonly List<OkxOrderFieldDifference> Fields=new();
        internal bool Truncated;
        internal void Add(OkxOrderFieldDifference field)
        {
            if(Fields.Any(f=>f.Name==field.Name))return;
            if(Fields.Count==16){Truncated=true;return;}
            Fields.Add(field);
        }
    }
}
