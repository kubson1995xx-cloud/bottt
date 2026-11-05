using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Okx.DemoAcceptance;
using Xunit;

namespace Apex.Tests;

public sealed class OkxBalanceSchemaDiagnosticTests
{
    public OkxBalanceSchemaDiagnosticTests() => DenyNetwork();
    // Install after VSTest opens its local communication socket. TSYNC covers
    // existing async workers; no product transport can create IPv4/IPv6 sockets.
    private static readonly object NetworkGate = new();
    private static bool networkDenied;
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Filter { public ushort Code; public byte Jt,Jf; public uint K; public Filter(ushort c,byte t,byte f,uint k){Code=c;Jt=t;Jf=f;K=k;} }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct FilterProgram { public ushort Length; public IntPtr Filters; }
    [System.Runtime.InteropServices.DllImport("libc",SetLastError=true)] private static extern int prctl(int option,ulong a,ulong b,ulong c,ulong d);
    [System.Runtime.InteropServices.DllImport("libc",SetLastError=true)] private static extern long syscall(long number,int operation,uint flags,ref FilterProgram program);
    private static void DenyNetwork()
    {
        if(!OperatingSystem.IsLinux())return;
        lock(NetworkGate)
        {
            if(networkDenied)return;
            Assert.Equal(System.Runtime.InteropServices.Architecture.X64,System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
            var filters=new[]{new Filter(0x20,0,0,0),new Filter(0x15,0,5,41),new Filter(0x20,0,0,16),new Filter(0x15,2,0,2),new Filter(0x15,1,0,10),new Filter(0x06,0,0,0x7fff0000),new Filter(0x06,0,0,0x5000d),new Filter(0x06,0,0,0x7fff0000)};
            var pinned=System.Runtime.InteropServices.GCHandle.Alloc(filters,System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                var program=new FilterProgram{Length=(ushort)filters.Length,Filters=pinned.AddrOfPinnedObject()};
                Assert.Equal(0,prctl(38,1,0,0,0));Assert.Equal(0L,syscall(317,1,1,ref program));networkDenied=true;
            }
            finally{pinned.Free();}
        }
    }
    [Fact]
    public async Task Linux_network_denial_is_calibrated_on_async_workers()
    {
        if(!OperatingSystem.IsLinux())return;
        await Task.Run(()=>
        {
            foreach(var family in new[]{System.Net.Sockets.AddressFamily.InterNetwork,System.Net.Sockets.AddressFamily.InterNetworkV6})
            {
                var error=Assert.Throws<System.Net.Sockets.SocketException>(()=>new System.Net.Sockets.Socket(family,System.Net.Sockets.SocketType.Stream,System.Net.Sockets.ProtocolType.Tcp));
                Assert.Equal(13,error.NativeErrorCode);
            }
        });
    }
    // Independent oracle: EEA REST R1333-1554 and account WS R4871/4899/5043/5051.
    // The production change under test supplies CLOSED diagnostics, NOT admission.
    public static IEnumerable<object[]> DocumentedNamesAndKinds()
    {
        foreach (string name in new[] { "coinUsdPrice", "maxSpotInUseAmt" })
            foreach (var value in new[] { ("\"" + Peer.Private + "\"", "String"), ("123", "Number"), ("false", "Boolean"), ("null", "Null"), ("{}", "Object"), ("[]", "Array") })
                yield return new object[] { name, value.Item1, value.Item2 };
    }
    [Theory]
    [MemberData(nameof(DocumentedNamesAndKinds))]
    public async Task Documented_WS_only_name_is_identified_but_still_refused_in_REST(string name, string wire, string kind)
    {
        using var peer = new Peer(d => d[name] = JsonNode.Parse(wire));
        var result = await Discover(peer);
        using var report = Report(result, peer);
        var summary = Summary(report.RootElement);
        var item = Assert.Single(summary.EnumerateArray());
        Assert.Equal(name, item.GetProperty("Name").GetString());
        Assert.Equal("String", item.GetProperty("DocumentedType").GetString());
        Assert.Equal(kind, item.GetProperty("Actual").GetString());
        Assert.Equal(1, item.GetProperty("Count").GetInt32());
    }
    [Fact]
    public async Task Arbitrary_names_are_grouped_by_kind_and_never_copied_or_recursed()
    {
        using var peer = new Peer(d =>
        {
            for (int i=0;i<100;i++) d[Peer.Private+i] = Peer.Private;
            d[Peer.Private+"obj"] = new JsonObject { [Peer.Private] = Peer.Private };
            d[Peer.Private+"array"] = new JsonArray(Peer.Private);
            d["coinUsdPrice"] = Peer.Private;
            d["maxSpotInUseAmt"] = "";
        });
        using var report = Report(await Discover(peer), peer);
        var summary = Summary(report.RootElement).EnumerateArray().ToArray();
        Assert.Equal(5, summary.Length);
        Assert.Equal(100, summary.Single(x=>x.GetProperty("Name").GetString()=="<unknown>" && x.GetProperty("Actual").GetString()=="String").GetProperty("Count").GetInt32());
        Assert.Equal(104, summary.Sum(x=>x.GetProperty("Count").GetInt32()));
        Assert.All(summary.Where(x=>x.GetProperty("Name").GetString()=="<unknown>"), x=>Assert.Equal("Undocumented",x.GetProperty("DocumentedType").GetString()));
    }
    [Theory]
    [InlineData("CoinUsdPrice")]
    [InlineData("MAXSPOTINUSEAMT")]
    public async Task Diagnostic_names_are_exact_case_not_aliases(string name)
    {
        using var peer = new Peer(d=>d[name]=Peer.Private);
        using var report = Report(await Discover(peer),peer);
        var item = Assert.Single(Summary(report.RootElement).EnumerateArray());
        Assert.Equal("<unknown>",item.GetProperty("Name").GetString());
        Assert.Equal("String",item.GetProperty("Actual").GetString());
    }
    [Fact]
    public async Task Recursive_duplicate_guard_runs_before_unsupported_summary()
    {
        using var peer = new Peer(d=>d["coinUsdPrice"]=new JsonObject { ["cashBal"]="1", ["CASHBAL"]="2" });
        var result=await Discover(peer);
        Assert.Null(result.Observation); Assert.Equal("DISCOVERY_INVALID_PAYLOAD",result.Code);Assert.Equal(3,peer.Count);
        Assert.Equal("PARSE_UNIQUE",result.Diagnostic!.Phase);
        Assert.Equal("DUPLICATE_PROPERTY",result.Diagnostic.Payload!.Reason);
        using var doc=JsonDocument.Parse(JsonSerializer.Serialize(result.Diagnostic.Payload));
        Assert.False(doc.RootElement.TryGetProperty("UnsupportedProperties",out _));
        AssertPrivateAbsent(JsonSerializer.Serialize(result));
    }
    [Fact]
    public async Task Current_independent_REST_example_is_accepted_without_diagnostic_or_money_rewrite()
    {
        using var peer = new Peer(null);
        var result=await Discover(peer);
        Assert.Equal("CONNECTED_READ_ONLY_STOP",result.Code);Assert.Equal(4,peer.Count);Assert.Null(result.Diagnostic);
        var row=Assert.Single(result.Observation!.Balances);
        Assert.Equal("4850.435693622894",row.Cash.Raw);Assert.Equal(4850.435693622894m,row.Cash.Value);
        Assert.Equal("1705449605015",row.UTime.Raw);Assert.Equal("USDT",row.Currency);
    }
    private static JsonElement Summary(JsonElement root)
    {
        var payload=root.GetProperty("discoveryDiagnostic").GetProperty("Diagnostic").GetProperty("Payload");
        Assert.True(payload.TryGetProperty("UnsupportedProperties",out var summary),"CLOSED_UNSUPPORTED_SUMMARY_MISSING"); // intended old-DLL RED
        return summary;
    }
    private static JsonDocument Report(OkxEnrollmentDiscovery result,Peer peer)
    {
        Assert.Equal("DISCOVERY_INVALID_PAYLOAD",result.Code);Assert.Null(result.Observation);Assert.Equal(3,peer.Count);
        var d=result.Diagnostic!;Assert.Equal(200,d.HttpStatus);Assert.Equal("0",d.OkxCode);Assert.Equal("/api/v5/account/balance",d.Endpoint);
        Assert.True(d.ConfigAuthenticated);Assert.True(d.AccountSafetyValidated);Assert.Equal("ACCOUNT_SAFETY_VALIDATED",d.LastCompletedStage);Assert.Equal("PARSE_SCHEMA",d.Phase);
        var f=d.Payload!;Assert.Equal("UNSUPPORTED_PROPERTY",f.Reason);Assert.Equal("$.data[].details[].<unknown>",f.Path);Assert.Equal("KnownProperty",f.Expected);
        byte[] bytes=TransactionReport.Build("synthetic-schema",DateTimeOffset.UtcNow,true,"READ_ONLY_DISCOVERY_REFUSED_STOP_NO_TRADING",new[]{"STOP"},null,new{result.Code,result.Diagnostic},null,false,null,"CHECK_CONNECTION_GET_ONLY");
        try
        {
            AssertPrivateAbsent(Encoding.UTF8.GetString(bytes));var report=JsonDocument.Parse(Encoding.UTF8.GetString(bytes));
            var root=report.RootElement;Assert.Equal("STOP",root.GetProperty("mode").GetString());Assert.False(root.GetProperty("armed").GetBoolean());Assert.False(root.GetProperty("schedulerStarted").GetBoolean());
            foreach(string name in new[]{"baseline","orders","intents","fills","reservations","transport"})Assert.Equal(JsonValueKind.Null,root.GetProperty(name).ValueKind);
            return report;
        }
        finally{CryptographicOperations.ZeroMemory(bytes);}
    }
    private static void AssertPrivateAbsent(string text)
    {
        foreach(string s in new[]{Peer.Private,Peer.Key,Peer.Secret,Peer.Pass,Peer.Uid,Peer.Ip,Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(Peer.Key))).ToLowerInvariant()})
            Assert.True(!text.Contains(s,StringComparison.Ordinal),"PRIVATE_SENTINEL_WITHHELD");
    }
    private static Task<OkxEnrollmentDiscovery> Discover(Peer peer)=>OkxEnrollmentDiscoveryClient.RunAsync("Demo","DemoTradingApi",Encoding.ASCII.GetBytes(Peer.Key),Encoding.ASCII.GetBytes(Peer.Secret),Encoding.ASCII.GetBytes(Peer.Pass),null,null,default,peer);
    private sealed class Peer(Action<JsonObject>? mutate):HttpMessageHandler
    {
        internal const string Private="SYNTHETIC_UNTRUSTED_BALANCE_SCHEMA_VALUE",Key="schema-key",Secret="schema-secret",Pass="schema-pass",Uid="987654321098765432",Ip="192.0.2.198";
        internal int Count;
        private static readonly string[] Paths={"/api/v5/public/time","/api/v5/account/config","/api/v5/account/balance","/api/v5/account/instruments?instType=SPOT"};
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get,request.Method);Assert.Null(request.Content);Assert.Equal("https",request.RequestUri!.Scheme);Assert.Equal("eea.okx.com",request.RequestUri.Host);Assert.Equal("1",request.Headers.GetValues("x-simulated-trading").Single());
            string path=request.RequestUri.PathAndQuery;Assert.Equal(Paths[Count++],path);
            if(Count>1){string stamp=request.Headers.GetValues("OK-ACCESS-TIMESTAMP").Single();Assert.Equal(OkxRequestSigner.Sign(Encoding.ASCII.GetBytes(Secret),stamp,"GET",path,ReadOnlySpan<byte>.Empty),request.Headers.GetValues("OK-ACCESS-SIGN").Single());}
            JsonNode row;
            if(Count==1)row=new JsonObject{["ts"]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()};
            else if(Count==2)row=JsonNode.Parse("{\"uid\":\""+Uid+"\",\"mainUid\":\""+Uid+"\",\"acctLv\":\"1\",\"perm\":\"read_only,trade\",\"ip\":\""+Ip+"\",\"feeType\":\"0\",\"autoLoan\":false,\"enableSpotBorrow\":false,\"spotBorrowAutoRepay\":false,\"type\":\"0\",\"spotRoleType\":\"0\",\"kycLv\":\"2\"}")!;
            else if(Count==3)
            {
                using var stream=typeof(OkxBalanceSchemaDiagnosticTests).Assembly.GetManifestResourceStream("Apex.Tests.Fixtures.okx-balance-current.json")!;
                row=JsonNode.Parse(stream)!["data"]![0]!.DeepClone();mutate?.Invoke(row["details"]![0]!.AsObject());
            }
            else row=JsonNode.Parse("{\"instType\":\"SPOT\",\"instId\":\"BTC-USDC\",\"baseCcy\":\"BTC\",\"quoteCcy\":\"USDC\",\"state\":\"live\",\"tradeQuoteCcyList\":[\"USDC\"]}")!;
            var body=new JsonObject{["code"]="0",["msg"]=Private,["data"]=new JsonArray(row)};
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body.ToJsonString())});
        }
    }
}
