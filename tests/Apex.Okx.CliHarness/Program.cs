using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Apex.Adapters.Okx.Execution;
using Apex.Okx.DemoAcceptance;

// Friend-only process fixture. NEVER shipped in the production package.
internal static class CliHarness
{
    internal static byte[] Field(int seed) => Enumerable.Range(0,64).Select(i=>(byte)(33+(i*31+seed*17+i/19)%94)).ToArray();
    private static void Check(bool b) { if(!b)throw new Exception("SYNTHETIC_ASSERTION_FAILED"); }
    public static async Task<int> Main(string[] args)
    {
        string root=Path.GetFullPath(args[1]);
        if(!root.StartsWith("/home/ubuntu/apex-deliveries/apex-okx-persistent-cli-tests/",StringComparison.Ordinal))return 90;
        var vault=new LinuxDemoCredentialVault(Path.Combine(root,"vault"));
        if(args[0]=="seed") {await vault.SaveNewAsync(Field(1),Field(2),Field(3));return 0;}
        if(args[0]=="verify") {using var b=await vault.LoadAsync();int s=int.Parse(args[2]);Check(b.ApiKey.Span.SequenceEqual(Field(s))&&b.SecretKey.Span.SequenceEqual(Field(s+1))&&b.Passphrase.Span.SequenceEqual(Field(s+2)));return 0;}
        if(args[0]=="hold")
        {
            Directory.CreateDirectory(Path.Combine(root,"run"),(UnixFileMode)448);
            using var l=new FileStream(Path.Combine(root,"run/.run.lock"),new FileStreamOptions{Mode=FileMode.OpenOrCreate,Access=FileAccess.ReadWrite,Share=FileShare.None,UnixCreateMode=(UnixFileMode)384});
            Console.WriteLine("LOCK_HELD");await Task.Delay(15000);return 0;
        }
        string scenario=args[2];string[] cli=args.Skip(3).ToArray();
        // Missing friend isolation is a fixture failure, never permission to run
        // production Main or touch its financial report/root.
        var entry=typeof(Apex.Okx.DemoAcceptance.Program).GetMethod("RunAsync",BindingFlags.NonPublic|BindingFlags.Static);
        if(entry==null)throw new InvalidOperationException("ISOLATED_ENTRY_REQUIRED_NEVER_FALL_BACK_TO_PRODUCTION_ROOT");
        int constructions=0,sends=0;
        if(scenario.StartsWith("market:",StringComparison.Ordinal))
        {
            var marketEntry=typeof(Apex.Okx.DemoAcceptance.Program).GetMethod("RunWithObligationFactoryAsync",BindingFlags.NonPublic|BindingFlags.Static);
            if(marketEntry is null)throw new InvalidOperationException("ISOLATED_MARKET_ENTRY_REQUIRED_NO_MAIN_FALLBACK");
            var peer=new MarketCliPeer(scenario[7..],()=>sends++);
            Func<EphemeralCredentials,CancellationToken,Task<OkxEnrollmentDiscovery>> marketDiscover=(owner,ct)=>{constructions++;return owner.DiscoverPrivateSchema((_,_,_)=>{},ct,peer);};
            Func<OkxExecutionScope,IOkxSigningMaterialProvider,string,IOkxObservationReadFence,OkxExecutionObservationClient> factory=(s,p,h,f)=>OkxExecutionObservationClient.ForTests(peer,TimeProvider.System,s,p,h,f);
            int code=await (Task<int>)marketEntry.Invoke(null,new object[]{cli,Path.Combine(root,"run"),vault,marketDiscover,factory})!;
            Console.WriteLine($"TEST_DISCOVERY_CONSTRUCTIONS={constructions};GET_SENDS={sends}");return code;
        }
        Func<EphemeralCredentials,CancellationToken,Task<OkxEnrollmentDiscovery>> discover=async(owner,ct)=>
        {
            constructions++;
            Check(owner.ContainsSecret(Field(1))); // Real loaded ownership, not merely a status result.
            if(scenario=="deny")throw new Exception("NETWORK_CONSTRUCTION_FORBIDDEN");
            using var peer=new Peer(scenario,()=>sends++);
            using var material=await owner.ForObservation(new OkxExecutionScope("Demo","987654321098765432","987654321098765432","USDC","synthetic-cli")).OpenAsync(ct);
            return await OkxEnrollmentDiscoveryClient.RunAsync("Demo","DemoTradingApi",material.Key,material.Secret,material.Passphrase,null,null,ct,peer,LinuxInput.SafeForPrivateDispatch);
        };
        int result=await (Task<int>)entry.Invoke(null,new object[]{cli,Path.Combine(root,"run"),vault,discover})!;
        Console.WriteLine($"TEST_DISCOVERY_CONSTRUCTIONS={constructions};GET_SENDS={sends}");return result;
    }
    private sealed class Peer(string scenario,Action sent) : HttpMessageHandler
    {
        private int count;
        private static readonly string[] Targets={"/api/v5/public/time","/api/v5/account/config","/api/v5/account/balance","/api/v5/account/instruments?instType=SPOT"};
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Check(request.Method==HttpMethod.Get&&request.Content==null&&request.RequestUri!.Scheme=="https"&&request.RequestUri.Host=="eea.okx.com");
            Check(request.Headers.GetValues("x-simulated-trading").Single()=="1");
            string path=request.RequestUri!.PathAndQuery;Check(count<4&&path==Targets[count]);sent();count++;
            if(count>1)
            {
                string stamp=request.Headers.GetValues("OK-ACCESS-TIMESTAMP").Single();
                Check(request.Headers.GetValues("OK-ACCESS-SIGN").Single()==Convert.ToBase64String(HMACSHA256.HashData(Field(2),System.Text.Encoding.ASCII.GetBytes(stamp+"GET"+path))));
                Check(request.Headers.GetValues("OK-ACCESS-PASSPHRASE").Single()==System.Text.Encoding.ASCII.GetString(Field(3)));
                Check(request.Headers.GetValues("OK-ACCESS-KEY").Single()==System.Text.Encoding.ASCII.GetString(Field(1)));
            }
            using var stream=typeof(CliHarness).Assembly.GetManifestResourceStream(typeof(CliHarness).Assembly.GetManifestResourceNames().Single(n=>n.EndsWith("baseline-schema.json",StringComparison.Ordinal)))!;
            var fixture=JsonNode.Parse(stream)!;
            var body=fixture["endpoints"]![new[]{"time","account","balance","instruments"}[count-1]]!["control"]!.DeepClone();var row=body["data"]![0]!;
            if(count==1)row["ts"]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            if(count==2){row["uid"]="987654321098765432";row["mainUid"]="987654321098765432";}
            if(scenario=="parser"&&count==3)row["details"]![0]!["cashBal"]=123;
            if(scenario=="safety"&&count==2)row["autoLoan"]=true;
            if(scenario.StartsWith("balance-exact",StringComparison.Ordinal)&&count==3)
            {
                row["details"]![0]!["autoLendAmt"]=scenario=="balance-exact-amount"?"2.75":"0";
                row["details"]![0]!["autoStakingStatus"]=scenario=="balance-exact-active"?"active":"unsupported";
            }
            if(scenario.StartsWith("instrument-exact",StringComparison.Ordinal)&&count==3)
            {row["details"]![0]!["autoLendAmt"]="0";row["details"]![0]!["autoStakingStatus"]="off";}
            if(scenario.StartsWith("instrument-exact",StringComparison.Ordinal)&&count==4)
            {
                row["freq"]=scenario=="instrument-exact-freq"?"SYNTHETIC_UNSUPPORTED_FREQ":"";
                row["method"]=scenario=="instrument-exact-method"?"SYNTHETIC_UNSUPPORTED_METHOD":"";
                if(scenario=="instrument-exact-type")row["method"]=JsonValue.Create(0);
                if(scenario=="instrument-exact-nonspot")row["instType"]="MARGIN";
                if(scenario=="instrument-exact-unknown")row["syntheticExtra"]="";
            }
            if(scenario=="balance-known"&&count==3){row["details"]![0]!["coinUsdPrice"]="SYNTHETIC_PRIVATE_BALANCE_VALUE";row["details"]![0]!["maxSpotInUseAmt"]="";}
            if(scenario=="balance-unknown"&&count==3){row["details"]![0]!["SYNTHETIC_PRIVATE_PROPERTY_NAME"]="SYNTHETIC_PRIVATE_BALANCE_VALUE";row["details"]![0]!["ANOTHER_PRIVATE_PROPERTY_NAME"]="";}
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created){Content=new StringContent(body.ToJsonString())});
        }
    }
}
