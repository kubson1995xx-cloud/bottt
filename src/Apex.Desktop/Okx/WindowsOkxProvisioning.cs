using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Apex.Desktop.Services;

namespace Apex.Desktop.Okx;
internal static class WindowsOkxProvisioning
{
    internal static async Task<OkxScopeBinding?> CreateAsync(ConfigStore config,CancellationToken ct)
    {
        if(!OperatingSystem.IsWindows())throw new InvalidOperationException("NATIVE_WINDOWS_REQUIRED");
        var metadata=await WindowsOkxDialog.ShowAsync("APEX — konfiguracja OKX","Dedykowany cash SPOT ≤1x, bez pożyczek, feeType=1: opłaty tylko w quote. Warunkowe TP/SL po fill może zawieść; po anulowaniu i nieudanej sprzedaży może pozostać niechroniona reszta bez limitu czasu. STOP nie likwiduje. Bez połączenia/START; LIVE domyślnie OFF. Istniejących zakresów nie nadpisujemy.",new[]{
            new OkxNativeField("Środowisko: wpisz dokładnie DEMO albo LIVE (brak domyślnego)"),new OkxNativeField("Nazwa zakresu (litery ASCII, cyfry, - lub _)"),
            new OkxNativeField("UID głównego rachunku (bez subkonta)"),new OkxNativeField("Rzeczywista waluta kwotowana (np. EUR; bez przeliczania USDT)")},ct).ConfigureAwait(false);
        if(metadata==null)return null;
        OkxScopeOptions option;OkxScopeBinding binding;
        try
        {
            string env=Encoding.UTF8.GetString(metadata[0]);
            if(env is not ("DEMO" or "LIVE"))throw new InvalidDataException("OKX_SCOPE_INVALID");
            option=new(){ScopeId=Encoding.UTF8.GetString(metadata[1]),Environment=env=="DEMO"?"OkxDemo":"Live",Region="EEA",ExpectedUid=Encoding.UTF8.GetString(metadata[2]),ExpectedMainUid=Encoding.UTF8.GetString(metadata[2]),QuoteCurrency=Encoding.UTF8.GetString(metadata[3]),CredentialBindingId=Guid.NewGuid().ToString("N"),SafetyVersion="okx-cash-v1"};
            var all=(config.Config.OkxNaval?.Scopes??Array.Empty<OkxScopeOptions>()).Append(option).ToArray();
            binding=OkxConfiguration.Bind(new(){Schema="okx-naval-config-v1",Scopes=all}).Single(b=>b.ScopeId==option.ScopeId);
        }
        finally{foreach(var bytes in metadata)CryptographicOperations.ZeroMemory(bytes);}
        if(!await WindowsOkxApprovalProvider.ConfirmAsync(WindowsOkxApprovalProvider.Scope(binding)+"\nUtworzenie lokalnego magazynu klucza. Pochodzenie klucza musi odpowiadać wybranemu środowisku. Nie uruchamia handlu. Bez wypłat, transferów ani pożyczek.",ct).ConfigureAwait(false))return null;
        var secrets=await WindowsOkxDialog.ShowAsync("APEX — lokalny klucz OKX",WindowsOkxApprovalProvider.Scope(binding)+"\nTylko lokalny formularz. Dane nie trafiają do przeglądarki. Klucz musi pochodzić z wybranego DEMO/LIVE; nie zostanie automatycznie połączony.",new[]{new OkxNativeField("API key",true),new OkxNativeField("Secret key",true),new OkxNativeField("Passphrase",true)},ct).ConfigureAwait(false);
        if(secrets==null)return null;
        try
        {
            ct.ThrowIfCancellationRequested();
            new WindowsOkxCredentialStore(config.DataDir).Create(binding,OkxConfiguration.Paths(config.DataDir,binding),option.Environment=="OkxDemo"?"DemoTradingApi":"LiveAccountApi",secrets[0],secrets[1],secrets[2]);
            // If persistence fails, preserve the ciphertext/partial state for investigation; never overwrite/reseed.
            config.Update(c=>c.OkxNaval=new(){Schema="okx-naval-config-v1",Scopes=(c.OkxNaval?.Scopes??Array.Empty<OkxScopeOptions>()).Append(option).ToArray()});
            return binding;
        }
        finally{foreach(var bytes in secrets)CryptographicOperations.ZeroMemory(bytes);}
    }
}
