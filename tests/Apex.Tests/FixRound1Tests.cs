using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Apex.Core.Engine;
using Apex.Core.Model;
using Apex.Desktop.Services;
using Xunit;

namespace Apex.Tests;

public sealed partial class FixRound1Tests
{
    const string Key = "APEX APEX 123456";
    static readonly DateTime Now = RecoveryTests.Now;
    static string TempDir() => Path.Combine(Path.GetTempPath(), "apex-fix1-" + Guid.NewGuid().ToString("N"));
    sealed class CoverageBroker : FakeBroker
    {
        public bool Required;
        public override BrokerHistorySnapshot GetHistory() => new()
        { AccountNumber = AccountNumber, Generation = "scoped-fixture", Complete = true, RequiredCoverage = Required, Closed = ClosedDeals.ToArray(), Positions = GetPositions() };
    }
    [Theory]
    [InlineData(true, 1, true)]
    [InlineData(false, 1, false)]
    [InlineData(true, 2, false)]
    public void PrestartPartialCanFinalizeOnlyWithTrustedConsistentLifetimeCoverage(bool trusted, double finalVolume, bool finalize)
    {
        var broker = new CoverageBroker { Now = Now, Required = trusted };
        broker.Positions.Add(new PositionInfo { Id = 42, Label = "APEX", Symbol = "US500", Direction = TradeDirection.Long,
            VolumeUnits = 1, EntryPrice = 4000, EntryTimeUtc = Now.AddHours(-1), StopLoss = 3990, TakeProfit = 4020 });
        broker.ClosedDeals.Add(HistoryIntegrationTests.Deal(deal: 1).ToInfo());
        var store = new MemoryStore(); var e = RecoveryTests.Engine(broker, store, new NullJournal()); e.SaveState(Now, true);
        Assert.Single(e.Plans); Assert.Empty(e.RecentTrades);
        var restarted = RecoveryTests.Engine(broker, store, new NullJournal());
        broker.Positions.Clear(); broker.ClosedDeals.Add(HistoryIntegrationTests.Deal(deal: 2, volume: finalVolume).ToInfo());
        restarted.OnTimer(Now.AddSeconds(2)); restarted.SaveState(Now.AddSeconds(2), true);
        if (finalize)
        {
            var record = Assert.Single(restarted.RecentTrades); Assert.Equal(2, record.Volume); Assert.Equal(15, record.NetProfit);
            Assert.Equal(0, record.InitialRiskMoney); Assert.True(double.IsNaN(record.RMultiple));
            Assert.Empty(restarted.Plans); Assert.True(restarted.HistoryReady);
            var replay = RecoveryTests.Engine(broker, store, new NullJournal());
            Assert.Equal(1, replay.GetStatus(Now).TradesTotal); Assert.Equal(15, replay.GetStatus(Now).RealizedTotal);
        }
        else
        {
            Assert.Empty(restarted.RecentTrades); Assert.Single(restarted.Plans); Assert.False(restarted.HistoryReady);
            if (!trusted) Assert.Equal(0, restarted.GetStatus(Now).RealizedTotal);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("rollback")]
    [InlineData("authority-ahead")]
    [InlineData("crash-during-rebuild")]
    public void AuthoritativeRecordsRebuildProjectionEvenAfterOutboxDrained(string loss)
    {
        string dir = TempDir();
        try
        {
            var broker = new RecoveryTests.HistoryBroker { Now = Now };
            broker.History.Add(RecoveryTests.Closed());
            var store = new FileStore(Path.Combine(dir, "state"));
            string journalDir = Path.Combine(dir, "journal"), csv = Path.Combine(journalDir, "transakcje.csv");
            var e = RecoveryTests.Engine(broker, store, new FileJournal(journalDir));
            string old = File.ReadAllText(csv);
            broker.History.Add(RecoveryTests.Closed(44, net: 20)); e.OnTimer(Now.AddSeconds(2));
            Assert.Empty(JsonNode.Parse(store.Get(Key)!)!["ClosedOutbox"]!.AsArray());
            if (loss == "missing" || loss == "crash-during-rebuild") File.Delete(csv);
            else if (loss == "rollback") File.WriteAllText(csv, old);
            else File.WriteAllText(csv, Apex.Core.Performance.TradeRecord.CsvHeader + Environment.NewLine);
            broker.History.Clear(); // reconstruction must come from authority, not the source replay
            if (loss == "crash-during-rebuild")
            {
                var failing = new RecoveryTests.FaultJournal(new FileJournal(journalDir)) { FailAfter = true };
                var interrupted = RecoveryTests.Engine(broker, store, failing);
                Assert.False(interrupted.HistoryReady);
                Assert.Equal(2, File.ReadAllLines(csv).Length); // first authority row published, then throw
            }
            var restarted = RecoveryTests.Engine(broker, store, new FileJournal(journalDir));
            Assert.True(restarted.HistoryReady, restarted.HistoryMessage);
            Assert.Equal(3, File.ReadAllLines(csv).Length);
            Assert.Equal(new[] { "42", "44" }, File.ReadAllLines(csv).Skip(1).Select(x => x.Split(';')[0]).OrderBy(x => x));
            Assert.Equal(2, restarted.GetStatus(Now).TradesTotal); Assert.Equal(35, restarted.GetStatus(Now).RealizedTotal);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    [Theory]
    [InlineData("conflict")]
    [InlineData("unknown-row")]
    [InlineData("corrupt")]
    [InlineData("corrupt-header")]
    [InlineData("empty-file")]
    public void SuspectProjectionIsPreservedAndBlocksRiskButNotFullClose(string fault)
    {
        string dir = TempDir();
        try
        {
            var broker = new RecoveryTests.HistoryBroker { Now = Now };
            broker.History.Add(RecoveryTests.Closed()); broker.History.Add(RecoveryTests.Closed(44));
            var store = new FileStore(Path.Combine(dir, "state"));
            string journalDir = Path.Combine(dir, "journal"), csv = Path.Combine(journalDir, "transakcje.csv");
            RecoveryTests.Engine(broker, store, new FileJournal(journalDir));
            var lines = File.ReadAllLines(csv); var cells = lines[2].Split(';');
            if (fault == "conflict") cells[17] = "999.00";
            if (fault == "unknown-row") cells[0] = "999";
            string suspect = lines[0] + Environment.NewLine + (fault == "corrupt" ? "broken" : string.Join(';', cells)) + Environment.NewLine;
            if (fault == "corrupt-header") suspect = "wrong;header" + Environment.NewLine + string.Join(';', cells) + Environment.NewLine;
            if (fault == "empty-file") suspect = "";
            File.WriteAllText(csv, suspect); broker.History.Clear();
            broker.Positions.Add(RecoveryTests.Open()); broker.Positions.Add(RecoveryTests.Open(43, "APEX-other"));
            var e = RecoveryTests.Engine(broker, store, new FileJournal(journalDir)); e.CmdStart();
            Assert.False(e.HistoryReady); Assert.NotEqual(EngineMode.Running, e.Mode);
            Assert.Equal(suspect, File.ReadAllText(csv));
            e.CmdCloseAllOwn(Now); Assert.Equal(new long[] { 42 }, broker.Closes);
            Assert.Equal(suspect, File.ReadAllText(csv));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void FractionalCsvPublicationCrashDrainsIdenticalOutboxAfterRestart()
    {
        string dir = TempDir();
        try
        {
            var broker = new RecoveryTests.HistoryBroker { Now = Now };
            broker.Positions.Add(RecoveryTests.Open());
            var store = new FileStore(Path.Combine(dir, "state"));
            var journal = new RecoveryTests.FaultJournal(new FileJournal(Path.Combine(dir, "journal")));
            var engine = RecoveryTests.Engine(broker, store, journal);
            engine.Plans[42].Reconstructed = false; engine.Plans[42].RiskAmount = 1.004;
            journal.FailAfter = true;
            broker.Positions.Clear(); broker.History.Add(RecoveryTests.Closed(net: 0.504));
            engine.OnPositionClosed(broker.History[0]);
            Assert.False(engine.HistoryReady);
            var restarted = RecoveryTests.Engine(broker, store, new FileJournal(Path.Combine(dir, "journal")));
            Assert.True(restarted.HistoryReady, restarted.HistoryMessage);
            Assert.Equal(1, restarted.GetStatus(Now).TradesTotal);
            Assert.Equal(0.504, restarted.GetStatus(Now).RealizedTotal);
            Assert.Empty(JsonNode.Parse(store.Get(Key)!)!["ClosedOutbox"]!.AsArray());
            var cells = File.ReadAllLines(Path.Combine(dir, "journal", "transakcje.csv"))[1].Split(';');
            Assert.Equal("1.00", cells[13]); Assert.Equal("0.50", cells[17]); Assert.Equal("0.502", cells[18]);
            Assert.Equal(2, File.ReadAllLines(Path.Combine(dir, "journal", "transakcje.csv")).Length);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
