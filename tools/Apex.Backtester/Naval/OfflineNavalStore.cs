using System;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using Apex.Core.Engine;
using Apex.Core.Naval;
namespace Apex.Backtester.Naval;

internal sealed class OfflineNavalStore : IKeyValueStore
{
    private readonly string _directory;
    internal OfflineNavalStore(string directory){_directory=Path.GetFullPath(directory);}
    private string PathFor(string key)=>key==NavalCoreStateOwner.StateKey?Path.Combine(_directory,key+".json"):throw new ArgumentException("Offline Naval scope key only");
    public string? Get(string key){string path=PathFor(key);return File.Exists(path)?File.ReadAllText(path):null;}
    public void Set(string key,string value)
    {
        string path=PathFor(key),temp=path+".candidate";
        // Single leased writer; crash leaves either old or new authority, never a fallback backup.
        using(var stream=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
        {var bytes=Encoding.UTF8.GetBytes(value);stream.Write(bytes);stream.Flush(true);}
        if(OperatingSystem.IsLinux())
        {
            File.Move(temp,path,true);
            int fd=OpenDirectory(_directory,65536); // O_RDONLY | O_DIRECTORY
            if(fd<0)throw new IOException("Cannot open authority directory for fsync");
            try{if(Fsync(fd)!=0)throw new IOException("Ambiguous authority directory fsync");}finally{Close(fd);}
        }
        else if(OperatingSystem.IsWindows())
        {
            if(!MoveFileEx(temp,path,9))throw new IOException("Ambiguous durable authority rename: "+Marshal.GetLastPInvokeError()); // REPLACE_EXISTING | WRITE_THROUGH
        }
        else throw new PlatformNotSupportedException("Offline Naval persistence supports Linux/Windows only");
    }
    [DllImport("libc",EntryPoint="open",SetLastError=true)]private static extern int OpenDirectory(string path,int flags);
    [DllImport("libc",EntryPoint="fsync",SetLastError=true)]private static extern int Fsync(int fd);
    [DllImport("libc",EntryPoint="close")]private static extern int Close(int fd);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]private static extern bool MoveFileEx(string from,string to,int flags);
    public void Flush() { } // Set fsyncs candidate and directory / uses write-through rename.
}
