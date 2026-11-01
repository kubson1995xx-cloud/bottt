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
    public async Task Production_control_create_path_never_promotes_synthetic_baseline_to_ready_trader()
    {
        await using var f=new OperationalPeer();
        // Runtime lookup must reuse the accepted synthetic account service, not connect the Internet.
        Assert.Same(f.Service,f.Runtime.AccountService(f.Binding.ScopeId));
        var config=new ConfigStore(f.Root);
        await using var control=new OkxOperatorControl(f.Runtime,config);
        Assert.Empty(f.Paths);Assert.Equal(0,f.SecretOpens);
        Assert.Equal("BASELINE_APPROVAL_REQUIRED",await control.ExecuteAsync(f.Binding.ScopeId,"connect","connect-1",default));
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
    }
}
