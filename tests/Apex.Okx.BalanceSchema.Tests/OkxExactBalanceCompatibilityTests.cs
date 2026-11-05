using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Apex.Adapters.Okx.Execution;
using Apex.Okx.DemoAcceptance;
using Xunit;

namespace Apex.Tests;

// Independent official OKX toolkit oracle e5b60e4f0bc2afc75fab19c8360dafa6b84f3ed7:
// packages/cli/src/commands/auto-earn.ts and packages/core/src/tools/account.ts.
// Regression: removing either exact field from Shape rejects legitimate inactive balances.
public sealed class OkxExactBalanceCompatibilityTests
{
    public OkxExactBalanceCompatibilityTests() => _ = new OkxBalanceSchemaDiagnosticTests();
    private const string Endpoint="/api/v5/account/balance";
    private static JsonObject Body(bool extensions=true)
    {
        using var stream=typeof(OkxExactBalanceCompatibilityTests).Assembly.GetManifestResourceStream("Apex.Tests.Fixtures.okx-balance-current.json")!;
        var body=JsonNode.Parse(stream)!.AsObject();
        if(extensions){Detail(body)["autoLendAmt"]="0";Detail(body)["autoStakingStatus"]="unsupported";}
        return body;
    }
    private static JsonObject Detail(JsonObject b)=>b["data"]![0]!["details"]![0]!.AsObject();
    private static OkxExecutionBalanceDetailDto Parse(JsonObject b)=>Assert.Single(Assert.Single(OkxObservationJson.Parse<OkxExecutionBalanceDto>(Encoding.UTF8.GetBytes(b.ToJsonString()),Endpoint)).Details);
    [Theory]
    [InlineData("unsupported")]
    [InlineData("off")]
    public void Exact_official_fields_accept_without_rewriting_selected_money(string status)
    {
        var b=Body();Detail(b)["autoStakingStatus"]=status;
        var row=Parse(b);var original=Parse(Body(false));Assert.Equal(original,row);
        Assert.Equal("4850.435693622894",row.Cash.Raw);Assert.Equal(4850.435693622894m,row.Cash.Value);
        Assert.Equal("4834.317093622894",row.Available.Raw);Assert.Equal("4992.890093622894",row.Equity.Raw);
        Assert.Equal(150m,row.StrategyEquity.Value); // original populated official example is not a clean financial account
    }
    [Theory]
    [InlineData("absent-both")]
    [InlineData("absent-amount")]
    [InlineData("absent-status")]
    [InlineData("empty-amount")]
    public void Optional_extensions_preserve_legacy_and_empty_amount_is_not_a_cash_alias(string shape)
    {
        var b=Body();var d=Detail(b);
        if(shape=="absent-both"||shape=="absent-amount")d.Remove("autoLendAmt");
        if(shape=="absent-both"||shape=="absent-status")d.Remove("autoStakingStatus");
        if(shape=="empty-amount")d["autoLendAmt"]="";
        Assert.Equal(Parse(Body(false)),Parse(b));
    }
    public static IEnumerable<object[]> WrongKinds()
    {
        foreach(var n in new[]{"autoLendAmt","autoStakingStatus"})foreach(var value in new[]{"123","false","null","{}","[]"})yield return new object[]{n,value};
    }
    [Theory,MemberData(nameof(WrongKinds))]
    public void Extensions_are_string_only_no_null_or_coercion(string name,string wire)
    {
        var b=Body();Detail(b)[name]=JsonNode.Parse(wire);
        var e=Assert.Throws<OkxObservationJson.PayloadException>(()=>Parse(b));Assert.Equal("TYPE",e.Failure.Reason);Assert.Equal("String",e.Failure.Expected);
        Assert.Equal("$.data[].details[]."+name,e.Failure.Path);
    }
    [Theory]
    [InlineData("autoLendAmt","1","AUTO_EARN_UNSUPPORTED")]
    [InlineData("autoLendAmt","-1","AUTO_EARN_UNSUPPORTED")]
    [InlineData("autoLendAmt","0.0000000000000000000000000001","AUTO_EARN_UNSUPPORTED")]
    [InlineData("autoLendAmt","0.00000000000000000000000000001","NUMBER_PRECISION")]
    [InlineData("autoLendAmt","79228162514264337593543950335.1","NUMBER_PRECISION")]
    [InlineData("autoLendAmt","1e-2","NUMBER")]
    [InlineData("autoLendAmt"," 0","NUMBER")]
    [InlineData("autoStakingStatus","active","AUTO_EARN_UNSUPPORTED")]
    [InlineData("autoStakingStatus","pending","AUTO_EARN_UNSUPPORTED")]
    [InlineData("autoStakingStatus","","AUTO_EARN_STATUS")]
    [InlineData("autoStakingStatus","OFF","AUTO_EARN_STATUS")]
    [InlineData("autoStakingStatus","SYNTHETIC_PRIVATE_EARN_VALUE","AUTO_EARN_STATUS")]
    public void Financial_or_unestablished_extensions_refuse_before_any_projection(string name,string value,string reason)
    {
        var b=Body();Detail(b)[name]=value;
        var e=Assert.Throws<OkxObservationJson.PayloadException>(()=>Parse(b));Assert.Equal(reason,e.Failure.Reason);
        Assert.Equal("$.data[].details[]."+name,e.Failure.Path);Assert.DoesNotContain("SYNTHETIC_PRIVATE_EARN_VALUE",JsonSerializer.Serialize(e.Failure));
    }
    [Theory]
    [InlineData("AutoLendAmt","PROPERTY_CASE")]
    [InlineData("AUTOSTAKINGSTATUS","PROPERTY_CASE")]
    [InlineData("unestablishedBalanceField","UNSUPPORTED_PROPERTY")]
    public void Exact_names_only_no_unknown_acceptance(string name,string reason)
    {
        var b=Body();Detail(b).Remove("autoLendAmt");Detail(b).Remove("autoStakingStatus");Detail(b)[name]="0";
        Assert.Equal(reason,Assert.Throws<OkxObservationJson.PayloadException>(()=>Parse(b)).Failure.Reason);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recursive_duplicate_and_case_duplicate_checks_still_run_first(bool recursive)
    {
        string wire=Body().ToJsonString();
        wire=recursive?wire.Replace("\"autoLendAmt\":\"0\"","\"autoLendAmt\":{\"x\":\"a\",\"X\":\"b\"}"):wire.Replace("\"autoLendAmt\":\"0\"","\"autoLendAmt\":\"0\",\"autoLendAmt\":\"0\"");
        Assert.Equal("DUPLICATE_PROPERTY",Assert.Throws<OkxObservationJson.PayloadException>(()=>OkxObservationJson.Parse<OkxExecutionBalanceDto>(Encoding.UTF8.GetBytes(wire),Endpoint)).Failure.Reason);
    }
    [Fact]
    public void Existing_money_precision_and_detail_cardinality_are_not_relaxed()
    {
        var b=Body();Detail(b)["cashBal"]="0.00000000000000000000000000001";
        Assert.Equal("NUMBER_PRECISION",Assert.Throws<OkxObservationJson.PayloadException>(()=>Parse(b)).Failure.Reason);
        b=Body();var rows=b["data"]![0]!["details"]!.AsArray();for(int i=1;i<257;i++)rows.Add(rows[0]!.DeepClone());
        Assert.Equal("CARDINALITY",Assert.Throws<OkxObservationJson.CardinalityException>(()=>Parse(b)).Failure!.Reason);
    }
    [Theory]
    [InlineData("inactive",true)]
    [InlineData("amount",false)]
    [InlineData("staking",false)]
    [InlineData("empty-status",false)]
    public async Task Signed_discovery_and_real_no_owner_report_follow_exact_extension_contract(string scenario,bool accepted)
    {
        using var peer=new Peer(scenario);var result=await OkxEnrollmentDiscoveryClient.RunAsync("Demo","DemoTradingApi",Peer.Key,Peer.Secret,Peer.Pass,null,null,default,peer);
        Assert.Equal(accepted?"CONNECTED_READ_ONLY_STOP":"DISCOVERY_INVALID_PAYLOAD",result.Code);Assert.Equal(accepted?4:3,peer.Count);
        Assert.Equal(accepted,result.Observation is not null);
        if(!accepted){Assert.Equal(200,result.Diagnostic!.HttpStatus);Assert.Equal("0",result.Diagnostic.OkxCode);Assert.True(result.Diagnostic.ConfigAuthenticated&&result.Diagnostic.AccountSafetyValidated);}
        var bytes=TransactionReport.Build("exact-synthetic",DateTimeOffset.UtcNow,true,accepted?"READ_ONLY_DISCOVERY_COMPLETED_STOP_NO_TRADING":"READ_ONLY_DISCOVERY_REFUSED_STOP_NO_TRADING",new[]{"STOP"},null,new{result.Code,result.Diagnostic},null,false,null,"CHECK_CONNECTION_GET_ONLY");
        try
        {
            using var report=JsonDocument.Parse(bytes);var r=report.RootElement;Assert.Equal("STOP",r.GetProperty("mode").GetString());Assert.False(r.GetProperty("armed").GetBoolean());Assert.False(r.GetProperty("schedulerStarted").GetBoolean());
            foreach(var n in new[]{"baseline","orders","intents","fills","reservations","transport"})Assert.Equal(JsonValueKind.Null,r.GetProperty(n).ValueKind);
            Assert.DoesNotContain("SYNTHETIC_PRIVATE_EARN_VALUE",Encoding.UTF8.GetString(bytes));
        }
        finally{CryptographicOperations.ZeroMemory(bytes);}
    }
    private sealed class Peer(string scenario):HttpMessageHandler
    {
        internal static readonly byte[] Key=Encoding.ASCII.GetBytes("exact-synthetic-key"),Secret=Encoding.ASCII.GetBytes("exact-synthetic-secret"),Pass=Encoding.ASCII.GetBytes("exact-synthetic-pass");
        internal int Count;
        private static readonly string[] Paths={"/api/v5/public/time","/api/v5/account/config",Endpoint,"/api/v5/account/instruments?instType=SPOT"};
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get,request.Method);Assert.Null(request.Content);Assert.Equal("eea.okx.com",request.RequestUri!.Host);Assert.Equal("https",request.RequestUri.Scheme);Assert.Equal("1",request.Headers.GetValues("x-simulated-trading").Single());
            string path=request.RequestUri.PathAndQuery;Assert.Equal(Paths[Count++],path);
            if(Count>1)
            {
                string stamp=request.Headers.GetValues("OK-ACCESS-TIMESTAMP").Single();string expected=Convert.ToBase64String(HMACSHA256.HashData(Secret,Encoding.ASCII.GetBytes(stamp+"GET"+path)));
                Assert.Equal(expected,request.Headers.GetValues("OK-ACCESS-SIGN").Single());Assert.Equal(Encoding.ASCII.GetString(Key),request.Headers.GetValues("OK-ACCESS-KEY").Single());Assert.Equal(Encoding.ASCII.GetString(Pass),request.Headers.GetValues("OK-ACCESS-PASSPHRASE").Single());
            }
            JsonObject b;
            if(Count==3){b=Body();if(scenario=="amount")Detail(b)["autoLendAmt"]="2.75";if(scenario=="staking")Detail(b)["autoStakingStatus"]="active";if(scenario=="empty-status")Detail(b)["autoStakingStatus"]="";}
            else
            {
                JsonNode row=Count==1?new JsonObject{["ts"]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()}:Count==2?JsonNode.Parse("{\"uid\":\"987654321098765432\",\"mainUid\":\"987654321098765432\",\"acctLv\":\"1\",\"perm\":\"read_only,trade\",\"ip\":\"192.0.2.198\",\"feeType\":\"0\",\"autoLoan\":false,\"enableSpotBorrow\":false,\"spotBorrowAutoRepay\":false,\"type\":\"0\",\"spotRoleType\":\"0\",\"kycLv\":\"2\"}")!:JsonNode.Parse("{\"instType\":\"SPOT\",\"instId\":\"BTC-USDC\",\"baseCcy\":\"BTC\",\"quoteCcy\":\"USDC\",\"state\":\"live\",\"tradeQuoteCcyList\":[\"USDC\"]}")!;
                b=new JsonObject{["code"]="0",["msg"]="SYNTHETIC_PRIVATE_EARN_VALUE",["data"]=new JsonArray(row)};
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(b.ToJsonString())});
        }
    }
}
