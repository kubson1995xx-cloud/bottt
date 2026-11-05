using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Apex.Adapters.Okx.Execution;
using Xunit;
namespace Apex.Tests;

public sealed class OkxExactInstrumentResponseDigestTests
{
    public OkxExactInstrumentResponseDigestTests()=>_=new OkxBalanceSchemaDiagnosticTests();
    // Stripping empty properties before hashing would keep selected DTOs correct
    // while corrupting observation evidence. Assert the actual signed-read digest
    // equals independently hashed exact wire bytes, not a projected DTO digest.
    [Fact]
    public async Task Actual_observation_raw_response_digest_includes_empty_extensions_without_DTO_changes()
    {
        var digests=new List<string>();string? selected=null;
        using var peer=new Peer();
        await using var client=OkxExecutionObservationClient.ForTests(peer,TimeProvider.System,peer.Scope,peer,peer.Fingerprint,peer);
        for(int mask=0;mask<4;mask++)
        {
            peer.Wire=new JsonObject{["code"]="0",["msg"]="",["data"]=new JsonArray(OkxExactInstrumentCompatibilityTests.Row(mask))}.ToJsonString();
            var result=await client.ReadInstrumentsAsync(peer.Context,default);
            Assert.Equal(OkxObservationStatus.Observed,result.Status);Assert.Null(result.ErrorCode);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(peer.Wire))).ToLowerInvariant(),result.ResponseDigest);
            digests.Add(result.ResponseDigest!);string dto=JsonSerializer.Serialize(Assert.Single(result.Rows));
            if(selected is null)selected=dto;else Assert.Equal(selected,dto);
        }
        Assert.Equal(4,digests.Distinct(StringComparer.Ordinal).Count());Assert.Equal(4,peer.Count);
    }
    private sealed class Peer:HttpMessageHandler,IOkxSigningMaterialProvider,IOkxObservationReadFence
    {
        private static readonly byte[] Key=Encoding.ASCII.GetBytes("digest-synthetic-key"),Secret=Encoding.ASCII.GetBytes("digest-synthetic-secret"),Pass=Encoding.ASCII.GetBytes("digest-synthetic-pass");
        internal readonly OkxExecutionScope Scope;
        internal readonly OkxObservationContext Context=new(Guid.NewGuid(),Guid.NewGuid());
        internal string Wire="";internal int Count;
        internal string Fingerprint=>Convert.ToHexString(SHA256.HashData(Key)).ToLowerInvariant();
        internal Peer(){string uid=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();Scope=new("Demo",uid,uid,"USDC","synthetic-digest");}
        public ValueTask<OkxSigningMaterial> OpenAsync(CancellationToken ct)=>ValueTask.FromResult(new OkxSigningMaterial(Scope,"DemoTradingApi",Key.ToArray(),Secret.ToArray(),Pass.ToArray()));
        public bool CanObserve(OkxExecutionScope s,OkxObservationContext c,DateTimeOffset utc){Assert.Equal(Scope,s);Assert.Equal(Context,c);return true;}
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage q,CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get,q.Method);Assert.Null(q.Content);Assert.Equal("https",q.RequestUri!.Scheme);Assert.Equal("eea.okx.com",q.RequestUri.Host);Assert.Equal(443,q.RequestUri.Port);
            Assert.Equal("/api/v5/account/instruments?instType=SPOT",q.RequestUri.PathAndQuery);Assert.Equal("1",q.Headers.GetValues("x-simulated-trading").Single());
            string stamp=q.Headers.GetValues("OK-ACCESS-TIMESTAMP").Single();Assert.Equal(Convert.ToBase64String(HMACSHA256.HashData(Secret,Encoding.ASCII.GetBytes(stamp+"GET"+q.RequestUri.PathAndQuery))),q.Headers.GetValues("OK-ACCESS-SIGN").Single());
            Assert.Equal(Encoding.ASCII.GetString(Key),q.Headers.GetValues("OK-ACCESS-KEY").Single());Assert.Equal(Encoding.ASCII.GetString(Pass),q.Headers.GetValues("OK-ACCESS-PASSPHRASE").Single());
            Assert.True(++Count<=4);return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(Wire,Encoding.UTF8,"application/json")});
        }
    }
}
