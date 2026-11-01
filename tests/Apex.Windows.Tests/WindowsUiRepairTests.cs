using System.Text.Json;
using Xunit;

namespace Apex.Windows.Tests;

[Collection("Extracted Windows")]
public sealed class WindowsUiRepairTests
{
    private static string J(string s)=>JsonSerializer.Serialize(s);
    private static async Task Screenshot(WindowsCdpBrowser b,string sid,string name)
    {
        var r=await b.CommandAsync("Page.captureScreenshot",new{format="png",captureBeyondViewport=false},sid);
        File.WriteAllBytes(Path.Combine(Environment.GetEnvironmentVariable("APEX_WINDOWS_EVIDENCE")!,name+".png"),Convert.FromBase64String(r.GetProperty("data").GetString()!));
    }
    private static async Task<JsonElement> Hit(WindowsCdpBrowser b,string sid,string selector)
    {
        return await b.Evaluate(sid,"(()=>{let e=document.querySelector("+J(selector)+");e.scrollIntoView({block:'center'});let r=e.getBoundingClientRect(),x=r.x+r.width/2,y=r.y+r.height/2,h=document.elementFromPoint(x,y);return {x,y,width:r.width,height:r.height,hit:e===h||e.contains(h),target:h?.tagName,targetClass:h?.className,disabled:!!e.disabled};})()");
    }
    private static async Task Pointer(WindowsCdpBrowser b,string sid,JsonElement hit)
    {
        double x=hit.GetProperty("x").GetDouble(),y=hit.GetProperty("y").GetDouble();
        await b.CommandAsync("Input.dispatchMouseEvent",new{type="mouseMoved",x,y},sid);
        await b.CommandAsync("Input.dispatchMouseEvent",new{type="mousePressed",x,y,button="left",clickCount=1},sid);
        await b.CommandAsync("Input.dispatchMouseEvent",new{type="mouseReleased",x,y,button="left",clickCount=1},sid);
    }
    [Fact,Trait("Gate","UiRepair")]
    public async Task ExtractedPointerConfigureMustBeReachable()
    {
        await using var h=new WindowsTestHost(WindowsTestHost.Package,WindowsTestHost.NewRoot());await h.InitializeAsync();
        await using var b=await WindowsCdpBrowser.StartAsync(Path.Combine(h.FixtureRoot,"ui-edge"));string sid=await b.NewPage();
        await b.CommandAsync("Emulation.setDeviceMetricsOverride",new{width=1440,height=900,deviceScaleFactor=1,mobile=false},sid);
        await b.Navigate(sid,new Uri(h.Origin,"/naval/").AbsoluteUri);
        await b.Wait(sid,"document.querySelector('#okx-status')?.textContent.includes('Brak konta')===true && document.querySelector('.rail')!==null","Original UI did not load");
        var hit=await Hit(b,sid,"#okx-operator button[data-action=configure]");
        await Screenshot(b,sid,"pointer-configure-before");await Pointer(b,sid,hit);
        await Task.Delay(750);
        var after=await b.Evaluate(sid,"({result:document.querySelector('#okx-result').textContent,hash:location.hash,box:document.querySelector('#okx-operator').getBoundingClientRect().toJSON(),rail:document.querySelector('.rail').getBoundingClientRect().toJSON()})");
        WindowsTestHost.Evidence("pointer-configure",new{hit,after,pointerInput=true,domClick=false,exeSha256=h.PayloadHash});
        await Screenshot(b,sid,"pointer-configure-after");
        Assert.True(hit.GetProperty("hit").GetBoolean(),"Configure center occluded: actual pointer is not delivered to the button");
        await h.CancelOkxConfigurationAsync();
        await b.Wait(sid,"document.querySelector('#okx-result').textContent.includes('NATIVE_CANCELLED')","Native cancellation not shown");
    }
}
