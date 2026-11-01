using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Apex.Desktop.Okx;
using Apex.Desktop.Services;
using Xunit;
namespace Apex.Tests;
public sealed class OkxOwnerControlTests
{
    [Fact]
    public async Task Runtime_control_lookup_uses_the_registered_account_owner()
    {
        await using var f=new OperationalPeer();
        Assert.Same(f.Service,f.Runtime.AccountService(f.Binding.ScopeId));
    }
    [Fact]
    public async Task Stop_is_not_lost_when_the_bounded_command_cache_is_full()
    {
        await using var f=new OperationalPeer();var config=new ConfigStore(f.Root);
        await using var control=new OkxOperatorControl(f.Runtime,config);
        for(int i=0;i<512;i++)Assert.Equal("CONNECT_REQUIRED",await control.ExecuteAsync(f.Binding.ScopeId,"start","fill-"+i,default));
        Assert.Equal("OPERATOR_STOP",await control.ExecuteAsync(f.Binding.ScopeId,"stop","safety-stop",default));
        Assert.Equal("EMERGENCY_STOP_NO_OPEN_OWNER",await control.ExecuteAsync(f.Binding.ScopeId,"emergency","safety-emergency",default));
        Assert.Empty(f.Paths);Assert.Equal(0,f.SecretOpens);
    }
    [Fact]
    public async Task Production_control_create_path_never_promotes_synthetic_baseline_to_ready_trader()
    {
        // Advancing a scripted clock and recollecting updates the process-wide public quota.
        // Isolate that deliberate future clock from unrelated fixtures; do not weaken production quotas.
        var start=new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")??"dotnet"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
        start.ArgumentList.Add(typeof(SyntheticHostEntry).Assembly.Location);start.ArgumentList.Add("--okx-owner-control");
        using var child=System.Diagnostics.Process.Start(start)!;
        try{await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));Assert.Equal(0,child.ExitCode);}
        finally{if(!child.HasExited){child.Kill(true);await child.WaitForExitAsync();}}
    }
    internal static async Task<int> ControlProbe()
    {
        await using var f=new OperationalPeer();
        // Runtime lookup must reuse the accepted synthetic account service, not connect the Internet.
        Assert.Same(f.Service,f.Runtime.AccountService(f.Binding.ScopeId));
        var config=new ConfigStore(f.Root);
        await using var control=new OkxOperatorControl(f.Runtime,config);
        Assert.Empty(f.Paths);Assert.Equal(0,f.SecretOpens);
        Assert.Equal("BASELINE_APPROVAL_REQUIRED",await control.ExecuteAsync(f.Binding.ScopeId,"connect","connect-1",default));
        f.Clock.Advance(TimeSpan.FromSeconds(31)); // User waits before the separate native baseline action.
        Assert.Equal("CONNECTED_STOP",await control.ExecuteAsync(f.Binding.ScopeId,"baseline","baseline-1",default));
        Assert.Equal("PRODUCTION_PROTECTION_PROFILE_UNAPPROVED",await control.ExecuteAsync(f.Binding.ScopeId,"start","start-1",default));
        using(var status=JsonDocument.Parse(JsonSerializer.Serialize(control.ReadStatus())))
        {
            var a=status.RootElement.GetProperty("accounts")[0];Assert.False(a.GetProperty("ready").GetBoolean());
            Assert.Equal("STOP",a.GetProperty("mode").GetString());Assert.Equal("1234.56",a.GetProperty("cash").GetString());
        }
        Assert.Equal("OPERATOR_STOP",await control.ExecuteAsync(f.Binding.ScopeId,"stop","stop-1",default));
        Assert.Equal("COMMAND_ALREADY_SEEN_OR_LIMIT",await control.ExecuteAsync(f.Binding.ScopeId,"start","start-1",default));
        Assert.All(f.Methods,m=>Assert.Equal(System.Net.Http.HttpMethod.Get,m));
        return 0;
    }
}
