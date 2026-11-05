using System.Net;
using System.Security.Cryptography;
using System.Text;
using Apex.Adapters.Okx.Execution;
using Xunit;
namespace Apex.Adapters.Okx.Tests;

// Separate from the accepted GET-only peer. No socket or public/private network.
internal sealed class OkxExecutionHttpFixture : HttpMessageHandler, IOkxSigningMaterialProvider, IOkxFinalDispatchFence
{
    internal readonly ManualClock Clock = new();
    internal OkxExecutionScope Scope = new("Demo", "123456", "123456", "USDC", "execution-test");
    internal const string Key = "SYNTHETIC_key", Secret = "SYNTHETIC_secret", Pass = "SYNTHETIC_pass";
    internal const string SubmitBody = """{"instId":"BTC-USDC","tdMode":"cash","side":"buy","ordType":"fok","sz":"0.01","px":"90000","clOrdId":"Ab9","tradeQuoteCcy":"USDC"}""";
    internal readonly Queue<(string Method,string Path,string? Body,Func<CancellationToken,Task<HttpResponseMessage>> Reply)> Steps = new();
    internal readonly List<(string Path,DateTimeOffset At,string Body)> Sent = [];
    internal int Opens, Fences;
    internal bool Allowed = true;
    internal string? ProvenanceOverride;
    internal OkxExecutionScope? CredentialScope;
    internal Func<TimeSpan,CancellationToken,Task>? Delay;
    internal Action? OnOpen;
    internal string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Key))).ToLowerInvariant();
    internal OkxExecutionClient Client() => OkxExecutionClient.ForTests(this, Clock, Scope, this, Fingerprint, this, Delay ?? Clock.Delay);
    internal OkxPreparedRequest Request(string id = "Ab9", DateTimeOffset? deadline = null) => OkxPreparedRequest.Submit(Scope, "BTC-USDC", "buy", "fok", "0.01", "90000", id, deadline ?? Clock.Now.AddSeconds(20));
    public ValueTask<OkxSigningMaterial> OpenAsync(CancellationToken ct)
    {
        Opens++; OnOpen?.Invoke(); ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new OkxSigningMaterial(CredentialScope ?? Scope, ProvenanceOverride ?? (Scope.Environment == "Demo" ? "DemoTradingApi" : "LiveAccountApi"), Encoding.UTF8.GetBytes(Key), Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(Pass)));
    }
    public bool CanDispatch(OkxPreparedRequest r, DateTimeOffset now) { Fences++; return Allowed; }
    internal static string Ack(string id = "Ab9", string code = "0", string top = "0", string order = "987654") => "{\"code\":\""+top+"\",\"msg\":\"\",\"data\":[{\"ordId\":\""+order+"\",\"clOrdId\":\""+id+"\",\"ts\":\"1793448000000\",\"sCode\":\""+code+"\",\"sMsg\":\"\",\"subCode\":\"\"}]}";
    internal static string Detail(string id = "Ab9", string order = "987654") => "{\"code\":\"0\",\"msg\":\"\",\"data\":[{\"instType\":\"SPOT\",\"instId\":\"BTC-USDC\",\"tdMode\":\"cash\",\"ordId\":\""+order+"\",\"clOrdId\":\""+id+"\",\"side\":\"buy\",\"state\":\"partially_filled\",\"sz\":\"0.01\",\"accFillSz\":\"0.005\",\"avgPx\":\"89000\",\"tradeQuoteCcy\":\"USDC\"}]}";
    internal void Add(string method, string path, string? body, string reply, HttpStatusCode status = HttpStatusCode.OK) => Steps.Enqueue((method,path,body,ct => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(reply) })));
    internal void SubmitReply(string reply, HttpStatusCode status = HttpStatusCode.OK) => Add("POST", "/api/v5/trade/order", SubmitBody, reply, status);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
    {
        Assert.NotEmpty(Steps); var step = Steps.Dequeue();
        Assert.Equal("https",r.RequestUri!.Scheme); Assert.Equal("eea.okx.com",r.RequestUri.Host); Assert.Equal(443,r.RequestUri.Port);
        Assert.Equal(step.Method,r.Method.Method); Assert.Equal(step.Path,r.RequestUri.PathAndQuery);
        var body = r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct); Assert.Equal(step.Body ?? "",body);
        Assert.Equal(Scope.Environment == "Demo" ? "1" : "0",Assert.Single(r.Headers.GetValues("x-simulated-trading")));
        Assert.Equal(Key,Assert.Single(r.Headers.GetValues("OK-ACCESS-KEY"))); Assert.Equal(Pass,Assert.Single(r.Headers.GetValues("OK-ACCESS-PASSPHRASE")));
        string stamp=Assert.Single(r.Headers.GetValues("OK-ACCESS-TIMESTAMP")); Assert.Equal(Clock.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'",System.Globalization.CultureInfo.InvariantCulture),stamp);
        var signature=Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret),Encoding.UTF8.GetBytes(stamp+step.Method+step.Path+body)));
        Assert.Equal(signature,Assert.Single(r.Headers.GetValues("OK-ACCESS-SIGN")));
        if (step.Path == "/api/v5/trade/order" && step.Method == "POST") Assert.True(long.Parse(Assert.Single(r.Headers.GetValues("expTime"))) > Clock.Now.ToUnixTimeMilliseconds());
        else Assert.False(r.Headers.Contains("expTime"));
        if (step.Method == "POST") Assert.Equal("application/json",r.Content!.Headers.ContentType!.MediaType);
        Sent.Add((step.Path,Clock.Now,body)); return await step.Reply(ct);
    }
    internal void Done() => Assert.Empty(Steps);
}
