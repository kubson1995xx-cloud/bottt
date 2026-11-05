using System.Net;
using System.Text.Json;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.ControllerPanel.Tests;
public sealed class BrowserFixtureTests
{
    [Fact] public async Task Desktop_local_policy_origin_csrf_and_render_assets_are_enforced()
    {
        await using var h=await Host.Open(desktopLocal:true);
        Assert.Equal(HttpStatusCode.OK,(await h.Send("status",null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await h.Send("status",null,origin:"https://evil.test")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await h.Send("stop",null,"{\"commandId\":\"s\"}",csrf:false)).StatusCode);
        foreach(string asset in new[]{"remote-controller.js","remote-controller.css"}){
            var r=await h.Client.GetAsync("/"+asset);Assert.Equal(HttpStatusCode.OK,r.StatusCode);Assert.NotEmpty(await r.Content.ReadAsByteArrayAsync());
            Assert.Equal(HttpStatusCode.NotFound,(await h.Client.GetAsync("/"+asset+"/")).StatusCode);
        }
        var naval=await h.Client.GetStringAsync("/naval/");Assert.Contains("/remote-controller.js",naval);Assert.Contains("/okx-controls.js",naval);
    }
    [Fact] public async Task Optional_local_browser_strict_stream_fixture()
    {
        string? evidence=Environment.GetEnvironmentVariable("CONTROLLER_BROWSER_EVIDENCE");if(evidence==null)return;
        await using var peer=new StrictPeer();var control=new OkxRemoteControllerControl(ct=>Task.FromResult<IOkxControllerFrameTransport>(peer.Client));await using var h=await Host.Open(control,true);
        Directory.CreateDirectory(evidence);await File.WriteAllTextAsync(Path.Combine(evidence,"browser-host.json"),JsonSerializer.Serialize(new{url=h.Client.BaseAddress!.AbsoluteUri,synthetic=true,localPolicyPrincipalInjected=true}));
        var until=DateTime.UtcNow.AddMinutes(4);while(DateTime.UtcNow<until&&!File.Exists(Path.Combine(evidence,"browser-done")))await Task.Delay(250);
        await File.WriteAllTextAsync(Path.Combine(evidence,"browser-wire-actions.json"),JsonSerializer.Serialize(peer.Actions.ToArray()));
        Assert.Contains("START",peer.Actions);Assert.Contains("STOP",peer.Actions);
    }
}
