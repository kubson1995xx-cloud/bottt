using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Apex.Desktop.Okx;

// A descriptive research projection. Not consumed by any execution or strategy owner.
internal sealed record OkxAdvisoryStatus(string Comparison,string PlannedEntryFilter,string Reason)
{
    public string Instrument {get;init;}="";
    public string Mode => "SHADOW_ONLY";
    public bool AutomaticActivation => false;
    public bool ExecutionAllowed => false;
    public bool BlocksMandatoryExit => false;
}

internal static class OkxAdvisoryComparison
{
    internal const int MaximumBytes=16384;
    private static readonly HashSet<string> Fields=new(StringComparer.Ordinal){"Schema","Status","Tool","Instrument","QuoteCurrency","AsOfUtc","ExpiresUtc","DatasetSha256","Provider","Model","Recommendation","ExecutionAllowed","EvidenceSources","Summary","SourceCommit"};
    private static readonly Regex Utc=new(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?Z\z",RegexOptions.CultureInvariant|RegexOptions.NonBacktracking);
    private static readonly Regex Hash=new(@"\A[0-9a-f]{64}\z",RegexOptions.CultureInvariant|RegexOptions.NonBacktracking);
    private sealed record Report(string Status,string Recommendation,string DatasetSha256,DateTimeOffset AsOfUtc);
    internal static OkxAdvisoryStatus Unavailable(string reason)=>new("UNAVAILABLE","WOULD_VETO_ENTRY",reason);
    internal static bool InstrumentAllowed(string instrument)=>instrument is "BTC-EUR" or "ETH-EUR" or "SOL-EUR";
    private static Report? Parse(byte[] bytes,string tool,DateTimeOffset now,string instrument)
    {
        if(bytes.Length is 0 or >MaximumBytes)return null;
        try
        {
            using var document=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=4});
            var root=document.RootElement;if(root.ValueKind!=JsonValueKind.Object)return null;
            var found=new HashSet<string>(StringComparer.Ordinal);
            foreach(var property in root.EnumerateObject())
            {
                if(!Fields.Contains(property.Name)||!found.Add(property.Name))return null;
                if(property.Name=="ExecutionAllowed"){if(property.Value.ValueKind!=JsonValueKind.False)return null;}
                else if(property.Name=="EvidenceSources"){if(property.Value.ValueKind!=JsonValueKind.Array)return null;}
                else if(property.Value.ValueKind!=JsonValueKind.String)return null;
            }
            if(found.Count!=Fields.Count)return null;
            string Text(string name)=>root.GetProperty(name).GetString()!;
            if(Text("Schema")!="apex-advisory-v1"||Text("Status") is not ("ok" or "unavailable")||Text("Tool")!=tool||
                Text("Instrument")!=instrument||Text("QuoteCurrency")!="EUR"||Text("Provider")!="openai-codex"||Text("Model")!="gpt-6.1-sol"||
                Text("SourceCommit")!=(tool=="vibe-trading"?"8172827ec537146920e112a9952e8e7e063e218b":"9fa1196fffbcfd9de5146d2dd2869df84ab99838")||
                !Hash.IsMatch(Text("DatasetSha256"))||Text("Recommendation") is not ("BUY" or "SELL" or "WAIT"))return null;
            bool Clock(string value,out DateTimeOffset clock)
            {clock=default;return Utc.IsMatch(value)&&DateTimeOffset.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out clock);}
            // DAILY_SHADOW research only: completed D1 candles are usable during
            // their current UTC day, never as executable quotes or protection proof.
            if(!Clock(Text("AsOfUtc"),out var asOf)||!Clock(Text("ExpiresUtc"),out var expires)||
                asOf!=new DateTimeOffset(now.UtcDateTime.Date,TimeSpan.Zero)||expires!=asOf.AddDays(1)||expires<=now)return null;
            string summary=Text("Summary");if(summary.Length>1000||summary.Any(char.IsControl))return null;
            var urls=root.GetProperty("EvidenceSources");if(urls.GetArrayLength()>8)return null;
            foreach(var value in urls.EnumerateArray())
            {
                if(value.ValueKind!=JsonValueKind.String)return null;
                string url=value.GetString()!;
                if(url.Length is 0 or >1024||url.Any(char.IsWhiteSpace)||url.Any(char.IsControl)||
                    !Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.Host.Length==0||uri.UserInfo.Length!=0||uri.Query.Length!=0||uri.Fragment.Length!=0)return null;
            }
            return new(Text("Status"),Text("Recommendation"),Text("DatasetSha256"),asOf);
        }
        catch(JsonException){return null;}
    }
    internal static OkxAdvisoryStatus Compare(byte[] vibe,byte[] agents,DateTimeOffset now)
        =>CompareForInstrument(vibe,agents,now,"SOL-EUR");
    internal static OkxAdvisoryStatus CompareForInstrument(byte[] vibe,byte[] agents,DateTimeOffset now,string instrument)
    {
        if(!InstrumentAllowed(instrument))return Unavailable("INVALID_INSTRUMENT");
        OkxAdvisoryStatus Refuse(string reason)=>Unavailable(reason) with{Instrument=instrument};
        var left=Parse(vibe,"vibe-trading",now,instrument);if(left==null)return Refuse("INVALID_VIBE");
        var right=Parse(agents,"tradingagents",now,instrument);if(right==null)return Refuse("INVALID_TRADINGAGENTS");
        if(left.Status!="ok"||right.Status!="ok")return Refuse("TOOL_UNAVAILABLE");
        if(left.DatasetSha256!=right.DatasetSha256)return Refuse("PAIR_DATASET_MISMATCH");
        if((left.AsOfUtc-right.AsOfUtc).Duration()>TimeSpan.FromSeconds(60))return Refuse("PAIR_CLOCK_MISMATCH");
        string a=left.Recommendation,b=right.Recommendation;
        return new(a==b?"AGREE_"+a:"DISAGREE",a=="BUY"&&b=="BUY"?"WOULD_ALLOW_ENTRY":"WOULD_VETO_ENTRY","COMPATIBLE_PAIR"){Instrument=instrument};
    }
}
