using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Apex.Core.Engine;
using System.Text.Json;
using Apex.Core.Naval;
using Xunit;
namespace Apex.Tests;
public class NavalReadJournalTests
{
    // Missing append must fail after the real coordinator has durably reconciled and started.
    [Fact] public void NavalReadJournal_CommitPersistsCheckpointAndBatchTogether()
    {
        using var f=new NavalCashFixture();f.Run();
        using var stored=JsonDocument.Parse(f.Store.Value!);
        Assert.True(stored.RootElement.TryGetProperty("NavalReadJournal",out var journal),"Accepted owner commit must contain its publication checkpoint and batches");
        Assert.Equal(2,journal.GetProperty("LastSequence").GetInt64());
        Assert.Equal(2,journal.GetProperty("Checkpoint").GetProperty("FinancialRevision").GetInt64());
        Assert.Equal(2,journal.GetProperty("Batches").GetArrayLength());
        var p=f.C.ReadPublication()!;Assert.Equal(2,p.AsOfSequence);Assert.Equal(2,p.FinancialRevision);
        Assert.Equal(10000m,p.Journal.Checkpoint.Accounts.Single().Cash.Amount);
    }
    [Fact] public void NavalReadJournal_PartialThenReductionFinalityRetainsHoldUntilCoverage()
    {
        using var f=new NavalCashFixture();f.Run();var r=f.Reserve();
        f.C.Confirm(f.Receipt(NavalEvidenceKind.Confirm,fills:new[]{f.Fill(r,2,100,.2m)}));
        Assert.Single(f.C.ReadPublication()!.Journal.Batches.Last().Facts,x=>x.Kind==NavalReadFactKind.OfflineFillAccepted);
        Assert.DoesNotContain(f.C.ReadPublication()!.Journal.Batches.SelectMany(b=>b.Facts),x=>x.Coverage?.OfflineLifecycleFinal==true);
        f.C.Terminal(f.Receipt(NavalEvidenceKind.Terminal,true,true,terminal:new[]{r.ReservationId!}));
        Assert.DoesNotContain(f.C.ReadPublication()!.Journal.Batches.Last().Facts,x=>x.Coverage?.OfflineLifecycleFinal==true);
        f.C.Confirm(f.Receipt(NavalEvidenceKind.Confirm,fills:new[]{f.Fill(r,2,110,.22m,true,"sell")}));
        Assert.Equal(10019.580m,f.View.Cash);Assert.Equal(.260m,f.View.Held);
        var p=f.C.ReadPublication()!;var stored=p.Journal.Checkpoint.Fleets.SelectMany(x=>x.Reservations).Single().Reservation;
        Assert.True(stored.ReductionUnsettled);Assert.Equal(0,stored.Inventory);
        Assert.DoesNotContain(p.Journal.Batches.Last().Facts,x=>x.Coverage?.OfflineLifecycleFinal==true);
        f.C.Terminal(f.Receipt(NavalEvidenceKind.Terminal,true,true,terminal:new[]{r.ReservationId!}));
        Assert.Equal(0,f.View.Held);Assert.Equal(10019.580m,f.View.Cash);
        Assert.Contains(f.C.ReadPublication()!.Journal.Batches.Last().Facts,x=>x.Kind==NavalReadFactKind.OfflineReductionCoverageAccepted&&x.Coverage!.OfflineLifecycleFinal);
    }
    [Fact] public void NavalReadJournal_DuplicateDoesNotPostOrAppend()
    {
        using var f=new NavalCashFixture();f.Run();var intent=f.Intent();var r=f.C.ForFleet(f.A[0],NavalFleet.Alpha).TryReserve(intent);
        var receipt=f.Receipt(NavalEvidenceKind.Confirm,fills:new[]{f.Fill(r,2,100,.2m)});
        f.C.Confirm(receipt);var before=f.Store.Value;var p=f.C.ReadPublication()!;
        Assert.Equal(NavalMutationStatus.Duplicate,f.C.Confirm(receipt).Status);
        Assert.Equal(NavalMutationStatus.Duplicate,f.C.ForFleet(f.A[0],NavalFleet.Alpha).TryReserve(intent).Status);Assert.Equal(before,f.Store.Value);
        f.C.Confirm(f.Receipt(NavalEvidenceKind.Confirm,fills:receipt.Fills));
        Assert.Equal(9799.8m,f.View.Cash);Assert.Equal(p.AsOfSequence+1,f.C.ReadPublication()!.AsOfSequence);
        Assert.DoesNotContain(f.C.ReadPublication()!.Journal.Batches.Last().Facts,x=>x.Kind==NavalReadFactKind.OfflineFillAccepted);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void NavalReadJournal_ConflictingOrInvalidBatchNeverEmitsAcceptedFill(bool conflict)
    {
        using var f=new NavalCashFixture();f.Run();var r=f.Reserve();var buy=f.Fill(r,1,100,.1m);
        if(conflict)f.C.Confirm(f.Receipt(NavalEvidenceKind.Confirm,fills:new[]{buy}));
        decimal cash=f.View.Cash;
        var invalid=conflict?buy with{Price=101}:buy with{Quantity=20,TradeId="bad"};
        var result=f.C.Confirm(f.Receipt(NavalEvidenceKind.Confirm,fills:new[]{buy with{TradeId="would-be-valid"},invalid}));
        Assert.Equal(NavalMutationStatus.Stopped,result.Status);Assert.Equal(cash,f.View.Cash);
        Assert.DoesNotContain(f.C.ReadPublication()!.Journal.Batches.Last().Facts,x=>x.Kind==NavalReadFactKind.OfflineFillAccepted);
    }
    [Theory] [InlineData("before",false)] [InlineData("after",true)] [InlineData("flush",true)]
    public void NavalReadJournal_AmbiguousSetAndFlushReopenSameEventIds(string fault,bool accepted)
    {
        using var f=new NavalCashFixture();f.Owner.Dispose();
        string directory=Path.Combine(f.DirectoryPath,"durable");Directory.CreateDirectory(directory);var disk=new PublicationDiskStore(directory);
        f.Owner=new(directory,disk,f.D);f.C=new(f.Owner,f.D,f.Clock);f.Run();var r=f.Reserve();
        var p=f.C.ReadPublication()!;disk.Fault=fault;
        Assert.Equal(NavalMutationStatus.Stopped,f.C.Confirm(f.Receipt(NavalEvidenceKind.Confirm,fills:new[]{f.Fill(r,2,100,.2m)})).Status);
        Assert.Equal(NavalJson.Serialize(p),NavalJson.Serialize(f.C.ReadPublication())); // no optimistic publication
        var durable=JsonSerializer.Deserialize<EngineState>(disk.Get(NavalCoreStateOwner.StateKey)!)!;var ids=durable.NavalReadJournal!.Batches.Select(b=>b.EventId).ToArray();
        disk.Fault=null;f.Owner.Dispose();f.Owner=new(directory,new PublicationDiskStore(directory),f.D);f.C=new(f.Owner,f.D,f.Clock);var reopened=f.C.ReadPublication()!;
        Assert.Equal(accepted?9799.8m:10000m,f.View.Cash);Assert.Equal(1,f.View.ActiveCount);Assert.Equal(accepted?200.720m:400.920m,f.View.Held);
        var obligation=Assert.Single(f.View.Reservations);Assert.Equal(accepted?2m:4m,obligation.Pending);Assert.Equal(accepted?2m:0m,obligation.Inventory);
        Assert.False(obligation.TerminalCoverage);Assert.False(obligation.ReductionUnsettled);
        Assert.Equal(.520m,obligation.CloseFeeHold);
        Assert.Equal(p.AsOfSequence+(accepted?1:0),reopened.AsOfSequence);Assert.Equal(ids,reopened.Journal.Batches.Select(b=>b.EventId));
        Assert.Equal(accepted?1:0,f.Owner.Read().Naval!.Fills.Count);
    }
    [Fact] public void NavalReadJournal_RestartPreservesEpochButCannotClaimRuntimeReady()
    {
        using var f=new NavalCashFixture();f.Run();var before=f.C.ReadPublication()!;f.Restart();
        var p=f.C.ReadPublication()!;Assert.Equal(before.Journal.Epoch,p.Journal.Epoch);Assert.Equal(before.AsOfSequence,p.AsOfSequence);
        Assert.Equal("RUNNING",p.Journal.Checkpoint.DurableMode);Assert.Equal("UNKNOWN",p.Journal.Checkpoint.RuntimeReadiness);Assert.False(f.View.Ready);
        Assert.Null(p.Journal.Checkpoint.BrokerEquity);Assert.Null(p.Journal.Checkpoint.BrokerAccountId);Assert.Equal("NOT_CONFIGURED",p.Journal.Checkpoint.BrokerState);
        f.Reconcile();Assert.Equal(2,f.C.ReadPublication()!.Journal.Checkpoint.SourceGeneration);
    }
    [Fact] public void NavalReadJournal_LegacyBaselineHasNoHistoricLifecycle()
    {
        using var f=new NavalCashFixture();f.Run();var r=f.Reserve();f.C.Confirm(f.Receipt(NavalEvidenceKind.Confirm,fills:new[]{f.Fill(r,2,100,.2m)}));
        long baseline=f.View.DurableRevision;f.Owner.Dispose();var n=JsonNode.Parse(f.Store.Value!)!;n.AsObject().Remove("NavalReadJournal");f.Store.Value=n.ToJsonString();
        f.Owner=new(f.DirectoryPath,f.Store,f.D);f.C=new(f.Owner,f.D,f.Clock);var unchanged=f.Store.Value;Assert.Null(f.C.ReadPublication());Assert.Equal(unchanged,f.Store.Value);
        f.C.Stop("LEGACY_STOP");var p=f.C.ReadPublication()!;Assert.Equal(baseline,p.Journal.BaselineRevision);Assert.Equal(1,p.AsOfSequence);
        Assert.Single(p.Journal.Checkpoint.Fleets.SelectMany(x=>x.Fills));Assert.DoesNotContain(p.Journal.Batches.SelectMany(x=>x.Facts),x=>x.Kind==NavalReadFactKind.OfflineFillAccepted);
    }
    [Fact] public void NavalReadJournal_MissingFinalityFieldStillRefuses()
    {
        using var f=new NavalCashFixture();f.Run();f.Reserve();f.Owner.Dispose();var n=JsonNode.Parse(f.Store.Value!)!;
        n.AsObject().Remove("NavalReadJournal");n["Naval"]!["Reservations"]![0]!.AsObject().Remove("ReductionUnsettled");f.Store.Value=n.ToJsonString();
        Assert.ThrowsAny<Exception>(()=>new NavalCoreStateOwner(f.DirectoryPath,f.Store,f.D));
    }
    [Theory] [InlineData("Epoch")] [InlineData("LastSequence")] [InlineData("Checkpoint")] [InlineData("IntegrityDigest")] [InlineData("missing")]
    public void NavalReadJournal_CorruptMetadataNeverReseeds(string field)
    {
        using var f=new NavalCashFixture();f.Run();f.Owner.Dispose();var n=JsonNode.Parse(f.Store.Value!)!;
        if(field=="missing")n["NavalReadJournal"]!.AsObject().Remove("BaselineRevision");else n["NavalReadJournal"]![field]=null;
        f.Store.Value=n.ToJsonString();var bytes=f.Store.Value;Assert.ThrowsAny<Exception>(()=>new NavalCoreStateOwner(f.DirectoryPath,f.Store,f.D));Assert.Equal(bytes,f.Store.Value);
    }
    [Fact] public void NavalReadJournal_NullCfdRoundTripUnchanged()
    {
        var e=JsonSerializer.Deserialize<EngineState>("{\"AccountNumber\":123,\"RealizedTotal\":42}")!;Assert.Null(e.NavalReadJournal);
        Assert.Null(NavalJson.Clone(e).NavalReadJournal);Assert.Equal(42,NavalJson.Clone(e).RealizedTotal);
    }
    [Fact] public void NavalReadJournal_CollectionsAreDetached()
    {
        using var f=new NavalCashFixture();f.Run();var p=f.C.ReadPublication()!;
        Assert.Throws<NotSupportedException>(()=>((IList<NavalReadBatch>)p.Journal.Batches).Clear());
        Assert.Throws<NotSupportedException>(()=>((IList<NavalReadAccount>)p.Journal.Checkpoint.Accounts).Clear());
        Assert.Throws<NotSupportedException>(()=>((IList<NavalReadFact>)p.Journal.Batches[0].Facts).Clear());
        var old=NavalJson.Serialize(p);f.Reserve();Assert.Equal(old,NavalJson.Serialize(p));
        var ownerCopy=f.Owner.Read();ownerCopy.NavalReadJournal=null;Assert.NotNull(f.C.ReadPublication());
    }
    [Fact] public void NavalReadJournal_RetentionPagesDoNotSkip()
    {
        using var f=new NavalCashFixture();f.Owner.Dispose();string directory=Path.Combine(f.DirectoryPath,"retained");Directory.CreateDirectory(directory);
        f.Owner=new(directory,new PublicationDiskStore(directory),f.D);f.C=new(f.Owner,f.D,f.Clock);
        f.Run();var r=f.Reserve();f.C.Confirm(f.Receipt(NavalEvidenceKind.Confirm,fills:new[]{f.Fill(r,1,100,.1m)}));
        for(int i=0;i<515;i++)Assert.Equal(NavalMutationStatus.Stopped,f.C.Stop("RETENTION_TEST").Status);
        var p=f.C.ReadPublication()!;var j=p.Journal;Assert.Equal(519,p.AsOfSequence);Assert.Equal(512,j.Batches.Count);Assert.Equal(8,j.OldestAvailableSequence);
        Assert.Single(f.Owner.Read().Naval!.Fills);Assert.Single(f.Owner.Read().Naval!.Commands);Assert.Equal(2,f.Owner.Read().Naval!.Receipts.Count);Assert.Equal(9899.9m,f.View.Cash);
        var cursor=new NavalCursor(j.Epoch,j.ScopeId,7);var seen=new List<long>();
        do{var page=NavalReadJournal.ReadPage(p,cursor,100);Assert.False(page.Gap);seen.AddRange(page.Batches.Select(x=>x.Sequence));cursor=page.NextCursor;if(!page.HasMore)break;}while(true);
        Assert.Equal(Enumerable.Range(8,512).Select(i=>(long)i),seen);Assert.Equal(519,cursor.Sequence);
        Assert.Equal(cursor,NavalCursor.Decode(cursor.Encode()));Assert.Empty(NavalReadJournal.ReadPage(p,cursor).Batches);
        foreach(var c in new[]{cursor with{Sequence=0},cursor with{Epoch=Guid.NewGuid().ToString("N")},cursor with{ScopeId=new string('a',64)},cursor with{Sequence=520}})
        {var page=NavalReadJournal.ReadPage(p,c);Assert.True(page.ResetRequired);Assert.True(page.Gap);Assert.Empty(page.Batches);}
        Assert.Throws<ArgumentException>(()=>NavalReadJournal.ReadPage(p,cursor,101));Assert.Throws<ArgumentException>(()=>NavalReadJournal.ReadPage(p,cursor,-1));
        Assert.Throws<ArgumentException>(()=>NavalCursor.Decode(new string('a',2049)));Assert.Throws<ArgumentException>(()=>NavalCursor.Decode("e30"));
        var big=new NavalCursor(j.Epoch,j.ScopeId,9007199254740993);Assert.Equal(big,NavalCursor.Decode(big.Encode()));
        f.Owner.Dispose();f.Owner=new(directory,new PublicationDiskStore(directory),f.D);f.C=new(f.Owner,f.D,f.Clock);
        Assert.Equal(j.Epoch,f.C.ReadPublication()!.Journal.Epoch);Assert.Equal(519,f.C.ReadPublication()!.AsOfSequence);Assert.Single(f.Owner.Read().Naval!.Fills);
    }
    [Fact] public void NavalReadJournal_RefusedIntentIsTypedAndDetached()
    {
        using var f=new NavalCashFixture();var intent=f.Intent();
        Assert.Equal(NavalMutationStatus.Refused,f.C.ForFleet(f.A[0],NavalFleet.Alpha).TryReserve(intent).Status);
        var fact=Assert.Single(f.C.ReadPublication()!.Journal.Batches.Single().Facts);
        Assert.Equal(NavalReadFactKind.OfflineReservationRefused,fact.Kind);Assert.Equal(intent,fact.Intent);Assert.Null(fact.Fill);
    }
    [Theory] [InlineData("CurrentQuality","FRESH")] [InlineData("RuntimeReadiness","READY")] [InlineData("BrokerAccountId","private-account")]
    public void NavalReadJournal_PresentUnknownSemanticsCannotBeSilentlyNormalized(string field,string value)
    {
        using var f=new NavalCashFixture();f.Run();f.Owner.Dispose();var n=JsonNode.Parse(f.Store.Value!)!;
        n["NavalReadJournal"]!["Checkpoint"]![field]=value;f.Store.Value=n.ToJsonString();
        Assert.ThrowsAny<Exception>(()=>new NavalCoreStateOwner(f.DirectoryPath,f.Store,f.D));
    }
    [Fact] public void NavalReadJournal_ConflictingIdentityAndMissingMiddleRequireReset()
    {
        using var f=new NavalCashFixture();f.Run();f.C.Stop("STOP");var p=f.C.ReadPublication()!;
        var j=p.Journal;var original=NavalJson.Serialize(p);
        var badBatch=new NavalReadBatch(2,j.Batches[0].EventId,2,null,null,f.Clock.Now,1,1,j.Batches[1].Facts);
        foreach(var batches in new[]{new[]{j.Batches[0],j.Batches[2]},new[]{j.Batches[0],badBatch,j.Batches[2]}})
        {
            var bad=new NavalReadJournalState(j.Schema,j.Epoch,j.ScopeId,j.BaselineRevision,j.LastSequence,j.OldestAvailableSequence,j.Checkpoint,batches,j.IntegrityDigest);
            var page=NavalReadJournal.ReadPage(p with{Journal=bad},new(j.Epoch,j.ScopeId,0));Assert.True(page.ResetRequired);Assert.Empty(page.Batches);
        }
        Assert.Equal(original,NavalJson.Serialize(p));
    }
    [Theory] [InlineData("generation")] [InlineData("gap")] [InlineData("future")] [InlineData("regress")]
    public void NavalReadJournal_OldGenerationGapOrTimeNeverPublishesEconomics(string fault)
    {
        using var f=new NavalCashFixture();f.Run();var r=f.Reserve();var before=f.C.ReadPublication()!;
        var receipt=new NavalEvidenceReceipt(f.C.OwnerToken,f.D.Id,"out-of-order",fault=="generation"?0:1,
            fault=="gap"?4:2,fault=="future"?f.Clock.Now.AddSeconds(1):fault=="regress"?f.Clock.Now.AddSeconds(-1):f.Clock.Now,
            f.Clock.Now,NavalEvidenceKind.Confirm,false,false,f.Marks,new[]{f.Fill(r,1,100,.1m)});
        Assert.Equal(NavalMutationStatus.Stopped,f.C.Confirm(receipt).Status);var p=f.C.ReadPublication()!;
        Assert.Equal(before.AsOfSequence+1,p.AsOfSequence);Assert.Equal(before.Journal.Checkpoint.SourceGeneration,p.Journal.Checkpoint.SourceGeneration);
        Assert.Equal(10000m,f.View.Cash);Assert.DoesNotContain(p.Journal.Batches.Last().Facts,x=>x.Kind==NavalReadFactKind.OfflineFillAccepted);
    }
    [Theory] [InlineData("identity")] [InlineData("source")] [InlineData("checkpoint")] [InlineData("sequence")]
    [InlineData("factScope")] [InlineData("factIntent")]
    public void NavalReadJournal_InvalidMetadataWithValidChecksumStillRefuses(string fault)
    {
        using var f=new NavalCashFixture();f.Run();var r=f.Reserve();f.C.Confirm(f.Receipt(NavalEvidenceKind.Confirm,fills:new[]{f.Fill(r,1,100,.1m)}));
        f.Owner.Dispose();var n=JsonNode.Parse(f.Store.Value!)!;var j=n["NavalReadJournal"]!;
        if(fault=="identity")j["Batches"]![1]!["EventId"]=j["Batches"]![0]!["EventId"]!.GetValue<string>();
        if(fault=="source")j["Batches"]![1]!["SourceGeneration"]=0;
        if(fault=="checkpoint")j["Checkpoint"]!["FinancialRevision"]=999;
        if(fault=="sequence")j["LastSequence"]=99;
        if(fault=="factScope")j["Batches"]![3]!["Facts"]![0]!["Account"]!["AccountId"]="foreign";
        if(fault=="factIntent")j["Batches"]![2]!["Facts"]![0]!["Intent"]!["CommandId"]="other";
        j["IntegrityDigest"]="";var blank=JsonSerializer.Deserialize<NavalReadJournalState>(j.ToJsonString())!;
        j["IntegrityDigest"]=NavalJson.Hash(blank);f.Store.Value=n.ToJsonString();var bytes=f.Store.Value;
        Assert.ThrowsAny<Exception>(()=>new NavalCoreStateOwner(f.DirectoryPath,f.Store,f.D));Assert.Equal(bytes,f.Store.Value);
    }
    [Fact] public void NavalReadJournal_DuplicateMetadataFieldCannotBypassOptionalValidation()
    {
        using var f=new NavalCashFixture();f.Run();f.Owner.Dispose();f.Store.Value=f.Store.Value!.Replace("\"NavalReadJournal\":","\"NavalReadJournal\":null,\"NavalReadJournal\":");
        Assert.ThrowsAny<Exception>(()=>new NavalCoreStateOwner(f.DirectoryPath,f.Store,f.D));
    }
    private sealed class PublicationDiskStore:IKeyValueStore
    {
        private readonly string _path;internal string? Fault;
        internal PublicationDiskStore(string directory){_path=Path.Combine(directory,"authority.json");}
        public string? Get(string key)=>File.Exists(_path)?File.ReadAllText(_path):null;
        public void Set(string key,string value)
        {
            if(Fault=="before")throw new IOException("before Set");
            using(var stream=new FileStream(_path+".candidate",FileMode.Create,FileAccess.Write,FileShare.None))
            {var bytes=System.Text.Encoding.UTF8.GetBytes(value);stream.Write(bytes);stream.Flush(true);}
            File.Move(_path+".candidate",_path,true);if(Fault=="after")throw new IOException("after Set");
        }
        public void Flush(){if(Fault=="flush")throw new IOException("Flush");}
    }
}
