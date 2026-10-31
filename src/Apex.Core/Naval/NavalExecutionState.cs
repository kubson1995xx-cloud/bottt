using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Linq;
using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Apex.Core.Engine;
using System.Text.Json.Serialization;

namespace Apex.Core.Naval;

// Persistence data only. These records cannot issue financial or broker authority.
[JsonConverter(typeof(NavalExecutionEnumConverter<NavalClientIdRole>))]
public enum NavalClientIdRole { Parent, ProtectionAlgo }
[JsonConverter(typeof(NavalExecutionEnumConverter<NavalExecutionOperation>))]
public enum NavalExecutionOperation { Submit, Cancel, PlaceProtection, CancelProtection }
[JsonConverter(typeof(NavalExecutionEnumConverter<NavalExecutionStage>))]
public enum NavalExecutionStage { Prepared, Dispatching, Accepted, SubmitUnknown, NotSent }
[JsonConverter(typeof(NavalExecutionEnumConverter<NavalDispatchDisposition>))]
public enum NavalDispatchDisposition { NotSent, Acknowledged, Unknown }

public sealed class NavalExecutionEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if(reader.TokenType!=JsonTokenType.String)throw new JsonException("Invalid execution enum");
        string? text=reader.GetString();
        if(!Enum.TryParse<T>(text,false,out var value)||!Enum.IsDefined(typeof(T),value)||value.ToString()!=text)
            throw new JsonException("Invalid execution enum");
        return value;
    }
    public override void Write(Utf8JsonWriter writer,T value,JsonSerializerOptions options)
    {
        if(!Enum.IsDefined(typeof(T),value))throw new JsonException("Invalid execution enum");
        writer.WriteStringValue(value.ToString());
    }
}

public sealed record NavalOfflineExecutionBinding(NavalAccountId Account, string TransportEnvironment,
    string Uid, string MainUid, string QuoteCurrency, string CredentialBindingId,
    string ScopeId, string ConfigDigest, string SafetyVersion);
public sealed record NavalReservedClientId(string ExecutionCommandId,string ReservationId,NavalClientIdRole Role,ulong Counter,string ClientId);
public sealed record NavalExecutionIdAllocation(string ExecutionCommandId,string ReservationId,
    IReadOnlyList<NavalClientIdRole> Roles,IReadOnlyList<NavalReservedClientId> Ids,string AllocationDigest);
public sealed record NavalExecutionOutcome(NavalDispatchDisposition Disposition,string OriginalClientId,string? OrderId,
    string Reason,string? Code,string? SCode,string? SubCode);
public sealed record NavalExecutionIntent
{
    public string IntentId { get; init; }="";
    public string ReservationId { get; init; }="";
    public string CommandId { get; init; }="";
    public string SignalId { get; init; }="";
    public NavalExecutionOperation Operation { get; init; }
    public NavalOfflineExecutionBinding Binding { get; init; }=null!;
    public string Method { get; init; }="";
    public string Path { get; init; }="";
    public string Instrument { get; init; }="";
    public string OriginalClientId { get; init; }="";
    public string? OrderId { get; init; }
    public IReadOnlyList<string> AllocationKeys { get; init; }=Array.Empty<string>();
    private readonly byte[] _body=Array.Empty<byte>();
    public byte[] Body { get => (byte[])_body.Clone(); init => _body=(byte[])value.Clone(); }
    public string RequestDigest { get; init; }="";
    public DateTimeOffset Deadline { get; init; }
    public long PreparedRevision { get; init; }
    public string ReservationDigest { get; init; }="";
    public string DefinitionDigest { get; init; }="";
    public string CatalogDigest { get; init; }="";
    public long SourceGeneration { get; init; }
    public long SourceSequence { get; init; }
    public string PreparationSessionId { get; init; }="";
    public ulong StopEpoch { get; init; }
    public NavalExecutionStage Stage { get; init; }
    public long? DispatchRevision { get; init; }
    public NavalExecutionOutcome? Outcome { get; init; }
}
public sealed record NavalExecutionState
{
    public string Schema { get; init; }="naval-execution-offline-v1";
    public NavalPortfolioId PortfolioId { get; init; }=null!;
    public string FinancialConfigDigest { get; init; }="";
    public long FinancialRevision { get; init; }
    public NavalOfflineExecutionBinding Binding { get; init; }=null!;
    public string Namespace { get; init; }="";
    public ulong HighWater { get; init; }
    public IReadOnlyList<NavalExecutionIdAllocation> IdAllocations { get; init; }=Array.Empty<NavalExecutionIdAllocation>();
    public IReadOnlyList<NavalExecutionIntent> Intents { get; init; }=Array.Empty<NavalExecutionIntent>();
    public ulong StopEpoch { get; init; }
    public string IntegrityDigest { get; init; }="";
}

internal static class NavalExecutionValidation
{
    // This parser is deliberately limited to this one optional schema, before deserialization defaults/coercion.
    internal static void RequireRaw(JsonElement root)
    {
        try
        {
            var roots=root.EnumerateObject().Where(p=>string.Equals(p.Name,"NavalExecution",StringComparison.OrdinalIgnoreCase)).ToArray();
            if(roots.Length==0)return;
            if(roots.Length!=1||roots[0].Name!="NavalExecution")throw Invalid();
            if(roots[0].Value.ValueKind==JsonValueKind.Null)return;
            RequireRecord(roots[0].Value,typeof(NavalExecutionState));
        }
        catch(Exception e) when(e is not OutOfMemoryException){throw Invalid();}
    }
    private static void RequireRecord(JsonElement e,Type type,string? field=null)
    {
        if(e.ValueKind==JsonValueKind.Null)
        {if(field is "OrderId" or "DispatchRevision" or "Outcome" or "Code" or "SCode" or "SubCode")return;throw Invalid();}
        type=Nullable.GetUnderlyingType(type)??type;
        if(type==typeof(string)) {if(e.ValueKind!=JsonValueKind.String)throw Invalid();_=e.GetString();return;}
        if(type==typeof(byte[]))
        {if(e.ValueKind!=JsonValueKind.String)throw Invalid();var text=e.GetString()!;if(text.Length>21848)throw Invalid();var bytes=Convert.FromBase64String(text);if(bytes.Length<1||bytes.Length>16384)throw Invalid();return;}
        if(type==typeof(ulong)){if(e.ValueKind!=JsonValueKind.Number||!e.TryGetUInt64(out _))throw Invalid();return;}
        if(type==typeof(long)){if(e.ValueKind!=JsonValueKind.Number||!e.TryGetInt64(out _))throw Invalid();return;}
        if(type==typeof(DateTimeOffset)){if(e.ValueKind!=JsonValueKind.String||!e.TryGetDateTimeOffset(out _))throw Invalid();return;}
        if(type==typeof(NavalEnvironment))
        {if(e.ValueKind!=JsonValueKind.Number||!e.TryGetInt32(out var value)||value!=(int)NavalEnvironment.Offline)throw Invalid();return;}
        if(type.IsEnum)
        {if(e.ValueKind!=JsonValueKind.String||!Enum.GetNames(type).Contains(e.GetString(),StringComparer.Ordinal))throw Invalid();return;}
        if(type.IsGenericType&&type.GetGenericTypeDefinition()==typeof(IReadOnlyList<>))
        {if(e.ValueKind!=JsonValueKind.Array)throw Invalid();foreach(var item in e.EnumerateArray())RequireRecord(item,type.GetGenericArguments()[0]);return;}
        if(type!=typeof(NavalExecutionState)&&type!=typeof(NavalOfflineExecutionBinding)&&type!=typeof(NavalPortfolioId)&&type!=typeof(NavalAccountId)&&
            type!=typeof(NavalExecutionIdAllocation)&&type!=typeof(NavalReservedClientId)&&type!=typeof(NavalExecutionIntent)&&type!=typeof(NavalExecutionOutcome))throw Invalid();
        if(e.ValueKind!=JsonValueKind.Object)throw Invalid();var properties=type.GetProperties();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var p in e.EnumerateObject())if(!seen.Add(p.Name)||!properties.Any(x=>x.Name==p.Name))throw Invalid();
        foreach(var p in properties)
        {if(!e.TryGetProperty(p.Name,out var value))throw Invalid();RequireRecord(value,p.PropertyType,p.Name);}
    }
    internal static InvalidDataException Invalid()=>new("Incompatible execution state; preserve files and upgrade with a matching writer (downgrade unsupported)");
    internal static bool Match(string? value,string pattern)=>value!=null&&Regex.IsMatch(value,"\\A(?:"+pattern+")\\z",RegexOptions.CultureInvariant);
    internal static string AllocationDigest(string reservationId,string executionCommandId,IReadOnlyList<NavalClientIdRole> roles)
        =>NavalJson.Hash(new{reservationId,executionCommandId,roles});
    // Same minimal checked arithmetic used by real allocation. A full 2^64 history fixture is neither valid nor necessary.
    internal static ulong CheckedAllocationEnd(ulong highWater,int count)
    {if(count<1||count>16)throw new ArgumentOutOfRangeException(nameof(count));return checked(highWater+(ulong)count);}
    internal static string ClientId(string ns,ulong counter)=>ns+counter.ToString("X16",CultureInfo.InvariantCulture);
    internal static NavalExecutionState Seal(NavalExecutionState s,long revision)
    {s=s with{FinancialRevision=revision,IntegrityDigest=""};return s with{IntegrityDigest=NavalJson.Hash(s)};}
    internal static void ValidateBinding(NavalOfflineExecutionBinding b,NavalPortfolioDefinition definition)
    {
        if(b==null||b.Account==null||b.Account.Environment!=NavalEnvironment.Offline||
            definition.Members.Any(m=>m.Account.Environment!=NavalEnvironment.Offline)||!definition.Members.Any(m=>m.Account==b.Account)||
            b.QuoteCurrency!=b.Account.QuoteCurrency||b.TransportEnvironment is not ("Demo" or "Live")||
            !Match(b.Uid,"[0-9]{1,32}")||b.MainUid!=b.Uid||!Match(b.QuoteCurrency,"[A-Z0-9]{2,12}")||
            !Match(b.CredentialBindingId,"[A-Za-z0-9_-]{1,64}")||!Match(b.ScopeId,"[A-Za-z0-9_-]{1,64}")||
            !Match(b.ConfigDigest,"[a-f0-9]{64}")||b.SafetyVersion!="okx-cash-v1")throw Invalid();
    }
    internal static string RequestDigest(NavalExecutionIntent i)
    {
        var b=i.Binding;
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[]{b.TransportEnvironment,b.Uid,b.MainUid,b.QuoteCurrency,
            b.CredentialBindingId,i.Method,i.Path,i.Instrument,i.OriginalClientId,i.OrderId??"",i.Deadline.ToString("O",CultureInfo.InvariantCulture),Convert.ToBase64String(i.Body)}))).ToLowerInvariant();
    }
    internal static void Validate(EngineState envelope,NavalPortfolioDefinition definition)
    {
        if(envelope.NavalExecution is not {} x)return;
        var s=envelope.Naval??throw Invalid();ValidateBinding(x.Binding,definition);
        if(x.Schema!="naval-execution-offline-v1"||x.PortfolioId!=s.Id||x.FinancialConfigDigest!=definition.Digest||
            x.FinancialRevision!=envelope.DurableRevision||x.FinancialRevision!=s.Revision||x.IntegrityDigest!=Seal(x,x.FinancialRevision).IntegrityDigest||
            !Match(x.Namespace,"[A-F0-9]{16}")||x.IdAllocations==null||x.Intents==null)throw Invalid();
        ulong counter=0;var commands=new HashSet<string>(StringComparer.Ordinal);var ids=new Dictionary<string,NavalReservedClientId>(StringComparer.Ordinal);
        foreach(var a in x.IdAllocations)
        {
            if(a==null||!NavalCashRiskPolicy.Text(a.ExecutionCommandId)||!commands.Add(a.ExecutionCommandId)||a.Roles==null||a.Ids==null||
                a.Roles.Count<1||a.Roles.Count>16||a.Ids.Count!=a.Roles.Count||a.AllocationDigest!=AllocationDigest(a.ReservationId,a.ExecutionCommandId,a.Roles)||
                !s.Reservations.Any(r=>r.Id==a.ReservationId&&r.Account==x.Binding.Account))throw Invalid();
            for(int n=0;n<a.Ids.Count;n++)
            {
                var id=a.Ids[n];counter=checked(counter+1);
                if(id==null||!Enum.IsDefined(typeof(NavalClientIdRole),a.Roles[n])||id.ExecutionCommandId!=a.ExecutionCommandId||id.ReservationId!=a.ReservationId||
                    id.Role!=a.Roles[n]||id.Counter!=counter||id.ClientId!=ClientId(x.Namespace,counter)||!ids.TryAdd(id.ClientId,id))throw Invalid();
            }
        }
        if(counter!=x.HighWater)throw Invalid();
        var intentIds=new HashSet<string>(StringComparer.Ordinal);var usedPlacements=new HashSet<string>(StringComparer.Ordinal);
        foreach(var i in x.Intents)
        {
            var r=s.Reservations.SingleOrDefault(r=>r.Id==i.ReservationId);
            if(!NavalCashRiskPolicy.Text(i.IntentId)||!intentIds.Add(i.IntentId)||r==null||r.Account!=x.Binding.Account||
                i.CommandId!=r.Intent.CommandId||i.SignalId!=r.Intent.SignalId||i.Instrument!=r.Intent.Instrument||i.Binding!=x.Binding||
                i.DefinitionDigest!=definition.Digest||i.CatalogDigest!=NavalJson.Hash(definition.Catalog)||!Match(i.ReservationDigest,"[A-F0-9]{64}")||
                i.SourceGeneration<1||i.SourceSequence<1||i.SourceGeneration>s.Generation||(i.SourceGeneration==s.Generation&&i.SourceSequence>s.Sequence)||
                i.PreparedRevision<1||i.PreparedRevision>x.FinancialRevision||i.StopEpoch>x.StopEpoch||!Guid.TryParseExact(i.PreparationSessionId,"N",out _)||
                i.Method!="POST"||!Enum.IsDefined(typeof(NavalExecutionOperation),i.Operation)||!Match(i.Instrument,"[A-Z0-9]{1,16}-[A-Z0-9]{2,12}")||
                i.Deadline.Offset!=TimeSpan.Zero||i.Deadline.ToUnixTimeMilliseconds()<=0||i.Deadline.Ticks%TimeSpan.TicksPerMillisecond!=0||
                i.Body.Length<1||i.Body.Length>16384||i.RequestDigest!=RequestDigest(i)||i.AllocationKeys==null||i.AllocationKeys.Count<1||
                i.AllocationKeys.Distinct(StringComparer.Ordinal).Count()!=i.AllocationKeys.Count||
                i.AllocationKeys.Any(k=>!x.IdAllocations.Any(a=>a.ExecutionCommandId==k&&a.ReservationId==r.Id))||
                !ids.TryGetValue(i.OriginalClientId,out var original)||original.ReservationId!=r.Id||!i.AllocationKeys.Contains(original.ExecutionCommandId)||
                (i.OrderId!=null&&!Match(i.OrderId,"[0-9]{1,32}")))throw Invalid();
            bool placement=i.Operation is NavalExecutionOperation.Submit or NavalExecutionOperation.PlaceProtection;
            var role=i.Operation is NavalExecutionOperation.Submit or NavalExecutionOperation.Cancel?NavalClientIdRole.Parent:NavalClientIdRole.ProtectionAlgo;
            if(original.Role!=role||placement&&(original.ExecutionCommandId!=i.IntentId||!usedPlacements.Add(i.OriginalClientId)||i.OrderId!=null))throw Invalid();
            ValidateStage(i,x.FinancialRevision);
            if(i.Stage==NavalExecutionStage.SubmitUnknown&&(s.Mode!="STOP"||r.Stage!=NavalReservationStage.Uncertain))throw Invalid();
            ValidateBody(i,ids,usedPlacements);
        }
    }
    internal static bool ValidOutcome(NavalExecutionIntent i,NavalExecutionOutcome o)
        =>o!=null&&Enum.IsDefined(typeof(NavalDispatchDisposition),o.Disposition)&&o.OriginalClientId==i.OriginalClientId&&
            (o.OrderId==null||Match(o.OrderId,"[0-9]{1,32}"))&&(i.OrderId==null||o.OrderId==i.OrderId)&&
            (o.Disposition!=NavalDispatchDisposition.Acknowledged||o.OrderId!=null&&o.Code=="0"&&o.SCode=="0"&&o.SubCode==null)&&
            o.Reason==(o.Disposition==NavalDispatchDisposition.Acknowledged?"TRANSPORT_ACK":o.Disposition==NavalDispatchDisposition.NotSent?"TRANSPORT_NOT_SENT":"TRANSPORT_UNKNOWN")&&new[]{o.Code,o.SCode,o.SubCode}.All(c=>c==null||c=="0"||Match(c,"[1-9][0-9]{4,7}"));
    private static void ValidateStage(NavalExecutionIntent i,long revision)
    {
        if(i.DispatchRevision is {} attempt&&(attempt!=checked(i.PreparedRevision+1)||attempt>revision))throw Invalid();
        switch(i.Stage)
        {
            case NavalExecutionStage.Prepared:if(i.DispatchRevision!=null||i.Outcome!=null)throw Invalid();break;
            case NavalExecutionStage.Dispatching:if(i.DispatchRevision==null||i.Outcome!=null)throw Invalid();break;
            case NavalExecutionStage.Accepted:case NavalExecutionStage.SubmitUnknown:case NavalExecutionStage.NotSent:
                if(i.Outcome==null||!ValidOutcome(i,i.Outcome)||
                    i.Outcome.Disposition!=(i.Stage==NavalExecutionStage.Accepted?NavalDispatchDisposition.Acknowledged:i.Stage==NavalExecutionStage.NotSent?NavalDispatchDisposition.NotSent:NavalDispatchDisposition.Unknown)||
                    i.Stage!=NavalExecutionStage.NotSent&&i.DispatchRevision==null)throw Invalid();break;
            default:throw Invalid();
        }
    }
    internal static void ValidateTransition(EngineState previous,EngineState candidate)
    {
        var old=previous.NavalExecution;var next=candidate.NavalExecution;
        if(old==null)
        {
            if(next!=null&&(next.HighWater!=0||next.IdAllocations.Count!=0||next.Intents.Count!=0||next.StopEpoch!=0||
                NavalJson.Serialize(previous.Naval! with{Revision=candidate.Naval!.Revision,StateDigest=""})!=NavalJson.Serialize(candidate.Naval! with{StateDigest=""})))throw Invalid();
            return;
        }
        if(next==null||next.Namespace!=old.Namespace||next.Binding!=old.Binding||next.PortfolioId!=old.PortfolioId||
            next.FinancialConfigDigest!=old.FinancialConfigDigest||next.HighWater<old.HighWater||next.StopEpoch<old.StopEpoch||
            next.IdAllocations.Count<old.IdAllocations.Count||next.Intents.Count<old.Intents.Count)throw Invalid();
        for(int n=0;n<old.IdAllocations.Count;n++)if(NavalJson.Serialize(old.IdAllocations[n])!=NavalJson.Serialize(next.IdAllocations[n]))throw Invalid();
        for(int n=0;n<old.Intents.Count;n++)
        {
            var a=old.Intents[n];var b=next.Intents[n];
            if(NavalJson.Serialize(a with{Stage=b.Stage,DispatchRevision=b.DispatchRevision,Outcome=b.Outcome})!=NavalJson.Serialize(b))throw Invalid();
            if(NavalJson.Serialize(a)==NavalJson.Serialize(b))continue;
            bool dispatch=a.Stage==NavalExecutionStage.Prepared&&b.Stage==NavalExecutionStage.Dispatching&&b.DispatchRevision==candidate.DurableRevision;
            bool unsent=a.Stage==NavalExecutionStage.Prepared&&b.Stage==NavalExecutionStage.NotSent&&b.DispatchRevision==null;
            bool finish=a.Stage==NavalExecutionStage.Dispatching&&b.Stage is NavalExecutionStage.Accepted or NavalExecutionStage.SubmitUnknown or NavalExecutionStage.NotSent&&a.DispatchRevision==b.DispatchRevision;
            if(!dispatch&&!unsent&&!finish)throw Invalid();
        }
        foreach(var appended in next.Intents.Skip(old.Intents.Count))
            if(appended.Stage!=NavalExecutionStage.Prepared||appended.PreparedRevision!=candidate.DurableRevision||appended.DispatchRevision!=null||appended.Outcome!=null)throw Invalid();
        foreach(var c in previous.Naval!.Commands)
            if(!candidate.Naval!.Commands.Any(n=>NavalJson.Serialize(n)==NavalJson.Serialize(c)))throw Invalid();
        foreach(var r in previous.Naval.Reservations.Where(r=>old.IdAllocations.Any(a=>a.ReservationId==r.Id)))
            if(!candidate.Naval!.Reservations.Any(n=>n.Id==r.Id&&n.Intent==r.Intent&&n.Account==r.Account&&n.Fleet==r.Fleet))throw Invalid();
        // Full validation additionally proves the complete consecutive history and high-water, including newly appended counters.
    }
    private static void ValidateBody(NavalExecutionIntent i,IReadOnlyDictionary<string,NavalReservedClientId> ids,HashSet<string> usedPlacements)
    {
        using var doc=JsonDocument.Parse(i.Body);var e=doc.RootElement;
        if(i.Operation==NavalExecutionOperation.CancelProtection)
        {if(e.ValueKind!=JsonValueKind.Array||e.GetArrayLength()!=1)throw Invalid();e=e[0];}
        if(e.ValueKind!=JsonValueKind.Object)throw Invalid();
        var fields=new Dictionary<string,object>();
        string S(JsonElement obj,string key)=>obj.GetProperty(key).ValueKind==JsonValueKind.String?obj.GetProperty(key).GetString()!:throw Invalid();
        string Text(string key)=>S(e,key);
        void Add(string key,string value){if(Text(key)!=value)throw Invalid();fields.Add(key,value);}
        Add("instId",i.Instrument);
        switch(i.Operation)
        {
            case NavalExecutionOperation.Submit:
            case NavalExecutionOperation.PlaceProtection:
                Add("tdMode","cash");string side=Text("side");if(i.Operation==NavalExecutionOperation.PlaceProtection?side!="sell":side is not ("buy" or "sell"))throw Invalid();Add("side",side);
                string type=Text("ordType");if(i.Operation==NavalExecutionOperation.Submit?type is not ("limit" or "fok"):type is not ("conditional" or "oco"))throw Invalid();Add("ordType",type);
                Add("sz",Number(Text("sz")));if(i.Operation==NavalExecutionOperation.Submit)Add("px",Number(Text("px")));
                Add(i.Operation==NavalExecutionOperation.Submit?"clOrdId":"algoClOrdId",i.OriginalClientId);Add("tradeQuoteCcy",i.Binding.QuoteCurrency);
                if(i.Operation==NavalExecutionOperation.Submit&&e.TryGetProperty("attachAlgoOrds",out var attached))
                {
                    if(attached.ValueKind!=JsonValueKind.Array||attached.GetArrayLength()!=1)throw Invalid();var a=attached[0];string id=S(a,"attachAlgoClOrdId");
                    if(!ids.TryGetValue(id,out var allocation)||allocation.Role!=NavalClientIdRole.ProtectionAlgo||allocation.ReservationId!=i.ReservationId||
                        !i.AllocationKeys.Contains(allocation.ExecutionCommandId)||id==i.OriginalClientId||!usedPlacements.Add(id))throw Invalid();
                    fields.Add("attachAlgoOrds",new[]{new Dictionary<string,object>{{"attachAlgoClOrdId",id},{"tpTriggerPx",Number(S(a,"tpTriggerPx"))},{"tpOrdPx","-1"},{"tpTriggerPxType","last"},
                        {"slTriggerPx",Number(S(a,"slTriggerPx"))},{"slOrdPx","-1"},{"slTriggerPxType","last"}}});
                }
                if(i.Operation==NavalExecutionOperation.PlaceProtection)
                {
                    bool tp=e.TryGetProperty("tpTriggerPx",out _),sl=e.TryGetProperty("slTriggerPx",out _);
                    if((!tp&&!sl)||(type=="conditional"&&tp&&sl)||(type=="oco"&&(!tp||!sl)))throw Invalid();
                    if(tp){Add("tpTriggerPx",Number(Text("tpTriggerPx")));Add("tpOrdPx","-1");Add("tpTriggerPxType","last");}
                    if(sl){Add("slTriggerPx",Number(Text("slTriggerPx")));Add("slOrdPx","-1");Add("slTriggerPxType","last");}
                }
                break;
            case NavalExecutionOperation.Cancel:case NavalExecutionOperation.CancelProtection:
                bool algo=i.Operation==NavalExecutionOperation.CancelProtection;
                if(i.OrderId!=null)Add(algo?"algoId":"ordId",i.OrderId);Add(algo?"algoClOrdId":"clOrdId",i.OriginalClientId);break;
            default:throw Invalid();
        }
        string path=i.Operation switch{NavalExecutionOperation.Submit=>"/api/v5/trade/order",NavalExecutionOperation.Cancel=>"/api/v5/trade/cancel-order",
            NavalExecutionOperation.PlaceProtection=>"/api/v5/trade/order-algo",NavalExecutionOperation.CancelProtection=>"/api/v5/trade/cancel-algos",_=>throw Invalid()};
        byte[] canonical=i.Operation==NavalExecutionOperation.CancelProtection?JsonSerializer.SerializeToUtf8Bytes(new[]{fields}):JsonSerializer.SerializeToUtf8Bytes(fields);
        if(i.Path!=path||!i.Body.SequenceEqual(canonical))throw Invalid();
    }
    private static string Number(string text)
    {
        if(!Match(text,"(?:0|[1-9][0-9]{0,27})(?:\\.[0-9]{1,28})?")||text.Length>58||text.Replace(".","").TrimStart('0').TrimEnd('0').Length>28||
            !decimal.TryParse(text,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var number)||number<=0)throw Invalid();return text;
    }
}
