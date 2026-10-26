using System;
using System.Linq;
using System.Reflection;
using Apex.Core.Bridge;
using Xunit;

namespace Apex.Platform.Tests;
public sealed class ScopedPlatformTests
{
    [Fact]
    public void CatalogInventoriesOldLabelsAndMissingJournalWithoutRotatingAwayObligations()
    {
        var type = typeof(ScopedPlatformTests).Assembly.GetType("Apex.Platforms.CTrader.PlatformDeploymentCatalog"); Assert.NotNull(type);
        var values = new System.Collections.Generic.Dictionary<string, string>();
        Func<string, string?> read = key => values.TryGetValue(key, out var value) ? value : null;
        Action<string, string> write = (key, value) => values[key] = value;
        var capture = type!.GetMethod("Inventory")!;
        object[] Args(string label) => new object[] { "broker", 123L, "USD", label, read, write };
        var first = (System.Collections.Generic.List<PlatformStateInventory>)capture.Invoke(null, Args("APEX"))!;
        Assert.All(first, x => Assert.True(x.Healthy && !x.Missing && x.Entries == 0));
        var journalKey = values.Keys.Single(k => k != values.Keys.Single(k => values[k].Contains("Labels")));
        values.Remove(journalKey);
        var next = (System.Collections.Generic.List<PlatformStateInventory>)capture.Invoke(null, Args("fresh"))!;
        Assert.Contains(next, x => x.Label == "APEX" && x.Missing);
        Assert.DoesNotContain(values.Values, v => v.Contains("fresh")); // no rotation registration after loss
    }

    [Fact]
    public void UncertainJournalCanRecoverActualOpeningGraphReadOnlyWithoutResend()
    {
        var journal = new Apex.Platforms.CTrader.CommandJournal(null, _ => { });
        var cmd = new OpenCmd { Id = "op", SignalId = "signal", Symbol = "US500", Units = 2, SlPips = 10, TpPips = 20, Label = "fresh-scope", Comment = "signal" };
        journal.Run(cmd.Id, BridgeProtocol.Serialize(cmd), "signal", () => new ResultMsg { Id = cmd.Id, Status = "uncertain" });
        var lookup = journal.GetType().GetMethod("LookupOpeningRequest", BindingFlags.NonPublic | BindingFlags.Instance); Assert.NotNull(lookup);
        var request = (Apex.Core.Model.OrderRequest)lookup!.Invoke(journal, new object[] { cmd.Id })!;
        var fill = BridgeProtocol.Deserialize<ResultMsg>(OpeningEvidenceTests.Wire)!.OpeningFill!;
        var method = typeof(Apex.Platforms.CTrader.CTraderScopeEvidence).GetMethod("RecoverOpening"); Assert.NotNull(method);
        var result = (ResultMsg)method!.Invoke(null, new object[] { journal.Lookup(cmd.Id)!, request, fill.OpeningDeals, 0.1 })!;
        Assert.NotNull(result.OpeningFill); Assert.Equal("op", result.OpeningFill!.Request.ExecutionId);
        Assert.Equal(1, result.OpeningFill.InitialVolumeUnits); Assert.Equal(2, result.OpeningFill.Request.VolumeUnits);
        Assert.Equal("uncertain", result.Status); Assert.Equal(1, journal.Count);
    }

    [Fact]
    public void CaptureCannotRenewFreshnessAfterLongMainThreadFreeze()
    {
        dynamic s = State(); s.BrokerEvent(true); s.Observe(Array.Empty<PositionDto>(), Array.Empty<PendingOrderDto>());
        var method = ((object)s).GetType().GetMethods().Single(m => m.Name == "Capture");
        Assert.True(method.GetParameters().Length == 4, "capture must retain its start monotonic tick");
        long before = System.Diagnostics.Stopwatch.GetTimestamp() - 31 * System.Diagnostics.Stopwatch.Frequency;
        ScopeFrame capture = (ScopeFrame)method.Invoke(s, new object[] { Guid.NewGuid(), "fresh", "transport", before })!;
        Assert.False((bool)s.CanOpen(capture));
    }

    static dynamic State()
    {
        var type = typeof(ScopedPlatformTests).Assembly.GetType("Apex.Platforms.CTrader.ScopedPlatformState");
        Assert.NotNull(type); return Activator.CreateInstance(type!, true)!;
    }
    [Fact]
    public void EventEpochChangesEvenWhenOutageFallsBetweenPollsAndFencesActualDispatch()
    {
        dynamic s = State(); s.BrokerEvent(true); string before = s.BrokerEpoch;
        s.Observe(Array.Empty<PositionDto>(), Array.Empty<PendingOrderDto>());
        ScopeFrame capture = s.Capture(Guid.NewGuid(), "fresh", "transport");
        Assert.True((bool)s.CanOpen(capture));
        s.BrokerEvent(false); s.BrokerEvent(true);
        Assert.NotEqual(before, (string)s.BrokerEpoch); Assert.False((bool)s.CanOpen(capture));
        int calls = 0; Assert.False((bool)s.TryRun(capture, (Func<bool>)(() => true), (Action)(() => calls++))); Assert.Equal(0, calls);
    }
    [Theory]
    [InlineData("pending")]
    [InlineData("protection")]
    [InlineData("foreign")]
    public void ChangedRevisionBetweenPermitAndActualSdkCallCannotOpen(string fault)
    {
        dynamic s = State(); s.BrokerEvent(true);
        var p = new PositionDto { Id = 42, Label = "fresh", Symbol = "US500", Units = 1, Sl = 3990, Tp = 4020 };
        s.Observe(new[] { p }, Array.Empty<PendingOrderDto>());
        ScopeFrame capture = s.Capture(Guid.NewGuid(), "fresh", "transport");
        Assert.True((bool)s.CanOpen(capture));
        if (fault == "protection") p.Sl = 3991;
        var positions = fault == "foreign" ? new[] { p, new PositionDto { Id = 43, Label = "foreign" } } : new[] { p };
        var pending = fault == "pending" ? new[] { new PendingOrderDto(88, "foreign") } : Array.Empty<PendingOrderDto>();
        s.Observe(positions, pending);
        int calls = 0; Assert.False((bool)s.TryRun(capture, (Func<bool>)(() => true), (Action)(() => calls++))); Assert.Equal(0, calls);
    }
    [Fact]
    public void ReadOnlyRecoveryLookupNeverExecutesOrErasesOriginalResult()
    {
        string saved = ""; var journal = new Apex.Platforms.CTrader.CommandJournal(null, x => saved = x);
        var result = BridgeProtocol.Deserialize<ResultMsg>(OpeningEvidenceTests.Wire)!;
        journal.Run(result.Id, "fp", "signal", () => result);
        var method = journal.GetType().GetMethod("Lookup", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var recovered = (ResultMsg)method!.Invoke(new Apex.Platforms.CTrader.CommandJournal(saved, _ => throw new Exception("read-only lookup must not write")), new object[] { result.Id })!;
        Assert.Equal(BridgeProtocol.Serialize(result.OpeningFill), BridgeProtocol.Serialize(recovered.OpeningFill));
    }
}
