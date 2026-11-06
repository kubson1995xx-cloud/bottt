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
    [Fact][Trait("Gate","WindowsAdvisoryIntegration")]
    public async Task Windows_new_strategy_buy_without_AI_vetoes_before_registration_without_global_STOP()
    {
        Assert.True(OperatingSystem.IsWindows());
        await using var fixture=await MultiFixture.Create(windowsLocalSeed:true);var f=fixture.F;
        fixture.Runtime=OkxNavalRuntime.Compose(f.Root,new[]{f.Binding},f,f);
        var approval=new WindowsOkxOrdinaryApprovalProvider(()=>fixture.Account);
        fixture.Account=OkxAccountConnectionService.ForTests(fixture.Runtime,f.Root,f.Binding,approval,f.Clock,
            (s,k,d,x)=>OkxExecutionObservationClient.ForTests(f,f.Clock,s,k,d,x,(t,c)=>{f.Clock.Advance(t);return Task.CompletedTask;}),
            (s,k,d,x)=>OkxObservationStream.ForTests((u,c)=>Task.FromResult<WebSocket>(new OperationalSocket(u)),f.Clock,s,k,d,x),()=>OkxNavalStore.OpenNative(f.Root,f.Binding));
        Assert.True(await fixture.Runtime.AttachWindowsExistingAsync(f.Binding,0,default,new object(),()=>true));
        var request=f.Request with{Session=Guid.NewGuid()};var permission=await fixture.Runtime.Activation.RequestAsync(request,default);Assert.NotNull(permission);
        Assert.True(await fixture.Account.ConnectAsync(request,permission!,default),fixture.Account.Status);
        fixture.Loop=OkxOperationalTradingService.Create(fixture.Account,f.Clock,(s,k,d,x)=>OkxExecutionClient.ForTests(fixture.Peer,f.Clock,s,k,d,x),fixture.Market);
        Assert.Equal("ORDINARY_DEMO_PREPARED",(await fixture.Loop.PrepareOrdinaryDemoAsync(default)).Code);
        request=request with{Purpose=OkxActivationPurpose.Execute,Session=fixture.Account.TradingSessionId};permission=await fixture.Runtime.Activation.RequestAsync(request,default);Assert.True(fixture.Loop.Arm(request,permission!));
        Assert.Equal(NavalMutationStatus.Applied,(await fixture.Loop.ReconcileAsync(default)).Status);Assert.Equal(NavalMutationStatus.Applied,fixture.Loop.Start().Status);
        fixture.Market.Wait=false;var before=fixture.Account.BrokerSession!.ReadState();int posts=fixture.Peer.Posts;
        var result=await fixture.Loop.RunOnceAsync(NavalFleet.Alpha,"BTC-EUR",default);
        Assert.StartsWith("AI_ENTRY_VETO",result.Code);Assert.Equal(NavalMutationStatus.Refused,result.Status);Assert.Equal(posts,fixture.Peer.Posts);
        var after=fixture.Account.BrokerSession.ReadState();Assert.Equal(before.NavalBroker!.Operational!.Signals.Count,after.NavalBroker!.Operational!.Signals.Count);Assert.Equal(before.Naval.Reservations.Count,after.Naval.Reservations.Count);Assert.Equal(before.NavalBroker!.Operational!.Intents.Count,after.NavalBroker!.Operational!.Intents.Count);
        Assert.Equal("RUNNING",fixture.Loop.ReadStatus().Mode);Assert.True(fixture.Loop.CanExecute);
        fixture.Loop.Stop();Assert.Equal("STOP",fixture.Loop.ReadStatus().Mode);
    }
}
