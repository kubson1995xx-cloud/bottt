using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Apex.Core.Bridge;
using Apex.Desktop.Bridge;
using Xunit;

namespace Apex.Tests;
public sealed class ScopedRuntimeTests
{
    static (BridgeSession session, Guid baseline, ScopeFrame frame) Scoped()
    {
        var s = Session(); var id = Guid.NewGuid(); s.ConfigureScope(id);
        var frame = new ScopeFrame { BaselineId = id, Label = "APEX", ExecutionPath = "desktop-bridge", SafetyVersion = "scoped-demo-v1", TransportGeneration = s.Id, BrokerEpoch = "startup:1", BrokerConnected = true, PositionRevision = 1, PendingOrderRevision = 1, SnapshotId = "capture-1" };
        s.Handle(BridgeProtocol.Serialize(new PositionsMsg { Items = new(), PendingOrders = new(), Scoped = frame with { SnapshotId = "" } }));
        return (s, id, frame);
    }
    static HistoryPage ScopedPage(BridgeSession s, ScopeFrame frame) => new() { RequestId = s.HistoryRequestId, Pages = 1, Total = 0,
        CapturedAt = BridgeProtocol.ToUnixMs(DateTime.UtcNow), Account = Account, Positions = new(), Scoped = frame,
        PendingOrders = new(), OpeningObservations = new(), RecoveredResults = new() };

    [Theory]
    [InlineData("path")]
    [InlineData("label")]
    [InlineData("baseline")]
    [InlineData("epoch")]
    [InlineData("transport")]
    [InlineData("revision")]
    [InlineData("orders")]
    [InlineData("future")]
    [InlineData("missing-path")]
    [InlineData("missing-safety")]
    public async Task WrongScopedMetadataCannotPublishReceipt(string fault)
    {
        var (s, _, frame) = Scoped(); await s.RequestHistoryAsync("APEX");
        var page = ScopedPage(s, frame);
        page.Scoped = fault switch { "path" => frame with { ExecutionPath = "native" }, "label" => frame with { Label = "wrong" },
            "baseline" => frame with { BaselineId = Guid.NewGuid() }, "epoch" => frame with { BrokerEpoch = "startup:2" },
            "transport" => frame with { TransportGeneration = "other" }, "revision" => frame with { PositionRevision = 2 }, _ => frame };
        if (fault == "orders") page.PendingOrders = null;
        if (fault == "future") page.CapturedAt += 60000;
        int receipts = 0; s.ScopedCompleted += (_, _) => receipts++;
        string json = BridgeProtocol.Serialize(page);
        if (fault == "missing-path") json = json.Replace("\"executionPath\":\"desktop-bridge\",", "");
        if (fault == "missing-safety") json = json.Replace("\"safetyVersion\":\"scoped-demo-v1\",", "");
        s.Handle(json); Assert.Equal(0, receipts); Assert.False(s.History.Complete);
    }
    [Theory]
    [InlineData("closed")][InlineData("positions")]
    public async Task ExpectedMutationDuringHistoryCaptureDiscardsWithoutPermanentProtocolFault(string mutation)
    {
        var (s, _, frame) = Scoped(); await s.RequestHistoryAsync("APEX");
        var pending = ScopedPage(s, frame);
        if (mutation == "closed") s.Handle(BridgeProtocol.Serialize(new ClosedMsg { Position = new ClosedDto { Id = 42 } }));
        else s.Handle(BridgeProtocol.Serialize(new PositionsMsg { Items = new() { new PositionDto { Id = 42, Label = "APEX", Units = 0.1 } }, PendingOrders = new(), Scoped = frame with { PositionRevision = 2 } }));
        s.Handle(BridgeProtocol.Serialize(pending)); Assert.False(s.ProtocolFault); Assert.False(s.History.Complete);
        await s.RequestHistoryAsync("APEX"); var next = ScopedPage(s, frame with { PositionRevision = mutation == "closed" ? 1 : 2, SnapshotId = "after-mutation" });
        next.Positions = s.Positions.ToList();
        s.Handle(BridgeProtocol.Serialize(next)); Assert.True(s.History.Complete); Assert.False(s.ProtocolFault);
    }

    [Fact]
    public async Task CurrentReceiptIsValueBasedAndRoutineRefreshDoesNotRenewOrRetireLastGood()
    {
        var (s, _, frame) = Scoped(); await s.RequestHistoryAsync("APEX");
        Apex.Desktop.Bridge.ScopedSnapshot? snapshot = null; s.ScopedCompleted += (_, receipt) => snapshot = receipt;
        s.Handle(BridgeProtocol.Serialize(ScopedPage(s, frame))); Assert.NotNull(snapshot);
        var copy = JsonSerializer.Deserialize<Apex.Core.Engine.ScopedReceipt>(JsonSerializer.Serialize(snapshot!.Receipt))!;
        Assert.True(s.IsCurrent(copy));
        await s.RequestHistoryAsync("APEX"); Assert.True(s.IsCurrent(copy)); Assert.True(s.History.Complete);
        s.Handle(BridgeProtocol.Serialize(ScopedPage(s, frame with { SnapshotId = "capture-2" })));
        Assert.False(s.IsCurrent(copy)); Assert.True(s.IsCurrent(snapshot!.Receipt));
    }
    [Fact]
    public async Task MissingPendingInventoryInLiveFrameInvalidatesEvenWhenPreviouslyEmpty()
    {
        var (s, _, frame) = Scoped(); await s.RequestHistoryAsync("APEX");
        Apex.Desktop.Bridge.ScopedSnapshot? snapshot = null; s.ScopedCompleted += (_, receipt) => snapshot = receipt;
        s.Handle(BridgeProtocol.Serialize(ScopedPage(s, frame))); Assert.True(s.IsCurrent(snapshot!.Receipt));
        s.Handle(BridgeProtocol.Serialize(new PositionsMsg { Items = new(), Scoped = frame with { SnapshotId = "" } }));
        Assert.False(s.IsCurrent(snapshot.Receipt));
    }

    [Fact]
    public void ScopedHelloCannotInferMissingHedgingFlagFromDtoDefault()
    {
        var s = new BridgeSession(new HistoryIntegrationTests.Transport());
        s.HelloReceived += (s, _) => s.State = SessionState.Active;
        string hello = BridgeProtocol.Serialize(new HelloMsg { Platform = "cTrader", Account = Account, ScopedCapability = ScopeFrame.Capability });
        s.Handle(hello.Replace("\"hedging\":true,", ""));
        Assert.NotEqual(SessionState.Active, s.State);
    }

    [Theory]
    [InlineData("_accountReceivedTick")]
    [InlineData("_positionsReceivedTick")]
    [InlineData("_pendingReceivedTick")]
    public async Task IndependentLiveSnapshotsHaveLocalMonotonicExpiryEvenIfWallClockMovesBack(string field)
    {
        var (s, _, frame) = Scoped(); await s.RequestHistoryAsync("APEX");
        Apex.Desktop.Bridge.ScopedSnapshot? snapshot = null; s.ScopedCompleted += (_, receipt) => snapshot = receipt;
        s.Handle(BridgeProtocol.Serialize(ScopedPage(s, frame))); Assert.True(s.IsCurrent(snapshot!.Receipt));
        var tick = typeof(BridgeSession).GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(tick);
        tick!.SetValue(s, BridgeSession.MonotonicNow() - TimeSpan.FromSeconds(31));
        Assert.False(s.IsCurrent(snapshot.Receipt));
    }
    [Fact]
    public async Task InvalidCaptureRangeImmediatelyInvalidatesLastGoodRatherThanThrowingBeforeFence()
    {
        var (s, _, frame) = Scoped(); await s.RequestHistoryAsync("APEX");
        Apex.Desktop.Bridge.ScopedSnapshot? snapshot = null; s.ScopedCompleted += (_, receipt) => snapshot = receipt;
        s.Handle(BridgeProtocol.Serialize(ScopedPage(s, frame))); Assert.True(s.IsCurrent(snapshot!.Receipt));
        await s.RequestHistoryAsync("APEX"); var bad = ScopedPage(s, frame); bad.CapturedAt = long.MaxValue;
        Assert.Null(Record.Exception(() => s.Handle(BridgeProtocol.Serialize(bad)))); Assert.False(s.IsCurrent(snapshot.Receipt));
    }

    [Fact]
    public void PublicPositionViewCannotMutateLiveProofSource()
    {
        var s = Session(); s.Handle(BridgeProtocol.Serialize(new PositionsMsg { Items = new() { new PositionDto { Id = 42, Label = "APEX", Sl = 3990 } } }));
        s.Positions[0].Sl = 1;
        Assert.Equal(3990, s.Positions[0].Sl);
    }

    [Fact]
    public async Task SourceRevisionRollbackInSameBrokerEpochCannotPublishFreshAuthority()
    {
        var (s, _, frame) = Scoped(); await s.RequestHistoryAsync("APEX");
        s.Handle(BridgeProtocol.Serialize(ScopedPage(s, frame))); Assert.True(s.History.Complete);
        var rolled = frame with { PositionRevision = 0, SnapshotId = "rollback" };
        s.Handle(BridgeProtocol.Serialize(new PositionsMsg { Items = new(), PendingOrders = new(), Scoped = rolled }));
        await s.RequestHistoryAsync("APEX");
        Apex.Desktop.Bridge.ScopedSnapshot? snapshot = null; s.ScopedCompleted += (_, receipt) => snapshot = receipt;
        s.Handle(BridgeProtocol.Serialize(ScopedPage(s, rolled)));
        Assert.True(snapshot == null || !s.IsCurrent(snapshot.Receipt));
    }

    static readonly AccountDto Account = new() { Number = 78123, Broker = "Pepperstone Scope", Currency = "USD", Balance = 10000, Equity = 10000, FreeMargin = 10000, IsLive = false, Hedging = true };
    static BridgeSession Session()
    {
        var s = new BridgeSession(new HistoryIntegrationTests.Transport());
        s.HelloReceived += (s, _) => s.State = SessionState.Active;
        s.Handle(JsonSerializer.Serialize(new { type = "hello", platform = "cTrader", account = Account, historyCapability = HistoryPage.Capability,
            scopedCapability = "scoped-recovery-v1", label = "APEX", brokerEpoch = "startup:1", brokerConnected = true,
            deploymentInventory = new[] { new PlatformStateInventory("APEX", true, 0, false) } }, BridgeProtocol.Json));
        s.Handle(BridgeProtocol.Serialize(new PositionsMsg()));
        return s;
    }
    static HistoryPage Page(BridgeSession s) => new() { RequestId = s.HistoryRequestId, Pages = 1, Page = 0, Total = 0,
        CapturedAt = BridgeProtocol.ToUnixMs(DateTime.UtcNow), Account = Account, Positions = new() };
    [Fact]
    public async Task BrokerOutageBetweenPollsInvalidatesLastGoodEvenWithWebSocketAlive()
    {
        var s = Session(); await s.RequestHistoryAsync("APEX"); s.Handle(BridgeProtocol.Serialize(Page(s)));
        Assert.True(s.History.Complete);
        s.Handle("{\"type\":\"brokerState\",\"brokerEpoch\":\"startup:2\",\"brokerConnected\":false}");
        s.Handle("{\"type\":\"brokerState\",\"brokerEpoch\":\"startup:3\",\"brokerConnected\":true}");
        Assert.True(s.IsAlive); Assert.False(s.History.Complete);
    }
    [Fact]
    public async Task ForeignPositionMutationInvalidatesDedicatedSnapshot()
    {
        var s = Session(); await s.RequestHistoryAsync("APEX"); s.Handle(BridgeProtocol.Serialize(Page(s)));
        Assert.True(s.History.Complete);
        s.Handle(BridgeProtocol.Serialize(new PositionsMsg { Items = new() { new PositionDto { Id = 55, Label = "foreign", Units = 1 } } }));
        Assert.False(s.History.Complete);
    }
    [Fact]
    public async Task PendingOrderMutationInvalidatesDedicatedSnapshot()
    {
        var s = Session(); await s.RequestHistoryAsync("APEX"); s.Handle(BridgeProtocol.Serialize(Page(s)));
        Assert.True(s.History.Complete);
        s.Handle("{\"type\":\"positions\",\"items\":[],\"pendingOrders\":[{\"id\":88,\"label\":\"foreign\"}]}");
        Assert.False(s.History.Complete);
    }
}
