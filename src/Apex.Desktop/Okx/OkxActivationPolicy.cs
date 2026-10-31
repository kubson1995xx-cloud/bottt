using System;
using System.Threading;
using System.Threading.Tasks;
namespace Apex.Desktop.Okx;
internal enum OkxActivationPurpose { Observe, Execute }
internal sealed record OkxActivationRequest(OkxScopeBinding Binding, string CredentialFingerprint, Guid Session, long Generation, OkxActivationPurpose Purpose, DateTimeOffset ExpiresUtc);
internal interface IOkxProtectedActivationProvider { ValueTask<bool> ConfirmAsync(OkxActivationRequest request, CancellationToken cancellation); }
internal sealed class UnavailableOkxActivationProvider : IOkxProtectedActivationProvider
{
    public ValueTask<bool> ConfirmAsync(OkxActivationRequest request, CancellationToken cancellation) => ValueTask.FromResult(false);
}
internal sealed class OkxActivationLease { private OkxActivationLease() { } }
// Compile-only deny-by-default policy; never issues a lease.
internal sealed class OkxActivationPolicy
{
    internal OkxActivationPolicy(IOkxProtectedActivationProvider provider, Func<DateTimeOffset> clock) { }
    internal ValueTask<OkxActivationLease?> RequestAsync(OkxActivationRequest request, CancellationToken cancellation) => ValueTask.FromResult<OkxActivationLease?>(null);
    internal bool TryConsume(OkxActivationLease lease, OkxActivationRequest expected, DateTimeOffset now) => false;
    internal void Revoke() { }
}
