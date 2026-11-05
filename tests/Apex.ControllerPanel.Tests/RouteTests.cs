using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Apex.Desktop.Naval;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;
namespace Apex.ControllerPanel.Tests;
public sealed class RouteTests
{
    [Fact] public async Task Replay_cache_is_bounded_and_reserves_safety_commands_without_eviction()
    {
        await using var control=new Apex.Desktop.Okx.OkxRemoteControllerControl();
        for(int i=0;i<240;i++)Assert.Equal("NOT_CONFIGURED",(await control.ExecuteAsync("connect","id"+i)).Code);
        Assert.Equal("LOCAL_COMMAND_BUDGET_EXHAUSTED",(await control.ExecuteAsync("connect","exhausted")).Code);
        Assert.Equal("NOT_CONFIGURED",(await control.ExecuteAsync("stop","safety")).Code);
        Assert.Equal("NOT_CONFIGURED",(await control.ExecuteAsync("connect","id0")).Code);
        Assert.Equal("COMMAND_ID_CONFLICT",(await control.ExecuteAsync("start","id0")).Code);
    }
    [Fact] public async Task Default_status_is_protected_unknown_and_connect_does_not_access_provider()
    {
        await using var h=await Host.Open();
        Assert.Equal(HttpStatusCode.Unauthorized,(await h.Send("status",null)).StatusCode);
        foreach(var role in new[]{"reader","operator"}){
            var r=await h.Send("status",role);Assert.Equal(HttpStatusCode.OK,r.StatusCode);
            using var d=JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            Assert.Equal("NOT_CONFIGURED",d.RootElement.GetProperty("availability").GetString());
            Assert.Equal(JsonValueKind.Null,d.RootElement.GetProperty("snapshot").ValueKind);
        }
        var connect=await h.Send("connect","operator","{\"commandId\":\"one\"}");
        Assert.Equal(HttpStatusCode.OK,connect.StatusCode);
        Assert.Contains("NOT_CONFIGURED",await connect.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden,(await h.Send("stop","reader","{\"commandId\":\"two\"}")).StatusCode);
    }
}
internal sealed class Host:IAsyncDisposable
{
    internal readonly WebApplication App;
    internal readonly HttpClient Client;
    readonly string root;
    readonly Dictionary<string,string> secrets=new(){["operator"]=Secret(),["reader"]=Secret()};
    internal object? Control;
    internal string Root=>root;
    internal Uri? Target;
    Host(WebApplication app,string root){App=app;this.root=root;Client=new(new HttpClientHandler{UseProxy=false}){Timeout=TimeSpan.FromSeconds(6)};}
    static string Secret()=>Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+','-').Replace('/','_');
    internal static async Task<Host> Open(object? control=null,bool desktopLocal=false)
    {
        var root=Path.Combine(Path.GetTempPath(),"controller-panel-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        File.SetUnixFileMode(root,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions{ContentRootPath=AppContext.BaseDirectory,WebRootPath=Path.Combine(AppContext.BaseDirectory,"wwwroot")});builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");
        var h=new Host(builder.Build(),root);var path=Path.Combine(root,"access.json");
        File.WriteAllText(path,JsonSerializer.Serialize(new{schema="naval-local-access-v1",principals=h.secrets.Select(p=>new{id=p.Key,role=p.Key=="operator"?"Operator":"ReadOnly",secretSha256=Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(p.Value))).ToLowerInvariant(),allowedScopeIds=new[]{"offline"}})}));
        File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite);
        var runtime=NavalAccessPolicy.Load(new(){Enabled=true,CredentialFilePath=path,Scope=new(){Id="offline",PublicationScopeId=new('a',64),ConfigDigest=new('b',64),PortfolioId=new("test","EUR"),Accounts=new[]{new Apex.Core.Naval.NavalAccountId("Offline",Apex.Core.Naval.NavalEnvironment.Offline,"test","EUR")}}})!;
        var policy=runtime.Access;
        if(desktopLocal){var ctor=typeof(NavalAccessPolicy).GetConstructors(BindingFlags.Instance|BindingFlags.NonPublic).Single();policy=(NavalAccessPolicy)ctor.Invoke(new[]{typeof(NavalAccessPolicy).GetField("principals",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(policy),runtime.Scope});}
        h.App.Use(async(ctx,next)=>{if(ctx.Request.Host.Host!="127.0.0.1"){ctx.Response.StatusCode=403;return;}if(await policy.Enter(ctx))await next();});
        var assembly=typeof(NavalReadOptions).Assembly;var type=assembly.GetType("Apex.Desktop.Okx.OkxRemoteControllerEndpoints");
        if(type!=null){h.Control=control??Activator.CreateInstance(assembly.GetType("Apex.Desktop.Okx.OkxRemoteControllerControl")!,true);type.GetMethod("Map",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,new[]{h.App,h.Control,policy,runtime.Scope});}
        h.App.UseStaticFiles();NavalFrontendEndpoints.MapNavalFrontendEndpoints(h.App);
        h.App.MapGet("/",async()=>Results.Content((await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"wwwroot/index.html"))).Replace("__APEX_TOKEN__","").Replace("</body>","<script src=\"/remote-controller.js\" defer></script></body>"),"text/html"));
        await h.App.StartAsync();h.Client.BaseAddress=new Uri(h.App.Urls.Single());return h;
    }
    internal Task<HttpResponseMessage> Send(string action,string? role="operator",string? body=null,string? origin=null,bool csrf=true)=>SendPath("/api/naval/controller/"+action,role,body,origin,csrf);
    internal async Task<HttpResponseMessage> SendPath(string path,string? role="operator",string? body=null,string? origin=null,bool csrf=true)
    {
        var req=new HttpRequestMessage(body==null?HttpMethod.Get:HttpMethod.Post,new Uri(Target??Client.BaseAddress!,path));
        if(role=="reader")req.Headers.Authorization=new("Bearer",secrets[role]);
        if(role=="operator")req.Headers.Authorization=new("Basic",Convert.ToBase64String(Encoding.ASCII.GetBytes("operator:"+secrets[role])));
        if(body!=null){req.Content=new StringContent(body,Encoding.UTF8,"application/json");if(csrf)req.Headers.Add("X-Apex-Local","1");}
        if(origin!=null)req.Headers.Add("Origin",origin);
        return await Client.SendAsync(req);
    }
    public async ValueTask DisposeAsync(){Client.Dispose();await App.DisposeAsync();if(Control is IAsyncDisposable c)await c.DisposeAsync();Directory.Delete(root,true);}
}
