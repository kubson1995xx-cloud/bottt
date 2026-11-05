using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Apex.Adapters.Okx.Execution;
using Apex.Okx.DemoAcceptance;
using Xunit;
namespace Apex.Tests;

public sealed class OkxMarketObligationsCliTests
{
    public OkxMarketObligationsCliTests(){SyntheticObligationEnvironment.Prepare();_=new OkxBalanceSchemaDiagnosticTests();}
    private static readonly byte[] Key="market-synthetic-key"u8.ToArray(),Secret="market-synthetic-secret"u8.ToArray(),Pass="market-synthetic-pass"u8.ToArray();
    [Theory]
    [InlineData("normal",0,9)] [InlineData("empty-records",0,9)] [InlineData("missing-number",0,9)]
    [InlineData("http",4,7)] [InlineData("unsupported",4,7)] [InlineData("account-policy",4,5)]
    [InlineData("identity-after",4,8)] [InlineData("balance-moved",4,9)] [InlineData("future-balance",4,6)]
    [InlineData("malformed",4,7)]
    public async Task Native_stored_key_actual_Program_mode_preserves_financial_bytes(string scenario,int exit,int sends)
    {
        await new OkxBalanceSchemaDiagnosticTests().Linux_network_denial_is_calibrated_on_async_workers();
        string root=NewRoot();var vault=new LinuxDemoCredentialVault(Path.Combine(root,"vault"));await vault.SaveNewAsync(Key,Secret,Pass);
        string run=Path.Combine(root,"run");Directory.CreateDirectory(run,(UnixFileMode)448);
        string[] financial={"last-report.json","naval_authority_broker_v2.json",".naval-broker-initialized-v2"};
        foreach(string n in financial)PrivateWrite(Path.Combine(run,n),"SYNTHETIC_PRESERVE_FINANCIAL"u8.ToArray());
        var before=Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).ToDictionary(p=>p,File.ReadAllBytes);
        var peer=new Peer(scenario);int calls=0;
        var stdout=Console.Out;var stderr=Console.Error;using var text=new StringWriter();int code;
        Console.SetOut(text);Console.SetError(text);
        try
        {
            code=await Program.RunWithObligationFactoryAsync(new[]{"--check-obligations"},run,vault,
                (owner,ct)=>owner.DiscoverPrivateSchema((_,_,_)=>{},ct,peer),
                (s,p,h,f)=>{calls++;Assert.Equal(peer.Uid,s.Uid);Assert.Equal("EUR",s.QuoteCurrency);
                    // The same run lock excludes management while credentials remain loaded.
                    Assert.Throws<IOException>(()=>new FileStream(Path.Combine(run,".run.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None));
                    return OkxExecutionObservationClient.ForTests(peer,TimeProvider.System,s,p,h,f);});
        }
        finally{Console.SetOut(stdout);Console.SetError(stderr);}
        Assert.Equal(exit,code);Assert.Equal(sends,peer.Count);Assert.Equal(1,calls);
        foreach(var p in before)Assert.True(File.ReadAllBytes(p.Key).SequenceEqual(p.Value),"PRIVATE_PRESERVATION_FAILED");
        string report=Assert.Single(Directory.GetFiles(Path.Combine(run,"obligation-reports"),"*.json"));
        Assert.Equal((UnixFileMode)384,File.GetUnixFileMode(report));Assert.Equal((UnixFileMode)448,File.GetUnixFileMode(Path.GetDirectoryName(report)!));
        byte[] bytes=File.ReadAllBytes(report);string printed=text.ToString();
        foreach(byte[] field in new[]{Key,Secret,Pass}){Assert.True(bytes.AsSpan().IndexOf(field)<0);Assert.DoesNotContain(Encoding.ASCII.GetString(field),printed);}
        string fp=Convert.ToHexString(SHA256.HashData(Key)).ToLowerInvariant();Assert.DoesNotContain(fp,Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("PRIVATE_RESPONSE_SENTINEL",Encoding.UTF8.GetString(bytes)+printed);
        using var doc=JsonDocument.Parse(bytes);var r=doc.RootElement;
        Assert.Equal("CHECK_OBLIGATIONS_GET_ONLY",r.GetProperty("operation").GetString());Assert.Equal("MARKET_LOANS_ONLY",r.GetProperty("scope").GetString());Assert.Equal("NOT_ESTABLISHED",r.GetProperty("AllAccountClearance").GetString());
        Assert.False(r.GetProperty("TradingAuthorityGranted").GetBoolean());Assert.True(r.GetProperty("complete").GetBoolean());Assert.Equal("STOP",r.GetProperty("mode").GetString());Assert.False(r.GetProperty("armed").GetBoolean());Assert.False(r.GetProperty("schedulerStarted").GetBoolean());
        foreach(string n in new[]{"financialOwner","baseline","orders","intents"})Assert.Equal(JsonValueKind.Null,r.GetProperty(n).ValueKind);
        Assert.False(r.TryGetProperty("obligations",out _));Assert.Equal(4,r.GetProperty("identity").GetProperty("assemblies").GetArrayLength());
        var o=r.GetProperty("observation");Assert.Equal(exit==0,o.GetProperty("bracketUnchanged").GetBoolean());
        Assert.Equal(sends-4,o.GetProperty("readTrace").GetArrayLength());
        if(exit==0)
        {
            Assert.Equal("MARKET_LOANS_OBSERVED_STOP_NO_TRADING",r.GetProperty("result").GetString());Assert.Contains("MARKET_LOANS_OBSERVED_STOP_NO_TRADING",printed);
            var balances=o.GetProperty("balancesBefore").GetProperty("Details");Assert.Equal(6,balances.GetArrayLength());Assert.All(balances.EnumerateArray(),b=>Assert.Equal(JsonValueKind.Null,b.GetProperty("Interest").GetProperty("Value").ValueKind));
            Assert.True(o.GetProperty("coverage").GetProperty("positiveAggregate").GetBoolean());Assert.False(o.GetProperty("coverage").GetProperty("scheduleTimesAreFreshness").GetBoolean());
            Assert.Equal(scenario=="empty-records",o.GetProperty("coverage").GetProperty("emptyRecords").GetBoolean());
        }
        else if(scenario is "http" or "unsupported")
        {
            Assert.Equal(scenario=="http"?503:200,o.GetProperty("http")[2].GetProperty("HttpStatus").GetInt32());
            Assert.Equal(scenario=="http"?"HttpFailure":"51000",r.GetProperty("result").GetString());
        }
    }
    [Theory][InlineData("missing")][InlineData("corrupt")][InlineData("locked")][InlineData("status")][InlineData("default-financial")][InlineData("wrong-args")]
    public async Task Refusals_and_offline_management_do_not_enter_discovery_or_change_reports(string kind)
    {
        string root=NewRoot();string run=Path.Combine(root,"run");Directory.CreateDirectory(run,(UnixFileMode)448);PrivateWrite(Path.Combine(run,"last-report.json"),"SYNTHETIC_PRESERVE"u8.ToArray());
        var vault=new LinuxDemoCredentialVault(Path.Combine(root,"vault"));if(kind!="missing")await vault.SaveNewAsync(Key,Secret,Pass);
        if(kind=="corrupt")PrivateWrite(Path.Combine(root,"vault/credentials.cred"),"corrupt-native-cipher"u8.ToArray());
        if(kind=="default-financial")PrivateWrite(Path.Combine(run,".naval-broker-initialized-v2"),"SYNTHETIC"u8.ToArray());
        FileStream? held=null;if(kind=="locked"){PrivateWrite(Path.Combine(run,".run.lock"),[]);held=new(Path.Combine(run,".run.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None);}
        int calls=0;int code;
        try{code=await Program.RunAsync(kind=="status"?new[]{"--credentials-status"}:kind=="default-financial"?[]:kind=="wrong-args"?new[]{"--check-obligations","--type=1"}:new[]{"--check-obligations"},run,vault,(_,_)=>{calls++;throw new Exception();});}
        finally{held?.Dispose();}
        Assert.Equal(kind switch{"locked"=>9,"status"=>0,"default-financial"=>3,"wrong-args"=>2,_=>8},code);Assert.Equal(0,calls);Assert.Equal("SYNTHETIC_PRESERVE",File.ReadAllText(Path.Combine(run,"last-report.json")));Assert.False(Directory.Exists(Path.Combine(run,"obligation-reports")));
    }
    [Theory][InlineData("utc")][InlineData("mono")][InlineData("cancel")]
    public async Task Whole_capture_budget_remains_an_independent_restriction(string clockKind)
    {
        var clock=new CaptureClock();using var cancel=new CancellationTokenSource();using var peer=new Peer("normal"){Count=4};
        var scope=new OkxExecutionScope("Demo",peer.Uid,peer.Uid,"EUR","capture-clock");
        using var owner=new EphemeralCredentials(Key.ToArray(),Secret.ToArray(),Pass.ToArray());int checkpoints=0;
        peer.OnReply=n=>{if(n==6){if(clockKind=="utc")clock.Utc+=TimeSpan.FromSeconds(2);if(clockKind=="mono")clock.Mono+=TimeSpan.FromSeconds(2).Ticks;}};
        object result=await MarketObligationsObservation.CollectAsync(scope,owner.ForObservation(scope),owner.Fingerprint,()=>true,cancel.Token,
            _=>{if(++checkpoints==1){if(clockKind=="utc")clock.Utc+=TimeSpan.FromSeconds(89);if(clockKind=="mono")clock.Mono+=TimeSpan.FromSeconds(89).Ticks;if(clockKind=="cancel")cancel.Cancel();}},
            (s,p,h,f)=>OkxExecutionObservationClient.ForTests(peer,clock,s,p,h,f,(t,ct)=>{clock.Utc+=t;clock.Mono+=t.Ticks;return Task.CompletedTask;}),clock);
        var r=JsonSerializer.SerializeToElement(result);Assert.False(r.GetProperty("bracketUnchanged").GetBoolean());Assert.Equal(clockKind=="cancel"?5:6,peer.Count);
        Assert.Equal("BALANCE_BEFORE",r.GetProperty("stage").GetString());Assert.NotEqual("MARKET_LOANS_OBSERVED_STOP_NO_TRADING",r.GetProperty("result").GetString());
    }
    private sealed class CaptureClock:TimeProvider
    {
        internal DateTimeOffset Utc=DateTimeOffset.UtcNow;internal long Mono;
        public override DateTimeOffset GetUtcNow()=>Utc;public override long GetTimestamp()=>Mono;public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
    }
    private static string NewRoot(){string root="/home/ubuntu/apex-deliveries/apex-okx-private-schema-tests/market-"+Guid.NewGuid().ToString("N");Directory.CreateDirectory(root,(UnixFileMode)448);return root;}
    private static void PrivateWrite(string path,byte[] bytes){using var f=new FileStream(path,new FileStreamOptions{Mode=FileMode.Create,Access=FileAccess.Write,UnixCreateMode=(UnixFileMode)384});f.Write(bytes);}
    private sealed class Peer(string scenario):HttpMessageHandler
    {
        internal readonly string Uid=Random.Shared.NextInt64(800000000,899999999).ToString();internal int Count;internal Action<int>? OnReply;
        private readonly string balanceTime=DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeMilliseconds().ToString();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
        {
            string[] paths={"/api/v5/public/time","/api/v5/account/config","/api/v5/account/balance","/api/v5/account/instruments?instType=SPOT","/api/v5/account/config","/api/v5/account/balance","/api/v5/account/interest-limits?type=2","/api/v5/account/config","/api/v5/account/balance"};
            Assert.InRange(Count,0,8);string path=paths[Count++];Assert.Equal(path,r.RequestUri!.PathAndQuery);Assert.Equal(HttpMethod.Get,r.Method);Assert.Null(r.Content);Assert.Equal("https",r.RequestUri.Scheme);Assert.Equal("eea.okx.com",r.RequestUri.Host);Assert.Equal(443,r.RequestUri.Port);Assert.Equal("1",r.Headers.GetValues("x-simulated-trading").Single());
            if(Count>1){string ts=r.Headers.GetValues("OK-ACCESS-TIMESTAMP").Single();Assert.Equal(Encoding.ASCII.GetString(Key),r.Headers.GetValues("OK-ACCESS-KEY").Single());Assert.Equal(Encoding.ASCII.GetString(Pass),r.Headers.GetValues("OK-ACCESS-PASSPHRASE").Single());Assert.Equal(Convert.ToBase64String(HMACSHA256.HashData(Secret,Encoding.ASCII.GetBytes(ts+"GET"+path))),r.Headers.GetValues("OK-ACCESS-SIGN").Single());}
            JsonNode row;
            if(Count==1)row=new JsonObject{["ts"]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()};
            else if(Count is 2 or 5 or 8)
            {
                row=JsonNode.Parse("{\"uid\":\"1\",\"mainUid\":\"1\",\"acctLv\":\"1\",\"perm\":\"read_only,trade\",\"ip\":\"192.0.2.198\",\"feeType\":\"0\",\"autoLoan\":false,\"enableSpotBorrow\":false,\"spotBorrowAutoRepay\":false,\"type\":\"0\",\"spotRoleType\":\"0\",\"kycLv\":\"3\"}")!;row["uid"]=Uid;row["mainUid"]=Uid;
                if(Count==5&&scenario=="account-policy")row["autoLoan"]=true;if(Count==8&&scenario=="identity-after")row["uid"]="123456789";
            }
            else if(Count is 3 or 6 or 9)
            {
                var details=new JsonArray();foreach(string ccy in new[]{"USDC","EUR","BTC","ETH","SOL","ADA"})details.Add(new JsonObject{["ccy"]=ccy,["cashBal"]="100",["availBal"]="100",["eq"]="100",["frozenBal"]="0",["ordFrozen"]="0",["stgyEq"]="0",["fixedBal"]="0",["interest"]="",["liab"]=ccy is "USDC" or "EUR"?"0":"",["crossLiab"]=ccy is "USDC" or "EUR"?"0":"",["isoLiab"]=ccy is "USDC" or "EUR"?"0":"",["uTime"]=balanceTime});
                if(Count==9&&scenario=="balance-moved")details[0]!["cashBal"]="101";
                if(Count==6&&scenario=="future-balance")details[0]!["uTime"]=DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds().ToString();
                row=new JsonObject{["uTime"]=balanceTime,["details"]=details};
            }
            else if(Count==4)row=JsonNode.Parse("{\"instType\":\"SPOT\",\"instId\":\"BTC-USDC\",\"baseCcy\":\"BTC\",\"quoteCcy\":\"USDC\",\"state\":\"live\",\"tradeQuoteCcyList\":[\"USDC\"]}")!;
            else
            {
                using var stream=typeof(OkxMarketObligationsCliTests).Assembly.GetManifestResourceStream("Apex.Tests.Fixtures.market-loans-official.json")!;row=JsonNode.Parse(stream)!["data"]![0]!.DeepClone();
                row["nextInterestTime"]=DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds().ToString();
                if(scenario=="empty-records")row["records"]=new JsonArray();if(scenario=="missing-number")row["records"]![0]!.AsObject().Remove("usedLmt");if(scenario=="malformed")row["debt"]=false;
            }
            string code=Count==7&&scenario=="unsupported"?"51000":"0";
            var body=new JsonObject{["code"]=code,["msg"]="PRIVATE_RESPONSE_SENTINEL",["data"]=code=="0"?new JsonArray(row):new JsonArray()};
            OnReply?.Invoke(Count);
            return Task.FromResult(new HttpResponseMessage(Count==7&&scenario=="http"?HttpStatusCode.ServiceUnavailable:HttpStatusCode.OK){Content=new StringContent(body.ToJsonString())});
        }
    }
}

internal static class SyntheticObligationEnvironment
{
    internal static void Prepare()
    {
        var allowed=new HashSet<string>{"HOME","PATH","DOTNET_ROOT","DOTNET_EnableDiagnostics","COMPlus_EnableDiagnostics","LANG","TMPDIR"};
        foreach(System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())if(!allowed.Contains((string)e.Key))Environment.SetEnvironmentVariable((string)e.Key,null);
        Environment.SetEnvironmentVariable("DOTNET_EnableDiagnostics","0");Environment.SetEnvironmentVariable("COMPlus_EnableDiagnostics","0");
        LinuxInput.Harden();
    }
}
