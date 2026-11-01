using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
namespace Apex.Tests;
internal static class TradingEconomicsFixture
{
    internal static void InstallTriggeredExit(OperationalPeer f,TradingPeer peer)
    {
        Install(f,peer,peer.Size,peer.Size*.11m,true);var entry=f.Mutate!;
        string N(decimal n)=>n.ToString("0.############################",CultureInfo.InvariantCulture);
        f.Mutate=(path,body)=>
        {
            entry(path,body);
            if(path=="/api/v5/trade/order-algo") {body["data"]![0]!["state"]="effective";body["data"]![0]!["ordIdList"]=new JsonArray("778");}
            if((path.StartsWith("/api/v5/trade/fills",StringComparison.Ordinal)||path.StartsWith("/api/v5/account/bills",StringComparison.Ordinal))&&body["data"]!.AsArray().Count>0)
            {
                var row=body["data"]![0]!.DeepClone();row["ordId"]="778";row["clOrdId"]="";row["tradeId"]="902";row["billId"]="903";row["fee"]=N(-peer.Size*.12m);
                if(path.Contains("fills",StringComparison.Ordinal)){row["side"]="sell";row["fillPx"]="120";}
                else {row["px"]="120";row["subType"]="2";row["balChg"]=N(peer.Size*119.88m);}
                body["data"]!.AsArray().Insert(0,row);
            }
            if(path.StartsWith("/api/v5/trade/orders-history",StringComparison.Ordinal)&&body["data"]!.AsArray().Count>0)
            {
                var child=body["data"]![0]!.DeepClone();child["ordId"]="778";child["clOrdId"]="";child["tradeId"]="902";child["side"]="sell";child["fillPx"]="120";child["avgPx"]="120";child["fee"]=N(-peer.Size*.12m);child["attachAlgoOrds"]=new JsonArray();
                body["data"]!.AsArray().Insert(0,child);
            }
            if(path=="/api/v5/trade/order"&&f.Paths.Last().Contains("ordId=778",StringComparison.Ordinal))
            {
                var row=body["data"]![0]!;row["ordId"]="778";row["clOrdId"]="";row["tradeId"]="902";row["side"]="sell";row["fee"]=N(-peer.Size*.12m);row["fillPx"]="120";row["avgPx"]="120";row["attachAlgoOrds"]=new JsonArray();
            }
            if(path=="/api/v5/account/balance")
            {
                var quote=body["data"]![0]!["details"]![0]!.DeepClone();foreach(string field in new[]{"cashBal","availBal","eq"})quote[field]=N(1234.56m+peer.Size*9.77m);
                body["data"]![0]!["details"]=new JsonArray(quote);
            }
        };
    }
    internal static void Install(OperationalPeer f,TradingPeer peer,decimal quantity,decimal fee,bool protection=false,bool baseFee=false)
    {
        string N(decimal n)=>n.ToString("0.############################",CultureInfo.InvariantCulture);
        var dir=new DirectoryInfo(AppContext.BaseDirectory);while(dir!=null&&!Directory.Exists(Path.Combine(dir.FullName,"tests/Apex.Adapters.Okx.Tests/Fixtures")))dir=dir.Parent;
        var oracle=JsonNode.Parse(File.ReadAllText(Path.Combine(dir!.FullName,"tests/Apex.Adapters.Okx.Tests/Fixtures/ExecutionObservations/recovery-schema.json")))!;
        var algoOracle=JsonNode.Parse(File.ReadAllText(Path.Combine(dir.FullName,"tests/Apex.Adapters.Okx.Tests/Fixtures/ExecutionObservations/stream-algo-schema.json")))!;
        var prior=System.Text.Json.JsonSerializer.Deserialize<Apex.Core.Engine.EngineState>(File.ReadAllText(f.Target))!.Naval!.Fills.FirstOrDefault(p=>!p.Fill.Reduction);
        string now=(prior?.Fill.TradeUtc??f.Clock.GetUtcNow()).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        f.Mutate=(path,body)=>
        {
            string? kind=path switch{"/api/v5/trade/order"=>"order","/api/v5/trade/orders-history" or "/api/v5/trade/orders-history-archive"=>"order","/api/v5/trade/fills" or "/api/v5/trade/fills-history"=>"fills","/api/v5/account/bills" or "/api/v5/account/bills-archive"=>"bills",_=>null};
            if(kind!=null)
            {
                if(f.Paths.Last().Contains("after=",StringComparison.Ordinal)){body["data"]=new JsonArray();return;}
                var row=oracle["endpoints"]![kind]!["control"]!["data"]![0]!.DeepClone();
                row["instId"]="BTC-USDC";row["ordId"]="777";row["clOrdId"]=peer.Client;row["tradeId"]="900";row["fillTime"]=now;
                if(kind=="order")
                {
                    row["cTime"]=now;row["uTime"]=now;row["state"]=quantity==peer.Size?"filled":"canceled";row["sz"]=N(peer.Size);row["accFillSz"]=N(quantity);row["fillSz"]=N(quantity);row["fillPx"]="110";row["avgPx"]="110";row["px"]="110";
                    row["fee"]="-"+N(fee);row["feeCcy"]=baseFee?"BTC":"USDC";row["tradeQuoteCcy"]="USDC";row["ordType"]="fok";row["tgtCcy"]="base_ccy";
                    row["attachAlgoOrds"]=new JsonArray(new JsonObject{["attachAlgoId"]="888",["attachAlgoClOrdId"]=peer.Attachment,["failCode"]=""});
                }
                if(kind=="fills")
                {row["ts"]=now;row["billId"]="901";row["fillSz"]=N(quantity);row["fillPx"]="110";row["fee"]="-"+N(fee);row["feeCcy"]=baseFee?"BTC":"USDC";row["tradeQuoteCcy"]="USDC";}
                if(kind=="bills")
                {row["ts"]=now;row["billId"]="901";row["ccy"]=baseFee?"BTC":"USDC";row["type"]="2";row["mgnMode"]="cash";row["sz"]=N(quantity);row["px"]="110";row["fee"]="-"+N(fee);row["balChg"]=N(-(baseFee?fee:quantity*110+fee));}
                body["data"]=new JsonArray(row);
            }
            if(protection&&path=="/api/v5/trade/order-algo")
            {
                var state=System.Text.Json.JsonSerializer.Deserialize<Apex.Core.Engine.EngineState>(File.ReadAllText(f.Target))!;var reservation=state.Naval!.Reservations.Single();
                var algo=algoOracle["endpoints"]!["detail"]!["control"]!["data"]![0]!.DeepClone();
                algo["instId"]="BTC-USDC";algo["algoId"]="888";algo["algoClOrdId"]=peer.Attachment;algo["side"]="sell";algo["ordType"]="oco";algo["tradeQuoteCcy"]="USDC";
                algo["slOrdPx"]="-1";algo["tpOrdPx"]="-1";algo["slTriggerPxType"]="last";algo["tpTriggerPxType"]="last";
                algo["sz"]=N(quantity-(baseFee?fee:0));algo["slTriggerPx"]=N(reservation.Intent.StopPrice);algo["tpTriggerPx"]=N(reservation.Intent.TakeProfitPrice);algo["cTime"]=now;algo["uTime"]=now;
                body["data"]=new JsonArray(algo);
            }
            if(path=="/api/v5/account/balance")
            {
                var quote=body["data"]![0]!["details"]![0]!;foreach(string field in new[]{"cashBal","availBal","eq"})quote[field]=N(1234.56m-quantity*110-(baseFee?0:fee));
                var inventory=quote.DeepClone();inventory["ccy"]="BTC";foreach(string field in new[]{"cashBal","availBal","eq"})inventory[field]=N(quantity-(baseFee?fee:0));
                body["data"]![0]!["details"]=new JsonArray(quote.DeepClone(),inventory);
            }
        };
    }
}
