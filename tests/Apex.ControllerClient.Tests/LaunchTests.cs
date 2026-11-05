using Apex.Desktop.Okx;
using Xunit;
namespace Apex.ControllerClient.Tests;
public sealed class LaunchTests
{
    [Fact] public void Strict_specification_is_present_without_a_native_launch_issuer()
    {
        var type=typeof(OkxControllerSnapshot).Assembly.GetType("Apex.Desktop.Okx.OkxControllerSshLaunchSpec");Assert.NotNull(type);
        var spec=OkxControllerSshLaunchSpec.Build(new("vps.example",22,"apex","/owned/identity","/owned/host keys"),"/usr/bin/ssh","/dev/null");
        Assert.Empty(spec.Environment);Assert.Equal("/usr/bin/ssh",spec.Executable);
        var a=spec.Arguments.ToArray();Assert.Equal(new[]{"-F","/dev/null","-T","-x","-a"},a.Take(5));
        Assert.Contains("StrictHostKeyChecking=yes",a);Assert.Contains("BatchMode=yes",a);Assert.Contains("UserKnownHostsFile=\"/owned/host keys\"",a);Assert.Contains("GlobalKnownHostsFile=/dev/null",a);
        foreach(var option in new[]{"IdentityAgent=none","IdentitiesOnly=yes","ForwardAgent=no","ClearAllForwardings=yes","ProxyCommand=none","ProxyJump=none","PermitLocalCommand=no","ControlPath=none"})Assert.Contains(option,a);
        Assert.Equal(new[]{"--","vps.example",OkxControllerSshLaunchSpec.Relay},a.TakeLast(3));
        Assert.Throws<InvalidOperationException>(()=>spec.CreateStartInfo(null!));
        Assert.Empty(typeof(OkxControllerSshLaunchSpec.AdmittedTransportCapability).GetConstructors());
    }
    [Theory][InlineData("-option")][InlineData("host; touch x")][InlineData("host\nSTART")][InlineData("user@host")]
    public void Endpoint_cannot_inject_arguments(string host)=>Assert.Throws<ArgumentException>(()=>OkxControllerSshLaunchSpec.Build(new(host,22,"apex","/owned/id","/owned/hosts"),"/usr/bin/ssh","/dev/null"));
}
