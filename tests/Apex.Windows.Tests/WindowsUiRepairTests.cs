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
        // Closed native <details> uses content-visibility: descendants can retain
        // nonzero layout boxes while not paintable. Open ancestors with real input,
        // never by mutating .open, before measuring a nested disclosure control.
        string token=Guid.NewGuid().ToString("N");
        var ancestors=await b.Evaluate(sid,"(()=>{let e=document.querySelector("+J(selector)+"),a=[],p=e?.parentElement;while(p){if(p.tagName==='DETAILS'&&!p.open&&p.querySelector(':scope>summary')!==e){let id="+J(token)+"+'-'+a.length;p.setAttribute('data-ui-open',id);a.push('details[data-ui-open=\"'+id+'\"]>summary');}p=p.parentElement;}return a.reverse();})()");
        foreach(var ancestor in ancestors.EnumerateArray()){var a=await Hit(b,sid,ancestor.GetString()!);Assert.True(a.GetProperty("hit").GetBoolean(),"Closed disclosure ancestor not reachable");await Pointer(b,sid,a);}
        return await b.Evaluate(sid,"(()=>{let e=document.querySelector("+J(selector)+");e.scrollIntoView({block:'center'});let r=e.getBoundingClientRect(),x=r.x+r.width/2,y=r.y+r.height/2,h=document.elementFromPoint(x,y);return {x,y,width:r.width,height:r.height,visible:e.checkVisibility({checkVisibilityCSS:true}),hit:e===h||e.contains(h),target:h?.tagName,targetClass:h?.className,disabled:!!e.disabled};})()");
    }
    private static async Task Pointer(WindowsCdpBrowser b,string sid,JsonElement hit)
    {
        double x=hit.GetProperty("x").GetDouble(),y=hit.GetProperty("y").GetDouble();
        await b.CommandAsync("Input.dispatchMouseEvent",new{type="mouseMoved",x,y},sid);
        await b.CommandAsync("Input.dispatchMouseEvent",new{type="mousePressed",x,y,button="left",clickCount=1},sid);
        await b.CommandAsync("Input.dispatchMouseEvent",new{type="mouseReleased",x,y,button="left",clickCount=1},sid);
    }
    private static async Task<JsonElement> Click(WindowsCdpBrowser b,string sid,string selector)
    {
        var hit=await Hit(b,sid,selector);Assert.True(hit.GetProperty("hit").GetBoolean(),"Pointer target occluded: "+selector);await Pointer(b,sid,hit);return hit;
    }
    private static async Task Key(WindowsCdpBrowser b,string sid,string key,int code)
    {
        await b.CommandAsync("Input.dispatchKeyEvent",new{type="keyDown",key,code=key,windowsVirtualKeyCode=code,nativeVirtualKeyCode=code,text=key=="Enter"?"\r":""},sid);await b.CommandAsync("Input.dispatchKeyEvent",new{type="keyUp",key,code=key,windowsVirtualKeyCode=code,nativeVirtualKeyCode=code},sid);
    }
    private static object Diagnostics(WindowsCdpBrowser b,string sid)=>new{
        exceptions=b.Events.Count(e=>e.Session==sid&&e.Method=="Runtime.exceptionThrown"),
        console=b.Events.Where(e=>e.Session==sid&&e.Method=="Runtime.consoleAPICalled").Select(e=>new{type=e.Data.GetProperty("type").GetString()}).ToArray(),
        responses=b.Events.Where(e=>e.Session==sid&&e.Method=="Network.responseReceived").Select(e=>new{path=new Uri(e.Data.GetProperty("response").GetProperty("url").GetString()!).AbsolutePath,status=e.Data.GetProperty("response").GetProperty("status").GetInt32()}).ToArray()};
    [Fact,Trait("Gate","EnrollmentOriginal")]
    public async Task MetadataApprovalFailureMustNotBeReportedAsUserCancellation()
    {
        await using var h=new WindowsTestHost(WindowsTestHost.Package,WindowsTestHost.NewRoot());await h.InitializeAsync();
        await using var b=await WindowsCdpBrowser.StartAsync(Path.Combine(h.FixtureRoot,"enrollment-edge"));string sid=await b.NewPage();
        await b.Navigate(sid,new Uri(h.Origin,"/naval/").AbsoluteUri);await b.Wait(sid,"document.querySelector('#okx-operator')!==null","Operator missing");
        await Click(b,sid,"[data-action=configure]");var w=await WindowsNativeUi.Metadata(h.Pid);
        foreach(var(id,text)in new[]{(100,"DEMO"),(101,"native-proof"),(102,"123456"),(103,"EUR")})WindowsNativeUi.Text(w,id,text);
        WindowsNativeUi.Click(w,1);await WindowsTestHost.Until(()=>Task.FromResult(!WindowsNativeUi.Visible(w)),"Metadata did not close");
        var deadline=DateTime.UtcNow.AddSeconds(130);JsonElement state=default;
        do{state=await h.Json("/api/naval/okx/status");if(state.GetProperty("lastResult").GetString()!="NOT_CONNECTED")break;await Task.Delay(100);}while(DateTime.UtcNow<deadline);
        string? result=state.GetProperty("lastResult").GetString();
        WindowsTestHost.Evidence("original-enrollment-boundary",new{result,stage=state.TryGetProperty("enrollmentStage",out var stage)?stage.GetString():null,metadataAccepted=true,userCancelled=false,secretInputEntered=false,credentialsSaved=Directory.Exists(Path.Combine(h.DataRoot,"okx")),exeSha256=h.PayloadHash});
        await h.StopAsync();Assert.NotEqual("NATIVE_CANCELLED",result);Assert.StartsWith("APPROVAL_",result);
        await Screenshot(b,sid,"enrollment-protected-outcome");
    }
    [Fact,Trait("Gate","UiNavigation")]
    public async Task OperatorReturnNavigationMustRemainPointerReachable()
    {
        await using var h=new WindowsTestHost(WindowsTestHost.Package,WindowsTestHost.NewRoot());await h.InitializeAsync();await using var b=await WindowsCdpBrowser.StartAsync(Path.Combine(h.FixtureRoot,"navigation-edge"));string sid=await b.NewPage();var records=new List<object>();
        try{foreach(var(width,height)in new[]{(1440,900),(900,650),(640,800)}){
            await b.CommandAsync("Emulation.setDeviceMetricsOverride",new{width,height,deviceScaleFactor=1,mobile=false},sid);await b.Navigate(sid,new Uri(h.Origin,"/naval/").AbsoluteUri);await b.Wait(sid,"document.querySelector('#okx-operator-slot #okx-operator')!==null","Operator did not mount");
            var hit=await Hit(b,sid,".rail-bottom button");records.Add(new{width,height,hit});await Screenshot(b,sid,width+"x"+height+"-return-navigation");Assert.True(hit.GetProperty("hit").GetBoolean(),"Constrained return-to-OKX navigation is hidden/clipped");await Pointer(b,sid,hit);await b.Wait(sid,"document.activeElement?.id==='okx-operator'","Operator return did not focus actual panel");
        }}finally{WindowsTestHost.Evidence("ui-navigation",new{records,diagnostics=Diagnostics(b,sid)});}
    }
    [Fact,Trait("Gate","UiUpgrade")]
    public async Task PreviousExeProfilePreservedByFreshUiAndRestart()
    {
        await using var h=new WindowsTestHost(WindowsTestHost.Package,WindowsTestHost.NewRoot());h.Port=WindowsTestHost.FreePort();
        await h.StartAsync(new[]{"--data",h.DataRoot,"--port",h.Port.ToString(),"--no-browser"},legacyGenerator:true);await h.ReadyAsync();
        h.Credentials=Apex.Desktop.Services.WindowsLocalAccess.Load(h.DataRoot,"presentation");await h.StopAsync();
        var before=WindowsTestHost.Inventory(h.DataRoot).Where(p=>!p.Key.StartsWith("logi/",StringComparison.Ordinal)).ToDictionary(p=>p.Key,p=>p.Value);
        await using var b=await WindowsCdpBrowser.StartAsync(Path.Combine(h.FixtureRoot,"upgrade-ui-edge"));string sid=await b.NewPage();
        for(int n=0;n<2;n++){
            await h.StartAsync(new[]{"--data",h.DataRoot,"--port",h.Port.ToString(),"--no-browser"});await h.ReadyAsync();await b.Navigate(sid,new Uri(h.Origin,"/naval/").AbsoluteUri);
            await b.Wait(sid,"document.querySelector('#okx-operator-slot #okx-operator')!==null && document.querySelector('.connection.connected')!==null","Preserved prior EXE profile UI failed");
            foreach(string route in new[]{"command","radar","fleets","animations","analytics"}){await Click(b,sid,".rail nav a[href='#/"+route+"']");await b.Wait(sid,"location.hash==="+J("#/"+route),"Preserved profile navigation failed");}
            await Screenshot(b,sid,"preserved-profile-"+n);await h.StopAsync();
            await WindowsTestHost.Until(()=>{try{using var l=new FileStream(Path.Combine(h.DataRoot,"apex.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None);return Task.FromResult(true);}catch(IOException){return Task.FromResult(false);}},"Preserved profile lock did not release");
            var after=WindowsTestHost.Inventory(h.DataRoot);foreach(var pair in before)Assert.True(after.TryGetValue(pair.Key,out var hash)&&hash==pair.Value,"Private profile bytes changed; contents withheld");
        }
        Assert.Equal(0,b.AuthenticationCommands);Assert.Equal(0,b.AuthorizationRequests);Assert.Equal(0,b.BasicChallenges);
        WindowsTestHost.Evidence("ui-preserved-profile",new{originalExeSha256=Environment.GetEnvironmentVariable("APEX_LEGACY_EXE_SHA256"),newExeSha256=h.PayloadHash,preservedFiles=before.Count,twoStartsFiveViews=true,noLogin=true,diagnostics=Diagnostics(b,sid)});
    }
    [Fact,Trait("Gate","UiInventory")]
    public async Task AllViewsControlsAndOperatorPointerInventory()
    {
        await using var h=new WindowsTestHost(WindowsTestHost.Package,WindowsTestHost.NewRoot());await h.InitializeAsync();
        await using var b=await WindowsCdpBrowser.StartAsync(Path.Combine(h.FixtureRoot,"inventory-edge"));string sid=await b.NewPage();
        var records=new List<object>();
        try{
            using(var inputs=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(h.PackageRoot,"Weryfikacja","source-inputs.json"))))foreach(string name in new[]{"okx-controls.js","okx-controls.css"}){
                using var response=await h.Send("/"+name);Assert.Equal(System.Net.HttpStatusCode.OK,response.StatusCode);byte[] payload=await response.Content.ReadAsByteArrayAsync();string hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant();string path="src/Apex.Desktop/Okx/"+name;var expected=inputs.RootElement.GetProperty("files").EnumerateArray().Single(f=>f.GetProperty("path").GetString()==path);Assert.Equal(expected.GetProperty("sha256").GetString(),hash);records.Add(new{embeddedAsset=name,sha256=hash,bytes=payload.Length,status=200});
            }
            // Owned nonsecret metadata fixture ONLY: zero key envelope, private connection or trade.
            await h.StopAsync();string config=Path.Combine(h.DataRoot,"apex.config.json");var c=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(config))!;
            c["OkxNaval"]=System.Text.Json.Nodes.JsonNode.Parse("{\"Schema\":\"okx-naval-config-v1\",\"Scopes\":[{\"ScopeId\":\"ui-demo\",\"Environment\":\"OkxDemo\",\"Region\":\"EEA\",\"ExpectedUid\":\"123456\",\"ExpectedMainUid\":\"123456\",\"QuoteCurrency\":\"EUR\",\"CredentialBindingId\":\"no-credential\",\"SafetyVersion\":\"okx-cash-v1\"},{\"ScopeId\":\"ui-demo-two\",\"Environment\":\"Live\",\"Region\":\"EEA\",\"ExpectedUid\":\"234567\",\"ExpectedMainUid\":\"234567\",\"QuoteCurrency\":\"EUR\",\"CredentialBindingId\":\"no-credential-two\",\"SafetyVersion\":\"okx-cash-v1\"}]}");
            File.WriteAllText(config,c.ToJsonString());await h.StartAsync(new[]{"--data",h.DataRoot,"--no-browser"});await h.ReadyAsync();
            foreach(var (width,height,scale) in new[]{(1440,900,1d),(900,650,1.25d),(640,800,1.25d)}){
                string viewport=width+"x"+height;
                await b.CommandAsync("Emulation.setDeviceMetricsOverride",new{width,height,deviceScaleFactor=scale,mobile=false},sid);
                await b.Navigate(sid,new Uri(h.Origin,"/naval/").AbsoluteUri);
                await b.Wait(sid,"document.querySelector('#okx-scope')?.value==='ui-demo' && document.querySelector('.connection.connected')!==null","Actual host state did not render");
                var layout=await b.Evaluate(sid,"(()=>{let p=document.querySelector('#okx-operator').getBoundingClientRect(),r=document.querySelector('.rail').getBoundingClientRect();return {horizontalOverflow:document.documentElement.scrollWidth>innerWidth,overlap:Math.min(p.right,r.right)>Math.max(p.left,r.left)&&Math.min(p.bottom,r.bottom)>Math.max(p.top,r.top),slot:document.querySelector('#okx-operator').parentElement.id,globalReadOnly:document.body.textContent.includes('READ-ONLY APPLICATION')};})()");
                records.Add(new{viewport,layout});Assert.False(layout.GetProperty("overlap").GetBoolean());Assert.False(layout.GetProperty("horizontalOverflow").GetBoolean());Assert.False(layout.GetProperty("globalReadOnly").GetBoolean());
                await Screenshot(b,sid,viewport+"-operator");
                await Click(b,sid,"[data-action=configure]");var form=await WindowsNativeUi.Metadata(h.Pid);records.Add(new{viewport,action="configure",native=WindowsNativeUi.Observe(form,h.Pid)});WindowsNativeUi.ScreenshotMetadata(form,viewport+"-native-configure");WindowsNativeUi.Cancel(form);await b.Wait(sid,"document.querySelector('#okx-result').textContent.includes('METADATA_USER_CANCELLED')","Constrained native Cancel did not return");await b.CommandAsync("Page.bringToFront",new{},sid);
                await Click(b,sid,"#okx-scope");await Key(b,sid,"ArrowDown",40);await Key(b,sid,"Enter",13);
                await b.Wait(sid,"document.querySelector('#okx-scope').value==='ui-demo-two'","Pointer/keyboard scope selection failed");records.Add(new{viewport,control="okx-scope",result="ui-demo-two; selection only, no connect"});
                await Click(b,sid,"#okx-scope");await Key(b,sid,"ArrowUp",38);await Key(b,sid,"Enter",13);
                await b.Wait(sid,"document.querySelector('#okx-scope').value==='ui-demo'","DEMO must be selected before any command; inactive synthetic LIVE metadata is never activated");
                foreach(var(action,expected) in new[]{("diagnose","METADATA_ONLY_NO_KEY"),("test-demo","DEMO_TEST_CREDENTIAL_OR_LOCAL_READ_REFUSED"),("connect","LOCAL_ACTION_REFUSED_PRESERVED"),("baseline","BASELINE_REFUSED_PRESERVED"),("arm","BASELINE_REQUIRED"),("start","BASELINE_REQUIRED"),("stop","OPERATOR_STOP"),("emergency","EMERGENCY_STOP_NO_OPEN_OWNER"),("disconnect","DISCONNECTED_STOP")}){
                    await b.Wait(sid,"!document.querySelector('[data-action="+action+"]').disabled","Action remained busy: "+action);
                    var hit=await Click(b,sid,"#okx-operator button[data-action="+action+"]");await b.Wait(sid,"document.querySelector('#okx-result').textContent.includes("+J(expected)+")","Missing control result: "+action);records.Add(new{viewport,action,hit,result=expected});
                }
                var disabled=await b.Evaluate(sid,"({recover:document.querySelector('[data-action=recover]').disabled,recoverySelector:document.querySelector('#okx-reservation').disabled,reason:document.querySelector('[data-action=recover]').title})");records.Add(new{viewport,action="recover",result=disabled});Assert.True(disabled.GetProperty("recover").GetBoolean());Assert.True(disabled.GetProperty("recoverySelector").GetBoolean());
                await Click(b,sid,"[data-action=refresh]");await b.Wait(sid,"!document.querySelector('[data-action=refresh]').disabled","Manual refresh did not finish");
                await b.CommandAsync("Network.setBlockedURLs",new{urls=new[]{"*/api/naval/okx/status*"}},sid);
                await b.Wait(sid,"document.querySelector('#okx-status').textContent.includes('Status niedostępny')","Network status failure not surfaced");
                await Click(b,sid,"[data-action=stop]");await b.Wait(sid,"document.querySelector('#okx-result').textContent.includes('OPERATOR_STOP')","STOP must not wait on failed status");
                records.Add(new{viewport,fault="actual browser network status denial",result="visible error; risk controls disabled; independent STOP replied"});
                await b.CommandAsync("Network.setBlockedURLs",new{urls=Array.Empty<string>()},sid);await Click(b,sid,"[data-action=refresh]");await b.Wait(sid,"!document.querySelector('[data-action=start]').disabled","Restored actual status did not recover controls");
                foreach(var(route,title) in new[]{("command","Command Center"),("radar","Tactical Radar"),("fleets","Fleet Operations"),("animations","Trade Animations"),("analytics","Analytics Center")}){
                    await Click(b,sid,".rail nav a[href='#/"+route+"']");await b.Wait(sid,"document.querySelector('main h1')?.textContent==="+J(title),"Pointer navigation failed: "+route);
                    await b.Wait(sid,"(()=>{let s=document.querySelector('.scene-status')?.textContent;return !s||s.includes('3D historical')||s.includes('_FAILED')||s.includes('CONTEXT_LOST')||s.includes('NO_WEBGL2')||!!document.querySelector('.naval-app.minimal');})()","Optional renderer did not settle before control inventory");
                    records.Add(new{viewport,route,renderer=await b.Evaluate(sid,"document.querySelector('.scene-status')?.textContent??'not present'")});
                    await b.Evaluate(sid,"document.querySelector('main').scrollIntoView({block:'start'})");await Screenshot(b,sid,viewport+"-"+route);
                    // Enumerate every currently present view control. Pure test locator attributes,
                    // no event handlers/state/globals injected; every action uses CDP input.
                    var controls=await b.Evaluate(sid,"Array.from(document.querySelectorAll('main button,main summary,main [role=button]')).map((e,i)=>{e.setAttribute('data-ui-probe',String(i));return {id:i,label:(e.getAttribute('aria-label')||e.textContent).trim(),disabled:!!e.disabled};})");
                    foreach(var item in controls.EnumerateArray().Reverse()){
                        string selector="[data-ui-probe='"+item.GetProperty("id").GetInt32()+"']";
                        Assert.True((await b.Evaluate(sid,"document.querySelector("+J(selector)+")!==null")).GetBoolean(),"Inventory control lost before its pointer action: "+route+" "+item.GetProperty("label").GetString());
                        var hit=await Hit(b,sid,selector);records.Add(new{viewport,route,item,probe=hit});if(!hit.GetProperty("visible").GetBoolean()||hit.GetProperty("width").GetDouble()==0||hit.GetProperty("height").GetDouble()==0){records.Add(new{viewport,route,item,result="not rendered (optional capability / closed details)"});continue;}
                        if(!hit.GetProperty("hit").GetBoolean())await Screenshot(b,sid,viewport+"-"+route+"-failed-hit");
                        Assert.True(hit.GetProperty("hit").GetBoolean(),"View control occluded: "+route+" "+item.GetProperty("label").GetString());await Pointer(b,sid,hit);await Task.Delay(40);
                        var after=await b.Evaluate(sid,"(()=>{let e=document.querySelector("+J(selector)+");return {pressed:e?.getAttribute('aria-pressed'),disabled:!!e?.disabled,detailsOpen:e?.parentElement?.open??null};})()");records.Add(new{viewport,route,item,hit,after});
                    }
                    var toolbar=await b.Evaluate(sid,"Array.from(document.querySelectorAll('.toolbar input,.toolbar select,.toolbar button')).map((e,i)=>{e.setAttribute('data-toolbar-probe',String(i));return {id:i,tag:e.tagName,label:e.parentElement.textContent.trim()};})");
                    foreach(var item in toolbar.EnumerateArray()){
                        string selector="[data-toolbar-probe='"+item.GetProperty("id").GetInt32()+"']";await Click(b,sid,selector);
                        if(item.GetProperty("tag").GetString()=="SELECT"){await Key(b,sid,"ArrowDown",40);await Key(b,sid,"Enter",13);await Click(b,sid,selector);await Key(b,sid,"Home",36);await Key(b,sid,"Enter",13);}
                        else if(item.GetProperty("tag").GetString()=="INPUT")await Click(b,sid,selector);
                        records.Add(new{viewport,route,toolbar=item,result="real pointer; input toggled/restored or resync completed"});
                    }
                    await b.Wait(sid,"document.querySelector('.connection.connected')!==null","Resync did not recover");
                }
                await Click(b,sid,".source-foot summary");records.Add(new{viewport,navigation="source provenance disclosure",result="pointer toggled"});
                var operatorNav=await Hit(b,sid,".rail-bottom button");records.Add(new{viewport,navigation="Naval to OKX",hit=operatorNav});await Screenshot(b,sid,viewport+"-operator-navigation-hit");Assert.True(operatorNav.GetProperty("hit").GetBoolean(),"OKX return navigation must remain pointer reachable at constrained width/height");await Pointer(b,sid,operatorNav);
                await b.Wait(sid,"document.activeElement?.id==='okx-operator'","OKX navigation did not focus panel");await Click(b,sid,"#okx-operator nav button");await b.Wait(sid,"document.activeElement?.id==='main'","OKX back to current Naval view failed");
                await Click(b,sid,"#okx-operator details>summary");await Click(b,sid,"#okx-operator details>summary");records.Add(new{viewport,navigation="OKX collapse/expand and return Naval",result="pointer PASS"});
                await b.Reload(sid);await b.Wait(sid,"document.querySelector('#okx-operator-slot #okx-operator')!==null && document.querySelector('.connection.connected')!==null","Reload broke integration");
                await Click(b,sid,"#okx-operator nav a[href='/']");await b.Wait(sid,"document.querySelector('.app')!==null && document.querySelector('#okx-operator')!==null","Old panel link failed");await Screenshot(b,sid,viewport+"-old-panel");
                await Click(b,sid,"#okx-operator nav a[href='/naval/']");await b.Wait(sid,"document.querySelector('.naval-app')!==null","Return Naval failed");records.Add(new{viewport,navigation="old-panel-and-back; reload",result="PASS"});
            }
            Assert.Equal(0,b.AuthenticationCommands);Assert.Equal(0,b.AuthorizationRequests);Assert.Equal(0,b.BasicChallenges);
            var state=await h.Json("/api/naval/okx/status");Assert.All(state.GetProperty("accounts").EnumerateArray(),a=>{Assert.False(a.GetProperty("connected").GetBoolean());Assert.False(a.GetProperty("ready").GetBoolean());Assert.Equal("STOP",a.GetProperty("mode").GetString());});Assert.False(Directory.Exists(Path.Combine(h.DataRoot,"okx")));
        }finally{WindowsTestHost.Evidence("ui-inventory",new{records,diagnostics=Diagnostics(b,sid),syntheticNonsecretBindings=true,credentialsEntered=false,privateBrokerConnected=false,domClick=false,exeSha256=h.PayloadHash});}
    }
    [Fact,Trait("Gate","UiRepair")]
    public async Task NativeInvalidMetadataMustRemainVisible()
    {
        await using var h=new WindowsTestHost(WindowsTestHost.Package,WindowsTestHost.NewRoot());await h.InitializeAsync();
        await using var b=await WindowsCdpBrowser.StartAsync(Path.Combine(h.FixtureRoot,"native-edge"));string sid=await b.NewPage();
        await b.Navigate(sid,new Uri(h.Origin,"/").AbsoluteUri);
        await b.Wait(sid,"document.querySelector('#okx-status')?.textContent.includes('Brak konta')===true","Panel did not load");
        var hit=await Hit(b,sid,"#okx-operator button[data-action=configure]");Assert.True(hit.GetProperty("hit").GetBoolean());await Pointer(b,sid,hit);
        IntPtr window=await WindowsNativeUi.Metadata(h.Pid);
        WindowsTestHost.Evidence("native-metadata-visible",WindowsNativeUi.Observe(window,h.Pid));WindowsNativeUi.ScreenshotMetadata(window,"native-metadata-initial");
        WindowsNativeUi.Click(window,1);await Task.Delay(350);
        bool remains=WindowsNativeUi.Visible(window);WindowsTestHost.Evidence("native-metadata-validation",new{emptySubmitRemainsVisible=remains,errorVisible=remains&&!string.IsNullOrEmpty(WindowsNativeUi.Error(window))});
        if(remains)WindowsNativeUi.ScreenshotMetadata(window,"native-metadata-validation");
        Assert.True(remains,"Invalid metadata silently closes the native form instead of showing validation");
        Assert.False(string.IsNullOrWhiteSpace(WindowsNativeUi.Error(window)));
        foreach(var (id,text) in new[]{(100,"demo"),(101,"ui-form-proof"),(102,"123456"),(103,"EUR")})WindowsNativeUi.Text(window,id,text);
        WindowsNativeUi.Click(window,1);await Task.Delay(250);Assert.True(WindowsNativeUi.Visible(window));Assert.Contains("Niepoprawny",WindowsNativeUi.Error(window));
        WindowsNativeUi.ScreenshotMetadata(window,"native-metadata-invalid-environment");
        WindowsNativeUi.Text(window,100,"DEMO");WindowsNativeUi.Click(window,1);
        await WindowsTestHost.Until(()=>Task.FromResult(!WindowsNativeUi.Visible(window)),"Valid DEMO metadata did not advance from the actual native form");
        // Genuine secure approval may use the secure desktop. No password is known for
        // the hosted runner identity, and no bypass/provider replacement is permitted.
        await Task.Delay(1200);
        WindowsTestHost.Evidence("native-metadata-progression",new{validDemoMetadataAccepted=true,securePositiveVerified=false,maskedSecretFormVerified=false,windowsPasswordEntered=false,providerReplaced=false,reason="Hosted runner account has no owned interactive credential acceptance lane; stop before approval / secret enrollment"});
        await h.StopAsync();
        Assert.False(Directory.Exists(Path.Combine(h.DataRoot,"okx")));
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
        WindowsTestHost.Evidence("pointer-configure",new{hit,after,pointerInput=true,domClick=false,exeSha256=h.PayloadHash,diagnostics=Diagnostics(b,sid)});
        await Screenshot(b,sid,"pointer-configure-after");
        Assert.True(hit.GetProperty("hit").GetBoolean(),"Configure center occluded: actual pointer is not delivered to the button");
        var pointerNative=await WindowsNativeUi.Metadata(h.Pid);WindowsTestHost.Evidence("native-pointer-configure",WindowsNativeUi.Observe(pointerNative,h.Pid));WindowsNativeUi.ScreenshotMetadata(pointerNative,"native-pointer-configure");WindowsNativeUi.Cancel(pointerNative);
        await b.Wait(sid,"document.querySelector('#okx-result').textContent.includes('METADATA_USER_CANCELLED')","Native cancellation not shown");
        await b.Wait(sid,"!document.querySelector('[data-action=configure]').disabled","Configure must leave busy state before keyboard action");
        await b.CommandAsync("Page.bringToFront",new{},sid);
        await b.Evaluate(sid,"document.querySelector('[data-action=configure]').focus()");await Key(b,sid,"Enter",13);
        WindowsTestHost.Evidence("native-keyboard-admission",new{state=await b.Evaluate(sid,"({focused:document.hasFocus(),action:document.activeElement?.dataset?.action,disabled:document.querySelector('[data-action=configure]').disabled,result:document.querySelector('#okx-result').textContent})"),diagnostics=Diagnostics(b,sid)});
        var native=await WindowsNativeUi.Metadata(h.Pid);WindowsTestHost.Evidence("native-keyboard-configure",WindowsNativeUi.Observe(native,h.Pid));WindowsNativeUi.ScreenshotMetadata(native,"native-keyboard-configure");WindowsNativeUi.Cancel(native);
        await b.Wait(sid,"document.querySelector('#okx-result').textContent.includes('METADATA_USER_CANCELLED')","Keyboard native cancellation not shown");
        WindowsTestHost.Evidence("configure-diagnostics",Diagnostics(b,sid));
    }
}
