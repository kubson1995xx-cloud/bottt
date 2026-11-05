using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Engine;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
namespace Apex.Okx.DemoAcceptance;

// No production path, transport, environment or scripted command overrides.
internal sealed partial class LinuxDemoHost
{
    // An unproved drain is terminal for this process; never let GC release its lock
    // or secret owner while a transport might still hold a request.
    private static LinuxDemoHost? undrained;
    private readonly string root;
    private readonly LinuxDemoCredentialVault vault;
    private readonly Func<CancellationToken,ValueTask<string?>> read;
    private readonly TextWriter output;
    private readonly Func<EphemeralCredentials,CancellationToken,Task<OkxEnrollmentDiscovery>> discover;
    private readonly FiniteEntryTestComposition? transport; // wire-only friend seam; never finite authority
    private readonly Func<bool> safe;
    private readonly object gate=new();
    private readonly CancellationTokenSource lifetime;
    private CancellationTokenSource? operation,scheduling;
    private Task? scheduler;
    private long epoch;
    private string? action;
    private FileStream? processLock;
    private EphemeralCredentials? credentials;
    private OkxNavalRuntime? runtime;
    private OkxAccountConnectionService? account;
    private OkxOperationalTradingService? loop;
    private OkxScopeBinding? binding;
    private LinuxApproval? approval;
    // Explicitly configured only by production entry or the isolated advisory friend.
    // Other friend fixtures cannot accidentally read the real public-research root.
    private Func<string,OkxAdvisoryStatus> advisory=instrument=>OkxAdvisoryComparison.Unavailable("NOT_CONFIGURED") with{Instrument=instrument};
    private LinuxDemoHost(string root,LinuxDemoCredentialVault vault,Func<CancellationToken,ValueTask<string?>> read,TextWriter output,
        Func<EphemeralCredentials,CancellationToken,Task<OkxEnrollmentDiscovery>> discover,FiniteEntryTestComposition? transport,Func<bool> safe,CancellationToken ct)
    {this.root=root;this.vault=vault;this.read=read;this.output=output;this.discover=discover;this.transport=transport;this.safe=safe;lifetime=CancellationTokenSource.CreateLinkedTokenSource(ct);}
    internal static async Task<int> RunAsync()
    {
        try
        {
            LinuxInput.Harden();using var terminal=new LinuxCommandInput();
            var host=new LinuxDemoHost(Program.Root,new LinuxDemoCredentialVault(),terminal.ReadAsync,Console.Out,(k,ct)=>k.Discover(ct),null,()=>terminal.IsCurrent&&LinuxInput.SafeForPrivateDispatch(),default);
            host.advisory=instrument=>OkxAdvisoryReader.ReadProductionForInstrument(instrument,DateTimeOffset.UtcNow);
            return await host.RunCoreAsync().ConfigureAwait(false);
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException){Console.Error.WriteLine("LOCAL_INTERACTIVE_LINUX_TTY_REQUIRED_NO_CREDENTIALS_OPENED");return 2;}
    }
    internal static Task<int> RunForTestsAsync(string root,LinuxDemoCredentialVault vault,TextReader input,TextWriter output,
        Func<EphemeralCredentials,CancellationToken,Task<OkxEnrollmentDiscovery>> discover,FiniteEntryTestComposition? transport,CancellationToken ct)
        =>new LinuxDemoHost(root,vault,ct=>new ValueTask<string?>(Task.Run(()=>input.ReadLineAsync(ct).AsTask(),ct).WaitAsync(ct)),output,discover,transport,LinuxInput.SafeForPrivateDispatch,ct).RunCoreAsync();
    internal static Task<int> RunAdvisoryForTestsAsync(string reportsRoot,TextReader input,TextWriter output,DateTimeOffset now,CancellationToken ct)
    {
        if(!OkxAdvisoryReader.TestRootAllowed(reportsRoot))
        {output.WriteLine("ADVISORY_TEST_ROOT_REFUSED");return Task.FromResult(2);}
        async ValueTask<string?> ReadAdvisory(CancellationToken token)
        {
            while(true)
            {
                string? line=await input.ReadLineAsync(token).ConfigureAwait(false);
                if(line is null or "STATUS" or "STOP" or "EMERGENCY" or "DISCONNECT" or "QUIT"||AdvisoryInstrument(line)!=null)return line;
                lock(output){output.WriteLine("ADVISORY_TEST_COMMAND_REFUSED");}
            }
        }
        var host=new LinuxDemoHost(reportsRoot,null!,ReadAdvisory,output,
            (_,_)=>throw new InvalidOperationException("ADVISORY_TEST_DISCOVERY_FORBIDDEN"),null,()=>true,ct);
        host.advisory=instrument=>OkxAdvisoryReader.ReadInstrumentForTests(reportsRoot,instrument,now);
        return host.RunCoreAsync();
    }
    private static string? AdvisoryInstrument(string command)=>command switch
    {
        "ADVISORY-STATUS" or "ADVISORY-STATUS SOL-EUR"=>"SOL-EUR",
        "ADVISORY-STATUS BTC-EUR"=>"BTC-EUR",
        "ADVISORY-STATUS ETH-EUR"=>"ETH-EUR",
        _=>null
    };
    private void Say(string code){lock(output){output.WriteLine(code);output.Flush();}}
    private bool Current=>!lifetime.IsCancellationRequested&&safe();
    private void Revoke()
    {
        lock(gate)
        {
            Interlocked.Increment(ref epoch);action=null;operation?.Cancel();scheduling?.Cancel();runtime?.Activation.Revoke();loop?.Stop();
        }
    }
    private async Task DrainScheduler()
    {
        Task? task;lock(gate){scheduling?.Cancel();task=scheduler;}
        if(task!=null)try{await task.ConfigureAwait(false);}catch(OperationCanceledException){}
        lock(gate){scheduler=null;scheduling?.Dispose();scheduling=null;}
    }
    private async Task CloseConnection()
    {
        Revoke();await DrainScheduler().ConfigureAwait(false);
        if(loop!=null)await loop.DisposeAsync().ConfigureAwait(false);loop=null;
        if(account!=null)await account.DisposeAsync().ConfigureAwait(false);account=null;
        if(runtime!=null)await runtime.DisposeAsync().ConfigureAwait(false);runtime=null;
        credentials?.Dispose();credentials=null;binding=null;approval=null;
    }
    private async Task<int> RunCoreAsync()
    {
        if(undrained!=null){Say("DRAIN_UNPROVEN_PROCESS_RESTART_REQUIRED");return 7;}
        var commands=Channel.CreateBounded<(string Command,long Epoch)>(8);
        var signals=new List<PosixSignalRegistration>();Task? reader=null;
        using var cancelled=lifetime.Token.Register(Revoke);int exit=0;
        try
        {
            foreach(var signal in new[]{PosixSignal.SIGINT,PosixSignal.SIGTERM,PosixSignal.SIGHUP,PosixSignal.SIGQUIT,PosixSignal.SIGTSTP})
                signals.Add(PosixSignalRegistration.Create(signal,c=>{c.Cancel=true;Revoke();lifetime.Cancel();}));
            Say("STOP_DISCONNECTED_UNARMED");
            Say("LOCAL DEMO: CONNECT; DIAGNOSE observes order difference names/classes; DIAGNOSE-PENDING reads three pending algo filters with bounded failure metadata; both require STOP/unarmed and make no financial changes. PREPARE explicitly transitions the same settled historical owner with pinned SOL dust in STOP; BASELINE only for a new dedicated clean allocation; ARM; separate START. STOP does not liquidate. SNAPSHOT projects bounded already observed controller data without refreshing it; STATUS; ADVISORY-STATUS [BTC-EUR|ETH-EUR|SOL-EUR] reads fixed per-instrument public research reports only while STOP/unarmed (default SOL-EUR; SHADOW_ONLY; no strategy activation). DISCONNECT; QUIT. LIVE disabled. No transfer/borrow/withdraw routes.");
            reader=Task.Run(async()=>
            {
                try
                {
                    while(!lifetime.IsCancellationRequested)
                    {
                        var command=await read(lifetime.Token).ConfigureAwait(false);
                        if(command is null){Revoke();lifetime.Cancel();break;}
                        if(command is "STOP" or "EMERGENCY" or "DISCONNECT" or "QUIT")Revoke();
                        if(!commands.Writer.TryWrite((command,controllerCapability!=null?Interlocked.Read(ref controllerCommandEpoch):Interlocked.Read(ref epoch)))){Revoke();lifetime.Cancel();break;}
                        if(command=="QUIT")break;
                    }
                }
                catch(Exception e) when(e is IOException or OperationCanceledException){Revoke();lifetime.Cancel();}
                finally{commands.Writer.TryComplete();}
            });
            await foreach(var item in commands.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                if(item.Command=="QUIT")break;
                if(item.Command is "STOP" or "EMERGENCY")
                {if(item.Command=="EMERGENCY")loop?.EmergencyStop();await DrainScheduler();Say("STOP_DRAINED_UNARMED");continue;}
                if(item.Command=="DISCONNECT"){await CloseConnection();Say("STOP_DISCONNECTED_UNARMED");continue;}
                if(item.Command=="STATUS"){Status();continue;}
                if(item.Command=="SNAPSHOT")
                {
                    try{Say(OkxControllerSnapshotCodec.Encode(loop?.ReadControllerSnapshot()??OkxControllerSnapshotCodec.Disconnected(DateTimeOffset.UtcNow)));}
                    catch(InvalidDataException){Say("CONTROLLER_SNAPSHOT_UNAVAILABLE");}
                    continue;
                }
                if(AdvisoryInstrument(item.Command) is {} instrument)
                {
                    var status=loop?.ReadStatus();
                    if(scheduler!=null||status!=null&&(status.Armed||status.Mode!="STOP"))
                        Say("ADVISORY_REFUSED_STOP_UNARMED_REQUIRED");
                    else Say("ADVISORY_STATUS "+JsonSerializer.Serialize(advisory(instrument)));
                    continue;
                }
                if(item.Epoch!=Interlocked.Read(ref epoch)){Say("COMMAND_REVOKED");continue;}
                lock(gate){operation=new CancellationTokenSource();action=item.Command;}
                using var linked=CancellationTokenSource.CreateLinkedTokenSource(operation.Token,lifetime.Token);
                try
                {
                    if(!Current)throw new OperationCanceledException();
                    switch(item.Command)
                    {
                        case "CONNECT":await Connect(linked.Token);break;
                        case "DIAGNOSE":
                            if(loop==null||scheduler!=null){Say("DIAGNOSE_REFUSED_STOP_UNARMED_ORDINARY_REQUIRED");break;}
                            var diagnosis=await loop.DiagnoseOrderDifferencesAsync(linked.Token);
                            foreach(var disagreement in diagnosis.Disagreements)
                                Say("ORDER_DIFFERENCE "+JsonSerializer.Serialize(new{LeftOrigin=disagreement.LeftOrigin.ToString(),RightOrigin=disagreement.RightOrigin.ToString(),disagreement.Difference}));
                            Say(diagnosis.Code+" truncated="+diagnosis.Truncated);break;
                        case "DIAGNOSE-PENDING":
                            if(loop==null||scheduler!=null){Say("PENDING_DIAGNOSE_REFUSED_STOP_UNARMED_ORDINARY_REQUIRED");break;}
                            var pendingDiagnosis=await loop.DiagnosePendingAlgosAsync(linked.Token);
                            foreach(var failure in pendingDiagnosis.Failures)
                                Say("PENDING_ALGO_FAILURE "+JsonSerializer.Serialize(new{Filter=failure.Filter.ToString(),failure.ErrorCode,failure.Diagnostic}));
                            Say(pendingDiagnosis.Code+" failures="+pendingDiagnosis.Failures.Count);break;
                        case "BASELINE":
                            if(account==null||!await account.ApproveAndCreateAsync(linked.Token,refresh:true)){Say("BASELINE_REFUSED_PRESERVED");break;}
                            CreateLoop();Say("CONNECTED_STOP");break;
                        case "PREPARE":
                            if(loop==null){Say("PREPARE_REFUSED_NO_OWNER");break;}
                            var prepared=await loop.PrepareOrdinaryDemoAsync(linked.Token);Say(prepared.Code);break;
                        case "ARM":
                            if(loop==null||account==null||runtime==null){Say("ARM_REFUSED");break;}
                            var request=Request(OkxActivationPurpose.Execute,account.TradingSessionId);
                            var lease=await runtime.Activation.RequestAsync(request,linked.Token);
                            if(lease==null||!loop.Arm(request,lease)){Say("ARM_REFUSED");break;}
                            await loop.ReconcileAsync(linked.Token);Say(loop.ReadStatus().Armed?"ARMED_STOP":"ARM_REFUSED");break;
                        case "START":
                            if(loop==null||!loop.ReadStatus().Armed||scheduler!=null||loop.Start().Status!=NavalMutationStatus.Applied){Say("START_REFUSED");break;}
                            lock(gate)
                            {
                                if(item.Epoch!=epoch||!Current){loop.Stop();Say("START_REFUSED");break;}
                                scheduling=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);scheduler=loop.RunAsync(scheduling.Token);
                            }
                            Say("SCHEDULER_STARTED");break;
                        default:Say("COMMAND_REFUSED");break;
                    }
                }
                catch(DemoVaultException){Say("LOCAL_VAULT_UNAVAILABLE_NO_REENROLLMENT_NO_TRADING");await CloseConnection();}
                catch(OperationCanceledException){Revoke();await DrainScheduler();Say("ACTION_CANCELLED_STOP");}
                catch(HostRefusal e){Say(e.Code);await CloseConnection();}
                catch(Exception e) when(e is IOException or InvalidOperationException or ArgumentException or JsonException)
                {Say("HOST_ACTION_REFUSED_PRESERVED_STOP");await CloseConnection();}
                finally{lock(gate){action=null;operation?.Dispose();operation=null;}}
            }
        }
        catch(OperationCanceledException){}
        catch(Exception e) when(e is IOException or InvalidOperationException){Say("HOST_REFUSED_PRESERVED_STOP");exit=7;}
        finally
        {
            Revoke();lifetime.Cancel();bool drained=false;
            try{await CloseConnection().ConfigureAwait(false);drained=true;}
            catch(Exception e) when(e is not OutOfMemoryException)
            {undrained=this;exit=7;Say("DRAIN_UNPROVEN_LOCK_RETAINED_UNTIL_PROCESS_EXIT");}
            finally
            {
                foreach(var signal in signals)signal.Dispose();
                if(reader!=null)try{await reader.ConfigureAwait(false);}catch(OperationCanceledException){}
                if(drained){processLock?.Dispose();Say("HOST_DRAINED_CREDENTIALS_CLOSED");}
                lifetime.Dispose();
            }
        }
        return exit;
    }
    private void Status()
    {
        if(loop==null){Say("STOP_DISCONNECTED_UNARMED");return;}
        var s=loop.ReadStatus();var state=account!.BrokerSession!.ReadState();
        Say($"STATUS mode={s.Mode} connected={s.Connected} armed={s.Armed} ready={s.Ready} flat={state.Naval!.Reservations.All(r=>r.Inventory==0&&r.Pending==0)} fills={state.Naval.Fills.Count} holds={s.Held>0} obligations={s.Obligations.Count} evaluations={s.Evaluation.Completed} wait={s.Evaluation.Wait} entry={s.Evaluation.Entry} exit={s.Evaluation.Exit} evaluation={s.Evaluation.Code}");
    }
    private OkxActivationRequest Request(OkxActivationPurpose purpose,Guid session)=>new(binding!,credentials!.Fingerprint,session,Interlocked.Read(ref epoch),purpose,DateTimeOffset.UtcNow.AddSeconds(120));
    private void CreateLoop()
    {loop=transport==null?OkxOperationalTradingService.Create(account!):OkxOperationalTradingService.Create(account!,transport.Clock,transport.Writes,transport.Market);}
    private async Task Connect(CancellationToken ct)
    {
        if(account!=null){Say("ALREADY_CONNECTED_OR_DISCONNECT_REQUIRED");return;}
        if(processLock==null)
        {
            OkxLinuxPrivate.AdmitDirectory(root,true);Program.AdmitTree(root);
            string path=Path.Combine(root,".run.lock");if(File.Exists(path))OkxLinuxPrivate.AdmitFile(path);
            try{processLock=new(path,new FileStreamOptions{Mode=FileMode.OpenOrCreate,Access=FileAccess.ReadWrite,Share=FileShare.None,UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite});}
            catch(IOException){throw new HostRefusal("RUN_LOCK_BUSY_NO_CHANGES");}
            OkxLinuxPrivate.AdmitFile(path);
        }
        var saved=ExistingOwner(); // finite/malformed/overlapping allocation refuses BEFORE decrypt/discovery
        if(controllerCapability!=null&&saved==null)throw new HostRefusal("CONTROLLER_EXISTING_OWNER_REQUIRED");
        using(var bundle=await vault.LoadAsync(ct).ConfigureAwait(false))credentials=bundle.TransferTo((k,s,p)=>new EphemeralCredentials(k,s,p));
        var result=await discover(credentials,ct).ConfigureAwait(false);ct.ThrowIfCancellationRequested();
        var observed=result.Observation??throw new HostRefusal("AUTHENTICATED_DISCOVERY_REFUSED_NO_OWNER");
        if(observed.Environment!="Demo"||observed.Provenance!="DemoTradingApi"||observed.Fingerprint!=credentials.Fingerprint||observed.Uid!=observed.MainUid||observed.ObservedUtc>DateTimeOffset.UtcNow||DateTimeOffset.UtcNow-observed.ObservedUtc>=TimeSpan.FromSeconds(35))throw new HostRefusal("DEMO_PROVENANCE_IDENTITY_OR_FRESHNESS_REFUSED");
        string quote;
        if(saved==null)
        {
            var balances=observed.Balances.Select(OkxBrokerObservationMapper.MapBalance).ToArray();
            bool nullable=balances.Any(b=>new decimal?[]{b.Cash,b.Available,b.Equity,b.Frozen,b.OrderFrozen,b.Liability,b.Interest,b.CrossLiability,b.IsolatedLiability,b.StrategyEquity,b.FixedBalance}.Any(v=>v==null));
            if(balances.Length!=1||nullable)throw new HostRefusal($"ORDINARY_DEDICATED_SINGLE_QUOTE_REQUIRED rows={Math.Min(257,balances.Length)} nullable={nullable}; NO_BASELINE_NO_MIGRATION");
            var q=balances[0];quote=q.Currency;
            if(!NavalBrokerValidation.CleanFiniteBalance(q)||q.Cash is not >0)throw new HostRefusal("ORDINARY_CLEAN_CASH_BALANCE_REQUIRED_NO_BASELINE");
        }
        else quote=saved.Account.QuoteCurrency;
        binding=saved??OkxConfiguration.Bind(new OkxNavalOptions{Schema="okx-naval-config-v1",Scopes=new[]{new OkxScopeOptions{ScopeId="linux-demo-bot",Environment="OkxDemo",Region="EEA",ExpectedUid=observed.Uid,ExpectedMainUid=observed.MainUid,QuoteCurrency=quote,CredentialBindingId="native-demo-vault",SafetyVersion="okx-cash-v1"}}})[0];
        if(saved!=null&&saved!=binding)throw new HostRefusal("EXISTING_ORDINARY_BINDING_MISMATCH_PRESERVED");
        var ownerSecrets=new BoundSecrets(credentials,binding,root);
        approval=new(this);
        runtime=OkxNavalRuntime.ComposeLinuxDemo(root,binding,ownerSecrets,approval,()=>Current);
        account=transport==null?OkxAccountConnectionService.CreateLinuxDemo(runtime,root,binding,approval):OkxAccountConnectionService.ForTests(runtime,root,binding,approval,transport.Clock,transport.Reads,transport.Streams,()=>OkxNavalStore.OpenLinuxDemo(root,binding));
        var request=Request(OkxActivationPurpose.Observe,Guid.NewGuid());var lease=await runtime.Activation.RequestAsync(request,ct);
        if(lease==null||!await account.ConnectAsync(request,lease,ct).ConfigureAwait(false))throw new HostRefusal("ORDINARY_ACCOUNT_ADMISSION_REFUSED_PRESERVED status="+account.Status);
        if(account.BrokerSession!=null){CreateLoop();Say("CONNECTED_STOP");}else Say("BASELINE_APPROVAL_REQUIRED");
    }
    private OkxScopeBinding? ExistingOwner()
    {
        var targets=Directory.EnumerateFiles(root,OkxNavalStore.Target,SearchOption.AllDirectories).ToArray();
        var markers=Directory.EnumerateFiles(root,NavalBrokerValidation.Marker,SearchOption.AllDirectories).ToArray();
        if(targets.Length==0&&markers.Length==0)return null;
        if(targets.Length!=1||markers.Length!=1||Path.GetDirectoryName(targets[0])!=Path.GetDirectoryName(markers[0]))throw new HostRefusal("EXISTING_FINANCIAL_STATE_UNCLASSIFIED_PRESERVED");
        var bytes=OkxLinuxPrivate.ReadFile(targets[0]);var state=JsonSerializer.Deserialize<EngineState>(bytes);
        var b=state?.NavalBroker?.Baseline;
        if(b==null)throw new HostRefusal("EXISTING_FINANCIAL_STATE_UNCLASSIFIED_PRESERVED");
        using(var document=JsonDocument.Parse(bytes))NavalBrokerRaw.Require(document.RootElement);
        NavalBrokerValidation.Validate(state!,NavalBrokerValidation.Definition(b));
        var stored=b.Binding;
        var candidate=new OkxScopeBinding(stored.ScopeId,stored.Account,stored.MainUid,stored.CredentialBindingId,stored.SafetyVersion,stored.ConfigDigest);
        OkxConfiguration.ValidateBinding(candidate);
        if(!(candidate.ScopeId=="linux-demo-bot"&&candidate.CredentialBindingId=="native-demo-vault"||candidate.ScopeId=="finite-demo"&&candidate.CredentialBindingId=="foreground-demo"&&b.Observations.FiniteAllocation!=null)||candidate.Account.Environment!=NavalEnvironment.OkxDemo||targets[0]!=Path.Combine(OkxConfiguration.Paths(root,candidate).Authority,OkxNavalStore.Target))throw new HostRefusal("EXISTING_OTHER_OWNER_PRESERVED_NO_NEW_ALLOCATION");
        return candidate;
    }
    private sealed class BoundSecrets(EphemeralCredentials credentials,OkxScopeBinding binding,string root):IOkxExecutionSecretProvider
    {
        public OkxExecutionCredentialLease Open(OkxScopeBinding b,OkxScopePaths paths)
        {if(b!=binding||b.Account.Environment!=NavalEnvironment.OkxDemo||paths!=OkxConfiguration.Paths(root,binding))throw new InvalidOperationException("LINUX_DEMO_CREDENTIAL_BINDING_REFUSED");return credentials.Open(b,paths);}
    }
    private sealed class LinuxApproval(LinuxDemoHost host):IOkxProtectedActivationProvider,IOkxProtectedBaselineProvider
    {
        private decimal? cash;
        public ValueTask<bool> ConfirmAsync(OkxActivationRequest r,CancellationToken ct)=>ValueTask.FromResult(!ct.IsCancellationRequested&&host.Current&&r.Binding==host.binding&&r.CredentialFingerprint==host.credentials?.Fingerprint&&r.Generation==Interlocked.Read(ref host.epoch)&&
            (r.Purpose==OkxActivationPurpose.Observe&&host.action=="CONNECT"||r.Purpose==OkxActivationPurpose.Execute&&host.action=="ARM"&&r.Session==host.account?.TradingSessionId));
        public async ValueTask<OkxKeyScopeInspection?> InspectAsync(OkxScopeBinding b,string fp,CancellationToken ct)
        {
            var service=host.account;if(!host.Current||b!=host.binding||fp!=host.credentials?.Fingerprint||service?.ObservationClient==null)return null;
            var a=await service.ObservationClient.ReadAccountAsync(service.ObservationContext!,ct);
            var balances=await service.ObservationClient.ReadBalancesAsync(service.ObservationContext!,ct);
            if(a.Status!=OkxObservationStatus.Observed||a.Rows.Count!=1||balances.Status!=OkxObservationStatus.Observed||balances.Rows.Count!=1)return null;
            var row=a.Rows[0];var mapped=balances.Rows[0].Details.Select(OkxBrokerObservationMapper.MapBalance).ToArray();
            if(row.Uid!=b.Account.AccountId||row.MainUid!=b.MainUid||row.Permissions!="read_only,trade"||row.AccountLevel!="1"||row.AccountType!="0"||row.SpotRoleType!="0"||row.AutoLoan||row.EnableSpotBorrow||row.SpotBorrowAutoRepay||row.FeeType is not ("0" or "1")||string.IsNullOrWhiteSpace(row.Ip)||
                mapped.Length is <1 or >256||mapped.Select(v=>v.Currency).Distinct(StringComparer.Ordinal).Count()!=mapped.Length||mapped.Any(v=>!NavalCurrentMarketFunding.RawDebtCompatible(v)||v.StrategyEquity!=0||v.FixedBalance!=0||v.SourceUtc==null||v.SourceUtc>service.ObservationClock.GetUtcNow()))return null;
            _=await OkxCurrentMarketFunding.ReadOrdinaryAsync(service.ObservationClient,service.ObservationContext!,fp,mapped,service.ObservationClock,ct);
            cash=mapped.SingleOrDefault(v=>v.Currency==b.Account.QuoteCurrency)?.Cash;
            return new(fp,"okx-ordinary-demo-current-market-v1","Fresh signed DEMO Read+Trade/no Withdraw, no borrowing/funding; closed cash-only transport. Local baseline approval is separate from ARM and START.",row.Ip);
        }
        public ValueTask<bool> ApproveAsync(NavalBaselineApprovalRequest r,CancellationToken ct)=>ValueTask.FromResult(!ct.IsCancellationRequested&&host.Current&&host.action=="BASELINE"&&r.Account==host.binding?.Account&&r.Account.Environment==NavalEnvironment.OkxDemo&&r.Session==host.account?.TradingSessionId&&r.Cash==cash&&cash>0&&r.Quote==host.binding.Account.QuoteCurrency&&r.ExpiresUtc>DateTimeOffset.UtcNow);
    }
    private sealed class HostRefusal(string code):IOException{internal string Code=>code;}
}
