using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Apex.Windows.Tests;

// Native association is untouched. WMI start notifications plus repeated snapshots
// observe transient shell processes as well as persistent URL-bearing processes.
// Command lines remain in private observer memory; no trace is written.
internal sealed class WindowsShellObserver : IAsyncDisposable
{
    private readonly Process child;
    private readonly Task<string> output, error;
    private WindowsShellObserver(Process child) { this.child=child; output=child.StandardOutput.ReadToEndAsync(); error=child.StandardError.ReadToEndAsync(); }
    internal static async Task<WindowsShellObserver> Start(Uri origin)
    {
        string url=new Uri(origin,"/?view=naval").AbsoluteUri;
        Assert.Equal("127.0.0.1",origin.Host); Assert.Equal("http",origin.Scheme);
        string script="""
$ErrorActionPreference='Stop'
$url='__URL__'
$source='ApexTask6Shell-'+[Guid]::NewGuid().ToString('N')
$handler=(Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice' -ErrorAction SilentlyContinue).ProgId
if(-not $handler){throw 'No native HTTP association'}
$browser='^(msedge|chrome|firefox|brave|opera)\.exe$'
function IsExact($p) { $p.CommandLine -and ([regex]::Matches($p.CommandLine,'"[^"]*"|[^\s]+')|Where-Object {$_.Value.Trim('"') -ceq $url}) }
$subscription=Register-WmiEvent -Class Win32_ProcessStartTrace -SourceIdentifier $source
try {
 $baseline=@(Get-CimInstance Win32_Process)
 $ids=@($baseline|ForEach-Object {[int]$_.ProcessId})
 $baselineBrowsers=@($baseline|Where-Object {$_.Name -match $browser}|ForEach-Object {[int]$_.ProcessId})
 $baselineUrl=@($baseline|Where-Object {$_.Name -match $browser -and (IsExact $_)}|ForEach-Object {[int]$_.ProcessId})
 $seen=@{};$uncertain=@{};$startEvents=0;$polls=0;$phase='observe'
 $began=[DateTime]::UtcNow;$end=$began.AddSeconds(8)
 Write-Output 'OBSERVER_READY'
 while([DateTime]::UtcNow -lt $end) {
  $polls++
  foreach($p in @(Get-CimInstance Win32_Process|Where-Object {$ids -notcontains $_.ProcessId -and $_.Name -match $browser})) {
   if(IsExact $p){$seen[[int]$p.ProcessId]=$p}
  }
  foreach($event in @(Get-Event -SourceIdentifier $source -ErrorAction SilentlyContinue)) {
   $n=$event.SourceEventArgs.NewEvent
   if($n.ProcessName -match $browser -and $ids -notcontains [int]$n.ProcessID) {
    $startEvents++
    $p=Get-CimInstance Win32_Process -Filter ('ProcessId='+$n.ProcessID) -ErrorAction SilentlyContinue
    if($p -and $p.CommandLine){if(IsExact $p){$seen[[int]$p.ProcessId]=$p}}
    elseif(-not $seen.ContainsKey([int]$n.ProcessID)){$uncertain[[int]$n.ProcessID]=$true}
   }
   Remove-Event -EventIdentifier $event.EventIdentifier
  }
  Start-Sleep -Milliseconds 50
 }
 $phase='cleanup';$cleanup=@()
 foreach($id in @($seen.Keys)) {
  $original=$seen[$id]
  $current=Get-CimInstance Win32_Process -Filter ('ProcessId='+$id) -ErrorAction SilentlyContinue
  $same=$current -and $current.CreationDate -eq $original.CreationDate -and $current.ExecutablePath -ceq $original.ExecutablePath -and (IsExact $current)
  if($same){Stop-Process -Id $id -Force -ErrorAction SilentlyContinue;Wait-Process -Id $id -Timeout 10 -ErrorAction SilentlyContinue}
  $cleanup+=@{pid=[int]$id;alreadyExited=(-not $current);identityMatched=[bool]$same;exited=(-not [bool](Get-Process -Id $id -ErrorAction SilentlyContinue))}
 }
 @{url=$url;handler=$handler;associationOverridden=$false;boundedSeconds=8;beganUtc=$began.ToString('o');endedUtc=[DateTime]::UtcNow.ToString('o');polls=$polls;processStartNotifications=$true;browserStartEvents=$startEvents;baselineBrowserPids=$baselineBrowsers;baselineExactUrlPids=$baselineUrl;newExactUrlPids=@($seen.Keys|ForEach-Object {[int]$_});unresolvedNewBrowserPids=@($uncertain.Keys|ForEach-Object {[int]$_});cleanup=$cleanup;rawCommandLinesRetained=$false}|ConvertTo-Json -Depth 5 -Compress
} catch {
 @{observerFailure=$true;phase=$phase;errorType=$_.Exception.GetType().FullName;errorId=$_.FullyQualifiedErrorId;line=$_.InvocationInfo.ScriptLineNumber}|ConvertTo-Json -Compress
 exit 1
} finally {
 Unregister-Event -SourceIdentifier $source -ErrorAction SilentlyContinue
 Get-Event -SourceIdentifier $source -ErrorAction SilentlyContinue|Remove-Event

}
""";
        script=script.Replace("__URL__",url);
        var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe")){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
        start.ArgumentList.Add("-NoProfile");start.ArgumentList.Add("-EncodedCommand");start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        var p=Process.Start(start)!;
        try
        {
            Assert.Equal("OBSERVER_READY",await p.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
            return new WindowsShellObserver(p);
        }
        catch { if(!p.HasExited){p.Kill(true);await p.WaitForExitAsync();}p.Dispose();throw; }
    }
    internal async Task AssertNoLaunch(string stage,Process refused) => await Observe(stage,false,refused);
    internal async Task AssertLaunch(string stage) => await Observe(stage,true,null);
    private async Task Observe(string stage,bool expected,Process? refused)
    {
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
        _=await error;
        using var document=JsonDocument.Parse(await output);var proof=document.RootElement;
        WindowsTestHost.Evidence((expected?"shell-control-":"no-shell-")+stage,proof);
        Assert.Equal(0,child.ExitCode);
        Assert.True(proof.GetProperty("polls").GetInt32()>1);
        if(expected) Assert.NotEmpty(proof.GetProperty("newExactUrlPids").EnumerateArray());
        else Assert.Empty(proof.GetProperty("newExactUrlPids").EnumerateArray());
        Assert.Empty(proof.GetProperty("unresolvedNewBrowserPids").EnumerateArray());
        if(refused!=null)
        {
            DateTime began=proof.GetProperty("beganUtc").GetDateTime().ToUniversalTime(),ended=proof.GetProperty("endedUtc").GetDateTime().ToUniversalTime();
            bool launchCovered=began<=refused.StartTime.ToUniversalTime();
            bool exitAndGraceCovered=ended>=refused.ExitTime.ToUniversalTime().AddSeconds(2);
            WindowsTestHost.Evidence("no-shell-window-"+stage,new{pid=refused.Id,launchCovered,exitAndGraceCovered,postExitGraceAtLeastTwoSeconds=exitAndGraceCovered});
            Assert.True(launchCovered&&exitAndGraceCovered,"Bounded shell observation did not cover refused process lifetime plus grace");
        }
    }
    public async ValueTask DisposeAsync()
    {
        if(!child.HasExited){try{await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));}catch(TimeoutException){child.Kill(true);await child.WaitForExitAsync();}}
        _=await output;_=await error;
        WindowsTestHost.Evidence("negative-shell-observer-exit-"+child.Id,new{pid=child.Id,exited=child.HasExited,kind="bounded native exact-URL process observer"});
        child.Dispose();
    }
}
