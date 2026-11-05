using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Apex.Desktop.Okx;
using Apex.Adapters.Okx.Execution;
namespace Apex.Okx.DemoAcceptance;

internal sealed class LinuxInput : IDisposable
{
    private int tty=-1;
    private byte[]? saved;
    private readonly CancellationToken cancellation;
    private readonly long started=System.Diagnostics.Stopwatch.GetTimestamp();
    internal LinuxInput(CancellationToken ct)
    {
        cancellation=ct;Harden();
        try
        {
            if(new[]{0,1,2}.Any(fd=>isatty(fd)!=1||tcgetpgrp(fd)!=getpgrp()))throw new IOException("TTY_REQUIRED");
            string? name=Marshal.PtrToStringUTF8(ttyname(0));
            if(name==null||Marshal.PtrToStringUTF8(ttyname(1))!=name||Marshal.PtrToStringUTF8(ttyname(2))!=name||Environment.GetEnvironmentVariable("SSH_TTY")!=name||string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_CONNECTION")))throw new IOException("SSH_TTY_REQUIRED");
            tty=open("/dev/tty",2|0x80000|0x100,0);if(tty<0||tcgetpgrp(tty)!=getpgrp())throw new IOException("TTY_REQUIRED");
            saved=new byte[64];if(tcgetattr(tty,saved)!=0)throw new IOException("TTY_REQUIRED");
            byte[] hidden=saved.ToArray();uint flags=BitConverter.ToUInt32(hidden,12);flags&=~(8u|64u|2u|32768u);BitConverter.GetBytes(flags).CopyTo(hidden,12);hidden[17+6]=1;hidden[17+5]=0;
            if(tcsetattr(tty,2,hidden)!=0)throw new IOException("TTY_REQUIRED");
            byte[] observed=new byte[64];if(tcgetattr(tty,observed)!=0||(BitConverter.ToUInt32(observed,12)&(8|64))!=0)throw new IOException("TTY_REQUIRED");
        }
        catch{Dispose();throw;}
    }
    internal byte[] Read(string label)
    {
        byte[] destination=new byte[4096],single=new byte[1];int used=0;
        try
        {
            byte[] prompt=Encoding.ASCII.GetBytes(label);if(write(tty,prompt,(nuint)prompt.Length)!=prompt.Length)throw new IOException("TTY_REFUSED");
            while(true)
            {
                cancellation.ThrowIfCancellationRequested();if(System.Diagnostics.Stopwatch.GetElapsedTime(started)>=TimeSpan.FromMinutes(2))throw new OperationCanceledException();
                var p=new Poll{fd=tty,events=1};int ready=poll(ref p,1,100);if(ready<0){if(Marshal.GetLastPInvokeError()==4)continue;throw new IOException("TTY_REFUSED");}if(ready==0)continue;
                if(read(tty,single,1)!=1)throw new IOException("TTY_REFUSED");byte b=single[0];single[0]=0;
                if(b is 3 or 4)throw new OperationCanceledException();
                if(b is 10 or 13){if(used<4)throw new IOException("CREDENTIAL_INVALID");write(tty,new byte[]{10},1);return destination.AsSpan(0,used).ToArray();}
                if(b is 8 or 127){if(used>0)destination[--used]=0;continue;}
                if(b is <33 or >126||used==destination.Length)throw new IOException("CREDENTIAL_INVALID");destination[used++]=b;
            }
        }
        finally{CryptographicOperations.ZeroMemory(destination);CryptographicOperations.ZeroMemory(single);}
    }
    internal static void Harden()
    {
        if(!OperatingSystem.IsLinux()||RuntimeInformation.ProcessArchitecture!=Architecture.X64)throw new IOException("LINUX_X64_REQUIRED");
        umask(0x3f);var limit=new Rlimit{cur=0,max=0};if(setrlimit(4,ref limit)!=0||prctl(4,0,0,0,0)!=0||prctl(3,0,0,0,0)!=0)throw new IOException("PROCESS_HARDENING_REFUSED");
        if(Environment.GetEnvironmentVariable("DOTNET_EnableDiagnostics")!="0"||Environment.GetEnvironmentVariable("COMPlus_EnableDiagnostics")!="0"||!SafeForPrivateDispatch())throw new IOException("UNSAFE_PROCESS_REFUSED");
        var allowed=new HashSet<string>(new[]{"HOME","PATH","DOTNET_ROOT","DOTNET_EnableDiagnostics","COMPlus_EnableDiagnostics","LANG","SSH_CONNECTION","SSH_TTY","TMPDIR"},StringComparer.Ordinal);
        foreach(System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())if(!allowed.Contains((string)e.Key))throw new IOException("ENVIRONMENT_REFUSED");
    }
    internal static bool SafeForPrivateDispatch()
    {
        try
        {
            string[] swap=File.ReadAllLines("/proc/swaps");if(swap.Length<1||!swap[0].StartsWith("Filename",StringComparison.Ordinal)||swap.Skip(1).Any(s=>!string.IsNullOrWhiteSpace(s)))return false;
            string? tracer=File.ReadLines("/proc/self/status").SingleOrDefault(s=>s.StartsWith("TracerPid:",StringComparison.Ordinal));
            return tracer!=null&&tracer.Split(':')[1].Trim()=="0"&&prctl(3,0,0,0,0)==0;
        }
        catch{return false;}
    }
    public void Dispose(){if(tty<0)return;try{if(saved!=null)tcsetattr(tty,2,saved);}finally{close(tty);tty=-1;if(saved!=null)CryptographicOperations.ZeroMemory(saved);}}
    [StructLayout(LayoutKind.Sequential)]private struct Rlimit{public ulong cur,max;}
    [StructLayout(LayoutKind.Sequential)]private struct Poll{public int fd;public short events,revents;}
    [DllImport("libc",SetLastError=true)]private static extern int open(string path,int flags,uint mode);
    [DllImport("libc")]private static extern int close(int fd);
    [DllImport("libc")]private static extern int isatty(int fd);
    [DllImport("libc")]private static extern int getpgrp();
    [DllImport("libc")]private static extern int tcgetpgrp(int fd);
    [DllImport("libc")]private static extern IntPtr ttyname(int fd);
    [DllImport("libc")]private static extern int tcgetattr(int fd,[Out]byte[] bytes);
    [DllImport("libc")]private static extern int tcsetattr(int fd,int action,byte[] bytes);
    [DllImport("libc",SetLastError=true)]private static extern int poll(ref Poll fds,nuint count,int timeout);
    [DllImport("libc",SetLastError=true)]private static extern nint read(int fd,[Out]byte[] buffer,nuint count);
    [DllImport("libc")]private static extern nint write(int fd,byte[] buffer,nuint count);
    [DllImport("libc")]private static extern uint umask(uint mask);
    [DllImport("libc",SetLastError=true)]private static extern int setrlimit(int resource,ref Rlimit limit);
    [DllImport("libc")]private static extern int prctl(int option,ulong a,ulong b,ulong c,ulong d);
}

internal sealed class EphemeralCredentials(byte[] key,byte[] secret,byte[] passphrase) : IDisposable,IOkxExecutionSecretProvider
{
    private bool disposed;
    internal string Fingerprint=>Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant();
    public OkxExecutionCredentialLease Open(OkxScopeBinding binding,OkxScopePaths paths)
    {ObjectDisposedException.ThrowIf(disposed,this);return new(binding,"DemoTradingApi",key.ToArray(),secret.ToArray(),passphrase.ToArray());}
    internal Task<OkxEnrollmentDiscovery> Discover(CancellationToken ct)=>OkxEnrollmentDiscoveryClient.DiscoverFiniteDemoAsync(key,secret,passphrase,LinuxInput.SafeForPrivateDispatch,ct);
    internal Task<OkxEnrollmentDiscovery> DiscoverPrivateSchema(Action<string?,string,int> observer,CancellationToken ct,HttpMessageHandler? handler=null)
        =>OkxEnrollmentDiscoveryClient.RunAsync("Demo","DemoTradingApi",key,secret,passphrase,null,null,ct,handler,LinuxInput.SafeForPrivateDispatch,observer);
    internal IOkxSigningMaterialProvider ForObservation(OkxExecutionScope scope)=>new ObservationSecrets(this,scope);
    private sealed class ObservationSecrets(EphemeralCredentials owner,OkxExecutionScope scope):IOkxSigningMaterialProvider
    {
        public ValueTask<OkxSigningMaterial> OpenAsync(CancellationToken ct)
        {ct.ThrowIfCancellationRequested();ObjectDisposedException.ThrowIf(owner.disposed,owner);if(scope.Environment!="Demo")throw new InvalidOperationException("FINITE_DEMO_ONLY");return ValueTask.FromResult(owner.OpenObservationMaterial(scope));}
    }
    private OkxSigningMaterial OpenObservationMaterial(OkxExecutionScope scope)=>new(scope,"DemoTradingApi",key,secret,passphrase);
    internal bool ContainsSecret(ReadOnlySpan<byte> payload)=>payload.IndexOf(key)>=0||payload.IndexOf(secret)>=0||payload.IndexOf(passphrase)>=0;
    public void Dispose(){if(disposed)return;disposed=true;CryptographicOperations.ZeroMemory(key);CryptographicOperations.ZeroMemory(secret);CryptographicOperations.ZeroMemory(passphrase);}
}
