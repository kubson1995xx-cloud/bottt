using System;
using System.Reflection;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Engine;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.Tests;
public sealed class OkxFiniteDemoTests
{
    private static EngineState State(OperationalPeer f)=>JsonSerializer.Deserialize<EngineState>(File.ReadAllText(f.Target))!;
    [Fact]
    public async Task Production_finite_sequence_cancels_first_buys_once_sells_only_net_and_retains_exact_dust_STOP()
    {
        await using var f=new OperationalPeer(finite:true);using var peer=new FiniteDemoPeer(f);f.Mutate=peer.Observe;
        await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        await using var loop=OkxOperationalTradingService.Create(f.Service,f.Clock,(s,k,h,x)=>OkxExecutionClient.ForTests(peer,f.Clock,s,k,h,x),peer);
        loop.BindFiniteSafety(()=>!peer.UnsafeAtFinal);
        var stages=new System.Collections.Generic.List<string>();
        var errors=new System.Collections.Generic.List<string>();
        EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> trace=(_,e)=>{if(e.Exception is InvalidOperationException or ArgumentException)errors.Add(e.Exception.ToString());};
        AppDomain.CurrentDomain.FirstChanceException+=trace;
        string result;try{result=await OkxFiniteDemoSequence.RunAsync(loop,f.Service,Guid.NewGuid().ToString("N"),stages.Add,default);}finally{AppDomain.CurrentDomain.FirstChanceException-=trace;}
        Assert.True(result=="COMPLETED_STOP_WITH_EXACT_DUST",result+"; "+string.Join(";",stages)+"; "+string.Join("\n",errors));
        Assert.Equal(new[]{"/api/v5/trade/order","/api/v5/trade/cancel-order","/api/v5/trade/order","/api/v5/trade/cancel-algos","/api/v5/trade/order"},peer.Sends);
        var state=State(f);Assert.Equal(2,state.Naval!.Fills.Count);Assert.Equal(0.0000998m,state.Naval.Reservations.Sum(r=>r.Inventory));Assert.Equal(.0001m,peer.SellQuantity);
        byte[] report=Apex.Okx.DemoAcceptance.TransactionReport.Build(Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow,true,result,stages.ToArray(),state,null,loop.FiniteDiagnostics(),false);
        using(var projection=JsonDocument.Parse(report))
        {
            Assert.True(projection.RootElement.GetProperty("complete").GetBoolean());Assert.False(projection.RootElement.GetProperty("schedulerStarted").GetBoolean());
            var deltas=projection.RootElement.GetProperty("balanceDeltas").EnumerateArray().ToArray();Assert.Equal(2,deltas.Length);
            foreach(var d in deltas)Assert.Equal(d.GetProperty("netDelta").GetDecimal(),d.GetProperty("acceptedLedgerGrossLeg").GetDecimal()+d.GetProperty("acceptedLedgerFeeLeg").GetDecimal());
            Assert.Equal(.0000998m,deltas.Single(d=>d.GetProperty("Currency").GetString()=="BTC").GetProperty("netDelta").GetDecimal());
        }
        using(var credentials=new Apex.Okx.DemoAcceptance.EphemeralCredentials(OperationalPeer.Key.ToArray(),OperationalPeer.Secret.ToArray(),OperationalPeer.Pass.ToArray()))
        {Assert.False(credentials.ContainsSecret(report));Assert.True(credentials.ContainsSecret(OperationalPeer.Secret));}
        Assert.Equal(.0002m,peer.BuyQuantity);Assert.Equal("STOP",loop.ReadStatus().Mode);Assert.False(loop.ReadStatus().Armed);
        Assert.Equal("FINITE_DEMO_NO_STRATEGY_SCHEDULER",(await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default)).Code);
        int sends=peer.Sends.Count;await loop.SubmitFiniteDemoAsync(false,default);Assert.Equal(sends,peer.Sends.Count);
        await f.Service.DisconnectAsync();Assert.True(f.Service.OpenExisting());Assert.Equal("STOP",f.Service.Coordinator!.Snapshot().Mode);Assert.Equal(.0000998m,State(f).Naval!.Reservations.Sum(r=>r.Inventory));
    }
    [Theory]
    [InlineData("submit")]
    [InlineData("cancel")]
    [InlineData("foreign")]
    public async Task Unknown_submit_cancel_or_foreign_balance_never_allows_second_buy(string failure)
    {
        await using var f=new OperationalPeer(finite:true);using var peer=new FiniteDemoPeer(f){UnknownSubmit=failure=="submit",UnknownCancel=failure=="cancel"};f.Mutate=peer.Observe;
        await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));peer.ForeignBalance=failure=="foreign";
        await using var loop=OkxOperationalTradingService.Create(f.Service,f.Clock,(s,k,h,x)=>OkxExecutionClient.ForTests(peer,f.Clock,s,k,h,x),peer);loop.BindFiniteSafety(()=>true);
        string result=await OkxFiniteDemoSequence.RunAsync(loop,f.Service,Guid.NewGuid().ToString("N"),_=>{},default);
        Assert.DoesNotContain("COMPLETED",result);Assert.Equal("",peer.BuyClient);Assert.True(peer.Sends.Count<=2);Assert.Equal("STOP",loop.ReadStatus().Mode);Assert.False(loop.ReadStatus().Armed);
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("wrong")]
    [InlineData("rebate")]
    [InlineData("overrun")]
    public async Task Missing_wrong_third_currency_rebate_and_fee_overrun_stop_without_sell(string failure)
    {
        await using var f=new OperationalPeer(finite:true);using var peer=new FiniteDemoPeer(f){MissingFee=failure=="missing",WrongFee=failure=="wrong",Rebate=failure=="rebate",FeeOverrun=failure=="overrun"};f.Mutate=peer.Observe;
        await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        await using var loop=OkxOperationalTradingService.Create(f.Service,f.Clock,(s,k,h,x)=>OkxExecutionClient.ForTests(peer,f.Clock,s,k,h,x),peer);loop.BindFiniteSafety(()=>true);
        string result=await OkxFiniteDemoSequence.RunAsync(loop,f.Service,Guid.NewGuid().ToString("N"),_=>{},default);
        Assert.DoesNotContain("COMPLETED",result);Assert.Equal("",peer.SellClient);Assert.Equal(3,peer.Sends.Count);Assert.Equal("STOP",loop.ReadStatus().Mode);
    }
    [Fact]
    public void Minimum_gross_buy_uses_only_net_base_lots_and_refuses_ten_quote_overrun()
    {
        var spec=new NavalInstrument("BTC-USDC","BTC","USDC",.0001m,.0001m,.01m);
        var q=new NavalBrokerQuote(spec.Instrument,109.99m,110m,100,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,1);
        var plan=OkxOperationalTradingService.MinimumRoundTrip(spec,new(.001m,true,.001m,false),q,false);
        Assert.Equal(.0002m,plan.GrossQuantity);Assert.Equal(.0001m,plan.Sellable);Assert.Equal(.0000998m,plan.Dust);Assert.Equal(.022m,plan.Debit);
        var refused=Assert.Throws<OkxFiniteSizingRefusal>(()=>OkxOperationalTradingService.MinimumRoundTrip(spec with{MinimumQuantity=1},new(.001m,true,.001m,false),q,false));Assert.True(refused.Plan.Debit>10);
        Assert.Equal(.0001m,OkxOperationalTradingService.MinimumRoundTrip(spec,new(.001m,false,.001m,false),q,false).GrossQuantity);
    }

    [Fact]
    public async Task Known_maker_fee_above_the_taker_reservation_bound_refuses_before_pending_risk()
    {
        await using var f=new OperationalPeer(finite:true);using var peer=new FiniteDemoPeer(f);f.Mutate=(p,b)=>{peer.Observe(p,b);if(p=="/api/v5/account/trade-fee")b["data"]![0]!["feeGroup"]![0]!["maker"]="-0.002";};
        await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        await using var loop=OkxOperationalTradingService.Create(f.Service,f.Clock,(s,k,h,x)=>OkxExecutionClient.ForTests(peer,f.Clock,s,k,h,x),peer);loop.BindFiniteSafety(()=>true);
        var result=await OkxFiniteDemoSequence.RunAsync(loop,f.Service,Guid.NewGuid().ToString("N"),_=>{},default);
        Assert.Equal("PREFLIGHT_RECONCILIATION_REFUSED",result);Assert.Empty(peer.Sends);Assert.Empty(State(f).Naval!.Reservations);
    }
    [Fact]
    public async Task Pending_partial_fill_is_owned_and_disclosed_without_a_second_buy_or_guessed_foreign_liquidation()
    {
        await using var f=new OperationalPeer(finite:true);using var peer=new FiniteDemoPeer(f){FillPending=true};f.Mutate=peer.Observe;await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        await using var loop=OkxOperationalTradingService.Create(f.Service,f.Clock,(s,k,h,x)=>OkxExecutionClient.ForTests(peer,f.Clock,s,k,h,x),peer);loop.BindFiniteSafety(()=>true);
        var result=await OkxFiniteDemoSequence.RunAsync(loop,f.Service,Guid.NewGuid().ToString("N"),_=>{},default);
        Assert.Equal("PENDING_NOT_PROVEN_ZERO_FILL_CANCELED_NO_SECOND_BUY",result);Assert.Equal(2,peer.Sends.Count);Assert.Equal("",peer.BuyClient);Assert.Equal("",peer.SellClient);
        Assert.Single(State(f).Naval!.Fills);Assert.Equal(.0000999m,State(f).Naval!.Reservations.Single().Inventory);Assert.Equal("STOP",loop.ReadStatus().Mode);
    }
    [Fact]
    public async Task Unknown_sale_is_observed_by_original_ID_but_never_replaced_or_reported_as_sequence_success()
    {
        await using var f=new OperationalPeer(finite:true);using var peer=new FiniteDemoPeer(f){UnknownSell=true};f.Mutate=peer.Observe;await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        await using var loop=OkxOperationalTradingService.Create(f.Service,f.Clock,(s,k,h,x)=>OkxExecutionClient.ForTests(peer,f.Clock,s,k,h,x),peer);loop.BindFiniteSafety(()=>true);
        var result=await OkxFiniteDemoSequence.RunAsync(loop,f.Service,Guid.NewGuid().ToString("N"),_=>{},default);
        Assert.Equal("OWNED_EXIT_INCOMPLETE_CHECK_RESIDUAL_PROTECTION",result);Assert.Equal(5,peer.Sends.Count);Assert.Contains(f.Paths,p=>p.Contains("clOrdId="+peer.SellClient,StringComparison.Ordinal));Assert.Equal("STOP",loop.ReadStatus().Mode);
        await loop.ExitAsync(State(f).Naval!.Reservations.Last().Id,default);Assert.Equal(5,peer.Sends.Count);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Final_finite_process_fence_is_checked_after_real_signing_with_an_unmutated_send_control(bool revokeInProvider)
    {
        await using var f=new OperationalPeer(finite:true);using var peer=new FiniteDemoPeer(f);f.Mutate=peer.Observe;await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        bool revoked=false;int opens=0;
        await using var loop=OkxOperationalTradingService.Create(f.Service,f.Clock,(s,k,h,x)=>OkxExecutionClient.ForTests(peer,f.Clock,s,new OpenHook(k,()=>{opens++;revoked=revokeInProvider;}),h,x),peer);
        loop.BindFiniteSafety(()=>!revoked);Assert.True(loop.ArmFiniteDemo());Assert.Equal(NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);
        Assert.Equal(NavalMutationStatus.Applied,loop.PlanFiniteDemo("BTC-USDC",Guid.NewGuid().ToString("N")).Status);Assert.Equal(NavalMutationStatus.Applied,loop.Start().Status);
        var outcome=await loop.SubmitFiniteDemoAsync(true,default);Assert.Equal(1,opens);Assert.Equal(revokeInProvider?0:1,peer.Sends.Count);
        var intent=Assert.Single(State(f).NavalBroker!.Operational!.Intents);Assert.Equal(revokeInProvider?NavalExecutionStage.NotSent:NavalExecutionStage.Accepted,intent.Stage);
        Assert.Equal(revokeInProvider?null:(long?)intent.DispatchRevision,intent.DispatchRevision);
    }
    [Fact]
    public async Task Ordinary_runtime_cannot_use_finite_connect_or_plan_and_LIVE_cannot_compose()
    {
        await using var f=new OperationalPeer();Assert.False(await f.Service.ConnectFiniteDemoAsync(f.Request.CredentialFingerprint,default));Assert.Equal(0,f.SecretOpens);Assert.Empty(f.Paths);
        Assert.Throws<InvalidOperationException>(()=>OkxNavalRuntime.ComposeFiniteDemo(f.Root,f.Binding with{Account=f.Binding.Account with{Environment=NavalEnvironment.Live}},f,()=>true));
    }
    private sealed class OpenHook(IOkxSigningMaterialProvider inner,Action action):IOkxSigningMaterialProvider
    {public async ValueTask<OkxSigningMaterial> OpenAsync(System.Threading.CancellationToken ct){var material=await inner.OpenAsync(ct);action();return material;}}
    [Fact]
    public void Finite_demo_composition_is_separate_from_native_activation_and_has_no_strategy_entry()
    {
        Assert.NotNull(typeof(OkxNavalRuntime).GetMethod("ComposeFiniteDemo",BindingFlags.Static|BindingFlags.NonPublic));
        Assert.NotNull(typeof(OkxAccountConnectionService).GetMethod("ConnectFiniteDemoAsync",BindingFlags.Instance|BindingFlags.NonPublic));
        Assert.NotNull(typeof(OkxOperationalTradingService).GetMethod("SubmitFiniteDemoAsync",BindingFlags.Instance|BindingFlags.NonPublic));
    }
}
