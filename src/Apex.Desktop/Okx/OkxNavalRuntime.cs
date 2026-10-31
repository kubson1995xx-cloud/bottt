using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace Apex.Desktop.Okx;

// Owns only frozen security composition. No transport, financial owner, capital,
// broker identity, readiness, arming or trading START exists in this stage.
internal sealed class OkxNavalRuntime : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly Lazy<IOkxExecutionSecretProvider> secrets;
    private readonly OkxCredentialUseRegistry credentialUses = new();
    private bool disposed;
    internal IReadOnlyList<OkxScopeBinding> Bindings { get; }
    internal OkxActivationPolicy Activation { get; }
    internal string Mode => "STOP";
    internal string Reason => Bindings.Count == 0 ? "NOT_CONFIGURED" : "ACTIVATION_REQUIRED";
    private OkxNavalRuntime(string admittedDataRoot, IReadOnlyList<OkxScopeBinding> bindings, Func<IOkxExecutionSecretProvider> secrets, IOkxProtectedActivationProvider activation)
    {
        var copy = bindings.ToArray();
        foreach (var binding in copy) _ = OkxConfiguration.Paths(admittedDataRoot, binding);
        if (copy.Select(b => b.Account.Environment).Distinct().Count() != copy.Length || copy.Select(b => b.ScopeId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != copy.Length) throw new System.IO.InvalidDataException("OKX_SCOPE_INVALID");
        Bindings = Array.AsReadOnly(copy);
        this.secrets = new(secrets);
        Activation = new(activation, () => DateTimeOffset.UtcNow);
    }
    // Production entry has no provider injection, synthetic switch or network factory.
    internal static OkxNavalRuntime Compose(string admittedDataRoot, IReadOnlyList<OkxScopeBinding> bindings) => new(admittedDataRoot, bindings, () => new WindowsOkxCredentialStore(admittedDataRoot), new UnavailableOkxActivationProvider());
    // Direct existing friend-test seam only; never selected by Program/JSON/CLI/env.
    internal static OkxNavalRuntime Compose(string admittedDataRoot, IReadOnlyList<OkxScopeBinding> bindings, IOkxExecutionSecretProvider secrets, IOkxProtectedActivationProvider activation) => new(admittedDataRoot, bindings, () => secrets, activation);
    internal ValueTask StartAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        lock (sync) { if (disposed) throw new ObjectDisposedException(nameof(OkxNavalRuntime)); }
        return ValueTask.CompletedTask;
    }
    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (!disposed) { disposed = true; Activation.Close(); }
        }
        return ValueTask.CompletedTask;
    }
}
