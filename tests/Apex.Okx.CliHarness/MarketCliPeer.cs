using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Apex.Adapters.Okx.Execution;
using Apex.Okx.DemoAcceptance;

internal sealed class MarketCliPeer(string scenario,Action sent):HttpMessageHandler
{
    private int count;
    private readonly string changed=DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeMilliseconds().ToString();
    private string? withinReadUpdate;
    private static void Check(bool b){if(!b)throw new Exception("STRICT_MARKET_PEER_REFUSED");}
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
    {
        string[] targets={"/api/v5/public/time","/api/v5/account/config","/api/v5/account/balance","/api/v5/account/instruments?instType=SPOT","/api/v5/account/config","/api/v5/account/balance","/api/v5/account/interest-limits?type=2","/api/v5/account/config","/api/v5/account/balance"};
        Check(count<9&&r.Method==HttpMethod.Get&&r.Content==null&&r.RequestUri!.Scheme=="https"&&r.RequestUri.Host=="eea.okx.com"&&r.RequestUri.Port==443&&r.RequestUri.PathAndQuery==targets[count]);
        Check(r.Headers.GetValues("x-simulated-trading").Single()=="1");string target=targets[count++];sent();
        if(count>1)
        {
            string ts=r.Headers.GetValues("OK-ACCESS-TIMESTAMP").Single();
            Check(r.Headers.GetValues("OK-ACCESS-KEY").Single()==Encoding.ASCII.GetString(CliHarness.Field(1))&&r.Headers.GetValues("OK-ACCESS-PASSPHRASE").Single()==Encoding.ASCII.GetString(CliHarness.Field(3)));
            Check(r.Headers.GetValues("OK-ACCESS-SIGN").Single()==Convert.ToBase64String(HMACSHA256.HashData(CliHarness.Field(2),Encoding.ASCII.GetBytes(ts+"GET"+target))));
        }
        using var stream=typeof(MarketCliPeer).Assembly.GetManifestResourceStream(typeof(MarketCliPeer).Assembly.GetManifestResourceNames().Single(n=>n.EndsWith(count==7?"market-loans-official.json":"baseline-schema.json",StringComparison.Ordinal)))!;
        var oracle=JsonNode.Parse(stream)!;
        var body=count==7?oracle:oracle["endpoints"]![count switch{1=>"time",2 or 5 or 8=>"account",3 or 6 or 9=>"balance",_=>"instruments"}]!["control"]!.DeepClone();
        var row=body["data"]![0]!;body["msg"]="PRIVATE_MARKET_SENTINEL";
        if(count==1)row["ts"]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        if(count is 2 or 5 or 8){row["uid"]="987654321098765432";row["mainUid"]="987654321098765432";row["ip"]="192.0.2.198";if(scenario=="identity"&&count==8)row["uid"]="123456789";}
        if(count is 3 or 6 or 9)
        {
            // First collector balance: genuinely newer than collection/request, but not receive.
            // Keep the exact aggregate source unchanged for the closing bookend; row sources stay old.
            if(scenario=="within-read"&&count==6)
            {
                withinReadUpdate=DateTimeOffset.UtcNow.AddMilliseconds(80).ToUnixTimeMilliseconds().ToString();
                await Task.Delay(150,ct);
            }
            var details=new JsonArray();foreach(string c in new[]{"EUR","USDC","BTC","ETH","SOL","ADA"})details.Add(new JsonObject{["ccy"]=c,["cashBal"]="100",["availBal"]="100",["eq"]="100",["frozenBal"]="0",["ordFrozen"]="0",["stgyEq"]="0",["fixedBal"]="0",["liab"]="",["interest"]="",["crossLiab"]="",["isoLiab"]="",["uTime"]=changed});
            row["uTime"]=withinReadUpdate??changed;row["details"]=details;if(scenario=="movement"&&count==9)details[0]!["cashBal"]="101";
            if(scenario=="aggregate-only"&&count==9)row["uTime"]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            if(scenario=="row-clock"&&count==9)details[0]!["uTime"]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        }
        if(count==7)
        {
            if(scenario=="wait"){Console.WriteLine("SYNTHETIC_MARKET_WAIT");await Task.Delay(Timeout.Infinite,ct);}
            row["nextInterestTime"]=DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds().ToString();
            if(scenario=="blank")row["records"]![0]!.AsObject().Remove("usedLmt");
            if(scenario=="empty")row["records"]=new JsonArray();
            if(scenario=="secret")row["records"]![0]!["ccy"]=Encoding.ASCII.GetString(CliHarness.Field(1));
            if(scenario=="unsupported"){body["code"]="51000";body["data"]=new JsonArray();}
        }
        return new HttpResponseMessage(count==7&&scenario=="http"?HttpStatusCode.ServiceUnavailable:HttpStatusCode.OK){Content=new StringContent(body.ToJsonString())};
    }
}
