using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Apex.Adapters.Okx;
using Xunit;
[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace Apex.Adapters.Okx.Tests;
internal sealed class ManualClock : TimeProvider
{
    internal DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public override long GetTimestamp() => Now.UtcTicks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    internal Task Delay(TimeSpan duration, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Now += duration; return Task.CompletedTask; }
}
internal sealed class PublicTestSecrets : IOkxDemoSecretProvider
{
    internal int Opens;
    internal string Binding = Guid.NewGuid().ToString("N");
    internal const string Key = "PUBLIC_TEST_KEY_not_a_credential", Secret = "PUBLIC_TEST_SECRET_not_a_credential", Pass = "PUBLIC_TEST_PASSPHRASE_not_a_credential";
    internal Func<CancellationToken, ValueTask<OkxDemoCredentialLease>>? Open;
    public ValueTask<OkxDemoCredentialLease> OpenAsync(CancellationToken ct) { Opens++; ct.ThrowIfCancellationRequested(); return Open?.Invoke(ct) ?? ValueTask.FromResult(OkxDemoCredentialLease.FromSecretStore(Binding, Key, Encoding.UTF8.GetBytes(Secret), Pass)); }
}
internal sealed class OkxHttpFixture : HttpMessageHandler
{
    internal readonly ManualClock Clock = new();
    internal readonly PublicTestSecrets Secrets = new();
    internal readonly Queue<(string Path, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Reply)> Steps = new();
    internal readonly List<(string Path, DateTimeOffset At, bool Private, string? Timestamp, string? Signature)> Sent = [];
    internal Action<HttpRequestMessage>? Inspect;
    internal OkxEeaDemoReadClient Client(bool privateReader = true) => OkxEeaDemoReadClient.ForTests(this, Clock, privateReader ? Secrets : null, privateReader ? new(Secrets.Binding, Uid) : null, Clock.Delay);
    internal const string Uid = "999999999999999999999999999999999";
    internal static string Envelope(params string[] rows) => "{\"code\":\"0\",\"data\":[" + string.Join(',', rows) + "]}";
    internal static string Config(string? changes = null) => changes ?? "{\"uid\":\"" + Uid + "\",\"mainUid\":\"main-1\",\"acctLv\":\"1\",\"perm\":\"read_only\",\"ip\":\"192.0.2.4\",\"feeType\":\"0\",\"autoLoan\":false,\"enableSpotBorrow\":false}";
    internal static string Balance(string cash = "100", string liab = "0") => "{\"uTime\":\"1793361600000\",\"details\":[{\"ccy\":\"USD\",\"cashBal\":\"" + cash + "\",\"availBal\":\"100\",\"frozenBal\":\"0\",\"ordFrozen\":\"0\",\"eq\":\"100\",\"liab\":\"" + liab + "\"}]}";
    internal const string Instrument = """{"instId":"123-Ab","instType":"SPOT","baseCcy":"ABC","quoteCcy":"USD","tradeQuoteCcyList":["USD"],"state":"live","tickSz":"0.01","lotSz":"0.001","minSz":"0.01","listTime":"1600000000000","expTime":"","groupId":"1","maxLmtSz":"100"}""";
    internal static string Order(string id = "ord-9999999999999999999999", string state = "live", string type = "SPOT", string mode = "cash") => JsonSerializer.Serialize(new { instType = type, instId = "123-Ab", ordId = id, clOrdId = "same-client", state, side = "buy", tdMode = mode, sz = "1", accFillSz = "0", avgPx = "", cTime = "1793361500000", uTime = "1793361500000", tradeQuoteCcy = "USD" });
    internal static string Fill(string bill = "bill-B", string trade = "trade-A", string fee = "-0.00000001", string ccy = "ABC", string price = "10", string ts = "1793361590000", string fillTime = "1793361580000") => JsonSerializer.Serialize(new { instType = "SPOT", instId = "123-Ab", ordId = "ord-9999999999999999999999", clOrdId = "same-client", tradeId = trade, billId = bill, side = "buy", fillPx = price, fillSz = "1", fee, feeCcy = ccy, feeRate = "", execType = "M", fillTime, ts, tradeQuoteCcy = "USD" });
    internal static string Bill(string id = "bill-B", string change = "-10", string balance = "90", string type = "2") => JsonSerializer.Serialize(new { billId = id, ccy = "USD", bal = balance, balChg = change, fee = "0", ordId = "ord-9999999999999999999999", tradeId = "trade-A", instId = "123-Ab", type, subType = "1", ts = "1793361590000" });
    internal void Add(string path, string body, HttpStatusCode status = HttpStatusCode.OK, string? location = null, string? retryAfter = null) => Steps.Enqueue((path, (r, ct) => { var response = new HttpResponseMessage(status) { Content = new StringContent(body) }; if (location != null) response.Headers.Location = new Uri(location); if (retryAfter != null) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter); return Task.FromResult(response); }));
    internal void AddError(string path, Exception error) => Steps.Enqueue((path, (r, ct) => Task.FromException<HttpResponseMessage>(error)));
    internal void Bootstrap(string? config = null) { Steps.Enqueue(("/api/v5/public/time", (r, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Envelope("{\"ts\":\"" + Clock.Now.ToUnixTimeMilliseconds() + "\"}")) }))); Add("/api/v5/account/config", Envelope(config ?? Config())); }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Assert.NotEmpty(Steps); var step = Steps.Dequeue(); Assert.Equal(step.Path, request.RequestUri!.PathAndQuery);
        Assert.Equal("https", request.RequestUri.Scheme); Assert.Equal("eea.okx.com", request.RequestUri.Host); Assert.Equal(443, request.RequestUri.Port); Assert.Equal(HttpMethod.Get, request.Method); Assert.Null(request.Content);
        Assert.Equal(new[] { "1" }, request.Headers.GetValues("x-simulated-trading"));
        var priv = !request.RequestUri.AbsolutePath.StartsWith("/api/v5/public/", StringComparison.Ordinal);
        string? timestamp = null, signature = null;
        if (priv) { Assert.Equal(PublicTestSecrets.Key, Assert.Single(request.Headers.GetValues("OK-ACCESS-KEY"))); Assert.Equal(PublicTestSecrets.Pass, Assert.Single(request.Headers.GetValues("OK-ACCESS-PASSPHRASE"))); timestamp = Assert.Single(request.Headers.GetValues("OK-ACCESS-TIMESTAMP")); signature = Assert.Single(request.Headers.GetValues("OK-ACCESS-SIGN")); var expected = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(PublicTestSecrets.Secret), Encoding.UTF8.GetBytes(timestamp + "GET" + step.Path))); Assert.Equal(expected, signature); }
        else Assert.DoesNotContain(request.Headers, h => h.Key.StartsWith("OK-ACCESS-", StringComparison.OrdinalIgnoreCase));
        Sent.Add((step.Path, Clock.Now, priv, timestamp, signature)); Inspect?.Invoke(request); return step.Reply(request, ct);
    }
    internal void Done() => Assert.Empty(Steps);
    internal void CaptureScript(bool moved = false, string? fill = null, string? bill = null, bool failFills = false)
    {
        Bootstrap(); Add("/api/v5/account/instruments?instType=SPOT", Envelope(Instrument)); Add("/api/v5/account/balance", Envelope(Balance()));
        Add("/api/v5/trade/orders-pending?limit=100", Envelope());
        var q = "begin=1793361540000&end=1793361600000&instType=SPOT&limit=100";
        if (failFills) for (var i = 0; i < 3; i++) AddError("/api/v5/trade/fills?" + q, new HttpRequestException("synthetic disconnect"));
        else { Add("/api/v5/trade/fills?" + q, Envelope(fill ?? Fill())); Add("/api/v5/trade/fills?after=bill-B&" + q, Envelope()); }
        var bq = "begin=1793361540000&end=1793361600000&limit=100";
        Add("/api/v5/account/bills?" + bq, Envelope(bill ?? Bill())); Add("/api/v5/account/bills?after=bill-B&" + bq, Envelope());
        Add("/api/v5/account/balance", Envelope(Balance(moved ? "90" : "100"))); Add("/api/v5/trade/orders-pending?limit=100", Envelope()); Add("/api/v5/account/config", Envelope(Config()));
    }
    internal OkxSnapshotRequest SnapshotRequest() => new(Clock.Now.AddMinutes(-1), Clock.Now, Array.Empty<OkxOrderRef>());
}
