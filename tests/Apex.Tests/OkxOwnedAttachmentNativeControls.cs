using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Apex.Core.Engine;
using Apex.Core.Naval;
using Apex.Okx.DemoAcceptance;
using Xunit;
namespace Apex.Tests;

internal static class OkxOwnedAttachmentNativeControls
{
    internal static async Task Check(OperationalPeer f,FiniteDemoPeer peer,string root,LinuxDemoCredentialVault vault,FiniteEntryTestComposition composition,EngineState before,Action<string,JsonNode> original,int sends)
    {
        string reports=Path.Combine(root,"owned-exit-diagnostic-reports");
        var preserved=Directory.GetFiles(root,"*",SearchOption.AllDirectories).Where(p=>p!=f.Target).ToDictionary(p=>p,p=>File.ReadAllBytes(p));
        var results=new System.Collections.Generic.List<object>();
        foreach(string variant in new[]{"parsed","account-refused","malformed-first","secret-first","groups-cap-first"})
        {
            int order=0;f.Paths.Clear();f.Mutate=(p,b)=>
            {
                original(p,b);
                if(p=="/api/v5/account/config"&&variant=="account-refused")b["data"]![0]!["uid"]="99999999";
                if(p is not ("/api/v5/trade/order" or "/api/v5/trade/order-algo"))return;
                var row=b["data"]![0]!;bool algo=p.EndsWith("order-algo",StringComparison.Ordinal);
                if(algo){row["aggressiveness"]="opaque";row["lmtOrderNumber"]="opaque";row["triggerParams"]=new JsonArray();}
                else row["slippage"]="synthetic-nonempty-opaque";
                if(variant is "parsed" or "account-refused")return;
                var nested=new JsonObject{[algo?"slOrdPx":"failCode"]=""};
                if(algo)nested["independentProtection"]="VALUE_MUST_NOT_LEAK";
                else if(order++>0)nested["laterUnknown"]="VALUE_MUST_NOT_LEAK";
                else if(variant=="groups-cap-first")for(int n=0;n<33;n++)nested["syntheticUnknown"+(char)('A'+n/26)+(char)('A'+n%26)]="VALUE_MUST_NOT_LEAK";
                else nested[variant=="secret-first"?Encoding.ASCII.GetString(OperationalPeer.Secret):"not-identifier"]="VALUE_MUST_NOT_LEAK";
                row["attachAlgoOrds"]=new JsonArray(nested);
            };
            var old=Directory.GetFiles(reports,"*.json").ToHashSet(StringComparer.Ordinal);
            int code=await Program.RunFiniteForTestsAsync(new[]{"--diagnose-owned-exit"},root,vault,(_,_)=>throw new Exception("NO_DISCOVERY"),composition);
            Assert.Equal(variant=="account-refused"?7:5,code);
            string report=Assert.Single(Directory.GetFiles(reports,"*.json").Where(p=>!old.Contains(p)));
            using var doc=JsonDocument.Parse(File.ReadAllBytes(report));var r=doc.RootElement;
            Assert.Equal("EXISTING_OWNER_RETROSPECTIVE_ATTACHMENT_GET_ONLY",r.GetProperty("operation").GetString());Assert.True(r.GetProperty("complete").GetBoolean());Assert.False(r.GetProperty("armed").GetBoolean());Assert.False(r.GetProperty("schedulerStarted").GetBoolean());
            var state=JsonSerializer.Deserialize<EngineState>(File.ReadAllText(f.Target))!;
            Assert.Equal("STOP",state.Naval!.Mode);Assert.Equal(NavalJson.Serialize(before.NavalBroker!.Baseline),NavalJson.Serialize(state.NavalBroker!.Baseline));
            Assert.Equal(NavalJson.Serialize(before.Naval!.Reservations),NavalJson.Serialize(state.Naval.Reservations));Assert.Equal(NavalJson.Serialize(before.Naval.Fills),NavalJson.Serialize(state.Naval.Fills));
            Assert.Equal(NavalJson.Serialize(before.NavalBroker.Operational),NavalJson.Serialize(state.NavalBroker.Operational! with{StopEpoch=before.NavalBroker.Operational!.StopEpoch}));Assert.Equal(before.NavalBroker.Namespace,state.NavalBroker.Namespace);Assert.Equal(before.NavalBroker.HighWater,state.NavalBroker.HighWater);Assert.Equal(sends,peer.Sends.Count);
            string directory=Path.Combine(reports,Path.GetFileNameWithoutExtension(report));
            var inventories=Directory.Exists(directory)?Directory.GetFiles(directory,"order-protection-identifiers.json",SearchOption.AllDirectories):Array.Empty<string>();
            bool withheld=variant.EndsWith("first",StringComparison.Ordinal);Assert.Equal(withheld?1:0,inventories.Length);Assert.Equal(variant=="account-refused"?1:4,f.Paths.Count);
            if(withheld)
            {
                using var inventory=JsonDocument.Parse(File.ReadAllBytes(inventories[0]));var m=inventory.RootElement;Assert.Equal("/api/v5/trade/order-algo",m.GetProperty("endpoint").GetString());Assert.Equal("independentProtection",Assert.Single(m.GetProperty("groups").EnumerateArray()).GetProperty("name").GetString());
                var obs=r.GetProperty("transport").GetProperty("observation");var endpoints=obs.GetProperty("endpointInventories");Assert.Equal("WITHHELD",endpoints.EnumerateArray().Single(e=>e.GetProperty("endpoint").GetString()=="/api/v5/trade/order").GetProperty("state").GetString());
                Assert.All(obs.GetProperty("reads").EnumerateArray(),read=>Assert.Equal(JsonValueKind.Null,read.GetProperty("observation").ValueKind));
            }
            else if(variant=="parsed")Assert.All(r.GetProperty("transport").GetProperty("observation").GetProperty("reads").EnumerateArray(),read=>Assert.NotEqual(JsonValueKind.Null,read.GetProperty("observation").ValueKind));
            foreach(var pair in preserved)Assert.True(pair.Value.AsSpan().SequenceEqual(File.ReadAllBytes(pair.Key)),"Prior report/inventory/cipher changed");
            foreach(string file in Directory.GetFiles(reports,"*",SearchOption.AllDirectories))
            {byte[] bytes=File.ReadAllBytes(file);Assert.True(bytes.AsSpan().IndexOf(OperationalPeer.Secret)<0);Assert.DoesNotContain("VALUE_MUST_NOT_LEAK",Encoding.UTF8.GetString(bytes));}
            results.Add(new{variant,code,gets=f.Paths.Count,inventories=inventories.Length,preserved=true,noWriter=true,noEconomicOrIdMutation=true});
        }
        File.WriteAllText(Path.Combine(root,"retrospective-controls.json"),JsonSerializer.Serialize(results));
    }
}
