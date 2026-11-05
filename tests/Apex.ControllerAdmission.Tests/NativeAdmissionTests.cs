using System.Reflection;
using System.Security.Cryptography;
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
        using var lease=(IDisposable)type.GetMethod("Open",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new[]{f.Root,config})!;
        var spec=(OkxControllerSshLaunchSpec)type.GetProperty("Spec",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(lease)!;
        var capability=(OkxControllerSshLaunchSpec.AdmittedTransportCapability)type.GetProperty("Capability",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(lease)!;
        var info=spec.CreateStartInfo(capability);
        Assert.EndsWith(@"\System32\OpenSSH\ssh.exe",info.FileName,StringComparison.OrdinalIgnoreCase);
        Assert.False(info.UseShellExecute);Assert.Empty(info.Environment);Assert.Contains("StrictHostKeyChecking=yes",info.ArgumentList);
        Assert.DoesNotContain(Profile.FakeIdentity,info.ArgumentList);Assert.Equal(OkxControllerSshLaunchSpec.Relay,info.ArgumentList.Last());
    }
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
        internal Profile()
        {
            Assert.True(OperatingSystem.IsWindows(),"Native admission cannot pass on Linux");
            Root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ApexControllerAdmission-"+Guid.NewGuid().ToString("N"));
            WindowsPathSecurity.CreatePrivateDirectory(Root);WindowsPathSecurity.CreatePrivateDirectory(DirectoryPath);
            File.WriteAllText(Identity,FakeIdentity,new System.Text.UTF8Encoding(false));
            File.WriteAllText(KnownHosts,"[vps.example]:2222 ssh-ed25519 "+Pin+"\n",new System.Text.UTF8Encoding(false));
        }
        public void Dispose(){Directory.Delete(Root,true);}
    }
}
