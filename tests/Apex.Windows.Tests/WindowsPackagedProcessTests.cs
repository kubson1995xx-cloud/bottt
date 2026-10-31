using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Apex.Desktop.Services;
using Xunit;

namespace Apex.Windows.Tests;

[Collection("Extracted Windows")]
public sealed class WindowsPackagedProcessTests
{
    [Fact,Trait("Gate","Package")]
    public async Task ExtractedExeHasNoSharedRuntimeDependency()
    {
        await using var h=new WindowsTestHost(WindowsTestHost.Package,WindowsTestHost.NewRoot());
        await h.InitializeAsync(); h.Modules(); h.CheckPrivateTree(h.DataRoot);
        string controls=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"WinSxS","amd64_microsoft.windows.common-controls_6595b64144ccf1df_6.0.26100.33648_none_ee38cd5ddaee2cd9","comctl32.dll");
        Assert.True(WindowsTestHost.IsCommonControlsPath("comctl32.dll",controls));
        Assert.False(WindowsTestHost.IsCommonControlsPath("vcruntime140.dll",controls.Replace("comctl32.dll","vcruntime140.dll")));
        Assert.False(WindowsTestHost.IsCommonControlsPath("comctl32.dll",Path.Combine(h.FixtureRoot,"comctl32.dll")));
        var status=await h.Json("/api/status"); Assert.Empty(status.GetProperty("data").GetProperty("accounts").EnumerateArray());Assert.False(status.GetProperty("host").GetProperty("liveFeature").GetBoolean());
    }
    [Fact,Trait("Gate","Package")]
    public async Task ExistingConfigRestartSecondLaunchAndPortCollision()
    {
        await using var h=new WindowsTestHost(WindowsTestHost.Package,WindowsTestHost.NewRoot());await h.InitializeAsync();
        string config=Path.Combine(h.DataRoot,"apex.config.json");byte[] before=File.ReadAllBytes(config);
        string publication=WindowsTestHost.Hash(Path.Combine(h.DataRoot,"publication","naval-publication.json"));
        var first=await h.Json("/api/naval/snapshot");
        await using(var second=new WindowsTestHost(WindowsTestHost.Package,h.DataRoot,false))
        {await second.StartAsync(new[]{"--data",h.DataRoot});string text=await second.ExitAsync();Assert.Contains("HOST_ALREADY_RUNNING",text);}
        await using(var collision=new WindowsTestHost(WindowsTestHost.Package,WindowsTestHost.NewRoot()))
        {
            await collision.InitializeAsync();await collision.StopAsync();
            await collision.StartAsync(new[]{"--data",collision.DataRoot,"--port",h.Port.ToString()});
            Assert.Contains("HOST_HTTP_BIND_FAILED",await collision.ExitAsync());
        }
        Assert.Equal(before,File.ReadAllBytes(config)); await h.StopAsync();
        await h.StartAsync(new[]{"--data",h.DataRoot,"--no-browser"});await h.ReadyAsync();
        var later=await h.Json("/api/naval/snapshot");Assert.NotEqual(first.GetProperty("hostEpoch").GetString(),later.GetProperty("hostEpoch").GetString());
        Assert.Equal(first.GetProperty("checkpoint").GetRawText(),later.GetProperty("checkpoint").GetRawText());
        Assert.Equal(publication,WindowsTestHost.Hash(Path.Combine(h.DataRoot,"publication","naval-publication.json")));Assert.Equal(before,File.ReadAllBytes(config));
        WindowsTestHost.Evidence("restart",new{secondLaunchRefused=true,collisionRefused=true,configPreserved=true,checkpointPreserved=true,newHostEpoch=true});
    }
    [Fact,Trait("Gate","Package")]
    public async Task OnlyIpv4LoopbackListeners()
    {
        await using var h=new WindowsTestHost(WindowsTestHost.Package,WindowsTestHost.NewRoot());await h.InitializeAsync();
        int tcp=WindowsTestHost.FreePort();await h.StopAsync();
        var path=Path.Combine(h.DataRoot,"apex.config.json");var c=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(path))!;c["BridgeTcpPort"]=tcp;File.WriteAllText(path,c.ToJsonString());
        await h.StartAsync(new[]{"--data",h.DataRoot,"--no-browser"});await h.ReadyAsync();
        await WindowsTestHost.Until(()=>Task.FromResult(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(p=>p.Port==tcp)),"TCP did not bind");
        var listeners=WindowsTestHost.OwnedListeners(h.Pid);Assert.Equal(2,listeners.Length);Assert.All(listeners,p=>{Assert.Equal("IPv4",p.Family);Assert.Equal("127.0.0.1",p.Address);});
        WindowsTestHost.Evidence("listeners",new{pid=h.Pid,listeners,webSocketSharesHttp=true,allOwnedIpv4AndIpv6TablesInspected=true});
        int pid=h.Pid;await h.StopAsync();Assert.Empty(WindowsTestHost.OwnedListeners(pid));
    }
    [Fact,Trait("Gate","Package")]
    public async Task ConfiguredSourceFailurePreservesEmergency()
    {
        await using var h=new WindowsTestHost(WindowsTestHost.Package,WindowsTestHost.NewRoot());await h.InitializeAsync();
        File.WriteAllText(Path.Combine(h.DataRoot,"publication","naval-publication.json"),"{");
        using var bad=await h.Send("/api/naval/snapshot");Assert.Equal(HttpStatusCode.ServiceUnavailable,bad.StatusCode);
        using var panel=await h.Send("/");Assert.Equal(HttpStatusCode.OK,panel.StatusCode);string html=await panel.Content.ReadAsStringAsync();Assert.Contains("name=\"apex-token\" content=\"\"",html);
        using var js=await h.Send("/app.js");Assert.Contains("AWARYJNY STOP",await js.Content.ReadAsStringAsync());
        WindowsTestHost.Evidence("source-failure",new{snapshot503=true,oldPanel200=true,emptyLegacyToken=true,textEmergencyAvailable=true});
    }
    [Fact,Trait("Gate","Package")]
    public async Task PackageBytesUnchanged()
    {
        await using var h=new WindowsTestHost(WindowsTestHost.Package,WindowsTestHost.NewRoot());await h.InitializeAsync();
        using var r=await h.Send("/naval/");Assert.Equal(HttpStatusCode.OK,r.StatusCode);WindowsTestHost.SameInventory(h.Before,h.PackageRoot);
    }
}

[Collection("Extracted Windows")]
public sealed class WindowsFirstRunUiTests
{
    [Fact,Trait("Gate","Package")]
    public async Task FirstLaunchWithoutArgumentsCreatesPrivateDefaultData()
    {
        string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"APEX Trading AI");
        Assert.False(Directory.Exists(root)||File.Exists(root),"Real KnownFolder default exists: refuse without touching it");
        Assert.DoesNotContain(Process.GetProcesses(),p=>p.ProcessName.Equals("ApexDesktop",StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(),p=>p.Port==5077||p.Port==5078);
        await using var h=new WindowsTestHost(WindowsTestHost.Package,root);h.Port=5077;
        // Observe the actual OS association; do NOT replace/register a test handler.
        using var observer=StartObserver();
        Assert.Equal("OBSERVER_READY",await observer.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
        var observation=observer.StandardOutput.ReadToEndAsync();var observerError=observer.StandardError.ReadToEndAsync();
        int[] ownedShell=Array.Empty<int>();
        try
        {
            await h.StartAsync(Array.Empty<string>());
            await h.NativeDialogAsync();await h.ReadyAsync();h.Credentials=WindowsLocalAccess.Load(root,"presentation");
            await observer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(50));Assert.Equal(0,observer.ExitCode);
            using var proof=JsonDocument.Parse(await observation);var shell=proof.RootElement;
            WindowsTestHost.Evidence("default-shell",shell);
            ownedShell=shell.GetProperty("ownedUrlPids").EnumerateArray().Select(p=>p.GetInt32()).ToArray();
            Assert.True(shell.GetProperty("observed").GetBoolean(),"Native shell association did not yield an observed URL process; hosted UI gate FAILED, not substituted");
            Assert.True(shell.GetProperty("httpReadyAtObservation").GetBoolean(),"Browser process appeared without ready HTTP");
            Assert.NotEmpty(shell.GetProperty("handler").GetString()!);
            var status=await h.Json("/api/status");Assert.Empty(status.GetProperty("data").GetProperty("accounts").EnumerateArray());
            h.CheckPrivateTree(root);h.Modules();
            await h.StopAsync();
            var privateBefore=WindowsTestHost.Inventory(Path.Combine(root,"private"));
            await h.StartAsync(new[]{"--show-local-access"});await h.NativeDialogAsync();
            await h.Child!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));Assert.Equal(0,h.Child.ExitCode);
            WindowsTestHost.SameInventory(privateBefore,Path.Combine(root,"private"));
            Assert.DoesNotContain(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(),p=>p.Port==h.Port);
            WindowsTestHost.Evidence("first-run-ui",new{noArguments=true,realKnownFolder=true,firstNativeModalAcknowledged=true,dpapiCurrentUserMatched=true,zeroAccounts=true,showLocalAccessNativeModal=true,credentialsUnchanged=true,noShowListener=true,brokerTested=false});
        }
        finally
        {
            if(!observer.HasExited){observer.Kill(true);await observer.WaitForExitAsync();}
            _=await observerError;
            WindowsTestHost.Evidence("shell-observer-exit-"+observer.Id,new{pid=observer.Id,exited=observer.HasExited,kind="default-association-observer"});
            // Only newly observed processes with our exact nonsecret startup URL.
            foreach(int pid in ownedShell)
            {try{using var p=Process.GetProcessById(pid);p.Kill(true);await p.WaitForExitAsync();WindowsTestHost.Evidence("shell-exit-"+pid,new{pid,exited=p.HasExited,ownership="new exact-loopback-URL process"});}catch(ArgumentException){WindowsTestHost.Evidence("shell-exit-"+pid,new{pid,exited=true,alreadyExited=true});}}
        }
    }
    private static Process StartObserver()
    {
        // Command lines remain private. Only handler, PID, and booleans leave this observer.
        string script="""
$ErrorActionPreference='Stop'
$handler=(Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice' -ErrorAction SilentlyContinue).ProgId
if (-not $handler) {$handler=(Get-Item 'Registry::HKEY_CLASSES_ROOT\http\shell\open\command').GetValue('')}
$baseline=@(Get-CimInstance Win32_Process|ForEach-Object {$_.ProcessId})
Write-Output 'OBSERVER_READY'
$end=[DateTime]::UtcNow.AddSeconds(40);$found=@();$ready=$false
while([DateTime]::UtcNow -lt $end) {
 $found=@(Get-CimInstance Win32_Process|Where-Object {$baseline -notcontains $_.ProcessId -and $_.CommandLine -and $_.CommandLine.Contains('http://127.0.0.1:5077/?view=naval') -and $_.Name -match '^(msedge|chrome|firefox|brave|opera)\.exe$'})
 if($found.Count -gt 0){try {$r=[Net.HttpWebRequest]::Create('http://127.0.0.1:5077/');$r.Proxy=$null;$r.Timeout=2000;$response=$r.GetResponse();$ready=$true;$response.Close()}catch{if($_.Exception.InnerException.Response.StatusCode -eq 401 -or $_.Exception.Response.StatusCode -eq 401){$ready=$true}};break}
 Start-Sleep -Milliseconds 100
}
@{handler=$handler;observed=($found.Count -gt 0);httpReadyAtObservation=$ready;ownedUrlPids=@($found|ForEach-Object {[int]$_.ProcessId});associationOverridden=$false;renderingProof=$false}|ConvertTo-Json -Compress
""";
        var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe")){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
        start.ArgumentList.Add("-NoProfile");start.ArgumentList.Add("-EncodedCommand");start.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)));return Process.Start(start)!;
    }
}
