using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Xunit;
namespace Apex.Tests;
internal sealed class HostChild : IDisposable
{
    internal readonly Process Process;
    private readonly Channel<string> lines=Channel.CreateUnbounded<string>();
    private readonly StringBuilder log=new();
    private readonly Task pump,error;
    internal string Log=>log.ToString();
    private readonly string scenario;
    private HostChild(Process process,string scenario)
    {
        this.scenario=scenario;
        Process=process;
        pump=Task.Run(async()=>{try{while(await Process.StandardOutput.ReadLineAsync() is {} s){log.AppendLine(s);await lines.Writer.WriteAsync(s);}}finally{lines.Writer.TryComplete();}});
        error=Task.Run(async()=>{string s=await Process.StandardError.ReadToEndAsync();lock(log)log.Append(s);});
    }
    internal static HostChild Start(string root,string scenario,bool production=false)
    {
        var p=new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")??"dotnet"){UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        string home=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),tmp=Environment.GetEnvironmentVariable("TMPDIR")!;
        p.Environment.Clear();foreach(var v in new Dictionary<string,string>{{"HOME",home},{"PATH","/usr/bin:/bin"},{"DOTNET_ROOT",Path.GetDirectoryName(p.FileName)!},{"DOTNET_EnableDiagnostics","0"},{"COMPlus_EnableDiagnostics","0"},{"LANG","C.UTF-8"},{"TMPDIR",tmp}})p.Environment[v.Key]=v.Value;
        foreach(string a in production?new[]{typeof(Apex.Okx.DemoAcceptance.Program).Assembly.Location,"--demo-bot"}:new[]{typeof(SyntheticHostEntry).Assembly.Location,"--okx-linux-demo-host",scenario,root})p.ArgumentList.Add(a);
        return new(Process.Start(p)!,scenario);
    }
    internal async Task Send(string command){lock(log)log.AppendLine("> "+command);await Process.StandardInput.WriteLineAsync(command);await Process.StandardInput.FlushAsync();}
    internal async Task<string> Expect(string marker)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try{while(await lines.Reader.WaitToReadAsync(timeout.Token))while(lines.Reader.TryRead(out var line))if(line.Contains(marker,StringComparison.Ordinal))return line;}
        catch(OperationCanceledException){}
        throw new Xunit.Sdk.XunitException("Expected "+marker+"; output: "+Log);
    }
    internal async Task Command(string command,string marker){await Send(command);await Expect(marker);}
    internal async Task Exit(int code){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));await Process.WaitForExitAsync(timeout.Token);await Task.WhenAll(pump,error);Assert.True(Process.ExitCode==code,"Child code "+Process.ExitCode+"; "+Log);}
    public void Dispose()
    {
        if(!Process.HasExited){Process.Kill(true);Process.WaitForExit();}Task.WaitAll(pump,error);
        string text=Log;
        foreach(var b in new[]{OperationalPeer.Key,OperationalPeer.Secret,OperationalPeer.Pass})Assert.DoesNotContain(Encoding.ASCII.GetString(b),text);
        Assert.DoesNotContain(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(OperationalPeer.Key)).ToLowerInvariant(),text);
        var dir=new DirectoryInfo(AppContext.BaseDirectory);while(dir!=null&&!Directory.Exists(Path.Combine(dir.FullName,"tests/Apex.Adapters.Okx.Tests/Fixtures")))dir=dir.Parent;
        string evidence=Path.Combine(dir!.FullName,scenario.StartsWith("mixed-",StringComparison.Ordinal)?"artifacts/operational-mixed-account/native-transcripts":"artifacts/operational-linux-host/native-transcripts");Directory.CreateDirectory(evidence);
        File.WriteAllText(Path.Combine(evidence,scenario+"-"+Guid.NewGuid().ToString("N")+".log"),text);Process.Dispose();
    }
}
