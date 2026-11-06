using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Apex.Desktop.Okx;
using Apex.Desktop.Services;
using Apex.Windows.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;
namespace Apex.ControllerComposition.Tests;

// Black-box tests of the EXTRACTED win-x64 EXE. The runner's hosts file maps eea.okx.com /
// wseeapap.okx.com to 127.0.0.1 and trusts a runner-only test root, so the unmodified EXE
// reaches only this loopback synthetic OKX DEMO. No product seam, no real exchange.
[Collection("Composition")]
public sealed class LocalPackagedTests
{
    private const string LiveKey="synthetic-live-key",DemoKey="synthetic-demo-key",Secret="synthetic-demo-secret",Pass="synthetic-demo-pass",Uid="99887766";
    private sealed class SyntheticOkx:IAsyncDisposable
    {
        internal readonly List<string> Requests=new();private readonly WebApplication app;
        private SyntheticOkx(WebApplication app){this.app=app;}
        internal static async Task<SyntheticOkx> Start()
        {
            string pfx=Environment.GetEnvironmentVariable("APEX_SYNTHETIC_OKX_PFX")??throw new Exception("SYNTHETIC_OKX_CERT_REQUIRED");
            var cert=new X509Certificate2(pfx,Environment.GetEnvironmentVariable("APEX_SYNTHETIC_OKX_PFX_PASSWORD"));
            var dir=new DirectoryInfo(AppContext.BaseDirectory);while(dir!=null&&!Directory.Exists(Path.Combine(dir.FullName,"tests/Apex.Adapters.Okx.Tests/Fixtures")))dir=dir.Parent;
            var oracle=JsonNode.Parse(File.ReadAllText(Path.Combine(dir!.FullName,"tests/Apex.Adapters.Okx.Tests/Fixtures/ExecutionObservations/baseline-schema.json")))!;
            var builder=WebApplication.CreateSlimBuilder();builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k=>k.Listen(IPAddress.Loopback,443,o=>o.UseHttps(cert)));
            var app=builder.Build();var okx=new SyntheticOkx(app);
            app.Run(async ctx=>
            {
                string path=ctx.Request.Path.Value??"",now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
                lock(okx.Requests)okx.Requests.Add(ctx.Request.Method+" "+path+" sim="+ctx.Request.Headers["x-simulated-trading"]+" host="+ctx.Request.Host.Host);
                if(ctx.Request.Method!="GET"||ctx.Request.Headers["x-simulated-trading"]!="1"){ctx.Response.StatusCode=400;await ctx.Response.WriteAsync("{\"code\":\"50000\",\"msg\":\"\",\"data\":[]}");return;}
                if(path!="/api/v5/public/time"&&ctx.Request.Headers["OK-ACCESS-KEY"]==LiveKey)
                {ctx.Response.StatusCode=401;ctx.Response.ContentType="application/json";await ctx.Response.WriteAsync("{\"code\":\"50101\",\"msg\":\"APIKey does not match current environment.\",\"data\":[]}");return;}
                string name=path switch{"/api/v5/account/config"=>"account","/api/v5/account/balance"=>"balance","/api/v5/account/instruments"=>"instruments","/api/v5/public/time"=>"time",_=>""};
                if(name==""){ctx.Response.StatusCode=404;return;}
                var body=oracle["endpoints"]![name]!["control"]!.DeepClone();var row=body["data"]![0]!;
                if(name=="time")row["ts"]=now;
                if(name=="account"){row["uid"]=Uid;row["mainUid"]=Uid;row["acctLv"]="1";row["perm"]="read_only,trade";row["ip"]="192.0.2.1";row["feeType"]="1";row["autoLoan"]=false;row["enableSpotBorrow"]=false;row["spotBorrowAutoRepay"]=false;row["type"]="0";row["spotRoleType"]="0";}
                if(name=="balance")
                {
                    row["uTime"]=now;var template=row["details"]![0]!;var details=new JsonArray();
                    foreach(var (ccy,amount) in new[]{("EUR","1000"),("BTC","0.5"),("SOL","3.5")})
                    {var d=template.DeepClone();d["ccy"]=ccy;foreach(string f in new[]{"cashBal","availBal","eq"})d[f]=amount;foreach(string f in new[]{"frozenBal","ordFrozen","liab","interest","crossLiab","isoLiab","stgyEq","fixedBal"})d[f]="0";d["uTime"]=now;details.Add(d);}
                    row["details"]=details;
                }
                if(name=="instruments")
                {
                    var rows=new JsonArray();
                    foreach(string b in new[]{"BTC","ETH","SOL"}){var i=row.DeepClone();i["instId"]=b+"-EUR";i["baseCcy"]=b;i["quoteCcy"]="EUR";i["tradeQuoteCcyList"]=new JsonArray("EUR");i["state"]="live";rows.Add(i);}
                    body["data"]=rows;
                }
                ctx.Response.ContentType="application/json";await ctx.Response.WriteAsync(body.ToJsonString());
            });
            await app.StartAsync();return okx;
        }
        public async ValueTask DisposeAsync(){await app.StopAsync();await app.DisposeAsync();}
    }
    private static string Exe=>Environment.GetEnvironmentVariable("APEX_COMPOSITION_PACKAGE_EXE")??throw new Exception("EXTRACTED_EXE_REQUIRED");
    private static async Task<JsonElement> OkxStatus(CompositionTests.Host host)
    {
        for(int n=0;n<250;n++){try{return await host.Http.GetFromJsonAsync<JsonElement>("/api/naval/okx/status");}catch(HttpRequestException){await Task.Delay(100);}}
        throw new Xunit.Sdk.XunitException("EXTRACTED_EXE_OKX_STATUS_UNAVAILABLE");
    }
    private static async Task<string> Post(CompositionTests.Host host,string path)
    {
        using var r=await host.Http.PostAsJsonAsync(path,new{commandId=Guid.NewGuid().ToString("N")});r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
    }
    private static async Task Pointer(WindowsCdpBrowser browser,string sid,string selector)
    {
        string target=JsonSerializer.Serialize(selector);
        var point=await browser.Evaluate(sid,"(()=>{const e=document.querySelector("+target+");if(!e)return null;e.scrollIntoView({block:'center',behavior:'instant'});const r=e.getBoundingClientRect(),x=r.x+r.width/2,y=r.y+r.height/2;return {x,y,hit:e.contains(document.elementFromPoint(x,y)),disabled:!!e.disabled}})()");
        Assert.True(point.ValueKind==JsonValueKind.Object&&point.GetProperty("hit").GetBoolean()&&!point.GetProperty("disabled").GetBoolean(),"Pointer target unavailable: "+selector);
        double x=point.GetProperty("x").GetDouble(),y=point.GetProperty("y").GetDouble();
        await browser.CommandAsync("Input.dispatchMouseEvent",new{type="mousePressed",x,y,button="left",clickCount=1},sid);
        await browser.CommandAsync("Input.dispatchMouseEvent",new{type="mouseReleased",x,y,button="left",clickCount=1},sid);
    }
    // Real Edge on the EXTRACTED EXE's /local/ panel: mode pill, honest disconnected "—" state, every
    // requested view, and the collapsed "Wersja VPS" section still mounting the VPS controller.
    private static async Task<string> Label(string baseUri,string root,string evidence,string expected,params string[] views)
    {
        await using var browser=await WindowsCdpBrowser.StartAsync(root+"-browser");string sid=await browser.NewPage();
        await browser.CommandAsync("Emulation.setDeviceMetricsOverride",new{width=1440,height=1350,deviceScaleFactor=1,mobile=false},sid);
        await browser.Navigate(sid,new Uri(new Uri(baseUri),"/local/#dashboard").AbsoluteUri);
        await browser.Wait(sid,"document.querySelector('#mode-pill')?.textContent.startsWith("+JsonSerializer.Serialize(expected)+")===true","Mode label not rendered: "+expected);
        string text=(await browser.Evaluate(sid,"document.querySelector('#mode-pill').textContent")).GetString()!;
        string dom=(await browser.Evaluate(sid,"document.documentElement.outerHTML")).GetString()!;
        foreach(string banned in new[]{"USDT","XRP","Prototyp","IndexDipBuy","Pauza"})Assert.DoesNotContain(banned,dom);
        Assert.Equal("—",(await browser.Evaluate(sid,"document.getElementById('capital-cash').textContent")).GetString());
        Assert.True((await browser.Evaluate(sid,"!document.getElementById('data-notice').hidden&&document.getElementById('data-notice').textContent.includes('nie jest połączony')")).GetBoolean(),"Disconnected state not explicit");
        string? dir=Environment.GetEnvironmentVariable("APEX_COMPOSITION_EVIDENCE");
        foreach(string view in views.Length==0?new[]{"dashboard"}:views)
        {
            await Pointer(browser,sid,"#desktop-nav [data-page='"+view+"']");
            await browser.Wait(sid,"document.getElementById("+JsonSerializer.Serialize(view)+").classList.contains('active')","View not shown: "+view);
            if(view=="strategies")
            {
                await browser.Wait(sid,"document.getElementById('ai-tools')!==null&&!document.getElementById('setup-ai').disabled","AI setup pointer missing");
                await Pointer(browser,sid,"#setup-ai");IntPtr wizard=IntPtr.Zero;
                await WindowsTestHost.Until(()=>Task.FromResult((wizard=Window("APEX — lokalne narzędzia AI"))!=IntPtr.Zero),"Extracted native AI setup wizard missing");
                string words=Children(wizard);Assert.Contains("Bez VPS",words);Assert.Contains("48",words);Assert.Contains("900",words);
                SetForegroundWindow(wizard);Assert.True(SpinWait.SpinUntil(()=>GetForegroundWindow()==wizard,3000));keybd_event(0x1B,0,0,UIntPtr.Zero);keybd_event(0x1B,0,2,UIntPtr.Zero);
                await WindowsTestHost.Until(()=>Task.FromResult(Window("APEX — lokalne narzędzia AI")==IntPtr.Zero),"Native setup ESC did not close its zero-field dialog");
                await browser.Wait(sid,"document.getElementById('ai-entry')!==null&&!document.getElementById('setup-ai').disabled","Cancelled setup did not return to panel");
                WindowsTestHost.Evidence("extracted-advisory-setup",new{actualExtractedExe=true,pointer=true,nativeWizard=true,cancelled=true,userSubscriptionLoginTested=false,genuineInference=false});
            }
            if(view=="settings")
            {
                Assert.False((await browser.Evaluate(sid,"document.getElementById('vps-details').open")).GetBoolean());
                await Pointer(browser,sid,"#vps-details summary");
                await browser.Wait(sid,"document.querySelector('#vps-details #remote-controller')!==null&&(document.querySelector('#remote-state')?.textContent||'').length>0","VPS controller did not mount under Ustawienia");
                Assert.True((await browser.Evaluate(sid,"document.getElementById('remote-controller').compareDocumentPosition(document.getElementById('control-card'))&Node.DOCUMENT_POSITION_PRECEDING")).GetInt32()!=0,"VPS section must not sit above the local controls");
            }
            if(dir!=null){var image=await browser.CommandAsync("Page.captureScreenshot",new{format="png"},sid);File.WriteAllBytes(Path.Combine(dir,evidence+"-"+view+".png"),Convert.FromBase64String(image.GetProperty("data").GetString()!));}
        }
        if(views.Length>1)
        {
            await browser.CommandAsync("Emulation.setDeviceMetricsOverride",new{width=390,height=844,deviceScaleFactor=1,mobile=true},sid);
            await Pointer(browser,sid,"#mobile-nav [data-page='dashboard']");
            await browser.Wait(sid,"document.getElementById('dashboard').classList.contains('active')","Mobile navigation did not select Pulpit");
            if(dir!=null){var image=await browser.CommandAsync("Page.captureScreenshot",new{format="png"},sid);File.WriteAllBytes(Path.Combine(dir,evidence+"-mobile.png"),Convert.FromBase64String(image.GetProperty("data").GetString()!));}
        }
        return text;
    }
    [Fact] public async Task Extracted_EXE_first_run_local_enrollment_screens_LIVE_refused_confirmation_then_secure_approval_nothing_saved()
    {
        Assert.True(File.Exists(Exe));
        string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ApexLocalFirstRun-"+Guid.NewGuid().ToString("N"));
        await using var okx=await SyntheticOkx.Start();string stage="START";
        try
        {
            await using var host=new CompositionTests.Host(root);
            var first=await OkxStatus(host);
            Assert.Equal("NOT_SELECTED",first.GetProperty("executionMode").GetString());Assert.True(first.GetProperty("localCreateAvailable").GetBoolean());Assert.False(first.GetProperty("keepAwake").GetBoolean());Assert.Equal("STOP",first.GetProperty("mode").GetString());
            string script=await host.Http.GetStringAsync("/okx-controls.js");Assert.Contains("Lokalnie (ten PC)",script);Assert.Contains("create-local",script);
            stage="LABEL";string label=await Label(host.Http.BaseAddress!.AbsoluteUri,root,"extracted-first-run","Tryb: nie wybrano","dashboard");
            string page=await host.Http.GetStringAsync("/local/");Assert.Contains("Sterowanie botem",page);Assert.DoesNotContain("<script>",page);
            stage="CREATE_POST";var creating=Post(host,"/api/naval/okx/create-local");
            IntPtr form=IntPtr.Zero;
            await WindowsTestHost.Until(()=>Task.FromResult((form=Window("APEX — lokalny bot: klucz OKX DEMO"))!=IntPtr.Zero),"Native DEMO key form missing");
            Assert.Contains("BEZ Withdraw",Children(form));Assert.Contains("Klucze LIVE są odrzucane",Children(form));
            stage="LIVE_KEY";FillMasked(form,LiveKey,Secret,Pass);Enter(form);
            await WindowsTestHost.Until(()=>Task.FromResult(Text(GetDlgItem(form,91)).StartsWith("LOCAL_DEMO_KEYS_REFUSED_",StringComparison.Ordinal)),"LIVE key not refused in the native form");
            string liveRefusal=Text(GetDlgItem(form,91));Assert.False(Directory.Exists(Path.Combine(root,"okx")));
            stage="DEMO_KEY";FillMasked(form,DemoKey,Secret,Pass);Enter(form);
            IntPtr confirm=IntPtr.Zero;
            await WindowsTestHost.Until(()=>Task.FromResult((confirm=Window("APEX — potwierdź nowego lokalnego bota"))!=IntPtr.Zero),"Polish confirmation dialog missing");
            stage="CONFIRM_TEXT";string text="";
            await WindowsTestHost.Until(()=>{text=Children(confirm);return Task.FromResult(text.Contains("Kapitał bota",StringComparison.Ordinal));},"Confirmation text missing");
            foreach(string required in new[]{"NOWĄ lokalną historię","oddzielną od historii bota na VPS","bot na VPS MUSI pozostać zatrzymany (STOP)","Kapitał bota = całe saldo EUR na tym koncie DEMO: 1000 EUR","Inne waluty (BTC, SOL) są wykluczone i nie będą sprzedawane","UID "+Uid,"Brak automatycznego START"})Assert.Contains(required,text);
            Assert.False(Directory.Exists(Path.Combine(root,"okx")),"nothing may be saved before confirmation and protected approval");
            stage="CONFIRM_FILL";string[] lines="Nie będę wykonywać innych operacji podczas\nWirtualne środki i nie korzystałem z pożyczek".Split('\n');
            Fill(confirm,lines[0],lines[1],"TWORZĘ LOKALNEGO BOTA");Enter(confirm);
            // The next step is the CREDUIWIN_SECURE_PROMPT approval on the secure desktop, which UI automation
            // cannot reach. Wait for the product to report that stage (or the OS prompt result), then STOP.
            stage="SECURE_APPROVAL";string reached="";
            await WindowsTestHost.Until(async()=>{var s=await OkxStatus(host);reached=s.GetProperty("enrollmentStage").GetString()??"";return reached=="APPROVAL_WINDOWS_SECURE_DESKTOP_EXPECTED"||creating.IsCompleted;},"Secure approval stage not reached");
            stage="STOP";string stop=await Post(host,"/api/naval/okx/cancel");Assert.Equal("OPERATOR_STOP",stop);
            string created=await creating.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(created=="LOCAL_CREATE_CANCELLED"||created.StartsWith("APPROVAL_",StringComparison.Ordinal),created);
            var after=await OkxStatus(host);
            Assert.Equal("NOT_SELECTED",after.GetProperty("executionMode").GetString());Assert.False(after.GetProperty("keepAwake").GetBoolean());Assert.Equal("STOP",after.GetProperty("mode").GetString());
            Assert.False(Directory.Exists(Path.Combine(root,"okx")),"no key/owner after cancelled protected approval");
            Assert.Empty(OkxConfiguration.Bind(new ConfigStore(root).Config.OkxNaval));
            string[] seen;lock(okx.Requests)seen=okx.Requests.ToArray();
            Assert.NotEmpty(seen);Assert.All(seen,r=>Assert.Matches("^GET /api/v5/(public/time|account/config|account/balance|account/instruments) sim=1 host=eea\\.okx\\.com$",r));
            WindowsTestHost.Evidence("extracted-local-first-run",new{actualExtractedExe=true,label,liveRefusal,confirmationTextVerified=true,reachedStage=reached,createResult=created,stop,nothingSaved=true,syntheticRequests=seen,secureDesktopApprovalAutomated=false,startOnExtractedExe=false});
        }
        catch{WindowsTestHost.Evidence("extracted-local-first-run-failure",new{stage});throw;}
        finally{if(Directory.Exists(root))Directory.Delete(root,true);if(Directory.Exists(root+"-browser"))Directory.Delete(root+"-browser",true);}
    }
    [Fact] public async Task Extracted_EXE_restart_with_local_scope_shows_local_mode_START_refused_STOP_no_keep_awake()
    {
        Assert.True(File.Exists(Exe));
        string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ApexLocalRestart-"+Guid.NewGuid().ToString("N"));
        try
        {
            await using(var host=new CompositionTests.Host(root))Assert.Equal("NOT_SELECTED",(await OkxStatus(host)).GetProperty("executionMode").GetString());
            // A configured local scope as left by a completed first run (key/owner state are not needed for these checks).
            new ConfigStore(root).Update(c=>c.OkxNaval=new(){Schema="okx-naval-config-v1",Scopes=new[]{new OkxScopeOptions{ScopeId=WindowsOkxLocalEnrollment.ScopeId,Environment="OkxDemo",Region="EEA",ExpectedUid=Uid,ExpectedMainUid=Uid,QuoteCurrency="EUR",CredentialBindingId=WindowsOkxLocalEnrollment.CredentialBindingId,SafetyVersion="okx-cash-v1"}}});
            for(int restart=0;restart<2;restart++)
            {
                await using var host=new CompositionTests.Host(root);var s=await OkxStatus(host);
                Assert.Equal("LOCAL",s.GetProperty("executionMode").GetString());Assert.False(s.GetProperty("localCreateAvailable").GetBoolean());Assert.Equal("STOP",s.GetProperty("mode").GetString());Assert.False(s.GetProperty("keepAwake").GetBoolean());
                string label=restart==0?await Label(host.Http.BaseAddress!.AbsoluteUri,root,"extracted-local","Tryb: Lokalnie (ten PC)","dashboard","positions","strategies","history","settings"):"";
                var dash=await host.Http.GetFromJsonAsync<JsonElement>("/api/naval/okx/local-dashboard");
                Assert.Equal("apex-local-dashboard-v1",dash.GetProperty("schema").GetString());Assert.Equal("DISCONNECTED",dash.GetProperty("snapshotState").GetString());
                Assert.Equal(JsonValueKind.Null,dash.GetProperty("snapshot").ValueKind);Assert.Equal(JsonValueKind.Null,dash.GetProperty("limits").ValueKind);Assert.Equal(40,dash.GetProperty("strategy").GetProperty("entryDays").GetInt32());
                string start=await Post(host,"/api/naval/okx/"+WindowsOkxLocalEnrollment.ScopeId+"/start");Assert.Equal("CONNECT_REQUIRED",start);
                string stop=await Post(host,"/api/naval/okx/"+WindowsOkxLocalEnrollment.ScopeId+"/stop");Assert.Equal("OPERATOR_STOP",stop);
                var after=await OkxStatus(host);Assert.Equal("STOP",after.GetProperty("mode").GetString());Assert.False(after.GetProperty("keepAwake").GetBoolean());
                Assert.Equal("LOCAL_SCOPE_ALREADY_CONFIGURED_USE_ATTACH",await Post(host,"/api/naval/okx/create-local"));
                WindowsTestHost.Evidence("extracted-local-restart-"+restart,new{actualExtractedExe=true,executionMode="LOCAL",label,start,stop,keepAwake=false,noAutoStart=true});
            }
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);if(Directory.Exists(root+"-browser"))Directory.Delete(root+"-browser",true);}
    }
    // Masked (ES_PASSWORD) fields cannot be read cross-process by design; the product's response proves receipt.
    private static void Fill(IntPtr w,params string[] values)=>Fill(w,true,values);
    private static void FillMasked(IntPtr w,params string[] values)=>Fill(w,false,values);
    private static void Fill(IntPtr w,bool readBack,string[] values){for(int i=0;i<values.Length;i++){SendText(GetDlgItem(w,100+i),0xC,IntPtr.Zero,values[i]);if(readBack)Assert.Equal(values[i],Text(GetDlgItem(w,100+i)));}}
    private static IntPtr Window(string title)
    {
        var pids=Process.GetProcessesByName("ApexDesktop").Select(p=>(uint)p.Id).ToHashSet();IntPtr found=IntPtr.Zero;
        EnumWindows((h,_)=>{GetWindowThreadProcessId(h,out uint owner);var t=new StringBuilder(256);GetWindowTextW(h,t,t.Capacity);if(pids.Contains(owner)&&t.ToString()==title&&IsWindowVisible(h)){found=h;return false;}return true;},IntPtr.Zero);return found;
    }
    private static string Children(IntPtr w){var text=new StringBuilder();EnumChildWindows(w,(h,_)=>{text.Append(Text(h)).Append('\n');return true;},IntPtr.Zero);return text.ToString();}
    private static string Text(IntPtr w){var s=new StringBuilder(8192);SendRead(w,0xD,new IntPtr(s.Capacity),s);return s.ToString();}
    private static void Enter(IntPtr w){SetForegroundWindow(w);Assert.True(SpinWait.SpinUntil(()=>GetForegroundWindow()==w,3000),"NATIVE_WINDOW_NOT_FOREGROUND");keybd_event(0x0D,0,0,UIntPtr.Zero);keybd_event(0x0D,0,2,UIntPtr.Zero);}
    private delegate bool EnumProc(IntPtr w,IntPtr data);
    [DllImport("user32.dll")]private static extern bool EnumWindows(EnumProc callback,IntPtr data);
    [DllImport("user32.dll")]private static extern bool EnumChildWindows(IntPtr parent,EnumProc callback,IntPtr data);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr w,out uint pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowTextW(IntPtr w,StringBuilder text,int count);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="SendMessageW")]private static extern IntPtr SendText(IntPtr w,uint msg,IntPtr a,string text);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="SendMessageW")]private static extern IntPtr SendRead(IntPtr w,uint msg,IntPtr a,StringBuilder text);
    [DllImport("user32.dll")]private static extern IntPtr GetDlgItem(IntPtr w,int id);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(IntPtr w);
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(IntPtr w);
    [DllImport("user32.dll")]private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]private static extern void keybd_event(byte key,byte scan,uint flags,UIntPtr extra);
}
