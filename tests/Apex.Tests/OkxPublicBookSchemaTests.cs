using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.Tests;

public sealed class OkxPublicBookSchemaTests
{
    private const string Good="{\"code\":\"0\",\"data\":[{\"asks\":[[\"110.00\",\"100.0000\",\"0\",\"1\"]],\"bids\":[[\"109.99\",\"100\",\"0\",\"1\"]],\"ts\":\"1793777345208\"}]}";
    private static NavalInstrument Spec(string instrument="ADA-USDC")=>new(instrument,"ADA","USDC",.0001m,.0001m,.01m);
    private sealed class Clock:TimeProvider
    {
        public DateTimeOffset Now=DateTimeOffset.FromUnixTimeMilliseconds(1793777345308);
        public override DateTimeOffset GetUtcNow()=>Now;
        public override long GetTimestamp()=>100;
    }
    internal sealed class Peer(string body):HttpMessageHandler
    {
        internal int Books;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get,r.Method);Assert.Null(r.Content);Assert.Empty(r.Headers.UserAgent);
            Assert.Equal("eea.okx.com",r.RequestUri!.Host);Assert.Equal("/api/v5/market/books",r.RequestUri.AbsolutePath);
            Assert.Equal("?instId=ADA-USDC&sz=1",r.RequestUri.Query);Assert.Equal("1",r.Headers.GetValues("x-simulated-trading").Single());
            Assert.DoesNotContain(r.Headers,h=>h.Key.StartsWith("OK-ACCESS-",StringComparison.OrdinalIgnoreCase));Books++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body)});
        }
    }
    private static string Mutate(string name)
    {
        var root=JsonNode.Parse(Good)!;var data=(JsonArray)root["data"]!;var row=data[0]!;
        switch(name)
        {
            case "data-empty":data.Clear();break;
            case "data-two":data.Add(row.DeepClone());break;
            case "row-null":data[0]=null;break;
            case "row-array":data[0]=new JsonArray();break;
            case "ask-missing":row.AsObject().Remove("asks");break;
            case "bid-missing":row.AsObject().Remove("bids");break;
            case "ask-kind":row["asks"]=new JsonObject();break;
            case "bid-kind":row["bids"]=null;break;
            case "ask-empty":row["asks"]=new JsonArray();break;
            case "bid-empty":row["bids"]=new JsonArray();break;
            case "ask-two":row["asks"]!.AsArray().Add(row["asks"]![0]!.DeepClone());break;
            case "bid-two":row["bids"]!.AsArray().Add(row["bids"]![0]!.DeepClone());break;
            case "ask-level-kind":row["asks"]![0]="PRIVATE_BODY_SENTINEL_MUST_NOT_ESCAPE";break;
            case "bid-level-kind":row["bids"]![0]=null;break;
            case "ask-fields":row["asks"]![0]!.AsArray().RemoveAt(3);break;
            case "bid-fields":row["bids"]![0]!.AsArray().Add("5");break;
            case "ts-missing":row.AsObject().Remove("ts");break;
            case "ts-kind":row["ts"]=123;break;
            case "ts-null":row["ts"]=null;break;
            case "ts-format":row["ts"]="PRIVATE_BODY_SENTINEL_MUST_NOT_ESCAPE";break;
            case "ts-overflow":row["ts"]="999999999999999999999999999";break;
            case "ts-range":row["ts"]="9223372036854775807";break;
            case "bid-number-kind":row["bids"]![0]![0]=1;break;
            case "ask-number-null":row["asks"]![0]![0]=null;break;
            case "size-number-null":row["asks"]![0]![1]=null;break;
            case "number-empty":row["asks"]![0]![0]="";break;
            case "number-exponent":row["asks"]![0]![0]="1e2";break;
            case "number-sign":row["asks"]![0]![0]="-1";break;
            case "number-space":row["asks"]![0]![0]=" 1";break;
            case "number-char":row["asks"]![0]![0]="PRIVATE_BODY_SENTINEL_MUST_NOT_ESCAPE";break;
            case "number-leading-zero":row["asks"]![0]![0]="01";break;
            case "number-scale":row["asks"]![0]![0]="0.12345678901234567890123456789";break;
            case "number-precision":row["asks"]![0]![0]="1.1234567890123456789012345678";break;
            case "number-range":row["asks"]![0]![0]="9999999999999999999999999999.9";break;
            default:throw new Exception("FIXTURE_UNKNOWN");
        }
        return root.ToJsonString();
    }
    public static IEnumerable<object[]> Failures()
    {
        foreach(var x in new[]{
            ("data-empty","BOOK_CARDINALITY","$.data"),("data-two","BOOK_CARDINALITY","$.data"),
            ("row-null","JSON_KIND","$.data[0]"),("row-array","JSON_KIND","$.data[0]"),
            ("ask-missing","PROPERTY_MISSING","$.data[0].asks"),("bid-missing","PROPERTY_MISSING","$.data[0].bids"),
            ("ask-kind","JSON_KIND","$.data[0].asks"),("bid-kind","JSON_KIND","$.data[0].bids"),
            ("ask-empty","BOOK_DEPTH","$.data[0].asks"),("bid-empty","BOOK_DEPTH","$.data[0].bids"),
            ("ask-two","BOOK_DEPTH","$.data[0].asks"),("bid-two","BOOK_DEPTH","$.data[0].bids"),
            ("ask-level-kind","JSON_KIND","$.data[0].asks[0]"),("bid-level-kind","JSON_KIND","$.data[0].bids[0]"),
            ("ask-fields","BOOK_FIELDS","$.data[0].asks[0]"),("bid-fields","BOOK_FIELDS","$.data[0].bids[0]"),
            ("ts-missing","PROPERTY_MISSING","$.data[0].ts"),("ts-kind","JSON_KIND","$.data[0].ts"),("ts-null","JSON_KIND","$.data[0].ts"),
            ("ts-format","TIMESTAMP_FORMAT","$.data[0].ts"),("ts-overflow","TIMESTAMP_OVERFLOW","$.data[0].ts"),("ts-range","TIMESTAMP_RANGE","$.data[0].ts"),
            ("bid-number-kind","JSON_KIND","$.data[0].bids[0][0]"),("ask-number-null","NUMBER_NULL","$.data[0].asks[0][0]"),("size-number-null","NUMBER_NULL","$.data[0].asks[0][1]"),
            ("number-empty","NUMBER_LEXICAL","$.data[0].asks[0][0]"),("number-exponent","NUMBER_LEXICAL","$.data[0].asks[0][0]"),
            ("number-sign","NUMBER_LEXICAL","$.data[0].asks[0][0]"),("number-space","NUMBER_LEXICAL","$.data[0].asks[0][0]"),
            ("number-char","NUMBER_LEXICAL","$.data[0].asks[0][0]"),("number-leading-zero","NUMBER_LEXICAL","$.data[0].asks[0][0]"),
            ("number-scale","NUMBER_LEXICAL","$.data[0].asks[0][0]"),("number-precision","NUMBER_PRECISION","$.data[0].asks[0][0]")})yield return new object[]{x.Item1,x.Item2,x.Item3};
    }
    [Theory,MemberData(nameof(Failures))]
    public async Task Every_reached_deep_failure_retains_closed_operands(string name,string reason,string path)
    {
        using var peer=new Peer(Mutate(name));using var feed=new OkxPublicMarketFeed(new Clock(),peer);
        var e=await Record.ExceptionAsync(()=>feed.ReadQuoteAsync(Spec(),default));Assert.NotNull(e);Assert.Equal(1,peer.Books);
        using var doc=JsonDocument.Parse(JsonSerializer.Serialize(feed.LastDiagnostic));var d=doc.RootElement;
        Assert.True(d.TryGetProperty("QuoteParser",out var p)&&p.ValueKind==JsonValueKind.Object,"DEEP_QUOTE_OPERAND_LOST");
        Assert.Equal("PAYLOAD_OBSERVED",d.GetProperty("Reason").GetString());Assert.Equal(200,d.GetProperty("HttpStatus").GetInt32());Assert.Equal(0,d.GetProperty("Code").GetInt32());
        Assert.Equal("ADA-USDC",d.GetProperty("Instrument").GetString());Assert.Equal(reason,p.GetProperty("Reason").GetString());Assert.Equal(path,p.GetProperty("Path").GetString());
        Assert.NotNull(p.GetProperty("RequestUtc").GetString());Assert.NotNull(p.GetProperty("ReceivedUtc").GetString());
        if(name=="ask-empty"){Assert.Equal(1,p.GetProperty("DataRows").GetInt32());Assert.Equal(0,p.GetProperty("AskRows").GetInt32());Assert.Equal(1,p.GetProperty("BidRows").GetInt32());Assert.Equal("Array",p.GetProperty("ExpectedKind").GetString());Assert.Equal("Array",p.GetProperty("ActualKind").GetString());Assert.Equal(1,p.GetProperty("ExpectedCount").GetInt32());Assert.Equal(0,p.GetProperty("ActualCount").GetInt32());}
        if(name=="number-precision"){Assert.Equal("PLAIN_DECIMAL",p.GetProperty("NumberClass").GetString());Assert.Equal(29,p.GetProperty("SignificantDigits").GetInt32());Assert.Equal(28,p.GetProperty("FractionDigits").GetInt32());}
        if(name=="number-exponent")Assert.Equal("EXPONENT",p.GetProperty("NumberClass").GetString());
        Assert.DoesNotContain("PRIVATE_BODY_SENTINEL_MUST_NOT_ESCAPE",doc.RootElement.GetRawText());
    }
    [Theory]
    [InlineData("ask-missing",1,null)]
    [InlineData("bid-missing",null,1)]
    public async Task Missing_property_still_retains_all_other_observed_depth_counts(string name,int? bidRows,int? askRows)
    {
        using var feed=new OkxPublicMarketFeed(new Clock(),new Peer(Mutate(name)));Assert.NotNull(await Record.ExceptionAsync(()=>feed.ReadQuoteAsync(Spec(),default)));
        using var d=JsonDocument.Parse(JsonSerializer.Serialize(feed.LastDiagnostic));var p=d.RootElement.GetProperty("QuoteParser");
        Assert.Equal(bidRows,p.GetProperty("BidRows").Deserialize<int?>());Assert.Equal(askRows,p.GetProperty("AskRows").Deserialize<int?>());
    }
    [Fact]
    public async Task Source_regression_retains_original_and_previous_source_clocks()
    {
        var clock=new Clock();using var feed=new OkxPublicMarketFeed(clock,new Peer(Good));await feed.ReadQuoteAsync(Spec(),default);
        // A separate instrument never aliases the monotonic source cache.
        var q=await feed.ReadQuoteAsync(Spec(),default);Assert.Equal(109.99m,q.Bid);
        clock.Now=clock.Now.AddSeconds(1);
        // Mutable response exercises the actual same-feed regression boundary.
        using var peer=new SequencePeer(Good,Good.Replace("1793777345208","1793777345207"));using var other=new OkxPublicMarketFeed(clock,peer);
        await other.ReadQuoteAsync(Spec(),default);await Assert.ThrowsAsync<InvalidDataException>(()=>other.ReadQuoteAsync(Spec(),default));
        using var d=JsonDocument.Parse(JsonSerializer.Serialize(other.LastDiagnostic));var p=d.RootElement.GetProperty("QuoteParser");Assert.Equal("QUOTE_SOURCE_REGRESSION",p.GetProperty("Reason").GetString());Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1793777345208),p.GetProperty("PreviousSourceUtc").GetDateTimeOffset());Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1793777345207),p.GetProperty("SourceUtc").GetDateTimeOffset());
    }
    private sealed class SequencePeer(string first,string second):HttpMessageHandler
    {private int count;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(++count==1?first:second)});}
    [Theory]
    [InlineData("ADA-USDC?credential=PRIVATE_BODY_SENTINEL_MUST_NOT_ESCAPE")]
    [InlineData("../ADA-USDC")]
    public async Task Invalid_instrument_never_reaches_public_transport_or_diagnostic(string instrument)
    {using var peer=new Peer(Good);using var feed=new OkxPublicMarketFeed(new Clock(),peer);await Assert.ThrowsAsync<InvalidDataException>(()=>feed.ReadQuoteAsync(Spec(instrument),default));Assert.Equal(0,peer.Books);Assert.DoesNotContain("PRIVATE_BODY_SENTINEL",JsonSerializer.Serialize(feed.LastDiagnostic));}
    internal static HttpMessageHandler NativePeer(string action)=>new Peer(Mutate(action=="book-empty"?"ask-empty":"number-exponent"));
    internal static void AssertNative(string path,string action,int code,int books,int sends,string[] privatePaths)
    {
        OkxFinitePublicFailureTests.AssertReport(path,"public-depth",code,books,sends,privatePaths);
        using var d=JsonDocument.Parse(File.ReadAllBytes(path));var f=d.RootElement.GetProperty("selection").GetProperty("boundary").GetProperty("PublicFeed");
        Assert.True(f.TryGetProperty("QuoteParser",out var p)&&p.ValueKind==JsonValueKind.Object,"DEEP_QUOTE_OPERAND_LOST_WITH_NULL_OWNER");Assert.Equal("ADA-USDC",f.GetProperty("Instrument").GetString());Assert.Equal(action=="book-empty"?"BOOK_DEPTH":"NUMBER_LEXICAL",p.GetProperty("Reason").GetString());
        Assert.Equal(action=="book-empty"?"$.data[0].asks":"$.data[0].asks[0][0]",p.GetProperty("Path").GetString());
    }
}
