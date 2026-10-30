using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using Apex.Adapters.Okx;
using Xunit;
namespace Apex.Adapters.Okx.Tests;
public class OkxSafetyTests
{
    [Fact]
    public async Task OnlyAllowlistedDemoGets()
    {
        using var f = new OkxHttpFixture(); f.Bootstrap();
        f.Add("/api/v5/account/balance", OkxHttpFixture.Envelope(OkxHttpFixture.Balance()));
        f.Add("/api/v5/account/instruments?instType=SPOT", OkxHttpFixture.Envelope(OkxHttpFixture.Instrument));
        f.Add("/api/v5/trade/orders-pending?after=A%26x%3D1%3F&limit=100", OkxHttpFixture.Envelope());
        f.Add("/api/v5/trade/order?instId=%2F%2Fevil.example%2F%3Fx%3D1&ordId=ord%26q%3D2", OkxHttpFixture.Envelope(OkxHttpFixture.Order()));
        f.Add("/api/v5/trade/fills?instType=SPOT&limit=100", OkxHttpFixture.Envelope());
        f.Add("/api/v5/trade/fills-history?instType=SPOT&limit=100", OkxHttpFixture.Envelope());
        f.Add("/api/v5/account/bills?limit=100", OkxHttpFixture.Envelope());
        f.Add("/api/v5/account/bills-archive?limit=100", OkxHttpFixture.Envelope());
        f.Add("/api/v5/public/instruments?instType=SPOT", OkxHttpFixture.Envelope(OkxHttpFixture.Instrument));
        await using var c = f.Client(); Assert.Equal(OkxReadStatus.Observed, (await c.GetConfigAsync(default)).Status);
        Assert.Equal(OkxReadStatus.Observed, (await c.GetBalancesAsync(default)).Status);
        Assert.Single((await c.GetAccountSpotInstrumentsAsync(default)).Rows);
        Assert.Equal(OkxReadStatus.Observed, (await c.GetPendingOrdersAsync(new("A&x=1?"), default)).Status);
        Assert.Single((await c.GetOrderAsync(new("//evil.example/?x=1", "ord&q=2"), default)).Rows);
        Assert.Equal(OkxReadStatus.Observed, (await c.GetFillsAsync(OkxHistoryWindow.Recent, new(), default)).Status);
        Assert.Equal(OkxReadStatus.Observed, (await c.GetFillsAsync(OkxHistoryWindow.Archive, new(), default)).Status);
        Assert.Equal(OkxReadStatus.Observed, (await c.GetBillsAsync(OkxHistoryWindow.Recent, new(), default)).Status);
        Assert.Equal(OkxReadStatus.Observed, (await c.GetBillsAsync(OkxHistoryWindow.Archive, new(), default)).Status);
        Assert.Single((await c.GetPublicSpotInstrumentsAsync(default)).Rows); f.Done();
        Assert.DoesNotContain(typeof(OkxEeaDemoReadClient).GetMethods(), m => m.Name.Contains("Send") || m.Name.Contains("CancelOrder") || m.Name.Contains("Place"));
    }
    [Theory]
    [InlineData(302, "https://eea.okx.com/api/v5/account/config")]
    [InlineData(307, "https://www.okx.com/api/v5/account/config")]
    [InlineData(308, "https://evil.example/")]
    public async Task RedirectNeverFollowed(int status, string location)
    { using var f = new OkxHttpFixture(); f.Add("/api/v5/public/time", "", (HttpStatusCode)status, location); await using var c = f.Client(false); var result = await c.GetTimeAsync(default); Assert.Equal("RedirectDenied", result.ErrorCode); Assert.Single(f.Sent); }
    [Fact]
    public async Task PublicHasNoSecrets()
    { using var f = new OkxHttpFixture(); f.Add("/api/v5/public/time", System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures/synthetic/okx-read-v1.json"))).RootElement.GetProperty("time").GetRawText()); await using var c = f.Client(false); Assert.Equal(OkxReadStatus.Observed, (await c.GetTimeAsync(default)).Status); Assert.Equal("CredentialsUnavailable", (await c.GetBalancesAsync(default)).ErrorCode); Assert.Equal(0, f.Secrets.Opens); Assert.Single(f.Sent); }
    [Theory]
    [InlineData("\"uid\":\"999999999999999999999999999999999\"", "\"uid\":\"other\"")]
    [InlineData("\"acctLv\":\"1\"", "\"acctLv\":\"2\"")]
    [InlineData("\"perm\":\"read_only\"", "\"perm\":\"read_only,trade\"")]
    [InlineData("\"perm\":\"read_only\"", "\"perm\":\"withdraw\"")]
    [InlineData("\"perm\":\"read_only\"", "\"perm\":\"unknown\"")]
    [InlineData("\"ip\":\"192.0.2.4\"", "\"ip\":\"\"")]
    [InlineData("\"autoLoan\":false", "\"autoLoan\":true")]
    [InlineData("\"enableSpotBorrow\":false", "\"enableSpotBorrow\":true")]
    [InlineData("\"enableSpotBorrow\":false", "\"unrelated\":false")]
    public async Task ConfigPreflightBindsUidAndReadOnly(string find, string replacement)
    { using var f = new OkxHttpFixture(); f.Bootstrap(OkxHttpFixture.Config().Replace(find, replacement)); await using var c = f.Client(); var r = await c.GetBalancesAsync(default); Assert.Equal(OkxReadStatus.Failed, r.Status); Assert.Equal(2, f.Sent.Count); Assert.DoesNotContain(f.Sent, s => s.Path.Contains("balance")); }
    [Fact]
    public async Task WrongBindingAbortsBeforePrivateSend()
    { using var f = new OkxHttpFixture(); var binding = f.Secrets.Binding; await using var c = OkxEeaDemoReadClient.ForTests(f, f.Clock, f.Secrets, new("wrong", OkxHttpFixture.Uid), f.Clock.Delay); var r = await c.GetBalancesAsync(default); Assert.Equal("CredentialBindingMismatch", r.ErrorCode); Assert.Empty(f.Sent); }
    [Fact]
    public async Task SecretEchoRedacted()
    {
        using var f = new OkxHttpFixture(); f.Secrets.Open = ct => throw new InvalidOperationException(PublicTestSecrets.Secret + PublicTestSecrets.Key + PublicTestSecrets.Pass); await using var c = f.Client(); var r = await c.GetBalancesAsync(default); Assert.Equal("CredentialsUnavailable", r.ErrorCode); var text = JsonSerializer.Serialize(r) + r;
        using var lease = OkxDemoCredentialLease.FromSecretStore("opaque", PublicTestSecrets.Key, System.Text.Encoding.UTF8.GetBytes(PublicTestSecrets.Secret), PublicTestSecrets.Pass); text += JsonSerializer.Serialize(lease) + lease;
        foreach (var secret in new[] { PublicTestSecrets.Secret, PublicTestSecrets.Key, PublicTestSecrets.Pass }) Assert.DoesNotContain(secret, text);
        using var g = new OkxHttpFixture(); g.Add("/api/v5/public/time", "{\"code\":\"50101\",\"msg\":\"" + PublicTestSecrets.Secret + "\",\"data\":[]}"); await using var p = g.Client(false); var error = await p.GetTimeAsync(default); Assert.Equal("50101", error.ErrorCode); Assert.DoesNotContain(PublicTestSecrets.Secret, JsonSerializer.Serialize(error));
    }
    [Theory]
    [InlineData(200, "50101", "50101", 1)]
    [InlineData(200, "50119", "50119", 1)]
    [InlineData(200, "50102", "50102", 1)]
    [InlineData(403, "0", "HttpForbidden", 1)]
    [InlineData(429, "0", "RateLimited", 3)]
    [InlineData(200, "50011", "RateLimited", 3)]
    [InlineData(503, "0", "HttpUnavailable", 3)]
    [InlineData(200, "50004", "ExchangeTimeout", 3)]
    public async Task CancellationLimitsBackoffAndErrorSemantics(int status, string code, string expected, int count)
    { using var f = new OkxHttpFixture(); for (var i = 0; i < count; i++) f.Add("/api/v5/public/time", "{\"code\":\"" + code + "\",\"data\":[]}", (HttpStatusCode)status); await using var c = f.Client(false); var r = await c.GetTimeAsync(default); Assert.Equal(OkxReadStatus.Failed, r.Status); Assert.Equal(expected, r.ErrorCode); Assert.Equal(count, f.Sent.Count); Assert.Empty(r.Rows); if (count == 3) Assert.True(f.Sent[2].At - f.Sent[0].At >= TimeSpan.FromSeconds(3)); f.Done(); }
    [Fact]
    public async Task InvalidTlsNeverRetries()
    { using var f = new OkxHttpFixture(); f.AddError("/api/v5/public/time", new HttpRequestException("secret", new AuthenticationException("private"))); await using var c = f.Client(false); Assert.Equal("TlsFailure", (await c.GetTimeAsync(default)).ErrorCode); Assert.Single(f.Sent); }
    [Fact]
    public async Task RetryAfterOutsideBudgetFailsWithoutWaiting()
    { using var f = new OkxHttpFixture(); f.Add("/api/v5/public/time", "{}", HttpStatusCode.TooManyRequests, retryAfter: "31"); await using var c = f.Client(false); var r = await c.GetTimeAsync(default); Assert.Equal("RateLimited", r.ErrorCode); Assert.Single(f.Sent); }
    [Fact]
    public async Task PrivateStartsAreAtLeast600MillisecondsApart()
    { using var f = new OkxHttpFixture(); f.Bootstrap(); f.Add("/api/v5/account/balance", OkxHttpFixture.Envelope(OkxHttpFixture.Balance())); f.Add("/api/v5/account/balance", OkxHttpFixture.Envelope(OkxHttpFixture.Balance())); await using var c = f.Client(); await c.GetBalancesAsync(default); await c.GetBalancesAsync(default); var sends = f.Sent.Where(s => s.Private).ToArray(); Assert.Equal(3, sends.Length); for (var i = 1; i < sends.Length; i++) Assert.True(sends[i].At - sends[i - 1].At >= TimeSpan.FromMilliseconds(600)); Assert.Equal(1, f.Secrets.Opens); }
    [Fact]
    public async Task CancellationPropagatesFromProvider()
    { using var f = new OkxHttpFixture(); using var source = new CancellationTokenSource(); f.Secrets.Open = ct => { source.Cancel(); ct.ThrowIfCancellationRequested(); throw new Exception(); }; await using var c = f.Client(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.GetBalancesAsync(source.Token)); Assert.Empty(f.Sent); }
    [Fact]
    public async Task CancellationPropagatesFromSendAndDelay()
    { using var f = new OkxHttpFixture(); using var source = new CancellationTokenSource(); f.Steps.Enqueue(("/api/v5/public/time", (r, ct) => { source.Cancel(); return Task.FromCanceled<HttpResponseMessage>(ct); })); await using var c = f.Client(false); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.GetTimeAsync(source.Token)); Assert.Single(f.Sent); }
    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task InvalidPageNeverSends(int limit)
    { using var f = new OkxHttpFixture(); await using var c = f.Client(); var r = await c.GetPendingOrdersAsync(new(Limit: limit), default); Assert.Equal("InvalidRequest", r.ErrorCode); Assert.Empty(f.Sent); }
    [Theory]
    [InlineData("provider")]
    [InlineData("limiter")]
    [InlineData("body")]
    [InlineData("page")]
    [InlineData("delay")]
    public async Task CancelAtEveryAwaitStopsLaterNetwork(string stage)
    {
        using var f = new OkxHttpFixture(); using var stop = new CancellationTokenSource();
        if (stage == "provider") f.Secrets.Open = async ct => { await Task.Yield(); stop.Cancel(); ct.ThrowIfCancellationRequested(); return null!; };
        else if (stage == "body") f.Steps.Enqueue(("/api/v5/public/time", (r, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new CancelStream(stop)) })));
        else if (stage == "page") { f.Bootstrap(); f.Add("/api/v5/trade/orders-pending?limit=100", OkxHttpFixture.Envelope(OkxHttpFixture.Order())); f.Inspect = r => { if (r.RequestUri!.AbsolutePath.EndsWith("orders-pending")) stop.Cancel(); }; }
        else if (stage == "delay") f.Add("/api/v5/public/time", "{}", HttpStatusCode.TooManyRequests);
        else { f.Bootstrap(); f.Add("/api/v5/account/balance", OkxHttpFixture.Envelope(OkxHttpFixture.Balance())); }
        Func<TimeSpan, CancellationToken, Task> delay = (d, t) => { stop.Cancel(); t.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        await using var c = OkxEeaDemoReadClient.ForTests(f, f.Clock, stage is "provider" or "page" or "limiter" ? f.Secrets : null, new(f.Secrets.Binding, OkxHttpFixture.Uid), stage is "limiter" or "delay" ? delay : f.Clock.Delay);
        if (stage == "page") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OkxReadPager.Collect("/api/v5/trade/orders-pending", new(), c.GetPendingOrdersAsync, o => o.OrdId, o => o.UTime.Utc, null, stop.Token));
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stage is "provider" or "limiter" ? c.GetBalancesAsync(stop.Token).ContinueWith(t => t.GetAwaiter().GetResult(), TaskContinuationOptions.ExecuteSynchronously) : c.GetTimeAsync(stop.Token).ContinueWith(t => t.GetAwaiter().GetResult(), TaskContinuationOptions.ExecuteSynchronously));
        Assert.True(f.Sent.Count <= 3);
    }
    private sealed class CancelStream(CancellationTokenSource stop) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => 0; public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct = default) { stop.Cancel(); return ValueTask.FromCanceled<int>(ct); }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException(); public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
    [Fact]
    public async Task OversizedBodyAndDepthFailWithoutRetry()
    {
        using var f = new OkxHttpFixture(); f.Steps.Enqueue(("/api/v5/public/time", (r, ct) => { var content = new StringContent("{}"); content.Headers.ContentLength = 8 * 1024 * 1024 + 1; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }); })); await using var c = f.Client(false); Assert.Equal("BodyLimit", (await c.GetTimeAsync(default)).ErrorCode); Assert.Single(f.Sent);
        using var g = new OkxHttpFixture(); g.Add("/api/v5/public/time", "{\"code\":\"0\",\"data\":[],\"extra\":" + new string('[', 65) + "0" + new string(']', 65) + "}"); await using var d = g.Client(false); Assert.Equal("InvalidPayload", (await d.GetTimeAsync(default)).ErrorCode); Assert.Single(g.Sent);
    }
    [Fact]
    public async Task AttemptTimeoutIsDistinctAndAtMostThreeAttempts()
    { using var f = new OkxHttpFixture(); for (var i = 0; i < 3; i++) f.AddError("/api/v5/public/time", new TaskCanceledException()); await using var c = f.Client(false); var r = await c.GetTimeAsync(default); Assert.Equal("Timeout", r.ErrorCode); Assert.Equal(3, f.Sent.Count); Assert.Empty(r.Rows); }
    [Fact]
    public async Task ClockSkewAndRttBlockPrivateConfig()
    {
        using var f = new OkxHttpFixture(); f.Add("/api/v5/public/time", OkxHttpFixture.Envelope("{\"ts\":\"" + f.Clock.Now.AddHours(8).ToUnixTimeMilliseconds() + "\"}")); await using var c = f.Client(); Assert.Equal("ClockPreflightFailed", (await c.GetBalancesAsync(default)).ErrorCode); Assert.Single(f.Sent);
        using var g = new OkxHttpFixture(); g.Bootstrap(); g.Inspect = r => g.Clock.Now += TimeSpan.FromSeconds(3); await using var d = g.Client(); Assert.Equal("ClockPreflightFailed", (await d.GetBalancesAsync(default)).ErrorCode); Assert.Single(g.Sent);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task InvalidTypedFieldsCannotBootstrap(int variant)
    { using var f = new OkxHttpFixture(); await using var c = f.Client(); if (variant == 0) Assert.Equal("InvalidRequest", (await c.GetPendingOrdersAsync(new(BeginUtc: f.Clock.Now), default)).ErrorCode); if (variant == 1) Assert.Equal("InvalidRequest", (await c.GetFillsAsync((OkxHistoryWindow)42, new(), default)).ErrorCode); if (variant == 2) Assert.Equal("InvalidRequest", (await c.GetOrderAsync(new("A\nB", "ord"), default)).ErrorCode); Assert.Empty(f.Sent); }
    [Fact]
    public async Task ConfigTimeErrorPoisonsUntilExplicitRecapture()
    { using var f = new OkxHttpFixture(); f.Bootstrap(); f.Add("/api/v5/account/balance", "{\"code\":\"50102\",\"data\":[]}"); await using var c = f.Client(); Assert.Equal("50102", (await c.GetBalancesAsync(default)).ErrorCode); var count = f.Sent.Count; Assert.Equal("50102", (await c.GetBalancesAsync(default)).ErrorCode); Assert.Equal(count, f.Sent.Count); await c.BeginCapture(default); f.Bootstrap(); f.Add("/api/v5/account/balance", OkxHttpFixture.Envelope(OkxHttpFixture.Balance())); Assert.Equal(OkxReadStatus.Observed, (await c.GetBalancesAsync(default)).Status); Assert.Equal(1, f.Secrets.Opens); f.Done(); }

    [Fact]
    public async Task PrivateDiagnosticsCannotExposeCredentials()
    {
        using var instrumentation = new System.Diagnostics.DiagnosticListener("HttpHandlerDiagnosticListener"); using var subscription = instrumentation.Subscribe(new IgnoreDiagnostic()); using var f = new OkxHttpFixture(); f.Bootstrap(); f.Add("/api/v5/account/balance", OkxHttpFixture.Envelope(OkxHttpFixture.Balance())); await using var c = f.Client(); var r = await c.GetBalancesAsync(default); Assert.Equal("UnsafeHttpDiagnostics", r.ErrorCode); Assert.Empty(f.Sent); Assert.Equal(0, f.Secrets.Opens);
    }
    private sealed class IgnoreDiagnostic : IObserver<KeyValuePair<string, object?>> { public void OnNext(KeyValuePair<string, object?> value) { } public void OnError(Exception e) { } public void OnCompleted() { } }
    [Fact]
    public async Task QuotaIsSharedAcrossClientsAndValidatedUid()
    {
        using var f = new OkxHttpFixture(); f.Bootstrap(); f.Bootstrap(); f.Add("/api/v5/account/balance", OkxHttpFixture.Envelope(OkxHttpFixture.Balance())); f.Add("/api/v5/account/balance", OkxHttpFixture.Envelope(OkxHttpFixture.Balance()));
        await using var a = OkxEeaDemoReadClient.ForTests(f, f.Clock, f.Secrets, new(f.Secrets.Binding, OkxHttpFixture.Uid), f.Clock.Delay, true);
        await using var b = OkxEeaDemoReadClient.ForTests(f, f.Clock, f.Secrets, new(f.Secrets.Binding, OkxHttpFixture.Uid), f.Clock.Delay, true);
        Assert.Equal(OkxReadStatus.Observed, (await a.GetConfigAsync(default)).Status); Assert.Equal(OkxReadStatus.Observed, (await b.GetConfigAsync(default)).Status); Assert.Equal(OkxReadStatus.Observed, (await a.GetBalancesAsync(default)).Status); Assert.Equal(OkxReadStatus.Observed, (await b.GetBalancesAsync(default)).Status);
        var sent = f.Sent.Where(s => s.Private).ToArray(); for (int i = 1; i < sent.Length; i++) Assert.True(sent[i].At - sent[i - 1].At >= TimeSpan.FromMilliseconds(600)); f.Done();
    }
    [Fact]
    public async Task ThirtySecondLogicalBudgetIncludesAttemptTimeAndRetryAfter()
    { using var f = new OkxHttpFixture(); f.Add("/api/v5/public/time", "{}", HttpStatusCode.TooManyRequests, retryAfter: "25"); f.Add("/api/v5/public/time", "{}", HttpStatusCode.TooManyRequests, retryAfter: "25"); await using var c = f.Client(false); var r = await c.GetTimeAsync(default); Assert.Equal("RateLimited", r.ErrorCode); Assert.Equal(2, f.Sent.Count); Assert.InRange((f.Clock.Now - f.Sent[0].At).TotalSeconds, 25, 26); }
    [Fact]
    public void AssemblyCannotIssueCoreAuthority()
    { var a = typeof(OkxEeaDemoReadClient).Assembly; Assert.DoesNotContain(a.GetReferencedAssemblies(), r => r.Name!.Contains("Apex.Core") || r.Name.Contains("Backtest")); Assert.Equal("Apex.Adapters.Okx.Tests", Assert.Single(a.GetCustomAttributes(typeof(System.Runtime.CompilerServices.InternalsVisibleToAttribute), false).Cast<System.Runtime.CompilerServices.InternalsVisibleToAttribute>()).AssemblyName); }

    [Fact]
    public async Task LateResponseCannotBecomeObservedAfterLogicalBudget()
    { using var f = new OkxHttpFixture(); f.Bootstrap(); f.Inspect = r => f.Clock.Now += TimeSpan.FromSeconds(31); await using var c = f.Client(false); var r = await c.GetTimeAsync(default); Assert.Equal(OkxReadStatus.Failed, r.Status); Assert.Equal("Timeout", r.ErrorCode); Assert.Single(f.Sent); }
    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task PrivateRedirectCannotForwardAuthentication(int status)
    { using var f = new OkxHttpFixture(); f.Bootstrap(); var time = f.Steps.Dequeue(); f.Steps.Clear(); f.Steps.Enqueue(time); f.Add("/api/v5/account/config", "", (HttpStatusCode)status, "https://www.okx.com/api/v5/account/config"); await using var c = f.Client(); Assert.Equal("RedirectDenied", (await c.GetBalancesAsync(default)).ErrorCode); Assert.Equal(2, f.Sent.Count); Assert.Equal(1, f.Sent.Count(s => s.Private)); }
    [Fact]
    public void LeaseOwnsAndErasesSecretBuffer()
    { var original = System.Text.Encoding.UTF8.GetBytes(PublicTestSecrets.Secret); var lease = OkxDemoCredentialLease.FromSecretStore("opaque", PublicTestSecrets.Key, original, PublicTestSecrets.Pass); var owned = (byte[])typeof(OkxDemoCredentialLease).GetField("secret", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(lease)!; Assert.NotSame(original, owned); lease.Dispose(); Assert.All(owned, b => Assert.Equal(0, b)); Assert.NotEqual(0, original[0]); using var request = new HttpRequestMessage(HttpMethod.Get, "https://eea.okx.com/api/v5/account/config"); Assert.Throws<ObjectDisposedException>(() => lease.Authenticate(request, "2026-10-30T12:00:00.000Z", "/api/v5/account/config")); Assert.DoesNotContain(typeof(OkxDemoCredentialLease).GetProperties(), p => p.Name != "BindingId"); }

    [Fact]
    public async Task InFlightUidReadAndSecondClientBootstrapShareBindingSerialization()
    {
        using var f = new OkxHttpFixture(); f.Bootstrap(); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Steps.Enqueue(("/api/v5/account/balance", async (r, ct) => { entered.SetResult(); await release.Task.WaitAsync(ct); return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(OkxHttpFixture.Envelope(OkxHttpFixture.Balance())) }; })); f.Bootstrap();
        await using var a = OkxEeaDemoReadClient.ForTests(f, f.Clock, f.Secrets, new(f.Secrets.Binding, OkxHttpFixture.Uid), f.Clock.Delay, true); await using var b = OkxEeaDemoReadClient.ForTests(f, f.Clock, f.Secrets, new(f.Secrets.Binding, OkxHttpFixture.Uid), f.Clock.Delay, true); await a.GetConfigAsync(default);
        var read = a.GetBalancesAsync(default); await entered.Task; var bootstrap = b.GetConfigAsync(default);
        try { await Task.Yield(); Assert.False(bootstrap.IsCompleted); Assert.Equal(2, f.Sent.Count(s => s.Private)); } finally { release.TrySetResult(); await read; await bootstrap; }
        f.Done();
    }

}
