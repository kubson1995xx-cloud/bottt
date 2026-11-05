using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Engine;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Apex.Desktop.Services;
using Xunit;
namespace Apex.Tests;
public sealed class OkxOrdinaryMixedAccountTests
{
    internal static void Shape(OperationalPeer f,FiniteDemoPeer peer,string path,JsonNode body)
    {
        OkxCurrentMarketFundingTests.Shape(f,peer,path,body);
        if(path=="/api/v5/account/instruments")
        {
            var sol=body["data"]![0]!.DeepClone();sol["instId"]="SOL-USDC";sol["baseCcy"]="SOL";sol["lotSz"]="0.000001";sol["minSz"]="0.000001";body["data"]=new JsonArray(sol);
        }
        if(path=="/api/v5/account/balance")
        {
            var rows=body["data"]![0]!["details"]!.AsArray();
            var sol=rows.Where(v=>v!["ccy"]!.GetValue<string>()=="SOL").ToArray();
            if(peer.BuyQuantity>0)rows.Remove(sol.Last());
            else foreach(var field in new[]{"cashBal","availBal","eq"})sol.Single()![field]="0";
        }
        if(path.StartsWith("/api/v5/account/bills",StringComparison.Ordinal))
        {
            var rows=body["data"]!.AsArray();
            foreach(var anchor in rows.ToArray())
            {
                bool sell=anchor!["ordId"]!.GetValue<string>()=="778";decimal q=sell?peer.SellQuantity:peer.BuyQuantity;
                anchor["balChg"]=N(sell?q*109.99m*(1-peer.FeeRate):q-peer.BuyFee);
                var counter=anchor.DeepClone();counter["billId"]=sell?"905":"904";counter["ccy"]=sell?"SOL":"USDC";counter["fee"]="0";counter["balChg"]=N(sell?-q:-q*peer.BuyPrice);rows.Add(counter);
            }
            body["data"]=new JsonArray(rows.OrderByDescending(v=>long.Parse(v!["billId"]!.GetValue<string>())).Select(v=>v!.DeepClone()).ToArray());
        }
    }
    private static string N(decimal n)=>n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    [Fact]
    public Task Settled_SOL_history_transitions_same_owner_with_full_loss_pinned_dust_and_idempotent_STOP()=>OkxSyntheticClockIsolationTests.Run("ordinary-mixed-history");
    [Theory][InlineData("BeforeReplace")][InlineData("AfterReplace")]
    public Task Transition_atomic_write_interruption_reopens_only_original_or_complete_pinned_owner(string boundary)=>OkxSyntheticClockIsolationTests.Run("ordinary-mixed-crash-"+boundary);
    internal static async Task HistoryScenario(string? crash=null)
    {
        await using var f=new OperationalPeer(finite:true);using var peer=new FiniteDemoPeer(f){BaseCurrency="SOL"};
        f.Mutate=(p,b)=>Shape(f,peer,p,b);f.Runtime.FiniteFundingPremise=(NavalFiniteOperatorPremise)OkxCurrentMarketFundingTests.Premise(f);
        await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        await using var loop=OkxOperationalTradingService.Create(f.Service,f.Clock,(s,k,h,x)=>OkxExecutionClient.ForTests(peer,f.Clock,s,k,h,x),peer);loop.BindFiniteSafety(()=>true);
        Assert.Equal("COMPLETED_STOP_WITH_EXACT_DUST",await OkxFiniteDemoSequence.RunAsync(loop,f.Service,Guid.NewGuid().ToString("N"),_=>{},default));
        var before=f.Service.BrokerSession!.ReadState();var dust=Assert.Single(before.Naval!.Reservations.Where(r=>r.Inventory>0));
        Assert.True(dust.Inventory<before.NavalBroker!.Baseline.Observations.Catalog.Single().Quantum);Assert.False(dust.ReductionUnsettled);
        Assert.All(before.NavalBroker.Operational!.Intents.Where(i=>i.Operation!=NavalExecutionOperation.Cancel),i=>Assert.Contains(i.Id,before.NavalBroker.Operational.SettledIntents));
        Assert.All(before.NavalBroker.Operational.Intents.Where(i=>i.Operation==NavalExecutionOperation.Cancel),i=>Assert.Contains(before.NavalBroker.Operational.Orders,o=>o.ClientId==i.ClientId&&o.State=="canceled"&&o.Accumulated==0));
        var prepare=typeof(NavalBrokerSession).GetMethod("PrepareOrdinaryDemo",BindingFlags.Instance|BindingFlags.NonPublic);
        Assert.True(prepare!=null,"Reached settled six-currency SOL owner with historical residual; explicit SAME-owner transition is absent");
        // Finite authority cannot issue ordinary operational admission.
        var refused=(NavalRiskResult)prepare!.Invoke(f.Service.BrokerSession,Array.Empty<object>())!;
        Assert.Equal(NavalMutationStatus.Refused,refused.Status);
        await loop.DisposeAsync();await f.Service.DisconnectAsync();f.Clock.Advance(TimeSpan.FromMinutes(31));
        await using var runtime=OkxNavalRuntime.ComposeLinuxDemo(f.Root,f.Binding,f,f,()=>true);
        int matchingBoundary=0;
        await using var ordinary=OkxAccountConnectionService.ForTests(runtime,f.Root,f.Binding,f,f.Clock,
            (s,k,h,x)=>OkxExecutionObservationClient.ForTests(f,f.Clock,s,k,h,x,(t,c)=>{f.Clock.Advance(t);return Task.CompletedTask;}),
            (s,k,h,x)=>OkxObservationStream.ForTests((uri,c)=>Task.FromResult<System.Net.WebSockets.WebSocket>(new OperationalSocket(uri)),f.Clock,s,k,h,x),()=>OkxNavalStore.ForTests(f.Root,f.Binding,b=>{if(crash!=null&&b.ToString()==crash&&++matchingBoundary==2)throw new IOException("synthetic-transition-write-interruption");}));
        var request=f.Request with{Session=Guid.NewGuid()};var lease=await runtime.Activation.RequestAsync(request,default);Assert.NotNull(lease);Assert.True(await ordinary.ConnectAsync(request,lease!,default),ordinary.Status);
        await using var normal=OkxOperationalTradingService.Create(ordinary,f.Clock,(s,k,h,x)=>OkxExecutionClient.ForTests(peer,f.Clock,s,k,h,x),peer);
        Assert.NotEqual(NavalMutationStatus.Applied,normal.Start().Status);
        var prepared=await normal.PrepareOrdinaryDemoAsync(default);
        if(crash!=null)
        {
            Assert.Equal("STORAGE_FROZEN",prepared.Code);await normal.DisposeAsync();await ordinary.DisposeAsync();
            using var reopenedStore=OkxNavalStore.ForTests(f.Root,f.Binding);
            using var reopened=NavalBrokerSession.OpenExisting(reopenedStore,before.NavalBroker!.Baseline.Binding,f.Service);var durable=reopened.ReadState();
            Assert.Equal(crash=="AfterReplace",durable.NavalBroker!.Operational!.OrdinaryDemo!=null);Assert.Equal("STOP",reopened.Coordinator.Snapshot().Mode);
            Assert.Equal(NavalJson.Serialize(before.NavalBroker.Baseline),NavalJson.Serialize(durable.NavalBroker.Baseline));Assert.Equal(NavalJson.Serialize(before.Naval.Fills),NavalJson.Serialize(durable.Naval!.Fills));Assert.Equal(NavalJson.Serialize(before.Naval.Accounts),NavalJson.Serialize(durable.Naval.Accounts));return;
        }
        Assert.Equal("ORDINARY_DEMO_PREPARED",prepared.Code);
        Assert.Equal("ORDINARY_DEMO_ALREADY_PREPARED",(await normal.PrepareOrdinaryDemoAsync(default)).Code);
        var after=ordinary.BrokerSession!.ReadState();var pin=after.NavalBroker!.Operational!.OrdinaryDemo!;
        Assert.Equal(NavalJson.Serialize(before.NavalBroker.Baseline),NavalJson.Serialize(after.NavalBroker.Baseline));Assert.Equal(before.NavalBroker.Namespace,after.NavalBroker.Namespace);Assert.Equal(before.NavalBroker.HighWater,after.NavalBroker.HighWater);
        Assert.Equal(NavalJson.Serialize(before.Naval.Fills),NavalJson.Serialize(after.Naval!.Fills));Assert.Equal(NavalJson.Serialize(before.Naval.Accounts),NavalJson.Serialize(after.Naval.Accounts));
        Assert.Equal(dust.Inventory,pin.Dust.Quantity);Assert.Equal(dust.AllocatedCost,pin.Dust.Cost);var quarantined=after.Naval.Reservations.Single(r=>r.Id==dust.Id);
        Assert.Equal(quarantined.Inventory*quarantined.QuarantineMark,quarantined.RiskHold);Assert.False(NavalCashRiskPolicy.Active(quarantined));
        var definition=NavalBrokerValidation.Definition(after.NavalBroker.Baseline);var s=after.Naval;
        decimal capital=Math.Min(s.Accounts.Sum(a=>a.Cash)+s.Reservations.Sum(r=>r.AllocatedCost),s.LiquidationEquity!.Value),reserve=quarantined.RiskHold;
        var candidate=new NavalReservation{Account=f.Binding.Account,Fleet=NavalFleet.Bravo,Intent=new("risk","risk","SOL-USDC",.001m,110,100,130),RiskHold=capital*.005m-reserve/2};
        Assert.Equal("TRADE_RISK",NavalCashRiskPolicy.Admission(s,candidate,definition));
        var without=s with{Reservations=s.Reservations.Select(r=>r.Id==dust.Id?r with{RiskHold=0,QuarantineMark=null,Inventory=0}:r).ToArray()};
        Assert.Null(NavalCashRiskPolicy.Admission(without,candidate,definition));
        Assert.True(NavalOrdinaryDemoPolicy.Blocking(after,"PROTECTION_GAP:new-reservation"));Assert.False(NavalOrdinaryDemoPolicy.Blocking(after,"PROTECTION_GAP:"+dust.Id));
        Assert.Equal(NavalMutationStatus.Applied,ordinary.BrokerSession.UpdateQuote(new("SOL-USDC",109.99m,125m,100,f.Clock.GetUtcNow(),f.Clock.GetUtcNow(),f.Clock.GetTimestamp())).Status);
        Assert.Equal(dust.Inventory*125m,ordinary.BrokerSession.ReadState().Naval!.Reservations.Single(r=>r.Id==dust.Id).RiskHold);
        request=request with{Purpose=OkxActivationPurpose.Execute,Session=ordinary.TradingSessionId};lease=await runtime.Activation.RequestAsync(request,default);Assert.True(normal.Arm(request,lease!));
        Assert.Equal(NavalMutationStatus.Applied,(await normal.ReconcileAsync(default)).Status);Assert.Equal(NavalMutationStatus.Applied,normal.Start().Status);normal.Stop();
    }
    [Fact]
    public Task Ordinary_current_Market_policy_accepts_unknown_raws_without_renewing_expired_finite_premise()=>OkxSyntheticClockIsolationTests.Run("ordinary-mixed-funding");
    internal static async Task FundingScenario()
    {
        await using var f=new OperationalPeer(finite:true);using var peer=new FiniteDemoPeer(f){BaseCurrency="SOL"};
        f.Mutate=(p,b)=>Shape(f,peer,p,b);f.Runtime.FiniteFundingPremise=(NavalFiniteOperatorPremise)OkxCurrentMarketFundingTests.Premise(f);
        await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        var baseline=f.Service.BrokerSession!.ReadState().NavalBroker!.Baseline;
        Assert.Equal(6,baseline.Observations.Balances.Count);Assert.Equal("SOL-USDC",Assert.Single(baseline.Observations.Catalog).Instrument);
        Assert.All(baseline.Observations.Balances,b=>Assert.Null(b.Interest));
        var funding=baseline.Observations.CurrentMarketFunding!;var expired=baseline.Observations.OperatorPremise! with{IssuedUtc=f.Clock.GetUtcNow().AddHours(-2),ExpiresUtc=f.Clock.GetUtcNow().AddHours(-1.9)};
        Assert.False(NavalCurrentMarketFunding.Validate(funding,expired,baseline.Observations.Balances,f.Binding.Account.AccountId,f.Binding.MainUid,f.Binding.CredentialBindingId,f.Request.CredentialFingerprint,funding.Connection,funding.Capture));
        var method=typeof(NavalCurrentMarketFunding).GetMethod("ValidateOrdinary",BindingFlags.Static|BindingFlags.NonPublic);
        Assert.True(method!=null,"Reached six-currency nullable finite owner; separately named ordinary Market sufficiency policy is absent");
        bool Validate(CurrentMarketFundingEvidenceV1 e)=> (bool)method!.Invoke(null,new object[]{e,baseline.Observations.Balances,f.Binding.Account.AccountId,f.Binding.MainUid,f.Binding.CredentialBindingId,f.Request.CredentialFingerprint,e.Connection,e.Capture})!;
        Assert.True(Validate(funding));Assert.False(Validate(funding with{DebtUsd=new("0.01",.01m)}));Assert.False(Validate(funding with{AllProductClearance="ESTABLISHED"}));
    }
}
