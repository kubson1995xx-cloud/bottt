using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.ControllerTransport.Tests;
public sealed class NativeEnvironmentTests
{
    [Fact] public async Task Admitted_unmodified_spec_authenticates_actual_system_ssh_and_fixed_hello()
    {
        await using var f=await NativeSshFixture.Open();
        using var admission=WindowsOkxControllerTransportAdmission.Open(f.Root,f.Config);
        using var child=Process.Start(admission.Spec.CreateStartInfo(admission.Capability))!;
        var stderr=child.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try
        {
            string? line=await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            if(line==null){await child.WaitForExitAsync();throw new Xunit.Sdk.XunitException("UNMODIFIED_ADMITTED_SSH_EOF exit="+child.ExitCode+" peer="+f.Events.Length);}
            Assert.Equal(OkxControllerProtocol.Version,OkxControllerProtocol.DecodeHello(line).Schema);
            Assert.Contains("AUTHENTICATED_FIXED_RELAY",f.Events);
        }
        finally{child.StandardInput.Close();if(!child.HasExited)child.Kill(true);await child.WaitForExitAsync();await stderr;}
    }
    [Fact] public void Immutable_native_spec_has_exact_os_authored_environment_despite_poisoned_caller()
    {
        using var f=new Apex.ControllerAdmission.Tests.NativeAdmissionTests.Profile();
        var windows=new StringBuilder(32768);Assert.InRange(GetSystemWindowsDirectoryW(windows,(uint)windows.Capacity),1u,32767u);
        var folder=new Guid("62AB5D82-FDC1-4DC3-A9DD-070D1D495D97");Assert.Equal(0,SHGetKnownFolderPath(ref folder,0,IntPtr.Zero,out var pointer));
        string common;try{common=Marshal.PtrToStringUni(pointer)!;}finally{Marshal.FreeCoTaskMem(pointer);}
        string[] names={"SystemRoot","PROGRAMDATA","WINDIR","PATH","SSH_AUTH_SOCK","SSH_ASKPASS","HTTP_PROXY","ALL_PROXY","GIT_SSH_COMMAND","LD_PRELOAD","DOTNET_STARTUP_HOOKS"};
        var previous=names.ToDictionary(n=>n,Environment.GetEnvironmentVariable);
        try
        {
            foreach(var n in names)Environment.SetEnvironmentVariable(n,"POISON_DO_NOT_INHERIT");
            using var lease=WindowsOkxControllerTransportAdmission.Open(f.Root,f.Config);
            Assert.Equal(new[]{"PROGRAMDATA","SystemRoot"},lease.Spec.Environment.Keys.OrderBy(x=>x,StringComparer.Ordinal));
            Assert.Equal(windows.ToString(),lease.Spec.Environment["SystemRoot"]);Assert.Equal(common,lease.Spec.Environment["PROGRAMDATA"]);
            var info=lease.Spec.CreateStartInfo(lease.Capability);Assert.Equal(2,info.Environment.Count);
            Assert.Equal(windows.ToString(),info.Environment["SystemRoot"]);Assert.Equal(common,info.Environment["PROGRAMDATA"]);
            Assert.Throws<NotSupportedException>(()=>((IDictionary<string,string>)lease.Spec.Environment).Add("PATH","bad"));
            info.Environment.Clear();Assert.Equal(2,lease.Spec.CreateStartInfo(lease.Capability).Environment.Count);
            var clone=OkxControllerSshLaunchSpec.Build(new(f.Config.Host,f.Config.Port,f.Config.User,f.Identity,f.KnownHosts),lease.Spec.Executable,"NUL");
            Assert.Empty(clone.Environment);Assert.Throws<InvalidOperationException>(()=>clone.CreateStartInfo(lease.Capability));
            lease.Dispose();Assert.Throws<InvalidOperationException>(()=>lease.Spec.CreateStartInfo(lease.Capability));
        }
        finally{foreach(var n in names)Environment.SetEnvironmentVariable(n,previous[n]);}
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern uint GetSystemWindowsDirectoryW(StringBuilder b,uint n);
    [DllImport("shell32.dll")]private static extern int SHGetKnownFolderPath(ref Guid folder,uint flags,IntPtr token,out IntPtr path);
}
