using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Engine;
using Apex.Core.Naval;
using Xunit;
namespace Apex.Tests;

// Independent bounded account peer. Synthetic ONLY, all requests go through the
// production signer/parser/pager/coordinator/final durable dispatch fence.
internal sealed class FiniteDemoPeer(OperationalPeer owner) : HttpMessageHandler
{
    internal string BaseCurrency { get; init; }="BTC";
    internal string Instrument=>BaseCurrency+"-"+owner.Binding.Account.QuoteCurrency;
    internal decimal FeeRate { get; init; }=.001m;
    internal bool GrossProtection { get; init; }
    internal bool OwnFreezes { get; init; }
    internal bool Canceled,ProtectionCanceled,UnknownSubmit,UnknownCancel,FillPending,ForeignBalance,MissingFee,WrongFee,Rebate,FeeOverrun,UnknownSell;
    internal decimal BuyQuantity,SellQuantity,BuyFee;
    internal decimal BuyPrice=110;
    internal string PendingClient="",BuyClient="",SellClient="",Attachment="";
    internal readonly List<string> Sends=new();
    internal bool UnsafeAtFinal;
    private readonly string at=owner.Clock.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
    private EngineState State()=>JsonSerializer.Deserialize<EngineState>(File.ReadAllText(owner.Target))!;
    private static string N(decimal n)=>n.ToString(CultureInfo.InvariantCulture);
    private static HttpResponseMessage Reply(JsonNode b)=>new(HttpStatusCode.OK){Content=new StringContent(b.ToJsonString(),Encoding.UTF8,"application/json")};
    private static JsonObject Envelope(params JsonNode[] rows)=>new(){["code"]="0",["msg"]="",["data"]=new JsonArray(rows)};
    private static JsonNode Oracle(string file,string endpoint)
    {
        var d=new DirectoryInfo(AppContext.BaseDirectory);while(d!=null&&!Directory.Exists(Path.Combine(d.FullName,"tests/Apex.Adapters.Okx.Tests/Fixtures")))d=d.Parent;
        return JsonNode.Parse(File.ReadAllText(Path.Combine(d!.FullName,"tests/Apex.Adapters.Okx.Tests/Fixtures/ExecutionObservations",file)))!["endpoints"]![endpoint]!["control"]!["data"]![0]!.DeepClone();
    }
    internal void Observe(string path,JsonNode body)
    {
        if(path=="/api/v5/account/instruments"){body["data"]![0]!["tickSz"]="0.01";body["data"]![0]!["lotSz"]="0.0001";body["data"]![0]!["minSz"]="0.0001";return;}
        if(path=="/api/v5/account/config"){body["data"]![0]!["feeType"]="0";return;}
        if(path=="/api/v5/account/balance")
        {
            decimal net=BuyQuantity-BuyFee-SellQuantity;
            var q=body["data"]![0]!["details"]![0]!.DeepClone();foreach(string f in new[]{"cashBal","availBal","eq"})q[f]=N(1234.56m-BuyQuantity*BuyPrice+SellQuantity*109.99m*(1-FeeRate));
            if(OwnFreezes&&PendingClient!=""&&!Canceled){var r=State().Naval!.Reservations.First();decimal frozen=r.Quantity*r.Intent.EntryPriceCeiling;q["frozenBal"]=N(frozen);q["ordFrozen"]=N(frozen);q["availBal"]=N(decimal.Parse(q["cashBal"]!.GetValue<string>(),CultureInfo.InvariantCulture)-frozen);}
            body["data"]=new JsonArray(new JsonObject{["uTime"]=at,["details"]=new JsonArray(q)});
            if(net>0||ForeignBalance){var b=q.DeepClone();b["ccy"]=BaseCurrency;foreach(string f in new[]{"cashBal","availBal","eq"})b[f]=N(net+(ForeignBalance?1:0));if(OwnFreezes&&!ProtectionCanceled){b["frozenBal"]=N(net);b["ordFrozen"]=N(net);b["availBal"]="0";}body["data"]![0]!["details"]!.AsArray().Add(b);}
            return;
        }
        if(!path.StartsWith("/api/v5/trade/",StringComparison.Ordinal)&&!path.StartsWith("/api/v5/account/bills",StringComparison.Ordinal))return;
        if(path.Contains("orders-algo",StringComparison.Ordinal))return; // exact algo detail below is authoritative; empty account-wide surfaces.
        if(path=="/api/v5/trade/order-algo")
        {
            var state=State();
            // Only the finite acceptance's own algo is answered here; an owner without finite history
            // (Windows-local fresh owner) is served by the ordinary multi-instrument peer.
            if(state.NavalBroker?.Operational?.DemoAcceptance is not {} acceptance)return;
            var reservation=state.Naval!.Reservations.Single(r=>r.Intent.CommandId==acceptance.EntryCommand);
            var a=Oracle("stream-algo-schema.json","detail");a["instId"]=Instrument;a["algoId"]="889";a["algoClOrdId"]=Attachment;a["ordType"]="oco";a["side"]="sell";a["tdMode"]="cash";a["tradeQuoteCcy"]=owner.Binding.Account.QuoteCurrency;a["sz"]=N(GrossProtection?BuyQuantity:BuyQuantity-BuyFee);a["state"]=ProtectionCanceled?"canceled":"live";
            a["slTriggerPx"]=N(reservation.Intent.StopPrice);a["tpTriggerPx"]=N(reservation.Intent.TakeProfitPrice);a["slOrdPx"]="-1";a["tpOrdPx"]="-1";a["tpTriggerPxType"]="last";a["slTriggerPxType"]="last";a["cTime"]=at;a["uTime"]=at;a["ordIdList"]=new JsonArray();
            body["data"]=new JsonArray(a);return;
        }
        if(owner.Paths.Last().Contains("after=",StringComparison.Ordinal)){body["data"]=new JsonArray();return;}
        var rows=new List<JsonNode>();
        if(path.Contains("fills",StringComparison.Ordinal)||path.StartsWith("/api/v5/account/bills",StringComparison.Ordinal))
        {
            bool bill=path.StartsWith("/api/v5/account/bills",StringComparison.Ordinal);
            if(BuyQuantity>0)rows.Add(Economic(bill,false));if(SellQuantity>0)rows.Insert(0,Economic(bill,true));
        }
        else if(path=="/api/v5/trade/order"||path.Contains("orders-",StringComparison.Ordinal))
        {
            if(PendingClient!="")rows.Add(Order(false,true));if(BuyClient!="")rows.Insert(0,Order(false,false));if(SellClient!="")rows.Insert(0,Order(true,false));
            if(path=="/api/v5/trade/orders-pending")rows=rows.Where(r=>r["state"]!.GetValue<string>()=="live").ToList();
            if(path=="/api/v5/trade/order")rows=rows.Where(r=>owner.Paths.Last().Contains(r["clOrdId"]!.GetValue<string>(),StringComparison.Ordinal)||owner.Paths.Last().Contains("ordId="+r["ordId"]!.GetValue<string>(),StringComparison.Ordinal)).ToList();
        }
        body["data"]=new JsonArray(rows.ToArray());
    }
    private JsonNode Order(bool sell,bool pending)
    {
        var r=Oracle("recovery-schema.json","order");decimal quantity=pending?State().Naval!.Reservations.First().Quantity:sell?SellQuantity:BuyQuantity;
        string client=pending?PendingClient:sell?SellClient:BuyClient;var reservation=State().Naval!.Reservations.Single(x=>x.Intent.CommandId==(pending?State().NavalBroker!.Operational!.DemoAcceptance!.PendingCommand:State().NavalBroker!.Operational!.DemoAcceptance!.EntryCommand));
        r["instId"]=Instrument;r["ordId"]=pending?"776":sell?"778":"777";r["clOrdId"]=client;r["side"]=sell?"sell":"buy";r["state"]=pending?(Canceled?"canceled":"live"):"filled";r["sz"]=N(quantity);r["accFillSz"]=pending?"0":N(quantity);r["fillSz"]=pending?"0":N(quantity);r["fee"]=pending?"0":N(sell?-SellQuantity*109.99m*FeeRate:-BuyFee);r["feeCcy"]=pending?"":sell?owner.Binding.Account.QuoteCurrency:BaseCurrency;r["tradeQuoteCcy"]=owner.Binding.Account.QuoteCurrency;r["ordType"]=pending?"post_only":"fok";r["px"]=N(sell?109.99m:reservation.Intent.EntryPriceCeiling);r["fillPx"]=N(sell?109.99m:110);r["avgPx"]=pending?"":N(sell?109.99m:110);r["cTime"]=at;r["uTime"]=at;r["fillTime"]=at;
        r["attachAlgoOrds"]=pending||sell?new JsonArray():new JsonArray(new JsonObject{["attachAlgoId"]="888",["attachAlgoClOrdId"]=Attachment,["failCode"]=""});
        if(pending&&FillPending&&Canceled){r["accFillSz"]=N(BuyQuantity);r["fillSz"]=N(BuyQuantity);r["fee"]=N(-BuyFee);r["feeCcy"]=BaseCurrency;r["fillPx"]=N(BuyPrice);r["avgPx"]=N(BuyPrice);}
        return r;
    }
    private JsonNode Economic(bool bill,bool sell)
    {
        var r=Oracle("recovery-schema.json",bill?"bills":"fills");decimal q=sell?SellQuantity:BuyQuantity,fee=sell?-q*109.99m*FeeRate:-BuyFee;
        r["instId"]=Instrument;r["ordId"]=sell?"778":BuyClient==""?"776":"777";r["clOrdId"]=sell?SellClient:BuyClient==""?PendingClient:BuyClient;r["tradeId"]=sell?"902":"900";r["billId"]=sell?"903":"901";r["ts"]=at;r["fee"]=N(fee);
        if(bill){r["ccy"]=sell?owner.Binding.Account.QuoteCurrency:BaseCurrency;r["sz"]=N(q);r["px"]=sell?"109.99":N(BuyPrice);r["balChg"]=N(sell?q*109.99m+fee:fee);r["type"]="2";r["mgnMode"]="cash";}
        else {r["side"]=sell?"sell":"buy";r["fillSz"]=N(q);r["fillPx"]=sell?"109.99":N(BuyPrice);r["feeCcy"]=sell?owner.Binding.Account.QuoteCurrency:BaseCurrency;r["fillTime"]=at;r["tradeQuoteCcy"]=owner.Binding.Account.QuoteCurrency;if(!sell){if(MissingFee)r["feeCcy"]="";if(WrongFee)r["feeCcy"]="EUR";if(Rebate)r["fee"]="0.00000001";if(FeeOverrun)r["fee"]=N(-BuyFee*2);}}
        return r;
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        Assert.Equal("eea.okx.com",request.RequestUri!.Host);Assert.Equal("1",request.Headers.GetValues("x-simulated-trading").Single());string path=request.RequestUri.AbsolutePath;
        if(request.Method==HttpMethod.Get)
        {
            string now=owner.Clock.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            if(path=="/api/v5/market/books")return Reply(JsonNode.Parse("{\"code\":\"0\",\"msg\":\"\",\"data\":[{\"asks\":[[\"110\",\"100\",\"0\",\"1\"]],\"bids\":[[\"109.99\",\"100\",\"0\",\"1\"]],\"ts\":\""+now+"\",\"seqId\":1}]}")!);
            if(path=="/api/v5/market/index-tickers")return Reply(Envelope(new JsonObject{["instId"]=BaseCurrency+"-USD",["idxPx"]="110",["ts"]=now}));
            Assert.Equal("/api/v5/public/price-limit",path);return Reply(Envelope(new JsonObject{["instId"]=Instrument,["instType"]="SPOT",["buyLmt"]="150",["sellLmt"]="90",["enabled"]=true,["ts"]=now}));
        }
        string body=await request.Content!.ReadAsStringAsync(ct),stamp=request.Headers.GetValues("OK-ACCESS-TIMESTAMP").Single();
        Assert.Equal(Convert.ToBase64String(HMACSHA256.HashData(OperationalPeer.Secret,Encoding.UTF8.GetBytes(stamp+"POST"+path+body))),request.Headers.GetValues("OK-ACCESS-SIGN").Single());
        var saved=State();Assert.Contains(saved.NavalBroker!.Operational!.Intents,i=>i.Stage==NavalExecutionStage.Dispatching);Sends.Add(path);
        using var doc=JsonDocument.Parse(body);var row=doc.RootElement;
        if(path=="/api/v5/trade/cancel-algos"){ProtectionCanceled=true;return Reply(Envelope(new JsonObject{["algoId"]="889",["algoClOrdId"]=Attachment,["sCode"]="0",["sMsg"]=""}));}
        string client=row.GetProperty("clOrdId").GetString()!,id;
        if(path=="/api/v5/trade/cancel-order"){Assert.Equal(PendingClient,client);Canceled=true;id="776";if(FillPending){BuyQuantity=State().Naval!.Reservations.First().Quantity/2;BuyPrice=109.99m;BuyFee=BuyQuantity*FeeRate;}if(UnknownCancel)return Reply(new JsonObject{["code"]="50004",["msg"]="private sentinel not exported",["data"]=new JsonArray()});}
        else if(row.GetProperty("side").GetString()=="sell")
        {Assert.True(ProtectionCanceled);SellQuantity=decimal.Parse(row.GetProperty("sz").GetString()!,CultureInfo.InvariantCulture);Assert.True(SellQuantity<=BuyQuantity-BuyFee);SellClient=client;id="778";if(UnknownSell)return Reply(new JsonObject{["code"]="50004",["msg"]="",["data"]=new JsonArray()});}
        else if(row.GetProperty("ordType").GetString()=="post_only"){PendingClient=client;id="776";}
        else {Assert.Equal("fok",row.GetProperty("ordType").GetString());Assert.True(Canceled);BuyQuantity=decimal.Parse(row.GetProperty("sz").GetString()!,CultureInfo.InvariantCulture);BuyFee=BuyQuantity*FeeRate;BuyClient=client;Attachment=row.GetProperty("attachAlgoOrds")[0].GetProperty("attachAlgoClOrdId").GetString()!;id="777";}
        if(UnknownSubmit&&path=="/api/v5/trade/order")return Reply(new JsonObject{["code"]="50004",["msg"]="",["data"]=new JsonArray()});
        return Reply(Envelope(new JsonObject{["ordId"]=id,["clOrdId"]=client,["sCode"]="0",["sMsg"]="",["ts"]=owner.Clock.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}));
    }
}
