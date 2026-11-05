using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Apex.Okx.DemoAcceptance;

internal static partial class Program
{
    private static readonly string Base = Path.GetFullPath(Environment.GetEnvironmentVariable("VAULT_TEST_ROOT") ?? throw new Exception("TEST_ROOT_REQUIRED"));
    private static int passed, failed;
    private static string Root() => Path.Combine(Base, Guid.NewGuid().ToString("N"));
    private static string Record(string root) => Path.Combine(root, LinuxDemoCredentialVault.RecordName);
    // Independent deterministic synthetic operands. Never real API material.
    private static byte[] Field(int seed, int length = 4096) => Enumerable.Range(0, length).Select(i => (byte)(33 + (i * 31 + seed * 17 + i / 19) % 94)).ToArray();
    private static void Check(bool ok, string code) { if (!ok) throw new TestFailure(code); }
    private sealed class TestFailure(string code) : Exception(code);
    private static async Task Test(string name, Func<Task> run)
    {
        try { await run(); Console.WriteLine("PASS " + name); passed++; }
        catch (TestFailure e) { Console.WriteLine("FAIL " + name + " assertion=" + e.Message); failed++; }
        catch (DemoVaultException e) { Console.WriteLine("FAIL " + name + " safe_code=" + e.Code); failed++; }
        catch { Console.WriteLine("FAIL " + name + " unexpected_exception_suppressed"); failed++; }
    }
    private static async Task Refuse(Func<Task> run, params DemoVaultError[] allowed)
    {
        try { await run(); } catch (DemoVaultException e) { Check(allowed.Contains(e.Code), "WRONG_REFUSAL_CODE"); Check(e.InnerException == null, "INNER_EXCEPTION_LEAK"); return; }
        throw new TestFailure("EXPECTED_REFUSAL");
    }
    private static Task Save(LinuxDemoCredentialVault vault, int seed = 1, CancellationToken ct = default) => vault.SaveNewAsync(Field(seed), Field(seed+1), Field(seed+2), ct);
    private static void Verify(DemoCredentialBundle b, int seed=1)
    {
        Check(b.Provenance == "DemoTradingApi", "DEMO_PROVENANCE");
        Check(b.ApiKey.Span.SequenceEqual(Field(seed)) && b.SecretKey.Span.SequenceEqual(Field(seed+1)) && b.Passphrase.Span.SequenceEqual(Field(seed+2)), "ROUNDTRIP_BYTES");
    }
    private static void Mk(string root) => Directory.CreateDirectory(root, UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
    private static void Put(string root, byte[] cipher)
    {
        Mk(root); using var s=new FileStream(Record(root),new FileStreamOptions {Mode=FileMode.CreateNew,Access=FileAccess.Write,UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite}); s.Write(cipher);
    }
    private static byte[] Envelope(int seed=1)
    {
        byte[] b=new byte[32+4096*3]; "APXOKXD1"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8),1); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12),1); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16),1);
        for(int i=0;i<3;i++) { BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20+4*i),4096);Field(seed+i).CopyTo(b,32+4096*i); } return b;
    }
    // Independent fixture encoder: real native process, anonymous pipes only.
    private static async Task<byte[]> EncryptFixture(byte[] plain, string name="apex-okx-demo-credentials-v1", bool user=true)
    {
        var p=new ProcessStartInfo("/usr/bin/systemd-creds") {UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        p.Environment.Clear();p.Environment["HOME"]="/home/ubuntu";p.Environment["PATH"]="/usr/bin:/bin";p.Environment["LANG"]="C.UTF-8";
        foreach(var a in (user?new[]{"--user"}:Array.Empty<string>()).Concat(new[]{"--with-key=host","--name="+name,"--no-ask-password","encrypt","-","-"}))p.ArgumentList.Add(a);
        using var process=Process.Start(p)!;using var output=new MemoryStream();
        var stdout=process.StandardOutput.BaseStream.CopyToAsync(output);var stderr=process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        await process.StandardInput.BaseStream.WriteAsync(plain);process.StandardInput.Close();await Task.WhenAll(stdout,stderr,process.WaitForExitAsync());
        Check(process.ExitCode==0,"NATIVE_FIXTURE_ENCRYPT");return output.ToArray();
    }
    private static async Task Child(string command,string root)
    {
        var p=new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")??"/home/ubuntu/.dotnet/dotnet") {UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,RedirectStandardInput=true};
        p.Environment.Clear();p.Environment["HOME"]="/home/ubuntu";p.Environment["PATH"]="/usr/bin:/bin";p.Environment["LANG"]="C.UTF-8";p.Environment["DOTNET_EnableDiagnostics"]="0";p.Environment["COMPlus_EnableDiagnostics"]="0";p.Environment["VAULT_TEST_ROOT"]=Base;
        p.ArgumentList.Add(typeof(Program).Assembly.Location);p.ArgumentList.Add(command);p.ArgumentList.Add(root);
        using var process=Process.Start(p)!;process.StandardInput.Close();var o=process.StandardOutput.ReadToEndAsync();var e=process.StandardError.ReadToEndAsync();await process.WaitForExitAsync();
        Check(process.ExitCode==0&&await o=="CHILD_OK\n"&&await e=="","FRESH_PROCESS_NO_INPUT_OR_OUTPUT_LEAK");
    }
    public static async Task<int> Main(string[] args)
    {
        Mk(Base);
        if(args.Length==2 && args[0]=="child-load") { using var b=await new LinuxDemoCredentialVault(args[1]).LoadAsync();Verify(b);Console.WriteLine("CHILD_OK");return 0; }
        if(args.Length==2 && args[0]=="child-save") {try{await Save(new(args[1]));Console.WriteLine("CHILD_SAVED");}catch(DemoVaultException e) when(e.Code==DemoVaultError.AlreadyStored){Console.WriteLine("CHILD_EXISTS");}return 0;}
        await Test("missing_status_does_not_create_root",async()=>{string r=Root();var v=new LinuxDemoCredentialVault(r);Check((await v.StatusAsync()).State==DemoVaultState.NotStored,"EXPECTED_NOT_STORED");Check(!Directory.Exists(r),"STATUS_CREATED_ROOT");});
        await Test("real_maximum_roundtrip_repeated_load_private_modes",async()=>{string r=Root();var v=new LinuxDemoCredentialVault(r);await Save(v);Check(File.GetUnixFileMode(r)==(UnixFileMode)448&&File.GetUnixFileMode(Record(r))==(UnixFileMode)384,"PRIVATE_MODES");for(int i=0;i<3;i++){using var b=await v.LoadAsync();Verify(b);}Check((await v.StatusAsync()).State==DemoVaultState.Stored,"STORED_STATUS");});
        await Test("fresh_process_reuses_without_tty_or_secret_input",async()=>{string r=Root();await Save(new(r));await Child("child-load",r);await Child("child-load",r);});
        await Test("ciphertext_contains_no_plaintext_and_only_one_record",async()=>{string r=Root();await Save(new(r));byte[] disk=File.ReadAllBytes(Record(r));for(int i=1;i<4;i++)Check(disk.AsSpan().IndexOf(Field(i).AsSpan(0,32))<0,"PLAINTEXT_ON_DISK");Check(Directory.GetFileSystemEntries(r).Length==1,"UNEXPECTED_PERSISTENT_FILE");});
        await Test("new_save_no_clobber_and_explicit_replace",async()=>{string r=Root();var v=new LinuxDemoCredentialVault(r);await Save(v);var old=File.ReadAllBytes(Record(r));await Refuse(()=>Save(v,10),DemoVaultError.AlreadyStored);Check(File.ReadAllBytes(Record(r)).SequenceEqual(old),"CLOBBERED");await v.ReplaceAsync(Field(10),Field(11),Field(12));using var b=await v.LoadAsync();Verify(b,10);});
        await Test("corrupt_existing_preserved_by_save_and_status",async()=>{string r=Root();byte[] corrupt={1,3,5,7};Put(r,corrupt);var v=new LinuxDemoCredentialVault(r);await Refuse(()=>Save(v),DemoVaultError.AlreadyStored);Check((await v.StatusAsync()).State==DemoVaultState.Unavailable,"CORRUPT_STATUS");await Refuse(async()=>{using var b=await v.LoadAsync();},DemoVaultError.NativeUnavailable,DemoVaultError.InvalidEnvelope);Check(File.ReadAllBytes(Record(r)).SequenceEqual(corrupt),"CORRUPT_CHANGED");});
        await Test("cancelled_replace_preserves_old",async()=>{string r=Root();var v=new LinuxDemoCredentialVault(r);await Save(v);byte[] old=File.ReadAllBytes(Record(r));using var c=new CancellationTokenSource();c.Cancel();await Refuse(()=>v.ReplaceAsync(Field(8),Field(9),Field(10),c.Token),DemoVaultError.Cancelled);Check(File.ReadAllBytes(Record(r)).SequenceEqual(old),"CANCEL_CHANGED_OLD");});
        await Test("forget_removes_record_only_and_is_idempotent",async()=>{string r=Root();var v=new LinuxDemoCredentialVault(r);await Save(v);File.WriteAllText(Path.Combine(r,"financial-state.fixture"),"preserve");Check(await v.ForgetAsync(),"FORGET_RETURN");Check(!await v.ForgetAsync(),"FORGET_IDEMPOTENT");Check(File.ReadAllText(Path.Combine(r,"financial-state.fixture"))=="preserve","FINANCIAL_STATE_CHANGED");Check((await v.StatusAsync()).State==DemoVaultState.NotStored,"FORGOT_STATUS");});
        await Test("dispose_clears_owned_fields_and_denies_reuse",async()=>{var v=new LinuxDemoCredentialVault(Root());await Save(v);var b=await v.LoadAsync();var k=b.ApiKey;var s=b.SecretKey;var p=b.Passphrase;b.Dispose();Check(k.Span.IndexOfAnyExcept((byte)0)<0&&s.Span.IndexOfAnyExcept((byte)0)<0&&p.Span.IndexOfAnyExcept((byte)0)<0,"OWNED_BYTES_NOT_ZERO");try{_=b.ApiKey;throw new TestFailure("DISPOSE_READ");}catch(ObjectDisposedException){}b.Dispose();});
        await Test("ownership_transfer_no_string_copies",async()=>{var v=new LinuxDemoCredentialVault(Root());await Save(v);using var b=await v.LoadAsync();var key=b.ApiKey;var arrays=b.TransferTo((k,s,p)=>new Owned(k,s,p));b.Dispose();Check(key.Span.SequenceEqual(Field(1)),"TRANSFER_PREMATURE_ZERO");arrays.Dispose();Check(key.Span.IndexOfAnyExcept((byte)0)<0,"TRANSFER_OWNER_CLEANUP");});
        foreach(int n in new[]{0,3,4097}) await Test("field_length_refused_"+n,async()=>{string r=Root();var v=new LinuxDemoCredentialVault(r);await Refuse(()=>v.SaveNewAsync(Field(1,n),Field(2),Field(3)),DemoVaultError.InvalidCredential);Check(!Directory.Exists(r),"INVALID_CREATED_ROOT");});
        foreach(byte bad in new byte[]{0,10,32,127,255}) await Test("field_byte_refused_"+bad,async()=>{byte[] f=Field(1);f[111]=bad;await Refuse(()=>new LinuxDemoCredentialVault(Root()).SaveNewAsync(f,Field(2),Field(3)),DemoVaultError.InvalidCredential);});
        await Test("tampered_native_ciphertext_fails_closed",async()=>{string r=Root();var v=new LinuxDemoCredentialVault(r);await Save(v);byte[] b=File.ReadAllBytes(Record(r));b[b.Length/2]=(byte)(b[b.Length/2]=='A'?'B':'A');File.WriteAllBytes(Record(r),b);await Refuse(async()=>{using var x=await v.LoadAsync();},DemoVaultError.NativeUnavailable);Check((await v.StatusAsync()).State==DemoVaultState.Unavailable,"TAMPER_STATUS");});
        await Test("wrong_native_name_refused",async()=>{string r=Root();Put(r,await EncryptFixture(Envelope(),"unrelated-credential"));await Refuse(async()=>{using var x=await new LinuxDemoCredentialVault(r).LoadAsync();},DemoVaultError.NativeUnavailable);});
        foreach(string kind in new[]{"magic","schema","live","purpose","zero","oversize","partial","trailing","badbyte"}) await Test("authenticated_invalid_envelope_"+kind,async()=>{byte[] b=Envelope();switch(kind){case "magic":b[0]=0;break;case "schema":b[8]=2;break;case "live":b[12]=2;break;case "purpose":b[16]=2;break;case "zero":b[20]=0;b[21]=0;break;case "oversize":b[20]=1;b[21]=17;break;case "partial":b=b[..^1];break;case "trailing":b=b.Concat(new byte[]{0}).ToArray();break;case "badbyte":b[32]=0;break;}string r=Root();Put(r,await EncryptFixture(b));await Refuse(async()=>{using var x=await new LinuxDemoCredentialVault(r).LoadAsync();},DemoVaultError.InvalidEnvelope);});
        foreach(string kind in new[]{"symlink","hardlink","fifo","mode","oversize","directory","symlink_to_foreign_file"}) await Test("record_path_refused_"+kind,async()=>{string r=Root();Mk(r);string path=Record(r);switch(kind){case "symlink":File.CreateSymbolicLink(path,"missing");break;case "hardlink":File.WriteAllText(path,"unknown");Check(link(path,Path.Combine(r,"other"))==0,"FIXTURE_LINK");break;case "fifo":Check(mkfifo(path,384)==0,"FIXTURE_FIFO");break;case "mode":PutFile(path,new byte[]{1});File.SetUnixFileMode(path,(UnixFileMode)420);break;case "oversize":PutFile(path,new byte[32769]);break;case "directory":Mk(path);break;case "symlink_to_foreign_file":File.CreateSymbolicLink(path,"/etc/passwd");break;}var v=new LinuxDemoCredentialVault(r);await Refuse(async()=>{using var b=await v.LoadAsync();},DemoVaultError.PathRefused);await Refuse(()=>v.ForgetAsync(),DemoVaultError.PathRefused);Check(File.Exists(path)||Directory.Exists(path),"UNKNOWN_REMOVED");});
        foreach(string kind in new[]{"relative","dotdot","double","symlink","mode"}) await Test("root_path_refused_"+kind,async()=>{string r=Root();switch(kind){case "relative":r="relative";break;case "dotdot":r=Base+"/x/../y";break;case "double":r=Base+"//double";break;case "symlink":File.CreateSymbolicLink(r,Base);break;case "mode":Mk(r);File.SetUnixFileMode(r,(UnixFileMode)493);break;}var v=new LinuxDemoCredentialVault(r);await Refuse(()=>Save(v),DemoVaultError.PathRefused);});
        await Test("cross_instance_no_clobber_race",async()=>{string r=Root();var a=new LinuxDemoCredentialVault(r);var b=new LinuxDemoCredentialVault(r);var outcomes=await Task.WhenAll(Attempt(a),Attempt(b));Check(outcomes.Count(x=>x)==1,"EXACT_ONE_WINNER");using var loaded=await a.LoadAsync();Verify(loaded);async Task<bool> Attempt(LinuxDemoCredentialVault v){try{await Save(v);return true;}catch(DemoVaultException e) when(e.Code==DemoVaultError.AlreadyStored){return false;}}});
        await AdditionalTests();
        Console.WriteLine($"RESULT passed={passed} failed={failed} total={passed+failed}");return failed==0?0:1;
    }
    private sealed class Owned(byte[] k,byte[] s,byte[] p) : IDisposable { public void Dispose(){CryptographicOperations.ZeroMemory(k);CryptographicOperations.ZeroMemory(s);CryptographicOperations.ZeroMemory(p);} }
    private static void PutFile(string p,byte[] b){using var s=new FileStream(p,new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite});s.Write(b);}
    [DllImport("libc",SetLastError=true)]private static extern int link(string oldpath,string newpath);
    [DllImport("libc",SetLastError=true)]private static extern int mkfifo(string path,uint mode);
}
