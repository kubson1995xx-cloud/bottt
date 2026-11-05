using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.Tests;

public sealed class OkxFinitePublicFailureTests
{
    // Original tester intentionally consumes only existing APIs and JSON, so the
    // SAME executable/operands can run against retained old and final closures.
    internal sealed class PublicPeer(HttpMessageHandler valid,string scenario) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker fallback=new(valid,false);
        internal int Books;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get,r.Method);Assert.Null(r.Content);
            Assert.Equal("https",r.RequestUri!.Scheme);Assert.Equal("eea.okx.com",r.RequestUri.Host);
            Assert.Equal("1",r.Headers.GetValues("x-simulated-trading").Single());
            Assert.DoesNotContain(r.Headers,h=>h.Key.StartsWith("OK-ACCESS-",StringComparison.OrdinalIgnoreCase));
            Assert.Equal("/api/v5/market/books",r.RequestUri.AbsolutePath);Books++;
            Assert.Equal(1,Books); // First uncertain public failure must halt, not skip asset.
            if(scenario=="public-transport")throw new HttpRequestException("PRIVATE_EXCEPTION_SENTINEL_MUST_NOT_ESCAPE");
            if(scenario=="public-cancel")throw new OperationCanceledException("PRIVATE_EXCEPTION_SENTINEL_MUST_NOT_ESCAPE");
            if(scenario=="public-valid")return fallback.SendAsync(r,ct);
            string body=scenario switch {
                // Exact observed public 51001 shape; prices are never fabricated as a remedy.
                "public-code"=>"{\"code\":\"51001\",\"msg\":\"Instrument ID does not exist.\"}",
                "public-json"=>"PRIVATE_BODY_SENTINEL_MUST_NOT_ESCAPE",
                "public-depth"=>"{\"code\":\"0\",\"data\":[{\"asks\":[],\"bids\":[],\"ts\":\"1\"}]}",
                "public-duplicate"=>"{\"code\":\"51001\",\"Code\":\"PRIVATE_BODY_SENTINEL_MUST_NOT_ESCAPE\",\"data\":[]}",
                "public-text-code"=>"{\"code\":\"PRIVATE_BODY_SENTINEL_MUST_NOT_ESCAPE\",\"data\":[]}",
                _=>"PRIVATE_BODY_SENTINEL_MUST_NOT_ESCAPE"};
            return Task.FromResult(new HttpResponseMessage(scenario=="public-http"?HttpStatusCode.ServiceUnavailable:HttpStatusCode.OK){Content=new StringContent(body)});
        }
        protected override void Dispose(bool disposing){if(disposing)fallback.Dispose();base.Dispose(disposing);}
    }
    internal static void AssertReport(string path,string scenario,int code,int books,int sends,string[] privatePaths)
    {
        Assert.Equal(scenario=="public-cancel"?6:7,code);Assert.Equal(1,books);Assert.Equal(0,sends);
        Assert.Equal(new[]{"/api/v5/account/config","/api/v5/account/balance","/api/v5/account/interest-limits?type=2","/api/v5/account/instruments?instType=SPOT","/api/v5/account/trade-fee?instId=ADA-USDC&instType=SPOT"},privatePaths.TakeLast(5));
        using var doc=JsonDocument.Parse(File.ReadAllBytes(path));var r=doc.RootElement;
        Assert.Equal("STOP",r.GetProperty("mode").GetString());Assert.True(r.GetProperty("complete").GetBoolean());
        Assert.False(r.GetProperty("armed").GetBoolean());Assert.False(r.GetProperty("schedulerStarted").GetBoolean());
        foreach(string n in new[]{"baseline","orders","fills","intents","reservations","transport"})Assert.Equal(JsonValueKind.Null,r.GetProperty(n).ValueKind);
        Assert.Contains("CONNECTED_READ_ONLY_STOP",r.GetProperty("discoveryDiagnostic").GetRawText());
        var selection=r.GetProperty("selection");Assert.Equal(5,selection.GetProperty("reads").GetArrayLength());
        Assert.True(selection.TryGetProperty("boundary",out var boundary),"POST_FEE_PUBLIC_FAILURE_DIAGNOSTIC_LOST_WITH_NULL_OWNER");
        Assert.Equal("PUBLIC_BOOK",boundary.GetProperty("Stage").GetString());
        string reason=scenario switch{"public-code"=>"PUBLIC_PROVIDER_REJECTED","public-http"=>"PUBLIC_HTTP_REJECTED","public-json" or "public-duplicate" or "public-text-code"=>"PUBLIC_PAYLOAD_INVALID","public-depth"=>"PUBLIC_SCHEMA_INVALID","public-cancel"=>"CANCELLED",_=>"PUBLIC_TRANSPORT_FAILURE"};
        Assert.Equal(reason,boundary.GetProperty("Reason").GetString());
        var feed=boundary.GetProperty("PublicFeed");Assert.Equal("/api/v5/market/books",feed.GetProperty("Endpoint").GetString());
        int? expectedHttp=scenario is "public-transport" or "public-cancel"?null:scenario=="public-http"?503:200;
        int? expectedCode=scenario=="public-code"?51001:scenario=="public-depth"?0:null;
        Assert.Equal(expectedHttp,feed.GetProperty("HttpStatus").Deserialize<int?>());Assert.Equal(expectedCode,feed.GetProperty("Code").Deserialize<int?>());
        string text=r.GetRawText();foreach(string needle in new[]{"PRIVATE_EXCEPTION_SENTINEL_MUST_NOT_ESCAPE","PRIVATE_BODY_SENTINEL_MUST_NOT_ESCAPE","Instrument ID does not exist"})Assert.DoesNotContain(needle,text);
        string root=Path.GetDirectoryName(path)!;Assert.Empty(Directory.EnumerateFiles(root,"naval_authority_broker_v2.json",SearchOption.AllDirectories));Assert.Empty(Directory.EnumerateFiles(root,".naval-broker-initialized-v2",SearchOption.AllDirectories));
    }
    [Fact]
    public async Task Valid_book_preserves_original_prices_depth_and_source()
    {
        await using var owner=new OperationalPeer(finite:true);using var peer=new FiniteDemoPeer(owner){BaseCurrency="ADA"};
        using var feed=new OkxPublicMarketFeed(owner.Clock,peer);var i=new NavalInstrument("ADA-USDC","ADA","USDC",.0001m,.0001m,.01m);
        var q=await feed.ReadQuoteAsync(i,default);Assert.Equal(109.99m,q.Bid);Assert.Equal(110m,q.Ask);Assert.Equal(100m,q.AskQuantity);Assert.True(q.SourceUtc<=q.ReceivedUtc);Assert.True(q.ReceivedUtc-q.SourceUtc<TimeSpan.FromSeconds(1));
    }
}
