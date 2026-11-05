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
        string stage="FORM";
        try
        {
            IntPtr form=IntPtr.Zero;
            await WindowsTestHost.Until(()=>Task.FromResult((form=Window(child.Id,"APEX — parowanie kontrolera VPS"))!=IntPtr.Zero),"Extracted pairing form missing");
            stage="INVALID_INPUT";Enter(form);
            await WindowsTestHost.Until(()=>Task.FromResult(Children(form).Contains("Uzupełnij wszystkie pola",StringComparison.Ordinal)),"Invalid native input validation not reached");
            Assert.True(IsWindowVisible(form));Assert.False(File.Exists(Path.Combine(root,"controller-connection.json")));
            stage="FIELDS";
            string[] expected={config.Host,config.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),config.User,"ssh-ed25519 "+config.HostKey};
            for(int i=0;i<4;i++)SendText(GetDlgItem(form,100+i),0xC,IntPtr.Zero,expected[i]);
            bool fieldsAgree=Enumerable.Range(0,4).All(i=>Text(GetDlgItem(form,100+i))==expected[i]);
            Assert.True(fieldsAgree,"NATIVE_METADATA_FIELD_READBACK_MISMATCH");
            var input=typeof(Apex.Desktop.Program).Assembly.GetType("Apex.Desktop.Okx.WindowsOkxControllerPairing")!.GetMethod("Input",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
            object?[] operands={expected.Select(System.Text.Encoding.UTF8.GetBytes).ToArray(),null,0,null,null};
            bool parserAccepts=(bool)input.Invoke(null,operands)!;Assert.True(parserAccepts,"PRODUCTION_METADATA_PARSER_REFUSED_NATIVE_OPERANDS");
            stage="SUBMIT";Enter(form);
            IntPtr confirm=IntPtr.Zero;
            await WindowsTestHost.Until(()=>Task.FromResult((confirm=Window(child.Id,"APEX — potwierdź zaufany klucz hosta"))!=IntPtr.Zero),"Independent host-pin trust dialog missing");
            stage="TRUST_TEXT";
            string fingerprint="SHA256:"+Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Convert.FromBase64String(config.HostKey))).TrimEnd('=');
            await WindowsTestHost.Until(()=>{string text=Children(confirm);return Task.FromResult(text.Contains(fingerprint,StringComparison.Ordinal)&&text.Contains(config.HostKey,StringComparison.Ordinal));},"Exact operator host fingerprint not displayed");
            stage="TRUST_CLICK";Click(GetDlgItem(confirm,trust?1:2));
            if(trust)
            {
                stage="COMPLETION";IntPtr done=IntPtr.Zero;
                await WindowsTestHost.Until(()=>Task.FromResult((done=Window(child.Id,"APEX — konfiguracja kontrolera"))!=IntPtr.Zero),"Pairing completion modal missing");
                Assert.True(File.Exists(Path.Combine(root,"controller-public-key.txt")),"NATIVE_PROVISIONING_DID_NOT_COMPLETE");Click(GetDlgItem(done,1));
            }
            stage="EXIT";await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));await Task.WhenAll(stdout,stderr);Assert.Equal(trust?0:2,child.ExitCode);
            WindowsTestHost.Evidence("native-pairing-"+child.Id,new{extractedExe=true,invalidInputStaysOpen=true,fieldsAgree,parserAccepts,keyboardSubmit=true,hostFingerprintDisplayed=true,explicitTrust=trust,noNetworkOrBrokerConnect=true,processExited=true});
            return trust;
        }
        catch{WindowsTestHost.Evidence("native-pairing-failure-"+child.Id,new{stage,childExited=child.HasExited});throw;}
        finally
        {
            if(!child.HasExited){child.Kill(true);await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));}
            await Task.WhenAll(stdout,stderr).WaitAsync(TimeSpan.FromSeconds(10));
            WindowsTestHost.Evidence("native-pairing-child-exit-"+child.Id,new{processExited=child.HasExited});
        }
    }
    [Fact] public async Task Extracted_native_pairing_trust_cancel_preserves_existing_profile()
    {
        using var root=new CompositionTests.Root();
        File.WriteAllBytes(Path.Combine(root.Path_,"apex.lock"),Array.Empty<byte>());
        var before=CompositionTests.Inventory(root.Path_);
        var wire=new byte[]{0,0,0,11,115,115,104,45,101,100,50,53,53,49,57,0,0,0,32}.Concat(Enumerable.Repeat((byte)7,32)).ToArray();
        Assert.False(await Wizard(root.Path_,new("vps.example",22,"apex",new string('0',64),Convert.ToBase64String(wire)),false));
        Assert.Equal(before,CompositionTests.Inventory(root.Path_));
    }
    private static IntPtr Window(int pid,string title)
    {IntPtr found=IntPtr.Zero;EnumWindows((h,_)=>{GetWindowThreadProcessId(h,out uint owner);var text=new StringBuilder(256);GetWindowTextW(h,text,text.Capacity);if(owner==pid&&text.ToString()==title&&IsWindowVisible(h)){found=h;return false;}return true;},IntPtr.Zero);return found;}
    private static string Children(IntPtr w)
    {var text=new StringBuilder();EnumChildWindows(w,(h,_)=>{text.Append(Text(h));return true;},IntPtr.Zero);return text.ToString();}
    private static string Text(IntPtr w){var s=new StringBuilder(4096);SendRead(w,0xD,new IntPtr(s.Capacity),s);return s.ToString();}
    private static void Foreground(IntPtr w)
    {SetForegroundWindow(w);Assert.True(SpinWait.SpinUntil(()=>GetForegroundWindow()==w,2000),"NATIVE_WINDOW_NOT_FOREGROUND");}
    private static void Enter(IntPtr w){Foreground(w);keybd_event(0x0D,0,0,UIntPtr.Zero);keybd_event(0x0D,0,2,UIntPtr.Zero);}
    private static void Click(IntPtr button)
    {
        Assert.NotEqual(IntPtr.Zero,button);Assert.True(IsWindowVisible(button));Foreground(GetAncestor(button,2));
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
    [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="SendMessageW")]private static extern IntPtr SendText(IntPtr w,uint msg,IntPtr a,string text);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="SendMessageW")]private static extern IntPtr SendRead(IntPtr w,uint msg,IntPtr a,StringBuilder text);
    [DllImport("user32.dll")]private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]private static extern IntPtr GetDlgItem(IntPtr w,int id);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(IntPtr w);
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(IntPtr w);
    [DllImport("user32.dll")]private static extern IntPtr GetAncestor(IntPtr w,uint flags);
    [DllImport("user32.dll")]private static extern bool GetWindowRect(IntPtr w,out Rect rect);
    [DllImport("user32.dll")]private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")]private static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")]private static extern void mouse_event(uint flags,uint dx,uint dy,uint data,UIntPtr extra);
    [DllImport("user32.dll")]private static extern void keybd_event(byte key,byte scan,uint flags,UIntPtr extra);
}
