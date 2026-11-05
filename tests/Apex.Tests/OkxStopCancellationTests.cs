using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
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
    [Theory]
    [InlineData("/api/v5/trade/fills-history")]
    [InlineData("/api/v5/account/balance")]
    [InlineData("quote")]
    public async Task Orderly_cancel_of_known_economics_preserves_complete_state_in_STOP(string boundary)
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        using var peer=new TradingPeer(f);using var cancel=new CancellationTokenSource();using var market=new StopMarket(peer);
        await using var loop=OkxOperationalTradingService.Create(f.Service,f.Clock,(s,k,h,x)=>OkxExecutionClient.ForTests(peer,f.Clock,s,k,h,x),market);
        await Start(f,loop);await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);TradingEconomicsFixture.Install(f,peer,peer.Size,peer.Size*.11m,true);
        Assert.Equal(NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);
        var before=State(f);var basis=f.Mutate!;bool reached=false;
        void Cancel(){reached=true;loop.Stop();cancel.Cancel();}
        f.Mutate=(p,b)=>{basis(p,b);if(p==boundary)Cancel();};if(boundary=="quote")market.OnBook=Cancel;
        await loop.ReconcileAsync(cancel.Token);Assert.True(reached);var after=State(f);
        Assert.Equal(NavalJson.Serialize(before.NavalBroker!.Operational!.Obligations),NavalJson.Serialize(after.NavalBroker!.Operational!.Obligations));
        Assert.Equal(NavalJson.Serialize(before.NavalBroker.Operational.LastBalances),NavalJson.Serialize(after.NavalBroker.Operational.LastBalances));
        Assert.Equal(before.NavalBroker.Operational.LastBalanceCaptureUtc,after.NavalBroker.Operational.LastBalanceCaptureUtc);
        Assert.Equal(NavalJson.Serialize(before.NavalBroker.Operational.FillEvidence),NavalJson.Serialize(after.NavalBroker.Operational.FillEvidence));
        Assert.Equal(NavalJson.Serialize(before.Naval!.Fills),NavalJson.Serialize(after.Naval!.Fills));Assert.Equal("STOP",after.Naval.Mode);Assert.False(loop.ReadStatus().Ready);Assert.Null(PendingContinuation(f));
        Assert.Equal(before.NavalBroker.HighWater,after.NavalBroker.HighWater);Assert.Equal(1,peer.Posts);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Cancel_does_not_discard_new_fill_or_release_unknown_entry(bool unknown)
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f){Unknown=unknown};await using var loop=Production(f,peer);
        await Start(f,loop);await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);var before=State(f);TradingEconomicsFixture.Install(f,peer,peer.Size,peer.Size*.11m,true);var basis=f.Mutate!;using var cancel=new CancellationTokenSource();
        f.Mutate=(p,b)=>{basis(p,b);if(p=="/api/v5/account/balance"){loop.Stop();cancel.Cancel();}};
        await loop.ReconcileAsync(cancel.Token);var after=State(f);var r=Assert.Single(after.Naval!.Reservations);
        Assert.Single(after.Naval.Fills);Assert.Equal(peer.Size,r.Inventory);Assert.True(loop.ReadStatus().Held>0);Assert.Equal("STOP",after.Naval.Mode);Assert.False(loop.ReadStatus().Ready);
        Assert.Contains("BROKER_COVERAGE_INCOMPLETE",after.NavalBroker!.Operational!.Obligations);Assert.Equal(NavalJson.Serialize(before.NavalBroker!.Operational!.Intents),NavalJson.Serialize(after.NavalBroker.Operational.Intents));
        Assert.Equal(1,peer.Posts);Assert.Null(PendingContinuation(f));
    }
    [Theory]
    [InlineData("failed-read",false)] [InlineData("failed-read",true)] [InlineData("conflict",false)] [InlineData("conflict",true)]
    public async Task Genuine_failed_read_or_conflicting_evidence_stays_strict_even_if_later_canceled(string fault,bool canceled)
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f);await using var loop=Production(f,peer);
        await Start(f,loop);await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);TradingEconomicsFixture.Install(f,peer,peer.Size,peer.Size*.11m,true);await loop.ReconcileAsync(default);var basis=f.Mutate!;using var cancel=new CancellationTokenSource();
        f.Mutate=(p,b)=>{basis(p,b);if(fault=="failed-read"&&p=="/api/v5/trade/fills-history")b["code"]="50011";if(fault=="conflict"&&p=="/api/v5/trade/orders-history")foreach(var row in b["data"]!.AsArray())row!["px"]="109";if(canceled&&p=="/api/v5/account/balance"){loop.Stop();cancel.Cancel();}};
        await loop.ReconcileAsync(cancel.Token);var after=State(f);Assert.Equal("STOP",after.Naval!.Mode);Assert.False(loop.ReadStatus().Ready);
        Assert.Contains(after.NavalBroker!.Operational!.Obligations,o=>o.StartsWith(fault=="failed-read"?"HISTORY_INCOMPLETE:FillsArchive":"ORDER_CHANGED_DURING_PASS:",StringComparison.Ordinal));Assert.Equal(1,peer.Posts);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task STOP_during_submitted_response_then_cancel_keeps_original_reservation_and_new_fill(bool observeFill)
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        using var peer=new TradingPeer(f){Unknown=true,HoldResponse=new(TaskCreationOptions.RunContinuationsAsynchronously)};await using var loop=Production(f,peer);await Start(f,loop);
        var send=loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);await peer.Invoked.Task.WaitAsync(TimeSpan.FromSeconds(10));loop.Stop();peer.HoldResponse.SetResult();await send;
        var original=State(f).NavalBroker!.Operational!.Intents.Single();Assert.Equal(NavalExecutionStage.SubmitUnknown,original.Stage);
        if(observeFill)TradingEconomicsFixture.Install(f,peer,peer.Size,peer.Size*.11m,true);var basis=f.Mutate;using var cancel=new CancellationTokenSource();
        f.Mutate=(p,b)=>{basis?.Invoke(p,b);if(p==(observeFill?"/api/v5/account/balance":"/api/v5/trade/orders-pending"))cancel.Cancel();};
        await loop.ReconcileAsync(cancel.Token);var after=State(f);Assert.Equal(NavalJson.Serialize(original),NavalJson.Serialize(after.NavalBroker!.Operational!.Intents.Single()));
        var r=Assert.Single(after.Naval!.Reservations);Assert.True(loop.ReadStatus().Held>0);Assert.Equal(observeFill?1:0,after.Naval.Fills.Count);Assert.Equal(observeFill?peer.Size:0,r.Inventory);Assert.Equal("STOP",after.Naval.Mode);Assert.False(loop.ReadStatus().Ready);Assert.Equal(1,peer.Posts);
    }
    [Fact]
    public async Task Process_evaluation_status_counts_real_wait_and_distinguishes_skip_and_failure_without_persistence()
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f);using var market=new StopMarket(peer){Wait=true};
        await using var loop=OkxOperationalTradingService.Create(f.Service,f.Clock,(s,k,h,x)=>OkxExecutionClient.ForTests(peer,f.Clock,s,k,h,x),market);
        await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);var skipped=Evaluation(loop);Assert.Equal(0L,Number(skipped,"Completed"));Assert.Equal("SKIPPED_CAN_EXECUTE",Value(skipped,"Code"));
        await Start(f,loop);byte[] before=File.ReadAllBytes(f.Target);await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);
        var wait=Evaluation(loop);Assert.Equal(1L,Number(wait,"Completed"));Assert.Equal(1L,Number(wait,"Wait"));Assert.Equal(0L,Number(wait,"Entry"));Assert.Equal(0L,Number(wait,"Exit"));Assert.Equal("EVALUATED_WAIT",Value(wait,"Code"));
        Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(f.Target)));Assert.Empty(State(f).NavalBroker!.Operational!.Signals);Assert.Equal(0,peer.Posts);
        market.Fail=true;await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);var failed=Evaluation(loop);Assert.Equal(1L,Number(failed,"Completed"));Assert.Equal("EVALUATION_FAILED",Value(failed,"Code"));
    }
    [Fact]
    public async Task Process_evaluation_counts_real_entry_exit_and_fresh_instance_resets()
    {
        await using var f=ContinuationAccount();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f);await using var loop=Production(f,peer);
        await ContinuationHolding(f,peer,loop);var entry=Evaluation(loop);Assert.Equal(1L,Number(entry,"Completed"));Assert.Equal(1L,Number(entry,"Entry"));Assert.Equal("EVALUATED_ENTRY",Value(entry,"Code"));
        await StrategyExit(f,peer,loop);var exit=Evaluation(loop);Assert.Equal(2L,Number(exit,"Completed"));Assert.Equal(1L,Number(exit,"Entry"));Assert.Equal(1L,Number(exit,"Exit"));Assert.Equal(0L,Number(exit,"Wait"));Assert.Equal("EVALUATED_EXIT",Value(exit,"Code"));
        await loop.DisposeAsync();await using var fresh=Production(f,peer);Assert.Equal(0L,Number(Evaluation(fresh),"Completed"));Assert.Equal("NOT_EVALUATED",Value(Evaluation(fresh),"Code"));
    }
    private static object Evaluation(OkxOperationalTradingService loop)
    {var p=loop.ReadStatus().GetType().GetProperty("Evaluation");Assert.True(p!=null,"STATUS lacks process-only reached-evaluator evidence");return p!.GetValue(loop.ReadStatus())!;}
    private static object Value(object o,string name)=>o.GetType().GetProperty(name)!.GetValue(o)!;
    private static long Number(object o,string name)=>(long)Value(o,name);
    private sealed class StopMarket(TradingPeer peer):HttpMessageHandler
    {
        internal Action? OnBook;internal bool Wait,Fail;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
        {
            if(r.RequestUri!.AbsolutePath=="/api/v5/market/books")OnBook?.Invoke();
            ct.ThrowIfCancellationRequested();if(Fail)throw new HttpRequestException("SENSITIVE_NOT_A_STATUS_CODE");
            var response=await new HttpMessageInvoker(peer,false).SendAsync(r,ct);
            if(Wait&&r.RequestUri.AbsolutePath=="/api/v5/market/candles")
            {var b=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;foreach(var row in b["data"]!.AsArray()){row![2]="101";row[3]="99";row[4]="100";}response.Content=new StringContent(b.ToJsonString());}
            return response;
        }
    }
}
