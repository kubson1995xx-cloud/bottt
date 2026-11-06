using System;
using System.Linq;
using System.Net.WebSockets;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.Tests;
public sealed partial class OkxConditionalTradingTests
{
    private static async Task<MultiFixture> NativeAdvisoryFixture(Func<IOkxSerializedDispatchFence,IOkxSerializedDispatchFence>? wrap=null)
    {
        Assert.True(OperatingSystem.IsWindows());
        var fixture=await MultiFixture.Create(windowsLocalSeed:true);var f=fixture.F;
        fixture.Runtime=OkxNavalRuntime.Compose(f.Root,new[]{f.Binding},f,f);
        var approval=new WindowsOkxOrdinaryApprovalProvider(()=>fixture.Account);
        fixture.Account=OkxAccountConnectionService.ForTests(fixture.Runtime,f.Root,f.Binding,approval,f.Clock,
            (s,k,d,x)=>OkxExecutionObservationClient.ForTests(f,f.Clock,s,k,d,x,(t,c)=>{f.Clock.Advance(t);return Task.CompletedTask;}),
            (s,k,d,x)=>OkxObservationStream.ForTests((u,c)=>Task.FromResult<WebSocket>(new OperationalSocket(u)),f.Clock,s,k,d,x),()=>OkxNavalStore.OpenNative(f.Root,f.Binding));
        Assert.True(await fixture.Runtime.AttachWindowsExistingAsync(f.Binding,0,default,new object(),()=>true));
        var request=f.Request with{Session=Guid.NewGuid()};var permission=await fixture.Runtime.Activation.RequestAsync(request,default);Assert.NotNull(permission);
        Assert.True(await fixture.Account.ConnectAsync(request,permission!,default),fixture.Account.Status);
        fixture.Loop=OkxOperationalTradingService.Create(fixture.Account,f.Clock,(s,k,d,x)=>OkxExecutionClient.ForTests(fixture.Peer,f.Clock,s,k,d,wrap?.Invoke((IOkxSerializedDispatchFence)x)??x),fixture.Market);
        Assert.Equal("ORDINARY_DEMO_PREPARED",(await fixture.Loop.PrepareOrdinaryDemoAsync(default)).Code);
        request=request with{Purpose=OkxActivationPurpose.Execute,Session=fixture.Account.TradingSessionId};permission=await fixture.Runtime.Activation.RequestAsync(request,default);Assert.True(fixture.Loop.Arm(request,permission!));
        Assert.Equal(NavalMutationStatus.Applied,(await fixture.Loop.ReconcileAsync(default)).Status);Assert.Equal(NavalMutationStatus.Applied,fixture.Loop.Start().Status);
        fixture.Market.Wait=false;return fixture;
    }
    [Fact][Trait("Gate","WindowsAdvisoryIntegration")]
    public async Task Windows_new_strategy_buy_without_AI_vetoes_before_registration_without_global_STOP()
    {
        await using var fixture=await NativeAdvisoryFixture();
        var before=fixture.Account.BrokerSession!.ReadState();int posts=fixture.Peer.Posts;
        var result=await fixture.Loop.RunOnceAsync(NavalFleet.Alpha,"BTC-EUR",default);
        Assert.StartsWith("AI_ENTRY_VETO",result.Code);Assert.Equal(NavalMutationStatus.Refused,result.Status);Assert.Equal(posts,fixture.Peer.Posts);
        var after=fixture.Account.BrokerSession.ReadState();Assert.Equal(before.NavalBroker!.Operational!.Signals.Count,after.NavalBroker!.Operational!.Signals.Count);Assert.Equal(before.Naval.Reservations.Count,after.Naval.Reservations.Count);Assert.Equal(before.NavalBroker!.Operational!.Intents.Count,after.NavalBroker!.Operational!.Intents.Count);
        Assert.Equal("RUNNING",fixture.Loop.ReadStatus().Mode);Assert.True(fixture.Loop.CanExecute);
        fixture.Loop.Stop();Assert.Equal("STOP",fixture.Loop.ReadStatus().Mode);
    }
    private static async Task SeedSyntheticAdvice(MultiFixture f,string pair="BTC-EUR",string mode="buy")
    {
        var spec=NavalOrdinaryCatalogPolicy.Definition(f.Account.BrokerSession!.ReadState().NavalBroker!).Catalog.Single(i=>i.Instrument==pair);
        using var feed=new OkxPublicMarketFeed(f.F.Clock,f.Market);var series=await feed.ReadSeriesAsync(spec,default);
        var cutoff=new DateTimeOffset(f.F.Clock.GetUtcNow().UtcDateTime.Date,TimeSpan.Zero);
        var reports=new OkxWindowsAdvisoryConsumerTests.Reports{Digest=OkxWindowsAdvisoryConsumerTests.Hash(OkxAdvisoryCanonicalData.Serialize(series,cutoff))};
        reports.Mutate=(path,node)=>
        {
            if(node.ContainsKey("Instrument"))node["Instrument"]=pair;
            if(node.ContainsKey("AsOfUtc"))node["AsOfUtc"]=cutoff.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            if(node.ContainsKey("ExpiresUtc"))node["ExpiresUtc"]=cutoff.AddDays(1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            if(mode=="fixture"&&path=="manifest.json")node["Inference"]="fixture";
        };
        reports.Build();string root=System.IO.Path.Combine(f.F.Root,"advisory","reports");
        using var guard=Apex.Desktop.Services.WindowsPrivateData.Open(root,AppContext.BaseDirectory);
        foreach(var row in reports.Files){string path=System.IO.Path.Combine(root,pair,row.Key);System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);System.IO.File.WriteAllBytes(path,row.Value);}
    }
    private sealed class AdviceFinalFence(IOkxSerializedDispatchFence inner,Action<OkxPreparedRequest> mutate):IOkxSerializedDispatchFence
    {
        internal int Enters;
        public bool CanDispatch(OkxPreparedRequest r,DateTimeOffset now)=>false;
        public IDisposable? TryEnterDispatch(OkxPreparedRequest r,DateTimeOffset now)
        {Enters++;mutate(r);return inner.TryEnterDispatch(r,now);}
    }
    [Theory][InlineData("none")][InlineData("generation")][InlineData("expiry")]
    [Trait("Gate","WindowsAdvisoryIntegration")]
    public async Task Windows_actual_post_signing_fence_has_send_control_and_mutations_are_NotSent(string mode)
    {
        AdviceFinalFence? fence=null;MultiFixture? current=null;
        await using var fixture=await NativeAdvisoryFixture(x=>fence=new(x,r=>
        {
            if(mode=="generation")
            {
                string pointer=System.IO.Path.Combine(current!.F.Root,"advisory","reports","BTC-EUR","CURRENT.json");
                var p=System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(pointer))!;p["Generation"]=new string('c',32);
                System.IO.File.WriteAllText(pointer+".next",p.ToJsonString());System.IO.File.Move(pointer+".next",pointer,true);
            }
            var prepared=typeof(OkxOperationalTradingService).GetMethod("PreparedAdmission",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
            // Financial/quote/maxima guards remain valid at this reached post-signing fence.
            // Expiry mutation changes only the report, never the shared financial clock.
            if(mode=="expiry")
            {
                string folder=System.IO.Path.Combine(current!.F.Root,"advisory","reports","BTC-EUR");
                string pointer=System.IO.Path.Combine(folder,"CURRENT.json");var p=System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(pointer))!;
                string manifest=System.IO.Path.Combine(folder,"generations",p["Generation"]!.GetValue<string>(),"manifest.json");
                var m=System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(manifest))!;m["ExpiresUtc"]=current.F.Clock.GetUtcNow().AddSeconds(-1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
                byte[] bytes=System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(m);System.IO.File.WriteAllBytes(manifest,bytes);p["ManifestSha256"]=OkxWindowsAdvisoryConsumerTests.Hash(bytes);
                System.IO.File.WriteAllText(pointer+".next",p.ToJsonString());System.IO.File.Move(pointer+".next",pointer,true);
            }
            Assert.True((bool)prepared.Invoke(current!.Loop,new object[]{r})!,"Earlier private maxima/funding fence must remain valid after report-only mutation");
        }));current=fixture;
        await SeedSyntheticAdvice(fixture);int before=fixture.Peer.Posts;
        // All funding, sizing, quotes, private maxima, protection and durable Prepare must pass
        // before the actual transport calls this wrapper, AFTER Authenticate in OkxExecutionClient.
        var result=await fixture.Loop.RunOnceAsync(NavalFleet.Alpha,"BTC-EUR",default);
        Assert.Equal(1,fence!.Enters);
        var intent=fixture.Account.BrokerSession!.ReadState().NavalBroker!.Operational!.Intents.Last();Assert.StartsWith("entry-",intent.Id);
        Assert.Equal(mode=="none"?before+1:before,fixture.Peer.Posts);
        Assert.Equal(mode=="none"?NavalDispatchDisposition.Acknowledged:NavalDispatchDisposition.NotSent,intent.Outcome!.Disposition);
        Assert.Equal(mode=="none"?NavalExecutionStage.Accepted:NavalExecutionStage.NotSent,intent.Stage);
    }
}
