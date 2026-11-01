using System.Runtime.InteropServices;
using System.Text;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.Windows.Tests;
[Collection("Extracted Windows")]
public sealed class WindowsOkxSimpleConnectTests
{
    // Break caught: old metadata-first wizard has no DEMO/LIVE control and does
    // not provide the one local connect action at the three-secret boundary.
    [Fact,Trait("Gate","SimpleConnectSource")]
    public async Task One_form_has_demo_default_disabled_live_three_masked_fields_and_connect_action()
    {
        Assert.True(OperatingSystem.IsWindows());
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var binding=OkxConfiguration.Bind(new(){Schema="okx-naval-config-v1",Scopes=new[]{new OkxScopeOptions{ScopeId="owned-form",Environment="OkxDemo",Region="EEA",ExpectedUid="123456",ExpectedMainUid="123456",QuoteCurrency="EUR",CredentialBindingId="owned-form",SafetyVersion="okx-cash-v1"}}})[0];
        var pending=WindowsOkxProvisioning.ShowSecretsAsync(binding,stop.Token);
        try
        {
            var w=await WindowsNativeUi.Window(Environment.ProcessId,"APEX — lokalny klucz OKX");
            Assert.True(Enumerable.Range(100,3).All(i=>WindowsNativeUi.Masked(w,i)),"Three native masked fields required");
            Assert.NotEqual(IntPtr.Zero,GetDlgItem(w,80));
            var classes=new List<string>();EnumChildWindows(w,(child,_)=>{var name=new StringBuilder(128);GetClassNameW(child,name,name.Capacity);classes.Add(name.ToString());return true;},IntPtr.Zero);Assert.Equal(3,classes.Count(c=>c=="Edit"));Assert.DoesNotContain("ComboBox",classes);
            Assert.NotEqual(IntPtr.Zero,GetDlgItem(w,81));Assert.False(IsWindowEnabled(GetDlgItem(w,81)));
            var text=new StringBuilder(80);GetWindowTextW(GetDlgItem(w,1),text,text.Capacity);Assert.Equal("Połącz",text.ToString());
            WindowsNativeUi.Click(w,1);await Task.Delay(100);Assert.True(WindowsNativeUi.Visible(w));
            WindowsNativeUi.Cancel(w);Assert.Null((await pending).Values);
        }
        finally{stop.Cancel();await pending;}
    }
    [Fact,Trait("Gate","SimpleConnectSource")]
    public async Task Additive_dpapi_store_empty_account_restart_duplicate_and_no_activation()
    {
        string root=WindowsTestHost.NewRoot();using(var admitted=Apex.Desktop.Services.WindowsPrivateData.Open(root,AppContext.BaseDirectory)){}
        byte[][] values=Enumerable.Range(0,3).Select(_=>Encoding.ASCII.GetBytes(Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)))).ToArray();
        try
        {
            var config=new Apex.Desktop.Services.ConfigStore(root);byte[] before=File.ReadAllBytes(config.Path_);
            var observed=new Apex.Adapters.Okx.Execution.OkxEnrollmentObservation("123456","123456","Demo","DemoTradingApi",Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(values[0])).ToLowerInvariant(),DateTimeOffset.UtcNow,System.Collections.Immutable.ImmutableArray<Apex.Adapters.Okx.Execution.OkxExecutionBalanceDetailDto>.Empty,System.Collections.Immutable.ImmutableArray<Apex.Adapters.Okx.Execution.OkxExecutionInstrumentDto>.Empty);
            var store=new WindowsOkxReadStore(root);var account=store.Save(observed,values);using(var opened=store.Open(account.ScopeId)){Assert.Equal(account,opened.Account);Assert.True(values.Select((v,i)=>v.AsSpan().SequenceEqual(opened.Values[i])).All(v=>v),"DPAPI roundtrip mismatch; values withheld");}
            Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(config.Path_)),"Existing config rewritten");
            var inventory=WindowsTestHost.Inventory(root);Assert.Throws<InvalidDataException>(()=>store.Save(observed,values));WindowsTestHost.SameInventory(inventory,root);
            await using var runtime=OkxNavalRuntime.Compose(root,OkxConfiguration.Bind(config.Config.OkxNaval));await using var control=new OkxOperatorControl(runtime,config);
            string status=System.Text.Json.JsonSerializer.Serialize(control.ReadStatus());Assert.Contains("SAVED_DISCONNECTED_RECONNECT_REQUIRED",status);Assert.Contains("\"armed\":false",status);Assert.Contains("\"quote\":null",status);
            foreach(string action in new[]{"arm","start","connect","baseline","recover"})Assert.Equal("READ_ONLY_NO_ACTIVATION",await control.ExecuteAsync(account.ScopeId,action,Guid.NewGuid().ToString("N"),default));
            Assert.Empty(runtime.Bindings);Assert.False(Directory.Exists(Path.Combine(root,"okx")));Assert.Single(new WindowsOkxReadStore(root).List());
            foreach(var secret in values)foreach(var file in Directory.GetFiles(root,"*",SearchOption.AllDirectories))Assert.False(File.ReadAllBytes(file).AsSpan().IndexOf(secret)>=0,"Plaintext persisted; value withheld");
            WindowsTestHost.Evidence("simple-connect-empty-store",new{emptyAccountVisible=true,quoteUnknown=true,legacyConfigByteIdentical=true,duplicatePreserved=true,restartDisconnected=true,activationDenied=true,networkCalled=false,fullProtectedPositive=false});
        }
        finally{foreach(var v in values)System.Security.Cryptography.CryptographicOperations.ZeroMemory(v);Directory.Delete(root,true);}
    }
    [Fact,Trait("Gate","SimpleConnectSource")]
    public async Task Async_native_form_refusal_stays_open_and_cancel_revokes_pending_work()
    {
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(20));var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var ended=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[][]? captured=null;
        var pending=WindowsOkxSimpleConnect.FormAsync(stop.Token,async(values,ct)=>{captured=values;entered.SetResult();try{await Task.Delay(Timeout.InfiniteTimeSpan,ct);return null;}finally{ended.SetResult();}});
        try
        {
            var w=await WindowsNativeUi.Window(Environment.ProcessId,"APEX — lokalny klucz OKX");for(int i=100;i<103;i++)WindowsNativeUi.Text(w,i,"owned-synthetic-not-broker");WindowsNativeUi.Click(w,1);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));WindowsNativeUi.Cancel(w);var result=await pending.WaitAsync(TimeSpan.FromSeconds(5));Assert.Null(result.Values);Assert.True(ended.Task.IsCompleted);Assert.NotNull(captured);Assert.True(captured.All(v=>v.All(b=>b==0)),"Cancelled form retained owned input bytes; values withheld");
        }
        finally{stop.Cancel();await pending;}
    }
    [DllImport("user32.dll")]private static extern IntPtr GetDlgItem(IntPtr w,int id);
    [DllImport("user32.dll")]private static extern bool IsWindowEnabled(IntPtr w);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowTextW(IntPtr w,StringBuilder text,int count);
    private delegate bool EnumChild(IntPtr w,IntPtr p);
    [DllImport("user32.dll")]private static extern bool EnumChildWindows(IntPtr w,EnumChild callback,IntPtr p);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetClassNameW(IntPtr w,StringBuilder name,int count);
}
