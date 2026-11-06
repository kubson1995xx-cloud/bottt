using System;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Naval;
namespace Apex.Desktop.Okx;

internal sealed record OkxKeyScopeInspection(string Fingerprint,string Policy,string Evidence,string Ip);
internal interface IOkxProtectedBaselineProvider
{
    ValueTask<OkxKeyScopeInspection?> InspectAsync(OkxScopeBinding binding,string fingerprint,CancellationToken cancellation);
    ValueTask<bool> ApproveAsync(NavalBaselineApprovalRequest request,CancellationToken cancellation);
}
internal sealed class UnavailableOkxBaselineProvider : IOkxProtectedBaselineProvider
{
    public ValueTask<OkxKeyScopeInspection?> InspectAsync(OkxScopeBinding binding,string fingerprint,CancellationToken ct)=>ValueTask.FromResult<OkxKeyScopeInspection?>(null);
    public ValueTask<bool> ApproveAsync(NavalBaselineApprovalRequest request,CancellationToken ct)=>ValueTask.FromResult(false);
}
// Explicit host service; no route/config/CLI can inject approval or synthetic transports.
internal sealed class OkxAccountConnectionService : IAsyncDisposable,IOkxObservationStreamFence,INavalProtectedBaselineApproval,INavalBrokerClock
{
    private readonly OkxNavalRuntime runtime;
    private readonly string root;
    private readonly OkxScopeBinding binding;
    private readonly IOkxProtectedBaselineProvider approval;
    private readonly TimeProvider clock;
    private readonly Func<OkxExecutionScope,IOkxSigningMaterialProvider,string,IOkxObservationReadFence,OkxExecutionObservationClient> readFactory;
    private readonly Func<OkxExecutionScope,IOkxSigningMaterialProvider,string,IOkxObservationStreamFence,OkxObservationStream> streamFactory;
    private readonly Func<OkxNavalStore> storeFactory;
    private readonly SemaphoreSlim serial=new(1,1);
    private CancellationTokenSource? connection;
    private OkxExecutionObservationClient? reads;
    private readonly object credentialSync=new();
    private OkxExecutionCredentialLease? nativeConnectionCredential;
    internal bool HasNativeCredential(string fingerprint){lock(credentialSync)return OperatingSystem.IsWindows()&&connected&&!disposed&&runtime.FiniteSafe&&nativeConnectionCredential?.Binding==binding&&nativeConnectionCredential.Fingerprint==fingerprint;}
    internal string? NativeCredentialFingerprint{get{lock(credentialSync)return connected&&!disposed?nativeConnectionCredential?.Fingerprint:null;}}
    private object? lastReadDiagnostics,refusalOperands;
    internal object ReadDiagnostics()=>new{reads=(object?)reads?.ReadDiagnostics()??lastReadDiagnostics,refusal=status,operands=refusalOperands};
    private OkxObservationStream? stream;
    private Task? pumping;
    private readonly TaskCompletionSource paired=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private OkxObservationContext? context;
    private OkxExecutionScope? scope;
    private OkxNavalStore? store;
    private NavalBrokerBootstrap? bootstrap;
    private NavalBaselineCandidate? candidate;
    private NavalBrokerSession? session;
    private NavalBrokerBinding? brokerBinding;
    private NavalFiniteBaselineAuthority? FiniteBaselineAuthority()=>IsFiniteDemo&&brokerBinding!=null&&context!=null?new(brokerBinding,context.ConnectionId):null;
    private string keyScopeDigest="";
    private NavalBrokerObservationBatch? original;
    private readonly System.Collections.Concurrent.ConcurrentQueue<OkxStreamObservation> activity=new();
    private long activityVersion;
    internal long ActivityVersion=>Interlocked.Read(ref activityVersion);
    internal OkxStreamObservation[] ReadActivity()=>activity.ToArray();
    internal void AcknowledgeActivity(long sequence){while(activity.TryPeek(out var p)&&p.Sequence<=sequence)activity.TryDequeue(out _);}
    private bool openingExisting,baselineCreationAttempted;
    private volatile bool connected,disposed;
    private string status="NOT_CONNECTED";
    internal string Status=>status;
    internal NavalRiskResult RegisterConfirmedSignal(NavalEvaluationRequest request)=>session?.Coordinator.RegisterBrokerSignal(request)??new(NavalMutationStatus.Refused,"BROKER_REQUIRED",null,0);
    private OkxOperationalTradingService? trading;
    internal void RegisterTrading(OkxOperationalTradingService service){if(trading!=null)throw new InvalidOperationException("ONE_TRADING_LOOP_PER_ACCOUNT");trading=service;}
    internal void UnregisterTrading(OkxOperationalTradingService service){if(ReferenceEquals(trading,service))trading=null;}
    internal OkxScopeBinding Binding=>binding;
    internal bool IsDisposed=>disposed;
    internal bool IsConnected=>connected;
    internal NavalFiniteOperatorPremise? FundingPremise=>IsFiniteDemo?runtime.FiniteFundingPremise:null;
    internal string FundingFingerprint=>brokerBinding?.CredentialFingerprint??throw new InvalidOperationException("BROKER_BINDING_REQUIRED");
    internal TimeProvider ObservationClock=>clock;
    internal bool IsFiniteDemo=>runtime.IsFiniteDemo&&binding.Account.Environment==NavalEnvironment.OkxDemo;
    internal bool OrdinaryDemoPolicy=>(runtime.IsLinuxDemo||runtime.IsWindowsOrdinary(binding))&&!IsFiniteDemo&&binding.Account.Environment==NavalEnvironment.OkxDemo;
    internal bool ArmFiniteDemo()=>IsFiniteDemo&&runtime.FiniteSafe&&connected&&!disposed&&session!=null;
    internal NavalBrokerSession? BrokerSession=>session;
    internal OkxExecutionObservationClient? ObservationClient=>reads;
    internal OkxObservationContext? ObservationContext=>context;
    internal OkxExecutionScope? ExecutionScope=>scope;
    internal Guid TradingSessionId=>context?.ConnectionId??Guid.Empty;
    internal IOkxSigningMaterialProvider ExecutionSigningProvider()=>new SigningProvider(this,brokerBinding!.CredentialFingerprint);
    internal bool ArmExecution(OkxActivationRequest request,OkxActivationLease lease)=>connected&&!disposed&&session!=null&&request.Purpose==OkxActivationPurpose.Execute&&
        request.Session==TradingSessionId&&request.Binding==binding&&request.CredentialFingerprint==brokerBinding!.CredentialFingerprint&&runtime.AuthorizeConnection(this,request,lease);
    internal NavalPortfolioCoordinator? Coordinator=>session?.Coordinator;
    public DateTimeOffset UtcNow=>clock.GetUtcNow();
    public long Timestamp=>clock.GetTimestamp();
    public long TimestampFrequency=>clock.TimestampFrequency;
    private OkxAccountConnectionService(OkxNavalRuntime runtime,string root,OkxScopeBinding binding,IOkxProtectedBaselineProvider approval,TimeProvider clock,
        Func<OkxExecutionScope,IOkxSigningMaterialProvider,string,IOkxObservationReadFence,OkxExecutionObservationClient> reads,
        Func<OkxExecutionScope,IOkxSigningMaterialProvider,string,IOkxObservationStreamFence,OkxObservationStream> streams,Func<OkxNavalStore> storeFactory)
    {this.runtime=runtime;this.root=root;this.binding=binding;this.approval=approval;this.clock=clock;readFactory=reads;streamFactory=streams;this.storeFactory=storeFactory;runtime.RegisterAccountService(this,root,binding);}
    internal static OkxAccountConnectionService Create(OkxNavalRuntime runtime,string root,OkxScopeBinding binding)
    {
        OkxAccountConnectionService? service=null;
        IOkxProtectedBaselineProvider provider=runtime.IsWindowsOrdinary(binding)?new WindowsOkxOrdinaryApprovalProvider(()=>service):new WindowsOkxApprovalProvider(()=>service?.original);
        service=new(runtime,root,binding,provider,TimeProvider.System,OkxExecutionObservationClient.Create,OkxObservationStream.Create,()=>OkxNavalStore.OpenNative(root,binding));
        return service;
    }
    internal bool DispatchSafe=>runtime.FiniteSafe;
    internal static OkxAccountConnectionService CreateLinuxDemo(OkxNavalRuntime runtime,string root,OkxScopeBinding binding,IOkxProtectedBaselineProvider approval)
    {
        if(!runtime.IsLinuxDemo||runtime.IsFiniteDemo||binding.Account.Environment!=NavalEnvironment.OkxDemo)throw new InvalidOperationException("LINUX_ORDINARY_DEMO_ONLY");
        return new(runtime,root,binding,approval,TimeProvider.System,OkxExecutionObservationClient.Create,OkxObservationStream.Create,()=>OkxNavalStore.OpenLinuxDemo(root,binding));
    }
    internal static OkxAccountConnectionService ForTests(OkxNavalRuntime runtime,string root,OkxScopeBinding binding,IOkxProtectedBaselineProvider approval,TimeProvider clock,
        Func<OkxExecutionScope,IOkxSigningMaterialProvider,string,IOkxObservationReadFence,OkxExecutionObservationClient> reads,
        Func<OkxExecutionScope,IOkxSigningMaterialProvider,string,IOkxObservationStreamFence,OkxObservationStream> streams,Func<OkxNavalStore>? storeFactory=null)
        =>new(runtime,root,binding,approval,clock,reads,streams,storeFactory??(()=>OkxNavalStore.ForTests(root,binding)));
    internal async Task<bool> ConnectAsync(OkxActivationRequest request,OkxActivationLease lease,CancellationToken cancellation)
        =>!runtime.IsFiniteDemo&&request.Purpose==OkxActivationPurpose.Observe&&await ConnectCoreAsync(request,lease,false,cancellation).ConfigureAwait(false);
    internal static OkxAccountConnectionService CreateFiniteDemo(OkxNavalRuntime runtime,string root,OkxScopeBinding binding,IOkxProtectedBaselineProvider approval)
    {
        if(!runtime.IsFiniteDemo||binding.Account.Environment!=NavalEnvironment.OkxDemo)throw new InvalidOperationException("FINITE_DEMO_ONLY");
        return new(runtime,root,binding,approval,TimeProvider.System,OkxExecutionObservationClient.Create,OkxObservationStream.Create,()=>OkxNavalStore.OpenLinuxFiniteDemo(root,binding));
    }
    internal Task<bool> ConnectFiniteDemoAsync(string fingerprint,CancellationToken ct)
        =>ConnectCoreAsync(new(binding,fingerprint,Guid.NewGuid(),0,OkxActivationPurpose.Observe,DateTimeOffset.UtcNow.AddSeconds(120)),null,true,ct);
    private async Task<bool> ConnectCoreAsync(OkxActivationRequest request,OkxActivationLease? lease,bool finite,CancellationToken cancellation)
    {
        await serial.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if(disposed||connection!=null||request.Binding!=binding){status="CONNECTION_ACTION_DENIED";return false;}
            connected=true;
            if(finite? !IsFiniteDemo : lease==null||!runtime.AuthorizeConnection(this,request,lease)){connected=false;status="ACTIVATION_REQUIRED";return false;}
            connection=new();context=new(Guid.NewGuid(),Guid.NewGuid());
            if(OperatingSystem.IsWindows()){var admittedCredential=runtime.OpenCredential(binding,request.CredentialFingerprint);lock(credentialSync)nativeConnectionCredential=admittedCredential;}
            scope=new(binding.Account.Environment==NavalEnvironment.OkxDemo?"Demo":"Live",binding.Account.AccountId,binding.MainUid,binding.Account.QuoteCurrency,binding.CredentialBindingId);
            var provider=new SigningProvider(this,request.CredentialFingerprint);
            reads=readFactory(scope,provider,request.CredentialFingerprint,this);stream=streamFactory(scope,provider,request.CredentialFingerprint,this);
            openingExisting=File.Exists(Path.Combine(OkxConfiguration.Paths(root,binding).Authority,NavalBrokerValidation.Marker));
            pumping=Pump();
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellation,connection.Token);
            await paired.Task.WaitAsync(TimeSpan.FromSeconds(20),linked.Token).ConfigureAwait(false);
            if(openingExisting)
            {
                var read=await reads.ReadAccountAsync(context,linked.Token).ConfigureAwait(false);
                if(read.Status!=OkxObservationStatus.Observed||read.Rows.Count!=1)throw new InvalidDataException("EXISTING_ACCOUNT_UNPROVEN");
                var actual=read.Rows[0];var keyInspection=await approval.InspectAsync(binding,request.CredentialFingerprint,linked.Token).ConfigureAwait(false);
                if(actual.Uid!=binding.Account.AccountId||actual.MainUid!=binding.MainUid||actual.AccountLevel!="1"||actual.AccountType!="0"||actual.SpotRoleType!="0"||actual.AutoLoan||actual.EnableSpotBorrow||actual.SpotBorrowAutoRepay||actual.Permissions!="read_only,trade"||
                    keyInspection==null||keyInspection.Fingerprint!=request.CredentialFingerprint||!(keyInspection.Policy=="okx-dedicated-cash-no-funding-v1"||OrdinaryDemoPolicy&&keyInspection.Policy=="okx-ordinary-demo-current-market-v1")||string.IsNullOrWhiteSpace(keyInspection.Evidence)||keyInspection.Ip!=actual.Ip||!connected)throw new InvalidDataException("EXISTING_ACCOUNT_SCOPE_UNPROVEN");
                store=storeFactory();brokerBinding=new(binding.Account,binding.MainUid,binding.ScopeId,binding.ConfigDigest,binding.CredentialBindingId,request.CredentialFingerprint,binding.SafetyVersion,store.StorageIdentity);
                session=NavalBrokerSession.OpenExisting(store,brokerBinding,this);openingExisting=false;
                if(!connected){session.Dispose();session=null;throw new InvalidOperationException("OBSERVATION_GAP");}
                status="CONNECTED_STOP_RECONCILIATION_REQUIRED";return true;
            }
            if(runtime.IsWindowsOrdinary(binding))throw new InvalidDataException("LOCAL_EXISTING_OWNER_REQUIRED_NO_BASELINE");
            if(IsFiniteDemo){store=storeFactory();brokerBinding=new(binding.Account,binding.MainUid,binding.ScopeId,binding.ConfigDigest,binding.CredentialBindingId,request.CredentialFingerprint,binding.SafetyVersion,store.StorageIdentity);}
            var observations=await OkxBrokerObservationMapper.CollectAsync(reads,context,binding,clock,stream.ReadState,linked.Token,FiniteBaselineAuthority(),runtime.FiniteNomination,runtime.FiniteFundingPremise).ConfigureAwait(false);
            original=observations;
            if(!connected)throw new InvalidOperationException("OBSERVATION_GAP");
            var inspection=await approval.InspectAsync(binding,request.CredentialFingerprint,linked.Token).ConfigureAwait(false);
            if(inspection==null||inspection.Fingerprint!=request.CredentialFingerprint||!(inspection.Policy=="okx-dedicated-cash-no-funding-v1"||OrdinaryDemoPolicy&&inspection.Policy=="okx-ordinary-demo-current-market-v1")||
                string.IsNullOrWhiteSpace(inspection.Evidence)||inspection.Ip!=observations.Account.Ip)
            {
                connected=false;bootstrap?.Revoke();runtime.Activation.Revoke();
                await DrainTransportsAsync().ConfigureAwait(false);
                status="PERMISSION_SCOPE_UNPROVEN";return false;
            }
            if(store==null){store=storeFactory();brokerBinding=new(binding.Account,binding.MainUid,binding.ScopeId,binding.ConfigDigest,binding.CredentialBindingId,request.CredentialFingerprint,binding.SafetyVersion,store.StorageIdentity);}
            bootstrap=new(brokerBinding!,this,this,FiniteBaselineAuthority());
            // Existing state is never interpreted as a candidate for a new allocation.
            if(File.Exists(Path.Combine(store.DirectoryPath,NavalBrokerValidation.Marker)))
            {session=NavalBrokerSession.OpenExisting(store,brokerBinding,this);status="CONNECTED_STOP";return true;}
            keyScopeDigest=NavalJson.Hash(inspection);
            try{candidate=bootstrap.ConsiderBaseline(observations,keyScopeDigest);}
            catch(InvalidOperationException e) when(e.Message=="BASELINE_EXPIRED"&&observations.ServerUtc<=UtcNow&&observations.Balances.All(b=>b.SourceUtc<=UtcNow)&&
                UtcNow>=observations.CaptureUtc&&Timestamp>=observations.CaptureTimestamp&&
                (UtcNow-observations.CaptureUtc>=TimeSpan.FromSeconds(30)||(decimal)(Timestamp-observations.CaptureTimestamp)/TimestampFrequency>=30))
            {candidate=null;} // Only old valid captures may wait for the separate fresh-baseline action.
            status="BASELINE_APPROVAL_REQUIRED";return true;
        }
        catch(Exception ex) when(SafeFailure(ex))
        {connected=false;bootstrap?.Revoke();if(ex is OkxFiniteAccountRefusal r)refusalOperands=r.Operands;await DrainTransportsAsync().ConfigureAwait(false);status=ex is OkxFiniteAccountRefusal refusal?refusal.Code:"CONNECTION_REFUSED";return false;}
        finally{serial.Release();}
    }
    private async Task Pump()
    {
        try
        {
            await foreach(var item in stream!.ObserveAsync(context!,connection!.Token).ConfigureAwait(false))
            {
                if(item.Kind==OkxStreamObservationKind.Buffering)paired.TrySetResult();
                if(item.Kind is OkxStreamObservationKind.OrdersUpdate or OkxStreamObservationKind.AlgoUpdate)
                {
                    if(session==null&&!openingExisting){OnGap(scope!,context!,item.Epoch,"BaselineActivityChanged");continue;}
                    activity.Enqueue(item);Interlocked.Increment(ref activityVersion);
                    if(activity.Count>512)OnGap(scope!,context!,item.Epoch,"ActivityBufferOverflow");
                }
            }
        }
        finally{if(connected)OnGap(scope!,context!,Guid.Empty,"Closed");}
    }
    public bool CanObserve(OkxExecutionScope requested,OkxObservationContext capture,DateTimeOffset utc)=>connected&&!disposed&&runtime.FiniteSafe&&scope==requested&&context==capture;
    public void OnGap(OkxExecutionScope requested,OkxObservationContext capture,Guid epoch,string reason)
    {
        connected=false;status="OBSERVATION_GAP";bootstrap?.Revoke();runtime.Activation.Revoke();session?.Disconnect();connection?.Cancel();
    }
    public ValueTask<bool> ApproveAsync(NavalBaselineApprovalRequest request,CancellationToken ct)=>approval.ApproveAsync(request,ct);
    internal async Task<bool> ApproveAndCreateAsync(CancellationToken cancellation,bool refresh=false)
    {
        await serial.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if(!connected||disposed||bootstrap==null||(!refresh&&candidate==null)||store==null||session!=null||baselineCreationAttempted)return false;
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellation,connection!.Token);
            if(refresh)
            {
                // New capture/digest, never rewritten old clocks. Existing allocation is never reseeded.
                bootstrap.Revoke();candidate=null;
                original=await OkxBrokerObservationMapper.CollectAsync(reads!,context!,binding,clock,stream!.ReadState,linked.Token,FiniteBaselineAuthority(),runtime.FiniteNomination,runtime.FiniteFundingPremise).ConfigureAwait(false);
                bootstrap=new(brokerBinding!,this,this,FiniteBaselineAuthority());candidate=bootstrap.ConsiderBaseline(original,keyScopeDigest);
            }
            var before=stream!.ReadState();var consent=await bootstrap.RequestApprovalAsync(candidate!,linked.Token).ConfigureAwait(false);
            if(consent!=null)await OkxBrokerObservationMapper.VerifyUnchangedAsync(reads!,context!,original!,linked.Token,clock).ConfigureAwait(false);
            var after=stream.ReadState();
            if(consent==null||!connected||before.Epoch!=after.Epoch||before.Sequence!=after.Sequence||after.HasGap)return false;
            baselineCreationAttempted=true;
            session=bootstrap.InitializeBaseline(candidate!,consent,store);
            if(!connected){session.Dispose();session=null;return false;}
            status="CONNECTED_STOP";candidate=null;return true;
        }
        catch(Exception ex) when(SafeFailure(ex)){if(ex is OkxFiniteAccountRefusal r)refusalOperands=r.Operands;status=ex is OkxFiniteAccountRefusal refusal?refusal.Code:"BASELINE_REFUSED_PRESERVED";bootstrap?.Revoke();return false;}
        finally{serial.Release();}
    }
    internal bool OpenExisting()
    {
        serial.Wait();
        try
        {
            if(disposed||connected||session!=null)return false;
            store=storeFactory();
            if(brokerBinding==null)
            {
                store.Admit(false);
                var raw=store.Get(NavalBrokerValidation.StateKey)??throw NavalBrokerValidation.Invalid();
                using var doc=System.Text.Json.JsonDocument.Parse(raw);NavalBrokerRaw.Require(doc.RootElement);
                var saved=System.Text.Json.JsonSerializer.Deserialize<Apex.Core.Engine.EngineState>(raw)!.NavalBroker!.Baseline.Binding;
                brokerBinding=new(binding.Account,binding.MainUid,binding.ScopeId,binding.ConfigDigest,binding.CredentialBindingId,saved.CredentialFingerprint,binding.SafetyVersion,store.StorageIdentity);
            }
            session=NavalBrokerSession.OpenExisting(store,brokerBinding,this);status="REOPENED_STOP";return true;
        }
        catch(Exception ex) when(SafeFailure(ex)){status="REOPEN_REFUSED_PRESERVED";store?.Dispose();store=null;return false;}
        finally{serial.Release();}
    }
    internal void RevokePermissions(){bootstrap?.Revoke();runtime.Activation.Revoke();trading?.Stop();session?.Disconnect();}
    internal object ReadAccountStatus()
    {
        var observed=original;
        return new { authenticated=observed!=null&&connected,uid=observed?.Account.Uid,mainUid=observed?.Account.MainUid,
            quote=binding.Account.QuoteCurrency,cash=observed?.Balances.FirstOrDefault(b=>b.Currency==binding.Account.QuoteCurrency)?.Cash?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            instruments=observed?.Catalog.Select(i=>i.Instrument).ToArray()??Array.Empty<string>(),baselineApprovalRequired=status=="BASELINE_APPROVAL_REQUIRED" };
    }
    internal async ValueTask DisconnectAsync()
    {
        // Revoke before waiting for the serialized owner lane or adapter drains.
        connected=false;bootstrap?.Revoke();runtime.Activation.Revoke();connection?.Cancel();
        var loop=trading;if(loop!=null)await loop.DisposeAsync().ConfigureAwait(false);
        await serial.WaitAsync().ConfigureAwait(false);
        try
        {
            await DrainTransportsAsync().ConfigureAwait(false);
            session?.Disconnect();session?.Dispose();session=null;store?.Dispose();store=null;
            status="DISCONNECTED_STOP";
        }
        finally{serial.Release();}
    }
    public async ValueTask DisposeAsync(){if(disposed)return;disposed=true;await DisconnectAsync().ConfigureAwait(false);connection?.Dispose();}
    private async Task DrainTransportsAsync()
    {
        connection?.Cancel();
        if(reads!=null){lastReadDiagnostics=reads.ReadDiagnostics();await reads.DisposeAsync().ConfigureAwait(false);}
        if(stream!=null)await stream.DisposeAsync().ConfigureAwait(false);
        if(pumping!=null)await pumping.ConfigureAwait(false);
        reads=null;stream=null;pumping=null;
        lock(credentialSync){nativeConnectionCredential?.Dispose();nativeConnectionCredential=null;}
    }
    private static bool SafeFailure(Exception ex)=>ex is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException or Apex.Desktop.Services.WindowsDataException;
    private sealed class SigningProvider(OkxAccountConnectionService owner,string fingerprint) : IOkxSigningMaterialProvider
    {
        public ValueTask<OkxSigningMaterial> OpenAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();if(!owner.connected||owner.disposed)throw new InvalidOperationException("CONNECTION_REVOKED");
            if(OperatingSystem.IsWindows())lock(owner.credentialSync)
            {
                if(!owner.HasNativeCredential(fingerprint))throw new InvalidOperationException("CONNECTION_REVOKED");
                return ValueTask.FromResult(owner.nativeConnectionCredential!.OpenSigningMaterial(owner.scope!));
            }
            using var credential=owner.runtime.OpenCredential(owner.binding,fingerprint);
            return ValueTask.FromResult(credential.OpenSigningMaterial(owner.scope!));
        }
    }
}
