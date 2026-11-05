using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
namespace Apex.Okx.DemoAcceptance;

internal static class Program
{
    internal const string Root="/home/ubuntu/apex-private/okx-demo-acceptance";
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    public static Task<int> Main(string[] args)
    {
        if(args.Length==0){Console.WriteLine("APEX OKX DEMO STOP_DISCONNECTED_UNARMED. --demo-bot: explicit local ordinary host; --finite-demo: separate bounded acceptance entry. --help for existing management/recovery commands. No credentials opened; no network.");return Task.FromResult(0);}
        if(args.SequenceEqual(new[]{"--help"}))Console.WriteLine("--demo-bot: ordinary Linux DEMO host, passive STOP until interactive CONNECT, separate BASELINE/ARM/START. STOP/EOF/signals drain; no LIVE or daemon. No arguments: passive status only. --finite-demo: legacy bounded acceptance, never the ordinary scheduler.");
        if(args.SequenceEqual(new[]{"--controller-server"}))return LinuxControllerServer.RunProductionAsync();
        if(args.SequenceEqual(new[]{"--controller-relay"}))return LinuxControllerRelay.RunAsync();
        if(args.SequenceEqual(new[]{"--demo-bot"}))return LinuxDemoHost.RunAsync();
        return RunAsync(args.SequenceEqual(new[]{"--finite-demo"})?Array.Empty<string>():args,Root,new LinuxDemoCredentialVault(),(owner,ct)=>owner.Discover(ct));
    }

    // Friend-only composition. Production Main has no path/transport override channel.
    internal static Task<int> RunAsync(string[] args,string root,LinuxDemoCredentialVault vault,Func<EphemeralCredentials,CancellationToken,Task<OkxEnrollmentDiscovery>> discover)=>RunCoreAsync(args,root,vault,discover,null);
    internal static Task<int> RunWithObligationFactoryAsync(string[] args,string root,LinuxDemoCredentialVault vault,Func<EphemeralCredentials,CancellationToken,Task<OkxEnrollmentDiscovery>> discover,
        Func<OkxExecutionScope,IOkxSigningMaterialProvider,string,IOkxObservationReadFence,OkxExecutionObservationClient> factory)=>RunCoreAsync(args,root,vault,discover,factory);
    internal static Task<int> RunFiniteForTestsAsync(string[] args,string root,LinuxDemoCredentialVault vault,Func<EphemeralCredentials,CancellationToken,Task<OkxEnrollmentDiscovery>> discover,FiniteEntryTestComposition composition)=>RunCoreAsync(args,root,vault,discover,null,composition);
    private static async Task<int> RunCoreAsync(string[] args,string root,LinuxDemoCredentialVault vault,Func<EphemeralCredentials,CancellationToken,Task<OkxEnrollmentDiscovery>> discover,
        Func<OkxExecutionScope,IOkxSigningMaterialProvider,string,IOkxObservationReadFence,OkxExecutionObservationClient>? obligationFactory,FiniteEntryTestComposition? test=null)
    {
        if(args.SequenceEqual(new[]{"--help"}))
        {Console.WriteLine("APEX finite OKX EEA DEMO. Recovery: --recover-owned-exit is an explicit once-only same-owner STOP cash reduction: retrospective exact own order/protection metadata observation, terminal full parent/fill/fee/bill/funding/balance reconciliation, at most one exact own protection cancel followed by terminal observation, one bounded own-net FOK sell, then fee/balance/dust STOP. No new BUY/START/baseline/premise renewal/LIVE/scheduler/retry. Unknown advanced configuration or child refuses without sell; cancellation can leave PROTECTION_GAP and final dust is unprotected. Separate owned-reduction-reports; independent material review required before actual invocation. --diagnose-owned-exit is a separate explicit GET-only retrospective same-owner attachment diagnostic using the already approved owned-recovery parser profile. It persists STOP before vault decryption, never binds trading or constructs a writer/reduction capability, retains strict rejection and only the first rejected nested object per exact endpoint, and publishes separate run-scoped owned-exit-diagnostic-reports/private identifiers. No schema allowance, financial clearance, cancel or sell. --recover-diagnose reopens only the existing owner in STOP for fresh signed config, exact original-client/known-fill-order lookups and independently allocated protection-client lookups; GET-only, separate order-protection-reports and bounded private schema identifiers, no cancel/sell or financial clearance. --recover-stop reopens the exact existing sealed finite-demo owner in durable STOP, loads the native vault under the shared lock, performs fresh signed original-ID lookup and at most one exact-own cancellation, then reconciles observations while retaining unproven holds. --recover-observe is the same existing-owner GET-only path, with no cancel. Original last-report is preserved; separate private recovery-reports. Expired entry plans do not block cleanup and are never renewed. --save-credentials: exactly three hidden SSH TTY fields, native encrypted local storage, NO_NETWORK/NO_TRADING. --replace-credentials: deliberate hidden replacement. --credentials-status: local stored/notstored/unavailable, not broker validation. --forget-credentials: remove owned ciphertext only; not secure erase or OKX revocation. --finite-demo: reuse stored credentials without TTY for the already authorized single bounded post-only/cancel and FOK buy/own-net-sell (cap 10 actual EUR/USDC), then STOP. Existing financial state refuses BEFORE decrypt/private calls. --check-connection: stored credentials, only four discovery GETs, no financial owner/orders; separate private connection-reports. --check-obligations: stored credentials without TTY, signed discovery then account/balance bookends around exact Market-loan GET type=2 without currency filter; separate private obligation-reports, MARKET_LOANS_ONLY, all-account clearance NOT_ESTABLISHED, STOP/no owner/no orders. No scheduler or LIVE. Storage is not trading authority. --provision-funding-premise: explicit parent JSON metadata on stdin, current encrypted record binding, shared lock, NO_DECRYPT/NO_NETWORK/NO_TRADING; finite entry requires that unexpired private receipt before decrypt. --identity is public; --offline-input-check/--offline-input-error are network/state-free hidden input tests. At-rest encryption does not protect against root/same-UID/full-host compromise or native/framework copies. Finite private report: "+Root+"/last-report.json.");return 0;}
        if(args.SequenceEqual(new[]{"--identity"}))
        {Console.WriteLine(JsonSerializer.Serialize(BinaryIdentity(),Json));return 0;}
        if(args.SequenceEqual(new[]{"--recover-owned-exit"}))return await FiniteStopRecovery.RunOwnedAsync(root,vault,test).ConfigureAwait(false);
        if(args.SequenceEqual(new[]{"--diagnose-owned-exit"}))return await FiniteStopRecovery.RunOwnedDiagnosticAsync(root,vault,test).ConfigureAwait(false);
        if(args.SequenceEqual(new[]{"--recover-diagnose"}))return await FiniteStopRecovery.RunAsync(root,vault,test,false,true).ConfigureAwait(false);
        if(args.SequenceEqual(new[]{"--recover-stop"})||args.SequenceEqual(new[]{"--recover-observe"}))return await FiniteStopRecovery.RunAsync(root,vault,test,args[0]=="--recover-stop").ConfigureAwait(false);
        bool Is(string value)=>args.SequenceEqual(new[]{value});
        bool inputCheck=Is("--offline-input-check"),inputError=Is("--offline-input-error"),save=Is("--save-credentials"),replace=Is("--replace-credentials"),status=Is("--credentials-status"),forget=Is("--forget-credentials"),check=Is("--check-connection"),obligations=Is("--check-obligations"),provision=Is("--provision-funding-premise");
        if(args.Length!=0&&!inputCheck&&!inputError&&!save&&!replace&&!status&&!forget&&!check&&!obligations&&!provision){Console.Error.WriteLine("ARGUMENTS_REFUSED");return 2;}
        bool inputOnly=inputCheck||inputError,management=save||replace||status||forget;
        using var cancellation=new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var signals=new List<PosixSignalRegistration>();
        EphemeralCredentials? credentials=null;OkxNavalRuntime? runtime=null;OkxAccountConnectionService? account=null;OkxOperationalTradingService? loop=null;FileStream? runLock=null;
        string runId=Guid.NewGuid().ToString("N"),result="NOT_STARTED";DateTimeOffset started=DateTimeOffset.UtcNow;var stages=new List<string>();object? discoveryDiagnostic=null,selectionDiagnostic=null;bool reportEnabled=false,complete=false;
        object? obligationObservation=null;
        string reportPath=obligations?Path.Combine(root,"obligation-reports",runId+".json"):check?Path.Combine(root,"connection-reports",runId+".json"):Path.Combine(root,"last-report.json");
        byte[]? key=null,secret=null,pass=null;
        void Publish(string stage)
        {
            if(!reportEnabled)return;
            stages.Add(stage);
            var state=account?.BrokerSession?.ReadState();
            byte[] bytes=obligations?MarketObligationsObservation.Report(runId,started,complete,result,stages.ToArray(),discoveryDiagnostic,obligationObservation):TransactionReport.Build(runId,started,complete,result,stages.ToArray(),state,discoveryDiagnostic,loop?.FiniteDiagnostics()??account?.ReadDiagnostics(),loop?.ReadStatus().Armed??false,selectionDiagnostic,check?"CHECK_CONNECTION_GET_ONLY":"FINITE_DEMO_SEQUENCE");
            try{if(credentials?.ContainsSecret(bytes)==true)throw new IOException("OUTPUT_PRIVACY_REFUSED");OkxLinuxPrivate.Publish(reportPath,bytes);}
            finally{CryptographicOperations.ZeroMemory(bytes);}
        }
        try
        {
            LinuxInput.Harden();
            foreach(var signal in new[]{PosixSignal.SIGINT,PosixSignal.SIGTERM,PosixSignal.SIGHUP,PosixSignal.SIGQUIT,PosixSignal.SIGTSTP})signals.Add(PosixSignalRegistration.Create(signal,ctx=>{ctx.Cancel=true;cancellation.Cancel();}));
            if(!inputOnly)
            {
                OkxLinuxPrivate.AdmitDirectory(root,true);
                AdmitTree(root);
                string lockPath=Path.Combine(root,".run.lock");
                if(File.Exists(lockPath))OkxLinuxPrivate.AdmitFile(lockPath);
                try{runLock=new(lockPath,new FileStreamOptions{Mode=FileMode.OpenOrCreate,Access=FileAccess.ReadWrite,Share=FileShare.None,UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite});}
                catch(IOException){Console.Error.WriteLine("RUN_LOCK_BUSY_NO_CHANGES");return 9;}
                OkxLinuxPrivate.AdmitFile(lockPath);
                // Management never constructs an account, transport or financial owner.
                if(status)
                {
                    var local=await vault.StatusAsync(cancellation.Token).ConfigureAwait(false);
                    Console.WriteLine(JsonSerializer.Serialize(new{State=local.State.ToString(),local.Schema,local.Provenance},Json));Console.WriteLine("LOCAL_STORAGE_ONLY_NO_NETWORK_NO_TRADING_NOT_BROKER_VALIDATION");
                    return local.State==DemoVaultState.Unavailable?8:0;
                }
                if(forget)
                {bool removed=await vault.ForgetAsync(cancellation.Token).ConfigureAwait(false);Console.WriteLine(removed?"DEMO_CREDENTIALS_REMOVED_NO_NETWORK_NO_TRADING_NOT_SECURE_ERASE_OR_OKX_REVOCATION":"DEMO_CREDENTIALS_NOT_STORED_NO_NETWORK_NO_TRADING");return 0;}
                // Saved material never resets single-use/Unknown state. Check is GET-only
                // and may diagnose while preserving every existing financial file.
                if(!management&&!check&&!obligations&&(Directory.EnumerateFiles(root,"naval_authority_broker_v2.json",SearchOption.AllDirectories).Any()||Directory.EnumerateFiles(root,".naval-broker-initialized-v2",SearchOption.AllDirectories).Any()))
                {Console.Error.WriteLine("EXISTING_FINANCIAL_STATE_PRESERVED_NO_NEW_BUY; review private last-report.json");return 3;}
            }
            if(inputOnly||save||replace)
            {
                Console.WriteLine("DEMO Trading API only: Read + Trade, no Withdraw. Exactly three hidden SSH TTY fields; Enter confirms, Ctrl+C cancels. Never enter secrets in chat, argv, environment or plaintext files. This entry operation makes NO_NETWORK/NO_TRADING.");
                using(var tty=new LinuxInput(cancellation.Token)){key=tty.Read("API Key: ");secret=tty.Read("Secret Key: ");pass=tty.Read("Passphrase: ");}
                if(save||replace)
                {
                    if(replace)await vault.ReplaceAsync(key,secret,pass,cancellation.Token).ConfigureAwait(false);
                    else await vault.SaveNewAsync(key,secret,pass,cancellation.Token).ConfigureAwait(false);
                    var local=await vault.StatusAsync(cancellation.Token).ConfigureAwait(false);
                    if(local.State!=DemoVaultState.Stored)throw new DemoVaultException(DemoVaultError.CommitUncertain);
                    Console.WriteLine("DEMO_CREDENTIALS_STORED_NO_NETWORK_NO_TRADING");return 0;
                }
                credentials=new(key,secret,pass);key=null;secret=null;pass=null;
                if(inputCheck){Console.WriteLine("OFFLINE_INPUT_ACCEPTED_NO_NETWORK_NO_FILES");return 0;}
                throw new IOException("SYNTHETIC_PRIVATE_MESSAGE_MUST_NOT_ESCAPE");
            }
            NavalFiniteOperatorPremise? premise=null;
            if(provision)
            {
                await FiniteOperatorReceipt.ProvisionAsync(root,vault,Console.OpenStandardInput(),cancellation.Token).ConfigureAwait(false);
                _=await FiniteOperatorReceipt.LoadAsync(root,vault,cancellation.Token).ConfigureAwait(false);
                Console.WriteLine("FINITE_OPERATOR_PREMISE_STORED_NO_DECRYPT_NO_NETWORK_NO_TRADING");return 0;
            }
            if(!check&&!obligations)premise=await FiniteOperatorReceipt.LoadAsync(root,vault,cancellation.Token).ConfigureAwait(false);
            using(var bundle=await vault.LoadAsync(cancellation.Token).ConfigureAwait(false))
                credentials=bundle.TransferTo((k,s,p)=>new EphemeralCredentials(k,s,p));
            if(check||obligations)OkxLinuxPrivate.AdmitDirectory(Path.GetDirectoryName(reportPath)!,true);
            reportEnabled=true;
            Publish("STORED_CREDENTIALS_LOADED_NO_PRIVATE_CALLS_YET");
            cancellation.Token.ThrowIfCancellationRequested();
            var discovery=await discover(credentials,cancellation.Token).ConfigureAwait(false);discoveryDiagnostic=new{discovery.Code,discovery.Diagnostic};
            if(obligations)
            {
                if(discovery.Observation is not {} observation){result="OBLIGATION_DISCOVERY_REFUSED_STOP_NO_TRADING";complete=true;Publish(result);return 4;}
                string? obligationQuote=new[]{"EUR","USDC"}.FirstOrDefault(q=>observation.Balances.Any(b=>b.Currency==q&&b.Available.Value>0));
                if(obligationQuote is null){result="OBLIGATION_READ_SCOPE_UNAVAILABLE";complete=true;Publish(result);return 4;}
                var readScope=new OkxExecutionScope("Demo",observation.Uid,observation.MainUid,obligationQuote,"foreground-demo");
                obligationObservation=await MarketObligationsObservation.CollectAsync(readScope,credentials.ForObservation(readScope),credentials.Fingerprint,
                    ()=>!cancellation.IsCancellationRequested&&LinuxInput.SafeForPrivateDispatch(),cancellation.Token,
                    d=>{obligationObservation=d;Publish("GET_ONLY_OBSERVATION_CHECKPOINT");},obligationFactory).ConfigureAwait(false);
                result=JsonSerializer.SerializeToElement(obligationObservation).GetProperty("result").GetString()!;
                complete=true;Publish("GET_ONLY_OBSERVATION_FINISHED_NO_OWNER_NO_ORDERS");Console.WriteLine(result+"; private report: "+reportPath);
                return result=="MARKET_LOANS_OBSERVED_STOP_NO_TRADING"?0:4;
            }
            if(check)
            {
                result=discovery.Observation is null?"READ_ONLY_DISCOVERY_REFUSED_STOP_NO_TRADING":"READ_ONLY_DISCOVERY_COMPLETED_STOP_NO_TRADING";
                complete=true;Publish("READ_ONLY_DISCOVERY_FINISHED_NO_OWNER_NO_ORDERS");
                Console.WriteLine(result+"; private report: "+reportPath);return discovery.Observation is null?4:0;
            }
            if(discovery.Observation is not {} observed){result="DISCOVERY_REFUSED";complete=true;Publish(result);return 4;}
            string? readQuote=new[]{"EUR","USDC"}.FirstOrDefault(q=>observed.Balances.Any(b=>b.Currency==q&&b.Available.Value>0));
            if(readQuote==null)throw new OkxFiniteAccountRefusal("FINITE_NO_FUNDED_EUR_OR_USDC");
            // Temporary GET-only scope is not capital allocation or execution authority.
            var selectionScope=new OkxExecutionScope("Demo",observed.Uid,observed.MainUid,readQuote,"foreground-demo");object? selectionReads=null;OkxFiniteSelectionDiagnostic? selectionBoundary=null;
            var nominated=await OkxFiniteAccountSelection.ObserveFundedAsync(selectionScope,credentials.ForObservation(selectionScope),credentials.Fingerprint,
                ()=>!cancellation.IsCancellationRequested&&LinuxInput.SafeForPrivateDispatch(),cancellation.Token,premise!,d=>{selectionReads=d;selectionDiagnostic=new{reads=d,boundary=selectionBoundary};},test?.Clock,test?.Reads,test?.Market,d=>selectionBoundary=d).ConfigureAwait(false);
            selectionDiagnostic=new{nomination=nominated,reads=selectionReads,boundary=selectionBoundary};string quote=nominated.Quote;
            var binding=OkxConfiguration.Bind(new OkxNavalOptions{Schema="okx-naval-config-v1",Scopes=new[]{new OkxScopeOptions{ScopeId="finite-demo",Environment="OkxDemo",Region="EEA",ExpectedUid=observed.Uid,ExpectedMainUid=observed.MainUid,QuoteCurrency=quote,CredentialBindingId="foreground-demo",SafetyVersion="okx-cash-v1"}}})[0];
            runtime=OkxNavalRuntime.ComposeFiniteDemo(root,binding,credentials,()=>!cancellation.IsCancellationRequested&&LinuxInput.SafeForPrivateDispatch()&&NavalCurrentMarketFunding.Premise(premise,DateTimeOffset.UtcNow),nominated);
            runtime.FiniteFundingPremise=premise;
            var approval=new SignedFiniteBaseline(binding,credentials.Fingerprint,()=>account);
            account=test==null?OkxAccountConnectionService.CreateFiniteDemo(runtime,root,binding,approval):OkxAccountConnectionService.ForTests(runtime,root,binding,approval,test.Clock,test.Reads,test.Streams,()=>OkxNavalStore.OpenLinuxFiniteDemo(root,binding));
            if(!await account.ConnectFiniteDemoAsync(credentials.Fingerprint,cancellation.Token).ConfigureAwait(false)){result="AUTHENTICATED_PREFLIGHT_REFUSED:"+account.Status;complete=true;Publish(result);return 4;}
            if(!await account.ApproveAndCreateAsync(cancellation.Token,refresh:true).ConfigureAwait(false)){result="FRESH_BASELINE_REFUSED:"+account.Status;complete=true;Publish(result);return 4;}
            loop=test==null?OkxOperationalTradingService.Create(account):OkxOperationalTradingService.Create(account,test.Clock,test.Writes,test.Market);loop.BindFiniteSafety(()=>!cancellation.IsCancellationRequested&&LinuxInput.SafeForPrivateDispatch());
            result=await OkxFiniteDemoSequence.RunAsync(loop,account,runId,Publish,cancellation.Token).ConfigureAwait(false);
            complete=true;Publish("FINAL_REPORT");Console.WriteLine("STOP. Private report: "+reportPath+"; result="+result);
            return result.StartsWith("COMPLETED_STOP_",StringComparison.Ordinal)?0:5;
        }
        catch(DemoVaultException e)
        {
            result=e.Message;
            if(e.Code==DemoVaultError.NotStored)Console.Error.WriteLine("CREDENTIALS_NOT_STORED; run --save-credentials in direct interactive SSH; NO_NETWORK_NO_TRADING");
            if(e.Code==DemoVaultError.CommitUncertain)Console.Error.WriteLine("COMMIT_UNCERTAIN_NO_BLIND_RETRY; inspect --credentials-status under the same run lock");
            return e.Code==DemoVaultError.Cancelled?6:8;
        }
        catch(OkxFiniteAccountRefusal e){result=e.Code;selectionDiagnostic=new{previous=selectionDiagnostic,refusal=e.Code,operands=e.Operands};return 4;}
        catch(OperationCanceledException){result="CANCELLED_OR_BUDGET_EXPIRED_STOP_CHECK_REPORT";return 6;}
        catch(Exception){result="SAFE_REFUSAL_STOP_CHECK_REPORT";return 7;}
        finally
        {
            try
            {
                loop?.Stop();complete=true;
                // Publish while the exact owner and diagnostics still exist, before drains.
                Publish("PROCESS_FINALLY_STOP");
                if(loop!=null)await loop.DisposeAsync().ConfigureAwait(false);
                if(account!=null)await account.DisposeAsync().ConfigureAwait(false);
                if(runtime!=null)await runtime.DisposeAsync().ConfigureAwait(false);
            }
            catch{Console.Error.WriteLine("SANITIZED_REPORT_OR_DRAIN_REFUSED_PRESERVE_AUTHORITY");}
            finally
            {
                credentials?.Dispose();if(key!=null)CryptographicOperations.ZeroMemory(key);if(secret!=null)CryptographicOperations.ZeroMemory(secret);if(pass!=null)CryptographicOperations.ZeroMemory(pass);
                runLock?.Dispose();foreach(var signal in signals)signal.Dispose();
                if(result!="NOT_STARTED"&&!result.StartsWith("COMPLETED_STOP_",StringComparison.Ordinal)&&!inputCheck)Console.Error.WriteLine("STOP / "+result);
            }
        }
    }
    internal static void AdmitTree(string root)
    {
        var queue=new Queue<string>();queue.Enqueue(root);int count=0;
        while(queue.Count>0)
        {
            string path=queue.Dequeue();OkxLinuxPrivate.AdmitDirectory(path,false);
            foreach(string entry in Directory.EnumerateFileSystemEntries(path))
            {if(++count>256)throw new IOException("PRIVATE_TREE_CAP");if(Directory.Exists(entry)){OkxLinuxPrivate.AdmitDirectory(entry,false);queue.Enqueue(entry);}else OkxLinuxPrivate.AdmitFile(entry);}
        }
    }
    internal static object BinaryIdentity()=>new{runtime=RuntimeInformation.FrameworkDescription,assemblies=new[]{typeof(Program).Assembly,typeof(OkxOperationalTradingService).Assembly,typeof(NavalBrokerBaseline).Assembly,typeof(OkxExecutionClient).Assembly}.Select(a=>new{name=a.GetName().Name,version=a.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute),false).Cast<System.Reflection.AssemblyInformationalVersionAttribute>().SingleOrDefault()?.InformationalVersion,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location))).ToLowerInvariant()}).ToArray()};
}

internal sealed class SignedFiniteBaseline(OkxScopeBinding binding,string fingerprint,Func<OkxAccountConnectionService?> service) : IOkxProtectedBaselineProvider
{
    private decimal? cash;
    public async ValueTask<OkxKeyScopeInspection?> InspectAsync(OkxScopeBinding b,string fp,CancellationToken ct)
    {
        var owner=service();if(b!=binding||fp!=fingerprint||owner?.ObservationClient==null||owner.ObservationContext==null)return null;
        var account=await owner.ObservationClient.ReadAccountAsync(owner.ObservationContext,ct).ConfigureAwait(false);
        var balances=await owner.ObservationClient.ReadBalancesAsync(owner.ObservationContext,ct).ConfigureAwait(false);
        if(account.Status!=OkxObservationStatus.Observed||balances.Status!=OkxObservationStatus.Observed||account.Rows.Count!=1||balances.Rows.Count!=1)return null;
        var a=account.Rows[0];var rows=balances.Rows[0].Details;
        var mapped=rows.Select(OkxBrokerObservationMapper.MapBalance).ToArray();
        bool market=owner.FundingPremise!=null;
        if(market)_=await OkxCurrentMarketFunding.ReadAsync(owner.ObservationClient,owner.ObservationContext,owner.FundingPremise!,fingerprint,mapped,owner.ObservationClock,ct).ConfigureAwait(false);
        if(a.Uid!=binding.Account.AccountId||a.MainUid!=binding.MainUid||a.AccountLevel!="1"||a.AccountType!="0"||a.SpotRoleType!="0"||a.Permissions!="read_only,trade"||a.AutoLoan||a.EnableSpotBorrow||a.SpotBorrowAutoRepay||a.FeeType is not ("0" or "1")||string.IsNullOrWhiteSpace(a.Ip)||
            rows.Length is <1 or >256||rows.Select(r=>r.Currency).Distinct(StringComparer.Ordinal).Count()!=rows.Length||rows.Select(OkxBrokerObservationMapper.MapBalance).Any(b=>!NavalCurrentMarketFunding.Balance(b,market)||b.SourceUtc>owner.ObservationClock.GetUtcNow()))return null;
        cash=rows.SingleOrDefault(r=>r.Currency==binding.Account.QuoteCurrency)?.Cash.Value;
        return new(fp,"okx-dedicated-cash-no-funding-v1","Fresh authenticated Read+Trade account and balances; finite DEMO process has closed cash order/cancel transport, no funding/withdraw/borrow routes. Not Windows approval or an external key-permission audit.",a.Ip);
    }
    public ValueTask<bool> ApproveAsync(NavalBaselineApprovalRequest r,CancellationToken ct)=>ValueTask.FromResult(!ct.IsCancellationRequested&&r.Account==binding.Account&&r.Account.Environment==NavalEnvironment.OkxDemo&&r.Cash==cash&&cash>0&&r.Quote==binding.Account.QuoteCurrency&&r.ExpiresUtc>DateTimeOffset.UtcNow);
}
