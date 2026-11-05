using System.Text.Json;
namespace Apex.Adapters.Okx.Execution;

internal static partial class OkxObservationJson
{
    // Parent-selected local evidence sufficiency, NOT exact-endpoint contractual
    // support or an assertion of inactive execution configuration. Discard only
    // these bounded root metadata operands; canonical and wire digests are original.
    private static void OwnedRecoveryMetadata(JsonElement row,bool algo)
    {
        if(S(row,"instType")!="SPOT"||S(row,"tdMode")!="cash")throw Failure("PARSE_VALUES","OWNED_RECOVERY_CASH_SPOT_REQUIRED",".tdMode","CashSpot","String");
        foreach(string name in algo?new[]{"aggressiveness","lmtOrderNumber"}:new[]{"slippage"})
        if(row.TryGetProperty(name,out var value))
        {
            if(value.ValueKind!=JsonValueKind.String)throw Failure("PARSE_SCHEMA","TYPE","."+name,"String",Kind(value));
            string s=value.GetString()!;
            if(s.Length>128)throw Failure("PARSE_VALUES","OWNED_RECOVERY_OPAQUE_BOUND","."+name,"StringAtMost128","String");
            for(int n=0;n<s.Length;n++)if(char.IsSurrogate(s[n])&&(!char.IsHighSurrogate(s[n])||n+1==s.Length||!char.IsLowSurrogate(s[++n])))throw Failure("PARSE_VALUES","OWNED_RECOVERY_UNICODE","."+name,"WellFormedString","String");
        }
        if(algo&&row.TryGetProperty("triggerParams",out var triggers)&&(triggers.ValueKind!=JsonValueKind.Array||triggers.GetArrayLength()!=0))
            throw Failure("PARSE_VALUES","OWNED_RECOVERY_TRIGGER_SCHEDULE_UNSUPPORTED",".triggerParams","EmptyArray",Kind(triggers));
        if(algo)
        foreach(string name in new[]{"pxVar","pxSpread","szLimit","pxLimit","timeInterval","callbackRatio","callbackSpread","activePx","moveTriggerPx","chaseType","chaseVal","maxChaseType","maxChaseVal","advanceOrdType","triggerPx","triggerPxType","ordPx","closeFraction"})
        if(row.TryGetProperty(name,out var setting)&&(setting.ValueKind!=JsonValueKind.String||setting.GetString()!=""))
            throw Failure("PARSE_VALUES","OWNED_RECOVERY_ADVANCED_CONFIGURATION_UNSUPPORTED","."+name,"EmptyUnsupportedConfiguration",Kind(setting));
    }
    // Local retrospective policy only: provider vocabulary/type is corroborated,
    // not this response contract or financial meaning. Digests retain the raw row.
    private static void OwnedRecoveryAttachmentMetadata(JsonElement row)
    {
        if(!row.TryGetProperty("percent",out var value))return;
        string s=value.GetString()!; // Exact nested ObservedShape already requires String.
        if(s.Length>128)throw Failure("PARSE_VALUES","OWNED_RECOVERY_OPAQUE_BOUND",".percent","StringAtMost128","String");
        for(int n=0;n<s.Length;n++)if(char.IsSurrogate(s[n])&&(!char.IsHighSurrogate(s[n])||n+1==s.Length||!char.IsLowSurrogate(s[++n])))throw Failure("PARSE_VALUES","OWNED_RECOVERY_UNICODE",".percent","WellFormedString","String");
    }
    private const string OrderAttachmentStrings="attachAlgoId attachAlgoClOrdId tpOrdKind tpTriggerPx tpTriggerRatio tpTriggerPxType tpOrdPx slTriggerPx slTriggerRatio slTriggerPxType slOrdPx sz amendPxOnTriggerType callbackRatio callbackSpread activePx failCode failReason";
    private const string AlgoAttachmentStrings="attachAlgoClOrdId tpTriggerPx tpTriggerRatio tpTriggerPxType tpOrdPx slTriggerPx slTriggerRatio slTriggerPxType slOrdPx callbackRatio callbackSpread activePx";
    private const string AlgoChaseStrings="chaseType chaseVal maxChaseType maxChaseVal";
    private const string AlgoTriggerStrings="triggerAction triggerStrategy triggerPx triggerCond timeframe thold timePeriod";
    private static string[] ExactDiagnosticNames(string endpoint)=>(endpoint=="/api/v5/trade/order"?RecoverySchemas[endpoint].Strings+" "+OrderAttachmentStrings+" linkedAlgoOrd attachAlgoOrds algoId":AlgoSchemas[endpoint]+" "+AlgoAttachmentStrings+" "+AlgoChaseStrings+" "+AlgoTriggerStrings+" ordIdList subAlgoIdList linkedOrd ordId advChaseParams attachAlgoOrds triggerParams").Split(' ').Concat(new[]{"code","msg","data","slippage"}).Distinct(StringComparer.Ordinal).ToArray();
    private static void ObservedShape(JsonElement row,string strings,string arrays="",string objects="",Action<string?,string,int>? observer=null,bool exactCashSpotEmptySlippage=false)
    {
        // Same closed admission as Shape, with explicit nested object kinds. Only
        // after the original refusal may an opted-in friend inspect identifiers.
        // Parent-approved LOCAL ExactCashSpotEmptySlippageV1; not a provider
        // response contract or financial statement. Only exact REST root rows
        // opt in. Digest the original row, never a normalized/filtered copy.
        if(exactCashSpotEmptySlippage)strings+=" slippage";
        try
        {
            if(row.ValueKind!=JsonValueKind.Object)throw Failure("PARSE_SCHEMA","TYPE","","Object",Kind(row));
            var objectNames=objects.Split(' ',StringSplitOptions.RemoveEmptyEntries);
            foreach(var p in row.EnumerateObject())
            {
                if(exactCashSpotEmptySlippage&&p.Name=="slippage")
                {
                    if(p.Value.ValueKind!=JsonValueKind.String||p.Value.GetString()!.Length!=0||
                        !row.TryGetProperty("instType",out var type)||type.ValueKind!=JsonValueKind.String||type.GetString()!="SPOT"||
                        !row.TryGetProperty("tdMode",out var mode)||mode.ValueKind!=JsonValueKind.String||mode.GetString()!="cash")
                        throw Failure("PARSE_SCHEMA","EXACT_CASH_SPOT_EMPTY_SLIPPAGE_V1",".slippage","EmptyStringOnExactCashSpot",Kind(p.Value));
                }
                if(objectNames.Contains(p.Name,StringComparer.Ordinal))
                {if(p.Value.ValueKind!=JsonValueKind.Object)throw Failure("PARSE_SCHEMA","TYPE","."+p.Name,"Object",Kind(p.Value));}
                else
                {
                    // Validate one property against the whole exact schema so
                    // source-owned case/type paths remain unchanged and bounded.
                    string? known=(strings+" "+arrays+" "+objects).Split(' ').FirstOrDefault(n=>n.Equals(p.Name,StringComparison.OrdinalIgnoreCase));
                    if(known==null)throw Failure("PARSE_SCHEMA","UNSUPPORTED_PROPERTY",".<unknown>","KnownProperty",Kind(p.Value));
                    if(known!=p.Name)throw Failure("PARSE_SCHEMA","PROPERTY_CASE","."+known,"ExactProperty",Kind(p.Value));
                    bool array=arrays.Split(' ').Contains(known,StringComparer.Ordinal);
                    if(p.Value.ValueKind!=(array?JsonValueKind.Array:JsonValueKind.String))throw Failure("PARSE_SCHEMA","TYPE","."+known,array?"Array":"String",Kind(p.Value));
                }
            }
        }
        catch(PayloadException e) when(e.Failure.Reason=="UNSUPPORTED_PROPERTY")
        {if(observer!=null)NotifyPrivateSchema(row,strings+" "+arrays+" "+objects,observer);throw;}
    }
    private static void ExactCount(JsonElement array,int minimum,int maximum,string expected)
    {
        int count=array.GetArrayLength();
        if(count<minimum||count>maximum)throw new CardinalityException(new("CARDINALITY","",expected,count==0?"Empty":count>maximum&&maximum==1?"Multiple":"OverLimit"){ActualCount=count,MinimumCount=minimum,MaximumCount=maximum},"PARSE_SCHEMA");
    }
    private static void Identity(JsonElement row,string name,string expected,Func<string,bool> valid)
    {string value=S(row,name);if(!valid(value))throw Failure("PARSE_VALUES","IDENTIFIER","."+name,expected,"String");}
}
