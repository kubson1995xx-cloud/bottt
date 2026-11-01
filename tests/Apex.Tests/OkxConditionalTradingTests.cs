using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Engine;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.Tests;

public sealed class OkxConditionalTradingTests
{
    [Fact]
    public async Task Production_Create_has_release_conditional_policy_but_stays_STOP_unarmed()
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        int reads=f.Paths.Count,opens=f.SecretOpens;
        await using var loop=OkxOperationalTradingService.Create(f.Service);
        Assert.NotNull(loop.ProtectionProfile);Assert.True(loop.ReadStatus().ProductionProfileApproved);
        Assert.False(loop.ReadStatus().Armed);Assert.False(loop.ReadStatus().Ready);Assert.Equal("STOP",loop.ReadStatus().Mode);
        Assert.Equal(reads,f.Paths.Count);Assert.Equal(opens,f.SecretOpens);Assert.Equal(NavalMutationStatus.Refused,loop.Start().Status);
    }
    [Fact]
    public async Task Public_quote_source_regression_cannot_be_rejuvenated_by_new_receipt()
    {
        var clock=new OperationalClock();using var peer=new QuotePeer(clock);using var feed=new OkxPublicMarketFeed(clock,peer);
        var spec=new NavalInstrument("BTC-USDC","BTC","USDC",.0001m,.0001m,.01m);
        await feed.ReadQuoteAsync(spec,default);peer.Regress=true;
        await Assert.ThrowsAsync<InvalidDataException>(()=>feed.ReadQuoteAsync(spec,default));
    }
    private static EngineState State(OperationalPeer f)=>JsonSerializer.Deserialize<EngineState>(File.ReadAllText(f.Target))!;
    private static OkxOperationalTradingService Production(OperationalPeer f,TradingPeer p)=>OkxOperationalTradingService.Create(f.Service,f.Clock,
        (s,k,h,x)=>Apex.Adapters.Okx.Execution.OkxExecutionClient.ForTests(p,f.Clock,s,k,h,x),p);
    private static async Task Arm(OperationalPeer f,OkxOperationalTradingService loop)
    {
        var r=f.Request with{Purpose=OkxActivationPurpose.Execute,Session=f.Service.TradingSessionId,ExpiresUtc=DateTimeOffset.UtcNow.AddSeconds(120)};
        var lease=await f.Runtime.Activation.RequestAsync(r,default);Assert.NotNull(lease);Assert.True(loop.Arm(r,lease!));
    }
    private static async Task Start(OperationalPeer f,OkxOperationalTradingService loop)
    {
        await Arm(f,loop);var result=await loop.ReconcileAsync(default);Assert.True(result.Status==NavalMutationStatus.Applied,result.Code);
        var started=loop.Start();Assert.True(started.Status==NavalMutationStatus.Applied,started.Code);
    }
    [Fact]
    public async Task Production_Create_uses_same_strategy_signed_FOK_protection_economics_and_restart_STOP()
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f);await using var loop=Production(f,peer);
        await Start(f,loop);Assert.Equal(NavalMutationStatus.Applied,(await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default)).Status);
        Assert.Equal(1,peer.Posts);Assert.True(peer.SawDispatching);Assert.Equal(1234.56m,loop.ReadStatus().Cash);
        Assert.False(loop.ReadStatus().Ready);Assert.Equal("ENTRY_OBSERVATION_PENDING",loop.ReadStatus().Code);
        TradingEconomicsFixture.Install(f,peer,peer.Size,peer.Size*.11m,true);Assert.Equal(NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);
        Assert.Equal(peer.Size,State(f).Naval!.Reservations.Single().Inventory);Assert.True(loop.ReadStatus().Ready);
        TradingEconomicsFixture.InstallTriggeredExit(f,peer);Assert.Equal(NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);
        Assert.Equal(0,State(f).Naval!.Reservations.Single().Inventory);Assert.Equal(1234.56m+peer.Size*9.77m,loop.ReadStatus().Cash);
        await f.Service.DisconnectAsync();Assert.True(f.Service.OpenExisting());Assert.Equal("STOP",f.Service.Coordinator!.Snapshot().Mode);Assert.Equal(1,peer.Posts);
    }
    [Theory]
    [InlineData("maxLmtSz","0.000001")]
    [InlineData("maxLmtAmt","0.01")]
    [InlineData("maxMktSz","0.01")]
    [InlineData("maxStopSz","")]
    public async Task Authenticated_private_maximum_or_unknown_unit_denies_before_signed_invocation(string field,string value)
    {
        await using var f=new OperationalPeer();f.Mutate=(p,b)=>{if(p=="/api/v5/account/instruments")b["data"]![0]![field]=value;};
        await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f);await using var loop=Production(f,peer);await Start(f,loop);
        var result=await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);
        Assert.Equal(0,peer.Posts);Assert.Equal(NavalMutationStatus.Refused,result.Status);Assert.Empty(State(f).Naval!.Reservations);
    }
    [Fact]
    public async Task Quote_only_production_refuses_base_fee_account_before_risk_increase()
    {
        await using var f=new OperationalPeer();f.Mutate=(p,b)=>{if(p=="/api/v5/account/config")b["data"]![0]!["feeType"]="0";};await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        using var peer=new TradingPeer(f);await using var loop=Production(f,peer);await Arm(f,loop);await loop.ReconcileAsync(default);
        Assert.Null(loop.ProtectionProfile);Assert.Equal(NavalMutationStatus.Refused,loop.Start().Status);await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);Assert.Equal(0,peer.Posts);
    }
    [Fact]
    public async Task STOP_revokes_entries_not_observation_and_explicit_rearm_can_recover_exact_unprotected_inventory()
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f);await using var loop=Production(f,peer);await Start(f,loop);
        Assert.Equal(NavalMutationStatus.Applied,(await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default)).Status);
        TradingEconomicsFixture.Install(f,peer,peer.Size,peer.Size*.11m,true);var basis=f.Mutate!;
        f.Mutate=(p,b)=>{basis(p,b);if(p=="/api/v5/trade/order-algo")b["data"]![0]!["state"]="canceled";};
        f.Service.RevokePermissions();int reads=f.Paths.Count;await loop.ReconcileAsync(default);
        Assert.True(f.Paths.Count>reads);Assert.Contains(State(f).NavalBroker!.Operational!.Obligations,o=>o.StartsWith("PROTECTION_GAP:",StringComparison.Ordinal));
        Assert.Equal(NavalMutationStatus.Refused,loop.Start().Status);await Arm(f,loop);peer.ProtectionCanceled=true;
        var exit=await loop.ExitAsync(State(f).Naval!.Reservations.Single().Id,default);Assert.True(exit.Status==NavalMutationStatus.Applied,exit.Code);Assert.Equal(2,peer.Posts);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Canceled_protection_then_failed_quote_or_Unknown_sell_persists_residual_and_never_resends(bool unknown)
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f);await using var loop=Production(f,peer);await Start(f,loop);
        Assert.Equal(NavalMutationStatus.Applied,(await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default)).Status);
        TradingEconomicsFixture.Install(f,peer,peer.Size,peer.Size*.11m,true);var basis=f.Mutate!;
        f.Mutate=(p,b)=>{basis(p,b);if(p=="/api/v5/trade/order-algo"&&peer.ProtectionCanceled)b["data"]![0]!["state"]="canceled";};
        Assert.Equal(NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);
        peer.FailBookAfterCancel=!unknown;peer.UnknownSell=unknown;string reservation=State(f).Naval!.Reservations.Single().Id;
        var result=await loop.ExitAsync(reservation,default);Assert.Equal("STOP",loop.ReadStatus().Mode);
        Assert.Equal(unknown?3:2,peer.Posts);
        Assert.Contains(State(f).NavalBroker!.Operational!.Obligations,o=>o.StartsWith("PROTECTION_GAP:",StringComparison.Ordinal));
        Assert.Equal(peer.Size,State(f).Naval!.Reservations.Single().Inventory);Assert.True(loop.ReadStatus().Held>0);
        int posts=peer.Posts;await loop.ExitAsync(reservation,default);Assert.Equal(posts,peer.Posts);
        peer.FailBookAfterCancel=false;await loop.ReconcileAsync(default);Assert.Equal(posts,peer.Posts);Assert.Equal(NavalMutationStatus.Refused,loop.Start().Status);
        await f.Service.DisconnectAsync();Assert.True(f.Service.OpenExisting());Assert.Equal("STOP",f.Service.Coordinator!.Snapshot().Mode);Assert.Equal(peer.Size,State(f).Naval!.Reservations.Single().Inventory);
    }
    [Fact]
    public async Task Explicit_attachment_placement_failure_is_observed_as_unsafe_and_allows_only_exact_owned_reduction()
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f);await using var loop=Production(f,peer);await Start(f,loop);
        await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);TradingEconomicsFixture.Install(f,peer,peer.Size,peer.Size*.11m,true);var basis=f.Mutate!;
        f.Mutate=(p,b)=>{basis(p,b);if(p is "/api/v5/trade/order" or "/api/v5/trade/orders-history" or "/api/v5/trade/orders-history-archive")foreach(var row in b["data"]!.AsArray()){row!["attachAlgoOrds"]![0]!["failCode"]="51020";row["attachAlgoOrds"]![0]!["attachAlgoId"]="";}};
        await loop.ReconcileAsync(default);Assert.Equal("STOP",loop.ReadStatus().Mode);Assert.False(loop.ReadStatus().Ready);
        Assert.Contains(State(f).NavalBroker!.Operational!.Obligations,o=>o.StartsWith("PROTECTION_GAP:",StringComparison.Ordinal));Assert.Equal(1,peer.Posts);
        Assert.Equal(NavalMutationStatus.Refused,loop.Start().Status);peer.ProtectionCanceled=true;
        var reduction=await loop.ExitAsync(State(f).Naval!.Reservations.Single().Id,default);Assert.True(reduction.Status==NavalMutationStatus.Applied,reduction.Code);Assert.Equal(2,peer.Posts);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Conclusively_canceled_zero_fill_sale_can_recover_only_on_explicit_new_operation_after_reconciliation(bool unknown)
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f);await using var loop=Production(f,peer);await Start(f,loop);
        await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);TradingEconomicsFixture.Install(f,peer,peer.Size,peer.Size*.11m,true);var basis=f.Mutate!;
        f.Mutate=(p,b)=>{basis(p,b);if(p=="/api/v5/trade/order-algo"&&peer.ProtectionCanceled)b["data"]![0]!["state"]="canceled";};
        await loop.ReconcileAsync(default);string reservation=State(f).Naval!.Reservations.Single().Id;peer.UnknownSell=unknown;
        await loop.ExitAsync(reservation,default);Assert.Equal(3,peer.Posts);string original=peer.ExitClient;
        await loop.ExitAsync(reservation,default);Assert.Equal(3,peer.Posts);
        f.Mutate=(p,b)=>
        {
            basis(p,b);if(p=="/api/v5/trade/order-algo")b["data"]![0]!["state"]="canceled";
            if(p is "/api/v5/trade/order" or "/api/v5/trade/orders-history" or "/api/v5/trade/orders-history-archive")
            {
                if(b["data"]!.AsArray().Count==0)return;var canceled=b["data"]![0]!.DeepClone();canceled["ordId"]="778";canceled["clOrdId"]=original;canceled["side"]="sell";canceled["state"]="canceled";
                foreach(string k in new[]{"accFillSz","fillSz","fee"})canceled[k]="0";canceled["attachAlgoOrds"]=new System.Text.Json.Nodes.JsonArray();
                if(p=="/api/v5/trade/order"&&(f.Paths.Last().Contains("clOrdId="+original,StringComparison.Ordinal)||f.Paths.Last().Contains("ordId=778",StringComparison.Ordinal)))b["data"]=new System.Text.Json.Nodes.JsonArray(canceled);
                else if(p!="/api/v5/trade/order")b["data"]!.AsArray().Insert(0,canceled);
            }
        };
        await loop.ReconcileAsync(default);Assert.Equal(3,peer.Posts);Assert.Equal("STOP",loop.ReadStatus().Mode);
        Assert.Contains(State(f).NavalBroker!.Operational!.SettledIntents,id=>State(f).NavalBroker!.Operational!.Intents.Any(i=>i.Id==id&&i.Reduction));
        peer.UnknownSell=false;peer.ExitOrderId="779";await Arm(f,loop);
        var recovery=await loop.ExitAsync(reservation,default);Assert.True(recovery.Status==NavalMutationStatus.Applied,recovery.Code);Assert.Equal(4,peer.Posts);Assert.NotEqual(original,peer.ExitClient);
        await loop.ExitAsync(reservation,default);Assert.Equal(4,peer.Posts);await f.Service.DisconnectAsync();Assert.True(f.Service.OpenExisting());Assert.Equal("STOP",f.Service.Coordinator!.Snapshot().Mode);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Signed_strategy_sale_individual_economics_resolve_even_Unknown_without_resend(bool unknown)
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f);await using var loop=Production(f,peer);await Start(f,loop);
        await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);TradingEconomicsFixture.Install(f,peer,peer.Size,peer.Size*.11m,true);var basis=f.Mutate!;
        f.Mutate=(p,b)=>{basis(p,b);if(p=="/api/v5/trade/order-algo"&&peer.ProtectionCanceled)b["data"]![0]!["state"]="canceled";};await loop.ReconcileAsync(default);
        peer.UnknownSell=unknown;peer.ExitSignal=true;await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);Assert.Equal(3,peer.Posts);
        TradingEconomicsFixture.InstallTriggeredExit(f,peer);var exit=f.Mutate!;
        f.Mutate=(p,b)=>
        {
            exit(p,b);if(p=="/api/v5/trade/order-algo"){b["data"]![0]!["state"]="canceled";b["data"]![0]!["ordIdList"]=new System.Text.Json.Nodes.JsonArray();}
            if(p.StartsWith("/api/v5/trade/",StringComparison.Ordinal)||p.StartsWith("/api/v5/account/bills",StringComparison.Ordinal))foreach(var row in b["data"]!.AsArray())if(row!["ordId"]?.GetValue<string>()=="778")row["clOrdId"]=peer.ExitClient;
        };
        var result=await loop.ReconcileAsync(default);Assert.True(result.Status==NavalMutationStatus.Applied,result.Code);
        var r=State(f).Naval!.Reservations.Single();Assert.Equal(0,r.Inventory);Assert.False(r.ReductionUnsettled);Assert.Equal(NavalReservationStage.Terminal,r.Stage);Assert.Equal(0,loop.ReadStatus().Held);
        Assert.Equal(1234.56m+peer.Size*9.77m,loop.ReadStatus().Cash);Assert.Equal("STOP",loop.ReadStatus().Mode);await loop.ReconcileAsync(default);Assert.Equal(3,peer.Posts);
    }
    [Fact]
    public async Task Existing_operator_surface_explains_conditional_risk_and_closed_exact_recovery_commands()
    {
        await using var h=new NavalProductionHost();await h.Start();using var response=await h.Send("/api/naval/okx/status","operator");
        using var state=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.True(state.RootElement.GetProperty("conditionalPolicyAvailable").GetBoolean());
        string script=await (await h.Send("/okx-controls.js","operator")).Content.ReadAsStringAsync();Assert.Contains("STOP nie oznacza",script);Assert.Contains("reservationId",script);
        using var request=h.Request("/api/naval/okx/missing/recover","operator","POST");request.Headers.Add("X-Apex-Local","1");
        request.Content=new StringContent("{\"commandId\":\"recover-1\",\"reservationId\":\"owned-1\"}",System.Text.Encoding.UTF8,"application/json");
        Assert.Equal(HttpStatusCode.NotFound,(await h.Client.SendAsync(request)).StatusCode);
    }
    [Theory]
    [InlineData("ordType","conditional")]
    [InlineData("slOrdPx","")]
    [InlineData("tpTriggerPxType","index")]
    public async Task Conditional_policy_requires_observed_two_sided_market_last_OCO_not_request_fields(string field,string value)
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f);await using var loop=Production(f,peer);await Start(f,loop);
        await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);TradingEconomicsFixture.Install(f,peer,peer.Size,peer.Size*.11m,true);var basis=f.Mutate!;
        f.Mutate=(p,b)=>{basis(p,b);if(p=="/api/v5/trade/order-algo")b["data"]![0]![field]=value;};await loop.ReconcileAsync(default);
        Assert.Equal("STOP",loop.ReadStatus().Mode);Assert.False(loop.ReadStatus().Ready);Assert.Equal(1,peer.Posts);
    }
    [Fact]
    public async Task Ordinary_FOK_canceled_without_fill_releases_only_after_exact_zero_fee_balance_concordance()
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f);await using var loop=Production(f,peer);await Start(f,loop);
        await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);Assert.True(loop.ReadStatus().Held>0);TradingEconomicsFixture.Install(f,peer,0,0);var basis=f.Mutate!;
        f.Mutate=(p,b)=>
        {
            basis(p,b);
            if(p.Contains("fills",StringComparison.Ordinal)||p.StartsWith("/api/v5/account/bills",StringComparison.Ordinal))b["data"]=new System.Text.Json.Nodes.JsonArray();
            if(p is "/api/v5/trade/order" or "/api/v5/trade/orders-history" or "/api/v5/trade/orders-history-archive")foreach(var row in b["data"]!.AsArray())row!["attachAlgoOrds"]=new System.Text.Json.Nodes.JsonArray();
            if(p=="/api/v5/account/balance")b["data"]![0]!["details"]!.AsArray().RemoveAt(1);
        };
        var result=await loop.ReconcileAsync(default);Assert.True(result.Status==NavalMutationStatus.Applied,result.Code);Assert.Equal(0,loop.ReadStatus().Held);Assert.Equal(1234.56m,loop.ReadStatus().Cash);Assert.Empty(State(f).Naval!.Fills);Assert.Equal(NavalReservationStage.Terminal,State(f).Naval!.Reservations.Single().Stage);
        await f.Service.DisconnectAsync();Assert.True(f.Service.OpenExisting());Assert.Equal(0,f.Service.Coordinator!.Snapshot().Held);Assert.Equal("STOP",f.Service.Coordinator.Snapshot().Mode);Assert.Equal(1,peer.Posts);
    }
    [Fact]
    public async Task Unknown_protection_cancel_is_settled_only_from_exact_observed_cancellation_not_ACK()
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f);await using var loop=Production(f,peer);await Start(f,loop);
        await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);TradingEconomicsFixture.Install(f,peer,peer.Size,peer.Size*.11m,true);var basis=f.Mutate!;
        f.Mutate=(p,b)=>{basis(p,b);if(p=="/api/v5/trade/order-algo"&&peer.ProtectionCanceled)b["data"]![0]!["state"]="canceled";};await loop.ReconcileAsync(default);
        peer.UnknownCancel=true;await loop.ExitAsync(State(f).Naval!.Reservations.Single().Id,default);Assert.Equal(2,peer.Posts);
        await loop.ReconcileAsync(default);Assert.Equal(2,peer.Posts);
        Assert.Contains(State(f).NavalBroker!.Operational!.SettledIntents,id=>State(f).NavalBroker!.Operational!.Intents.Any(i=>i.Id==id&&i.Operation==NavalExecutionOperation.CancelProtection));
        var reduced=await loop.ExitAsync(State(f).Naval!.Reservations.Single().Id,default);Assert.True(reduced.Status==NavalMutationStatus.Applied,reduced.Code);Assert.Equal(3,peer.Posts);
    }
    private sealed class QuotePeer(OperationalClock clock):HttpMessageHandler
    {
        internal bool Regress;
        private readonly long first=clock.GetUtcNow().ToUnixTimeMilliseconds();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"code\":\"0\",\"data\":[{\"asks\":[[\"110\",\"100\",\"0\",\"1\"]],\"bids\":[[\"109.99\",\"100\",\"0\",\"1\"]],\"ts\":\""+(Regress?first-1:first)+"\"}]}")});
    }
}
