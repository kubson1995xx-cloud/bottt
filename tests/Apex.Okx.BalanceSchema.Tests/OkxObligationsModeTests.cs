using Apex.Okx.DemoAcceptance;
using Xunit;
namespace Apex.Tests;
public sealed class OkxObligationsModeTests
{
    public OkxObligationsModeTests(){SyntheticObligationEnvironment.Prepare();_=new OkxBalanceSchemaDiagnosticTests();}
    [Fact] public async Task Exact_obligations_flag_is_recognized_without_TTY_or_default_fallback()
    {
        string root="/home/ubuntu/apex-deliveries/apex-okx-private-schema-tests/obligation-mode-"+Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(root,(UnixFileMode)448);
        var vault=new LinuxDemoCredentialVault(Path.Combine(root,"vault"));
        int discovery=0;
        int result=await Program.RunAsync(new[]{"--check-obligations"},root,vault,(_,_)=>{discovery++;throw new Exception("NO_DISCOVERY_WITHOUT_STORED_KEY");});
        Assert.NotEqual(2,result); // retained old executable returns ARGUMENTS_REFUSED
        Assert.Equal(8,result);Assert.Equal(0,discovery);Assert.False(File.Exists(Path.Combine(root,"last-report.json")));
    }
}
