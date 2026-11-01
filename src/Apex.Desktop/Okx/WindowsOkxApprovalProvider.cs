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

// Protected permission only. This provider cannot mint a broker protection/finality profile.
internal sealed class WindowsOkxApprovalProvider : IOkxProtectedActivationProvider,IOkxProtectedBaselineProvider
{
    public ValueTask<bool> ConfirmAsync(OkxActivationRequest r,CancellationToken ct)=>new(ConfirmAsync(
        $"{r.Purpose} | {Scope(r.Binding)}\nKonfiguracja {r.Binding.ConfigDigest}\nSesja {r.Session} / generacja {r.Generation}\nWażne do {r.ExpiresUtc:O}. To nie jest START ani dowód ochrony brokera.",ct));
    internal static string Scope(OkxScopeBinding b)=>$"OKX EEA {b.Account.Environment} | UID {b.Account.AccountId} | {b.Account.QuoteCurrency} | zakres {b.ScopeId}";
    internal static Task<bool> ConfirmAsync(string message,CancellationToken ct)
    {
        if(!OperatingSystem.IsWindows()||!Environment.UserInteractive||ct.IsCancellationRequested)return Task.FromResult(false);
        var tcs=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{try{tcs.SetResult(Confirm(message,ct));}catch(Exception e) when(e is not OutOfMemoryException){tcs.SetResult(false);}});
        thread.SetApartmentState(ApartmentState.STA);thread.IsBackground=true;thread.Start();return tcs.Task;
    }
    private static bool Confirm(string message,CancellationToken ct)
    {
        if(!OperatingSystem.IsWindows())return false;
        var info=new CredUi{Size=Marshal.SizeOf<CredUi>(),Caption="APEX — chroniona zgoda OKX",Message=message};
        uint package=0;bool save=false;IntPtr packed=IntPtr.Zero;uint packedSize=0;long start=Environment.TickCount64;
        var user=new char[514];var domain=new char[257];var password=new char[257];
        GCHandle u=default,d=default,p=default;
        try
        {
            // CREDUIWIN_SECURE_PROMPT | ENUMERATE_CURRENT_USER. Never save/copy a Windows password.
            if(CredUIPromptForWindowsCredentialsW(ref info,0,ref package,IntPtr.Zero,0,out packed,out packedSize,ref save,0x1200)!=0||ct.IsCancellationRequested||Environment.TickCount64-start>=120000)return false;
            uint us=(uint)user.Length,ds=(uint)domain.Length,ps=(uint)password.Length;
            if(!CredUnPackAuthenticationBufferW(0,packed,packedSize,user,ref us,domain,ref ds,password,ref ps))return false;
            u=GCHandle.Alloc(user,GCHandleType.Pinned);d=GCHandle.Alloc(domain,GCHandleType.Pinned);p=GCHandle.Alloc(password,GCHandleType.Pinned);
            if(!LogonUserW(u.AddrOfPinnedObject(),d.AddrOfPinnedObject(),p.AddrOfPinnedObject(),2,0,out var token))return false;
            using(token)
            using(var identity=new WindowsIdentity(token.DangerousGetHandle()))
            using(var current=WindowsIdentity.GetCurrent())
            {
                // Interactive credential logon tokens are assigned session zero until attached.
                // Compare the host's live interactive session independently, and deny session zero.
                int hostSession=Process.GetCurrentProcess().SessionId;
                return identity.User!=null&&identity.User==current.User&&hostSession!=0&&
                    ProcessIdToSessionId((uint)Environment.ProcessId,out uint actual)&&actual==hostSession&&
                    !ct.IsCancellationRequested&&Environment.TickCount64-start<120000;
            }
        }
        finally
        {
            Array.Clear(user);Array.Clear(domain);Array.Clear(password);
            if(u.IsAllocated)u.Free();if(d.IsAllocated)d.Free();if(p.IsAllocated)p.Free();
            if(packed!=IntPtr.Zero){for(uint i=0;i<packedSize;i++)Marshal.WriteByte(packed,(int)i,0);Marshal.FreeCoTaskMem(packed);}
        }
    }
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
    public ValueTask<bool> ApproveAsync(NavalBaselineApprovalRequest r,CancellationToken ct)=>new(ConfirmAsync(
        $"Baza z uwierzytelnionego odczytu: {r.Account.Environment} / UID {r.Account.AccountId}\nKapitał {r.Cash.ToString(CultureInfo.InvariantCulture)} {r.Quote}\nKatalog {r.CatalogDigest}\nOpłaty {r.FeeDigest}\nDokładny skrót bazy {r.Digest}\nNie tworzy ochrony ani zgody START. Zatwierdź wyłącznie zgodne rzeczywiste obserwacje.",ct));
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct CredUi{internal int Size;internal IntPtr Parent;internal string Message;internal string Caption;internal IntPtr Banner;}
    [DllImport("credui.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]private static extern uint CredUIPromptForWindowsCredentialsW(ref CredUi info,uint error,ref uint package,IntPtr input,uint inputSize,out IntPtr output,out uint outputSize,ref bool save,uint flags);
    [DllImport("credui.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]private static extern bool CredUnPackAuthenticationBufferW(uint flags,IntPtr buffer,uint size,[Out]char[] user,ref uint userSize,[Out]char[] domain,ref uint domainSize,[Out]char[] password,ref uint passwordSize);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]private static extern bool LogonUserW(IntPtr user,IntPtr domain,IntPtr password,int type,int provider,out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll")]private static extern bool ProcessIdToSessionId(uint pid,out uint session);
}
