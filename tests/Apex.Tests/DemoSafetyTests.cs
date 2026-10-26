using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Apex.Core.Adaptive;
using Apex.Core.Bridge;
using Apex.Core.Engine;
using Apex.Core.Model;
using Apex.Core.Risk;
using Apex.Core.Safety;
using Xunit;

namespace Apex.Tests;

public class DemoSafetyTests
{
    private sealed class DurableStore : IKeyValueStore
    {
        public readonly Dictionary<string, string> Durable = new();
        private readonly Dictionary<string, string> _pending = new();
        public bool FailSet, FailFlush, FailGet;
        public string? Get(string key) => FailGet ? throw new IOException("test disk read failure") : Durable.GetValueOrDefault(key);
        public void Set(string key, string value)
        {
            if (FailSet) throw new IOException("test disk write failure");
            _pending[key] = value;
        }
        public void Flush()
        {
            if (FailFlush) throw new IOException("test disk flush failure");
            foreach (var x in _pending) Durable[x.Key] = x.Value;
            _pending.Clear();
        }
    }

    private sealed class Gateway : IBrokerGateway, IBrokerHistory, IMarketData
    {
        public FakeBroker Inner = new();
        public int QuoteOffsetSeconds;
        public bool RejectModify, IgnoreModify, FailClose, DropTp;
        public Action? BeforeOpen, BeforeClose;
        public bool IsConnected => Inner.IsConnected;
        public BrokerHistorySnapshot GetHistory() => Inner.GetHistory();
        public AccountSnapshot GetAccount() => Inner.GetAccount();
        public IReadOnlyList<PositionInfo> GetPositions() => Inner.GetPositions();
        public OrderOutcome OpenMarket(OrderRequest request)
        {
            BeforeOpen?.Invoke();
            var result = Inner.OpenMarket(request);
            if (DropTp && Inner.Positions.Count > 0)
            {
                var p = Inner.Positions[^1];
                Inner.Positions[^1] = Position(p.Id, p.Label, p.StopLoss, null, p.Comment, p.EntryPrice);
            }
            return result;
        }
        public OrderOutcome ModifyStopLoss(long id, double sl) => RejectModify
            ? OrderOutcome.Rejected(BrokerErrorKind.InvalidStopLossTakeProfit, "test rejected SL")
            : IgnoreModify ? OrderOutcome.Filled(id, sl) : Inner.ModifyStopLoss(id, sl);
        public OrderOutcome ClosePosition(long id, double? units)
        {
            BeforeClose?.Invoke();
            return FailClose ? OrderOutcome.Uncertain(BrokerErrorKind.Timeout, "test close timeout") : Inner.ClosePosition(id, units);
        }
        public double EstimateMargin(string s, TradeDirection d, double u) => Inner.EstimateMargin(s, d, u);
        public InstrumentSpec? GetSpec(string s) => Inner.GetSpec(s);
        public Quote? GetQuote(string s)
        {
            var q = Inner.GetQuote(s);
            return q.HasValue ? new Quote(q.Value.Bid, q.Value.Ask, q.Value.TimeUtc.AddSeconds(QuoteOffsetSeconds)) : null;
        }
        public bool IsMarketOpen(string s, DateTime utc, out TimeSpan close) => Inner.IsMarketOpen(s, utc, out close);
    }

    private sealed class Rig
    {
        public Gateway G;
        public DurableStore Store;
        public TradingEngine Engine;
        public DateTime Now;
        public BarStore Bars;
        public Rig(DurableStore? store = null, Gateway? gateway = null, bool start = true)
        {
            var seed = new EngineRig();
            G = gateway ?? new Gateway { Inner = seed.Broker };
            Store = store ?? new DurableStore();
            Now = seed.Now;
            Bars = seed.Bars;
            var cfg = new EngineConfig { CollectionWindowSeconds = 0, VerificationTimeoutSeconds = 60 };
            cfg.News.Enabled = false;
            Engine = new TradingEngine(cfg, new StrategyBook(Scenarios.DipOnlyProfile()), G, G, Bars, Store, new NullLog(), new NullJournal());
            Engine.SetUniverse(new[] { new UniverseEntry { Symbol = "US500", AssetClass = AssetClass.Index, BaseCurrency = "US500" } });
            Engine.SetGuard(new AccountGuardResult { Allowed = true, Reason = "DEMO test" });
            Engine.Initialize(Now);
            if (start) Engine.CmdStart();
        }
        public void Tick(int seconds = 2) { Now = Now.AddSeconds(seconds); G.Inner.Now = Now; Engine.OnTimer(Now); }
    }

    private static PositionInfo Position(long id, string label = "APEX", double? sl = 4800, double? tp = 6000,
        string comment = "", double entry = 5000) => new()
    {
        Id = id, Symbol = "US500", Direction = TradeDirection.Long, VolumeUnits = 0.1,
        EntryPrice = entry, CurrentPrice = 5000, StopLoss = sl, TakeProfit = tp, Label = label, Comment = comment,
    };

    [Fact]
    public void FreshEngine_RequiresExplicitStart()
    {
        var r = new Rig(start: false);
        r.Tick();
        Assert.Equal(EngineMode.Stopped, r.Engine.Mode);
        Assert.Empty(r.G.Inner.Orders);
    }

    [Fact]
    public void Restart_NeverRestoresRunningMode()
    {
        var r = new Rig();
        r.Engine.SaveState(r.Now, true);
        var restarted = new Rig(r.Store, r.G, start: false);
        restarted.Tick();
        Assert.Equal(EngineMode.Stopped, restarted.Engine.Mode);
        Assert.Empty(r.G.Inner.Orders);
    }

    [Theory]
    [InlineData(true, "")]
    [InlineData(false, "not-json")]
    [InlineData(false, "null")]
    [InlineData(false, "{\"AccountNumber\":999}")]
    public void CorruptOrUnreadableState_CannotBeOverwrittenOrStarted(bool failGet, string content)
    {
        var original = new Rig();
        string key = original.Store.Durable.Keys.Single();
        original.Store.Durable[key] = content;
        original.Store.FailGet = failGet;
        var restarted = new Rig(original.Store, original.G, start: false);
        restarted.Engine.CmdStart();
        restarted.Engine.CmdExecutionTest(restarted.Now, "US500");
        restarted.Engine.SaveState(restarted.Now, true);
        restarted.Tick();
        Assert.Empty(restarted.G.Inner.Orders);
        Assert.NotEqual(EngineMode.Running, restarted.Engine.Mode);
        Assert.Equal(content, restarted.Store.Durable[key]);
    }

    [Fact]
    public void LiveGuard_RejectsEvenAllLegacyUnlocks()
    {
        var acc = new AccountSnapshot { Number = 1, Balance = 10000, Equity = 10000, IsLive = true };
        Assert.False(AccountGuard.Evaluate(acc, new AccountGuardSettings
        { AllowLive = true, AllowedAccounts = new[] { 1L }, LiveConfirmation = AccountGuard.RequiredLivePhrase(1) }).Allowed);
    }

    [Fact]
    public void Engine_RejectsLiveSnapshotEvenWithForgedDemoGuard()
    {
        var r = new Rig();
        r.G.Inner.Live = true;
        r.Tick();
        Assert.Empty(r.G.Inner.Orders);
        Assert.False(r.Engine.GuardAllowed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StrategyOrder_WriteOrFlushFailurePreventsOpen(bool flush)
    {
        var r = new Rig();
        r.Store.FailSet = !flush;
        r.Store.FailFlush = flush;
        r.Tick();
        Assert.Empty(r.G.Inner.Orders);
        Assert.NotEqual(EngineMode.Running, r.Engine.Mode);
    }

    [Fact]
    public void StrategyWithoutTarget_GetsProtectiveThreeRTarget()
    {
        var r = new Rig();
        r.Tick();
        var o = Assert.Single(r.G.Inner.Orders);
        Assert.Equal(o.StopLossPips * 3, o.TakeProfitPips!.Value, 6);
    }

    [Fact]
    public void EmergencyIntent_IsDurableBeforeFirstBrokerAction()
    {
        var r = new Rig();
        r.G.Inner.Positions.Add(Position(99));
        r.G.BeforeClose = () => Assert.Contains("EmergencyStopped", Assert.Single(r.Store.Durable).Value);
        r.Engine.CmdEmergencyStop(r.Now, "test");
    }

    [Fact]
    public void EmergencyRestart_RetriesFailedCloseAndNeverTouchesForeign()
    {
        var r = new Rig();
        r.G.Inner.Positions.Add(Position(99));
        r.G.Inner.Positions.Add(Position(100, "foreign", null, null));
        r.G.FailClose = true;
        r.Engine.CmdEmergencyStop(r.Now, "test");
        r.G.FailClose = false;
        var restarted = new Rig(r.Store, r.G, start: false);
        restarted.Tick(20); // durable close retry keeps its persisted backoff across restart
        Assert.Equal(EngineMode.EmergencyStopped, restarted.Engine.Mode);
        Assert.Equal(100, Assert.Single(r.G.Inner.Positions).Id);
    }

    [Theory]
    [InlineData("daily")]
    [InlineData("drawdown")]
    [InlineData("minimum-risk")]
    [InlineData("positions")]
    [InlineData("pending")]
    [InlineData("leverage")]
    [InlineData("margin")]
    [InlineData("disconnected")]
    [InlineData("changed-account")]
    [InlineData("live")]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("crypto")]
    public void ExecutionTest_CannotBypassSafety(string failure)
    {
        var r = new Rig(start: false);
        string symbol = "US500";
        switch (failure)
        {
            case "daily": r.G.Inner.EquityOffset = -160; break;
            case "drawdown": r.G.Inner.EquityOffset = -600; break;
            case "minimum-risk": r.G.Inner.Balance = 1; break;
            case "positions":
                for (int i = 0; i < 3; i++) r.G.Inner.Positions.Add(new PositionInfo
                { Id = 100 + i, Symbol = "OTHER" + i, Label = "APEX", Direction = TradeDirection.Long, StopLoss = 1, TakeProfit = 100, VolumeUnits = 1, EntryPrice = 2, CurrentPrice = 2 });
                break;
            case "pending": r.Engine.Ledger.TryReserve("uncertain", symbol, r.Now); break;
            case "leverage": r.Engine.Config.Risk.MaxEffectiveLeverage = 0.001; break;
            case "margin": r.Engine.Config.Risk.MaxMarginUseOfFree = 0.00001; break;
            case "disconnected": r.G.Inner.Connected = false; break;
            case "changed-account": r.G.Inner.AccountNumber++; break;
            case "live": r.G.Inner.Live = true; break;
            case "stale": r.G.QuoteOffsetSeconds = -181; break;
            case "future": r.G.QuoteOffsetSeconds = 30; break;
            case "crypto": symbol = "BTCUSD"; r.G.Inner.AddInstrument(symbol, AssetClass.Index, 50000, 1, 1, 0.001, 0.001); break;
        }
        r.Engine.CmdExecutionTest(r.Now, symbol);
        Assert.Empty(r.G.Inner.Orders);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExecutionTest_PersistsReservationBeforeOpen(bool flushFailure)
    {
        var r = new Rig(start: false);
        r.G.BeforeOpen = () => Assert.Contains("PendingVerification", Assert.Single(r.Store.Durable).Value);
        r.Store.FailFlush = flushFailure;
        r.Engine.CmdExecutionTest(r.Now, "US500");
        if (flushFailure) Assert.Empty(r.G.Inner.Orders);
        else Assert.Single(r.G.Inner.Orders);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExecutionTest_FailedOrUnappliedModifyNeverPasses(bool rejected)
    {
        var r = new Rig(start: false);
        r.G.RejectModify = rejected;
        r.G.IgnoreModify = !rejected;
        r.Engine.CmdExecutionTest(r.Now, "US500");
        for (int i = 0; i < 45; i++) r.Tick(5);
        Assert.Empty(r.G.Inner.Positions);
        Assert.DoesNotContain(r.Engine.LastExecutionTest, x => x.Contains("TEST ZALICZONY"));
        Assert.Contains(r.Engine.LastExecutionTest, x => x.Contains("TEST NIEZALICZONY"));
    }

    [Fact]
    public void MissingTargetAfterFill_ClosesOwnPositionImmediately()
    {
        var r = new Rig();
        r.G.DropTp = true;
        r.Tick();
        Assert.Single(r.G.Inner.Orders);
        Assert.Empty(r.G.Inner.Positions);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    public void InvalidExistingTarget_IsClosedWithoutTouchingForeign(double tp)
    {
        var r = new Rig(start: false);
        r.G.Inner.Positions.Add(Position(90, tp: tp));
        r.G.Inner.Positions.Add(Position(91, "foreign", tp: tp));
        r.Tick();
        Assert.Equal(91, Assert.Single(r.G.Inner.Positions).Id);
    }

    [Fact]
    public void OrphanExecutionTest_WithSavedPlanStillClosesAfterRestart()
    {
        var r = new Rig(start: false);
        r.Engine.CmdExecutionTest(r.Now, "US500");
        r.Tick();
        r.Engine.SaveState(r.Now, true);
        var restarted = new Rig(r.Store, r.G, start: false);
        restarted.Tick();
        Assert.Empty(r.G.Inner.Positions);
    }

    [Fact]
    public void BarCorrection_InvalidatesCachedIndicators()
    {
        var bars = new BarStore();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        bars.Merge("X", Tf.H1, new[] { new Bar(t, 10, 12, 9, 11) }, true);
        bars.TryGet("X", Tf.H1, t.AddHours(1), out _, out _);
        bars.Merge("X", Tf.H1, new[] { new Bar(t, 10, 14, 9, 13) }, false);
        Assert.True(bars.TryGet("X", Tf.H1, t.AddHours(1), out var f, out var i));
        Assert.Equal(13, f.Close[i]);
    }

    [Fact]
    public void BarStore_NeverExposesUnclosedFutureBar()
    {
        var bars = new BarStore();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        bars.Merge("X", Tf.H1, new[] { new Bar(t, 10, 12, 9, 11), new Bar(t.AddHours(1), 10, 12, 9, 11) }, true);
        Assert.True(bars.TryGet("X", Tf.H1, t.AddHours(1), out var f, out var i));
        Assert.Equal(t, f.Bars.Times[i]);
        Assert.Equal(t, bars.LastClosedBarTime("X", Tf.H1, t.AddHours(1)));
    }

    [Fact]
    public void NaNRiskConfiguration_IsRejected()
    {
        Assert.NotNull(new RiskSettings { RiskPerTradePct = double.NaN }.Validate());
    }

    [Fact]
    public void StrategyOrder_RoundedStopStillFitsExactRiskBudget()
    {
        var r = new Rig();
        r.Engine.Config.Risk.SlippageAllowanceAtr = 0;
        r.G.Inner.Specs["US500"] = new InstrumentSpec
        {
            Name = "US500", AssetClass = AssetClass.Index, PipSize = 0.1, TickSize = 0.001,
            PipValuePerUnit = 0.1, VolumeMin = 0.000001, VolumeStep = 0.000001, VolumeMax = 10000,
        };
        r.Tick();
        var o = Assert.Single(r.G.Inner.Orders);
        Assert.True(o.VolumeUnits * o.StopLossPips * 0.1 <= 50.000001);
    }

    [Fact]
    public void StrategyOrder_FutureQuoteIsRejected()
    {
        var r = new Rig();
        r.G.QuoteOffsetSeconds = 30;
        r.Tick();
        Assert.Empty(r.G.Inner.Orders);
    }

    [Fact]
    public void AnyUncertainOrder_BlocksFurtherPortfolioEntries()
    {
        var r = new Rig();
        r.Engine.Ledger.TryReserve("uncertain", "OTHER", r.Now);
        r.Tick();
        Assert.Empty(r.G.Inner.Orders);
        r.Engine.CmdExecutionTest(r.Now, "US500");
        Assert.Empty(r.G.Inner.Orders);
    }

    [Fact]
    public void EquityPeak_IsDurableEvenWithoutSignalsOrRiskEvents()
    {
        var r = new Rig(start: false);
        r.G.Inner.EquityOffset = 1000;
        r.Tick(6);
        r.G.Inner.EquityOffset = 400;
        var restarted = new Rig(r.Store, r.G, start: false);
        Assert.True(restarted.Engine.Risk.State.MaxDrawdownHit);
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    public void NonfiniteAccount_IsInvalid(double value)
    {
        Assert.False(new AccountSnapshot { Number = 1, Balance = value, Equity = value }.IsValid);
    }

    [Fact]
    public void InfiniteEntryPrice_IsRejectedBySizer()
    {
        var r = new Rig();
        Assert.False(PositionSizer.Size(r.G.Inner.Specs["US500"], 50, 10, double.PositiveInfinity, 0, 0).Ok);
    }

    [Fact]
    public void EmergencyStop_StillClosesOwnWhenEquityIsNonPositive()
    {
        var r = new Rig();
        r.G.Inner.Positions.Add(Position(99));
        r.G.Inner.EquityOffset = -10000;
        r.Engine.CmdEmergencyStop(r.Now, "zero equity");
        Assert.Empty(r.G.Inner.Positions);
    }

    [Fact]
    public void BrokerRejectedModify_ClosesExecutionTestWithoutWaitingForTimeout()
    {
        var r = new Rig(start: false);
        r.G.RejectModify = true;
        r.Engine.CmdExecutionTest(r.Now, "US500");
        r.Tick(5);
        r.Tick(20);
        Assert.Empty(r.G.Inner.Positions);
        Assert.DoesNotContain(r.Engine.LastExecutionTest, x => x.Contains("TEST ZALICZONY"));
    }

    [Fact]
    public void NonfiniteVolumeStep_IsRejectedBySizer()
    {
        var spec = new InstrumentSpec
        {
            Name = "US500", AssetClass = AssetClass.Index, PipSize = 0.1, TickSize = 0.01, PipValuePerUnit = 0.1,
            VolumeMin = 0.1, VolumeStep = double.PositiveInfinity, VolumeMax = 100,
        };
        Assert.False(PositionSizer.Size(spec, 50, 10, 5000, 0, 0).Ok);
    }

    [Fact]
    public void MissingRequiredNewsCalendar_BlocksStrategyAndTest()
    {
        var r = new Rig();
        r.Engine.Config.News.Enabled = true;
        r.Engine.Config.News.RequireCalendar = true;
        r.Tick();
        r.Engine.CmdExecutionTest(r.Now, "US500");
        Assert.Empty(r.G.Inner.Orders);
    }

    [Fact]
    public void EmergencyReset_RequiresConfirmedLiquidation()
    {
        var r = new Rig();
        r.G.Inner.Positions.Add(Position(99));
        r.G.FailClose = true;
        r.Engine.CmdEmergencyStop(r.Now, "test");
        r.Engine.CmdResetEmergency(r.Now);
        Assert.Equal(EngineMode.EmergencyStopped, r.Engine.Mode);
    }

    [Fact]
    public void ExecutionTest_RoundedModificationMustActuallyTightenStop()
    {
        var r = new Rig(start: false);
        r.G.Inner.Specs["US500"] = new InstrumentSpec
        {
            Name = "US500", AssetClass = AssetClass.Index, PipSize = 0.1, TickSize = 1000,
            PipValuePerUnit = 0.1, VolumeMin = 0.1, VolumeStep = 0.1, VolumeMax = 1000,
        };
        r.Engine.CmdExecutionTest(r.Now, "US500");
        for (int i = 0; i < 45; i++) r.Tick(5);
        Assert.DoesNotContain(r.Engine.LastExecutionTest, x => x.Contains("TEST ZALICZONY"));
        Assert.Empty(r.G.Inner.Modifications);
        Assert.Empty(r.G.Inner.Positions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnclosedOwnPositionWithoutTarget_BlocksAllNewEntries(bool executionTest)
    {
        var r = new Rig();
        r.G.Inner.AddInstrument("OTHER", AssetClass.Index, 5000, 0.1, 0.1, 0.1, 0.1);
        r.G.Inner.Positions.Add(new PositionInfo
        {
            Id = 99, Symbol = "OTHER", Label = "APEX", Direction = TradeDirection.Long,
            VolumeUnits = 0.1, EntryPrice = 5000, StopLoss = 4999, CurrentPrice = 5000,
        });
        r.G.FailClose = true;
        if (executionTest) r.Engine.CmdExecutionTest(r.Now, "US500");
        else r.Tick();
        Assert.Empty(r.G.Inner.Orders);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void NonfiniteOrNegativeProposalRisk_IsRejected(double risk)
    {
        var r = new RiskManager(new RiskSettings());
        var acc = new FakeBroker().GetAccount();
        Assert.False(r.EvaluateNewTrade(new TradeProposal { RiskAmount = risk, Notional = 1, EstimatedMargin = 0 }, acc, Array.Empty<OpenRisk>()).Allowed);
    }
}
