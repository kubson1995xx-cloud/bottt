using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Apex.Desktop.Services;
using Xunit;

namespace Apex.Windows.Tests;

[CollectionDefinition("Extracted Windows", DisableParallelization = true)]
public sealed class ExtractedWindowsCollection { }

// Nonshipped. Every launch is the explicit sealed EXE, never a source DLL.
internal sealed class WindowsTestHost : IAsyncDisposable
{
    internal static string Package => Environment.GetEnvironmentVariable("APEX_PACKAGE_ROOT") ?? throw new InvalidOperationException("APEX_PACKAGE_ROOT required; no repository fallback");
    internal readonly string PackageRoot, DataRoot, FixtureRoot, Exe;
    internal Process? Child;
    internal int Pid => Child?.Id ?? throw new InvalidOperationException("Not started");
    internal int Port = 0;
    internal Uri Origin => new($"http://127.0.0.1:{Port}");
    internal HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
    internal WindowsLocalAccess.Credentials? Credentials;
    private Task<string>? output, error;
    private readonly bool deleteData;
    internal readonly List<object> Processes = new();
    internal readonly Dictionary<string,string> Before;
    internal string Extract => Path.Combine(FixtureRoot, "bundle");
    internal string PayloadHash;

    internal WindowsTestHost(string packageRoot, string dataRoot, bool deleteData = true)
    {
        Assert.True(OperatingSystem.IsWindows(), "Actual Windows required; no skip/pseudo-pass");
        if (!Path.IsPathFullyQualified(packageRoot) || !Path.IsPathFullyQualified(dataRoot)) throw new ArgumentException("Explicit absolute roots required");
        PackageRoot = Path.GetFullPath(packageRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(PackageRoot, Path.GetFullPath(Package).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Root differs from independently extracted APEX_PACKAGE_ROOT");
        DataRoot = Path.GetFullPath(dataRoot);
        if (DataRoot.StartsWith(PackageRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || DataRoot.Equals(PackageRoot, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Data inside payload refused");
        Exe = Path.Combine(PackageRoot, "APEX-Desktop", "ApexDesktop.exe");
        using var meta = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(PackageRoot,"apex-build.json")));
        if (meta.RootElement.GetProperty("target").GetString() != "win-x64" || !File.Exists(Exe)) throw new ArgumentException("Invalid package; no fallback");
        foreach (string path in new[]{PackageRoot,Path.GetDirectoryName(Exe)!,Exe}) if ((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0) throw new ArgumentException("Reparse payload refused");
        this.deleteData = deleteData;
        FixtureRoot = NewRoot(); WindowsPathSecurity.CreatePrivateDirectory(FixtureRoot);
        WindowsPathSecurity.CreatePrivateDirectory(Extract);
        WindowsPathSecurity.CreatePrivateDirectory(Path.Combine(FixtureRoot,"empty-runtime"));
        PayloadHash = Hash(Exe); Before = Inventory(PackageRoot);
    }
    internal static string NewRoot() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ApexTask6-"+Guid.NewGuid().ToString("N"));
    internal static int FreePort() { using var l = new TcpListener(IPAddress.Loopback,0); l.Start(); return ((IPEndPoint)l.LocalEndpoint).Port; }
    internal static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    internal static Dictionary<string,string> Inventory(string root) => Directory.GetFiles(root,"*",SearchOption.AllDirectories).Order().ToDictionary(p=>Path.GetRelativePath(root,p).Replace('\\','/'),Hash);
    internal static void SameInventory(Dictionary<string,string> first, string root) => Assert.True(first.OrderBy(x=>x.Key).SequenceEqual(Inventory(root).OrderBy(x=>x.Key)),"Sealed package bytes changed");
    internal static void Evidence(string name, object record)
    {
        string dir = Environment.GetEnvironmentVariable("APEX_WINDOWS_EVIDENCE") ?? throw new InvalidOperationException("External evidence root required");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir,name+".json"),JsonSerializer.Serialize(record,new JsonSerializerOptions{WriteIndented=true}));
    }
    internal async Task StartAsync(string[] arguments)
    {
        if (Child != null) throw new InvalidOperationException("Stop owned child before restart");
        var p = new ProcessStartInfo(Exe) { UseShellExecute=false, RedirectStandardOutput=true, RedirectStandardError=true, WorkingDirectory=Path.GetDirectoryName(Exe)! };
        foreach (var a in arguments) p.ArgumentList.Add(a);
        foreach (string variable in new[]{"APEX_DATA_DIR","DOTNET_STARTUP_HOOKS","DOTNET_ADDITIONAL_DEPS"}) p.Environment.Remove(variable);
        foreach (string variable in new[]{"DOTNET_ROOT","DOTNET_ROOT_X64","DOTNET_ROOT(x86)","DOTNET_SHARED_STORE"}) p.Environment[variable]=Path.Combine(FixtureRoot,"empty-runtime");
        p.Environment["DOTNET_MULTILEVEL_LOOKUP"]="0";
        p.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"]=Extract;
        p.Environment["PATH"]=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32")+";"+Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.All(p.Environment["PATH"]!.Split(';'),path=>Assert.False(File.Exists(Path.Combine(path,"dotnet.exe"))));
        Child = Process.Start(p)!; output=Child.StandardOutput.ReadToEndAsync(); error=Child.StandardError.ReadToEndAsync();
        Evidence("owned-"+Pid,new{pid=Pid,exe=Exe,exeSha256=PayloadHash,argumentCount=arguments.Length,data=Path.GetFileName(DataRoot),isolatedRuntime=true});
        await Task.CompletedTask;
    }
    internal async Task InitializeAsync()
    {
        Port=FreePort();
        await StartAsync(new[]{"--data",DataRoot,"--port",Port.ToString(),"--no-browser"});
        await NativeDialogAsync(); await ReadyAsync();
        Credentials=WindowsLocalAccess.Load(DataRoot,"presentation");
        // Subsequent starts use the generated production config, retaining all settings.
        await StopAsync();
        var c=JsonNode.Parse(File.ReadAllBytes(Path.Combine(DataRoot,"apex.config.json")))!;
        c["HttpPort"]=Port; c["BridgeTcpPort"]=0; c["OpenBrowserOnStart"]=false;
        File.WriteAllText(Path.Combine(DataRoot,"apex.config.json"),c.ToJsonString());
        await StartAsync(new[]{"--data",DataRoot,"--no-browser"}); await ReadyAsync();
    }
    internal async Task ReadyAsync()
    {
        await Until(async()=> { if (Child!.HasExited) throw new Exception("Extracted EXE exited before ready (private output withheld)"); try { using var r=await Client.GetAsync(Origin); return r.StatusCode==HttpStatusCode.Unauthorized; } catch(HttpRequestException){return false;} catch(TaskCanceledException){return false;} },"EXE did not bind before deadline");
    }
    internal async Task<HttpResponseMessage> Send(string path, string role="operator", string method="GET", string? body=null)
    {
        using var r=new HttpRequestMessage(new HttpMethod(method),new Uri(Origin,path));
        if (role=="operator") r.Headers.TryAddWithoutValidation("Authorization","Basic "+Convert.ToBase64String(Encoding.ASCII.GetBytes("operator:"+Credentials!.OperatorSecret)));
        if (role=="reader") r.Headers.TryAddWithoutValidation("Authorization","Bearer "+Credentials!.ReaderSecret);
        if (body!=null) r.Content=new StringContent(body,Encoding.UTF8,"application/json");
        return await Client.SendAsync(r);
    }
    internal async Task<JsonElement> Json(string path)
    {
        using var r=await Send(path); Assert.Equal(HttpStatusCode.OK,r.StatusCode);
        using var d=JsonDocument.Parse(await r.Content.ReadAsByteArrayAsync()); return d.RootElement.Clone();
    }
    internal async Task NativeDialogAsync()
    {
        IntPtr modal=IntPtr.Zero;
        await Until(()=> { if(Child!.HasExited) throw new Exception("Native credential UI unavailable: process exited (no --no-browser substitution)"); EnumWindows((h,_)=>{GetWindowThreadProcessId(h,out uint pid); var cls=new StringBuilder(100); GetClassName(h,cls,100); if(pid==Pid && cls.ToString()=="#32770") modal=h; return true;},IntPtr.Zero); return Task.FromResult(modal!=IntPtr.Zero); },"Native credential modal not observed");
        var text=new StringBuilder(); EnumChildWindows(modal,(h,_)=>{var s=new StringBuilder(4096);GetWindowText(h,s,s.Capacity);text.Append(s);return true;},IntPtr.Zero);
        var credentials=WindowsLocalAccess.Load(DataRoot,"presentation");
        // Boolean assertions only: xUnit must never format credential text.
        Assert.True(text.ToString().Contains(credentials.OperatorSecret) && text.ToString().Contains("operator"),"Native modal did not match CurrentUser DPAPI");
        Assert.False(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(p=>p.Port==Port && Port!=0),"Credential display started listener");
        SendMessage(modal,0x111,new IntPtr(1),IntPtr.Zero); // WM_COMMAND IDOK, private native acknowledgement
    }
    internal void CheckPrivateTree(string root)
    {
        var records=new List<object>(); var user=WindowsIdentity.GetCurrent().User!;
        foreach(var path in new[]{root}.Concat(Directory.GetFileSystemEntries(root,"*",SearchOption.AllDirectories)))
        {
            bool directory=Directory.Exists(path); Assert.False((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0);
            FileSystemSecurity acl=directory?new DirectoryInfo(path).GetAccessControl():new FileInfo(path).GetAccessControl();
            var owner=acl.GetOwner(typeof(SecurityIdentifier))!.Value;
            Assert.Contains(owner,new[]{user.Value,"S-1-5-18","S-1-5-32-544"});
            var rules=acl.GetAccessRules(true,true,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray(); Assert.NotEmpty(rules);
            Assert.All(rules,r=>{Assert.Equal(AccessControlType.Allow,r.AccessControlType);Assert.Contains(r.IdentityReference.Value,new[]{user.Value,"S-1-5-18","S-1-5-32-544"});});
            records.Add(new{relative=Path.GetRelativePath(root,path).Replace('\\','/'),directory,ownerIsCurrentUser=owner==user.Value,protectedDacl=acl.AreAccessRulesProtected,grants=rules.Select(r=>new{principal=r.IdentityReference==user?"current-user":r.IdentityReference.Value,rights=r.FileSystemRights.ToString(),inherited=r.IsInherited}).ToArray()});
        }
        Evidence("acl-"+Guid.NewGuid().ToString("N"),new{kind=root==Extract?"native-runtime-extraction":"private-data",items=records});
    }
    internal void Modules()
    {
        Child!.Refresh(); var modules=new List<object>();
        using var publish=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(PackageRoot,"Weryfikacja","windows-publish.json")));
        foreach(ProcessModule m in Child.Modules)
        {
            string name=m.ModuleName!,path=Path.GetFullPath(m.FileName!);
            bool os=path.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32")+"\\",StringComparison.OrdinalIgnoreCase);
            bool main=path.Equals(Path.GetFullPath(Exe),StringComparison.OrdinalIgnoreCase);
            if(!os && !main)
            {
                if(!path.StartsWith(Extract+"\\",StringComparison.OrdinalIgnoreCase)) Evidence("module-refused-"+Pid,new{pid=Pid,name,path,reason="outside System32/main/private authenticated bundle; requires explicit OS provenance classification"});
                Assert.True(path.StartsWith(Extract+"\\",StringComparison.OrdinalIgnoreCase),"Module borrowed from machine runtime: "+name);
                var proof=publish.RootElement.GetProperty("nativeLibraries").EnumerateArray().Single(n=>n.GetProperty("name").GetString()!.Equals(name,StringComparison.OrdinalIgnoreCase));
                Assert.Equal(proof.GetProperty("sha256").GetString(),Hash(path));
            }
            modules.Add(new{name,origin=main?"APEX-Desktop/ApexDesktop.exe":os?"Windows/System32/"+name:"bundle/"+Path.GetRelativePath(Extract,path).Replace('\\','/'),sha256=Hash(path)});
        }
        Assert.Equal(PayloadHash,Hash(Child.MainModule!.FileName!)); CheckPrivateTree(Extract);
        Evidence("modules-"+Pid,new{pid=Pid,payloadSha256=PayloadHash,modules});
    }
    internal async Task StopAsync()
    {
        if(Child==null)return;
        int pid=Pid; if(!Child.HasExited){Child.Kill(true);await Child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));}
        string text=await output!+await error!;
        if(Credentials!=null) foreach(string secret in new[]{Credentials.OperatorSecret,Credentials.ReaderSecret}) Assert.True(!text.Contains(secret)&&!text.Contains(Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(secret))).ToLowerInvariant()),"Secret leaked to process output");
        Assert.False(text.Contains("Authorization:",StringComparison.OrdinalIgnoreCase),"Authorization leaked");
        bool exited=Child.HasExited; Child.Dispose();Child=null;
        if(Port!=0) Assert.DoesNotContain(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(),p=>p.Port==Port);
        Processes.Add(new{pid,exited,listenersRemoved=true,outputSanitized=true});
        Evidence("exit-"+pid,new{pid,exited,listenersRemoved=true,outputSanitized=true});
    }
    internal async Task<string> ExitAsync()
    {
        await Child!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); Assert.Equal(2,Child.ExitCode); return await output!+await error!;
    }
    public async ValueTask DisposeAsync()
    {
        try{await StopAsync();SameInventory(Before,PackageRoot);}
        finally
        {
            Client.Dispose(); if(deleteData&&Directory.Exists(DataRoot))Directory.Delete(DataRoot,true);
            if(Directory.Exists(FixtureRoot))Directory.Delete(FixtureRoot,true);
            Evidence("cleanup-"+Guid.NewGuid().ToString("N"),new{processes=Processes,dataRemoved=deleteData?!Directory.Exists(DataRoot):(bool?)null,fixtureRemoved=!Directory.Exists(FixtureRoot),packageUnchanged=Before.OrderBy(x=>x.Key).SequenceEqual(Inventory(PackageRoot).OrderBy(x=>x.Key))});
        }
    }
    internal static async Task Until(Func<Task<bool>> condition,string failure,int seconds=35)
    {var end=DateTime.UtcNow.AddSeconds(seconds);while(DateTime.UtcNow<end){if(await condition())return;await Task.Delay(75);}throw new TimeoutException(failure);}
    private delegate bool EnumProc(IntPtr h,IntPtr p);
    [DllImport("user32.dll")]private static extern bool EnumWindows(EnumProc callback,IntPtr p);
    [DllImport("user32.dll")]private static extern bool EnumChildWindows(IntPtr h,EnumProc callback,IntPtr p);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetClassName(IntPtr h,StringBuilder s,int n);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowText(IntPtr h,StringBuilder s,int n);
    [DllImport("user32.dll")]private static extern IntPtr SendMessage(IntPtr h,uint msg,IntPtr w,IntPtr l);
}
