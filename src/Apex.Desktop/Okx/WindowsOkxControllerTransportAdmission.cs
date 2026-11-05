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
            stage="IDENTITY";var key=lease.HoldFile(identity,true,65536);
            if(!Convert.ToHexString(SHA256.HashData(key.Stream)).Equals(config.IdentitySha256,StringComparison.OrdinalIgnoreCase))throw Denied();
            key.Stream.Position=0;
            stage="HOST_PIN";var hosts=lease.HoldFile(knownHosts,true,1024);
            string expected=(config.Port==22?config.Host:"["+config.Host+"]:"+config.Port.ToString(System.Globalization.CultureInfo.InvariantCulture))+" ssh-ed25519 "+config.HostKey+"\n";
            byte[] bytes=new byte[checked((int)hosts.Stream.Length)];hosts.Stream.ReadExactly(bytes);hosts.Stream.Position=0;
            if(!bytes.AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(expected)))throw Denied();
            // GetSystemWindowsDirectoryW is OS authority; WINDIR/PATH/SSH environment is never read.
            stage="SYSTEM_SSH";var windows=new StringBuilder(32768);uint n=GetSystemWindowsDirectoryW(windows,(uint)windows.Capacity);
            if(n==0||n>=windows.Capacity||!Environment.Is64BitProcess)throw Denied();
            string exe=Path.Combine(ExactSyntax(windows.ToString()),"System32","OpenSSH","ssh.exe");
            lease.HoldDirectoryChain(Path.GetDirectoryName(exe)!,null,false);
            lease.HoldFile(exe,false,64*1024*1024);
            lease.Spec=OkxControllerSshLaunchSpec.Build(new(config.Host,config.Port,config.User,identity,knownHosts),exe,"NUL");
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
    private void HoldDirectoryChain(string target,string? privateRoot,bool checkLocation)
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
                WindowsPathSecurity.Validate(opened,privateTarget);
                if(privateTarget)privateDirectories.Add(opened.Path);
                directories.Add(opened);opened=null!;
            }
            finally{opened?.Dispose();}
        }
    }
    private OpenedFile HoldFile(string path,bool privateFile,long maximum)
    {
        var file=new OpenedFile(ExactSyntax(path),privateFile,maximum,sid);
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
                    WindowsPathSecurity.Validate(d,privateDirectories.Contains(d.Path));
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
        internal bool Private {get;}
        internal FileStream Stream {get;}
        private readonly WindowsPathSecurity.FileInformation original;
        private readonly string owner;
        private readonly long maximum;
        internal OpenedFile(string path,bool privateFile,long max,string sid)
        {
            Path=path;Private=privateFile;owner=sid;maximum=max;
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
            if(!GetFileInformationByHandle(handle,out var info)||info.Links!=1||(info.Attributes&(uint)(FileAttributes.Directory|FileAttributes.ReparsePoint))!=0||info.VolumeSerial!=original.VolumeSerial||info.IndexHigh!=original.IndexHigh||info.IndexLow!=original.IndexLow||Stream.Length is <=0||Stream.Length>maximum)throw Denied("FILE_IDENTITY");
            var name=new StringBuilder(32768);uint n=GetFinalPathNameByHandleW(handle,name,(uint)name.Capacity,0);
            if(n==0||n>=name.Capacity||!name.ToString().Equals("\\\\?\\"+Path,StringComparison.OrdinalIgnoreCase))throw Denied();
            using(var view=new WindowsPathSecurity.NativeObject(new SafeFileHandle(handle.DangerousGetHandle(),false),Path,info))WindowsPathSecurity.Validate(view,Private);
            if(GetSecurityInfo(handle,1,5,out _,out _,out _,out _,out var sd)!=0||sd==IntPtr.Zero)throw Denied();
            try
            {
                uint size=GetSecurityDescriptorLength(sd);if(size is 0 or >65536)throw Denied();
                byte[] bytes=new byte[size];Marshal.Copy(sd,bytes,0,bytes.Length);var acl=new RawSecurityDescriptor(bytes,0);
                if(Private&&acl.Owner?.Value!=owner)throw Denied("FILE_OWNER");
                // Non-private executable may be readable, never writable, by untrusted principals.
                foreach(GenericAce a in acl.DiscretionaryAcl!)if(a is CommonAce ace&&ace.AceType==AceType.AccessAllowed&&ace.SecurityIdentifier.Value!=owner&&!ace.SecurityIdentifier.IsWellKnown(WellKnownSidType.LocalSystemSid)&&!ace.SecurityIdentifier.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)&&ace.SecurityIdentifier.Value!="S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"&&(ace.AccessMask&unchecked((int)0x500D0116))!=0)throw Denied();
            }
            finally{LocalFree(sd);}
        }
        public void Dispose()=>Stream.Dispose();
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern uint GetSystemWindowsDirectoryW(StringBuilder buffer,uint size);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr attributes,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetFileInformationByHandle(SafeFileHandle file,out WindowsPathSecurity.FileInformation info);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file,StringBuilder path,uint size,uint flags);
    [DllImport("advapi32.dll")]private static extern uint GetSecurityInfo(SafeFileHandle file,uint type,uint information,out IntPtr owner,out IntPtr group,out IntPtr dacl,out IntPtr sacl,out IntPtr descriptor);
    [DllImport("advapi32.dll")]private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")]private static extern IntPtr LocalFree(IntPtr pointer);
}
