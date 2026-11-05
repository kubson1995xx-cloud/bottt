using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Bridge;
using Apex.Core.Engine;
using Apex.Core.Model;
using Apex.Desktop.Bridge;
using Apex.Platforms.CTrader;
using Xunit;

namespace Apex.Tests;

public sealed class PartialDispatchFenceTests
{
    internal sealed class Transport : IBridgeTransport
    {
        internal BridgeSession Session = null!;
        internal readonly List<CloseCmd> Closes = new();
        internal Func<CloseCmd, ResultMsg> Execute = null!;
        public string Kind => "test";
        public string Remote => "memory";
        public Task CloseAsync(string reason) => Task.CompletedTask;
        public Task SendAsync(string json, CancellationToken ct)
        {
            if (BridgeProtocol.ReadType(json) == "close")
            {
                // Actual wire serialization roundtrip, never a replacement DTO.
                var c = BridgeProtocol.Deserialize<CloseCmd>(json)!; Closes.Add(c);
                Session.Handle(BridgeProtocol.Serialize(Execute(c)));
            }
            return Task.CompletedTask;
        }
    }
    internal sealed class Rig
    {
        internal readonly ScopedPlatformState Platform = new();
        internal readonly Transport Transport = new();
        internal readonly BridgeSession Session;
        internal readonly SessionAdapter Adapter;
        internal readonly CommandJournal Journal;
        internal readonly PositionDto Position = new() { Id = 42, Symbol = "US500", Label = "APEX", Units = 2, Sl = 3990, Tp = 4020, Comment = "s1" };
        internal readonly AccountDto Account = new() { Number = 123, Broker = "Pepperstone", Currency = "USD", IsLive = false, Hedging = true };
        internal ScopedReceipt Receipt = null!;
        internal readonly ScopeFrame Frame;
        internal string Saved = "";
        internal Action? DuringFlush;
        internal int PartialCalls, FullCalls, AuthChecks;
        internal TimeSpan Tick;
        internal readonly TaskCompletionSource Queued = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly PartialCloseRequest Request = new("partial", 42, "s1", 2, 1);
        internal readonly ScopedPartialDispatchAuthorization Authorization;
        internal Rig(RecoveryBaseline? baseline = null, PositionInfo? original = null)
        {
            if (baseline != null)
            {
                Account.Number = baseline.Identity.AccountNumber; Account.Broker = baseline.Identity.Broker; Account.Currency = baseline.Identity.Currency;
                Position.Label = baseline.Identity.Label;
            }
            if (original != null)
            {
                Position.Entry = original.EntryPrice; Position.EntryTime = BridgeProtocol.ToUnixMs(original.EntryTimeUtc);
                Position.Comment = original.Comment;
            }
            long accountNumber = Account.Number; string broker = Account.Broker, currency = Account.Currency, label = Position.Label;
            Session = new BridgeSession(Transport); Transport.Session = Session;
            Session.HelloReceived += (session, _) => session.State = SessionState.Active;
            Platform.BrokerEvent(true); Observe();
            Session.Handle(BridgeProtocol.Serialize(new HelloMsg { Platform = "cTrader", Account = Account, HistoryCapability = HistoryPage.Capability,
                ScopedCapability = ScopeFrame.Capability, Label = label, BrokerEpoch = Platform.BrokerEpoch, BrokerConnected = true,
                DeploymentInventory = new() { new(label, true, 0, false) } }));
            Session.ConfigureScope(baseline?.BaselineId ?? Guid.NewGuid());
            Frame = Platform.Capture(Session.BaselineId, label, Session.Id);
            Live(Frame);
            Session.ScopedCompleted += (_, snapshot) => Receipt = snapshot.Receipt;
            Session.RequestHistoryAsync(label).GetAwaiter().GetResult();
            Session.Handle(BridgeProtocol.Serialize(new HistoryPage { RequestId = Session.HistoryRequestId, Pages = 1, Total = 0,
                CapturedAt = BridgeProtocol.ToUnixMs(DateTime.UtcNow), Account = Account, Positions = new() { Position }, PendingOrders = new(),
                Scoped = Frame, OpeningObservations = new(), RecoveredResults = new() }));
            Adapter = new SessionAdapter(() => null); Adapter.Attach(Session);
            Assert.NotNull(Receipt); Assert.True(Session.IsCurrent(Receipt));
            Authorization = new ScopedPartialDispatchAuthorization(Receipt, r =>
            {
                if (Interlocked.Increment(ref AuthChecks) == 2) Queued.TrySetResult();
                return r == Request && Tick < TimeSpan.FromSeconds(30);
            });
            Journal = new CommandJournal(null, saved => { Saved = saved; DuringFlush?.Invoke(); });
            Transport.Execute = c => c.Units == null
                ? Journal.RunFullClose(c.Id, () => { if (Account.Number != accountNumber || Account.IsLive) return new ResultMsg { Id = c.Id, Status = "rejected" };
                    FullCalls++; return new ResultMsg { Id = c.Id, Status = "filled" }; })
                : Journal.Run(c.Id, BridgeProtocol.Serialize(c), null, () =>
                {
                    // Same production final fence used by ApexBridge.OnClose, AFTER Run flush.
                    bool ran = Platform.TryRun(c.Scoped, () =>
                    {
                        Observe();
                        return Account.Number == accountNumber && !Account.IsLive && Account.Hedging && Account.Broker == broker && Account.Currency == currency
                            && c.Scoped?.BaselineId == Session.BaselineId && c.Scoped.Label == label && c.Scoped.TransportGeneration == Session.Id;
                    }, () => PartialCalls++);
                    var result = new ResultMsg { Id = c.Id, Status = ran ? "filled" : "rejected", Error = ran ? null : "NoTradingPermission" };
                    if (ran)
                    {
                        result.ReductionFill = new ReductionFillEvidence { ActionId = c.Id, PositionId = c.PositionId, SignalId = c.SignalId!, Label = label,
                            Symbol = "US500", BeforeUnits = 2, ClosedUnits = 1, RemainingUnits = 1 };
                        Journal.RecordReductionFill(c.Id, result.ReductionFill);
                    }
                    return result;
                });
        }
        internal void Observe() => Platform.Observe(new[] { Position }, Array.Empty<PendingOrderDto>());
        internal void Live(ScopeFrame f) => Session.Handle(BridgeProtocol.Serialize(new PositionsMsg { Items = new() { Position }, PendingOrders = new(), Scoped = f with { SnapshotId = "" } }));
        internal OrderOutcome Send() => ((IScopedPartialDispatchGateway)Adapter).ClosePartialScoped(Request, Authorization);
        internal void ExpireOriginalCapture()
        {
            var captures = (Dictionary<string, (ScopeFrame frame, long tick)>)typeof(ScopedPlatformState)
                .GetField("captures", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Platform)!;
            captures[Frame.SnapshotId] = (Frame, Stopwatch.GetTimestamp() - 31 * Stopwatch.Frequency);
        }
    }

    [Theory]
    [InlineData("expiry")][InlineData("epoch")][InlineData("position")][InlineData("pending")][InlineData("protection")][InlineData("transport")]
    public async Task Fix1_PartialMustReauthorizeAfterWaitingForTransportLock(string fault)
    {
        var r = new Rig();
        var gate = (SemaphoreSlim)typeof(BridgeSession).GetField("_sendLock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(r.Session)!;
        await gate.WaitAsync();
        var task = Task.Run(r.Send);
        await r.Queued.Task.WaitAsync(TimeSpan.FromSeconds(5)); // actual authorized path has entered RequestAsync
        if (fault == "expiry") r.Tick = TimeSpan.FromSeconds(30);
        if (fault == "epoch") r.Session.Handle(BridgeProtocol.Serialize(new BrokerStateMsg { BrokerEpoch = "epoch-next", BrokerConnected = true }));
        if (fault == "position") { r.Position.Units = 1.5; r.Live(r.Frame with { PositionRevision = 2 }); }
        if (fault == "pending") r.Live(r.Frame with { PendingOrderRevision = 2 });
        if (fault == "protection") { r.Position.Sl = 3991; r.Live(r.Frame with { PositionRevision = 2 }); }
        if (fault == "transport") r.Adapter.Detach(r.Session);
        gate.Release();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(r.Transport.Closes); Assert.Equal(0, r.PartialCalls);
        Assert.Equal(OrderStatus.Uncertain, result.Status); // no durable no-send result, no retry
        r.Adapter.Attach(r.Session);
        Assert.Equal(OrderStatus.Filled, r.Adapter.ClosePosition(42, null).Status); Assert.Equal(1, r.FullCalls);
    }

    [Fact]
    public void Fix1_PartialMustCarryOriginalCaptureOnWire()
    {
        var r = new Rig(); Assert.Equal(OrderStatus.Filled, r.Send().Status);
        Assert.Equal(r.Frame, Assert.Single(r.Transport.Closes).Scoped);
        Assert.Equal(1, r.PartialCalls);
        // Replay sees the positive reduction checkpoint and never invokes the delegate again.
        Assert.Equal(OrderStatus.Filled, r.Send().Status); Assert.Equal(1, r.PartialCalls);
        var restart = new CommandJournal(r.Saved, _ => throw new Exception("lookup is read-only"));
        Assert.NotNull(restart.Lookup("partial")!.ReductionFill);
        Assert.Equal(OrderStatus.Filled, ProtocolResult.ToOutcome(restart.Run("partial", BridgeProtocol.Serialize(r.Transport.Closes[0]), null,
            () => throw new Exception("replay must not send"))).Status);
    }

    [Theory]
    [InlineData("expiry")][InlineData("epoch")][InlineData("position")][InlineData("pending")][InlineData("protection")]
    [InlineData("baseline")][InlineData("label")][InlineData("transport")][InlineData("account")]
    public void Fix1_PartialFinalFenceRunsAfterJournalFlush(string fault)
    {
        var r = new Rig(); bool flushed = false;
        r.DuringFlush = () =>
        {
            if (flushed) return; flushed = true;
            Assert.Equal(0, r.PartialCalls);
            var command = Assert.Single(r.Transport.Closes);
            if (fault == "expiry") r.ExpireOriginalCapture();
            if (fault == "epoch") { r.Platform.BrokerEvent(false); r.Platform.BrokerEvent(true); }
            if (fault == "position") { r.Position.Units = 1.5; r.Observe(); }
            if (fault == "pending") r.Platform.Observe(new[] { r.Position }, new[] { new PendingOrderDto(88, "foreign") });
            if (fault == "protection") r.Position.Sl = 3991; // picked up by safe() Observe and second fence check
            if (fault == "baseline") command.Scoped = command.Scoped! with { BaselineId = Guid.NewGuid() };
            if (fault == "label") command.Scoped = command.Scoped! with { Label = "foreign" };
            if (fault == "transport") command.Scoped = command.Scoped! with { TransportGeneration = "reconnected" };
            if (fault == "account") r.Account.Number++;
        };
        Assert.Equal(OrderStatus.Rejected, r.Send().Status); Assert.True(flushed); Assert.Equal(0, r.PartialCalls);
        Assert.Null(r.Journal.Lookup("partial")!.ReductionFill);
        Assert.Equal(OrderStatus.Rejected, r.Send().Status); Assert.Equal(0, r.PartialCalls); // replay never gets a new window
        Assert.Equal(fault == "account" ? OrderStatus.Rejected : OrderStatus.Filled, r.Adapter.ClosePosition(42, null).Status);
        Assert.Equal(fault == "account" ? 0 : 1, r.FullCalls);
    }

    [Fact]
    public void Fix1_GenericPartialCannotBypassAuthorizationAndFullCloseSurvivesStorageFault()
    {
        var r = new Rig();
        Assert.Equal(OrderStatus.Rejected, r.Adapter.ClosePartial("bypass", 42, "s1", 2, 1).Status);
        Assert.Equal(OrderStatus.Rejected, r.Adapter.ClosePosition(42, 1).Status); Assert.Empty(r.Transport.Closes);
        var wire = r.Session.Request(new CloseCmd { Id = "generic-wire", PositionId = 42, SignalId = "s1", ObservedUnits = 2, Units = 1 },
            "generic-wire", TimeSpan.FromSeconds(2));
        Assert.True(wire.sent); Assert.Equal("rejected", BridgeProtocol.Deserialize<ResultMsg>(wire.response!)!.Status);
        Assert.Equal(0, r.PartialCalls); // even a raw generic wire command cannot bypass the SDK capture fence
        r.DuringFlush = () => throw new System.IO.IOException("ambiguous flush");
        Assert.Equal(OrderStatus.Rejected, r.Send().Status); Assert.Equal(0, r.PartialCalls);
        Assert.Equal(OrderStatus.Filled, r.Adapter.ClosePosition(42, null).Status); Assert.Equal(1, r.FullCalls);
    }
}
