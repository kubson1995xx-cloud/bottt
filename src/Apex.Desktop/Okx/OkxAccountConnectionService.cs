using System;
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
    private NavalBrokerObservationBatch? original;
    private volatile bool connected,disposed;
    private string status="NOT_CONNECTED";
    internal string Status=>status;
    internal bool IsConnected=>connected;
    internal NavalPortfolioCoordinator? Coordinator=>session?.Coordinator;
    public DateTimeOffset UtcNow=>clock.GetUtcNow();
    public long Timestamp=>clock.GetTimestamp();
    public long TimestampFrequency=>clock.TimestampFrequency;
    private OkxAccountConnectionService(OkxNavalRuntime runtime,string root,OkxScopeBinding binding,IOkxProtectedBaselineProvider approval,TimeProvider clock,
        Func<OkxExecutionScope,IOkxSigningMaterialProvider,string,IOkxObservationReadFence,OkxExecutionObservationClient> reads,
        Func<OkxExecutionScope,IOkxSigningMaterialProvider,string,IOkxObservationStreamFence,OkxObservationStream> streams,Func<OkxNavalStore> storeFactory)
    {this.runtime=runtime;this.root=root;this.binding=binding;this.approval=approval;this.clock=clock;readFactory=reads;streamFactory=streams;this.storeFactory=storeFactory;runtime.RegisterAccountService(this,root,binding);}
    internal static OkxAccountConnectionService Create(OkxNavalRuntime runtime,string root,OkxScopeBinding binding)=>new(runtime,root,binding,new UnavailableOkxBaselineProvider(),TimeProvider.System,
        OkxExecutionObservationClient.Create,OkxObservationStream.Create,()=>OkxNavalStore.OpenNative(root,binding));
    internal static OkxAccountConnectionService ForTests(OkxNavalRuntime runtime,string root,OkxScopeBinding binding,IOkxProtectedBaselineProvider approval,TimeProvider clock,
        Func<OkxExecutionScope,IOkxSigningMaterialProvider,string,IOkxObservationReadFence,OkxExecutionObservationClient> reads,
        Func<OkxExecutionScope,IOkxSigningMaterialProvider,string,IOkxObservationStreamFence,OkxObservationStream> streams,Func<OkxNavalStore>? storeFactory=null)
        =>new(runtime,root,binding,approval,clock,reads,streams,storeFactory??(()=>OkxNavalStore.ForTests(root,binding)));
    internal async Task<bool> ConnectAsync(OkxActivationRequest request,OkxActivationLease lease,CancellationToken cancellation)
    {
        await serial.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if(disposed||connection!=null||request.Binding!=binding){status="CONNECTION_ACTION_DENIED";return false;}
            connected=true;
            if(!runtime.AuthorizeConnection(this,request,lease)){connected=false;status="ACTIVATION_REQUIRED";return false;}
            connection=new();context=new(Guid.NewGuid(),Guid.NewGuid());
            scope=new(binding.Account.Environment==NavalEnvironment.OkxDemo?"Demo":"Live",binding.Account.AccountId,binding.MainUid,binding.Account.QuoteCurrency,binding.CredentialBindingId);
            var provider=new SigningProvider(this,request.CredentialFingerprint);
            reads=readFactory(scope,provider,request.CredentialFingerprint,this);stream=streamFactory(scope,provider,request.CredentialFingerprint,this);
            pumping=Pump();
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellation,connection.Token);
            await paired.Task.WaitAsync(TimeSpan.FromSeconds(20),linked.Token).ConfigureAwait(false);
            var observations=await OkxBrokerObservationMapper.CollectAsync(reads,context,binding,clock,stream.ReadState,linked.Token).ConfigureAwait(false);
            original=observations;
            if(!connected)throw new InvalidOperationException("OBSERVATION_GAP");
            var inspection=await approval.InspectAsync(binding,request.CredentialFingerprint,linked.Token).ConfigureAwait(false);
            if(inspection==null||inspection.Fingerprint!=request.CredentialFingerprint||inspection.Policy!="okx-dedicated-cash-no-funding-v1"||
                string.IsNullOrWhiteSpace(inspection.Evidence)||inspection.Ip!=observations.Account.Ip){status="PERMISSION_SCOPE_UNPROVEN";return false;}
            store=storeFactory();brokerBinding=new(binding.Account,binding.MainUid,binding.ScopeId,binding.ConfigDigest,binding.CredentialBindingId,request.CredentialFingerprint,binding.SafetyVersion,store.StorageIdentity);
            bootstrap=new(brokerBinding,this,this);
            // Existing state is never interpreted as a candidate for a new allocation.
            if(File.Exists(Path.Combine(store.DirectoryPath,NavalBrokerValidation.Marker)))
            {session=NavalBrokerSession.OpenExisting(store,brokerBinding,this);status="CONNECTED_STOP";return true;}
            candidate=bootstrap.ConsiderBaseline(observations,NavalJson.Hash(inspection));
            status="BASELINE_APPROVAL_REQUIRED";return true;
        }
        catch(Exception ex) when(SafeFailure(ex))
        {connected=false;bootstrap?.Revoke();await DrainTransportsAsync().ConfigureAwait(false);status="CONNECTION_REFUSED";return false;}
        finally{serial.Release();}
    }
    private async Task Pump()
    {
        try
        {
            await foreach(var item in stream!.ObserveAsync(context!,connection!.Token).ConfigureAwait(false))
            {
                if(item.Kind==OkxStreamObservationKind.Buffering)paired.TrySetResult();
                if(item.Kind is OkxStreamObservationKind.OrdersUpdate or OkxStreamObservationKind.AlgoUpdate)OnGap(scope!,context!,item.Epoch,"ActivityChanged");
            }
        }
        finally{if(connected)OnGap(scope!,context!,Guid.Empty,"Closed");}
    }
    public bool CanObserve(OkxExecutionScope requested,OkxObservationContext capture,DateTimeOffset utc)=>connected&&!disposed&&scope==requested&&context==capture;
    public void OnGap(OkxExecutionScope requested,OkxObservationContext capture,Guid epoch,string reason)
    {
        connected=false;status="OBSERVATION_GAP";bootstrap?.Revoke();runtime.Activation.Revoke();session?.Disconnect();connection?.Cancel();
    }
    public ValueTask<bool> ApproveAsync(NavalBaselineApprovalRequest request,CancellationToken ct)=>approval.ApproveAsync(request,ct);
    internal async Task<bool> ApproveAndCreateAsync(CancellationToken cancellation)
    {
        await serial.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if(!connected||disposed||bootstrap==null||candidate==null||store==null||session!=null)return false;
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellation,connection!.Token);
            var before=stream!.ReadState();var consent=await bootstrap.RequestApprovalAsync(candidate,linked.Token).ConfigureAwait(false);
            if(consent!=null)await OkxBrokerObservationMapper.VerifyUnchangedAsync(reads!,context!,original!,linked.Token).ConfigureAwait(false);
            var after=stream.ReadState();
            if(consent==null||!connected||before.Epoch!=after.Epoch||before.Sequence!=after.Sequence||after.HasGap)return false;
            session=bootstrap.InitializeBaseline(candidate,consent,store);
            if(!connected){session.Dispose();session=null;return false;}
            status="CONNECTED_STOP";candidate=null;return true;
        }
        catch(Exception ex) when(SafeFailure(ex)){status="BASELINE_REFUSED_PRESERVED";bootstrap?.Revoke();return false;}
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
    internal async ValueTask DisconnectAsync()
    {
        // Revoke before waiting for the serialized owner lane or adapter drains.
        connected=false;bootstrap?.Revoke();runtime.Activation.Revoke();connection?.Cancel();
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
        if(reads!=null)await reads.DisposeAsync().ConfigureAwait(false);
        if(stream!=null)await stream.DisposeAsync().ConfigureAwait(false);
        if(pumping!=null)await pumping.ConfigureAwait(false);
        reads=null;stream=null;pumping=null;
    }
    private static bool SafeFailure(Exception ex)=>ex is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException or Apex.Desktop.Services.WindowsDataException;
    private sealed class SigningProvider(OkxAccountConnectionService owner,string fingerprint) : IOkxSigningMaterialProvider
    {
        public ValueTask<OkxSigningMaterial> OpenAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();if(!owner.connected||owner.disposed)throw new InvalidOperationException("CONNECTION_REVOKED");
            using var credential=owner.runtime.OpenCredential(owner.binding,fingerprint);
            return ValueTask.FromResult(credential.OpenSigningMaterial(owner.scope!));
        }
    }
}
