using System;
using System.Collections.Generic;
using System.Linq;
using Apex.Core.Adaptive;
using Apex.Core.Bridge;
using Apex.Core.Engine;
using Apex.Core.Execution;
using Apex.Core.Indicators;
using Apex.Core.Management;
using Apex.Core.Model;
using Apex.Core.Risk;
using Apex.Core.Safety;
using Apex.Core.Strategies;
using Xunit;

namespace Apex.Tests
{
    public class IndicatorTests
    {
        [Fact]
        public void Sma_UsesFullWindowOnly()
        {
            var r = Ta.Sma(new double[] { 1, 2, 3, 4, 5 }, 3);
            Assert.True(double.IsNaN(r[0]) && double.IsNaN(r[1]));
            Assert.Equal(2, r[2], 10);
            Assert.Equal(4, r[4], 10);
        }

        [Fact]
        public void Ema_IsSeededWithSma()
        {
            var r = Ta.Ema(new double[] { 1, 2, 3, 4, 5 }, 3);
            Assert.Equal(2, r[2], 10);
            Assert.Equal(3, r[3], 10); // 2 + 0,5 × (4 − 2)
            Assert.Equal(4, r[4], 10);
        }

        [Fact]
        public void Rsi_ExtremesAndFlat()
        {
            var up = Enumerable.Range(1, 30).Select(i => (double)i).ToArray();
            var down = up.Reverse().ToArray();
            var flat = Enumerable.Repeat(5.0, 30).ToArray();
            Assert.Equal(100, Ta.Rsi(up, 14)[^1], 6);
            Assert.Equal(0, Ta.Rsi(down, 14)[^1], 6);
            Assert.Equal(50, Ta.Rsi(flat, 14)[^1], 6);
        }

        [Fact]
        public void Atr_OfConstantRangeBars_EqualsRange()
        {
            int n = 40;
            var h = Enumerable.Repeat(11.0, n).ToArray();
            var l = Enumerable.Repeat(9.0, n).ToArray();
            var c = Enumerable.Repeat(10.0, n).ToArray();
            var atr = Ta.Atr(h, l, c, 14);
            Assert.True(double.IsNaN(atr[13]));
            Assert.Equal(2.0, atr[14], 10);
            Assert.Equal(2.0, atr[^1], 10);
        }

        [Fact]
        public void Correlation_IdenticalAndOpposite()
        {
            var rng = new Random(1);
            var a = Enumerable.Range(0, 60).Select(_ => rng.NextDouble()).ToArray();
            var b = a.Select(x => -2 * x).ToArray();
            Assert.Equal(1, Ta.Correlation(a, a), 9);
            Assert.Equal(-1, Ta.Correlation(a, b), 9);
            Assert.True(double.IsNaN(Ta.Correlation(a.Take(5).ToArray(), a.Take(5).ToArray())), "za mało danych = NaN");
        }

        [Fact]
        public void EfficiencyRatio_StraightLineIsOne()
        {
            var up = Enumerable.Range(1, 30).Select(i => (double)i).ToArray();
            Assert.Equal(1.0, Ta.EfficiencyRatio(up, 10)[^1], 10);
        }

        [Fact]
        public void PercentRank_IsFractionBelow()
        {
            var src = new double[] { 1, 2, 3, 4, 2.5 };
            Assert.Equal(0.5, Ta.PercentRank(src, 4, 4), 10);
        }
    }

    public class SizingTests
    {
        private static InstrumentSpec Fx(CommissionKind kind = CommissionKind.UsdPerLot, double comm = 3.5) => new()
        {
            Name = "EURUSD", AssetClass = AssetClass.Forex, BaseAsset = "EUR", QuoteAsset = "USD", Digits = 5, PipSize = 0.0001, TickSize = 0.00001,
            PipValuePerUnit = 0.0001, LotSize = 100_000, VolumeMin = 1000, VolumeStep = 1000, VolumeMax = 10_000_000,
            CommissionKind = kind, CommissionValue = comm,
        };

        [Fact]
        public void RiskIncludingCommissionNeverExceedsBudget()
        {
            var r = PositionSizer.Size(Fx(), 50, 0.0020, 1.1, 0.00002, 0.0001);
            Assert.True(r.Ok, r.Reason);
            Assert.True(r.RiskAmount <= 50 + 1e-9);
            Assert.Equal(0, r.Units % 1000, 6); // wielokrotność kroku
            // Strata = jednostki × (SL + poślizg) × wartość ceny + prowizja w obie strony
            double expected = r.Units * (0.0020 + 0.0001) * 1.0 + 2 * r.Units / 100_000 * 3.5;
            Assert.Equal(expected, r.RiskAmount, 6);
        }

        [Fact]
        public void VolumeIsRoundedDownOnly()
        {
            var r = PositionSizer.Size(Fx(CommissionKind.None, 0), 50, 0.0020, 1.1, 0, 0);
            Assert.Equal(25_000, r.Units); // 50 / 0,002 = 25 000 dokładnie
            var r2 = PositionSizer.Size(Fx(CommissionKind.None, 0), 49.99, 0.0020, 1.1, 0, 0);
            Assert.Equal(24_000, r2.Units);
        }

        [Fact]
        public void MinimumVolumeAboveBudget_IsRejected()
        {
            var r = PositionSizer.Size(Fx(), 1.0, 0.0050, 1.1, 0.00002, 0.0001);
            Assert.False(r.Ok);
            Assert.Contains("minimalny wolumen", r.Reason);
        }

        [Fact]
        public void UnknownCommission_IsRejected()
        {
            var r = PositionSizer.Size(Fx(CommissionKind.Unknown, 0), 50, 0.0020, 1.1, 0.00002, 0.0001);
            Assert.False(r.Ok);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(double.NaN)]
        public void InvalidStop_IsRejected(double stop)
        {
            Assert.False(PositionSizer.Size(Fx(), 50, stop, 1.1, 0.00002, 0.0001).Ok);
        }

        [Fact]
        public void CommissionKinds_AreComputedPerSide()
        {
            var perMillion = Fx(CommissionKind.UsdPerMillionUsdVolume, 30);
            Assert.Equal(3.0, perMillion.CommissionOneSide(100_000, 1.0), 9);
            var perLot = Fx(CommissionKind.UsdPerLot, 3.5);
            Assert.Equal(3.5, perLot.CommissionOneSide(100_000, 1.1), 9);
            var pct = Fx(CommissionKind.PercentOfVolume, 0.1);
            Assert.Equal(110, pct.CommissionOneSide(100_000, 1.1), 6);
        }

        [Fact]
        public void BrokerMinimumStopDistance_IsEnforced()
        {
            var spec = new InstrumentSpec
            {
                Name = "EURUSD", PipSize = 0.0001, TickSize = 0.00001, PipValuePerUnit = 0.0001, LotSize = 100_000, VolumeMin = 1000, VolumeStep = 1000,
                VolumeMax = 1e7, CommissionKind = CommissionKind.None, MinStopDistancePips = 10,
            };
            Assert.False(PositionSizer.Size(spec, 50, 0.0005, 1.1, 0, 0).Ok);
        }
    }

    public class RiskManagerTests
    {
        private static AccountSnapshot Acc(double equity = 10_000, double margin = 0) => new()
        {
            Number = 1, Balance = 10_000, Equity = equity, Margin = margin, FreeMargin = equity - margin, TimeUtc = new DateTime(2026, 1, 5, 10, 0, 0, DateTimeKind.Utc),
        };

        private static TradeProposal P(string sym = "EURUSD", double risk = 50, double notional = 10_000, double margin = 300, TradeDirection d = TradeDirection.Long,
            AssetClass cls = AssetClass.Forex, string? b = "EUR", string? q = "USD", double corr = double.NaN) => new()
        {
            Symbol = sym, AssetClass = cls, Direction = d, RiskAmount = risk, Notional = notional, EstimatedMargin = margin, BaseCurrency = b, QuoteCurrency = q,
            MaxSignedCorrelation = corr, MaxCorrelationWith = double.IsNaN(corr) ? null : "X",
        };

        private static OpenRisk O(string sym, AssetClass cls = AssetClass.Forex, string? b = null, string? q = null, TradeDirection d = TradeDirection.Long, double worst = 50, double notional = 10_000, bool sl = true) => new()
        {
            Symbol = sym, AssetClass = cls, BaseCurrency = b, QuoteCurrency = q, Direction = d, WorstCaseLoss = worst, Notional = notional, HasStopLoss = sl,
        };

        private static RiskManager Rm(RiskSettings? s = null)
        {
            var rm = new RiskManager(s ?? new RiskSettings());
            rm.Update(Acc(), new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc));
            return rm;
        }

        [Fact]
        public void DefaultsMatchSpecification()
        {
            var s = new RiskSettings();
            Assert.Equal(0.5, s.RiskPerTradePct);
            Assert.Equal(1.5, s.MaxDailyLossPct);
            Assert.Equal(5.0, s.MaxDrawdownPct);
            Assert.Equal(3, s.MaxOpenPositions);
            Assert.Equal(5.0, s.MaxEffectiveLeverage);
            Assert.Null(s.Validate());
        }

        [Fact]
        public void CleanTrade_IsAllowed()
        {
            var r = Rm().EvaluateNewTrade(P(), Acc(), new List<OpenRisk>());
            Assert.True(r.Allowed, r.ToString());
        }

        [Fact]
        public void MaxOpenPositions_Enforced()
        {
            var open = new List<OpenRisk> { O("A", AssetClass.Index, "A"), O("B", AssetClass.Metal, "B"), O("C", AssetClass.Energy, "C") };
            var r = Rm().EvaluateNewTrade(P(), Acc(), open);
            Assert.Contains(r.Failures, f => f.Contains("limit pozycji"));
        }

        [Fact]
        public void Leverage_Enforced()
        {
            var r = Rm().EvaluateNewTrade(P(notional: 60_000), Acc(), new List<OpenRisk>());
            Assert.Contains(r.Failures, f => f.Contains("dźwignia"));
        }

        [Fact]
        public void RiskAboveBudget_Rejected()
        {
            var r = Rm().EvaluateNewTrade(P(risk: 51), Acc(), new List<OpenRisk>());
            Assert.Contains(r.Failures, f => f.Contains("budżet"));
        }

        [Fact]
        public void WorstCaseProjection_RespectsDailyLimit()
        {
            // Dwie otwarte pozycje z ryzykiem 60 + nowa 50 = 170 > 150 (1,5% z 10 000).
            var open = new List<OpenRisk> { O("XAUUSD", AssetClass.Metal, "XAU", "USD", worst: 60), O("US500", AssetClass.Index, "US500", worst: 60) };
            var r = Rm().EvaluateNewTrade(P(), Acc(), open);
            Assert.Contains(r.Failures, f => f.Contains("najgorszy scenariusz dnia"));
        }

        [Fact]
        public void PositionWithoutStopLoss_BlocksNewTrades()
        {
            var open = new List<OpenRisk> { O("US500", AssetClass.Index, "US500", worst: 0, sl: false) };
            var r = Rm().EvaluateNewTrade(P(), Acc(), open);
            Assert.Contains(r.Failures, f => f.Contains("bez SL"));
        }

        [Fact]
        public void SameCurrencyExposure_Enforced()
        {
            var open = new List<OpenRisk> { O("EURJPY", b: "EUR", q: "JPY", worst: 1), O("EURGBP", AssetClass.Metal, b: "EUR", q: "GBP", worst: 1) };
            var r = Rm().EvaluateNewTrade(P(), Acc(), open);
            Assert.Contains(r.Failures, f => f.Contains("koncentracja ekspozycji +EUR"));
        }

        [Fact]
        public void Correlation_Enforced()
        {
            var r = Rm().EvaluateNewTrade(P(corr: 0.9), Acc(), new List<OpenRisk>());
            Assert.Contains(r.Failures, f => f.Contains("korelacja"));
        }

        [Fact]
        public void Margin_Enforced()
        {
            var r = Rm().EvaluateNewTrade(P(margin: 6000), Acc(), new List<OpenRisk>());
            Assert.Contains(r.Failures, f => f.Contains("depozyt"));
        }

        [Fact]
        public void DailyLimitLatch_ResetsOnNewDayOnly()
        {
            var rm = Rm();
            var day = new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);
            rm.Update(Acc(9_840), day);
            Assert.True(rm.State.DailyLimitHit);
            rm.Update(Acc(10_000), day.AddHours(2));
            Assert.True(rm.State.DailyLimitHit, "odrobienie straty tego samego dnia nie zdejmuje blokady");
            rm.Update(Acc(10_000), day.AddDays(1));
            Assert.False(rm.State.DailyLimitHit);
        }

        [Fact]
        public void MaxDrawdownLatch_RequiresManualReset()
        {
            var rm = Rm();
            var t = new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);
            rm.Update(Acc(9_400), t);
            Assert.True(rm.State.MaxDrawdownHit);
            rm.Update(Acc(10_500), t.AddDays(3));
            Assert.True(rm.State.MaxDrawdownHit);
            rm.ResetDrawdownLatch(10_500);
            Assert.False(rm.State.MaxDrawdownHit);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(2.5)]
        public void AbsurdRiskSettings_FailValidation(double riskPct)
        {
            Assert.NotNull(new RiskSettings { RiskPerTradePct = riskPct }.Validate());
        }
    }

    public class ClassifierTests
    {
        [Theory]
        [InlineData("BTCUSD", "Bitcoin vs US Dollar", "BTC", "USD")]
        [InlineData("ETHUSD", "", "", "")]
        [InlineData("XBTEUR", "", "", "")]
        [InlineData("DOGEUSDT", "", "", "")]
        [InlineData("SOL/USD", "", "", "")]
        [InlineData("Crypto10", "Crypto index", "", "")]
        [InlineData("BTC.cash", "", "", "")]
        [InlineData("LNKUSD", "", "", "")]
        [InlineData("XYZ", "Some DeFi token basket", "", "")]
        public void Crypto_IsDetected(string name, string desc, string b, string q)
        {
            Assert.True(InstrumentClassifier.IsCrypto(name, desc, b, q));
            Assert.Equal(AssetClass.Crypto, InstrumentClassifier.Classify(name, desc, b, q));
        }

        [Theory]
        [InlineData("EURUSD", "Euro vs US Dollar", "EUR", "USD", AssetClass.Forex)]
        [InlineData("USDRON", "", "USD", "RON", AssetClass.Forex)]
        [InlineData("XAUUSD", "Gold vs US Dollar", "XAU", "USD", AssetClass.Metal)]
        [InlineData("US500", "US 500 Index", "", "", AssetClass.Index)]
        [InlineData("GER40", "Germany 40 Index", "", "", AssetClass.Index)]
        [InlineData("SpotCrude", "WTI crude oil", "", "", AssetClass.Energy)]
        [InlineData("AAPL.US", "Apple Inc", "", "", AssetClass.Stock)]
        [InlineData("MSFT.US", "Microsoft Corp", "", "", AssetClass.Stock)]
        [InlineData("COCOA", "Cocoa", "", "", AssetClass.Commodity)]
        public void NonCrypto_IsNotFlagged(string name, string desc, string b, string q, AssetClass expected)
        {
            Assert.False(InstrumentClassifier.IsCrypto(name, desc, b, q));
            Assert.Equal(expected, InstrumentClassifier.Classify(name, desc, b, q));
        }

        [Fact]
        public void AmbiguousDescription_IsExcluded_FailClosed()
        {
            // Świadomie zachowawcze: opis zawierający "defi" jako fragment wyrazu wyklucza instrument.
            // Fałszywe wykluczenie akcji jest bezpieczne; przepuszczenie kryptowaluty – nie.
            Assert.True(InstrumentClassifier.IsCrypto("DH.US", "Definitive Healthcare Corp", "", ""));
        }

        [Fact]
        public void Unrecognised_IsUnknown_FailClosed()
        {
            Assert.Equal(AssetClass.Unknown, InstrumentClassifier.Classify("QWERTY", "", "", ""));
        }

        [Fact]
        public void CurrencyLegs_IndexIsOwnAssetOnly()
        {
            Assert.Equal(("EUR", "USD"), InstrumentClassifier.CurrencyLegs(AssetClass.Forex, "EURUSD", null, null));
            var (b, q) = InstrumentClassifier.CurrencyLegs(AssetClass.Index, "US500", "", "USD");
            Assert.Equal("US500", b);
            Assert.Null(q);
        }
    }

    public class AccountGuardTests
    {
        private static AccountSnapshot Acc(bool live, long nr = 5551234) => new() { Number = nr, IsLive = live, BrokerName = "Pepperstone", Balance = 1000, Equity = 1000, FreeMargin = 1000 };

        [Fact]
        public void Demo_IsAllowedByDefault()
        {
            var r = AccountGuard.Evaluate(Acc(false), new AccountGuardSettings());
            Assert.True(r.Allowed);
            Assert.False(r.IsLive);
        }

        [Fact]
        public void Live_IsBlockedByDefault()
        {
            var r = AccountGuard.Evaluate(Acc(true), new AccountGuardSettings());
            Assert.False(r.Allowed);
            Assert.True(r.IsLive);
        }

        [Fact]
        public void Live_IsRejectedEvenWithAllLegacyUnlockConditions()
        {
            long nr = 5551234;
            string phrase = AccountGuard.RequiredLivePhrase(nr);
            Assert.False(AccountGuard.Evaluate(Acc(true), new AccountGuardSettings { AllowLive = true, LiveConfirmation = phrase }).Allowed);
            Assert.False(AccountGuard.Evaluate(Acc(true), new AccountGuardSettings { AllowLive = true, AllowedAccounts = new[] { nr } }).Allowed);
            Assert.False(AccountGuard.Evaluate(Acc(true), new AccountGuardSettings { AllowedAccounts = new[] { nr }, LiveConfirmation = phrase }).Allowed);
            Assert.False(AccountGuard.Evaluate(Acc(true), new AccountGuardSettings { AllowLive = true, AllowedAccounts = new[] { nr }, LiveConfirmation = phrase.ToLowerInvariant() }).Allowed);
            Assert.False(AccountGuard.Evaluate(Acc(true), new AccountGuardSettings { AllowLive = true, AllowedAccounts = new[] { 999L }, LiveConfirmation = AccountGuard.RequiredLivePhrase(999) }).Allowed);
            Assert.False(AccountGuard.Evaluate(Acc(true), new AccountGuardSettings { AllowLive = true, AllowedAccounts = new[] { nr }, LiveConfirmation = phrase }).Allowed);
        }

        [Fact]
        public void MissingOrInvalidAccount_IsBlocked()
        {
            Assert.False(AccountGuard.Evaluate(null, new AccountGuardSettings()).Allowed);
            Assert.False(AccountGuard.Evaluate(new AccountSnapshot { Number = 0 }, new AccountGuardSettings()).Allowed);
        }

        [Fact]
        public void BrokerAllowList_IsEnforced()
        {
            var r = AccountGuard.Evaluate(Acc(false), new AccountGuardSettings { AllowedBrokers = new[] { "IC Markets" } });
            Assert.False(r.Allowed);
        }
    }

    public class ManagementTests
    {
        private static InstrumentSpec Spec() => new() { Name = "US500", PipSize = 0.1, TickSize = 0.01, Digits = 2, PipValuePerUnit = 0.1, VolumeMin = 0.1, VolumeStep = 0.1, VolumeMax = 1000 };

        private static (TradePlan plan, PositionInfo pos) Long(ExitPlan exit, double stop = 4900)
        {
            var plan = new TradePlan { PositionId = 1, Direction = TradeDirection.Long, EntryPrice = 5000, InitialStop = stop, InitialRisk = 5000 - stop, BestPrice = 5000, EntryAtr = 40, Exit = exit, LastManagedBarTime = new DateTime(2026, 1, 1) };
            var pos = new PositionInfo { Id = 1, Direction = TradeDirection.Long, EntryPrice = 5000, StopLoss = stop, VolumeUnits = 1 };
            return (plan, pos);
        }

        private static ManagementInput In(TradePlan plan, PositionInfo pos, double close, double high, double low, int bar = 1, StrategyExit? exit = null) => new()
        {
            Plan = plan, Position = pos, Spec = Spec(), Quote = new Quote(close - 0.25, close + 0.25, DateTime.UtcNow), BarClose = close, BarHigh = high, BarLow = low,
            BarTime = new DateTime(2026, 1, 1).AddHours(bar), CurrentAtr = 40, StrategyExit = exit ?? StrategyExit.None,
        };

        [Fact]
        public void BreakEven_MovesStopAboveEntryOnly()
        {
            var (plan, pos) = Long(new ExitPlan { BreakEvenAtR = 1.0 });
            var a = PositionManagerRules.Evaluate(In(plan, pos, 5090, 5110, 5050));
            var m = Assert.Single(a, x => x.Kind == ManagementActionKind.ModifyStop);
            Assert.True(m.NewStop > 5000 && m.NewStop < 5090);
        }

        [Fact]
        public void Stop_IsNeverLoosened()
        {
            var (plan, pos) = Long(new ExitPlan { TrailAtrMultiple = 3, TrailStartR = 0 }, stop: 4990);
            // trailing 3×ATR od maksimum 5010 = 4890 < obecny SL 4990 → brak zmiany
            var a = PositionManagerRules.Evaluate(In(plan, pos, 5005, 5010, 4995));
            Assert.DoesNotContain(a, x => x.Kind == ManagementActionKind.ModifyStop);
        }

        [Fact]
        public void TimeStop_Closes()
        {
            var (plan, pos) = Long(new ExitPlan { MaxBarsHeld = 2 });
            Assert.Empty(PositionManagerRules.Evaluate(In(plan, pos, 5001, 5005, 4995, bar: 1)));
            var a = PositionManagerRules.Evaluate(In(plan, pos, 5001, 5005, 4995, bar: 2));
            Assert.Contains(a, x => x.Kind == ManagementActionKind.Close);
        }

        [Fact]
        public void StrategyExit_Closes()
        {
            var (plan, pos) = Long(new ExitPlan());
            var a = PositionManagerRules.Evaluate(In(plan, pos, 5001, 5005, 4995, exit: new StrategyExit(true, "test")));
            Assert.Contains(a, x => x.Kind == ManagementActionKind.Close && x.Reason == "test");
        }

        [Fact]
        public void Partial_EvaluationNeverClaimsExecution()
        {
            var (plan, pos) = Long(new ExitPlan { PartialAtR = 1, PartialFraction = 0.5 });
            var a = PositionManagerRules.Evaluate(In(plan, pos, 5110, 5120, 5050));
            var p = Assert.Single(a, x => x.Kind == ManagementActionKind.PartialClose);
            Assert.Equal(0.5, p.CloseUnits, 9);
            Assert.False(plan.PartialDone);
            var again = PositionManagerRules.Evaluate(In(plan, pos, 5130, 5140, 5100, bar: 2));
            Assert.Contains(again, x => x.Kind == ManagementActionKind.PartialClose);
        }

        [Fact]
        public void EmergencyStop_IsOnCorrectSideOfPrice()
        {
            var pos = new PositionInfo { Id = 1, Direction = TradeDirection.Short, EntryPrice = 5000, VolumeUnits = 1 };
            double sl = PositionManagerRules.EmergencyStop(null, pos, Spec(), new Quote(4999.75, 5000.25, DateTime.UtcNow), 40);
            Assert.True(sl > 5000.25);
        }
    }

    public class LedgerTests
    {
        [Fact]
        public void SignalCanBeReservedOnlyOnce()
        {
            var l = new ExecutionLedger();
            var t = DateTime.UtcNow;
            Assert.True(l.TryReserve("s1", "EURUSD", t));
            Assert.False(l.TryReserve("s1", "EURUSD", t));
            Assert.True(l.HasPendingFor("eurusd"));
            l.MarkConsumed("s1", "x", t);
            Assert.False(l.HasPendingFor("EURUSD"));
            Assert.False(l.TryReserve("s1", "EURUSD", t), "wykorzystany sygnał nigdy nie jest ponawiany");
        }

        [Fact]
        public void Prune_KeepsPendingEntries()
        {
            var l = new ExecutionLedger();
            var t = DateTime.UtcNow;
            l.TryReserve("p", "A", t.AddDays(-30));
            l.TryReserve("c", "B", t.AddDays(-30));
            l.MarkConsumed("c", "x", t.AddDays(-30));
            Assert.Equal(1, l.Prune(t, TimeSpan.FromDays(10)));
            Assert.True(l.IsKnown("p"));
        }

        [Fact]
        public void Comment_RoundTrips()
        {
            string c = ExecutionLedger.BuildComment(StrategyId.DailyTrend, "abc123");
            Assert.Equal("APEX|DT|abc123|v1", c);
            Assert.True(ExecutionLedger.TryParseComment(c, out var s, out var id));
            Assert.Equal(StrategyId.DailyTrend, s);
            Assert.Equal("abc123", id);
            Assert.False(ExecutionLedger.TryParseComment("Inny|DT|x", out _, out _));
            Assert.False(ExecutionLedger.TryParseComment(null, out _, out _));
        }

        [Fact]
        public void SignalId_IsDeterministic()
        {
            var t = new DateTime(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc);
            Assert.Equal(SignalIdGenerator.Create("US500", StrategyId.IndexDipBuy, TradeDirection.Long, t),
                         SignalIdGenerator.Create("US500", StrategyId.IndexDipBuy, TradeDirection.Long, t));
            Assert.NotEqual(SignalIdGenerator.Create("US500", StrategyId.IndexDipBuy, TradeDirection.Long, t),
                            SignalIdGenerator.Create("US500", StrategyId.IndexDipBuy, TradeDirection.Long, t.AddDays(1)));
        }
    }

    public class NewsTests
    {
        private const string Json = @"[
 {""title"":""Non-Farm Employment Change"",""country"":""USD"",""date"":""2026-10-02T08:30:00-04:00"",""impact"":""High""},
 {""title"":""German Prelim CPI"",""country"":""EUR"",""date"":""2026-10-02T08:00:00-04:00"",""impact"":""Medium""},
 {""title"":""G20 Meetings"",""country"":""All"",""date"":""2026-10-03T00:00:00-04:00"",""impact"":""High""},
 {""title"":""broken"",""country"":"""",""date"":""x"",""impact"":""High""}
]";

        [Fact]
        public void Parser_HandlesTimezonesAndAll()
        {
            var ev = NewsCalendar.ParseFfJson(Json);
            var nfp = Assert.Single(ev, e => e.Title.StartsWith("Non-Farm"));
            Assert.Equal(new DateTime(2026, 10, 2, 12, 30, 0, DateTimeKind.Utc), nfp.TimeUtc);
            Assert.Equal(NewsImpact.High, nfp.Impact);
            Assert.Equal(9, ev.Count(e => e.Title == "G20 Meetings"));
            Assert.DoesNotContain(ev, e => e.Title == "broken");
        }

        [Fact]
        public void Blackout_AffectsRelatedInstrumentsOnly()
        {
            var cal = new NewsCalendar();
            cal.SetEvents(NewsCalendar.ParseFfJson(Json), DateTime.UtcNow, "test");
            var s = new NewsSettings();
            var us500 = new InstrumentSpec { Name = "US500", AssetClass = AssetClass.Index, QuoteAsset = "USD" };
            var ger = new InstrumentSpec { Name = "GER40", AssetClass = AssetClass.Index, QuoteAsset = "EUR" };
            var t = new DateTime(2026, 10, 2, 12, 10, 0, DateTimeKind.Utc);
            Assert.True(cal.IsBlackout(us500, t, s, out var e));
            Assert.StartsWith("Non-Farm", e!.Title);
            Assert.False(cal.IsBlackout(ger, t, s, out _), "wydarzenie EUR o średniej wadze nie blokuje przy progu High");
            Assert.False(cal.IsBlackout(us500, t.AddMinutes(60), s, out _));
        }

        [Fact]
        public void EmptyOrGarbage_IsSafe()
        {
            Assert.Empty(NewsCalendar.ParseFfJson(""));
            Assert.Empty(NewsCalendar.ParseFfJson("{}"));
        }
    }

    public class UniverseTests
    {
        private static SymbolDto S(string name, string desc = "", string b = "", string q = "") => new()
        {
            Name = name, Description = desc, BaseAsset = b, QuoteAsset = q, PipSize = 0.0001, TickSize = 0.00001, PipValue = 0.0001, LotSize = 100000,
            VolumeMin = 1000, VolumeStep = 1000, VolumeMax = 1e7, CommissionKind = "UsdPerLot", CommissionValue = 3.5,
        };

        [Fact]
        public void Crypto_IsNeverSelected_AndStrategylessClassesAreSkipped()
        {
            var symbols = new[] { S("EURUSD", "", "EUR", "USD"), S("BTCUSD", "Bitcoin", "BTC", "USD"), S("US500.cash", "US 500 Index"), S("XAUUSD", "Gold", "XAU", "USD"), S("QWERTY") };
            var res = UniverseSelector.Select(symbols, new UniverseSettings(), new HashSet<AssetClass> { AssetClass.Index, AssetClass.Forex }, new HashSet<AssetClass>());
            var selected = res.Where(r => r.Selected).Select(r => r.Symbol).ToList();
            Assert.Contains("EURUSD", selected);
            Assert.Contains("US500.cash", selected);
            Assert.DoesNotContain("BTCUSD", selected);
            Assert.DoesNotContain("XAUUSD", selected);
            Assert.DoesNotContain("QWERTY", selected);
            Assert.Contains("kryptowaluta", res.Single(r => r.Symbol == "BTCUSD").Reason);
        }

        [Fact]
        public void ManualMode_StillExcludesCrypto()
        {
            var symbols = new[] { S("EURUSD", "", "EUR", "USD"), S("ETHUSD", "Ethereum", "ETH", "USD") };
            var u = new UniverseSettings { Mode = "manual", Manual = new() { "ETHUSD", "EURUSD" } };
            var res = UniverseSelector.Select(symbols, u, new HashSet<AssetClass> { AssetClass.Forex, AssetClass.Crypto }, new HashSet<AssetClass>());
            Assert.DoesNotContain(res, r => r.Selected && r.Symbol == "ETHUSD");
            Assert.Contains(res, r => r.Selected && r.Symbol == "EURUSD");
        }

        [Fact]
        public void BrokerSuffixes_Match()
        {
            Assert.True(UniverseSelector.Matches("US500CASH", "US500"));
            Assert.True(UniverseSelector.Matches("EURUSDM", "EURUSD"));
            Assert.False(UniverseSelector.Matches("US5000", "US500"));
        }
    }

    public class ProtocolTests
    {
        [Fact]
        public void OpenCommand_SerializesCamelCase()
        {
            string json = BridgeProtocol.Serialize(new OpenCmd { Id = "1", Symbol = "EURUSD", Side = "buy", Units = 1000, SlPips = 20, Label = "APEX", Comment = "APEX|TP|x|v1" });
            Assert.Contains("\"type\":\"open\"", json);
            Assert.Contains("\"slPips\":20", json);
            Assert.DoesNotContain("tpPips", json); // null pomijane
            Assert.Equal("open", BridgeProtocol.ReadType(json));
            var back = BridgeProtocol.Deserialize<OpenCmd>(json)!;
            Assert.Equal(20, back.SlPips);
        }

        [Fact]
        public void MalformedJson_IsRejectedSafely()
        {
            Assert.Null(BridgeProtocol.ReadType("{not json"));
            Assert.Null(BridgeProtocol.ReadType("[1,2]"));
            Assert.Null(BridgeProtocol.Deserialize<OpenCmd>("{\"slPips\":\"abc\"}"));
        }

        [Fact]
        public void Sides_AndTimes_RoundTrip()
        {
            Assert.Equal(TradeDirection.Long, BridgeProtocol.ParseSide("BUY"));
            Assert.Null(BridgeProtocol.ParseSide("hold"));
            var t = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
            Assert.Equal(t, BridgeProtocol.FromUnixMs(BridgeProtocol.ToUnixMs(t)));
        }

        [Fact]
        public void SpecMapper_UnknownCommissionMakesSpecInvalid()
        {
            var dto = new SymbolDto { Name = "EURUSD", BaseAsset = "EUR", QuoteAsset = "USD", PipSize = 0.0001, TickSize = 0.00001, PipValue = 0.0001, LotSize = 100000, VolumeMin = 1000, VolumeStep = 1000, VolumeMax = 1e7, CommissionKind = "weird" };
            var spec = SpecMapper.ToSpec(dto, null, null);
            Assert.NotNull(spec.Validate());
            var fixedSpec = SpecMapper.ToSpec(dto, null, new CommissionOverride { Kind = "UsdPerLot", Value = 3.5 });
            Assert.Null(fixedSpec.Validate());
        }
    }

    public class ProfileTests
    {
        [Fact]
        public void DefaultProfile_IsValid_AndHasNoValidatedEdgeClaims()
        {
            var p = DefaultProfile.Load();
            Assert.Empty(StrategyBook.Validate(p));
            Assert.DoesNotContain(p.Entries, e => e.Status == ValidationStatus.Validated);
        }

        [Fact]
        public void LiveBook_ExcludesExperimental()
        {
            var p = Scenarios.DipOnlyProfile(ValidationStatus.Experimental);
            Assert.NotEmpty(new StrategyBook(p, false).For(AssetClass.Index));
            Assert.Empty(new StrategyBook(p, true).For(AssetClass.Index));
        }
    }
}
