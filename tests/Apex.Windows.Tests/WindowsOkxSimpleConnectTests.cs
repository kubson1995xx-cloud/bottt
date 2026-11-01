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
            Assert.Equal(new IntPtr(1),SendMessageW(GetDlgItem(w,80),0xF0,IntPtr.Zero,IntPtr.Zero));
            Assert.NotEqual(IntPtr.Zero,GetDlgItem(w,81));Assert.False(IsWindowEnabled(GetDlgItem(w,81)));
            var text=new StringBuilder(80);GetWindowTextW(GetDlgItem(w,1),text,text.Capacity);Assert.Equal("Połącz",text.ToString());
            WindowsNativeUi.Click(w,1);await Task.Delay(100);Assert.True(WindowsNativeUi.Visible(w));
            WindowsNativeUi.Cancel(w);Assert.Null((await pending).Values);
        }
        finally{stop.Cancel();await pending;}
    }
    [DllImport("user32.dll")]private static extern IntPtr GetDlgItem(IntPtr w,int id);
    [DllImport("user32.dll")]private static extern bool IsWindowEnabled(IntPtr w);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowTextW(IntPtr w,StringBuilder text,int count);
    [DllImport("user32.dll")]private static extern IntPtr SendMessageW(IntPtr w,uint msg,IntPtr a,IntPtr b);
}
