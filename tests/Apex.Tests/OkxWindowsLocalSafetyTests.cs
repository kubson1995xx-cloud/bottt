using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Immutable;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Apex.Desktop.Services;
using Xunit;
namespace Apex.Tests;
public sealed class OkxWindowsLocalSafetyTests
{
    [Theory]
    [InlineData("stop")][InlineData("cancel")][InlineData("dispose")]
    [Trait("Gate","WindowsLocalExecution")]
    public async Task Late_protected_attachment_after_revocation_cannot_publish_ordinary_authority(string action)
    {
        Assert.True(OperatingSystem.IsWindows());await using var f=new OperationalPeer(native:true);await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));await f.Service.DisconnectAsync();
        byte[] before=File.ReadAllBytes(f.Target);var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.ActivationOverride=(r,ct)=>{Assert.Equal(OkxActivationPurpose.LocalAttachment,r.Purpose);entered.TrySetResult();return new(release.Task);};
        var control=new OkxOperatorControl(f.Runtime,new ConfigStore(f.Root));
        try
        {
            var attaching=control.ExecuteAsync(f.Binding.ScopeId,"attach-local","attach",default);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task? disposing=null;if(action=="dispose")disposing=control.DisposeAsync().AsTask();else Assert.Equal("OPERATOR_STOP",await control.ExecuteAsync(f.Binding.ScopeId,action,"revoke",default));
            release.SetResult(true);Assert.NotEqual("LOCAL_EXISTING_DEMO_ATTACHED_STOP",await attaching);if(disposing!=null)await disposing;
            Assert.False(f.Service.OrdinaryDemoPolicy);Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(f.Target)));Assert.False(f.Service.IsConnected);
        }
        finally{release.TrySetResult(false);await control.DisposeAsync();}
    }
    [Fact]
    [Trait("Gate","WindowsLocalExecution")]
    public async Task Existing_remote_profile_or_missing_native_history_refuses_without_prompt_or_reseed()
    {
        Assert.True(OperatingSystem.IsWindows());await using var f=new OperationalPeer(native:true);var config=new ConfigStore(f.Root);int prompts=0;f.ActivationOverride=(r,c)=>{prompts++;return ValueTask.FromResult(true);};
        await using var control=new OkxOperatorControl(f.Runtime,config);
        string remote=Path.Combine(f.Root,"controller-connection.json");File.WriteAllText(remote,"{}");
        Assert.Equal("LOCAL_ATTACH_REFUSED_PRESERVED",await control.ExecuteAsync(f.Binding.ScopeId,"attach-local","remote",default));Assert.Equal(0,prompts);File.Delete(remote);
        Assert.NotEqual("LOCAL_EXISTING_DEMO_ATTACHED_STOP",await control.ExecuteAsync(f.Binding.ScopeId,"attach-local","missing",default));Assert.Equal(0,prompts);Assert.False(File.Exists(f.Target));Assert.False(f.Service.OrdinaryDemoPolicy);Assert.Equal(0,f.Approvals);
    }
    [Fact]
    [Trait("Gate","WindowsLocalExecution")]
    public async Task Read_enrollment_cannot_attach_prepare_arm_start_baseline_or_recover_and_does_not_create_execution_scope()
    {
        Assert.True(OperatingSystem.IsWindows());await using var f=new OperationalPeer(native:true);var config=new ConfigStore(f.Root);
        var observed=new OkxEnrollmentObservation(f.Binding.Account.AccountId,f.Binding.MainUid,"Demo","DemoTradingApi",f.Request.CredentialFingerprint,DateTimeOffset.UtcNow,ImmutableArray<OkxExecutionBalanceDetailDto>.Empty,ImmutableArray<OkxExecutionInstrumentDto>.Empty);
        var account=new WindowsOkxReadStore(f.Root).Save(observed,new[]{OperationalPeer.Key,OperationalPeer.Secret,OperationalPeer.Pass});
        await using var runtime=OkxNavalRuntime.Compose(f.Root,Array.Empty<OkxScopeBinding>(),f,f);await using var control=new OkxOperatorControl(runtime,config);
        foreach(string action in new[]{"attach-local","prepare","connect","baseline","arm","start","recover"})Assert.Equal("READ_ONLY_NO_ACTIVATION",await control.ExecuteAsync(account.ScopeId,action,Guid.NewGuid().ToString("N"),default));
        Assert.Empty(runtime.Bindings);Assert.False(File.Exists(f.Target));Assert.Empty(f.Paths);
    }
    [Fact]
    [Trait("Gate","WindowsLocalExecution")]
    public async Task Attachment_lease_is_not_Observe_Execute_or_serializable_execution_authority()
    {
        Assert.True(OperatingSystem.IsWindows());await using var f=new OperationalPeer(native:true);
        var request=f.Request with{Purpose=OkxActivationPurpose.LocalAttachment};var lease=await f.Runtime.Activation.RequestAsync(request,default);Assert.NotNull(lease);
        Assert.False(await f.Service.ConnectAsync(request,lease!,default));Assert.Empty(f.Paths);Assert.False(f.Service.OrdinaryDemoPolicy);
        Assert.ThrowsAny<Exception>(()=>System.Text.Json.JsonSerializer.Deserialize<WindowsOkxLocalAttachment>("{}"));
        Assert.False(OkxControlEndpoints.IsPath("/api/naval/okx/account/activate"));Assert.False(OkxControlEndpoints.IsPath("/api/naval/okx/account/attach-local/extra"));
    }
}
