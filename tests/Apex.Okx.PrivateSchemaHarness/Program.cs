using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Apex.Adapters.Okx.Execution;
using Apex.Okx.DemoAcceptance;

internal static class Harness
{
    internal static readonly byte[][] Fields={"probeKey"u8.ToArray(),"probeSecret"u8.ToArray(),"probePass"u8.ToArray()};
    internal static void Check(bool value,string code="SYNTHETIC_ASSERTION_FAILED") {if(!value)throw new Exception(code);}
    internal static JsonNode Resource(string suffix)
    {
        var a=typeof(Harness).Assembly;
        using var s=a.GetManifestResourceStream(a.GetManifestResourceNames().Single(n=>n.EndsWith(suffix,StringComparison.Ordinal)))!;
        return JsonNode.Parse(s)!;
    }
    internal static async Task<OkxEnrollmentDiscovery> Discover(Peer peer,Action<string?,string,int>? observer,CancellationToken ct=default)
    {
        var m=typeof(OkxEnrollmentDiscoveryClient).GetMethod("RunAsync",BindingFlags.Static|BindingFlags.NonPublic)!;
        object?[] args={"Demo","DemoTradingApi",(ReadOnlyMemory<byte>)Fields[0],(ReadOnlyMemory<byte>)Fields[1],(ReadOnlyMemory<byte>)Fields[2],null,null,ct,peer,(Func<bool>)(()=>true)};
        if(m.GetParameters().Length==11)args=args.Append(observer).ToArray();
        return await (Task<OkxEnrollmentDiscovery>)m.Invoke(null,args)!;
    }
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if(args[0]=="hold")
            {
                Check(Path.GetFullPath(args[1]).StartsWith("/home/ubuntu/apex-deliveries/apex-okx-private-schema-tests/",StringComparison.Ordinal));
                using var held=new FileStream(args[1],FileMode.Open,FileAccess.ReadWrite,FileShare.None);
                Console.WriteLine("SYNTHETIC_LOCK_HELD");await Task.Delay(20000);return 0;
            }
            if(args[0] is "probe-suite" or "instrument-suite"){await ProbeTests.Run(args[1],args[0]=="instrument-suite");return 0;}
            if(args[0] is "observer" or "observer-instruments")
            {
                bool instruments=args[0]=="observer-instruments";
                var names=new List<(string?,string,int)>();using var p=new Peer(instruments?"instrument:two":"two");
                var result=await Discover(p,(n,k,c)=>names.Add((n,k,c)));
                Check(result.Observation==null&&result.Diagnostic?.Payload?.Reason=="UNSUPPORTED_PROPERTY"&&p.Count==(instruments?4:3),"STRICT_REFUSAL_REQUIRED");
                if(instruments)Check(result.Diagnostic is {Endpoint:"/api/v5/account/instruments?instType=SPOT",HttpStatus:200,OkxCode:"0",Phase:"PARSE_SCHEMA",LastCompletedStage:"BALANCE_VALIDATED",ConfigAuthenticated:true,AccountSafetyValidated:true,Payload:{Path:"$.data[].<unknown>",Actual:"String"}},"STRICT_REFUSAL_REQUIRED");
                Check(names.SequenceEqual(new[]{((string?)"alphaMetric","String",1),((string?)"betaMetric","String",1)}),"PRIVATE_IDENTIFIERS_MISSING");
                Check(!JsonSerializer.Serialize(result).Contains("alphaMetric"),"PUBLIC_DIAGNOSTICS_LEAK");
                Console.WriteLine("PASS observer_strict_private_inventory");return 0;
            }
            throw new Exception("UNKNOWN_SYNTHETIC_MODE");
        }
        catch(DemoVaultException e){Console.Error.WriteLine("SYNTHETIC_SETUP_"+e.Code);return 1;}
        catch(Exception e){Console.Error.WriteLine(e.Message is "PRIVATE_IDENTIFIERS_MISSING" or "STRICT_REFUSAL_REQUIRED" or "PUBLIC_DIAGNOSTICS_LEAK" or "PROBE_ENTRY_REQUIRED" or "PROBE_PUBLICATION_REQUIRED"?e.Message:"SYNTHETIC_ASSERTION_FAILED_"+e.GetType().Name);return 1;}
    }
    internal sealed class Peer(string input,CancellationTokenSource? cancellation=null):HttpMessageHandler
    {
        private readonly bool instruments=input.StartsWith("instrument:",StringComparison.Ordinal);
        private readonly string scenario=input.StartsWith("instrument:",StringComparison.Ordinal)?input[11..]:input;
        internal int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            string[] paths={"/api/v5/public/time","/api/v5/account/config","/api/v5/account/balance","/api/v5/account/instruments?instType=SPOT"};
            Check(request.Method==HttpMethod.Get&&request.Content==null&&request.RequestUri!.Scheme=="https"&&request.RequestUri.Host=="eea.okx.com"&&request.RequestUri.Port==443);
            Check(Count<4&&request.RequestUri!.PathAndQuery==paths[Count]);
            Check(request.Headers.GetValues("x-simulated-trading").Single()=="1");
            if(Count>0)
            {
                string stamp=request.Headers.GetValues("OK-ACCESS-TIMESTAMP").Single();
                Check(request.Headers.GetValues("OK-ACCESS-KEY").Single()==Encoding.ASCII.GetString(Fields[0]));
                Check(request.Headers.GetValues("OK-ACCESS-PASSPHRASE").Single()==Encoding.ASCII.GetString(Fields[2]));
                Check(request.Headers.GetValues("OK-ACCESS-SIGN").Single()==Convert.ToBase64String(HMACSHA256.HashData(Fields[1],Encoding.ASCII.GetBytes(stamp+"GET"+paths[Count]))));
            }
            else Check(!request.Headers.Contains("OK-ACCESS-KEY")&&!request.Headers.Contains("OK-ACCESS-SIGN"));
            Count++;
            var body=Resource("baseline-schema.json")["endpoints"]![new[]{"time","account","balance","instruments"}[Count-1]]!["control"]!.DeepClone();
            if(Count==1)body["data"]![0]!["ts"]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            if(Count==3)
            {
                body=Resource("okx-balance-current.json");
                if(instruments)
                {
                    // Exact accepted balance profile must pass before fourth GET.
                    body["data"]![0]!["details"]![0]!["autoLendAmt"]="0";
                    body["data"]![0]!["details"]![0]!["autoStakingStatus"]="off";
                    if(scenario=="balance-first")body["data"]![0]!["details"]![0]!["balanceOnlyMetric"]="VALUE_NEVER_PUBLISH";
                }
            }
            if(Count==(instruments?4:3))
            {
                var detail=instruments?body["data"]![0]!:body["data"]![0]!["details"]![0]!;
                if(scenario=="two"){detail["alphaMetric"]="VALUE_NEVER_PUBLISH";detail["betaMetric"]="";}
                if(scenario.StartsWith("name:",StringComparison.Ordinal))detail[scenario[5..]]="VALUE_NEVER_PUBLISH";
                if(scenario=="recursive")detail["alphaMetric"]=new JsonObject{["RECURSIVE_NEVER_COPY"]="VALUE_NEVER_PUBLISH"};
                if(scenario=="number")detail["alphaMetric"]=123;
                if(scenario=="boolean")detail["alphaMetric"]=true;
                if(scenario=="null")detail["alphaMetric"]=null;
                if(scenario=="array")detail["alphaMetric"]=new JsonArray("VALUE_NEVER_PUBLISH");
                string known=instruments?"tickSz":"cashBal";
                if(scenario=="wrong-type")detail[known]=123;
                if(scenario=="wrong-shape") {if(instruments)body["data"]=new JsonArray(123);else body["data"]![0]!["details"]=new JsonArray(123);}
                if(scenario=="case") {detail.AsObject().Remove(known);detail[instruments?"TickSz":"CashBal"]="0";}
                if(scenario=="cap")for(int i=0;i<33;i++)detail["extra"+(char)('A'+i/26)+(char)('A'+i%26)]="";
                if(scenario=="property-cap")for(int i=0;i<257;i++)detail["extra"+(char)('A'+i/26)+(char)('A'+i%26)]="";
                if(scenario=="first-only")
                {
                    var later=detail.DeepClone();detail["alphaMetric"]="VALUE_NEVER_PUBLISH";
                    later["OTHER_ROW_NEVER_COPY"]="VALUE_NEVER_PUBLISH";
                    (instruments?body["data"]!:body["data"]![0]!["details"]!).AsArray().Add(later);
                }
                if(scenario=="middle-only")
                {
                    var before=detail.DeepClone();var after=detail.DeepClone();detail["alphaMetric"]="VALUE_NEVER_PUBLISH";
                    after["OTHER_ROW_NEVER_COPY"]="VALUE_NEVER_PUBLISH";body["data"]!.AsArray().Insert(0,before);body["data"]!.AsArray().Add(after);
                }
                if(scenario=="known-mixed")
                {detail["futureSettlement"]=false;detail["tradeQuoteCcyList"]=new JsonArray("EUR");detail["upcChg"]=new JsonArray();detail["instIdCode"]=123;detail["alphaMetric"]="VALUE_NEVER_PUBLISH";}
                if(scenario=="nested-known")detail["upcChg"]=new JsonArray(new JsonObject{["alphaMetric"]="VALUE_NEVER_PUBLISH"});
                if(scenario=="bool-type")detail["futureSettlement"]="false";
                if(scenario=="array-type")detail["tradeQuoteCcyList"]="EUR";
                if(scenario=="integer-type")detail["instIdCode"]="123";
                if(scenario=="cancel")cancellation!.Cancel();
            }
            string json=body.ToJsonString();
            if(Count==(instruments?4:3)&&scenario=="duplicate")
            {string key=instruments?"instId":"ccy";json=json.Replace("\""+key+"\":","\"alphaMetric\":\"x\",\"alphaMetric\":\"y\",\""+key+"\":",StringComparison.Ordinal);}
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json)});
        }
    }
}
