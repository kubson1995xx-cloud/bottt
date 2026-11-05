using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
namespace Apex.Desktop.Okx;

internal sealed record OkxControllerHello(string Schema,string Epoch,string Session);
internal sealed record OkxControllerCommand(string Schema,string Epoch,string Session,string CommandId,string Action);
internal sealed record OkxControllerReceipt(string Schema,string Epoch,string Session,string CommandId,string Outcome,string Code,string Mode,bool Armed,OkxControllerSnapshot? Snapshot);
// Shared wire contracts contain no admission or financial authority.
internal static class OkxControllerProtocol
{
    internal const string Version="apex-controller-v1";
    internal const int CommandLimit=1024,ReceiptLimit=280000;
    internal static readonly HashSet<string> Actions=new(StringComparer.Ordinal){"CONNECT","PREPARE","ARM","START","STOP","EMERGENCY","STATUS","SNAPSHOT","ADVISORY-STATUS SOL-EUR","ADVISORY-STATUS BTC-EUR","ADVISORY-STATUS ETH-EUR","DISCONNECT"};
    internal static bool Mutation(string a)=>a is "CONNECT" or "PREPARE" or "ARM" or "START" or "STOP" or "EMERGENCY" or "DISCONNECT";
    private static bool Id(string s)=>s.Length==32&&s.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void Require(bool b){if(!b)throw new InvalidDataException("CONTROLLER_FRAME_REFUSED");}
    private static JsonDocument Parse(string line,int cap,params string[] fields)
    {
        Require(Encoding.UTF8.GetByteCount(line)<=cap&&!line.Contains('\n')&&!line.Contains('\r'));
        try
        {
            var d=JsonDocument.Parse(line,new JsonDocumentOptions{MaxDepth=32});
            try{Require(d.RootElement.ValueKind==JsonValueKind.Object);var names=d.RootElement.EnumerateObject().Select(p=>p.Name).ToArray();Require(names.Length==fields.Length&&names.Distinct(StringComparer.Ordinal).Count()==names.Length&&names.All(fields.Contains));return d;}catch{d.Dispose();throw;}
        }
        catch(JsonException){throw new InvalidDataException("CONTROLLER_FRAME_REFUSED");}
    }
    private static string Text(JsonElement j,string n){Require(j.GetProperty(n).ValueKind==JsonValueKind.String);return j.GetProperty(n).GetString()!;}
    internal static string EncodeHello(OkxControllerHello h){var s=JsonSerializer.Serialize(h);DecodeHello(s);return s;}
    internal static OkxControllerHello DecodeHello(string s)
    {using var d=Parse(s,CommandLimit,"Schema","Epoch","Session");var j=d.RootElement;var h=new OkxControllerHello(Text(j,"Schema"),Text(j,"Epoch"),Text(j,"Session"));Require(h.Schema==Version&&Id(h.Epoch)&&Id(h.Session));return h;}
    internal static string EncodeCommand(OkxControllerCommand c){var s=JsonSerializer.Serialize(c);DecodeCommand(s);return s;}
    internal static OkxControllerCommand DecodeCommand(string s)
    {using var d=Parse(s,CommandLimit,"Schema","Epoch","Session","CommandId","Action");var j=d.RootElement;var c=new OkxControllerCommand(Text(j,"Schema"),Text(j,"Epoch"),Text(j,"Session"),Text(j,"CommandId"),Text(j,"Action"));Require(c.Schema==Version&&Id(c.Epoch)&&Id(c.Session)&&Id(c.CommandId)&&Actions.Contains(c.Action));return c;}
    internal static string EncodeReceipt(OkxControllerReceipt r){var s=JsonSerializer.Serialize(r);DecodeReceipt(s);return s;}
    internal static OkxControllerReceipt DecodeReceipt(string s)
    {
        using var d=Parse(s,ReceiptLimit,"Schema","Epoch","Session","CommandId","Outcome","Code","Mode","Armed","Snapshot");var j=d.RootElement;
        Require(j.GetProperty("Armed").ValueKind is JsonValueKind.True or JsonValueKind.False);
        var r=new OkxControllerReceipt(Text(j,"Schema"),Text(j,"Epoch"),Text(j,"Session"),Text(j,"CommandId"),Text(j,"Outcome"),Text(j,"Code"),Text(j,"Mode"),j.GetProperty("Armed").GetBoolean(),j.GetProperty("Snapshot").ValueKind==JsonValueKind.Null?null:OkxControllerSnapshotCodec.Decode("CONTROLLER_SNAPSHOT "+j.GetProperty("Snapshot").GetRawText()));
        Require(r.Schema==Version&&Id(r.Epoch)&&Id(r.Session)&&Id(r.CommandId)&&r.Outcome is "confirmed" or "refused" or "pending-or-unknown"&&r.Code.Length is >0 and <=64&&r.Code.All(c=>c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')&&r.Mode is "STOP" or "RUNNING" or "EMERGENCY");
        Require(r.Snapshot==null||r.Snapshot.Mode==r.Mode&&r.Snapshot.Armed==r.Armed);return r;
    }
}
