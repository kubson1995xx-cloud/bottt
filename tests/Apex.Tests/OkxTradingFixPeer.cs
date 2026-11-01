using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Engine;
using Xunit;
namespace Apex.Tests;

// Bounded extension fixture for ETH history isolation and two real reservation lifecycles.
// Multi-position synthetic peer. Attachment and generated numeric IDs are
// deliberately distinct; immutable original reviewer fixtures live in evidence.
internal sealed class TradingFixPeer(OperationalPeer owner) : HttpMessageHandler
{
    internal string Instrument="BTC-USDC",OrderId="777",AttachmentId="888",AlgoId="889",ExitOrderId="778";
    internal string Client="",Attachment="",ExitClient="";
    internal decimal Size;
    internal int Posts,BtcHistoryCalls,OwnHistoryCalls;
    internal bool FailBtc,ExitSignal,ProtectionCanceled;
    internal readonly System.Collections.Generic.List<string> Mutations=new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        Assert.Equal("eea.okx.com",request.RequestUri!.Host);string path=request.RequestUri.AbsolutePath;
        if(request.Method==HttpMethod.Post)
        {
            string body=await request.Content!.ReadAsStringAsync(ct);string stamp=request.Headers.GetValues("OK-ACCESS-TIMESTAMP").Single();
            Assert.Equal(Convert.ToBase64String(HMACSHA256.HashData(OperationalPeer.Secret,Encoding.UTF8.GetBytes(stamp+"POST"+path+body))),request.Headers.GetValues("OK-ACCESS-SIGN").Single());
            Assert.Equal("1",request.Headers.GetValues("x-simulated-trading").Single());
            var saved=JsonSerializer.Deserialize<EngineState>(File.ReadAllText(owner.Target))!;
            var dispatched=saved.NavalBroker!.Operational!.Intents.Last();Assert.Equal(Apex.Core.Naval.NavalExecutionStage.Dispatching,dispatched.Stage);
            var held=saved.Naval!.Reservations.Single(r=>r.Id==dispatched.ReservationId);Assert.True(held.EntryHold+held.CloseFeeHold>0);
            using var doc=JsonDocument.Parse(body);var row=doc.RootElement;Posts++;Mutations.Add(path+":"+body);
            if(path=="/api/v5/trade/cancel-algos")
            {
                Assert.Equal(AlgoId,row[0].GetProperty("algoId").GetString());Assert.Equal(Attachment,row[0].GetProperty("algoClOrdId").GetString());Assert.Equal(Instrument,row[0].GetProperty("instId").GetString());ProtectionCanceled=true;
                return Reply(JsonSerializer.Serialize(new{code="0",msg="",data=new[]{new{algoId=AlgoId,algoClOrdId=Attachment,sCode="0",sMsg=""}}}));
            }
            Assert.Equal("/api/v5/trade/order",path);Assert.Equal(Instrument,row.GetProperty("instId").GetString());Assert.Equal("cash",row.GetProperty("tdMode").GetString());Assert.Equal("fok",row.GetProperty("ordType").GetString());
            string client=row.GetProperty("clOrdId").GetString()!;
            if(row.GetProperty("side").GetString()=="sell")
            {
                Assert.True(ProtectionCanceled);Assert.Equal(Size,decimal.Parse(row.GetProperty("sz").GetString()!,System.Globalization.CultureInfo.InvariantCulture));Assert.False(row.TryGetProperty("attachAlgoOrds",out _));ExitClient=client;
            }
            else
            {
                Assert.Equal("buy",row.GetProperty("side").GetString());Size=decimal.Parse(row.GetProperty("sz").GetString()!,System.Globalization.CultureInfo.InvariantCulture);Client=client;Attachment=row.GetProperty("attachAlgoOrds")[0].GetProperty("attachAlgoClOrdId").GetString()!;
            }
            return Reply(JsonSerializer.Serialize(new{code="0",msg="",data=new[]{new{ordId=ExitClient==client?ExitOrderId:OrderId,clOrdId=client,ts=owner.Clock.GetUtcNow().ToUnixTimeMilliseconds().ToString(),sCode="0",sMsg=""}}}));
        }
        Assert.Equal(HttpMethod.Get,request.Method);Assert.False(request.Headers.Contains("OK-ACCESS-KEY"));
        if(path=="/api/v5/market/candles")
        {
            Assert.Contains("bar=1Dutc",request.RequestUri.Query);bool btc=request.RequestUri.Query.Contains("instId=BTC-USDC",StringComparison.Ordinal);
            if(btc){BtcHistoryCalls++;if(FailBtc)throw new HttpRequestException("synthetic BTC-only history outage");}else{Assert.Contains("instId="+Instrument,request.RequestUri.Query);OwnHistoryCalls++;}
            var cutoff=new DateTimeOffset(owner.Clock.GetUtcNow().UtcDateTime.Date,TimeSpan.Zero);
            var rows=Enumerable.Range(0,200).Reverse().Select(n=>new[]{cutoff.AddDays(n-200).ToUnixTimeMilliseconds().ToString(),"100",n==199?"112":"101",n==199&&ExitSignal?"89":"99",n==199?(ExitSignal?"90":"110"):"100","1","100","100","1"});
            return Reply(JsonSerializer.Serialize(new{code="0",msg="",data=rows}));
        }
        Assert.Equal("/api/v5/market/books",path);
        return Reply("{\"code\":\"0\",\"msg\":\"\",\"data\":[{\"asks\":[[\"110\",\"100\",\"0\",\"1\"]],\"bids\":[[\"109.99\",\"100\",\"0\",\"1\"]],\"ts\":\""+owner.Clock.GetUtcNow().ToUnixTimeMilliseconds()+"\",\"seqId\":1}]}");
    }
    private static HttpResponseMessage Reply(string body)=>new(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")};
}
