using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Apex.Core.Engine;
using Apex.Core.Execution;
using Apex.Core.Model;
using Xunit;

namespace Apex.Tests;

public sealed class ScopedRecoveryTests
{
    [Fact]
    public void RuntimeSynchronizationConsumesCurrentScopeInsteadOfUniversalCoverageAndStartStillNeedsTask2()
    {
        var f = new Fixture(); f.Establish();
        f.Engine.OnTimer(Now);
        Assert.True(f.Engine.HistoryReady);
        Assert.Contains("PASS", f.Engine.CmdStart());
        Assert.Equal(EngineMode.Stopped, f.Engine.Mode);
        f.Current = false; f.Engine.OnTimer(Now);
        Assert.False(f.Engine.HistoryReady);
    }

    [Fact]
    public void HarmlessDurableSaveDoesNotPermanentlyLoseOriginalCurrentReceipt()
    {
        var f = new Fixture(); f.Establish(); f.Engine.SaveState(Now, true);
        f.Engine.OnTimer(Now); Assert.True(f.Engine.HistoryReady);
        f.Tick = TimeSpan.FromSeconds(31); f.Engine.OnTimer(Now.AddSeconds(31)); Assert.False(f.Engine.HistoryReady);
    }

    [Fact]
    public void PendingVisibleOpeningWaitsForReadOnlyWitnessInsteadOfAdoptingRemainingOrLatchingCollision()
    {
        var f = new Fixture(); f.Establish(); OrderRequest? sent = null;
        f.Broker.OnOpen = r => { sent = r; return OrderOutcome.Uncertain(BrokerErrorKind.Timeout, "missing result"); };
        Assert.Equal(OrderStatus.Uncertain, f.Engine.SendAdmittedOpen(f.Request, new Apex.Core.Management.TradePlan { Strategy = StrategyId.ExecutionTest, RiskAmount = 10 }, Now).Status);
        f.Broker.Positions.Add(f.Position(1));
        Assert.False(f.Engine.ReconcileScoped(f.Receipt(f.Position(1)), f.Authority, Now));
        f.Engine.OnTimer(Now);
        Assert.NotEqual(ScopedRecoveryStatus.Conflict, f.Engine.ScopedStatus); Assert.Empty(f.Engine.Plans);
        Assert.True(f.Engine.RecoverOpeningOutcome(sent!.ExecutionId, OrderOutcome.Filled(42, 4000, f.Fill(sent)), Now));
        Assert.False(f.Engine.ReconcileScoped(f.Receipt(f.Position(1)), f.Authority, Now)); // missing earlier partial
        f.Broker.ClosedDeals.Add(f.Deal(1, 1)); Assert.True(f.Engine.ReconcileScoped(f.Receipt(f.Position(1)), f.Authority, Now));
        Assert.Equal(2, f.State.OpenAdmissions[0].Fill!.InitialVolumeUnits); Assert.Single(f.Broker.Orders);
    }

    [Fact]
    public void RegularScopedTimerSavePreservesUsablePermitToOriginalDeadline()
    {
        var f = new Fixture(); f.Establish(); f.Tick = TimeSpan.FromSeconds(6);
        f.Engine.OnTimer(Now.AddSeconds(6)); Assert.NotNull(f.Engine.GetScopedPermit(f.Authority, Now.AddSeconds(6)));
        f.Tick = TimeSpan.FromSeconds(30); f.Engine.OnTimer(Now.AddSeconds(30)); Assert.Null(f.Engine.GetScopedPermit(f.Authority, Now.AddSeconds(30)));
    }

    [Fact]
    public void ProposedScopeGuidesReadOnlyRePairInsteadOfClaimingRuntimeIsMissing()
    {
        var f = new Fixture(); Assert.True(f.Engine.ReserveBaseline(f.Proposal, Now));
        Assert.Contains("Proposed", f.Engine.ScopedMessage); Assert.Contains(f.Proposal.Identity.Label, f.Engine.ScopedMessage);
        Assert.Null(f.Engine.GetScopedPermit(f.Authority, Now));
    }

    static readonly DateTime Now = RecoveryTests.Now;
    static readonly JsonSerializerOptions Json = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    sealed class Broker : FakeBroker
    {
        internal Func<OrderRequest, OrderOutcome>? OnOpen;
        public override OrderOutcome OpenMarket(OrderRequest r) { Orders.Add(r); return OnOpen!(r); }
        public override BrokerHistorySnapshot GetHistory() => new() { AccountNumber = AccountNumber, Generation = "transport", Complete = true, Positions = GetPositions(), Closed = ClosedDeals.ToArray() };
    }
    // Retain fixture data without exposing FakeBroker's trusted-simulation capability.
    sealed class OrdinaryGateway(Broker broker) : IBrokerGateway, IBrokerHistory
    {
        public bool IsConnected => broker.IsConnected;
        public AccountSnapshot GetAccount() => broker.GetAccount();
        public System.Collections.Generic.IReadOnlyList<PositionInfo> GetPositions() => broker.GetPositions();
        public BrokerHistorySnapshot GetHistory() => broker.GetHistory();
        public OrderOutcome OpenMarket(OrderRequest request) => broker.OpenMarket(request);
        public OrderOutcome ModifyStopLoss(long id, double sl) => broker.ModifyStopLoss(id, sl);
        public OrderOutcome ClosePosition(long id, double? units) => broker.ClosePosition(id, units);
        public double EstimateMargin(string symbol, TradeDirection direction, double units) => broker.EstimateMargin(symbol, direction, units);
    }
    sealed class FileProjection(string dir) : IRecoveryJournal
    {
        internal readonly Apex.Desktop.Services.FileJournal Inner = new(dir);
        internal Action? DuringReconcile;
        internal Action? DuringExport;
        internal bool FailReconcile;
        internal bool Healthy = true;
        public bool IsHealthy => Healthy && Inner.IsHealthy;
        public System.Collections.Generic.IReadOnlyList<Apex.Core.Performance.TradeRecord> ClosedRecords => Inner.ClosedRecords;
        public void Reconcile(System.Collections.Generic.IReadOnlyList<Apex.Core.Performance.TradeRecord> authority)
        {
            DuringReconcile?.Invoke();
            if (FailReconcile) throw new IOException("projection reconcile failed");
            Inner.Reconcile(authority);
        }
        public void TradeClosed(Apex.Core.Performance.TradeRecord record) { DuringExport?.Invoke(); Inner.TradeClosed(record); }
        public void TradeOpened(Apex.Core.Management.TradePlan plan, double units, string details) => Inner.TradeOpened(plan, units, details);
        public void Signal(string line) => Inner.Signal(line);
        public void Event(string line) => Inner.Event(line);
    }
    sealed class FaultStore : IKeyValueStore
    {
        internal IKeyValueStore Inner = new MemoryStore();
        internal bool Fail;
        internal Action? AfterWrite;
        public string? Get(string key) => Inner.Get(key);
        public void Set(string key, string value) { if (Fail) throw new IOException("save fault"); Inner.Set(key, value); AfterWrite?.Invoke(); }
        public void Flush() { if (Fail) throw new IOException("flush fault"); }
    }
    sealed class Fixture
    {
        internal readonly Broker Broker = new() { Now = Now };
        internal readonly FaultStore Store = new();
        internal readonly RecoveryBaseline Proposal;
        internal TradingEngine Engine = null!;
        internal ScopedReceiptAuthority Authority = null!;
        internal TimeSpan Tick;
        internal bool Current = true;
        internal long Sequence;
        internal string Key => $"APEX {Proposal.Identity.Label} 123456";
        internal EngineState State => JsonSerializer.Deserialize<EngineState>(Store.Get(Key)!, Json)!;
        internal ITradeJournal Journal;
        internal Fixture(IKeyValueStore? store = null, ITradeJournal? journal = null)
        {
            if (store != null) Store.Inner = store;
            Journal = journal ?? new NullJournal();
            Broker.AddInstrument("US500", AssetClass.Index, 4000, 1, 1, 0.1, 0.1);
            Proposal = TradingEngine.CreateBaselineProposal(new RecoveryIdentity { Platform = "cTrader", Broker = "Pepperstone Test", AccountNumber = 123456,
                Currency = "USD", IsLive = false, IsHedging = true, ExecutionPath = RecoveryExecutionPath.DesktopBridge, SafetyVersion = ScopedRecoveryPolicy.SafetyVersion });
            Restart();
        }
        internal void Restart()
        {
            Engine = new TradingEngine(new EngineConfig { Label = Proposal.Identity.Label }, new Apex.Core.Adaptive.StrategyBook(Scenarios.DipOnlyProfile()), new OrdinaryGateway(Broker), Broker, new Apex.Core.Bridge.BarStore(), Store, new NullLog(), Journal);
            Engine.SetGuard(new Apex.Core.Safety.AccountGuardResult { Allowed = true }); Engine.Initialize(Now);
            Authority = new ScopedReceiptAuthority(Engine, _ => Current, () => Tick);
        }
        internal ScopedReceipt Receipt(params PositionInfo[] positions) => new()
        {
            Identity = Proposal.Identity, BaselineId = Proposal.BaselineId, ReceiptId = "receipt-" + ++Sequence, RequestId = "request-" + Sequence,
            TransportGeneration = "transport", BrokerGeneration = "broker", BrokerConnected = true, TransportComplete = true,
            CapturedUtc = Now, ReceivedUtc = Now, Sequence = Sequence, Positions = positions, PendingOrders = Array.Empty<PendingOrderObservation>(),
            Closed = Broker.ClosedDeals.ToArray(), OpeningObservations = Array.Empty<OpeningDealEvidence>(),
        };
        internal void Establish()
        { Assert.True(Engine.ReserveBaseline(Proposal, Now)); Assert.True(Engine.EstablishBaseline(Receipt(), Authority, Now)); }
        internal OrderRequest Request => new() { SignalId = "s1", Symbol = "US500", Direction = TradeDirection.Long, VolumeUnits = 2,
            Label = Proposal.Identity.Label, Comment = "APEX|ET|s1|v1", StopLossPips = 10, TakeProfitPips = 20 };
        internal OpeningFillEvidence Fill(OrderRequest r, double actual = 2, double step = 0.1) => new(new OpeningRequestEvidence
        { ExecutionId = r.ExecutionId, SignalId = r.SignalId, Symbol = r.Symbol, Direction = r.Direction, VolumeUnits = r.VolumeUnits, Label = r.Label,
            Comment = r.Comment, StopLossPips = r.StopLossPips, TakeProfitPips = r.TakeProfitPips, MaxSlippagePips = r.MaxSlippagePips },
            42, actual, step, 4000, Now, new[] { new OpeningDealEvidence { DealId = long.MaxValue - 1, OrderId = 1, PositionId = 42, VolumeUnits = actual,
                Price = 4000, TimeUtc = Now, Symbol = r.Symbol, Direction = r.Direction, Label = r.Label, Comment = r.Comment } });
        internal PositionInfo Position(double units) => new() { Id = 42, Symbol = "US500", Direction = TradeDirection.Long, Label = Request.Label,
            Comment = Request.Comment, EntryPrice = 4000, EntryTimeUtc = Now, VolumeUnits = units, StopLoss = 3990, TakeProfit = 4020 };
        internal ClosedPositionInfo Deal(long id, double units) => new() { PositionId = 42, ClosingDealId = id, Label = Request.Label, Comment = Request.Comment,
            Symbol = "US500", Direction = TradeDirection.Long, Volume = units, EntryPrice = 4000, EntryTimeUtc = Now, ExitPrice = 4010, ExitTimeUtc = Now.AddSeconds(1), NetProfit = 5 };
        internal OrderOutcome Send(double actual = 2)
        {
            Broker.OnOpen = r => { Assert.Single(State.OpenAdmissions); Assert.Equal(r.ExecutionId, State.OpenAdmissions[0].OperationId);
                Assert.Equal(r.VolumeUnits, State.OpenAdmissions[0].Request.VolumeUnits);
                Assert.Equal(0.1, State.OpenAdmissions[0].RequestedVolumeStep);
                using var risk = JsonDocument.Parse(State.OpenAdmissions[0].RiskInputsJson);
                Assert.Equal(123456, risk.RootElement.GetProperty("Account").GetProperty("Number").GetInt64());
                Assert.Equal("US500", risk.RootElement.GetProperty("Instrument").GetProperty("Name").GetString());
                Assert.True(risk.RootElement.TryGetProperty("Settings", out _));
                return OrderOutcome.Filled(42, 4000, Fill(r, actual)); };
            return Engine.SendAdmittedOpen(Request, new Apex.Core.Management.TradePlan { Strategy = StrategyId.ExecutionTest, RiskAmount = 10 }, Now);
        }
    }

    [Theory]
    [InlineData("establish", 31, false)][InlineData("establish", 1, true)]
    [InlineData("admit", 31, false)][InlineData("admit", 1, true)]
    [InlineData("account", 31, false)][InlineData("account", 1, true)]
    [InlineData("export", 31, false)][InlineData("export", 1, true)]
    [InlineData("reconcile", 31, false)][InlineData("reconcile", 1, true)]
    public void BlockingScopeWorkCannotRenewOriginalReceiptDeadline(string stage, int seconds, bool allowed)
    {
        string dir = Path.Combine(Path.GetTempPath(), "scope-deadline-" + Guid.NewGuid().ToString("N"));
        try
        {
            var projection = new FileProjection(Path.Combine(dir, "csv"));
            var f = new Fixture(new Apex.Desktop.Services.FileStore(Path.Combine(dir, "state")), projection);
            Assert.True(f.Engine.ReserveBaseline(f.Proposal, Now));
            Action delay = () => f.Tick = TimeSpan.FromSeconds(seconds); // stable broker, generations and revisions
            bool result;
            if (stage == "establish")
            {
                f.Store.AfterWrite = delay;
                result = f.Engine.EstablishBaseline(f.Receipt(), f.Authority, Now);
                Assert.Equal(BaselineStage.Established, f.State.Baseline!.Stage);
            }
            else
            {
                Assert.True(f.Engine.EstablishBaseline(f.Receipt(), f.Authority, Now));
                if (stage == "admit")
                {
                    f.Store.AfterWrite = delay;
                    Assert.Equal(allowed ? OrderStatus.Filled : OrderStatus.Rejected, f.Send().Status);
                    Assert.Equal(allowed ? AdmissionStage.Bound : AdmissionStage.Rejected, Assert.Single(f.State.OpenAdmissions).Stage);
                    Assert.Equal(allowed ? 1 : 0, f.Broker.Orders.Count);
                    Assert.Null(f.Engine.GetScopedPermit(f.Authority, Now));
                    return;
                }
                if (stage != "reconcile")
                {
                    Assert.Equal(OrderStatus.Filled, f.Send().Status);
                    f.Broker.Orders.Clear(); f.Broker.ClosedDeals.Add(f.Deal(1, 2));
                }
                if (stage == "account") f.Store.AfterWrite = delay;
                if (stage == "export") projection.DuringExport = delay;
                if (stage == "reconcile") projection.DuringReconcile = delay;
                result = f.Engine.ReconcileScoped(f.Receipt(), f.Authority, Now);
                if (stage != "reconcile")
                {
                    Assert.Equal(1, f.State.TradesTotal); Assert.Equal(5, f.State.RealizedTotal);
                    Assert.Empty(f.State.ClosedOutbox);
                    Assert.Equal(2, File.ReadAllLines(Path.Combine(dir, "csv", "transakcje.csv")).Length);
                }
            }
            Assert.Equal(allowed, result);
            Assert.Equal(allowed, f.Engine.GetScopedPermit(f.Authority, Now) != null);
            Assert.Empty(f.Broker.Orders);
            if (allowed)
            {
                f.Tick = TimeSpan.FromSeconds(30); // even a short commit must not extend the original 30-second window
                Assert.Null(f.Engine.GetScopedPermit(f.Authority, Now));
            }
            else
            {
                Assert.Equal(OrderStatus.Rejected, f.Send().Status); Assert.Empty(f.Broker.Orders);
            }
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Theory][InlineData("reserve")][InlineData("establish")][InlineData("established")]
    public void RealMalformedFileJournalCannotGrantScopedAuthorityWithEmptyOutbox(string stage)
    {
        string dir = Path.Combine(Path.GetTempPath(), "scope-corrupt-projection-" + Guid.NewGuid().ToString("N"));
        try
        {
            string csv = Path.Combine(dir, "csv"); Directory.CreateDirectory(csv);
            var store = new Apex.Desktop.Services.FileStore(Path.Combine(dir, "state"));
            var f = new Fixture(store, new Apex.Desktop.Services.FileJournal(csv));
            if (stage != "reserve") Assert.True(f.Engine.ReserveBaseline(f.Proposal, Now));
            if (stage == "established") Assert.True(f.Engine.EstablishBaseline(f.Receipt(), f.Authority, Now));
            string path = Path.Combine(csv, "transakcje.csv"), bad = "malformed header\noriginal evidence\n";
            File.WriteAllText(path, bad);
            var journal = new Apex.Desktop.Services.FileJournal(csv);
            Assert.False(journal.IsHealthy);
            // Same generated identity and durable state; restart through actual initialization and its successful save.
            f.Journal = journal; f.Restart(); f.Engine.SaveState(Now, true);
            Assert.Empty(f.State.ClosedOutbox);
            bool granted = stage switch {
                "reserve" => f.Engine.ReserveBaseline(f.Proposal, Now),
                "establish" => f.Engine.EstablishBaseline(f.Receipt(), f.Authority, Now),
                _ => f.Engine.ReconcileScoped(f.Receipt(), f.Authority, Now) };
            Assert.False(granted);
            Assert.Null(f.Engine.GetScopedPermit(f.Authority, Now));
            Assert.Equal(OrderStatus.Rejected, f.Send().Status); Assert.Empty(f.State.OpenAdmissions); Assert.Empty(f.Broker.Orders);
            Assert.Equal(stage == "reserve" ? null : stage == "establish" ? BaselineStage.Proposed : BaselineStage.Established, f.State.Baseline?.Stage);
            f.Broker.Positions.Add(f.Position(2)); f.Broker.Positions.Add(RecoveryTests.Open(99, "foreign"));
            f.Engine.CmdCloseAllOwn(Now);
            Assert.Equal(new long[] { 42 }, f.Broker.Closes);
            Assert.Equal(99, Assert.Single(f.Broker.Positions).Id);
            Assert.Equal(bad, File.ReadAllText(path)); Assert.False(journal.IsHealthy);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Theory][InlineData("reserve")][InlineData("reserve-commit")][InlineData("establish")][InlineData("consume")][InlineData("send")][InlineData("publish")]
    public void CurrentProjectionHealthIsRequiredAtEveryScopedRiskBoundary(string stage)
    {
        string dir = Path.Combine(Path.GetTempPath(), "scope-live-projection-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new FileProjection(Path.Combine(dir, "csv"));
            var f = new Fixture(new Apex.Desktop.Services.FileStore(Path.Combine(dir, "state")), journal);
            if (stage is not ("reserve" or "reserve-commit")) Assert.True(f.Engine.ReserveBaseline(f.Proposal, Now));
            if (stage is "consume" or "send" or "publish") Assert.True(f.Engine.EstablishBaseline(f.Receipt(), f.Authority, Now));
            if (stage is "send" or "reserve-commit") f.Store.AfterWrite = () => journal.Healthy = false;
            else if (stage == "publish") journal.DuringReconcile = () => journal.Healthy = false;
            else journal.Healthy = false;
            if (stage is "reserve" or "reserve-commit") Assert.False(f.Engine.ReserveBaseline(f.Proposal, Now));
            if (stage == "establish") Assert.False(f.Engine.EstablishBaseline(f.Receipt(), f.Authority, Now));
            if (stage == "publish") Assert.False(f.Engine.ReconcileScoped(f.Receipt(), f.Authority, Now));
            var result = f.Send();
            Assert.Equal(OrderStatus.Rejected, result.Status); Assert.Empty(f.Broker.Orders);
            Assert.Null(f.Engine.GetScopedPermit(f.Authority, Now));
            f.Engine.SaveState(Now, true); // state durability cannot repair the independent projection fault
            Assert.Null(f.Engine.GetScopedPermit(f.Authority, Now));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void SuccessfulStateSaveCannotClearFailedAuthorityProjectionReconciliation()
    {
        string dir = Path.Combine(Path.GetTempPath(), "scope-unreconciled-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new FileProjection(Path.Combine(dir, "csv")) { FailReconcile = true };
            var f = new Fixture(new Apex.Desktop.Services.FileStore(Path.Combine(dir, "state")), journal);
            Assert.True(journal.IsHealthy); Assert.Empty(f.State.ClosedOutbox);
            f.Engine.SaveState(Now, true);
            Assert.False(f.Engine.ReserveBaseline(f.Proposal, Now));
            Assert.Null(f.State.Baseline); Assert.Null(f.Engine.GetScopedPermit(f.Authority, Now)); Assert.Empty(f.Broker.Orders);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void GeneratedProposalEstablishesReadOnlyAndPermitIsOwnerRevisionAndMonotonicBound()
    {
        var f = new Fixture(); Assert.NotEqual(Guid.Empty, f.Proposal.BaselineId); Assert.InRange(f.Proposal.Identity.Label.Length, 1, 20);
        Assert.NotEqual(new Fixture().Proposal.Identity.Label, f.Proposal.Identity.Label);
        Assert.Null(f.Engine.GetScopedPermit(f.Authority, Now));
        Assert.False(f.Engine.ReserveBaseline(f.Proposal with { }, Now)); // arbitrary copied record cannot reserve a generated namespace
        f.Establish();
        Assert.Equal(ScopedRecoveryStatus.Ready, f.Engine.ScopedStatus); Assert.Empty(f.Broker.Orders);
        var permit = f.Engine.GetScopedPermit(f.Authority, Now); Assert.NotNull(permit);
        Assert.False(f.Engine.ValidateScopedPermit(permit, new ScopedReceiptAuthority(new object(), _ => true, () => TimeSpan.Zero), Now));
        f.Tick = TimeSpan.FromSeconds(31); Assert.False(f.Engine.ValidateScopedPermit(permit, f.Authority, Now));
        Assert.Equal(BaselineStage.Established, f.State.Baseline!.Stage);
        Assert.False(f.Engine.HistoryReady); // no Phase C producer wired into production admission
    }
    [Theory]
    [InlineData("identity")][InlineData("pending")][InlineData("foreign")][InlineData("netting")][InlineData("stale")][InlineData("current")][InlineData("incomplete")]
    [InlineData("path")][InlineData("currency")][InlineData("number")][InlineData("safety")][InlineData("missingflag")][InlineData("platform")]
    public void EstablishmentRejectsUnsafeReceipts(string kind)
    {
        var f = new Fixture(); Assert.True(f.Engine.ReserveBaseline(f.Proposal, Now)); var r = f.Receipt();
        r = kind switch {
            "identity" => r with { Identity = r.Identity with { Broker = "PepperstoneTest" } },
            "path" => r with { Identity = r.Identity with { ExecutionPath = RecoveryExecutionPath.Native } },
            "currency" => r with { Identity = r.Identity with { Currency = "EUR" } },
            "number" => r with { Identity = r.Identity with { AccountNumber = 654321 } },
            "safety" => r with { Identity = r.Identity with { SafetyVersion = "unknown" } },
            "missingflag" => r with { Identity = r.Identity with { IsLive = null } },
            "platform" => r with { Identity = r.Identity with { Platform = "Symulator" } },
            "netting" => r with { Identity = r.Identity with { IsHedging = false } },
            "pending" => r with { PendingOrders = new[] { new PendingOrderObservation(1, "other") } },
            "foreign" => r with { Positions = new[] { RecoveryTests.Open(42, "other") } },
            "stale" => r with { CapturedUtc = Now.AddMinutes(-1) },
            "incomplete" => r with { TransportComplete = false }, _ => r };
        if (kind == "current") f.Current = false;
        Assert.False(f.Engine.EstablishBaseline(r, f.Authority, Now)); Assert.Null(f.Engine.GetScopedPermit(f.Authority, Now)); Assert.Empty(f.Broker.Orders);
    }
    [Fact]
    public void FillManifestIsDurableBeforePollRetainedAfterFinalAndSdkOmission()
    {
        var f = new Fixture(); f.Establish(); Assert.Equal(OrderStatus.Filled, f.Send().Status);
        Assert.Equal(2, f.State.OpenAdmissions[0].Fill!.InitialVolumeUnits);
        f.Broker.ClosedDeals.Add(f.Deal(1, 1)); Assert.True(f.Engine.ReconcileScoped(f.Receipt(f.Position(1)), f.Authority, Now));
        Assert.Equal(5, f.State.RealizedTotal); Assert.Equal(0, f.State.TradesTotal);
        f.Restart(); f.Broker.ClosedDeals.Clear(); f.Broker.ClosedDeals.Add(f.Deal(2, 1));
        Assert.True(f.Engine.ReconcileScoped(f.Receipt(), f.Authority, Now));
        Assert.Equal(10, f.State.RealizedTotal); Assert.Equal(1, f.State.TradesTotal); Assert.Empty(f.State.Plans);
        Assert.Equal(AdmissionStage.Terminal, f.State.OpenAdmissions[0].Stage); Assert.Equal(2, f.State.OpenAdmissions[0].Fill!.InitialVolumeUnits);
        Assert.True(f.Engine.ReconcileScoped(f.Receipt(), f.Authority, Now)); Assert.Equal(1, f.State.TradesTotal);
        f.Broker.ClosedDeals.Add(f.Deal(3, 0.1)); Assert.False(f.Engine.ReconcileScoped(f.Receipt(), f.Authority, Now));
        Assert.Equal(ScopedRecoveryStatus.Conflict, f.Engine.ScopedStatus);
    }
    [Fact]
    public void ActualPartialFillNeverUsesRequestedOrRemainingUnitsAsOriginal()
    {
        var f = new Fixture(); f.Establish(); f.Send(1); f.Broker.ClosedDeals.Add(f.Deal(1, 0.5));
        Assert.True(f.Engine.ReconcileScoped(f.Receipt(f.Position(0.5)), f.Authority, Now));
        var a = Assert.Single(f.State.OpenAdmissions); Assert.Equal(2, a.Request.VolumeUnits); Assert.Equal(1, a.Fill!.InitialVolumeUnits);
        Assert.Equal(1, Assert.Single(f.State.Plans).InitialVolume);
    }
    [Fact]
    public void MissingPartialBlocksWhileOpenAndLateEarlierDealRepairsDeficit()
    {
        var f = new Fixture(); f.Establish(); f.Send();
        Assert.False(f.Engine.ReconcileScoped(f.Receipt(f.Position(1)), f.Authority, Now));
        Assert.Equal(ScopedRecoveryStatus.AwaitingEvidence, f.Engine.ScopedStatus); Assert.Null(f.Engine.GetScopedPermit(f.Authority, Now));
        Assert.Contains("C+O", f.Engine.ScopedMessage);
        f.Broker.ClosedDeals.Add(f.Deal(1, 1)); Assert.True(f.Engine.ReconcileScoped(f.Receipt(f.Position(1)), f.Authority, Now));
    }
    [Theory]
    [InlineData("entry")][InlineData("comment")][InlineData("label")][InlineData("opening")][InlineData("growth")][InlineData("quantum")]
    public void MutationsLatchConflictNeverRebaseOriginal(string kind)
    {
        var f = new Fixture(); f.Establish(); f.Send(); var p = f.Position(2); var r = f.Receipt(p);
        if (kind == "growth") { f.Broker.ClosedDeals.Add(f.Deal(1, 1)); Assert.True(f.Engine.ReconcileScoped(f.Receipt(f.Position(1)), f.Authority, Now)); r = f.Receipt(f.Position(1.5)); }
        if (kind == "entry" || kind == "comment" || kind == "label") r = r with { Positions = new[] { new PositionInfo { Id = p.Id, Symbol = p.Symbol, Direction = p.Direction,
            Label = kind == "label" ? "other" : p.Label, Comment = kind == "comment" ? "spoof" : p.Comment, EntryPrice = kind == "entry" ? 4001 : p.EntryPrice,
            EntryTimeUtc = p.EntryTimeUtc, VolumeUnits = 2 } } };
        if (kind == "opening") r = r with { OpeningObservations = new[] { f.State.OpenAdmissions[0].Fill!.OpeningDeals[0] with { DealId = 7 } } };
        if (kind == "quantum") { f.Broker.ClosedDeals.Add(f.Deal(1, 0.1)); r = f.Receipt(f.Position(2)); }
        Assert.False(f.Engine.ReconcileScoped(r, f.Authority, Now)); Assert.Equal(ScopedRecoveryStatus.Conflict, f.Engine.ScopedStatus);
        Assert.Equal(2, f.State.OpenAdmissions[0].Fill!.InitialVolumeUnits);
        f.Restart(); Assert.Null(f.Engine.GetScopedPermit(f.Authority, Now)); Assert.NotEmpty(f.State.ScopeConflict);
        Assert.Equal(ScopedRecoveryStatus.Conflict, f.Engine.ScopedStatus);
    }
    [Theory][InlineData("reserve")][InlineData("establish")][InlineData("admit")][InlineData("bind")][InlineData("account")]
    public void CandidateFaultNeverPublishesPermitOrAccounting(string stage)
    {
        var f = new Fixture();
        if (stage == "reserve") { f.Store.Fail = true; Assert.False(f.Engine.ReserveBaseline(f.Proposal, Now)); }
        else { Assert.True(f.Engine.ReserveBaseline(f.Proposal, Now));
            if (stage == "establish") { f.Store.Fail = true; Assert.False(f.Engine.EstablishBaseline(f.Receipt(), f.Authority, Now)); }
            else { Assert.True(f.Engine.EstablishBaseline(f.Receipt(), f.Authority, Now));
                if (stage == "admit") { f.Store.Fail = true; f.Send(); Assert.Empty(f.Broker.Orders); }
                if (stage == "bind") { f.Broker.OnOpen = r => { f.Store.Fail = true; return OrderOutcome.Filled(42, 4000, f.Fill(r)); }; f.Engine.SendAdmittedOpen(f.Request, new() { RiskAmount = 10 }, Now); }
                if (stage == "account") { f.Send(); f.Broker.ClosedDeals.Add(f.Deal(1, 2)); f.Store.Fail = true; Assert.False(f.Engine.ReconcileScoped(f.Receipt(), f.Authority, Now)); Assert.Equal(0, f.State.TradesTotal); Assert.Equal(0, f.State.RealizedTotal); }
            }
        }
        Assert.Null(f.Engine.GetScopedPermit(f.Authority, Now));
    }
    [Fact]
    public void UnknownOriginalRemainsObligationAndReadOnlyOutcomeRecoveryCanBindAfterCrash()
    {
        var f = new Fixture(); f.Establish(); OpeningFillEvidence? observed = null;
        f.Broker.OnOpen = r => { observed = f.Fill(r); return OrderOutcome.Uncertain(BrokerErrorKind.Timeout, "unknown"); };
        f.Engine.SendAdmittedOpen(f.Request, new() { RiskAmount = 10 }, Now);
        f.Restart(); Assert.False(f.Engine.ReconcileScoped(f.Receipt(), f.Authority, Now)); Assert.Null(f.Engine.GetScopedPermit(f.Authority, Now));
        Assert.True(f.Engine.RecoverOpeningOutcome(observed!.Request.ExecutionId, OrderOutcome.Filled(42, 4000, observed), Now));
        Assert.False(f.Engine.ReconcileScoped(f.Receipt(), f.Authority, Now)); // missing actual closing deals, not disappearance
        Assert.Single(f.Broker.Orders); Assert.Equal(2, f.State.OpenAdmissions[0].Fill!.InitialVolumeUnits);
    }
    [Fact]
    public void RejectionDischargesButCannotResendSameSignal()
    {
        var f = new Fixture(); f.Establish(); f.Broker.OnOpen = _ => OrderOutcome.Rejected(BrokerErrorKind.NoMoney, "definitive rejection");
        f.Engine.SendAdmittedOpen(f.Request, new() { RiskAmount = 10 }, Now);
        Assert.Equal(AdmissionStage.Rejected, f.State.OpenAdmissions[0].Stage); Assert.True(f.Engine.ReconcileScoped(f.Receipt(), f.Authority, Now));
        f.Engine.SendAdmittedOpen(f.Request, new() { RiskAmount = 10 }, Now); Assert.Single(f.Broker.Orders);
    }

    [Fact]
    public void GenerationChangeDuringEstablishmentCommitCannotPublishWindow()
    {
        var f = new Fixture(); Assert.True(f.Engine.ReserveBaseline(f.Proposal, Now));
        f.Store.AfterWrite = () => f.Current = false;
        Assert.False(f.Engine.EstablishBaseline(f.Receipt(), f.Authority, Now)); Assert.Null(f.Engine.GetScopedPermit(f.Authority, Now));
        Assert.Equal(BaselineStage.Established, f.State.Baseline!.Stage); Assert.Empty(f.Broker.Orders);
    }
    [Theory][InlineData("legacy")][InlineData("missing")][InlineData("null")][InlineData("unknown-version")]
    public void OldOrMalformedScopeCannotGainReadinessOrEraseLatches(string kind)
    {
        var f = new Fixture(); var state = f.State; state.Mode = EngineMode.EmergencyStopped;
        if (kind == "legacy") state.SchemaVersion = 2;
        if (kind == "unknown-version") state.SchemaVersion = 99;
        string json = JsonSerializer.Serialize(state, Json);
        if (kind == "missing") { var node = System.Text.Json.Nodes.JsonNode.Parse(json)!; node.AsObject().Remove("OpenAdmissions"); json = node.ToJsonString(); }
        if (kind == "null") json = json.Replace("\"OpenAdmissions\":[]", "\"OpenAdmissions\":null");
        f.Store.Set(f.Key, json); f.Restart();
        Assert.False(f.Engine.ReserveBaseline(f.Proposal, Now)); Assert.Null(f.Engine.GetScopedPermit(f.Authority, Now));
        if (kind == "legacy") { Assert.True(f.State.LegacyScopeQuarantine); Assert.Null(f.State.Baseline); Assert.Equal(EngineMode.EmergencyStopped, f.State.Mode); }
        else Assert.Equal(json, f.Store.Get(f.Key));
    }
    [Fact]
    public void OneQuantumOnLargePositionIsNotRelativeToleranceAndInvalidStepCannotBind()
    {
        var f = new Fixture(); f.Establish();
        var r = new OrderRequest { SignalId = "large", Symbol = "US500", Direction = TradeDirection.Long, VolumeUnits = 100000000,
            Label = f.Request.Label, Comment = f.Request.Comment, StopLossPips = 10, TakeProfitPips = 20 };
        f.Broker.OnOpen = q => OrderOutcome.Filled(42, 4000, f.Fill(q, 100000000, 0.1));
        f.Engine.SendAdmittedOpen(r, new() { RiskAmount = 10 }, Now);
        Assert.False(f.Engine.ReconcileScoped(f.Receipt(f.Position(99999999.9)), f.Authority, Now));
        Assert.Equal(ScopedRecoveryStatus.AwaitingEvidence, f.Engine.ScopedStatus);
        f.Broker.ClosedDeals.Add(f.Deal(1, 0.1)); Assert.True(f.Engine.ReconcileScoped(f.Receipt(f.Position(99999999.9)), f.Authority, Now));
        var g = new Fixture(); g.Establish(); g.Broker.OnOpen = q => OrderOutcome.Filled(42, 4000, g.Fill(q, 2, Math.BitIncrement(0.1)));
        g.Engine.SendAdmittedOpen(g.Request, new() { RiskAmount = 10 }, Now); Assert.Null(g.State.OpenAdmissions[0].Fill); Assert.Null(g.Engine.GetScopedPermit(g.Authority, Now));
    }
    [Theory][InlineData("position")][InlineData("closing")][InlineData("pending")]
    public void UnknownExactLabelObjectsCannotBeAdoptedByComment(string kind)
    {
        var f = new Fixture(); f.Establish(); var r = f.Receipt();
        if (kind == "position") r = f.Receipt(f.Position(2));
        if (kind == "closing") r = r with { Closed = new[] { f.Deal(1, 2) } };
        if (kind == "pending") r = r with { PendingOrders = new[] { new PendingOrderObservation(1, f.Request.Label) } };
        Assert.False(f.Engine.ReconcileScoped(r, f.Authority, Now)); Assert.Equal(ScopedRecoveryStatus.Conflict, f.Engine.ScopedStatus);
        Assert.Empty(f.State.OpenAdmissions); Assert.Empty(f.State.Plans); Assert.Empty(f.Broker.Closes);
    }
    [Fact]
    public void ForgedCoverageAndPlatformOrMutableBacktestCannotCreateScopedPermit()
    {
        var broker = new FakeBroker { Now = Now }; var cfg = new EngineConfig(); var store = new MemoryStore();
        var e = new TradingEngine(cfg, new Apex.Core.Adaptive.StrategyBook(Scenarios.DipOnlyProfile()), broker, broker, new Apex.Core.Bridge.BarStore(), store, new NullLog(), new NullJournal());
        e.Initialize(Now); cfg.IsBacktest = true;
        e.ReplayHistory(new BrokerHistorySnapshot { AccountNumber = broker.AccountNumber, Complete = true, RequiredCoverage = true,
            Generation = "Symulator", Scope = "required", Positions = Array.Empty<PositionInfo>() }, Now);
        Assert.Null(e.GetScopedPermit(new ScopedReceiptAuthority(e, _ => true, () => TimeSpan.Zero), Now));
        Assert.Null(JsonSerializer.Deserialize<EngineState>(store.Get("APEX APEX 123456")!, Json)!.Baseline);
    }
    [Fact]
    public void AlteredFillRequestCannotBindAndOldQueuedReceiptCannotUnlockOrInventGrowth()
    {
        var f = new Fixture(); f.Establish(); OpeningFillEvidence? fill = null;
        f.Broker.OnOpen = q => { fill = f.Fill(q); return OrderOutcome.Uncertain(BrokerErrorKind.Timeout, "unknown"); };
        f.Engine.SendAdmittedOpen(f.Request, new() { RiskAmount = 10 }, Now);
        var bad = new OpeningFillEvidence(fill!.Request with { VolumeUnits = 3 }, 42, 2, 0.1, 4000, Now, fill.OpeningDeals);
        Assert.False(f.Engine.RecoverOpeningOutcome(fill.Request.ExecutionId, OrderOutcome.Filled(42, 4000, bad), Now)); Assert.Null(f.State.OpenAdmissions[0].Fill);
        var g = new Fixture(); g.Establish(); g.Send(); var old = g.Receipt(g.Position(2));
        g.Broker.ClosedDeals.Add(g.Deal(1, 1)); Assert.True(g.Engine.ReconcileScoped(g.Receipt(g.Position(1)), g.Authority, Now));
        g.Current = false; Assert.False(g.Engine.ReconcileScoped(old, g.Authority, Now)); Assert.NotEqual(ScopedRecoveryStatus.Conflict, g.Engine.ScopedStatus);
        Assert.Equal(1, g.State.OpenAdmissions[0].LastRemainingUnits); Assert.Null(g.Engine.GetScopedPermit(g.Authority, Now));
    }

    [Fact]
    public void ImmutableAdmissionsOutliveSignalLedgerPruningAndRestart()
    {
        var f = new Fixture(); f.Establish(); Assert.True(f.Engine.Ledger.TryReserve("s1", "US500", Now)); f.Send();
        Assert.Equal(SignalState.Executed, Assert.Single(f.Engine.Ledger.Entries).State);
        Assert.Equal(1, f.Engine.Ledger.Prune(Now.AddDays(20), TimeSpan.FromDays(10)));
        f.Engine.SaveState(Now.AddDays(20), true); Assert.Empty(f.State.Ledger);
        f.Restart(); Assert.Single(f.Engine.ScopedAdmissions); Assert.Equal(2, f.Engine.ScopedAdmissions[0].Fill!.InitialVolumeUnits);
    }

    [Fact]
    public void LiveDisconnectInvalidatesPermitEvenBeforeProducerFenceRuns()
    {
        var f = new Fixture(); f.Establish(); var permit = f.Engine.GetScopedPermit(f.Authority, Now); Assert.NotNull(permit);
        f.Broker.Connected = false;
        Assert.False(f.Engine.ValidateScopedPermit(permit, f.Authority, Now));
    }
    [Theory][InlineData("establish")][InlineData("bind")][InlineData("account")]
    public void ThrowAfterPublicationPreservesCandidateAcrossRestartWithoutInMemorySuccess(string stage)
    {
        var f = new Fixture(); Assert.True(f.Engine.ReserveBaseline(f.Proposal, Now));
        if (stage == "establish")
        {
            f.Store.AfterWrite = () => throw new IOException("published but acknowledgment lost");
            Assert.False(f.Engine.EstablishBaseline(f.Receipt(), f.Authority, Now));
            Assert.Equal(BaselineStage.Established, f.State.Baseline!.Stage);
        }
        else
        {
            Assert.True(f.Engine.EstablishBaseline(f.Receipt(), f.Authority, Now));
            if (stage == "bind")
            {
                f.Broker.OnOpen = q => { f.Store.AfterWrite = () => throw new IOException("bound publication ambiguous"); return OrderOutcome.Filled(42, 4000, f.Fill(q)); };
                Assert.Equal(OrderStatus.Uncertain, f.Engine.SendAdmittedOpen(f.Request, new() { RiskAmount = 10 }, Now).Status);
                Assert.NotNull(f.State.OpenAdmissions[0].Fill);
            }
            else
            {
                f.Send(); f.Broker.ClosedDeals.Add(f.Deal(1, 2));
                f.Store.AfterWrite = () => throw new IOException("accounting publication ambiguous");
                Assert.False(f.Engine.ReconcileScoped(f.Receipt(), f.Authority, Now));
                Assert.Empty(f.Engine.RecentTrades); Assert.Equal(1, f.State.TradesTotal);
            }
        }
        Assert.Null(f.Engine.GetScopedPermit(f.Authority, Now)); f.Engine.SaveState(Now, true);
        f.Store.AfterWrite = null; f.Restart();
        Assert.Equal(BaselineStage.Established, f.State.Baseline!.Stage);
        if (stage == "account") { Assert.Equal(1, f.State.TradesTotal); Assert.Equal(5, f.State.RealizedTotal); Assert.Empty(f.State.ClosedOutbox); }
    }

    [Fact]
    public void NonNormalizedPresendRequestCannotReachGateway()
    {
        var f = new Fixture(); f.Establish();
        f.Broker.OnOpen = _ => OrderOutcome.Rejected(BrokerErrorKind.InvalidRequest, "should not send");
        var req = new OrderRequest { SignalId = "bad-units", Symbol = "US500", Direction = TradeDirection.Long, VolumeUnits = 2.05,
            Label = f.Request.Label, Comment = f.Request.Comment, StopLossPips = 10, TakeProfitPips = 20 };
        f.Engine.SendAdmittedOpen(req, new() { RiskAmount = 10 }, Now); Assert.Empty(f.Broker.Orders); Assert.Empty(f.State.OpenAdmissions);
    }

    [Theory]
    [InlineData("RiskAmount")][InlineData("Request")][InlineData("ReceiptId")][InlineData("OperationId")][InlineData("Identity")][InlineData("Stage")][InlineData("Fill")]
    public void MissingManifestFieldCannotDefaultIntoProof(string field)
    {
        var f = new Fixture(); f.Establish(); f.Send();
        var node = System.Text.Json.Nodes.JsonNode.Parse(f.Store.Get(f.Key)!)!;
        node["OpenAdmissions"]![0]!.AsObject().Remove(field); string original = node.ToJsonString();
        f.Store.Set(f.Key, original); f.Restart();
        Assert.Equal(original, f.Store.Get(f.Key)); Assert.Null(f.Engine.GetScopedPermit(f.Authority, Now));
    }

    [Fact]
    public void PreManifestCrashReservationCannotDisappearFromScopedObligations()
    {
        var f = new Fixture(); f.Establish(); var state = f.State;
        state.Ledger.Add(new LedgerEntry { SignalId = "pre-manifest", Symbol = "US500", State = SignalState.PendingVerification, TimeUtc = Now });
        f.Store.Set(f.Key, JsonSerializer.Serialize(state, Json)); f.Restart();
        Assert.False(f.Engine.ReconcileScoped(f.Receipt(), f.Authority, Now)); Assert.Null(f.Engine.GetScopedPermit(f.Authority, Now));
        Assert.Single(f.State.Ledger);
    }

    [Fact]
    public void RealFileAuthorityRecoversCrashAfterFillBeforeBindingAndFastCloseOnce()
    {
        string dir = Path.Combine(Path.GetTempPath(), "scope-authority-" + Guid.NewGuid().ToString("N"));
        try
        {
            var f = new Fixture(new Apex.Desktop.Services.FileStore(Path.Combine(dir, "state")), new Apex.Desktop.Services.FileJournal(Path.Combine(dir, "csv")));
            f.Establish(); OpeningFillEvidence? actual = null;
            f.Broker.OnOpen = q => { actual = f.Fill(q, 1); f.Store.Fail = true; return OrderOutcome.Filled(42, 4000, actual); };
            Assert.Equal(OrderStatus.Uncertain, f.Engine.SendAdmittedOpen(f.Request, new() { RiskAmount = 10 }, Now).Status);
            Assert.Null(f.State.OpenAdmissions[0].Fill); f.Store.Fail = false;
            f.Store.Inner = new Apex.Desktop.Services.FileStore(Path.Combine(dir, "state")); f.Restart();
            Assert.Null(f.Engine.GetScopedPermit(f.Authority, Now));
            Assert.True(f.Engine.RecoverOpeningOutcome(actual!.Request.ExecutionId, OrderOutcome.Filled(42, 4000, actual), Now));
            f.Broker.ClosedDeals.Add(f.Deal(1, 1)); Assert.True(f.Engine.ReconcileScoped(f.Receipt(), f.Authority, Now));
            f.Restart(); Assert.True(f.Engine.ReconcileScoped(f.Receipt(), f.Authority, Now));
            Assert.Equal(1, f.State.TradesTotal); Assert.Equal(5, f.State.RealizedTotal); Assert.Equal(1, f.State.OpenAdmissions[0].Fill!.InitialVolumeUnits);
            Assert.Equal(2, File.ReadAllLines(Path.Combine(dir, "csv", "transakcje.csv")).Length); Assert.Single(f.Broker.Orders);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void LiveCycleDoesNotAdoptOrAutoModifyUnknownScopedObject()
    {
        var f = new Fixture(); f.Establish();
        f.Broker.Positions.Add(new PositionInfo { Id = 77, Label = f.Request.Label, Symbol = "US500", Direction = TradeDirection.Long,
            VolumeUnits = 2, EntryPrice = 4000, EntryTimeUtc = Now, Comment = "APEX|ET|spoof|v1" });
        f.Engine.OnTimer(Now.AddSeconds(1));
        Assert.Empty(f.Engine.Plans); Assert.Empty(f.Broker.Closes); Assert.Empty(f.Broker.Modifications);
        Assert.Equal(ScopedRecoveryStatus.Conflict, f.Engine.ScopedStatus);
    }

    [Fact]
    public void AuthorizedQueuedAccountingCanCommitAfterExpiryButNeverGrantPermit()
    {
        var f = new Fixture(); f.Establish(); f.Send(); f.Broker.ClosedDeals.Add(f.Deal(1, 1));
        var r = f.Receipt(f.Position(1)); f.Current = false;
        Assert.False(f.Engine.ReconcileScoped(r, f.Authority, Now.AddMinutes(1)));
        Assert.Equal(5, f.State.RealizedTotal); Assert.Null(f.Engine.GetScopedPermit(f.Authority, Now.AddMinutes(1)));
    }

    [Fact]
    public void ClientBacktestFlagCannotAuthorizeAnOrdinaryGateway()
    {
        var b = new RecoveryTests.LegacyBroker(); var cfg = new EngineConfig { IsBacktest = true };
        var e = new TradingEngine(cfg, new Apex.Core.Adaptive.StrategyBook(Scenarios.DipOnlyProfile()), b, b, new Apex.Core.Bridge.BarStore(), new MemoryStore(), new NullLog(), new NullJournal());
        e.SetGuard(new Apex.Core.Safety.AccountGuardResult { Allowed = true }); e.Initialize(Now);
        Assert.False(e.HistoryReady); e.CmdStart(); Assert.Equal(EngineMode.Stopped, e.Mode);
    }

    [Fact]
    public void UncertainAbsentOpenNeverTimesOutIntoNonExecution()
    {
        var store = new MemoryStore();
        var seed = new EngineState { AccountNumber = 123456, Broker = "Pepperstone Test", Currency = "USD", Label = "APEX" };
        seed.Ledger.Add(new LedgerEntry { SignalId = "uncertain", Symbol = "US500", State = SignalState.PendingVerification, TimeUtc = Now.AddHours(-1) });
        store.Set("APEX APEX 123456", JsonSerializer.Serialize(seed));
        var broker = new FakeBroker { Now = Now };
        var engine = RecoveryTests.Engine(broker, store, new NullJournal());
        engine.OnTimer(Now.AddMinutes(2)); engine.SaveState(Now.AddMinutes(2), true);
        var state = JsonSerializer.Deserialize<EngineState>(store.Get("APEX APEX 123456")!, new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!;
        Assert.Equal(SignalState.PendingVerification, Assert.Single(state.Ledger).State);
        Assert.Empty(broker.Orders);
    }
}
