using System;
namespace Apex.Desktop.Okx;
// Compile-only unavailable store. No plaintext fallback, file or permission mutation.
internal sealed class WindowsOkxCredentialStore : IOkxExecutionSecretProvider
{
    internal WindowsOkxCredentialStore(string admittedDataRoot) { }
    public OkxExecutionCredentialLease Open(OkxScopeBinding binding, OkxScopePaths paths) => throw new InvalidOperationException("OKX_CREDENTIAL_UNAVAILABLE");
    internal void Create(OkxScopeBinding binding, OkxScopePaths paths, string provenance, byte[] key, byte[] secret, byte[] passphrase) => throw new InvalidOperationException("OKX_CREDENTIAL_UNAVAILABLE");
}
