using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.Tests;

public sealed class OkxOrdinaryOrderConsistencyTests
{
    [Fact]
    public Task Own_cash_confirmed_fields_project_quote_close_both_bill_legs_and_PREPARE()
        =>OkxSyntheticClockIsolationTests.Run("order-consistency-cash");
    [Fact]
    public Task Distinct_endpoint_provenance_with_equal_complete_order_semantics_closes_mixed_PREPARE()
        =>OkxSyntheticClockIsolationTests.Run("order-consistency-equal");

    [Fact]
    public Task Finite_collector_retains_original_raw_digest_conflict_policy()=>OkxSyntheticClockIsolationTests.Run("order-consistency-finite");

    [Theory]
    [InlineData("state")][InlineData("price")][InlineData("quantity")][InlineData("fee")][InlineData("fee-currency")]
    [InlineData("client")][InlineData("order")][InlineData("attachment")][InlineData("stop")][InlineData("children")]
    [InlineData("config")][InlineData("mode")][InlineData("pending")][InlineData("pending-terminal")][InlineData("child-detail")]
    public Task True_cross_endpoint_contradiction_keeps_mixed_PREPARE_in_STOP(string fault)
        =>OkxSyntheticClockIsolationTests.Run("order-consistency-"+fault);

    internal static async Task Scenario(string fault)
    {
        await using var f=new OperationalPeer(finite:true);using var peer=new FiniteDemoPeer(f){BaseCurrency="SOL"};
        f.Mutate=(p,b)=>OkxOrdinaryMixedAccountTests.Shape(f,peer,p,b);
        f.Runtime.FiniteFundingPremise=(NavalFiniteOperatorPremise)OkxCurrentMarketFundingTests.Premise(f);
        await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        await using var finite=OkxOperationalTradingService.Create(f.Service,f.Clock,(s,k,h,x)=>OkxExecutionClient.ForTests(peer,f.Clock,s,k,h,x),peer);finite.BindFiniteSafety(()=>true);
        Assert.Equal("COMPLETED_STOP_WITH_EXACT_DUST",await OkxFiniteDemoSequence.RunAsync(finite,f.Service,Guid.NewGuid().ToString("N"),_=>{},default));
        var before=f.Service.BrokerSession!.ReadState();int sends=peer.Sends.Count;
        var observed=new List<(string Endpoint,OkxExecutionOrderDto Row)>();int exactSellReads=0;
        f.Mutate=(p,b)=>
        {
            OkxOrdinaryMixedAccountTests.Shape(f,peer,p,b);
            if(fault=="child-detail"&&p=="/api/v5/trade/order-algo")b["data"]![0]!["ordIdList"]=new JsonArray("778");
            if((fault is "pending" or "pending-terminal")&&p=="/api/v5/trade/orders-pending"&&!f.Paths.Last().Contains("after=",StringComparison.Ordinal))
            {
                var history=new JsonObject{["data"]=new JsonArray()};peer.Observe("/api/v5/trade/orders-history",history);
                var pending=history["data"]!.AsArray().Single(r=>r!["ordId"]!.GetValue<string>()=="776")!.DeepClone();pending["state"]=fault=="pending"?"live":"canceled";b["data"]=new JsonArray(pending);
            }
            if(p is not ("/api/v5/trade/order" or "/api/v5/trade/orders-pending" or "/api/v5/trade/orders-history" or "/api/v5/trade/orders-history-archive"))return;
            foreach(var row in b["data"]!.AsArray())
            {
                if(fault=="cash"&&row!["state"]!.GetValue<string>()=="filled")
                {
                    row!["posSide"]=p=="/api/v5/trade/order"?"net":"";
                    row["tradeQuoteCcy"]=p=="/api/v5/trade/order"?"":"USDC";
                }
                // Synthetic demonstration, NOT recovered original broker payloads.
                // Numeric lexical formatting and descriptive labels are not new economics.
                if(p!="/api/v5/trade/order")
                {
                    foreach(string n in new[]{"px","sz","fee","accFillSz"})
                    {
                        string s=row![n]!.GetValue<string>();row[n]=s+(s.Contains('.')?"0":".0");
                    }
                    row!["tag"]=p.EndsWith("archive",StringComparison.Ordinal)?"archive-label":"history-label";
                }
                if(p=="/api/v5/trade/order"&&row!["ordId"]!.GetValue<string>()=="778"&&++exactSellReads>1&&fault=="child-detail")row["fee"]="-0.123";
                if(p=="/api/v5/trade/orders-history"&&row!["ordId"]!.GetValue<string>()=="777")
                {
                    switch(fault)
                    {
                        case "state":row["state"]="live";break;
                        case "price":row["px"]="111";break;
                        case "quantity":row["sz"]="0.123";break;
                        case "fee":row["fee"]="-0.123";break;
                        case "fee-currency":row["feeCcy"]="EUR";break;
                        case "client":row["clOrdId"]="OtherOrder1";break;
                        case "order":row["ordId"]="779";break;
                        case "attachment":row["attachAlgoOrds"]![0]!["attachAlgoId"]="999";break;
                        case "stop":row["attachAlgoOrds"]![0]!["slTriggerPx"]="99";break;
                        case "children":row["linkedAlgoOrd"]!["algoId"]="999";break;
                        case "config":row["quickMgnType"]="auto_borrow";break;
                        case "mode":row["tdMode"]="cross";break;
                    }
                }
                // Leave malformed/unsupported rows to the actual production parser;
                // this observation inventory must not replace that parser's refusal.
                try{observed.Add((p,OkxOrderConsistencyOperandTests.Parse(row!,p,true)));}
                catch(Exception) when(fault=="mode"){}
            }
        };
        if(fault=="finite")
        {
            await finite.ReconcileAsync(default);var held=f.Service.BrokerSession!.ReadState();
            Assert.Contains(held.NavalBroker!.Operational!.Obligations,o=>o.StartsWith("ORDER_CHANGED_DURING_PASS:",StringComparison.Ordinal));
            Assert.Equal("STOP",held.Naval!.Mode);Assert.Equal(sends,peer.Sends.Count);return;
        }
        await finite.DisposeAsync();await f.Service.DisconnectAsync();f.Clock.Advance(TimeSpan.FromMinutes(31));
        await using var runtime=OkxNavalRuntime.ComposeLinuxDemo(f.Root,f.Binding,f,f,()=>true);
        await using var ordinary=OkxAccountConnectionService.ForTests(runtime,f.Root,f.Binding,f,f.Clock,
            (s,k,h,x)=>OkxExecutionObservationClient.ForTests(f,f.Clock,s,k,h,x,(t,c)=>{f.Clock.Advance(t);return Task.CompletedTask;}),
            (s,k,h,x)=>OkxObservationStream.ForTests((uri,c)=>Task.FromResult<System.Net.WebSockets.WebSocket>(new OperationalSocket(uri)),f.Clock,s,k,h,x),()=>OkxNavalStore.ForTests(f.Root,f.Binding));
        var request=f.Request with{Session=Guid.NewGuid()};var lease=await runtime.Activation.RequestAsync(request,default);Assert.NotNull(lease);Assert.True(await ordinary.ConnectAsync(request,lease!,default),ordinary.Status);
        await using var normal=OkxOperationalTradingService.Create(ordinary,f.Clock,(s,k,h,x)=>OkxExecutionClient.ForTests(peer,f.Clock,s,k,h,x),peer);
        var prepared=await normal.PrepareOrdinaryDemoAsync(default);var after=ordinary.BrokerSession!.ReadState();var x=after.NavalBroker!.Operational!;
        Assert.Contains(observed.GroupBy(v=>v.Row.OrdId),g=>g.Select(v=>v.Row.PayloadDigest).Distinct().Count()>1);
        Assert.Equal(sends,peer.Sends.Count);Assert.Equal(before.NavalBroker.HighWater,after.NavalBroker.HighWater);
        Assert.Equal(NavalJson.Serialize(before.NavalBroker.Operational!.Intents),NavalJson.Serialize(x.Intents));
        Assert.Equal("STOP",ordinary.BrokerSession.Coordinator.Snapshot().Mode);
        if(fault is not ("equal" or "cash"))
        {
            Assert.Equal("ORDINARY_DEMO_FRESH_RECONCILIATION_REQUIRED",prepared.Code);Assert.Null(x.OrdinaryDemo);
            string required=fault=="pending-terminal"?"PENDING_ORDER_LIFECYCLE_CONFLICT:776":fault=="mode"?"HISTORY_INCOMPLETE:History":fault=="order"?"FOREIGN_ORDER:":"ORDER_CHANGED_DURING_PASS:"+(fault=="pending"?"776":fault=="child-detail"?"778":"777");
            Assert.Contains(x.Obligations,o=>o.StartsWith(required,StringComparison.Ordinal));
            if(fault=="child-detail")Assert.True(exactSellReads>=2,"Required late exact-child merge was not reached");
            return;
        }
        foreach(var row in observed)Assert.Contains(x.Evidence,e=>e.Endpoint=="ORDER_ROW:"+row.Endpoint&&e.Digest==row.Row.PayloadDigest);
        foreach(var group in observed.GroupBy(v=>v.Row.OrdId))
            if(fault=="cash"&&group.First().Row.State=="filled")
            {
                var exact=group.First(v=>v.Endpoint=="/api/v5/trade/order").Row;
                Assert.Equal("",exact.TradeQuoteCcy);Assert.Equal("net",exact.PosSide);
                Assert.All(group.Where(v=>v.Endpoint!="/api/v5/trade/order"),v=>Assert.False(OkxOrderConsistency.Equivalent(exact,v.Row)));
                Assert.Contains(x.Orders,o=>o.OrderId==exact.OrdId&&o.ActualQuote=="USDC");
            }
            else Assert.All(group,v=>Assert.True(OkxOrderConsistency.Equivalent(group.First().Row,v.Row)));
        Assert.True(prepared.Code=="ORDINARY_DEMO_PREPARED","Reached closed mixed PREPARE: "+prepared.Code+" / "+string.Join(",",x.Obligations));
        Assert.DoesNotContain(x.Obligations,o=>o.StartsWith("UNLINKED_BILL:",StringComparison.Ordinal)||o=="BROKER_COVERAGE_INCOMPLETE"||o.StartsWith("ORDER_CHANGED_DURING_PASS:",StringComparison.Ordinal));
        Assert.Equal(4,x.Bills.Count);Assert.All(after.Naval!.Reservations,r=>Assert.True(r.TerminalCoverage));
        foreach(string endpoint in new[]{"/api/v5/trade/order","/api/v5/trade/orders-history","/api/v5/trade/orders-history-archive"})Assert.Contains(x.Evidence,e=>e.Endpoint==endpoint);
        Assert.Equal(sends,peer.Sends.Count);Assert.Equal(before.NavalBroker.HighWater,after.NavalBroker.HighWater);
        Assert.Equal(NavalJson.Serialize(before.NavalBroker.Baseline),NavalJson.Serialize(after.NavalBroker.Baseline));
        Assert.Equal(NavalJson.Serialize(before.Naval!.Fills),NavalJson.Serialize(after.Naval!.Fills));
        Assert.Equal("STOP",ordinary.BrokerSession.Coordinator.Snapshot().Mode);
    }
}
