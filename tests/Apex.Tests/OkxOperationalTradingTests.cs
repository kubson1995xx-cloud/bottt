using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Xunit;

namespace Apex.Tests;

public sealed class OkxOperationalTradingTests
{
    [Fact]
    public async Task Protected_session_runs_existing_risk_to_signed_HTTP_with_durable_dispatch()
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        using var peer=new TradingPeer(f);
        await using var loop=OkxOperationalTradingService.ForTests(f.Service,(s,p,h,x)=>Apex.Adapters.Okx.Execution.OkxExecutionClient.ForTests(peer,f.Clock,s,p,h,x),peer,f.Clock);
        var request=f.Request with{Purpose=OkxActivationPurpose.Execute,Session=f.Service.TradingSessionId,ExpiresUtc=DateTimeOffset.UtcNow.AddSeconds(120)};
        var lease=await f.Runtime.Activation.RequestAsync(request,default);Assert.NotNull(lease);Assert.True(loop.Arm(request,lease!));
        Assert.Equal(NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);
        Assert.Equal(NavalMutationStatus.Applied,loop.Start().Status);
        var sent=await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);
        Assert.Equal(NavalMutationStatus.Applied,sent.Status);
        Assert.Equal(1,peer.Posts);Assert.True(peer.SawDispatching);Assert.True(peer.Size>0);
        Assert.Equal(1234.56m,f.Service.Coordinator!.Snapshot().Cash); // ACK never posts economics.
        Assert.True(f.Service.Coordinator.Snapshot().Held>0);
        await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);
        Assert.Equal(1,peer.Posts);
        loop.Stop();Assert.Equal("STOP",f.Service.Coordinator.Snapshot().Mode);
        await f.Service.DisconnectAsync();Assert.True(f.Service.OpenExisting());
        Assert.True(f.Service.Coordinator!.Snapshot().Held>0);Assert.Equal("STOP",f.Service.Coordinator.Snapshot().Mode);
    }

    [Fact]
    public async Task Individual_partial_fill_posts_once_even_while_protection_and_finality_block()
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        using var peer=new TradingPeer(f);
        await using var loop=OkxOperationalTradingService.ForTests(f.Service,(s,p,h,x)=>Apex.Adapters.Okx.Execution.OkxExecutionClient.ForTests(peer,f.Clock,s,p,h,x),peer,f.Clock);
        var request=f.Request with{Purpose=OkxActivationPurpose.Execute,Session=f.Service.TradingSessionId,ExpiresUtc=DateTimeOffset.UtcNow.AddSeconds(120)};
        var lease=await f.Runtime.Activation.RequestAsync(request,default);Assert.True(loop.Arm(request,lease!));
        Assert.Equal(NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);Assert.Equal(NavalMutationStatus.Applied,loop.Start().Status);
        Assert.Equal(NavalMutationStatus.Applied,(await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default)).Status);
        TradingEconomicsFixture.Install(f,peer,.1m,.011m);
        await loop.ReconcileAsync(default);
        var view=f.Service.Coordinator!.Snapshot();
        Assert.Equal(1223.549m,view.Cash);Assert.Equal(.1m,view.Reservations.Single().Inventory);Assert.Equal(11.011m,view.Reservations.Single().AllocatedCost);
        Assert.Equal("STOP",view.Mode);Assert.False(view.Ready);
        await loop.ReconcileAsync(default);Assert.Equal(1223.549m,f.Service.Coordinator.Snapshot().Cash);
        await f.Service.DisconnectAsync();Assert.True(f.Service.OpenExisting());Assert.Equal(1223.549m,f.Service.Coordinator!.Snapshot().Cash);
        var state=System.Text.Json.JsonSerializer.Deserialize<Apex.Core.Engine.EngineState>(File.ReadAllText(f.Target))!;
        Assert.Single(state.Naval!.Fills);Assert.True(state.NavalBroker!.Operational!.Obligations.Count>0);Assert.Equal(1,peer.Posts);
    }

    [Fact]
    public async Task Full_fill_bill_and_observed_exact_protection_reconcile_without_permanent_STOP()
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        using var peer=new TradingPeer(f);await using var loop=Loop(f,peer);
        await Arm(f,loop);Assert.Equal(NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);Assert.Equal(NavalMutationStatus.Applied,loop.Start().Status);
        Assert.Equal(NavalMutationStatus.Applied,(await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default)).Status);
        TradingEconomicsFixture.Install(f,peer,peer.Size,peer.Size*.11m,true);
        var result=await loop.ReconcileAsync(default);Assert.True(result.Status==NavalMutationStatus.Applied,result.Code);
        var view=f.Service.Coordinator!.Snapshot();Assert.Equal("RUNNING",view.Mode);Assert.True(view.Ready);Assert.True(view.Reservations.Single().TerminalCoverage);
        Assert.Equal(1234.56m-peer.Size*110.11m,view.Cash);Assert.Equal(peer.Size,view.Reservations.Single().Inventory);
        Assert.Equal(NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);Assert.Equal(view.Cash,f.Service.Coordinator.Snapshot().Cash);Assert.Equal(1,peer.Posts);
    }
    [Fact]
    public async Task Normal_large_catalog_selects_supported_quote_assets_instead_of_rejecting_account()
    {
        await using var f=new OperationalPeer();f.Mutate=(path,body)=>
        {
            if(path!="/api/v5/account/instruments")return;
            var original=body["data"]![0]!.DeepClone();var rows=body["data"]!.AsArray();
            for(int n=0;n<70;n++){var item=original.DeepClone();item["instId"]="ZZ"+n+"-USD";item["baseCcy"]="ZZ"+n;item["quoteCcy"]="USD";rows.Add(item);}
        };
        var lease=await f.Runtime.Activation.RequestAsync(f.Request,default);Assert.True(await f.Service.ConnectAsync(f.Request,lease!,default),f.Service.Status);
        Assert.True(await f.Service.ApproveAndCreateAsync(default));var state=System.Text.Json.JsonSerializer.Deserialize<Apex.Core.Engine.EngineState>(File.ReadAllText(f.Target))!;
        Assert.Single(state.NavalBroker!.Baseline.Observations.Catalog);Assert.Equal("BTC-USDC",state.NavalBroker.Baseline.Observations.Catalog[0].Instrument);
    }
    [Fact]
    public async Task Unknown_ack_retains_original_ID_hold_and_no_resubmit_after_STOP_or_reopen()
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f){Unknown=true};await using var loop=Loop(f,peer);
        await Arm(f,loop);Assert.Equal(NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);Assert.Equal(NavalMutationStatus.Applied,loop.Start().Status);
        Assert.Equal(NavalMutationStatus.Stopped,(await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default)).Status);
        string client=peer.Client;Assert.True(f.Service.Coordinator!.Snapshot().Held>0);Assert.Equal(NavalReservationStage.Uncertain,f.Service.Coordinator.Snapshot().Reservations.Single().Stage);
        await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);Assert.Equal(1,peer.Posts);
        await f.Service.DisconnectAsync();Assert.True(f.Service.OpenExisting());Assert.Equal("STOP",f.Service.Coordinator!.Snapshot().Mode);Assert.Contains(client,File.ReadAllText(f.Target));Assert.True(f.Service.Coordinator.Snapshot().Held>0);
    }
    [Theory]
    [InlineData("body")]
    [InlineData("counter")]
    public async Task Reopen_rejects_resealed_execution_identity_corruption(string change)
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f);await using var loop=Loop(f,peer);
        await Arm(f,loop);Assert.Equal(NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);Assert.Equal(NavalMutationStatus.Applied,loop.Start().Status);Assert.Equal(NavalMutationStatus.Applied,(await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default)).Status);
        await f.Service.DisconnectAsync();var state=System.Text.Json.JsonSerializer.Deserialize<Apex.Core.Engine.EngineState>(File.ReadAllText(f.Target))!;var b=state.NavalBroker!;
        if(change=="counter")b=b with{HighWater=b.HighWater+1};
        else {var x=b.Operational!;var original=x.Intents.Single();b=b with{Operational=x with{Intents=new[]{original with{Body=System.Text.Encoding.UTF8.GetBytes("{}")}}}};}
        state.NavalBroker=NavalBrokerValidation.Seal(b);string bytes=NavalJson.Serialize(state);File.WriteAllText(f.Target,bytes);
        Assert.False(f.Service.OpenExisting());Assert.Equal(bytes,File.ReadAllText(f.Target));
    }
    [Fact]
    public async Task Ordinary_order_stream_activity_is_buffered_not_a_fabricated_connection_gap()
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        var dir=new DirectoryInfo(AppContext.BaseDirectory);while(dir!=null&&!Directory.Exists(Path.Combine(dir.FullName,"tests/Apex.Adapters.Okx.Tests/Fixtures")))dir=dir.Parent;
        var oracle=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(dir!.FullName,"tests/Apex.Adapters.Okx.Tests/Fixtures/ExecutionObservations/stream-algo-schema.json")))!;
        var frame=oracle["endpoints"]!["orders"]!["control"]!.DeepClone();frame["arg"]!["uid"]=f.Binding.Account.AccountId;
        var field=typeof(OperationalSocket).GetField("replies",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        var queue=(System.Threading.Channels.Channel<byte[]>)field.GetValue(f.Sockets[0])!;queue.Writer.TryWrite(System.Text.Encoding.UTF8.GetBytes(frame.ToJsonString()));
        await Task.Delay(100);
        Assert.True(f.Service.IsConnected,f.Service.Status);
    }
    [Fact]
    public async Task STOP_after_signing_provider_admission_prevents_actual_invocation_and_retains_hold()
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f);
        OkxOperationalTradingService? loop=null;bool reached=false;
        await using var owned=loop=OkxOperationalTradingService.ForTests(f.Service,(s,p,h,x)=>Apex.Adapters.Okx.Execution.OkxExecutionClient.ForTests(peer,f.Clock,s,new StopProvider(p,()=>{reached=true;Assert.Contains("Prepared",File.ReadAllText(f.Target));loop!.Stop();}),h,x),peer,f.Clock);
        await Arm(f,loop);Assert.Equal(NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);Assert.Equal(NavalMutationStatus.Applied,loop.Start().Status);
        await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);Assert.True(reached);Assert.Equal(0,peer.Posts);Assert.Equal("STOP",f.Service.Coordinator!.Snapshot().Mode);Assert.True(f.Service.Coordinator.Snapshot().Held>0);
    }
    private sealed class StopProvider(Apex.Adapters.Okx.Execution.IOkxSigningMaterialProvider inner,Action stop):Apex.Adapters.Okx.Execution.IOkxSigningMaterialProvider
    {
        public async System.Threading.Tasks.ValueTask<Apex.Adapters.Okx.Execution.OkxSigningMaterial> OpenAsync(System.Threading.CancellationToken ct){var material=await inner.OpenAsync(ct);stop();return material;}
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_fee_stays_unapplied_then_known_quote_or_base_fee_posts_once(bool inBase)
    {
        await using var f=new OperationalPeer();if(inBase)f.Mutate=(path,body)=>{if(path=="/api/v5/account/config")body["data"]![0]!["feeType"]="0";};
        await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f);await using var loop=Loop(f,peer);
        await Arm(f,loop);Assert.Equal(NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);Assert.Equal(NavalMutationStatus.Applied,loop.Start().Status);Assert.Equal(NavalMutationStatus.Applied,(await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default)).Status);
        TradingEconomicsFixture.Install(f,peer,.1m,inBase ? .0001m:.011m,false,inBase);var fixture=f.Mutate!;
        f.Mutate=(path,body)=>{fixture(path,body);if(path=="/api/v5/account/config"&&inBase)body["data"]![0]!["feeType"]="0";if(path.StartsWith("/api/v5/trade/fills",StringComparison.Ordinal))foreach(var row in body["data"]!.AsArray())row!["fee"]="";};
        await loop.ReconcileAsync(default);Assert.Equal(1234.56m,f.Service.Coordinator!.Snapshot().Cash);Assert.True(f.Service.Coordinator.Snapshot().Held>0);
        f.Mutate=(path,body)=>{fixture(path,body);if(path=="/api/v5/account/config"&&inBase)body["data"]![0]!["feeType"]="0";};
        await loop.ReconcileAsync(default);Assert.Equal(inBase?1223.56m:1223.549m,f.Service.Coordinator.Snapshot().Cash);Assert.Equal(inBase ? .0999m:.1m,f.Service.Coordinator.Snapshot().Reservations.Single().Inventory);
        await loop.ReconcileAsync(default);Assert.Equal(inBase?1223.56m:1223.549m,f.Service.Coordinator.Snapshot().Cash);
    }
    [Fact]
    public async Task Production_profile_is_not_approved_by_successful_account_reads_or_execute_arming()
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f);await using var loop=Loop(f,peer,false);
        await Arm(f,loop);Assert.Equal(NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);
        Assert.Equal("PRODUCTION_PROTECTION_PROFILE_UNAPPROVED",loop.Start().Code);await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);Assert.Equal(0,peer.Posts);
    }
    [Fact]
    public async Task Observed_attached_algo_child_exit_posts_sell_and_fee_once_then_releases_settled_inventory()
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f);await using var loop=Loop(f,peer);
        await Arm(f,loop);Assert.Equal(NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);Assert.Equal(NavalMutationStatus.Applied,loop.Start().Status);Assert.Equal(NavalMutationStatus.Applied,(await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default)).Status);
        TradingEconomicsFixture.Install(f,peer,peer.Size,peer.Size*.11m,true);Assert.Equal(NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);
        TradingEconomicsFixture.InstallTriggeredExit(f,peer);var result=await loop.ReconcileAsync(default);
        Assert.Equal(1234.56m+peer.Size*9.77m,f.Service.Coordinator!.Snapshot().Cash);Assert.True(result.Status==NavalMutationStatus.Applied,result.Code);
        var r=f.Service.Coordinator.Snapshot().Reservations.Single();Assert.Equal(0,r.Inventory);Assert.Equal(0,r.AllocatedCost);Assert.False(r.ReductionUnsettled);Assert.Equal(0,r.CloseFeeHold);
        await loop.ReconcileAsync(default);Assert.Equal(1234.56m+peer.Size*9.77m,f.Service.Coordinator.Snapshot().Cash);Assert.Equal(1,peer.Posts);
    }
    [Fact]
    public async Task Exact_owned_exit_observes_protection_cancel_before_one_durable_bounded_sell()
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f);await using var loop=Loop(f,peer);
        await Arm(f,loop);Assert.Equal(NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);Assert.Equal(NavalMutationStatus.Applied,loop.Start().Status);Assert.Equal(NavalMutationStatus.Applied,(await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default)).Status);
        TradingEconomicsFixture.Install(f,peer,peer.Size,peer.Size*.11m,true);var fixture=f.Mutate!;
        f.Mutate=(path,body)=>{fixture(path,body);if(path=="/api/v5/trade/order-algo"&&peer.ProtectionCanceled)body["data"]![0]!["state"]="canceled";};
        Assert.Equal(NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);var before=f.Service.Coordinator!.Snapshot();
        Assert.Equal(NavalMutationStatus.Refused,(await loop.ExitAsync("foreign",default)).Status);Assert.Equal(1,peer.Posts);
        peer.ExitSignal=true;var result=await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);Assert.True(result.Status==NavalMutationStatus.Applied,result.Code+" posts="+peer.Posts+" saved="+string.Join(",",System.Text.Json.JsonSerializer.Deserialize<Apex.Core.Engine.EngineState>(File.ReadAllText(f.Target))!.NavalBroker!.Operational!.Intents.Select(i=>i.Operation+":"+i.Stage)));Assert.Equal(3,peer.Posts);Assert.True(peer.ProtectionCanceled);Assert.NotEmpty(peer.ExitClient);
        Assert.Equal(before.Cash,f.Service.Coordinator.Snapshot().Cash);Assert.Equal(before.Reservations.Single().Inventory,f.Service.Coordinator.Snapshot().Reservations.Single().Inventory);
        await loop.ExitAsync(before.Reservations.Single().Id,default);Assert.Equal(3,peer.Posts);
    }
    [Fact]
    public async Task Fresh_reconnect_opens_original_nonempty_account_allocation_then_reconciles_without_reseed_or_resend()
    {
        await using var f=new OperationalPeer();using var peer=new TradingPeer(f);
        // This fixture's consumer is System UTC. A separate origin+Stopwatch UTC can
        // run ahead of coarse Windows UTC; future server/book timestamps correctly refuse.
        f.Clock.UseSystemUtc=true;
        // Both generations use real clocks: process-lifetime per-account quotas cannot be
        // reset by switching from the older peer's artificially advanced clock to System.
        OkxAccountConnectionService Service()=>OkxAccountConnectionService.ForTests(f.Runtime,f.Root,f.Binding,f,TimeProvider.System,
            (s,p,h,x)=>Apex.Adapters.Okx.Execution.OkxExecutionObservationClient.ForTests(f,TimeProvider.System,s,p,h,x),
            (s,p,h,x)=>Apex.Adapters.Okx.Execution.OkxObservationStream.ForTests((uri,ct)=>{var socket=new OperationalSocket(uri);f.Sockets.Add(socket);return Task.FromResult<System.Net.WebSockets.WebSocket>(socket);},TimeProvider.System,s,p,h,x));
        await using var initial=Service();var observe=await f.Runtime.Activation.RequestAsync(f.Request,default);
        string caught="";
        EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> trace=(_,ev)=>
        {
            if(!ev.Exception.Message.StartsWith("BASELINE_",StringComparison.Ordinal)&&!ev.Exception.Message.StartsWith("BROKER_OBSERVATION_FAILED:",StringComparison.Ordinal))return;
            try
            {
                var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
                var observation=(NavalBrokerObservationBatch?)typeof(OkxAccountConnectionService).GetField("original",flags)!.GetValue(initial);
                var bootstrap=typeof(OkxAccountConnectionService).GetField("bootstrap",flags)!.GetValue(initial);
                var now=DateTimeOffset.UtcNow;
                caught=System.Text.Json.JsonSerializer.Serialize(new{code=ev.Exception.Message,utc=now,peerUtc=f.Clock.GetUtcNow(),observation?.CaptureUtc,observation?.ServerUtc,
                    balanceUtc=observation?.Balances[0].SourceUtc,lastUtc=bootstrap?.GetType().GetField("lastUtc",flags)?.GetValue(bootstrap),closed=bootstrap?.GetType().GetField("closed",flags)?.GetValue(bootstrap)});
            }
            catch{/* Diagnostic cannot alter the production decision. */}
        };
        AppDomain.CurrentDomain.FirstChanceException+=trace;
        try
        {
        bool connected=await initial.ConnectAsync(f.Request,observe!,default);
        Assert.True(connected,initial.Status+" "+caught+" paths="+string.Join(",",f.Paths));Assert.True(await initial.ApproveAndCreateAsync(default),initial.Status+" "+caught);
        await using var loop=OkxOperationalTradingService.ForTests(initial,(s,p,h,x)=>Apex.Adapters.Okx.Execution.OkxExecutionClient.ForTests(peer,TimeProvider.System,s,p,h,x),peer,TimeProvider.System);
        var execute=f.Request with{Purpose=OkxActivationPurpose.Execute,Session=initial.TradingSessionId,ExpiresUtc=DateTimeOffset.UtcNow.AddSeconds(120)};var armed=await f.Runtime.Activation.RequestAsync(execute,default);Assert.True(loop.Arm(execute,armed!));
        string Predicate(string phase,NavalRiskResult result)
        {
            var quoteMap=(System.Collections.Generic.Dictionary<string,NavalBrokerQuote>)typeof(OkxOperationalTradingService).GetField("quotes",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(loop)!;
            var now=DateTimeOffset.UtcNow;var state=initial.BrokerSession!.ReadState();
            return System.Text.Json.JsonSerializer.Serialize(new{phase,result.Code,result.Status,utc=now,peerUtc=f.Clock.GetUtcNow(),snapshot=initial.Coordinator!.Snapshot().Reason,lastBookSource=peer.LastBookSourceUtc,lastBookFuture=peer.LastBookSourceUtc>now,
                obligations=state.NavalBroker!.Operational?.Obligations,caught,quotes=quoteMap.Values.Select(q=>new{q.SourceUtc,q.ReceivedUtc,sourceAfterReceipt=q.SourceUtc>q.ReceivedUtc,ageMilliseconds=(now-q.SourceUtc).TotalMilliseconds}),reads=f.Paths.Count,posts=peer.Posts});
        }
        var initialReconcile=await loop.ReconcileAsync(default);Assert.True(initialReconcile.Status==NavalMutationStatus.Applied,Predicate("initial-reconcile",initialReconcile));
        var initialStart=loop.Start();Assert.True(initialStart.Status==NavalMutationStatus.Applied,Predicate("initial-start",initialStart));
        var initialEntry=await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);Assert.True(initialEntry.Status==NavalMutationStatus.Applied,Predicate("initial-entry",initialEntry));
        TradingEconomicsFixture.Install(f,peer,peer.Size,peer.Size*.11m,true);Assert.Equal(NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);decimal cash=initial.Coordinator!.Snapshot().Cash;
        await loop.DisposeAsync();await initial.DisconnectAsync();await using var service=Service();
        var request=f.Request with{Session=Guid.NewGuid(),ExpiresUtc=DateTimeOffset.UtcNow.AddSeconds(120)};var lease=await f.Runtime.Activation.RequestAsync(request,default);
        Assert.True(await service.ConnectAsync(request,lease!,default),service.Status);Assert.Equal(cash,service.Coordinator!.Snapshot().Cash);Assert.Equal("STOP",service.Coordinator.Snapshot().Mode);Assert.Equal(1,f.Approvals);Assert.Equal(1,peer.Posts);
        }
        finally{AppDomain.CurrentDomain.FirstChanceException-=trace;}
    }
    [Fact]
    public async Task Unknown_original_can_settle_from_exact_fills_without_retry_then_requires_explicit_rearmed_START()
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f){Unknown=true};await using var loop=Loop(f,peer);
        await Arm(f,loop);Assert.Equal(NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);Assert.Equal(NavalMutationStatus.Applied,loop.Start().Status);Assert.Equal(NavalMutationStatus.Stopped,(await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default)).Status);
        TradingEconomicsFixture.Install(f,peer,peer.Size,peer.Size*.11m,true);Assert.Equal(NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);
        Assert.Equal("STOP",f.Service.Coordinator!.Snapshot().Mode);await Arm(f,loop);var result=loop.Start();Assert.True(result.Status==NavalMutationStatus.Applied,result.Code);
        await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);Assert.Equal(1,peer.Posts);
    }
    [Fact]
    public async Task Tiny_individual_trade_product_cannot_silently_round_to_free_inventory()
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f);await using var loop=Loop(f,peer);
        await Arm(f,loop);Assert.Equal(NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);Assert.Equal(NavalMutationStatus.Applied,loop.Start().Status);Assert.Equal(NavalMutationStatus.Applied,(await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default)).Status);
        TradingEconomicsFixture.Install(f,peer,.0000000000000000000000000001m,0);var fixture=f.Mutate!;
        f.Mutate=(path,body)=>{fixture(path,body);if(path.StartsWith("/api/v5/trade/fills",StringComparison.Ordinal))foreach(var row in body["data"]!.AsArray())row!["fillPx"]="0.1";};
        await loop.ReconcileAsync(default);var state=System.Text.Json.JsonSerializer.Deserialize<Apex.Core.Engine.EngineState>(File.ReadAllText(f.Target))!;
        Assert.Empty(state.Naval!.Fills);Assert.NotEmpty(state.NavalBroker!.Operational!.FillEvidence);Assert.True(state.Naval.Reservations.Single().EntryHold>0);
    }
    [Fact]
    public async Task Account_disconnect_drains_invoked_trading_mutation_before_closing_same_owner()
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f){HoldResponse=new(TaskCreationOptions.RunContinuationsAsynchronously)};await using var loop=Loop(f,peer);
        await Arm(f,loop);Assert.Equal(NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);Assert.Equal(NavalMutationStatus.Applied,loop.Start().Status);
        var sending=loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);await peer.Invoked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disconnect=f.Service.DisconnectAsync().AsTask();await Task.Delay(100);bool drainedBeforeClose=!disconnect.IsCompleted;
        peer.HoldResponse.SetResult();await sending;await disconnect;
        Assert.True(drainedBeforeClose);Assert.Null(f.Service.Coordinator);Assert.True(f.Service.OpenExisting());Assert.Equal(1,peer.Posts);Assert.True(f.Service.Coordinator!.Snapshot().Held>0);
    }
    private static OkxOperationalTradingService Loop(OperationalPeer f,TradingPeer peer,bool protection=true)=>OkxOperationalTradingService.ForTests(f.Service,(s,p,h,x)=>Apex.Adapters.Okx.Execution.OkxExecutionClient.ForTests(peer,f.Clock,s,p,h,x),peer,f.Clock,protection);
    private static async Task Arm(OperationalPeer f,OkxOperationalTradingService loop)
    {
        var request=f.Request with{Purpose=OkxActivationPurpose.Execute,Session=f.Service.TradingSessionId,ExpiresUtc=DateTimeOffset.UtcNow.AddSeconds(120)};
        var lease=await f.Runtime.Activation.RequestAsync(request,default);Assert.NotNull(lease);Assert.True(loop.Arm(request,lease!));
    }
    [Fact]
    public async Task Broker_session_accepts_confirmed_D1_signal_into_same_durable_owner()
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        var cutoff=new DateTimeOffset(f.Clock.GetUtcNow().UtcDateTime.Date,TimeSpan.Zero);
        var bars=Enumerable.Range(0,200).Select(n=>new NavalBar(cutoff.AddDays(n-200),100, n==199?112:101,99,n==199?110:100,1,true)).ToArray();
        var series=new NavalSeries("BTC-USDC","BTC","USDC","synthetic strict protocol","trade",bars);
        var decision=new NavalD1Evaluator().Evaluate(new(NavalFleet.Alpha,series,series,cutoff,false));
        Assert.Equal(NavalAction.EntrySignal,decision.Action);
        var result=f.Service.RegisterConfirmedSignal(new(NavalFleet.Alpha,series,series,cutoff,false));
        Assert.Equal(NavalMutationStatus.Applied,result.Status);
        var saved=File.ReadAllText(f.Target);
        Assert.Contains(decision.StableId,saved);
        Assert.Contains("naval-broker-operational-v1",saved);
        Assert.Equal(NavalMutationStatus.Duplicate,f.Service.RegisterConfirmedSignal(new(NavalFleet.Alpha,series,series,cutoff,false)).Status);
        await f.Service.DisconnectAsync();Assert.True(f.Service.OpenExisting());
        Assert.Equal("STOP",f.Service.Coordinator!.Snapshot().Mode);
        Assert.Contains(decision.StableId,File.ReadAllText(f.Target));
    }
}
