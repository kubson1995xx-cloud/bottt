using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Engine;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Apex.Desktop.Services;
using Apex.Okx.DemoAcceptance;
using Xunit;
namespace Apex.Tests;

// Isolated native encrypted process; every wire is synthetic and friend-only.
internal static class OkxMixedHostNative
{
    private static string N(decimal n)=>n.ToString(CultureInfo.InvariantCulture);
    private static JsonNode Oracle(string endpoint)=>OkxOwnedExitDiagnosticTests.Oracle("recovery-schema.json",endpoint)["data"]![0]!.DeepClone();
    private static EngineState State(OperationalPeer f)=>JsonSerializer.Deserialize<EngineState>(File.ReadAllText(f.Target))!;
    internal static async Task<int> Run(string scenario,string root)
    {
        bool cashCompatibility=scenario.StartsWith("mixed-cash-",StringComparison.Ordinal);
        if(cashCompatibility)scenario="mixed-"+scenario.Substring(11);
        Assert.StartsWith("/home/ubuntu/apex-deliveries/linux-host-native-",root);LinuxInput.Harden();OkxLinuxPrivate.AdmitDirectory(root,true);
        AppDomain.CurrentDomain.FirstChanceException+=(_,e)=>{if(e.Exception is not OperationCanceledException&&e.Exception.StackTrace?.Contains("LinuxDemoHost",StringComparison.Ordinal)==true)Console.WriteLine("MIXED_SAFE_EXCEPTION_TYPE "+e.Exception.GetType().FullName+" "+e.Exception.StackTrace);};
        string fixtureQuote=cashCompatibility?"EUR":"USDC";
        var binding=OkxConfiguration.Bind(new OkxNavalOptions{Schema="okx-naval-config-v1",Scopes=new[]{new OkxScopeOptions{ScopeId="finite-demo",Environment="OkxDemo",Region="EEA",ExpectedUid="9911112222",ExpectedMainUid="9911112222",QuoteCurrency=fixtureQuote,CredentialBindingId="foreground-demo",SafetyVersion="okx-cash-v1"}}})[0];
        await using var f=new OperationalPeer(root,binding,preserve:true,finite:true);using var history=new FiniteDemoPeer(f){BaseCurrency="SOL",OwnFreezes=true};
        var vault=new LinuxDemoCredentialVault(Path.Combine(root,"vault"));bool reopen=scenario=="mixed-reopen";
        bool recovered=false;
        f.Mutate=(p,b)=>{if(recovered)History(f,history,p,b);else OkxOrdinaryMixedAccountTests.Shape(f,history,p,b);FixtureQuote(b,fixtureQuote);};
        if(!reopen)
        {
            await vault.SaveNewAsync(OperationalPeer.Key,OperationalPeer.Secret,OperationalPeer.Pass);
            f.Runtime.FiniteFundingPremise=((NavalFiniteOperatorPremise)OkxCurrentMarketFundingTests.Premise(f)) with{CredentialRecordDigest=await vault.RecordIdentityAsync()};
            await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
            await using(var finite=OkxOperationalTradingService.Create(f.Service,f.Clock,(s,k,h,x)=>OkxExecutionClient.ForTests(history,f.Clock,s,k,h,x),history))
            {
                finite.BindFiniteSafety(()=>true);Assert.True(finite.ArmFiniteDemo());Assert.Equal(NavalMutationStatus.Applied,(await finite.ReconcileAsync(default)).Status);
                Assert.Equal(NavalMutationStatus.Applied,finite.PlanFiniteDemo(history.Instrument,Guid.NewGuid().ToString("N")).Status);Assert.Equal(NavalMutationStatus.Applied,finite.Start().Status);
                f.WriteBoundary=b=>{if(history.Sends.Count>0&&b==AtomicWriteBoundary.BeforeTempCreate)throw new IOException("synthetic-post-send-commit-fault");};
                Assert.Equal("STORAGE_FROZEN",(await finite.SubmitFiniteDemoAsync(true,default)).Code);f.WriteBoundary=null;
            }
            var original=State(f);var parent=Assert.Single(original.NavalBroker!.Operational!.Intents);Assert.Equal(NavalExecutionStage.Dispatching,parent.Stage);Assert.Null(parent.Outcome);
            history.Canceled=true;history.FillPending=true;history.BuyQuantity=original.Naval!.Reservations.Single().Quantity;history.BuyFee=history.BuyQuantity*history.FeeRate;history.BuyPrice=original.Naval.Reservations.Single().Intent.EntryPriceCeiling;history.Attachment=original.NavalBroker.Operational.IdAllocations.SelectMany(a=>a.Ids).Single(i=>i.Role==NavalClientIdRole.ProtectionAlgo).ClientId;
            await f.Service.DisconnectAsync();recovered=true;Harden(root);f.Clock.Advance(TimeSpan.FromMinutes(31));
            using var recoveryWrites=new HistoricalWrites(history);
            var recovery=new FiniteEntryTestComposition(f.Clock,(s,k,h,x)=>OkxExecutionObservationClient.ForTests(f,f.Clock,s,k,h,x,(t,c)=>{f.Clock.Advance(t);return Task.CompletedTask;}),(s,k,h,x)=>throw new Exception("RECOVERY_NO_STREAM"),(s,k,h,x)=>OkxExecutionClient.ForTests(recoveryWrites,f.Clock,s,k,h,x),history);
            int code=await Program.RunFiniteForTestsAsync(new[]{"--recover-owned-exit"},root,vault,(_,_)=>throw new Exception("NO_DISCOVERY"),recovery);
            Assert.True(code==0,"Synthetic historical recovery failed: "+code);
            var seed=State(f);Assert.Equal(NavalJson.Serialize(parent),NavalJson.Serialize(seed.NavalBroker!.Operational!.Intents.First()));Assert.Equal(2,seed.Naval!.Fills.Count);Assert.True(seed.Naval.Reservations.Single().Inventory>0);Assert.False(seed.Naval.Reservations.Single().ReductionUnsettled);
            File.WriteAllText(Path.Combine(root,"mixed-history.json"),NavalJson.Serialize(seed));Harden(root);
        }
        else
        {
            var seed=State(f);var parent=seed.NavalBroker!.Operational!.Intents.First();history.PendingClient=parent.ClientId;history.Canceled=true;history.FillPending=true;history.BuyQuantity=seed.Naval!.Reservations.Single(r=>r.Id==parent.ReservationId).Quantity;history.BuyFee=history.BuyQuantity*history.FeeRate;history.BuyPrice=seed.Naval.Reservations.Single(r=>r.Id==parent.ReservationId).Intent.EntryPriceCeiling;history.Attachment=seed.NavalBroker.Operational.IdAllocations.SelectMany(a=>a.Ids).First(i=>i.Role==NavalClientIdRole.ProtectionAlgo).ClientId;
            var sell=seed.NavalBroker.Operational.Intents.First(i=>i.Reduction);history.SellClient=sell.ClientId;using var request=JsonDocument.Parse(sell.Body);history.SellQuantity=decimal.Parse(request.RootElement.GetProperty("sz").GetString()!,CultureInfo.InvariantCulture);history.ProtectionCanceled=true;recovered=true;
        }
        var before=JsonSerializer.Deserialize<EngineState>(File.ReadAllText(Path.Combine(root,"mixed-history.json")))!;byte[] cipher=File.ReadAllBytes(Path.Combine(root,"vault",LinuxDemoCredentialVault.RecordName));
        using var peer=new TradingPeer(f){CandleInstrument="SOL-"+fixtureQuote};var cycles=new List<Cycle>();
        bool fault=false;
        void Update(string p,JsonNode b)
        {
            History(f,history,p,b);Ordinary(f,history,cycles,p,b);
            if(cashCompatibility&&p is "/api/v5/trade/order" or "/api/v5/trade/orders-history" or "/api/v5/trade/orders-history-archive")
                foreach(var row in b["data"]!.AsArray())
                {row!["posSide"]=p=="/api/v5/trade/order"?"net":"";row["tradeQuoteCcy"]=p=="/api/v5/trade/order"?"":"USDC";}
            if(scenario=="mixed-diagnose"&&!cashCompatibility&&p=="/api/v5/trade/orders-history")
                foreach(var row in b["data"]!.AsArray())row!["cancelSourceReason"]="SENSITIVE_PROVIDER_SENTINEL";
            if(fault)
            {
                if(scenario=="mixed-debt"&&p=="/api/v5/account/interest-limits")b["data"]![0]!["debt"]="0.01";
                if(scenario=="mixed-excluded"&&p=="/api/v5/account/balance")foreach(var v in b["data"]![0]!["details"]!.AsArray().Where(v=>v!["ccy"]!.GetValue<string>()=="BTC"))foreach(string k in new[]{"cashBal","availBal","eq"})v![k]="2";
                if(scenario=="mixed-child"&&p=="/api/v5/trade/order-algo")b["data"]![0]!["ordIdList"]=new JsonArray("999999");
                if(scenario=="mixed-raw-debt"&&p=="/api/v5/account/balance")b["data"]![0]!["details"]![0]!["liab"]="0.01";
            }
            if(p=="/api/v5/account/instruments")
            {var btc=b["data"]![0]!.DeepClone();btc["instId"]="BTC-USDC";btc["baseCcy"]="BTC";b["data"]!.AsArray().Add(btc);}
            FixtureQuote(b,fixtureQuote);
        }
        f.Mutate=Update;
        peer.BeforeResponse=()=>
        {
            Assert.InRange(peer.Size*110,0m,10m);peer.ExpectedSellSize=peer.Size*(1-.001m);peer.ProtectionCanceled=false;
            var n=cycles.Count;peer.EntryOrderId=(1777+n*10).ToString();peer.ExitOrderId=(1778+n*10).ToString();peer.GeneratedAlgoId=(1889+n*10).ToString();
            cycles.Add(new(peer.Client,peer.Attachment,peer.EntryOrderId,peer.ExitOrderId,peer.GeneratedAlgoId,peer.Size,f.Clock.GetUtcNow(),scenario=="mixed-newdust"?.00095m:.001m));
        };
        peer.BeforeMutationResponse=kind=>{var c=cycles.Last();if(kind=="cancel")c.Canceled=true;if(kind=="sell"){c.ExitClient=peer.ExitClient;c.Sold=true;c.SellUtc=f.Clock.GetUtcNow();}};
        var transport=new FiniteEntryTestComposition(f.Clock,(s,k,h,x)=>OkxExecutionObservationClient.ForTests(f,f.Clock,s,k,h,x,(t,c)=>{f.Clock.Advance(t);return Task.CompletedTask;}),
            (s,k,h,x)=>OkxObservationStream.ForTests((uri,c)=>Task.FromResult<WebSocket>(new OperationalSocket(uri)),f.Clock,s,k,h,x),
            (s,k,h,x)=>OkxExecutionClient.ForTests(peer,f.Clock,s,k,h,x),new MixedMarket(peer,cycles,f,history,()=>fault&&scenario=="mixed-stale"));
        int discoveries=0;
        byte[]? diagnosticBefore=null;bool diagnosticCompared=false;
        var result=await LinuxDemoHost.RunForTestsAsync(root,vault,new FaultInput(()=>fault=true,line=>
        {
            if(line=="DIAGNOSE")diagnosticBefore=File.ReadAllBytes(f.Target);
            if(line=="STATUS"&&scenario=="mixed-diagnose")
            {Assert.True(diagnosticBefore!.AsSpan().SequenceEqual(File.ReadAllBytes(f.Target)),"DIAGNOSE must not commit economics or admission");diagnosticCompared=true;}
        }),Console.Out,async(k,ct)=>{discoveries++;return await k.DiscoverPrivateSchema((_,_,_)=>{},ct,new DiscoveryReads(f));},transport,default);
        if(scenario=="mixed-diagnose")
        {
            Assert.NotNull(diagnosticBefore);
            Assert.True(diagnosticCompared,"Exact target comparison must precede STOP/QUIT lifecycle writes");
            Assert.Equal(0,peer.Posts);Assert.Equal(0,cycles.Count);
            Assert.Equal(before.NavalBroker!.HighWater,State(f).NavalBroker!.HighWater);
            using(var released=new FileStream(Path.Combine(root,".run.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None)){}
            Console.WriteLine("NATIVE_DIAGNOSE_PASS noWrites=True noNewIds=True unchangedOwner=True");
        }
        Assert.Equal(0,result);Assert.Equal(1,discoveries);Assert.True(cipher.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(root,"vault",LinuxDemoCredentialVault.RecordName))));
        var after=State(f);Assert.Equal("STOP",after.Naval!.Mode);Assert.Equal(NavalJson.Serialize(before.NavalBroker!.Baseline),NavalJson.Serialize(after.NavalBroker!.Baseline));Assert.Equal(before.NavalBroker.Namespace,after.NavalBroker.Namespace);Assert.True(after.NavalBroker.HighWater>=before.NavalBroker.HighWater);
        foreach(var fill in before.Naval!.Fills)Assert.Contains(fill,after.Naval.Fills);
        foreach(var i in before.NavalBroker.Operational!.Intents)Assert.Equal(NavalJson.Serialize(i),NavalJson.Serialize(after.NavalBroker.Operational!.Intents.Single(p=>p.Id==i.Id)));
        if(scenario is "mixed-cycle" or "mixed-reopen" or "mixed-newdust"){Assert.NotNull(after.NavalBroker.Operational!.OrdinaryDemo);Assert.Single(after.Naval.Reservations.Where(r=>r.QuarantineMark!=null));}
        else{Assert.Null(after.NavalBroker.Operational!.OrdinaryDemo);Assert.Equal(0,peer.Posts);Assert.Equal(2,after.Naval.Fills.Count);}
        if(reopen)Assert.Equal(0,peer.Posts);else if(scenario=="mixed-cycle"){Assert.True(cycles.Count>=2,"Second distinct ordinary signal did not dispatch");Assert.All(cycles,c=>Assert.True(c.Sold));Assert.Equal(6,after.Naval.Fills.Count);}
        else if(scenario=="mixed-newdust"){Assert.Equal(1,peer.Posts);Assert.Equal(3,after.Naval.Fills.Count);Assert.Equal(2,after.NavalBroker.Operational!.Obligations.Count(o=>o.StartsWith("PROTECTION_GAP:",StringComparison.Ordinal)));}
        if(cashCompatibility&&scenario=="mixed-cycle")
        {
            var dust=after.Naval.Reservations.Single(r=>r.QuarantineMark!=null);
            Assert.Equal(dust.Inventory*dust.QuarantineMark,dust.RiskHold);
            var s=after.Naval;decimal capital=Math.Min(s.Accounts.Sum(a=>a.Cash)+s.Reservations.Sum(r=>r.AllocatedCost),s.LiquidationEquity!.Value);
            var risky=new NavalReservation{Account=binding.Account,Fleet=NavalFleet.Bravo,Intent=new("risk","risk","SOL-"+fixtureQuote,.001m,110,100,130),RiskHold=capital*.005m-dust.RiskHold/2};
            Assert.Equal("TRADE_RISK",NavalCashRiskPolicy.Admission(s,risky,NavalBrokerValidation.Definition(after.NavalBroker.Baseline)));
            Console.WriteLine("CASH_RISK_CAP_PASS pinnedFullLoss=True");
        }
        Console.WriteLine("MIXED_NATIVE_PASS sameOwner=True historicalDispatchingNullAck=True segregatedCurrencies=6 pinnedDust=True normalScheduler=True contextualCash="+cashCompatibility+" quote="+fixtureQuote+" ordinaryEntries="+cycles.Count+" posts="+peer.Posts);return result;
    }
    // Fixture denomination only: construct all synthetic input surfaces in one
    // declared quote, including original requests; production never converts aliases.
    private static void FixtureQuote(JsonNode node,string quote)
    {
        if(quote=="USDC")return;
        if(node is JsonObject obj)
            foreach(var p in obj.ToArray())
                if(p.Value is JsonValue value&&value.TryGetValue<string>(out var text))obj[p.Key]=text=="USDC"?quote:text.EndsWith("-USDC",StringComparison.Ordinal)?text[..^4]+quote:text;
                else if(p.Value!=null)FixtureQuote(p.Value,quote);
        if(node is JsonArray array)
            for(int n=0;n<array.Count;n++)
                if(array[n] is JsonValue value&&value.TryGetValue<string>(out var text)&&text=="USDC")array[n]=quote;
                else if(array[n]!=null)FixtureQuote(array[n]!,quote);
    }
    private static void Harden(string root)
    {foreach(string p in Directory.GetDirectories(root,"*",SearchOption.AllDirectories))File.SetUnixFileMode(p,(UnixFileMode)448);foreach(string p in Directory.GetFiles(root,"*",SearchOption.AllDirectories))File.SetUnixFileMode(p,(UnixFileMode)384);}
    private static void History(OperationalPeer f,FiniteDemoPeer h,string p,JsonNode b)
    {
        if(p is "/api/v5/trade/order" or "/api/v5/trade/orders-history" or "/api/v5/trade/orders-history-archive" or "/api/v5/trade/orders-pending")
        {
            var state=State(f);var r=state.Naval!.Reservations.First();var rows=new List<JsonNode>();
            if(p!="/api/v5/trade/orders-pending"&&!f.Paths.Last().Contains("after=",StringComparison.Ordinal))
            foreach(bool sell in h.SellClient==""?new[]{false}:new[]{true,false})
            {
                var o=Oracle("order");decimal q=sell?h.SellQuantity:h.BuyQuantity;o["instId"]="SOL-USDC";o["ordId"]=sell?"778":"776";o["clOrdId"]=sell?h.SellClient:h.PendingClient;o["side"]=sell?"sell":"buy";o["state"]="filled";o["ordType"]=sell?"fok":"post_only";o["sz"]=N(q);o["accFillSz"]=N(q);o["fillSz"]=N(q);o["px"]=N(sell?109.99m:r.Intent.EntryPriceCeiling);o["fillPx"]=N(sell?109.99m:h.BuyPrice);o["avgPx"]=o["fillPx"]!.DeepClone();o["fee"]=N(sell?-q*109.99m*.001m:-h.BuyFee);o["feeCcy"]=sell?"USDC":"SOL";o["tradeQuoteCcy"]="USDC";o["cTime"]=r.Intent.CommandId==""?"0":state.NavalBroker!.Operational!.Intents.First().Deadline.AddSeconds(-5).ToUnixTimeMilliseconds().ToString();o["uTime"]=o["cTime"]!.DeepClone();o["fillTime"]=o["cTime"]!.DeepClone();o["attachAlgoOrds"]=sell?new JsonArray():new JsonArray(new JsonObject{["attachAlgoId"]="888",["attachAlgoClOrdId"]=h.Attachment,["failCode"]=""});rows.Add(o);
            }
            if(p=="/api/v5/trade/order")rows=rows.Where(o=>f.Paths.Last().Contains(o["clOrdId"]!.GetValue<string>(),StringComparison.Ordinal)||f.Paths.Last().Contains("ordId="+o["ordId"]!.GetValue<string>(),StringComparison.Ordinal)).ToList();
            b["data"]=new JsonArray(rows.ToArray());return;
        }
        if(p=="/api/v5/trade/order-algo")
        {
            var r=State(f).Naval!.Reservations.First();var a=OkxOwnedExitDiagnosticTests.Oracle("stream-algo-schema.json","detail")["data"]![0]!.DeepClone();a["instId"]="SOL-USDC";a["algoId"]="889";a["algoClOrdId"]=h.Attachment;a["side"]="sell";a["tdMode"]="cash";a["tradeQuoteCcy"]="USDC";a["ordType"]="oco";a["state"]=h.ProtectionCanceled?"canceled":"live";a["sz"]=N(h.BuyQuantity-h.BuyFee);a["slTriggerPx"]=N(r.Intent.StopPrice);a["tpTriggerPx"]=N(r.Intent.TakeProfitPrice);a["slOrdPx"]="-1";a["tpOrdPx"]="-1";a["slTriggerPxType"]="last";a["tpTriggerPxType"]="last";a["ordIdList"]=new JsonArray();b["data"]=new JsonArray(a);return;
        }
        OkxOrdinaryMixedAccountTests.Shape(f,h,p,b);
    }
    private sealed class FaultInput(Action fault,Action<string?>? diagnostic=null):TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken ct)
        {var line=await Console.In.ReadLineAsync(ct);diagnostic?.Invoke(line);if(line=="FAULT"){fault();return "STATUS";}return line;}
    }
    private sealed class DiscoveryReads(OperationalPeer f):HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
        {
            f.Clock.UseSystemUtc=true;try{return await new HttpMessageInvoker(f,false).SendAsync(r,ct);}finally{f.Clock.UseSystemUtc=false;}
        }
    }
    private sealed class HistoricalWrites(FiniteDemoPeer history):HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
        {
            if(r.RequestUri!.AbsolutePath=="/api/v5/trade/cancel-algos")
            {history.ProtectionCanceled=true;return new(HttpStatusCode.OK){Content=new StringContent("{\"code\":\"0\",\"msg\":\"\",\"data\":[{\"algoId\":\"889\",\"algoClOrdId\":\""+history.Attachment+"\",\"sCode\":\"0\",\"sMsg\":\"\"}]}")};}
            return await new HttpMessageInvoker(history,false).SendAsync(r,ct);
        }
    }
    private sealed class MixedMarket(TradingPeer peer,List<Cycle> cycles,OperationalPeer f,FiniteDemoPeer history,Func<bool> stale):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
        {
            if(r.RequestUri!.AbsolutePath=="/api/v5/market/candles"&&cycles.Count==1&&cycles[0].Sold&&!advanced&&State(f).Naval!.Mode=="RUNNING"){advanced=true;f.Clock.Advance(TimeSpan.FromDays(1));}
            if(stale())throw new HttpRequestException("SYNTHETIC_NO_FRESH_VALUATION");
            if(r.RequestUri!.AbsolutePath=="/api/v5/market/books"&&r.RequestUri.Query.Contains("instId=SOL-USD&",StringComparison.Ordinal))throw new HttpRequestException("SYNTHETIC_EEA_USD_BOOK_UNAVAILABLE_INDEX_AVAILABLE");
            if(r.RequestUri.AbsolutePath=="/api/v5/market/index-tickers")return new HttpMessageInvoker(history,false).SendAsync(r,ct);
            peer.ExitSignal=cycles.LastOrDefault() is {Sold:false};return new HttpMessageInvoker(peer,false).SendAsync(r,ct);
        }
        private bool advanced;
    }
    private sealed class Cycle(string client,string attachment,string order,string exitOrder,string algo,decimal size,DateTimeOffset at,decimal feeRate)
    {
        internal readonly string Client=client,Attachment=attachment,Order=order,ExitOrder=exitOrder,Algo=algo;
        internal readonly decimal Size=size,FeeRate=feeRate;
        internal readonly DateTimeOffset At=at;
        internal bool Canceled,Sold;
        internal string ExitClient="";
        internal DateTimeOffset SellUtc;
    }
    private static void Ordinary(OperationalPeer f,FiniteDemoPeer h,List<Cycle> cycles,string p,JsonNode b)
    {
        if(cycles.Count==0)return;
        if(p=="/api/v5/account/balance")
        {
            var rows=b["data"]![0]!["details"]!.AsArray();decimal debit=cycles.Sum(c=>c.Size*110-(c.Sold?c.Size*(1-c.FeeRate)*109.99m*.999m:0));decimal inventory=cycles.Where(c=>!c.Sold).Sum(c=>c.Size*(1-c.FeeRate));
            var q=rows.Single(v=>v!["ccy"]!.GetValue<string>()=="USDC")!;foreach(string k in new[]{"cashBal","availBal","eq"})q[k]=N(decimal.Parse(q[k]!.GetValue<string>(),CultureInfo.InvariantCulture)-debit);
            var sol=rows.Single(v=>v!["ccy"]!.GetValue<string>()=="SOL")!;foreach(string k in new[]{"cashBal","availBal","eq"})sol[k]=N(decimal.Parse(sol[k]!.GetValue<string>(),CultureInfo.InvariantCulture)+inventory);
            return;
        }
        if(p=="/api/v5/trade/order-algo")
        {
            var c=cycles.SingleOrDefault(c=>f.Paths.Last().Contains(c.Attachment,StringComparison.Ordinal)||f.Paths.Last().Contains("algoId="+c.Algo,StringComparison.Ordinal));if(c==null)return;
            var a=b["data"]![0]!;var r=State(f).Naval!.Reservations.Single(r=>State(f).NavalBroker!.Operational!.Intents.Any(i=>i.ReservationId==r.Id&&i.ClientId==c.Client));a["algoId"]=c.Algo;a["algoClOrdId"]=c.Attachment;a["sz"]=N(decimal.Floor(c.Size*(1-c.FeeRate)/.000001m)*.000001m);a["slTriggerPx"]=N(r.Intent.StopPrice);a["tpTriggerPx"]=N(r.Intent.TakeProfitPrice);a["state"]=c.Canceled?"canceled":"live";return;
        }
        string? kind=p switch{"/api/v5/trade/order" or "/api/v5/trade/orders-history" or "/api/v5/trade/orders-history-archive"=>"order","/api/v5/trade/fills" or "/api/v5/trade/fills-history"=>"fills","/api/v5/account/bills" or "/api/v5/account/bills-archive"=>"bills",_=>null};
        if(kind==null||f.Paths.Last().Contains("after=",StringComparison.Ordinal))return;
        var rowsOut=b["data"]!.AsArray();
        foreach(var c in cycles)
        foreach(bool sell in c.Sold?new[]{false,true}:new[]{false})
        {
            string id=sell?c.ExitOrder:c.Order,client=sell?c.ExitClient:c.Client;decimal size=sell?c.Size*(1-c.FeeRate):c.Size,price=sell?109.99m:110m,fee=sell?size*price*.001m:size*c.FeeRate;string at=(sell?c.SellUtc:c.At).ToUnixTimeMilliseconds().ToString();
            var v=Oracle(kind);v["instId"]="SOL-USDC";v["ordId"]=id;v["clOrdId"]=client;v["tradeId"]=(long.Parse(id)+3000).ToString();v["fee"]=N(-fee);v["ts"]=at;
            if(kind=="order")
            {v["side"]=sell?"sell":"buy";v["state"]="filled";v["ordType"]="fok";v["sz"]=N(size);v["accFillSz"]=N(size);v["fillSz"]=N(size);v["px"]=N(price);v["avgPx"]=N(price);v["fillPx"]=N(price);v["feeCcy"]=sell?"USDC":"SOL";v["tradeQuoteCcy"]="USDC";v["cTime"]=at;v["uTime"]=at;v["fillTime"]=at;v["attachAlgoOrds"]=sell?new JsonArray():new JsonArray(new JsonObject{["attachAlgoId"]=(long.Parse(c.Algo)-1).ToString(),["attachAlgoClOrdId"]=c.Attachment,["failCode"]=""});v.AsObject().Remove("ts");}
            if(kind=="fills"){v["side"]=sell?"sell":"buy";v["fillSz"]=N(size);v["fillPx"]=N(price);v["feeCcy"]=sell?"USDC":"SOL";v["tradeQuoteCcy"]="USDC";v["fillTime"]=at;v["billId"]=(long.Parse(id)*10).ToString();}
            if(kind=="bills")
            {v["billId"]=(long.Parse(id)*10).ToString();v["ccy"]=sell?"USDC":"SOL";v["type"]="2";v["mgnMode"]="cash";v["sz"]=N(size);v["px"]=N(price);v["balChg"]=N(sell?size*price-fee:size-fee);var counter=v.DeepClone();counter["billId"]=(long.Parse(id)*10+1).ToString();counter["ccy"]=sell?"SOL":"USDC";counter["fee"]="0";counter["balChg"]=N(sell?-size:-size*price);rowsOut.Add(counter);}
            rowsOut.Add(v);
        }
        if(p=="/api/v5/trade/order")b["data"]=new JsonArray(rowsOut.Where(o=>f.Paths.Last().Contains(o!["clOrdId"]!.GetValue<string>(),StringComparison.Ordinal)||f.Paths.Last().Contains("ordId="+o["ordId"]!.GetValue<string>(),StringComparison.Ordinal)).Select(o=>o!.DeepClone()).ToArray());
        else{string key=kind=="order"?"ordId":kind=="fills"?"tradeId":"billId";b["data"]=new JsonArray(rowsOut.OrderByDescending(o=>long.Parse(o![key]!.GetValue<string>())).Select(o=>o!.DeepClone()).ToArray());}
    }
}
