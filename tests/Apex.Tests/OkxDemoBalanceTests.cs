using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx;
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
    [Theory]
    [InlineData("valid","DEMO_TEST_OBSERVED_READ_ONLY_STOP",2)]
    [InlineData("uid","DEMO_TEST_PRIVATE_AUTH_IdentityConflict",1)]
    [InlineData("main","DEMO_TEST_ACCOUNT_BINDING_MISMATCH",1)]
    [InlineData("trade","DEMO_TEST_PRIVATE_AUTH_UnsafeAccountConfig",1)]
    [InlineData("auth","DEMO_TEST_PRIVATE_AUTH_50119",1)]
    [InlineData("public","DEMO_TEST_PUBLIC_TIME_InvalidPayload",0)]
    [InlineData("cancel","DEMO_TEST_PUBLIC_TIME_Timeout",0)]
    public async Task Real_scoped_reader_and_signer_only_publish_observations(string mode,string expected,int privateSends)
    {
        var binding=Binding();using var material=Printable();var peer=new Peer(binding,material,mode);
        var result=await OkxDemoBalanceTest.RunAsync(binding,()=>new(binding,"DemoTradingApi",material.Key.ToArray(),material.Secret.ToArray(),material.Passphrase.ToArray()),default,
            (provider,scope)=>OkxEeaDemoReadClient.ForTests(peer,TimeProvider.System,provider,scope,sharedQuota:true));
        Assert.Equal(expected,result.Code);Assert.Equal(privateSends,peer.PrivateSends);Assert.Empty(peer.Violations);
        Assert.All(peer.Paths,p=>Assert.Contains(p,new[]{"/api/v5/public/time","/api/v5/account/config","/api/v5/account/balance"}));
        if(mode=="valid"){Assert.Equal("100",result.Cash);Assert.Equal("90",result.Available);Assert.Equal(binding.Account.AccountId,result.Uid);Assert.Equal("OkxDemo",result.Environment);Assert.Equal(binding.Account.QuoteCurrency,result.Currency);Assert.True(result.ObservedUtc<=DateTimeOffset.UtcNow);}
        else{Assert.Null(result.Cash);Assert.Null(result.Available);}
    }
    [Theory]
    [InlineData("live","DEMO_TEST_LIVE_DENIED")]
    [InlineData("scope","DEMO_TEST_SCOPE_MISMATCH")]
    [InlineData("credential","DEMO_TEST_CREDENTIAL_ENVIRONMENT_MISMATCH")]
    public async Task Environment_and_binding_mismatch_make_zero_factory_or_network_calls(string mode,string expected)
    {
        var demo=Binding();var binding=mode=="live"?Binding(NavalEnvironment.Live):mode=="scope"?demo with{ConfigDigest=new string('a',64)}:demo;
        using var f=new CredentialFixture();int opened=0,factories=0;
        var result=await OkxDemoBalanceTest.RunAsync(binding,()=>{opened++;var actual=mode=="credential"?Binding(NavalEnvironment.Live):binding;return new(actual,actual.Account.Environment==NavalEnvironment.Live?"LiveAccountApi":"DemoTradingApi",f.Key.ToArray(),f.Secret.ToArray(),f.Passphrase.ToArray());},default,
            (p,s)=>{factories++;throw new InvalidOperationException("network must not be constructed");});
        Assert.Equal(expected,result.Code);Assert.Equal(mode=="credential"?1:0,opened);Assert.Equal(0,factories);
    }
    private sealed class Peer(OkxScopeBinding binding,CredentialFixture secrets,string mode):HttpMessageHandler
    {
        internal int PrivateSends;internal readonly List<string> Paths=new();internal readonly List<string> Violations=new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
        {
            string path=r.RequestUri!.AbsolutePath;Paths.Add(path);
            if(r.Method!=HttpMethod.Get||r.RequestUri.Host!="eea.okx.com"||r.RequestUri.Scheme!="https"||r.RequestUri.Query!=""||!r.Headers.TryGetValues("x-simulated-trading",out var h)||h.Single()!="1")Violations.Add("method-region-environment");
            string data;
            if(path=="/api/v5/public/time"){
                if(mode=="cancel")throw new OperationCanceledException();
                data=mode=="public"?"{}":"{\"ts\":\""+DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+"\"}";
            }else{
                PrivateSends++;
                string stamp=r.Headers.GetValues("OK-ACCESS-TIMESTAMP").Single();
                string expected=Convert.ToBase64String(HMACSHA256.HashData(secrets.Secret,Encoding.UTF8.GetBytes(stamp+"GET"+path)));
                if(r.Headers.GetValues("OK-ACCESS-SIGN").Single()!=expected||r.Headers.GetValues("OK-ACCESS-KEY").Single()!=Encoding.UTF8.GetString(secrets.Key)||r.Headers.GetValues("OK-ACCESS-PASSPHRASE").Single()!=Encoding.UTF8.GetString(secrets.Passphrase))Violations.Add("signing");
                if(mode=="auth")return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"code\":\"50119\",\"msg\":\"discard-private-message\",\"data\":[]}")});
                if(path=="/api/v5/account/config")data="{\"uid\":\""+(mode=="uid"?"999999":binding.Account.AccountId)+"\",\"mainUid\":\""+(mode=="main"?"999999":binding.MainUid)+"\",\"acctLv\":\"1\",\"perm\":\""+(mode=="trade"?"read_only,trade":"read_only")+"\",\"ip\":\"192.0.2.4\",\"feeType\":\"1\",\"autoLoan\":false,\"enableSpotBorrow\":false}";
                else if(path=="/api/v5/account/balance")data="{\"uTime\":\"1793361600000\",\"details\":[{\"ccy\":\""+binding.Account.QuoteCurrency+"\",\"cashBal\":\"100\",\"availBal\":\"90\",\"frozenBal\":\"10\",\"ordFrozen\":\"10\",\"eq\":\"100\",\"liab\":\"0\"}]}";
                else{Violations.Add("unexpected-endpoint");data="{}";}
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"code\":\"0\",\"msg\":\"\",\"data\":["+data+"]}")});
        }
    }
}
