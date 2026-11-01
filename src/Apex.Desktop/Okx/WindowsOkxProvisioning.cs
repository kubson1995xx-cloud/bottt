using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apex.Desktop.Services;

namespace Apex.Desktop.Okx;
internal sealed record OkxEnrollmentResult(OkxScopeBinding? Binding,string Code);
internal static class WindowsOkxProvisioning
{
    internal static async Task<OkxEnrollmentResult> CreateAsync(ConfigStore config,CancellationToken ct,Action<string>? progress=null)
    {
        if(!OperatingSystem.IsWindows())return new(null,"NATIVE_WINDOWS_REQUIRED");
        progress?.Invoke("METADATA_WINDOW_EXPECTED");
        var form=await WindowsOkxDialog.ShowDetailedAsync("APEX — konfiguracja OKX","Krok 1/3: dane zakresu, NIE klucze. Dalej chroniona zgoda Windows (hasło tego użytkownika, nie hasło OKX), potem API Key / Secret Key / Passphrase. Bez połączenia ani START. Istniejących zakresów nie nadpisujemy.",new[]{
            new OkxNativeField("Środowisko: wpisz dokładnie DEMO albo LIVE (brak domyślnego)"),new OkxNativeField("Nazwa zakresu (litery ASCII, cyfry, - lub _)"),
            new OkxNativeField("UID głównego rachunku (bez subkonta)"),new OkxNativeField("Rzeczywista waluta kwotowana (np. EUR; bez przeliczania USDT)")},ct,values=>
            {
                try{ReadMetadata(config,values);return null;}
                catch(Exception e) when(e is InvalidDataException or InvalidOperationException or ArgumentException){return "OKX_METADATA_INVALID";}
            }).ConfigureAwait(false);
        if(form.Values==null)return new(null,"METADATA_"+form.Code);
        OkxScopeOptions option;OkxScopeBinding binding;
        try{(option,binding)=ReadMetadata(config,form.Values);}
        finally{foreach(var bytes in form.Values)CryptographicOperations.ZeroMemory(bytes);}
        progress?.Invoke("APPROVAL_WINDOWS_SECURE_DESKTOP_EXPECTED");
        var approval=await WindowsOkxApprovalProvider.ConfirmDetailedAsync(WindowsOkxApprovalProvider.Scope(binding)+"\nKrok 2/3: potwierdź hasłem tego samego użytkownika Windows. To nie jest hasło OKX. Następnie otworzy się lokalny formularz trzech kluczy OKX. Brak połączenia, START lub zgody Execute.",ct).ConfigureAwait(false);
        if(!approval.Accepted)return new(null,approval.SafeCode);
        progress?.Invoke("SECRET_WINDOW_API_KEY_SECRET_KEY_PASSPHRASE_EXPECTED");
        var secretForm=await ShowSecretsAsync(binding,ct).ConfigureAwait(false);
        if(secretForm.Values==null)return new(null,"SECRET_FORM_"+secretForm.Code);
        try
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke("KEY_STORE_DPAPI_WRITE");
            return PersistAndVerify(config,option,binding,secretForm.Values,progress);
        }
        finally{foreach(var bytes in secretForm.Values)CryptographicOperations.ZeroMemory(bytes);}
    }
    // Same actual native form used by production. Tests of this function alone do
    // NOT establish that Windows protected approval succeeded.
    internal static Task<OkxNativeForm> ShowSecretsAsync(OkxScopeBinding binding,CancellationToken ct)=>
        WindowsOkxDialog.ShowDetailedAsync("APEX — lokalny klucz OKX",WindowsOkxApprovalProvider.Scope(binding)+"\nKrok 3/3: trzy maskowane pola, wyłącznie lokalnie. Klucz z wybranego DEMO/LIVE. Odczyt DEMO wymaga klucza read_only (bez Trade). Zapis CurrentUser DPAPI; bez automatycznego połączenia.",new[]{new OkxNativeField("API Key",true),new OkxNativeField("Secret Key",true),new OkxNativeField("Passphrase",true)},ct,values=>values.Length==3&&values.All(v=>v.Length is >0 and <=4096&&!v.Any(b=>b<32||b==127))?null:"OKX_SECRET_INVALID");
    internal static OkxEnrollmentResult PersistAndVerify(ConfigStore config,OkxScopeOptions option,OkxScopeBinding binding,byte[][] secrets,Action<string>? progress=null)
    {
        string failure="KEY_STORE_WRITE_FAILED_PRESERVED";
        try
        {
            // The immutable store refuses existing/partial scopes; never repair or reseed.
            var store=new WindowsOkxCredentialStore(config.DataDir);
            store.Create(binding,OkxConfiguration.Paths(config.DataDir,binding),option.Environment=="OkxDemo"?"DemoTradingApi":"LiveAccountApi",secrets[0],secrets[1],secrets[2]);
            failure="CONFIG_WRITE_FAILED_KEY_PRESERVED";progress?.Invoke("KEY_SAVED_CONFIG_WRITE");
            config.Update(c=>c.OkxNaval=new(){Schema="okx-naval-config-v1",Scopes=(c.OkxNaval?.Scopes??Array.Empty<OkxScopeOptions>()).Append(option).ToArray()});
            failure="SAVE_READBACK_REFUSED_PRESERVED";progress?.Invoke("CONFIG_SAVED_DPAPI_READBACK");
            if(!SavedBindingMatches(config,binding))return new(null,failure);
            using var opened=store.Open(binding,OkxConfiguration.Paths(config.DataDir,binding));
            if(opened.Binding!=binding||!opened.ApiKey.SequenceEqual(secrets[0])||!opened.Secret.SequenceEqual(secrets[1])||!opened.Passphrase.SequenceEqual(secrets[2]))return new(null,failure);
            return new(binding,"SAVED_KEY_CONFIG_READBACK_VERIFIED_STOP");
        }
        catch(Exception e) when(e is not OutOfMemoryException){return new(null,failure);}
    }
    internal static bool SavedBindingMatches(ConfigStore config,OkxScopeBinding binding)
    {
        // Read only; unlike ConfigStore.Load this cannot normalize or create files.
        string raw=File.ReadAllText(config.Path_);OkxConfiguration.ValidateRaw(raw);
        var saved=JsonSerializer.Deserialize<HostConfig>(raw,ConfigStore.Json);
        return saved!=null&&OkxConfiguration.Bind(saved.OkxNaval).Contains(binding)&&OkxConfiguration.Bind(config.Config.OkxNaval).Contains(binding);
    }
    internal static string PassiveKeyState(string root,OkxScopeBinding binding)
    {
        var p=OkxConfiguration.Paths(root,binding);
        try
        {
            var attributes=File.GetAttributes(p.Root);
            if((attributes&(FileAttributes.Directory|FileAttributes.ReparsePoint))!=FileAttributes.Directory)return "INCOMPLETE_STORE_PRESERVED";
            return File.Exists(p.Credentials)&&!File.Exists(p.Credentials+".pending")?"CIPHERTEXT_PRESENT_NOT_VERIFIED":"INCOMPLETE_STORE_PRESERVED";
        }
        catch(Exception e) when(e is FileNotFoundException or DirectoryNotFoundException){return "METADATA_ONLY_NO_KEY";}
        catch(Exception e) when(e is not OutOfMemoryException){return "STORE_STATUS_UNAVAILABLE_PRESERVED";}
    }
    internal static string PassiveUnboundState(ConfigStore config)
    {
        try
        {
            string root=Path.Combine(config.DataDir,"okx");if(!Directory.Exists(root))return "NO_UNBOUND_SCOPE_OBSERVED";
            var known=OkxConfiguration.Bind(config.Config.OkxNaval).Select(b=>OkxConfiguration.Paths(config.DataDir,b).Root).ToHashSet(OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal);
            foreach(string env in Directory.EnumerateFileSystemEntries(root))
            {
                if(!Directory.Exists(env)||Path.GetFileName(env) is not ("demo" or "live"))return "UNBOUND_OR_PARTIAL_STORE_PRESERVED";
                foreach(string scope in Directory.EnumerateFileSystemEntries(env))if(!known.Contains(scope))return "UNBOUND_OR_PARTIAL_STORE_PRESERVED";
            }
            return "NO_UNBOUND_SCOPE_OBSERVED";
        }
        catch(Exception e) when(e is not OutOfMemoryException){return "STORE_STATUS_UNAVAILABLE_PRESERVED";}
    }
    private static (OkxScopeOptions,OkxScopeBinding) ReadMetadata(ConfigStore config,byte[][] metadata)
    {
        string env=Encoding.UTF8.GetString(metadata[0]);
        if(env is not ("DEMO" or "LIVE"))throw new InvalidDataException("OKX_SCOPE_INVALID");
        var option=new OkxScopeOptions{ScopeId=Encoding.UTF8.GetString(metadata[1]),Environment=env=="DEMO"?"OkxDemo":"Live",Region="EEA",ExpectedUid=Encoding.UTF8.GetString(metadata[2]),ExpectedMainUid=Encoding.UTF8.GetString(metadata[2]),QuoteCurrency=Encoding.UTF8.GetString(metadata[3]),CredentialBindingId=Guid.NewGuid().ToString("N"),SafetyVersion="okx-cash-v1"};
        var all=(config.Config.OkxNaval?.Scopes??Array.Empty<OkxScopeOptions>()).Append(option).ToArray();
        var binding=OkxConfiguration.Bind(new(){Schema="okx-naval-config-v1",Scopes=all}).Single(b=>b.ScopeId==option.ScopeId);
        return(option,binding);
    }
}
