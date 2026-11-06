using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Apex.Desktop.Okx;

internal sealed record OkxAdvisoryTool(string Tool,string? Original,string? Mapped,string State,string ReportSha256,string NativeSha256);
// Immutable identity; no trading/arming authority. Only a fresh store lease can admit an entry.
internal sealed record OkxAdvisorySnapshot(string Instrument,string DatasetSha256,DateTimeOffset Cutoff,DateTimeOffset ExpiresUtc,string Generation,string ManifestSha256,string Inference,OkxAdvisoryTool Vibe,OkxAdvisoryTool Agents)
{
    public bool AllowsEntry=>Inference=="genuine"&&Vibe.State=="completed"&&Agents.State=="completed"&&Vibe.Mapped=="BUY"&&Agents.Mapped=="BUY";
    internal static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static bool Hex(string value,int count)=>value.Length==count&&value.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static JsonDocument Document(byte[] bytes,int max=16384)
    {
        if(bytes.Length==0||bytes.Length>max)throw new InvalidDataException("ADVISORY_SIZE");
        var doc=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=12});
        try { Unique(doc.RootElement);return doc; } catch {doc.Dispose();throw;}
    }
    static void Unique(JsonElement e)
    {
        if(e.ValueKind==JsonValueKind.Object){var seen=new HashSet<string>(StringComparer.Ordinal);foreach(var p in e.EnumerateObject()){if(!seen.Add(p.Name))throw new InvalidDataException("ADVISORY_DUPLICATE");Unique(p.Value);}}
        else if(e.ValueKind==JsonValueKind.Array)foreach(var p in e.EnumerateArray())Unique(p);
    }
    static void Fields(JsonElement e,string names)
    {
        if(e.ValueKind!=JsonValueKind.Object||!e.EnumerateObject().Select(p=>p.Name).OrderBy(x=>x,StringComparer.Ordinal).SequenceEqual(names.Split(' ').OrderBy(x=>x,StringComparer.Ordinal)))throw new InvalidDataException("ADVISORY_SCHEMA");
    }
    static string Text(JsonElement e,string name)=>e.GetProperty(name).ValueKind==JsonValueKind.String?e.GetProperty(name).GetString()!:throw new InvalidDataException("ADVISORY_TYPE");
    static void Require(bool value){if(!value)throw new InvalidDataException("ADVISORY_BINDING");}
    static string? Nullable(JsonElement e,string key)=>e.GetProperty(key).ValueKind==JsonValueKind.Null?null:Text(e,key);
    static string? Map(string tool,string? rating)=>rating==null?null:tool=="vibe-trading"?rating is "BUY" or "WAIT" or "SELL"?rating:throw new InvalidDataException("ADVISORY_RATING"):rating switch{"Buy" or "Overweight"=>"BUY","Hold"=>"WAIT","Underweight" or "Sell"=>"SELL",_=>throw new InvalidDataException("ADVISORY_RATING")};
    internal static OkxAdvisorySnapshot Read(Func<string,byte[]> read,string instrument,string digest,DateTimeOffset cutoff,DateTimeOffset now)
    {
        Require(OkxAdvisoryComparison.InstrumentAllowed(instrument)&&Hex(digest,64)&&cutoff==new DateTimeOffset(now.UtcDateTime.Date,TimeSpan.Zero)&&now<cutoff.AddDays(1));
        string stamp(DateTimeOffset d)=>d.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'",CultureInfo.InvariantCulture);
        using var pd=Document(read("CURRENT.json"),4096);var p=pd.RootElement;Fields(p,"Schema Generation ManifestSha256");
        string gen=Text(p,"Generation"),mh=Text(p,"ManifestSha256");Require(Text(p,"Schema")=="apex-advisory-pointer-v1"&&Hex(gen,32)&&Hex(mh,64));
        string folder="generations/"+gen+"/";var mb=read(folder+"manifest.json");Require(Hash(mb)==mh);
        using var md=Document(mb);var m=md.RootElement;Fields(m,"Schema Generation Instrument DatasetSha256 AsOfUtc ExpiresUtc Inference Tools");
        Require(Text(m,"Schema")=="apex-advisory-pair-v1"&&Text(m,"Generation")==gen&&Text(m,"Instrument")==instrument&&Text(m,"DatasetSha256")==digest&&Text(m,"AsOfUtc")==stamp(cutoff)&&Text(m,"ExpiresUtc")==stamp(cutoff.AddDays(1)));
        string inference=Text(m,"Inference");Require(inference is "genuine" or "fixture");var tools=m.GetProperty("Tools");Fields(tools,"vibe-trading tradingagents");
        var reportBytes=new List<byte[]>();
        OkxAdvisoryTool Tool(string tool)
        {
            var row=tools.GetProperty(tool);Fields(row,"ReportSha256 NativeSha256 OriginalRecommendation MappedRecommendation State");
            string rh=Text(row,"ReportSha256"),nh=Text(row,"NativeSha256"),state=Text(row,"State");Require(Hex(rh,64)&&Hex(nh,64)&&state is "completed" or "failed");
            var rb=read(folder+tool+".json");var nb=read(folder+tool+"-native.json");Require(Hash(rb)==rh&&Hash(nb)==nh);
            using var rd=Document(rb);using var nd=Document(nb,1048576);var r=rd.RootElement;var n=nd.RootElement;Fields(n,"native error");
            string? original=Nullable(row,"OriginalRecommendation"),mapped=Nullable(row,"MappedRecommendation");Require(Map(tool,original)==mapped);
            Require(Text(r,"Tool")==tool&&Text(r,"Instrument")==instrument&&Text(r,"DatasetSha256")==digest&&Text(r,"AsOfUtc")==stamp(cutoff)&&Text(r,"ExpiresUtc")==stamp(cutoff.AddDays(1)));
            var urls=r.GetProperty("EvidenceSources");Require(urls.ValueKind==JsonValueKind.Array&&urls.GetArrayLength()==1&&urls[0].GetString()=="https://eea.okx.com/api/v5/market/candles");
            if(state=="completed")
            {
                Require(n.GetProperty("error").ValueKind==JsonValueKind.Null&&original!=null);
                var native=n.GetProperty("native");Fields(native,"native_rating recommendation summary module orchestration_module tool_calls DatasetSha256");
                Require(Text(native,"module").Length is >0 and <=24000&&Text(native,"orchestration_module").Length is >0 and <=24000&&Text(native,"native_rating")==original&&Text(native,"recommendation")==mapped&&Text(native,"DatasetSha256")==digest&&Text(native,"summary").Length is >0 and <=16000&&native.GetProperty("tool_calls").GetInt32()>=2);
                Require(Text(r,"Status")== (inference=="genuine"?"ok":"unavailable")&&Text(r,"Recommendation")== (inference=="genuine"?mapped:"WAIT"));
            }
            else Require(n.GetProperty("native").ValueKind==JsonValueKind.Null&&n.GetProperty("error").ValueKind==JsonValueKind.String&&original==null&&mapped==null&&Text(r,"Status")=="unavailable"&&Text(r,"Recommendation")=="WAIT");
            reportBytes.Add(rb);return new(tool,original,mapped,state,rh,nh);
        }
        var vibe=Tool("vibe-trading");var agents=Tool("tradingagents");
        var compatibility=OkxAdvisoryComparison.CompareForInstrument(reportBytes[0],reportBytes[1],now,instrument);
        // The unchanged closed v1 validator remains authoritative even for partial generations.
        Require(compatibility.Reason is "COMPATIBLE_PAIR" or "TOOL_UNAVAILABLE");
        return new(instrument,digest,cutoff,cutoff.AddDays(1),gen,mh,inference,vibe,agents);
    }
}
