using System;
using System.Text.Json;
using System.Threading.Tasks;
using Apex.Core.Bridge;
using Apex.Desktop.Bridge;
using Xunit;

namespace Apex.Tests;
public sealed class ScopedRuntimeTests
{
    static readonly AccountDto Account = new() { Number = 78123, Broker = "Pepperstone Scope", Currency = "USD", Balance = 10000, Equity = 10000, FreeMargin = 10000, IsLive = false, Hedging = true };
    static BridgeSession Session()
    {
        var s = new BridgeSession(new HistoryIntegrationTests.Transport());
        s.HelloReceived += (s, _) => s.State = SessionState.Active;
        s.Handle(JsonSerializer.Serialize(new { type = "hello", platform = "cTrader", account = Account, historyCapability = HistoryPage.Capability,
            scopedCapability = "scoped-recovery-v1", label = "APEX", brokerEpoch = "startup:1", brokerConnected = true }, BridgeProtocol.Json));
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
