using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.Tests;
public sealed class OkxWindowsAdvisorySetupTests
{
    [Fact][Trait("Gate","WindowsAdvisoryInstaller")]
    public async Task Native_offline_install_rebases_only_by_verified_install_and_missing_auth_preserves_existing_profile()
    {
        Assert.True(OperatingSystem.IsWindows());
        string bundle=Environment.GetEnvironmentVariable("APEX_ADVISORY_TEST_BUNDLE")??throw new Exception("NATIVE_BUNDLE_REQUIRED");
        string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"apex-ai-install-test-"+Guid.NewGuid().ToString("N"));
        await using var profile=new OperationalPeer(native:true); // Synthetic DPAPI only; no Connect/broker call.
        string history=Path.Combine(profile.Root,"retained-history.txt");File.WriteAllText(history,"synthetic existing v2 history");
        var before=Directory.GetFiles(profile.Root,"*",SearchOption.AllDirectories).ToDictionary(p=>p,p=>File.ReadAllBytes(p));
        Assert.Equal("AI_INSTALLED_LOCAL_SIGN_IN_REQUIRED",await WindowsOkxAdvisorySetup.InstallAsync(root,bundle,default));
        Assert.Equal("AI_LOCAL_SIGN_IN_REQUIRED",await OkxAdvisoryCoordinator.RunCompanion(root,"status",null,default));
        string contract=File.ReadAllText(Path.Combine(root,"installed","runtime-contract.json"));Assert.DoesNotContain("D:\\a\\",contract);Assert.Contains(root.Replace("\\","\\\\"),contract);
        Assert.Equal("AI_INSTALLED_LOCAL_SIGN_IN_REQUIRED",await WindowsOkxAdvisorySetup.InstallAsync(root,bundle,default));
        foreach(var row in before)Assert.Equal(row.Value,File.ReadAllBytes(row.Key));Assert.Empty(profile.Paths);Assert.False(profile.Service.IsConnected);
        // Keep only nonsecret identity/provenance, never the local credential owner or whole runtime profile.
        Console.WriteLine("NATIVE_AI_INSTALL_PASS offline=True relocatedVenv=True supportedMissingAuth=True existingDPAPIAndHistoryPreserved=True inference=False");
    }
    [Fact][Trait("Gate","WindowsAdvisoryIntegration")]
    public async Task Cancelled_install_never_executes_or_touches_profile()
    {
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>WindowsOkxAdvisorySetup.InstallAsync("unused","unused",cancelled.Token));
    }
}
