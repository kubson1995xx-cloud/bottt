using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.Tests;

public sealed partial class OkxConditionalTradingTests
{
    private static object? PendingContinuation(OperationalPeer f)=>typeof(NavalPortfolioCoordinator).GetField("_brokerContinuation",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(f.Service.Coordinator);
    private static async Task ContinuationHolding(OperationalPeer f,TradingPeer peer,OkxOperationalTradingService loop)
    {
        await Start(f,loop);Assert.Null(PendingContinuation(f));
        Assert.Equal(NavalMutationStatus.Applied,(await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default)).Status);
        peer.ExpectedSellSize=peer.Size*(1-.001m);InstallOrdinaryPair(f,peer,peer.Size*.001m);
        Assert.Equal(NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);
        Assert.Equal("RUNNING",loop.ReadStatus().Mode);Assert.True(loop.ReadStatus().Ready);peer.ExitSignal=true;
    }
    private static OperationalPeer ContinuationAccount()
    {
        var f=new OperationalPeer();f.Mutate=(p,b)=>{if(p=="/api/v5/account/config")b["data"]![0]!["feeType"]="0";};return f;
    }
    private static async Task StrategyExit(OperationalPeer f,TradingPeer peer,OkxOperationalTradingService loop)
    {
        var exit=await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);Assert.True(exit.Status==NavalMutationStatus.Applied,exit.Code);
        Assert.Equal(3,peer.Posts);Assert.NotNull(PendingContinuation(f));Assert.Equal("STOP",loop.ReadStatus().Mode);
    }
    [Fact]
    public Task Strategy_continuation_RunAsync_consumes_once_then_next_distinct_signal_dispatches_once()=>OkxSyntheticClockIsolationTests.Run("ordinary-continuation-run");
    internal static async Task ContinuationRunScenario()
    {
        await using var f=ContinuationAccount();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        using var peer=new TradingPeer(f);await using var loop=Production(f,peer);await ContinuationHolding(f,peer,loop);await StrategyExit(f,peer,loop);
        InstallOrdinarySellPair(f,peer,0);
        using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var running=loop.RunAsync(cancellation.Token);
        for(int n=0;n<200&&loop.ReadStatus().Mode!="RUNNING";n++)await Task.Delay(10);
        Assert.Equal("RUNNING",loop.ReadStatus().Mode);Assert.True(loop.ReadStatus().Ready);Assert.Null(PendingContinuation(f));
        Assert.Equal(0,State(f).Naval!.Reservations.Single().Inventory);Assert.Equal(3,peer.Posts);
        // Same-day entry was already consumed; changing the feed is not a new signal.
        peer.ExitSignal=false;Assert.Equal("SIGNAL_CONSUMED",(await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default)).Code);Assert.Equal(3,peer.Posts);
        string original=State(f).Naval!.Commands.Single().SignalId;
        peer.EntryOrderId="1777";
        f.Clock.Advance(TimeSpan.FromDays(1),true,true);
        for(int n=0;n<1000&&(peer.Posts!=4||State(f).NavalBroker!.Operational!.Intents.Last().Stage!=NavalExecutionStage.Accepted);n++)
        {
            Assert.False(cancellation.IsCancellationRequested);await Task.Delay(10);
            if(peer.Posts==4&&State(f).NavalBroker!.Operational!.Intents.Last().Stage==NavalExecutionStage.Accepted)break;
        }
        cancellation.Cancel();try{await running;}catch(OperationCanceledException){}
        Assert.Equal(4,peer.Posts);
        var state=State(f);Assert.Equal(2,state.Naval!.Reservations.Count);Assert.Equal(2,state.Naval.Commands.Count);Assert.NotEqual(original,state.Naval.Commands.Last().SignalId);
        Assert.Equal(NavalExecutionStage.Accepted,state.NavalBroker!.Operational!.Intents.Last().Stage);
        Assert.Equal("1777",state.NavalBroker.Operational.Intents.Last().Outcome!.OrderId);
        await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);await loop.RunOnceAsync(NavalFleet.Bravo,"BTC-USDC",default);Assert.Equal(4,peer.Posts);
    }
    [Theory]
    [InlineData("stop")]
    [InlineData("emergency")]
    [InlineData("rearm")]
    [InlineData("permissions")]
    [InlineData("unrelated")]
    [InlineData("disconnect")]
    [InlineData("dispose")]
    public async Task Strategy_continuation_external_veto_after_issue_never_resurrects(string veto)
    {
        await using var f=ContinuationAccount();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        using var peer=new TradingPeer(f);await using var loop=Production(f,peer);await ContinuationHolding(f,peer,loop);await StrategyExit(f,peer,loop);
        switch(veto)
        {
            case "stop":loop.Stop();break;
            case "emergency":loop.EmergencyStop();break;
            case "rearm":await Arm(f,loop);break;
            case "permissions":f.Service.RevokePermissions();break;
            case "unrelated":f.Service.Coordinator!.Stop("UNRELATED_SAFETY_STOP");break;
            case "disconnect":await f.Service.DisconnectAsync();Assert.True(f.Service.OpenExisting());break;
            case "dispose":await loop.DisposeAsync();break;
        }
        Assert.Null(PendingContinuation(f));InstallOrdinarySellPair(f,peer,0);
        if(veto is "disconnect" or "dispose")
        {
            if(veto=="dispose"){await f.Service.DisconnectAsync();Assert.True(f.Service.OpenExisting());}
            // New owner/connection never inherits the nonserialized capability.
            Assert.Equal("STOP",f.Service.Coordinator!.Snapshot().Mode);Assert.Null(PendingContinuation(f));
        }
        else for(int n=0;n<2;n++)
        {
            await loop.ReconcileAsync(default);Assert.Equal("STOP",loop.ReadStatus().Mode);Assert.Null(PendingContinuation(f));
            Assert.Equal(0,State(f).Naval!.Reservations.Single().Inventory);Assert.Empty(loop.ReadStatus().Obligations);
        }
        Assert.Equal(3,peer.Posts);
    }
    [Fact]
    public async Task Strategy_continuation_operator_STOP_while_cancel_response_blocked_cannot_be_erased()
    {
        await using var f=ContinuationAccount();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        using var peer=new TradingPeer(f);await using var loop=Production(f,peer);await ContinuationHolding(f,peer,loop);
        var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);peer.HoldCancelResponse=new(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.BeforeMutationResponse=kind=>{if(kind=="cancel")reached.TrySetResult();};
        var exit=loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(PendingContinuation(f));loop.Stop();Assert.Null(PendingContinuation(f));peer.HoldCancelResponse.SetResult();await exit;
        Assert.Equal(2,peer.Posts);Assert.Empty(peer.ExitClient);
        // A fresh explicit rearm permits a manual safety reduction, never the old continuation.
        await Arm(f,loop);var manual=await loop.ExitAsync(State(f).Naval!.Reservations.Single().Id,default);Assert.True(manual.Status==NavalMutationStatus.Applied,manual.Code);
        InstallOrdinarySellPair(f,peer,0);for(int n=0;n<2;n++){await loop.ReconcileAsync(default);Assert.Equal("STOP",loop.ReadStatus().Mode);Assert.Null(PendingContinuation(f));}
        Assert.Equal(3,peer.Posts);Assert.Equal(0,State(f).Naval!.Reservations.Single().Inventory);
    }
    [Fact]
    public async Task Strategy_continuation_operator_STOP_during_final_flat_observation_cannot_be_erased()
    {
        await using var f=ContinuationAccount();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        using var peer=new TradingPeer(f);await using var loop=Production(f,peer);await ContinuationHolding(f,peer,loop);await StrategyExit(f,peer,loop);InstallOrdinarySellPair(f,peer,0);
        var basis=f.Mutate!;using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        f.Mutate=(p,b)=>{basis(p,b);if(p=="/api/v5/account/balance"){entered.Set();Assert.True(release.Wait(TimeSpan.FromSeconds(10)));}};
        var final=Task.Run(()=>loop.ReconcileAsync(default));Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));Assert.NotNull(PendingContinuation(f));loop.Stop();release.Set();await final;
        f.Mutate=basis;await loop.ReconcileAsync(default);Assert.Equal(0,State(f).Naval!.Reservations.Single().Inventory);Assert.Empty(loop.ReadStatus().Obligations);Assert.True(f.Service.Coordinator!.Snapshot().Ready);
        Assert.Equal("STOP",loop.ReadStatus().Mode);Assert.Null(PendingContinuation(f));Assert.Equal(3,peer.Posts);
    }
    [Theory]
    [InlineData("unknown-sell")]
    [InlineData("unknown-cancel")]
    [InlineData("collection")]
    [InlineData("foreign")]
    [InlineData("quote")]
    public async Task Strategy_continuation_unsafe_or_uncertain_boundary_revokes_permanently(string fault)
    {
        await using var f=ContinuationAccount();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        using var peer=new TradingPeer(f);await using var loop=Production(f,peer);await ContinuationHolding(f,peer,loop);
        peer.UnknownSell=fault=="unknown-sell";peer.UnknownCancel=fault=="unknown-cancel";peer.FailBookAfterCancel=fault=="quote";
        if(fault is "collection" or "foreign")
        {
            var basis=f.Mutate!;f.Mutate=(p,b)=>{basis(p,b);if(!peer.ProtectionCanceled)return;
                if(fault=="collection"&&p=="/api/v5/account/balance")b["code"]="50011";
                if(fault=="foreign"&&p=="/api/v5/trade/orders-pending"&&!f.Paths.Last().Contains("after=",StringComparison.Ordinal)){var copy=System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(FindOracle()))!["endpoints"]!["order"]!["control"]!["data"]![0]!.DeepClone();copy["ordId"]="999";copy["clOrdId"]="foreign";copy["instId"]="BTC-USDC";copy["tradeQuoteCcy"]="USDC";copy["state"]="live";b["data"]!.AsArray().Add(copy);}
            };
        }
        await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);Assert.Null(PendingContinuation(f));Assert.Equal("STOP",loop.ReadStatus().Mode);
        if(fault=="foreign")Assert.Contains(loop.ReadStatus().Obligations,o=>o.StartsWith("FOREIGN_ORDER:",StringComparison.Ordinal));
        if(fault=="collection")Assert.Contains("BALANCE_UNKNOWN",loop.ReadStatus().Obligations);
        if(fault is "unknown-sell" or "unknown-cancel")Assert.Equal(NavalExecutionStage.SubmitUnknown,State(f).NavalBroker!.Operational!.Intents.Last().Stage);
        if(fault=="unknown-sell")
        {
            InstallOrdinarySellPair(f,peer,0);for(int n=0;n<2;n++){await loop.ReconcileAsync(default);Assert.Equal("STOP",loop.ReadStatus().Mode);Assert.Null(PendingContinuation(f));}
            Assert.Equal(0,State(f).Naval!.Reservations.Single().Inventory);Assert.Empty(loop.ReadStatus().Obligations);
        }
        else{peer.FailBookAfterCancel=false;await loop.ReconcileAsync(default);Assert.Equal("STOP",loop.ReadStatus().Mode);Assert.Null(PendingContinuation(f));}
        int posts=peer.Posts;await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);Assert.Equal(posts,peer.Posts);
    }
    private static string FindOracle()
    {
        var dir=new System.IO.DirectoryInfo(AppContext.BaseDirectory);while(dir!=null&&!System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName,"tests/Apex.Adapters.Okx.Tests/Fixtures")))dir=dir.Parent;
        return System.IO.Path.Combine(dir!.FullName,"tests/Apex.Adapters.Okx.Tests/Fixtures/ExecutionObservations/recovery-schema.json");
    }
    [Theory]
    [InlineData("account")]
    [InlineData("collection")]
    [InlineData("quote")]
    public async Task Strategy_continuation_final_pass_refusal_then_clean_flat_pass_never_restores_token(string fault)
    {
        await using var f=ContinuationAccount();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        using var peer=new TradingPeer(f);await using var loop=Production(f,peer);await ContinuationHolding(f,peer,loop);await StrategyExit(f,peer,loop);InstallOrdinarySellPair(f,peer,0);
        var basis=f.Mutate!;f.Mutate=(p,b)=>{basis(p,b);if(fault=="account"&&p=="/api/v5/account/config")b["data"]![0]!["autoLoan"]=true;if(fault=="collection"&&p=="/api/v5/account/balance")b["code"]="50011";};
        peer.FailBookAfterCancel=fault=="quote";await loop.ReconcileAsync(default);Assert.Equal("STOP",loop.ReadStatus().Mode);Assert.Null(PendingContinuation(f));
        f.Mutate=basis;peer.FailBookAfterCancel=false;
        for(int n=0;n<2;n++){await loop.ReconcileAsync(default);Assert.Equal("STOP",loop.ReadStatus().Mode);Assert.Null(PendingContinuation(f));Assert.Empty(loop.ReadStatus().Obligations);Assert.Equal(0,State(f).Naval!.Reservations.Single().Inventory);}
        Assert.Equal(3,peer.Posts);
    }
    [Fact]
    public async Task Strategy_continuation_finite_production_sequence_never_mints_or_consumes_capability()
    {
        await using var f=new OperationalPeer(finite:true);using var peer=new FiniteDemoPeer(f);f.Mutate=peer.Observe;
        await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        await using var loop=OkxOperationalTradingService.Create(f.Service,f.Clock,(s,k,h,x)=>Apex.Adapters.Okx.Execution.OkxExecutionClient.ForTests(peer,f.Clock,s,k,h,x),peer);
        loop.BindFiniteSafety(()=>true);Assert.Equal(-1,loop.ContinuationEpoch);Assert.Null(PendingContinuation(f));
        Assert.Equal("COMPLETED_STOP_WITH_EXACT_DUST",await OkxFiniteDemoSequence.RunAsync(loop,f.Service,Guid.NewGuid().ToString("N"),_=>Assert.Null(PendingContinuation(f)),default));
        Assert.Equal("NO_STRATEGY_CONTINUATION",f.Service.BrokerSession!.ContinueStrategy().Code);Assert.Null(PendingContinuation(f));Assert.Equal("STOP",loop.ReadStatus().Mode);
        Assert.Equal("FINITE_DEMO_NO_STRATEGY_SCHEDULER",(await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default)).Code);
    }
    [Fact]
    public async Task Strategy_continuation_original_token_rejects_LIVE_and_changed_source_bindings()
    {
        await using var f=ContinuationAccount();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        using var peer=new TradingPeer(f);await using var loop=Production(f,peer);await ContinuationHolding(f,peer,loop);
        Assert.Equal(NavalMutationStatus.Refused,f.Service.BrokerSession!.RequestStrategyExit(State(f).Naval!.Reservations.Single().Id,"unregistered-exit").Status);Assert.Null(PendingContinuation(f));
        await StrategyExit(f,peer,loop);var token=PendingContinuation(f)!;
        var binding=typeof(NavalPortfolioCoordinator).GetMethod("StrategyBinding",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var original=State(f);Assert.True((bool)binding.Invoke(f.Service.Coordinator,new[]{f.Service.BrokerSession,original,token})!);
        foreach(string mutation in new[]{"live","account","generation","stop-epoch"})
        {
            var changed=State(f);var baseline=changed.NavalBroker!.Baseline;
            if(mutation is "live" or "account")changed.NavalBroker=changed.NavalBroker with{Baseline=baseline with{Binding=baseline.Binding with{Account=baseline.Binding.Account with{Environment=mutation=="live"?NavalEnvironment.Live:NavalEnvironment.OkxDemo,AccountId=mutation=="account"?"other":baseline.Binding.Account.AccountId}},Digest=mutation=="live"?baseline.Digest:"changed-source"}};
            if(mutation=="generation")changed.Naval=changed.Naval! with{Generation=changed.Naval.Generation+1};
            if(mutation=="stop-epoch")changed.NavalBroker=changed.NavalBroker with{Operational=changed.NavalBroker.Operational! with{StopEpoch=changed.NavalBroker.Operational.StopEpoch+1}};
            Assert.False((bool)binding.Invoke(f.Service.Coordinator,new[]{f.Service.BrokerSession,changed,token})!);
        }
        // Classification above is offline cloned input, not authority to create/use a LIVE session.
        loop.Stop();InstallOrdinarySellPair(f,peer,0);await loop.ReconcileAsync(default);Assert.Equal("STOP",loop.ReadStatus().Mode);
    }
    [Fact]
    public async Task Strategy_continuation_manual_exit_remains_STOP_with_flat_fresh_clean_observations()
    {
        await using var f=ContinuationAccount();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        using var peer=new TradingPeer(f);await using var loop=Production(f,peer);await ContinuationHolding(f,peer,loop);
        Assert.Equal(NavalMutationStatus.Applied,(await loop.ExitAsync(State(f).Naval!.Reservations.Single().Id,default)).Status);Assert.Null(PendingContinuation(f));InstallOrdinarySellPair(f,peer,0);
        for(int n=0;n<2;n++){await loop.ReconcileAsync(default);Assert.Equal("STOP",loop.ReadStatus().Mode);Assert.True(loop.ReadStatus().Ready);Assert.Null(PendingContinuation(f));}Assert.Equal(3,peer.Posts);
    }
    [Fact]
    public async Task Strategy_continuation_actual_sell_dust_keeps_inventory_and_never_grants_entry()
    {
        await using var f=ContinuationAccount();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        using var peer=new TradingPeer(f);await using var loop=Production(f,peer);await ContinuationHolding(f,peer,loop);await StrategyExit(f,peer,loop);
        decimal dust=State(f).NavalBroker!.Baseline.Observations.Catalog.Single().Quantum/10;
        peer.ExpectedSellSize-=dust;InstallOrdinarySellPair(f,peer,dust);
        for(int n=0;n<2;n++){await loop.ReconcileAsync(default);Assert.Equal("STOP",loop.ReadStatus().Mode);Assert.Null(PendingContinuation(f));Assert.Equal(dust,State(f).Naval!.Reservations.Single().Inventory);}
        Assert.Equal(3,peer.Posts);
    }
    [Fact]
    public async Task Strategy_continuation_conclusive_zero_fill_canceled_sell_revokes_instead_of_waiting_for_flat()
    {
        await using var f=ContinuationAccount();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        using var peer=new TradingPeer(f);await using var loop=Production(f,peer);await ContinuationHolding(f,peer,loop);await StrategyExit(f,peer,loop);
        var basis=f.Mutate!;f.Mutate=(p,b)=>
        {
            basis(p,b);
            if(p is "/api/v5/trade/order" or "/api/v5/trade/orders-history" or "/api/v5/trade/orders-history-archive")
            {
                if(b["data"]!.AsArray().Count==0)return;var canceled=b["data"]![0]!.DeepClone();canceled["ordId"]=peer.ExitOrderId;canceled["clOrdId"]=peer.ExitClient;canceled["side"]="sell";canceled["state"]="canceled";
                foreach(string k in new[]{"accFillSz","fillSz","fee"})canceled[k]="0";canceled["sz"]=peer.ExpectedSellSize!.Value.ToString(CultureInfo.InvariantCulture);canceled["feeCcy"]="USDC";canceled["attachAlgoOrds"]=new System.Text.Json.Nodes.JsonArray();
                if(p=="/api/v5/trade/order"&&(f.Paths.Last().Contains("clOrdId="+peer.ExitClient,StringComparison.Ordinal)||f.Paths.Last().Contains("ordId="+peer.ExitOrderId,StringComparison.Ordinal)))b["data"]=new System.Text.Json.Nodes.JsonArray(canceled);
                else if(p!="/api/v5/trade/order")b["data"]!.AsArray().Insert(0,canceled);
            }
        };
        await loop.ReconcileAsync(default);var state=State(f);Assert.Contains(state.NavalBroker!.Operational!.Intents.Last().Id,state.NavalBroker.Operational.SettledIntents);
        Assert.Equal(peer.ExpectedSellSize,state.Naval!.Reservations.Single().Inventory);Assert.Null(PendingContinuation(f));Assert.Equal("STOP",loop.ReadStatus().Mode);
        await loop.ReconcileAsync(default);Assert.Null(PendingContinuation(f));Assert.Equal("STOP",loop.ReadStatus().Mode);Assert.Equal(3,peer.Posts);
    }
}
