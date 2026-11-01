using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Desktop.Services;
namespace Apex.Desktop.Okx;
internal sealed record OkxSimpleConnectionResult(OkxReadAccount? Account,OkxEnrollmentObservation? Observation,string Code);
internal static class WindowsOkxSimpleConnect
{
    internal static Task<OkxNativeForm> FormAsync(CancellationToken ct,Func<byte[][],CancellationToken,Task<string?>>? submit=null)=>
        WindowsOkxDialog.ShowDetailedAsync("APEX — lokalny klucz OKX","Połącz konto OKX • EEA • SPOT\nDEMO: tylko odczyt. UID i waluty zostaną wykryte. LIVE zablokowane. Po odczycie wymagana chroniona zgoda Windows na zapis CurrentUser DPAPI. Bez handlu, ownera, Arm ani START.",new[]{new OkxNativeField("API Key",true),new OkxNativeField("Secret Key",true),new OkxNativeField("Passphrase",true)},ct,
            values=>values.Length==3&&values.All(v=>v.Length is >0 and <=4096&&!v.Any(b=>b<33||b>126))?null:"OKX_SECRET_INVALID",true,submit);
    internal static async Task<OkxSimpleConnectionResult> CreateAsync(ConfigStore config,CancellationToken ct,Action<string>? progress=null)
    {
        OkxSimpleConnectionResult result=new(null,null,"CONNECT_CANCELLED");
        progress?.Invoke("SINGLE_NATIVE_CONNECT_FORM");
        var form=await FormAsync(ct,async(values,operation)=>
        {
            progress?.Invoke("SIGNED_GET_ONLY_DISCOVERY");
            var read=await OkxEnrollmentDiscoveryClient.DiscoverAsync("Demo","DemoTradingApi",values[0],values[1],values[2],null,null,operation).ConfigureAwait(false);
            if(read.Observation is not {} observed){result=new(null,null,read.Code);return read.Code;}
            // Duplicate accounts (including legacy scopes) never overwrite a key,
            // change a financial digest or seed another owner.
            var store=new WindowsOkxReadStore(config.DataDir);
            if(store.List().Any(a=>a.Uid==observed.Uid&&a.Environment==observed.Environment)||OkxConfiguration.Bind(config.Config.OkxNaval).Any(b=>b.Account.AccountId==observed.Uid&&b.Account.Environment==Apex.Core.Naval.NavalEnvironment.OkxDemo))
                return "ACCOUNT_ALREADY_SAVED_USE_RECONNECT";
            string request=Guid.NewGuid().ToString("N");var expires=DateTimeOffset.UtcNow.AddSeconds(120);
            progress?.Invoke("APPROVAL_WINDOWS_SECURE_DESKTOP_EXPECTED");
            var approved=await WindowsOkxApprovalProvider.ConfirmDetailedAsync($"Zapis wyłącznie READ-ONLY / STOP / nieuzbrojony\nOKX EEA DEMO | UID {observed.Uid} / główny {observed.MainUid}\nŻądanie {request} | klucz {observed.Fingerprint} | odczyt {observed.ObservedUtc:O}\nZgoda ważna do {expires:O}. Bez Observe/Execute, ownera, Arm, START i WebSocket. Użyj hasła tego samego użytkownika Windows, nie PIN/hasła OKX.",operation).ConfigureAwait(false);
            if(!approved.Accepted){result=new(null,null,approved.SafeCode);return ApprovalHelp(approved.SafeCode);}
            if(operation.IsCancellationRequested||DateTimeOffset.UtcNow>=expires||observed.Environment!="Demo"||observed.Provenance!="DemoTradingApi"||observed.Fingerprint!=Convert.ToHexString(SHA256.HashData(values[0])).ToLowerInvariant())return "CONNECT_APPROVAL_EXPIRED_OR_BINDING_CHANGED";
            progress?.Invoke("CURRENT_USER_DPAPI_SAVE_READBACK");
            var account=store.Save(observed,values);operation.ThrowIfCancellationRequested();
            result=new(account,observed,"CONNECTED_READ_ONLY_STOP");return null;
        }).ConfigureAwait(false);
        if(form.Values!=null)foreach(var value in form.Values)CryptographicOperations.ZeroMemory(value);
        return form.Code=="ACCEPTED"?result:new(null,null,result.Code=="CONNECT_CANCELLED"?"CONNECT_"+form.Code:result.Code);
    }
    internal static string ApprovalHelp(string code)=>code.Contains("WIN32_1326",StringComparison.Ordinal)?code+" — Windows odrzucił dane logowania. Użyj hasła konta Windows (nie PIN), sprawdź nazwę i domenę konta. 1326 nie jest błędem API OKX; przyczyna na tym komputerze nie została ustalona.":code;
}
