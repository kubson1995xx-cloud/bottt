using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Apex.Adapters.Okx.Execution;
using Xunit;
namespace Apex.Adapters.Okx.Tests;

// Separate UTC/monotonic clocks deliberately permit rollback and elapsed-budget interleavings.
internal sealed class ObservationTestClock : TimeProvider
{
    internal DateTimeOffset Utc=new(2026,11,1,12,0,0,TimeSpan.Zero);
    internal long Mono;
    internal Action? OnUtcRead;
    public override DateTimeOffset GetUtcNow() { OnUtcRead?.Invoke();return Utc; }
    public override long GetTimestamp()=>Mono;
    public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
    internal void Advance(TimeSpan t) { Utc+=t;Mono+=t.Ticks; }
}
internal sealed class OkxExecutionObservationHttpFixture : HttpMessageHandler, IOkxSigningMaterialProvider, IOkxObservationReadFence
{
    internal const string Key="observation-synthetic-key", Secret="observation-synthetic-secret", Pass="observation-synthetic-pass";
    internal readonly ObservationTestClock Clock=new();
    internal OkxExecutionScope Scope;
    private static long uid=700000;
    internal static string NextUid()=>Interlocked.Increment(ref uid).ToString();
    internal readonly OkxObservationContext Context=new(Guid.NewGuid(),Guid.NewGuid());
    internal readonly Queue<(string Path,Func<CancellationToken,Task<HttpResponseMessage>> Reply)> Steps=new();
    internal readonly List<(string Path,DateTimeOffset Utc,long Mono)> Sent=[];
    internal int Opens;
    internal bool Allowed=true;
    internal Action? OnOpen, OnFence;
    internal Action<string>? OnAuthenticatedRequest;
    internal Func<DateTimeOffset>? ExpectedSigningUtc;
    internal Exception? ProviderError;
    internal OkxExecutionScope? MaterialScope;
    internal OkxSigningMaterial? LastMaterial;
    internal Func<TimeSpan,CancellationToken,Task>? Delay;
    internal string Fingerprint=>Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(Key))).ToLowerInvariant();
    internal OkxExecutionObservationHttpFixture()
    { var id=NextUid();Scope=new("Demo",id,id,"USD","observation"); }
    internal OkxExecutionObservationHttpFixture(ObservationTestClock clock):this(){Clock=clock;}
    internal OkxExecutionObservationClient Client()=>OkxExecutionObservationClient.ForTests(this,Clock,Scope,this,Fingerprint,this,Delay??((t,ct)=>{ct.ThrowIfCancellationRequested();Clock.Advance(t);return Task.CompletedTask;}));
    public ValueTask<OkxSigningMaterial> OpenAsync(CancellationToken ct)
    { Opens++;OnOpen?.Invoke();if(ProviderError is not null)throw ProviderError;LastMaterial=new OkxSigningMaterial(MaterialScope??Scope,Scope.Environment=="Demo"?"DemoTradingApi":"LiveAccountApi",Encoding.ASCII.GetBytes(Key),Encoding.ASCII.GetBytes(Secret),Encoding.ASCII.GetBytes(Pass));return ValueTask.FromResult(LastMaterial); }
    public bool CanObserve(OkxExecutionScope scope,OkxObservationContext context,DateTimeOffset utc)
    { Assert.Equal(Scope,scope);Assert.Equal(Context,context);OnFence?.Invoke();return Allowed; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        Assert.Equal(HttpMethod.Get,request.Method);Assert.Null(request.Content);
        Assert.Equal("https",request.RequestUri!.Scheme);Assert.Equal("eea.okx.com",request.RequestUri.Host);Assert.Equal(443,request.RequestUri.Port);
        Assert.Equal(Scope.Environment=="Demo"?"1":"0",request.Headers.GetValues("x-simulated-trading").Single());
        var path=request.RequestUri.PathAndQuery;
        if(path=="/api/v5/public/time")Assert.DoesNotContain(request.Headers,h=>h.Key.StartsWith("OK-ACCESS-",StringComparison.OrdinalIgnoreCase));
        else
        {
            Assert.Equal(Key,request.Headers.GetValues("OK-ACCESS-KEY").Single());Assert.Equal(Pass,request.Headers.GetValues("OK-ACCESS-PASSPHRASE").Single());
            var stamp=request.Headers.GetValues("OK-ACCESS-TIMESTAMP").Single();
            Assert.Equal((ExpectedSigningUtc?.Invoke()??Clock.Utc).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'",System.Globalization.CultureInfo.InvariantCulture),stamp);
            Assert.Equal(new[]{"OK-ACCESS-KEY","OK-ACCESS-PASSPHRASE","OK-ACCESS-SIGN","OK-ACCESS-TIMESTAMP","x-simulated-trading"}.OrderBy(x=>x,StringComparer.Ordinal),request.Headers.Select(x=>x.Key).OrderBy(x=>x,StringComparer.Ordinal));
            // Independent BCL HMAC, no use of production signer/query builder/parser.
            var expected=Convert.ToBase64String(HMACSHA256.HashData(Encoding.ASCII.GetBytes(Secret),Encoding.UTF8.GetBytes(stamp+"GET"+path)));
            Assert.Equal(expected,request.Headers.GetValues("OK-ACCESS-SIGN").Single());
            OnAuthenticatedRequest?.Invoke(stamp);
        }
        Assert.NotEmpty(Steps);var step=Steps.Dequeue();Assert.Equal(step.Path,path);
        Sent.Add((path,Clock.Utc,Clock.Mono));return step.Reply(ct);
    }
    internal void Add(string name,string? body=null,int status=200)
    { var path=PathFor(name);Steps.Enqueue((path,_=>Task.FromResult(new HttpResponseMessage((HttpStatusCode)status){Content=new StringContent(body??Control(name),Encoding.UTF8,"application/json")}))); }
    internal void Done()=>Assert.Empty(Steps);
    internal static string PathFor(string name)=>name switch {"account"=>"/api/v5/account/config","balance"=>"/api/v5/account/balance","instruments"=>"/api/v5/account/instruments?instType=SPOT","fees"=>"/api/v5/account/trade-fee?instId=BTC-USDC&instType=SPOT",_=>"/api/v5/public/time"};
    internal static JsonNode Oracle()
    {
        var dir=new DirectoryInfo(AppContext.BaseDirectory);
        while(dir is not null && !File.Exists(Path.Combine(dir.FullName,"tests/Apex.Adapters.Okx.Tests/Fixtures/ExecutionObservations/baseline-schema.json")))dir=dir.Parent;
        return JsonNode.Parse(File.ReadAllText(Path.Combine(dir!.FullName,"tests/Apex.Adapters.Okx.Tests/Fixtures/ExecutionObservations/baseline-schema.json")))!;
    }
    internal static string Control(string name)=>Oracle()["endpoints"]![name]!["control"]!.ToJsonString();
}
