using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Xunit;
namespace Apex.ControllerPanel.Tests;
public sealed class ProductionEntryTests
{
    static int FreePort(){using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();return ((IPEndPoint)listener.LocalEndpoint).Port;}
    [Fact] public async Task Actual_production_entry_serves_default_unpaired_routes_original_panel_and_naval_assets()
    {
        await using var h=await Host.Open();int port=FreePort();string data=Path.Combine(h.Root,"production");Directory.CreateDirectory(data);
        await File.WriteAllTextAsync(Path.Combine(data,"apex.config.json"),JsonSerializer.Serialize(new{HttpPort=port,BridgeTcpPort=FreePort(),ApiToken="synthetic_legacy_token",PairingKey="SYNTHETIC",Accounts=new Dictionary<string,object>(),OpenBrowserOnStart=false,News=new{Enabled=false},NavalRead=new{Enabled=true,CredentialFilePath=Path.Combine(h.Root,"access.json"),Scope=new{Id="offline",PublicationScopeId=new string('a',64),ConfigDigest=new string('b',64),PortfolioId=new{PortfolioId="test",ReportingCurrency="EUR"},Accounts=new[]{new{Venue="Offline",Environment="Offline",AccountId="test",QuoteCurrency="EUR"}}}}}));
        using var stop=new CancellationTokenSource();var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task=Apex.Desktop.Program.Run(new[]{"--data",data,"--no-browser"},false,null,null,stop.Token,(stage,_)=>{if(stage=="http")ready.TrySetResult();});
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));h.Target=new Uri($"http://127.0.0.1:{port}");
            foreach(var role in new[]{"operator","reader"}){var r=await h.Send("status",role);Assert.Equal(HttpStatusCode.OK,r.StatusCode);Assert.Contains("NOT_CONFIGURED",await r.Content.ReadAsStringAsync());}
            Assert.Equal(HttpStatusCode.Unauthorized,(await h.Send("status",null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,(await h.Send("start","reader","{\"commandId\":\"s\"}")).StatusCode);
            var original=await h.SendPath("/");Assert.Contains("/remote-controller.js",await original.Content.ReadAsStringAsync());
            foreach(string path in new[]{"/remote-controller.js","/remote-controller.css","/naval/"}){
                var r=await h.SendPath(path);Assert.Equal(HttpStatusCode.OK,r.StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden,(await h.SendPath(path,"reader")).StatusCode);
                if(path=="/naval/")Assert.Contains("/remote-controller.js",await r.Content.ReadAsStringAsync());
                else{
                    var dir=new DirectoryInfo(AppContext.BaseDirectory);while(dir!=null&&!Directory.Exists(Path.Combine(dir.FullName,"src/Apex.Desktop/Okx")))dir=dir.Parent;
                    Assert.NotNull(dir);byte[] source=await File.ReadAllBytesAsync(Path.Combine(dir!.FullName,"src/Apex.Desktop/Okx",path.TrimStart('/'))),served=await r.Content.ReadAsByteArrayAsync();Assert.Equal(source,served);
                    string? proof=Environment.GetEnvironmentVariable("CONTROLLER_PANEL_EVIDENCE");if(proof!=null)await File.WriteAllTextAsync(Path.Combine(proof,path.TrimStart('/')+".sha256"),Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(served)).ToLowerInvariant());
                }
            }
            Assert.False(Directory.Exists(Path.Combine(data,"okx")));
        }
        finally{stop.Cancel();Assert.Equal(0,await task.WaitAsync(TimeSpan.FromSeconds(10)));}
    }
}
