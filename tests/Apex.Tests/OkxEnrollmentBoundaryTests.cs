using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Apex.Desktop.Services;
using Xunit;
namespace Apex.Tests;
public sealed class OkxEnrollmentBoundaryTests
{
    [Fact]
    public async Task Live_read_test_is_rejected_before_opening_any_credential_or_account()
    {
        await using var h=new OkxCompositionFixture();var config=new ConfigStore(h.Root);
        config.Update(c=>c.OkxNaval=JsonSerializer.Deserialize<OkxNavalOptions>(OkxCompositionFixture.ValidConfig(NavalEnvironment.Live)));
        int opens=0;var provider=new DenyProvider(()=>opens++);
        await using var runtime=OkxNavalRuntime.Compose(h.Root,OkxConfiguration.Bind(config.Config.OkxNaval),provider,new UnavailableOkxActivationProvider());
        await using var control=new OkxOperatorControl(runtime,config);
        Assert.Equal("DEMO_TEST_LIVE_DENIED",await control.ExecuteAsync("synthetic-live","test-demo","live-test",default));
        Assert.Equal(0,opens);Assert.Equal("STOP",runtime.Mode);Assert.False(Directory.Exists(Path.Combine(h.Root,"okx")));
    }
    [Fact]
    public async Task Explicit_saved_diagnosis_distinguishes_metadata_only_and_does_not_seed()
    {
        await using var h=new OkxCompositionFixture();var config=new ConfigStore(h.Root);
        config.Update(c=>c.OkxNaval=JsonSerializer.Deserialize<OkxNavalOptions>(OkxCompositionFixture.ValidConfig(NavalEnvironment.OkxDemo)));
        await using var runtime=OkxNavalRuntime.Compose(h.Root,OkxConfiguration.Bind(config.Config.OkxNaval));
        await using var control=new OkxOperatorControl(runtime,config);
        Assert.Equal("METADATA_ONLY_NO_KEY",await control.ExecuteAsync("synthetic-demo","diagnose","diagnose-test",default));
        Assert.False(Directory.Exists(Path.Combine(h.Root,"okx")));
    }
    [Theory]
    [InlineData("test-demo")]
    [InlineData("diagnose")]
    public void Explicit_commands_are_closed_operator_routes(string action)
    {
        Assert.True(OkxControlEndpoints.IsPath("/api/naval/okx/synthetic-demo/"+action));
        Assert.False(OkxControlEndpoints.IsPath("/api/naval/okx/../"+action));
    }
    [Fact]
    public async Task Live_http_test_is_denied_and_reader_cannot_call_either_new_command()
    {
        await using var host=new NavalProductionHost();host.Configuration["OkxNaval"]=System.Text.Json.Nodes.JsonNode.Parse(OkxCompositionFixture.ValidConfig(NavalEnvironment.Live));await host.Start();
        foreach(var action in new[]{"test-demo","diagnose"})foreach(var role in new[]{"operator","read"})
        {
            using var request=host.Request("/api/naval/okx/synthetic-live/"+action,role,"POST");request.Headers.Add("X-Apex-Local","1");request.Content=new System.Net.Http.StringContent("{\"commandId\":\"http-boundary-test\"}",System.Text.Encoding.UTF8,"application/json");
            using var response=await host.Client.SendAsync(request);
            Assert.Equal(role=="read"?System.Net.HttpStatusCode.Forbidden:System.Net.HttpStatusCode.OK,response.StatusCode);
            if(role=="operator"&&action=="test-demo")using(var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync()))Assert.Equal("DEMO_TEST_LIVE_DENIED",doc.RootElement.GetProperty("code").GetString());
        }
    }
    private sealed class DenyProvider(Action open):IOkxExecutionSecretProvider
    {public OkxExecutionCredentialLease Open(OkxScopeBinding b,OkxScopePaths p){open();throw new InvalidDataException("synthetic-private-provider-error");}}
}
