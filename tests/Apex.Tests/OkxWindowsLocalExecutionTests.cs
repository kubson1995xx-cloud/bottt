using System;
using System.Threading.Tasks;
using Apex.Desktop.Okx;
using Apex.Desktop.Services;
using Xunit;
namespace Apex.Tests;
public sealed class OkxWindowsLocalExecutionTests
{
    [Fact]
    [Trait("Gate","WindowsLocalExecution")]
    public async Task Native_existing_Windows_owner_protected_attachment_is_distinct_from_Observe_and_START()
    {
        Assert.True(OperatingSystem.IsWindows(),"Native Windows gate must not silently skip");
        await using var f=new OperationalPeer(native:true);
        await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        await f.Service.DisconnectAsync();
        var config=new ConfigStore(f.Root);
        await using var control=new OkxOperatorControl(f.Runtime,config);
        Assert.False(f.Service.OrdinaryDemoPolicy);
        Assert.Equal("LOCAL_EXISTING_DEMO_ATTACHED_STOP",await control.ExecuteAsync(f.Binding.ScopeId,"attach-local","attach",default));
        Assert.True(f.Service.OrdinaryDemoPolicy);
        Assert.Equal("STOP",f.Runtime.Mode);
    }
}
