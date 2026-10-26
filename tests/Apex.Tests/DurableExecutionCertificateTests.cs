using System;
using System.Linq;
using System.Text.Json;
using Apex.Core.Engine;
using Apex.Core.Management;
using Apex.Core.Model;
using Xunit;

namespace Apex.Tests;
public sealed partial class ScopedRecoveryTests
{
    [Fact]
    public void DurablePassMissingCertificateDeniesActualStrategyDispatchDespiteCurrentPermit()
    {
        var f = new Fixture(); f.Establish(); f.Engine.Config.IsBacktest = true;
        f.Broker.OnOpen = r => OrderOutcome.Filled(42, 4000, f.Fill(r));
        var result = f.Engine.SendAdmittedOpen(f.Request, new TradePlan { Strategy = StrategyId.IndexDipBuy, RiskAmount = 10 }, Now);
        Assert.Equal(OrderStatus.Rejected, result.Status); Assert.Empty(f.Broker.Orders);
    }

    static void PrepareTechnical(Fixture f, bool uncertain = false)
    {
        f.Establish(); f.Engine.SetUniverse(new[] { new UniverseEntry { Symbol = "US500", AssetClass = AssetClass.Index, BaseCurrency = "US500" } });
        f.Broker.OnOpen = r =>
        {
            var fill = f.Fill(r, r.VolumeUnits);
            f.Broker.Positions.Add(new PositionInfo { Id = 42, Symbol = r.Symbol, Label = r.Label, Comment = r.Comment,
                Direction = r.Direction, EntryPrice = fill.EntryPrice, EntryTimeUtc = fill.EntryTimeUtc, VolumeUnits = fill.InitialVolumeUnits,
                StopLoss = fill.EntryPrice - r.StopLossPips, TakeProfit = fill.EntryPrice + r.TakeProfitPips });
            return uncertain ? new OrderOutcome { Status = OrderStatus.Uncertain, PositionId = 42, OpeningFill = fill, Message = "uncertain original" }
                : OrderOutcome.Filled(42, fill.EntryPrice, fill);
        };
        Assert.Contains("rozpoczęty", f.Engine.CmdExecutionTest(Now, "US500"));
    }
    static void TechnicalTick(Fixture f, int seconds)
    {
        var at = Now.AddSeconds(seconds); f.Broker.Now = at; f.Tick = TimeSpan.FromSeconds(seconds);
        f.Engine.ReconcileScoped(f.Receipt(f.Broker.Positions.ToArray()) with { CapturedUtc = at, ReceivedUtc = at }, f.Authority, at);
        f.Engine.OnTimer(at);
    }
    static void CompleteTechnical(Fixture f)
    { TechnicalTick(f, 1); TechnicalTick(f, 22); TechnicalTick(f, 23); TechnicalTick(f, 44); TechnicalTick(f, 45); }

    [Fact]
    public void DurablePassActualEvidencePublishesOnlyAfterTerminalEconomicsThenPersistsAcrossRestart()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "certificate-" + Guid.NewGuid().ToString("N"));
        try
        {
            var f = new Fixture(new Apex.Desktop.Services.FileStore(dir)); PrepareTechnical(f);
            Assert.Equal(ExecutionTestOutcome.InProgress, f.State.ExecutionTest?.Outcome);
            TechnicalTick(f, 1); TechnicalTick(f, 22); TechnicalTick(f, 23); TechnicalTick(f, 44);
            Assert.False(f.Engine.GetExecutionReadiness().Valid); Assert.Equal(0, f.State.TradesTotal);
            TechnicalTick(f, 45);
            Assert.True(f.Engine.GetExecutionReadiness().Valid); Assert.Equal(ExecutionTestOutcome.Passed, f.State.ExecutionTest?.Outcome);
            Assert.Single(f.State.ExecutionTest!.ClosingDealIds); Assert.Single(f.State.OpenAdmissions); Assert.Equal(1, f.State.TradesTotal);
            Assert.Contains("uruchomiony", f.Engine.CmdStart());
            f.Restart(); Assert.Equal(EngineMode.Stopped, f.Engine.Mode); Assert.True(f.Engine.GetExecutionReadiness().Valid);
            TechnicalTick(f, 46); Assert.Equal(EngineMode.Stopped, f.Engine.Mode); Assert.Contains("uruchomiony", f.Engine.CmdStart());
            Assert.Single(f.Broker.Orders);
        }
        finally { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("missing-sl")][InlineData("incorrect-sl")][InlineData("incorrect-tp")]
    [InlineData("modify-unconfirmed")][InlineData("natural-close")][InlineData("manual-disappearance")]
    [InlineData("uncertain-close")][InlineData("late-deals")][InlineData("interrupted")][InlineData("restart")][InlineData("disk-fault")][InlineData("connection")][InlineData("uncertain-open")]
    public void DurablePassIncompleteEvidenceNeverCertifiesOrReanimates(string fault)
    {
        var f = new Fixture(); PrepareTechnical(f, fault == "uncertain-open");
        if (fault is "missing-sl" or "incorrect-sl" or "incorrect-tp")
        {
            var p = f.Broker.Positions.Single();
            f.Broker.Positions[0] = new PositionInfo { Id = p.Id, Symbol = p.Symbol, Label = p.Label, Comment = p.Comment,
                Direction = p.Direction, EntryPrice = p.EntryPrice, EntryTimeUtc = p.EntryTimeUtc, VolumeUnits = p.VolumeUnits,
                StopLoss = fault == "missing-sl" ? null : fault == "incorrect-sl" ? p.StopLoss - 10 : p.StopLoss,
                TakeProfit = fault == "incorrect-tp" ? p.TakeProfit + 10 : p.TakeProfit };
        }
        TechnicalTick(f, 1);
        if (fault == "interrupted") f.Engine.InvalidateScopedRuntime("test outage");
        if (fault == "restart") f.Restart();
        if (fault == "connection") f.Broker.Connected = false;
        if (fault is "natural-close" or "manual-disappearance") { if (fault == "natural-close") f.Broker.ClosePosition(42, null); else f.Broker.Positions.Clear(); }
        TechnicalTick(f, 22);
        if (fault == "connection") f.Broker.Connected = true;
        if (fault == "modify-unconfirmed")
        {
            var p = f.Broker.Positions.Single();
            f.Broker.Positions[0] = new PositionInfo { Id = p.Id, Symbol = p.Symbol, Label = p.Label, Comment = p.Comment,
                Direction = p.Direction, EntryPrice = p.EntryPrice, EntryTimeUtc = p.EntryTimeUtc, VolumeUnits = p.VolumeUnits,
                StopLoss = f.State.ExecutionTest!.InitialStop, TakeProfit = p.TakeProfit };
        }
        TechnicalTick(f, 23);
        if (fault == "uncertain-close") f.Broker.OnClose = (id, u) => { f.Broker.Positions.Clear(); return OrderOutcome.Uncertain(BrokerErrorKind.Timeout, "uncertain"); };
        if (fault == "disk-fault") f.Store.Fail = true;
        TechnicalTick(f, 44);
        var late = f.Broker.ClosedDeals.ToArray();
        if (fault == "late-deals") f.Broker.ClosedDeals.Clear();
        TechnicalTick(f, 45); TechnicalTick(f, 200);
        if (fault == "late-deals") { f.Broker.ClosedDeals.AddRange(late); TechnicalTick(f, 201); }
        Assert.False(f.Engine.GetExecutionReadiness().Valid); Assert.NotEqual(ExecutionTestOutcome.Passed, f.State.ExecutionTest?.Outcome);
        Assert.DoesNotContain("uruchomiony", f.Engine.CmdStart()); Assert.Single(f.Broker.Orders);
        if (fault is "restart" or "interrupted" or "connection") Assert.Equal(ExecutionTestOutcome.Interrupted, f.State.ExecutionTest?.Outcome);
    }

    [Theory]
    [InlineData("AccountNumber")][InlineData("Broker")][InlineData("Currency")][InlineData("IsLive")][InlineData("IsHedging")]
    [InlineData("Platform")][InlineData("Label")][InlineData("ExecutionPath")][InlineData("SafetyVersion")]
    [InlineData("BaselineId")][InlineData("ProtocolVersion")][InlineData("TestVersion")][InlineData("ScopeVersion")]
    [InlineData("missing-field")][InlineData("missing-record")][InlineData("wrong-protection")][InlineData("missing-deal")]
    public void DurablePassBindingAndMalformedStateCannotUnlockOrEraseEvidence(string field)
    {
        var f = new Fixture(); PrepareTechnical(f); CompleteTechnical(f); Assert.True(f.Engine.GetExecutionReadiness().Valid);
        var root = System.Text.Json.Nodes.JsonNode.Parse(f.Store.Get(f.Key)!)!; var c = root["ExecutionTest"]!;
        if (field == "missing-field") c.AsObject().Remove("ModifyObservedUtc");
        else if (field == "missing-record") root.AsObject().Remove("ExecutionTest");
        else if (field == "wrong-protection") c["InitialStop"] = 1;
        else if (field == "missing-deal") c["ClosingDealIds"] = new System.Text.Json.Nodes.JsonArray();
        else if (field is "BaselineId" or "ProtocolVersion" or "TestVersion" or "ScopeVersion")
        {
            if (field == "BaselineId") c[field] = Guid.NewGuid();
            else if (field == "ProtocolVersion") c[field] = 9;
            else c[field] = "old-version";
        }
        else
        {
            var i = c["Identity"]!;
            if (field == "AccountNumber") i[field] = 999;
            else if (field is "IsLive" or "IsHedging") i[field] = field == "IsLive";
            else i[field] = field == "ExecutionPath" ? "Native" : field == "SafetyVersion" ? "old-version" : "OTHER";
        }
        string bytes = root.ToJsonString(); f.Store.Set(f.Key, bytes); f.Restart();
        Assert.False(f.Engine.GetExecutionReadiness().Valid); Assert.DoesNotContain("uruchomiony", f.Engine.CmdStart());
        // Old but structurally valid versions can be retained/migrated; malformed evidence is quarantined verbatim.
        if (field is not ("ProtocolVersion" or "TestVersion" or "ScopeVersion")) Assert.Equal(bytes, f.Store.Get(f.Key));
    }

    [Theory]
    [InlineData("before-write")][InlineData("after-write")]
    public void DurablePassPublicationFaultCannotPublishInProcessSuccess(string stage)
    {
        var f = new Fixture(); PrepareTechnical(f); TechnicalTick(f, 1); TechnicalTick(f, 22); TechnicalTick(f, 23); TechnicalTick(f, 44);
        if (stage == "before-write") f.Store.Fail = true;
        else f.Store.AfterWrite = () => { if (f.State.ExecutionTest?.Outcome == ExecutionTestOutcome.Passed) throw new System.IO.IOException("publication flush ambiguous"); };
        TechnicalTick(f, 45);
        Assert.False(f.Engine.GetExecutionReadiness().Valid); Assert.DoesNotContain(f.Engine.LastExecutionTest, l => l.Contains("trwały PASS"));
        Assert.DoesNotContain("uruchomiony", f.Engine.CmdStart()); Assert.Single(f.Broker.Closes);
    }

    [Fact]
    public void DurablePassTrustedSimulationNeverCreatesProductionCertificateAndProtectiveCloseNeedsNone()
    {
        var r = new EngineRig(configure: c => c.IsBacktest = true); Assert.Equal(EngineMode.Running, r.Engine.Mode);
        Assert.Contains("rozpoczęty", r.Engine.CmdExecutionTest(r.Now, "US500"));
        r.Tick(); r.Tick(21); r.Tick(); r.Tick(21); r.Tick();
        Assert.False(r.Engine.GetExecutionReadiness().Valid); Assert.Equal("SimulationOnly", r.Engine.GetExecutionReadiness().Status);
        var f = new Fixture(); PrepareTechnical(f); f.Store.Fail = true;
        f.Engine.CmdCloseAllOwn(Now.AddSeconds(1)); Assert.Single(f.Broker.Closes); Assert.False(f.Engine.GetExecutionReadiness().Valid);
    }

    [Fact]
    public void DurablePassDelayedDealsWithinActiveDeadlineCanCertifyButDispatchStillNeedsCurrentScope()
    {
        var f = new Fixture(); PrepareTechnical(f); TechnicalTick(f, 1); TechnicalTick(f, 22); TechnicalTick(f, 23); TechnicalTick(f, 44);
        var deals = f.Broker.ClosedDeals.ToArray(); f.Broker.ClosedDeals.Clear(); TechnicalTick(f, 45);
        Assert.False(f.Engine.GetExecutionReadiness().Valid); Assert.Equal(ExecutionTestOutcome.InProgress, f.State.ExecutionTest?.Outcome);
        f.Broker.ClosedDeals.AddRange(deals); TechnicalTick(f, 46); Assert.True(f.Engine.GetExecutionReadiness().Valid);
        f.Engine.Config.IsBacktest = true; f.Current = false;
        var denied = f.Engine.SendAdmittedOpen(f.Request, new TradePlan { Strategy = StrategyId.IndexDipBuy, RiskAmount = 10 }, Now.AddSeconds(46));
        Assert.Equal(OrderStatus.Rejected, denied.Status); Assert.Single(f.Broker.Orders);
        f.Current = true; TechnicalTick(f, 47);
        f.Broker.OnOpen = r => {
            var old = f.Fill(r); var d = old.OpeningDeals[0];
            var fill = new OpeningFillEvidence(old.Request, 43, old.InitialVolumeUnits, old.VolumeStep, old.EntryPrice, old.EntryTimeUtc,
                new[] { d with { PositionId = 43, DealId = 1234 } });
            return OrderOutcome.Filled(43, fill.EntryPrice, fill);
        };
        Assert.Equal(OrderStatus.Filled, f.Engine.SendAdmittedOpen(f.Request, new TradePlan { Strategy = StrategyId.IndexDipBuy, RiskAmount = 10 }, Now.AddSeconds(47)).Status);
        Assert.Equal(2, f.Broker.Orders.Count);
    }

    [Fact]
    public void DurablePassLateEconomicsOnFirstTimerAfterDeadlineCannotPublishPass()
    {
        var f = new Fixture(); PrepareTechnical(f); TechnicalTick(f, 1); TechnicalTick(f, 22); TechnicalTick(f, 23); TechnicalTick(f, 44);
        var deals = f.Broker.ClosedDeals.ToArray(); f.Broker.ClosedDeals.Clear(); TechnicalTick(f, 45);
        f.Broker.ClosedDeals.AddRange(deals); TechnicalTick(f, 200);
        Assert.Equal(1, f.State.TradesTotal); Assert.False(f.Engine.GetExecutionReadiness().Valid);
        Assert.Equal(ExecutionTestOutcome.Failed, f.State.ExecutionTest?.Outcome);
    }

    [Fact]
    public void DurablePassExplicitRetestStopsPreviouslyRunningEngineAndCannotAutomaticallyResume()
    {
        var f = new Fixture(); PrepareTechnical(f); CompleteTechnical(f); Assert.Contains("uruchomiony", f.Engine.CmdStart());
        TechnicalTick(f, 120); f.Broker.NextId = 43;
        f.Broker.OnOpen = r => OrderOutcome.Rejected(BrokerErrorKind.NoMoney, "test rejection");
        f.Engine.CmdExecutionTest(f.Broker.Now, "US500");
        Assert.Equal(EngineMode.Stopped, f.Engine.Mode); Assert.False(f.Engine.GetExecutionReadiness().Valid);
    }

    [Fact]
    public void DurablePassRestartClosesLateAppearingOrphanEvenWithPersistedPlanWithoutCertifying()
    {
        var f = new Fixture(); PrepareTechnical(f); TechnicalTick(f, 1);
        var original = f.Broker.Positions.Single(); f.Broker.Positions.Clear(); f.Restart();
        f.Broker.Positions.Add(original); TechnicalTick(f, 2);
        Assert.Single(f.Broker.Closes); Assert.False(f.Engine.GetExecutionReadiness().Valid);
        Assert.Equal(ExecutionTestOutcome.Interrupted, f.State.ExecutionTest?.Outcome);
    }

    [Fact]
    public void DurablePassSchemaMigrationNeverInfersCertificateFromLegacySuccess()
    {
        var f = new Fixture(); f.Establish();
        var old = System.Text.Json.Nodes.JsonNode.Parse(f.Store.Get(f.Key)!)!;
        old["SchemaVersion"] = 3; old["LastExecutionTest"] = "TEST ZALICZONY";
        f.Store.Set(f.Key, old.ToJsonString()); f.Restart();
        Assert.Equal(4, f.State.SchemaVersion);
        using var document = JsonDocument.Parse(f.Store.Get(f.Key)!);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("ExecutionTest").ValueKind);
        Assert.Contains("PASS", f.Engine.CmdStart());
    }
}
