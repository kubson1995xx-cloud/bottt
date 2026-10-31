using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
namespace Apex.Desktop.Okx;
// Compile-only stopped composition, not yet wired into the production host.
internal sealed class OkxNavalRuntime : IAsyncDisposable
{
    internal static OkxNavalRuntime Compose(string admittedDataRoot, IReadOnlyList<OkxScopeBinding> bindings) => new();
    internal static OkxNavalRuntime Compose(string admittedDataRoot, IReadOnlyList<OkxScopeBinding> bindings, IOkxExecutionSecretProvider secrets, IOkxProtectedActivationProvider activation) => new();
    internal IReadOnlyList<OkxScopeBinding> Bindings => Array.Empty<OkxScopeBinding>();
    internal OkxActivationPolicy Activation { get; } = new(new UnavailableOkxActivationProvider(), () => DateTimeOffset.UtcNow);
    internal string Mode => "STOP";
    internal string Reason => "NOT_CONFIGURED";
    internal ValueTask StartAsync(CancellationToken cancellation) => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
