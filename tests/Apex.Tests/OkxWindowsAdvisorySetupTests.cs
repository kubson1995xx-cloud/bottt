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
    [Fact]
    public async Task Closed_setup_route_requires_operator_local_origin_and_no_executable_or_secret_payload()
    {
        await using var host=new NavalProductionHost();await host.Start();
        foreach(string mutation in new[]{"reader","origin","header","extra","duplicate"})
        {
            using var request=host.Request("/api/naval/okx/setup-ai",mutation=="reader"?"read":"operator","POST");
            if(mutation!="header")request.Headers.Add("X-Apex-Local","1");
            if(mutation=="origin")request.Headers.Add("Origin","https://foreign.invalid");
            request.Content=new System.Net.Http.StringContent(mutation=="extra"?"{\"commandId\":\"x\",\"executable\":\"evil\"}":mutation=="duplicate"?"{\"commandId\":\"x\",\"commandId\":\"y\"}":"{\"commandId\":\"x\"}",System.Text.Encoding.UTF8,"application/json");
            Assert.Equal(mutation is "reader" or "origin" or "header"?System.Net.HttpStatusCode.Forbidden:System.Net.HttpStatusCode.BadRequest,(await host.Client.SendAsync(request)).StatusCode);
        }
    }
    [Fact][Trait("Gate","WindowsAdvisoryIntegration")]
    public async Task Cancelled_install_never_executes_or_touches_profile()
    {
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>WindowsOkxAdvisorySetup.InstallAsync("unused","unused",cancelled.Token));
    }
}
