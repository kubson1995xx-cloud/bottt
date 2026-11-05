using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
namespace Apex.Adapters.Okx.Execution;

// Endpoint schemas independently pinned in baseline-schema.json (retained EEA R anchors).
internal static partial class OkxObservationJson
{
    internal sealed class CardinalityException(OkxEnrollmentPayloadFailure? failure=null,string phase="PARSE_ENVELOPE",string? code=null) : Exception
    {
        internal OkxEnrollmentPayloadFailure? Failure { get; }=failure;
        internal string Phase { get; }=phase;
        internal string? OkxCode { get; }=code;
    }
    internal sealed class VenueException(string code) : Exception { internal string Code { get; }=code; }
    internal static IReadOnlyList<T> Parse<T>(ReadOnlyMemory<byte> bytes,string endpoint,int? pageLimit=null,Action<string?,string,int>? privateSchemaObserver=null)
        =>Parse<T>(bytes,endpoint,pageLimit,privateSchemaObserver,false);
    internal static IReadOnlyList<T> Parse<T>(ReadOnlyMemory<byte> bytes,string endpoint,int? pageLimit,Action<string?,string,int>? privateSchemaObserver,bool ownedRecovery)
    {
        bool discovery=endpoint is "/api/v5/public/time" or "/api/v5/account/config" or "/api/v5/account/balance" or "/api/v5/account/instruments";
        bool pendingDiagnostic=ownedRecovery&&endpoint=="/api/v5/trade/orders-algo-pending";
        bool exact=endpoint is "/api/v5/trade/order" or "/api/v5/trade/order-algo",detailed=discovery||exact||pendingDiagnostic;
        // Explicit internal retrospective profile only; public/default parsing stays strict.
        if(ownedRecovery&&!exact&&endpoint is not ("/api/v5/trade/orders-pending" or "/api/v5/trade/orders-history" or "/api/v5/trade/orders-history-archive" or "/api/v5/trade/orders-algo-pending"))throw new JsonException();
        try{_=new UTF8Encoding(false,true).GetCharCount(bytes.Span);}
        catch(DecoderFallbackException) when(detailed){throw Failure("PARSE_UTF8","INVALID_UTF8","$","Utf8","Invalid");}
        JsonDocument document;
        try{document=JsonDocument.Parse(bytes,new JsonDocumentOptions { MaxDepth=32 });}
        catch(JsonException) when(detailed){throw Failure("PARSE_JSON","INVALID_JSON","$","JsonDocument","Invalid");}
        using var doc=document;
        var root=doc.RootElement;
        if(detailed)UniqueDiscovery(root,"$",exact||pendingDiagnostic?ExactDiagnosticNames(endpoint):null);else Grammar.Unique(root);
        string? validatedCode=null;
        // Retain an independently syntax-validated code even when the data kind
        // fails the envelope. This is diagnostic only, never acceptance evidence.
        if(pendingDiagnostic&&root.ValueKind==JsonValueKind.Object&&root.TryGetProperty("code",out var observedCode)&&observedCode.ValueKind==JsonValueKind.String)
        {string? c=observedCode.GetString();if(c=="0"||c!=null&&Grammar.Match(c,"[1-9][0-9]{4,7}"))validatedCode=c;}
        try
        {
        if(exact)ObservedShape(root,"code msg",arrays:"data",observer:privateSchemaObserver);else Shape(root,"code msg",arrays:"data");
        string code=S(root,"code");_=S(root,"msg");
        if(code!="0" && !Grammar.Match(code,"[1-9][0-9]{4,7}"))throw Failure("PARSE_ENVELOPE","VENUE_CODE",".code","NumericVenueCode","String");
        var data=Required(root,"data","Array");if(data.ValueKind!=JsonValueKind.Array)throw Failure("PARSE_ENVELOPE","TYPE",".data","Array",Kind(data));
        validatedCode=code;
        if(code!="0")throw new VenueException(code);
        if(AlgoSchemas.TryGetValue(endpoint,out var algoStrings))
        {
            if(typeof(T)!=typeof(OkxExecutionAlgoDto))throw new JsonException();
            if(endpoint=="/api/v5/trade/order-algo")At(".data",()=>{ExactCount(data,1,1,"ExactlyOne");return true;});
            else if(pageLimit is null or <1 or >100||data.GetArrayLength()>pageLimit)throw new CardinalityException();
            return data.EnumerateArray().Select(r=>(T)(object)At(".data[]",()=>ParseAlgo(r,algoStrings,endpoint!="/api/v5/trade/order-algo",privateSchemaObserver,ownedRecovery))).ToImmutableArray();
        }
        var recovery=RecoverySchemas.GetValueOrDefault(endpoint);
        Type expected=recovery?.Type??(endpoint switch
        {
            "/api/v5/public/time"=>typeof(OkxTimeDto),"/api/v5/account/config"=>typeof(OkxExecutionAccountDto),
            "/api/v5/account/balance"=>typeof(OkxExecutionBalanceDto),"/api/v5/account/instruments"=>typeof(OkxExecutionInstrumentDto),
            "/api/v5/account/trade-fee"=>typeof(OkxExecutionFeeDto),"/api/v5/account/interest-limits"=>typeof(OkxMarketLoansDto),_=>throw new JsonException()
        });
        if(typeof(T)!=expected)throw new JsonException();
        if(recovery is not null&&endpoint!="/api/v5/trade/order")
        {if(pageLimit is null or <1 or >100)throw new JsonException();if(data.GetArrayLength()>pageLimit)throw new CardinalityException();}
        else if(endpoint=="/api/v5/account/instruments") { if(data.GetArrayLength()>4096)throw new CardinalityException(new("CARDINALITY",".data","AtMost4096","OverLimit")); }
        else if(data.GetArrayLength()!=1)
        {
            throw new CardinalityException(new("CARDINALITY",".data","ExactlyOne",data.GetArrayLength()==0?"Empty":"Multiple"){ActualCount=data.GetArrayLength(),MinimumCount=1,MaximumCount=1});
        }
        return data.EnumerateArray().Select(r=>(T)At(".data[]",()=>recovery is not null?Recovery(r,recovery,privateSchemaObserver,ownedRecovery):endpoint switch
        {
            "/api/v5/public/time"=>(object)Time(r),"/api/v5/account/config"=>Account(r),
            "/api/v5/account/balance"=>Balance(r,privateSchemaObserver),"/api/v5/account/instruments"=>Instrument(r,privateSchemaObserver),
            "/api/v5/account/trade-fee"=>Fees(r),"/api/v5/account/interest-limits"=>MarketLoans(r),_=>throw new JsonException()
        })).ToImmutableArray();
        }
        catch(PayloadException e) when(detailed){throw e.Prefix("$").WithCode(validatedCode);}
        catch(CardinalityException e) when(detailed&&e.Failure!=null){throw new CardinalityException(e.Failure with {Path="$"+e.Failure.Path},e.Phase,validatedCode);}
    }
    private sealed record RecoverySchema(Type Type,string Strings,bool ExactCashSpotEmptySlippage=false);
    // Separate registration for each endpoint; no schema union with old lookup, algos or streams.
    private static readonly Dictionary<string,RecoverySchema> RecoverySchemas=new(StringComparer.Ordinal)
    {
        ["/api/v5/trade/order"]=new(typeof(OkxExecutionOrderDto),"instType instId tgtCcy ccy ordId clOrdId tag px pxUsd pxVol pxType sz pnl ordType side posSide tdMode accFillSz fillPx tradeId fillSz fillTime avgPx state stpId stpMode lever attachAlgoClOrdId tpTriggerPx tpTriggerPxType tpOrdPx slTriggerPx slTriggerPxType slOrdPx feeCcy fee rebateCcy rebate source category reduceOnly isTpLimit cancelSource cancelSourceReason quickMgnType algoClOrdId algoId uTime cTime tradeQuoteCcy outcome",ExactCashSpotEmptySlippage:true),
        ["/api/v5/trade/orders-pending"]=new(typeof(OkxExecutionOrderDto),"instType instId tgtCcy ccy ordId clOrdId tag px pxUsd pxVol pxType sz pnl ordType side posSide tdMode accFillSz fillPx tradeId fillSz fillTime avgPx state stpId stpMode lever attachAlgoClOrdId tpTriggerPx tpTriggerPxType tpOrdPx slTriggerPx slTriggerPxType slOrdPx feeCcy fee rebateCcy rebate source category reduceOnly isTpLimit cancelSource cancelSourceReason quickMgnType algoClOrdId algoId uTime cTime tradeQuoteCcy outcome"),
        ["/api/v5/trade/orders-history"]=new(typeof(OkxExecutionOrderDto),"instType instId tgtCcy ccy ordId clOrdId tag px pxUsd pxVol pxType sz pnl ordType side posSide tdMode accFillSz fillPx tradeId fillSz fillTime avgPx state stpId stpMode lever attachAlgoClOrdId tpTriggerPx tpTriggerPxType tpOrdPx slTriggerPx slTriggerPxType slOrdPx feeCcy fee rebateCcy rebate source category reduceOnly isTpLimit cancelSource cancelSourceReason quickMgnType algoClOrdId algoId uTime cTime tradeQuoteCcy outcome"),
        ["/api/v5/trade/orders-history-archive"]=new(typeof(OkxExecutionOrderDto),"instType instId tgtCcy ccy ordId clOrdId tag px pxUsd pxVol pxType sz pnl ordType side posSide tdMode accFillSz fillPx tradeId fillSz fillTime avgPx state stpId stpMode lever attachAlgoClOrdId tpTriggerPx tpTriggerPxType tpOrdPx slTriggerPx slTriggerPxType slOrdPx feeCcy fee rebateCcy rebate source category reduceOnly isTpLimit cancelSource cancelSourceReason quickMgnType algoClOrdId algoId uTime cTime tradeQuoteCcy outcome"),
        ["/api/v5/trade/fills"]=new(typeof(OkxExecutionFillDto),"instType instId tradeId ordId clOrdId billId subType tag fillPx fillSz fillIdxPx fillPnl fillPxVol fillPxUsd fillMarkVol fillFwdPx fillMarkPx side posSide execType feeCcy fee ts fillTime feeRate tradeQuoteCcy"),
        ["/api/v5/trade/fills-history"]=new(typeof(OkxExecutionFillDto),"instType instId tradeId ordId clOrdId billId subType tag fillPx fillSz fillIdxPx fillPnl fillPxVol fillPxUsd fillMarkVol fillFwdPx fillMarkPx side posSide execType feeCcy fee ts fillTime feeRate tradeQuoteCcy"),
        ["/api/v5/account/bills"]=new(typeof(OkxExecutionBillDto),"instType billId type subType ts balChg posBalChg bal posBal sz px ccy pnl fee earnAmt earnApr mgnMode instId ordId execType from to notes interest tag fillTime tradeId clOrdId fillIdxPx fillMarkPx fillPxVol fillPxUsd fillMarkVol fillFwdPx"),
        ["/api/v5/account/bills-archive"]=new(typeof(OkxExecutionBillDto),"instType billId type subType ts balChg posBalChg bal posBal sz px ccy pnl fee earnAmt earnApr mgnMode instId ordId execType from to notes interest tag fillTime tradeId clOrdId fillIdxPx fillMarkPx fillPxVol fillPxUsd fillMarkVol fillFwdPx"),
    };
    private static object Recovery(JsonElement r,RecoverySchema schema,Action<string?,string,int>? observer=null,bool ownedRecovery=false)
    {
        if(schema.Type==typeof(OkxExecutionOrderDto))
        {
            if(ownedRecovery)OwnedRecoveryMetadata(r,false);
            ObservedShape(r,schema.Strings+(ownedRecovery?" slippage":""),arrays:"attachAlgoOrds",objects:"linkedAlgoOrd",observer:observer,exactCashSpotEmptySlippage:schema.ExactCashSpotEmptySlippage&&!ownedRecovery);
            foreach(var p in r.EnumerateObject())
                if(p.Name=="linkedAlgoOrd")At(".linkedAlgoOrd",()=>{ObservedShape(p.Value,"algoId",observer:observer);return true;});
                else if(p.Name=="attachAlgoOrds")
                {
                    At(".attachAlgoOrds",()=>{ExactCount(p.Value,0,32,"AtMost32");return true;});
                    foreach(var a in p.Value.EnumerateArray())At(".attachAlgoOrds[]",()=>{ObservedShape(a,OrderAttachmentStrings+(ownedRecovery?" percent":""),observer:observer);if(ownedRecovery)OwnedRecoveryAttachmentMetadata(a);return true;});
                }
            Identity(r,"instType","NonemptyString",v=>v.Length>0);Identity(r,"instId","Instrument",Grammar.Instrument);Identity(r,"ordId","OrderId",Grammar.OrderId);
            _=S(r,"clOrdId");return OrderRow(r);
        }
        Shape(r,schema.Strings);
        if(!Grammar.OrderId(S(r,"billId")))throw new JsonException();
        if(schema.Type==typeof(OkxExecutionFillDto))
        {
            if(S(r,"instType").Length==0||!Grammar.Instrument(S(r,"instId")))throw new JsonException();
            _=S(r,"ordId");_=S(r,"tradeId");_=S(r,"clOrdId");return FillRow(r);
        }
        if(S(r,"ccy").Length==0)throw new JsonException();_=S(r,"type");_=S(r,"subType");return BillRow(r);
    }
    private static OkxTimeDto Time(JsonElement r) { Shape(r,TimeStrings);return new(Timestamp(r,"ts",true)); }
    private static OkxExecutionAccountDto Account(JsonElement r)
    {
        // R:2849-2934. settleCcyList array is the explicit parent compatibility ruling (R:2838).
        Shape(r,AccountStrings,AccountBools,AccountArrays);
        foreach(var n in new[]{"traderInsts","spotTraderInsts","settleCcyList"})if(r.TryGetProperty(n,out var a))At("."+n,()=>StringArray(a));
        return new(S(r,"uid"),S(r,"mainUid"),S(r,"acctLv"),S(r,"perm"),S(r,"ip"),S(r,"feeType"),B(r,"autoLoan"),B(r,"enableSpotBorrow"),B(r,"spotBorrowAutoRepay"),S(r,"type"),S(r,"spotRoleType"),S(r,"kycLv"));
    }
    private static OkxExecutionBalanceDto Balance(JsonElement r,Action<string?,string,int>? privateSchemaObserver)
    {
        // R:1418-1554. USD aggregate fields are discarded, never quote cash.
        Shape(r,BalanceStrings,arrays:"details");
        var details=Required(r,"details","Array");if(details.GetArrayLength()>256)throw new CardinalityException(new("CARDINALITY",".details","AtMost256","OverLimit"),"PARSE_VALUES");
        return new(Timestamp(r,"uTime",true),details.EnumerateArray().Select(d=>At(".details[]",()=>
        {
            try{Shape(d,BalanceDetailStrings,"collateralRestrict collateralEnabled");}
            catch(PayloadException e) when(e.Failure.Reason=="UNSUPPORTED_PROPERTY")
            {
                if(privateSchemaObserver is not null)NotifyPrivateSchema(d,BalanceDetailStrings+" collateralRestrict collateralEnabled",privateSchemaObserver);
                throw new PayloadException(e.Phase,e.Failure with {UnsupportedProperties=BalanceUnsupportedProperties(d)},e.OkxCode);
            }
            // Official OKX toolkit e5b60e4f: optional REST details extensions.
            // Invested earn amount is NOT matched amount or cash. Keep active
            // lending/staking unsupported before any financial projection.
            if(Number(d,"autoLendAmt").Value is {} earnAmount && earnAmount!=0)
                throw Failure("PARSE_VALUES","AUTO_EARN_UNSUPPORTED",".autoLendAmt","ZeroOrUnavailable","String");
            if(Optional(d,"autoStakingStatus") is {} staking)
            {
                if(staking is "pending" or "active")throw Failure("PARSE_VALUES","AUTO_EARN_UNSUPPORTED",".autoStakingStatus","InactiveAutoEarn","String");
                if(staking is not ("unsupported" or "off"))throw Failure("PARSE_VALUES","AUTO_EARN_STATUS",".autoStakingStatus","InactiveAutoEarn","String");
            }
            return new OkxExecutionBalanceDetailDto(S(d,"ccy"),Number(d,"cashBal"),Number(d,"availBal"),Number(d,"frozenBal"),Number(d,"ordFrozen"),Number(d,"eq"),Number(d,"liab"),Timestamp(d,"uTime"),Number(d,"interest"),Number(d,"crossLiab"),Number(d,"isoLiab"),Number(d,"stgyEq"),Number(d,"fixedBal"));
        })).ToImmutableArray());
    }
    private static readonly ImmutableDictionary<string,string> MaxUnits=new Dictionary<string,string>
    {
        ["maxLmtSz"]="base currency",["maxMktSz"]="USDT",["maxLmtAmt"]="USD",["maxMktAmt"]="USD",
        ["maxTwapSz"]="base currency",["maxIcebergSz"]="base currency",["maxTriggerSz"]="base currency",["maxStopSz"]="USDT",
        ["maxPlatOILmt"]="USD (derivatives only)",["maxPlatOICoinLmt"]="coins (derivatives only)"
    }.ToImmutableDictionary(StringComparer.Ordinal);
    private static OkxExecutionInstrumentDto Instrument(JsonElement r,Action<string?,string,int>? privateSchemaObserver)
    {
        // R:1109-1274. Quote list is private eligibility, not catalog quote equivalence.
        try{Shape(r,InstrumentStrings,"futureSettlement","tradeQuoteCcyList upcChg","instIdCode");}
        catch(PayloadException e) when(e.Failure.Reason=="UNSUPPORTED_PROPERTY")
        {
            if(privateSchemaObserver is not null)NotifyPrivateSchema(r,InstrumentStrings+" futureSettlement tradeQuoteCcyList upcChg instIdCode",privateSchemaObserver);
            throw;
        }
        // Parent-approved LOCAL empty-only wire policy, not an asserted provider
        // account/instruments contract. These exact public vocabulary extensions
        // never supply financial state, units or authority. No nonempty value is supported.
        foreach(var name in new[]{"freq","method"})
            if(r.TryGetProperty(name,out var extension)&&(S(r,"instType")!="SPOT"||extension.GetString()!=""))
                throw Failure("PARSE_VALUES","INSTRUMENT_EXTENSION_UNSUPPORTED","."+name,"EmptyStringOnSpot","String");
        if(r.TryGetProperty("upcChg",out var changes))foreach(var c in changes.EnumerateArray())At(".upcChg[]",()=>{Shape(c,"param newValue effTime");return true;});
        return new(S(r,"instId"),S(r,"instType"),S(r,"baseCcy"),S(r,"quoteCcy"),ReadStringArray(r,"tradeQuoteCcyList"),Optional(r,"groupId"),S(r,"state"),Number(r,"tickSz"),Number(r,"lotSz"),Number(r,"minSz"),Timestamp(r,"listTime"),Timestamp(r,"expTime"),MaxUnits.ToImmutableDictionary(x=>x.Key,x=>new OkxObservationMaximum(Number(r,x.Key),x.Value),StringComparer.Ordinal));
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
    private static OkxExecutionOrderDto OrderRow(JsonElement r)=>new(S(r,"instType"),
        S(r,"instId"),
        S(r,"ordId"),
        S(r,"clOrdId"),
        Number(r,"px"),
        Number(r,"sz"),
        Number(r,"pnl"),
        Number(r,"accFillSz"),
        Number(r,"fillPx"),
        Number(r,"fillSz"),
        Number(r,"avgPx"),
        Number(r,"tpTriggerPx"),
        Number(r,"tpOrdPx"),
        Number(r,"slTriggerPx"),
        Number(r,"slOrdPx"),
        Number(r,"fee"),
        Number(r,"rebate"),
        Timestamp(r,"fillTime"),
        Timestamp(r,"uTime"),
        Timestamp(r,"cTime"),
        Optional(r,"tgtCcy"),
        Optional(r,"ccy"),
        Optional(r,"tag"),
        Optional(r,"ordType"),
        Optional(r,"side"),
        Optional(r,"posSide"),
        Optional(r,"tdMode"),
        Optional(r,"tradeId"),
        Optional(r,"state"),
        Optional(r,"stpMode"),
        Optional(r,"attachAlgoClOrdId"),
        Optional(r,"tpTriggerPxType"),
        Optional(r,"slTriggerPxType"),
        Optional(r,"feeCcy"),
        Optional(r,"rebateCcy"),
        Optional(r,"source"),
        Optional(r,"category"),
        Optional(r,"reduceOnly"),
        Optional(r,"isTpLimit"),
        Optional(r,"cancelSource"),
        Optional(r,"algoClOrdId"),
        Optional(r,"algoId"),
        Optional(r,"tradeQuoteCcy"),
        Optional(r,"outcome"),
        r.TryGetProperty("attachAlgoOrds",out var aa)?aa.EnumerateArray().Select(a=>At(".attachAlgoOrds[]",()=>AttachmentRow(a))).ToImmutableArray():null,
        r.TryGetProperty("linkedAlgoOrd",out var ll)?LinkedAlgoRow(ll):null,
        PayloadDigest(r)) { ConsistencyExtras=OrderExtras(r,"pxUsd pxVol pxType stpId lever cancelSourceReason quickMgnType slippage") };
    private static ImmutableDictionary<string,string> OrderExtras(JsonElement row,string names)=>names.Split(' ')
        .Where(n=>row.TryGetProperty(n,out _)).ToImmutableDictionary(n=>n,n=>S(row,n),StringComparer.Ordinal);
    private static OkxExecutionAttachmentDto AttachmentRow(JsonElement r)=>new(Optional(r,"attachAlgoId"),
        Optional(r,"attachAlgoClOrdId"),
        Optional(r,"tpOrdKind"),
        Optional(r,"tpTriggerPxType"),
        Optional(r,"slTriggerPxType"),
        Optional(r,"amendPxOnTriggerType"),
        Optional(r,"failCode"),
        Number(r,"tpTriggerPx"),
        Number(r,"tpTriggerRatio"),
        Number(r,"tpOrdPx"),
        Number(r,"slTriggerPx"),
        Number(r,"slTriggerRatio"),
        Number(r,"slOrdPx"),
        Number(r,"sz"),
        Number(r,"callbackRatio"),
        Number(r,"callbackSpread"),
        Number(r,"activePx"),
        PayloadDigest(r)) { ConsistencyExtras=OrderExtras(r,"percent failReason") };
    private static OkxExecutionLinkedAlgoDto LinkedAlgoRow(JsonElement r)=>new(Optional(r,"algoId"));
    private static OkxExecutionFillDto FillRow(JsonElement r)=>new(S(r,"instType"),
        S(r,"instId"),
        S(r,"billId"),
        S(r,"tradeId"),
        S(r,"ordId"),
        S(r,"clOrdId"),
        Number(r,"fillPx"),
        Number(r,"fillSz"),
        Number(r,"fillPnl"),
        Number(r,"fee"),
        Number(r,"feeRate"),
        Timestamp(r,"ts"),
        Timestamp(r,"fillTime"),
        Optional(r,"subType"),
        Optional(r,"tag"),
        Optional(r,"side"),
        Optional(r,"posSide"),
        Optional(r,"execType"),
        Optional(r,"feeCcy"),
        Optional(r,"tradeQuoteCcy"),
        PayloadDigest(r));
    private static OkxExecutionBillDto BillRow(JsonElement r)=>new(S(r,"billId"),
        S(r,"ccy"),
        S(r,"type"),
        S(r,"subType"),
        Number(r,"balChg"),
        Number(r,"posBalChg"),
        Number(r,"bal"),
        Number(r,"posBal"),
        Number(r,"sz"),
        Number(r,"px"),
        Number(r,"pnl"),
        Number(r,"fee"),
        Number(r,"earnAmt"),
        Number(r,"earnApr"),
        Number(r,"interest"),
        Timestamp(r,"ts"),
        Timestamp(r,"fillTime"),
        Optional(r,"instType"),
        Optional(r,"mgnMode"),
        Optional(r,"instId"),
        Optional(r,"ordId"),
        Optional(r,"execType"),
        Optional(r,"from"),
        Optional(r,"to"),
        Optional(r,"tag"),
        Optional(r,"tradeId"),
        Optional(r,"clOrdId"),
        PayloadDigest(r));
    private static int CanonicalBound(JsonElement e)
    {
        checked
        {
            if(e.ValueKind==JsonValueKind.String)return 2+6*e.GetString()!.Length;
            int size=2;
            if(e.ValueKind==JsonValueKind.Object)foreach(var p in e.EnumerateObject())size+=4+6*p.Name.Length+CanonicalBound(p.Value);
            else if(e.ValueKind==JsonValueKind.Array)foreach(var v in e.EnumerateArray())size+=1+CanonicalBound(v);
            else throw new JsonException();
            return size;
        }
    }
    internal static string PayloadDigest(JsonElement row)
    {
        // One bounded allocation BEFORE private writes. Neither a stream writer nor its
        // pooled escaping helpers may own growing/abandoned plaintext arrays here.
        // Six bytes per UTF-16 code unit covers JSON escaping as well as raw UTF-8.
        byte[] buffer=new byte[CanonicalBound(row)];
        int position=0;
        try
        {
            Write(row);
            Span<byte> hash=stackalloc byte[32];
            try
            {
                System.Security.Cryptography.SHA256.HashData(buffer.AsSpan(0,position),hash);
                return Convert.ToHexString(hash).ToLowerInvariant();
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(hash); }
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer); }
        void Byte(byte value)=>buffer[position++]=value;
        void Hex(char value)
        {
            const string digits="0123456789ABCDEF";
            Byte((byte)'\\');Byte((byte)'u');
            Byte((byte)digits[(value>>12)&15]);Byte((byte)digits[(value>>8)&15]);
            Byte((byte)digits[(value>>4)&15]);Byte((byte)digits[value&15]);
        }
        void String(string value)
        {
            Byte((byte)'"');
            for(int i=0;i<value.Length;i++)
            {
                char c=value[i];int scalar=c;
                if(char.IsHighSurrogate(c))
                {
                    if(i+1==value.Length||!char.IsLowSurrogate(value[i+1]))throw new JsonException();
                    scalar=char.ConvertToUtf32(c,value[++i]);
                }
                else if(char.IsLowSurrogate(c))throw new JsonException();
                // Query the same BCL allowlist, but do not call its escaping routines:
                // large-string writer escaping can rent additional uncleared arrays.
                if(System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping.WillEncode(scalar))
                {
                    byte escape=c switch {'"'=>(byte)'"','\\'=>(byte)'\\','\b'=>(byte)'b','\t'=>(byte)'t','\n'=>(byte)'n','\f'=>(byte)'f','\r'=>(byte)'r',_=>0};
                    if(escape!=0){Byte((byte)'\\');Byte(escape);}
                    else {Hex(c);if(scalar>0xffff)Hex(value[i]);}
                }
                else if(scalar<=0x7f)Byte((byte)scalar);
                else if(scalar<=0x7ff){Byte((byte)(0xc0|(scalar>>6)));Byte((byte)(0x80|(scalar&63)));}
                else if(scalar<=0xffff){Byte((byte)(0xe0|(scalar>>12)));Byte((byte)(0x80|((scalar>>6)&63)));Byte((byte)(0x80|(scalar&63)));}
                else {Byte((byte)(0xf0|(scalar>>18)));Byte((byte)(0x80|((scalar>>12)&63)));Byte((byte)(0x80|((scalar>>6)&63)));Byte((byte)(0x80|(scalar&63)));}
            }
            Byte((byte)'"');
        }
        void Write(JsonElement e)
        {
            if(e.ValueKind==JsonValueKind.Object)
            {
                Byte((byte)'{');bool comma=false;
                foreach(var p in e.EnumerateObject().OrderBy(x=>x.Name,StringComparer.Ordinal))
                {if(comma)Byte((byte)',');comma=true;String(p.Name);Byte((byte)':');Write(p.Value);}
                Byte((byte)'}');
            }
            else if(e.ValueKind==JsonValueKind.Array)
            {
                Byte((byte)'[');bool comma=false;
                foreach(var v in e.EnumerateArray()){if(comma)Byte((byte)',');comma=true;Write(v);}
                Byte((byte)']');
            }
            else if(e.ValueKind==JsonValueKind.String)String(e.GetString()!);else throw new JsonException();
        }
    }
    internal static string S(JsonElement e,string n)
    {
        var p=Required(e,n,"String");
        if(p.ValueKind!=JsonValueKind.String)throw Failure("PARSE_VALUES","TYPE","."+n,"String",Kind(p));
        return p.GetString()!;
    }
    internal static string? Optional(JsonElement e,string n)=>e.TryGetProperty(n,out var p)?p.GetString():null;
    private static bool B(JsonElement e,string n)=>Required(e,n,"Boolean").GetBoolean();
    internal static ImmutableArray<string> StringArray(JsonElement a)
    { if(a.ValueKind!=JsonValueKind.Array)throw Failure("PARSE_SCHEMA","TYPE","","Array",Kind(a));return a.EnumerateArray().Select(v=>v.ValueKind==JsonValueKind.String?v.GetString()!:throw Failure("PARSE_SCHEMA","TYPE","[]","String",Kind(v))).ToImmutableArray(); }
    internal static void Shape(JsonElement e,string strings,string bools="",string arrays="",string nullableInts="")
    {
        if(e.ValueKind!=JsonValueKind.Object)throw Failure("PARSE_SCHEMA","TYPE","","Object",Kind(e));
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
            if(!valid)
            {
                string? known=ss.Concat(bs).Concat(aa).Concat(ii).FirstOrDefault(n=>n.Equals(p.Name,StringComparison.OrdinalIgnoreCase));
                if(known==null)throw Failure("PARSE_SCHEMA","UNSUPPORTED_PROPERTY",".<unknown>","KnownProperty",Kind(p.Value));
                if(known!=p.Name)throw Failure("PARSE_SCHEMA","PROPERTY_CASE","."+known,"ExactProperty",Kind(p.Value));
                throw Failure("PARSE_SCHEMA","TYPE","."+known,ss.Contains(known)?"String":bs.Contains(known)?"Boolean":aa.Contains(known)?"Array":"IntegerOrNull",Kind(p.Value));
            }
        }
    }
    internal static OkxDecimal Number(JsonElement e,string n)
    {
        var raw=Optional(e,n);if(string.IsNullOrEmpty(raw))return new(raw,null);
        if(!Grammar.Match(raw,"-?(?:0|[1-9][0-9]*)(?:\\.[0-9]+)?") || raw.Length>128 || !decimal.TryParse(raw,NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var value))throw Failure("PARSE_VALUES","NUMBER","."+n,"ExactDecimal","String");
        // decimal.TryParse can silently round/underflow. Compare exact rational coefficients.
        var fraction=raw.Contains('.')?raw.Length-raw.IndexOf('.')-1:0;
        var coeff=BigInteger.Parse(raw.Replace(".",""),CultureInfo.InvariantCulture);
        var bits=decimal.GetBits(value);var actual=(BigInteger)(uint)bits[0]+((BigInteger)(uint)bits[1]<<32)+((BigInteger)(uint)bits[2]<<64);
        if(value<0)actual=-actual;int scale=(bits[3]>>16)&255;
        if(coeff*BigInteger.Pow(10,scale)!=actual*BigInteger.Pow(10,fraction))throw Failure("PARSE_VALUES","NUMBER_PRECISION","."+n,"ExactDecimal","String");
        return new(raw,value);
    }
    internal static OkxTimestamp Timestamp(JsonElement e,string n,bool required=false)
    {
        var raw=required?S(e,n):Optional(e,n);if(string.IsNullOrEmpty(raw))return new(raw,null);
        if(!Grammar.Match(raw,"[0-9]{1,17}") || !long.TryParse(raw,NumberStyles.None,CultureInfo.InvariantCulture,out var ms))throw Failure("PARSE_VALUES","TIMESTAMP","."+n,"UnixMilliseconds","String");
        try{return new(raw,DateTimeOffset.FromUnixTimeMilliseconds(ms));}catch(ArgumentOutOfRangeException){throw Failure("PARSE_VALUES","TIMESTAMP","."+n,"UnixMilliseconds","String");}
    }

    // R:11989–12125 / 12264–12423 / 12570–12731: distinct endpoint registrations.
    private static readonly Dictionary<string,string> AlgoSchemas=new(StringComparer.Ordinal)
    {
        ["/api/v5/trade/order-algo"]="instType instId ccy ordId algoId clOrdId sz closeFraction ordType side posSide tdMode tgtCcy state lever tpTriggerPx tpTriggerPxType tpOrdPx slTriggerPx slTriggerPxType slOrdPx triggerPx triggerPxType ordPx advanceOrdType actualSz actualPx tag actualSide triggerTime pxVar pxSpread szLimit pxLimit timeInterval callbackRatio callbackSpread activePx moveTriggerPx reduceOnly quickMgnType last failCode algoClOrdId amendPxOnTriggerType cTime uTime isTradeBorrowMode chaseType chaseVal maxChaseType maxChaseVal tradeQuoteCcy",
        ["/api/v5/trade/orders-algo-pending"]="instType instId ccy ordId algoId clOrdId sz closeFraction ordType side posSide tdMode tgtCcy state lever tpTriggerPx tpTriggerPxType tpOrdPx slTriggerPx slTriggerPxType slOrdPx triggerPx triggerPxType ordPx advanceOrdType actualSz actualPx tag actualSide triggerTime pxVar pxSpread szLimit pxLimit timeInterval callbackRatio callbackSpread activePx moveTriggerPx reduceOnly quickMgnType last failCode algoClOrdId amendPxOnTriggerType cTime uTime isTradeBorrowMode chaseType chaseVal maxChaseType maxChaseVal tradeQuoteCcy lmtOrderNumber aggressiveness",
        ["/api/v5/trade/orders-algo-history"]="instType instId ccy ordId algoId clOrdId sz closeFraction ordType side posSide tdMode tgtCcy state lever tpTriggerPx tpTriggerPxType tpOrdPx slTriggerPx slTriggerPxType slOrdPx triggerPx triggerPxType ordPx advanceOrdType actualSz actualPx tag actualSide triggerTime pxVar pxSpread szLimit pxLimit timeInterval callbackRatio callbackSpread activePx moveTriggerPx reduceOnly quickMgnType last failCode algoClOrdId amendPxOnTriggerType cTime uTime isTradeBorrowMode chaseType chaseVal maxChaseType maxChaseVal tradeQuoteCcy lmtOrderNumber aggressiveness",
    };
    private static OkxExecutionAlgoDto ParseAlgo(JsonElement r,string strings,bool advanced,Action<string?,string,int>? observer=null,bool ownedRecovery=false)
    {
        if(ownedRecovery)OwnedRecoveryMetadata(r,true);
        ValidateAlgoShape(r,strings+(ownedRecovery?" aggressiveness lmtOrderNumber":""),advanced,observer,exactCashSpotEmptySlippage:!advanced,ownedRecovery:ownedRecovery);
        return AlgoRow(r);
    }
    internal static void ValidateAlgoShape(JsonElement r,string strings,bool advanced,Action<string?,string,int>? observer=null)
        =>ValidateAlgoShape(r,strings,advanced,observer,exactCashSpotEmptySlippage:false);
    private static void ValidateAlgoShape(JsonElement r,string strings,bool advanced,Action<string?,string,int>? observer,bool exactCashSpotEmptySlippage,bool ownedRecovery=false)
    {
        ObservedShape(r,strings,arrays:"ordIdList subAlgoIdList advChaseParams attachAlgoOrds"+(advanced||ownedRecovery?" triggerParams":""),objects:"linkedOrd",observer:observer,exactCashSpotEmptySlippage:exactCashSpotEmptySlippage);
        foreach(var p in r.EnumerateObject())
        {
            if(p.Name is "ordIdList" or "subAlgoIdList")
            {At("."+p.Name,()=>{ExactCount(p.Value,0,256,"AtMost256");return StringArray(p.Value);});}
            else if(p.Name=="linkedOrd")At(".linkedOrd",()=>{ObservedShape(p.Value,"ordId",observer:observer);return true;});
            else if(p.Name is "advChaseParams" or "attachAlgoOrds"||advanced&&p.Name=="triggerParams")
            {
                At("."+p.Name,()=>{ExactCount(p.Value,0,32,"AtMost32");return true;});
                foreach(var a in p.Value.EnumerateArray())At("."+p.Name+"[]",()=>{ObservedShape(a,p.Name switch {"advChaseParams"=>AlgoChaseStrings,"attachAlgoOrds"=>AlgoAttachmentStrings,_=>AlgoTriggerStrings},observer:observer);return true;});
            }
        }
        Identity(r,"instType","NonemptyString",v=>v.Length>0);Identity(r,"instId","NonemptyStringAtMost128",v=>v.Length is >0 and <=128);Identity(r,"algoId","OrderId",Grammar.OrderId);
    }
    internal static OkxExecutionAlgoDto AlgoRow(JsonElement r)=>new(S(r,"instType"),
        S(r,"instId"),
        S(r,"algoId"),
        Optional(r,"algoClOrdId"),
        Optional(r,"clOrdId"),
        Optional(r,"ordId"),
        Optional(r,"state"),
        Optional(r,"ordType"),
        Optional(r,"side"),
        Optional(r,"posSide"),
        Optional(r,"tdMode"),
        Optional(r,"tgtCcy"),
        Optional(r,"ccy"),
        Optional(r,"tag"),
        Optional(r,"actualSide"),
        Optional(r,"tpTriggerPxType"),
        Optional(r,"slTriggerPxType"),
        Optional(r,"triggerPxType"),
        Optional(r,"failCode"),
        Optional(r,"reduceOnly"),
        Optional(r,"isTradeBorrowMode"),
        Optional(r,"amendPxOnTriggerType"),
        Optional(r,"tradeQuoteCcy"),
        Optional(r,"advanceOrdType"),
        Optional(r,"chaseType"),
        Optional(r,"maxChaseType"),
        Optional(r,"quickMgnType"),
        Number(r,"sz"),
        Number(r,"closeFraction"),
        Number(r,"actualSz"),
        Number(r,"actualPx"),
        Number(r,"tpTriggerPx"),
        Number(r,"tpOrdPx"),
        Number(r,"slTriggerPx"),
        Number(r,"slOrdPx"),
        Number(r,"triggerPx"),
        Number(r,"ordPx"),
        Number(r,"last"),
        Number(r,"callbackRatio"),
        Number(r,"callbackSpread"),
        Number(r,"activePx"),
        Number(r,"moveTriggerPx"),
        Number(r,"chaseVal"),
        Number(r,"maxChaseVal"),
        Timestamp(r,"cTime"),
        Timestamp(r,"uTime"),
        Timestamp(r,"triggerTime"),
        r.TryGetProperty("ordIdList",out var ordIdList)?StringArray(ordIdList):null,
        r.TryGetProperty("subAlgoIdList",out var subAlgoIdList)?StringArray(subAlgoIdList):null,
        r.TryGetProperty("attachAlgoOrds",out var attachments)?attachments.EnumerateArray().Select(a=>At(".attachAlgoOrds[]",()=>AlgoAttachmentRow(a))).ToImmutableArray():null,
        r.TryGetProperty("advChaseParams",out var chase)?chase.EnumerateArray().Select(a=>At(".advChaseParams[]",()=>AlgoChaseRow(a))).ToImmutableArray():null,
        r.TryGetProperty("linkedOrd",out var linked)?new OkxAlgoLinkedOrderDto(Optional(linked,"ordId"),PayloadDigest(linked)):null,
        PayloadDigest(r));
    private static OkxAlgoAttachmentDto AlgoAttachmentRow(JsonElement r)=>new(Optional(r,"attachAlgoClOrdId"),
        Optional(r,"tpTriggerPxType"),
        Optional(r,"slTriggerPxType"),
        Number(r,"tpTriggerPx"),
        Number(r,"tpTriggerRatio"),
        Number(r,"tpOrdPx"),
        Number(r,"slTriggerPx"),
        Number(r,"slTriggerRatio"),
        Number(r,"slOrdPx"),
        Number(r,"callbackRatio"),
        Number(r,"callbackSpread"),
        Number(r,"activePx"),
        PayloadDigest(r));
    private static OkxAlgoChaseDto AlgoChaseRow(JsonElement r)=>new(Optional(r,"chaseType"),
        Optional(r,"maxChaseType"),
        Number(r,"chaseVal"),
        Number(r,"maxChaseVal"),
        PayloadDigest(r));
}
