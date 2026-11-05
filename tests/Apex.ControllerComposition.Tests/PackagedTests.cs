using System.Security.Cryptography;
using System.Text.Json;
using Apex.ControllerTransport.Tests;
using Apex.Desktop.Okx;
using Apex.Desktop.Services;
using Apex.Windows.Tests;
using Xunit;
namespace Apex.ControllerComposition.Tests;
[Collection("Composition")]
public sealed class PackagedTests
{
    private static async Task InstallPublic(NativeSshFixture peer,string root,bool wizard=false)
    {
        Assert.True(wizard?await NativePairingTests.Wizard(root,peer.Config,true):await CompositionTests.Pair(root,peer.Config,true));
        string p=Path.Combine(peer.ServerRoot,"authorized_keys");File.WriteAllText(p,File.ReadAllText(Path.Combine(root,"controller-public-key.txt")));CompositionTests.Owner(p);
    }
    private static async Task Pointer(WindowsCdpBrowser b,string sid,string action)
    {
        string selector="[data-controller-action="+action+"]";
        await b.Wait(sid,$"document.querySelector('{selector}')!==null && !document.querySelector('{selector}').disabled","Control not enabled: "+action);
        var rect=await b.Evaluate(sid,$"(()=>{{const e=document.querySelector('{selector}');e.scrollIntoView({{block:'center'}});const r=e.getBoundingClientRect(),x=r.x+r.width/2,y=r.y+r.height/2;return {{x,y,hit:document.elementFromPoint(x,y)===e}};}})()");
        Assert.True(rect.GetProperty("hit").GetBoolean(),"Control occluded: "+action);
        double x=rect.GetProperty("x").GetDouble(),y=rect.GetProperty("y").GetDouble();
        await b.CommandAsync("Input.dispatchMouseEvent",new{type="mouseMoved",x,y},sid);
        await b.CommandAsync("Input.dispatchMouseEvent",new{type="mousePressed",x,y,button="left",clickCount=1},sid);
        await b.CommandAsync("Input.dispatchMouseEvent",new{type="mouseReleased",x,y,button="left",clickCount=1},sid);
        await b.Wait(sid,"document.querySelector('#remote-result')?.textContent.includes('Potwierdzono odpowiedź serwera: "+action+" /')===true","No confirmed pointer command: "+action);
    }
    [Fact] public async Task Extracted_EXE_first_start_and_restart_are_local_no_login_and_not_configured()
    {
        Assert.NotNull(Environment.GetEnvironmentVariable("APEX_COMPOSITION_PACKAGE_EXE"));
        string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ApexControllerFirstStart-"+Guid.NewGuid().ToString("N"));
        try
        {
            for(int attempt=0;attempt<2;attempt++)
            {
                await using var host=new CompositionTests.Host(root);var state=await host.Status();Assert.Equal("NOT_CONFIGURED",state.GetProperty("availability").GetString());
                using var response=await host.Http.GetAsync("/");Assert.Equal(System.Net.HttpStatusCode.OK,response.StatusCode);Assert.False(response.Headers.Contains("WWW-Authenticate"));
                Assert.False(Directory.Exists(Path.Combine(root,"controller-ssh")));Assert.False(File.Exists(Path.Combine(root,"controller-connection.json")));
            }
            WindowsTestHost.Evidence("extracted-first-start-restart",new{actualExtractedExe=true,firstStart=true,restart=true,noManualLogin=true,noAutomaticSsh=true,notConfigured=true,defaultAssociationTested=false});
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Fact] public async Task Extracted_EXE_native_SSH_pointer_lifecycle_five_views_and_close_after_START()
    {
        string exe=Environment.GetEnvironmentVariable("APEX_COMPOSITION_PACKAGE_EXE")??throw new Exception("EXTRACTED_EXE_REQUIRED");Assert.True(File.Exists(exe));
        await using var peer=await NativeSshFixture.Open("composition");using var root=new CompositionTests.Root();await InstallPublic(peer,root.Path_,true);
        await using var host=new CompositionTests.Host(root.Path_);var initial=await host.Status();Assert.Equal("DISCONNECTED",initial.GetProperty("availability").GetString());Assert.Empty(peer.Events);
        string browserRoot=root.Path_+"-browser";
        await using var browser=await WindowsCdpBrowser.StartAsync(browserRoot);string sid=await browser.NewPage();
        await browser.Navigate(sid,new Uri(host.Http.BaseAddress!,"/naval/").AbsoluteUri);
        await browser.Wait(sid,"document.querySelector('#remote-state')?.textContent.includes('DISCONNECTED')===true","Controller not rendered from extracted EXE");
        foreach(var (route,title) in new[]{("command","Command Center"),("radar","Tactical Radar"),("fleets","Fleet Operations"),("animations","Trade Animations"),("analytics","Analytics Center")})
        {await browser.Evaluate(sid,"location.hash="+JsonSerializer.Serialize("#/"+route));await browser.Wait(sid,"document.querySelector('main h1')?.textContent==="+JsonSerializer.Serialize(title),"Five-view route missing: "+route);}
        await Pointer(browser,sid,"connect");Assert.DoesNotContain("CONNECT",peer.Events);
        await Pointer(browser,sid,"connect-account");
        await Pointer(browser,sid,"prepare");await Pointer(browser,sid,"prepare");
        await Pointer(browser,sid,"arm");await Pointer(browser,sid,"start");
        await browser.Wait(sid,"document.querySelector('#remote-state')?.textContent.includes('RUNNING')===true && document.querySelector('#remote-data')?.textContent.includes('BTC')===true","Authoritative synthetic snapshot not rendered");
        string dom=(await browser.Evaluate(sid,"document.documentElement.outerHTML")).GetString()!;
        var credentials=WindowsLocalAccess.Load(root.Path_,"presentation");
        foreach(string secret in new[]{credentials.OperatorSecret,credentials.ReaderSecret})
        {Assert.False(dom.Contains(secret,StringComparison.Ordinal));Assert.False(dom.Contains(Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret))).ToLowerInvariant(),StringComparison.OrdinalIgnoreCase));}
        Assert.Equal(0,browser.AuthenticationCommands);Assert.Equal(0,browser.AuthorizationRequests);Assert.Equal(0,browser.BasicChallenges);
        string? evidence=Environment.GetEnvironmentVariable("APEX_COMPOSITION_EVIDENCE");
        if(evidence!=null){var image=await browser.CommandAsync("Page.captureScreenshot",new{format="png"},sid);File.WriteAllBytes(Path.Combine(evidence,"extracted-controller-running.png"),Convert.FromBase64String(image.GetProperty("data").GetString()!));}
        await Pointer(browser,sid,"stop");Assert.Equal("STOP",File.ReadAllText(Path.Combine(peer.ServerRoot,"state")));
        await Pointer(browser,sid,"arm");await Pointer(browser,sid,"start");
        await host.DisposeAsync();
        Assert.Equal("RUNNING",File.ReadAllText(Path.Combine(peer.ServerRoot,"state")));Assert.Equal(1,peer.Events.Count(x=>x=="STOP"));
        // No implicit STOP after confirmed START; this is a disposable synthetic peer.
        WindowsTestHost.Evidence("extracted-controller-acceptance",new{actualExtractedExe=true,pinnedLoopbackSsh=true,nativePairing=true,pointerCommands=true,fiveViews=5,syntheticBalancesAndHistory=true,explicitStopConfirmed=true,closeAfterConfirmedStartDidNotSendStop=true,noBrowserCredentials=true,brokerTested=false,vpsTested=false});
        File.WriteAllText(Path.Combine(peer.ServerRoot,"state"),"STOP"); // fixture cleanup, not a broker command
    }
    [Fact] public async Task Extracted_EXE_lost_START_ack_is_unknown_stale_and_never_retried()
    {
        Assert.NotNull(Environment.GetEnvironmentVariable("APEX_COMPOSITION_PACKAGE_EXE"));
        await using var peer=await NativeSshFixture.Open("composition-lost-start");using var root=new CompositionTests.Root();await InstallPublic(peer,root.Path_);
        await using var host=new CompositionTests.Host(root.Path_);await host.Status();
        foreach(var (action,ready) in new[]{("connect","canConnectAccount"),("connect-account","canPrepare"),("prepare","canArm"),("arm","canStart")})
        {
            Assert.Equal("confirmed",(await host.Command(action)).GetProperty("outcome").GetString());
            await WindowsTestHost.Until(async()=>{var state=await host.Status();return state.GetProperty(ready).GetBoolean()&&state.GetProperty("pending").GetInt32()==0;},"Fresh post-command readiness missing: "+action);
        }
        var result=await host.Command("start");Assert.Equal("pending-or-unknown",result.GetProperty("outcome").GetString());
        var s=await host.Status();Assert.True(s.GetProperty("stale").GetBoolean());Assert.False(s.GetProperty("canStart").GetBoolean());
        Assert.Equal(1,peer.Events.Count(x=>x=="START"));Assert.DoesNotContain("STOP",peer.Events);
        WindowsTestHost.Evidence("extracted-lost-ack",new{actualExtractedExe=true,outcome="pending-or-unknown",stale=true,startCount=1,noImplicitStop=true,brokerTested=false});
    }
}
