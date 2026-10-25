using System;
using System.Collections.Generic;
using System.Reflection;
using Apex.Core.Bridge;
using Apex.Platforms.CTrader;
using cAlgo.API;
using cAlgo.API.Internals;
using Xunit;

namespace Apex.Platform.Tests
{
    // Unit doubles for the installed SDK interfaces, NOT a cTrader/Pepperstone server mock.
    // These tests execute the production mapper/quote code using explicit SDK-shaped inputs.
    public class CTraderDataTests
    {
        public class Properties : DispatchProxy
        {
            public Dictionary<string, object?> Values = new();
            protected override object? Invoke(MethodInfo? method, object?[]? args) =>
                Values.TryGetValue(method!.Name, out var value) ? value : throw new InvalidOperationException("Unexpected SDK access: " + method.Name);
        }
        private static T Stub<T>(params (string key, object? value)[] values) where T : class
        {
            T stub = DispatchProxy.Create<T, Properties>();
            foreach (var (key, value) in values) ((Properties)(object)stub).Values[key] = value;
            return stub;
        }
        private static Symbol Symbol(SymbolCommissionType kind = SymbolCommissionType.UsdPerOneLot, double value = 3.5, double minimum = 0,
            SymbolMinCommissionType minimumType = SymbolMinCommissionType.Asset, string? minimumAsset = "USD")
        {
            var hours = Stub<MarketHours>(("IsOpened", true), ("TimeTillClose", TimeSpan.FromHours(1)));
            return Stub<Symbol>(("get_Name", "EURUSD"), ("get_Description", "Euro vs US Dollar"),
                ("get_BaseAsset", Stub<Asset>(("get_Name", "EUR"))), ("get_QuoteAsset", Stub<Asset>(("get_Name", "USD"))),
                ("get_Digits", 5), ("get_PipSize", 0.0001), ("get_TickSize", 0.00001), ("get_LotSize", 100000L),
                ("get_VolumeInUnitsMin", 1000.0), ("get_VolumeInUnitsStep", 1000.0), ("get_VolumeInUnitsMax", 1000000.0),
                ("get_CommissionType", kind), ("get_Commission", value), ("get_MinCommission", minimum),
                ("get_MinCommissionType", minimumType), ("get_MinCommissionAsset", minimumAsset == null ? null : Stub<Asset>(("get_Name", minimumAsset))),
                ("get_MinDistanceType", SymbolMinDistanceType.Pips), ("get_MinStopLossDistance", 2.0), ("get_MinTakeProfitDistance", 2.0),
                ("get_Bid", 1.1), ("get_Ask", 1.1001), ("get_IsTradingEnabled", true), ("get_TradingMode", SymbolTradingMode.FullAccess),
                ("get_IsCustom", false), ("get_MarketHours", hours), ("get_PipValue", 99.0));
        }
        private static Tick Tick(DateTime time, double bid = 1.1, double ask = 1.1001) =>
            (Tick)Activator.CreateInstance(typeof(Tick), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new object[] { time, bid, ask }, null)!;

        [Fact]
        public void PipValueAndUsdCommissionAreConvertedIntoAccountCurrencyNotFrozenStartupValue()
        {
            var dto = CTraderData.Describe(Symbol(), _ => 4.0); // Explicit USD -> PLN fixture, not live FX.
            var spec = SpecMapper.ToSpec(dto, dto.PipValue, null);
            Assert.Equal(0.0004, spec.PipValuePerUnit, 8);
            Assert.Equal(14.0, spec.CommissionOneSide(100000, 1.1), 8);
            Assert.Equal(100000, spec.LotSize);
        }

        [Theory]
        [InlineData(SymbolCommissionType.UsdPerMillionUsdVolume, 30.0, 14.4)]
        [InlineData(SymbolCommissionType.PercentageOfTradingVolume, 0.1, 480.0)]
        [InlineData(SymbolCommissionType.QuoteCurrencyPerOneLot, 3.0, 12.0)]
        public void CommissionKindsKeepTheirUnitsAndAccountCurrency(SymbolCommissionType kind, double value, double expected)
        {
            var dto = CTraderData.Describe(Symbol(kind, value), _ => 4.0);
            Assert.Equal(expected, SpecMapper.ToSpec(dto, dto.PipValue, null).CommissionOneSide(100000, 1.2), 8);
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        [InlineData(double.PositiveInfinity)]
        public void MissingFxCannotFallBackToOneAndEnableTrading(double invalid)
        {
            var dto = CTraderData.Describe(Symbol(), _ => invalid);
            Assert.False(dto.TradingEnabled);
            Assert.Equal("Unknown", dto.CommissionKind);
            Assert.NotNull(SpecMapper.ToSpec(dto, dto.PipValue, null).Validate());
        }

        [Theory]
        [InlineData(SymbolMinCommissionType.Asset, "EUR", 6.0)]
        [InlineData(SymbolMinCommissionType.QuoteAsset, null, 4.0)]
        public void ActualSdkMinimumTypesAreConvertedToAccountCurrency(SymbolMinCommissionType minimumType, string? asset, double expected)
        {
            var dto = CTraderData.Describe(Symbol(minimum: 1, minimumType: minimumType, minimumAsset: asset),
                from => from switch { "USD" => 4.0, "EUR" => 6.0, _ => double.NaN });
            var spec = SpecMapper.ToSpec(BridgeProtocol.Deserialize<SymbolDto>(BridgeProtocol.Serialize(dto))!, null, null);
            Assert.Null(spec.Validate());
            Assert.True(dto.TradingEnabled);
            Assert.Equal("UsdPerLot", dto.CommissionKind);
            Assert.Equal(expected, spec.CommissionOneSide(1000, 1.1), 8);
            Assert.Equal(14, spec.CommissionOneSide(100000, 1.1), 8);
        }

        [Theory]
        [InlineData((SymbolMinCommissionType)999, "USD")]
        [InlineData(SymbolMinCommissionType.Asset, null)]
        [InlineData(SymbolMinCommissionType.Asset, "")]
        [InlineData(SymbolMinCommissionType.Asset, "MISSING")]
        public void UnsupportedMinimumCommissionIsExplicitlyUnverifiedEvenWithOverride(SymbolMinCommissionType minimumType, string? asset)
        {
            var dto = CTraderData.Describe(Symbol(minimum: 1, minimumType: minimumType, minimumAsset: asset),
                from => from == "USD" ? 1 : double.NaN);
            Assert.Equal("Unknown", dto.CommissionKind);
            Assert.NotNull(SpecMapper.ToSpec(dto, dto.PipValue, new CommissionOverride { Kind = "None" }).Validate());
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void InvalidSdkMinimumCannotBeHiddenByOverride(double minimum)
        {
            var dto = CTraderData.Describe(Symbol(minimum: minimum), _ => 1);
            Assert.NotNull(SpecMapper.ToSpec(dto, null, new CommissionOverride { Kind = "None" }).Validate());
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void InvalidMinimumAssetConversionRemainsUnknown(double invalid)
        {
            var dto = CTraderData.Describe(Symbol(minimum: 1, minimumAsset: "EUR"), from => from == "EUR" ? invalid : 1);
            Assert.Equal("Unknown", dto.CommissionKind);
            Assert.NotNull(SpecMapper.ToSpec(dto, null, new CommissionOverride { Kind = "None" }).Validate());
        }

        [Fact]
        public void RepeatedPublicationKeepsExactBrokerTickTimeAndPrices()
        {
            var time = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
            var ticks = Stub<Ticks>(("get_Count", 1), ("get_LastTick", Tick(time, 1.2, 1.2002)));
            var symbol = Symbol();
            var first = CTraderData.Quote(symbol, ticks, true, _ => 1)!;
            var second = CTraderData.Quote(symbol, ticks, true, _ => 1)!;
            Assert.Equal(1792756800000, first.T);
            Assert.Equal(first.T, second.T);
            Assert.Equal(1.2, first.B);
            Assert.Equal(1.2002, first.A);
            Assert.True(first.O);
            Assert.Equal(3600, first.Ttc);
        }

        [Theory]
        [InlineData(TradeType.Buy, 1.0, 1.2, true)]
        [InlineData(TradeType.Buy, 1.2, 1.0, false)]
        [InlineData(TradeType.Sell, 1.2, 1.0, true)]
        [InlineData(TradeType.Sell, 1.0, 1.2, false)]
        [InlineData(TradeType.Buy, null, 1.2, false)]
        [InlineData(TradeType.Buy, 1.0, null, false)]
        public void NewlyOpenedProtectionMustBeOnCorrectSidesOfEntry(TradeType side, double? sl, double? tp, bool valid)
        {
            var position = Stub<Position>(("get_TradeType", side), ("get_EntryPrice", 1.1), ("get_StopLoss", sl), ("get_TakeProfit", tp));
            Assert.Equal(valid, CTraderData.Protected(position));
        }

        [Fact]
        public void BrokerDisconnectionOverridesMarketHoursOpen()
        {
            var ticks = Stub<Ticks>(("get_Count", 1), ("get_LastTick", Tick(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))));
            var quote = CTraderData.Quote(Symbol(), ticks, false, _ => 1)!;
            Assert.False(quote.O);
            Assert.Equal(-1, quote.Ttc);
        }

        [Fact]
        public void NoTickMeansNoQuoteNotAnInventedFreshPrice() =>
            Assert.Null(CTraderData.Quote(Symbol(), Stub<Ticks>(("get_Count", 0)), true, _ => 1));
    }
}
