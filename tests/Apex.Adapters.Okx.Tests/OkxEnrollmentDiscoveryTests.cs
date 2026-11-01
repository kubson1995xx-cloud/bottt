using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Apex.Adapters.Okx.Execution;
using Xunit;
namespace Apex.Adapters.Okx.Tests;
public sealed class OkxEnrollmentDiscoveryTests
{
    [Theory]
    [InlineData("funded","CONNECTED_READ_ONLY_STOP",4)]
    [InlineData("empty","CONNECTED_READ_ONLY_STOP",4)]
    [InlineData("withdraw","DISCOVERY_UNSAFE_ACCOUNT",2)]
    [InlineData("borrow","DISCOVERY_UNSAFE_ACCOUNT",2)]
    [InlineData("identity","DISCOVERY_IDENTITY_MISMATCH",2)]
    [InlineData("live","DISCOVERY_DEMO_ONLY",0)]
    [InlineData("provenance","DISCOVERY_PROVENANCE_MISMATCH",0)]
    [InlineData("cancel","DISCOVERY_CANCELLED",0)]
    public async Task Identity_first_signed_closed_get_discovery_without_financial_scope(string scenario,string code,int sends)
    {
        var peer=new Peer(scenario);using var stop=new CancellationTokenSource();if(scenario=="cancel")stop.Cancel();
        var r=await OkxEnrollmentDiscoveryClient.RunAsync(scenario=="live"?"Live":"Demo",scenario=="provenance"?"LiveAccountApi":"DemoTradingApi",Encoding.ASCII.GetBytes("owned-key"),Encoding.ASCII.GetBytes("owned-secret"),Encoding.ASCII.GetBytes("owned-pass"),scenario=="identity"?"999999":null,scenario=="identity"?"999999":null,stop.Token,peer);
        Assert.Equal(code,r.Code);Assert.Equal(sends,peer.Paths.Count);
        if(scenario is "funded" or "empty")
        {
            var o=Assert.IsType<OkxEnrollmentObservation>(r.Observation);Assert.Equal("123456",o.Uid);Assert.Equal("123456",o.MainUid);Assert.Equal("Demo",o.Environment);
            Assert.NotEmpty(o.Instruments);Assert.Equal(scenario=="empty"?0:2,o.Balances.Length);
            if(scenario=="funded"){Assert.Equal(new[]{"USD","USDT"},o.Balances.Select(b=>b.Currency));Assert.Equal(new decimal?[]{100,1},o.Balances.Select(b=>b.Cash.Value));}
        }
        else Assert.Null(r.Observation);
    }
    [Fact]
    public async Task Bootstrap_serializes_unknown_identity_against_existing_private_reader_and_stop_revokes_waiter()
    {
        var peer=new OkxExecutionObservationHttpFixture();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Steps.Enqueue(("/api/v5/account/config",async ct=>{entered.TrySetResult();await release.Task.WaitAsync(ct);return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(OkxExecutionObservationHttpFixture.Control("account"))};}));
        await using var existing=peer.Client();var oldRead=existing.ReadAccountAsync(peer.Context,default);await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var bootstrap=new Peer("empty");using var stop=new CancellationTokenSource();
        var waiting=OkxEnrollmentDiscoveryClient.RunAsync("Demo","DemoTradingApi",Encoding.ASCII.GetBytes("owned-key"),Encoding.ASCII.GetBytes("owned-secret"),Encoding.ASCII.GetBytes("owned-pass"),null,null,stop.Token,bootstrap);
        await Task.Delay(100);Assert.Empty(bootstrap.Paths);stop.Cancel();Assert.Equal("DISCOVERY_CANCELLED",(await waiting.WaitAsync(TimeSpan.FromSeconds(3))).Code);Assert.Empty(bootstrap.Paths);
        release.TrySetResult();Assert.Equal(OkxObservationStatus.Observed,(await oldRead).Status);peer.Done();
        var control=await OkxEnrollmentDiscoveryClient.RunAsync("Demo","DemoTradingApi",Encoding.ASCII.GetBytes("owned-key"),Encoding.ASCII.GetBytes("owned-secret"),Encoding.ASCII.GetBytes("owned-pass"),null,null,default,bootstrap);
        Assert.Equal("CONNECTED_READ_ONLY_STOP",control.Code);Assert.Equal(4,bootstrap.Paths.Count);
    }
    private sealed class Peer(string mode):HttpMessageHandler
    {
        internal readonly List<string> Paths=[];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get,r.Method);Assert.Null(r.Content);Assert.Equal("eea.okx.com",r.RequestUri!.Host);Assert.Equal("1",r.Headers.GetValues("x-simulated-trading").Single());
            string path=r.RequestUri.PathAndQuery;Paths.Add(path);
            Assert.Equal(new[]{"/api/v5/public/time","/api/v5/account/config","/api/v5/account/balance","/api/v5/account/instruments?instType=SPOT"}[Paths.Count-1],path);
            JsonNode body;
            if(Paths.Count==1){Assert.False(r.Headers.Contains("OK-ACCESS-KEY"));body=JsonNode.Parse("{\"code\":\"0\",\"msg\":\"\",\"data\":[{\"ts\":\""+DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+"\"}]}")!;}
            else
            {
                string stamp=r.Headers.GetValues("OK-ACCESS-TIMESTAMP").Single();
                Assert.True(r.Headers.GetValues("OK-ACCESS-SIGN").Single()==Convert.ToBase64String(HMACSHA256.HashData(Encoding.ASCII.GetBytes("owned-secret"),Encoding.UTF8.GetBytes(stamp+"GET"+path))),"Synthetic GET signature mismatch; values withheld");
                body=JsonNode.Parse(OkxExecutionObservationHttpFixture.Control(Paths.Count==2?"account":Paths.Count==3?"balance":"instruments"))!;
                if(Paths.Count==2){var a=body["data"]![0]!;a["uid"]="123456";a["mainUid"]="123456";a["perm"]=mode=="withdraw"?"read_only,withdraw":"read_only,trade";if(mode=="borrow")a["autoLoan"]=true;}
                if(Paths.Count==3){var b=body["data"]![0]!;var rows=(JsonArray)b["details"]!;if(mode=="empty")rows.Clear();else{var first=rows[0]!.DeepClone();first["ccy"]="USD";first["cashBal"]="100";var second=first.DeepClone();second["ccy"]="USDT";second["cashBal"]="1";b["details"]=new JsonArray(first,second);}}
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body.ToJsonString())});
        }
    }
}
