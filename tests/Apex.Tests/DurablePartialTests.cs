using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Apex.Core.Engine;
using Apex.Core.Model;
using Xunit;

namespace Apex.Tests;

public sealed partial class ScopedRecoveryTests
{
    static bool Partial(Fixture f, double units, DateTime? now = null)
    {
        return f.Engine.RequestPartialClose(f.Broker.GetPositions().Single(), units, now ?? Now);
    }
    static string PartialStage(Fixture f) => JsonDocument.Parse(f.Store.Get(f.Key)!).RootElement.GetProperty("Plans")[0].GetProperty("Partial").GetProperty("Stage").GetString()!;
    static Fixture PartialFixture()
    {
        var f = new Fixture(); f.Establish(); f.Send(); f.Broker.Positions.Add(f.Position(2));
        Assert.True(f.Engine.ReconcileScoped(f.Receipt(f.Position(2)), f.Authority, Now));
        return f;
    }

    [Theory]
    [InlineData("healthy")][InlineData("expiry")][InlineData("currentness")][InlineData("account")][InlineData("request")]
    public void Fix1_CoreIssuesOnlyOriginalReservedPartialAuthorization(string fault)
    {
        var f = PartialFixture(); int calls = 0;
        f.Tick = TimeSpan.FromSeconds(29); // already almost expired; dispatch cannot start another 30s window
        f.Broker.OnScopedPartial = (request, authorization) =>
        {
            Assert.Equal("Reserved", PartialStage(f));
            Assert.Equal(f.State.Plans[0].Partial!.ActionId, request.ActionId);
            Assert.True(authorization.IsCurrent(request));
            Assert.Equal(f.State.Plans[0].Partial!.ObservationSequence, authorization.Receipt.Sequence);
            if (fault == "expiry") f.Tick = TimeSpan.FromSeconds(30);
            if (fault == "currentness") f.Current = false;
            if (fault == "account") f.Broker.Live = true;
            var actual = fault == "request" ? request with { Units = 0.5 } : request;
            if (authorization.IsCurrent(actual)) calls++;
            return OrderOutcome.Uncertain(BrokerErrorKind.Timeout, "no positive durable result");
        };
        Assert.True(Partial(f, 1)); Assert.Equal(fault == "healthy" ? 1 : 0, calls);
        Assert.Equal("AwaitingEvidence", PartialStage(f));
        Assert.False(Partial(f, 1)); // never resend this unknown/unsent reservation
        f.Restart(); Assert.False(Partial(f, 1));
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async System.Threading.Tasks.Task Fix1_ActualCoreReservedAuthorizationSurvivesThroughDesktopWithoutRenewal(bool expire)
    {
        var f = PartialFixture();
        var r = new PartialDispatchFenceTests.Rig(f.Proposal, f.Position(2));
        var now = DateTime.UtcNow; f.Broker.Now = now;
        Assert.True(f.Engine.ReconcileScoped(r.Receipt with { Sequence = ++f.Sequence }, f.Authority, now));
        f.Tick = TimeSpan.FromSeconds(29);
        f.Broker.OnScopedPartial = (request, authorization) =>
            ((IScopedPartialDispatchGateway)r.Adapter).ClosePartialScoped(request, authorization);
        var gate = (System.Threading.SemaphoreSlim)typeof(Apex.Desktop.Bridge.BridgeSession)
            .GetField("_sendLock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(r.Session)!;
        var pending = (System.Collections.Concurrent.ConcurrentDictionary<string, System.Threading.Tasks.TaskCompletionSource<string>>)
            typeof(Apex.Desktop.Bridge.BridgeSession).GetField("_pending", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(r.Session)!;
        await gate.WaitAsync();
        var task = System.Threading.Tasks.Task.Run(() => Partial(f, 1, now));
        try
        {
            Assert.True(System.Threading.SpinWait.SpinUntil(() => !pending.IsEmpty, TimeSpan.FromSeconds(5)), "actual authorized Core request must reach transport scheduling");
            Assert.Equal("Reserved", PartialStage(f));
            if (expire) f.Tick = TimeSpan.FromSeconds(30);
        }
        finally { gate.Release(); }
        Assert.True(await task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(expire ? 0 : 1, r.PartialCalls); Assert.Equal(expire ? 0 : 1, r.Transport.Closes.Count);
        Assert.Equal("AwaitingEvidence", PartialStage(f)); Assert.False(Partial(f, 1, now));
        if (!expire)
        {
            Assert.Equal(r.Frame, r.Transport.Closes[0].Scoped);
            Assert.Equal(f.State.Plans[0].Partial!.ActionId, r.Transport.Closes[0].Id);
            Assert.NotNull(f.State.Plans[0].Partial!.Confirmation);
        }
    }

    [Fact]
    public void Task2B_PartialRejectionPersistsIntentBeforeSendAndRequiresFreshBackoffRetry()
    {
        var f = PartialFixture(); int sends = 0;
        var actions = new System.Collections.Generic.List<string>();
        f.Broker.OnPartial = (action, id, signal, before, units) =>
        {
            actions.Add(action); Assert.Equal("s1", signal); Assert.Equal(2, before);
            sends++; Assert.Equal(42, id); Assert.Equal(1, units);
            Assert.Equal("Reserved", PartialStage(f)); Assert.False(f.State.Plans[0].PartialDone);
            return OrderOutcome.Rejected(BrokerErrorKind.MarketClosed, "no mutation");
        };
        Assert.True(Partial(f, 1)); Assert.Equal("Rejected", PartialStage(f));
        Assert.False(Partial(f, 1)); Assert.Equal(1, sends);
        f.Restart(); Assert.False(Partial(f, 1)); // no fresh receipt
        Assert.True(f.Engine.ReconcileScoped(f.Receipt(f.Position(2)), f.Authority, Now.AddSeconds(20)));
        Assert.True(Partial(f, 1, Now.AddSeconds(20))); Assert.Equal(2, sends);
        Assert.NotEqual(actions[0], actions[1]); Assert.Equal(2, f.State.Plans[0].Partial!.Attempt);
        Assert.False(f.State.Plans[0].PartialDone);
    }

    static OrderOutcome PartialFill(Fixture f, string action, double before = 2, double close = 1, double after = 1) => new()
    {
        Status = OrderStatus.Filled, PositionId = 42,
        ReductionFill = new ReductionFillEvidence { ActionId = action, PositionId = 42, SignalId = "s1", Label = f.Request.Label,
            Symbol = "US500", BeforeUnits = before, ClosedUnits = close, RemainingUnits = after }
    };

    [Theory]
    [InlineData("reserved")][InlineData("sent")][InlineData("result-before-save")][InlineData("result-save-ambiguous")][InlineData("confirmation-before-save")]
    public void Task2B_RestartBoundariesNeverResendAndPositiveWitnessNeedsClosingUnion(string boundary)
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "partial-" + Guid.NewGuid().ToString("N"));
        try
        {
            var f = new Fixture(new Apex.Desktop.Services.FileStore(dir)); f.Establish(); f.Send(); f.Broker.Positions.Add(f.Position(2));
            Assert.True(f.Engine.ReconcileScoped(f.Receipt(f.Position(2)), f.Authority, Now));
            int sends = 0;
            if (boundary == "reserved") f.Store.AfterWrite = () => throw new System.IO.IOException("lost reserve ack");
            f.Broker.OnPartial = (action, id, signal, before, close) =>
            {
                sends++; Assert.Equal("Reserved", PartialStage(f));
                f.Broker.Positions.Clear(); f.Broker.Positions.Add(f.Position(1));
                var outcome = PartialFill(f, action); f.Broker.PartialResults[action] = outcome;
                if (boundary == "sent") throw new System.IO.IOException("lost send response");
                if (boundary == "result-before-save") f.Store.Fail = true;
                if (boundary == "result-save-ambiguous") f.Store.AfterWrite = () => throw new System.IO.IOException("lost result ack");
                return outcome;
            };
            Partial(f, 1); Assert.Equal(boundary == "reserved" ? 0 : 1, sends);
            f.Store.Fail = false; f.Store.AfterWrite = null; f.Restart();
            var position = f.Position(boundary == "reserved" ? 2 : 1);
            f.Engine.ReconcileScoped(f.Receipt(position), f.Authority, Now);
            Assert.False(f.State.Plans[0].PartialDone); // witness without economic union cannot complete
            Assert.False(Partial(f, 1));
            if (boundary != "reserved")
            {
                f.Broker.ClosedDeals.Add(f.Deal(1, 1));
                if (boundary == "confirmation-before-save") f.Store.AfterWrite = () => throw new System.IO.IOException("lost confirmation ack");
                f.Engine.ReconcileScoped(f.Receipt(position), f.Authority, Now);
                f.Store.AfterWrite = null; f.Restart();
                Assert.True(f.State.Plans[0].PartialDone); Assert.Equal("Confirmed", PartialStage(f));
                Assert.True(f.Engine.ReconcileScoped(f.Receipt(position), f.Authority, Now));
                Assert.False(Partial(f, 1)); Assert.Equal(1, sends);
                Assert.Equal(1, f.Broker.GetPositions().Single().VolumeUnits); Assert.Equal(5, f.State.RealizedTotal);
                Assert.Single(f.State.ClosingDeals); Assert.Equal(0, f.State.TradesTotal);
            }
        }
        finally { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void Task2B_UncertainOrMismatchedWitnessCannotClaimManualReductionAndFullCloseSurvivesFault(bool mismatched)
    {
        var f = PartialFixture(); int sends = 0;
        f.Broker.OnPartial = (action, id, signal, before, close) =>
        {
            sends++; f.Broker.Positions.Clear(); f.Broker.Positions.Add(f.Position(1));
            return mismatched ? PartialFill(f, "unrelated-action") : OrderOutcome.Uncertain(BrokerErrorKind.Timeout, "unknown");
        };
        Partial(f, 1); f.Broker.ClosedDeals.Add(f.Deal(1, 1));
        f.Restart(); Assert.True(f.Engine.ReconcileScoped(f.Receipt(f.Position(1)), f.Authority, Now));
        Assert.False(f.State.Plans[0].PartialDone); Assert.False(Partial(f, 0.5)); Assert.Equal(1, sends);
        f.Store.Fail = true; int full = 0;
        f.Broker.OnClose = (id, units) => { Assert.Null(units); Assert.Equal(42, id); full++; return OrderOutcome.Filled(42, 4010); };
        f.Engine.CloseAllOwn(Now, "protective full"); Assert.Equal(1, full);
        f.Broker.Positions.Clear(); f.Broker.ClosedDeals.Add(f.Deal(2, 1)); f.Store.Fail = false; f.Restart();
        Assert.True(f.Engine.ReconcileScoped(f.Receipt(), f.Authority, Now));
        Assert.True(f.Engine.ReconcileScoped(f.Receipt(), f.Authority, Now));
        Assert.Empty(f.Engine.Plans); Assert.Equal(1, f.State.TradesTotal); Assert.Equal(10, f.State.RealizedTotal);
        Assert.Equal(2, f.State.ClosingDeals.Count);
    }

    [Theory]
    [InlineData("stale")][InlineData("foreign")][InlineData("real")][InlineData("conflict")][InlineData("expired-during-reserve")][InlineData("disk")]
    public void Task2B_PartialNegativeControlsNeverMutate(string fault)
    {
        var f = PartialFixture(); int sends = 0;
        f.Broker.OnPartial = (_, _, _, _, _) => { sends++; return OrderOutcome.Filled(42, 4010); };
        if (fault == "stale") f.Current = false;
        if (fault == "disk") f.Store.Fail = true;
        if (fault == "expired-during-reserve") f.Store.AfterWrite = () => f.Tick = TimeSpan.FromSeconds(31);
        if (fault == "foreign") { f.Broker.Positions.Clear(); f.Broker.Positions.Add(new PositionInfo { Id = 42, Label = "foreign", VolumeUnits = 2 }); }
        if (fault == "real") f.Broker.Live = true;
        if (fault == "conflict") { f.Broker.ClosedDeals.Add(f.Deal(1, 0.1)); f.Engine.ReconcileScoped(f.Receipt(f.Position(2)), f.Authority, Now); }
        Assert.False(Partial(f, 1)); Assert.Equal(0, sends);
    }

    [Fact]
    public void Task2B_MigratingAcceptedV4CertificatePreservesPassButRestartStillRequiresExplicitStart()
    {
        var f = new Fixture(); PrepareTechnical(f); CompleteTechnical(f);
        Assert.True(f.Engine.GetExecutionReadiness().Valid);
        var node = System.Text.Json.Nodes.JsonNode.Parse(f.Store.Get(f.Key)!)!;
        string certificate = node["ExecutionTest"]!.ToJsonString(); node["SchemaVersion"] = 4;
        f.Store.Set(f.Key, node.ToJsonString()); f.Restart();
        Assert.Equal(5, f.State.SchemaVersion); Assert.Equal(EngineMode.Stopped, f.Engine.Mode);
        Assert.True(f.Engine.GetExecutionReadiness().Valid);
        Assert.Equal(certificate, System.Text.Json.Nodes.JsonNode.Parse(f.Store.Get(f.Key)!)!["ExecutionTest"]!.ToJsonString());
        TechnicalTick(f, 46); Assert.Contains("uruchomiony", f.Engine.CmdStart()); Assert.Single(f.Broker.Orders);
    }

    [Fact]
    public void Task2B_LegacyPartialFlagsNeverInventSuccessOrAuthorizeRepeatAndMalformedV5Quarantines()
    {
        var f = PartialFixture();
        var node = System.Text.Json.Nodes.JsonNode.Parse(f.Store.Get(f.Key)!)!;
        node["SchemaVersion"] = 4; node["Plans"]![0]!["PartialDone"] = true;
        f.Store.Set(f.Key, node.ToJsonString()); f.Restart();
        Assert.False(f.State.Plans[0].PartialDone); Assert.Equal("LegacyUnknown", PartialStage(f));
        Assert.False(Partial(f, 1));
        string original = f.Store.Get(f.Key)!;
        node = System.Text.Json.Nodes.JsonNode.Parse(original)!;
        node["Plans"]![0]!["Partial"]!.AsObject().Remove("ActionId");
        f.Store.Set(f.Key, node.ToJsonString()); string bad = f.Store.Get(f.Key)!;
        f.Restart(); f.Engine.SaveState(Now, true);
        Assert.Equal(bad, f.Store.Get(f.Key)); Assert.False(f.Engine.HistoryReady);
    }

    [Theory]
    [InlineData(0)][InlineData(2)][InlineData(1.95)]
    public void Task2B_MinimumSkipIsDurableTerminalNotFakeCompletion(double units)
    {
        var f = PartialFixture();
        Assert.False(Partial(f, units)); Assert.Equal("Skipped", PartialStage(f));
        f.Restart(); Assert.False(Partial(f, 1)); Assert.False(f.State.Plans[0].PartialDone);
    }
}
