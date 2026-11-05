using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Apex.Desktop.Okx;
using Apex.Windows.Tests;
using Xunit;
namespace Apex.ControllerComposition.Tests;
[Collection("Composition")]
public sealed class NativePairingTests
{
    internal static async Task<bool> Wizard(string root,WindowsOkxControllerTransportConfig config,bool trust)
    {
        string exe=Environment.GetEnvironmentVariable("APEX_COMPOSITION_PACKAGE_EXE")??throw new Exception("EXTRACTED_EXE_REQUIRED");
        var start=new ProcessStartInfo(exe){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string a in new[]{"--data",root,"--pair-controller"})start.ArgumentList.Add(a);
        using var child=Process.Start(start)!;var stdout=child.StandardOutput.BaseStream.CopyToAsync(Stream.Null);var stderr=child.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try
        {
            IntPtr form=IntPtr.Zero;
            await WindowsTestHost.Until(()=>Task.FromResult((form=Window(child.Id,"APEX — parowanie kontrolera VPS"))!=IntPtr.Zero),"Extracted pairing form missing");
            Click(GetDlgItem(form,1)); // invalid empty input must stay open
            await WindowsTestHost.Until(()=>Task.FromResult(Children(form).Contains("Uzupełnij wszystkie pola",StringComparison.Ordinal)),"Invalid native input validation not reached");
            Assert.True(IsWindowVisible(form));Assert.False(File.Exists(Path.Combine(root,"controller-connection.json")));
            foreach(var (id,value) in new[]{(100,config.Host),(101,config.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)),(102,config.User),(103,"ssh-ed25519 "+config.HostKey)})Assert.True(SetWindowTextW(GetDlgItem(form,id),value));
            Click(GetDlgItem(form,1));
            IntPtr confirm=IntPtr.Zero;
            await WindowsTestHost.Until(()=>Task.FromResult((confirm=Window(child.Id,"APEX — potwierdź zaufany klucz hosta"))!=IntPtr.Zero),"Independent host-pin trust dialog missing");
            string text=Children(confirm);string fingerprint="SHA256:"+Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Convert.FromBase64String(config.HostKey))).TrimEnd('=');
            Assert.True(text.Contains(fingerprint,StringComparison.Ordinal),"Exact operator host fingerprint not displayed");Assert.True(text.Contains(config.HostKey,StringComparison.Ordinal));
            Click(GetDlgItem(confirm,trust?1:2));
            if(trust)
            {
                IntPtr done=IntPtr.Zero;
                await WindowsTestHost.Until(()=>Task.FromResult((done=Window(child.Id,"APEX — konfiguracja kontrolera"))!=IntPtr.Zero),"Pairing completion modal missing");
                Assert.True(File.Exists(Path.Combine(root,"controller-public-key.txt")),"NATIVE_PROVISIONING_DID_NOT_COMPLETE");Click(GetDlgItem(done,1));
            }
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));await Task.WhenAll(stdout,stderr);Assert.Equal(trust?0:2,child.ExitCode);
            WindowsTestHost.Evidence("native-pairing-"+child.Id,new{extractedExe=true,invalidInputStaysOpen=true,hostFingerprintDisplayed=true,explicitTrust=trust,noNetworkOrBrokerConnect=true,processExited=true});
            return trust;
        }
        finally{if(!child.HasExited){child.Kill(true);await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));}}
    }
    [Fact] public async Task Extracted_native_pairing_trust_cancel_preserves_existing_profile()
    {
        using var root=new CompositionTests.Root();
        // Main owns/creates this empty lock during explicit startup; pin it before comparison.
        File.WriteAllBytes(Path.Combine(root.Path_,"apex.lock"),Array.Empty<byte>());
        var before=CompositionTests.Inventory(root.Path_);
        var wire=new byte[]{0,0,0,11,115,115,104,45,101,100,50,53,53,49,57,0,0,0,32}.Concat(Enumerable.Repeat((byte)7,32)).ToArray();
        Assert.False(await Wizard(root.Path_,new("vps.example",22,"apex",new string('0',64),Convert.ToBase64String(wire)),false));
        Assert.Equal(before,CompositionTests.Inventory(root.Path_));
    }
    private static IntPtr Window(int pid,string title)
    {IntPtr found=IntPtr.Zero;EnumWindows((h,_)=>{GetWindowThreadProcessId(h,out uint owner);var text=new StringBuilder(256);GetWindowTextW(h,text,text.Capacity);if(owner==pid&&text.ToString()==title&&IsWindowVisible(h)){found=h;return false;}return true;},IntPtr.Zero);return found;}
    private static string Children(IntPtr w)
    {var text=new StringBuilder();EnumChildWindows(w,(h,_)=>{var s=new StringBuilder(4096);GetWindowTextW(h,s,s.Capacity);text.Append(s);return true;},IntPtr.Zero);return text.ToString();}
    private static void Click(IntPtr button)
    {
        Assert.NotEqual(IntPtr.Zero,button);Assert.True(IsWindowVisible(button));SetForegroundWindow(GetAncestor(button,2));
        Assert.True(GetWindowRect(button,out var r));int x=(r.Left+r.Right)/2,y=(r.Top+r.Bottom)/2;
        Assert.Equal(button,WindowFromPoint(new Point{X=x,Y=y}));Assert.True(SetCursorPos(x,y));mouse_event(2,0,0,0,UIntPtr.Zero);mouse_event(4,0,0,0,UIntPtr.Zero);
    }
    private delegate bool EnumProc(IntPtr w,IntPtr data);
    [StructLayout(LayoutKind.Sequential)]private struct Rect{internal int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]private struct Point{internal int X,Y;}
    [DllImport("user32.dll")]private static extern bool EnumWindows(EnumProc callback,IntPtr data);
    [DllImport("user32.dll")]private static extern bool EnumChildWindows(IntPtr parent,EnumProc callback,IntPtr data);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr w,out uint pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowTextW(IntPtr w,StringBuilder text,int count);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern bool SetWindowTextW(IntPtr w,string text);
    [DllImport("user32.dll")]private static extern IntPtr GetDlgItem(IntPtr w,int id);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(IntPtr w);
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(IntPtr w);
    [DllImport("user32.dll")]private static extern IntPtr GetAncestor(IntPtr w,uint flags);
    [DllImport("user32.dll")]private static extern bool GetWindowRect(IntPtr w,out Rect rect);
    [DllImport("user32.dll")]private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")]private static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")]private static extern void mouse_event(uint flags,uint dx,uint dy,uint data,UIntPtr extra);
}
