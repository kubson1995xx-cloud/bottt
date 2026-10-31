using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Xunit;

namespace Apex.Tests;
internal sealed class CredentialFixture : IDisposable
{
    internal byte[] Key = RandomNumberGenerator.GetBytes(24), Secret = RandomNumberGenerator.GetBytes(32), Passphrase = RandomNumberGenerator.GetBytes(24);
    internal static OkxScopeBinding Binding(NavalEnvironment env = NavalEnvironment.OkxDemo) => OkxConfiguration.Bind(JsonSerializer.Deserialize<OkxNavalOptions>(OkxCompositionFixture.ValidConfig(env)))[0];
    internal OkxExecutionCredentialLease Lease(OkxScopeBinding? binding = null, string? provenance = null)
    {
        var b = binding ?? Binding();
        return new(b, provenance ?? (b.Account.Environment == NavalEnvironment.OkxDemo ? "DemoTradingApi" : "LiveAccountApi"), Key, Secret, Passphrase);
    }
    public void Dispose() { CryptographicOperations.ZeroMemory(Key); CryptographicOperations.ZeroMemory(Secret); CryptographicOperations.ZeroMemory(Passphrase); }
}
internal sealed class ActivationSpy : IOkxProtectedActivationProvider
{
    internal int Calls;
    internal bool Accept = true;
    internal Func<ValueTask<bool>>? Pending;
    public ValueTask<bool> ConfirmAsync(OkxActivationRequest request, CancellationToken cancellation) { Calls++; return Pending?.Invoke() ?? ValueTask.FromResult(Accept); }
}
internal sealed class SecretSpy : IOkxExecutionSecretProvider
{
    internal int OpenCount;
    public OkxExecutionCredentialLease Open(OkxScopeBinding binding, OkxScopePaths paths) { OpenCount++; throw new Exception("Unexpected secret open"); }
}

public sealed class OkxCredentialTests
{
    [Fact]
    public void OkxCredentials_LeaseErasesOnDisposeAndErrors()
    {
        using var f = new CredentialFixture(); byte[] original = f.Key.ToArray();
        var lease = f.Lease();
        Assert.True(original.AsSpan().SequenceEqual(lease.ApiKey), "Lease must expose the original synthetic key bytes without publishing them");
        Assert.Equal(Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant(), lease.Fingerprint);
        Assert.Equal(CredentialFixture.Binding(), lease.Binding);
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(lease));
        lease.Dispose(); lease.Dispose();
        Assert.All(f.Key.Concat(f.Secret).Concat(f.Passphrase), b => Assert.Equal((byte)0, b));
        Assert.Throws<ObjectDisposedException>(() => lease.ApiKey.ToArray());
        using var invalid = new CredentialFixture();
        Assert.ThrowsAny<Exception>(() => invalid.Lease(provenance: "LiveAccountApi"));
        Assert.All(invalid.Key.Concat(invalid.Secret).Concat(invalid.Passphrase), b => Assert.Equal((byte)0, b));
    }
    [Fact]
    public void OkxCredentials_RegistryRejectsFingerprintReuseAcrossBindings()
    {
        var demo = CredentialFixture.Binding(); var live = CredentialFixture.Binding(NavalEnvironment.Live);
        var registry = new OkxCredentialUseRegistry(); string fp = new string('a', 64);
        Assert.True(registry.TryBind(demo, fp)); Assert.True(registry.TryBind(demo, fp));
        Assert.False(registry.TryBind(live, fp));
        Assert.True(registry.TryBind(live, new string('b', 64)));
        Assert.False(registry.TryBind(demo with { ScopeId = "other" }, fp));
        Assert.False(registry.TryBind(demo, "invalid"));
    }
    [Fact]
    public void OkxCredentials_CrossScopeRootAndEnvironmentRefuse()
    {
        var demo = CredentialFixture.Binding(); var live = CredentialFixture.Binding(NavalEnvironment.Live);
        string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "synthetic-okx-root"));
        var a = OkxConfiguration.Paths(root, demo); var b = OkxConfiguration.Paths(root, live);
        Assert.NotEqual(a.Root, b.Root); Assert.NotEqual(a.Authority, b.Authority); Assert.NotEqual(a.Credentials, b.Credentials);
        Assert.NotEqual(a, OkxConfiguration.Paths(root + "-other", demo));
        Assert.ThrowsAny<Exception>(() => OkxConfiguration.Paths(root, demo with { ScopeId = "../escape" }));
        using var invalid = new CredentialFixture();
        Assert.ThrowsAny<Exception>(() => invalid.Lease(demo with { ConfigDigest = live.ConfigDigest }));
        Assert.All(invalid.Key.Concat(invalid.Secret).Concat(invalid.Passphrase), x => Assert.Equal((byte)0, x));
    }
    [Fact]
    public void UnsupportedNativeStoreNeverWrites()
    {
        if (OperatingSystem.IsWindows()) return; // Portable refusal only; native class requires Windows.
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "apex-unsupported-" + Guid.NewGuid().ToString("N"));
        var b = CredentialFixture.Binding(); var p = OkxConfiguration.Paths(root, b);
        var store = new WindowsOkxCredentialStore(root); using var f = new CredentialFixture();
        Assert.ThrowsAny<Exception>(() => store.Create(b, p, "DemoTradingApi", f.Key, f.Secret, f.Passphrase));
        Assert.ThrowsAny<Exception>(() => store.Open(b, p));
        Assert.False(System.IO.Directory.Exists(root));
    }
}
