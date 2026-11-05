using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Apex.Adapters.Okx.Execution;
using Xunit;
namespace Apex.Adapters.Okx.Tests;

public sealed class OkxCanonicalDigestTests
{
    // Original F1 operands, not shortened or reordered to hide intermediate writer growth.
    internal static string ReviewerRow=>"{\"billId\":\"1\",\"ccy\":\"EUR\",\"type\":\""+new string('z',20000)+"\",\"subType\":\"\",\"notes\":\"REVIEW_PRIVATE_SENTINEL\"}";
    internal static string ExpansionRow=>"{\"billId\":\"1\",\"ccy\":\"EUR\",\"type\":\"\",\"subType\":\"\",\"notes\":\""+new string('\u2028',180000)+"\"}";

    [Theory][InlineData("review")][InlineData("expansion")][InlineData("unicode")][InlineData("nested")]
    public void CanonicalDigest_ProductionDllMatchesIndependentBclCanonicalBytes(string fixture)
    {
        // Catches canonical byte drift, especially relaxed escaping, supplementary characters,
        // ordinal property ordering, wire array order and empty/present distinctions.
        string row=fixture switch
        {
            "review"=>ReviewerRow,"expansion"=>ExpansionRow,
            "unicode"=>JsonSerializer.Serialize(new Dictionary<string,string>{{"notes",UnicodeCorpus()},{"type",""}}),
            _=>"{\"z\":[{\"b\":\"\\u2028\",\"a\":\"\\\"\\\\\\n\"},[],{}],\"A\":\"é😀\",\"a\":\"\"}"
        };
        using var document=JsonDocument.Parse(row);
        Assert.Equal(Oracle(document.RootElement),Digest(document.RootElement));
    }

    [Fact]
    public async Task CanonicalDigest_OriginalReviewerRowPassesRealSignedReader()
    {
        using var f=new OkxExecutionObservationHttpFixture();await using var c=f.Client();
        f.Steps.Enqueue(("/api/v5/account/bills?begin=1793448000000&end=1793534400000&limit=100",_=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {Content=new StringContent("{\"code\":\"0\",\"msg\":\"\",\"data\":["+ReviewerRow+"]}",Encoding.UTF8,"application/json")})));
        var result=await c.ReadBillsAsync(OkxObservationWindow.Recent,new(null,null,DateTimeOffset.FromUnixTimeMilliseconds(1793448000000),DateTimeOffset.FromUnixTimeMilliseconds(1793534400000),null),f.Context,default);
        Assert.Equal(OkxObservationStatus.Observed,result.Status);var row=Assert.Single(result.Rows);
        using var document=JsonDocument.Parse(ReviewerRow);
        Assert.Equal(Oracle(document.RootElement),row.PayloadDigest);f.Done();
    }

    private static string UnicodeCorpus()
    {
        var b=new StringBuilder();
        for(int i=0;i<=char.MaxValue;i++)if(!char.IsSurrogate((char)i))b.Append((char)i);
        for(int i=0x10000;i<=0x10ffff;i+=257)b.Append(char.ConvertFromUtf32(i));
        return b.ToString();
    }
    private static string Digest(JsonElement row)=>(string)typeof(OkxExecutionObservationClient).Assembly
        .GetType("Apex.Adapters.Okx.Execution.OkxObservationJson")!
        .GetMethod("PayloadDigest",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[row])!;
    private static string Oracle(JsonElement row)
    {
        using var stream=new MemoryStream();
        using(var writer=new Utf8JsonWriter(stream,new JsonWriterOptions {Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping}))Write(row,writer);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
        static void Write(JsonElement e,Utf8JsonWriter w)
        {
            if(e.ValueKind==JsonValueKind.Object){w.WriteStartObject();foreach(var p in e.EnumerateObject().OrderBy(p=>p.Name,StringComparer.Ordinal)){w.WritePropertyName(p.Name);Write(p.Value,w);}w.WriteEndObject();}
            else if(e.ValueKind==JsonValueKind.Array){w.WriteStartArray();foreach(var v in e.EnumerateArray())Write(v,w);w.WriteEndArray();}
            else w.WriteStringValue(e.GetString());
        }
    }
}
