using System.Runtime.InteropServices;
using System.Text;
namespace Apex.Okx.DemoAcceptance;

// Commands come only from this process's foreground controlling terminal, never pipes.
internal sealed class LinuxCommandInput : IDisposable
{
    private readonly int terminal;
    internal LinuxCommandInput()
    {
        if(!Foreground())throw new IOException("LOCAL_TTY_REQUIRED");
        terminal=open("/dev/tty",2|0x80000|0x100,0);
        if(terminal<0||tcgetpgrp(terminal)!=getpgrp()){if(terminal>=0)close(terminal);throw new IOException("LOCAL_TTY_REQUIRED");}
    }
    internal bool IsCurrent=>Foreground()&&tcgetpgrp(terminal)==getpgrp();
    private static bool Foreground()
    {
        if(new[]{0,1,2}.Any(fd=>isatty(fd)!=1||tcgetpgrp(fd)!=getpgrp()))return false;
        string? name=Marshal.PtrToStringUTF8(ttyname(0));
        return name!=null&&Marshal.PtrToStringUTF8(ttyname(1))==name&&Marshal.PtrToStringUTF8(ttyname(2))==name;
    }
    internal ValueTask<string?> ReadAsync(CancellationToken ct)=>new(Task.Run(()=>
    {
        byte[] bytes=new byte[32],one=new byte[1];int used=0;
        try
        {
            while(true)
            {
                ct.ThrowIfCancellationRequested();if(!IsCurrent)throw new IOException("LOCAL_TTY_REVOKED");
                var f=new Poll{fd=terminal,events=1};int ready=poll(ref f,1,100);
                if(ready<0){if(Marshal.GetLastPInvokeError()==4)continue;throw new IOException("LOCAL_TTY_REFUSED");}if(ready==0)continue;
                nint count=read(terminal,one,1);if(count==0)return null;if(count!=1)throw new IOException("LOCAL_TTY_REFUSED");
                byte b=one[0];if(b is 4)return null;if(b is 10 or 13)return Encoding.ASCII.GetString(bytes,0,used);
                if(b is <32 or >126||used==bytes.Length)throw new IOException("LOCAL_COMMAND_REFUSED");bytes[used++]=b;
            }
        }
        finally{Array.Clear(bytes);Array.Clear(one);}
    },ct));
    public void Dispose()=>close(terminal);
    [StructLayout(LayoutKind.Sequential)]private struct Poll{public int fd;public short events,revents;}
    [DllImport("libc",SetLastError=true)]private static extern int open(string path,int flags,uint mode);
    [DllImport("libc")]private static extern int close(int fd);
    [DllImport("libc")]private static extern int isatty(int fd);
    [DllImport("libc")]private static extern int getpgrp();
    [DllImport("libc")]private static extern int tcgetpgrp(int fd);
    [DllImport("libc")]private static extern IntPtr ttyname(int fd);
    [DllImport("libc",SetLastError=true)]private static extern int poll(ref Poll fds,nuint count,int timeout);
    [DllImport("libc",SetLastError=true)]private static extern nint read(int fd,[Out]byte[] buffer,nuint count);
}
