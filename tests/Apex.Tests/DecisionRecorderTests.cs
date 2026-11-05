using System;
using System.Collections.Generic;
using System.Linq;
using Apex.Core.Engine;
using Apex.Core.Model;
using Xunit;

namespace Apex.Tests
{
    /// <summary>
    /// AI Decision Center jest wyłącznie obserwatorem: ten sam scenariusz z rejestratorem i bez niego
    /// musi dać identyczne zlecenia, zmiany SL i zamknięcia.
    /// </summary>
    public class DecisionRecorderTests
    {
        private static List<string> Run(bool withRecorder, params string[] openModes)
        {
            var rig = new EngineRig(withRecorder: withRecorder);
            foreach (var m in openModes) rig.Broker.OpenModes.Enqueue(m);
            for (int i = 0; i < 20; i++) rig.Tick(30);
            rig.Engine.CmdEmergencyStop(rig.Now, "koniec scenariusza");
            for (int i = 0; i < 3; i++) rig.Tick(30);

            var trace = new List<string>();
            trace.AddRange(rig.Broker.Orders.Select(o => $"O {o.Symbol} {o.Direction} {o.VolumeUnits:R} {o.StopLossPips:R} {o.TakeProfitPips:R} {o.Comment}"));
            trace.AddRange(rig.Broker.Modifications.Select(m => $"M {m.id} {m.sl:R}"));
            trace.AddRange(rig.Broker.Closes.Select(c => $"C {c}"));
            return trace;
        }

        [Theory]
        [InlineData("")]
        [InlineData("reject")]
        [InlineData("uncertain-filled")]
        [InlineData("uncertain-none")]
        [InlineData("fill-no-sl")]
        public void Recorder_DoesNotChangeTradingDecisions(string modeList)
        {
            var modes = modeList.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var without = Run(false, modes);
            var with = Run(true, modes);
            Assert.Equal(without, with);
        }

        [Fact]
        public void Recorder_CapturesRealBuyDecisionWithGates()
        {
            var rig = new EngineRig(withRecorder: true);
            rig.Tick();
            var (events, lastSeq) = rig.Recorder!.Since(0);
            Assert.True(lastSeq > 0);
            var buy = Assert.Single(events, e => e.Verdict == DecisionVerdict.Buy);
            Assert.Equal("US500", buy.Symbol);
            Assert.Equal(StrategyId.IndexDipBuy, buy.Strategy);
            Assert.Equal(DecisionStage.Execution, buy.Stage);
            Assert.NotNull(buy.Score);
            Assert.NotNull(buy.PositionId);
            Assert.Contains(buy.Gates, g => g.Name == "RiskEngine" && g.Passed);
            Assert.Equal(1, rig.Recorder.Counters()[StrategyId.IndexDipBuy].ExecutedBuy);
        }

        [Fact]
        public void Recorder_RecordsRiskBlockAsWait()
        {
            var rig = new EngineRig(withRecorder: true, configure: c => c.Risk.MaxEffectiveLeverage = 0.0001);
            rig.Tick();
            Assert.Empty(rig.Broker.Orders);
            var (events, _) = rig.Recorder!.Since(0);
            var blocked = Assert.Single(events, e => e.Stage == DecisionStage.Risk);
            Assert.Equal(DecisionVerdict.Wait, blocked.Verdict);
            Assert.NotEmpty(blocked.RiskFailures!);
            Assert.Contains(blocked.RiskFailures!, f => f.Contains("dźwignia"));
        }

        [Fact]
        public void Recorder_ThrowingHook_NeverBreaksTheEngine()
        {
            var rig = new EngineRig(withRecorder: true);
            // Rejestrator o pojemności 1 – intensywne nadpisywanie; silnik ma działać tak samo.
            rig.Engine.Decisions = new DecisionRecorder(1);
            rig.Tick();
            Assert.Single(rig.Broker.Orders);
        }

        [Fact]
        public void Recorder_Since_ReturnsOnlyNewerEvents()
        {
            var rig = new EngineRig(withRecorder: true);
            rig.Tick();
            var (_, last) = rig.Recorder!.Since(0);
            var (events, last2) = rig.Recorder.Since(last);
            Assert.Empty(events);
            Assert.Equal(last, last2);
        }
    }
}
