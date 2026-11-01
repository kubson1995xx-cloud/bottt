using System.Runtime.InteropServices;
using System.Text;
using Xunit;
namespace Apex.Windows.Tests;
// Test-only real Win32 observer and pointer driver. Never capture a credential/secret window.
internal static class WindowsNativeUi
{
    internal static async Task<IntPtr> Metadata(int pid)
        =>await Window(pid,"APEX — lokalny klucz OKX");
    internal static bool Masked(IntPtr w,int id)=>(GetWindowLongPtrW(GetDlgItem(w,id),-16).ToInt64()&0x20)!=0;
    internal static async Task<IntPtr> Window(int pid,string title)
    {
        IntPtr found=IntPtr.Zero;
        await WindowsTestHost.Until(()=>{EnumWindows((w,_)=>{GetWindowThreadProcessId(w,out uint p);var t=new StringBuilder(256);GetWindowText(w,t,t.Capacity);if(p==pid&&t.ToString()==title&&IsWindowVisible(w))found=w;return true;},IntPtr.Zero);return Task.FromResult(found!=IntPtr.Zero);},"Visible expected native form not found");
        return found;
    }
    internal static void Click(IntPtr w,int id)
    {
        var control=GetDlgItem(w,id);Assert.NotEqual(IntPtr.Zero,control);Assert.True(GetWindowRect(control,out var r));
        SetForegroundWindow(w);SetCursorPos((r.Left+r.Right)/2,(r.Top+r.Bottom)/2);mouse_event(2,0,0,0,UIntPtr.Zero);mouse_event(4,0,0,0,UIntPtr.Zero);
    }
    internal static void Text(IntPtr w,int id,string text)=>SendMessage(GetDlgItem(w,id),0xC,IntPtr.Zero,text);
    internal static bool Visible(IntPtr w)=>IsWindow(w)&&IsWindowVisible(w);
    internal static string Error(IntPtr w){var b=new StringBuilder(512);GetWindowText(GetDlgItem(w,91),b,b.Capacity);return b.ToString();}
    internal static void Cancel(IntPtr w)=>Click(w,2);
    internal static object Observe(IntPtr w,int pid)
    {
        GetWindowRect(w,out var r);GetWindowThreadProcessId(GetForegroundWindow(),out uint foregroundPid);
        return new{visible=Visible(w),foreground=GetForegroundWindow()==w,foregroundSameProcess=foregroundPid==pid,width=r.Right-r.Left,height=r.Bottom-r.Top,fields=Enumerable.Range(100,4).Select(id=>new{id,visible=IsWindowVisible(GetDlgItem(w,id)),enabled=IsWindowEnabled(GetDlgItem(w,id))}).ToArray()};
    }
    internal static void ScreenshotMetadata(IntPtr w,string name)
    {
        var title=new StringBuilder(256);GetWindowText(w,title,title.Capacity);Assert.Equal("APEX — lokalny klucz OKX",title.ToString());
        // Screenshots are permitted only before any secret input. Masking is not
        // a privacy oracle: refuse populated native fields even when masked.
        for(int id=100;id<103;id++){var value=new StringBuilder(4097);GetWindowText(GetDlgItem(w,id),value,value.Capacity);Assert.True(value.Length==0,"Never screenshot a populated credential form");}
        GetWindowRect(w,out var r);int width=r.Right-r.Left,height=r.Bottom-r.Top;Assert.InRange(width,100,2000);Assert.InRange(height,100,1600);
        IntPtr dc=GetWindowDC(w),copy=CreateCompatibleDC(dc),bitmap=CreateCompatibleBitmap(dc,width,height),old=SelectObject(copy,bitmap);
        try{
            Assert.True(PrintWindow(w,copy,2));var info=new BitmapInfo{size=40,width=width,height=height,planes=1,bits=32,sizeImage=(uint)(width*height*4)};byte[] bytes=new byte[width*height*4];Assert.NotEqual(0,GetDIBits(copy,bitmap,0,(uint)height,bytes,ref info,0));
            using var file=File.Create(Path.Combine(Environment.GetEnvironmentVariable("APEX_WINDOWS_EVIDENCE")!,name+".bmp"));using var b=new BinaryWriter(file);b.Write((ushort)0x4d42);b.Write((uint)(54+bytes.Length));b.Write(0u);b.Write(54u);b.Write(40u);b.Write(width);b.Write(height);b.Write((ushort)1);b.Write((ushort)32);b.Write(0u);b.Write((uint)bytes.Length);b.Write(0);b.Write(0);b.Write(0u);b.Write(0u);b.Write(bytes);
        }finally{SelectObject(copy,old);DeleteObject(bitmap);DeleteDC(copy);ReleaseDC(w,dc);}
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo{public uint size;public int width,height;public ushort planes,bits;public uint compression,sizeImage;public int x,y;public uint used,important;}
    private delegate bool EnumProc(IntPtr w,IntPtr p);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc f,IntPtr p);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr w,out uint pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr w,StringBuilder text,int count);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr w);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr w);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr w);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr w,int id);
    [DllImport("user32.dll",ExactSpelling=true)] private static extern IntPtr GetWindowLongPtrW(IntPtr w,int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr w,out Rect r);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr w);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr w,uint message,IntPtr a,string text);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr w);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr w,IntPtr dc);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr w,IntPtr dc,uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc,int width,int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc,IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc,IntPtr bitmap,uint start,uint count,byte[] bits,ref BitmapInfo info,uint usage);
}
