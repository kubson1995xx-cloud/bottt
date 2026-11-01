using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Apex.Core.Engine;
namespace Apex.Tests;
internal sealed class TradingFixEconomics(OperationalPeer f,TradingFixPeer peer)
{
    internal sealed record Position(string Instrument,string Parent,string AttachmentId,string Algo,string Child,string Client,string Attachment,decimal Size,decimal Stop,decimal Take,string Time)
    {internal bool Triggered;}
    internal readonly List<Position> Positions=new();
    private static string N(decimal n)=>n.ToString("0.############################",CultureInfo.InvariantCulture);
    private static JsonNode Oracle(string file,string endpoint)
    {
        var dir=new DirectoryInfo(AppContext.BaseDirectory);while(dir!=null&&!Directory.Exists(Path.Combine(dir.FullName,"tests/Apex.Adapters.Okx.Tests/Fixtures")))dir=dir.Parent;
        return JsonNode.Parse(File.ReadAllText(Path.Combine(dir!.FullName,"tests/Apex.Adapters.Okx.Tests/Fixtures/ExecutionObservations",file)))!["endpoints"]![endpoint]!["control"]!["data"]![0]!.DeepClone();
    }
    internal Position Add()
    {
        var r=JsonSerializer.Deserialize<EngineState>(File.ReadAllText(f.Target))!.Naval!.Reservations.Last();
        var p=new Position(peer.Instrument,peer.OrderId,peer.AttachmentId,peer.AlgoId,peer.ExitOrderId,peer.Client,peer.Attachment,peer.Size,r.Intent.StopPrice,r.Intent.TakeProfitPrice,f.Clock.GetUtcNow().ToUnixTimeMilliseconds().ToString());Positions.Add(p);return p;
    }
    internal void Mutate(string path,JsonNode body)
    {
        if(path=="/api/v5/account/instruments"&&peer.Instrument=="ETH-USDC")AddEth(path,body);
        string? kind=path switch{"/api/v5/trade/order" or "/api/v5/trade/orders-history" or "/api/v5/trade/orders-history-archive"=>"order","/api/v5/trade/fills" or "/api/v5/trade/fills-history"=>"fills","/api/v5/account/bills" or "/api/v5/account/bills-archive"=>"bills",_=>null};
        if(kind!=null)
        {
            var rows=new JsonArray();
            if(!f.Paths.Last().Contains("after=",StringComparison.Ordinal))foreach(var p in Positions.AsEnumerable().Reverse())foreach(bool sell in p.Triggered?new[]{true,false}:new[]{false})
            {
                string id=sell?p.Child:p.Parent;
                if(path=="/api/v5/trade/order"&&!f.Paths.Last().Contains("ordId="+id,StringComparison.Ordinal)&&!( !sell&&f.Paths.Last().Contains("clOrdId="+p.Client,StringComparison.Ordinal)))continue;
                var row=Oracle("recovery-schema.json",kind);row["instId"]=p.Instrument;row["ordId"]=id;row["clOrdId"]=sell?"":p.Client;row["tradeId"]=sell?p.Child+"2":p.Parent+"2";row["fillTime"]=p.Time;row["fee"]=N(-p.Size*(sell?.12m:.11m));if(kind!="bills")row["feeCcy"]="USDC";
                if(kind=="order")
                {
                    row["cTime"]=p.Time;row["uTime"]=p.Time;row["state"]="filled";row["side"]=sell?"sell":"buy";row["sz"]=N(p.Size);row["accFillSz"]=N(p.Size);row["fillSz"]=N(p.Size);foreach(string key in new[]{"fillPx","avgPx","px"})row[key]=sell?"120":"110";
                    row["tradeQuoteCcy"]="USDC";row["ordType"]="fok";row["tgtCcy"]="base_ccy";row["attachAlgoOrds"]=sell?new JsonArray():new JsonArray(new JsonObject{["attachAlgoId"]=p.AttachmentId,["attachAlgoClOrdId"]=p.Attachment,["failCode"]=""});
                    if(sell){row["algoId"]=p.Algo;row["algoClOrdId"]=p.Attachment;}
                }
                else
                {
                    row["ts"]=p.Time;row["billId"]=id+"3";
                    if(kind=="fills"){row["side"]=sell?"sell":"buy";row["fillSz"]=N(p.Size);row["fillPx"]=sell?"120":"110";row["tradeQuoteCcy"]="USDC";}
                    else {row["ccy"]="USDC";row["type"]="2";row["mgnMode"]="cash";row["sz"]=N(p.Size);row["px"]=sell?"120":"110";row["balChg"]=N(p.Size*(sell?119.88m:-110.11m));}
                }
                rows.Add(row);
            }
            body["data"]=rows;
        }
        if(path=="/api/v5/trade/order-algo")
        {
            var p=Positions.Single(p=>f.Paths.Last().Contains("algoClOrdId="+p.Attachment,StringComparison.Ordinal));var row=Oracle("stream-algo-schema.json","detail");row["instId"]=p.Instrument;row["algoId"]=p.Algo;row["algoClOrdId"]=p.Attachment;row["side"]="sell";row["ordType"]="oco";row["tradeQuoteCcy"]="USDC";row["sz"]=N(p.Size);row["slTriggerPx"]=N(p.Stop);row["tpTriggerPx"]=N(p.Take);row["cTime"]=p.Time;row["uTime"]=p.Time;
            row["state"]=p.Triggered?"effective":p.Algo==peer.AlgoId&&peer.ProtectionCanceled?"canceled":"live";row["ordIdList"]=p.Triggered?new JsonArray(p.Child):new JsonArray();body["data"]=new JsonArray(row);
        }
        if(path=="/api/v5/account/balance")
        {
            var quote=body["data"]![0]!["details"]![0]!.DeepClone();foreach(string key in new[]{"cashBal","availBal","eq"})quote[key]=N(1234.56m+Positions.Sum(p=>p.Size*(p.Triggered?9.77m:-110.11m)));
            var rows=new JsonArray(quote);foreach(var g in Positions.Where(p=>!p.Triggered).GroupBy(p=>p.Instrument)){var row=quote.DeepClone();row["ccy"]=g.Key.Split('-')[0];foreach(string key in new[]{"cashBal","availBal","eq"})row[key]=N(g.Sum(p=>p.Size));rows.Add(row);}body["data"]![0]!["details"]=rows;
        }
    }
    internal static void AddEth(string path,JsonNode body)
    {
        if(path!="/api/v5/account/instruments")return;var row=body["data"]![0]!.DeepClone();row["instId"]="ETH-USDC";row["baseCcy"]="ETH";body["data"]!.AsArray().Add(row);
    }
}
