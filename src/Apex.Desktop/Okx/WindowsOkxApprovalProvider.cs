using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Naval;
using Microsoft.Win32.SafeHandles;

namespace Apex.Desktop.Okx;

internal sealed record OkxWindowsApproval(bool Accepted,string Code,uint? NativeCode=null)
{
    internal string SafeCode=>Code+(NativeCode.HasValue?"_WIN32_"+NativeCode.Value.ToString(CultureInfo.InvariantCulture):"");
}
// Protected permission only. This provider cannot mint a broker protection/finality profile.
internal sealed class WindowsOkxApprovalProvider : IOkxProtectedActivationProvider,IOkxProtectedBaselineProvider
{
    private readonly Func<NavalBrokerObservationBatch?>? baselineSnapshot;
    private readonly Func<OkxActivationRequest,bool>? activationAdmitted;
    internal WindowsOkxApprovalProvider(Func<NavalBrokerObservationBatch?>? baselineSnapshot=null,Func<OkxActivationRequest,bool>? activationAdmitted=null)
    {this.baselineSnapshot=baselineSnapshot;this.activationAdmitted=activationAdmitted;}
    public ValueTask<bool> ConfirmAsync(OkxActivationRequest r,CancellationToken ct)
    {
        // A direct/fake internal request must not solicit OS credentials without an enrolled exact key.
        if(ct.IsCancellationRequested||activationAdmitted?.Invoke(r)!=true)return ValueTask.FromResult(false);
        return new(ConfirmAsync(
            $"{r.Purpose} | {Scope(r.Binding)}\nKonfiguracja {r.Binding.ConfigDigest}\nSesja {r.Session} / generacja {r.Generation}\nWażne do {r.ExpiresUtc:O}. To nie jest START ani dowód ochrony brokera.\nExecute zezwala na wykonanie strategii po osobnym START oraz jawne redukcje. TP/SL po fill może zawieść; po anulowaniu i nieudanej/Unknown sprzedaży może pozostać niechroniona reszta bez limitu czasu. STOP nie likwiduje. LIVE wymaga tej osobnej zgody dla dokładnego rachunku/sesji.",ct));
    }
    internal static string Scope(OkxScopeBinding b)=>$"OKX EEA {b.Account.Environment} | UID {b.Account.AccountId} | {b.Account.QuoteCurrency} | zakres {b.ScopeId}";
    internal static async Task<bool> ConfirmAsync(string message,CancellationToken ct)=>(await ConfirmDetailedAsync(message,ct).ConfigureAwait(false)).Accepted;
    internal static Task<OkxWindowsApproval> ConfirmDetailedAsync(string message,CancellationToken ct)
    {
        if(ct.IsCancellationRequested)return Task.FromResult(new OkxWindowsApproval(false,"APPROVAL_CANCELLED"));
        if(!OperatingSystem.IsWindows()||!Environment.UserInteractive)return Task.FromResult(new OkxWindowsApproval(false,"APPROVAL_INTERACTIVE_WINDOWS_REQUIRED"));
        return RunApprovalAsync(()=>Confirm(message,ct),ct);
    }
    // One process-wide OS modal. Cancellation revokes the waiting action, NOT the
    // native operation's ownership. Late positive results can never grant permission.
    private static int nativePending;
    internal static async Task<bool> RunConfirmationAsync(Func<bool> native,CancellationToken ct)=>
        (await RunApprovalAsync(()=>new(native(),"APPROVAL_TEST_ADAPTER"),ct).ConfigureAwait(false)).Accepted;
    internal static async Task<OkxWindowsApproval> RunApprovalAsync(Func<OkxWindowsApproval> native,CancellationToken ct,TimeSpan? timeout=null)
    {
        if(ct.IsCancellationRequested)return new(false,"APPROVAL_CANCELLED");
        if(Interlocked.CompareExchange(ref nativePending,1,0)!=0)return new(false,"APPROVAL_NATIVE_PENDING_DISMISS_PREVIOUS");
        var tcs=new TaskCompletionSource<OkxWindowsApproval>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            OkxWindowsApproval result=new(false,"APPROVAL_NATIVE_EXCEPTION");
            try{result=native();}catch(Exception e) when(e is not OutOfMemoryException){}
            finally{Interlocked.Exchange(ref nativePending,0);tcs.TrySetResult(ct.IsCancellationRequested?new(false,"APPROVAL_CANCELLED"):result);}
        });
        try{if(OperatingSystem.IsWindows())thread.SetApartmentState(ApartmentState.STA);thread.IsBackground=true;thread.Start();}
        catch(Exception e) when(e is not OutOfMemoryException){Interlocked.Exchange(ref nativePending,0);return new(false,"APPROVAL_THREAD_UNAVAILABLE");}
        try{var result=await tcs.Task.WaitAsync(timeout??TimeSpan.FromSeconds(120),ct).ConfigureAwait(false);return ct.IsCancellationRequested?new(false,"APPROVAL_CANCELLED"):result;}
        catch(OperationCanceledException){return new(false,"APPROVAL_CANCELLED");}
        catch(TimeoutException){return new(false,"APPROVAL_TIMEOUT_DISMISS_WINDOWS_PROMPT");}
    }
    private static OkxWindowsApproval Confirm(string message,CancellationToken ct)
    {
        if(!OperatingSystem.IsWindows())return new(false,"APPROVAL_INTERACTIVE_WINDOWS_REQUIRED");
        var info=new CredUi{Size=Marshal.SizeOf<CredUi>(),Caption="APEX — chroniona zgoda OKX",Message=message};
        uint package=0;bool save=false;IntPtr packed=IntPtr.Zero;uint packedSize=0;long start=Environment.TickCount64;
        var user=new char[514];var domain=new char[257];var password=new char[257];
        GCHandle u=default,d=default,p=default;
        try
        {
            // Preserve SECURE_PROMPT | ENUMERATE_CURRENT_USER and same-SID/session
            // verification. No generic prompt, cached approval or DEMO bypass.
            uint code=CredUIPromptForWindowsCredentialsW(ref info,0,ref package,IntPtr.Zero,0,out packed,out packedSize,ref save,0x1200);
            if(code!=0)return new(false,code==1223?"APPROVAL_USER_CANCELLED":"APPROVAL_PROMPT_OS_ERROR",code);
            if(ct.IsCancellationRequested)return new(false,"APPROVAL_CANCELLED");
            if(Environment.TickCount64-start>=120000)return new(false,"APPROVAL_EXPIRED");
            uint us=(uint)user.Length,ds=(uint)domain.Length,ps=(uint)password.Length;
            // Documented same-logon protected credential decryption. No PIN to
            // password conversion or generic/nonsecure fallback is attempted.
            if(!UnpackProtected(packed,packedSize,user,ref us,domain,ref ds,password,ref ps))return new(false,"APPROVAL_UNPACK_UNSUPPORTED_METHOD",(uint)Marshal.GetLastWin32Error());
            u=GCHandle.Alloc(user,GCHandleType.Pinned);d=GCHandle.Alloc(domain,GCHandleType.Pinned);p=GCHandle.Alloc(password,GCHandleType.Pinned);
            // UPN names require NULL domain. Preserve an explicit local/domain/
            // MicrosoftAccount provider domain exactly; do not guess or retry it.
            bool upn=Array.IndexOf(user,'@')>=0&&domain[0]=='\0';
            if(!LogonUserW(u.AddrOfPinnedObject(),upn?IntPtr.Zero:d.AddrOfPinnedObject(),p.AddrOfPinnedObject(),2,0,out var token))return new(false,"APPROVAL_AUTHENTICATION_REFUSED",(uint)Marshal.GetLastWin32Error());
            using(token)
            using(var identity=new WindowsIdentity(token.DangerousGetHandle()))
            using(var current=WindowsIdentity.GetCurrent())
            {
                int hostSession=Process.GetCurrentProcess().SessionId;
                if(identity.User==null||identity.User!=current.User)return new(false,"APPROVAL_SAME_USER_REQUIRED");
                if(hostSession==0||!ProcessIdToSessionId((uint)Environment.ProcessId,out uint actual)||actual!=hostSession)return new(false,"APPROVAL_INTERACTIVE_SESSION_REQUIRED");
                return ct.IsCancellationRequested?new(false,"APPROVAL_CANCELLED"):Environment.TickCount64-start>=120000?new(false,"APPROVAL_EXPIRED"):new(true,"APPROVAL_VERIFIED");
            }
        }
        finally
        {
            Array.Clear(user);Array.Clear(domain);Array.Clear(password);
            if(u.IsAllocated)u.Free();if(d.IsAllocated)d.Free();if(p.IsAllocated)p.Free();
            if(packed!=IntPtr.Zero){for(uint i=0;i<packedSize;i++)Marshal.WriteByte(packed,(int)i,0);Marshal.FreeCoTaskMem(packed);}
        }
    }
    internal static bool UnpackProtected(IntPtr packed,uint size,char[] user,ref uint userSize,char[] domain,ref uint domainSize,char[] password,ref uint passwordSize)=>
        CredUnPackAuthenticationBufferW(4,packed,size,user,ref userSize,domain,ref domainSize,password,ref passwordSize);
    public async ValueTask<OkxKeyScopeInspection?> InspectAsync(OkxScopeBinding binding,string fingerprint,CancellationToken ct)
    {
        var fields=await WindowsOkxDialog.ShowAsync("APEX — uprawnienia klucza OKX",Scope(binding)+"\nSprawdź klucz w OKX. Trade może obejmować dodatkowe operacje. Nie potwierdzaj bez dowodu wykluczenia finansowania/pożyczek/earn/wypłat.",new[]{
            new OkxNativeField("Adres IP przypisany do klucza (dokładnie jak w OKX)"),new OkxNativeField("Odniesienie do rzeczywistej kontroli uprawnień (bez sekretów)"),
            new OkxNativeField("Wpisz: BEZ TRANSFERÓW POŻYCZEK EARN WYPŁAT")},ct).ConfigureAwait(false);
        if(fields==null)return null;
        try
        {
            string ip=Encoding.UTF8.GetString(fields[0]),evidence=Encoding.UTF8.GetString(fields[1]),confirmation=Encoding.UTF8.GetString(fields[2]);
            if(confirmation!="BEZ TRANSFERÓW POŻYCZEK EARN WYPŁAT"||evidence.Length>512||ip.Length>256)return null;
            if(!await ConfirmAsync(Scope(binding)+"\nZgoda na odczyt i kontrolę klucza. IP: "+ip+"\nOdniesienie: "+evidence+"\nTo oświadczenie operatora, NIE dowód gotowości do handlu.",ct).ConfigureAwait(false))return null;
            return new(fingerprint,"okx-dedicated-cash-no-funding-v1",Convert.ToHexString(SHA256.HashData(fields[1])).ToLowerInvariant(),ip);
        }
        finally{foreach(var bytes in fields)CryptographicOperations.ZeroMemory(bytes);}
    }
    public ValueTask<bool> ApproveAsync(NavalBaselineApprovalRequest r,CancellationToken ct)
    {
        var actual=baselineSnapshot?.Invoke();
        if(actual==null||actual.Connection!=r.Session||actual.Account.Uid!=r.Account.AccountId||actual.CatalogDigest!=r.CatalogDigest||actual.FeeDigest!=r.FeeDigest||actual.Balances.Count!=1||actual.Balances[0].Cash!=r.Cash)return ValueTask.FromResult(false);
        string text=$"Baza z uwierzytelnionego odczytu: {r.Account.Environment} / UID {r.Account.AccountId}\nKapitał {r.Cash.ToString(CultureInfo.InvariantCulture)} {r.Quote}\nRachunek cash / poziom {actual.Account.Level}; IP {actual.Account.Ip}\nInstrumenty: {string.Join(", ",System.Linq.Enumerable.Select(actual.Catalog,i=>i.Instrument))}\nOpłata wejścia {actual.Fees.EntryRate.ToString(CultureInfo.InvariantCulture)} ({(actual.Fees.EntryInBase?"base":"quote")}), wyjścia {actual.Fees.ExitRate.ToString(CultureInfo.InvariantCulture)} ({(actual.Fees.ExitInBase?"base":"quote")}).\nKatalog {r.CatalogDigest}\nOpłaty {r.FeeDigest}\nDokładny skrót bazy {r.Digest}\nNie tworzy ochrony ani zgody START. Zatwierdź wyłącznie zgodne rzeczywiste obserwacje.";
        return text.Length>16000?ValueTask.FromResult(false):new(ConfirmAsync(text,ct));
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct CredUi{internal int Size;internal IntPtr Parent;internal string Message;internal string Caption;internal IntPtr Banner;}
    [DllImport("credui.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]private static extern uint CredUIPromptForWindowsCredentialsW(ref CredUi info,uint error,ref uint package,IntPtr input,uint inputSize,out IntPtr output,out uint outputSize,ref bool save,uint flags);
    [DllImport("credui.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]private static extern bool CredUnPackAuthenticationBufferW(uint flags,IntPtr buffer,uint size,[Out]char[] user,ref uint userSize,[Out]char[] domain,ref uint domainSize,[Out]char[] password,ref uint passwordSize);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]private static extern bool LogonUserW(IntPtr user,IntPtr domain,IntPtr password,int type,int provider,out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll")]private static extern bool ProcessIdToSessionId(uint pid,out uint session);
}
