using System;
namespace Apex.Desktop.Okx;
internal interface IOkxExecutionSecretProvider { OkxExecutionCredentialLease Open(OkxScopeBinding binding, OkxScopePaths paths); }
// Compile-only fail-closed seams for behavioral RED. No provisioning or key access.
internal sealed class OkxExecutionCredentialLease : IDisposable
{
    internal OkxScopeBinding Binding { get; }
    internal string Fingerprint => "";
    internal ReadOnlySpan<byte> ApiKey => ReadOnlySpan<byte>.Empty;
    internal ReadOnlySpan<byte> Secret => ReadOnlySpan<byte>.Empty;
    internal ReadOnlySpan<byte> Passphrase => ReadOnlySpan<byte>.Empty;
    internal OkxExecutionCredentialLease(OkxScopeBinding binding, string provenance, byte[] key, byte[] secret, byte[] passphrase) { Binding = binding; }
    public void Dispose() { }
}
internal sealed class OkxCredentialUseRegistry
{
    internal bool TryBind(OkxScopeBinding binding, string fingerprint) => false;
}
