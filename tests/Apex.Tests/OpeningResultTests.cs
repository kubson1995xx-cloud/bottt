using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Bridge;
using Apex.Core.Model;
using Apex.Desktop.Bridge;
using Xunit;

namespace Apex.Tests;

public sealed class OpeningResultTests
{
    private sealed class Transport : IBridgeTransport
    {
        public string Kind => "test";
        public string Remote => "memory";
        public BridgeSession Session = null!;
        public string Status = "filled";
        public string? LastOpenId;
        public Task SendAsync(string json, CancellationToken ct)
        {
            using var command = JsonDocument.Parse(json);
            if (!command.RootElement.TryGetProperty("id", out var commandId)) return Task.CompletedTask;
            string id = commandId.GetString()!;
            if (BridgeProtocol.ReadType(json) == "open") LastOpenId = id;
            var response = new ResultMsg { Id = id, Status = Status, PositionId = 42, Price = 4000 };
            // Supply optional new evidence through the wire, not by peeking into adapter internals.
            string result = BridgeProtocol.Serialize(response).TrimEnd('}') + ",\"openingFill\":" + Evidence + "}";
            Session.Handle(result);
            return Task.CompletedTask;
        }
        public Task CloseAsync(string reason) => Task.CompletedTask;
    }
    private const string Evidence = """
        {"request":{"executionId":"original-command","signalId":"signal","symbol":"US500","direction":1,"volumeUnits":2,
          "stopLossPips":10,"takeProfitPips":20,"label":"fresh-scope","comment":"signal"},
         "positionId":42,"initialVolumeUnits":1,"volumeStep":0.1,"entryPrice":4000,"entryTimeUtc":"2026-10-26T08:00:00Z",
         "openingDeals":[{"dealId":9223372036854775800,"orderId":17,"positionId":42,"volumeUnits":1,"price":4000,
           "timeUtc":"2026-10-26T08:00:00Z","symbol":"US500","direction":1,"label":"fresh-scope","comment":"signal"}]}
        """;

    [Fact]
    public void OpenWirePreservesCoreSignalIdentityDistinctFromBrokerComment()
    {
        var command = BridgeProtocol.Deserialize<OpenCmd>("{\"id\":\"command\",\"signalId\":\"exact-core-signal\",\"comment\":\"short-broker-comment\"}")!;
        using var json = JsonDocument.Parse(BridgeProtocol.Serialize(command));
        Assert.True(json.RootElement.TryGetProperty("signalId", out var signal), "Original signal identity was lost");
        Assert.Equal("exact-core-signal", signal.GetString());
    }

    [Fact]
    public async Task RealOpenDispatchUsesCorePrecommittedOperationId()
    {
        var t = new Transport(); var s = t.Session = new BridgeSession(t, trustedSyntheticComposition: true);
        var account = new AccountDto { Number = 910001, Broker = "Pepperstone", Currency = "USD", Balance = 10000, Equity = 10000, FreeMargin = 10000 };
        s.Handle(BridgeProtocol.Serialize(new HelloMsg { Platform = "cTrader", Account = account, HistoryCapability = HistoryPage.Capability }));
        s.State = SessionState.Active; s.Handle(BridgeProtocol.Serialize(new PositionsMsg()));
        await s.RequestHistoryAsync("fresh-scope");
        s.Handle(BridgeProtocol.Serialize(new HistoryPage { RequestId = s.HistoryRequestId, Page = 0, Pages = 1, Total = 0,
            CapturedAt = BridgeProtocol.ToUnixMs(DateTime.UtcNow), Account = account, Positions = new(), Coverage = "required" }));
        Assert.True(s.History.RequiredCoverage);
        var adapter = new SessionAdapter(() => null, () => true, trustedSimulation: true); adapter.Attach(s);
        adapter.OpenMarket(new OrderRequest { ExecutionId = "core-precommitted-operation", SignalId = "signal", Symbol = "US500", VolumeUnits = 2,
            Direction = TradeDirection.Long, Label = "fresh-scope", Comment = "signal", StopLossPips = 10, TakeProfitPips = 20 });
        Assert.Equal("core-precommitted-operation", t.LastOpenId);
    }

    [Theory]
    [InlineData("filled")]
    [InlineData("uncertain")]
    public void RealSessionRequestMapsOriginalFillEvenOnUncertainProtectionOutcome(string status)
    {
        var transport = new Transport { Status = status };
        var session = transport.Session = new BridgeSession(transport);
        session.Handle(BridgeProtocol.Serialize(new HelloMsg { Platform = "cTrader", Account = new AccountDto
        {
            Number = 910001, Broker = "Pepperstone", Currency = "USD", IsLive = false, Hedging = true,
            Balance = 10000, Equity = 10000, FreeMargin = 10000,
        } }));
        session.State = SessionState.Active;
        session.Handle(BridgeProtocol.Serialize(new PositionsMsg()));
        var adapter = new SessionAdapter(() => null); adapter.Attach(session);
        // Reduce-only request exercises the SAME result boundary as open without granting any open gate.
        var outcome = adapter.ModifyStopLoss(42, 3995);
        using var result = JsonDocument.Parse(BridgeProtocol.Serialize(outcome));
        Assert.True(result.RootElement.TryGetProperty("openingFill", out var fill), "Adapter discarded original opening evidence");
        Assert.Equal(1, fill.GetProperty("initialVolumeUnits").GetDouble());
        Assert.Equal(2, fill.GetProperty("request").GetProperty("volumeUnits").GetDouble());
        Assert.Equal("original-command", fill.GetProperty("request").GetProperty("executionId").GetString());
        Assert.Equal(status == "filled" ? OrderStatus.Filled : OrderStatus.Uncertain, outcome.Status);
        Assert.Equal(42, outcome.PositionId); // uncertain execution identity must not be discarded either
        Assert.False(adapter.GetHistory().RequiredCoverage);
    }
}
