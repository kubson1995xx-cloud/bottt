using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Apex.Adapters.Okx;
using Apex.Adapters.Okx.Execution;
using Apex.Okx.DemoAcceptance;
using Xunit;

namespace Apex.Tests;

// Local policy oracle: exact-instrument-empty-policy-ruling.md, NOT an asserted
// provider account/instruments schema. Public vocabulary: OKX toolkit e5b60e4f,
// event-helpers.ts BrowseSeriesResult / event-series (different endpoint).
// Removing exact empty support breaks positives; relaxing content/type/SPOT or
// unknown checks breaks refusals. Every input is synthetic, never captured wire.
public sealed class OkxExactInstrumentCompatibilityTests
{
    public OkxExactInstrumentCompatibilityTests() => _ = new OkxBalanceSchemaDiagnosticTests();
    private const string Endpoint="/api/v5/account/instruments";
    private const string Private="SYNTHETIC_UNTRUSTED_INSTRUMENT_VALUE";
    internal static JsonObject Row(int mask=3)
    {
        var row=JsonNode.Parse("""
        {"instType":"SPOT","instId":"BTC-USDC","baseCcy":"BTC","quoteCcy":"USDT","tradeQuoteCcyList":["USDC"],"groupId":"7","state":"live","tickSz":"0.0100","lotSz":"0.00001000","minSz":"0.00010000","listTime":"1700000000000","expTime":"","maxLmtSz":"12.3400","maxMktSz":"98.7600","maxLmtAmt":"765.4300","maxMktAmt":"654.3200","maxTwapSz":"11.1100","maxIcebergSz":"22.2200","maxTriggerSz":"33.3300","maxStopSz":"44.4400","maxPlatOILmt":"55.5500","maxPlatOICoinLmt":"66.6600"}
        """)!.AsObject();
        if((mask&1)!=0)row["freq"]="";
        if((mask&2)!=0)row["method"]="";
        return row;
    }
    private static string Wire(JsonObject row)=>new JsonObject{["code"]="0",["msg"]=Private,["data"]=new JsonArray(row)}.ToJsonString();
    private static OkxExecutionInstrumentDto Parse(string wire)=>Assert.Single(OkxObservationJson.Parse<OkxExecutionInstrumentDto>(Encoding.UTF8.GetBytes(wire),Endpoint));
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void Absent_each_empty_and_both_empty_preserve_every_selected_field_and_unit(int mask)
    {
        var a=Parse(Wire(Row(0)));var b=Parse(Wire(Row(mask)));
        Assert.Equal(JsonSerializer.Serialize(a),JsonSerializer.Serialize(b));
        Assert.Equal("USDT",b.CatalogQuote);Assert.Equal(new[]{"USDC"},b.TradeQuoteCurrencies);Assert.Equal("7",b.GroupId);
        Assert.Equal(new OkxDecimal("0.0100",0.0100m),b.TickSize);Assert.Equal("0.00001000",b.LotSize.Raw);Assert.Equal("0.00010000",b.MinimumSize.Raw);
        Assert.Equal("1700000000000",b.ListTime.Raw);Assert.Equal("",b.ExpiryTime.Raw);
        var keys=new[]{"maxLmtSz","maxMktSz","maxLmtAmt","maxMktAmt","maxTwapSz","maxIcebergSz","maxTriggerSz","maxStopSz","maxPlatOILmt","maxPlatOICoinLmt"};
        var raw=new[]{"12.3400","98.7600","765.4300","654.3200","11.1100","22.2200","33.3300","44.4400","55.5500","66.6600"};
        var units=new[]{"base currency","USDT","USD","USD","base currency","base currency","base currency","USDT","USD (derivatives only)","coins (derivatives only)"};
        Assert.Equal(10,b.MaxValues.Count);
        for(int i=0;i<keys.Length;i++){Assert.Equal(raw[i],b.MaxValues[keys[i]].Amount.Raw);Assert.Equal(units[i],b.MaxValues[keys[i]].Unit);}
    }
    [Fact]
    public void Raw_canonical_digest_keeps_absence_each_empty_and_both_empty_distinct()
    {
        var digests=new List<string>();
        for(int mask=0;mask<4;mask++)
        {
            var r=Row(mask);_=Parse(Wire(r.DeepClone().AsObject()));
            using var doc=JsonDocument.Parse(r.ToJsonString());digests.Add(OkxObservationJson.PayloadDigest(doc.RootElement));
        }
        Assert.Equal(4,digests.Distinct(StringComparer.Ordinal).Count());
        var absent=Row(0);using var d=JsonDocument.Parse(absent.ToJsonString());
        Assert.Equal(digests[0],OkxObservationJson.PayloadDigest(d.RootElement));
    }
    public static IEnumerable<object[]> Forbidden()
    {
        foreach(string name in new[]{"freq","method"})
        {
            foreach(string wire in new[]{"null","true","false","0","1.25","[]","{}","[\"\"]","{\"nested\":\"\"}"})yield return new object[]{name,wire,"TYPE"};
            foreach(string value in new[]{" ","\t","0","false","daily","price_up_down","Ω","\u200b","\0",Private})yield return new object[]{name,JsonSerializer.Serialize(value),"INSTRUMENT_EXTENSION_UNSUPPORTED"};
            yield return new object[]{name,"\"\\u0061\"","INSTRUMENT_EXTENSION_UNSUPPORTED"};
        }
    }
    [Theory,MemberData(nameof(Forbidden))]
    public async Task Every_forbidden_content_or_kind_has_no_observation_owner_orders_or_later_dispatch(string name,string wire,string reason)
    {
        var row=Row();row[name]=JsonNode.Parse(wire);
        Assert.Equal(reason,Assert.Throws<OkxObservationJson.PayloadException>(()=>Parse(Wire(row.DeepClone().AsObject()))).Failure.Reason);
        using var peer=new Peer(row);var r=await Discover(peer);
        AssertRefused(r,peer,reason);AssertNoOwnerReport(r);
    }
    [Theory]
    [InlineData("MARGIN")] [InlineData("SWAP")] [InlineData("FUTURES")] [InlineData("OPTION")] [InlineData("EVENTS")] [InlineData("spot")] [InlineData("")]
    public async Task Empty_extensions_on_any_non_exact_SPOT_row_are_refused(string type)
    {
        var row=Row();row["instType"]=type;using var peer=new Peer(row);var r=await Discover(peer);
        AssertRefused(r,peer,"INSTRUMENT_EXTENSION_UNSUPPORTED");AssertNoOwnerReport(r);
    }
    [Fact]
    public async Task Signed_empty_SPOT_extensions_complete_four_GETs_without_owner()
    {
        using var peer=new Peer(Row());var r=await Discover(peer);
        Assert.Equal(4,peer.Count);
        if(r.Observation is null) // record/assert exact old-runtime failure BEFORE the intended RED
        {
            AssertRefused(r,peer,"UNSUPPORTED_PROPERTY");
            Assert.Equal("PARSE_SCHEMA",r.Diagnostic!.Phase);
        }
        Assert.True(r.Observation is not null,"EXACT_EMPTY_SPOT_EXTENSIONS_UNSUPPORTED_AFTER_BALANCE_VALIDATED_FOURTH_GET");
        Assert.Equal("CONNECTED_READ_ONLY_STOP",r.Code);Assert.Null(r.Diagnostic);AssertNoOwnerReport(r);
        Assert.Equal("USDT",Assert.Single(r.Observation!.Instruments).CatalogQuote);
    }
    [Theory]
    [InlineData("Freq","PROPERTY_CASE")] [InlineData("METHOD","PROPERTY_CASE")] [InlineData("syntheticExtra","UNSUPPORTED_PROPERTY")]
    public void Case_and_all_other_unknown_names_remain_refused(string name,string reason)
    {
        var row=Row(0);row[name]="";Assert.Equal(reason,Assert.Throws<OkxObservationJson.PayloadException>(()=>Parse(Wire(row))).Failure.Reason);
    }
    [Theory]
    [InlineData("\"freq\":\"\",\"freq\":\"\"")]
    [InlineData("\"freq\":\"\",\"FREQ\":\"\"")]
    [InlineData("\"freq\":{\"x\":\"a\",\"X\":\"b\"}")]
    public void Recursive_duplicates_precede_extension_compatibility(string replacement)
    {
        string wire=Wire(Row()).Replace("\"freq\":\"\"",replacement,StringComparison.Ordinal);
        Assert.Equal("DUPLICATE_PROPERTY",Assert.Throws<OkxObservationJson.PayloadException>(()=>Parse(wire)).Failure.Reason);
    }
    [Theory]
    [InlineData("tickSz")] [InlineData("lotSz")] [InlineData("minSz")] [InlineData("maxMktSz")] [InlineData("maxLmtAmt")]
    public void Existing_exact_decimal_guards_still_reject_precision_loss(string name)
    {
        var row=Row();row[name]="0.00000000000000000000000000001";
        Assert.Equal("NUMBER_PRECISION",Assert.Throws<OkxObservationJson.PayloadException>(()=>Parse(Wire(row))).Failure.Reason);
    }
    [Fact]
    public void Missing_instType_and_strict_envelope_and_cardinality_still_refuse()
    {
        var row=Row();row.Remove("instType");Assert.Equal("MISSING",Assert.Throws<OkxObservationJson.PayloadException>(()=>Parse(Wire(row))).Failure.Reason);
        string wire=Wire(Row()).Replace("\"code\":\"0\"","\"code\":0",StringComparison.Ordinal);
        Assert.Equal("TYPE",Assert.Throws<OkxObservationJson.PayloadException>(()=>Parse(wire)).Failure.Reason);
        wire="{\"code\":\"0\",\"msg\":\"\",\"data\":["+string.Join(",",Enumerable.Repeat(Row().ToJsonString(),4097))+"]}";
        Assert.Equal("CARDINALITY",Assert.Throws<OkxObservationJson.CardinalityException>(()=>Parse(wire)).Failure!.Reason);
    }
    private static Task<OkxEnrollmentDiscovery> Discover(Peer p)=>OkxEnrollmentDiscoveryClient.RunAsync("Demo","DemoTradingApi",Peer.Key,Peer.Secret,Peer.Pass,null,null,default,p);
    private static void AssertRefused(OkxEnrollmentDiscovery r,Peer p,string reason)
    {
        Assert.Equal("DISCOVERY_INVALID_PAYLOAD",r.Code);Assert.Null(r.Observation);Assert.Equal(4,p.Count);
        var d=r.Diagnostic!;Assert.Equal(201,d.HttpStatus);Assert.Equal("0",d.OkxCode);Assert.Equal(Endpoint+"?instType=SPOT",d.Endpoint);
        Assert.True(d.ConfigAuthenticated&&d.AccountSafetyValidated);Assert.Equal("BALANCE_VALIDATED",d.LastCompletedStage);Assert.Equal(reason,d.Payload!.Reason);
        Assert.DoesNotContain(Private,JsonSerializer.Serialize(d));
    }
    private static void AssertNoOwnerReport(OkxEnrollmentDiscovery r)
    {
        byte[] bytes=TransactionReport.Build("synthetic-instrument",DateTimeOffset.UtcNow,true,r.Observation is null?"READ_ONLY_DISCOVERY_REFUSED_STOP_NO_TRADING":"READ_ONLY_DISCOVERY_COMPLETED_STOP_NO_TRADING",new[]{"STOP"},null,new{r.Code,r.Diagnostic},null,false,null,"CHECK_CONNECTION_GET_ONLY");
        try
        {
            using var d=JsonDocument.Parse(bytes);var root=d.RootElement;Assert.Equal("STOP",root.GetProperty("mode").GetString());Assert.False(root.GetProperty("armed").GetBoolean());Assert.False(root.GetProperty("schedulerStarted").GetBoolean());
            foreach(var n in new[]{"baseline","orders","intents","fills","reservations","transport"})Assert.Equal(JsonValueKind.Null,root.GetProperty(n).ValueKind);
            Assert.DoesNotContain(Private,Encoding.UTF8.GetString(bytes));
        }
        finally{CryptographicOperations.ZeroMemory(bytes);}
    }
    private sealed class Peer(JsonObject instrument):HttpMessageHandler
    {
        internal static readonly byte[] Key=Encoding.ASCII.GetBytes("instrument-synthetic-key"),Secret=Encoding.ASCII.GetBytes("instrument-synthetic-secret"),Pass=Encoding.ASCII.GetBytes("instrument-synthetic-pass");
        internal int Count;
        private static readonly string[] Targets={"/api/v5/public/time","/api/v5/account/config","/api/v5/account/balance",Endpoint+"?instType=SPOT"};
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage q,CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get,q.Method);Assert.Null(q.Content);Assert.Equal("https",q.RequestUri!.Scheme);Assert.Equal("eea.okx.com",q.RequestUri.Host);Assert.Equal(443,q.RequestUri.Port);
            Assert.Equal("1",q.Headers.GetValues("x-simulated-trading").Single());Assert.True(Count<4,"NO_LATER_DISPATCH");string path=q.RequestUri.PathAndQuery;Assert.Equal(Targets[Count++],path);
            if(Count>1)
            {
                string stamp=q.Headers.GetValues("OK-ACCESS-TIMESTAMP").Single();Assert.Equal(Convert.ToBase64String(HMACSHA256.HashData(Secret,Encoding.ASCII.GetBytes(stamp+"GET"+path))),q.Headers.GetValues("OK-ACCESS-SIGN").Single());
                Assert.Equal(Encoding.ASCII.GetString(Key),q.Headers.GetValues("OK-ACCESS-KEY").Single());Assert.Equal(Encoding.ASCII.GetString(Pass),q.Headers.GetValues("OK-ACCESS-PASSPHRASE").Single());
            }
            JsonNode row;
            if(Count==1)row=new JsonObject{["ts"]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()};
            else if(Count==2)row=JsonNode.Parse("""{"uid":"987654321098765432","mainUid":"987654321098765432","acctLv":"1","perm":"read_only,trade","ip":"192.0.2.198","feeType":"0","autoLoan":false,"enableSpotBorrow":false,"spotBorrowAutoRepay":false,"type":"0","spotRoleType":"0","kycLv":"2"}""")!;
            else if(Count==3)
            {
                using var s=typeof(OkxExactInstrumentCompatibilityTests).Assembly.GetManifestResourceStream("Apex.Tests.Fixtures.okx-balance-current.json")!;
                row=JsonNode.Parse(s)!["data"]![0]!.DeepClone();row["details"]![0]!["autoLendAmt"]="0";row["details"]![0]!["autoStakingStatus"]="off";
            }
            else row=instrument.DeepClone();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created){Content=new StringContent(new JsonObject{["code"]="0",["msg"]=Private,["data"]=new JsonArray(row)}.ToJsonString())});
        }
    }
}
