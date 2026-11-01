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
    internal IReadOnlyList<OkxScopeBinding> Bindings { get; }
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
    internal static OkxNavalRuntime Compose(string admittedDataRoot,IReadOnlyList<OkxScopeBinding> bindings)=>new(admittedDataRoot,bindings,()=>new WindowsOkxCredentialStore(admittedDataRoot),new UnavailableOkxActivationProvider());
    internal static OkxNavalRuntime Compose(string admittedDataRoot,IReadOnlyList<OkxScopeBinding> bindings,IOkxExecutionSecretProvider secrets,IOkxProtectedActivationProvider activation)=>new(admittedDataRoot,bindings,()=>secrets,activation);
    internal static OkxNavalExecutionDispatcher ComposeOfflineExecutionForTests(Apex.Core.Naval.NavalExecutionTransactions transactions,
        Func<Apex.Adapters.Okx.Execution.IOkxSerializedDispatchFence,Apex.Adapters.Okx.Execution.OkxExecutionClient> offlineClientFactory)=>new(transactions,offlineClientFactory);
    internal ValueTask StartAsync(CancellationToken cancellation)
    {cancellation.ThrowIfCancellationRequested();lock(sync){if(disposed)throw new ObjectDisposedException(nameof(OkxNavalRuntime));}return ValueTask.CompletedTask;}
    internal void RegisterAccountService(OkxAccountConnectionService service,string root,OkxScopeBinding binding)
    {
        lock(sync){if(disposed||root!=dataRoot||!Bindings.Contains(binding))throw new InvalidOperationException("OKX_CONNECTION_SCOPE_DENIED");accountServices.Add(service);}
    }
    internal OkxAccountConnectionService AccountService(string scopeId)=>OkxAccountConnectionService.Create(this,dataRoot,Bindings.Single(b=>b.ScopeId==scopeId));
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
