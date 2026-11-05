using System;
using System.IO;
using Apex.Core.Bridge;
using Apex.Platforms.CTrader;
using Xunit;

namespace Apex.Platform.Tests
{
    public class CommandJournalTests
    {
        private static ResultMsg Filled(string id) => new() { Id = id, Status = "filled", PositionId = 42, Price = 1.2 };

        [Fact]
        public void Task2B_LegacyTwentyThousandTerminalsCompactWithoutLosingReplay()
        {
            var entries = new System.Collections.Generic.Dictionary<string, CommandJournal.Entry>();
            for (int i = 0; i < 20000; i++) entries.Add("old-" + i, new() { Fingerprint = "open " + i, Signal = "signal-" + i, Result = Filled("old-" + i) });
            string saved = System.Text.Json.JsonSerializer.Serialize(entries, BridgeProtocol.Json);
            var journal = new CommandJournal(saved, s => saved = s);
            int executions = 0;
            Assert.Equal("filled", journal.Run("new", "close", null, () => { executions++; return Filled("new"); }).Status);
            Assert.True(journal.CachedCount < 1000);
            Assert.Equal(20001, journal.Count);
            var restored = new CommandJournal(saved, _ => { });
            Assert.True(restored.Healthy);
            Assert.Equal(42, restored.Lookup("old-0")!.PositionId);
            Assert.Equal("filled", restored.Run("old-0", "open 0", "signal-0", () => { executions++; return Filled("old-0"); }).Status);
            Assert.Equal("filled", restored.Run("fresh-id", "open 0", "signal-0", () => { executions++; return Filled("fresh-id"); }).Status);
            Assert.Equal("InvalidRequest", restored.Run("old-0", "different", null, () => Filled("old-0")).Error);
            Assert.Equal("InvalidRequest", restored.Run("fresh-conflict", "different", "signal-0", () => Filled("fresh-conflict")).Error);
            Assert.Equal(1, executions);
        }

        [Fact]
        public void RepeatedCommandExecutesOnlyOnceAndReturnsOriginalResult()
        {
            int calls = 0;
            var journal = new CommandJournal(null, _ => { });
            journal.Run("one", "open 1000", "signal", () => { calls++; return Filled("one"); });
            var result = journal.Run("one", "open 1000", "signal", () => { calls++; return Filled("one"); });
            Assert.Equal(1, calls);
            Assert.Equal(42, result.PositionId);
        }

        [Fact]
        public void ChangedPayloadForSameIdIsRejected()
        {
            var journal = new CommandJournal(null, _ => { });
            journal.Run("one", "close 1000", null, () => Filled("one"));
            Assert.Equal("rejected", journal.Run("one", "close 2000", null, () => Filled("one")).Status);
        }

        [Fact]
        public void SignalCannotExecuteAgainAfterRestartWithDifferentCommandId()
        {
            string saved = "";
            var journal = new CommandJournal(null, s => saved = s);
            journal.Run("old", "open 1000", "signal", () => Filled("old"));
            var restored = new CommandJournal(saved, _ => { });
            int calls = 0;
            var result = restored.Run("new", "open 1000", "signal", () => { calls++; return Filled("new"); });
            Assert.Equal(0, calls);
            Assert.Equal("new", result.Id);
            Assert.Equal(42, result.PositionId);
        }

        [Fact]
        public void ReservationIsDurableBeforeBrokerAction()
        {
            string saved = "";
            var journal = new CommandJournal(null, s => saved = s);
            bool reserved = false;
            journal.Run("one", "open", "signal", () =>
            {
                var restored = new CommandJournal(saved, _ => { });
                reserved = restored.Run("one", "open", "signal", () => Filled("one")).Status == "uncertain";
                return Filled("one");
            });
            Assert.True(reserved);
        }

        [Fact]
        public void FailedReservationNeverCallsBroker()
        {
            int calls = 0;
            var journal = new CommandJournal(null, _ => throw new IOException("disk"));
            var result = journal.Run("one", "open", null, () => { calls++; return Filled("one"); });
            Assert.Equal(0, calls);
            Assert.Equal("rejected", result.Status);
        }

        [Fact]
        public void FailedResultSaveCannotBeRetriedAndRemainsUncertain()
        {
            int writes = 0, calls = 0;
            var journal = new CommandJournal(null, _ => { if (++writes > 1) throw new IOException("disk"); });
            var result = journal.Run("one", "open", "signal", () => { calls++; return Filled("one"); });
            Assert.Equal("uncertain", result.Status);
            Assert.Equal("uncertain", journal.Run("one", "open", "signal", () => { calls++; return Filled("one"); }).Status);
            Assert.Equal(1, calls);
        }

        [Fact]
        public void CorruptJournalFailsClosed()
        {
            int calls = 0;
            var journal = new CommandJournal("broken", _ => { });
            var result = journal.Run("one", "open", null, () => { calls++; return Filled("one"); });
            Assert.Equal(0, calls);
            Assert.Equal("rejected", result.Status);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void FullLiquidationDoesNotDependOnJournalStorageButNewRiskDoes(bool corrupt)
        {
            int closes = 0, opens = 0;
            var journal = new CommandJournal(corrupt ? "broken" : null, _ => throw new IOException("disk"));
            var method = typeof(CommandJournal).GetMethod("RunFullClose", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(method);
            Func<ResultMsg> close = () => { closes++; return Filled("close-42"); };
            var result = (ResultMsg)method!.Invoke(journal, new object[] { "close-42", close })!;
            Assert.Equal("filled", result.Status);
            Assert.Equal(1, closes);
            journal.Run("open-42", "open", "new-signal", () => { opens++; return Filled("open-42"); });
            Assert.Equal(0, opens);
        }

        [Fact]
        public void BrokerExceptionLeavesUncertainReservationAcrossRestart()
        {
            string saved = "";
            var journal = new CommandJournal(null, s => saved = s);
            Assert.Equal("uncertain", journal.Run("one", "open", "signal", () => throw new Exception("transport")).Status);
            var restored = new CommandJournal(saved, _ => { });
            Assert.Equal("uncertain", restored.Run("one", "open", "signal", () => Filled("one")).Status);
        }
    }
}
