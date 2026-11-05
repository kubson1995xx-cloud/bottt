using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Apex.ControllerTransport.Tests;
using Apex.Desktop;
using Apex.Desktop.Okx;
using Apex.Desktop.Services;
using Xunit;
namespace Apex.ControllerComposition.Tests;

[CollectionDefinition("Composition",DisableParallelization=true)] public sealed class CompositionCollection{}
[Collection("Composition")]
public sealed class CompositionTests
{
    internal static async Task<bool> Pair(string root,WindowsOkxControllerTransportConfig c,bool trust,CancellationToken ct=default)
    {
        // Old production has no provisioning entry. No substitute writer/secret fixture.
        var t=typeof(Program).Assembly.GetType("Apex.Desktop.Okx.WindowsOkxControllerPairing");
        if(t==null)return false;
        try{return await (Task<bool>)t.GetMethod("ProvisionAsync",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new object[]{root,c.Host,c.Port,c.User,c.HostKey,trust,ct})!;}
        catch(TargetInvocationException e){System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException!).Throw();throw;}
    }
    internal sealed class Root:IDisposable
    {
        internal string Path_=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ApexComposition-"+Guid.NewGuid().ToString("N"));
        internal Root()
        {
            using var lease=WindowsPrivateData.Open(Path_,AppContext.BaseDirectory);
            var setup=WindowsLocalSetup.Ensure(Path_,Environment.GetEnvironmentVariable("APEX_PRESENTATION_TEST_DIRECTORY")!);
            setup.Store.Update(c=>{c.News.Enabled=false;c.OpenBrowserOnStart=false;c.BridgeTcpPort=0;});
        }
        public void Dispose(){if(Directory.Exists(Path_))Directory.Delete(Path_,true);}
    }
    internal sealed class Host:IAsyncDisposable
    {
        internal readonly CancellationTokenSource Stop=new(TimeSpan.FromSeconds(60));
        internal readonly HttpClient Http;
        private readonly Task<int> running;
        private readonly Process? child;
        private bool disposed;
        internal Host(string root)
        {
            using var l=new TcpListener(IPAddress.Loopback,0);l.Start();int port=((IPEndPoint)l.LocalEndpoint).Port;l.Stop();
            Http=new(){BaseAddress=new Uri($"http://127.0.0.1:{port}"),Timeout=TimeSpan.FromSeconds(20)};Http.DefaultRequestHeaders.Add("X-Apex-Local","1");
            var args=new[]{"--data",root,"--port",port.ToString(),"--no-browser"};
            string? exe=Environment.GetEnvironmentVariable("APEX_COMPOSITION_PACKAGE_EXE");
            if(exe!=null)
            {
                Assert.True(Path.IsPathFullyQualified(exe)&&File.Exists(exe),"EXPLICIT_PACKAGE_EXE_REQUIRED_NO_FALLBACK");
                var start=new ProcessStartInfo(exe){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var arg in args)start.ArgumentList.Add(arg);
                foreach(string name in new[]{"DOTNET_ROOT","DOTNET_ROOT_X64","DOTNET_ROOT(x86)","DOTNET_SHARED_STORE"})start.Environment[name]=Path.Combine(root,"absent-runtime");
                start.Environment["DOTNET_MULTILEVEL_LOOKUP"]="0";start.Environment["PATH"]=Environment.GetFolderPath(Environment.SpecialFolder.System);
                foreach(string name in new[]{"DOTNET_STARTUP_HOOKS","DOTNET_ADDITIONAL_DEPS","APEX_DATA_DIR"})start.Environment.Remove(name);
                child=Process.Start(start)!;_=child.StandardOutput.BaseStream.CopyToAsync(Stream.Null);_=child.StandardError.BaseStream.CopyToAsync(Stream.Null);
                running=Observe(child);
            }
            else running=Task.Run(()=>Program.Run(args,false,null,null,Stop.Token,null));
        }
        private static async Task<int> Observe(Process p){await p.WaitForExitAsync();return p.ExitCode;}
        internal async Task<JsonElement> Status()
        {
            var until=Stopwatch.StartNew();
            while(until.Elapsed<TimeSpan.FromSeconds(25))
            {
                Assert.False(running.IsCompleted,"PRODUCTION_HOST_EXITED_BEFORE_HTTP");
                try{return await Http.GetFromJsonAsync<JsonElement>("/api/naval/controller/status");}
                catch(HttpRequestException){await Task.Delay(50);}
            }
            throw new Xunit.Sdk.XunitException("PRODUCTION_HTTP_NOT_READY");
        }
        internal async Task<JsonElement> Command(string action)
        {using var r=await Http.PostAsJsonAsync("/api/naval/controller/"+action,new{commandId="cmd-"+Guid.NewGuid().ToString("N")});r.EnsureSuccessStatusCode();return await r.Content.ReadFromJsonAsync<JsonElement>();}
        public async ValueTask DisposeAsync()
        {
            if(disposed)return;disposed=true;
            Stop.Cancel();
            if(child!=null){if(!child.HasExited)child.Kill(true);await running.WaitAsync(TimeSpan.FromSeconds(20));child.Dispose();}
            else Assert.Equal(0,await running.WaitAsync(TimeSpan.FromSeconds(20)));
            Http.Dispose();Stop.Dispose();
        }
    }
    [Fact] public async Task Production_boot_is_passive_then_panel_connect_uses_pinned_native_factory()
    {
        await using var peer=await NativeSshFixture.Open();using var root=new Root();
        Directory.CreateDirectory(Path.Combine(root.Path_,"controller-ssh"));
        foreach(var leaf in new[]{"identity","known_hosts"}){string p=Path.Combine(root.Path_,"controller-ssh",leaf);File.Copy(Path.Combine(peer.Root,"controller-ssh",leaf),p);Owner(p);}
        ConnectionTests.Put(root.Path_,ConnectionTests.Json(peer.Config));
        await using var host=new Host(root.Path_);
        var s=await host.Status();Assert.Empty(peer.Events);
        Assert.True(s.GetProperty("configured").GetBoolean(),"PRODUCTION_FACTORY_NOT_COMPOSED");
        Assert.Equal("DISCONNECTED",s.GetProperty("availability").GetString());
        Assert.Equal("confirmed",(await host.Command("connect")).GetProperty("outcome").GetString());
        Assert.DoesNotContain("CONNECT",peer.Events); // channel bootstrap is not broker connect
        Assert.Equal("CONNECTED",(await host.Status()).GetProperty("availability").GetString());
    }
    [Fact] public async Task Unconfigured_production_profile_reports_NOT_CONFIGURED_without_creating_ssh_files()
    {
        using var root=new Root();await using var host=new Host(root.Path_);
        Assert.Equal("NOT_CONFIGURED",(await host.Status()).GetProperty("availability").GetString());
        Assert.Equal("NOT_CONFIGURED",(await host.Command("connect")).GetProperty("code").GetString());
        Assert.False(Directory.Exists(Path.Combine(root.Path_,"controller-ssh")));
    }
    [Fact] public async Task Native_pairing_generates_local_identity_exports_only_public_key_and_connects()
    {
        await using var peer=await NativeSshFixture.Open();using var root=new Root();
        Assert.True(await Pair(root.Path_,peer.Config,true),"NATIVE_PAIRING_NOT_IMPLEMENTED");
        var loaded=WindowsOkxControllerConnectionStore.Load(root.Path_);
        Assert.Equal(peer.Config.Host,loaded.Host);Assert.Equal(peer.Config.HostKey,loaded.HostKey);
        Assert.NotEqual(peer.Config.IdentitySha256,loaded.IdentitySha256);
        string publicKey=File.ReadAllText(Path.Combine(root.Path_,"controller-public-key.txt"));
        Assert.StartsWith("ssh-ed25519 ",publicKey);Assert.DoesNotContain("PRIVATE",publicKey);
        Assert.Equal(new[]{"identity","known_hosts"},Directory.GetFiles(Path.Combine(root.Path_,"controller-ssh")).Select(Path.GetFileName).OrderBy(x=>x,StringComparer.Ordinal));
        string authorized=Path.Combine(peer.ServerRoot,"authorized_keys");File.WriteAllText(authorized,publicKey);Owner(authorized);
        await using var host=new Host(root.Path_);Assert.Equal("DISCONNECTED",(await host.Status()).GetProperty("availability").GetString());Assert.Empty(peer.Events);
        Assert.Equal("confirmed",(await host.Command("connect")).GetProperty("outcome").GetString());
        Assert.DoesNotContain("CONNECT",peer.Events);
    }
    [Theory][InlineData(false)][InlineData(true)] public async Task Cancel_or_untrusted_pin_has_no_pairing_writes(bool cancelled)
    {
        await using var peer=await NativeSshFixture.Open();using var root=new Root();var before=Inventory(root.Path_);
        using var c=new CancellationTokenSource();if(cancelled)c.Cancel();
        try{Assert.False(await Pair(root.Path_,peer.Config,false,c.Token));}catch(OperationCanceledException){Assert.True(cancelled);}
        Assert.Equal(before,Inventory(root.Path_));Assert.Empty(peer.Events);
    }
    [Theory][InlineData("partial")][InlineData("existing")][InlineData("broad")][InlineData("pending")]
    public async Task Pairing_refuses_existing_partial_pending_or_broad_roots_without_repair(string kind)
    {
        await using var peer=await NativeSshFixture.Open();using var root=new Root();
        if(kind=="existing"){Assert.True(await Pair(root.Path_,peer.Config,true),"POSITIVE_PAIRING_REQUIRED");}
        else if(kind=="partial")WindowsPathSecurity.CreatePrivateDirectory(Path.Combine(root.Path_,"controller-ssh"));
        else if(kind=="pending")File.WriteAllText(Path.Combine(root.Path_,"controller-pairing.pending"),"incomplete");
        else {var acl=new DirectoryInfo(root.Path_).GetAccessControl();acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid,null),FileSystemRights.Read,AccessControlType.Allow));new DirectoryInfo(root.Path_).SetAccessControl(acl);}
        var before=Inventory(root.Path_);var security=new DirectoryInfo(root.Path_).GetAccessControl().GetSecurityDescriptorBinaryForm();
        Assert.False(await Pair(root.Path_,peer.Config,true));Assert.Equal(before,Inventory(root.Path_));Assert.Equal(security,new DirectoryInfo(root.Path_).GetAccessControl().GetSecurityDescriptorBinaryForm());Assert.Empty(peer.Events);
    }
    [Fact] public async Task Pending_pairing_production_start_refuses_without_reset()
    {
        using var root=new Root();File.WriteAllBytes(Path.Combine(root.Path_,"apex.lock"),Array.Empty<byte>());
        File.WriteAllText(Path.Combine(root.Path_,"controller-pairing.pending"),"incomplete");var before=Inventory(root.Path_);
        Assert.Equal(2,await Program.Run(new[]{"--data",root.Path_,"--no-browser"},false));Assert.Equal(before,Inventory(root.Path_));
    }
    [Theory][InlineData("invalid-host")][InlineData("invalid-port")][InlineData("invalid-user")][InlineData("invalid-pin")]
    public async Task Invalid_operator_metadata_never_creates_key_material(string kind)
    {
        await using var peer=await NativeSshFixture.Open();using var root=new Root();var c=peer.Config;
        c=kind switch{"invalid-host"=>c with{Host="host;command"},"invalid-port"=>c with{Port=0},"invalid-user"=>c with{User="root -o"},_=>c with{HostKey=new string('A',68)}};
        var before=Inventory(root.Path_);Assert.False(await Pair(root.Path_,c,true));Assert.Equal(before,Inventory(root.Path_));Assert.Empty(peer.Events);
    }
    internal static string[] Inventory(string root)=>Directory.GetFiles(root,"*",SearchOption.AllDirectories).Order().Select(p=>Path.GetRelativePath(root,p)+":"+Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))).ToArray();
    internal static void Owner(string p){var acl=new FileInfo(p).GetAccessControl();acl.SetOwner(WindowsIdentity.GetCurrent().User!);new FileInfo(p).SetAccessControl(acl);}
}
