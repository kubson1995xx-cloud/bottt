using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Apex.Core.Performance;
using Apex.Core.Adaptive;
using Apex.Core.Bridge;
using Apex.Core.Engine;
using Apex.Core.Model;
using Apex.Core.Safety;
using Apex.Core.Strategies;
using Apex.Desktop.Services;
using Xunit;

namespace Apex.Tests;

public sealed class RecoveryTests
{
    internal static readonly DateTime Now = new(2026, 10, 26, 10, 0, 0, DateTimeKind.Utc);
    internal static ClosedPositionInfo Closed(long id = 42, string label = "APEX") => new()
    {
        PositionId = id, ClosingDealId = id + 100000, Label = label, Symbol = "US500", Direction = TradeDirection.Long,
        Volume = 2, EntryPrice = 4000, ExitPrice = 4010, EntryTimeUtc = Now.AddHours(-2), ExitTimeUtc = Now,
        GrossProfit = 20, Commission = -3, Swap = -2, NetProfit = 15,
    };
    internal static PositionInfo Open(long id = 42, string label = "APEX") => new()
    {
        Id = id, Label = label, Symbol = "US500", Direction = TradeDirection.Long, VolumeUnits = 2,
        EntryPrice = 4000, EntryTimeUtc = Now.AddHours(-2), StopLoss = 3990, TakeProfit = 4020,
    };
    internal static TradingEngine Engine(IBrokerGateway broker, IKeyValueStore store, ITradeJournal journal)
    {
        var engine = new TradingEngine(new EngineConfig(), new StrategyBook(Scenarios.DipOnlyProfile(), false),
            broker, (IMarketData)broker, new BarStore(), store, new NullLog(), journal);
        engine.SetGuard(new AccountGuardResult { Allowed = true });
        engine.Initialize(Now);
        return engine;
    }
    [Fact]
    public void DuplicateClosedEventAndReplayCountOnceAcrossRestart()
    {
        string dir = Path.Combine(Path.GetTempPath(), "apex-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            var broker = new HistoryBroker { Now = Now };
            var store = new FileStore(Path.Combine(dir, "state"));
            var journal = new FileJournal(Path.Combine(dir, "journal"));
            var engine = Engine(broker, store, journal);
            broker.History.Add(Closed());
            engine.OnPositionClosed(Closed()); engine.OnPositionClosed(Closed()); engine.SaveState(Now, true);
            var restarted = Engine(broker, new FileStore(Path.Combine(dir, "state")), new FileJournal(Path.Combine(dir, "journal")));
            restarted.OnPositionClosed(Closed()); restarted.SaveState(Now, true);
            Assert.Single(restarted.RecentTrades);
            Assert.Equal(15, restarted.GetStatus(Now).RealizedTotal);
            Assert.Equal(2, File.ReadAllLines(Path.Combine(dir, "journal", "transakcje.csv")).Length);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    [Fact]
    public void MissingOfflinePositionKeepsPlanUntilEconomicReplay()
    {
        var broker = new FakeBroker { Now = Now };
        broker.Positions.Add(Open());
        var store = new MemoryStore();
        var engine = Engine(broker, store, new NullJournal());
        broker.Positions.Clear();
        engine.Reconcile(Now, "offline");
        Assert.True(engine.Plans.ContainsKey(42));
    }

    internal sealed class HistoryBroker : FakeBroker, IBrokerHistory
    {
        public bool Complete = true;
        public bool HistoryFails;
        public bool RejectClose;
        public string Generation = "connection-1";
        public readonly List<ClosedPositionInfo> History = new();
        public override BrokerHistorySnapshot GetHistory() => HistoryFails ? throw new IOException("history offline") : new()
        { AccountNumber = AccountNumber, Generation = Generation, Complete = Complete, RequiredCoverage = true, Closed = History.ToList(), Positions = GetPositions() };
        public override OrderOutcome ClosePosition(long id, double? units)
        {
            if (!RejectClose) return base.ClosePosition(id, units);
            Closes.Add(id);
            return OrderOutcome.Uncertain(BrokerErrorKind.Timeout, "synthetic timeout");
        }
    }

    [Fact]
    public void ReplayOfflineClosureRestoresNetCommissionSwap()
    {
        var broker = new HistoryBroker { Now = Now };
        var store = new MemoryStore();
        broker.Positions.Add(Open());
        var engine = Engine(broker, store, new NullJournal());
        engine.SaveState(Now, true);
        broker.Positions.Clear(); broker.History.Add(Closed());
        var restarted = Engine(broker, store, new NullJournal());
        var record = Assert.Single(restarted.RecentTrades);
        Assert.Equal(15, record.NetProfit); Assert.Equal(-3, record.Commission); Assert.Equal(-2, record.Swap);
        Assert.Equal(0, record.InitialRiskMoney); // reconstructed plan is not an entry-risk measurement
        Assert.True(double.IsNaN(record.RMultiple));
        Assert.Empty(restarted.Plans);
    }

    [Fact]
    public void ForeignAndIncompleteHistoryCannotCertifySync()
    {
        var broker = new HistoryBroker { Now = Now, Complete = false };
        broker.History.Add(Closed(43, "APEX-other"));
        var engine = Engine(broker, new MemoryStore(), new NullJournal());
        engine.CmdStart();
        Assert.Equal(EngineMode.Stopped, engine.Mode);
        Assert.Empty(engine.RecentTrades);
        broker.Complete = true; engine.OnTimer(Now.AddSeconds(2)); engine.CmdStart();
        Assert.Equal(EngineMode.Running, engine.Mode);
        Assert.Empty(engine.RecentTrades);
    }

    [Fact]
    public void HistoryFailureAndReconnectStayStopped()
    {
        var broker = new HistoryBroker { Now = Now };
        var engine = Engine(broker, new MemoryStore(), new NullJournal()); engine.CmdStart();
        broker.HistoryFails = true; engine.OnTimer(Now.AddSeconds(2));
        Assert.Equal(EngineMode.Stopped, engine.Mode);
        broker.HistoryFails = false; broker.Generation = "connection-2"; engine.OnTimer(Now.AddSeconds(4));
        Assert.Equal(EngineMode.Stopped, engine.Mode);
        engine.CmdStart(); Assert.Equal(EngineMode.Running, engine.Mode);
        broker.Connected = false; engine.OnTimer(Now.AddSeconds(6));
        broker.Connected = true; broker.Generation = "connection-3"; engine.OnTimer(Now.AddSeconds(8));
        Assert.Equal(EngineMode.Stopped, engine.Mode);
    }

    [Fact]
    public void OldGatewayCannotCertifySync()
    {
        var engine = Engine(new LegacyBroker(), new MemoryStore(), new NullJournal());
        engine.CmdStart(); Assert.Equal(EngineMode.Stopped, engine.Mode);
    }

    internal sealed class LegacyBroker : IBrokerGateway, IMarketData
    {
        readonly FakeBroker inner = new() { Now = Now };
        public bool IsConnected => inner.IsConnected;
        public AccountSnapshot GetAccount() => inner.GetAccount();
        public IReadOnlyList<PositionInfo> GetPositions() => inner.GetPositions();
        public OrderOutcome OpenMarket(OrderRequest r) => inner.OpenMarket(r);
        public OrderOutcome ModifyStopLoss(long id, double sl) => inner.ModifyStopLoss(id, sl);
        public OrderOutcome ClosePosition(long id, double? units) => inner.ClosePosition(id, units);
        public double EstimateMargin(string s, TradeDirection d, double u) => inner.EstimateMargin(s, d, u);
        public InstrumentSpec? GetSpec(string s) => inner.GetSpec(s);
        public Quote? GetQuote(string s) => inner.GetQuote(s);
        public bool IsMarketOpen(string s, DateTime t, out TimeSpan close) => inner.IsMarketOpen(s, t, out close);
    }
    internal sealed class FaultStore : IKeyValueStore
    {
        readonly IKeyValueStore inner;
        public int FailOnSet;
        public bool AfterWrite;
        public FaultStore(IKeyValueStore inner) => this.inner = inner;
        public string? Get(string key) => inner.Get(key);
        public void Set(string key, string value)
        {
            bool fail = FailOnSet > 0 && --FailOnSet == 0;
            if (fail && !AfterWrite) throw new IOException("crash before state commit");
            inner.Set(key, value);
            if (fail) throw new IOException("crash after state commit");
        }
        public void Flush() => inner.Flush();
    }
    internal sealed class FaultJournal : IRecoveryJournal
    {
        readonly FileJournal inner;
        public bool FailBefore, FailAfter;
        public FaultJournal(FileJournal inner) => this.inner = inner;
        public IReadOnlyList<TradeRecord> ClosedRecords => inner.AllRecent();
        public void TradeClosed(TradeRecord r)
        {
            if (FailBefore) throw new IOException("before journal commit");
            inner.TradeClosed(r);
            if (FailAfter) throw new IOException("after journal commit");
        }
        public void TradeOpened(Apex.Core.Management.TradePlan p, double v, string s) { }
        public void Signal(string s) { }
        public void Event(string s) { }
    }

    [Theory]
    [InlineData(0, false, true, false)]
    [InlineData(0, false, false, true)]
    [InlineData(1, false, false, false)]
    [InlineData(1, true, false, false)]
    [InlineData(2, false, false, false)]
    [InlineData(2, true, false, false)]
    public void JournalCrashBeforeAndAfterAppendRecoversExactlyOnce(int failSet, bool afterWrite, bool before, bool after)
    {
        string dir = Path.Combine(Path.GetTempPath(), "apex-crash-" + Guid.NewGuid().ToString("N"));
        try
        {
            var broker = new HistoryBroker { Now = Now };
            broker.Positions.Add(Open());
            var store = new FaultStore(new FileStore(Path.Combine(dir, "state")));
            var journal = new FaultJournal(new FileJournal(Path.Combine(dir, "journal")));
            var engine = Engine(broker, store, journal);
            store.FailOnSet = failSet; store.AfterWrite = afterWrite; journal.FailBefore = before; journal.FailAfter = after;
            broker.Positions.Clear(); broker.History.Add(Closed());
            try { engine.OnPositionClosed(Closed()); } catch (IOException) { }
            var restarted = Engine(broker, new FileStore(Path.Combine(dir, "state")), new FileJournal(Path.Combine(dir, "journal")));
            restarted.OnPositionClosed(Closed()); restarted.SaveState(Now, true);
            Assert.Single(restarted.RecentTrades);
            Assert.Equal(15, restarted.GetStatus(Now).RealizedTotal);
            Assert.Equal(2, File.ReadAllLines(Path.Combine(dir, "journal", "transakcje.csv")).Length);
            Assert.Empty(restarted.Plans);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void FullCloseIntentRetriesAfterRestart()
    {
        var broker = new HistoryBroker { Now = Now, RejectClose = true };
        broker.Positions.Add(Open()); broker.Positions.Add(Open(43, "APEX-foreign"));
        var store = new MemoryStore(); var engine = Engine(broker, store, new NullJournal());
        engine.CmdCloseAllOwn(Now);
        Assert.Single(broker.Closes);
        var restarted = Engine(broker, store, new NullJournal());
        restarted.OnTimer(Now.AddMinutes(1));
        Assert.Equal(new long[] {42, 42}, broker.Closes);
        Assert.Equal(EngineMode.Stopped, restarted.Mode);
    }

    [Fact]
    public void FullCloseStillWorksOnStorageFault()
    {
        var broker = new HistoryBroker { Now = Now };
        broker.Positions.Add(Open()); broker.Positions.Add(Open(43, "APEX-foreign"));
        var store = new FaultStore(new MemoryStore()); var engine = Engine(broker, store, new NullJournal());
        store.FailOnSet = 1; engine.CmdCloseAllOwn(Now);
        Assert.Equal(new long[] {42}, broker.Closes); Assert.Single(broker.Positions);
        engine.CmdStart(); Assert.NotEqual(EngineMode.Running, engine.Mode);
    }

    [Fact]
    public void CorruptRecoveryStateBlocksNewRisk()
    {
        var broker = new HistoryBroker { Now = Now };
        var store = new MemoryStore(); store.Set("APEX APEX 123456", "{broken");
        var engine = Engine(broker, store, new NullJournal()); engine.CmdStart();
        Assert.NotEqual(EngineMode.Running, engine.Mode);
        Assert.Equal("{broken", store.Get("APEX APEX 123456"));
    }

    [Fact]
    public void PartialDealsAggregateOnlyWhenPositionFullyClosed()
    {
        var broker = new HistoryBroker { Now = Now };
        broker.Positions.Add(Open()); var store = new MemoryStore(); var engine = Engine(broker, store, new NullJournal());
        broker.History.Add(HistoryIntegrationTests.Deal(deal: 1).ToInfo());
        broker.Positions.Clear(); broker.Positions.Add(new PositionInfo { Id = 42, Label = "APEX", Symbol = "US500", VolumeUnits = 1, EntryPrice = 4000, StopLoss = 3990, TakeProfit = 4020 });
        engine.OnTimer(Now.AddSeconds(2));
        Assert.Equal(7.5, engine.GetStatus(Now).RealizedTotal); Assert.Empty(engine.RecentTrades); Assert.Single(engine.Plans);
        engine.SaveState(Now, true); var restarted = Engine(broker, store, new NullJournal());
        broker.History.Add(HistoryIntegrationTests.Deal(deal: 2).ToInfo()); broker.Positions.Clear();
        restarted.OnTimer(Now.AddSeconds(4)); restarted.OnTimer(Now.AddSeconds(6));
        var final = Assert.Single(restarted.RecentTrades); Assert.Equal(2, final.Volume);
        Assert.Equal(15, final.NetProfit); Assert.Equal(-3, final.Commission); Assert.Equal(-2, final.Swap);
        Assert.Equal(15, restarted.GetStatus(Now).RealizedTotal); Assert.Empty(restarted.Plans);
    }

    [Fact]
    public void CloseHintCannotFinalizeUsingPartialOrEmptyHistory()
    {
        var broker = new HistoryBroker { Now = Now, Complete = false };
        var engine = Engine(broker, new MemoryStore(), new NullJournal());
        engine.OnPositionClosed(Closed());
        Assert.Empty(engine.RecentTrades); Assert.Equal(0, engine.GetStatus(Now).RealizedTotal);
    }

    [Fact]
    public void LegacyTotalsOverlapBlocksWithoutDestroyingEmergencyOrPlans()
    {
        var broker = new HistoryBroker { Now = Now }; broker.Positions.Add(Open());
        var store = new MemoryStore(); var initial = Engine(broker, store, new NullJournal()); initial.SaveState(Now, true);
        string json = store.Get("APEX APEX 123456")!;
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        node["SchemaVersion"] = 1; node["TradesTotal"] = 500; node["RealizedTotal"] = 1234;
        node["Mode"] = "EmergencyStopped"; string legacy = node.ToJsonString(); store.Set("APEX APEX 123456", legacy);
        broker.Positions.Clear(); broker.History.Add(Closed());
        var engine = Engine(broker, store, new NullJournal());
        Assert.Equal(EngineMode.EmergencyStopped, engine.Mode); Assert.Single(engine.Plans);
        Assert.Equal(1234, engine.GetStatus(Now).RealizedTotal); Assert.Equal(legacy, store.Get("APEX APEX 123456"));
    }
}
