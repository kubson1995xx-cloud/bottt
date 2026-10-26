using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

// Stałe regresje granic modułów: lokalne ramki syntetyczne, nigdy broker.
public sealed class ReviewBoundaryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apex-review-regression-" + Guid.NewGuid().ToString("N"));
    public ReviewBoundaryTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }
    private sealed class Transport : IBridgeTransport
    {
        public string Kind => "synthetic-test";
        public string Remote => "memory";
        public BridgeSession Session = null!;
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Sent = new();
        public Task SendAsync(string json, CancellationToken ct)
        {
            Sent.Enqueue(json);
            using var d = JsonDocument.Parse(json);
            if (BridgeProtocol.ReadType(json) == "close")
                Session.Handle(BridgeProtocol.Serialize(new ResultMsg { Id = d.RootElement.GetProperty("id").GetString()!, Status = "filled", PositionId = 77 }));
            return Task.CompletedTask;
        }
        public Task CloseAsync(string reason) => Task.CompletedTask;
    }
    private static AccountDto Account(double equity = 10000) => new() { Number = 910001, Broker = "Pepperstone", Currency = "USD", IsLive = false, Balance = 10000, Equity = equity, FreeMargin = equity };
    private static BridgeSession Session(Transport t)
    {
        var s = t.Session = new BridgeSession(t);
        s.Handle(BridgeProtocol.Serialize(new HelloMsg { Platform = "cTrader", Account = Account() }));
        s.State = SessionState.Active;
        s.Handle(BridgeProtocol.Serialize(new PositionsMsg()));
        s.Handle(BridgeProtocol.Serialize(new SymbolsMsg()));
        return s;
    }
    private AccountRuntime Runtime()
    {
        var c = new ConfigStore(_dir); c.Update(x => x.News.Enabled = false);
        return new AccountRuntime("cTrader:Pepperstone:910001", "cTrader", c, new NewsCalendar(), new AppLog(Path.Combine(_dir, "log")));
    }
    [Fact]
    public async Task StorageFailureBlocksEntriesButNotEmergencyFullClose()
    {
        using var r = Runtime(); var t = new Transport(); var s = Session(t); r.Attach(s); await r.Command(_ => "ready");
        s.Handle(BridgeProtocol.Serialize(new PositionsMsg { Items = new() { new PositionDto { Id = 77, Symbol = "US500", Side = "buy", Label = "APEX", Entry = 5000, Price = 5000, Sl = 4900, Tp = 5300, Units = .1 } } }));
        string path = Path.Combine(_dir, "konta", "cTrader_Pepperstone_910001", "stan", "APEX_APEX_910001.json");
        File.Delete(path); Directory.CreateDirectory(path);
        await r.Command(e => { e!.CmdSetRisk(.3); e.SaveState(DateTime.UtcNow, true); return "fault"; });
        Assert.StartsWith("Nie można uruchomić", await r.Command(e => r.ExecuteAction(e, "start", default)));
        await r.Command(e => r.ExecuteAction(e, "emergency", default));
        Assert.Contains(t.Sent, x => BridgeProtocol.ReadType(x) == "close");
        Assert.DoesNotContain(t.Sent, x => BridgeProtocol.ReadType(x) == "open");
    }
    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public async Task DistressedAuthenticatedDemoStillAllowsEmergencyClose(double equity)
    {
        using var r = Runtime(); var t = new Transport(); var s = Session(t); r.Attach(s); await r.Command(_ => "ready");
        s.Handle(BridgeProtocol.Serialize(new AccountMsg { Account = Account(equity) }));
        s.Handle(BridgeProtocol.Serialize(new PositionsMsg { Items = new() { new PositionDto { Id = 77, Symbol = "US500", Side = "buy", Label = "APEX", Entry = 5000, Price = 5000, Sl = 4900, Tp = 5300, Units = .1 } } }));
        Assert.Equal(SessionState.Active, s.State);
        Assert.StartsWith("Nie można uruchomić", await r.Command(e => r.ExecuteAction(e, "start", default)));
        await r.Command(e => r.ExecuteAction(e, "emergency", default));
        Assert.Contains(t.Sent, x => BridgeProtocol.ReadType(x) == "close");
    }
    [Fact]
    public async Task ReceivedCloseSurvivesNetworkThreadReconnectAndRetiredEventsAreIgnored()
    {
        using var r = Runtime(); var first = Session(new Transport()); r.Attach(first); await r.Command(_ => "ready");
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var blocked = r.Command(_ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); return "released"; });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        try
        {
            r.OnClosed(first, new ClosedDto { Id = 77, Label = "APEX", Symbol = "US500", EntryTime = 1, ExitTime = 2, Net = 123 });
            var second = Session(new Transport()); r.Attach(second);
            r.OnClosed(first, new ClosedDto { Id = 78, Label = "APEX", Symbol = "US500", EntryTime = 1, ExitTime = 2, Net = 321 });
        }
        finally { release.Set(); }
        await blocked; await r.Command(_ => "barrier");
        Assert.Single(r.Journal.AllRecent());
        Assert.Equal(77, r.Journal.AllRecent()[0].PositionId);
    }
    [Fact]
    public void ExecutionTestDisappearanceBeforeCloseIsNotPass()
    {
        var r = new EngineRig(); r.Engine.CmdStop();
        Assert.Contains("rozpoczęty", r.Engine.CmdExecutionTest(r.Now, "US500"));
        r.Tick(5); r.Tick(20); r.Tick(5);
        r.Broker.Positions.Clear(); r.Tick(5);
        Assert.Empty(r.Broker.Closes);
        Assert.DoesNotContain(r.Engine.LastExecutionTest, x => x.Contains("TEST ZALICZONY"));
        Assert.Contains(r.Engine.LastExecutionTest, x => x.Contains("TEST PRZERWANY"));
    }
}
