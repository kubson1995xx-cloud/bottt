using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Tasks;
using Apex.Desktop.Okx;
using Apex.Desktop.Services;
using Xunit;
namespace Apex.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[Collection("OKX composition"),Trait("Gate","NativeWindows")]
public sealed class OkxNativeBrokerStoreTests
{
    [Fact]
    public async Task Native_DPAPI_observation_baseline_real_store_reopens_STOP_and_rejects_broad_ACL_without_repair()
    {
        Assert.True(OperatingSystem.IsWindows(),"Native Windows required, never substitute Linux");
        await using var f=new OperationalPeer(native:true);
        await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));
        Assert.Equal(1234.56m,f.Service.Coordinator!.Snapshot().Cash);
        await f.Service.DisconnectAsync();string original=File.ReadAllText(f.Target);
        Assert.True(f.Service.OpenExisting());Assert.Equal("STOP",f.Service.Coordinator!.Snapshot().Mode);
        Assert.Equal(original,File.ReadAllText(f.Target));await f.Service.DisconnectAsync();
        var file=new FileInfo(f.Target);var acl=file.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid,null),FileSystemRights.ReadData,AccessControlType.Allow));
        file.SetAccessControl(acl);string before=file.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
        Assert.False(f.Service.OpenExisting());Assert.Equal(before,file.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
    }
    [Fact]
    public async Task Native_authority_copy_to_new_root_cannot_rebind_initial_capital()
    {
        Assert.True(OperatingSystem.IsWindows());
        await using var f=new OperationalPeer(native:true);await f.Connect();Assert.True(await f.Service.ApproveAndCreateAsync(default));await f.Service.DisconnectAsync();
        using var h=new NativeRoot();
        new WindowsOkxCredentialStore(h.Path).Create(f.Binding,OkxConfiguration.Paths(h.Path,f.Binding),"DemoTradingApi",OperationalPeer.Key,OperationalPeer.Secret,OperationalPeer.Pass);
        using(var store=OkxNavalStore.OpenNative(h.Path,f.Binding))foreach(string path in Directory.EnumerateFiles(Path.GetDirectoryName(f.Target)!))File.Copy(path,System.IO.Path.Combine(store.DirectoryPath,System.IO.Path.GetFileName(path)));
        await using var runtime=OkxNavalRuntime.Compose(h.Path,new[]{f.Binding});
        var service=runtime.AccountService(f.Binding.ScopeId);Assert.False(service.OpenExisting());
    }
    private sealed class NativeRoot:IDisposable
    {
        internal readonly string Path=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"okx-copy-"+Guid.NewGuid().ToString("N"));
        internal NativeRoot(){using var lease=WindowsPrivateData.Open(Path,AppContext.BaseDirectory);}
        public void Dispose()=>Directory.Delete(Path,true);
    }
}
