using System;
using System.Linq;
using Apex.Core.Adaptive;
using Apex.Core.Engine;
using Apex.Core.Execution;
using Apex.Core.Model;
using Apex.Core.Safety;
using Xunit;

namespace Apex.Tests
{
    /// <summary>Testy silnika na atrapie brokera: wejście z SL, ochrona przed duplikatami, restart, niepewne odpowiedzi, bezpieczeństwo.</summary>
    public class EngineTests
    {
        [Fact]
        public void Demo_DipSignal_OpensExactlyOnePositionWithStopLoss()
        {
            var rig = new EngineRig();
            rig.Tick();

            var order = Assert.Single(rig.Broker.Orders);
            Assert.Equal("US500", order.Symbol);
            Assert.Equal(TradeDirection.Long, order.Direction);
            Assert.True(order.StopLossPips > 0, "zlecenie musi mieć SL");
            Assert.Equal("APEX", order.Label);
            Assert.StartsWith("APEX|DB|", order.Comment);
            var pos = Assert.Single(rig.Broker.Positions);
            Assert.True(pos.StopLoss.HasValue);
            Assert.Single(rig.Engine.Plans);
        }

        [Fact]
        public void Sizing_RespectsRiskBudget()
        {
            var rig = new EngineRig();
            rig.Tick();
            var plan = Assert.Single(rig.Engine.Plans.Values);
            // 0,5% z 10 000 = 50; ryzyko pozycji (z poślizgiem i prowizją) nie może go przekroczyć.
            Assert.True(plan.RiskAmount > 0 && plan.RiskAmount <= 50.0 + 1e-6, $"ryzyko {plan.RiskAmount}");
            var order = rig.Broker.Orders[0];
            double lossAtSl = order.VolumeUnits * order.StopLossPips * 0.1; // wartość pipsa 0,1 na jednostkę
            Assert.True(lossAtSl <= 50.0 + 1e-6, $"strata na SL {lossAtSl}");
        }

        [Fact]
        public void RepeatedCycles_DoNotDuplicateTheSignal()
        {
            var rig = new EngineRig();
            for (int i = 0; i < 30; i++) rig.Tick();
            Assert.Single(rig.Broker.Orders);
            Assert.Single(rig.Broker.Positions);
        }

        [Fact]
        public void Restart_WithSavedState_RestoresPlanAndDoesNotDuplicate()
        {
            var rig1 = new EngineRig();
            rig1.Tick();
            Assert.Single(rig1.Broker.Orders);

            var rig2 = new EngineRig(store: rig1.Store, broker: rig1.Broker);
            for (int i = 0; i < 10; i++) rig2.Tick();

            Assert.Single(rig1.Broker.Orders);
            Assert.Single(rig2.Engine.Plans);
            Assert.False(rig2.Engine.Plans.Values.Single().Reconstructed, "plan powinien pochodzić z zapisu, nie z odtworzenia");
        }

        [Fact]
        public void Restart_WithLostState_AdoptsOwnPositionAndDoesNotDuplicate()
        {
            var rig1 = new EngineRig();
            rig1.Tick();
            var rig2 = new EngineRig(store: new MemoryStore(), broker: rig1.Broker);
            for (int i = 0; i < 10; i++) rig2.Tick();

            Assert.Single(rig1.Broker.Orders);
            var plan = Assert.Single(rig2.Engine.Plans.Values);
            Assert.True(plan.Reconstructed);
            Assert.Equal(StrategyId.IndexDipBuy, plan.Strategy);
        }

        [Fact]
        public void UncertainResponse_PositionExists_IsResolvedWithoutSecondOrder()
        {
            var rig = new EngineRig();
            rig.Broker.OpenModes.Enqueue("uncertain-filled");
            rig.Tick();
            Assert.Single(rig.Broker.Orders);
            Assert.Single(rig.Engine.Ledger.Pending);

            for (int i = 0; i < 10; i++) rig.Tick();
            Assert.Single(rig.Broker.Orders);
            Assert.Empty(rig.Engine.Ledger.Pending);
            var plan = Assert.Single(rig.Engine.Plans.Values);
            Assert.False(plan.Reconstructed, "plan powinien powstać z danych sygnału");
            Assert.Equal(SignalState.Executed, rig.Engine.Ledger.Get(plan.SignalId)!.State);
        }

        [Fact]
        public void UncertainResponse_NoPosition_IsWithdrawnAfterTimeoutWithoutRetry()
        {
            var rig = new EngineRig();
            rig.Broker.OpenModes.Enqueue("uncertain-none");
            rig.Tick();
            Assert.Single(rig.Engine.Ledger.Pending);

            rig.Tick(30);
            Assert.Single(rig.Engine.Ledger.Pending); // przed upływem limitu czasu weryfikacji – nadal czekamy
            rig.Tick(40);
            Assert.Empty(rig.Engine.Ledger.Pending);
            for (int i = 0; i < 10; i++) rig.Tick(30);

            Assert.Single(rig.Broker.Orders);
            Assert.Empty(rig.Broker.Positions);
            Assert.Equal(SignalState.Consumed, rig.Engine.Ledger.Entries.Single().State);
        }

        [Fact]
        public void RejectedOrder_IsConsumedAndNotRetried()
        {
            var rig = new EngineRig();
            rig.Broker.OpenModes.Enqueue("reject");
            for (int i = 0; i < 10; i++) rig.Tick(60);
            Assert.Single(rig.Broker.Orders);
            Assert.Empty(rig.Broker.Positions);
            Assert.Equal(SignalState.Consumed, rig.Engine.Ledger.Entries.Single().State);
        }

        [Fact]
        public void Disconnected_SendsNothing_AndRequiresExplicitStartAfterReconnect()
        {
            var rig = new EngineRig();
            rig.Broker.Connected = false;
            for (int i = 0; i < 5; i++) rig.Tick();
            Assert.Empty(rig.Broker.Orders);

            rig.Broker.Connected = true;
            rig.Tick();
            Assert.Empty(rig.Broker.Orders);
            Assert.Equal(EngineMode.Stopped, rig.Engine.Mode);
            rig.Engine.CmdStart(); rig.Tick();
            Assert.Single(rig.Broker.Orders);
        }

        [Fact]
        public void PositionOpenedWithoutStopLoss_IsProtectedImmediately()
        {
            var rig = new EngineRig();
            rig.Broker.OpenModes.Enqueue("fill-no-sl");
            rig.Tick();
            var pos = Assert.Single(rig.Broker.Positions);
            Assert.True(pos.StopLoss.HasValue, "silnik musi natychmiast ustawić SL");
            Assert.Contains(rig.Broker.Modifications, m => m.id == pos.Id);
            Assert.True(pos.StopLoss!.Value < pos.EntryPrice, "SL pozycji długiej poniżej ceny wejścia");
        }

        [Fact]
        public void LiveAccount_ExperimentalStrategy_NeverTrades()
        {
            var rig = new EngineRig(live: true, status: ValidationStatus.Experimental, guardAllowed: true);
            for (int i = 0; i < 5; i++) rig.Tick();
            Assert.Empty(rig.Broker.Orders);
        }

        [Fact]
        public void LiveAccount_BlockedByGuard_NeverTrades()
        {
            var rig = new EngineRig(live: true, status: ValidationStatus.Validated, guardAllowed: false);
            for (int i = 0; i < 5; i++) rig.Tick();
            Assert.Empty(rig.Broker.Orders);
            Assert.StartsWith("Nie można uruchomić", rig.Engine.CmdStart());
        }

        [Fact]
        public void RejectedStrategy_IsNeverLoaded()
        {
            var rig = new EngineRig(status: ValidationStatus.Rejected);
            for (int i = 0; i < 5; i++) rig.Tick();
            Assert.Empty(rig.Broker.Orders);
        }

        [Fact]
        public void ExecutionTest_IsRefusedOnLiveAccount()
        {
            var rig = new EngineRig(live: true, status: ValidationStatus.Validated, guardAllowed: true);
            string msg = rig.Engine.CmdExecutionTest(rig.Now, "US500");
            Assert.Contains("WYŁĄCZNIE na koncie DEMO", msg);
            Assert.Empty(rig.Broker.Orders);
        }

        [Fact]
        public void ExecutionTest_OnDemo_OpensModifiesAndCloses()
        {
            var rig = new EngineRig(configure: c => { });
            rig.Engine.CmdStop(); // bez sygnałów strategii – tylko test ścieżki zleceń
            string msg = rig.Engine.CmdExecutionTest(rig.Now, "US500");
            Assert.Contains("rozpoczęty", msg);
            for (int i = 0; i < 40; i++) rig.Tick(5);

            var order = Assert.Single(rig.Broker.Orders);
            Assert.StartsWith("APEX|XT|", order.Comment);
            Assert.True(order.StopLossPips > 0 && order.TakeProfitPips > 0);
            Assert.Single(rig.Broker.Modifications);
            Assert.Single(rig.Broker.Closes);
            Assert.Empty(rig.Broker.Positions);
            Assert.Contains(rig.Engine.LastExecutionTest, l => l.Contains("TEST ZALICZONY"));
        }

        [Fact]
        public void ManualAndForeignPositions_AreNeverTouched()
        {
            var rig = new EngineRig();
            rig.Broker.Positions.Add(new PositionInfo { Id = 9001, Symbol = "US500", Direction = TradeDirection.Short, VolumeUnits = 1, EntryPrice = 5000, Label = "", Comment = "" });
            rig.Broker.Positions.Add(new PositionInfo { Id = 9002, Symbol = "US500", Direction = TradeDirection.Long, VolumeUnits = 1, EntryPrice = 5000, Label = "InnyBot", Comment = "APEX|DB|abc|v1" });
            rig.Broker.Positions.Add(new PositionInfo { Id = 9003, Symbol = "US500", Direction = TradeDirection.Long, VolumeUnits = 1, EntryPrice = 5000, Label = "APEX-N", Comment = "" });
            for (int i = 0; i < 5; i++) rig.Tick();
            rig.Engine.CmdCloseAllOwn(rig.Now);
            rig.Engine.CmdEmergencyStop(rig.Now, "test");
            for (int i = 0; i < 5; i++) rig.Tick();

            long[] foreign = { 9001, 9002, 9003 };
            Assert.DoesNotContain(rig.Broker.Modifications, m => foreign.Contains(m.id));
            Assert.DoesNotContain(rig.Broker.Closes, id => foreign.Contains(id));
            Assert.Equal(3, rig.Broker.Positions.Count(p => foreign.Contains(p.Id)));
            Assert.DoesNotContain(rig.Engine.Plans.Keys, id => foreign.Contains(id));
        }

        [Fact]
        public void EmergencyStop_ClosesOwnPositionsAndBlocksNewEntries()
        {
            var rig = new EngineRig();
            rig.Tick();
            Assert.Single(rig.Broker.Positions);
            rig.Engine.CmdEmergencyStop(rig.Now, "test");
            for (int i = 0; i < 5; i++) rig.Tick();
            Assert.Empty(rig.Broker.Positions);
            Assert.Equal(EngineMode.EmergencyStopped, rig.Engine.Mode);
            Assert.StartsWith("Najpierw zresetuj", rig.Engine.CmdStart());
            Assert.Single(rig.Broker.Orders);
        }

        [Fact]
        public void DailyLossLimit_BlocksNewEntries()
        {
            var rig = new EngineRig();
            rig.Broker.EquityOffset = -160; // 1,6% straty dnia przy limicie 1,5%
            for (int i = 0; i < 5; i++) rig.Tick();
            Assert.Empty(rig.Broker.Orders);
            Assert.True(rig.Engine.Risk.State.DailyLimitHit);
        }

        [Fact]
        public void MaxDrawdown_TriggersEmergencyStop()
        {
            var rig = new EngineRig();
            rig.Broker.EquityOffset = -600; // 6% obsunięcia przy limicie 5%
            rig.Tick();
            Assert.Equal(EngineMode.EmergencyStopped, rig.Engine.Mode);
            Assert.Empty(rig.Broker.Orders);
        }

        [Fact]
        public void IncompleteHistory_ProducesNoTrade()
        {
            var rig = new EngineRig(d1Bars: 120); // strategia wymaga ≥ 220 świec D1
            for (int i = 0; i < 5; i++) rig.Tick();
            Assert.Empty(rig.Broker.Orders);
        }

        [Fact]
        public void MarketClosed_NoTrade()
        {
            var rig = new EngineRig();
            rig.Broker.MarketOpen = false;
            for (int i = 0; i < 5; i++) rig.Tick();
            Assert.Empty(rig.Broker.Orders);
        }

        [Fact]
        public void CryptoInstrument_MisclassifiedByBroker_IsNeverTraded()
        {
            var rig = new EngineRig();
            rig.AddDipInstrument("BTCUSD", AssetClass.Index);
            rig.Engine.SetUniverse(new[]
            {
                new UniverseEntry { Symbol = "BTCUSD", AssetClass = AssetClass.Index, BaseCurrency = "BTCUSD" },
            });
            for (int i = 0; i < 5; i++) rig.Tick();
            Assert.DoesNotContain(rig.Broker.Orders, o => o.Symbol == "BTCUSD");
        }

        [Fact]
        public void PausedEntries_StillManagesButOpensNothing()
        {
            var rig = new EngineRig();
            rig.Engine.CmdPauseEntries();
            for (int i = 0; i < 5; i++) rig.Tick();
            Assert.Empty(rig.Broker.Orders);
            Assert.Equal(EngineMode.PausedNewEntries, rig.Engine.Mode);
        }

        [Fact]
        public void AccountChange_BlocksTrading()
        {
            var rig = new EngineRig();
            rig.Broker.AccountNumber = 999999;
            for (int i = 0; i < 5; i++) rig.Tick();
            Assert.Empty(rig.Broker.Orders);
            Assert.False(rig.Engine.GuardAllowed);
        }
    }
}
