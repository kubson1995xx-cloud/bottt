using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Apex.Desktop.Services;
using Microsoft.Win32.SafeHandles;
namespace Apex.Desktop.Okx;

// One-time, native, offline setup. Never imports private keys or broker credentials.
// The accepted OS OpenSSH consumer requires a local ACL-protected identity file;
// presentation access retains its existing CurrentUser DPAPI policy unchanged.
internal static class WindowsOkxControllerPairing
{
    internal static async Task<bool> ShowAsync(string root,CancellationToken ct)
    {
        try
        {
            var form=await WindowsOkxDialog.ShowDetailedAsync("APEX — parowanie kontrolera VPS",
                "Tylko konfiguracja SSH, bez połączenia i handlu. Host, użytkownik i klucz hosta muszą pochodzić od administratora VPS przez zaufany kanał. Nie wpisuj kluczy OKX ani klucza prywatnego.",
                new[]{new OkxNativeField("Host VPS"),new OkxNativeField("Port SSH",false,"22"),new OkxNativeField("Użytkownik SSH"),new OkxNativeField("Klucz hosta: ssh-ed25519 BASE64")},ct,
                values=>Input(values,out _,out _,out _,out _)?null:"PAIRING_METADATA_INVALID");
            if(form.Values is not {} values)return false;
            try
            {
                if(!Input(values,out string host,out int port,out string user,out string key))return false;
                string fingerprint="SHA256:"+Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(key))).TrimEnd('=');
                // Deliberate default-Cancel trust. No ssh-keyscan / trust-on-first-use.
                if(ct.IsCancellationRequested||MessageBoxW(IntPtr.Zero,
                    $"VPS: {host}:{port}\nUżytkownik: {user}\nKlucz: ssh-ed25519 {key}\nOdcisk: {fingerprint}\n\nPorównaj pełny odcisk z administratorem VPS przez niezależny zaufany kanał. OK oznacza świadome zaufanie temu kluczowi hosta. Anuluj, jeśli nie możesz go potwierdzić.\n\nPowstanie nowy lokalny klucz SSH. Tylko klucz PUBLICZNY należy przekazać administratorowi do instalacji wymuszonego relay. Nie połączymy VPS ani brokera.",
                    "APEX — potwierdź zaufany klucz hosta",0x00010141)!=1)return false;
                bool saved=await ProvisionAsync(root,host,port,user,key,true,ct);
                MessageBoxW(IntPtr.Zero,saved?
                    "Zapisano konfigurację bez sieci i handlu. Klucz PUBLICZNY: controller-public-key.txt w prywatnym katalogu danych APEX. Przekaż wyłącznie ten plik administratorowi VPS do instalacji ograniczonego relay. Nigdy nie przekazuj controller-ssh/identity. Uruchom APEX ponownie, potem jawnie wybierz Połącz kanał VPS. Połączenie konta, PREPARE, ARM i START pozostają osobnymi działaniami.":
                    "PAIRING_REFUSED — nie nadpisano istniejącego profilu. Zachowaj dane; profil częściowy, szerokie uprawnienia lub brak systemowego OpenSSH wymagają sprawdzenia. Nie usuwaj plików, aby wymusić ponowienie.","APEX — konfiguracja kontrolera",0x00010040);
                return saved;
            }
            finally{foreach(var value in values)CryptographicOperations.ZeroMemory(value);}
        }
        catch(Exception e) when(e is not OutOfMemoryException){return false;}
    }
    private static bool Input(byte[][] values,out string host,out int port,out string user,out string key)
    {
        host=user=key="";port=0;
        if(values.Length!=4)return false;
        host=Encoding.UTF8.GetString(values[0]);user=Encoding.UTF8.GetString(values[2]);string publicKey=Encoding.UTF8.GetString(values[3]);
        if(!publicKey.StartsWith("ssh-ed25519 ",StringComparison.Ordinal))return false;
        key=publicKey[12..];
        return int.TryParse(Encoding.UTF8.GetString(values[1]),NumberStyles.None,CultureInfo.InvariantCulture,out port)&&Valid(host,port,user,key);
    }
    private static bool Valid(string host,int port,string user,string key)
    {
        if(host==null||host.Length is <1 or >253||!Regex.IsMatch(host,@"\A[a-z0-9]+(?:[.-][a-z0-9]+)*\z")||host.Split('.').Any(s=>s.Length>63)||port is <1 or >65535||user==null||!Regex.IsMatch(user,@"\A[a-z_][a-z0-9_-]{0,31}\z")||key==null||key.Length!=68)return false;
        try{var wire=Convert.FromBase64String(key);return wire.Length==51&&Convert.ToBase64String(wire)==key&&wire.AsSpan(0,19).SequenceEqual(new byte[]{0,0,0,11,115,115,104,45,101,100,50,53,53,49,57,0,0,0,32})&&wire.AsSpan(19).IndexOfAnyExcept((byte)0)>=0;}
        catch(FormatException){return false;}
    }
    internal static async Task<bool> ProvisionAsync(string root,string host,int port,string user,string key,bool trusted,CancellationToken ct)
    {
        // Reject invalid/unconfirmed input before writes. Main holds apex.lock; direct
        // native callers also serialize provisioning with a CREATE_NEW pending marker.
        if(!OperatingSystem.IsWindows()||!trusted||ct.IsCancellationRequested||!Valid(host,port,user,key))return false;
        string stage="ROOT";
        var systemDirectories=new List<WindowsPathSecurity.NativeObject>();
        try
        {
            using var lease=WindowsPrivateData.Open(root,AppContext.BaseDirectory);
            root=lease.Path;
            // Refuse unexplained startup state; never normalize/reset a partial profile.
            stage="PRESENTATION";_=WindowsLocalSetup.Ensure(root,Path.Combine(AppContext.BaseDirectory,"presentation"));
            string profile=Path.Combine(root,"controller-ssh"),metadata=Path.Combine(root,"controller-connection.json"),export=Path.Combine(root,"controller-public-key.txt"),pending=Path.Combine(root,"controller-pairing.pending");
            if(new[]{profile,metadata,export,pending}.Any(p=>File.Exists(p)||Directory.Exists(p)))return false;
            // OS-selected, protected native key generator; never PATH/WINDIR input.
            var windows=new StringBuilder(32768);uint n=GetSystemWindowsDirectoryW(windows,(uint)windows.Capacity);
            if(n==0||n>=windows.Capacity||!Environment.Is64BitProcess)return false;
            string exe=Path.Combine(windows.ToString(),"System32","OpenSSH","ssh-keygen.exe");
            foreach(string path in WindowsPrivateData.Components(Path.GetDirectoryName(exe)!))
            {
                var opened=WindowsPathSecurity.Open(path);systemDirectories.Add(opened);
                if(!opened.IsDirectory||!opened.Path.Equals(path,StringComparison.OrdinalIgnoreCase))return false;
                ValidateKeygenSecurity(opened.Handle,true,path.Equals(Path.GetDirectoryName(exe),StringComparison.OrdinalIgnoreCase));
            }
            stage="KEYGEN_ADMISSION";using var executable=WindowsPathSecurity.Open(exe);
            if(executable.IsDirectory||!executable.Path.Equals(exe,StringComparison.OrdinalIgnoreCase))return false;
            ValidateKeygenSecurity(executable.Handle,false,false);
            using var executableRead=CreateFileW(exe,0x80020000,1,IntPtr.Zero,3,0x00200000,IntPtr.Zero);
            if(executableRead.IsInvalid||!GetFileInformationByHandle(executableRead,out var executableIdentity))return false;
            using var executableView=new WindowsPathSecurity.NativeObject(new SafeFileHandle(executableRead.DangerousGetHandle(),false),exe,executableIdentity);
            if(!executableView.SameIdentity(executable))return false;
            stage="PENDING";using var marker=PrivateFile(pending);marker.WriteByte(1);marker.Flush(true);
            // A crash/failure deliberately leaves pending/partial state for explicit
            // investigation, never silent reenrollment or key replacement on restart.
            WindowsPathSecurity.CreatePrivateDirectory(profile);
            using var directory=WindowsPathSecurity.Open(profile);WindowsPathSecurity.Validate(directory,true);
            string identity=Path.Combine(profile,"identity");
            var start=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            start.Environment.Clear();start.Environment["SystemRoot"]=windows.ToString();start.Environment["PROGRAMDATA"]=Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            foreach(string arg in new[]{"-q","-t","ed25519","-N","","-C","apex-controller","-f",identity})start.ArgumentList.Add(arg);
            stage="KEYGEN";using var child=Process.Start(start)??throw new IOException();
            var output=Drain(child.StandardOutput.BaseStream);var error=Drain(child.StandardError.BaseStream);
            using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);budget.CancelAfter(TimeSpan.FromSeconds(15));
            try{await child.WaitForExitAsync(budget.Token);await Task.WhenAll(output,error).WaitAsync(budget.Token);if(child.ExitCode!=0){Console.Error.WriteLine("CONTROLLER_PAIRING_KEYGEN_EXIT_"+child.ExitCode.ToString(CultureInfo.InvariantCulture));return false;}}
            finally{if(!child.HasExited)child.Kill(true);await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}
            ct.ThrowIfCancellationRequested();
            // Only files created by this invocation receive their explicit current SID
            // owner. Existing files/roots are never repaired.
            stage="GENERATED_FILES";foreach(string p in new[]{identity,identity+".pub"})
            {
                var file=new FileInfo(p);var acl=file.GetAccessControl();acl.SetOwner(WindowsIdentity.GetCurrent().User!);file.SetAccessControl(acl);
                using var opened=WindowsPathSecurity.Open(p);WindowsPathSecurity.Validate(opened,true);
            }
            string publicKey=File.ReadAllText(identity+".pub");
            if(!publicKey.StartsWith("ssh-ed25519 ",StringComparison.Ordinal)||publicKey.Length>256)return false;
            stage="PUBLIC_EXPORT";using(var file=PrivateFile(export)){file.Write(Encoding.ASCII.GetBytes(publicKey));file.Flush(true);}
            File.Delete(identity+".pub");
            string hosts=(port==22?host:"["+host+"]:"+port.ToString(CultureInfo.InvariantCulture))+" ssh-ed25519 "+key+"\n";
            using(var file=PrivateFile(Path.Combine(profile,"known_hosts"))){file.Write(Encoding.ASCII.GetBytes(hosts));file.Flush(true);}
            string digest;using(var file=File.OpenRead(identity))digest=Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
            var config=new WindowsOkxControllerTransportConfig(host,port,user,digest,key);
            stage="PROFILE_ADMISSION";using(var admission=WindowsOkxControllerTransportAdmission.Open(root,config)){}
            ct.ThrowIfCancellationRequested();lease.ValidateDirectories();
            stage="METADATA";using(var file=PrivateFile(metadata)){file.Write(JsonSerializer.SerializeToUtf8Bytes(new{Schema="apex-controller-connection-v1",Host=host,Port=port,User=user,IdentitySha256=digest,HostKey=key}));file.Flush(true);}
            // Actual strict native readback; this never starts SSH or touches a broker.
            stage="READBACK";if(WindowsOkxControllerConnectionStore.Load(root)!=config)return false;
            marker.Dispose();File.Delete(pending);
            return true;
        }
        catch(Exception e) when(e is not OutOfMemoryException){Console.Error.WriteLine("CONTROLLER_PAIRING_REFUSED:"+stage+":"+(e is WindowsDataException w?w.Code:e.GetType().Name));return false;}
        finally{foreach(var directory in systemDirectories.AsEnumerable().Reverse())directory.Dispose();}
    }
    // Same narrow OS-servicing trust as the accepted transport, confined here to
    // the OS-selected ssh-keygen and its system ancestry; never used on private data.
    private static void ValidateKeygenSecurity(SafeFileHandle handle,bool directory,bool closed)
    {
        if(GetSecurityInfo(handle,1,5,out _,out _,out _,out _,out var sd)!=0||sd==IntPtr.Zero)throw new IOException("KEYGEN_SECURITY");
        try
        {
            uint size=GetSecurityDescriptorLength(sd);if(size is 0 or >65536)throw new IOException("KEYGEN_SECURITY");
            var bytes=new byte[size];Marshal.Copy(sd,bytes,0,bytes.Length);var descriptor=new RawSecurityDescriptor(bytes,0);
            bool Trusted(SecurityIdentifier? s)=>s!=null&&(s.IsWellKnown(WellKnownSidType.LocalSystemSid)||s.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)||s.Value=="S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
            if(!Trusted(descriptor.Owner)||(descriptor.ControlFlags&ControlFlags.DiscretionaryAclPresent)==0||descriptor.DiscretionaryAcl==null)throw new IOException("KEYGEN_SECURITY");
            foreach(GenericAce entry in descriptor.DiscretionaryAcl)
            {
                if(entry is not CommonAce ace||ace.IsCallback||ace.AceType is not(AceType.AccessAllowed or AceType.AccessDenied))throw new IOException("KEYGEN_SECURITY");
                if(ace.AceType==AceType.AccessDenied||Trusted(ace.SecurityIdentifier)||(ace.AceFlags&AceFlags.InheritOnly)!=0)continue;
                int rights=directory?(closed?unchecked((int)0x500D0156):unchecked((int)0x500D0150)):unchecked((int)0x500D0116);
                if((ace.AccessMask&rights)!=0)throw new IOException("KEYGEN_SECURITY");
            }
        }
        finally{LocalFree(sd);}
    }
    private static FileStream PrivateFile(string path)
    {
        var me=WindowsIdentity.GetCurrent().User!;var security=new FileSecurity();security.SetOwner(me);security.SetAccessRuleProtection(true,false);
        foreach(var sid in new[]{me,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)})security.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,AccessControlType.Allow));
        return FileSystemAclExtensions.Create(new FileInfo(path),FileMode.CreateNew,FileSystemRights.FullControl,FileShare.None,4096,FileOptions.WriteThrough,security);
    }
    private static async Task Drain(Stream stream)
    {byte[] buffer=new byte[4096];int total=0,n;while((n=await stream.ReadAsync(buffer))>0){total+=n;if(total>65536)throw new IOException("PAIRING_OUTPUT_LIMIT");}}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]private static extern uint GetSystemWindowsDirectoryW(StringBuilder buffer,uint length);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr attributes,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetFileInformationByHandle(SafeFileHandle file,out WindowsPathSecurity.FileInformation info);
    [DllImport("advapi32.dll")]private static extern uint GetSecurityInfo(SafeFileHandle file,uint type,uint information,out IntPtr owner,out IntPtr group,out IntPtr dacl,out IntPtr sacl,out IntPtr descriptor);
    [DllImport("advapi32.dll")]private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")]private static extern IntPtr LocalFree(IntPtr pointer);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]private static extern int MessageBoxW(IntPtr window,string text,string caption,uint flags);
}
