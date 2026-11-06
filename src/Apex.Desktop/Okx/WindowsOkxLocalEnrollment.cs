using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Naval;
using Apex.Desktop.Services;
namespace Apex.Desktop.Okx;

// What the operator sees before a new local owner is created. Facts come from one
// signed DEMO GET-only discovery of the exact keys typed into the native form.
internal sealed record WindowsLocalOwnerOffer(string Uid,string MainUid,decimal EurCash,IReadOnlyList<string> ExcludedCurrencies,string Fingerprint)
{
    internal string Text()=>
        "To utworzy NOWĄ lokalną historię bota na TYM komputerze — oddzielną od historii bota na VPS. Historia VPS nie zostanie przeniesiona ani zmieniona.\n"+
        "Jeśli to jest to samo konto OKX DEMO, którego używa bot na VPS, bot na VPS MUSI pozostać zatrzymany (STOP). Dwa boty na jednym koncie są niedozwolone.\n"+
        $"Konto: OKX EEA DEMO, UID {Uid}.\n"+
        $"Kapitał bota = całe saldo EUR na tym koncie DEMO: {EurCash.ToString(CultureInfo.InvariantCulture)} EUR. Inne waluty ({(ExcludedCurrencies.Count==0?"brak":string.Join(", ",ExcludedCurrencies))}) są wykluczone i nie będą sprzedawane.\n"+
        "Limity ryzyka, SL/TP i strategia bez zmian. Brak automatycznego START: po utworzeniu bot jest zatrzymany (STOP).\n"+
        "Aby potwierdzić, przepisz dokładnie oba zdania oświadczenia i słowo potwierdzenia.";
}
// Native operator surfaces. Production composition only ever uses NativeUi below;
// HTTP, controller frames, CLI arguments, environment and configuration carry no
// confirmation, provenance or premise field.
internal interface IWindowsLocalEnrollmentUi
{
    Task<OkxNativeForm> KeysAsync(Func<byte[][],CancellationToken,Task<string?>> submit,CancellationToken ct);
    // Returns the operator's typed lines (statement line 1, line 2, confirmation word) or null.
    Task<string[]?> ConfirmAsync(WindowsLocalOwnerOffer offer,CancellationToken ct);
    Task<OkxWindowsApproval> ProtectedApprovalAsync(string message,CancellationToken ct);
}
internal sealed record WindowsLocalTransports(
    Func<byte[],byte[],byte[],CancellationToken,Task<OkxEnrollmentDiscovery>> Discover,
    Func<OkxNavalRuntime,string,OkxScopeBinding,IOkxProtectedBaselineProvider,OkxAccountConnectionService> Service);
internal sealed class WindowsOkxLocalEnrollment
{
    internal const string ScopeId="local-demo",CredentialBindingId="local-demo-key",ConfirmationWord="TWORZĘ LOKALNEGO BOTA";
    private readonly IWindowsLocalEnrollmentUi ui;
    private readonly WindowsLocalTransports transports;
    private WindowsOkxLocalEnrollment(IWindowsLocalEnrollmentUi ui,WindowsLocalTransports transports){this.ui=ui;this.transports=transports;}
    internal static WindowsOkxLocalEnrollment Native()=>new(new NativeUi(),new(
        (k,s,p,ct)=>OkxEnrollmentDiscoveryClient.DiscoverAsync("Demo","DemoTradingApi",k,s,p,null,null,ct),
        OkxAccountConnectionService.CreateWindowsLocalFinite));
    // Friend-assembly composition for native synthetic tests only; no production caller.
    internal static WindowsOkxLocalEnrollment ForTests(IWindowsLocalEnrollmentUi ui,WindowsLocalTransports transports)=>new(ui,transports);
    internal static bool RemoteProfilePresent(string root)=>!WindowsOkxLocalAttachment.RemoteProfileAbsent(root);
    internal static bool LocalScopeConfigured(ConfigStore config)=>OkxConfiguration.Bind(config.Config.OkxNaval).Any(b=>b.Account.Environment==NavalEnvironment.OkxDemo);

    internal async Task<string> CreateAsync(ConfigStore config,OkxNavalRuntime main,CancellationToken ct,Action<string> progress)
    {
        if(!OperatingSystem.IsWindows())return "NATIVE_WINDOWS_REQUIRED";
        string root=config.DataDir;
        if(RemoteProfilePresent(root))return "LOCAL_CREATE_REFUSED_VPS_CONTROLLER_PROFILE_PRESENT";
        if(LocalScopeConfigured(config))return "LOCAL_SCOPE_ALREADY_CONFIGURED_USE_ATTACH";
        OkxEnrollmentObservation? observed=null;string refusal="LOCAL_KEYS_CANCELLED";
        progress("LOCAL_KEYS_NATIVE_FORM");
        var form=await ui.KeysAsync(async(values,operation)=>
        {
            progress("LOCAL_DEMO_DISCOVERY_GET_ONLY");
            var read=await transports.Discover(values[0],values[1],values[2],operation).ConfigureAwait(false);
            if(read.Observation is not {} o){refusal="LOCAL_DEMO_KEYS_REFUSED_"+read.Code;return refusal;}
            if(o.Environment!="Demo"||o.Provenance!="DemoTradingApi"){refusal="LOCAL_DEMO_ONLY_LIVE_REFUSED";return refusal;}
            if(o.Uid!=o.MainUid){refusal="LOCAL_MAIN_ACCOUNT_REQUIRED";return refusal;}
            if(Eur(o) is not > 0m){refusal="LOCAL_EUR_CASH_REQUIRED";return refusal;}
            observed=o;return null;
        },ct).ConfigureAwait(false);
        if(form.Values==null||observed==null)return form.Code=="ACCEPTED"?refusal:observed==null&&refusal!="LOCAL_KEYS_CANCELLED"?refusal:"LOCAL_KEYS_"+form.Code;
        try
        {
            var o=observed;
            var offer=new WindowsLocalOwnerOffer(o.Uid,o.MainUid,Eur(o)!.Value,o.Balances.Select(b=>b.Currency).Where(c=>c!="EUR").Distinct(StringComparer.Ordinal).OrderBy(c=>c,StringComparer.Ordinal).ToArray(),o.Fingerprint);
            progress("LOCAL_CONFIRMATION_NATIVE_DIALOG");
            var typed=await ui.ConfirmAsync(offer,ct).ConfigureAwait(false);
            if(typed is not {Length:3}||typed[0]+"\n"+typed[1]!=NavalCurrentMarketFunding.Statement||typed[2]!=ConfirmationWord)return "LOCAL_CONFIRMATION_REFUSED_NOTHING_SAVED";
            // Minted only here, immediately after the operator typed the exact
            // Statement in the native dialog on this PC. Never deserialized from input.
            var issued=DateTimeOffset.UtcNow;
            progress("APPROVAL_WINDOWS_SECURE_DESKTOP_EXPECTED");
            var approval=await ui.ProtectedApprovalAsync($"Nowy LOKALNY bot APEX na tym PC — OKX EEA DEMO, UID {offer.Uid}\nKapitał bota = całe saldo EUR: {offer.EurCash.ToString(CultureInfo.InvariantCulture)} EUR; inne waluty wykluczone i nie będą sprzedawane.\nNowa lokalna historia, oddzielna od VPS. Bot na VPS musi pozostać w STOP dla tego samego konta.\nZapis klucza DEMO (DPAPI) i utworzenie właściciela w STOP. Bez START, bez transakcji testowej.\nUżyj hasła tego samego użytkownika Windows, nie PIN ani hasła OKX.",ct).ConfigureAwait(false);
            if(!approval.Accepted)return approval.SafeCode;
            if(ct.IsCancellationRequested)return "LOCAL_CREATE_CANCELLED";
            var option=new OkxScopeOptions{ScopeId=ScopeId,Environment="OkxDemo",Region="EEA",ExpectedUid=o.Uid,ExpectedMainUid=o.MainUid,QuoteCurrency="EUR",CredentialBindingId=CredentialBindingId,SafetyVersion="okx-cash-v1"};
            var all=(config.Config.OkxNaval?.Scopes??Array.Empty<OkxScopeOptions>()).Append(option).ToArray();
            var binding=OkxConfiguration.Bind(new(){Schema="okx-naval-config-v1",Scopes=all}).Single(b=>b.ScopeId==ScopeId);
            var paths=OkxConfiguration.Paths(root,binding);
            progress("LOCAL_KEY_DPAPI_WRITE");
            var store=new WindowsOkxCredentialStore(root);
            string keyState=WindowsOkxProvisioning.PassiveKeyState(root,binding);
            if(keyState=="METADATA_ONLY_NO_KEY")store.Create(binding,paths,"DemoTradingApi",form.Values[0],form.Values[1],form.Values[2]);
            else if(keyState!="CIPHERTEXT_PRESENT_NOT_VERIFIED")return "LOCAL_KEY_STORE_PARTIAL_PRESERVED";
            string fingerprint;
            using(var opened=store.Open(binding,paths))
            {
                // A retried creation may reuse only the identical saved DEMO key; never overwrite.
                if(opened.Binding!=binding||opened.Provenance!="DemoTradingApi"||!opened.ApiKey.SequenceEqual(form.Values[0])||!opened.Secret.SequenceEqual(form.Values[1])||!opened.Passphrase.SequenceEqual(form.Values[2]))return "LOCAL_KEY_STORE_DIFFERENT_KEY_PRESERVED";
                fingerprint=opened.Fingerprint;
            }
            if(fingerprint!=o.Fingerprint)return "LOCAL_KEY_FINGERPRINT_MISMATCH_PRESERVED";
            string digest=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(paths.Credentials))).ToLowerInvariant();
            if(!File.Exists(Path.Combine(paths.Authority,NavalBrokerValidation.Marker)))
            {
                var premise=new NavalFiniteOperatorPremise("finite-operator-premise-v1","current-market-funding-v1",NavalCurrentMarketFunding.TestId,digest,
                    NavalCurrentMarketFunding.Statement,NavalCurrentMarketFunding.WindowsLocalProvenance,issued,issued.AddMinutes(30),"OkxDemo");
                if(!NavalCurrentMarketFunding.Premise(premise,DateTimeOffset.UtcNow))return "LOCAL_CONFIRMATION_EXPIRED_NOTHING_CREATED";
                progress("LOCAL_OWNER_BASELINE_STOP");
                string created=await CreateOwnerAsync(root,binding,store,premise,offer,fingerprint,ct).ConfigureAwait(false);
                if(created!="CREATED")return created;
            }
            progress("LOCAL_CONFIG_WRITE");
            config.Update(c=>c.OkxNaval=new(){Schema="okx-naval-config-v1",Scopes=(c.OkxNaval?.Scopes??Array.Empty<OkxScopeOptions>()).Append(option).ToArray()});
            if(!WindowsOkxProvisioning.SavedBindingMatches(config,binding))return "LOCAL_CONFIG_READBACK_REFUSED_PRESERVED";
            main.AddProvisionedBinding(binding);
            return "LOCAL_OWNER_CREATED_STOP_ATTACH_NEXT";
        }
        catch(OperationCanceledException){return "LOCAL_CREATE_CANCELLED";}
        catch(Exception e) when(e is not OutOfMemoryException){return "LOCAL_CREATE_REFUSED_PRESERVED";}
        finally{foreach(var v in form.Values)CryptographicOperations.ZeroMemory(v);}
    }
    private async Task<string> CreateOwnerAsync(string root,OkxScopeBinding binding,IOkxExecutionSecretProvider secrets,NavalFiniteOperatorPremise premise,WindowsLocalOwnerOffer offer,string fingerprint,CancellationToken ct)
    {
        // Existing finite-origin baseline: the whole EUR cash is the allocation, every
        // other balance is excluded. No nomination, no finite test trade, no START.
        await using var runtime=OkxNavalRuntime.ComposeFiniteDemo(root,binding,secrets,()=>!ct.IsCancellationRequested&&NavalCurrentMarketFunding.Premise(premise,DateTimeOffset.UtcNow));
        runtime.FiniteFundingPremise=premise;
        OkxAccountConnectionService? service=null;
        var approval=new WindowsOkxLocalBaseline(binding,fingerprint,offer.EurCash,()=>service);
        service=transports.Service(runtime,root,binding,approval);
        await using(service)
        {
            if(!await service.ConnectFiniteDemoAsync(fingerprint,ct).ConfigureAwait(false))return "LOCAL_OWNER_CONNECT_REFUSED_"+service.Status;
            if(!await service.ApproveAndCreateAsync(ct).ConfigureAwait(false))return approval.CapitalChanged?"LOCAL_CAPITAL_CHANGED_NOTHING_CREATED_RETRY":"LOCAL_OWNER_BASELINE_REFUSED_"+service.Status;
            var snapshot=service.Coordinator!.Snapshot();
            if(snapshot.Mode!="STOP"||snapshot.Cash!=offer.EurCash)return "LOCAL_OWNER_UNEXPECTED_STATE_PRESERVED";
            await service.DisconnectAsync().ConfigureAwait(false);
        }
        return "CREATED";
    }
    private static decimal? Eur(OkxEnrollmentObservation o)=>o.Balances.Where(b=>b.Currency=="EUR").Select(b=>b.Cash.Value).SingleOrDefault();

    private sealed class NativeUi:IWindowsLocalEnrollmentUi
    {
        public Task<OkxNativeForm> KeysAsync(Func<byte[][],CancellationToken,Task<string?>> submit,CancellationToken ct)=>
            WindowsOkxDialog.ShowDetailedAsync("APEX — lokalny bot: klucz OKX DEMO","Lokalny bot na tym PC • OKX EEA DEMO • SPOT EUR\nWklej klucz API utworzony w OKX → Demo Trading → API (uprawnienia: Read + Trade, BEZ Withdraw). Klucze LIVE są odrzucane. UID i salda zostaną odczytane (tylko odczyt). Nic nie zostanie zapisane przed potwierdzeniem i chronioną zgodą Windows.",
                new[]{new OkxNativeField("API Key",true),new OkxNativeField("Secret Key",true),new OkxNativeField("Passphrase",true)},ct,
                values=>values.Length==3&&values.All(v=>v.Length is >0 and <=4096&&!v.Any(b=>b<33||b>126))?null:"OKX_SECRET_INVALID",true,submit);
        public async Task<string[]?> ConfirmAsync(WindowsLocalOwnerOffer offer,CancellationToken ct)
        {
            string[] lines=NavalCurrentMarketFunding.Statement.Split('\n');
            var values=await WindowsOkxDialog.ShowAsync("APEX — potwierdź nowego lokalnego bota",offer.Text(),new[]{
                new OkxNativeField("Przepisz dokładnie: "+lines[0]),new OkxNativeField("Przepisz dokładnie: "+lines[1]),new OkxNativeField("Wpisz: "+ConfirmationWord)},ct,
                v=>v.Length==3&&Encoding.UTF8.GetString(v[0])==lines[0]&&Encoding.UTF8.GetString(v[1])==lines[1]&&Encoding.UTF8.GetString(v[2])==ConfirmationWord?null:"LOCAL_CONFIRMATION_TEXT_MISMATCH").ConfigureAwait(false);
            if(values==null)return null;
            try{return values.Select(v=>Encoding.UTF8.GetString(v)).ToArray();}
            finally{foreach(var v in values)CryptographicOperations.ZeroMemory(v);}
        }
        public Task<OkxWindowsApproval> ProtectedApprovalAsync(string message,CancellationToken ct)=>WindowsOkxApprovalProvider.ConfirmDetailedAsync(message,ct);
    }
}
// Same signed inspection as the Linux finite baseline, plus the exact EUR amount the
// operator confirmed. A changed balance refuses; nothing is reseeded or adjusted.
internal sealed class WindowsOkxLocalBaseline(OkxScopeBinding binding,string fingerprint,decimal confirmedCash,Func<OkxAccountConnectionService?> service):IOkxProtectedBaselineProvider
{
    private decimal? cash;
    internal bool CapitalChanged { get; private set; }
    public async ValueTask<OkxKeyScopeInspection?> InspectAsync(OkxScopeBinding b,string fp,CancellationToken ct)
    {
        var owner=service();if(b!=binding||fp!=fingerprint||owner?.ObservationClient==null||owner.ObservationContext==null||owner.FundingPremise==null)return null;
        var account=await owner.ObservationClient.ReadAccountAsync(owner.ObservationContext,ct).ConfigureAwait(false);
        var balances=await owner.ObservationClient.ReadBalancesAsync(owner.ObservationContext,ct).ConfigureAwait(false);
        if(account.Status!=OkxObservationStatus.Observed||balances.Status!=OkxObservationStatus.Observed||account.Rows.Count!=1||balances.Rows.Count!=1)return null;
        var a=account.Rows[0];var rows=balances.Rows[0].Details;
        var mapped=rows.Select(OkxBrokerObservationMapper.MapBalance).ToArray();
        _=await OkxCurrentMarketFunding.ReadAsync(owner.ObservationClient,owner.ObservationContext,owner.FundingPremise,fingerprint,mapped,owner.ObservationClock,ct).ConfigureAwait(false);
        if(a.Uid!=binding.Account.AccountId||a.MainUid!=binding.MainUid||a.AccountLevel!="1"||a.AccountType!="0"||a.SpotRoleType!="0"||a.Permissions!="read_only,trade"||a.AutoLoan||a.EnableSpotBorrow||a.SpotBorrowAutoRepay||a.FeeType is not ("0" or "1")||string.IsNullOrWhiteSpace(a.Ip)||
            rows.Length is <1 or >256||rows.Select(r=>r.Currency).Distinct(StringComparer.Ordinal).Count()!=rows.Length||mapped.Any(v=>!NavalCurrentMarketFunding.Balance(v,true)||v.SourceUtc>owner.ObservationClock.GetUtcNow()))return null;
        cash=rows.SingleOrDefault(r=>r.Currency==binding.Account.QuoteCurrency)?.Cash.Value;
        if(cash!=confirmedCash){CapitalChanged=true;return null;}
        return new(fp,"okx-dedicated-cash-no-funding-v1","Fresh authenticated DEMO Read+Trade account and balances; Windows-local finite-origin baseline, whole EUR cash allocated, other balances excluded. Native operator confirmation, not an external key-permission audit.",a.Ip);
    }
    public ValueTask<bool> ApproveAsync(NavalBaselineApprovalRequest r,CancellationToken ct)=>ValueTask.FromResult(!ct.IsCancellationRequested&&r.Account==binding.Account&&r.Account.Environment==NavalEnvironment.OkxDemo&&r.Cash==cash&&cash==confirmedCash&&cash>0&&r.Quote==binding.Account.QuoteCurrency&&r.ExpiresUtc>DateTimeOffset.UtcNow);
}
