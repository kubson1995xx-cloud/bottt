using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Engine;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.Tests;

// Reuses the original native protocol, but creates the original owner only once.
// Replays copy that genuine sealed synthetic seed; no replacement BUY or hand seal.
internal static class OkxOwnedLotNative
{
    internal static async Task<int> Run(string scenario,string? seed)
    {
        string root=seed==null?Path.Combine("/home/ubuntu/apex-deliveries/current-market-funding-synthetic","owned-lot-"+Guid.NewGuid().ToString("N")):Path.GetDirectoryName(Path.GetFullPath(seed))!;Directory.CreateDirectory(root,(UnixFileMode)448);
        Console.WriteLine("SYNTHETIC_OWNED_LOT_ROOT; root="+root);
        if(seed!=null)RestoreSeed(seed,root);
        bool rounded=scenario!="full-net";
        if(scenario=="quote-gap")AppDomain.CurrentDomain.FirstChanceException+=(_,e)=>{if(e.Exception.StackTrace?.Contains("CollectOwned",StringComparison.Ordinal)==true)Console.Error.WriteLine("SYNTHETIC_EXCEPTION_TYPE="+e.Exception.GetType().FullName+";STACK="+e.Exception.StackTrace);};
        var binding=OkxConfiguration.Bind(new OkxNavalOptions{Schema="okx-naval-config-v1",Scopes=new[]{new OkxScopeOptions{ScopeId="finite-demo",Environment="OkxDemo",Region="EEA",ExpectedUid="99123456789",ExpectedMainUid="99123456789",QuoteCurrency="USDC",CredentialBindingId="foreground-demo",SafetyVersion="okx-cash-v1"}}})[0];
        await using var f=new OperationalPeer(root,binding,preserve:true,finite:true);using var peer=new FiniteDemoPeer(f){BaseCurrency="ADA",OwnFreezes=true};
        bool recovering=false,observing=false;int exactReads=0,pendingRows=0;
        f.Mutate=(p,b)=>
        {
            if(p=="/api/v5/trade/order"&&recovering)
            {
                var state=JsonSerializer.Deserialize<EngineState>(File.ReadAllText(f.Target))!;var reservation=state.Naval!.Reservations.Single();bool sell=peer.SellClient!=""&&f.Paths.Last().Contains(peer.SellClient,StringComparison.Ordinal);
                var row=OkxOwnedExitDiagnosticTests.Oracle("recovery-schema.json","order")["data"]![0]!.DeepClone();
                row["instId"]=peer.Instrument;row["ordId"]=sell?"778":"776";row["clOrdId"]=sell?peer.SellClient:peer.PendingClient;row["instType"]="SPOT";row["tdMode"]="cash";row["tradeQuoteCcy"]="USDC";row["side"]=sell?"sell":"buy";row["ordType"]=sell?"fok":"post_only";row["state"]="filled";row["sz"]=N(sell?peer.SellQuantity:peer.BuyQuantity);row["accFillSz"]=row["sz"]!.DeepClone();row["fee"]=N(sell?-peer.SellQuantity*109.99m*peer.FeeRate:-peer.BuyFee);row["feeCcy"]=sell?"USDC":"ADA";row["px"]=N(sell?109.99m:reservation.Intent.EntryPriceCeiling);row["avgPx"]=N(sell?109.99m:peer.BuyPrice);row["fillPx"]=row["avgPx"]!.DeepClone();row["slippage"]="opaque-historical-setting";
                row["attachAlgoOrds"]=sell?new JsonArray():new JsonArray(new JsonObject{["attachAlgoId"]="888",["attachAlgoClOrdId"]=peer.Attachment,["failCode"]=""});
                if(!sell&&scenario=="metadata-lot")row["attachAlgoOrds"]![0]!["percent"]="synthetic-opaque";
                if(observing)row.AsObject().Remove("slippage");
                if(!observing&&scenario=="parent-live"&&!sell)row["state"]="live";
                b["data"]=scenario=="unknown-sell"&&sell?new JsonArray():new JsonArray(row);exactReads++;return;
            }
            if(p=="/api/v5/trade/orders-algo-pending"&&recovering&&!observing&&scenario.StartsWith("pending-",StringComparison.Ordinal)&&f.Paths.Last().Contains("ordType=conditional,oco",StringComparison.Ordinal)&&
                (scenario!="pending-live"||!peer.ProtectionCanceled))
            {
                var reservation=JsonSerializer.Deserialize<EngineState>(File.ReadAllText(f.Target))!.Naval!.Reservations.Single();
                var row=OkxOwnedExitDiagnosticTests.Oracle("stream-algo-schema.json","detail")["data"]![0]!.DeepClone();
                row["instType"]="SPOT";row["instId"]=peer.Instrument;row["algoId"]="889";row["algoClOrdId"]=peer.Attachment;
                row["side"]="sell";row["tdMode"]="cash";row["tradeQuoteCcy"]="USDC";row["ordType"]="oco";row["state"]="live";
                row["sz"]=N(ProtectionQuantity(peer,scenario));row["slTriggerPx"]=N(reservation.Intent.StopPrice);row["tpTriggerPx"]=N(reservation.Intent.TakeProfitPrice);
                row["slOrdPx"]="-1";row["tpOrdPx"]="-1";row["slTriggerPxType"]="last";row["tpTriggerPxType"]="last";row["ordIdList"]=new JsonArray();
                row["uTime"]=f.Clock.GetUtcNow().ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
                if(scenario=="pending-size")row["sz"]="0.0002";
                if(scenario=="pending-config")row["amendPxOnTriggerType"]="1";
                if(scenario=="pending-child"&&peer.ProtectionCanceled)row["ordIdList"]=new JsonArray("999");
                b["data"]=new JsonArray(row);pendingRows++;return;
            }
            if(p=="/api/v5/trade/order-algo"&&recovering)
            {
                var r=JsonSerializer.Deserialize<EngineState>(File.ReadAllText(f.Target))!.Naval!.Reservations.Single();var row=OkxOwnedExitDiagnosticTests.Oracle("stream-algo-schema.json","detail")["data"]![0]!.DeepClone();
                row["instId"]=peer.Instrument;row["instType"]="SPOT";row["algoId"]="889";row["algoClOrdId"]=peer.Attachment;row["side"]="sell";row["tdMode"]="cash";row["tradeQuoteCcy"]="USDC";row["ordType"]="oco";row["state"]=peer.ProtectionCanceled?"canceled":"live";row["sz"]=N(ProtectionQuantity(peer,scenario));row["slTriggerPx"]=N(r.Intent.StopPrice);row["tpTriggerPx"]=N(r.Intent.TakeProfitPrice);row["slOrdPx"]="-1";row["tpOrdPx"]="-1";row["slTriggerPxType"]="last";row["tpTriggerPxType"]="last";row["ordIdList"]=new JsonArray();row["aggressiveness"]="opaque-historical-setting";row["lmtOrderNumber"]="opaque-historical-setting";row["triggerParams"]=new JsonArray();
                if(observing){row.AsObject().Remove("aggressiveness");row.AsObject().Remove("lmtOrderNumber");row.AsObject().Remove("triggerParams");}
                else{if(scenario=="unknown-relation")row.AsObject().Remove("ordIdList");if(scenario=="foreign-ancestry")row["algoClOrdId"]="ForeignProtection1";if(scenario=="trigger")row["triggerParams"]=new JsonArray(new JsonObject());if(scenario=="advanced")row["ordType"]="iceberg";if(scenario=="advanced-config")row["pxVar"]="1";if(scenario=="child")row["ordIdList"]=new JsonArray("999");if(scenario=="unknown")row["unknownPrivate"]="not-supported";}
                b["data"]=new JsonArray(row);return;
            }
            if(p=="/api/v5/trade/orders-pending"&&recovering){b["data"]=new JsonArray();return;}
            OkxCurrentMarketFundingTests.Shape(f,peer,p,b);
            if(recovering&&!observing&&scenario=="balance"&&p=="/api/v5/account/balance")b["data"]![0]!["details"]![0]!["cashBal"]="1";
            if(recovering&&!observing&&scenario=="funding"&&p=="/api/v5/account/interest-limits")b["data"]![0]!["debt"]="0.01";
            if(recovering&&!observing&&scenario=="bill"&&p.StartsWith("/api/v5/account/bills",StringComparison.Ordinal)&&b["data"]!.AsArray().Count>0)b["data"]![0]!["balChg"]="0.01";
            if(recovering&&scenario=="own-unlinked"&&p.StartsWith("/api/v5/account/bills",StringComparison.Ordinal))foreach(var v in b["data"]!.AsArray()){v!["clOrdId"]="";if(v["ccy"]!.GetValue<string>()=="ADA")v["balChg"]=N(ProtectionQuantity(peer,scenario));}
            if(recovering&&scenario=="foreign"&&p.StartsWith("/api/v5/account/bills",StringComparison.Ordinal)&&b["data"]!.AsArray().Count>0){var other=b["data"]![0]!.DeepClone();other["billId"]="9999";other["ordId"]="9999";b["data"]!.AsArray().Add(other);}
        };
        var vault=new Apex.Okx.DemoAcceptance.LinuxDemoCredentialVault(Path.Combine(root,"vault"));
        if(seed==null)
        {
        await vault.SaveNewAsync(OperationalPeer.Key,OperationalPeer.Secret,OperationalPeer.Pass);
        f.Runtime.FiniteFundingPremise=((NavalFiniteOperatorPremise)OkxCurrentMarketFundingTests.Premise(f)) with{CredentialRecordDigest=await vault.RecordIdentityAsync()};f.BaselineOverride=new Apex.Okx.DemoAcceptance.SignedFiniteBaseline(f.Binding,f.Request.CredentialFingerprint,()=>f.Service);
        await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));await using var loop=OkxOperationalTradingService.Create(f.Service,f.Clock,(s,k,h,x)=>OkxExecutionClient.ForTests(peer,f.Clock,s,k,h,x),peer);loop.BindFiniteSafety(()=>true);
        Assert.True(loop.ArmFiniteDemo());Assert.Equal(NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);Assert.Equal(NavalMutationStatus.Applied,loop.PlanFiniteDemo(peer.Instrument,Guid.NewGuid().ToString("N")).Status);Assert.Equal(NavalMutationStatus.Applied,loop.Start().Status);
        f.WriteBoundary=b=>{if(peer.Sends.Count>0&&b.ToString()=="BeforeTempCreate")throw new IOException("synthetic-ack-persistence-fault");};Assert.Equal("STORAGE_FROZEN",(await loop.SubmitFiniteDemoAsync(true,default)).Code);f.WriteBoundary=null;
        await loop.DisposeAsync();await f.Service.DisconnectAsync();
        }
        else
        {
            using var seedDoc=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"seed.json")));
            var at=seedDoc.RootElement.GetProperty("clockUtc").GetDateTimeOffset();if(at>f.Clock.GetUtcNow())f.Clock.Advance(at-f.Clock.GetUtcNow());
        }
        var original=JsonSerializer.Deserialize<EngineState>(File.ReadAllText(f.Target))!;var originalIntent=NavalJson.Serialize(original.NavalBroker!.Operational!.Intents.Single());Assert.Equal(NavalExecutionStage.Dispatching,original.NavalBroker.Operational.Intents[0].Stage);Assert.Null(original.NavalBroker.Operational.Intents[0].Outcome);
        peer.PendingClient=original.NavalBroker!.Operational!.Intents.Single().ClientId;
        if(seed!=null)typeof(FiniteDemoPeer).GetField("at",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(peer,original.Naval!.Fills.Single().Fill.TradeUtc.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        peer.Canceled=true;peer.FillPending=true;peer.BuyQuantity=original.Naval!.Reservations.Single().Quantity;peer.BuyFee=peer.BuyQuantity*peer.FeeRate;peer.BuyPrice=original.Naval.Reservations.Single().Intent.EntryPriceCeiling;peer.Attachment=original.NavalBroker.Operational.IdAllocations.SelectMany(a=>a.Ids).Single(i=>i.Role==NavalClientIdRole.ProtectionAlgo).ClientId;
        foreach(string dir in Directory.GetDirectories(root,"*",SearchOption.AllDirectories))File.SetUnixFileMode(dir,(UnixFileMode)448);foreach(string file in Directory.GetFiles(root,"*",SearchOption.AllDirectories))File.SetUnixFileMode(file,(UnixFileMode)384);
        File.WriteAllText(Path.Combine(root,"last-report.json"),"ORIGINAL_REPORT_PRESERVED");File.SetUnixFileMode(Path.Combine(root,"last-report.json"),(UnixFileMode)384);if(seed==null)f.Clock.Advance(TimeSpan.FromMinutes(31));recovering=true;
        if(scenario=="inactive")peer.ProtectionCanceled=true;if(scenario=="unknown-sell")peer.UnknownSell=true;
        using var market=new RecoveryMarket(peer,()=>scenario=="quote-gap"&&peer.ProtectionCanceled);
        using var mutationPeer=new RecoveryWrites(peer,scenario);
        TimingFence? timing=null;
        var composition=new Apex.Okx.DemoAcceptance.FiniteEntryTestComposition(f.Clock,(s,k,h,x)=>{Assert.Equal("STOP",JsonSerializer.Deserialize<EngineState>(File.ReadAllText(f.Target))!.Naval!.Mode);Assert.Throws<IOException>(()=>new FileStream(Path.Combine(root,".run.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None));return OkxExecutionObservationClient.ForTests(f,f.Clock,s,k,h,x,(t,ct)=>{f.Clock.Advance(t);return Task.CompletedTask;});},(s,k,h,x)=>throw new Exception("NO_STREAM"),(s,k,h,x)=>OkxExecutionClient.ForTests(mutationPeer,f.Clock,s,k,h,timing=new TimingFence(x,f.Clock,scenario,root)),market);
        if(seed==null)
        {
        observing=true;
        Assert.Equal(0,await Apex.Okx.DemoAcceptance.Program.RunFiniteForTestsAsync(new[]{"--recover-observe"},root,vault,(_,_)=>throw new Exception("NO_DISCOVERY"),composition));observing=false;
        }
        var retained=JsonSerializer.Deserialize<EngineState>(File.ReadAllText(f.Target))!;Assert.Single(retained.Naval!.Fills);Assert.True(retained.Naval.Reservations.Single().Inventory>0);Assert.False(retained.Naval.Reservations.Single().TerminalCoverage);
        if(scenario=="own-unlinked")Assert.Contains(retained.NavalBroker!.Operational!.Obligations,o=>o.StartsWith("UNLINKED_BILL:",StringComparison.Ordinal));
        if(seed==null)
        {
            string seedRoot=Path.Combine(root,"replay-seed");Directory.CreateDirectory(seedRoot,(UnixFileMode)448);
            CopyTree(Path.Combine(root,"okx"),Path.Combine(seedRoot,"okx"));CopyTree(Path.Combine(root,"vault"),Path.Combine(seedRoot,"vault"));
            File.Copy(Path.Combine(root,"last-report.json"),Path.Combine(seedRoot,"last-report.json"));
            File.WriteAllText(Path.Combine(seedRoot,"seed.json"),JsonSerializer.Serialize(new{clockUtc=f.Clock.GetUtcNow(),generatedByOriginalProductionDispatch=true,originalBuyCount=1}));
            Console.WriteLine("SYNTHETIC_OWNED_LOT_SEED; root="+seedRoot);
        }
        int sends=peer.Sends.Count;string baseline=NavalJson.Serialize(original.NavalBroker.Baseline);
        int code=await Apex.Okx.DemoAcceptance.Program.RunFiniteForTestsAsync(new[]{"--recover-owned-exit"},root,vault,(_,_)=>throw new Exception("NO_DISCOVERY"),composition);
        bool success=scenario is "active" or "inactive" or "own-unlinked" or "authority" or "pending-live" or "lot" or "metadata-lot" or "full-net";Assert.True(code==(success?0:5),"Bounded STOP owned reduction result mismatch: "+scenario+" exit="+code);
        var after=JsonSerializer.Deserialize<EngineState>(File.ReadAllText(f.Target))!;Assert.Equal("STOP",after.Naval!.Mode);Assert.Equal(originalIntent,NavalJson.Serialize(after.NavalBroker!.Operational!.Intents.First()));Assert.Equal(baseline,NavalJson.Serialize(after.NavalBroker.Baseline));Assert.Equal("ORIGINAL_REPORT_PRESERVED",File.ReadAllText(Path.Combine(root,"last-report.json")));
        if(scenario=="foreign")Assert.All(retained.NavalBroker!.Operational!.Obligations.Where(o=>o.StartsWith("UNLINKED_BILL:",StringComparison.Ordinal)),o=>Assert.Contains(o,after.NavalBroker.Operational.Obligations));
        if(scenario is "quote-gap" or "utc" or "mono" or "source" or "revision" or "unknown-sell" or "cancel-unknown")Assert.Contains("PROTECTION_GAP:"+after.Naval.Reservations.Single().Id,after.NavalBroker.Operational.Obligations);
        if(success&&rounded)
        {
            decimal net=peer.BuyQuantity-peer.BuyFee,lot=decimal.Floor(net/.0001m)*.0001m;
            Assert.True(net>lot&&net-lot<.0001m);Assert.Equal(lot,peer.SellQuantity);Assert.Equal(net-lot,after.Naval!.Reservations.Single().Inventory);
            Assert.Contains("PROTECTION_GAP:"+after.Naval.Reservations.Single().Id,after.NavalBroker!.Operational!.Obligations);
        }
        if(success){Assert.Equal(scenario=="inactive"?1:2,peer.Sends.Count-sends);Assert.Equal(2,after.Naval.Fills.Count);Assert.True(after.Naval.Reservations.Single().TerminalCoverage);Assert.False(after.Naval.Reservations.Single().ReductionUnsettled);Assert.InRange(after.Naval.Reservations.Single().Inventory,0m,.000099999m);Assert.True(peer.SellQuantity<=peer.BuyQuantity-peer.BuyFee);}
        else{Assert.Equal(scenario is "quote-gap" or "unknown-sell" or "utc" or "mono" or "source" or "revision" or "cancel-ack-live" or "cancel-unknown" or "pending-terminal" or "pending-child"?(scenario=="unknown-sell"?2:1):0,peer.Sends.Count-sends);Assert.DoesNotContain(after.NavalBroker.Operational.Obligations,o=>o=="RECOVERY_NO_ENTRY_OR_FUNDING_AUTHORITY");}
        if(scenario.StartsWith("pending-",StringComparison.Ordinal))
        {
            Assert.True(pendingRows>0,"Matching pending fixture was not consumed");
            if(!success){Assert.Equal(0m,peer.SellQuantity);Assert.Single(after.Naval.Fills);Assert.DoesNotContain(after.Naval.Fills,fill=>fill.Fill.Reduction);Assert.Contains(after.NavalBroker.Operational.Obligations,o=>o.StartsWith("OWNED_PENDING_PROTECTION_",StringComparison.Ordinal));}
            if(scenario=="pending-child")Assert.Contains(after.NavalBroker.Operational.Obligations,o=>o.StartsWith("OWNED_PENDING_PROTECTION_CHILD_",StringComparison.Ordinal));
        }
        if(scenario is "authority" or "utc" or "mono" or "source" or "revision")Assert.True(timing?.Reached==true,"Did not reach signed final serialized fence");
        var report=Directory.GetFiles(Path.Combine(root,"owned-reduction-reports"),"*.json").Single();using(var doc=JsonDocument.Parse(File.ReadAllBytes(report))){Assert.True(doc.RootElement.GetProperty("complete").GetBoolean());Assert.False(doc.RootElement.GetProperty("armed").GetBoolean());Assert.False(doc.RootElement.GetProperty("schedulerStarted").GetBoolean());}
        // Same flag again: no replacement, no replay, no second sale, no new BUY.
        int previous=peer.Sends.Count;int replay=await Apex.Okx.DemoAcceptance.Program.RunFiniteForTestsAsync(new[]{"--recover-owned-exit"},root,vault,(_,_)=>throw new Exception("NO_DISCOVERY"),composition);Assert.Equal(5,replay);Assert.Equal(previous,peer.Sends.Count);
        File.WriteAllText(Path.Combine(root,"owned-reduction-summary.json"),JsonSerializer.Serialize(new{scenario,code,replay,reusedSeed=seed!=null,newBuyCount=seed==null?1:0,ownNet=peer.BuyQuantity-peer.BuyFee,protectionQuantity=ProtectionQuantity(peer,scenario),sellQuantity=peer.SellQuantity,mode=after.Naval.Mode,newWrites=peer.Sends.Count-sends,exactReads,originalDispatchingNullAckPreserved=true,baselinePreserved=true,expiredEntryNotRenewed=true,actualPrivateAccess=false,inventory=after.Naval.Reservations.Single().Inventory,obligations=after.NavalBroker.Operational.Obligations}));Console.WriteLine("SYNTHETIC_STOP_OWNED_REDUCTION_PASS; root="+root);return 0;
    }
    private static string N(decimal n)=>n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private sealed class TimingFence(IOkxFinalDispatchFence inner,OperationalClock clock,string scenario,string root):IOkxSerializedDispatchFence
    {
        internal bool Reached;
        public bool CanDispatch(OkxPreparedRequest r,DateTimeOffset now)=>false;
        public IDisposable? TryEnterDispatch(OkxPreparedRequest request,DateTimeOffset now)
        {
            if(request.Operation==OkxExecutionOperation.CancelProtection)CancelProof(inner,clock,root);
            if(request.Operation==OkxExecutionOperation.Submit)
            {
                Reached=true;var type=inner.GetType();object Field(string n)=>type.GetField(n,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(inner)!;
                var session=(NavalBrokerSession)Field("session");
                if(scenario=="authority")
                {
                    var cap=(NavalStopOwnedReduction)Field("ownedCap");var state=session.ReadState();var parent=state.NavalBroker!.Operational!.Intents.Single(i=>!i.Reduction&&i.Operation==NavalExecutionOperation.Submit);var intent=state.NavalBroker.Operational.Intents.Last();
                    Assert.False(((INavalBrokerTradingAuthority)inner).CanExecute);Assert.Equal(NavalMutationStatus.Refused,session.StartTrading().Status);Assert.Equal(NavalMutationStatus.Refused,session.ReserveIds(parent.ReservationId,"new-buy").Result.Status);Assert.Equal(NavalMutationStatus.Refused,session.ReserveIds(parent.ReservationId,"generic-sell",true).Result.Status);Assert.Equal(NavalMutationStatus.Refused,session.RequestExit(parent.ReservationId).Status);Assert.Null(session.OpenOwnedReduction(parent.Id));Assert.Throws<NotSupportedException>(()=>JsonSerializer.Serialize(cap));
                    var method=typeof(NavalPortfolioCoordinator).GetMethod("OwnedMutation",BindingFlags.Instance|BindingFlags.NonPublic)!;
                    bool Admit(NavalBrokerIntent i)=>(bool)method.Invoke(session.Coordinator,new object[]{session,cap,state,i})!;
                    Assert.True(Admit(intent));Assert.False(Admit(intent with{ReservationId="foreign"}));
                    var spec=state.NavalBroker.Baseline.Observations.Catalog.Single();decimal size=decimal.Ceiling(state.Naval!.Reservations.Single().Inventory/spec.Quantum)*spec.Quantum;
                    using var doc=JsonDocument.Parse(intent.Body);string price=doc.RootElement.GetProperty("px").GetString()!;
                    var oversize=OkxPreparedRequest.Submit(request.Scope,request.Instrument,"sell","fok",N(size),price,request.OriginalClientId,request.Deadline);
                    var forged=intent with{Body=oversize.CopyBody(),RequestDigest=oversize.Digest};var field=type.GetField("ownedRequest",BindingFlags.Instance|BindingFlags.NonPublic)!;field.SetValue(inner,oversize);
                    try{Assert.True(((INavalBrokerOwnedReductionAuthority)inner).CanReduce(parent,forged,clock.GetUtcNow()),"Oversize test must pass host maxima first");Assert.False(Admit(forged));}finally{field.SetValue(inner,request);}
                    var buy=OkxPreparedRequest.Submit(request.Scope,request.Instrument,"buy","fok","0.0001",price,request.OriginalClientId,request.Deadline);Assert.Null(((IOkxSerializedDispatchFence)inner).TryEnterDispatch(buy,now));Assert.Equal(NavalMutationStatus.Refused,session.Prepare(intent with{Id="new-buy",Reduction=false,Body=buy.CopyBody(),RequestDigest=buy.Digest}).Status);
                    Assert.Null(((IOkxSerializedDispatchFence)inner).TryEnterDispatch(OkxPreparedRequest.Submit(new("Demo","99123456788","99123456788","USDC","foreground-demo"),request.Instrument,"sell","fok","0.0001",price,request.OriginalClientId,request.Deadline),now));
                }
                if(scenario=="utc")clock.Advance(TimeSpan.FromSeconds(11),wall:true,monotonic:false);
                if(scenario=="mono")clock.Advance(TimeSpan.FromSeconds(31),wall:false,monotonic:true);
                if(scenario=="source")type.GetField("active",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(inner,false);
                if(scenario=="revision")session.Coordinator.Stop("SYNTHETIC_STOP_AFTER_SIGNING");
            }
            var guard=((IOkxSerializedDispatchFence)inner).TryEnterDispatch(request,now);
            if(request.Operation==OkxExecutionOperation.CancelProtection)
            {
                var path=Path.Combine(root,"lot-boundary-proof.json");var proof=JsonNode.Parse(File.ReadAllText(path))!;proof["actualEnterDispatchAdmitted"]=guard!=null;File.WriteAllText(path,proof.ToJsonString());
            }
            return guard;
        }
    }
    private sealed class RecoveryMarket(FiniteDemoPeer peer,Func<bool> fail):HttpMessageHandler
    {
        private readonly HttpMessageInvoker invoker=new(peer,false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)=>fail()?Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)):invoker.SendAsync(r,ct);
        protected override void Dispose(bool disposing){if(disposing)invoker.Dispose();base.Dispose(disposing);}
    }
    private sealed class RecoveryWrites(FiniteDemoPeer peer,string scenario):HttpMessageHandler
    {
        private readonly HttpMessageInvoker invoker=new(peer,false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var response=await invoker.SendAsync(request,ct);
            if(request.RequestUri!.AbsolutePath=="/api/v5/trade/cancel-algos")
            {
                if(scenario=="cancel-ack-live")peer.ProtectionCanceled=false;
                if(scenario=="cancel-unknown"){response.Dispose();return new(HttpStatusCode.OK){Content=new StringContent("{\"code\":\"50004\",\"msg\":\"\",\"data\":[]}",Encoding.UTF8,"application/json")};}
            }
            return response;
        }
        protected override void Dispose(bool disposing){if(disposing)invoker.Dispose();base.Dispose(disposing);}
    }

    private static decimal ProtectionQuantity(FiniteDemoPeer peer,string scenario)
    {
        decimal net=peer.BuyQuantity-peer.BuyFee,lot=decimal.Floor(net/.0001m)*.0001m;
        return scenario switch {"full-net"=>net,"under"=>lot-.0001m,"over"=>net+.0001m,"zero"=>0,"below-min"=>.00001m,"nonaligned"=>lot-.00000001m,"gross"=>peer.BuyQuantity,_=>lot};
    }
    private static void CopyTree(string source,string target)
    {
        Directory.CreateDirectory(target,(UnixFileMode)448);
        foreach(string f in Directory.GetFiles(source)) {string p=Path.Combine(target,Path.GetFileName(f));File.Copy(f,p);File.SetUnixFileMode(p,(UnixFileMode)384);}
        foreach(string d in Directory.GetDirectories(source))CopyTree(d,Path.Combine(target,Path.GetFileName(d)));
    }
    private static void RestoreSeed(string source,string target)
    {
        source=Path.GetFullPath(source);
        Assert.StartsWith("/home/ubuntu/apex-deliveries/current-market-funding-synthetic/owned-lot-",source,StringComparison.Ordinal);
        Assert.Equal("replay-seed",Path.GetFileName(source));Assert.True(File.Exists(Path.Combine(source,"seed.json")));
        // StorageIdentity is path-bound. Never rewrite its seal for relocation.
        // Restore only this fixture's already-retained original synthetic prefix,
        // at the original path, after the gate has retained the previous result.
        using(var guard=new FileStream(Path.Combine(target,".run.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
        {
            foreach(string name in new[]{"okx","vault","owned-reduction-reports","recovery-reports"})
            {var p=Path.Combine(target,name);if(Directory.Exists(p))Directory.Delete(p,true);}
            foreach(string name in new[]{"owned-reduction-summary.json","lot-boundary-proof.json","seed.json","last-report.json"})
            {var p=Path.Combine(target,name);if(File.Exists(p))File.Delete(p);}
            CopyTree(source,target);
        }
    }
    private static void CancelProof(IOkxFinalDispatchFence inner,OperationalClock clock,string root)
    {
        var type=inner.GetType();object Field(string n)=>type.GetField(n,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(inner)!;
        var session=(NavalBrokerSession)Field("session");var cap=(NavalStopOwnedReduction)Field("ownedCap");var state=session.ReadState();var x=state.NavalBroker!.Operational!;var intent=x.Intents.Last();var r=state.Naval!.Reservations.Single();var raw=x.Protection.Last();
        Assert.Equal(NavalExecutionStage.Prepared,intent.Stage);Assert.True(r.TerminalCoverage);Assert.False(r.ReductionUnsettled);Assert.Contains(cap.ParentId,x.SettledIntents);Assert.All(x.Obligations,o=>Assert.Equal("PROTECTION_GAP:"+r.Id,o));
        if(raw.Quantity!=r.Inventory)Assert.Contains("PROTECTION_GAP:"+r.Id,x.Obligations);
        Assert.True(((INavalBrokerOwnedReductionAuthority)inner).CanReduce(x.Intents.First(),intent,clock.GetUtcNow()));
        var method=typeof(NavalPortfolioCoordinator).GetMethod("OwnedMutation",BindingFlags.Instance|BindingFlags.NonPublic)!;
        bool Admit(EngineState e)=>(bool)method.Invoke(session.Coordinator,new object[]{session,cap,e,intent})!;
        Assert.True(Admit(state),"Reached Core cancellation predicate rejected the exact signed protection quantity");
        var spec=state.NavalBroker.Baseline.Observations.Catalog.Single();decimal lot=decimal.Floor(r.Inventory/spec.Quantum)*spec.Quantum;
        var operands=new[]{("full-net",r.Inventory,true),("floor-own-net",lot,true),("under",lot-spec.Quantum,false),("over",r.Inventory+spec.Quantum,false),("zero",0m,false),("below-min",spec.MinimumQuantity/2,false),("nonaligned",lot-spec.Quantum/10,false),("gross",r.Quantity,false)};
        foreach(var (name,q,want) in operands)
        {
            var e=NavalJson.Clone(state);var op=e.NavalBroker!.Operational!;e.NavalBroker=e.NavalBroker with{Operational=op with{Protection=op.Protection.Select(v=>v.AlgoId==raw.AlgoId?v with{Quantity=q}:v).ToArray()}};
            Assert.True(Admit(e)==want,"Reached Core cancellation quantity variant: "+name);
        }
        foreach(string fault in new[]{"foreign","child","stale","source"})
        {
            var e=NavalJson.Clone(state);var op=e.NavalBroker!.Operational!;
            if(fault=="foreign"||fault=="child")e.NavalBroker=e.NavalBroker with{Operational=op with{Protection=op.Protection.Select(v=>v.AlgoId==raw.AlgoId?fault=="foreign"?v with{AttachmentClientId="ForeignProtection1"}:v with{ChildOrderIds=new[]{"999"}}:v).ToArray()}};
            if(fault=="stale")e.DurableRevision++;
            if(fault=="source")type.GetField("active",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(inner,false);
            try{Assert.False(Admit(e),"Reached Core mutation must veto "+fault);}finally{if(fault=="source")type.GetField("active",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(inner,true);}
        }
        File.WriteAllText(Path.Combine(root,"lot-boundary-proof.json"),JsonSerializer.Serialize(new{prepareReached=true,earlierFullParentFillFeeBillFundingBalanceValid=true,rawProtectionQuantity=raw.Quantity,ownInventory=r.Inventory,quantum=spec.Quantum,minimum=spec.MinimumQuantity,quantityVariants=operands.Select(v=>new{name=v.Item1,quantity=v.Item2,admit=v.Item3}),additionalVetoes=new[]{"foreign","child","stale","source"},finalSigningFenceReached=true,protectedNowUnchanged=true}));
    }
}
