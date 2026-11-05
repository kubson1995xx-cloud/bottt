using System;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Engine;
using Apex.Desktop.Okx;
using Apex.Okx.DemoAcceptance;
using Xunit;
namespace Apex.Tests;
public sealed partial class OkxConditionalTradingTests
{
    internal static async Task<int> LinuxHostNative(string scenario,string root)
    {
        if(scenario.StartsWith("mixed-",StringComparison.Ordinal))return await OkxMixedHostNative.Run(scenario,root);
        Assert.StartsWith("/home/ubuntu/apex-deliveries/linux-host-native-",root);
        LinuxInput.Harden();
        var binding=OkxConfiguration.Bind(new OkxNavalOptions{Schema="okx-naval-config-v1",Scopes=new[]{new OkxScopeOptions{ScopeId="linux-demo-bot",Environment="OkxDemo",Region="EEA",ExpectedUid="9911112222",ExpectedMainUid="9911112222",QuoteCurrency="USDC",CredentialBindingId="native-demo-vault",SafetyVersion="okx-cash-v1"}}})[0];
        OkxLinuxPrivate.AdmitDirectory(root,true);
        if(scenario=="finite-owner")
        {
            var finiteBinding=OkxConfiguration.Bind(new OkxNavalOptions{Schema="okx-naval-config-v1",Scopes=new[]{new OkxScopeOptions{ScopeId="finite-demo",Environment="OkxDemo",Region="EEA",ExpectedUid="9911112222",ExpectedMainUid="9911112222",QuoteCurrency="USDC",CredentialBindingId="foreground-demo",SafetyVersion="okx-cash-v1"}}})[0];
            await using var finite=new OperationalPeer(root,finiteBinding,preserve:true,finite:true);await finite.Connect();Assert.True(await finite.Service.ApproveAndCreateAsync(default));
        }
        await using var f=new OperationalPeer(root,binding,preserve:true);
        var vault=new LinuxDemoCredentialVault(Path.Combine(root,"vault"));
        if(scenario!="reopen")await vault.SaveNewAsync(OperationalPeer.Key,OperationalPeer.Secret,OperationalPeer.Pass);
        byte[] cipher=File.ReadAllBytes(Path.Combine(root,"vault",LinuxDemoCredentialVault.RecordName));
        using var peer=new TradingPeer(f);Action<string,System.Text.Json.Nodes.JsonNode> economics=(p,b)=>
        {
            if(p=="/api/v5/account/config")b["data"]![0]!["feeType"]="0";
            if(scenario=="finite-owner"&&p=="/api/v5/account/interest-limits")b["data"]![0]!["debt"]="0.01";
            if(p=="/api/v5/account/instruments"){b["data"]![0]!["lotSz"]="0.000001";b["data"]![0]!["minSz"]="0.000001";}
            if(scenario=="six-balances"&&p=="/api/v5/account/balance")
            {
                var row=b["data"]![0]!;var first=row["details"]![0]!;var rows=new System.Text.Json.Nodes.JsonArray(first.DeepClone());
                foreach(string currency in new[]{"BTC","ETH","SOL","EUR","ADA"}){var v=first.DeepClone();v["ccy"]=currency;v["liab"]="";rows.Add(v);}row["details"]=rows;
            }
        };
        if(scenario=="blocked"){peer.HoldResponse=new(TaskCreationOptions.RunContinuationsAsynchronously);peer.BeforeResponse=()=>Console.WriteLine("DISPATCH_BLOCKED");}
        using var held=scenario=="lock"?new FileStream(Path.Combine(root,".run.lock"),new FileStreamOptions{Mode=FileMode.OpenOrCreate,Access=FileAccess.ReadWrite,Share=FileShare.None,UnixCreateMode=(UnixFileMode)384}):null;
        var preserved=Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).Where(p=>Path.GetFileName(p) is OkxNavalStore.Target or ".naval-broker-initialized-v2").ToDictionary(p=>p,File.ReadAllBytes);
        if(scenario=="cycle")
        {
            // Economic source clocks are execution-time facts, not first-read time.
            // Installing at the first later GET can place a fill beyond that pass's
            // already fixed cut, correctly vetoing continuation nondeterministically.
            peer.BeforeResponse=()=>
            {
                InstallOrdinaryPair(f,peer,peer.Size*.001m);economics=f.Mutate!;
                peer.ExpectedSellSize=peer.Size*(1-.001m);peer.ExitSignal=true;
            };
            peer.BeforeMutationResponse=kind=>{if(kind=="sell"){f.Mutate=economics;InstallOrdinarySellPair(f,peer,0);economics=f.Mutate!;}};
        }
        f.Mutate=(p,b)=>economics(p,b);
        // Install helpers replace f.Mutate. Retain this economic wrapper on each read.
        Action<string,System.Text.Json.Nodes.JsonNode> update=(p,b)=>{economics(p,b);if(p=="/api/v5/account/instruments"){b["data"]![0]!["lotSz"]="0.000001";b["data"]![0]!["minSz"]="0.000001";}};var transport=new FiniteEntryTestComposition(f.Clock,(s,k,h,x)=>OkxExecutionObservationClient.ForTests(new NativeReads(f,update),f.Clock,s,k,h,x,(t,ct)=>{f.Clock.Advance(t);return Task.CompletedTask;}),
            (s,k,h,x)=>OkxObservationStream.ForTests((uri,ct)=>Task.FromResult<WebSocket>(new OperationalSocket(uri)),f.Clock,s,k,h,x),
            (s,k,h,x)=>OkxExecutionClient.ForTests(scenario=="drain-fault"?new DrainFaultWrites():scenario=="blocked"?new BlockingWrites(peer,root):(System.Net.Http.HttpMessageHandler)peer,f.Clock,s,k,h,x),peer);
        int discoveries=0;byte[][]? secretBuffers=null;
        Func<EphemeralCredentials,CancellationToken,Task<OkxEnrollmentDiscovery>> discover=async(k,ct)=>
        {
            discoveries++;Assert.Throws<IOException>(()=>new FileStream(Path.Combine(root,".run.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None));
            Assert.Equal(9,await Apex.Okx.DemoAcceptance.Program.RunAsync(new[]{"--credentials-status"},root,vault,(_,_)=>throw new Exception("MANAGEMENT_MUST_NOT_DISCOVER")));
            Assert.Equal(9,await Apex.Okx.DemoAcceptance.Program.RunFiniteForTestsAsync(Array.Empty<string>(),root,vault,(_,_)=>throw new Exception("FINITE_MUST_NOT_DISCOVER"),transport));
            secretBuffers=new[]{"key","secret","passphrase"}.Select(name=>(byte[])typeof(EphemeralCredentials).GetFields(System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Single(field=>field.FieldType==typeof(byte[])&&field.Name.Contains(name,StringComparison.Ordinal)).GetValue(k)!).ToArray();
            var d=await k.DiscoverPrivateSchema((_,_,_)=>{},ct,new NativeReads(f,update));Assert.NotNull(d.Observation);
            return scenario switch
            {
                "environment"=>d with{Observation=d.Observation! with{Environment="Live"}},
                "provenance"=>d with{Observation=d.Observation! with{Provenance="LiveAccountApi"}},
                "fingerprint"=>d with{Observation=d.Observation! with{Fingerprint=new string('a',64)}},
                "identity"=>d with{Observation=d.Observation! with{Uid="999991",MainUid="999991"}},_=>d
            };
        };
        int code=await (Task<int>)OkxLinuxDemoHostTests.Entry().Invoke(null,new object?[]{root,vault,Console.In,Console.Out,discover,transport,CancellationToken.None})!;
        Assert.True(cipher.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(root,"vault",LinuxDemoCredentialVault.RecordName)))) ;
        if(scenario=="drain-fault")
        {
            Assert.Equal(7,code);Assert.Equal(0,peer.Posts);Assert.Throws<IOException>(()=>new FileStream(Path.Combine(root,".run.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None));
            Console.WriteLine("NATIVE_DRAIN_FAULT_RETAINED");return code;
        }
        if(secretBuffers!=null)Assert.All(secretBuffers,bytes=>Assert.All(bytes,b=>Assert.Equal((byte)0,b)));
        Assert.Equal(scenario is "lock"?0:1,discoveries);
        if(scenario is not ("cycle" or "reopen" or "blocked"))
        {
            Assert.False(File.Exists(f.Target));foreach(var item in preserved)
            {
                if(scenario=="finite-owner"&&Path.GetFileName(item.Key)==OkxNavalStore.Target)
                {var a=JsonSerializer.Deserialize<EngineState>(item.Value)!;var b=JsonSerializer.Deserialize<EngineState>(File.ReadAllBytes(item.Key))!;Assert.Equal(Apex.Core.Naval.NavalJson.Serialize(a.NavalBroker!.Baseline),Apex.Core.Naval.NavalJson.Serialize(b.NavalBroker!.Baseline));Assert.Equal(a.NavalBroker.Namespace,b.NavalBroker.Namespace);Assert.Equal(a.NavalBroker.HighWater,b.NavalBroker.HighWater);Assert.Empty(b.Naval!.Reservations);Assert.Null(b.NavalBroker.Operational?.OrdinaryDemo);}
                else Assert.True(item.Value.AsSpan().SequenceEqual(File.ReadAllBytes(item.Key)));
            }
            Console.WriteLine("NATIVE_ASSERTIONS_PASS posts="+peer.Posts);return code;
        }
        using(var released=new FileStream(Path.Combine(root,".run.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None)){}
        var state=JsonSerializer.Deserialize<EngineState>(File.ReadAllText(f.Target))!;Assert.Equal("STOP",state.Naval!.Mode);
        Assert.Null(state.NavalBroker!.Baseline.Observations.FiniteAllocation);Assert.Null(state.NavalBroker.Baseline.Observations.OperatorPremise);
        if(scenario=="cycle"){Assert.Equal(3,peer.Posts);Assert.Equal(2,state.Naval.Fills.Count);Assert.Equal(4,state.NavalBroker.Operational!.Bills.Count);Assert.Equal(0,state.Naval.Reservations.Single().Inventory);}
        else if(scenario=="blocked"){Assert.Equal(1,peer.Posts);Assert.Equal(Apex.Core.Naval.NavalExecutionStage.SubmitUnknown,state.NavalBroker.Operational!.Intents.Single().Stage);Assert.True(state.Naval.Reservations.Single().EntryHold>0);Assert.Empty(state.Naval.Fills);}
        else Assert.Equal(0,peer.Posts);
        Console.WriteLine("NATIVE_ASSERTIONS_PASS posts="+peer.Posts);return code;
    }
    private sealed class DrainFaultWrites:System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,CancellationToken ct)=>throw new Exception("NO_DISPATCH");
        protected override void Dispose(bool disposing)=>throw new IOException("SYNTHETIC_DRAIN_REFUSAL");
    }
    private sealed class BlockingWrites(TradingPeer peer,string root):System.Net.Http.HttpMessageHandler
    {
        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,CancellationToken ct)
        {
            using var cancel=ct.Register(()=>
            {
                Assert.Throws<IOException>(()=>new FileStream(Path.Combine(root,".run.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None));
                Console.WriteLine("DISPATCH_CANCELLED_LOCK_HELD");peer.HoldResponse!.TrySetResult();
            });
            return await new System.Net.Http.HttpMessageInvoker(peer,false).SendAsync(request,ct);
        }
    }
    private sealed class NativeReads(OperationalPeer f,Action<string,System.Text.Json.Nodes.JsonNode>? update):System.Net.Http.HttpMessageHandler
    {
        private string? last;
        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,CancellationToken ct)
        {
            if(File.Exists(f.Target))
            {
                var state=State(f);string status=state.Naval!.Mode+" "+state.Naval.Reason+" "+string.Join(",",state.NavalBroker!.Operational?.Obligations??Array.Empty<string>());
                if(last!=status){Console.WriteLine("NATIVE_STATE "+status);last=status;}
            }
            f.Mutate=update;
            return await new System.Net.Http.HttpMessageInvoker(f,false).SendAsync(request,ct);
        }
    }
}
