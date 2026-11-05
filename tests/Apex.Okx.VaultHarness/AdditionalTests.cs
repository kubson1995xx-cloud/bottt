using System.Diagnostics;
using System.Security.Cryptography;
using Apex.Okx.DemoAcceptance;

internal static partial class Program
{
    private sealed class TrackingAllocator : IBufferAllocator
    {
        internal readonly List<byte[]> All=new();
        internal readonly HashSet<byte[]> Released=new();
        internal int FailAt;
        private int calls;
        public byte[] Allocate(int capacity)
        {
            if(++calls==FailAt)throw new OutOfMemoryException("SYNTHETIC_ALLOCATION_FAULT");
            byte[] b=new byte[capacity];b.AsSpan().Fill(0xa5);All.Add(b);return b;
        }
        public void Release(byte[] buffer)
        {
            Check(buffer.AsSpan().IndexOfAnyExcept((byte)0)<0,"RELEASE_BEFORE_FULL_ZERO");
            Check(Released.Add(buffer),"DOUBLE_RELEASE");
        }
        internal void Clean() {Check(All.Count==Released.Count,"OWNED_ALLOCATION_RETAINED");foreach(var b in All)Check(b.AsSpan().IndexOfAnyExcept((byte)0)<0,"CAPACITY_NOT_ZERO");}
    }
    private sealed class AuditLauncher : ICredentialProcessLauncher
    {
        internal int Calls;
        public Process Start(ProcessStartInfo info)
        {
            Check(info.FileName=="/usr/bin/systemd-creds"&&!info.UseShellExecute&&info.RedirectStandardInput&&info.RedirectStandardOutput&&info.RedirectStandardError,"NATIVE_PROCESS_FLAGS");
            bool encrypt=info.ArgumentList.Contains("encrypt");
            string[] expected=encrypt?new[]{"--user","--with-key=host","--name=apex-okx-demo-credentials-v1","--no-ask-password","encrypt","-","-"}:new[]{"--user","--name=apex-okx-demo-credentials-v1","--refuse-null","--no-ask-password","decrypt","-","-"};
            Check(info.ArgumentList.SequenceEqual(expected),"NATIVE_EXACT_ARGS");
            Check(info.Environment.Count==3&&info.Environment["HOME"]=="/home/ubuntu"&&info.Environment["PATH"]=="/usr/bin:/bin"&&info.Environment["LANG"]=="C.UTF-8","MINIMAL_ENV");
            var p=Process.Start(info)!;
            try
            {
                byte[] argv=File.ReadAllBytes($"/proc/{p.Id}/cmdline"),env=File.ReadAllBytes($"/proc/{p.Id}/environ");
                for(int seed=1;seed<15;seed++){Check(argv.AsSpan().IndexOf(Field(seed).AsSpan(0,32))<0,"SECRET_ARGV");Check(env.AsSpan().IndexOf(Field(seed).AsSpan(0,32))<0,"SECRET_ENV");}
                Calls++;return p;
            }
            catch {p.Kill(true);p.Dispose();throw;}
        }
    }
    // Fault injection is confined to this friend executable. No production option
    // can select another executable or alternate crypto implementation.
    private sealed class FixtureLauncher(string code) : ICredentialProcessLauncher
    {
        internal int? Pid;
        public Process Start(ProcessStartInfo info)
        {
            info.FileName="/usr/bin/python3";info.ArgumentList.Clear();info.ArgumentList.Add("-c");info.ArgumentList.Add(code);
            var p=Process.Start(info)!;Pid=p.Id;return p;
        }
    }
    private sealed class UnavailableLauncher : ICredentialProcessLauncher
    { public Process Start(ProcessStartInfo info)=>throw new IOException("SYNTHETIC_SECRET_BEARING_NATIVE_ERROR_MUST_NOT_ESCAPE"); }
    private sealed class VerificationFailureLauncher : ICredentialProcessLauncher
    {
        private int calls;
        public Process Start(ProcessStartInfo info) => ++calls==2?throw new IOException("SYNTHETIC_VERIFICATION_SERVICE_FAILURE"):Process.Start(info)!;
    }
    private sealed class MismatchingCryptor(IBufferAllocator allocator) : ICredentialCryptor
    {
        private readonly SystemdCredentialCryptor real=new(allocator);
        public async Task<SensitiveBuffer> TransformAsync(bool encrypt,ReadOnlyMemory<byte> input,CancellationToken ct)
        {var b=await real.TransformAsync(encrypt,input,ct);if(!encrypt)b.Array[32]=(byte)(b.Array[32]=='A'?'B':'A');return b;}
    }
    private sealed class ClosedInputLauncher : ICredentialProcessLauncher
    { public Process Start(ProcessStartInfo info){var p=Process.Start(info)!;p.StandardInput.Close();return p;} }
    private static async Task AdditionalTests()
    {
        await Test("actual_native_args_env_and_tracking_default_dll_parity",async()=>
        {
            string r=Root();var alloc=new TrackingAllocator();var audit=new AuditLauncher();var tracked=new LinuxDemoCredentialVault(r,alloc,new SystemdCredentialCryptor(alloc,audit));
            await Save(tracked);alloc.Clean();using(var b=await new LinuxDemoCredentialVault(r).LoadAsync())Verify(b);
            using(var b=await tracked.LoadAsync())Verify(b);alloc.Clean();Check(audit.Calls==3,"NATIVE_AUDIT_CALLS");
        });
        await Test("minimum_field_length_roundtrip_and_unused_capacity_cleanup",async()=>
        {
            var a=new TrackingAllocator();var v=new LinuxDemoCredentialVault(Root(),a);await v.SaveNewAsync(Field(1,4),Field(2,4),Field(3,4));using(var b=await v.LoadAsync()){Check(b.ApiKey.Span.SequenceEqual(Field(1,4))&&b.SecretKey.Length==4&&b.Passphrase.Length==4,"MINIMUM_PARITY");}a.Clean();
        });
        await Test("load_field_allocation_failures_clear_every_owned_capacity",async()=>
        {
            string r=Root();await Save(new(r));
            // 1 ciphertext, 2 plaintext output, 3 stderr scratch, then 3 returned fields.
            for(int fail=1;fail<=6;fail++)
            {
                var a=new TrackingAllocator{FailAt=fail};var v=new LinuxDemoCredentialVault(r,a);
                await Refuse(async()=>{using var b=await v.LoadAsync();},DemoVaultError.StorageUnavailable,DemoVaultError.NativeUnavailable);a.Clean();
            }
        });
        await Test("save_allocation_failures_preserve_old_and_clear_every_capacity",async()=>
        {
            string r=Root();await Save(new(r));byte[] old=File.ReadAllBytes(Record(r));
            for(int fail=1;fail<=5;fail++)
            {
                var a=new TrackingAllocator{FailAt=fail};var v=new LinuxDemoCredentialVault(r,a);
                await Refuse(()=>v.ReplaceAsync(Field(10),Field(11),Field(12)),DemoVaultError.StorageUnavailable,DemoVaultError.NativeUnavailable);a.Clean();Check(File.ReadAllBytes(Record(r)).SequenceEqual(old),"ALLOC_FAIL_CHANGED_OLD");
            }
        });
        await Test("native_start_failure_preserves_old_and_sanitizes_cleanup",async()=>
        {
            string r=Root();await Save(new(r));byte[] old=File.ReadAllBytes(Record(r));var a=new TrackingAllocator();var v=new LinuxDemoCredentialVault(r,a,new SystemdCredentialCryptor(a,new UnavailableLauncher()));
            await Refuse(()=>v.ReplaceAsync(Field(10),Field(11),Field(12)),DemoVaultError.NativeUnavailable);a.Clean();Check(File.ReadAllBytes(Record(r)).SequenceEqual(old),"NATIVE_FAIL_CHANGED_OLD");
        });
        foreach(bool mismatch in new[]{false,true}) await Test("failed_preparation_verification_preserves_old_"+mismatch,async()=>
        {
            string r=Root();await Save(new(r));byte[] old=File.ReadAllBytes(Record(r));var a=new TrackingAllocator();ICredentialCryptor cryptor=mismatch?new MismatchingCryptor(a):new SystemdCredentialCryptor(a,new VerificationFailureLauncher());var v=new LinuxDemoCredentialVault(r,a,cryptor);
            await Refuse(()=>v.ReplaceAsync(Field(10),Field(11),Field(12)),DemoVaultError.NativeUnavailable);a.Clean();Check(File.ReadAllBytes(Record(r)).SequenceEqual(old)&&Directory.GetFileSystemEntries(r).Length==1,"VERIFY_FAIL_CHANGED_OLD");
        });
        await Test("native_input_write_failure_cleans_and_preserves_old",async()=>
        {
            string r=Root();await Save(new(r));byte[] old=File.ReadAllBytes(Record(r));var a=new TrackingAllocator();var v=new LinuxDemoCredentialVault(r,a,new SystemdCredentialCryptor(a,new ClosedInputLauncher()));
            await Refuse(()=>v.ReplaceAsync(Field(10),Field(11),Field(12)),DemoVaultError.NativeUnavailable);a.Clean();Check(File.ReadAllBytes(Record(r)).SequenceEqual(old),"WRITE_FAIL_CHANGED_OLD");
        });
        await Test("active_native_cancel_is_bounded_reaped_and_cleared",async()=>
        {
            string r=Root();await Save(new(r));byte[] old=File.ReadAllBytes(Record(r));var a=new TrackingAllocator();var fixture=new FixtureLauncher("import time; time.sleep(60)");var v=new LinuxDemoCredentialVault(r,a,new SystemdCredentialCryptor(a,fixture));
            using var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(250));var timer=Stopwatch.StartNew();
            await Refuse(()=>v.ReplaceAsync(Field(10),Field(11),Field(12),cancel.Token),DemoVaultError.Cancelled);a.Clean();Check(timer.Elapsed<TimeSpan.FromSeconds(5),"CANCEL_NOT_BOUNDED");Check(fixture.Pid!=null&&!Directory.Exists("/proc/"+fixture.Pid),"CHILD_NOT_REAPED");Check(File.ReadAllBytes(Record(r)).SequenceEqual(old),"ACTIVE_CANCEL_CHANGED_OLD");
        });
        await Test("native_timeout_budget_reaps_child_and_cleans_buffers",async()=>
        {
            var a=new TrackingAllocator();var fixture=new FixtureLauncher("import time; time.sleep(60)");var cryptor=new SystemdCredentialCryptor(a,fixture);var timer=Stopwatch.StartNew();
            await Refuse(async()=>{using var b=await cryptor.TransformAsync(true,Envelope(),CancellationToken.None);},DemoVaultError.NativeUnavailable);a.Clean();Check(timer.Elapsed>=TimeSpan.FromSeconds(12)&&timer.Elapsed<TimeSpan.FromSeconds(20),"NATIVE_TIMEOUT_BOUND");Check(fixture.Pid!=null&&!Directory.Exists("/proc/"+fixture.Pid),"TIMEOUT_CHILD_NOT_REAPED");
        });
        await Test("stderr_flood_plaintext_never_deadlocks_or_escapes",async()=>
        {
            var a=new TrackingAllocator();var fixture=new FixtureLauncher("import sys; x=sys.stdin.buffer.read(); sys.stderr.buffer.write(x*32); sys.stderr.flush(); sys.stdout.buffer.write(x); sys.stdout.flush()");var cryptor=new SystemdCredentialCryptor(a,fixture);
            using(var output=await cryptor.TransformAsync(false,Envelope(),CancellationToken.None)){Check(output.Memory.Span.SequenceEqual(Envelope()),"PRIVATE_PIPE_PARITY");}a.Clean();
        });
        await Test("stdout_overflow_clears_entire_guarded_output",async()=>
        {
            var a=new TrackingAllocator();var fixture=new FixtureLauncher("import sys; x=sys.stdin.buffer.read(); sys.stdout.buffer.write(x*4); sys.stdout.flush()");var cryptor=new SystemdCredentialCryptor(a,fixture);
            await Refuse(async()=>{using var b=await cryptor.TransformAsync(false,Envelope(),CancellationToken.None);},DemoVaultError.InvalidEnvelope);a.Clean();
        });
        await Test("transfer_factory_throw_clears_all_owned_fields",async()=>
        {
            string r=Root();await Save(new(r));var a=new TrackingAllocator();using var b=await new LinuxDemoCredentialVault(r,a).LoadAsync();
            try {b.TransferTo<Owned>((k,s,p)=>throw new TestFailure("EXPECTED_FACTORY_FAILURE"));}catch(TestFailure e) when(e.Message=="EXPECTED_FACTORY_FAILURE") { }
            a.Clean();
        });
        await Test("forget_missing_does_not_create_and_unknown_staging_survives",async()=>
        {
            string r=Root();var v=new LinuxDemoCredentialVault(r);Check(!await v.ForgetAsync()&&!Directory.Exists(r),"FORGET_CREATED_ROOT");Mk(r);string staging=Path.Combine(r,".credentials-unexplained.tmp");PutFile(staging,new byte[]{5,7,9});await Save(v);Check(await v.ForgetAsync(),"FORGET");Check(File.ReadAllBytes(staging).SequenceEqual(new byte[]{5,7,9}),"UNEXPLAINED_STAGING_REMOVED");
        });
        await Test("corrupt_existing_explicit_replace_is_deliberate",async()=>
        {
            string r=Root();Put(r,new byte[]{1,2,3});var v=new LinuxDemoCredentialVault(r);await v.ReplaceAsync(Field(1),Field(2),Field(3));using var b=await v.LoadAsync();Verify(b);
        });
        await Test("two_process_create_exactly_one_winner",async()=>
        {
            string r=Root();var outcomes=await Task.WhenAll(SaveChild(r),SaveChild(r));Check(outcomes.Count(x=>x=="CHILD_SAVED\n")==1&&outcomes.Count(x=>x=="CHILD_EXISTS\n")==1,"PROCESS_RACE_WINNERS");using var b=await new LinuxDemoCredentialVault(r).LoadAsync();Verify(b);
        });
        await Test("precommit_cancel_cleans_only_owned_staging_and_preserves_old",async()=>
        {
            string r=Root();await Save(new(r));byte[] old=File.ReadAllBytes(Record(r));string unknown=Path.Combine(r,".credentials-unknown.tmp");PutFile(unknown,new byte[]{4,6,8});
            using var dir=await VaultDirectory.OpenAsync(r,false,CancellationToken.None);var identity=dir!.InspectRecord();using var c=new CancellationTokenSource();c.Cancel();
            bool cancelled=false;try {dir.Publish(old,identity,true,c.Token);}catch(OperationCanceledException){cancelled=true;}
            Check(cancelled,"PRECOMMIT_CANCEL_NOT_PROPAGATED");Check(File.ReadAllBytes(Record(r)).SequenceEqual(old),"PRECOMMIT_CHANGED_OLD");Check(File.ReadAllBytes(unknown).SequenceEqual(new byte[]{4,6,8})&&Directory.GetFileSystemEntries(r).Length==2,"OWNED_STAGING_CLEANUP");
        });
        await Test("lock_wait_cancel_leaves_record_untouched",async()=>
        {
            string r=Root();await Save(new(r));byte[] old=File.ReadAllBytes(Record(r));using var held=await VaultDirectory.OpenAsync(r,false,CancellationToken.None);using var c=new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Refuse(()=>new LinuxDemoCredentialVault(r).ReplaceAsync(Field(10),Field(11),Field(12),c.Token),DemoVaultError.Cancelled);Check(File.ReadAllBytes(Record(r)).SequenceEqual(old),"LOCK_CANCEL_CHANGED_OLD");
        });
    }
    private static async Task<string> SaveChild(string root)
    {
        var p=new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")??"/home/ubuntu/.dotnet/dotnet") {UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        p.Environment.Clear();p.Environment["HOME"]="/home/ubuntu";p.Environment["PATH"]="/usr/bin:/bin";p.Environment["LANG"]="C.UTF-8";p.Environment["DOTNET_EnableDiagnostics"]="0";p.Environment["COMPlus_EnableDiagnostics"]="0";p.Environment["VAULT_TEST_ROOT"]=Base;
        p.ArgumentList.Add(typeof(Program).Assembly.Location);p.ArgumentList.Add("child-save");p.ArgumentList.Add(root);
        using var child=Process.Start(p)!;child.StandardInput.Close();var stdout=child.StandardOutput.ReadToEndAsync();var stderr=child.StandardError.ReadToEndAsync();await child.WaitForExitAsync();Check(child.ExitCode==0&&await stderr=="","CHILD_SAVE_PROCESS");return await stdout;
    }
}
