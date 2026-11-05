using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Apex.Desktop.Okx;
using Apex.Desktop.Services;
using Xunit;
namespace Apex.ControllerAdmission.Tests;
public sealed class NativeAdmissionTests
{
    [Fact] public void Valid_native_profile_issues_spec_bound_start_info_without_starting_ssh()
    {
        using var f=new Profile();
        // Real native ACL/path admission succeeds before testing the missing transport boundary.
        using(var root=WindowsPrivateData.Open(f.Root,AppContext.BaseDirectory)) root.ValidateTree();
        var type=typeof(OkxControllerSnapshot).Assembly.GetType("Apex.Desktop.Okx.WindowsOkxControllerTransportAdmission");
        Assert.NotNull(type);
        var configType=type.Assembly.GetType("Apex.Desktop.Okx.WindowsOkxControllerTransportConfig");Assert.NotNull(configType);
        var config=Activator.CreateInstance(configType,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new object[]{"vps.example",2222,"apex",f.IdentityDigest,Profile.Pin},null)!;
        IDisposable opened;
        try{opened=(IDisposable)type.GetMethod("Open",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new[]{f.Root,config})!;}
        catch(TargetInvocationException e){throw new Xunit.Sdk.XunitException("Native admission stage="+e.InnerException?.Data["Stage"]+" check="+e.InnerException?.Data["Check"]);}
        using var lease=opened;
        var spec=(OkxControllerSshLaunchSpec)type.GetProperty("Spec",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(lease)!;
        var capability=(OkxControllerSshLaunchSpec.AdmittedTransportCapability)type.GetProperty("Capability",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(lease)!;
        var info=spec.CreateStartInfo(capability);
        Assert.EndsWith(@"\System32\OpenSSH\ssh.exe",info.FileName,StringComparison.OrdinalIgnoreCase);
        Assert.False(info.UseShellExecute);Assert.Empty(info.Environment);Assert.Contains("StrictHostKeyChecking=yes",info.ArgumentList);
        Assert.DoesNotContain(Profile.FakeIdentity,info.ArgumentList);Assert.Equal(OkxControllerSshLaunchSpec.Relay,info.ArgumentList.Last());
    }
    [Fact] public void Foreign_identical_spec_other_lease_null_and_disposal_cannot_authorize_start_info()
    {
        using var f=new Profile();using var other=new Profile();
        using var lease=WindowsOkxControllerTransportAdmission.Open(f.Root,f.Config);
        using var second=WindowsOkxControllerTransportAdmission.Open(other.Root,other.Config);
        var foreign=OkxControllerSshLaunchSpec.Build(new("vps.example",2222,"apex",f.Identity,f.KnownHosts),lease.Spec.Executable,"NUL");
        Assert.Throws<InvalidOperationException>(()=>foreign.CreateStartInfo(lease.Capability));
        Assert.Throws<InvalidOperationException>(()=>second.Spec.CreateStartInfo(lease.Capability));
        Assert.Throws<InvalidOperationException>(()=>lease.Spec.CreateStartInfo(null!));
        Assert.NotNull(lease.Spec.CreateStartInfo(lease.Capability));
        var cap=lease.Capability;lease.Dispose();lease.Dispose();
        Assert.Throws<InvalidOperationException>(()=>lease.Spec.CreateStartInfo(cap));
        Assert.Throws<InvalidOperationException>(()=>OkxControllerSshLaunchSpec.AdmittedTransportCapability.Issue(lease));
        Assert.NotNull(second.Spec.CreateStartInfo(second.Capability));
    }
    [Fact] public void Native_read_sharing_leases_block_key_pin_and_directory_replacement_until_dispose()
    {
        using var f=new Profile();using var lease=WindowsOkxControllerTransportAdmission.Open(f.Root,f.Config);
        foreach(var p in new[]{f.Identity,f.KnownHosts})
        {
            Assert.Throws<IOException>(()=>File.WriteAllText(p,"replacement"));
            Assert.Throws<IOException>(()=>File.Delete(p));
            Assert.Throws<IOException>(()=>File.Move(p,p+".old"));
        }
        Assert.Throws<IOException>(()=>Directory.Move(f.DirectoryPath,f.DirectoryPath+"-moved"));
        Assert.NotNull(lease.Spec.CreateStartInfo(lease.Capability));
        lease.Dispose();File.WriteAllText(f.KnownHosts,"replacement");
    }
    [Theory][InlineData("key")][InlineData("pin")][InlineData("host")][InlineData("port")]
    public void Explicit_config_binding_refuses_mismatched_key_pin_host_or_port(string field)
    {
        using var f=new Profile();Control(f);var c=f.Config;
        c=field switch{"key"=>c with{IdentitySha256=new string('0',64)},"pin"=>c with{HostKey=Convert.ToBase64String(Convert.FromBase64String(Profile.Pin).Select((b,i)=>i==50?(byte)(b+1):b).ToArray())},"host"=>c with{Host="other.example"},_=>c with{Port=22}};
        Refuses(f.Root,c);Assert.Equal(Profile.FakeIdentity,File.ReadAllText(f.Identity));
    }
    [Theory][InlineData("wildcard")][InlineData("duplicate")][InlineData("crlf")][InlineData("comment")][InlineData("hashed")][InlineData("oversize")]
    public void Known_hosts_requires_one_exact_bounded_canonical_pinned_line(string kind)
    {
        using var f=new Profile();Control(f);string line=File.ReadAllText(f.KnownHosts);
        string mutant=kind switch{"wildcard"=>line.Replace("[vps.example]:2222","*"),"duplicate"=>line+line,"crlf"=>line.Replace("\n","\r\n"),"comment"=>line.TrimEnd()+" comment\n","hashed"=>line.Replace("[vps.example]:2222","|1|fake|fake"),_=>new string('x',1025)};
        File.WriteAllText(f.KnownHosts,mutant,new UTF8Encoding(false));Refuses(f.Root,f.Config);Assert.Equal(mutant,File.ReadAllText(f.KnownHosts));
    }
    [Theory][InlineData("root")][InlineData("profile")][InlineData("identity")][InlineData("known_hosts")]
    public void Broad_native_acl_is_refused_without_repair(string target)
    {
        using var f=new Profile();Control(f);string p=target switch{"root"=>f.Root,"profile"=>f.DirectoryPath,"identity"=>f.Identity,_=>f.KnownHosts};
        FileSystemSecurity acl=Directory.Exists(p)?new DirectoryInfo(p).GetAccessControl():new FileInfo(p).GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid,null),FileSystemRights.Read,AccessControlType.Allow));
        if(acl is DirectorySecurity d)new DirectoryInfo(p).SetAccessControl(d);else new FileInfo(p).SetAccessControl((FileSecurity)acl);
        FileSystemSecurity persisted=Directory.Exists(p)?new DirectoryInfo(p).GetAccessControl():new FileInfo(p).GetAccessControl();
        var before=persisted.GetSecurityDescriptorBinaryForm();Refuses(f.Root,f.Config);
        FileSystemSecurity after=Directory.Exists(p)?new DirectoryInfo(p).GetAccessControl():new FileInfo(p).GetAccessControl();Assert.Equal(before,after.GetSecurityDescriptorBinaryForm());
    }
    [Theory][InlineData("root")][InlineData("profile")][InlineData("identity")]
    public void Changed_acl_after_issuance_revokes_existing_capability(string target)
    {
        using var f=new Profile();using var lease=WindowsOkxControllerTransportAdmission.Open(f.Root,f.Config);
        string p=target=="root"?f.Root:target=="profile"?f.DirectoryPath:f.Identity;
        FileSystemSecurity acl=Directory.Exists(p)?new DirectoryInfo(p).GetAccessControl():new FileInfo(p).GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid,null),FileSystemRights.Read,AccessControlType.Allow));
        if(acl is DirectorySecurity d)new DirectoryInfo(p).SetAccessControl(d);else new FileInfo(p).SetAccessControl((FileSecurity)acl);
        Assert.Throws<InvalidOperationException>(()=>lease.Spec.CreateStartInfo(lease.Capability));
    }
    [Theory][InlineData("identity")][InlineData("known_hosts")]
    public void Hard_linked_file_refuses_actual_multi_link_native_identity(string leaf)
    {
        using var f=new Profile();Control(f);string target=leaf=="identity"?f.Identity:f.KnownHosts;
        string extra=Path.Combine(f.Root,"hardlink");Assert.True(CreateHardLinkW(extra,target,IntPtr.Zero));
        Refuses(f.Root,f.Config);Assert.True(File.Exists(extra));
    }
    [Fact] public void Reparse_profile_is_refused_without_following_or_repair()
    {
        using var f=new Profile();Control(f);string moved=Path.Combine(f.Root,"real-profile");Directory.Move(f.DirectoryPath,moved);
        Directory.CreateSymbolicLink(f.DirectoryPath,moved);
        try{Refuses(f.Root,f.Config);Assert.True((File.GetAttributes(f.DirectoryPath)&FileAttributes.ReparsePoint)!=0);}
        finally{Directory.Delete(f.DirectoryPath);Directory.Move(moved,f.DirectoryPath);}
    }
    [Fact] public void Reparse_key_is_refused_on_the_actual_read_handle()
    {
        using var f=new Profile();Control(f);string moved=Path.Combine(f.Root,"real-identity");File.Move(f.Identity,moved);File.CreateSymbolicLink(f.Identity,moved);
        Refuses(f.Root,f.Config);Assert.True((File.GetAttributes(f.Identity)&FileAttributes.ReparsePoint)!=0);
    }
    [Theory][InlineData("unc")][InlineData("environment")][InlineData("alias")][InlineData("traversal")][InlineData("device")]
    public void Noncanonical_root_syntax_or_native_alias_refuses(string kind)
    {
        using var f=new Profile();Control(f);var b=new StringBuilder(32768);Assert.NotEqual(0u,GetShortPathNameW(f.Root,b,b.Capacity));
        string alias=b.ToString();Assert.NotEqual(f.Root.ToUpperInvariant(),alias.ToUpperInvariant());
        string root=kind switch{"unc"=>@"\\localhost\"+f.Root[0]+"$"+f.Root[2..],"environment"=>f.Root+@"\%PROFILE%","alias"=>alias,"device"=>@"\\?\"+f.Root,_=>f.Root+@"\..\"+Path.GetFileName(f.Root)};
        Refuses(root,f.Config);
    }
    [Theory][InlineData("missing")][InlineData("extra")][InlineData("financial")]
    public void Missing_closed_profile_extra_companion_and_financial_root_refuse_without_provision(string kind)
    {
        using var f=new Profile();Control(f);
        if(kind=="missing"){Directory.Delete(f.DirectoryPath,true);Refuses(f.Root,f.Config);Assert.False(Directory.Exists(f.DirectoryPath));}
        else if(kind=="extra"){File.WriteAllText(f.Identity+"-cert.pub","synthetic");Refuses(f.Root,f.Config);Assert.True(File.Exists(f.Identity+"-cert.pub"));}
        else{File.WriteAllText(Path.Combine(f.Root,".naval-owner.lock"),"synthetic");Refuses(f.Root,f.Config);}
    }
    [Fact] public void System_ssh_is_not_selected_from_caller_environment()
    {
        using var f=new Profile();string? previous=Environment.GetEnvironmentVariable("WINDIR");
        try{Environment.SetEnvironmentVariable("WINDIR",f.Root);using var lease=WindowsOkxControllerTransportAdmission.Open(f.Root,f.Config);Assert.DoesNotContain(f.Root,lease.Spec.Executable);Assert.EndsWith(@"\System32\OpenSSH\ssh.exe",lease.Spec.Executable,StringComparison.OrdinalIgnoreCase);}
        finally{Environment.SetEnvironmentVariable("WINDIR",previous);}
    }
    [Fact] public void Wrong_platform_refuses_before_accessing_any_path()
    {
        if(OperatingSystem.IsWindows())return; // Executed separately on actual Linux; not native positive evidence.
        Assert.Throws<PlatformNotSupportedException>(()=>WindowsOkxControllerTransportAdmission.Open("/not-touched",new("vps.example",2222,"apex",new string('0',64),Profile.Pin)));
    }
    [Fact] public void Capability_expires_on_real_monotonic_time_while_native_handles_remain_held()
    {
        using var f=new Profile();using var lease=WindowsOkxControllerTransportAdmission.Open(f.Root,f.Config);
        Assert.NotNull(lease.Spec.CreateStartInfo(lease.Capability));
        Thread.Sleep(TimeSpan.FromSeconds(31));
        Assert.Throws<InvalidOperationException>(()=>lease.Spec.CreateStartInfo(lease.Capability));
        Assert.Throws<IOException>(()=>File.Delete(f.Identity));
    }
    [Fact] public void Default_port_uses_exact_unbracketed_hostname_pin()
    {
        using var f=new Profile();Control(f);File.WriteAllText(f.KnownHosts,"vps.example ssh-ed25519 "+Profile.Pin+"\n",new UTF8Encoding(false));
        using var lease=WindowsOkxControllerTransportAdmission.Open(f.Root,f.Config with{Port=22});Assert.NotNull(lease.Spec.CreateStartInfo(lease.Capability));
    }
    private static void Control(Profile f)
    {using var lease=WindowsOkxControllerTransportAdmission.Open(f.Root,f.Config);Assert.NotNull(lease.Spec.CreateStartInfo(lease.Capability));}
    private static void Refuses(string root,WindowsOkxControllerTransportConfig config)
    {Assert.Equal(WindowsOkxControllerTransportAdmission.Refused,Assert.Throws<InvalidOperationException>(()=>WindowsOkxControllerTransportAdmission.Open(root,config).Dispose()).Message);}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool CreateHardLinkW(string newName,string existing,IntPtr attributes);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern uint GetShortPathNameW(string path,StringBuilder output,int length);
    internal sealed class Profile:IDisposable
    {
        internal const string FakeIdentity="SYNTHETIC-NOT-A-PRIVATE-KEY-controller-admission";
        // SSH wire encoding: string ssh-ed25519, string 32-byte synthetic public key.
        internal static string Pin {get;}=Convert.ToBase64String(new byte[]{0,0,0,11}.Concat(System.Text.Encoding.ASCII.GetBytes("ssh-ed25519")).Concat(new byte[]{0,0,0,32}).Concat(Enumerable.Range(1,32).Select(i=>(byte)i)).ToArray());
        internal string Root {get;}
        internal string DirectoryPath=>Path.Combine(Root,"controller-ssh");
        internal string Identity=>Path.Combine(DirectoryPath,"identity");
        internal string KnownHosts=>Path.Combine(DirectoryPath,"known_hosts");
        internal string IdentityDigest=>Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(FakeIdentity))).ToLowerInvariant();
        internal WindowsOkxControllerTransportConfig Config=>new("vps.example",2222,"apex",IdentityDigest,Pin);
        internal Profile()
        {
            Assert.True(OperatingSystem.IsWindows(),"Native admission cannot pass on Linux");
            Root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ApexControllerAdmission-"+Guid.NewGuid().ToString("N"));
            WindowsPathSecurity.CreatePrivateDirectory(Root);WindowsPathSecurity.CreatePrivateDirectory(DirectoryPath);
            File.WriteAllText(Identity,FakeIdentity,new System.Text.UTF8Encoding(false));
            File.WriteAllText(KnownHosts,"[vps.example]:2222 ssh-ed25519 "+Pin+"\n",new System.Text.UTF8Encoding(false));
            // Elevated Windows token defaults file ownership to Administrators. Provision the
            // synthetic fixture with the required current-SID owner, before any admission.
            foreach(string p in new[]{Identity,KnownHosts})
            {var acl=new FileInfo(p).GetAccessControl();acl.SetOwner(WindowsIdentity.GetCurrent().User!);new FileInfo(p).SetAccessControl(acl);}
        }
        public void Dispose(){Directory.Delete(Root,true);}
    }
}
