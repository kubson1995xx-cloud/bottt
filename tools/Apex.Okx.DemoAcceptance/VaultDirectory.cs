using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static Apex.Okx.DemoAcceptance.LinuxDemoCredentialVault;

namespace Apex.Okx.DemoAcceptance;

// Minimal Linux-x64 native seam, analogous to OkxLinuxPrivate but pinned directory
// descriptors, exact 0700/0600 modes and no-clobber publication are needed here.
// It does not implement or duplicate a financial store.
internal sealed class VaultDirectory : IDisposable
{
    private const int NoFollow=0x20000,DirectoryFlag=0x10000,CloseExec=0x80000,NonBlock=0x800;
    private int fd;
    private readonly string recordName;
    private VaultDirectory(int fd,string recordName) {this.fd=fd;this.recordName=recordName;}
    internal sealed record Identity(ulong Device,ulong Inode,long Size);
    private static byte[] Stat(int file) {byte[] b=new byte[144];if(fstat(file,b)!=0)throw Error(DemoVaultError.PathRefused);return b;}
    private static Identity Admit(int file,bool directory,bool leaf)
    {
        byte[] b=Stat(file);uint mode=BitConverter.ToUInt32(b,24),uid=BitConverter.ToUInt32(b,28);ulong links=BitConverter.ToUInt64(b,16);long size=BitConverter.ToInt64(b,48);
        if((mode&0xf000)!=(directory?0x4000u:0x8000u)||(leaf?uid!=getuid():uid!=getuid()&&uid!=0)||
            (leaf?(mode&0xfff)!=(directory?448u:384u):(mode&0x12)!=0)||!directory&&(links!=1||size<0||size>MaximumCiphertextBytes))throw Error(DemoVaultError.PathRefused);
        return new(BitConverter.ToUInt64(b,0),BitConverter.ToUInt64(b,8),size);
    }
    internal static async Task<VaultDirectory?> OpenAsync(string path,bool create,CancellationToken ct,string recordName=RecordName)
    {
        if(recordName is not (RecordName or "balance-identifiers.json" or "instrument-identifiers.json" or "order-protection-identifiers.json"))throw Error(DemoVaultError.PathRefused);
        if(!OperatingSystem.IsLinux()||RuntimeInformation.ProcessArchitecture!=Architecture.X64||string.IsNullOrEmpty(path)||!Path.IsPathFullyQualified(path)||Path.GetFullPath(path)!=path||path.Contains("//",StringComparison.Ordinal)||path.EndsWith('/')||path.Contains('\0'))throw Error(DemoVaultError.PathRefused);
        int current=open("/",DirectoryFlag|NoFollow|CloseExec|NonBlock,0);if(current<0)throw Error(DemoVaultError.PathRefused);
        try
        {
            var parts=path.Split('/',StringSplitOptions.RemoveEmptyEntries);
            for(int i=0;i<parts.Length;i++)
            {
                ct.ThrowIfCancellationRequested();
                int next=openat(current,parts[i],DirectoryFlag|NoFollow|CloseExec|NonBlock,0);
                if(next<0&&Marshal.GetLastPInvokeError()==2)
                {
                    if(!create)return null;
                    if(mkdirat(current,parts[i],448)!=0&&Marshal.GetLastPInvokeError()!=17)throw Error(DemoVaultError.PathRefused);
                    if(fsync(current)!=0)throw Error(DemoVaultError.StorageUnavailable);
                    next=openat(current,parts[i],DirectoryFlag|NoFollow|CloseExec|NonBlock,0);
                }
                if(next<0)throw Error(DemoVaultError.PathRefused);
                try {Admit(next,true,i==parts.Length-1);}catch {close(next);throw;}
                close(current);current=next;
            }
            // No persistent lock file. Independently opened directory FDs conflict via flock
            // across tasks, vault instances and processes. Never unlink/replace this directory.
            while(flock(current,2|4)!=0)
            {
                if(Marshal.GetLastPInvokeError() is not (11 or 4))throw Error(DemoVaultError.StorageUnavailable);
                await Task.Delay(25,ct).ConfigureAwait(false);
            }
            Admit(current,true,true);var result=new VaultDirectory(current,recordName);current=-1;return result;
        }
        finally {if(current>=0)close(current);}
    }
    private int OpenRecord()
    {
        int file=openat(fd,recordName,NoFollow|CloseExec|NonBlock,0);
        if(file<0&&Marshal.GetLastPInvokeError()==2)return -1;
        if(file<0)throw Error(DemoVaultError.PathRefused);
        try {Admit(file,false,true);return file;}catch {close(file);throw;}
    }
    internal Identity? InspectRecord()
    {int file=OpenRecord();if(file<0)return null;try{return Admit(file,false,true);}finally{close(file);}}
    internal SensitiveBuffer ReadRecord(IBufferAllocator allocator)
    {
        int file=OpenRecord();if(file<0)throw Error(DemoVaultError.NotStored);
        using var handle=new SafeFileHandle((IntPtr)file,true);using var stream=new FileStream(handle,FileAccess.Read,bufferSize:1);
        Identity before=Admit(file,false,true);SensitiveBuffer? b=null;
        try
        {
            b=new(allocator,MaximumCiphertextBytes+1);
            stream.ReadExactly(b.Array,0,(int)before.Size);b.Length=(int)before.Size;
            if(stream.Read(b.Array,(int)before.Size,1)!=0||Admit(file,false,true)!=before)throw Error(DemoVaultError.PathRefused);
            var result=b;b=null;return result;
        }
        finally {b?.Dispose();}
    }
    private void Match(Identity? expected)
    {if(InspectRecord()!=expected)throw Error(DemoVaultError.PathRefused);}
    internal void Publish(ReadOnlySpan<byte> encrypted,Identity? old,bool replace,CancellationToken ct)
    {
        string staging=".credentials-"+Guid.NewGuid().ToString("N")+".tmp";
        int file=openat(fd,staging,1|64|128|NoFollow|CloseExec|NonBlock,384); // WRONLY|CREAT|EXCL
        if(file<0)throw Error(DemoVaultError.StorageUnavailable);
        Identity? owned=null;bool published=false;
        try
        {
            owned=Admit(file,false,true);
            using(var handle=new SafeFileHandle((IntPtr)file,true))
            using(var stream=new FileStream(handle,FileAccess.Write,bufferSize:1))
            {file=-1;stream.Write(encrypted);stream.Flush(flushToDisk:true);Admit(handle.DangerousGetHandle().ToInt32(),false,true);}
            Match(old);ct.ThrowIfCancellationRequested();
            int result=replace?renameat(fd,staging,fd,recordName):renameat2(fd,staging,fd,recordName,1); // RENAME_NOREPLACE
            if(result!=0)
            {
                if(!replace&&Marshal.GetLastPInvokeError()==17)throw Error(DemoVaultError.AlreadyStored);
                throw Error(DemoVaultError.StorageUnavailable);
            }
            published=true;
            if(fsync(fd)!=0)throw Error(DemoVaultError.CommitUncertain);
            var current=InspectRecord();
            if(current==null||current.Device!=owned.Device||current.Inode!=owned.Inode||current.Size!=encrypted.Length)throw Error(DemoVaultError.CommitUncertain);
        }
        catch(OperationCanceledException) when(!published) {throw;}
        catch(DemoVaultException) {throw;}
        catch {throw Error(published?DemoVaultError.CommitUncertain:DemoVaultError.StorageUnavailable);}
        finally
        {
            if(file>=0)close(file);
            // Clean only our exact created inode, never scan/delete unexplained staging files.
            if(!published&&owned!=null)
            {
                int leftover=openat(fd,staging,NoFollow|CloseExec|NonBlock,0);
                if(leftover>=0)
                {
                    try {var now=Admit(leftover,false,true);if(now.Device==owned.Device&&now.Inode==owned.Inode)unlinkat(fd,staging,0);}catch { }
                    finally {close(leftover);}
                }
            }
        }
    }
    internal bool Forget(CancellationToken ct)
    {
        var old=InspectRecord();if(old==null)return false;Match(old);ct.ThrowIfCancellationRequested();
        if(unlinkat(fd,recordName,0)!=0)throw Error(DemoVaultError.StorageUnavailable);
        if(fsync(fd)!=0||InspectRecord()!=null)throw Error(DemoVaultError.CommitUncertain);
        return true;
    }
    // Same flock used by .NET FileShare.None on the accepted process lock.
    // Existing inode only: no new lock file and no path-following fallback.
    internal SafeFileHandle AcquireExistingProcessLock()
    {
        int file=openat(fd,".run.lock",2|NoFollow|CloseExec|NonBlock,0);
        if(file<0)throw Error(DemoVaultError.PathRefused);
        try
        {
            Admit(file,false,true);
            if(flock(file,2|4)!=0)throw new IOException("RUN_LOCK_BUSY_NO_CHANGES");
            var handle=new SafeFileHandle((IntPtr)file,true);file=-1;return handle;
        }
        finally{if(file>=0)close(file);}
    }
    public void Dispose() {if(fd>=0){close(fd);fd=-1;}}
    [DllImport("libc",SetLastError=true)]private static extern int open(string path,int flags,uint mode);
    [DllImport("libc",SetLastError=true)]private static extern int openat(int dir,string path,int flags,uint mode);
    [DllImport("libc",SetLastError=true)]private static extern int mkdirat(int dir,string path,uint mode);
    [DllImport("libc",SetLastError=true)]private static extern int fstat(int file,[Out]byte[] stat);
    [DllImport("libc")]private static extern uint getuid();
    [DllImport("libc")]private static extern int close(int file);
    [DllImport("libc",SetLastError=true)]private static extern int fsync(int file);
    [DllImport("libc",SetLastError=true)]private static extern int flock(int file,int operation);
    [DllImport("libc",SetLastError=true)]private static extern int renameat(int olddir,string oldname,int newdir,string newname);
    [DllImport("libc",SetLastError=true)]private static extern int renameat2(int olddir,string oldname,int newdir,string newname,uint flags);
    [DllImport("libc",SetLastError=true)]private static extern int unlinkat(int dir,string path,int flags);
}
