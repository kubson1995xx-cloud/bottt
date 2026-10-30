using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Apex.Backtester;
using Apex.Backtester.Naval;
using Apex.Core.Naval;
using Xunit;
namespace Apex.Backtester.Tests;
public class NavalSharedPublicationTests
{
    static string Fixture=>Path.Combine(Program.FindRepoRoot(),"tests/Apex.Backtester.Tests/fixtures/synthetic/naval-shared-replay-v1.json");
    [Fact] public void NavalSharedPublication_ExportsAcceptedSyntheticAuthorityWithProvenance()
    {
        using var f=new Files();Assert.Equal(0,f.Run());
        using var publication=JsonDocument.Parse(File.ReadAllBytes(f.Publication));var e=publication.RootElement;var p=e.GetProperty("publication");
        using var durable=JsonDocument.Parse(File.ReadAllBytes(f.Authority));using var result=JsonDocument.Parse(File.ReadAllBytes(f.Result));
        Assert.Equal("naval-read-publication-v1",p.GetProperty("schema").GetString());
        Assert.Equal("HISTORICAL_OFFLINE_CHECKPOINT",e.GetProperty("temporalClassification").GetString());Assert.Equal("UNKNOWN",e.GetProperty("sourceFreshness").GetString());
        Assert.Equal(durable.RootElement.GetProperty("DurableRevision").GetInt64().ToString(),p.GetProperty("financialRevision").GetString());
        var j=p.GetProperty("journal");var checkpoint=j.GetProperty("checkpoint");
        Assert.Equal(p.GetProperty("asOfSequence").GetString(),j.GetProperty("lastSequence").GetString());
        Assert.Equal("10039.16",checkpoint.GetProperty("accounts")[0].GetProperty("cash").GetProperty("amount").GetString());
        Assert.Equal("USD",checkpoint.GetProperty("accounts")[0].GetProperty("cash").GetProperty("currency").GetString());
        Assert.Equal(10039.160m,result.RootElement.GetProperty("final").GetProperty("cash").GetDecimal());
        Assert.Equal(3,checkpoint.GetProperty("fleets").EnumerateArray().Sum(x=>x.GetProperty("fills").GetArrayLength()));
        Assert.Equal(Hash(Fixture),e.GetProperty("provenance").GetProperty("inputSha256").GetString());
        Assert.Equal(Hash(typeof(NavalPortfolioCoordinator).Assembly.Location),e.GetProperty("provenance").GetProperty("coreSha256").GetString());
        Assert.Equal(Hash(typeof(Program).Assembly.Location),e.GetProperty("provenance").GetProperty("backtesterSha256").GetString());
        var typed=JsonSerializer.Deserialize<PublicationEnvelope>(File.ReadAllBytes(f.Publication),ExportJson)!;
        var content=new {typed.Schema,typed.TemporalClassification,typed.SourceClockUtc,typed.SourceFreshness,typed.Provenance,typed.Publication};
        Assert.Equal(typed.ContentSha256,Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(content,ExportJson))).ToLowerInvariant());
        Assert.False(NavalReadJournal.ReadPage(typed.Publication,new(typed.Publication.Journal.Epoch,typed.Publication.Journal.ScopeId,typed.Publication.AsOfSequence)).ResetRequired);
        if(!OperatingSystem.IsWindows())Assert.Equal(UnixFileMode.UserRead|UnixFileMode.UserWrite,File.GetUnixFileMode(f.Publication));
    }
    [Fact] public void NavalSharedPublication_RestartReplayKeepsCashAndFillIds()
    {
        using var f=new Files();Assert.Equal(0,f.Run());var before=f.Read();var oldIds=before.Journal.Batches.Select(b=>b.EventId).ToArray();
        var input=JsonNode.Parse(File.ReadAllText(Fixture))!;
        input["steps"]=JsonNode.Parse("""
        [{"op":"reconcile"},{"op":"start"},
        {"op":"confirm","fills":[{"referenceCommand":"alpha-btc","tradeId":"buy-partial","quantity":2,"price":100,"fee":0.2,"feeCurrency":"USD","tradeUtc":"2026-01-01T12:00:00Z"}]},
        {"op":"confirm","fills":[{"referenceCommand":"alpha-btc","tradeId":"buy-1","quantity":2,"price":100,"fee":0.2,"feeCurrency":"USD","tradeUtc":"2026-01-01T12:00:00Z"}]},
        {"op":"confirm","fills":[{"referenceCommand":"alpha-btc","tradeId":"sell-1","reduction":true,"quantity":4,"price":110,"fee":0.44,"feeCurrency":"USD","tradeUtc":"2026-01-01T12:00:00Z"}]}]
        """);File.WriteAllText(f.Input,input.ToJsonString());Assert.Equal(0,f.Run(f.Input));var after=f.Read();
        Assert.Equal(before.Journal.Epoch,after.Journal.Epoch);Assert.Equal(oldIds,after.Journal.Batches.Take(oldIds.Length).Select(b=>b.EventId));
        Assert.Equal(before.FinancialRevision+5,after.FinancialRevision);Assert.Equal(10039.160m,after.Journal.Checkpoint.Accounts.Single().Cash.Amount);
        Assert.Equal(before.Journal.Checkpoint.Fleets.SelectMany(x=>x.Fills).Select(x=>x.Key),after.Journal.Checkpoint.Fleets.SelectMany(x=>x.Fills).Select(x=>x.Key));
        Assert.DoesNotContain(after.Journal.Batches.Skip(oldIds.Length).SelectMany(x=>x.Facts),x=>x.Kind==NavalReadFactKind.OfflineFillAccepted);
        string authority=Hash(f.Authority);f.Empty();Assert.Equal(0,f.Run(f.Input));Assert.Equal(authority,Hash(f.Authority));
        Assert.Equal(after.Journal.Epoch,f.Read().Journal.Epoch);Assert.Equal(after.AsOfSequence,f.Read().AsOfSequence);
    }
    [Fact] public void NavalSharedPublication_ExportFailureLeavesAuthorityIntact()
    {
        using var f=new Files();string blocker=Path.Combine(f.Dir,"blocker");File.WriteAllText(blocker,"not a directory");
        Assert.Equal(2,f.Run(publication:Path.Combine(blocker,"publication.json")));Assert.True(File.Exists(f.Authority));Assert.True(File.Exists(f.Result));
        string committed=Hash(f.Authority);using(var durable=JsonDocument.Parse(File.ReadAllBytes(f.Authority)))Assert.Equal(10039.160m,durable.RootElement.GetProperty("Naval").GetProperty("Accounts")[0].GetProperty("Cash").GetDecimal());
        f.Empty();Assert.Equal(0,f.Run(f.Input));Assert.Equal(committed,Hash(f.Authority));Assert.Equal(10039.160m,f.Read().Journal.Checkpoint.Accounts.Single().Cash.Amount);
        byte[] previous=File.ReadAllBytes(f.Publication);string tempDirectory=f.Publication+".directory";Directory.CreateDirectory(tempDirectory);
        Assert.Equal(2,f.Run(f.Input,tempDirectory));Assert.Equal(committed,Hash(f.Authority));Assert.Equal(previous,File.ReadAllBytes(f.Publication));
        Assert.Empty(Directory.GetFiles(f.Dir,"*.candidate-*"));
    }
    [Theory] [InlineData("state")] [InlineData("trailing")] [InlineData("root")] [InlineData("input")] [InlineData("output")] [InlineData("nativecase")] [InlineData("uri")]
    public void NavalSharedPublication_ContainmentRefusesBeforeOpeningOwner(string kind)
    {
        using var f=new Files();string path=kind switch{
            "state"=>Path.Combine(f.State,"publication.json"),"trailing"=>f.State+Path.DirectorySeparatorChar,
            "root"=>Path.GetPathRoot(f.Dir)!,"input"=>Fixture,"output"=>f.Result,"uri"=>"https://invalid/publication",
            _=>OperatingSystem.IsWindows()?Path.Combine(f.State.ToUpperInvariant(),"publication.json"):Path.Combine(f.State,"publication.json")};
        string fixtureHash=Hash(Fixture);Assert.Equal(2,f.Run(publication:path));Assert.False(Directory.Exists(f.State));Assert.Equal(fixtureHash,Hash(Fixture));
    }
    [Fact] public void NavalSharedPublication_ContainmentAllowsSiblingAndRefusesSymlink()
    {
        using var f=new Files();Assert.Equal(0,f.Run(publication:Path.Combine(f.Dir,"state-sibling","publication.json")));
        if(!OperatingSystem.IsWindows())
        {
            string link=Path.Combine(f.Dir,"link");Directory.CreateSymbolicLink(link,f.State);string before=Hash(f.Authority);
            Assert.Equal(2,f.Run(publication:Path.Combine(link,"publication.json")));Assert.Equal(before,Hash(f.Authority));
        }
    }
    [Fact] public void NavalSharedPublication_RejectsZeroStepLegacyAsUninitialized()
    {
        using var f=new Files();f.Empty();Assert.Equal(2,f.Run(f.Input));Assert.False(File.Exists(f.Publication));
        var authority=JsonNode.Parse(File.ReadAllText(f.Authority))!;Assert.Null(authority["NavalReadJournal"]);
        // A schema5 baseline with accepted old economics also remains eventless until a real new commit.
        using var populated=new Files();Assert.Equal(0,populated.Run());var legacy=JsonNode.Parse(File.ReadAllText(populated.Authority))!;
        legacy.AsObject().Remove("NavalReadJournal");File.WriteAllText(populated.Authority,legacy.ToJsonString());string bytes=Hash(populated.Authority);
        populated.Empty();File.Delete(populated.Publication);Assert.Equal(2,populated.Run(populated.Input));Assert.Equal(bytes,Hash(populated.Authority));Assert.False(File.Exists(populated.Publication));
    }
    [Fact] public void NavalSharedPublication_NoSecretsPathsOrBrokerClaims()
    {
        using var f=new Files();Assert.Equal(0,f.Run());using var d=JsonDocument.Parse(File.ReadAllBytes(f.Publication));
        var keys=Properties(d.RootElement).ToArray();foreach(string forbidden in new[]{"inputPath","dataDir","stateDirectory","pairingKey","OwnerToken","token","conflicts","marks","receipts","commands","canStart","runtimeMode","winRate","profitFactor","realizedTotal"})
            Assert.DoesNotContain(keys,k=>string.Equals(k,forbidden,StringComparison.OrdinalIgnoreCase));
        string text=File.ReadAllText(f.Publication);Assert.DoesNotContain(f.Dir,text);Assert.DoesNotContain("PF999",text);
        var checkpoint=d.RootElement.GetProperty("publication").GetProperty("journal").GetProperty("checkpoint");
        Assert.Equal("UNKNOWN",checkpoint.GetProperty("runtimeReadiness").GetString());Assert.Equal(JsonValueKind.Null,checkpoint.GetProperty("realizedPnl").ValueKind);
        Assert.Equal(JsonValueKind.Null,checkpoint.GetProperty("brokerAccountId").ValueKind);Assert.Equal("NOT_CONFIGURED",checkpoint.GetProperty("brokerState").GetString());
        var p=f.Read();Assert.Throws<NotSupportedException>(()=>((IList<NavalReadBatch>)p.Journal.Batches).Clear());
        Assert.Throws<NotSupportedException>(()=>((IList<NavalReadFill>)p.Journal.Checkpoint.Fleets[0].Fills).Clear());
        Assert.Equal(3,f.Read().Journal.Checkpoint.Fleets.SelectMany(x=>x.Fills).Count());
    }
    [Fact] public void NavalSharedPublication_ExactStringsDecimalsCurrenciesAndOpaqueIdsRoundTrip()
    {
        using var f=new Files();var input=JsonNode.Parse(File.ReadAllText(Fixture).Replace("cash-A","9007199254740993-opaque-ą"))!;
        input["fees"]!["entryInBase"]=true;var steps=input["steps"]!.AsArray();var reserve=steps[2]!.DeepClone();
        input["steps"]=new JsonArray(steps[0]!.DeepClone(),steps[1]!.DeepClone(),reserve,JsonNode.Parse("""
        {"op":"confirm","fills":[{"referenceCommand":"alpha-btc","tradeId":"9007199254740995-exact-ą","quantity":1.234,"price":99.99,"fee":0.001234,"feeCurrency":"BTC","tradeUtc":"2026-01-01T12:00:00Z"}]}
        """));File.WriteAllText(f.Input,input.ToJsonString());Assert.Equal(0,f.Run(f.Input));var p=f.Read();
        var fill=Assert.Single(p.Journal.Checkpoint.Fleets.SelectMany(x=>x.Fills));Assert.Equal(1.234m,fill.Fill.Quantity);Assert.Equal(.001234m,fill.Fill.Fee);
        Assert.Equal("BTC",fill.Fill.FeeCurrency);Assert.Equal("USD",fill.QuoteCurrency);Assert.Equal("9007199254740993-opaque-ą",fill.Fill.Account.AccountId);
        Assert.Equal("9007199254740995-exact-ą",fill.Fill.TradeId);Assert.Equal(9876.61234m,p.Journal.Checkpoint.Accounts.Single().Cash.Amount);
        string serialized=JsonSerializer.Serialize(new{revision=9007199254740993L,price=123.00000000000000000000000001m},ExportJson);
        using var d=JsonDocument.Parse(serialized);Assert.Equal("9007199254740993",d.RootElement.GetProperty("revision").GetString());Assert.Equal("123.00000000000000000000000001",d.RootElement.GetProperty("price").GetString());
        long roundtrip=JsonSerializer.Deserialize<long>("\"9007199254740993\"",ExportJson);Assert.Equal(9007199254740993,roundtrip);
        Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<long>("9007199254740993",ExportJson));
        Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<decimal>("\"NaN\"",ExportJson));
    }
    [Theory] [InlineData("duplicateWithinBatch")] [InlineData("duplicateAcrossBatches")]
    [InlineData("reservationTerminal")] [InlineData("reservationUnsettled")] [InlineData("coverageUnsettled")]
    [InlineData("contradictoryFinality")] [InlineData("omittedFinality")]
    public void NavalSharedPublication_RehashedInvalidFactsRefuseExportWithoutAuthorityMutation(string fault)
    {
        AssertRehashedExportRefusal(n=>
        {
            var batches=n["NavalReadJournal"]!["Batches"]!.AsArray();
            if(fault.StartsWith("duplicate",StringComparison.Ordinal))
            {
                var source=batches.First(b=>b!["Facts"]!.AsArray().Any(x=>x!["Fill"]!=null))!;
                var target=fault=="duplicateAcrossBatches"?batches.Last()!:source;var facts=target["Facts"]!.AsArray();
                var duplicate=source["Facts"]!.AsArray().First(x=>x!["Fill"]!=null)!.DeepClone();
                duplicate["FactId"]=target["EventId"]!.GetValue<string>()+":"+facts.Count;facts.Add(duplicate);
            }
            else
            {
                var fact=batches.SelectMany(b=>b!["Facts"]!.AsArray()).Single(x=>x!["Kind"]!.GetValue<int>()==(int)NavalReadFactKind.OfflineReductionCoverageAccepted)!;
                if(fault is "reservationTerminal" or "contradictoryFinality")fact["Reservation"]!["Reservation"]!["TerminalCoverage"]=false;
                if(fault is "reservationUnsettled" or "contradictoryFinality")fact["Reservation"]!["Reservation"]!["ReductionUnsettled"]=true;
                if(fault=="coverageUnsettled"){fact["Coverage"]!["ReductionUnsettled"]=true;fact["Coverage"]!["OfflineLifecycleFinal"]=false;}
                if(fault=="omittedFinality")fact["Coverage"]!["OfflineLifecycleFinal"]=false;
            }
        });
    }
    [Theory]
    [InlineData(NavalReadFactKind.OfflineReservationAccepted,"DurableMode")]
    [InlineData(NavalReadFactKind.OfflineReservationAccepted,"ResultCode")]
    [InlineData(NavalReadFactKind.OfflineReservationRefused,"DurableMode")]
    [InlineData(NavalReadFactKind.OfflineReservationRefused,"ResultCode")]
    [InlineData(NavalReadFactKind.OfflineReconciliationAccepted,"DurableMode")]
    [InlineData(NavalReadFactKind.OfflineReconciliationAccepted,"ResultCode")]
    [InlineData(NavalReadFactKind.OfflineSafetyChanged,"DurableMode")]
    [InlineData(NavalReadFactKind.OfflineSafetyChanged,"ResultCode")]
    [InlineData(NavalReadFactKind.OfflineModeChanged,"DurableMode")]
    [InlineData(NavalReadFactKind.OfflineModeChanged,"ResultCode")]
    public void NavalSharedPublication_RehashedUnsafeOperationRefusesExportWithoutAuthorityMutation(NavalReadFactKind kind,string field)
    {
        AssertRehashedExportRefusal(n=>
        {
            var operation=n["NavalReadJournal"]!["Batches"]!.AsArray().SelectMany(b=>b!["Facts"]!.AsArray())
                .First(x=>x!["Kind"]!.GetValue<int>()==(int)kind)!["Operation"]!;
            operation[field]=field=="DurableMode"?"READY":"SYNTHETIC_PRIVATE_DIAGNOSTIC_SENTINEL";
        });
    }
    [Fact] public void NavalSharedPublication_SanitizedFallbackCodesSurviveReopenAndExport()
    {
        using var f=new Files();Assert.Equal(0,f.Run());var n=JsonNode.Parse(File.ReadAllText(f.Authority))!;
        // CLI timeout produces UNCERTAIN; OFFLINE_STOP is the other intentional Core fallback.
        var j=n["NavalReadJournal"]!;
        j["Batches"]!.AsArray().SelectMany(b=>b!["Facts"]!.AsArray()).First(x=>x!["Operation"]!=null)!["Operation"]!["ResultCode"]="OFFLINE_STOP";
        RehashJournal(j);File.WriteAllText(f.Authority,n.ToJsonString());f.Empty();Assert.Equal(0,f.Run(f.Input));
        var operations=f.Read().Journal.Batches.SelectMany(b=>b.Facts).Where(x=>x.Operation!=null).Select(x=>x.Operation!).ToArray();
        Assert.Contains(operations,x=>x.ResultCode=="UNCERTAIN"&&x.DurableMode=="STOP");
        Assert.Contains(operations,x=>x.ResultCode=="OFFLINE_STOP"&&x.DurableMode=="STOP");
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_DIAGNOSTIC_SENTINEL",File.ReadAllText(f.Publication));
        var bytes=File.ReadAllBytes(f.Authority);f.Empty();Assert.Equal(0,f.Run(f.Input));Assert.Equal(bytes,File.ReadAllBytes(f.Authority));
    }
    // Control and invalid copy both go through actual Program.Main, including owner reopen.
    // Rehash only typed journal metadata; never alter financial state, seal, marker or revision.
    private static void AssertRehashedExportRefusal(Action<JsonNode> mutate)
    {
        using var f=new Files();Assert.Equal(0,f.Run());f.Empty();
        byte[] control=File.ReadAllBytes(f.Authority);Assert.Equal(0,f.Run(f.Input));Assert.Equal(control,File.ReadAllBytes(f.Authority));
        var n=JsonNode.Parse(control)!;string financial=n["Naval"]!.ToJsonString();string revision=n["DurableRevision"]!.ToJsonString();
        mutate(n);Assert.Equal(financial,n["Naval"]!.ToJsonString());Assert.Equal(revision,n["DurableRevision"]!.ToJsonString());
        RehashJournal(n["NavalReadJournal"]!);
        File.WriteAllText(f.Authority,n.ToJsonString());byte[] invalid=File.ReadAllBytes(f.Authority);
        byte[] marker=File.ReadAllBytes(Path.Combine(f.State,".naval-initialized"));
        File.Delete(f.Publication);File.Delete(f.Result);
        Assert.Equal(2,f.Run(f.Input));Assert.Equal(invalid,File.ReadAllBytes(f.Authority));
        Assert.Equal(marker,File.ReadAllBytes(Path.Combine(f.State,".naval-initialized")));
        Assert.False(File.Exists(f.Publication));Assert.False(File.Exists(f.Result));
    }
    private static void RehashJournal(JsonNode journal)
    {
        journal["IntegrityDigest"]="";var typed=JsonSerializer.Deserialize<NavalReadJournalState>(journal.ToJsonString())!;
        journal["IntegrityDigest"]=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(typed)));
    }
    static IEnumerable<string> Properties(JsonElement node)
    {
        if(node.ValueKind==JsonValueKind.Object)foreach(var property in node.EnumerateObject()){yield return property.Name;foreach(var child in Properties(property.Value))yield return child;}
        if(node.ValueKind==JsonValueKind.Array)foreach(var item in node.EnumerateArray())foreach(var child in Properties(item))yield return child;
    }
    // Inspect the exporter codec without granting this test assembly Core or tool friend authority.
    static JsonSerializerOptions ExportJson=>(JsonSerializerOptions)typeof(Program).Assembly.GetType("Apex.Backtester.Naval.SharedPublicationExport")!
        .GetField("Json",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
    private sealed record PublicationProvenance(string InputSha256,string CoreSha256,string BacktesterSha256);
    private sealed record PublicationEnvelope(string Schema,string TemporalClassification,DateTimeOffset SourceClockUtc,string SourceFreshness,
        PublicationProvenance Provenance,NavalReadPublication Publication,string ContentSha256);
    static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private sealed class Files:IDisposable
    {
        internal readonly string Dir=Path.Combine(Environment.GetEnvironmentVariable("TMPDIR")!,"publication-"+Guid.NewGuid().ToString("N"));
        internal string State=>Path.Combine(Dir,"state");internal string Authority=>Path.Combine(State,"naval-authority-v1.json");
        internal string Publication=>Path.Combine(Dir,"publication.json");internal string Result=>Path.Combine(Dir,"result.json");internal string Input=>Path.Combine(Dir,"input.json");
        internal Files(){Directory.CreateDirectory(Dir);}
        internal void Empty(){var n=JsonNode.Parse(File.ReadAllText(Fixture))!;n["steps"]=new JsonArray();File.WriteAllText(Input,n.ToJsonString());}
        internal int Run(string? input=null,string? publication=null)=>Program.Main(new[]{"naval-shared-replay","--input",input??Fixture,"--state-dir",State,"--output",Result,"--publication",publication??Publication});
        internal NavalReadPublication Read()=>JsonSerializer.Deserialize<PublicationEnvelope>(File.ReadAllBytes(Publication),ExportJson)!.Publication;
        public void Dispose(){Directory.Delete(Dir,true);}
    }
}
