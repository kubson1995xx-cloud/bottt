using System.Collections.Immutable;
using System.Text.Json;
namespace Apex.Adapters.Okx.Execution;

// These are diagnostic facts, not response text or authority. Paths contain only
// parser-owned schema identifiers and [] (never account values or array indices).
public sealed record OkxEnrollmentUnsupportedProperty(string Name,string DocumentedType,string Actual,int Count);
public sealed record OkxEnrollmentPayloadFailure(string Reason,string Path,string Expected,string Actual)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public System.Collections.Immutable.ImmutableArray<OkxEnrollmentUnsupportedProperty>? UnsupportedProperties { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? ActualCount { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? MinimumCount { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? MaximumCount { get; init; }
}
internal static partial class OkxObservationJson
{
    // The existing strict schemas are reused, not a second permissive parser.
    private const string TimeStrings="ts";
    private const string AccountStrings="uid mainUid acctLv acctStpMode posMode greeksType feeType level levelTmp ctIsoMode mgnIsoMode spotOffsetType stgyType roleType spotRoleType opAuth kycLv label ip perm liquidationGear type settleCcy";
    private const string AccountBools="autoLoan enableSpotBorrow spotBorrowAutoRepay";
    private const string AccountArrays="traderInsts spotTraderInsts settleCcyList";
    private const string BalanceStrings="uTime totalEq isoEq adjEq availEq ordFroz imr mmr borrowFroz mgnRatio notionalUsd notionalUsdForBorrow notionalUsdForSwap notionalUsdForFutures notionalUsdForOption upl delta deltaLever deltaNeutralStatus";
    private const string BalanceDetailStrings="ccy eq cashBal uTime isoEq availEq disEq fixedBal availBal frozenBal ordFrozen liab upl uplLiab crossLiab rewardBal isoLiab mgnRatio imr mmr interest twap frpType maxLoan eqUsd borrowFroz notionalLever stgyEq isoUpl spotInUseAmt clSpotInUseAmt maxSpotInUse spotIsoBal smtSyncEq spotCopyTradingEq spotBal openAvgPx accAvgPx spotUpl spotUplRatio totalPnl totalPnlRatio colRes colBorrAutoConversion autoLendStatus autoLendMtAmt autoLendAmt autoStakingStatus";
    private const string InstrumentStrings="instType seriesId instId uly groupId instFamily baseCcy quoteCcy settleCcy ctVal ctMult ctValCcy optType stk listTime auctionEndTime contTdSwTime preMktSwTime openType elp rpi expTime lever tickSz lotSz minSz ctType state ruleType posLmtAmt posLmtPct maxPlatOILmt maxPlatOICoinLmt longPosRemainingQuota shortPosRemainingQuota maxLmtSz maxMktSz maxLmtAmt maxMktAmt maxTwapSz maxIcebergSz maxTriggerSz maxStopSz instCategory initPxLmtPct floatPxLmtPct maxPxLmtPct freq method";
    // Diagnostic-only names from EEA account WS example/table R4871/5043 and
    // R4899/5051. They are NOT in the REST balance schema and stay refused.
    // Summarize just the first rejected details object, never values/indices or
    // arbitrary names. Three closed labels times six kinds bounds output at 18.
    private static System.Collections.Immutable.ImmutableArray<OkxEnrollmentUnsupportedProperty> BalanceUnsupportedProperties(JsonElement row)
    {
        var schema=(BalanceDetailStrings+" collateralRestrict collateralEnabled").Split(' ').ToHashSet(StringComparer.OrdinalIgnoreCase);
        return row.EnumerateObject().Where(p=>!schema.Contains(p.Name))
            .Select(p=>(Name:p.Name switch {"coinUsdPrice"=>"coinUsdPrice","maxSpotInUseAmt"=>"maxSpotInUseAmt",_=>"<unknown>"},Actual:Kind(p.Value)))
            .GroupBy(p=>p).OrderBy(g=>g.Key.Name,StringComparer.Ordinal).ThenBy(g=>g.Key.Actual,StringComparer.Ordinal)
            .Select(g=>new OkxEnrollmentUnsupportedProperty(g.Key.Name,g.Key.Name=="<unknown>"?"Undocumented":"String",g.Key.Actual,g.Count())).ToImmutableArray();
    }
    private static readonly string[] DiscoveryNames=(TimeStrings+" "+AccountStrings+" "+AccountBools+" "+AccountArrays+" "+BalanceStrings+" "+BalanceDetailStrings+" "+InstrumentStrings+" code msg data details collateralRestrict collateralEnabled futureSettlement tradeQuoteCcyList upcChg instIdCode param newValue effTime").Split(' ',StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
    internal sealed class PayloadException(string phase,OkxEnrollmentPayloadFailure failure,string? code=null):JsonException("InvalidObservationPayload")
    {
        internal string Phase { get; }=phase;
        internal OkxEnrollmentPayloadFailure Failure { get; }=failure;
        internal string? OkxCode { get; }=code;
        internal PayloadException Prefix(string path)=>new(Phase,Failure with {Path=path+Failure.Path},OkxCode);
        internal PayloadException WithCode(string? code)=>new(Phase,Failure,code);
    }
    private static PayloadException Failure(string phase,string reason,string path,string expected,string actual)=>new(phase,new(reason,path,expected,actual));
    private static string Kind(JsonElement e)=>e.ValueKind switch
    {
        JsonValueKind.Object=>"Object",JsonValueKind.Array=>"Array",JsonValueKind.String=>"String",JsonValueKind.Number=>"Number",
        JsonValueKind.True or JsonValueKind.False=>"Boolean",JsonValueKind.Null=>"Null",_=>"Missing"
    };
    private static T At<T>(string path,Func<T> action)
    {
        try{return action();}
        catch(PayloadException e){throw e.Prefix(path);}
        catch(CardinalityException e) when(e.Failure!=null){throw new CardinalityException(e.Failure with {Path=path+e.Failure.Path},e.Phase,e.OkxCode);}
    }
    private static JsonElement Required(JsonElement e,string name,string expected)
    {
        if(!e.TryGetProperty(name,out var p))throw Failure("PARSE_VALUES","MISSING","."+name,expected,"Missing");
        return p;
    }
    private static System.Collections.Immutable.ImmutableArray<string> ReadStringArray(JsonElement e,string name)
    {
        var p=Required(e,name,"Array");return At("."+name,()=>StringArray(p));
    }
    // Same recursive duplicate/case-insensitive uniqueness and deferred string
    // validation as Grammar.Unique. Never retain an unknown response property name.
    private static void UniqueDiscovery(JsonElement e,string path,string[]? knownNames=null)
    {
        knownNames??=DiscoveryNames;
        if(e.ValueKind==JsonValueKind.Object)
        {
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var p in e.EnumerateObject())
            {
                string name;
                try{name=p.Name;}
                catch(InvalidOperationException){throw Failure("PARSE_UNIQUE","INVALID_STRING",path+".<unknown>","UnicodeString","String");}
                string child=path+"."+(knownNames.FirstOrDefault(n=>n.Equals(name,StringComparison.OrdinalIgnoreCase))??"<unknown>");
                if(!seen.Add(name))throw Failure("PARSE_UNIQUE","DUPLICATE_PROPERTY",child,"UniqueProperty",Kind(p.Value));
                UniqueDiscovery(p.Value,child,knownNames);
            }
        }
        else if(e.ValueKind==JsonValueKind.Array)foreach(var v in e.EnumerateArray())UniqueDiscovery(v,path+"[]",knownNames);
        else if(e.ValueKind==JsonValueKind.String)
        {
            try{_=e.GetString();}
            catch(InvalidOperationException){throw Failure("PARSE_UNIQUE","INVALID_STRING",path,"UnicodeString","String");}
        }
    }
}
