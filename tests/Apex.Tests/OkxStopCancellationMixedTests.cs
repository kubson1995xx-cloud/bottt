using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Apex.Okx.DemoAcceptance;
using Xunit;
namespace Apex.Tests;

public sealed partial class OkxConditionalTradingTests
{
    [Fact]
    public async Task Mixed_EUR_SOL_nullACK_pinned_history_cancel_does_not_fabricate_obligations()
    {
        await using var fixture=await StopMixed.Create();var f=fixture.F;var loop=fixture.Loop;
        foreach(string boundary in new[]{"/api/v5/trade/fills-history","/api/v5/account/balance","quote"})
        {
        var before=fixture.Account.BrokerSession!.ReadState();
        using var cancel=new CancellationTokenSource();bool reached=false;var basis=f.Mutate!;
        void Cancel(){reached=true;loop.Stop();cancel.Cancel();}
        f.Mutate=(p,b)=>{basis(p,b);if(p==boundary)Cancel();};if(boundary=="quote")fixture.Market.OnBook=Cancel;
        await loop.ReconcileAsync(cancel.Token);Assert.True(reached);var after=fixture.Account.BrokerSession.ReadState();
        Assert.Equal(NavalJson.Serialize(before.NavalBroker!.Operational!.Obligations),NavalJson.Serialize(after.NavalBroker!.Operational!.Obligations));
        Assert.Equal(NavalJson.Serialize(before.NavalBroker.Operational.LastBalances),NavalJson.Serialize(after.NavalBroker.Operational.LastBalances));
        Assert.Equal(before.NavalBroker.Operational.LastBalanceCaptureUtc,after.NavalBroker.Operational.LastBalanceCaptureUtc);
        Assert.Equal(NavalJson.Serialize(before.NavalBroker.Operational.LastCurrentMarketFunding),NavalJson.Serialize(after.NavalBroker.Operational.LastCurrentMarketFunding));
        Assert.Equal(NavalJson.Serialize(before.NavalBroker.Operational.Intents),NavalJson.Serialize(after.NavalBroker.Operational.Intents));
        Assert.Equal(NavalJson.Serialize(before.Naval!.Fills),NavalJson.Serialize(after.Naval!.Fills));Assert.Equal(before.NavalBroker.HighWater,after.NavalBroker.HighWater);
        Assert.Equal("STOP",after.Naval.Mode);Assert.False(loop.ReadStatus().Ready);Assert.Null(PendingMixed(fixture));Assert.Equal(0,fixture.Peer.Posts);
        f.Mutate=basis;fixture.Market.OnBook=null;
        var request=f.Request with{Purpose=OkxActivationPurpose.Execute,Session=fixture.Account.TradingSessionId};var lease=await fixture.Runtime.Activation.RequestAsync(request,default);Assert.True(loop.Arm(request,lease!));Assert.Equal(NavalMutationStatus.Applied,(await loop.ReconcileAsync(default)).Status);Assert.Equal(NavalMutationStatus.Applied,loop.Start().Status);
        }
        await MixedRunWait(fixture);
    }
    private static async Task MixedRunWait(StopMixed fixture)
    {
        var loop=fixture.Loop;fixture.Market.Wait=true;
        using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(20));var running=loop.RunAsync(cancel.Token);
        try
        {
            for(int n=0;n<300&&Number(Evaluation(loop),"Completed")==0;n++)await Task.Delay(10);
            Assert.True(Number(Evaluation(loop),"Completed")>0);Assert.Equal(0L,Number(Evaluation(loop),"Entry"));Assert.Equal(0,fixture.Peer.Posts);
            // Exercise the existing host's actual STATUS projection without opening a vault,
            // launching a child, or adding a synthetic production switch.
            var type=typeof(LinuxDemoHost);var constructor=type.GetConstructors(BindingFlags.Instance|BindingFlags.NonPublic).Single();using var output=new StringWriter();
            var host=constructor.Invoke(new object?[]{fixture.F.Root,null,null,output,null,null,(Func<bool>)(()=>true),CancellationToken.None});
            type.GetField("loop",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(host,loop);type.GetField("account",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(host,fixture.Account);
            type.GetMethod("Status",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(host,null);string text=output.ToString();
            Assert.Contains("evaluations=",text);Assert.Contains("wait=",text);Assert.Contains("entry=0 exit=0 evaluation=EVALUATED_WAIT",text);Assert.DoesNotContain("SOL",text);Assert.DoesNotContain("EUR",text);
        }
        finally{loop.Stop();cancel.Cancel();try{await running;}catch(OperationCanceledException){}}
        Assert.Equal("STOP",loop.ReadStatus().Mode);Assert.Single(loop.ReadStatus().Obligations);Assert.Null(PendingMixed(fixture));
    }
    private static object? PendingMixed(StopMixed fixture)=>typeof(NavalPortfolioCoordinator).GetField("_brokerContinuation",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(fixture.Account.Coordinator);
    private sealed class StopMixed:IAsyncDisposable
    {
        internal readonly OperationalPeer F;internal readonly FiniteDemoPeer History;internal readonly TradingPeer Peer;internal readonly StopMarket Market;
        internal OkxNavalRuntime Runtime=null!;internal OkxAccountConnectionService Account=null!;internal OkxOperationalTradingService Loop=null!;
        private StopMixed(OperationalPeer f,FiniteDemoPeer history){F=f;History=history;Peer=new(f){CandleInstrument="SOL-EUR"};Market=new(Peer);}
        private static int nextMixedUid=99220000;
        internal static async Task<StopMixed> Create()
        {
            string uid=Guid.NewGuid().ToString("N");
            string accountUid=Interlocked.Increment(ref nextMixedUid).ToString();
            var binding=OkxConfiguration.Bind(new OkxNavalOptions{Schema="okx-naval-config-v1",Scopes=new[]{new OkxScopeOptions{ScopeId="account",Environment="OkxDemo",Region="EEA",ExpectedUid=accountUid,ExpectedMainUid=accountUid,QuoteCurrency="EUR",CredentialBindingId="key",SafetyVersion="okx-cash-v1"}}})[0];
            var f=new OperationalPeer(binding:binding,finite:true);var h=new FiniteDemoPeer(f){BaseCurrency="SOL"};var fixture=new StopMixed(f,h);bool benchmark=false;
            f.Mutate=(p,b)=>
            {
                OkxOrdinaryMixedAccountTests.Shape(f,h,p,b);
                if(benchmark&&p=="/api/v5/account/instruments"){var btc=b["data"]![0]!.DeepClone();btc["instId"]="BTC-USDC";btc["baseCcy"]="BTC";b["data"]!.AsArray().Add(btc);}
                Denominate(b);
                if(benchmark&&p is "/api/v5/trade/order" or "/api/v5/trade/orders-history" or "/api/v5/trade/orders-history-archive")
                    foreach(var row in b["data"]!.AsArray().Where(row=>row!["state"]!.GetValue<string>()=="filled")){row!["posSide"]=p=="/api/v5/trade/order"?"net":"";row["tradeQuoteCcy"]=p=="/api/v5/trade/order"?"":"EUR";}
            };
            try
            {
            f.Runtime.FiniteFundingPremise=(NavalFiniteOperatorPremise)OkxCurrentMarketFundingTests.Premise(f);await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
            await using(var finite=OkxOperationalTradingService.Create(f.Service,f.Clock,(s,k,d,x)=>OkxExecutionClient.ForTests(h,f.Clock,s,k,d,x),h))
            {
                finite.BindFiniteSafety(()=>true);
                string result=await OkxFiniteDemoSequence.RunAsync(finite,f.Service,uid,stage=>
                {
                    if(stage=="FILLED_BUY_SUBMIT_RESULT")f.WriteBoundary=b=>{if(h.SellClient!=""&&b==Apex.Desktop.Services.AtomicWriteBoundary.BeforeTempCreate)throw new IOException("synthetic-post-send-commit-fault");};
                },default);
                Assert.Equal("OWNED_EXIT_INCOMPLETE_CHECK_RESIDUAL_PROTECTION",result);f.WriteBoundary=null;
            }
            var failed=f.Service.BrokerSession!.ReadState();var original=failed.NavalBroker!.Operational!.Intents.Last();Assert.Equal(NavalExecutionStage.Dispatching,original.Stage);Assert.Null(original.Outcome);
            await f.Service.DisconnectAsync();
            fixture.Runtime=OkxNavalRuntime.ComposeLinuxDemo(f.Root,f.Binding,f,f,()=>true);
            fixture.Account=AccountFor(f,fixture.Runtime);
            var request=f.Request with{Session=Guid.NewGuid()};var lease=await fixture.Runtime.Activation.RequestAsync(request,default);Assert.True(await fixture.Account.ConnectAsync(request,lease!,default),fixture.Account.Status);
            fixture.Loop=OkxOperationalTradingService.Create(fixture.Account,f.Clock,(s,k,d,x)=>OkxExecutionClient.ForTests(fixture.Peer,f.Clock,s,k,d,x),fixture.Market);
            Assert.Equal("ORDINARY_DEMO_PREPARED",(await fixture.Loop.PrepareOrdinaryDemoAsync(default)).Code);benchmark=true;
            var state=fixture.Account.BrokerSession!.ReadState();Assert.Equal(6,state.NavalBroker!.Operational!.LastBalances!.Count);Assert.Single(state.NavalBroker.Operational.Obligations);Assert.NotNull(state.NavalBroker.Operational.OrdinaryDemo);Assert.Equal(NavalJson.Serialize(original),NavalJson.Serialize(state.NavalBroker.Operational.Intents.Single(i=>i.Id==original.Id)));
            request=request with{Purpose=OkxActivationPurpose.Execute,Session=fixture.Account.TradingSessionId};lease=await fixture.Runtime.Activation.RequestAsync(request,default);Assert.True(fixture.Loop.Arm(request,lease!));var observed=await fixture.Loop.ReconcileAsync(default);Assert.True(observed.Status==NavalMutationStatus.Applied,observed.Code+" "+string.Join(",",fixture.Loop.ReadStatus().Obligations));Assert.Equal(NavalMutationStatus.Applied,fixture.Loop.Start().Status);
            return fixture;
            }
            catch{f.WriteBoundary=null;await fixture.DisposeAsync();throw;}
        }
        private static OkxAccountConnectionService AccountFor(OperationalPeer f,OkxNavalRuntime runtime)=>OkxAccountConnectionService.ForTests(runtime,f.Root,f.Binding,f,f.Clock,
            (s,k,d,x)=>OkxExecutionObservationClient.ForTests(f,f.Clock,s,k,d,x,(t,ct)=>{f.Clock.Advance(t);return Task.CompletedTask;}),
            (s,k,d,x)=>OkxObservationStream.ForTests((u,ct)=>Task.FromResult<WebSocket>(new OperationalSocket(u)),f.Clock,s,k,d,x),()=>OkxNavalStore.ForTests(f.Root,f.Binding));
        private static void Denominate(JsonNode n)
        {
            if(n is JsonObject obj)foreach(var pair in obj.ToArray()){if(pair.Value is JsonValue v&&v.TryGetValue<string>(out var s))obj[pair.Key]=s.Replace("USDC","EUR",StringComparison.Ordinal);else if(pair.Value!=null)Denominate(pair.Value);}
            if(n is JsonArray a)for(int i=0;i<a.Count;i++){if(a[i] is JsonValue v&&v.TryGetValue<string>(out var s))a[i]=s.Replace("USDC","EUR",StringComparison.Ordinal);else if(a[i]!=null)Denominate(a[i]!);}
        }
        public async ValueTask DisposeAsync(){if(Loop!=null)await Loop.DisposeAsync();if(Account!=null)await Account.DisposeAsync();if(Runtime!=null)await Runtime.DisposeAsync();Market.Dispose();Peer.Dispose();History.Dispose();await F.DisposeAsync();}
    }
}
