using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace Apex.Desktop.Okx;

// Startup is passive. Only the explicit scoped connection action opens secrets/transports.
internal sealed class OkxNavalRuntime : IAsyncDisposable
{
    private readonly object sync=new();
    private readonly Lazy<IOkxExecutionSecretProvider> secrets;
    private readonly OkxCredentialUseRegistry credentialUses=new();
    private readonly string dataRoot;
    private readonly List<OkxAccountConnectionService> accountServices=new();
    private bool disposed;
    internal IReadOnlyList<OkxScopeBinding> Bindings { get; private set; }
    internal OkxActivationPolicy Activation { get; }
    internal string Mode=>"STOP";
    internal string Reason=>Bindings.Count==0?"NOT_CONFIGURED":"ACTIVATION_REQUIRED";
    private OkxNavalRuntime(string admittedDataRoot,IReadOnlyList<OkxScopeBinding> bindings,Func<IOkxExecutionSecretProvider> secrets,IOkxProtectedActivationProvider activation)
    {
        dataRoot=admittedDataRoot;var copy=bindings.ToArray();
        foreach(var binding in copy)_=OkxConfiguration.Paths(admittedDataRoot,binding);
        if(copy.Select(b=>b.Account.Environment).Distinct().Count()!=copy.Length||copy.Select(b=>b.ScopeId).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=copy.Length)throw new System.IO.InvalidDataException("OKX_SCOPE_INVALID");
        Bindings=Array.AsReadOnly(copy);this.secrets=new(secrets);Activation=new(activation,()=>DateTimeOffset.UtcNow);
    }
    internal static OkxNavalRuntime Compose(string admittedDataRoot,IReadOnlyList<OkxScopeBinding> bindings)
    {
        OkxNavalRuntime? runtime=null;
        var provider=new WindowsOkxApprovalProvider(activationAdmitted:r=>runtime?.NativeActivationAdmitted(r)==true);
        runtime=new(admittedDataRoot,bindings,()=>new WindowsOkxCredentialStore(admittedDataRoot),provider);return runtime;
    }
    private bool NativeActivationAdmitted(OkxActivationRequest request)
    {
        lock(sync)
        {
            if(disposed||!Bindings.Contains(request.Binding)||request.Purpose==OkxActivationPurpose.Execute&&!accountServices.Any(s=>s.IsConnected&&s.Binding==request.Binding&&s.TradingSessionId==request.Session))return false;
            try
            {
                using var credential=secrets.Value.Open(request.Binding,OkxConfiguration.Paths(dataRoot,request.Binding));
                return credential.Binding==request.Binding&&credential.Fingerprint==request.CredentialFingerprint;
            }
            catch(Exception e) when(e is not OutOfMemoryException){return false;}
        }
    }
    internal static OkxNavalRuntime Compose(string admittedDataRoot,IReadOnlyList<OkxScopeBinding> bindings,IOkxExecutionSecretProvider secrets,IOkxProtectedActivationProvider activation)=>new(admittedDataRoot,bindings,()=>secrets,activation);
    internal static OkxNavalExecutionDispatcher ComposeOfflineExecutionForTests(Apex.Core.Naval.NavalExecutionTransactions transactions,
        Func<Apex.Adapters.Okx.Execution.IOkxSerializedDispatchFence,Apex.Adapters.Okx.Execution.OkxExecutionClient> offlineClientFactory)=>new(transactions,offlineClientFactory);
    internal ValueTask StartAsync(CancellationToken cancellation)
    {cancellation.ThrowIfCancellationRequested();lock(sync){if(disposed)throw new ObjectDisposedException(nameof(OkxNavalRuntime));}return ValueTask.CompletedTask;}
    internal void RegisterAccountService(OkxAccountConnectionService service,string root,OkxScopeBinding binding)
    {
        lock(sync){if(disposed||root!=dataRoot||!Bindings.Contains(binding))throw new InvalidOperationException("OKX_CONNECTION_SCOPE_DENIED");accountServices.Add(service);}
    }
    internal void AddProvisionedBinding(OkxScopeBinding binding)
    {
        OkxConfiguration.ValidateBinding(binding);
        lock(sync)
        {
            if(disposed||accountServices.Any(s=>s.IsConnected)||Bindings.Any(b=>b.ScopeId.Equals(binding.ScopeId,StringComparison.OrdinalIgnoreCase)||b.Account.Environment==binding.Account.Environment))throw new InvalidOperationException("OKX_SCOPE_CHANGE_DENIED");
            Bindings=Array.AsReadOnly(Bindings.Append(binding).ToArray());Activation.Revoke();
        }
    }
    internal OkxExecutionCredentialLease OpenDemoReadCredential(OkxScopeBinding binding)
    {
        lock(sync)
        {
            if(disposed||binding.Account.Environment!=Apex.Core.Naval.NavalEnvironment.OkxDemo||!Bindings.Contains(binding))throw new InvalidOperationException("DEMO_SCOPE_REQUIRED");
            var credential=secrets.Value.Open(binding,OkxConfiguration.Paths(dataRoot,binding));
            if(credential.Binding==binding&&credential.Provenance=="DemoTradingApi"&&credentialUses.TryBind(binding,credential.Fingerprint))return credential;
            credential.Dispose();throw new InvalidOperationException("DEMO_CREDENTIAL_MISMATCH");
        }
    }
    internal bool VerifySavedCredential(OkxScopeBinding binding)
    {
        lock(sync)
        {
            if(disposed||!Bindings.Contains(binding))return false;
            using var credential=secrets.Value.Open(binding,OkxConfiguration.Paths(dataRoot,binding));
            return credential.Binding==binding&&credential.Provenance==(binding.Account.Environment==Apex.Core.Naval.NavalEnvironment.OkxDemo?"DemoTradingApi":"LiveAccountApi");
        }
    }
    internal string CredentialFingerprint(OkxScopeBinding binding)
    {
        lock(sync)
        {
            if(disposed||!Bindings.Contains(binding))throw new InvalidOperationException("OKX_CONNECTION_REVOKED");
            using var lease=secrets.Value.Open(binding,OkxConfiguration.Paths(dataRoot,binding));return lease.Fingerprint;
        }
    }
    internal OkxAccountConnectionService AccountService(string scopeId)
    {
        lock(sync)
        {
            if(disposed)throw new ObjectDisposedException(nameof(OkxNavalRuntime));
            var binding=Bindings.Single(b=>b.ScopeId==scopeId);
            return accountServices.LastOrDefault(s=>s.Binding==binding&&!s.IsDisposed)??OkxAccountConnectionService.Create(this,dataRoot,binding);
        }
    }
    internal bool AuthorizeConnection(OkxAccountConnectionService service,OkxActivationRequest request,OkxActivationLease lease)
    {
        lock(sync)
        {
            if(disposed||!accountServices.Contains(service)||!Bindings.Contains(request.Binding)||accountServices.Any(s=>s!=service&&s.IsConnected)||
                !Activation.TryConsume(lease,request,DateTimeOffset.UtcNow))return false;
            using var credential=secrets.Value.Open(request.Binding,OkxConfiguration.Paths(dataRoot,request.Binding));
            return credential.Binding==request.Binding&&credential.Fingerprint==request.CredentialFingerprint&&credentialUses.TryBind(request.Binding,credential.Fingerprint);
        }
    }
    internal OkxExecutionCredentialLease OpenCredential(OkxScopeBinding binding,string fingerprint)
    {
        lock(sync)
        {
            if(disposed||!Bindings.Contains(binding))throw new InvalidOperationException("OKX_CONNECTION_REVOKED");
            var credential=secrets.Value.Open(binding,OkxConfiguration.Paths(dataRoot,binding));
            if(credential.Binding==binding&&credential.Fingerprint==fingerprint&&credentialUses.TryBind(binding,fingerprint))return credential;
            credential.Dispose();throw new InvalidOperationException("OKX_CREDENTIAL_BINDING_DENIED");
        }
    }
    public async ValueTask DisposeAsync()
    {
        OkxAccountConnectionService[] services;
        lock(sync){if(disposed)return;disposed=true;Activation.Close();services=accountServices.ToArray();}
        foreach(var service in services)await service.DisposeAsync().ConfigureAwait(false);
    }
}
