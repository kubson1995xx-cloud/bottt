using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Apex.Core.Bridge;
using Xunit;

namespace Apex.Windows.Tests;

[Collection("Extracted Windows")]
public sealed class WindowsBrowserAcceptanceTests
{
    private static string J(string value)=>JsonSerializer.Serialize(value);
    private static async Task CheckDom(WindowsCdpBrowser browser,WindowsTestHost host,string session,string stage)
    {
        // DOM bytes cross only the private CDP response in memory. Never log,
        // write, hash or attach them, or inject credentials into the page.
        string buffer=(await browser.Evaluate(session,"document.documentElement.outerHTML+'\\n'+Array.from(document.querySelectorAll('input,textarea,select')).map(e=>e.value).join('\\n')")).GetString()!;
        host.CheckPrivateBuffer(buffer);
        Assert.False(buffer.Contains("Authorization",StringComparison.OrdinalIgnoreCase),"Authorization in DOM; content withheld");
        WindowsTestHost.Evidence("privacy-dom-"+stage,new{stage,currentDomChecked=true,operatorAndReaderExcluded=true,bothDigestsExcluded=true,noDomRetained=true});
    }
    private static async Task<string> Authenticate(WindowsCdpBrowser b,WindowsTestHost h)
    {
        string sid=await b.NewPage();int challenges=0;
        b.OnEvent=async(name,p,session)=>
        {
            if(session!=sid)return;
            if(name=="Fetch.requestPaused")await b.CommandAsync("Fetch.continueRequest",new{requestId=p.GetProperty("requestId").GetString()},sid);
            if(name=="Fetch.authRequired")
            {
                Interlocked.Increment(ref challenges);
                await b.CommandAsync("Fetch.continueWithAuth",new{requestId=p.GetProperty("requestId").GetString(),authChallengeResponse=new{response="ProvideCredentials",username="operator",password=h.Credentials!.OperatorSecret}},sid);
            }
        };
        await b.CommandAsync("Fetch.enable",new{handleAuthRequests=true},sid);
        await b.Navigate(sid,new Uri(h.Origin,"/?view=naval").AbsoluteUri);
        await b.Wait(sid,"location.pathname==='/naval/' && document.querySelector('[data-testid=revision]')?.textContent !== 'UNKNOWN'","Authenticated production redirect/checkpoint not rendered");
        Assert.Equal(1,challenges);
        await b.CommandAsync("Fetch.disable",new{},sid);b.OnEvent=null;
        await b.CommandAsync("Page.reload",new{ignoreCache=true},sid);
        await b.Wait(sid,"document.querySelector('.connection.connected')!==null && document.querySelector('[data-testid=revision]')?.textContent !== 'UNKNOWN'","Native Basic cache failed after auth callback removed");
        WindowsTestHost.Evidence("native-basic-cache",new{challenges,callbackRemovedBeforeReload=true,fetchDisabled=true,fixedRedirect=true,pageInjectedAuthentication=false,cachedReloadRendered=true});
        return sid;
    }
    [Fact,Trait("Gate","Browser")]
    public async Task ProductionBrowserNativeCacheRolesFiveViewsAndOptional3D()
    {
        await using var h=new WindowsTestHost(WindowsTestHost.Package,WindowsTestHost.NewRoot());await h.InitializeAsync();
        WindowsTestHost.Evidence("privacy-negative-controls",WindowsPrivacyTests.Controls());
        await using var b=await WindowsCdpBrowser.StartAsync(Path.Combine(h.FixtureRoot,"edge"));string sid=await Authenticate(b,h);
        var snapshot=await h.Json("/api/naval/snapshot");
        using var export=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(h.PackageRoot,"APEX-Desktop","presentation","naval-publication.json")));
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(export.RootElement.GetProperty("publication").GetProperty("journal").GetProperty("checkpoint").GetRawText()),System.Text.Json.Nodes.JsonNode.Parse(snapshot.GetProperty("checkpoint").GetRawText())),"Served checkpoint differs from shipped genuine producer export");
        Assert.Equal("AVAILABLE",snapshot.GetProperty("availability").GetString());
        var views=new List<object>();
        foreach(var (route,title) in new[]{("command","Command Center"),("radar","Tactical Radar"),("fleets","Fleet Operations"),("animations","Trade Animations"),("analytics","Analytics Center")})
        {
            await b.Evaluate(sid,"location.hash="+J("#/"+route));
            await b.Wait(sid,"document.querySelector('main h1')?.textContent==="+J(title)+" && document.querySelector('[data-testid=revision]')?.textContent==="+J(snapshot.GetProperty("financialRevision").GetString()!),"View missing real historical checkpoint: "+route);
            Assert.True((await b.Evaluate(sid,"document.body.innerText.includes('NOT_CONFIGURED') && document.body.innerText.includes('SYNTHETIC')")).GetBoolean());
            await CheckDom(b,h,sid,route);
            views.Add(new{route,title,revision=snapshot.GetProperty("financialRevision").GetString(),historicalState=true});
        }
        // Exact HTTP asset map, then actual browser lazy Script response and its bytes.
        string manifest=Path.Combine(h.PackageRoot,"APEX-Desktop","wwwroot","naval","build-manifest.json");using var assets=JsonDocument.Parse(File.ReadAllBytes(manifest));var parity=new List<object>();
        foreach(var asset in assets.RootElement.GetProperty("artifacts").EnumerateArray())
        {
            string path=asset.GetProperty("path").GetString()!;using var response=await h.Send("/naval/"+path);Assert.Equal(HttpStatusCode.OK,response.StatusCode);byte[] bytes=await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(asset.GetProperty("sha256").GetString(),Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());Assert.Equal(asset.GetProperty("bytes").GetInt64(),bytes.LongLength);parity.Add(new{path,sha256=asset.GetProperty("sha256").GetString(),bytes=bytes.Length});
        }
        await b.Evaluate(sid,"location.hash='#/fleets'");await b.Wait(sid,"document.querySelector('.scene-status')!==null","Scene viewport missing");
        await b.Wait(sid,"document.querySelector('.scene-status')?.textContent.includes('3D historical') || document.querySelector('.scene-status')?.textContent.includes('FAILED') || document.querySelector('.scene-status')?.textContent.includes('NO_WEBGL2')","Scene did not settle");
        var gpu=await b.Evaluate(sid,"(()=>{const c=document.querySelector('canvas.naval-canvas');const g=c?.getContext('webgl2');if(!g)return {available:false};const e=g.getExtension('WEBGL_debug_renderer_info');return {available:true,version:g.getParameter(g.VERSION),renderer:e?g.getParameter(e.UNMASKED_RENDERER_WEBGL):g.getParameter(g.RENDERER)};})()");
        var scripts=b.Events.Where(e=>e.Method=="Network.responseReceived"&&e.Session==sid&&e.Data.GetProperty("type").GetString()=="Script").ToArray();
        var lazy=scripts.FirstOrDefault(e=>e.Data.GetProperty("response").GetProperty("url").GetString()!.Contains("/naval/assets/chunk-"));Assert.NotEqual(default,lazy);
        string lazyUrl=lazy.Data.GetProperty("response").GetProperty("url").GetString()!;var lazyBody=await b.CommandAsync("Network.getResponseBody",new{requestId=lazy.Data.GetProperty("requestId").GetString()},sid);
        byte[] actual=lazyBody.GetProperty("base64Encoded").GetBoolean()?Convert.FromBase64String(lazyBody.GetProperty("body").GetString()!):Encoding.UTF8.GetBytes(lazyBody.GetProperty("body").GetString()!);
        Assert.Equal(WindowsTestHost.Hash(Path.Combine(h.PackageRoot,"APEX-Desktop","wwwroot",new Uri(lazyUrl).AbsolutePath.TrimStart('/').Replace('/',Path.DirectorySeparatorChar))),Convert.ToHexString(SHA256.HashData(actual)).ToLowerInvariant());
        // Real production preferences; minimal never hides the SVG/text data.
        await b.Evaluate(sid,"Array.from(document.querySelectorAll('label.preference')).find(e=>e.textContent.includes('Minimal')).querySelector('input').click()");
        await b.Wait(sid,"document.querySelector('.naval-app.minimal')!==null && document.querySelector('canvas.naval-canvas')===null && document.querySelector('.scene-fallback svg')!==null","Minimal SVG/list fallback failed");
        await b.CommandAsync("Emulation.setEmulatedMedia",new{features=new[]{new{name="prefers-reduced-motion",value="reduce"}}},sid);
        await b.Wait(sid,"document.querySelector('.naval-app.reduced-motion')!==null","Reduced-motion path failed");
        await b.Evaluate(sid,"Array.from(document.querySelectorAll('label.preference')).find(e=>e.textContent.includes('Minimal')).querySelector('input').click()");
        // Test capability denial only, never auth or product state. Reload same document
        // with getContext returning null, restore prototype, click production Retry in same lane.
        string hook="window.__task6OriginalContext=HTMLCanvasElement.prototype.getContext;HTMLCanvasElement.prototype.getContext=function(t,...a){return t==='webgl2'?null:window.__task6OriginalContext.call(this,t,...a)}";
        var injected=await b.CommandAsync("Page.addScriptToEvaluateOnNewDocument",new{source=hook},sid);await b.CommandAsync("Page.reload",new{ignoreCache=true},sid);
        await b.Wait(sid,"document.querySelector('.scene-fallback svg')!==null && Array.from(document.querySelectorAll('button')).some(b=>b.textContent==='Retry 3D')","No-WebGL fallback/Retry missing");
        await b.Evaluate(sid,"HTMLCanvasElement.prototype.getContext=window.__task6OriginalContext;delete window.__task6OriginalContext;Array.from(document.querySelectorAll('button')).find(b=>b.textContent==='Retry 3D').click()");
        if(gpu.GetProperty("available").GetBoolean())await b.Wait(sid,"document.querySelector('.scene-status')?.textContent.includes('3D historical')===true","Capability restored but same-lane Retry failed");
        else await b.Wait(sid,"document.querySelector('.scene-fallback svg')!==null","Unavailable WebGL must retain SVG");
        await b.CommandAsync("Page.removeScriptToEvaluateOnNewDocument",new{identifier=injected.GetProperty("identifier").GetString()},sid);
        await CheckDom(b,h,sid,"restored-retry");
        // A new isolated context has no inherited Basic cache. Only reader Bearer;
        // exactly the two documented GET APIs, never documents/modules/styles.
        string reader=await b.NewPage();await b.CommandAsync("Network.setExtraHTTPHeaders",new{headers=new{Authorization="Bearer "+h.Credentials!.ReaderSecret}},reader);
        await b.Navigate(reader,new Uri(h.Origin,"/naval/").AbsoluteUri);
        string css=parity.Select(x=>JsonSerializer.SerializeToElement(x).GetProperty("path").GetString()!).First(p=>p.EndsWith(".css"));
        string module=parity.Select(x=>JsonSerializer.SerializeToElement(x).GetProperty("path").GetString()!).First(p=>p.EndsWith(".js"));
        string eventsPath="/api/naval/events?hostEpoch="+Uri.EscapeDataString(snapshot.GetProperty("hostEpoch").GetString()!)+"&cursor="+Uri.EscapeDataString(snapshot.GetProperty("cursor").GetString()!);
        string[] paths={"/api/naval/snapshot",eventsPath,"/","/naval/","/naval/"+module,"/naval/"+css};
        var statuses=await b.Evaluate(reader,"Promise.all("+JsonSerializer.Serialize(paths)+".map(async p=>({path:p,status:(await fetch(p)).status})))",true);
        Assert.Equal(new[]{200,200,403,403,403,403},statuses.EnumerateArray().Select(s=>s.GetProperty("status").GetInt32()).ToArray());
        await b.CommandAsync("Network.setExtraHTTPHeaders",new{headers=new{}},reader);
        var cleared=await b.Evaluate(reader,"fetch('/api/naval/snapshot',{credentials:'omit'}).then(r=>r.status)",true);Assert.Equal(401,cleared.GetInt32());
        await b.Navigate(sid,new Uri(h.Origin,"/").AbsoluteUri);await b.Wait(sid,"document.querySelector('meta[name=apex-token]')!==null","Cached Basic original panel failed");
        Assert.True((await b.Evaluate(sid,"document.querySelector('meta[name=apex-token]').content===''")).GetBoolean());
        await CheckDom(b,h,sid,"original-panel");
        WindowsTestHost.Evidence("windows-browser",new{views,checkpointMatchesProducer=true,assets=parity,lazyScript=true,lazyScriptHashMatched=true,webgl=gpu,physicalGpuProof=false,minimalFallback=true,reducedMotion=true,noWebglFallback=true,sameLaneRestoredRetry=gpu.GetProperty("available").GetBoolean(),reader=statuses,clearedAuthStatus=401,oldPanelEmptyToken=true,noPageInjectedAuth=true});
    }
    [Fact,Trait("Gate","Browser")]
    public async Task H503TextEmergencyOwn77Foreign78DurableRestart()
    {
        await using var h=new WindowsTestHost(WindowsTestHost.Package,WindowsTestHost.NewRoot());await h.InitializeAsync();
        await using var b=await WindowsCdpBrowser.StartAsync(Path.Combine(h.FixtureRoot,"edge"));string sid=await Authenticate(b,h);
        var original=await h.Json("/api/naval/snapshot");string publication=Path.Combine(h.DataRoot,"publication","naval-publication.json");byte[] good=File.ReadAllBytes(publication);
        await using(var peer=new SyntheticPeer(h))
        {
            await peer.Connect();string key=await peer.Ready();
            File.WriteAllText(publication,"{");using var bad=await h.Send("/api/naval/snapshot");Assert.Equal(HttpStatusCode.ServiceUnavailable,bad.StatusCode);
            await b.CommandAsync("Page.reload",new{},sid);await b.Wait(sid,"document.querySelector('.unavailable')!==null","H503 unavailable UI not shown");
            await CheckDom(b,h,sid,"h503");
            await b.Evaluate(sid,"Array.from(document.querySelectorAll('a')).find(a=>a.getAttribute('href')==='/').click()");
            await b.Wait(sid,"Array.from(document.querySelectorAll('button')).some(b=>b.textContent.includes('AWARYJNY STOP'))","Text Emergency missing after static old-panel link");
            var decisions=await h.Send("/api/accounts/"+Uri.EscapeDataString(key)+"/decisions");Assert.Equal(HttpStatusCode.OK,decisions.StatusCode);decisions.Dispose();
            using var csv=await h.Send("/api/accounts/"+Uri.EscapeDataString(key)+"/journal.csv");Assert.Equal(HttpStatusCode.OK,csv.StatusCode);
            await b.Evaluate(sid,"Array.from(document.querySelectorAll('button')).find(b=>b.textContent.includes('AWARYJNY STOP')).click()");
            await b.Wait(sid,"document.querySelector('[role=dialog]')?.textContent.includes('AWARYJNE ZATRZYMANIE')===true","Original panel confirmation not shown");
            await CheckDom(b,h,sid,"emergency-confirmation");
            Assert.Equal(0,peer.Closed);
            await b.Evaluate(sid,"Array.from(document.querySelectorAll('[role=dialog] button')).find(b=>b.textContent==='ZATRZYMAJ AWARYJNIE').click()");
            await WindowsTestHost.Until(async()=>{var status=await h.Json("/api/status");return status.GetProperty("data").GetProperty("accounts")[0].GetProperty("engine").GetProperty("mode").GetString()=="EmergencyStopped";},"Emergency latch not published");
            Assert.Equal(1,peer.Closed);Assert.Empty(peer.RiskCommands);
        }
        string control=Directory.GetFiles(Path.Combine(h.DataRoot,"konta"),"desktop_control.json",SearchOption.AllDirectories).Single();byte[] latch=File.ReadAllBytes(control);using(var d=JsonDocument.Parse(latch))Assert.True(d.RootElement.GetProperty("EmergencyStopped").GetBoolean());
        File.WriteAllBytes(publication,good);await h.StopAsync();await h.StartAsync(new[]{"--data",h.DataRoot,"--no-browser"});await h.ReadyAsync();
        var restarted=await h.Json("/api/naval/snapshot");Assert.NotEqual(original.GetProperty("hostEpoch").GetString(),restarted.GetProperty("hostEpoch").GetString());Assert.Equal(original.GetProperty("checkpoint").GetRawText(),restarted.GetProperty("checkpoint").GetRawText());
        await using(var peer=new SyntheticPeer(h)){await peer.Connect();await peer.Ready();var status=await h.Json("/api/status");Assert.Equal("EmergencyStopped",status.GetProperty("data").GetProperty("accounts")[0].GetProperty("engine").GetProperty("mode").GetString());Assert.Equal(latch,File.ReadAllBytes(control));Assert.Empty(peer.RiskCommands);}
        WindowsTestHost.Evidence("emergency-browser",new{syntheticLocalPeerOnly=true,h503=true,staticOldPanelLink=true,originalPanelConfirmation=true,closedPosition=77,fullClose=true,foreign78Untouched=true,noOpenTestModify=true,decisions200=true,csv200=true,durableLatch=true,restartNewHostEpoch=true,checkpointUnchanged=true,brokerTested=false});
    }
    private sealed class SyntheticPeer:IAsyncDisposable
    {
        private readonly WindowsTestHost h;private readonly ClientWebSocket ws=new();private readonly CancellationTokenSource stop=new(TimeSpan.FromSeconds(100));private Task? pump;internal int Closed;internal readonly List<string> RiskCommands=new();
        private readonly AccountDto account=new(){Number=930006,Broker="SYNTHETIC_TASK6",Currency="USD",Balance=10000,Equity=10000,FreeMargin=10000};
        internal SyntheticPeer(WindowsTestHost h){this.h=h;}
        private async Task Send<T>(T message)=>await ws.SendAsync(Encoding.UTF8.GetBytes(BridgeProtocol.Serialize(message)),WebSocketMessageType.Text,true,stop.Token);
        internal async Task Connect()
        {
            using var config=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(h.DataRoot,"apex.config.json")));await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{h.Port}/bridge"),stop.Token);
            await Send(new HelloMsg{Key=config.RootElement.GetProperty("PairingKey").GetString()!,Platform="cTrader",Account=account});var welcome=await Receive();Assert.True(welcome.GetProperty("accepted").GetBoolean());
            await Send(new SymbolsMsg());await Send(new AccountMsg{Account=account});
            await Send(new PositionsMsg{Items=new(){new PositionDto{Id=77,Symbol="US500",Side="buy",Label="APEX",Entry=5000,Price=5000,Sl=4900,Tp=5300,Units=.1},new PositionDto{Id=78,Symbol="US500",Side="buy",Label="MANUAL",Entry=5000,Price=5000,Sl=4900,Tp=5300,Units=.1}}});
            pump=Task.Run(async()=>{try{while(!stop.IsCancellationRequested){var m=await Receive();string type=m.GetProperty("type").GetString()!;if(type=="close"){Assert.Equal(77,m.GetProperty("positionId").GetInt64());Assert.True(!m.TryGetProperty("units",out var u)||u.ValueKind==JsonValueKind.Null);Interlocked.Increment(ref Closed);await Send(new ResultMsg{Id=m.GetProperty("id").GetString()!,Status="filled",PositionId=77,Price=5000});}else if(type is "open" or "modify" or "executionTest" or "test"){RiskCommands.Add(type);throw new Exception("Risk-increasing synthetic command refused");}}}catch(Exception e)when(e is OperationCanceledException or WebSocketException){} });
        }
        private async Task<JsonElement> Receive(){var bytes=new byte[65536];using var stream=new MemoryStream();WebSocketReceiveResult r;do{r=await ws.ReceiveAsync(bytes,stop.Token);if(r.MessageType==WebSocketMessageType.Close)throw new WebSocketException();stream.Write(bytes,0,r.Count);Assert.True(stream.Length<1024*1024);}while(!r.EndOfMessage);using var d=JsonDocument.Parse(stream.ToArray());return d.RootElement.Clone();}
        internal async Task<string> Ready(){string? key=null;await WindowsTestHost.Until(async()=>{var d=await h.Json("/api/status");var a=d.GetProperty("data").GetProperty("accounts");if(a.GetArrayLength()==1&&a[0].GetProperty("engineReady").GetBoolean()){key=a[0].GetProperty("key").GetString();return true;}return false;},"Synthetic peer engine not ready");return key!;}
        public async ValueTask DisposeAsync(){stop.Cancel();ws.Abort();if(pump!=null)await pump;ws.Dispose();stop.Dispose();Assert.Empty(RiskCommands);}
    }
}
