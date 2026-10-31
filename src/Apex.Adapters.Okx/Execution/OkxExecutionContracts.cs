using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Apex.Adapters.Okx.Execution;

/// <summary>Immutable requested transport binding, not authenticated identity or trading authority.</summary>
public sealed record OkxExecutionScope
{
    public string Environment { get; }
    public string Uid { get; }
    public string MainUid { get; }
    public string QuoteCurrency { get; }
    public string CredentialBindingId { get; }
    public OkxExecutionScope(string environment, string uid, string mainUid, string quoteCurrency, string credentialBindingId)
    {
        if (environment is not ("Demo" or "Live") || !Grammar.Match(uid,"[0-9]{1,32}") || mainUid != uid ||
            !Grammar.Match(quoteCurrency,"[A-Z0-9]{2,12}") || !Grammar.Match(credentialBindingId,"[A-Za-z0-9_-]{1,64}")) throw Grammar.Invalid();
        Environment=environment; Uid=uid; MainUid=mainUid; QuoteCurrency=quoteCurrency; CredentialBindingId=credentialBindingId;
    }
}
public enum OkxExecutionOperation { Submit, Cancel, PlaceProtection, CancelProtection, Lookup }
/// <summary>Protocol result categories. Rejected is reserved for a future independently validated
/// rejection contract; the current transport emits Unknown for every post-invocation error.</summary>
public enum OkxWriteDisposition { ProvenNotSent, Acknowledged, Rejected, Unknown }
/// <summary>Protocol result only. ACK is not a fill, protection coverage or settled cancellation.</summary>
public sealed record OkxWriteOutcome
{
    public OkxWriteDisposition Disposition { get; }
    public string OriginalClientId { get; }
    public string? OrderId { get; }
    public string Reason { get; }
    public string? Code { get; }
    public string? SCode { get; }
    public string? SubCode { get; }
    internal OkxWriteOutcome(OkxWriteDisposition disposition,string originalClientId,string? orderId,string reason,string? code=null,string? sCode=null,string? subCode=null)
    { Disposition=disposition; OriginalClientId=originalClientId; OrderId=orderId; Reason=reason; Code=code; SCode=sCode; SubCode=subCode; }
}
/// <summary>Untrusted parent-only order-detail observation. Cumulative fields must never post individual fills.
/// Attachment success/failure is not surfaced; Observed is never protection coverage or finality.</summary>
public sealed record OkxOrderObservation
{
    public OkxExecutionScope Scope { get; }
    public string Instrument { get; }
    public string? OrderId { get; }
    public string OriginalClientId { get; }
    public string? State { get; }
    public string? ObservedClientId { get; }
    public string? Size { get; }
    public string? AccFillSize { get; }
    public string? AveragePrice { get; }
    public string? TradeQuoteCurrency { get; }
    public string Reason { get; }
    internal OkxOrderObservation(OkxExecutionScope scope,string instrument,string? orderId,string originalClientId,string? state,string? size,string? accFillSize,string? averagePrice,string? tradeQuoteCurrency,string reason,string? observedClientId=null)
    { Scope=scope; Instrument=instrument; OrderId=orderId; OriginalClientId=originalClientId; State=state; ObservedClientId=observedClientId; Size=size; AccFillSize=accFillSize; AveragePrice=averagePrice; TradeQuoteCurrency=tradeQuoteCurrency; Reason=reason; }
}
/// <summary>Synchronous last caller check after all waits/secret access, immediately before handler invocation.
/// Caller must validate its current STOP/session/source/catalog/reservation epoch, and serialize changes with dispatch.
/// This callback is not an authority issuer, durable send permit or authenticated readiness proof.</summary>
public interface IOkxFinalDispatchFence { bool CanDispatch(OkxPreparedRequest request, DateTimeOffset utcNow); }
public sealed record OkxAttachedProtection(string ClientId, string TakeProfitTrigger, string StopLossTrigger);

/// <summary>Closed immutable request. Persistence must retain every field and these exact bytes; no re-quote on recovery.</summary>
public sealed class OkxPreparedRequest
{
    private readonly byte[] body;
    public OkxExecutionScope Scope { get; }
    public OkxExecutionOperation Operation { get; }
    public string Method => Operation == OkxExecutionOperation.Lookup ? "GET" : "POST";
    public string Path { get; }
    public string Instrument { get; }
    public string OriginalClientId { get; }
    public string? OrderId { get; }
    public DateTimeOffset Deadline { get; }
    public string Digest { get; }
    public byte[] CopyBody() => body.ToArray();
    internal ReadOnlySpan<byte> Body => body;
    private OkxPreparedRequest(OkxExecutionScope scope,OkxExecutionOperation operation,string path,string instrument,string clientId,string? orderId,DateTimeOffset deadline,byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!Grammar.Instrument(instrument) || !Grammar.ClientId(clientId) || orderId is not null && !Grammar.OrderId(orderId) ||
            deadline.Offset!=TimeSpan.Zero || deadline.ToUnixTimeMilliseconds()<=0 || deadline.Ticks%TimeSpan.TicksPerMillisecond!=0) throw Grammar.Invalid();
        Scope=scope; Operation=operation; Path=path; Instrument=instrument; OriginalClientId=clientId; OrderId=orderId; Deadline=deadline; body=bytes.ToArray();
        Digest=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { scope.Environment,scope.Uid,scope.MainUid,scope.QuoteCurrency,scope.CredentialBindingId,Method,path,instrument,clientId,orderId??"",deadline.ToString("O",CultureInfo.InvariantCulture),Convert.ToBase64String(body) }))).ToLowerInvariant();
    }
    public static OkxPreparedRequest Submit(OkxExecutionScope scope,string instrument,string side,string orderType,string size,string price,string clientId,DateTimeOffset deadline,OkxAttachedProtection? protection=null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (side is not ("buy" or "sell") || orderType is not ("limit" or "fok") || !Grammar.Decimal(size) || !Grammar.Decimal(price)) throw Grammar.Invalid();
        var fields=new Dictionary<string,object> { ["instId"]=instrument,["tdMode"]="cash",["side"]=side,["ordType"]=orderType,["sz"]=size,["px"]=price,["clOrdId"]=clientId,["tradeQuoteCcy"]=scope.QuoteCurrency };
        if (protection is not null)
        {
            if (!Grammar.ClientId(protection.ClientId) || protection.ClientId==clientId || !Grammar.Decimal(protection.TakeProfitTrigger) || !Grammar.Decimal(protection.StopLossTrigger)) throw Grammar.Invalid();
            fields.Add("attachAlgoOrds",new[] { new Dictionary<string,object> { ["attachAlgoClOrdId"]=protection.ClientId,["tpTriggerPx"]=protection.TakeProfitTrigger,["tpOrdPx"]="-1",["tpTriggerPxType"]="last",["slTriggerPx"]=protection.StopLossTrigger,["slOrdPx"]="-1",["slTriggerPxType"]="last" } });
        }
        return new(scope,OkxExecutionOperation.Submit,"/api/v5/trade/order",instrument,clientId,null,deadline,JsonSerializer.SerializeToUtf8Bytes(fields));
    }
    public static OkxPreparedRequest Cancel(OkxExecutionScope scope,string instrument,string? orderId,string originalClientId,DateTimeOffset deadline) =>
        new(scope,OkxExecutionOperation.Cancel,"/api/v5/trade/cancel-order",instrument,originalClientId,orderId,deadline,CancelBytes(instrument,orderId,originalClientId,false));
    public static OkxPreparedRequest PlaceProtection(OkxExecutionScope scope,string instrument,string type,string size,string algoClientId,string? tpTrigger,string? slTrigger,DateTimeOffset deadline)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (type is not ("conditional" or "oco") || !Grammar.Decimal(size) || tpTrigger is null && slTrigger is null ||
            type=="conditional" && tpTrigger is not null && slTrigger is not null || type=="oco" && (tpTrigger is null || slTrigger is null) ||
            tpTrigger is not null && !Grammar.Decimal(tpTrigger) || slTrigger is not null && !Grammar.Decimal(slTrigger)) throw Grammar.Invalid();
        var fields=new Dictionary<string,object> { ["instId"]=instrument,["tdMode"]="cash",["side"]="sell",["ordType"]=type,["sz"]=size,["algoClOrdId"]=algoClientId,["tradeQuoteCcy"]=scope.QuoteCurrency };
        if(tpTrigger is not null) { fields.Add("tpTriggerPx",tpTrigger); fields.Add("tpOrdPx","-1"); fields.Add("tpTriggerPxType","last"); }
        if(slTrigger is not null) { fields.Add("slTriggerPx",slTrigger); fields.Add("slOrdPx","-1"); fields.Add("slTriggerPxType","last"); }
        return new(scope,OkxExecutionOperation.PlaceProtection,"/api/v5/trade/order-algo",instrument,algoClientId,null,deadline,JsonSerializer.SerializeToUtf8Bytes(fields));
    }
    public static OkxPreparedRequest CancelProtection(OkxExecutionScope scope,string instrument,string? algoId,string originalAlgoClientId,DateTimeOffset deadline) =>
        new(scope,OkxExecutionOperation.CancelProtection,"/api/v5/trade/cancel-algos",instrument,originalAlgoClientId,algoId,deadline,CancelBytes(instrument,algoId,originalAlgoClientId,true));
    private static byte[] CancelBytes(string inst,string? id,string client,bool algo)
    {
        var fields=new Dictionary<string,string> { ["instId"]=inst };
        if(id is not null) fields.Add(algo?"algoId":"ordId",id);
        fields.Add(algo?"algoClOrdId":"clOrdId",client);
        return algo?JsonSerializer.SerializeToUtf8Bytes(new[] { fields }):JsonSerializer.SerializeToUtf8Bytes(fields);
    }
    internal static OkxPreparedRequest Lookup(OkxExecutionScope scope,string inst,string? order,string client,DateTimeOffset deadline) =>
        new(scope,OkxExecutionOperation.Lookup,"/api/v5/trade/order?clOrdId="+client+"&instId="+inst+(order is null?"":"&ordId="+order),inst,client,order,deadline,[]);
    /// <summary>Closed canonical replay; refuses unknown fields, coercion, duplicates, mismatched identity and unsupported operations.</summary>
    public static OkxPreparedRequest Restore(OkxExecutionScope scope,OkxExecutionOperation operation,string instrument,string originalClientId,string? orderId,DateTimeOffset deadline,ReadOnlyMemory<byte> body)
    {
        try
        {
            if(body.Length is <=0 or >16384) throw Grammar.Invalid();
            var copy=body.ToArray(); using var doc=JsonDocument.Parse(copy); Grammar.Unique(doc.RootElement);
            var e=doc.RootElement;
            string S(string n)=>Grammar.String(e,n);
            OkxPreparedRequest r;
            switch(operation)
            {
                case OkxExecutionOperation.Submit:
                    OkxAttachedProtection? p=null;
                    if(e.TryGetProperty("attachAlgoOrds",out var a))
                    { if(a.ValueKind!=JsonValueKind.Array || a.GetArrayLength()!=1) throw Grammar.Invalid(); var t=a[0]; p=new(Grammar.String(t,"attachAlgoClOrdId"),Grammar.String(t,"tpTriggerPx"),Grammar.String(t,"slTriggerPx")); }
                    if(orderId is not null) throw Grammar.Invalid();
                    r=Submit(scope,instrument,S("side"),S("ordType"),S("sz"),S("px"),originalClientId,deadline,p); break;
                case OkxExecutionOperation.Cancel: r=Cancel(scope,instrument,orderId,originalClientId,deadline); break;
                case OkxExecutionOperation.PlaceProtection:
                    if(orderId is not null) throw Grammar.Invalid();
                    r=PlaceProtection(scope,instrument,S("ordType"),S("sz"),originalClientId,e.TryGetProperty("tpTriggerPx",out var tp)?tp.GetString():null,e.TryGetProperty("slTriggerPx",out var sl)?sl.GetString():null,deadline); break;
                case OkxExecutionOperation.CancelProtection: r=CancelProtection(scope,instrument,orderId,originalClientId,deadline); break;
                default: throw Grammar.Invalid();
            }
            if(!copy.AsSpan().SequenceEqual(r.body)) throw Grammar.Invalid();
            return r;
        }
        catch(Exception ex) when(ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { throw Grammar.Invalid(); }
    }
}
internal static class Grammar
{
    internal static bool Match(string? s,string pattern)=>s is not null && Regex.IsMatch(s,"\\A(?:"+pattern+")\\z",RegexOptions.CultureInvariant);
    internal static bool ClientId(string? s)=>Match(s,"[A-Za-z0-9]{1,32}");
    internal static bool OrderId(string? s)=>Match(s,"[0-9]{1,32}");
    internal static bool Instrument(string? s)=>Match(s,"[A-Z0-9]{1,16}-[A-Z0-9]{2,12}");
    // No exponent, locale, sign, silent precision loss, overflow or nonzero underflow.
    internal static bool Decimal(string? s,bool zero=false)
    {
        if(!Match(s,"(?:0|[1-9][0-9]{0,27})(?:\\.[0-9]{1,28})?") || s!.Length>58) return false;
        string digits=s.Replace(".","").TrimStart('0').TrimEnd('0');
        return digits.Length<=28 && decimal.TryParse(s,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var v) && (zero?v>=0:v>0);
    }
    internal static ArgumentException Invalid()=>new("InvalidExecutionRequest");
    internal static string String(JsonElement e,string name) { var p=e.GetProperty(name); if(p.ValueKind!=JsonValueKind.String) throw new JsonException(); return p.GetString()!; }
    internal static void Unique(JsonElement e)
    {
        if(e.ValueKind==JsonValueKind.Object)
        { var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase); foreach(var p in e.EnumerateObject()) { if(!seen.Add(p.Name)) throw new JsonException(); Unique(p.Value); } }
        else if(e.ValueKind==JsonValueKind.Array) foreach(var v in e.EnumerateArray()) Unique(v);
        else if(e.ValueKind==JsonValueKind.String) _=e.GetString(); // validate escaped surrogates, including ignored messages
    }
}
