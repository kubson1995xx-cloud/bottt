using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Apex.Adapters.Okx.Execution;
using Apex.Okx.DemoAcceptance;
using Xunit;
namespace Apex.Tests;

// Real collector + signer/parser. All wire values are synthetic, clocks are deterministic.
public sealed class OkxMarketObligationsClockTests
{
    public OkxMarketObligationsClockTests(){SyntheticObligationEnvironment.Prepare();_=new OkxBalanceSchemaDiagnosticTests();}
    private static readonly byte[] Key="clock-fixture-key"u8.ToArray(),Secret="clock-fixture-secret"u8.ToArray(),Pass="clock-fixture-pass"u8.ToArray();
    internal static readonly DateTimeOffset Anchor=DateTimeOffset.Parse("2026-01-02T03:04:05.000184+00:00");
    private static readonly string[] Cases={"within-read","row-within-read","old-idle","at-receive","aggregate-one-tick","row-one-tick","wrong-later-capture","after-aggregate-future","after-row-future","economic-before","economic-after","aggregate-clock-moved","row-clock-moved","identity-after","aggregate-only-advance","cashBal","availBal","eq","frozenBal","ordFrozen","stgyEq","fixedBal","interest","liab","crossLiab","isoLiab","null-to-empty","empty-to-zero","decimal-raw","currency-replaced","currency-missing","currency-added"};
    [Theory]
    [InlineData("within-read",true,5)] [InlineData("row-within-read",true,5)]
    [InlineData("old-idle",true,5)] [InlineData("at-receive",true,5)]
    [InlineData("aggregate-one-tick",false,2)] [InlineData("row-one-tick",false,2)]
    [InlineData("wrong-later-capture",false,2)]
    [InlineData("after-aggregate-future",false,5)] [InlineData("after-row-future",false,5)]
    [InlineData("economic-before",false,5)] [InlineData("economic-after",false,5)]
    // Original backward aggregate-clock fixture is preserved; only its expectation changes by the explicit local policy.
    [InlineData("aggregate-clock-moved",true,5)] [InlineData("row-clock-moved",false,5)]
    [InlineData("identity-after",false,4)]
    [InlineData("aggregate-only-advance",true,5)]
    [InlineData("cashBal",false,5)] [InlineData("availBal",false,5)] [InlineData("eq",false,5)]
    [InlineData("frozenBal",false,5)] [InlineData("ordFrozen",false,5)] [InlineData("stgyEq",false,5)] [InlineData("fixedBal",false,5)]
    [InlineData("interest",false,5)] [InlineData("liab",false,5)] [InlineData("crossLiab",false,5)] [InlineData("isoLiab",false,5)]
    [InlineData("null-to-empty",false,5)] [InlineData("empty-to-zero",false,5)] [InlineData("decimal-raw",false,5)]
    [InlineData("currency-replaced",false,5)] [InlineData("currency-missing",false,5)] [InlineData("currency-added",false,5)]
    public async Task Balance_source_is_bounded_by_its_own_read_receive_without_rewriting(string scenario,bool success,int sends)
    {
        var clock=new Clock();using var peer=new Peer(scenario,clock);
        var scope=new OkxExecutionScope("Demo",peer.Uid,peer.Uid,"EUR","source-clock-fixture");
        using var owner=new EphemeralCredentials(Key.ToArray(),Secret.ToArray(),Pass.ToArray());
        int checkpoints=0;
        object result=await MarketObligationsObservation.CollectAsync(scope,owner.ForObservation(scope),owner.Fingerprint,()=>true,CancellationToken.None,
            _=>{if(++checkpoints==1)clock.Set(Anchor.AddTicks(3217980));},
            (s,p,h,f)=>OkxExecutionObservationClient.ForTests(peer,clock,s,p,h,f,(t,ct)=>{clock.Advance(t);return Task.CompletedTask;}),clock);
        var r=JsonSerializer.SerializeToElement(result);var trace=r.GetProperty("readTrace");
        // These operands are identical against the retained old DLL and the corrected/extracted DLL.
        Assert.Equal(Anchor,r.GetProperty("requestUtc").GetDateTimeOffset());
        Assert.Equal(Anchor.AddTicks(3217980),trace[1].GetProperty("RequestUtc").GetDateTimeOffset());
        Assert.Equal(peer.FirstReceive,trace[1].GetProperty("ReceiveUtc").GetDateTimeOffset());
        Assert.Equal(6,r.GetProperty("balancesBefore").GetProperty("Details").GetArrayLength());
        Assert.Equal(peer.Aggregate,r.GetProperty("balancesBefore").GetProperty("UTime").GetProperty("Raw").GetString());
        if(scenario=="within-read")
        {
            var source=r.GetProperty("balancesBefore").GetProperty("UTime").GetProperty("Utc").GetDateTimeOffset();
            Assert.Equal(6988160,(source-Anchor).Ticks);
            Assert.Equal(3770180,(source-trace[1].GetProperty("RequestUtc").GetDateTimeOffset()).Ticks);
            Assert.Equal(625630,(peer.FirstReceive-source).Ticks);
        }
        if(scenario is "aggregate-one-tick" or "row-one-tick")Assert.Equal(1,(DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(peer.Update))-peer.FirstReceive).Ticks);
        // Establish the exact observed exclusion BEFORE the result assertion, including on the old-DLL RED.
        bool metadataMoved=scenario is "aggregate-only-advance" or "aggregate-clock-moved";
        if(metadataMoved)
        {
            Assert.Equal(5,peer.Count);
            Assert.NotEqual(r.GetProperty("balancesBefore").GetProperty("UTime").GetRawText(),r.GetProperty("balancesAfter").GetProperty("UTime").GetRawText());
            Assert.Equal(r.GetProperty("balancesBefore").GetProperty("Details").GetRawText(),r.GetProperty("balancesAfter").GetProperty("Details").GetRawText());
            Assert.Equal(scenario=="aggregate-clock-moved"?peer.Old:peer.Update,r.GetProperty("balancesAfter").GetProperty("UTime").GetProperty("Raw").GetString());
            Assert.True(DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(peer.Aggregate))<=trace[1].GetProperty("ReceiveUtc").GetDateTimeOffset());
            Assert.True(DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(scenario=="aggregate-clock-moved"?peer.Old:peer.Update))<=trace[4].GetProperty("ReceiveUtc").GetDateTimeOffset());
        }
        string expected=success?"MARKET_LOANS_OBSERVED_STOP_NO_TRADING":sends==2?"BALANCE_SHAPE_OR_FUTURE_SOURCE":sends==4?"ACCOUNT_BOOKEND_MOVED":"BALANCE_BOOKEND_MOVED";
        Assert.Equal(expected,r.GetProperty("result").GetString());
        Assert.Equal(success&&!metadataMoved,r.GetProperty("bracketUnchanged").GetBoolean());Assert.Equal(sends,peer.Count);
        Assert.Equal(success,r.GetProperty("selectedBalanceRowsConsistent").GetBoolean());
        Assert.Equal("BOUNDED_LOCAL_SELECTED_BALANCE_DTO_V1",r.GetProperty("consistencyPolicy").GetString());
        if(sends==5)Assert.Equal(metadataMoved||scenario=="after-aggregate-future",r.GetProperty("aggregateBalanceTimestampChanged").GetBoolean());
        else Assert.Equal(JsonValueKind.Null,r.GetProperty("aggregateBalanceTimestampChanged").ValueKind);
        Assert.Equal(success?"FINISHED":sends==2?"BALANCE_BEFORE":sends==4?"ACCOUNT_AFTER":"BALANCE_AFTER",r.GetProperty("stage").GetString());
        if(success)
        {
            if(!metadataMoved)Assert.Equal(r.GetProperty("balancesBefore").GetRawText(),r.GetProperty("balancesAfter").GetRawText());
            Assert.All(r.GetProperty("balancesBefore").GetProperty("Details").EnumerateArray(),x=>Assert.Equal(scenario=="row-within-read"?peer.Update:peer.Old,x.GetProperty("UTime").GetProperty("Raw").GetString()));
        }
    }
    private sealed class Clock:TimeProvider
    {
        internal DateTimeOffset Utc=Anchor;internal long Mono;
        internal void Advance(TimeSpan t){Utc+=t;Mono+=t.Ticks;}
        internal void Set(DateTimeOffset utc){Assert.True(utc>=Utc);Advance(utc-Utc);}
        public override DateTimeOffset GetUtcNow()=>Utc;public override long GetTimestamp()=>Mono;public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
    }
    private sealed class Peer(string scenario,Clock clock):HttpMessageHandler
    {
        internal string Uid=>"61234567890"+Array.IndexOf(Cases,scenario).ToString("D2");
        internal int Count;
        internal readonly string Old=Anchor.AddDays(-7).ToUnixTimeMilliseconds().ToString();
        internal readonly string Update=Anchor.AddTicks(6988160).ToUnixTimeMilliseconds().ToString();
        internal string Aggregate=>scenario is "old-idle" or "row-within-read" or "row-one-tick" or "aggregate-only-advance"?Old:scenario=="wrong-later-capture"?Anchor.AddMilliseconds(800).ToUnixTimeMilliseconds().ToString():Update;
        internal DateTimeOffset FirstReceive=>scenario is "aggregate-one-tick" or "row-one-tick"?DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(Update)).AddTicks(-1):scenario=="at-receive"?DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(Update)):Anchor.AddTicks(7613790);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            string[] paths={"/api/v5/account/config","/api/v5/account/balance","/api/v5/account/interest-limits?type=2","/api/v5/account/config","/api/v5/account/balance"};
            Assert.InRange(Count,0,4);string path=paths[Count++];Assert.Equal(path,request.RequestUri!.PathAndQuery);
            Assert.Equal(HttpMethod.Get,request.Method);Assert.Null(request.Content);Assert.Equal("eea.okx.com",request.RequestUri.Host);Assert.Equal("https",request.RequestUri.Scheme);Assert.Equal(443,request.RequestUri.Port);
            Assert.Equal("1",request.Headers.GetValues("x-simulated-trading").Single());
            string stamp=request.Headers.GetValues("OK-ACCESS-TIMESTAMP").Single();
            Assert.Equal(Encoding.ASCII.GetString(Key),request.Headers.GetValues("OK-ACCESS-KEY").Single());Assert.Equal(Encoding.ASCII.GetString(Pass),request.Headers.GetValues("OK-ACCESS-PASSPHRASE").Single());
            Assert.Equal(Convert.ToBase64String(HMACSHA256.HashData(Secret,Encoding.ASCII.GetBytes(stamp+"GET"+path))),request.Headers.GetValues("OK-ACCESS-SIGN").Single());
            JsonNode row;
            if(Count is 1 or 4)
            {
                row=JsonNode.Parse("{\"uid\":\"1\",\"mainUid\":\"1\",\"acctLv\":\"1\",\"perm\":\"read_only,trade\",\"ip\":\"192.0.2.198\",\"feeType\":\"0\",\"autoLoan\":false,\"enableSpotBorrow\":false,\"spotBorrowAutoRepay\":false,\"type\":\"0\",\"spotRoleType\":\"0\",\"kycLv\":\"3\"}")!;
                row["uid"]=Count==4&&scenario=="identity-after"?"6123456789999":Uid;row["mainUid"]=Uid;
            }
            else if(Count is 2 or 5)
            {
                if(Count==2)clock.Set(FirstReceive);
                var details=new JsonArray();foreach(string ccy in new[]{"EUR","USD","GBP","JPY","CHF","AUD"})details.Add(new JsonObject{["ccy"]=ccy,["cashBal"]="100",["availBal"]="100",["eq"]="100",["frozenBal"]="0",["ordFrozen"]="0",["stgyEq"]="0",["fixedBal"]="0",["interest"]="",["liab"]="",["crossLiab"]="",["isoLiab"]="",["uTime"]=scenario is "row-within-read" or "row-one-tick"?Update:Old});
                row=new JsonObject{["uTime"]=Aggregate,["details"]=details};
                if(scenario=="null-to-empty"&&Count==2)details[0]!.AsObject().Remove("interest");
                if((scenario=="economic-before"&&Count==2)||(scenario=="economic-after"&&Count==5))details[0]!["cashBal"]="101";
                if(Count==5)
                {
                    if(scenario=="aggregate-clock-moved")row["uTime"]=Old;
                    if(scenario=="aggregate-only-advance")row["uTime"]=Update;
                    if(scenario=="row-clock-moved")details[0]!["uTime"]=Anchor.AddDays(-6).ToUnixTimeMilliseconds().ToString();
                    if(new[]{"cashBal","availBal","eq","frozenBal","ordFrozen","stgyEq","fixedBal","interest","liab","crossLiab","isoLiab"}.Contains(scenario))details[0]![scenario]="7";
                    if(scenario=="empty-to-zero")details[0]!["interest"]="0";
                    if(scenario=="decimal-raw")details[0]!["cashBal"]="100.0";
                    if(scenario=="currency-replaced")details[0]!["ccy"]="NZD";
                    if(scenario=="currency-missing")details.RemoveAt(0);
                    if(scenario=="currency-added"){var extra=details[0]!.DeepClone();extra["ccy"]="NZD";details.Add(extra);}
                    string future=clock.Utc.AddMilliseconds(1).ToUnixTimeMilliseconds().ToString();
                    if(scenario=="after-aggregate-future")row["uTime"]=future;
                    if(scenario=="after-row-future")details[0]!["uTime"]=future;
                }
            }
            else
            {
                using var stream=typeof(OkxMarketObligationsClockTests).Assembly.GetManifestResourceStream("Apex.Tests.Fixtures.market-loans-official.json")!;
                row=JsonNode.Parse(stream)!["data"]![0]!.DeepClone();
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(new JsonObject{["code"]="0",["msg"]="",["data"]=new JsonArray(row)}.ToJsonString())});
        }
    }
}
