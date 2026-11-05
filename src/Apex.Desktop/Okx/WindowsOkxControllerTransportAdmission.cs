using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Apex.Desktop.Services;
using Microsoft.Win32.SafeHandles;
namespace Apex.Desktop.Okx;

// Explicit native configuration, not browser data or an arbitrary SSH command.
// IdentitySha256 selects already provisioned local key bytes; HostKey is canonical SSH wire base64.
internal sealed record WindowsOkxControllerTransportConfig(string Host,int Port,string User,string IdentitySha256,string HostKey);

// Admission only: never provisions, repairs ACLs, starts SSH, pairs, or touches financial stores.
// Keep this lease alive through a future Process.Create boundary. OpenSSH subsequently reopens
// pathname operands: sharing locks are not protection against a malicious same-SID/admin actor.
internal sealed class WindowsOkxControllerTransportAdmission : IDisposable
{
    private readonly object gate=new();
    private readonly List<WindowsPathSecurity.NativeObject> directories=new();
    private readonly HashSet<string> privateDirectories=new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> systemDirectories=new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> strictSystemDirectories=new(StringComparer.OrdinalIgnoreCase);
    private readonly List<OpenedFile> files=new();
    private readonly string sid;
    private readonly long issued=Stopwatch.GetTimestamp();
    private bool disposed;
    internal OkxControllerSshLaunchSpec Spec {get;private set;}=null!;
    internal OkxControllerSshLaunchSpec.AdmittedTransportCapability Capability {get;private set;}=null!;
    internal const string Refused="NATIVE_TRANSPORT_ADMISSION_REFUSED";
    private WindowsOkxControllerTransportAdmission(){sid=WindowsIdentity.GetCurrent().User?.Value??throw Denied();}
    private static InvalidOperationException Denied(string check="CONTRACT")
    {var e=new InvalidOperationException(Refused);e.Data["Check"]=check;return e;}

    internal static WindowsOkxControllerTransportAdmission Open(string root,WindowsOkxControllerTransportConfig config)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("WINDOWS_TRANSPORT_REQUIRED");
        if(config==null)throw Denied();
        ValidateConfig(config);
        var lease=new WindowsOkxControllerTransportAdmission();
        string stage="PROFILE";
        try
        {
            root=ExactSyntax(root);
            string profile=Path.Combine(root,"controller-ssh");
            lease.HoldDirectoryChain(profile,root,true);
            // Closed existing profile: automatic public/certificate companions and arbitrary config refuse.
            var entries=Directory.GetFileSystemEntries(profile).Select(Path.GetFileName).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
            if(!entries.SequenceEqual(new[]{"identity","known_hosts"}))throw Denied();
            string identity=Path.Combine(profile,"identity"),knownHosts=Path.Combine(profile,"known_hosts");
            stage="IDENTITY";var key=lease.HoldFile(identity,FileRole.PrivateData,65536);
            if(!Convert.ToHexString(SHA256.HashData(key.Stream)).Equals(config.IdentitySha256,StringComparison.OrdinalIgnoreCase))throw Denied();
            key.Stream.Position=0;
            stage="HOST_PIN";var hosts=lease.HoldFile(knownHosts,FileRole.PrivateData,1024);
            string expected=(config.Port==22?config.Host:"["+config.Host+"]:"+config.Port.ToString(System.Globalization.CultureInfo.InvariantCulture))+" ssh-ed25519 "+config.HostKey+"\n";
            byte[] bytes=new byte[checked((int)hosts.Stream.Length)];hosts.Stream.ReadExactly(bytes);hosts.Stream.Position=0;
            if(!bytes.AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(expected)))throw Denied();
            // GetSystemWindowsDirectoryW is OS authority; WINDIR/PATH/SSH environment is never read.
            stage="SYSTEM_SSH";var windows=new StringBuilder(32768);uint n=GetSystemWindowsDirectoryW(windows,(uint)windows.Capacity);
            if(n==0||n>=windows.Capacity||!Environment.Is64BitProcess)throw Denied();
            string systemRoot=ExactSyntax(windows.ToString());
            string exe=Path.Combine(systemRoot,"System32","OpenSSH","ssh.exe");
            lease.HoldDirectoryChain(Path.GetDirectoryName(exe)!,null,false);
            // Only this OS-composed protected executable may have servicing hard links.
            // Private keys/pins never enter this role; its observed link count is pinned, not ignored.
            lease.HoldFile(exe,FileRole.SystemSsh,64*1024*1024);
            stage="OS_ENVIRONMENT";
            var folder=new Guid("62AB5D82-FDC1-4DC3-A9DD-070D1D495D97");
            IntPtr common=IntPtr.Zero;string commonAppData;
            try
            {
                if(SHGetKnownFolderPath(ref folder,0,IntPtr.Zero,out common)!=0||common==IntPtr.Zero)throw Denied();
                commonAppData=ExactSyntax(Marshal.PtrToStringUni(common)!);
            }
            finally{if(common!=IntPtr.Zero)Marshal.FreeCoTaskMem(common);}
            // CommonAppData may allow new unrelated siblings. Pin its canonical root and
            // protected ancestry, not a new closed-directory policy or inherited value.
            lease.HoldDirectoryChain(commonAppData,null,false,false);
            lease.Spec=OkxControllerSshLaunchSpec.BuildWindows(new(config.Host,config.Port,config.User,identity,knownHosts),exe,systemRoot,commonAppData);
            stage="CAPABILITY";lease.Capability=OkxControllerSshLaunchSpec.AdmittedTransportCapability.Issue(lease);
            return lease;
        }
        catch(Exception e) when(e is not OutOfMemoryException)
        {lease.Dispose();var failure=Denied(e is WindowsDataException w?w.Code:e.Data["Check"] as string??"NATIVE_IO");failure.Data["Stage"]=stage;throw failure;} // Fixed vocabulary only; never paths or native exception details.
    }
    private static void ValidateConfig(WindowsOkxControllerTransportConfig c)
    {
        if(c.Host==null||c.Host.Length is <1 or >253||!Regex.IsMatch(c.Host,@"\A[a-z0-9]+(?:[.-][a-z0-9]+)*\z")||c.Host.Split('.').Any(s=>s.Length>63)||c.Port is <1 or >65535||c.User==null||!Regex.IsMatch(c.User,@"\A[a-z_][a-z0-9_-]{0,31}\z")||c.IdentitySha256==null||!Regex.IsMatch(c.IdentitySha256,@"\A[a-f0-9]{64}\z")||c.HostKey==null||c.HostKey.Length!=68)throw Denied();
        byte[] wire;
        try{wire=Convert.FromBase64String(c.HostKey);}catch(FormatException){throw Denied();}
        if(wire.Length!=51||Convert.ToBase64String(wire)!=c.HostKey||!wire.AsSpan(0,19).SequenceEqual(new byte[]{0,0,0,11,115,115,104,45,101,100,50,53,53,49,57,0,0,0,32})||wire.AsSpan(19).IndexOfAnyExcept((byte)0)<0)throw Denied();
    }
    private static string ExactSyntax(string path)
    {
        if(path==null||path.Any(c=>c is '$' or '"' or '`'))throw Denied();
        return WindowsPrivateData.ValidateSyntax(path);
    }
    private void HoldDirectoryChain(string target,string? privateRoot,bool checkLocation,bool closedTarget=true)
    {
        target=ExactSyntax(target);WindowsPathSecurity.ValidateVolume(Path.GetPathRoot(target)!);
        foreach(string path in WindowsPrivateData.Components(target))
        {
            bool privateTarget=privateRoot!=null&&WindowsPrivateData.Contains(privateRoot,path);
            if(checkLocation){WindowsPrivateData.CheckLocation(path,AppContext.BaseDirectory);}
            if(directories.Any(d=>d.Path.Equals(path,StringComparison.OrdinalIgnoreCase)))continue;
            var opened=WindowsPathSecurity.Open(path);
            try
            {
                if(!opened.IsDirectory||!opened.Path.Equals(path,StringComparison.OrdinalIgnoreCase))throw Denied();
                if(checkLocation)WindowsPathSecurity.Validate(opened,privateTarget);
                else ValidateSystemSecurity(opened.Handle,true,closedTarget&&path.Equals(target,StringComparison.OrdinalIgnoreCase));
                if(privateTarget)privateDirectories.Add(opened.Path);
                if(!checkLocation)systemDirectories.Add(opened.Path);
                if(!checkLocation&&closedTarget&&path.Equals(target,StringComparison.OrdinalIgnoreCase))strictSystemDirectories.Add(opened.Path);
                directories.Add(opened);opened=null!;
            }
            finally{opened?.Dispose();}
        }
    }
    private enum FileRole { PrivateData, SystemSsh }
    private OpenedFile HoldFile(string path,FileRole role,long maximum)
    {
        var file=new OpenedFile(ExactSyntax(path),role,maximum,sid);
        files.Add(file);return file;
    }
    // The capability can request only this exact spec. Both validation and StartInfo construction
    // linearize with disposal; an already returned mutable StartInfo is not a revocable OS handle.
    internal T WithValidatedSpec<T>(OkxControllerSshLaunchSpec spec,Func<T> action)
    {
        lock(gate)
        {
            if(disposed||!ReferenceEquals(spec,Spec)||Stopwatch.GetElapsedTime(issued)>TimeSpan.FromSeconds(30)||WindowsIdentity.GetCurrent().User?.Value!=sid)throw Denied();
            try
            {
                foreach(var d in directories)
                {
                    if(privateDirectories.Contains(d.Path))WindowsPathSecurity.Validate(d,true);
                    else if(systemDirectories.Contains(d.Path))ValidateSystemSecurity(d.Handle,true,strictSystemDirectories.Contains(d.Path));
                    else WindowsPathSecurity.Validate(d,false);
                    WindowsPrivateData.CheckMarkers(d.Path);
                    using var current=WindowsPathSecurity.Open(d.Path);
                    if(!current.SameIdentity(d)||!current.Path.Equals(d.Path,StringComparison.OrdinalIgnoreCase))throw Denied();
                }
                foreach(var f in files)f.Validate();
                return action();
            }
            catch(Exception e) when(e is not OutOfMemoryException){throw Denied(e is WindowsDataException w?w.Code:e.Data["Check"] as string??"NATIVE_IO");}
        }
    }
    public void Dispose()
    {
        lock(gate)
        {
            if(disposed)return;disposed=true;
            foreach(var f in files)f.Dispose();files.Clear();
            foreach(var d in directories.AsEnumerable().Reverse())d.Dispose();directories.Clear();
        }
    }
    private sealed class OpenedFile:IDisposable
    {
        internal string Path {get;}
        private readonly FileRole role;
        internal FileStream Stream {get;}
        private readonly WindowsPathSecurity.FileInformation original;
        private byte[]? originalSecurity;
        private readonly string owner;
        private readonly long maximum;
        internal OpenedFile(string path,FileRole role,long max,string sid)
        {
            Path=path;this.role=role;owner=sid;maximum=max;
            var handle=CreateFileW(path,0x80020000,1 /* share read only */,IntPtr.Zero,3,0x00200000,IntPtr.Zero);
            if(handle.IsInvalid){handle.Dispose();throw Denied();}
            try
            {
                if(!GetFileInformationByHandle(handle,out original))throw Denied();
                Stream=new FileStream(handle,FileAccess.Read,4096,false);
                Validate();
            }
            catch{handle.Dispose();throw;}
        }
        internal void Validate()
        {
            var handle=Stream.SafeFileHandle;
            if(!GetFileInformationByHandle(handle,out var info)||info.Links<1||info.Links!=original.Links||(role==FileRole.PrivateData&&info.Links!=1)||(info.Attributes&(uint)(FileAttributes.Directory|FileAttributes.ReparsePoint))!=0||info.VolumeSerial!=original.VolumeSerial||info.IndexHigh!=original.IndexHigh||info.IndexLow!=original.IndexLow||info.SizeHigh!=original.SizeHigh||info.SizeLow!=original.SizeLow||Stream.Length is <=0||Stream.Length>maximum)throw Denied("FILE_IDENTITY");
            var name=new StringBuilder(32768);uint n=GetFinalPathNameByHandleW(handle,name,(uint)name.Capacity,0);
            if(n==0||n>=name.Capacity||!name.ToString().Equals("\\\\?\\"+Path,StringComparison.OrdinalIgnoreCase))throw Denied();
            using(var view=new WindowsPathSecurity.NativeObject(new SafeFileHandle(handle.DangerousGetHandle(),false),Path,info))
            {
                if(role==FileRole.PrivateData)WindowsPathSecurity.Validate(view,true);else ValidateSystemSecurity(handle,false);
                using var current=WindowsPathSecurity.Open(Path);
                if(!current.SameIdentity(view)||!current.Path.Equals(Path,StringComparison.OrdinalIgnoreCase))throw Denied("FILE_IDENTITY");
            }
            if(GetSecurityInfo(handle,1,5,out _,out _,out _,out _,out var sd)!=0||sd==IntPtr.Zero)throw Denied();
            try
            {
                uint size=GetSecurityDescriptorLength(sd);if(size is 0 or >65536)throw Denied();
                byte[] bytes=new byte[size];Marshal.Copy(sd,bytes,0,bytes.Length);var acl=new RawSecurityDescriptor(bytes,0);
                if(role==FileRole.PrivateData&&acl.Owner?.Value!=owner)throw Denied("FILE_OWNER");
                if(role==FileRole.SystemSsh)
                {
                    if(originalSecurity!=null&&!bytes.AsSpan().SequenceEqual(originalSecurity))throw Denied("SYSTEM_SECURITY_CHANGED");
                    originalSecurity??=bytes;
                }
            }
            finally{LocalFree(sd);}
        }
        public void Dispose()=>Stream.Dispose();
    }
    // OS servicing owns Windows/System32 through TrustedInstaller. The private-data ancestor
    // policy intentionally does not allow that SID to write a private profile; do not widen it.
    private static void ValidateSystemSecurity(SafeFileHandle handle,bool directory,bool closedDirectory=false)
    {
        if(GetSecurityInfo(handle,1,5,out _,out _,out _,out _,out var sd)!=0||sd==IntPtr.Zero)throw Denied("SYSTEM_SECURITY");
        try
        {
            uint size=GetSecurityDescriptorLength(sd);if(size is 0 or >65536)throw Denied("SYSTEM_SECURITY");
            byte[] bytes=new byte[size];Marshal.Copy(sd,bytes,0,bytes.Length);var descriptor=new RawSecurityDescriptor(bytes,0);
            bool Trusted(SecurityIdentifier? s)=>s!=null&&(s.IsWellKnown(WellKnownSidType.LocalSystemSid)||s.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)||s.Value=="S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
            if(!Trusted(descriptor.Owner)||(descriptor.ControlFlags&ControlFlags.DiscretionaryAclPresent)==0||descriptor.DiscretionaryAcl==null)throw Denied("SYSTEM_SECURITY");
            foreach(GenericAce entry in descriptor.DiscretionaryAcl)
            {
                if(entry is not CommonAce ace||ace.IsCallback||ace.AceType is not(AceType.AccessAllowed or AceType.AccessDenied))throw Denied("SYSTEM_SECURITY");
                if(ace.AceType==AceType.AccessDenied||Trusted(ace.SecurityIdentifier)||(ace.AceFlags&AceFlags.InheritOnly)!=0)continue;
                // Ancestors may permit harmless new siblings; the executable's own directory
                // must also deny untrusted file creation (including potential DLL companions).
                int unsafeRights=directory?(closedDirectory?unchecked((int)0x500D0156):unchecked((int)0x500D0150)):unchecked((int)0x500D0116);
                if((ace.AccessMask&unsafeRights)!=0)throw Denied("SYSTEM_SECURITY");
            }
        }
        finally{LocalFree(sd);}
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern uint GetSystemWindowsDirectoryW(StringBuilder buffer,uint size);
    [DllImport("shell32.dll")]private static extern int SHGetKnownFolderPath(ref Guid folder,uint flags,IntPtr token,out IntPtr path);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr attributes,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetFileInformationByHandle(SafeFileHandle file,out WindowsPathSecurity.FileInformation info);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file,StringBuilder path,uint size,uint flags);
    [DllImport("advapi32.dll")]private static extern uint GetSecurityInfo(SafeFileHandle file,uint type,uint information,out IntPtr owner,out IntPtr group,out IntPtr dacl,out IntPtr sacl,out IntPtr descriptor);
    [DllImport("advapi32.dll")]private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")]private static extern IntPtr LocalFree(IntPtr pointer);
}
