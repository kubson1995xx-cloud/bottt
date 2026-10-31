using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Apex.Adapters.Okx.Execution;
using Xunit;
namespace Apex.Adapters.Okx.Tests;

public sealed class OkxExecutionTests
{
    [Theory]
    [InlineData("POST","/api/v5/trade/order","{\"note\":\"żółć\\path\"}","BMDJxkEBGbYXAoh7U368I1+KiXq9dKpY3NjwKuKyH3s=")]
    [InlineData("GET","/api/v5/trade/order?clOrdId=Ab9&instId=BTC-USDC","","fSWTEO/xQNGsVjd+94yb/0MZVzrOe8Pxiazo5Q7SEvU=")]
    public void IndependentPythonVectors(string method,string path,string body,string signature)
    {
        // Constants independently produced by Python hmac/hashlib, not the production signer.
        Assert.Equal(signature,OkxRequestSigner.Sign(Encoding.UTF8.GetBytes(OkxExecutionHttpFixture.Secret),"2026-10-31T12:00:00.123Z",method,path,Encoding.UTF8.GetBytes(body)));
        Assert.Equal("2026-10-31T12:00:00.123Z",OkxRequestSigner.Timestamp(new DateTimeOffset(2026,10,31,13,0,0,123,TimeSpan.FromHours(1))));
    }
    [Theory][InlineData("Demo")][InlineData("Live")]
    public async Task ExactWireAndDefensiveReplay(string environment)
    {
        using var f=new OkxExecutionHttpFixture(); f.Scope=new(environment,"123456","123456","USDC","execution-test");
        var prepared=f.Request(); var source=Encoding.UTF8.GetBytes(OkxExecutionHttpFixture.SubmitBody);
        var replay=OkxPreparedRequest.Restore(f.Scope,OkxExecutionOperation.Submit,"BTC-USDC","Ab9",null,prepared.Deadline,source);
        source[0]=0; var returned=replay.CopyBody(); if(returned.Length>0) returned[0]=0;
        Assert.Equal(OkxExecutionHttpFixture.SubmitBody,Encoding.UTF8.GetString(replay.CopyBody()));
        Assert.Equal(prepared.Digest,replay.Digest); Assert.Equal(prepared.Deadline,replay.Deadline);
        f.SubmitReply(OkxExecutionHttpFixture.Ack()); await using var c=f.Client();
        Assert.Equal(OkxWriteDisposition.Acknowledged,(await c.SubmitAsync(replay,default)).Disposition); Assert.Single(f.Sent); f.Done();
    }
    [Theory]
    [InlineData("Offline","123","123","USDC","a")][InlineData("0","123","123","USDC","a")]
    [InlineData("demo","123","123","USDC","a")][InlineData("Live","123","124","USDC","a")]
    [InlineData("Demo","x","x","USDC","a")][InlineData("Demo","123","123","usd","a")]
    [InlineData("Demo","123","123","USDC","../a")]
    public void InvalidScopeRefuses(string env,string uid,string main,string quote,string binding) => Assert.Throws<ArgumentException>(()=>new OkxExecutionScope(env,uid,main,quote,binding));

    [Theory]
    [InlineData("BTC-USDC-SWAP","buy","fok","1","2","Ab9")]
    [InlineData("BTC%2DUSDC","buy","fok","1","2","Ab9")]
    [InlineData("BTC-USDC","BUY","fok","1","2","Ab9")]
    [InlineData("BTC-USDC","buy","market","1","2","Ab9")]
    [InlineData("BTC-USDC","buy","ioc","1","2","Ab9")]
    [InlineData("BTC-USDC","buy","limit","1e-2","2","Ab9")]
    [InlineData("BTC-USDC","buy","limit","0","2","Ab9")]
    [InlineData("BTC-USDC","buy","limit","-1","2","Ab9")]
    [InlineData("BTC-USDC","buy","limit","1","0","Ab9")]
    [InlineData("BTC-USDC","buy","limit","0.00000000000000000000000000001","2","Ab9")]
    [InlineData("BTC-USDC","buy","limit","1","2","ab-9")]
    [InlineData("BTC-USDC","buy","limit","1","2","123456789012345678901234567890123")]
    public void InvalidOrderRefuses(string inst,string side,string type,string size,string px,string id)
    { using var f=new OkxExecutionHttpFixture(); Assert.Throws<ArgumentException>(()=>OkxPreparedRequest.Submit(f.Scope,inst,side,type,size,px,id,f.Clock.Now.AddSeconds(10))); }
    [Fact] public void InvalidDeadlineAndConditionalBracketRefuse()
    {
        using var f=new OkxExecutionHttpFixture();
        Assert.Throws<ArgumentException>(()=>f.Request(deadline:default(DateTimeOffset)));
        Assert.Throws<ArgumentException>(()=>f.Request(deadline:f.Clock.Now.ToOffset(TimeSpan.FromHours(1))));
        Assert.Throws<ArgumentException>(()=>OkxPreparedRequest.PlaceProtection(f.Scope,"BTC-USDC","conditional","0.01","Algo9","100000","80000",f.Clock.Now.AddSeconds(10)));
        Assert.Throws<ArgumentException>(()=>OkxPreparedRequest.Cancel(f.Scope,"BTC-USDC","bad%31","Ab9",f.Clock.Now.AddSeconds(10)));
        Assert.Throws<ArgumentException>(()=>OkxPreparedRequest.CancelProtection(f.Scope,"BTC-USDC",null,"",f.Clock.Now.AddSeconds(10)));
    }
    [Theory]
    [InlineData("\"tdMode\":\"cash\"","\"tdMode\":\"cross\"")]
    [InlineData("\"side\":\"buy\"","\"side\":\"buy\",\"reduceOnly\":true")]
    [InlineData("\"px\":\"90000\"","\"px\":90000")]
    [InlineData("\"sz\":\"0.01\"","\"sz\":\"0.01\",\"SZ\":\"1\"")]
    public void ReplayClosedGrammar(string from,string to)
    {
        using var f=new OkxExecutionHttpFixture(); var b=Encoding.UTF8.GetBytes(OkxExecutionHttpFixture.SubmitBody.Replace(from,to));
        Assert.Throws<ArgumentException>(()=>OkxPreparedRequest.Restore(f.Scope,OkxExecutionOperation.Submit,"BTC-USDC","Ab9",null,f.Clock.Now.AddSeconds(10),b));
    }
    [Fact] public async Task CancelAndProtectionUseSeparateIdentitiesAndAreOnlyAcknowledgements()
    {
        using var f=new OkxExecutionHttpFixture(); await using var c=f.Client(); var d=f.Clock.Now.AddSeconds(20);
        var cancel=OkxPreparedRequest.Cancel(f.Scope,"BTC-USDC","987654","Ab9",d);
        f.Add("POST","/api/v5/trade/cancel-order","""{"instId":"BTC-USDC","ordId":"987654","clOrdId":"Ab9"}""",OkxExecutionHttpFixture.Ack());
        Assert.Equal(OkxWriteDisposition.Acknowledged,(await c.CancelAsync(cancel,default)).Disposition);
        var p=OkxPreparedRequest.PlaceProtection(f.Scope,"BTC-USDC","oco","0.01","Algo9","100000","80000",d);
        f.Add("POST","/api/v5/trade/order-algo","""{"instId":"BTC-USDC","tdMode":"cash","side":"sell","ordType":"oco","sz":"0.01","algoClOrdId":"Algo9","tradeQuoteCcy":"USDC","tpTriggerPx":"100000","tpOrdPx":"-1","tpTriggerPxType":"last","slTriggerPx":"80000","slOrdPx":"-1","slTriggerPxType":"last"}""","""{"code":"0","msg":"","data":[{"algoId":"765432","algoClOrdId":"Algo9","sCode":"0","sMsg":""}]}""");
        Assert.Equal(OkxWriteDisposition.Acknowledged,(await c.PlaceProtectionAsync(p,default)).Disposition);
        var cp=OkxPreparedRequest.CancelProtection(f.Scope,"BTC-USDC","765432","Algo9",d);
        f.Add("POST","/api/v5/trade/cancel-algos","""[{"instId":"BTC-USDC","algoId":"765432","algoClOrdId":"Algo9"}]""","""{"code":"0","msg":"","data":[{"algoId":"765432","algoClOrdId":"","clOrdId":"","sCode":"0","sMsg":"","tag":""}]}""");
        Assert.Equal(OkxWriteDisposition.Acknowledged,(await c.CancelProtectionAsync(cp,default)).Disposition); Assert.Equal(3,f.Sent.Count); f.Done();
    }
    [Theory]
    [InlineData("50004","1")][InlineData("51016","1")][InlineData("50011","1")][InlineData("59113","1")]
    public async Task AmbiguousBusinessErrorsNeverRetry(string scode,string top)
    { using var f=new OkxExecutionHttpFixture(); f.SubmitReply(OkxExecutionHttpFixture.Ack(code:scode,top:top,order:"")); await using var c=f.Client(); var o=await c.SubmitAsync(f.Request(),default); Assert.Equal(OkxWriteDisposition.Unknown,o.Disposition); Assert.Equal(scode,o.SCode); Assert.Single(f.Sent); f.Done(); }
    [Theory][InlineData(302)][InlineData(429)][InlineData(400)][InlineData(503)]
    public async Task HttpFailureNeverRetriesOrProvesRejection(int status)
    { using var f=new OkxExecutionHttpFixture(); f.SubmitReply("PRIVATE_RESPONSE_SENTINEL",(HttpStatusCode)status); await using var c=f.Client(); var o=await c.SubmitAsync(f.Request(),default); Assert.Equal(OkxWriteDisposition.Unknown,o.Disposition); Assert.DoesNotContain("PRIVATE_RESPONSE",JsonSerializer.Serialize(o)); Assert.Single(f.Sent); f.Done(); }
    [Theory]
    [InlineData("{\"code\":\"0\",\"msg\":\"\",\"data\":[]}")]
    [InlineData("{\"code\":\"0\",\"msg\":\"\",\"data\":null}")]
    [InlineData("{\"code\":0,\"msg\":\"\",\"data\":[]}")]
    [InlineData("{\"code\":\"0\",\"Code\":\"0\",\"msg\":\"\",\"data\":[]}")]
    [InlineData("{\"code\":\"0\",\"msg\":\"\\uD800\",\"data\":[]}")]
    public async Task MalformedPayloadCannotAcknowledge(string body)
    { using var f=new OkxExecutionHttpFixture(); f.SubmitReply(body); await using var c=f.Client(); Assert.Equal(OkxWriteDisposition.Unknown,(await c.SubmitAsync(f.Request(),default)).Disposition); Assert.Single(f.Sent); f.Done(); }
    [Theory][InlineData("wrong")][InlineData("ab9")]
    public async Task MismatchedIdentityCannotAcknowledge(string id)
    { using var f=new OkxExecutionHttpFixture(); f.SubmitReply(OkxExecutionHttpFixture.Ack(id)); await using var c=f.Client(); Assert.Equal(OkxWriteDisposition.Unknown,(await c.SubmitAsync(f.Request(),default)).Disposition); f.Done(); }
    [Fact] public async Task MatchingDocumentedParameterRejectionIsNarrow()
    { using var f=new OkxExecutionHttpFixture(); f.SubmitReply(OkxExecutionHttpFixture.Ack(code:"51000",top:"1",order:"")); await using var c=f.Client(); var o=await c.SubmitAsync(f.Request(),default); Assert.Equal(OkxWriteDisposition.Rejected,o.Disposition); Assert.Equal("51000",o.SCode); f.Done(); }
    [Theory][InlineData("socket")][InlineData("cancel")][InlineData("timeout")]
    public async Task AfterHandlerInvocationIsUnknown(string failure)
    {
        using var f=new OkxExecutionHttpFixture(); using var cancel=new CancellationTokenSource();
        f.Steps.Enqueue(("POST","/api/v5/trade/order",OkxExecutionHttpFixture.SubmitBody,ct=>{
            if(failure=="cancel") { cancel.Cancel(); return Task.FromCanceled<HttpResponseMessage>(cancel.Token); }
            if(failure=="timeout") return Task.FromException<HttpResponseMessage>(new OperationCanceledException("PRIVATE_EXCEPTION"));
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("PRIVATE_EXCEPTION")); }));
        await using var c=f.Client(); var o=await c.SubmitAsync(f.Request(),cancel.Token); Assert.Equal(OkxWriteDisposition.Unknown,o.Disposition); Assert.DoesNotContain("PRIVATE_EXCEPTION",JsonSerializer.Serialize(o)); Assert.Single(f.Sent); f.Done();
    }
    [Fact] public async Task WrongOperationScopeCredentialAndExpiredRequestsNeverSend()
    {
        using var f=new OkxExecutionHttpFixture(); await using var c=f.Client();
        Assert.Equal(OkxWriteDisposition.ProvenNotSent,(await c.CancelAsync(f.Request(),default)).Disposition);
        var other=new OkxExecutionScope("Live","123456","123456","USDC","execution-test");
        Assert.Equal(OkxWriteDisposition.ProvenNotSent,(await c.SubmitAsync(OkxPreparedRequest.Submit(other,"BTC-USDC","buy","fok","1","2","X",f.Clock.Now.AddSeconds(2)),default)).Disposition);
        Assert.Equal(OkxWriteDisposition.ProvenNotSent,(await c.SubmitAsync(f.Request(deadline:f.Clock.Now.AddSeconds(-1)),default)).Disposition);
        using var token=new CancellationTokenSource(); token.Cancel(); Assert.Equal(OkxWriteDisposition.ProvenNotSent,(await c.SubmitAsync(f.Request(),token.Token)).Disposition);
        Assert.Empty(f.Sent);
    }
    [Theory][InlineData(true)][InlineData(false)]
    public async Task CredentialBindingAndProvenanceRefuseBeforeSend(bool scope)
    {
        using var f=new OkxExecutionHttpFixture(); if(scope) f.CredentialScope=new("Live","123456","123456","USDC","execution-test"); else f.ProvenanceOverride="LiveAccountApi";
        await using var c=f.Client(); var o=await c.SubmitAsync(f.Request(),default); Assert.Equal(OkxWriteDisposition.ProvenNotSent,o.Disposition); Assert.Equal(1,f.Opens); Assert.Empty(f.Sent);
    }
    [Theory][InlineData(null)][InlineData("987654")]
    public async Task LookupSupportsOriginalClientIdAndOrderIdPrecedence(string? order)
    {
        using var f=new OkxExecutionHttpFixture(); string path="/api/v5/trade/order?clOrdId=Ab9&instId=BTC-USDC"+(order is null?"":"&ordId=987654");
        f.Add("GET",path,null,OkxExecutionHttpFixture.Detail(order is null?"Ab9":"DifferentClient")); await using var c=f.Client(); var o=await c.LookupAsync("BTC-USDC",order,"Ab9",default);
        Assert.Equal("Observed",o.Reason); Assert.Equal("partially_filled",o.State); Assert.Equal("0.005",o.AccFillSize); Assert.Equal("987654",o.OrderId); f.Done();
    }
    [Theory][InlineData("{\"code\":\"51603\",\"msg\":\"Order does not exist\",\"data\":[]}")][InlineData("{\"code\":\"0\",\"msg\":\"\",\"data\":[]}")]
    public async Task LookupAbsenceIsUnknown(string response)
    { using var f=new OkxExecutionHttpFixture(); f.Add("GET","/api/v5/trade/order?clOrdId=Ab9&instId=BTC-USDC",null,response); await using var c=f.Client(); var o=await c.LookupAsync("BTC-USDC",null,"Ab9",default); Assert.Equal("Unknown",o.Reason); Assert.Null(o.State); Assert.Single(f.Sent); f.Done(); }
    [Fact] public void CredentialsOwnBuffersEraseAndRefuseSerialization()
    {
        using var f=new OkxExecutionHttpFixture(); var key=Encoding.UTF8.GetBytes(OkxExecutionHttpFixture.Key); var secret=Encoding.UTF8.GetBytes(OkxExecutionHttpFixture.Secret); var pass=Encoding.UTF8.GetBytes(OkxExecutionHttpFixture.Pass);
        var m=new OkxSigningMaterial(f.Scope,"DemoTradingApi",key,secret,pass); key[0]=0; secret[0]=0; pass[0]=0;
        Assert.Equal(Encoding.UTF8.GetBytes(OkxExecutionHttpFixture.Key),m.Key); Assert.Equal(f.Fingerprint,m.Fingerprint);
        Assert.Throws<NotSupportedException>(()=>JsonSerializer.Serialize(m,new JsonSerializerOptions { IncludeFields=true }));
        m.Dispose(); Assert.All(m.Key,b=>Assert.Equal(0,b)); Assert.All(m.Secret,b=>Assert.Equal(0,b)); Assert.All(m.Passphrase,b=>Assert.Equal(0,b));
    }
    [Fact] public async Task DiagnosticsRefusedBeforeOpeningSecrets()
    {
        using var diagnostics=new DiagnosticListener("HttpHandlerDiagnosticListener"); using var sub=diagnostics.Subscribe(new IgnoreEvents()); using var f=new OkxExecutionHttpFixture(); await using var c=f.Client();
        var o=await c.SubmitAsync(f.Request(),default); Assert.Equal(OkxWriteDisposition.ProvenNotSent,o.Disposition); Assert.Equal("UnsafeHttpDiagnostics",o.Reason); Assert.Equal(0,f.Opens); Assert.Empty(f.Sent);
    }
    private sealed class IgnoreEvents : IObserver<KeyValuePair<string,object?>> { public void OnNext(KeyValuePair<string,object?> v) {} public void OnCompleted(){} public void OnError(Exception e){} }
}
