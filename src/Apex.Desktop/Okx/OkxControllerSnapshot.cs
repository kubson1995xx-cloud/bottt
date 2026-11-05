using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace Apex.Desktop.Okx;

// Portable read model only: no account identity, credentials, storage or command authority.
internal sealed record OkxControllerSnapshot(string Schema,DateTimeOffset GeneratedUtc,bool Connected,string Mode,bool Armed,bool Ready,
    bool Emergency,long? Revision,OkxControllerCapital? Capital,OkxControllerObservation Observation,OkxStrategyEvaluationStatus Evaluation,
    OkxControllerWindow<OkxControllerBalance> Balances,OkxControllerWindow<OkxControllerInstrument> Catalog,
    OkxControllerWindow<OkxControllerInventory> Inventory,OkxControllerWindow<OkxControllerOrder> Orders,
    OkxControllerWindow<OkxControllerProtection> Protection,OkxControllerWindow<OkxControllerFill> History);
internal sealed record OkxControllerWindow<T>(int Total,bool Truncated,T[] Items);
internal sealed record OkxControllerCapital(string Quote,decimal Cash,decimal Held,decimal Free,decimal AllocatedCost,decimal? LiquidationEquity);
internal sealed record OkxControllerObservation(string Origin,DateTimeOffset? CaptureUtc,DateTimeOffset? RequestUtc,DateTimeOffset? ReceiveUtc,
    DateTimeOffset? EconomicUtc,bool EvidenceAvailable,bool HistoryAvailable);
internal sealed record OkxControllerBalance(string Currency,decimal? Total,decimal? Available,decimal? Frozen,DateTimeOffset? SourceUtc,bool Excluded);
internal sealed record OkxControllerInstrument(string Instrument,string Base,string Quote,decimal Quantum,decimal Minimum,decimal Tick);
internal sealed record OkxControllerInventory(string Instrument,decimal Quantity,decimal Pending,decimal AllocatedCost,decimal RiskHold,
    bool ReductionUnsettled,string Protection);
internal sealed record OkxControllerOrder(string Instrument,string? State,decimal? Quantity,decimal? Filled,decimal? SignedFee,string? FeeCurrency);
internal sealed record OkxControllerProtection(string Instrument,string? State,decimal? Quantity,decimal? Stop,decimal? TakeProfit);
internal sealed record OkxControllerFill(string Instrument,bool Reduction,decimal Quantity,decimal Price,decimal Fee,string FeeCurrency,DateTimeOffset TradeUtc);

internal static class OkxControllerSnapshotCodec
{
    internal const string Prefix="CONTROLLER_SNAPSHOT ",Schema="apex-controller-snapshot-v1";
    internal const int MaxLineBytes=262144,Window=32,MaxCatalog=256,MaxBalances=256;
    private static readonly JsonSerializerOptions Options=new(){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,MaxDepth=16};
    internal static OkxControllerSnapshot Disconnected(DateTimeOffset now)=>new(Schema,now,false,"STOP",false,false,false,null,null,
        new("NONE",null,null,null,null,false,false),new(0,0,0,0,"NOT_EVALUATED"),Empty<OkxControllerBalance>(),Empty<OkxControllerInstrument>(),
        Empty<OkxControllerInventory>(),Empty<OkxControllerOrder>(),Empty<OkxControllerProtection>(),Empty<OkxControllerFill>());
    private static OkxControllerWindow<T> Empty<T>()=>new(0,false,Array.Empty<T>());
    internal static string Encode(OkxControllerSnapshot snapshot)
    {
        Validate(snapshot);string line=Prefix+JsonSerializer.Serialize(snapshot,Options);
        if(Encoding.UTF8.GetByteCount(line)>MaxLineBytes)throw Invalid();return line;
    }
    internal static OkxControllerSnapshot Decode(string line)
    {
        if(line==null||line.Length>MaxLineBytes||!line.StartsWith(Prefix,StringComparison.Ordinal)||line.IndexOfAny(new[]{'\r','\n'})>=0||Encoding.UTF8.GetByteCount(line)>MaxLineBytes)throw Invalid();
        try
        {
            using var doc=JsonDocument.Parse(line[Prefix.Length..],new JsonDocumentOptions{MaxDepth=16});
            Shape(doc.RootElement,typeof(OkxControllerSnapshot));
            var snapshot=doc.RootElement.Deserialize<OkxControllerSnapshot>(Options)??throw Invalid();Validate(snapshot);return snapshot;
        }
        catch(Exception e) when(e is JsonException or ArgumentException or OverflowException){throw Invalid();}
    }
    // Required and duplicate properties are checked recursively, including nullable ones;
    // System.Text.Json alone would otherwise default absent constructor operands.
    private static void Shape(JsonElement value,Type type)
    {
        var underlying=Nullable.GetUnderlyingType(type);if(underlying!=null){if(value.ValueKind==JsonValueKind.Null)return;type=underlying;}
        if(type.IsArray){if(value.ValueKind!=JsonValueKind.Array)throw Invalid();foreach(var item in value.EnumerateArray())Shape(item,type.GetElementType()!);return;}
        if(type==typeof(string)||type.IsValueType)return;
        if(value.ValueKind!=JsonValueKind.Object)throw Invalid();var properties=type.GetProperties(BindingFlags.Instance|BindingFlags.Public);var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var field in value.EnumerateObject())
        {
            var property=properties.SingleOrDefault(p=>p.Name==field.Name);if(property==null||!seen.Add(field.Name))throw Invalid();
            if(field.Value.ValueKind==JsonValueKind.Null)
            {if(Nullable.GetUnderlyingType(property.PropertyType)==null&&new NullabilityInfoContext().Create(property).ReadState!=NullabilityState.Nullable)throw Invalid();}
            else Shape(field.Value,property.PropertyType);
        }
        if(seen.Count!=properties.Length)throw Invalid();
    }
    private static InvalidDataException Invalid()=>new("CONTROLLER_SNAPSHOT_INVALID");
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool valid){if(!valid)throw Invalid();}
    private static bool Token(string? value,int max=32)=>value is {Length:>0}&&value.Length<=max&&value.All(c=>c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '-');
    private static bool Instrument(string? value)=>Token(value)&&value!.Split('-') is {Length:2} parts&&parts.All(p=>Token(p,16));
    private static bool Before(DateTimeOffset? value,DateTimeOffset? end)=>value==null||end!=null&&value<=end;
    private static bool Nonnegative(decimal? value)=>value==null||value>=0;
    private static void CheckWindow<T>(OkxControllerWindow<T> window,int cap)
    {Require(window!=null&&window.Items!=null&&window.Total>=0&&window.Items.Length==Math.Min(window.Total,cap)&&window.Truncated==(window.Total>cap)&&window.Items.All(i=>i!=null));}
    private static void Validate(OkxControllerSnapshot s)
    {
        Require(s!=null&&s.Schema==Schema&&s.GeneratedUtc!=default&&s.Observation!=null&&s.Evaluation!=null&&s.Mode is "STOP" or "RUNNING");
        CheckWindow(s.Balances,MaxBalances);CheckWindow(s.Catalog,MaxCatalog);CheckWindow(s.Inventory,Window);CheckWindow(s.Orders,Window);CheckWindow(s.Protection,Window);CheckWindow(s.History,Window);
        Require(!s.Catalog.Truncated&&!s.Balances.Truncated); // Neither catalog nor balance coverage can be silently partial.
        var o=s.Observation;var e=s.Evaluation;
        Require(e.Completed>=0&&e.Wait>=0&&e.Entry>=0&&e.Exit>=0&&e.Wait<=e.Completed&&e.Entry<=e.Completed-e.Wait&&e.Exit==e.Completed-e.Wait-e.Entry);
        Require(e.Code is "NOT_EVALUATED" or "EVALUATED_WAIT" or "EVALUATED_ENTRY" or "EVALUATED_EXIT" or "SKIPPED_FINITE" or "SKIPPED_CAN_EXECUTE" or "SKIPPED_NOT_RUNNING" or "SKIPPED_ENTRY_OBSERVATION" or "SKIPPED_INELIGIBLE" or "SKIPPED_BTC_REFERENCE" or "EXECUTION_FAILED" or "EVALUATION_FAILED" or "SKIPPED_RECONCILIATION");
        Require(Before(o.CaptureUtc,s.GeneratedUtc)&&Before(o.RequestUtc,o.ReceiveUtc)&&Before(o.ReceiveUtc,s.GeneratedUtc)&&Before(o.EconomicUtc,s.GeneratedUtc));
        Require(o.RequestUtc.HasValue==o.ReceiveUtc.HasValue&&(!s.Ready||s.Connected&&s.Armed)&&(!s.Armed||s.Connected)&&(!s.Emergency||s.Mode=="STOP"));
        if(!s.Connected)
        {
            Require(s.Mode=="STOP"&&!s.Armed&&!s.Ready&&!s.Emergency&&s.Revision==null&&s.Capital==null&&o==new OkxControllerObservation("NONE",null,null,null,null,false,false)&&
                e==new OkxStrategyEvaluationStatus(0,0,0,0,"NOT_EVALUATED")&&s.Balances.Total==0&&s.Catalog.Total==0&&s.Inventory.Total==0&&s.Orders.Total==0&&s.Protection.Total==0&&s.History.Total==0);return;
        }
        Require(s.Revision>=0&&s.Capital!=null&&o.Origin is "BASELINE" or "OPERATIONAL"&&o.CaptureUtc!=null);
        var c=s.Capital!;Require(Token(c.Quote,16)&&c.Cash>=0&&c.Held>=0&&c.Free==c.Cash-c.Held&&c.AllocatedCost>=0&&Nonnegative(c.LiquidationEquity));
        Require(s.Balances.Items.Select(b=>b.Currency).Distinct(StringComparer.Ordinal).Count()==s.Balances.Items.Length);
        foreach(var b in s.Balances.Items)Require(Token(b.Currency,16)&&Nonnegative(b.Total)&&Nonnegative(b.Available)&&Nonnegative(b.Frozen)&&Before(b.SourceUtc,s.GeneratedUtc)&&Before(b.SourceUtc,o.ReceiveUtc??s.GeneratedUtc));
        Require(s.Catalog.Items.Select(i=>i.Instrument).Distinct(StringComparer.Ordinal).Count()==s.Catalog.Items.Length);
        foreach(var i in s.Catalog.Items)Require(Instrument(i.Instrument)&&Token(i.Base,16)&&Token(i.Quote,16)&&i.Instrument==i.Base+"-"+i.Quote&&i.Quote==c.Quote&&i.Quantum>0&&i.Minimum>0&&i.Tick>0);
        bool Admitted(string instrument)=>s.Catalog.Items.Any(i=>i.Instrument==instrument);
        foreach(var i in s.Inventory.Items)Require(Admitted(i.Instrument)&&i.Quantity>=0&&i.Pending>=0&&i.AllocatedCost>=0&&i.RiskHold>=0&&i.Protection is "UNPROTECTED_QUARANTINED_FULL_LOSS_RESERVED" or "PROTECTION_GAP" or "OBSERVED_PROTECTION" or "NO_INVENTORY");
        foreach(var r in s.Orders.Items)Require(Admitted(r.Instrument)&&OrderState(r.State)&&Nonnegative(r.Quantity)&&Nonnegative(r.Filled)&&(r.Quantity==null||r.Filled==null||r.Filled<=r.Quantity)&&(r.SignedFee==null||r.SignedFee<=0)&&(r.FeeCurrency==null||Token(r.FeeCurrency,16)));
        foreach(var p in s.Protection.Items)Require(Admitted(p.Instrument)&&p.State is null or "live" or "effective" or "canceled" or "order_failed" or "partially_effective"&&Nonnegative(p.Quantity)&&Nonnegative(p.Stop)&&Nonnegative(p.TakeProfit));
        foreach(var f in s.History.Items)Require(Admitted(f.Instrument)&&f.Quantity>0&&f.Price>0&&f.Fee>=0&&Token(f.FeeCurrency,16)&&f.TradeUtc!=default&&f.TradeUtc<=s.GeneratedUtc);
        Require(o.HistoryAvailable||s.History.Total==0&&s.Orders.Total==0&&s.Protection.Total==0);
    }
    private static bool OrderState(string? state)=>state is null or "live" or "partially_filled" or "filled" or "canceled" or "mmp_canceled";
}
