using System.Diagnostics;
using System.Reflection;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.ControllerTransport.Tests;
public sealed class NativeTransportTests
{
    private static T Property<T>(object t,string name)=>(T)t.GetType().GetProperty(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(t)!;
    private static async Task Reaped(IOkxControllerFrameTransport t)
    {await t.DisposeAsync();await t.DisposeAsync();Assert.True(Property<bool>(t,"ChildReaped"));}
    [Fact] public async Task Existing_native_sshd_accepts_disposable_key_and_exact_fixed_relay_before_transport_assertions()
    {
        await using var f=await NativeSshFixture.Open();
        // Independent OS positive: no product transport API needed to reach auth/ForceCommand.
        using var admission=WindowsOkxControllerTransportAdmission.Open(f.Root,f.Config);
        using var child=Process.Start(admission.Spec.CreateStartInfo(admission.Capability))!;
        var err=child.StandardError.ReadToEndAsync();
        try
        {
            string? hello=await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            if(hello==null){await child.WaitForExitAsync();string diagnostic=await err;throw new Xunit.Sdk.XunitException("CONTROL_EOF exit="+child.ExitCode+" client="+NativeSshFixture.Classify(diagnostic)+" syntheticDiagnostic="+NativeSshFixture.SyntheticError(diagnostic,f.Root)+" server="+f.ServerCodes+" peer="+f.Events.Length);}
            Assert.Equal(OkxControllerProtocol.Version,OkxControllerProtocol.DecodeHello(hello).Schema);
            Assert.Contains("AUTHENTICATED_FIXED_RELAY",f.Events);
        }
        finally{child.StandardInput.Close();if(!child.HasExited)child.Kill(true);await child.WaitForExitAsync();await err;}
    }
    [Fact] public async Task Actual_system_ssh_factory_bootstrap_start_close_and_explicit_reconnect_preserve_remote_running()
    {
        await using var f=await NativeSshFixture.Open();var factory=ConnectionTests.Factory(f.Root);
        var t=await factory(default);await using(var client=new OkxControllerClient(TimeSpan.FromSeconds(15),TimeSpan.FromSeconds(20)))
        {
            await client.ReconnectAsync(t);Assert.Equal("confirmed",client.View.Outcome);
            Assert.Equal(new[]{"AUTHENTICATED_FIXED_RELAY","STATUS","SNAPSHOT"},f.Events);
            var start=await client.CommandAsync("START");Assert.Equal("confirmed",start.Outcome);Assert.Equal("RUNNING",start.Receipt!.Mode);
        }
        await Reaped(t);Assert.Equal("RUNNING",File.ReadAllText(Path.Combine(f.ServerRoot,"state")));Assert.DoesNotContain("STOP",f.Events);Assert.DoesNotContain("DISCONNECT",f.Events);
        var next=await factory(default);await using(var client=new OkxControllerClient(TimeSpan.FromSeconds(15),TimeSpan.FromSeconds(20)))
        {await client.ReconnectAsync(next);Assert.Equal("RUNNING",client.View.Mode);Assert.Equal(1,f.Events.Count(x=>x=="START"));Assert.Equal("confirmed",(await client.CommandAsync("STOP")).Outcome);}
        await Reaped(next);Assert.Equal("STOP",File.ReadAllText(Path.Combine(f.ServerRoot,"state")));
    }
    [Theory][InlineData("lost-start")][InlineData("wrong-correlation")]
    public async Task Lost_or_foreign_receipt_is_unknown_without_mutation_replay_or_implicit_stop(string behavior)
    {
        await using var f=await NativeSshFixture.Open(behavior);var t=await ConnectionTests.Factory(f.Root)(default);
        await using(var client=new OkxControllerClient(TimeSpan.FromSeconds(3),TimeSpan.FromSeconds(10)))
        {
            await client.ReconnectAsync(t);var r=await client.CommandAsync("START");Assert.Equal("pending-or-unknown",r.Outcome);
            Assert.True(client.View.SnapshotStale);Assert.Equal("refused",(await client.CommandAsync("START")).Outcome);
        }
        await Reaped(t);Assert.Equal(1,f.Events.Count(x=>x=="START"));Assert.DoesNotContain("STOP",f.Events);
    }
    [Theory][InlineData("truncated")][InlineData("oversize")]
    public async Task Actual_native_stdout_refuses_truncated_or_over_limit_frames_and_reaps(string behavior)
    {
        await using var f=await NativeSshFixture.Open(behavior);var t=await ConnectionTests.Factory(f.Root)(default);
        await Assert.ThrowsAnyAsync<IOException>(()=>t.ReadAsync(1024,default));await Reaped(t);
    }
    [Theory][InlineData("silent")][InlineData("stderr")]
    public async Task Native_pending_read_cancellation_or_stderr_flood_has_bounded_cleanup(string behavior)
    {
        await using var f=await NativeSshFixture.Open(behavior);var t=await ConnectionTests.Factory(f.Root)(default);
        using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(3));var timer=Stopwatch.StartNew();
        try{await t.ReadAsync(1024,cancel.Token);}catch(Exception e) when(e is OperationCanceledException or IOException){}
        await Reaped(t);Assert.True(timer.Elapsed<TimeSpan.FromSeconds(10));Assert.InRange(Property<long>(t,"StderrBytes"),0,65536);
    }
    [Fact] public async Task Wrong_pinned_real_host_key_denies_authentication_with_no_peer_command()
    {
        await using var f=await NativeSshFixture.Open();var changed=f.Config with{HostKey=Apex.ControllerAdmission.Tests.NativeAdmissionTests.Profile.Pin};
        File.WriteAllText(f.Profile.KnownHosts,$"[127.0.0.1]:{changed.Port} ssh-ed25519 {changed.HostKey}\n");ConnectionTests.Put(f.Root,ConnectionTests.Json(changed));
        var t=await ConnectionTests.Factory(f.Root)(default);
        Assert.Null(await t.ReadAsync(1024,new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token));await Reaped(t);Assert.Empty(f.Events);
    }
}
