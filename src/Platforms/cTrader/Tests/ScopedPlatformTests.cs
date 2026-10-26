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
