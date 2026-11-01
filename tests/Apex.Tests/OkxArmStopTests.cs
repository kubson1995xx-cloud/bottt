using System;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.Tests;
public sealed class OkxArmStopTests
{
    [Fact]
    public async Task Stop_during_consumed_execute_credential_open_cannot_be_overwritten_by_late_arm()
    {
        await using var f=new OperationalPeer();using var secrets=new BlockingSecrets(f);
        await using var runtime=OkxNavalRuntime.Compose(f.Root,new[]{f.Binding},secrets,f);
        await using var account=OkxAccountConnectionService.ForTests(runtime,f.Root,f.Binding,f,f.Clock,
            (s,p,h,x)=>OkxExecutionObservationClient.ForTests(f,f.Clock,s,p,h,x,(t,ct)=>{f.Clock.Advance(t);return Task.CompletedTask;}),
            (s,p,h,x)=>OkxObservationStream.ForTests((uri,ct)=>{var socket=new OperationalSocket(uri);f.Sockets.Add(socket);return Task.FromResult<WebSocket>(socket);},f.Clock,s,p,h,x));
        var observe=await runtime.Activation.RequestAsync(f.Request,default);Assert.NotNull(observe);
        Assert.True(await account.ConnectAsync(f.Request,observe!,default));Assert.True(await account.ApproveAndCreateAsync(default));
        await using var trading=OkxOperationalTradingService.Create(account);
        var request=f.Request with{Purpose=OkxActivationPurpose.Execute,Session=account.TradingSessionId,ExpiresUtc=DateTimeOffset.UtcNow.AddSeconds(120)};
        var permission=await runtime.Activation.RequestAsync(request,default);Assert.NotNull(permission);
        secrets.Block=true;var arm=Task.Run(()=>trading.Arm(request,permission!));
        try
        {
            Assert.True(secrets.Entered.Wait(TimeSpan.FromSeconds(3)));
            trading.Stop();secrets.Release.Set();await arm.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(trading.ReadStatus().Armed);Assert.Equal("STOP",trading.ReadStatus().Mode);
        }
        finally{secrets.Release.Set();await arm;}
        secrets.Block=false;
        var fresh=await runtime.Activation.RequestAsync(request,default);Assert.NotNull(fresh);
        Assert.True(trading.Arm(request,fresh!));Assert.True(trading.ReadStatus().Armed);
        trading.EmergencyStop();Assert.False(trading.ReadStatus().Armed);
        Assert.All(f.Methods,m=>Assert.Equal(System.Net.Http.HttpMethod.Get,m));
    }
    private sealed class BlockingSecrets(OperationalPeer f):IOkxExecutionSecretProvider,IDisposable
    {
        internal bool Block;internal readonly ManualResetEventSlim Entered=new(),Release=new();
        public OkxExecutionCredentialLease Open(OkxScopeBinding binding,OkxScopePaths paths)
        {if(Block){Entered.Set();Release.Wait(TimeSpan.FromSeconds(5));}return f.Open(binding,paths);}
        public void Dispose(){Release.Set();Entered.Dispose();Release.Dispose();}
    }
}
