using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.ControllerTransport.Tests;
public sealed class CausalNativeTests
{
    [Fact] public async Task Retained_old_production_negative_and_new_transport_positive_use_same_native_fixture()
    {
        string path=Environment.GetEnvironmentVariable("APEX_RETAINED_EMPTY_ENV_DESKTOP")??throw new Xunit.Sdk.XunitException("RETAINED_OLD_PRODUCTION_REQUIRED");
        Assert.Equal("3d495d79d849ab5cdc3a0d51eb1e3c04c6313d1c44dc091b7e89f4f9f06356a5",Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());
        await using var f=await NativeSshFixture.Open();
        // Keep the same actual key bytes, pin, port, user, listener and fixed command.
        byte[] identity=SHA256.HashData(File.ReadAllBytes(f.Profile.Identity)),hosts=SHA256.HashData(File.ReadAllBytes(f.Profile.KnownHosts));
        var old=new AssemblyLoadContext("retained-empty-env-"+Guid.NewGuid().ToString("N"),true);
        const BindingFlags flags=BindingFlags.Static|BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
        try
        {
            var assembly=old.LoadFromAssemblyPath(Path.GetFullPath(path));
            var configType=assembly.GetType("Apex.Desktop.Okx.WindowsOkxControllerTransportConfig")!;
            var config=Activator.CreateInstance(configType,flags,null,new object[]{f.Config.Host,f.Config.Port,f.Config.User,f.Config.IdentitySha256,f.Config.HostKey},null)!;
            var type=assembly.GetType("Apex.Desktop.Okx.WindowsOkxControllerTransportAdmission")!;
            using var lease=(IDisposable)type.GetMethod("Open",flags)!.Invoke(null,new[]{f.Root,config})!;
            var spec=type.GetProperty("Spec",flags)!.GetValue(lease)!;
            var capability=type.GetProperty("Capability",flags)!.GetValue(lease)!;
            var start=(ProcessStartInfo)spec.GetType().GetMethod("CreateStartInfo",flags)!.Invoke(spec,new[]{capability})!;
            Assert.Empty(start.Environment); // Genuine retained production, not a rewritten StartInfo.
            using var child=Process.Start(start)!;
            var error=child.StandardError.BaseStream.CopyToAsync(Stream.Null);var output=child.StandardOutput.ReadToEndAsync();
            try
            {
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));await error;
                Assert.Equal(255,child.ExitCode);Assert.True((await output).Length==0);Assert.Empty(f.Events);
            }
            finally{if(!child.HasExited)child.Kill(true);await child.WaitForExitAsync();await error;}
        }
        finally{old.Unload();}
        var transport=await ConnectionTests.Factory(f.Root)(default);
        await using(transport)
        {
            string? hello=await transport.ReadAsync(1024,default);
            Assert.NotNull(hello);Assert.Equal(OkxControllerProtocol.Version,OkxControllerProtocol.DecodeHello(hello!).Schema);
            Assert.Equal(new[]{"AUTHENTICATED_FIXED_RELAY"},f.Events);
        }
        Assert.True(identity.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(f.Profile.Identity))));
        Assert.True(hosts.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(f.Profile.KnownHosts))));
        Assert.DoesNotContain("STOP",f.Events);Assert.DoesNotContain("DISCONNECT",f.Events);
    }
}
