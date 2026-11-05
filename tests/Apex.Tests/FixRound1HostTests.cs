using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Bridge;
using Apex.Core.Engine;
using Apex.Desktop.Bridge;
using Apex.Desktop.Runtime;
using Apex.Desktop.Services;
using Xunit;

namespace Apex.Tests;
public sealed partial class FixRound1Tests
{
    sealed class HostTransport : IBridgeTransport
    {
        public string Kind => "fixture";
        public string Remote => "memory";
        public readonly ConcurrentQueue<string> Sent = new();
        public BridgeSession? Session;
        public Task SendAsync(string json, CancellationToken ct)
        {
            Sent.Enqueue(json);
            if (BridgeProtocol.ReadType(json) == "close")
            {
                var cmd = BridgeProtocol.Deserialize<CloseCmd>(json)!;
                Session!.Handle(BridgeProtocol.Serialize(new ResultMsg { Id = cmd.Id, Status = "filled", PositionId = cmd.PositionId }));
            }
            return Task.CompletedTask;
        }
        public Task CloseAsync(string reason) => Task.CompletedTask;
    }
    static AccountDto HostAccount(bool live = false) => new() { Number = 123456, Broker = "Pepperstone Test", Currency = "USD", IsLive = live, Balance = 10000, Equity = 10000, FreeMargin = 10000 };
    static PositionDto HostPosition(long id = 42, string label = "APEX", double sl = 3990, double? tp = 4020) => new()
    { Id = id, Label = label, Symbol = "US500", Side = "buy", Units = 2, Entry = 4000, Sl = sl, Tp = tp, EntryTime = BridgeProtocol.ToUnixMs(Now.AddHours(-2)) };
    static BridgeSession HostSession(HostTransport t, bool live = false)
    {
        var s = new BridgeSession(t, trustedSyntheticComposition: true); t.Session = s;
        s.HelloReceived += (s, _) => s.State = SessionState.Active;
        s.Handle(BridgeProtocol.Serialize(new HelloMsg { Platform = "Symulator", Account = HostAccount(live), HistoryCapability = HistoryPage.Capability }));
        s.Handle(BridgeProtocol.Serialize(new PositionsMsg { Items = new() { HostPosition(), HostPosition(43, "APEX-other") } }));
        s.Handle(BridgeProtocol.Serialize(new SymbolsMsg()));
        return s;
    }
    static HistoryPage EmptyPage(BridgeSession s) => new()
    {
        RequestId = s.HistoryRequestId, Page = 0, Pages = 1, Total = 0, Coverage = "required",
        CapturedAt = BridgeProtocol.ToUnixMs(DateTime.UtcNow), Account = HostAccount(), Positions = new(),
    };
    [Theory]
    [InlineData("before-page0")]
    [InlineData("protection")]
    [InlineData("identity")]
    [InlineData("ownset")]
    public async Task FrozenHistoryNeverRepublishesSupersededLivePositions(string race)
    {
        var s = HostSession(new HostTransport()); await s.RequestHistoryAsync("APEX");
        var first = EmptyPage(s); first.Pages = 2; first.Account = null; first.Positions = null;
        var last = EmptyPage(s); last.Page = 1; last.Pages = 2; last.CapturedAt = first.CapturedAt;
        last.Positions = race == "before-page0" ? new() : new() { HostPosition(), HostPosition(43, "APEX-other") };
        if (race != "before-page0") s.Handle(BridgeProtocol.Serialize(first));
        var newer = HostPosition(sl: 3995, tp: null);
        if (race == "identity") { newer = HostPosition(); newer.Symbol = "OTHER"; newer.Side = "sell"; newer.Entry = 4100; newer.Comment = "new identity"; }
        if (race == "ownset") newer.Label = "APEX-other";
        s.Handle(BridgeProtocol.Serialize(new PositionsMsg { Items = new() { newer, HostPosition(43, "APEX-other") } }));
        var received = typeof(BridgeSession).GetField("_positionsReceivedUtc", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s);
        if (race == "before-page0") s.Handle(BridgeProtocol.Serialize(first));
        s.Handle(BridgeProtocol.Serialize(last));
        Assert.False(s.History.Complete);
        var actual = s.Positions.Single(p => p.Id == 42);
        Assert.Equal(newer.Sl, actual.Sl); Assert.Equal(newer.Tp, actual.Tp);
        Assert.Equal(newer.Symbol, actual.Symbol); Assert.Equal(newer.Label, actual.Label); Assert.Equal(newer.Comment, actual.Comment);
        Assert.Equal(received, typeof(BridgeSession).GetField("_positionsReceivedUtc", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s));
    }
    [Fact]
    public async Task ExpectedOwnMutationResynchronizesWithoutLatchingStopAndFrozenPageCannotRefreshLiveData()
    {
        var s = HostSession(new HostTransport()); s.Handle(BridgeProtocol.Serialize(new PositionsMsg()));
        await s.RequestHistoryAsync("APEX"); s.Handle(BridgeProtocol.Serialize(EmptyPage(s)));
        var e = SessionEngine(s); Assert.Equal(Apex.Core.Model.EngineMode.Running, e.Mode);
        s.Handle(BridgeProtocol.Serialize(new PositionsMsg { Items = new() { HostPosition() } }));
        s.EnsureHistory("APEX"); e.OnTimer(DateTime.UtcNow);
        Assert.False(e.HistoryReady); Assert.Equal(Apex.Core.Model.EngineMode.Running, e.Mode);
        var received = typeof(BridgeSession).GetField("_positionsReceivedUtc", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s);
        var page = EmptyPage(s); page.Positions = new() { HostPosition() }; s.Handle(BridgeProtocol.Serialize(page));
        e.OnTimer(DateTime.UtcNow);
        Assert.True(e.HistoryReady, e.HistoryMessage); Assert.Equal(Apex.Core.Model.EngineMode.Running, e.Mode);
        Assert.Equal(received, typeof(BridgeSession).GetField("_positionsReceivedUtc", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s));
    }
    static TradingEngine SessionEngine(BridgeSession s)
    {
        var adapter = new SessionAdapter(() => null, () => true, trustedSimulation: true); adapter.Attach(s);
        var e = new TradingEngine(new EngineConfig(), new Apex.Core.Adaptive.StrategyBook(Scenarios.DipOnlyProfile()),
            adapter, adapter, adapter, new MemoryStore(), new NullLog(), new NullJournal());
        e.SetGuard(new Apex.Core.Safety.AccountGuardResult { Allowed = true }); e.Initialize(DateTime.UtcNow); e.CmdStart();
        return e;
    }
    [Theory]
    [InlineData("success")]
    [InlineData("protocol-error")]
    [InlineData("disconnect")]
    [InlineData("expired")]
    public async Task RoutineScheduledHistoryRefreshKeepsRunningOnlyWhileAcceptedScopeRemainsValid(string result)
    {
        var t = new HostTransport(); var s = HostSession(t);
        s.Handle(BridgeProtocol.Serialize(new PositionsMsg()));
        await s.RequestHistoryAsync("APEX"); s.Handle(BridgeProtocol.Serialize(EmptyPage(s)));
        var e = SessionEngine(s); Assert.Equal(Apex.Core.Model.EngineMode.Running, e.Mode);
        string old = s.HistoryRequestId;
        typeof(BridgeSession).GetField("_historyStarted", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(s, DateTime.UtcNow.AddSeconds(-11));
        typeof(BridgeSession).GetField("_historyRequestTick", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(s, BridgeSession.MonotonicNow() - TimeSpan.FromSeconds(11));
        s.EnsureHistory("APEX"); Assert.NotEqual(old, s.HistoryRequestId);
        e.OnTimer(DateTime.UtcNow);
        Assert.Equal(Apex.Core.Model.EngineMode.Running, e.Mode);
        if (result == "protocol-error") { var bad = EmptyPage(s); bad.Error = "unavailable"; s.Handle(BridgeProtocol.Serialize(bad)); }
        else if (result == "disconnect") s.MarkClosed();
        else if (result == "expired") typeof(BridgeSession).GetField("_historyReceived", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(s, DateTime.UtcNow.AddSeconds(-31));
        else s.Handle(BridgeProtocol.Serialize(EmptyPage(s)));
        e.OnTimer(DateTime.UtcNow);
        Assert.Equal(result == "success" ? Apex.Core.Model.EngineMode.Running : Apex.Core.Model.EngineMode.Stopped, e.Mode);
        if (result is "protocol-error" or "expired") { s.Handle(BridgeProtocol.Serialize(EmptyPage(s))); e.OnTimer(DateTime.UtcNow); Assert.Equal(Apex.Core.Model.EngineMode.Stopped, e.Mode); }
    }
    [Theory]
    [InlineData("corrupt", "closeAll", "fresh", true)]
    [InlineData("corrupt", "emergency", "fresh", true)]
    [InlineData("readfault", "closeAll", "fresh", true)]
    [InlineData("readfault", "emergency", "fresh", true)]
    [InlineData("corrupt-control", "closeAll", "fresh", true)]
    [InlineData("corrupt-control", "emergency", "fresh", true)]
    [InlineData("readfault-control", "emergency", "fresh", true)]
    [InlineData("corrupt", "closeAll", "stale", false)]
    [InlineData("corrupt", "emergency", "live", false)]
    [InlineData("corrupt", "closeAll", "switch", false)]
    [InlineData("corrupt", "emergency", "foreign", false)]
    public async Task DesktopStartupStateFaultRetainsOnlyFreshOwnedDemoFullClose(string fault, string action, string condition, bool allowed)
    {
        string dir = TempDir();
        try
        {
            var config = new ConfigStore(dir);
            string stateDir = Path.Combine(dir, "konta", "Symulator_PepperstoneTest_123456", "stan");
            var store = new FileStore(stateDir);
            string path = Path.Combine(stateDir, fault.EndsWith("control") ? "desktop_control.json" : "APEX_APEX_123456.json");
            if (fault.StartsWith("corrupt")) store.Set(fault.EndsWith("control") ? "desktop-control" : Key, "{broken"); else Directory.CreateDirectory(path);
            var t = new HostTransport(); var s = HostSession(t, condition == "live");
            using (var runtime = new AccountRuntime("Symulator:PepperstoneTest:123456", "Symulator", config, new NewsCalendar(), new AppLog(Path.Combine(dir, "logs"))))
            {
                runtime.Attach(s);
                await runtime.Command(e => { Assert.Null(e); return "startup failed"; });
                if (condition == "stale") typeof(BridgeSession).GetField("_positionsReceivedUtc", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(s, DateTime.UtcNow.AddSeconds(-31));
                if (condition == "switch") { var a = HostAccount(); a.Number++; s.Handle(BridgeProtocol.Serialize(new AccountMsg { Account = a })); }
                if (condition == "foreign") s.Handle(BridgeProtocol.Serialize(new PositionsMsg { Items = new() { HostPosition(42, "APEX-other"), HostPosition(43, "APEX-other") } }));
                await runtime.Command(e => runtime.ExecuteAction(e, action, default));
                var closes = t.Sent.Where(x => BridgeProtocol.ReadType(x) == "close").Select(x => BridgeProtocol.Deserialize<CloseCmd>(x)!).ToArray();
                if (allowed) { var close = Assert.Single(closes); Assert.Equal(42, close.PositionId); Assert.Null(close.Units); }
                else Assert.Empty(closes);
                Assert.DoesNotContain(t.Sent, x => BridgeProtocol.ReadType(x) is "open" or "modify");
                await runtime.Command(e => { Assert.Null(e); return runtime.ExecuteAction(e, "start", default); });
                Assert.False(s.History.Complete);
            }
            if (fault.StartsWith("corrupt")) Assert.Equal("{broken", File.ReadAllText(path)); else Assert.True(Directory.Exists(path));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
