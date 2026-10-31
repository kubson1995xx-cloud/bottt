using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
namespace Apex.Adapters.Okx.Execution;

// Endpoint schemas independently pinned in baseline-schema.json (retained EEA R anchors).
internal static class OkxObservationJson
{
    internal sealed class CardinalityException : Exception { }
    internal sealed class VenueException(string code) : Exception { internal string Code { get; }=code; }
    internal static IReadOnlyList<T> Parse<T>(ReadOnlyMemory<byte> bytes,string endpoint)
    {
        _=new UTF8Encoding(false,true).GetCharCount(bytes.Span);
        using var doc=JsonDocument.Parse(bytes,new JsonDocumentOptions { MaxDepth=32 });
        var root=doc.RootElement;Grammar.Unique(root);
        Shape(root,"code msg",arrays:"data");
        string code=S(root,"code");_=S(root,"msg");
        if(code!="0" && !Grammar.Match(code,"[1-9][0-9]{4,7}"))throw new JsonException();
        var data=root.GetProperty("data");if(data.ValueKind!=JsonValueKind.Array)throw new JsonException();
        if(code!="0")throw new VenueException(code);
        if(endpoint=="/api/v5/account/instruments") { if(data.GetArrayLength()>4096)throw new CardinalityException(); }
        else if(data.GetArrayLength()!=1)throw new CardinalityException();
        return data.EnumerateArray().Select(r=>(T)(object)(endpoint switch
        {
            "/api/v5/public/time"=>Time(r),
            "/api/v5/account/config"=>(object)Account(r),
            "/api/v5/account/balance"=>Balance(r),
            "/api/v5/account/instruments"=>Instrument(r),
            "/api/v5/account/trade-fee"=>Fees(r),
            _=>throw new JsonException()
        })).ToImmutableArray();
    }
    private static OkxTimeDto Time(JsonElement r) { Shape(r,"ts");return new(Timestamp(r,"ts",true)); }
    private static OkxExecutionAccountDto Account(JsonElement r)
    {
        // R:2849-2934. settleCcyList array is the explicit parent compatibility ruling (R:2838).
        Shape(r,"uid mainUid acctLv acctStpMode posMode greeksType feeType level levelTmp ctIsoMode mgnIsoMode spotOffsetType stgyType roleType spotRoleType opAuth kycLv label ip perm liquidationGear type settleCcy",
            "autoLoan enableSpotBorrow spotBorrowAutoRepay","traderInsts spotTraderInsts settleCcyList");
        foreach(var n in new[]{"traderInsts","spotTraderInsts","settleCcyList"})if(r.TryGetProperty(n,out var a))StringArray(a);
        return new(S(r,"uid"),S(r,"mainUid"),S(r,"acctLv"),S(r,"perm"),S(r,"ip"),S(r,"feeType"),B(r,"autoLoan"),B(r,"enableSpotBorrow"),B(r,"spotBorrowAutoRepay"),S(r,"type"),S(r,"spotRoleType"),S(r,"kycLv"));
    }
    private static OkxExecutionBalanceDto Balance(JsonElement r)
    {
        // R:1418-1554. USD aggregate fields are discarded, never quote cash.
        Shape(r,"uTime totalEq isoEq adjEq availEq ordFroz imr mmr borrowFroz mgnRatio notionalUsd notionalUsdForBorrow notionalUsdForSwap notionalUsdForFutures notionalUsdForOption upl delta deltaLever deltaNeutralStatus",arrays:"details");
        var details=r.GetProperty("details");if(details.GetArrayLength()>256)throw new CardinalityException();
        return new(Timestamp(r,"uTime",true),details.EnumerateArray().Select(d=>
        {
            Shape(d,"ccy eq cashBal uTime isoEq availEq disEq fixedBal availBal frozenBal ordFrozen liab upl uplLiab crossLiab rewardBal isoLiab mgnRatio imr mmr interest twap frpType maxLoan eqUsd borrowFroz notionalLever stgyEq isoUpl spotInUseAmt clSpotInUseAmt maxSpotInUse spotIsoBal smtSyncEq spotCopyTradingEq spotBal openAvgPx accAvgPx spotUpl spotUplRatio totalPnl totalPnlRatio colRes colBorrAutoConversion autoLendStatus autoLendMtAmt","collateralRestrict collateralEnabled");
            return new OkxExecutionBalanceDetailDto(S(d,"ccy"),Number(d,"cashBal"),Number(d,"availBal"),Number(d,"frozenBal"),Number(d,"ordFrozen"),Number(d,"eq"),Number(d,"liab"),Timestamp(d,"uTime"),Number(d,"interest"),Number(d,"crossLiab"),Number(d,"isoLiab"),Number(d,"stgyEq"),Number(d,"fixedBal"));
        }).ToImmutableArray());
    }
    private static readonly ImmutableDictionary<string,string> MaxUnits=new Dictionary<string,string>
    {
        ["maxLmtSz"]="base currency",["maxMktSz"]="USDT",["maxLmtAmt"]="USD",["maxMktAmt"]="USD",
        ["maxTwapSz"]="base currency",["maxIcebergSz"]="base currency",["maxTriggerSz"]="base currency",["maxStopSz"]="USDT",
        ["maxPlatOILmt"]="USD (derivatives only)",["maxPlatOICoinLmt"]="coins (derivatives only)"
    }.ToImmutableDictionary(StringComparer.Ordinal);
    private static OkxExecutionInstrumentDto Instrument(JsonElement r)
    {
        // R:1109-1274. Quote list is private eligibility, not catalog quote equivalence.
        Shape(r,"instType seriesId instId uly groupId instFamily baseCcy quoteCcy settleCcy ctVal ctMult ctValCcy optType stk listTime auctionEndTime contTdSwTime preMktSwTime openType elp rpi expTime lever tickSz lotSz minSz ctType state ruleType posLmtAmt posLmtPct maxPlatOILmt maxPlatOICoinLmt longPosRemainingQuota shortPosRemainingQuota maxLmtSz maxMktSz maxLmtAmt maxMktAmt maxTwapSz maxIcebergSz maxTriggerSz maxStopSz instCategory initPxLmtPct floatPxLmtPct maxPxLmtPct","futureSettlement","tradeQuoteCcyList upcChg","instIdCode");
        if(r.TryGetProperty("upcChg",out var changes))foreach(var c in changes.EnumerateArray())Shape(c,"param newValue effTime");
        return new(S(r,"instId"),S(r,"instType"),S(r,"baseCcy"),S(r,"quoteCcy"),StringArray(r.GetProperty("tradeQuoteCcyList")),Optional(r,"groupId"),S(r,"state"),Number(r,"tickSz"),Number(r,"lotSz"),Number(r,"minSz"),Timestamp(r,"listTime"),Timestamp(r,"expTime"),MaxUnits.ToImmutableDictionary(x=>x.Key,x=>new OkxObservationMaximum(Number(r,x.Key),x.Value),StringComparer.Ordinal));
    }
    private static OkxExecutionFeeDto Fees(JsonElement r)
    {
        // R:3885-3923. Deprecated top-level rates/fiat are type-checked, never fee evidence.
        Shape(r,"level delivery exercise instType ts taker maker takerU makerU takerUSDC makerUSDC ruleType category settle",arrays:"feeGroup fiat");
        if(r.TryGetProperty("fiat",out var fiat))foreach(var f in fiat.EnumerateArray())Shape(f,"ccy taker maker");
        var groups=r.GetProperty("feeGroup");if(groups.GetArrayLength()>256)throw new CardinalityException();
        var output=ImmutableDictionary.CreateBuilder<string,OkxExecutionFeeGroupDto>(StringComparer.Ordinal);
        foreach(var g in groups.EnumerateArray())
        {
            Shape(g,"taker maker groupId elpMaker rpiMaker");var id=S(g,"groupId");_=S(g,"maker");_=S(g,"taker");
            if(id.Length==0 || output.ContainsKey(id))throw new JsonException();
            output.Add(id,new(id,Number(g,"maker"),Number(g,"taker"),Number(g,"rpiMaker"),Number(g,"elpMaker")));
        }
        return new(S(r,"instType"),Timestamp(r,"ts",true),output.ToImmutable());
    }
    private static string S(JsonElement e,string n)=>Grammar.String(e,n);
    private static string? Optional(JsonElement e,string n)=>e.TryGetProperty(n,out var p)?p.GetString():null;
    private static bool B(JsonElement e,string n)=>e.GetProperty(n).GetBoolean();
    private static ImmutableArray<string> StringArray(JsonElement a)
    { if(a.ValueKind!=JsonValueKind.Array)throw new JsonException();return a.EnumerateArray().Select(v=>v.ValueKind==JsonValueKind.String?v.GetString()!:throw new JsonException()).ToImmutableArray(); }
    private static void Shape(JsonElement e,string strings,string bools="",string arrays="",string nullableInts="")
    {
        if(e.ValueKind!=JsonValueKind.Object)throw new JsonException();
        var ss=strings.Split(' ',StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var bs=bools.Split(' ',StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var aa=arrays.Split(' ',StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var ii=nullableInts.Split(' ',StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        foreach(var p in e.EnumerateObject())
        {
            bool valid=ss.Contains(p.Name)?p.Value.ValueKind==JsonValueKind.String:
                bs.Contains(p.Name)?p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                aa.Contains(p.Name)?p.Value.ValueKind==JsonValueKind.Array:
                ii.Contains(p.Name)&&(p.Value.ValueKind==JsonValueKind.Null || p.Value.ValueKind==JsonValueKind.Number&&p.Value.TryGetInt64(out _));
            if(!valid)throw new JsonException();
        }
    }
    private static OkxDecimal Number(JsonElement e,string n)
    {
        var raw=Optional(e,n);if(string.IsNullOrEmpty(raw))return new(raw,null);
        if(!Grammar.Match(raw,"-?(?:0|[1-9][0-9]*)(?:\\.[0-9]+)?") || raw.Length>128 || !decimal.TryParse(raw,NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var value))throw new JsonException();
        // decimal.TryParse can silently round/underflow. Compare exact rational coefficients.
        var fraction=raw.Contains('.')?raw.Length-raw.IndexOf('.')-1:0;
        var coeff=BigInteger.Parse(raw.Replace(".",""),CultureInfo.InvariantCulture);
        var bits=decimal.GetBits(value);var actual=(BigInteger)(uint)bits[0]+((BigInteger)(uint)bits[1]<<32)+((BigInteger)(uint)bits[2]<<64);
        if(value<0)actual=-actual;int scale=(bits[3]>>16)&255;
        if(coeff*BigInteger.Pow(10,scale)!=actual*BigInteger.Pow(10,fraction))throw new JsonException();
        return new(raw,value);
    }
    private static OkxTimestamp Timestamp(JsonElement e,string n,bool required=false)
    {
        var raw=required?S(e,n):Optional(e,n);if(string.IsNullOrEmpty(raw))return new(raw,null);
        if(!Grammar.Match(raw,"[0-9]{1,17}") || !long.TryParse(raw,NumberStyles.None,CultureInfo.InvariantCulture,out var ms))throw new JsonException();
        try{return new(raw,DateTimeOffset.FromUnixTimeMilliseconds(ms));}catch(ArgumentOutOfRangeException){throw new JsonException();}
    }
}
