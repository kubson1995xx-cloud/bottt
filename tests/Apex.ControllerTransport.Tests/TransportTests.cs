using Apex.Desktop.Okx;
using Apex.Desktop.Services;
using Apex.ControllerAdmission.Tests;
using Xunit;
namespace Apex.ControllerTransport.Tests;
public sealed class TransportTests
{
    [Fact] public void Native_admitted_profile_has_real_process_and_fixed_store_composition()
    {
        using var f=new NativeAdmissionTests.Profile();
        using var lease=WindowsOkxControllerTransportAdmission.Open(f.Root,f.Config);
        Assert.NotNull(lease.Spec.CreateStartInfo(lease.Capability));
        var assembly=typeof(OkxControllerSnapshot).Assembly;
        Assert.NotNull(assembly.GetType("Apex.Desktop.Okx.WindowsOkxControllerSshTransport"));
        Assert.NotNull(assembly.GetType("Apex.Desktop.Okx.WindowsOkxControllerConnectionStore"));
    }
}
