using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Apex.Core.Bridge;
using Apex.Core.Model;
using Apex.Platforms.CTrader;
using Xunit;

namespace Apex.Platform.Tests;

public sealed class JournalLifecycleTests
{
    static ResultMsg Filled(string id) => new() { Id = id, Status = "filled", PositionId = 42, Price = 4000, Message = new string('d', 400) };
    static Dictionary<string, CommandJournal.Entry> Terminals(int count) => Enumerable.Range(0, count).ToDictionary(i => "old-" + i,
        i => new CommandJournal.Entry { Fingerprint = "close " + i, Signal = "signal-" + i, Result = Filled("old-" + i) });
    static string Encode(Dictionary<string, CommandJournal.Entry> entries) => JsonSerializer.Serialize(entries, BridgeProtocol.Json);

    [Fact]
    public void Task2B_CompactionRetainsActualOpeningLookupContractAndPendingCheckpoint()
    {
        var entries = new Dictionary<string, CommandJournal.Entry>();
        var open = BridgeProtocol.Deserialize<ResultMsg>(OpeningEvidenceTests.Wire)!;
        string fp = BridgeProtocol.Serialize(new OpenCmd { SignalId = "signal", Symbol = "US500", Units = 2, SlPips = 10, TpPips = 20,
            Label = "fresh-scope", Comment = "signal", MaxSlipPips = 1 });
        entries.Add("original-command", new() { Fingerprint = fp, Signal = "original-signal", Result = open });
        var pending = BridgeProtocol.Deserialize<ResultMsg>(OpeningEvidenceTests.Wire)!; pending.Id = "pending"; pending.Status = "uncertain";
        entries.Add("pending", new() { Fingerprint = fp, Signal = "pending-signal", Result = pending });
        entries.Add("missing-fill", new() { Fingerprint = fp, Result = Filled("missing-fill") });
        foreach (var entry in Terminals(20001)) entries.Add(entry.Key, entry.Value);
        string saved = Encode(entries);
        var journal = new CommandJournal(saved, s => saved = s);
        journal.Run("next", "close next", null, () => Filled("next"));
        Assert.InRange(journal.CachedCount, 1, 257);
        Assert.Equal(20005, journal.Count);
        var restored = new CommandJournal(saved, _ => throw new Exception("lookup/replay cannot write"));
        Assert.Equal(1, restored.Lookup("original-command")!.OpeningFill!.InitialVolumeUnits);
        Assert.Equal(2, restored.LookupOpeningRequest("original-command")!.VolumeUnits);
        Assert.Equal("original-command", restored.LookupOpeningRequest("original-command")!.ExecutionId);
        var recovered = CTraderScopeEvidence.RecoverOpening(restored.Lookup("missing-fill")!, restored.LookupOpeningRequest("missing-fill")!, open.OpeningFill!.OpeningDeals, 0.1);
        Assert.NotNull(recovered.OpeningFill); Assert.Equal(1, recovered.OpeningFill!.InitialVolumeUnits);
        Assert.Equal("missing-fill", recovered.OpeningFill.Request.ExecutionId);
        Assert.Equal("uncertain", restored.Run("pending", fp, "pending-signal", () => throw new Exception("resend")).Status);
        Assert.Equal(1, restored.Lookup("pending")!.OpeningFill!.InitialVolumeUnits);
        Assert.Equal("filled", restored.Run("new-command", fp, "original-signal", () => throw new Exception("duplicate open")).Status);
    }

    [Theory]
    [InlineData("corrupt")][InlineData("disk")][InlineData("full")]
    public void Task2B_ActualNativeModifyCannotReachSdkWhenJournalCannotReserve(string fault)
    {
        string? saved = fault == "corrupt" ? "broken" : fault == "full" ? Encode(Terminals(CommandJournal.ReplayCapacity)) : null;
        // Load the actual built native SDK assembly, whose Core is embedded. Do not widen
        // Core's simulation friend boundary just to compile a duplicate native type in tests.
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string dll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../ApexTradingAI/ApexTradingAI/bin", configuration, "net6.0/ApexTradingAI.dll"));
        var assembly = System.Reflection.Assembly.LoadFrom(dll);
        var journalType = assembly.GetType("Apex.Platforms.CTrader.CommandJournal")!;
        var journal = Activator.CreateInstance(journalType, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
            new object?[] { saved, (Action<string>)(_ => throw new IOException("disk")) }, null)!;
        var adapterType = assembly.GetType("cAlgo.Robots.CTraderAdapter")!;
        var adapter = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(adapterType);
        adapterType.GetField("_journal", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(adapter, journal);
        // No SDK/Robot is created. Any _r access is a failure: journal refusal must precede it.
        var result = adapterType.GetMethod("ModifyStopLoss")!.Invoke(adapter, new object[] { 42L, 3991.0 })!;
        Assert.Equal("Rejected", result.GetType().GetProperty("Status")!.GetValue(result)!.ToString());
        Assert.Equal("TechnicalError", result.GetType().GetProperty("Error")!.GetValue(result)!.ToString());
    }

    [Fact]
    public void Task2B_VersionedSnapshotDetectsWellFormedPayloadCorruption()
    {
        string saved = ""; var journal = new CommandJournal(null, s => saved = s);
        journal.Run("old", "fingerprint", "signal", () => Filled("old"));
        string corrupt = saved.Replace(":\"fingerprint\"", ":\"altered-payload\"");
        var restored = new CommandJournal(corrupt, _ => throw new Exception("cannot overwrite corrupt bytes"));
        Assert.False(restored.Healthy);
        Assert.Equal("rejected", restored.Run("new", "open", "new-signal", () => throw new Exception("no execution")).Status);
        Assert.Equal("filled", restored.RunFullClose("protect", () => Filled("protect")).Status);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void Task2B_AmbiguousCompactionFreezesWritesAndPreservesReservationOnEitherDiskOutcome(bool afterPublication)
    {
        string saved = Encode(Terminals(257)); int writes = 0, calls = 0;
        var journal = new CommandJournal(saved, s => { writes++; if (afterPublication) saved = s; throw new IOException("compaction failed"); });
        Assert.Equal("rejected", journal.Run("reserved", "partial", null, () => { calls++; return Filled("reserved"); }).Status);
        Assert.False(journal.Healthy);
        journal.Run("later", "open", "later", () => { calls++; return Filled("later"); });
        Assert.Equal(1, writes); Assert.Equal(0, calls);
        var restored = new CommandJournal(saved, _ => { }); Assert.True(restored.Healthy);
        Assert.Equal(afterPublication ? "uncertain" : null, restored.Lookup("reserved")?.Status);
        Assert.Equal(42, restored.Lookup("old-0")!.PositionId);
        Assert.Equal("filled", journal.RunFullClose("protect", () => Filled("protect")).Status);
    }

    [Fact]
    public void Task2B_ReplayCapacityIsFiniteFailClosedWithProtectiveCloseAndOldReplayAvailable()
    {
        string saved = Encode(Terminals(CommandJournal.ReplayCapacity));
        var journal = new CommandJournal(saved, _ => throw new Exception("capacity must not write"));
        Assert.True(journal.Healthy); int executions = 0;
        Assert.Equal("rejected", journal.Run("overflow", "open", "new", () => { executions++; return Filled("overflow"); }).Status);
        Assert.Equal("filled", journal.Run("old-0", "close 0", "signal-0", () => { executions++; return Filled("old-0"); }).Status);
        Assert.Equal("filled", journal.RunFullClose("protect", () => Filled("protect")).Status); Assert.Equal(0, executions);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":3,\"entries\":{},\"tombstones\":{}}")]
    [InlineData("{\"schemaVersion\":2,\"entries\":{}}")]
    [InlineData("{\"schemaVersion\":2,\"entries\":null,\"tombstones\":{}}")]
    public void Task2B_UnknownIncompleteOrCorruptVersionedJournalIsQuarantined(string saved)
    {
        int writes = 0;
        var journal = new CommandJournal(saved, _ => writes++); Assert.False(journal.Healthy);
        Assert.Equal("rejected", journal.Run("new", "partial", null, () => throw new Exception("no mutation")).Status);
        Assert.Equal(0, writes); Assert.Equal("filled", journal.RunFullClose("full", () => Filled("full")).Status);
    }

    [Theory]
    [InlineData("checkpoint")][InlineData("result")][InlineData("terminal")]
    public void Task2B_ReductionWitnessSurvivesCrashCopiesCompactionAndProtocol(string boundary)
    {
        string saved = ""; int writes = 0, executions = 0;
        var witness = new ReductionFillEvidence { ActionId = "partial", PositionId = 42, SignalId = "s1", Label = "scope", Symbol = "US500", BeforeUnits = 2, ClosedUnits = 1, RemainingUnits = 1 };
        var journal = new CommandJournal(null, s => { saved = s; writes++; if (boundary == "result" && writes == 3) throw new IOException("lost result ack"); });
        var result = journal.Run("partial", "close partial", null, () =>
        {
            executions++; Assert.True(journal.RecordReductionFill("partial", witness));
            if (boundary == "checkpoint") throw new IOException("crash after successful SDK checkpoint");
            return Filled("partial"); // final envelope may omit witness, journal must preserve it
        });
        Assert.Equal(witness, result.ReductionFill);
        for (int i = 0; i < 258; i++) journal.Run("other-" + i, "modify " + i, null, () => Filled("other-" + i));
        var restored = new CommandJournal(saved, _ => { });
        var replay = restored.Run("partial", "close partial", null, () => { executions++; return Filled("partial"); });
        Assert.Equal(witness, replay.ReductionFill); Assert.Equal(1, executions);
        Assert.Equal(witness, ProtocolResult.ToOutcome(BridgeProtocol.Deserialize<ResultMsg>(BridgeProtocol.Serialize(replay))!).ReductionFill);
    }

    [Fact]
    public void Task2B_SdkWitnessRequiresPositiveExactReductionNotMereVolumeDecreaseOrBadQuantum()
    {
        Assert.Null(PlatformSafety.ReductionWitness(false, "p", 42, "s", "scope", "US500", 2, 1, 1, 0.1));
        Assert.Null(PlatformSafety.ReductionWitness(true, "p", 42, "s", "scope", "US500", 2, 0.5, 1, 0.1));
        Assert.Null(PlatformSafety.ReductionWitness(true, "p", 42, "s", "scope", "US500", 2, 1, 1, Math.BitIncrement(0.1)));
        Assert.NotNull(PlatformSafety.ReductionWitness(true, "p", 42, "s", "scope", "US500", 2, 1, 1, 0.1));
    }
}
