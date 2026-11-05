using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.Tests;
public sealed class OkxLinuxDemoCompositionTests
{
    [Fact]
    public async Task Native_ordinary_composition_is_passive_not_finite_and_rejects_LIVE_before_open()
    {
        if(!OperatingSystem.IsLinux())return;
        var secrets=new SecretSpy();var approval=new ActivationSpy();var binding=CredentialFixture.Binding();bool safe=true;
        string root=Path.Combine(Environment.GetEnvironmentVariable("TMPDIR")!,"linux-composition-"+Guid.NewGuid().ToString("N"));
        await using var runtime=OkxNavalRuntime.ComposeLinuxDemo(root,binding,secrets,approval,()=>safe);
        await runtime.StartAsync(default);Assert.True(runtime.IsLinuxDemo);Assert.False(runtime.IsFiniteDemo);Assert.Null(runtime.FiniteNomination);Assert.Null(runtime.FiniteFundingPremise);Assert.Equal(0,secrets.OpenCount);Assert.False(Directory.Exists(root));
        safe=false;Assert.False(runtime.FiniteSafe);
        Assert.Throws<InvalidOperationException>(()=>OkxNavalRuntime.ComposeLinuxDemo(root,CredentialFixture.Binding(NavalEnvironment.Live),secrets,approval,()=>true));
        Assert.Throws<InvalidOperationException>(()=>OkxNavalStore.OpenLinuxDemo(root,CredentialFixture.Binding(NavalEnvironment.Live)));
        Assert.Equal(0,secrets.OpenCount);Assert.False(Directory.Exists(root));
    }
    [Fact]
    public void Internal_credential_friend_boundary_is_exact_and_unknown_assemblies_remain_denied()
    {
        var desktop=typeof(OkxNavalRuntime).Assembly;var tool=typeof(Apex.Okx.DemoAcceptance.LinuxDemoCredentialVault).Assembly;
        string[] Friends(Assembly a)=>a.GetCustomAttributes<System.Runtime.CompilerServices.InternalsVisibleToAttribute>().Select(a=>a.AssemblyName).OrderBy(s=>s,StringComparer.Ordinal).ToArray();
        Assert.Equal(new[]{"Apex.Okx.DemoAcceptance","Apex.Tests","Apex.Windows.Tests"},Friends(desktop));
        Assert.Equal(new[]{"Apex.Okx.PrivateSchemaProbe","Apex.Tests"},Friends(tool));
        Assert.False(typeof(IOkxExecutionSecretProvider).IsPublic);Assert.False(typeof(OkxExecutionCredentialLease).IsPublic);Assert.False(typeof(Apex.Okx.DemoAcceptance.EphemeralCredentials).IsPublic);
        Assert.False(typeof(OkxNavalRuntime).GetMethod("ComposeLinuxDemo",BindingFlags.Static|BindingFlags.NonPublic)!.IsPublic);
        Assert.Null(typeof(Apex.Okx.DemoAcceptance.Program).GetMethod("RunForTestsAsync",BindingFlags.Static|BindingFlags.Public));
    }
}
