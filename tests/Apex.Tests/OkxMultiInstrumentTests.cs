using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Apex.Core.Engine;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.Tests;
public sealed partial class OkxConditionalTradingTests
{
    [Fact]
    public Task MultiInstrument_same_owner_PREPARE_admits_available_BTC_ETH_SOL_EUR_without_reseeding()=>OkxSyntheticClockIsolationTests.Run("multi-catalog");
    internal static async Task<int> MultiScenario(string scenario)
    {
        if(scenario=="catalog")await MultiCatalogCase();
        else if(scenario=="cycles")await MultiCyclesCase();
        else if(scenario=="fresh-cycles")await MultiFreshCyclesCase();
        else if(scenario.StartsWith("availability-",StringComparison.Ordinal))await MultiAvailabilityCase(scenario[13..]);
        else if(scenario.StartsWith("invariant-",StringComparison.Ordinal))await MultiInvariantCase(scenario[10..]);
        else return 98;
        return 0;
    }
    private static async Task MultiCatalogCase()
    {
        await using var fixture=await MultiFixture.Create();
        Assert.True(fixture.Loop.ReadStatus().ProductionProfileApproved);
        var after=fixture.Account.BrokerSession!.ReadState();
        Assert.Equal(NavalJson.Serialize(fixture.Seed.NavalBroker!.Baseline),NavalJson.Serialize(after.NavalBroker!.Baseline));
        Assert.Equal(NavalJson.Serialize(fixture.Seed.Naval!.Accounts),NavalJson.Serialize(after.Naval!.Accounts));
        Assert.Equal(fixture.Seed.NavalBroker.HighWater,after.NavalBroker.HighWater);
        var parent=after.NavalBroker.Operational!.Intents.First();Assert.Equal(NavalExecutionStage.Dispatching,parent.Stage);Assert.Null(parent.Outcome);
        Assert.All(after.NavalBroker.Operational.LastBalances!,b=>Assert.Null(b.Interest));
        if(Environment.GetEnvironmentVariable("APEX_MULTI_OLD_CORE") is {} oldCore)
        {
            var load=new System.Runtime.Loader.AssemblyLoadContext("old-core-canonical",isCollectible:true);
            try
            {
                var assembly=load.LoadFromAssemblyPath(oldCore);var type=assembly.GetType("Apex.Core.Engine.EngineState",true)!;
                var oldModel=JsonSerializer.Deserialize(NavalJson.Serialize(fixture.Seed),type,NavalJson.Options)!;
                string oldBytes=JsonSerializer.Serialize(oldModel,type,NavalJson.Options);Assert.Equal(NavalJson.Serialize(fixture.Seed),oldBytes);Assert.DoesNotContain("OrdinaryCatalog",oldBytes);
                using var doc=JsonDocument.Parse(oldBytes);NavalBrokerRaw.Require(doc.RootElement);
                var reopened=JsonSerializer.Deserialize<EngineState>(oldBytes,NavalJson.Options)!;NavalBrokerValidation.Validate(reopened,NavalBrokerValidation.Definition(reopened.NavalBroker!.Baseline));
                MultiCapture("old-dll-canonical-owner.json",oldBytes);Console.WriteLine("MULTI_OLD_DLL_CANONICAL_PASS");
            }
            finally{load.Unload();}
        }
        MultiCapture("prepared-owner.json",NavalJson.Serialize(after));
        Assert.Equal(0,fixture.Peer.Posts);
        fixture.Loop.Stop();
    }
    [Fact]
    public Task MultiInstrument_RunAsync_conditional_BTC_ETH_cycles_keep_SOL_pin_and_one_shared_cash_owner()=>OkxSyntheticClockIsolationTests.Run("multi-cycles");
    private static async Task MultiCyclesCase()
    {
        await using var fixture=await MultiFixture.Create();var loop=fixture.Loop;var before=fixture.Account.BrokerSession!.ReadState();
        Assert.Equal("EXACT_OWNED_INVENTORY_REQUIRED",(await loop.ExitAsync("foreign-BTC-holding",default)).Code);
        fixture.Market.Wait=false;
        using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(40));var running=loop.RunAsync(cancel.Token);
        try
        {
            for(int n=0;n<600;n++)
            {
                var status=loop.ReadStatus();var state=fixture.Account.BrokerSession.ReadState();
                if(state.Naval!.Fills.Count==6&&status.Mode=="RUNNING")break;
                Assert.True(n<599,"Multi scheduler stalled: "+status.Code+" / "+string.Join(",",status.Obligations)+" posts="+fixture.Peer.Posts+" fills="+state.Naval.Fills.Count);
                await Task.Delay(25);
            }
        }
        finally{loop.Stop();cancel.Cancel();try{await running;}catch(OperationCanceledException){}}
        var after=fixture.Account.BrokerSession.ReadState();
        Assert.Equal(new[]{"BTC-EUR","ETH-EUR"},fixture.Cycles.Select(c=>c.Instrument).OrderBy(i=>i,StringComparer.Ordinal).ToArray());
        Assert.All(fixture.Cycles,c=>Assert.True(c.Sold&&c.Canceled));Assert.Equal(6,fixture.Peer.Posts);
        Assert.Equal(NavalJson.Serialize(before.NavalBroker!.Baseline),NavalJson.Serialize(after.NavalBroker!.Baseline));
        Assert.Equal(before.Naval.ConfigDigest,after.Naval!.ConfigDigest);Assert.Equal(before.NavalBroker.Namespace,after.NavalBroker.Namespace);
        foreach(var i in before.NavalBroker.Operational!.Intents)Assert.Equal(NavalJson.Serialize(i),NavalJson.Serialize(after.NavalBroker.Operational!.Intents.Single(v=>v.Id==i.Id)));
        Assert.Equal(before.Naval.Accounts.Single().Cash+fixture.Cycles.Sum(c=>-c.Size*110+c.Size*.999m*c.SellPrice*.999m),after.Naval.Accounts.Single().Cash);
        Assert.Equal(before.Naval.Accounts.Single().Seed,after.Naval.Accounts.Single().Seed);
        Assert.All(after.Naval.Reservations.Where(r=>r.QuarantineMark==null),r=>Assert.True(r.Inventory==0&&r.Pending==0&&!r.ReductionUnsettled&&r.TerminalCoverage));
        var dust=Assert.Single(after.Naval.Reservations,r=>r.QuarantineMark!=null);Assert.Equal(before.Naval.Reservations.Single(r=>r.Id==dust.Id).Inventory,dust.Inventory);Assert.Equal(dust.Inventory*dust.QuarantineMark,dust.RiskHold);
        Assert.Single(after.NavalBroker.Operational!.Obligations);Assert.True(loop.ReadStatus().Evaluation.Entry>=2&&loop.ReadStatus().Evaluation.Exit>=2);
        MultiCapture("cycle-before.json",NavalJson.Serialize(before));MultiCapture("cycle-after.json",NavalJson.Serialize(after));
        Assert.Contains("BTC-EUR",fixture.Market.Candles);Assert.Contains("ETH-EUR",fixture.Market.Candles);
        await loop.DisposeAsync();await fixture.Account.DisconnectAsync();Assert.True(fixture.Account.OpenExisting(),fixture.Account.Status);Assert.Equal("STOP",fixture.Account.Coordinator!.Snapshot().Mode);
        Assert.Equal(new[]{"BTC-EUR","ETH-EUR","SOL-EUR"},NavalOrdinaryCatalogPolicy.Catalog(fixture.Account.BrokerSession!.ReadState().NavalBroker!).Select(i=>i.Instrument).OrderBy(i=>i,StringComparer.Ordinal));
    }
    [Fact]
    public Task Fresh_local_owner_RunAsync_BTC_ETH_cycles_excluded_BTC_SOL_untouched()=>OkxSyntheticClockIsolationTests.Run("multi-fresh-cycles");
    private static async Task MultiFreshCyclesCase()
    {
        if(!OperatingSystem.IsLinux())return; // Linux ordinary host; the native Windows variant is the gate test.
        await using var fixture=await MultiFixture.Create(freshLinux:true);var loop=fixture.Loop;var before=fixture.Account.BrokerSession!.ReadState();
        var started=loop.Start();Assert.True(started.Status==NavalMutationStatus.Applied,started.Code);Assert.Equal("RUNNING",loop.ReadStatus().Mode);
        fixture.Market.Wait=false;
        using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(40));var running=loop.RunAsync(cancel.Token);
        try
        {
            for(int n=0;n<600;n++)
            {
                var status=loop.ReadStatus();var state=fixture.Account.BrokerSession.ReadState();
                if(running.IsFaulted)await running;
                if(state.Naval!.Fills.Count==4&&status.Mode=="RUNNING")break;
                Assert.True(n<599,"Fresh scheduler stalled: "+status.Code+" / "+string.Join(",",status.Obligations)+" posts="+fixture.Peer.Posts+" fills="+state.Naval.Fills.Count);
                await Task.Delay(25);
            }
        }
        finally{loop.Stop();cancel.Cancel();try{await running;}catch(OperationCanceledException){}}
        var after=fixture.Account.BrokerSession.ReadState();var x=after.NavalBroker!.Operational!;
        Assert.Equal(new[]{"BTC-EUR","ETH-EUR"},fixture.Cycles.Select(c=>c.Instrument).OrderBy(i=>i,StringComparer.Ordinal).ToArray());Assert.All(fixture.Cycles,c=>Assert.True(c.Sold&&c.Canceled));
        Assert.Equal(NavalJson.Serialize(before.NavalBroker!.Baseline),NavalJson.Serialize(after.NavalBroker.Baseline));
        Assert.Equal(before.Naval!.Accounts.Single().Cash+fixture.Cycles.Sum(c=>-c.Size*110+c.Size*.999m*c.SellPrice*.999m),after.Naval!.Accounts.Single().Cash);
        Assert.All(after.Naval.Reservations,r=>Assert.True(r.Inventory==0&&r.Pending==0&&!r.ReductionUnsettled&&r.TerminalCoverage&&r.QuarantineMark==null));
        Assert.DoesNotContain(after.Naval.Fills,a=>a.Fill.Instrument=="SOL-EUR");Assert.Null(x.OrdinaryDemo!.Dust);
        var excluded=after.NavalBroker.Baseline.Observations.FiniteAllocation!.Excluded;
        Assert.Equal(decimal.Parse(MultiFixture.HeldSol,CultureInfo.InvariantCulture),excluded.Single(b=>b.Currency=="SOL").Cash);Assert.True(excluded.Single(b=>b.Currency=="BTC").Cash>0);
        foreach(string instrument in new[]{"BTC-EUR","ETH-EUR"})
        {
            decimal bought=after.Naval.Fills.Where(a=>a.Fill.Instrument==instrument&&!a.Fill.Reduction).Sum(a=>a.Fill.Quantity),sold=after.Naval.Fills.Where(a=>a.Fill.Instrument==instrument&&a.Fill.Reduction).Sum(a=>a.Fill.Quantity);
            Assert.True(bought>0&&sold>0&&sold<=bought,instrument);
            Assert.All(x.Protection.Where(q=>q.Instrument==instrument&&q.Quantity!=null),q=>Assert.True(q.Quantity<=bought,instrument+" protection beyond own fills"));
        }
        Assert.Empty(x.Obligations);
    }
    private static string MultiN(decimal n)=>n.ToString(CultureInfo.InvariantCulture);
    private static void MultiCapture(string name,string value)
    {if(Environment.GetEnvironmentVariable("APEX_MULTI_CAPTURE") is {} path){Directory.CreateDirectory(path);File.WriteAllText(Path.Combine(path,name),value);}}
    [Theory]
    [InlineData("btc-missing")][InlineData("eth-missing")][InlineData("btc-suspended")][InlineData("btc-quote")][InlineData("btc-expiry")][InlineData("btc-fee")]
    public Task MultiInstrument_missing_or_unsupported_EUR_pair_is_ineligible_without_quote_switch(string scenario)=>OkxSyntheticClockIsolationTests.Run("multi-availability-"+scenario);
    private static async Task MultiAvailabilityCase(string scenario)
    {
        await using var f=await MultiFixture.Create(scenario);
        Assert.DoesNotContain(scenario=="eth-missing"?"ETH-EUR":"BTC-EUR",f.Loop.ReadStatus().Instruments);
        Assert.DoesNotContain(f.Loop.ReadStatus().Instruments,i=>i.EndsWith("-USD",StringComparison.Ordinal)||i.EndsWith("-USDT",StringComparison.Ordinal));
        if(scenario.StartsWith("btc-",StringComparison.Ordinal)&&scenario!="btc-fee")
        {Assert.Equal("CURRENT_BTC_REFERENCE_UNPROVEN",(await f.Loop.RunOnceAsync(NavalFleet.Alpha,"ETH-EUR",default)).Code);Assert.Equal("RUNNING",f.Loop.ReadStatus().Mode);}
        Assert.Equal(0,f.Peer.Posts);f.Loop.Stop();
    }
    [Theory]
    [InlineData("unknown-symbol")][InlineData("budget")][InlineData("cash-replay")][InlineData("excluded")][InlineData("foreign-freeze")]
    [InlineData("cross-freeze")][InlineData("owner")][InlineData("seal")][InlineData("oversell")]
    public Task MultiInstrument_shared_financial_fences_remain_strict(string scenario)=>OkxSyntheticClockIsolationTests.Run("multi-invariant-"+scenario);
    private static async Task MultiInvariantCase(string scenario)
    {
        await using var f=await MultiFixture.Create();var session=f.Account.BrokerSession!;var original=session.ReadState();
        if(scenario=="unknown-symbol")Assert.Equal("INSTRUMENT_INELIGIBLE",(await f.Loop.RunOnceAsync(NavalFleet.Bravo,"ETH-USDT",default)).Code);
        else if(scenario=="budget")
        {
            var cutoff=new DateTimeOffset(f.F.Clock.GetUtcNow().UtcDateTime.Date,TimeSpan.Zero);
            var bars=Enumerable.Range(0,200).Select(n=>new NavalBar(cutoff.AddDays(n-200),100,n==199?112:101,99,n==199?110:100,1,true)).ToArray();
            var request=new NavalEvaluationRequest(NavalFleet.Alpha,new("ETH-EUR","ETH","EUR","SYNTHETIC","TRADE",bars),new("BTC-EUR","BTC","EUR","SYNTHETIC","TRADE",bars),cutoff,false);
            var decision=new NavalD1Evaluator().Evaluate(request);Assert.Equal(NavalAction.EntrySignal,decision.Action);Assert.Equal(NavalMutationStatus.Applied,f.Account.RegisterConfirmedSignal(request).Status);
            var reserved=session.Coordinator.ForFleet(f.F.Binding.Account,NavalFleet.Alpha).TryReserve(new("overbudget",decision.StableId,"ETH-EUR",1,110,100,130));
            Assert.Equal("ORDINARY_DEMO_ENTRY_CASH_CAP",reserved.Code);Assert.Equal(original.Naval!.Accounts.Single().Cash,session.ReadState().Naval!.Accounts.Single().Cash);
        }
        else if(scenario=="seal")
        {
            var b=original.NavalBroker!;Assert.NotNull(b.Operational!.OrdinaryCatalog);
            foreach(var c in new[]{b.Operational.OrdinaryCatalog! with{BaselineDigest=new string('a',64)},b.Operational.OrdinaryCatalog! with{AdmissionDigest=new string('b',64)},b.Operational.OrdinaryCatalog! with{Instruments=new[]{new NavalInstrument("BTC-USDT","BTC","USDT",.000001m,.001m,.01m)}}})
            {var bad=NavalJson.Clone(original);bad.NavalBroker=NavalBrokerValidation.Seal(b with{Operational=b.Operational with{OrdinaryCatalog=c}});Assert.ThrowsAny<Exception>(()=>NavalBrokerValidation.Validate(bad,NavalBrokerValidation.Definition(b.Baseline)));Assert.ThrowsAny<Exception>(()=>NavalBrokerExecutionValidation.Transition(original,bad));}
        }
        else if(scenario is "cash-replay" or "oversell" or "cross-freeze")
        {
            f.Market.Wait=false;Assert.Equal(NavalMutationStatus.Applied,(await f.Loop.RunOnceAsync(NavalFleet.Alpha,"BTC-EUR",default)).Status);
            Assert.Equal(NavalMutationStatus.Applied,(await f.Loop.ReconcileAsync(default)).Status);var filled=session.ReadState();var r=Assert.Single(filled.Naval!.Reservations,v=>v.Intent.Instrument=="BTC-EUR");
            Assert.Equal(f.Cycles.Single().Size*.999m,r.Inventory);Assert.True(filled.NavalBroker!.Operational!.LastBalances!.Single(b=>b.Currency=="BTC").Cash>r.Inventory);
            if(scenario=="cash-replay")
            {Assert.Equal(NavalMutationStatus.Applied,(await f.Loop.ReconcileAsync(default)).Status);Assert.Equal(NavalJson.Serialize(filled.Naval.Accounts),NavalJson.Serialize(session.ReadState().Naval!.Accounts));Assert.Equal(3,session.ReadState().Naval!.Fills.Count);var replay=session.Coordinator.ForFleet(f.F.Binding.Account,NavalFleet.Alpha).TryReserve(r.Intent with{CommandId="replay-with-fresh-command"});Assert.Equal("SIGNAL_CONSUMED",replay.Code);Assert.Equal(filled.Naval.Reservations.Count,session.ReadState().Naval!.Reservations.Count);}
            else if(scenario=="oversell")
            {
                var d=NavalOrdinaryCatalogPolicy.Definition(filled.NavalBroker);
                var sell=new NavalFill(f.F.Binding.Account,"BTC-EUR","synthetic-oversell",r.Id,true,r.Inventory+.00000001m,109.9m,0,"EUR",f.F.Clock.GetUtcNow());
                Assert.Throws<InvalidOperationException>(()=>NavalCashRiskPolicy.PostFill(filled.Naval,sell,d));Assert.Throws<InvalidOperationException>(()=>NavalCashRiskPolicy.PostFill(filled.Naval,sell with{Instrument="ETH-EUR",Quantity=r.Inventory},d));
            }
            else
            {var basis=f.F.Mutate!;f.F.Mutate=(p,b)=>{basis(p,b);if(p=="/api/v5/account/balance"){var eth=b["data"]![0]!["details"]!.AsArray().Single(v=>v!["ccy"]!.GetValue<string>()=="ETH")!;eth["frozenBal"]=MultiN(r.Inventory);eth["availBal"]=MultiN(decimal.Parse(eth["cashBal"]!.GetValue<string>(),CultureInfo.InvariantCulture)-r.Inventory);}};Assert.NotEqual(NavalMutationStatus.Applied,(await f.Loop.ReconcileAsync(default)).Status);Assert.Contains("BROKER_BALANCE_DISCREPANCY",f.Loop.ReadStatus().Obligations);}
            Assert.Equal(1,f.Peer.Posts);
        }
        else
        {
            var basis=f.F.Mutate!;f.F.Mutate=(p,b)=>
            {
                basis(p,b);
                if(scenario=="owner"&&p=="/api/v5/account/config")b["data"]![0]!["uid"]="999988887777";
                if(p=="/api/v5/account/balance")
                {
                    var btc=b["data"]![0]!["details"]!.AsArray().Single(v=>v!["ccy"]!.GetValue<string>()=="BTC")!;
                    if(scenario=="excluded")foreach(string field in new[]{"cashBal","availBal","eq"})btc[field]="2";
                    if(scenario=="foreign-freeze"){btc["frozenBal"]="0.01";btc["availBal"]=MultiN(decimal.Parse(btc["cashBal"]!.GetValue<string>(),CultureInfo.InvariantCulture)-.01m);}
                }
            };
            Assert.NotEqual(NavalMutationStatus.Applied,(await f.Loop.ReconcileAsync(default)).Status);Assert.Equal("STOP",f.Loop.ReadStatus().Mode);
            Assert.Equal(original.Naval!.Accounts.Single().Cash,session.ReadState().Naval!.Accounts.Single().Cash);Assert.Equal(0,f.Peer.Posts);
        }
        Assert.Throws<ArgumentException>(()=>session.Coordinator.ForFleet(f.F.Binding.Account with{AccountId="foreign-owner"},NavalFleet.Alpha));f.Loop.Stop();
    }
    private sealed class MultiMarket(TradingPeer peer,List<MultiCycle> cycles):HttpMessageHandler
    {
        internal bool Wait=true;
        internal readonly HashSet<string> Candles=new(StringComparer.Ordinal);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
        {
            var query=System.Web.HttpUtility.ParseQueryString(r.RequestUri!.Query);string instrument=query["instId"]!;
            peer.BidPrice=instrument.StartsWith("BTC-",StringComparison.Ordinal)?109.9m:109.99m;
            if(r.RequestUri.AbsolutePath=="/api/v5/market/candles")
            {
                Candles.Add(instrument);peer.CandleInstrument=instrument;peer.ExitSignal=cycles.Any(c=>c.Instrument==instrument&&!c.Sold);
            }
            if(r.RequestUri.AbsolutePath=="/api/v5/market/index-tickers")peer.CandleInstrument=instrument;
            var response=await new HttpMessageInvoker(peer,false).SendAsync(r,ct);
            if(r.RequestUri.AbsolutePath=="/api/v5/market/candles"&&(Wait||instrument=="SOL-EUR"))
            {var b=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;foreach(var row in b["data"]!.AsArray()){row![2]="101";row[3]="99";row[4]="100";}response.Content=new StringContent(b.ToJsonString());}
            return response;
        }
    }
    private sealed class MultiCycle(string instrument,string client,string attachment,string order,string exitOrder,string algo,decimal size,DateTimeOffset at)
    {
        internal readonly string Instrument=instrument,Client=client,Attachment=attachment,Order=order,ExitOrder=exitOrder,Algo=algo;
        internal readonly decimal Size=size;internal readonly DateTimeOffset At=at;
        internal decimal SellPrice=>Instrument=="BTC-EUR"?109.9m:109.99m;
        internal bool Canceled,Sold;internal string ExitClient="";internal DateTimeOffset SellUtc;
    }
    private static void MultiOrdinary(MultiFixture fixture,string p,JsonNode b)
    {
        var f=fixture.F;var cycles=fixture.Cycles;
        if(cycles.Count==0)return;
        if(p=="/api/v5/account/balance")
        {
            var rows=b["data"]![0]!["details"]!.AsArray();decimal debit=cycles.Sum(c=>c.Size*110-(c.Sold?c.Size*.999m*c.SellPrice*.999m:0));
            var q=rows.Single(v=>v!["ccy"]!.GetValue<string>()=="EUR")!;foreach(string k in new[]{"cashBal","availBal","eq"})q[k]=MultiN(decimal.Parse(q[k]!.GetValue<string>(),CultureInfo.InvariantCulture)-debit);
            foreach(string asset in new[]{"BTC","ETH"})
            {var row=rows.Single(v=>v!["ccy"]!.GetValue<string>()==asset)!;decimal own=cycles.Where(c=>c.Instrument==asset+"-EUR"&&!c.Sold).Sum(c=>c.Size*.999m);foreach(string k in new[]{"cashBal","availBal","eq"})row[k]=MultiN(decimal.Parse(row[k]!.GetValue<string>(),CultureInfo.InvariantCulture)+own);}
            return;
        }
        if(p=="/api/v5/trade/order-algo")
        {
            var c=cycles.SingleOrDefault(c=>f.Paths.Last().Contains(c.Attachment,StringComparison.Ordinal)||f.Paths.Last().Contains("algoId="+c.Algo,StringComparison.Ordinal));if(c==null)return;
            var a=OkxOwnedExitDiagnosticTests.Oracle("stream-algo-schema.json","detail")["data"]![0]!.DeepClone();b["data"]=new JsonArray(a);a["tdMode"]="cash";a["side"]="sell";a["tradeQuoteCcy"]="EUR";a["ordType"]="oco";a["slOrdPx"]="-1";a["tpOrdPx"]="-1";a["slTriggerPxType"]="last";a["tpTriggerPxType"]="last";a["ordIdList"]=new JsonArray();var r=fixture.Account.BrokerSession!.ReadState().Naval!.Reservations.Single(r=>fixture.Account.BrokerSession!.ReadState().NavalBroker!.Operational!.Intents.Any(i=>i.ReservationId==r.Id&&i.ClientId==c.Client));a["instId"]=c.Instrument;a["algoId"]=c.Algo;a["algoClOrdId"]=c.Attachment;a["sz"]=MultiN(c.Size*.999m);a["slTriggerPx"]=MultiN(r.Intent.StopPrice);a["tpTriggerPx"]=MultiN(r.Intent.TakeProfitPrice);a["state"]=c.Canceled?"canceled":"live";return;
        }
        string? kind=p switch{"/api/v5/trade/order" or "/api/v5/trade/orders-history" or "/api/v5/trade/orders-history-archive"=>"order","/api/v5/trade/fills" or "/api/v5/trade/fills-history"=>"fills","/api/v5/account/bills" or "/api/v5/account/bills-archive"=>"bills",_=>null};
        if(kind==null||f.Paths.Last().Contains("after=",StringComparison.Ordinal))return;
        var rowsOut=b["data"]!.AsArray();
        foreach(var c in cycles)
        foreach(bool sell in c.Sold?new[]{false,true}:new[]{false})
        {
            string id=sell?c.ExitOrder:c.Order,client=sell?c.ExitClient:c.Client;decimal size=sell?c.Size*.999m:c.Size,price=sell?c.SellPrice:110m,fee=sell?size*price*.001m:size*.001m;string at=(sell?c.SellUtc:c.At).ToUnixTimeMilliseconds().ToString();
            var v=OkxOwnedExitDiagnosticTests.Oracle("recovery-schema.json",kind)["data"]![0]!.DeepClone();v["instId"]=c.Instrument;v["ordId"]=id;v["clOrdId"]=client;v["tradeId"]=(long.Parse(id)+3000).ToString();v["fee"]=MultiN(-fee);v["ts"]=at;
            if(kind=="order")
            {v["side"]=sell?"sell":"buy";v["state"]="filled";v["ordType"]="fok";v["sz"]=MultiN(size);v["accFillSz"]=MultiN(size);v["fillSz"]=MultiN(size);v["px"]=MultiN(price);v["avgPx"]=MultiN(price);v["fillPx"]=MultiN(price);v["feeCcy"]=sell?"EUR":c.Instrument.Split('-')[0];v["tradeQuoteCcy"]="EUR";v["cTime"]=at;v["uTime"]=at;v["fillTime"]=at;v["attachAlgoOrds"]=sell?new JsonArray():new JsonArray(new JsonObject{["attachAlgoId"]=(long.Parse(c.Algo)-1).ToString(),["attachAlgoClOrdId"]=c.Attachment,["failCode"]=""});v.AsObject().Remove("ts");}
            if(kind=="fills"){v["side"]=sell?"sell":"buy";v["fillSz"]=MultiN(size);v["fillPx"]=MultiN(price);v["feeCcy"]=sell?"EUR":c.Instrument.Split('-')[0];v["tradeQuoteCcy"]="EUR";v["fillTime"]=at;v["billId"]=(long.Parse(id)*10).ToString();}
            if(kind=="bills")
            {v["billId"]=(long.Parse(id)*10).ToString();v["ccy"]=sell?"EUR":c.Instrument.Split('-')[0];v["type"]="2";v["mgnMode"]="cash";v["sz"]=MultiN(size);v["px"]=MultiN(price);v["balChg"]=MultiN(sell?size*price-fee:size-fee);var counter=v.DeepClone();counter["billId"]=(long.Parse(id)*10+1).ToString();counter["ccy"]=sell?c.Instrument.Split('-')[0]:"EUR";counter["fee"]="0";counter["balChg"]=MultiN(sell?-size:-size*price);rowsOut.Add(counter);}
            rowsOut.Add(v);
        }
        if(p=="/api/v5/trade/order")b["data"]=new JsonArray(rowsOut.Where(o=>f.Paths.Last().Contains(o!["clOrdId"]!.GetValue<string>(),StringComparison.Ordinal)||f.Paths.Last().Contains("ordId="+o["ordId"]!.GetValue<string>(),StringComparison.Ordinal)).Select(o=>o!.DeepClone()).ToArray());
        else{string key=kind=="order"?"ordId":kind=="fills"?"tradeId":"billId";b["data"]=new JsonArray(rowsOut.OrderByDescending(o=>long.Parse(o![key]!.GetValue<string>())).Select(o=>o!.DeepClone()).ToArray());}
    }
    private sealed class MultiFixture:IAsyncDisposable
    {
        internal readonly OperationalPeer F;internal readonly FiniteDemoPeer History;internal readonly TradingPeer Peer;internal readonly MultiMarket Market;
        internal OkxNavalRuntime Runtime=null!;internal OkxAccountConnectionService Account=null!;internal OkxOperationalTradingService Loop=null!;
        internal readonly List<MultiCycle> Cycles=new();
        internal EngineState Seed=null!;
        internal Action EnterBenchmark=()=>{};
        // Production Windows first-run creation wired to this synthetic exchange; only the
        // native dialogs/CredUI are replaced by the supplied test surface.
        internal WindowsOkxLocalEnrollment LocalEnrollment(IWindowsLocalEnrollmentUi ui)=>WindowsOkxLocalEnrollment.ForTests(ui,new(
            (k,s,p,ct)=>OkxEnrollmentDiscoveryClient.RunAsync("Demo","DemoTradingApi",k,s,p,null,null,ct,F),
            (runtime,root,binding,approval)=>OkxAccountConnectionService.ForTests(runtime,root,binding,approval,F.Clock,
                (s,k,d,x)=>OkxExecutionObservationClient.ForTests(F,F.Clock,s,k,d,x,(t,c)=>{F.Clock.Advance(t);return Task.CompletedTask;}),
                (s,k,d,x)=>OkxObservationStream.ForTests((u,c)=>Task.FromResult<WebSocket>(new OperationalSocket(u)),F.Clock,s,k,d,x),()=>OkxNavalStore.OpenNative(root,binding))));
        private MultiFixture(OperationalPeer f,FiniteDemoPeer history){F=f;History=history;Peer=new(f){CandleInstrument="SOL-EUR"};Market=new(Peer,Cycles);}
        private static int nextMixedUid=99220000;
        internal const string HeldSol="3.5";
        internal static async Task<MultiFixture> Create(string availability="",bool windowsLocalSeed=false,bool windowsLocalCreate=false,bool freshLinux=false)
        {
            string uid=Guid.NewGuid().ToString("N");
            string accountUid=Interlocked.Increment(ref nextMixedUid).ToString();
            var binding=OkxConfiguration.Bind(new OkxNavalOptions{Schema="okx-naval-config-v1",Scopes=new[]{new OkxScopeOptions{ScopeId=windowsLocalCreate||freshLinux?WindowsOkxLocalEnrollment.ScopeId:"account",Environment="OkxDemo",Region="EEA",ExpectedUid=accountUid,ExpectedMainUid=accountUid,QuoteCurrency="EUR",CredentialBindingId=windowsLocalCreate||freshLinux?WindowsOkxLocalEnrollment.CredentialBindingId:"key",SafetyVersion="okx-cash-v1"}}})[0];
            var f=new OperationalPeer(binding:binding,finite:true,native:windowsLocalSeed||windowsLocalCreate,seedCredential:!windowsLocalCreate);var h=new FiniteDemoPeer(f){BaseCurrency="SOL"};var fixture=new MultiFixture(f,h);bool benchmark=false;
            f.Mutate=(p,b)=>
            {
                OkxOrdinaryMixedAccountTests.Shape(f,h,p,b);
                if(windowsLocalCreate||freshLinux)
                {
                    // Pre-existing holdings: BTC and SOL are held (excluded), ETH is unheld (finite-origin anchor).
                    if(p=="/api/v5/account/balance")
                        foreach(var row in b["data"]![0]!["details"]!.AsArray())
                        {
                            string c=row!["ccy"]!.GetValue<string>();
                            if(c is "SOL" or "ETH")foreach(string k in new[]{"cashBal","availBal","eq"})row[k]=c=="SOL"?HeldSol:"0";
                        }
                    if(p=="/api/v5/account/instruments"&&!benchmark)
                    {var eth=b["data"]![0]!.DeepClone();eth["instId"]="ETH-USDC";eth["baseCcy"]="ETH";eth["lotSz"]="0.000001";eth["minSz"]="0.001";eth["tickSz"]="0.01";b["data"]!.AsArray().Add(eth);}
                }
                if(p=="/api/v5/account/instruments")
                {
                    b["data"]![0]!["minSz"]="0.01";
                    if(benchmark)
                    {
                        var btc=b["data"]![0]!.DeepClone();btc["instId"]="BTC-USDC";btc["baseCcy"]="BTC";btc["lotSz"]="0.00000001";btc["minSz"]="0.0001";btc["tickSz"]="0.1";b["data"]!.AsArray().Add(btc);
                        var eth=btc.DeepClone();eth["instId"]="ETH-USDC";eth["baseCcy"]="ETH";eth["lotSz"]="0.000001";eth["minSz"]="0.001";eth["tickSz"]="0.01";b["data"]!.AsArray().Add(eth);
                    }
                }
                Denominate(b);
                if(benchmark&&p=="/api/v5/account/instruments")
                {
                    var rows=b["data"]!.AsArray();var btc=rows.Single(v=>v!["baseCcy"]!.GetValue<string>()=="BTC")!;
                    if(availability=="btc-missing")rows.Remove(btc);if(availability=="eth-missing")rows.Remove(rows.Single(v=>v!["baseCcy"]!.GetValue<string>()=="ETH"));
                    if(availability=="btc-suspended")btc["state"]="suspend";
                    if(availability=="btc-quote"){btc["quoteCcy"]="USD";btc["tradeQuoteCcyList"]=new JsonArray("USD");}
                    if(availability=="btc-expiry")btc["expTime"]="1600000000000";
                }
                if(benchmark&&availability=="btc-fee"&&p=="/api/v5/account/trade-fee"&&f.Paths.Last().Contains("instId=BTC-EUR",StringComparison.Ordinal))b["data"]![0]!["feeGroup"]![0]!["taker"]="-0.002";
                if(benchmark)MultiOrdinary(fixture,p,b);
                if(benchmark&&p is "/api/v5/trade/order" or "/api/v5/trade/orders-history" or "/api/v5/trade/orders-history-archive")
                    foreach(var row in b["data"]!.AsArray().Where(row=>row!["state"]!.GetValue<string>()=="filled")){row!["posSide"]=p=="/api/v5/trade/order"?"net":"";row["tradeQuoteCcy"]=p=="/api/v5/trade/order"?"":"EUR";}
            };
            fixture.EnterBenchmark=()=>benchmark=true;
            if(windowsLocalCreate)return fixture;
            try
            {
            if(freshLinux)
            {
                // Same Core owner shape as the Windows first-run (windows-local provenance, no history, held BTC/SOL),
                // driven through the Linux ordinary host so the trading phase runs in-process with visible exceptions.
                f.Runtime.FiniteFundingPremise=((NavalFiniteOperatorPremise)OkxCurrentMarketFundingTests.Premise(f)) with{Provenance=NavalCurrentMarketFunding.WindowsLocalProvenance};
                await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default),f.Service.Status);await f.Service.DisconnectAsync();benchmark=true;
                fixture.Runtime=OkxNavalRuntime.ComposeLinuxDemo(f.Root,f.Binding,f,f,()=>true);fixture.Account=AccountFor(f,fixture.Runtime);
                var freshRequest=f.Request with{Session=Guid.NewGuid()};var freshLease=await fixture.Runtime.Activation.RequestAsync(freshRequest,default);Assert.True(await fixture.Account.ConnectAsync(freshRequest,freshLease!,default),fixture.Account.Status);
                fixture.Loop=OkxOperationalTradingService.Create(fixture.Account,f.Clock,(s,k,d,x)=>OkxExecutionClient.ForTests(fixture.Peer,f.Clock,s,k,d,x),fixture.Market);
                fixture.Seed=fixture.Account.BrokerSession!.ReadState();Assert.Empty(fixture.Seed.Naval!.Fills);
                Assert.Equal("ORDINARY_DEMO_PREPARED",(await fixture.Loop.PrepareOrdinaryDemoAsync(default)).Code);
                Assert.Equal(new[]{"BTC-EUR","ETH-EUR","SOL-EUR"},fixture.Loop.ReadStatus().Instruments.OrderBy(i=>i,StringComparer.Ordinal).ToArray());
                freshRequest=freshRequest with{Purpose=OkxActivationPurpose.Execute,Session=fixture.Account.TradingSessionId};freshLease=await fixture.Runtime.Activation.RequestAsync(freshRequest,default);Assert.True(fixture.Loop.Arm(freshRequest,freshLease!));
                await fixture.Loop.ReconcileAsync(default);
                fixture.Peer.BeforeResponse=()=>
                {
                    var peer=fixture.Peer;Assert.InRange(peer.Size*110,0m,10m);peer.ExpectedSellSize=peer.Size*.999m;peer.ProtectionCanceled=false;
                    int n=fixture.Cycles.Count;peer.EntryOrderId=(1777+n*10).ToString();peer.ExitOrderId=(1778+n*10).ToString();peer.GeneratedAlgoId=(1889+n*10).ToString();
                    string instrument=fixture.Account.BrokerSession!.ReadState().NavalBroker!.Operational!.Intents.Last().Instrument;
                    fixture.Cycles.Add(new(instrument,peer.Client,peer.Attachment,peer.EntryOrderId,peer.ExitOrderId,peer.GeneratedAlgoId,peer.Size,f.Clock.GetUtcNow()));
                };
                fixture.Peer.BeforeMutationResponse=kind=>{var c=fixture.Cycles.Last();if(kind=="cancel")c.Canceled=true;if(kind=="sell"){c.ExitClient=fixture.Peer.ExitClient;c.Sold=true;c.SellUtc=f.Clock.GetUtcNow();if(fixture.Cycles.Count(v=>v.Sold)==2)fixture.Market.Wait=true;}}; // exactly two cycles: flat candles after the 2nd exit
                return fixture;
            }
            f.Runtime.FiniteFundingPremise=(NavalFiniteOperatorPremise)OkxCurrentMarketFundingTests.Premise(f);await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
            await using(var finite=OkxOperationalTradingService.Create(f.Service,f.Clock,(s,k,d,x)=>OkxExecutionClient.ForTests(h,f.Clock,s,k,d,x),h))
            {
                finite.BindFiniteSafety(()=>true);
                string result=await OkxFiniteDemoSequence.RunAsync(finite,f.Service,uid,stage=>
                {
                    if(stage=="FILLED_BUY_SUBMIT_RESULT")f.WriteBoundary=b=>{if(h.SellClient!=""&&b==Apex.Desktop.Services.AtomicWriteBoundary.BeforeTempCreate)throw new IOException("synthetic-post-send-commit-fault");};
                },default);
                Assert.Equal("OWNED_EXIT_INCOMPLETE_CHECK_RESIDUAL_PROTECTION",result);f.WriteBoundary=null;
            }
            var failed=f.Service.BrokerSession!.ReadState();var original=failed.NavalBroker!.Operational!.Intents.Last();Assert.Equal(NavalExecutionStage.Dispatching,original.Stage);Assert.Null(original.Outcome);
            await f.Service.DisconnectAsync();benchmark=true;
            // Actual-shaped retained null-ACK buy, not a fabricated broker response.
            // This synthetic seed projection preserves all body/ID/fill/capital operands;
            // the production reopen validates its canonical seal before any admission.
            var seed=JsonSerializer.Deserialize<EngineState>(File.ReadAllText(f.Target),NavalJson.Options)!;
            var buy=seed.NavalBroker!.Operational!.Intents.First(i=>i.Operation==NavalExecutionOperation.Submit&&!i.Reduction) with{Stage=NavalExecutionStage.Dispatching,Outcome=null};
            seed.NavalBroker=NavalBrokerValidation.Seal(seed.NavalBroker with{Operational=seed.NavalBroker.Operational with{Intents=seed.NavalBroker.Operational.Intents.Select(i=>i.Id==buy.Id?buy:i).ToArray()}});
            File.WriteAllText(f.Target,NavalJson.Serialize(seed));original=buy;
            fixture.Seed=NavalJson.Clone(seed);
            if(windowsLocalSeed)return fixture;
            fixture.Runtime=OkxNavalRuntime.ComposeLinuxDemo(f.Root,f.Binding,f,f,()=>true);
            fixture.Account=AccountFor(f,fixture.Runtime);
            var request=f.Request with{Session=Guid.NewGuid()};var lease=await fixture.Runtime.Activation.RequestAsync(request,default);Assert.True(await fixture.Account.ConnectAsync(request,lease!,default),fixture.Account.Status);
            fixture.Loop=OkxOperationalTradingService.Create(fixture.Account,f.Clock,(s,k,d,x)=>OkxExecutionClient.ForTests(fixture.Peer,f.Clock,s,k,d,x),fixture.Market);
            await fixture.Loop.ReconcileAsync(default);fixture.Seed=fixture.Account.BrokerSession!.ReadState();Assert.Equal(2,fixture.Seed.Naval!.Fills.Count);
            Assert.Equal("ORDINARY_DEMO_PREPARED",(await fixture.Loop.PrepareOrdinaryDemoAsync(default)).Code);benchmark=true;
            var expected=availability==""?new[]{"BTC-EUR","ETH-EUR","SOL-EUR"}:availability=="eth-missing"?new[]{"BTC-EUR","SOL-EUR"}:new[]{"ETH-EUR","SOL-EUR"};
            Assert.Equal(expected,fixture.Loop.ReadStatus().Instruments.OrderBy(i=>i,StringComparer.Ordinal).ToArray());
            var state=fixture.Account.BrokerSession!.ReadState();Assert.Equal(6,state.NavalBroker!.Operational!.LastBalances!.Count);Assert.Single(state.NavalBroker.Operational.Obligations);Assert.NotNull(state.NavalBroker.Operational.OrdinaryDemo);Assert.Equal(NavalJson.Serialize(original),NavalJson.Serialize(state.NavalBroker.Operational.Intents.Single(i=>i.Id==original.Id)));
            request=request with{Purpose=OkxActivationPurpose.Execute,Session=fixture.Account.TradingSessionId};lease=await fixture.Runtime.Activation.RequestAsync(request,default);Assert.True(fixture.Loop.Arm(request,lease!));var observed=await fixture.Loop.ReconcileAsync(default);Assert.True(observed.Status==NavalMutationStatus.Applied,observed.Code+" "+string.Join(",",fixture.Loop.ReadStatus().Obligations));Assert.Equal(NavalMutationStatus.Applied,fixture.Loop.Start().Status);
            fixture.Peer.BeforeResponse=()=>
            {
                var peer=fixture.Peer;Assert.InRange(peer.Size*110,0m,10m);peer.ExpectedSellSize=peer.Size*.999m;peer.ProtectionCanceled=false;
                int n=fixture.Cycles.Count;peer.EntryOrderId=(1777+n*10).ToString();peer.ExitOrderId=(1778+n*10).ToString();peer.GeneratedAlgoId=(1889+n*10).ToString();
                string instrument=fixture.Account.BrokerSession!.ReadState().NavalBroker!.Operational!.Intents.Last().Instrument;
                fixture.Cycles.Add(new(instrument,peer.Client,peer.Attachment,peer.EntryOrderId,peer.ExitOrderId,peer.GeneratedAlgoId,peer.Size,f.Clock.GetUtcNow()));
            };
            fixture.Peer.BeforeMutationResponse=kind=>{var c=fixture.Cycles.Last();if(kind=="cancel")c.Canceled=true;if(kind=="sell"){c.ExitClient=fixture.Peer.ExitClient;c.Sold=true;c.SellUtc=f.Clock.GetUtcNow();}};
            return fixture;
            }
            catch{f.WriteBoundary=null;await fixture.DisposeAsync();throw;}
        }
        private static OkxAccountConnectionService AccountFor(OperationalPeer f,OkxNavalRuntime runtime)=>OkxAccountConnectionService.ForTests(runtime,f.Root,f.Binding,f,f.Clock,
            (s,k,d,x)=>OkxExecutionObservationClient.ForTests(f,f.Clock,s,k,d,x,(t,ct)=>{f.Clock.Advance(t);return Task.CompletedTask;}),
            (s,k,d,x)=>OkxObservationStream.ForTests((u,ct)=>Task.FromResult<WebSocket>(new OperationalSocket(u)),f.Clock,s,k,d,x),()=>OkxNavalStore.ForTests(f.Root,f.Binding));
        private static void Denominate(JsonNode n)
        {
            if(n is JsonObject obj)foreach(var pair in obj.ToArray()){if(pair.Value is JsonValue v&&v.TryGetValue<string>(out var s))obj[pair.Key]=s.Replace("USDC","EUR",StringComparison.Ordinal);else if(pair.Value!=null)Denominate(pair.Value);}
            if(n is JsonArray a)for(int i=0;i<a.Count;i++){if(a[i] is JsonValue v&&v.TryGetValue<string>(out var s))a[i]=s.Replace("USDC","EUR",StringComparison.Ordinal);else if(a[i]!=null)Denominate(a[i]!);}
        }
        public async ValueTask DisposeAsync(){if(Loop!=null)await Loop.DisposeAsync();if(Account!=null)await Account.DisposeAsync();if(Runtime!=null)await Runtime.DisposeAsync();Market.Dispose();Peer.Dispose();History.Dispose();await F.DisposeAsync();}
    }
}
