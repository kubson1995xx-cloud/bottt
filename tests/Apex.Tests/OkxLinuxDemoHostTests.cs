using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Okx.DemoAcceptance;
using Xunit;
namespace Apex.Tests;

public sealed class OkxLinuxDemoHostTests
{
    internal static MethodInfo Entry()=>typeof(Apex.Okx.DemoAcceptance.Program).Assembly.GetType("Apex.Okx.DemoAcceptance.LinuxDemoHost")?.GetMethod("RunForTestsAsync",BindingFlags.Static|BindingFlags.NonPublic)
        ??throw new InvalidOperationException("ISOLATED_ORDINARY_HOST_ENTRY_REQUIRED_NO_MAIN_FALLBACK");
    [Fact]
    public async Task Ordinary_Linux_host_native_encrypted_process_runs_cycle_then_reopens_STOP_without_duplicate()
    {
        if(!OperatingSystem.IsLinux())return;
        string root=Path.Combine("/home/ubuntu/apex-deliveries","linux-host-native-"+Guid.NewGuid().ToString("N"));
        using(var child=HostChild.Start(root,"cycle"))
        {
            await child.Expect("STOP_DISCONNECTED_UNARMED");
            await child.Command("CONNECT","BASELINE_APPROVAL_REQUIRED");
            await child.Command("START","START_REFUSED");
            await child.Command("BASELINE","CONNECTED_STOP");
            await child.Command("START","START_REFUSED");
            await child.Command("ARM","ARMED_STOP");
            await child.Command("START","SCHEDULER_STARTED");
            for(int n=0;n<150;n++)
            {
                await child.Send("STATUS");string line=await child.Expect("STATUS mode=");
                if(line.Contains("mode=RUNNING")&&line.Contains("flat=True")&&line.Contains("fills=2"))break;
                Assert.True(n<149,"Scheduled ordinary cycle did not settle/continue; "+child.Log);await Task.Delay(200);
            }
            await child.Command("STOP","STOP_DRAINED_UNARMED");await child.Command("QUIT","HOST_DRAINED_CREDENTIALS_CLOSED");await child.Exit(0);
            Assert.Contains("NATIVE_ASSERTIONS_PASS posts=3",child.Log);
        }
        using(var child=HostChild.Start(root,"reopen"))
        {
            await child.Expect("STOP_DISCONNECTED_UNARMED");await child.Command("CONNECT","CONNECTED_STOP");
            await child.Send("STATUS");var status=await child.Expect("STATUS mode=");Assert.Contains("mode=STOP",status);Assert.Contains("armed=False",status);
            await child.Command("QUIT","HOST_DRAINED_CREDENTIALS_CLOSED");await child.Exit(0);Assert.Contains("NATIVE_ASSERTIONS_PASS posts=0",child.Log);
        }
        Directory.Delete(root,true);
    }
    [Theory]
    [InlineData("eof")]
    [InlineData("signal")]
    [InlineData("stop")]
    [InlineData("disconnect")]
    [InlineData("emergency")]
    public async Task Ordinary_Linux_host_revokes_blocked_dispatch_before_drain_and_lock_release(string veto)
    {
        if(!OperatingSystem.IsLinux())return;
        string root="/home/ubuntu/apex-deliveries/linux-host-native-"+Guid.NewGuid().ToString("N");
        using(var child=HostChild.Start(root,"blocked"))
        {
            await child.Expect("STOP_DISCONNECTED_UNARMED");await child.Command("CONNECT","BASELINE_APPROVAL_REQUIRED");await child.Command("BASELINE","CONNECTED_STOP");
            await child.Command("ARM","ARMED_STOP");await child.Send("START");await child.Expect("DISPATCH_BLOCKED");
            if(veto=="eof")child.Process.StandardInput.Close();
            else if(veto=="signal")Assert.Equal(0,Kill(child.Process.Id,15));
            else await child.Send(veto.ToUpperInvariant());
            await child.Expect("DISPATCH_CANCELLED_LOCK_HELD");
            if(veto is not ("eof" or "signal"))
            {await child.Expect(veto=="disconnect"?"STOP_DISCONNECTED_UNARMED":"STOP_DRAINED_UNARMED");await child.Send("QUIT");}
            await child.Expect("HOST_DRAINED_CREDENTIALS_CLOSED");await child.Exit(0);Assert.Contains("NATIVE_ASSERTIONS_PASS posts=1",child.Log);
        }
        Directory.Delete(root,true);
    }
    [Theory]
    [InlineData("six-balances","ORDINARY_DEDICATED_SINGLE_QUOTE_REQUIRED rows=6 nullable=True")]
    [InlineData("environment","DEMO_PROVENANCE_IDENTITY_OR_FRESHNESS_REFUSED")]
    [InlineData("provenance","DEMO_PROVENANCE_IDENTITY_OR_FRESHNESS_REFUSED")]
    [InlineData("fingerprint","DEMO_PROVENANCE_IDENTITY_OR_FRESHNESS_REFUSED")]
    [InlineData("identity","ORDINARY_ACCOUNT_ADMISSION_REFUSED_PRESERVED")]
    [InlineData("lock","RUN_LOCK_BUSY_NO_CHANGES")]
    public async Task Ordinary_Linux_host_refuses_incompatible_source_without_new_allocation(string scenario,string marker)
    {
        if(!OperatingSystem.IsLinux())return;
        string root="/home/ubuntu/apex-deliveries/linux-host-native-"+Guid.NewGuid().ToString("N");
        using(var child=HostChild.Start(root,scenario))
        {
            await child.Expect("STOP_DISCONNECTED_UNARMED");await child.Command("CONNECT",marker);await child.Command("QUIT","HOST_DRAINED_CREDENTIALS_CLOSED");await child.Exit(0);
            Assert.Contains("NATIVE_ASSERTIONS_PASS posts=0",child.Log);
        }
        Directory.Delete(root,true);
    }
    [Fact]
    public async Task Existing_finite_owner_current_admission_failure_preserves_original_allocation()
    {
        if(!OperatingSystem.IsLinux())return;
        string root="/home/ubuntu/apex-deliveries/linux-host-native-"+Guid.NewGuid().ToString("N");
        using(var child=HostChild.Start(root,"finite-owner"))
        {
            await child.Expect("STOP_DISCONNECTED_UNARMED");await child.Command("CONNECT","ORDINARY_ACCOUNT_ADMISSION_REFUSED_PRESERVED");
            await child.Command("QUIT","HOST_DRAINED_CREDENTIALS_CLOSED");await child.Exit(0);Assert.Contains("posts=0",child.Log);
        }
        Directory.Delete(root,true);
    }
    [Fact]
    public async Task Ordinary_Linux_host_drain_failure_is_not_success_and_keeps_lock_until_process_exit()
    {
        if(!OperatingSystem.IsLinux())return;
        string root="/home/ubuntu/apex-deliveries/linux-host-native-"+Guid.NewGuid().ToString("N");
        using(var child=HostChild.Start(root,"drain-fault"))
        {
            await child.Expect("STOP_DISCONNECTED_UNARMED");await child.Command("CONNECT","BASELINE_APPROVAL_REQUIRED");await child.Command("BASELINE","CONNECTED_STOP");
            await child.Send("QUIT");await child.Expect("DRAIN_UNPROVEN_LOCK_RETAINED_UNTIL_PROCESS_EXIT");await child.Exit(7);Assert.DoesNotContain("HOST_DRAINED_CREDENTIALS_CLOSED",child.Log);
        }
        Directory.Delete(root,true);
    }
    [System.Runtime.InteropServices.DllImport("libc",EntryPoint="kill")]private static extern int Kill(int pid,int signal);
    [Fact]
    public async Task Production_host_refuses_redirected_command_input_before_credential_access()
    {
        if(!OperatingSystem.IsLinux())return;
        using var child=HostChild.Start("unused","unused",production:true);await child.Exit(2);Assert.Contains("LOCAL_INTERACTIVE_LINUX_TTY_REQUIRED_NO_CREDENTIALS_OPENED",child.Log);
    }
    [Fact]
    public async Task Ordinary_Linux_host_passive_entry_does_not_open_vault_or_construct_transport()
    {
        var entry=Entry();
        string root=Path.Combine(Environment.GetEnvironmentVariable("TMPDIR")!,"linux-host-passive-"+Guid.NewGuid().ToString("N"));
        using var output=new StringWriter();
        Func<EphemeralCredentials,CancellationToken,Task<OkxEnrollmentDiscovery>> discover=(_,_)=>throw new Exception("PASSIVE_DISCOVERY_FORBIDDEN");
        int code=await (Task<int>)entry.Invoke(null,new object?[]{root,new LinuxDemoCredentialVault(Path.Combine(root,"vault")),new StringReader("STATUS\nQUIT\n"),output,discover,null,CancellationToken.None})!;
        Assert.Equal(0,code);Assert.Contains("STOP_DISCONNECTED_UNARMED",output.ToString());Assert.False(Directory.Exists(Path.Combine(root,"vault")));
        if(Directory.Exists(root))Directory.Delete(root,true);
    }
}
