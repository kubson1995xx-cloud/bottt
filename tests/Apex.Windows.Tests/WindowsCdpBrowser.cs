using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Apex.Desktop.Services;
using Xunit;

namespace Apex.Windows.Tests;

// Bounded native .NET CDP client. No transcript, HAR, SDK or page-injected auth.
internal sealed class WindowsCdpBrowser : IAsyncDisposable
{
    private readonly ClientWebSocket ws=new();
    private readonly SemaphoreSlim send=new(1,1);
    private readonly CancellationTokenSource stop=new();
    private readonly ConcurrentDictionary<int,TaskCompletionSource<JsonElement>> replies=new();
    private readonly ConcurrentDictionary<int,Task> handlers=new();
    private int serial,eventSerial;
    internal int AuthenticationCommands, AuthorizationRequests, BasicChallenges, RequestsObserved;
    private Process child=null!;
    private Task receiving=null!;
    private string profile=null!;
    private readonly List<string> contexts=new();
    internal Func<string,JsonElement,string?,Task>? OnEvent;
    internal readonly ConcurrentQueue<(string Method,JsonElement Data,string? Session)> Events=new();
    internal static async Task<WindowsCdpBrowser> StartAsync(string privateProfileDirectory)
    {
        Assert.True(OperatingSystem.IsWindows(),"Windows Edge required");
        if(!Path.IsPathFullyQualified(privateProfileDirectory)||Directory.Exists(privateProfileDirectory))throw new ArgumentException("New absolute private profile required");
        string? exe=new[]{Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)}.Select(p=>Path.Combine(p,"Microsoft","Edge","Application","msedge.exe")).FirstOrDefault(File.Exists);
        if(exe==null)throw new InvalidOperationException("Installed Edge absent; no downloaded browser fallback");
        WindowsPathSecurity.CreatePrivateDirectory(privateProfileDirectory);
        var b=new WindowsCdpBrowser{profile=privateProfileDirectory};
        try
        {
            var p=new ProcessStartInfo(exe){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(string arg in new[]{"--user-data-dir="+privateProfileDirectory,"--remote-debugging-port=0","--no-first-run","--no-default-browser-check","--disable-background-networking","--enable-unsafe-swiftshader","about:blank"})p.ArgumentList.Add(arg);
            b.child=Process.Start(p)!;
            _=b.child.StandardOutput.ReadToEndAsync();_=b.child.StandardError.ReadToEndAsync();
            string portFile=Path.Combine(privateProfileDirectory,"DevToolsActivePort");
            string[]? lines=null;int readinessPolls=0;
            await WindowsTestHost.Until(()=>
            {
                readinessPolls++;
                if(b.child.HasExited)throw new Exception("Owned Edge exited before CDP readiness; private output withheld");
                // File creation precedes completion/unlocking of Edge's write.
                // Retry only transient I/O within the existing overall deadline.
                try
                {
                    var candidate=File.ReadAllLines(portFile);
                    if(candidate.Length==2 && int.TryParse(candidate[0],out int port) && port is >0 and <=65535 && candidate[1].StartsWith("/devtools/browser/",StringComparison.Ordinal)){lines=candidate;return Task.FromResult(true);}
                }
                catch(IOException) { }
                return Task.FromResult(false);
            },"Edge DevToolsActivePort not readable/complete");
            await b.ws.ConnectAsync(new Uri("ws://127.0.0.1:"+lines![0]+lines[1]),b.stop.Token);
            b.receiving=b.Receive();
            WindowsTestHost.Evidence("edge-start-"+b.child.Id,new{pid=b.child.Id,installedExecutable=exe,privateProfile=true,debugPortEphemeral=true,devToolsFileReadinessPolls=readinessPolls,headless=false});
            return b;
        }
        catch{await b.DisposeAsync();throw;}
    }
    internal async Task<JsonElement> CommandAsync(string method,object parameters,string? sessionId=null)
    {
        if(method.StartsWith("Fetch.",StringComparison.Ordinal)||method=="Network.setExtraHTTPHeaders")Interlocked.Increment(ref AuthenticationCommands);
        int id=Interlocked.Increment(ref serial);var completion=new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);replies[id]=completion;
        byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(new{id,method,@params=parameters,sessionId},new JsonSerializerOptions{DefaultIgnoreCondition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull});
        await send.WaitAsync(stop.Token);try{await ws.SendAsync(bytes,WebSocketMessageType.Text,true,stop.Token);}finally{send.Release();}
        try{return await completion.Task.WaitAsync(TimeSpan.FromSeconds(25));}finally{replies.TryRemove(id,out _);}
    }
    private async Task Receive()
    {
        byte[] buffer=new byte[65536];
        try
        {
            while(!stop.IsCancellationRequested)
            {
                using var message=new MemoryStream();WebSocketReceiveResult r;
                do{r=await ws.ReceiveAsync(buffer,stop.Token);if(r.MessageType==WebSocketMessageType.Close)return;message.Write(buffer,0,r.Count);if(message.Length>16*1024*1024)throw new InvalidOperationException("CDP response bound exceeded");}while(!r.EndOfMessage);
                using var d=JsonDocument.Parse(message.ToArray());var root=d.RootElement;
                if(root.TryGetProperty("id",out var id)&&replies.TryGetValue(id.GetInt32(),out var reply))
                {if(root.TryGetProperty("error",out _))reply.TrySetException(new Exception("CDP command rejected; payload withheld"));else reply.TrySetResult(root.GetProperty("result").Clone());}
                else if(root.TryGetProperty("method",out var method))
                {
                    string name=method.GetString()!;var data=root.GetProperty("params").Clone();string? session=root.TryGetProperty("sessionId",out var s)?s.GetString():null;
                    if(name=="Network.requestWillBeSentExtraInfo")
                    {
                        Interlocked.Increment(ref RequestsObserved);
                        if(data.GetProperty("headers").EnumerateObject().Any(h=>h.Name.Equals("Authorization",StringComparison.OrdinalIgnoreCase)))Interlocked.Increment(ref AuthorizationRequests);
                    }
                    if(name=="Network.responseReceived" && data.GetProperty("response").GetProperty("headers").EnumerateObject().Any(h=>h.Name.Equals("WWW-Authenticate",StringComparison.OrdinalIgnoreCase)&&h.Value.ToString().StartsWith("Basic",StringComparison.OrdinalIgnoreCase)))Interlocked.Increment(ref BasicChallenges);
                    // Keep sensitive auth/request headers out of retained event records.
                    if(name is "Network.responseReceived" or "Network.loadingFinished" or "Page.javascriptDialogOpening")
                    {if(Events.Count>12000)throw new Exception("Event bound exceeded");Events.Enqueue((name,data,session));}
                    var handler=OnEvent;if(handler!=null){int key=Interlocked.Increment(ref eventSerial);Task task=Task.Run(()=>handler(name,data,session));handlers[key]=task;_=task.ContinueWith(_=>handlers.TryRemove(key,out var ignored));}
                }
            }
        }
        catch(Exception e)when(e is OperationCanceledException or WebSocketException or ObjectDisposedException){}
        finally{foreach(var t in replies.Values)t.TrySetException(new Exception("CDP disconnected; private payload withheld"));}
    }
    internal async Task<string> NewPage()
    {
        var context=await CommandAsync("Target.createBrowserContext",new{});string cid=context.GetProperty("browserContextId").GetString()!;contexts.Add(cid);
        var page=await CommandAsync("Target.createTarget",new{url="about:blank",browserContextId=cid});
        var session=await CommandAsync("Target.attachToTarget",new{targetId=page.GetProperty("targetId").GetString(),flatten=true});
        string sid=session.GetProperty("sessionId").GetString()!;
        foreach(string domain in new[]{"Page","Runtime","Network"})await CommandAsync(domain+".enable",new{},sid);
        return sid;
    }
    internal async Task<JsonElement> Evaluate(string session,string expression,bool awaitPromise=false)
    {
        var r=await CommandAsync("Runtime.evaluate",new{expression,returnByValue=true,awaitPromise},session);
        if(r.TryGetProperty("exceptionDetails",out _))throw new Exception("Page evaluation failed; details withheld");
        return r.GetProperty("result").TryGetProperty("value",out var v)?v.Clone():JsonSerializer.SerializeToElement<object?>(null);
    }
    internal async Task Wait(string sid,string expression,string failure,int seconds=35)=>await WindowsTestHost.Until(async()=>{try{return(await Evaluate(sid,expression)).ValueKind==JsonValueKind.True;}catch{return false;}},failure,seconds);
    internal async Task Navigate(string sid,string url){await CommandAsync("Page.navigate",new{url},sid);await CommandAsync("Page.bringToFront",new{},sid);await Wait(sid,"document.readyState === 'complete'","Page load deadline");}
    public async ValueTask DisposeAsync()
    {
        try{if(ws.State==WebSocketState.Open){OnEvent=null;foreach(var context in contexts)await CommandAsync("Target.disposeBrowserContext",new{browserContextId=context});await CommandAsync("Browser.close",new{});}}catch{}
        stop.Cancel();ws.Dispose();if(receiving!=null)await receiving;
        bool exited=true;int? pid=child?.Id;if(child!=null){if(!child.HasExited){child.Kill(true);await child.WaitForExitAsync();}exited=child.HasExited;child.Dispose();}
        if(profile!=null) {for(int i=0;i<30&&Directory.Exists(profile);i++){try{Directory.Delete(profile,true);}catch(IOException){await Task.Delay(100);}catch(UnauthorizedAccessException){await Task.Delay(100);}}Assert.False(Directory.Exists(profile),"Owned Edge profile not deleted");}
        WindowsTestHost.Evidence("edge-cleanup-"+Guid.NewGuid().ToString("N"),new{pid,ownedProcessExited=exited,profileDeleted=profile==null||!Directory.Exists(profile),contextsDisposed=contexts.Count});
        stop.Dispose();send.Dispose();
    }
}
