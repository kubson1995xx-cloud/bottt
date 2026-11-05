using System;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Apex.Okx.DemoAcceptance;
using Xunit;
namespace Apex.Tests;
internal static class OkxCurrentMarketFundingNative
{
    internal static async Task<int> Run(string[] args)
    {
        if(args.Length<3)return 91;string root=Path.GetFullPath(args[1]);
        if(!root.StartsWith("/home/ubuntu/apex-deliveries/current-market-funding-synthetic/",StringComparison.Ordinal))return 92;
        string run=Path.Combine(root,"run");Directory.CreateDirectory(root,(UnixFileMode)448);Directory.CreateDirectory(run,(UnixFileMode)448);
        var vault=new LinuxDemoCredentialVault(Path.Combine(root,"vault"));
        string action=args[2];
        if(action=="seed"){await vault.SaveNewAsync(OperationalPeer.Key,OperationalPeer.Secret,OperationalPeer.Pass);Console.WriteLine("SYNTHETIC_NATIVE_SEEDED");return 0;}
        // Explicit friend seam required; there is intentionally no production Main fallback.
        var binding=OkxConfiguration.Bind(new OkxNavalOptions{Schema="okx-naval-config-v1",Scopes=new[]{new OkxScopeOptions{ScopeId="finite-demo",Environment="OkxDemo",Region="EEA",ExpectedUid="99123456789",ExpectedMainUid="99123456789",QuoteCurrency="USDC",CredentialBindingId="foreground-demo",SafetyVersion="okx-cash-v1"}}})[0];
        await using var f=new OperationalPeer(run,binding,preserve:true);f.Clock.UseSystemUtc=true;
        using var peer=new FiniteDemoPeer(f){BaseCurrency="ADA",OwnFreezes=true};f.Mutate=(p,b)=>OkxCurrentMarketFundingTests.Shape(f,peer,p,b);
        using var publicPeer=action.StartsWith("public-",StringComparison.Ordinal)?new OkxFinitePublicFailureTests.PublicPeer(peer,action):null;
        using var bookPeer=action.StartsWith("book-",StringComparison.Ordinal)?OkxPublicBookSchemaTests.NativePeer(action):null;
        var composition=new FiniteEntryTestComposition(f.Clock,
            (s,k,h,x)=>OkxExecutionObservationClient.ForTests(f,f.Clock,s,k,h,x),
            (s,k,h,x)=>OkxObservationStream.ForTests((uri,ct)=>Task.FromResult<WebSocket>(new OperationalSocket(uri)),f.Clock,s,k,h,x),
            (s,k,h,x)=>OkxExecutionClient.ForTests(peer,f.Clock,s,k,h,x),bookPeer??publicPeer??(System.Net.Http.HttpMessageHandler)peer);
        int discovery=0;
        var cli=action switch{"provision"=>new[]{"--provision-funding-premise"},"check"=>new[]{"--check-connection"},"status"=>new[]{"--credentials-status"},_=>Array.Empty<string>()};
        int code=await Apex.Okx.DemoAcceptance.Program.RunFiniteForTestsAsync(cli,run,vault,async(owner,ct)=>{discovery++;return await owner.DiscoverPrivateSchema((_,_,_)=>{},ct,f);},composition);
        if(action.StartsWith("book-",StringComparison.Ordinal))OkxPublicBookSchemaTests.AssertNative(Path.Combine(run,"last-report.json"),action,code,((OkxPublicBookSchemaTests.Peer)bookPeer!).Books,peer.Sends.Count,f.Paths.ToArray());
        if(action.StartsWith("public-",StringComparison.Ordinal))OkxFinitePublicFailureTests.AssertReport(Path.Combine(run,"last-report.json"),action,code,publicPeer!.Books,peer.Sends.Count,f.Paths.ToArray());
        if(action=="run"&&code==0)
        {
            Assert.Equal(1,discovery);Assert.Equal(5,peer.Sends.Count);
            using var report=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(run,"last-report.json")));var json=report.RootElement;
            Assert.Equal("COMPLETED_STOP_WITH_EXACT_DUST",json.GetProperty("result").GetString());Assert.Equal("STOP",json.GetProperty("mode").GetString());Assert.False(json.GetProperty("armed").GetBoolean());
            Assert.Equal(2,json.GetProperty("fills").GetArrayLength());Assert.Equal(6,json.GetProperty("baseline").GetProperty("Observations").GetProperty("Balances").GetArrayLength());
            // Original balances have three missing records. New own ADA dust adds a
            // fourth uncovered currency; never invent its per-currency zero.
            Assert.Equal(4,json.GetProperty("funding").GetProperty("uncoveredBalanceCurrencies").GetArrayLength());
            Console.WriteLine("SYNTHETIC_NATIVE_FINITE_OWNER_SEQUENCE_STOP_5_SENDS_2_FILLS");
        }
        if(action=="oldstate"){Assert.Equal(3,code);Assert.Equal(0,discovery);Assert.Empty(f.Paths);Assert.Empty(peer.Sends);}
        if(action=="missing"){Assert.NotEqual(0,code);Assert.Equal(0,discovery);Assert.Empty(peer.Sends);}
        if(action is "provision" or "status"){Assert.Equal(0,discovery);Assert.Empty(peer.Sends);}
        Console.WriteLine("SYNTHETIC_DISCOVERY="+discovery+";SENDS="+peer.Sends.Count+";CODE="+code);return code;
    }
}
