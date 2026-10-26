using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Bridge;
using Apex.Desktop.Bridge;
using Xunit;

namespace Apex.Tests;
public sealed class HistoryIntegrationTests
{
    internal sealed class Transport : IBridgeTransport
    {
        public string Kind => "fixture";
        public string Remote => "127.0.0.1";
        public Task SendAsync(string json, CancellationToken ct) => Task.CompletedTask;
        public Task CloseAsync(string reason) => Task.CompletedTask;
    }
    static readonly AccountDto Account = new() { Number = 123456, Broker = "Pepperstone Test", Currency = "USD", Balance = 10000, Equity = 10000, FreeMargin = 10000 };
    internal static ClosedDto Deal(long id = 42, long deal = 1, double volume = 1) => new()
    {
        Id = id, ClosingDealId = deal, Symbol = "US500", Label = "APEX", Units = volume, Entry = 4000, Exit = 4010,
        EntryTime = BridgeProtocol.ToUnixMs(RecoveryTests.Now.AddHours(-1)), ExitTime = BridgeProtocol.ToUnixMs(RecoveryTests.Now),
        Gross = 10, Commission = -1.5, Swap = -1, Net = 7.5,
    };
    static BridgeSession Session(string platform = "Symulator")
    {
        var s = new BridgeSession(new Transport());
        s.HelloReceived += (s, _) => s.State = SessionState.Active;
        s.Handle(BridgeProtocol.Serialize(new HelloMsg { Platform = platform, Account = Account, HistoryCapability = HistoryPage.Capability }));
        return s;
    }
    static HistoryPage Page(BridgeSession s, int page, int pages = 2, int total = 2) => new()
    {
        RequestId = s.HistoryRequestId, Page = page, Pages = pages, Total = total,
        CapturedAt = BridgeProtocol.ToUnixMs(DateTime.UtcNow), Coverage = "required",
        Deals = new() { Deal(deal: page + 1) }, Account = page == pages - 1 ? Account : null,
        Positions = page == pages - 1 ? new() : null,
    };
    [Fact]
    public async Task HistorySnapshotSurvivesReconnectAndChunkBoundaries()
    {
        var s = Session(); await s.RequestHistoryAsync("APEX");
        var first = Page(s, 0); var last = Page(s, 1); last.CapturedAt = first.CapturedAt;
        s.Handle(BridgeProtocol.Serialize(first)); Assert.False(s.History.Complete);
        s.Handle(BridgeProtocol.Serialize(last)); Assert.True(s.History.Complete); Assert.Equal(2, s.History.Closed.Count);
        string old = s.HistoryRequestId; await s.RequestHistoryAsync("APEX");
        s.Handle(BridgeProtocol.Serialize(last)); Assert.False(s.History.Complete);
        var nextFirst = Page(s, 0); var nextLast = Page(s, 1); nextLast.CapturedAt = nextFirst.CapturedAt;
        s.Handle(BridgeProtocol.Serialize(nextFirst)); s.Handle(BridgeProtocol.Serialize(nextLast));
        Assert.True(s.History.Complete); Assert.NotEqual(old, s.HistoryRequestId);
        var reconnected = Session(); await reconnected.RequestHistoryAsync("APEX");
        reconnected.Handle(BridgeProtocol.Serialize(nextLast)); Assert.False(reconnected.History.Complete);
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-total")]
    [InlineData("wrong-account")]
    [InlineData("duplicate-conflict")]
    [InlineData("error")]
    public async Task IncompleteOrConflictingPagesNeverPublish(string fault)
    {
        var s = Session(); await s.RequestHistoryAsync("APEX");
        var first = Page(s, 0); var last = Page(s, 1); last.CapturedAt = first.CapturedAt;
        if (fault != "missing") s.Handle(BridgeProtocol.Serialize(first));
        if (fault == "wrong-total") last.Total = 3;
        if (fault == "wrong-account") last.Account = new AccountDto { Number = 99, Broker = "other", Currency = "USD" };
        if (fault == "duplicate-conflict") { first.Deals[0].Net = 100; s.Handle(BridgeProtocol.Serialize(first)); }
        if (fault == "error") last.Error = "unavailable";
        s.Handle(BridgeProtocol.Serialize(last)); Assert.False(s.History.Complete);
    }
    [Fact]
    public async Task ActualCTraderCannotClaimRequiredCoverageFromEnumeration()
    {
        var s = Session("cTrader"); await s.RequestHistoryAsync("APEX");
        var page = Page(s, 0, 1, 1); s.Handle(BridgeProtocol.Serialize(page));
        Assert.True(s.History.Complete); Assert.False(s.History.RequiredCoverage);
    }
}
