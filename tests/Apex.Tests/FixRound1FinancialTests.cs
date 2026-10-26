using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Apex.Core.Engine;
using Apex.Core.Model;
using Apex.Core.Performance;
using Apex.Desktop.Services;
using Xunit;

namespace Apex.Tests;
public sealed partial class FixRound1Tests
{
    [Theory]
    [InlineData("RealizedTotal", "NaN")]
    [InlineData("RealizedTotal", "Infinity")]
    [InlineData("RealizedTotal", "-Infinity")]
    [InlineData("NetProfit", "NaN")]
    [InlineData("Commission", "Infinity")]
    [InlineData("Swap", "-Infinity")]
    [InlineData("GrossProfit", "NaN")]
    [InlineData("Volume", "Infinity")]
    [InlineData("InitialRiskMoney", "NaN")]
    [InlineData("raw-sum", "overflow")]
    public void NonFiniteAuthoritativeMoneyBlocksStartPreservesFileAndAllowsProtectiveClose(string field, string value)
    {
        string dir = TempDir();
        try
        {
            var broker = new RecoveryTests.HistoryBroker { Now = Now };
            broker.History.Add(RecoveryTests.Closed()); broker.History.Add(RecoveryTests.Closed(44));
            var store = new FileStore(dir); RecoveryTests.Engine(broker, store, new NullJournal());
            var state = JsonNode.Parse(store.Get(Key)!)!;
            if (field == "RealizedTotal") state[field] = value;
            else if (field == "raw-sum")
            {
                foreach (var d in state["ClosingDeals"]!.AsArray()) d!["NetProfit"] = double.MaxValue;
                state["RealizedTotal"] = 0; // finite total must not hide an overflowing sum
            }
            else state["ClosedRecords"]![0]![field] = value;
            string corrupt = state.ToJsonString(); store.Set(Key, corrupt);
            broker.History.Clear(); broker.Positions.Add(RecoveryTests.Open()); broker.Positions.Add(RecoveryTests.Open(43, "APEX-other"));
            var restarted = RecoveryTests.Engine(broker, store, new NullJournal()); restarted.CmdStart();
            Assert.False(restarted.HistoryReady); Assert.NotEqual(EngineMode.Running, restarted.Mode);
            Assert.Equal(corrupt, store.Get(Key));
            restarted.CmdCloseAllOwn(Now); Assert.Equal(new long[] { 42 }, broker.Closes);
            Assert.Equal(corrupt, store.Get(Key));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    static ClosedPositionInfo MoneyDeal(long deal, double net = 1, double commission = 0, double volume = 1) => new()
    {
        PositionId = 42, ClosingDealId = deal, Label = "APEX", Symbol = "US500", Direction = TradeDirection.Long,
        EntryPrice = 4000, ExitPrice = 4010, EntryTimeUtc = Now.AddHours(-2), ExitTimeUtc = Now,
        Volume = volume, NetProfit = net, GrossProfit = net, Commission = commission,
    };
    [Theory]
    [InlineData("net")]
    [InlineData("commission")]
    [InlineData("volume")]
    [InlineData("raw-nan")]
    public void InvalidOrOverflowingReplayCannotPublishAnyNonFiniteAccounting(string fault)
    {
        var broker = new RecoveryTests.HistoryBroker { Now = Now };
        var store = new MemoryStore(); var e = RecoveryTests.Engine(broker, store, new NullJournal());
        string before = store.Get(Key)!;
        broker.History.Add(MoneyDeal(1, net: fault == "net" ? double.MaxValue : fault == "raw-nan" ? double.NaN : 1,
            commission: fault == "commission" ? double.MaxValue : 0, volume: fault == "volume" ? double.MaxValue : 1));
        broker.History.Add(MoneyDeal(2, net: fault == "net" ? double.MaxValue : 1,
            commission: fault == "commission" ? double.MaxValue : 0, volume: fault == "volume" ? double.MaxValue : 1));
        e.OnPositionClosed(broker.History[0]); e.CmdStart();
        Assert.False(e.HistoryReady); Assert.NotEqual(EngineMode.Running, e.Mode);
        Assert.Equal(0, e.GetStatus(Now).RealizedTotal); Assert.Empty(e.RecentTrades);
        var persisted = JsonNode.Parse(store.Get(Key)!)!;
        Assert.Empty(persisted["ClosingDeals"]!.AsArray()); Assert.Empty(persisted["ClosedRecords"]!.AsArray());
    }
    [Theory]
    [InlineData(12, "NaN")]
    [InlineData(13, "Infinity")]
    [InlineData(14, "NaN")]
    [InlineData(15, "Infinity")]
    [InlineData(16, "-Infinity")]
    [InlineData(17, "NaN")]
    public void NonFiniteCsvEconomicsAreUnhealthyAndPreserved(int cell, string value)
    {
        string dir = TempDir(); Directory.CreateDirectory(dir);
        try
        {
            var record = new TradeRecord { PositionId = 42, Symbol = "US500", Volume = 1, NetProfit = 1 };
            var cells = record.ToCsv().Split(';'); cells[cell] = value;
            string suspect = TradeRecord.CsvHeader + Environment.NewLine + string.Join(';', cells) + Environment.NewLine;
            string path = Path.Combine(dir, "transakcje.csv"); File.WriteAllText(path, suspect);
            var journal = new FileJournal(dir); Assert.False(journal.IsHealthy);
            Assert.Throws<IOException>(() => journal.TradeClosed(record));
            Assert.Equal(suspect, File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, true); }
    }
}
