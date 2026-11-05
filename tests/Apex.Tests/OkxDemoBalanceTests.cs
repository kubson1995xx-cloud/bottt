using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.Tests;
public sealed class OkxDemoBalanceTests
{
    private static OkxScopeBinding Binding(NavalEnvironment env=NavalEnvironment.OkxDemo)=>CredentialFixture.Binding(env);
    private static CredentialFixture Printable()
    {
        var f=new CredentialFixture();var key=Encoding.UTF8.GetBytes(Convert.ToHexString(f.Key));var pass=Encoding.UTF8.GetBytes(Convert.ToHexString(f.Passphrase));CryptographicOperations.ZeroMemory(f.Key);CryptographicOperations.ZeroMemory(f.Passphrase);f.Key=key;f.Passphrase=pass;return f;
    }
    private static OkxExecutionCredentialLease Lease(OkxScopeBinding b,CredentialFixture f)=>new(b,"DemoTradingApi",f.Key.ToArray(),f.Secret.ToArray(),f.Passphrase.ToArray());
    [Theory]
    [InlineData("valid","DEMO_TEST_OBSERVED_READ_ONLY_STOP",2)]
    [InlineData("trade","DEMO_TEST_OBSERVED_READ_ONLY_STOP",2)]
    [InlineData("reverse","DEMO_TEST_OBSERVED_READ_ONLY_STOP",2)]
    [InlineData("nullable","DEMO_TEST_OBSERVED_READ_ONLY_STOP",2)]
    [InlineData("uid","DEMO_TEST_ACCOUNT_BINDING_MISMATCH",1)]
    [InlineData("main","DEMO_TEST_ACCOUNT_BINDING_MISMATCH",1)]
    [InlineData("withdraw","DEMO_TEST_PRIVATE_AUTH_UnsafeAccountConfig",1)]
    [InlineData("unknown","DEMO_TEST_PRIVATE_AUTH_UnsafeAccountConfig",1)]
    [InlineData("trade-only","DEMO_TEST_PRIVATE_AUTH_UnsafeAccountConfig",1)]
    [InlineData("duplicate","DEMO_TEST_PRIVATE_AUTH_UnsafeAccountConfig",1)]
    [InlineData("empty-perm","DEMO_TEST_PRIVATE_AUTH_UnsafeAccountConfig",1)]
    [InlineData("spaces","DEMO_TEST_PRIVATE_AUTH_UnsafeAccountConfig",1)]
    [InlineData("margin","DEMO_TEST_PRIVATE_AUTH_UnsafeAccountConfig",1)]
    [InlineData("autoLoan","DEMO_TEST_PRIVATE_AUTH_UnsafeAccountConfig",1)]
    [InlineData("enableSpotBorrow","DEMO_TEST_PRIVATE_AUTH_UnsafeAccountConfig",1)]
    [InlineData("spotBorrowAutoRepay","DEMO_TEST_PRIVATE_AUTH_UnsafeAccountConfig",1)]
    [InlineData("ip","DEMO_TEST_PRIVATE_AUTH_UnsafeAccountConfig",1)]
    [InlineData("auth","DEMO_TEST_PRIVATE_AUTH_50119",1)]
    [InlineData("public","DEMO_TEST_PUBLIC_TIME_InvalidPayload",0)]
    [InlineData("clock","DEMO_TEST_PUBLIC_TIME_ClockPreflightFailed",0)]
    [InlineData("rtt","DEMO_TEST_PUBLIC_TIME_ClockPreflightFailed",0)]
    [InlineData("cancel","DEMO_TEST_PUBLIC_TIME_CanceledOrTimeout",0)]
    [InlineData("balance-api","DEMO_TEST_BALANCE_50119",2)]
    [InlineData("currency","DEMO_TEST_QUOTE_BALANCE_UNAVAILABLE",2)]
    [InlineData("duplicate-currency","DEMO_TEST_QUOTE_BALANCE_UNAVAILABLE",2)]
    [InlineData("bad-cash","DEMO_TEST_BALANCE_InvalidPayload",2)]
    public async Task Real_scoped_reader_and_signer_only_publish_observations(string mode,string expected,int privateSends)
    {
        var binding=Binding();using var material=Printable();var peer=new Peer(binding,material,mode);
        var result=await OkxDemoBalanceTest.RunAsync(binding,()=>Lease(binding,material),default,
            (scope,provider,fp,fence)=>OkxExecutionObservationClient.ForTests(peer,TimeProvider.System,scope,provider,fp,fence));
        Assert.Equal(expected,result.Code);Assert.Equal(privateSends,peer.PrivateSends);Assert.Empty(peer.Violations);
        Assert.All(peer.Paths,p=>Assert.Contains(p,new[]{"/api/v5/public/time","/api/v5/account/config","/api/v5/account/balance"}));
        if(mode is "valid" or "trade" or "reverse")
        {Assert.Equal("100",result.Cash);Assert.Equal("90",result.Available);Assert.Equal(binding.Account.AccountId,result.Uid);Assert.Equal("OkxDemo",result.Environment);Assert.Equal(binding.Account.QuoteCurrency,result.Currency);Assert.True(result.ObservedUtc>=peer.BalanceStarted);Assert.True(result.ObservedUtc<=DateTimeOffset.UtcNow);}
        else{Assert.Null(result.Cash);Assert.Null(result.Available);}
    }
    [Theory]
    [InlineData("live","DEMO_TEST_LIVE_DENIED")]
    [InlineData("scope","DEMO_TEST_SCOPE_MISMATCH")]
    [InlineData("credential","DEMO_TEST_CREDENTIAL_ENVIRONMENT_MISMATCH")]
    [InlineData("provenance","DEMO_TEST_CREDENTIAL_OR_LOCAL_READ_REFUSED")]
    public async Task Environment_and_binding_mismatch_make_zero_factory_or_network_calls(string mode,string expected)
    {
        var demo=Binding();var binding=mode=="live"?Binding(NavalEnvironment.Live):mode=="scope"?demo with{ConfigDigest=new string('a',64)}:demo;
        using var f=Printable();int opened=0,factories=0;
        var result=await OkxDemoBalanceTest.RunAsync(binding,()=>{opened++;var actual=mode=="credential"?Binding(NavalEnvironment.Live):binding;return new(actual,mode=="provenance"||actual.Account.Environment==NavalEnvironment.Live?"LiveAccountApi":"DemoTradingApi",f.Key.ToArray(),f.Secret.ToArray(),f.Passphrase.ToArray());},default,
            (s,p,fp,f)=>{factories++;throw new InvalidOperationException("network must not be constructed");});
        Assert.Equal(expected,result.Code);Assert.Equal(mode is "credential" or "provenance"?1:0,opened);Assert.Equal(0,factories);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Cancellation_during_private_read_or_after_signing_prevents_balance(bool afterMaterial)
    {
        var b=Binding();using var f=Printable();using var stop=new CancellationTokenSource();var peer=new Peer(b,f,"trade");
        peer.WaitForCancel=!afterMaterial;
        var entered=peer.Entered;
        var pending=OkxDemoBalanceTest.RunAsync(b,()=>Lease(b,f),stop.Token,(s,p,fp,fence)=>
            OkxExecutionObservationClient.ForTests(peer,TimeProvider.System,s,afterMaterial?new CancelMaterial(p,stop):p,fp,fence));
        if(!afterMaterial){await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));stop.Cancel();}
        var result=await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("DEMO_TEST_CANCELLED",result.Code);Assert.DoesNotContain("/api/v5/account/balance",peer.Paths);Assert.Equal(afterMaterial?0:1,peer.PrivateSends);
        Assert.Empty(peer.Violations);
    }
    private sealed class CancelMaterial(IOkxSigningMaterialProvider source,CancellationTokenSource stop):IOkxSigningMaterialProvider
    {public async ValueTask<OkxSigningMaterial> OpenAsync(CancellationToken ct){var m=await source.OpenAsync(ct);stop.Cancel();return m;}}
    [Fact]
    public async Task Per_request_timeout_is_sanitized_and_never_publishes_cash()
    {
        var b=Binding();using var f=Printable();var peer=new Peer(b,f,"trade"){WaitForCancel=true};
        var result=await OkxDemoBalanceTest.RunAsync(b,()=>Lease(b,f),default,(s,p,fp,fence)=>OkxExecutionObservationClient.ForTests(peer,TimeProvider.System,s,p,fp,fence));
        Assert.Equal("DEMO_TEST_PRIVATE_AUTH_CanceledOrTimeout",result.Code);Assert.Null(result.Cash);Assert.Equal(1,peer.PrivateSends);Assert.DoesNotContain("/api/v5/account/balance",peer.Paths);
    }
    [Theory][InlineData("scope")][InlineData("context")][InlineData("fingerprint")]
    public async Task Request_local_fence_and_material_binding_deny_substitution(string change)
    {
        var b=Binding();using var f=Printable();var peer=new Peer(b,f,"trade");IOkxObservationReadFence? retained=null;OkxExecutionScope? original=null;OkxObservationContext? context=null;
        var result=await OkxDemoBalanceTest.RunAsync(b,()=>Lease(b,f),default,(s,p,fp,fence)=>
        {
            retained=fence;original=s;
            var wrapped=new ProbeFence((scope,ctx,utc)=>{context=ctx;return fence.CanObserve(scope,change=="context"?ctx with{CaptureId=Guid.NewGuid()}:ctx,utc);});
            return OkxExecutionObservationClient.ForTests(peer,TimeProvider.System,change=="scope"?new("Demo",s.Uid,s.MainUid,s.QuoteCurrency,"other"):s,p,change=="fingerprint"?new string('a',64):fp,wrapped);
        });
        Assert.Equal(change=="fingerprint"?"DEMO_TEST_PRIVATE_AUTH_CredentialBindingMismatch":"DEMO_TEST_PUBLIC_TIME_ReadFenceDenied",result.Code);
        Assert.Equal(0,peer.PrivateSends);Assert.NotNull(context);Assert.False(retained!.CanObserve(original!,context!,DateTimeOffset.UtcNow));
    }
    [Fact]
    public async Task Fence_is_exact_bounded_and_closed_and_observation_quota_is_shared()
    {
        var b=Binding();using var f=Printable();IOkxObservationReadFence? retained=null;OkxObservationContext? context=null;OkxExecutionScope? scope=null;
        var first=new Peer(b,f,"trade");var second=new Peer(b,f,"trade");
        async Task<OkxDemoBalanceObservation> Run(Peer peer)=>await OkxDemoBalanceTest.RunAsync(b,()=>Lease(b,f),default,(s,p,fp,fence)=>
        {
            scope=s;retained=fence;
            var probe=new ProbeFence((requested,ctx,now)=>
            {
                context=ctx;
                Assert.False(fence.CanObserve(s,ctx,now.AddSeconds(35)));Assert.False(fence.CanObserve(s,ctx,now.AddSeconds(-35)));
                Assert.False(fence.CanObserve(s,ctx with{ConnectionId=Guid.NewGuid()},now));
                Assert.False(fence.CanObserve(new("Live",s.Uid,s.MainUid,s.QuoteCurrency,s.CredentialBindingId),ctx,now));
                return fence.CanObserve(requested,ctx,now);
            });
            return OkxExecutionObservationClient.ForTests(peer,TimeProvider.System,s,p,fp,probe);
        });
        Assert.Equal("DEMO_TEST_OBSERVED_READ_ONLY_STOP",(await Run(first)).Code);
        Assert.False(retained!.CanObserve(scope!,context!,DateTimeOffset.UtcNow));
        Assert.Equal("DEMO_TEST_OBSERVED_READ_ONLY_STOP",(await Run(second)).Code);
        var stamps=first.PrivateStamps.Concat(second.PrivateStamps).ToArray();Assert.Equal(4,stamps.Length);
        for(int i=1;i<stamps.Length;i++)Assert.True(Stopwatch.GetElapsedTime(stamps[i-1],stamps[i])>=TimeSpan.FromMilliseconds(590),"Shared production quota must space private sends across both clients");
    }
    private sealed class ProbeFence(Func<OkxExecutionScope,OkxObservationContext,DateTimeOffset,bool> check):IOkxObservationReadFence
    {public bool CanObserve(OkxExecutionScope s,OkxObservationContext c,DateTimeOffset now)=>check(s,c,now);}
    private sealed class Peer(OkxScopeBinding binding,CredentialFixture secrets,string mode):HttpMessageHandler
    {
        internal int PrivateSends;internal readonly List<string> Paths=new();internal readonly List<string> Violations=new();internal readonly List<long> PrivateStamps=new();
        internal DateTimeOffset BalanceStarted;internal bool WaitForCancel;internal readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
        {
            string path=r.RequestUri!.AbsolutePath;Paths.Add(path);
            if(r.Method!=HttpMethod.Get||r.Content is not null||r.RequestUri.Host!="eea.okx.com"||r.RequestUri.Scheme!="https"||r.RequestUri.Query!=""||!r.Headers.TryGetValues("x-simulated-trading",out var h)||h.Single()!="1")Violations.Add("method-region-environment");
            string data;
            if(path=="/api/v5/public/time"){
                if(mode=="cancel")throw new OperationCanceledException();
                if(mode=="rtt")await Task.Delay(TimeSpan.FromMilliseconds(2100),ct);
                if(r.Headers.Contains("OK-ACCESS-KEY"))Violations.Add("public-auth");
                data=mode=="public"?"{}":"{\"ts\":\""+DateTimeOffset.UtcNow.AddHours(mode=="clock"?8:0).ToUnixTimeMilliseconds()+"\"}";
            }else{
                PrivateSends++;PrivateStamps.Add(Stopwatch.GetTimestamp());
                string stamp=r.Headers.GetValues("OK-ACCESS-TIMESTAMP").Single();
                string expected=Convert.ToBase64String(HMACSHA256.HashData(secrets.Secret,Encoding.UTF8.GetBytes(stamp+"GET"+path)));
                if(r.Headers.GetValues("OK-ACCESS-SIGN").Single()!=expected||r.Headers.GetValues("OK-ACCESS-KEY").Single()!=Encoding.UTF8.GetString(secrets.Key)||r.Headers.GetValues("OK-ACCESS-PASSPHRASE").Single()!=Encoding.UTF8.GetString(secrets.Passphrase))Violations.Add("signing");
                if(WaitForCancel){Entered.TrySetResult();await Task.Delay(Timeout.InfiniteTimeSpan,ct);}
                if(mode=="auth"||mode=="balance-api"&&path=="/api/v5/account/balance")return new(HttpStatusCode.OK){Content=new StringContent("{\"code\":\"50119\",\"msg\":\"discard-private-message\",\"data\":[]}")};
                if(path=="/api/v5/account/config"){
                    // Original RED account fields/values retained. Added fields are
                    // required by the existing observation DTO schema, not authority.
                    var a=new JsonObject{["uid"]=mode=="uid"?"999999":binding.Account.AccountId,["mainUid"]=mode=="main"?"999999":binding.MainUid,["acctLv"]=mode=="margin"?"2":"1",
                        ["perm"]=mode switch{"trade"=>"read_only,trade","reverse"=>"trade,read_only","withdraw"=>"read_only,trade,withdraw","unknown"=>"read_only,future","trade-only"=>"trade","duplicate"=>"read_only,read_only","empty-perm"=>"","spaces"=>"read_only, trade",_=>"read_only"},
                        ["ip"]=mode=="ip"?" ":"192.0.2.4",["feeType"]="1",["autoLoan"]=mode=="autoLoan",["enableSpotBorrow"]=mode=="enableSpotBorrow",["spotBorrowAutoRepay"]=mode=="spotBorrowAutoRepay",["type"]="0",["spotRoleType"]="0",["kycLv"]="2"};data=a.ToJsonString();
                }
                else if(path=="/api/v5/account/balance"){
                    BalanceStarted=DateTimeOffset.UtcNow;
                    var row=new JsonObject{["ccy"]=mode=="currency"?"BTC":binding.Account.QuoteCurrency,["cashBal"]=mode=="nullable"?"":mode=="bad-cash"?"NaN":"100",["availBal"]=mode=="nullable"?"":"90",["frozenBal"]="10",["ordFrozen"]="10",["eq"]="100",["liab"]="0"};
                    var details=new JsonArray(row);if(mode=="duplicate-currency")details.Add(row.DeepClone());data=new JsonObject{["uTime"]="1793361600000",["details"]=details}.ToJsonString();
                }
                else{Violations.Add("unexpected-endpoint");data="{}";}
            }
            return new(HttpStatusCode.OK){Content=new StringContent("{\"code\":\"0\",\"msg\":\"\",\"data\":["+data+"]}")};
        }
    }
}
