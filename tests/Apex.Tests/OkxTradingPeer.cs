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

// Independent synthetic public feed and strict signed mutation peer, never venue data.
internal sealed class TradingPeer(OperationalPeer owner) : HttpMessageHandler
{
    internal DateTimeOffset? LastBookSourceUtc;
    internal int Posts;
    internal bool SawDispatching;
    internal decimal Size;
    internal decimal AskPrice=110m,BidPrice=109.99m,AskDepth=100m;
    internal decimal? ExpectedSellSize;
    internal string Client="",Attachment="";
    internal string GeneratedAlgoId="889";
    internal bool Unknown;
    internal bool ProtectionCanceled;
    internal bool FailBookAfterCancel,UnknownSell,UnknownCancel;
    internal bool ExitSignal;
    internal string CandleInstrument="BTC-USDC";
    internal string ExitClient="";
    internal string ExitOrderId="778";
    internal string EntryOrderId="777";
    internal Action? BeforeResponse=null;
    internal TaskCompletionSource? HoldResponse;
    internal Action<string>? BeforeMutationResponse;
    internal TaskCompletionSource? HoldCancelResponse;
    internal readonly TaskCompletionSource Invoked=new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        Assert.Equal("eea.okx.com",request.RequestUri!.Host);
        string path=request.RequestUri.AbsolutePath;
        if(request.Method==HttpMethod.Post)
        {
            string body=await request.Content!.ReadAsStringAsync(ct);
            string stamp=request.Headers.GetValues("OK-ACCESS-TIMESTAMP").Single();
            Assert.Equal(Convert.ToBase64String(HMACSHA256.HashData(OperationalPeer.Secret,Encoding.UTF8.GetBytes(stamp+"POST"+path+body))),request.Headers.GetValues("OK-ACCESS-SIGN").Single());
            Assert.Equal("1",request.Headers.GetValues("x-simulated-trading").Single());
            var saved=JsonDocument.Parse(File.ReadAllText(owner.Target));
            SawDispatching=saved.RootElement.GetProperty("NavalBroker").GetProperty("Operational").GetProperty("Intents").EnumerateArray().Any(i=>i.GetProperty("Stage").GetString()=="Dispatching");
            Assert.True(SawDispatching);Assert.Contains(saved.RootElement.GetProperty("Naval").GetProperty("Reservations").EnumerateArray(),r=>r.GetProperty("EntryHold").GetDecimal()+r.GetProperty("CloseFeeHold").GetDecimal()>0);
            using var doc=JsonDocument.Parse(body);var row=doc.RootElement;
            if(path=="/api/v5/trade/cancel-algos")
            {
                Assert.Equal(GeneratedAlgoId,row[0].GetProperty("algoId").GetString());Assert.Equal(Attachment,row[0].GetProperty("algoClOrdId").GetString());ProtectionCanceled=true;Posts++;
                BeforeMutationResponse?.Invoke("cancel");if(HoldCancelResponse!=null)await HoldCancelResponse.Task.ConfigureAwait(false);
                if(UnknownCancel)return Reply("{\"code\":\"50011\",\"msg\":\"synthetic uncertain cancel\",\"data\":[]}");
                return Reply("{\"code\":\"0\",\"msg\":\"\",\"data\":[{\"algoId\":\""+GeneratedAlgoId+"\",\"algoClOrdId\":\""+Attachment+"\",\"sCode\":\"0\",\"sMsg\":\"\"}]}");
            }
            if(row.GetProperty("side").GetString()=="sell")
            {
                Assert.True(ProtectionCanceled);Assert.Equal(ExpectedSellSize??Size,decimal.Parse(row.GetProperty("sz").GetString()!,System.Globalization.CultureInfo.InvariantCulture));Assert.False(row.TryGetProperty("attachAlgoOrds",out _));
                ExitClient=row.GetProperty("clOrdId").GetString()!;Posts++;
                BeforeMutationResponse?.Invoke("sell");
                if(UnknownSell)return Reply("{\"code\":\"50011\",\"msg\":\"synthetic uncertain sell\",\"data\":[]}");
                return Reply(JsonSerializer.Serialize(new{code="0",msg="",data=new[]{new{ordId=ExitOrderId,clOrdId=ExitClient,ts=owner.Clock.GetUtcNow().ToUnixTimeMilliseconds().ToString(),sCode="0",sMsg=""}}}));
            }
            Assert.Equal("cash",row.GetProperty("tdMode").GetString());Assert.Equal("buy",row.GetProperty("side").GetString());Assert.Equal("fok",row.GetProperty("ordType").GetString());
            Size=decimal.Parse(row.GetProperty("sz").GetString()!,System.Globalization.CultureInfo.InvariantCulture);Client=row.GetProperty("clOrdId").GetString()!;
            Attachment=row.GetProperty("attachAlgoOrds")[0].GetProperty("attachAlgoClOrdId").GetString()!;
            Posts++;BeforeResponse?.Invoke();Invoked.TrySetResult();if(HoldResponse!=null)await HoldResponse.Task.ConfigureAwait(false);
            return Reply(Unknown?"{\"code\":\"50011\",\"msg\":\"synthetic uncertain\",\"data\":[]}":"{\"code\":\"0\",\"msg\":\"\",\"data\":[{\"ordId\":\""+EntryOrderId+"\",\"clOrdId\":\""+Client+"\",\"sCode\":\"0\",\"sMsg\":\"\",\"tag\":\"\",\"ts\":\""+owner.Clock.GetUtcNow().ToUnixTimeMilliseconds()+"\"}]}");
        }
        Assert.Equal(HttpMethod.Get,request.Method);Assert.False(request.Headers.Contains("OK-ACCESS-KEY"));
        if(path=="/api/v5/market/candles")
        {
            Assert.Contains("bar=1Dutc",request.RequestUri.Query);Assert.True(request.RequestUri.Query.Contains("instId="+CandleInstrument,StringComparison.Ordinal)||request.RequestUri.Query.Contains("instId=BTC-"+owner.Binding.Account.QuoteCurrency,StringComparison.Ordinal));
            var cutoff=new DateTimeOffset(owner.Clock.GetUtcNow().UtcDateTime.Date,TimeSpan.Zero);
            var rows=Enumerable.Range(0,200).Reverse().Select(n=>new[]{cutoff.AddDays(n-200).ToUnixTimeMilliseconds().ToString(),"100",n==199?"112":"101",n==199&&ExitSignal?"89":"99",n==199?(ExitSignal?"90":"110"):"100","1","100","100","1"});
            return Reply(JsonSerializer.Serialize(new{code="0",msg="",data=rows}));
        }
        if(path=="/api/v5/market/index-tickers")return Reply(JsonSerializer.Serialize(new{code="0",msg="",data=new[]{new{instId=CandleInstrument.Split('-')[0]+"-USD",idxPx=AskPrice.ToString(System.Globalization.CultureInfo.InvariantCulture),ts=owner.Clock.GetUtcNow().ToUnixTimeMilliseconds().ToString()}}}));
        Assert.Equal("/api/v5/market/books",path);
        if(FailBookAfterCancel&&ProtectionCanceled)throw new HttpRequestException("synthetic unavailable book");
        LastBookSourceUtc=DateTimeOffset.FromUnixTimeMilliseconds(owner.Clock.GetUtcNow().ToUnixTimeMilliseconds());
        string N(decimal n)=>n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Reply("{\"code\":\"0\",\"msg\":\"\",\"data\":[{\"asks\":[[\""+N(AskPrice)+"\",\""+N(AskDepth)+"\",\"0\",\"1\"]],\"bids\":[[\""+N(BidPrice)+"\",\"100\",\"0\",\"1\"]],\"ts\":\""+LastBookSourceUtc.Value.ToUnixTimeMilliseconds()+"\",\"seqId\":1}]}");
    }
    private static HttpResponseMessage Reply(string body)=>new(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")};
}
