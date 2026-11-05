using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Apex.ControllerAdmission.Tests;
using Apex.Desktop.Okx;
using Apex.Desktop.Services;
using Xunit;
namespace Apex.ControllerTransport.Tests;

// Owned disposable loopback sshd. No service, machine config, account creation or private state.
internal sealed class NativeSshFixture:IAsyncDisposable
{
    internal readonly NativeAdmissionTests.Profile Profile=new();
    internal string Root=>Profile.Root;
    internal string ServerRoot=>Root+"-sshd";
    internal WindowsOkxControllerTransportConfig Config=null!;
    internal Process Server=null!;
    private Task stderr=Task.CompletedTask,stdout=Task.CompletedTask;
    private readonly StringBuilder serverDiagnostic=new();
    internal static string Classify(string text)=>string.Join("|",new[]{"Permission denied","Host key verification failed","Bad owner or permissions","bad ownership","Accepted publickey","Failed publickey","CreateProcessW failed","CreateProcessAsUserW failed","unable to load","Could not load","not allowed","No such file","not recognized","Could not resolve","Connection closed","Connection reset","Server listening","Starting session","shell request","exec request","subsystem request","get_passwd","privsep","posix_spawn","cannot find","not found","not implemented"}.Where(s=>text.Contains(s,StringComparison.OrdinalIgnoreCase)));
    private async Task DrainDiagnostic(StreamReader input)
    {char[] b=new char[2048];int n;while((n=await input.ReadAsync(b))>0){lock(serverDiagnostic){if(serverDiagnostic.Length<65536)serverDiagnostic.Append(b,0,Math.Min(n,65536-serverDiagnostic.Length));}}}
    internal static string SyntheticError(string text,string root)
    {
        // Independent synthetic OS control only, never the product transport. Redact fixture
        // paths, current user, public-key-shaped strings and nonprintable bytes before assertion.
        text=text.Replace(root,"[OWNED_ROOT]",StringComparison.OrdinalIgnoreCase).Replace(WindowsIdentity.GetCurrent().Name,"[SYNTHETIC_USER]",StringComparison.OrdinalIgnoreCase);
        text=System.Text.RegularExpressions.Regex.Replace(text,@"[A-Za-z0-9+/=]{40,}","[REDACTED]");
        text=new string(text.Where(c=>c is >= ' ' and <= '~').Take(384).ToArray());return text;
    }
    internal string ServerCodes{get{lock(serverDiagnostic)return Classify(serverDiagnostic.ToString());}}
    private readonly List<Process> helpers=new();
    private string sshDirectory="";
    internal static async Task<NativeSshFixture> Open(string behavior="normal")
    {
        var f=new NativeSshFixture();
        try{await f.Initialize(behavior);return f;}catch{await f.DisposeAsync();throw;}
    }
    private static void Owner(string path)
    {var acl=new FileInfo(path).GetAccessControl();acl.SetOwner(WindowsIdentity.GetCurrent().User!);new FileInfo(path).SetAccessControl(acl);}
    private async Task Run(string exe,params string[] args)
    {
        var start=new ProcessStartInfo(exe){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};foreach(var a in args)start.ArgumentList.Add(a);
        using var p=Process.Start(start)!;helpers.Add(p);
        var o=p.StandardOutput.BaseStream.CopyToAsync(Stream.Null);var e=p.StandardError.BaseStream.CopyToAsync(Stream.Null);
        using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(15));await p.WaitForExitAsync(budget.Token);await Task.WhenAll(o,e);Assert.Equal(0,p.ExitCode);
    }
    private async Task Initialize(string behavior)
    {
        using(var control=WindowsOkxControllerTransportAdmission.Open(Root,Profile.Config))sshDirectory=Path.GetDirectoryName(control.Spec.Executable)!;
        WindowsPathSecurity.CreatePrivateDirectory(ServerRoot);
        string keygen=Path.Combine(sshDirectory,"ssh-keygen.exe"),sshd=Path.Combine(sshDirectory,"sshd.exe");
        Assert.True(File.Exists(keygen)&&File.Exists(sshd),"EXISTING_OS_SSHD_AND_KEYGEN_REQUIRED_NO_INSTALL");
        string client=Path.Combine(ServerRoot,"client"),host=Path.Combine(ServerRoot,"host");
        await Run(keygen,"-q","-t","ed25519","-N","","-f",client);
        await Run(keygen,"-q","-t","ed25519","-N","","-f",host);
        File.Copy(client,Profile.Identity,true);Owner(Profile.Identity);
        string publicKey=File.ReadAllText(host+".pub").Split(' ',StringSplitOptions.RemoveEmptyEntries)[1];
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
        string user=WindowsIdentity.GetCurrent().Name.Split('\\').Last().ToLowerInvariant();
        Config=new("127.0.0.1",port,user,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Profile.Identity))).ToLowerInvariant(),publicKey);
        File.WriteAllText(Profile.KnownHosts,$"[127.0.0.1]:{port} ssh-ed25519 {publicKey}\n",new UTF8Encoding(false));Owner(Profile.KnownHosts);
        string authorized=Path.Combine(ServerRoot,"authorized_keys");File.Copy(client+".pub",authorized);Owner(authorized);Owner(host);
        File.WriteAllText(Path.Combine(ServerRoot,"behavior"),behavior);
        File.WriteAllText(Path.Combine(ServerRoot,"state"),"STOP");
        // This fixture-only ForceCommand receives the exact production relay as SSH_ORIGINAL_COMMAND.
        string dotnet=Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")??"dotnet";
        if(!Path.IsPathFullyQualified(dotnet))dotnet=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"dotnet","dotnet.exe");
        string force='"'+dotnet+'"'+" "+'"'+typeof(PeerProgram).Assembly.Location+'"'+" --peer "+'"'+ServerRoot+'"';
        string Slash(string p)=>p.Replace('\\','/');
        string config=Path.Combine(ServerRoot,"sshd_config");
        File.WriteAllText(config,$"Port {port}\nListenAddress 127.0.0.1\nHostKey \"{Slash(host)}\"\nPidFile \"{Slash(Path.Combine(ServerRoot,"sshd.pid"))}\"\nAuthorizedKeysFile \"{Slash(authorized)}\"\nPubkeyAuthentication yes\nPasswordAuthentication no\nKbdInteractiveAuthentication no\nAuthenticationMethods publickey\nStrictModes yes\nAllowUsers {user}\nAllowTcpForwarding no\nAllowAgentForwarding no\nPermitTTY no\nForceCommand {force}\nLogLevel DEBUG3\n",new UTF8Encoding(false));
        await Run(sshd,"-t","-f",config);
        var start=new ProcessStartInfo(sshd){UseShellExecute=false,RedirectStandardError=true,RedirectStandardOutput=true,CreateNoWindow=true};start.ArgumentList.Add("-D");start.ArgumentList.Add("-e");start.ArgumentList.Add("-f");start.ArgumentList.Add(config);
        Server=Process.Start(start)!;stderr=DrainDiagnostic(Server.StandardError);stdout=Server.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        var until=Stopwatch.StartNew();bool ready=false;
        while(until.Elapsed<TimeSpan.FromSeconds(10)&&!Server.HasExited)
        {try{using var socket=new TcpClient();await socket.ConnectAsync(IPAddress.Loopback,port);ready=true;break;}catch(SocketException){await Task.Delay(50);}}
        Assert.True(ready,"EPHEMERAL_LOOPBACK_SSHD_NOT_READY");
        ConnectionTests.Put(Root,ConnectionTests.Json(Config));
    }
    internal string[] Events=>File.Exists(Path.Combine(ServerRoot,"events"))?File.ReadAllLines(Path.Combine(ServerRoot,"events")):Array.Empty<string>();
    public async ValueTask DisposeAsync()
    {
        // Windows sshd can create ForceCommand via a different parent. Its silent/flood
        // test peer may outlive the SSH session and keep inherited sshd pipes open.
        // Reap only our exact registered launch, never a later process reusing that PID.
        if(Directory.Exists(ServerRoot))foreach(string file in Directory.GetFiles(ServerRoot,"peer-*.json"))
        {
            using var record=System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(file));
            int id=record.RootElement.GetProperty("Id").GetInt32();long started=record.RootElement.GetProperty("StartedUtcTicks").GetInt64();
            Process? peer=null;
            try{peer=Process.GetProcessById(id);}catch(ArgumentException){}
            if(peer!=null)using(peer)
            {
                if(!peer.HasExited&&peer.StartTime.ToUniversalTime().Ticks==started)
                {peer.Kill(true);await peer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}
            }
        }
        if(Server!=null)
        {
            if(!Server.HasExited)Server.Kill(true);
            using var b=new CancellationTokenSource(TimeSpan.FromSeconds(5));await Server.WaitForExitAsync(b.Token);await Task.WhenAll(stderr,stdout).WaitAsync(TimeSpan.FromSeconds(5));Server.Dispose();
        }
        // No generated identity, host key or authorization file enters evidence/artifacts.
        Profile.Dispose();if(Directory.Exists(ServerRoot))Directory.Delete(ServerRoot,true);
    }
}
