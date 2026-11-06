using System;
using System.Threading.Tasks;
using Apex.Desktop.Okx;
using Apex.Desktop.Services;
using Xunit;
namespace Apex.Tests;
public sealed class OkxWindowsLocalExecutionTests
{
    [Theory]
    [InlineData("attach-local")][InlineData("prepare")]
    [Trait("Gate","WindowsLocalExecution")]
    public async Task Closed_local_routes_require_Operator_origin_and_command_only(string action)
    {
        Assert.True(OkxControlEndpoints.IsPath("/api/naval/okx/account/"+action));
        await using var h=new NavalProductionHost();await h.Start();
        foreach(string mutation in new[]{"reader","origin","header","extra","duplicate","valid"})
        {
            using var request=h.Request("/api/naval/okx/missing/"+action,mutation=="reader"?"read":"operator","POST");
            if(mutation!="header")request.Headers.Add("X-Apex-Local","1");
            if(mutation=="origin")request.Headers.Add("Origin","https://foreign.invalid");
            string body=mutation=="extra"?"{\"commandId\":\"x\",\"execute\":true}":mutation=="duplicate"?"{\"commandId\":\"x\",\"commandId\":\"y\"}":"{\"commandId\":\"x\"}";
            request.Content=new System.Net.Http.StringContent(body,System.Text.Encoding.UTF8,"application/json");
            var status=(await h.Client.SendAsync(request)).StatusCode;
            Assert.Equal(mutation is "reader" or "origin" or "header"?System.Net.HttpStatusCode.Forbidden:mutation=="valid"?System.Net.HttpStatusCode.NotFound:System.Net.HttpStatusCode.BadRequest,status);
        }
    }
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
