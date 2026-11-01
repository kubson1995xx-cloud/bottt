using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using System.Threading.Tasks;
using Apex.Core.Engine;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.Tests;
public sealed class OkxOperationalTradingFixTests
{
    private static EngineState State(OperationalPeer f)=>JsonSerializer.Deserialize<EngineState>(File.ReadAllText(f.Target))!;
    private static OkxOperationalTradingService Loop(OperationalPeer f,System.Net.Http.HttpMessageHandler p)=>OkxOperationalTradingService.ForTests(f.Service,(s,k,h,x)=>Apex.Adapters.Okx.Execution.OkxExecutionClient.ForTests(p,f.Clock,s,k,h,x),p,f.Clock);
    private static async Task Start(OperationalPeer f,OkxOperationalTradingService loop)
    {
        var request=f.Request with{Purpose=OkxActivationPurpose.Execute,Session=f.Service.TradingSessionId,ExpiresUtc=DateTimeOffset.UtcNow.AddSeconds(120)};
        var lease=await f.Runtime.Activation.RequestAsync(request,default);Assert.NotNull(lease);Assert.True(loop.Arm(request,lease!));await Reconciled(f,loop);Assert.Equal(NavalMutationStatus.Applied,loop.Start().Status);
    }
    private static async Task Reconciled(OperationalPeer f,OkxOperationalTradingService loop)
    {var result=await loop.ReconcileAsync(default);Assert.True(result.Status==NavalMutationStatus.Applied,result.Code+" obligations="+string.Join(",",State(f).NavalBroker!.Operational!.Obligations));}
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stream_child_uses_exact_resolved_ancestry_but_foreign_order_still_stops(bool foreign)
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f);await using var loop=Loop(f,peer);await Start(f,loop);
        Assert.Equal(NavalMutationStatus.Applied,(await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default)).Status);
        TradingEconomicsFixture.Install(f,peer,peer.Size,peer.Size*.11m,true,false);await Reconciled(f,loop);TradingEconomicsFixture.InstallTriggeredExit(f,peer);
        var dir=new DirectoryInfo(AppContext.BaseDirectory);while(dir!=null&&!Directory.Exists(Path.Combine(dir.FullName,"tests/Apex.Adapters.Okx.Tests/Fixtures")))dir=dir.Parent;
        var oracle=JsonNode.Parse(File.ReadAllText(Path.Combine(dir!.FullName,"tests/Apex.Adapters.Okx.Tests/Fixtures/ExecutionObservations/stream-algo-schema.json")))!;
        var frame=oracle["endpoints"]!["orders"]!["control"]!.DeepClone();frame["arg"]!["uid"]=f.Binding.Account.AccountId;frame["arg"]!["instId"]="BTC-USDC";
        var row=frame["data"]![0]!;row["instId"]="BTC-USDC";row["ordId"]=foreign?"779":"778";row["clOrdId"]="";row["algoId"]=peer.GeneratedAlgoId;row["tradeQuoteCcy"]="USDC";
        string N(decimal n)=>n.ToString("0.############################",System.Globalization.CultureInfo.InvariantCulture);
        foreach(string key in new[]{"sz","accFillSz","fillSz"})row[key]=N(peer.Size);foreach(string key in new[]{"px","avgPx","fillPx"})row[key]="120";foreach(string key in new[]{"fee","fillFee"})row[key]=N(-peer.Size*.12m);
        row["feeCcy"]="USDC";row["fillFeeCcy"]="USDC";row["tradeId"]="902";row["ordType"]="fok";
        string time=State(f).Naval!.Fills[0].Fill.TradeUtc.ToUnixTimeMilliseconds().ToString();foreach(string key in new[]{"cTime","uTime","fillTime"})row[key]=time;
        var field=typeof(OperationalSocket).GetField("replies",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        ((Channel<byte[]>)field.GetValue(f.Sockets[0])!).Writer.TryWrite(Encoding.UTF8.GetBytes(frame.ToJsonString()));
        for(int n=0;n<100&&f.Service.ActivityVersion<1;n++)await Task.Delay(10);Assert.True(f.Service.ActivityVersion>=1);
        for(int n=0;n<2;n++)
        {
            var result=await loop.ReconcileAsync(default);var state=State(f);Assert.Equal(2,state.Naval!.Fills.Count);Assert.Equal(1234.56m+peer.Size*9.77m,state.Naval.Accounts.Single().Cash);
            if(foreign){Assert.Equal("STOP",state.Naval.Mode);Assert.Contains(state.NavalBroker!.Operational!.Obligations,o=>o.StartsWith("FOREIGN_STREAM_ORDER:",StringComparison.Ordinal));}
            else {Assert.True(result.Status==NavalMutationStatus.Applied,result.Code);Assert.Equal("RUNNING",state.Naval.Mode);Assert.Empty(state.NavalBroker!.Operational!.Obligations);}
        }
        Assert.Equal(1,peer.Posts);
    }
    [Fact]
    public async Task Expiry_after_real_Dispatching_replace_records_NotSent_without_storage_freeze_or_retry()
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingPeer(f);await using var loop=Loop(f,peer);await Start(f,loop);
        NavalBrokerIntent? dispatched=null;decimal held=0;
        f.WriteBoundary=b=>{if(dispatched==null&&b.ToString()=="AfterReplace"){var state=State(f);var i=state.NavalBroker!.Operational?.Intents.SingleOrDefault();if(i?.Stage==NavalExecutionStage.Dispatching){dispatched=i;held=f.Service.Coordinator!.Snapshot().Held;f.Clock.Advance(TimeSpan.FromSeconds(12),true,true);}}};
        var result=await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);Assert.NotNull(dispatched);Assert.Equal(0,peer.Posts);Assert.True(result.Status==NavalMutationStatus.Applied,result.Code);
        var intent=Assert.Single(State(f).NavalBroker!.Operational!.Intents);Assert.Equal(NavalExecutionStage.NotSent,intent.Stage);Assert.Equal(NavalDispatchDisposition.NotSent,intent.Outcome!.Disposition);Assert.Equal(dispatched!.DispatchRevision,intent.DispatchRevision);Assert.Equal(dispatched.PreparedRevision,intent.PreparedRevision);Assert.Equal(dispatched.RequestDigest,intent.RequestDigest);Assert.Equal(dispatched.ClientId,intent.ClientId);Assert.Equal(held,f.Service.Coordinator!.Snapshot().Held);Assert.True(held>0);
        await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);Assert.Equal(0,peer.Posts);f.WriteBoundary=null;await f.Service.DisconnectAsync();Assert.True(f.Service.OpenExisting());Assert.Equal("STOP",f.Service.Coordinator!.Snapshot().Mode);Assert.Equal(held,f.Service.Coordinator.Snapshot().Held);Assert.Equal(NavalExecutionStage.NotSent,Assert.Single(State(f).NavalBroker!.Operational!.Intents).Stage);
    }
    [Fact]
    public async Task Held_ETH_exit_reaches_signed_sell_despite_BTC_only_history_failure()
    {
        await using var f=new OperationalPeer();f.Mutate=TradingFixEconomics.AddEth;await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingFixPeer(f){Instrument="ETH-USDC"};await using var loop=Loop(f,peer);await Start(f,loop);
        Assert.Equal(NavalMutationStatus.Applied,(await loop.RunOnceAsync(NavalFleet.Alpha,"ETH-USDC",default)).Status);Assert.Equal(1,peer.BtcHistoryCalls);
        var economics=new TradingFixEconomics(f,peer);economics.Add();f.Mutate=economics.Mutate;await Reconciled(f,loop);
        peer.ExitSignal=true;peer.FailBtc=true;int own=peer.OwnHistoryCalls,btc=peer.BtcHistoryCalls;var result=await loop.RunOnceAsync(NavalFleet.Alpha,"ETH-USDC",default);
        Assert.True(result.Status==NavalMutationStatus.Applied,result.Code);Assert.Equal(btc,peer.BtcHistoryCalls);Assert.Equal(own+1,peer.OwnHistoryCalls);Assert.Equal(3,peer.Posts);Assert.True(peer.ProtectionCanceled);Assert.NotEmpty(peer.ExitClient);
    }
    [Fact]
    public async Task Completed_old_triggered_exit_does_not_block_new_reservation_exact_cancel_and_sell()
    {
        await using var f=new OperationalPeer();await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));using var peer=new TradingFixPeer(f);await using var loop=Loop(f,peer);await Start(f,loop);
        Assert.Equal(NavalMutationStatus.Applied,(await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default)).Status);var economics=new TradingFixEconomics(f,peer);var old=economics.Add();f.Mutate=economics.Mutate;await Reconciled(f,loop);
        old.Triggered=true;await Reconciled(f,loop);var ended=State(f).Naval!.Reservations.Single();Assert.Equal(NavalReservationStage.Terminal,ended.Stage);Assert.Equal(0,ended.Inventory);Assert.False(ended.ReductionUnsettled);
        f.Clock.Advance(TimeSpan.FromDays(1),true,true);await Reconciled(f,loop);Assert.Equal(NavalMutationStatus.Applied,loop.Start().Status);
        peer.OrderId="1777";peer.AttachmentId="1888";peer.AlgoId="1889";peer.ExitOrderId="1778";
        var entered=await loop.RunOnceAsync(NavalFleet.Alpha,"BTC-USDC",default);Assert.True(entered.Status==NavalMutationStatus.Applied,entered.Code);var later=economics.Add();Assert.NotEqual(old.Client,later.Client);await Reconciled(f,loop);
        Assert.Equal(2,State(f).Naval!.Reservations.Count);var result=await loop.ExitAsync(State(f).Naval!.Reservations.Last().Id,default);
        Assert.True(peer.ProtectionCanceled);Assert.True(result.Status==NavalMutationStatus.Applied,result.Code);Assert.Equal(4,peer.Posts);Assert.NotEmpty(peer.ExitClient);Assert.Single(peer.Mutations,m=>m.StartsWith("/api/v5/trade/cancel-algos:",StringComparison.Ordinal));Assert.Contains("1889",peer.Mutations[2]);
        var state=State(f);Assert.Contains(state.NavalBroker!.Operational!.Protection,p=>p.AttachmentId=="888"&&p.AlgoId=="889"&&p.State=="effective");Assert.Contains(state.NavalBroker.Operational.Protection,p=>p.AttachmentId=="1888"&&p.AlgoId=="1889"&&p.State=="canceled");Assert.Equal(NavalReservationStage.Terminal,state.Naval!.Reservations.First().Stage);
    }
}
