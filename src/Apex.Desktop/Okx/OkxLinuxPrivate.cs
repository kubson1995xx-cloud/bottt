using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace Apex.Desktop.Okx;

// Linux x64-only path admission for the separately composed finite DEMO process.
// Unknown existing paths are refused, never chmod/repaired. Same-UID/root attackers
// and replacement by a hostile privileged process are outside this boundary.
internal static class OkxLinuxPrivate
{
    private const int NoFollow=0x20000,DirectoryFlag=0x10000,CloseExec=0x80000,NonBlock=0x800;
    private static void Platform(){if(!OperatingSystem.IsLinux()||RuntimeInformation.ProcessArchitecture!=Architecture.X64)throw Refused();}
    internal static IOException Refused()=>new("LINUX_PRIVATE_PATH_REFUSED");
    private static byte[] Stat(int fd){var b=new byte[144];if(fstat(fd,b)!=0)throw Refused();return b;}
    private static void Check(int fd,bool directory,bool leaf)
    {
        var s=Stat(fd);uint mode=BitConverter.ToUInt32(s,24),uid=BitConverter.ToUInt32(s,28);ulong links=BitConverter.ToUInt64(s,16);
        if((mode&0xf000)!=(directory?0x4000u:0x8000u)||(leaf?uid!=getuid():uid!=getuid()&&uid!=0)||
            (mode&(leaf?0x3fu:0x12u))!=0||!directory&&links!=1)throw Refused();
    }
    internal static void AdmitDirectory(string path,bool create)
    {
        Platform();if(!Path.IsPathFullyQualified(path)||Path.GetFullPath(path)!=path||path.Contains("//",StringComparison.Ordinal))throw Refused();
        int fd=open("/",DirectoryFlag|NoFollow|CloseExec|NonBlock,0);if(fd<0)throw Refused();
        try
        {
            var parts=path.Split('/',StringSplitOptions.RemoveEmptyEntries);
            for(int n=0;n<parts.Length;n++)
            {
                int next=openat(fd,parts[n],DirectoryFlag|NoFollow|CloseExec|NonBlock,0);
                if(next<0&&Marshal.GetLastPInvokeError()==2&&create)
                {if(mkdirat(fd,parts[n],0x1c0)!=0)throw Refused();next=openat(fd,parts[n],DirectoryFlag|NoFollow|CloseExec|NonBlock,0);}
                if(next<0)throw Refused();
                try{Check(next,true,n==parts.Length-1);}catch{close(next);throw;}
                close(fd);fd=next;
            }
        }
        finally{close(fd);}
    }
    internal static SafeFileHandle OpenFile(string path,long cap=4*1024*1024)
    {
        AdmitDirectory(Path.GetDirectoryName(path)!,false);
        int fd=open(path,NoFollow|CloseExec|NonBlock,0);if(fd<0)throw Refused();
        try{Check(fd,false,true);var s=Stat(fd);long size=BitConverter.ToInt64(s,48);if(size<0||size>cap)throw Refused();return new SafeFileHandle((IntPtr)fd,true);}
        catch{close(fd);throw;}
    }
    internal static void AdmitFile(string path){using var file=OpenFile(path,16*1024*1024);}
    internal static byte[] ReadFile(string path,long cap=4*1024*1024)
    {using var handle=OpenFile(path,cap);using var stream=new FileStream(handle,FileAccess.Read);byte[] bytes=new byte[checked((int)stream.Length)];stream.ReadExactly(bytes);return bytes;}
    internal static void Publish(string path,byte[] bytes)
    {
        AdmitDirectory(Path.GetDirectoryName(path)!,false);
        if(File.Exists(path))AdmitFile(path);
        string staging=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var stream=new FileStream(staging,new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None,Options=FileOptions.WriteThrough,UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite}))
            {stream.Write(bytes);stream.Flush(true);}
            AdmitFile(staging);File.Move(staging,path,true);AdmitFile(path);
            int dir=open(Path.GetDirectoryName(path)!,DirectoryFlag|NoFollow|CloseExec,0);if(dir<0)throw Refused();try{if(fsync(dir)!=0)throw Refused();}finally{close(dir);}
        }
        finally{if(File.Exists(staging))File.Delete(staging);}
    }
    [DllImport("libc",SetLastError=true)] private static extern int open(string path,int flags,uint mode);
    [DllImport("libc",SetLastError=true)] private static extern int openat(int dir,string path,int flags,uint mode);
    [DllImport("libc",SetLastError=true)] private static extern int mkdirat(int dir,string path,uint mode);
    [DllImport("libc",SetLastError=true)] private static extern int fstat(int fd,[Out]byte[] stat);
    [DllImport("libc")] private static extern uint getuid();
    [DllImport("libc")] private static extern int close(int fd);
    [DllImport("libc")] private static extern int fsync(int fd);
}
