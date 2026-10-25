using System;
using Apex.Core.Bridge;
using Apex.Core.Model;
using Apex.Core.Risk;
using Xunit;

namespace Apex.Tests
{
    public class FeeIntegrationTests
    {
        // Wire fixtures also prove that v1 readers actually retain the optional fee data.
        private static SymbolDto Dto(double? floor = null, bool? known = null, string kind = "UsdPerLot",
            double usd = 1, double quote = 1, double value = 3.5, double maximum = 1000000) =>
            BridgeProtocol.Deserialize<SymbolDto>(BridgeProtocol.Serialize(new
            {
                name = "EURUSD", baseAsset = "EUR", quoteAsset = "USD", digits = 5,
                pipSize = 0.0001, tickSize = 0.00001, pipValue = 0.0001,
                lotSize = 100000, volumeMin = 1000, volumeStep = 1000, volumeMax = maximum,
                commissionKind = kind, commissionValue = value, usdToAccount = usd, quoteToAccount = quote,
                minCommissionAccount = floor, minCommissionKnown = known,
            }))!;

        [Fact]
        public void LegacyV1WithoutMinimumKeepsLinearFee()
        {
            var spec = SpecMapper.ToSpec(Dto(), null, null);
            Assert.Null(spec.Validate());
            Assert.Equal(0.035, spec.CommissionOneSide(1000, 1.1), 10);
        }

        [Fact]
        public void OptionalMinimumSurvivesV1RoundTripAndIsAFloorNotAnAddedFee()
        {
            var dto = BridgeProtocol.Deserialize<SymbolDto>(BridgeProtocol.Serialize(Dto(1.25, true)))!;
            var spec = SpecMapper.ToSpec(dto, null, null);
            Assert.Null(spec.Validate());
            Assert.Equal(1.25, spec.CommissionOneSide(1000, 1.1), 10);
            Assert.Equal(3.5, spec.CommissionOneSide(100000, 1.1), 10);
            Assert.Equal(0, spec.CommissionOneSide(0, 1.1));
        }

        [Fact]
        public void FeeOverrideCannotDiscardBrokerMinimum()
        {
            var spec = SpecMapper.ToSpec(Dto(2, true), null, new CommissionOverride { Kind = "None", Value = 0 });
            Assert.Null(spec.Validate());
            Assert.Equal(2, spec.CommissionOneSide(1000, 1.1));
        }

        [Fact]
        public void ExplicitUnknownMinimumCannotBeRepairedByBaseFeeOverride()
        {
            var spec = SpecMapper.ToSpec(Dto(null, false, kind: "Unknown"), null,
                new CommissionOverride { Kind = "UsdPerLot", Value = 3.5 });
            Assert.NotNull(spec.Validate());
            Assert.False(PositionSizer.Size(spec, 50, 0.002, 1.1, 0, 0).Ok);
        }

        [Fact]
        public void KnownMinimumWithoutAmountIsMalformedNotAZeroFloor()
        {
            var spec = SpecMapper.ToSpec(Dto(null, true), null, new CommissionOverride { Kind = "None" });
            Assert.NotNull(spec.Validate());
            Assert.False(PositionSizer.Size(spec, 50, 0.002, 1.1, 0, 0).Ok);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void InvalidMinimumIsRejectedEvenWithFeeOverride(double floor)
        {
            var spec = SpecMapper.ToSpec(Dto(floor, true), null, new CommissionOverride { Kind = "None" });
            Assert.NotNull(spec.Validate());
            Assert.False(PositionSizer.Size(spec, 50, 0.002, 1.1, 0, 0).Ok);
        }

        [Theory]
        [InlineData(50, 24000, 50, 2)]
        [InlineData(210, 101000, 209.07, 7.07)]
        public void SizingSolvesBothFloorAndLinearBranchesAndReportsActualCosts(double budget, double units, double risk, double commission)
        {
            var spec = SpecMapper.ToSpec(Dto(1, true), null, null);
            var result = PositionSizer.Size(spec, budget, 0.002, 1.1, 0.00002, 0);
            Assert.True(result.Ok, result.Reason);
            Assert.Equal(units, result.Units);
            Assert.Equal(risk, result.RiskAmount, 8);
            Assert.Equal(commission, result.CommissionRoundTrip, 8);
            Assert.Equal((0.00002 + commission / units) / 0.002, result.CostToRisk, 10);
            Assert.True(result.RiskAmount <= budget);
        }

        [Fact]
        public void MinimumVolumeWithFloorAboveBudgetIsRejected()
        {
            var spec = SpecMapper.ToSpec(Dto(1, true), null, null);
            Assert.False(PositionSizer.Size(spec, 3.99, 0.002, 1.1, 0, 0).Ok);
            var exact = PositionSizer.Size(spec, 4, 0.002, 1.1, 0, 0);
            Assert.True(exact.Ok, exact.Reason);
            Assert.Equal(1000, exact.Units);
            Assert.Equal(4, exact.RiskAmount);
        }

        [Fact]
        public void VolumeCapAndSlippageUseActualFloorAndCostAtFinalVolume()
        {
            var spec = SpecMapper.ToSpec(Dto(1, true, maximum: 10000), null, null);
            var result = PositionSizer.Size(spec, 50, 0.002, 1.1, 0.00002, 0.0001);
            Assert.True(result.Ok, result.Reason);
            Assert.Equal(10000, result.Units);
            Assert.Equal(23, result.RiskAmount, 8);
            Assert.Equal(2, result.CommissionRoundTrip);
            Assert.Equal(0.21, result.CostToRisk, 8);
        }

        [Fact]
        public void FloorSizingMatchesExhaustiveBrokerGridAcrossCrossover()
        {
            var spec = SpecMapper.ToSpec(Dto(1, true, maximum: 150000), null, null);
            for (int budget = 1; budget <= 320; budget++)
            {
                double expected = 0;
                for (int units = 1000; units <= 150000; units += 1000)
                    // Independent fixture: EURUSD stop loss 2 per 1000; 3.5/lot or 1 per side.
                    if (units * 0.002 + 2 * Math.Max(units * 0.000035, 1) <= budget) expected = units;
                var result = PositionSizer.Size(spec, budget, 0.002, 1.1, 0, 0);
                Assert.Equal(expected > 0, result.Ok);
                if (expected > 0)
                {
                    Assert.Equal(expected, result.Units);
                    Assert.InRange(result.RiskAmount, 0, budget);
                }
            }
        }

        [Theory]
        [InlineData("UsdPerLot", true)]
        [InlineData("UsdPerMillionUsdVolume", true)]
        [InlineData("UsdPerMillionUsdVolume", false)]
        [InlineData("PercentOfVolume", false)]
        [InlineData("QuoteCurrencyPerLot", false)]
        public void RequiredFxIsNeverReplacedByOneAndInvalidRatesFailClosed(string kind, bool isUsd)
        {
            foreach (double invalid in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                var dto = Dto(kind: kind, usd: isUsd ? invalid : 1, quote: isUsd ? 1 : invalid);
                var spec = SpecMapper.ToSpec(dto, null, null);
                Assert.Equal(invalid, isUsd ? spec.UsdToAccount : spec.QuoteToAccount);
                Assert.NotNull(spec.Validate());
                Assert.True(double.IsNaN(spec.CommissionOneSide(1000, 1.1)));
                Assert.False(PositionSizer.Size(spec, 50, 0.002, 1.1, 0, 0).Ok);
            }
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void InvalidBaseCommissionIsNotMaskedByFloor(double value)
        {
            var spec = SpecMapper.ToSpec(Dto(1, true, value: value), null, null);
            Assert.NotNull(spec.Validate());
            Assert.False(PositionSizer.Size(spec, 50, 0.002, 1.1, 0, 0).Ok);
        }
    }
}
