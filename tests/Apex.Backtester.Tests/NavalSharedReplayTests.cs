using System.Text.Json;
using Apex.Backtester;
using Xunit;
namespace Apex.Backtester.Tests;

public class NavalSharedReplayTests
{
    static string Fixture=>Path.Combine(Program.FindRepoRoot(),"tests/Apex.Backtester.Tests/fixtures/synthetic/naval-shared-replay-v1.json");
    [Fact] public void NavalSharedReplay_ExecutesSharedCapital_ActualCommandAndDurableContinuation()
    {
        string dir=Path.Combine(Environment.GetEnvironmentVariable("TMPDIR")??Path.GetTempPath(),"shared-replay-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try
        {
            string state=Path.Combine(dir,"state"),output=Path.Combine(dir,"result.json");
            Assert.Equal(0,Program.Main(new[]{"naval-shared-replay","--input",Fixture,"--state-dir",state,"--output",output}));
            using var report=JsonDocument.Parse(File.ReadAllText(output));var root=report.RootElement;
            Assert.Equal("OFFLINE_SHARED_CAPITAL / SYNTHETIC_NOT_RESEARCH / NO_BROKER_AUTHORITY",root.GetProperty("classification").GetString());
            var steps=root.GetProperty("steps").EnumerateArray().ToArray();
            Assert.Contains(steps,x=>x.GetProperty("view").GetProperty("cash").GetDecimal()==9599.600m);
            Assert.Equal(10039.160m,root.GetProperty("final").GetProperty("cash").GetDecimal());
            Assert.Equal(0,root.GetProperty("final").GetProperty("activeCount").GetInt32());
            Assert.Contains(steps,x=>x.GetProperty("op").GetString()=="restart"&&x.GetProperty("view").GetProperty("mode").GetString()=="STOP");
            var node=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Fixture))!;
            node["steps"]=System.Text.Json.Nodes.JsonNode.Parse("""
                [{"op":"reconcile"},{"op":"start"},
                 {"op":"confirm","fills":[{"referenceCommand":"alpha-btc","tradeId":"buy-partial","quantity":2,"price":100,"fee":0.2,"feeCurrency":"USD","tradeUtc":"2026-01-01T12:00:00Z"}]},
                 {"op":"confirm","fills":[{"referenceCommand":"alpha-btc","tradeId":"buy-1","quantity":2,"price":100,"fee":0.2,"feeCurrency":"USD","tradeUtc":"2026-01-01T12:00:00Z"}]},
                 {"op":"confirm","fills":[{"referenceCommand":"alpha-btc","tradeId":"sell-1","reduction":true,"quantity":4,"price":110,"fee":0.44,"feeCurrency":"USD","tradeUtc":"2026-01-01T12:00:00Z"}]}]
                """);
            string continuation=Path.Combine(dir,"continuation.json");File.WriteAllText(continuation,node.ToJsonString());
            Assert.Equal(0,Program.Main(new[]{"naval-shared-replay","--input",continuation,"--state-dir",state,"--output",output}));
            using var next=JsonDocument.Parse(File.ReadAllText(output));Assert.Equal(10039.160m,next.RootElement.GetProperty("final").GetProperty("cash").GetDecimal());
            Assert.Equal("STOP",next.RootElement.GetProperty("initial").GetProperty("mode").GetString());
            string authority=File.ReadAllText(Path.Combine(state,"naval-authority-v1.json"));using var durable=JsonDocument.Parse(authority);
            Assert.Equal(3,durable.RootElement.GetProperty("Naval").GetProperty("Fills").GetArrayLength());
        }
        finally{Directory.Delete(dir,true);}
    }
    [Theory] [InlineData("OkxDemo")] [InlineData("Live")] [InlineData("999")]
    public void NavalSharedReplay_RejectsBrokerEnvironmentBeforeMutation(string env)
    {
        string dir=Path.Combine(Environment.GetEnvironmentVariable("TMPDIR")??Path.GetTempPath(),"shared-deny-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try{string input=Path.Combine(dir,"input.json"),state=Path.Combine(dir,"state");
            File.WriteAllText(input,File.ReadAllText(Fixture).Replace("\"Offline\"","\""+env+"\""));
            Assert.NotEqual(0,Program.Main(new[]{"naval-shared-replay","--input",input,"--state-dir",state,"--output",Path.Combine(dir,"out.json")}));
            Assert.False(Directory.Exists(state));}
        finally{Directory.Delete(dir,true);}
    }
}
