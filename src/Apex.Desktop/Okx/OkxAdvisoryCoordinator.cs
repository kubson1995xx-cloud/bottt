using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Naval;
using Apex.Desktop.Services;
namespace Apex.Desktop.Okx;

// One public-only asynchronous owner, not in CanExecute/account readiness or the trading lane.
internal sealed class OkxAdvisoryCoordinator : IAsyncDisposable
{
    internal static string LocalRoot=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"APEX-Advisory");
    readonly string root;readonly object sync=new();readonly CancellationTokenSource lifetime=new();CancellationTokenSource? round;Task? work;bool closed;int setupEpoch;
    readonly Dictionary<string,object> status=new(StringComparer.Ordinal);
    readonly SemaphoreSlim seriesLane=new(1,1);readonly Dictionary<string,NavalSeries> seriesCache=new(StringComparer.Ordinal);DateTimeOffset seriesDay;
    // One completed daily window for BOTH strategy and analysts. A rolling endpoint
    // cannot silently rebase its 299/300-bar boundary after proof publication.
    internal NavalSeries PinSeries(NavalSeries incoming,DateTimeOffset cutoff)
    {
        lock(sync)
        {
            if(seriesDay==cutoff&&seriesCache.TryGetValue(incoming.Instrument,out var current))return current;
            _=OkxAdvisoryCanonicalData.Serialize(incoming,cutoff);
            if(seriesDay!=cutoff){seriesCache.Clear();seriesDay=cutoff;}
            if(seriesCache.Count>=3)throw new InvalidDataException("AI_SERIES_UNIVERSE_LIMIT");
            var frozen=incoming with{Bars=Array.AsReadOnly(incoming.Bars.ToArray())};seriesCache.Add(incoming.Instrument,frozen);return frozen;
        }
    }
    internal async Task<NavalSeries> ReadSeriesAsync(OkxPublicMarketFeed feed,NavalInstrument spec,DateTimeOffset cutoff,CancellationToken ct)
    {
        await seriesLane.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock(sync)if(seriesDay==cutoff&&seriesCache.TryGetValue(spec.Instrument,out var current))return current;
            return PinSeries(await feed.ReadSeriesAsync(spec,ct).ConfigureAwait(false),cutoff);
        }
        finally{seriesLane.Release();}
    }
    internal OkxAdvisoryCoordinator(string root){this.root=root;foreach(string p in new[]{"BTC-EUR","ETH-EUR","SOL-EUR"})status[p]=new{instrument=p,state="MISSING",reason="AI_LOCAL_SETUP_REQUIRED"};}
    internal void Start(){lock(sync){if(!closed&&work==null)work=Task.Run(Run);}}
    internal void CancelAnalyses(){lock(sync)round?.Cancel();}
    internal void SetupChanged(){lock(sync){setupEpoch++;round?.Cancel();}}
    internal object ReadStatus(){lock(sync)return new{mode="DUAL_CONFIRMATION_NEW_BUYS_ONLY",entryPolicy="STRATEGY_ENTRY_AND_BOTH_CURRENT_BUY",exitsBypass=true,setupAvailable=OperatingSystem.IsWindows(),pairs=status.Values.ToArray()};}
    void Set(string p,string state,string reason,OkxAdvisorySnapshot? snapshot=null)
    {lock(sync)status[p]=new{instrument=p,state,reason,cutoff=snapshot?.Cutoff,expiresUtc=snapshot?.ExpiresUtc,inference=snapshot?.Inference,generation=snapshot?.Generation,tools=snapshot==null?null:new[]{snapshot.Vibe,snapshot.Agents}};}
    async Task Run()
    {
        DateTimeOffset lastDay=default;int lastEpoch=-1;
        using var feed=new OkxPublicMarketFeed(TimeProvider.System);
        while(!lifetime.IsCancellationRequested)
        {
            var day=new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date,TimeSpan.Zero);int epoch;lock(sync)epoch=setupEpoch;
            if(day!=lastDay||epoch!=lastEpoch)
            {
                lastDay=day;lastEpoch=epoch;
                using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);lock(sync)round=cancellation;
                foreach(string pair in new[]{"BTC-EUR","ETH-EUR","SOL-EUR"})
                {
                    if(cancellation.IsCancellationRequested)break;
                    try
                    {
                        if(!File.Exists(Path.Combine(root,"installed","installation.json"))){Set(pair,"MISSING","AI_LOCAL_SETUP_REQUIRED");continue;}
                        Set(pair,"REFRESHING","PUBLIC_ANALYSIS_BACKGROUND");
                        var series=await ReadSeriesAsync(feed,new NavalInstrument(pair,pair.Split('-')[0],"EUR",.00000001m,.00000001m,.01m),day,cancellation.Token).ConfigureAwait(false);
                        byte[] raw=OkxAdvisoryCanonicalData.Serialize(series,day);
                        string result=await RunCompanion(root,"refresh",raw,cancellation.Token).ConfigureAwait(false);
                        using var store=new WindowsOkxAdvisoryStore(Path.Combine(root,"reports"));using var reports=store.Acquire(pair,OkxAdvisorySnapshot.Hash(raw),day,DateTimeOffset.UtcNow);
                        Set(pair,reports==null?"UNAVAILABLE":reports.Snapshot.AllowsEntry?"DUAL_BUY":"ENTRY_VETO",result,reports?.Snapshot);
                    }
                    catch(OperationCanceledException){Set(pair,"CANCELLED","AI_ANALYSIS_CANCELLED_NO_IMMEDIATE_RETRY");}
                    catch(Exception e) when(e is not OutOfMemoryException){Set(pair,"UNAVAILABLE","AI_RUNTIME_AUTH_DATA_OR_STORAGE_UNAVAILABLE");}
                }
                lock(sync)if(ReferenceEquals(round,cancellation))round=null;
            }
            try{await Task.Delay(TimeSpan.FromSeconds(15),lifetime.Token).ConfigureAwait(false);}catch(OperationCanceledException){break;}
        }
    }
    internal static async Task<string> RunCompanion(string root,string mode,byte[]? input,CancellationToken ct)
    {
        if(!OperatingSystem.IsWindows())return "AI_NATIVE_WINDOWS_REQUIRED";
        string install=Path.Combine(root,"installed"),home=Path.Combine(root,"inference-home"),control=Path.Combine(root,"control");
        using var lease=WindowsPrivateData.Open(control,AppContext.BaseDirectory);
        string cancel=Path.Combine(control,"cancel-"+Guid.NewGuid().ToString("N"));
        string python=Path.Combine(install,"hermes","env","Scripts","python.exe"),script=Path.Combine(install,"bridge","local_companion.py");
        // Only fixed locally installed paths; no endpoint/body/env executable injection.
        using var metadata=OkxAdvisorySnapshot.Document(File.ReadAllBytes(Path.Combine(install,"installation.json")));
        var m=metadata.RootElement;
        if(m.GetProperty("Schema").GetString()!="apex-advisory-installed-v1"||OkxAdvisorySnapshot.Hash(File.ReadAllBytes(python))!=m.GetProperty("PythonSha256").GetString()||OkxAdvisorySnapshot.Hash(File.ReadAllBytes(script))!=m.GetProperty("BridgeSha256").GetString())return "AI_INSTALL_IDENTITY_REFUSED";
        var start=StartInfo(python,home);start.ArgumentList.Add("-I");start.ArgumentList.Add(script);start.ArgumentList.Add(mode);start.ArgumentList.Add(root);start.ArgumentList.Add(cancel);
        using var process=new Process{StartInfo=start};process.Start();
        try
        {
            if(input!=null)await process.StandardInput.BaseStream.WriteAsync(input,ct).ConfigureAwait(false);process.StandardInput.Close();
            using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(mode=="login"?5:25));using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,timeout.Token);
            char[] buffer=new char[4097];var output=new StringBuilder();int n;
            while((n=await process.StandardOutput.ReadAsync(buffer.AsMemory(0,Math.Min(buffer.Length,4097-output.Length)),linked.Token).ConfigureAwait(false))>0)
            {output.Append(buffer,0,n);if(output.Length>4096)throw new InvalidDataException("AI_IPC_LIMIT");}
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            string text=output.ToString().Trim();
            if(process.ExitCode!=0)return "AI_LOCAL_COMPANION_REFUSED";
            if(text is "AI_LOCAL_SIGN_IN_REQUIRED" or "AI_LOCAL_AUTH_UNAVAILABLE" or "AI_LOCAL_AUTH_READY" or "AI_LOCAL_LOGIN_COMPLETED")return text;
            using var result=OkxAdvisorySnapshot.Document(Encoding.UTF8.GetBytes(text),4096);
            return result.RootElement.GetProperty("reason").GetString() is "REUSED"?"REUSED":"ANALYSIS_COMPLETED_CHECK_TOOL_STATUS";
        }
        finally
        {
            if(!process.HasExited)
            {
                File.WriteAllText(cancel,"cancel");
                try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);}catch(TimeoutException){process.Kill(true);await process.WaitForExitAsync().ConfigureAwait(false);}
            }
            if(File.Exists(cancel))File.Delete(cancel);
        }
    }
    internal static ProcessStartInfo StartInfo(string python,string home)
    {
        string windows=Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var p=new ProcessStartInfo{FileName=python,WorkingDirectory=home,UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=false};p.Environment.Clear();
        foreach(var pair in new[]{("SYSTEMROOT",windows),("WINDIR",windows),("PATH",Path.GetDirectoryName(python)+Path.PathSeparator+Path.Combine(windows,"System32")+Path.PathSeparator+windows),("HOME",home),("USERPROFILE",home),("HERMES_HOME",Path.Combine(home,"hermes")),("APPDATA",Path.Combine(home,"AppData","Roaming")),("LOCALAPPDATA",Path.Combine(home,"AppData","Local")),("TEMP",Path.Combine(home,"tmp")),("TMP",Path.Combine(home,"tmp")),("PYTHONUTF8","1"),("PYTHONIOENCODING","utf-8"),("PYTHONNOUSERSITE","1"),("PIP_CONFIG_FILE","NUL"),("NO_COLOR","1")})p.Environment[pair.Item1]=pair.Item2;
        return p;
    }
    public async ValueTask DisposeAsync()
    {Task? pending;lock(sync){if(closed)return;closed=true;lifetime.Cancel();round?.Cancel();pending=work;}if(pending!=null)await pending.ConfigureAwait(false);lifetime.Dispose();}
}
