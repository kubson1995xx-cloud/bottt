using System;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.Tests;
public sealed class OkxActivationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private static OkxActivationRequest Request() => new(CredentialFixture.Binding(), new string('a', 64), Guid.NewGuid(), 1, OkxActivationPurpose.Execute, Now.AddSeconds(120));
    [Fact]
    public async Task OkxActivation_ExactScopeSingleUseAndRevocation()
    {
        var spy = new ActivationSpy(); var policy = new OkxActivationPolicy(spy, () => Now); var request = Request();
        var lease = await policy.RequestAsync(request, CancellationToken.None);
        Assert.NotNull(lease); Assert.Equal(1, spy.Calls);
        Assert.False(policy.TryConsume(lease!, request with { Generation = request.Generation + 1 }, Now));
        Assert.True(policy.TryConsume(lease!, request, Now)); Assert.False(policy.TryConsume(lease!, request, Now));
        var revoked = await policy.RequestAsync(request, CancellationToken.None); Assert.NotNull(revoked);
        policy.Revoke(); Assert.False(policy.TryConsume(revoked!, request, Now));
    }
    [Theory]
    [InlineData("issuer")][InlineData("purpose")][InlineData("environment")][InlineData("account")][InlineData("config")][InlineData("fingerprint")][InlineData("session")][InlineData("scope")][InlineData("expired")][InlineData("clock-regression")]
    public async Task LeaseRejectsMismatchedAuthorityWithoutBurningExactControl(string mismatch)
    {
        var spy = new ActivationSpy(); var policy = new OkxActivationPolicy(spy, () => Now); var r = Request();
        var lease = await policy.RequestAsync(r, CancellationToken.None); Assert.NotNull(lease);
        var other = new OkxActivationPolicy(spy, () => Now);
        var expected = mismatch switch {
            "purpose" => r with { Purpose = OkxActivationPurpose.Observe },
            "environment" => r with { Binding = CredentialFixture.Binding(NavalEnvironment.Live) },
            "account" => r with { Binding = r.Binding with { MainUid = "999" } },
            "config" => r with { Binding = r.Binding with { ConfigDigest = new string('b', 64) } },
            "fingerprint" => r with { CredentialFingerprint = new string('b', 64) },
            "session" => r with { Session = Guid.NewGuid() },
            "scope" => r with { Binding = r.Binding with { ScopeId = "other" } }, _ => r };
        Assert.False((mismatch == "issuer" ? other : policy).TryConsume(lease!, expected, mismatch == "expired" ? Now.AddSeconds(120) : mismatch == "clock-regression" ? Now.AddTicks(-1) : Now));
        Assert.True(policy.TryConsume(lease!, r, Now));
        Assert.Throws<NotSupportedException>(() => System.Text.Json.JsonSerializer.Serialize(lease));
    }
    [Theory]
    [InlineData("ttl")][InlineData("expired")][InlineData("session")][InlineData("fingerprint")][InlineData("generation")][InlineData("purpose")][InlineData("binding")]
    public async Task InvalidRequestsNeverPrompt(string invalid)
    {
        var spy = new ActivationSpy(); var policy = new OkxActivationPolicy(spy, () => Now); var r = Request();
        r = invalid switch {
            "ttl" => r with { ExpiresUtc = Now.AddSeconds(121) }, "expired" => r with { ExpiresUtc = Now },
            "session" => r with { Session = Guid.Empty }, "fingerprint" => r with { CredentialFingerprint = "not-a-fingerprint" },
            "generation" => r with { Generation = -1 }, "purpose" => r with { Purpose = (OkxActivationPurpose)100 },
            _ => r with { Binding = r.Binding with { MainUid = "other" } } };
        Assert.Null(await policy.RequestAsync(r, CancellationToken.None)); Assert.Equal(0, spy.Calls);
    }
    [Fact]
    public async Task ProtectedDenialCancellationAndInFlightRevokeNeverIssue()
    {
        var spy = new ActivationSpy { Accept = false }; var policy = new OkxActivationPolicy(spy, () => Now);
        Assert.Null(await policy.RequestAsync(Request(), CancellationToken.None)); Assert.Equal(1, spy.Calls);
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        spy.Pending = () => new ValueTask<bool>(pending.Task);
        var issuing = policy.RequestAsync(Request(), CancellationToken.None); policy.Revoke(); pending.SetResult(true);
        Assert.Null(await issuing);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Null(await policy.RequestAsync(Request(), cancel.Token)); Assert.Equal(2, spy.Calls);
        var clock = Now; spy.Pending = () => { clock = Now.AddSeconds(121); return ValueTask.FromResult(true); };
        var slow = new OkxActivationPolicy(spy, () => clock);
        Assert.Null(await slow.RequestAsync(Request(), CancellationToken.None));
    }
    [Fact]
    public async Task OkxActivation_LocalAndProtectedOperatorCannotMintLease()
    {
        var ordinary = new OkxActivationPolicy(new UnavailableOkxActivationProvider(), () => Now);
        Assert.Null(await ordinary.RequestAsync(Request(), CancellationToken.None));
    }
}
