using System;
using Apex.Platforms.CTrader;
using Xunit;

namespace Apex.Platform.Tests
{
    public class PlatformSafetyTests
    {
        [Theory]
        [InlineData(-31.0, false)]
        [InlineData(-30.0, true)]
        [InlineData(0.0, true)]
        [InlineData(6.0, false)]
        public void StaleAndFutureMarketTimesAreNotAdmitted(double offsetSeconds, bool fresh)
        {
            var now = new DateTime(2026, 10, 26, 10, 0, 0, DateTimeKind.Utc);
            Assert.Equal(fresh, PlatformSafety.Fresh(now.AddSeconds(offsetSeconds), now, 30));
        }

        [Fact]
        public void ProtectionRepairExceptionStillAttemptsEmergencyClose()
        {
            bool closed = false;
            bool protectedPosition = PlatformSafety.RepairOrClose(() => throw new InvalidOperationException("platform"), () => closed = true);
            Assert.True(closed);
            Assert.False(protectedPosition);
        }

        [Fact]
        public void SuccessfulProtectionRepairDoesNotCloseThePosition()
        {
            bool closed = false;
            Assert.True(PlatformSafety.RepairOrClose(() => true, () => closed = true));
            Assert.False(closed);
        }

        [Fact]
        public void StorageKeysMeetActualCTraderAlphanumericAndLengthContractWithoutIdentityCollisions()
        {
            var key = PlatformSafety.StorageKey("Bridge", "Pepperstone Ltd. / Razor", 12345678, "APEX-N " + new string('x', 80));
            Assert.InRange(key.Length, 1, 50);
            Assert.Matches("^[a-zA-Z0-9]+(?: [a-zA-Z0-9]+)*$", key);
            Assert.Equal(key, PlatformSafety.StorageKey("Bridge", "Pepperstone Ltd. / Razor", 12345678, "APEX-N " + new string('x', 80)));
            Assert.NotEqual(key, PlatformSafety.StorageKey("Bridge", "Pepperstone Ltd. / Razor", 12345679, "APEX-N " + new string('x', 80)));
            Assert.NotEqual(PlatformSafety.StorageKey("Bridge", "Broker", 1, "APEX-N"), PlatformSafety.StorageKey("Bridge", "Broker", 1, "APEXN"));
            Assert.NotEqual(PlatformSafety.StorageKey("Bridge", "Broker", 1, "APEX"), PlatformSafety.StorageKey("Native", "Broker", 1, "APEX"));
        }

        [Theory]
        [InlineData(true, false, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, true)]
        public void RealIsRejectedRegardlessOfLegacyFlag(bool live, bool flag, bool allowed) => Assert.Equal(allowed, PlatformSafety.DemoAllowed(live, flag));

        [Theory]
        [InlineData("APEX", "APEX", true)]
        [InlineData("APEX-N", "APEX", false)]
        [InlineData("APEX-other", "APEX", false)]
        [InlineData("", "", false)]
        [InlineData(null, "APEX", false)]
        public void OwnershipIsExactNonemptyLabel(string? label, string expected, bool own) => Assert.Equal(own, PlatformSafety.OwnLabel(label, expected));

        [Theory]
        [InlineData(10, null, false)]
        [InlineData(10, 0.0, false)]
        [InlineData(double.PositiveInfinity, 10.0, false)]
        [InlineData(10, double.NaN, false)]
        [InlineData(10, 20.0, true)]
        public void BothFinitePositiveProtectionLegsAreMandatory(double sl, double? tp, bool valid) => Assert.Equal(valid, PlatformSafety.Protection(sl, tp));

        [Theory]
        [InlineData(1000, 1000, 100000, true)]
        [InlineData(double.PositiveInfinity, 1000, 100000, false)]
        [InlineData(100001, 1000, 100000, false)]
        [InlineData(double.NaN, 1000, 100000, false)]
        public void UnitsCannotExceedBrokerMaximum(double units, double min, double max, bool valid) => Assert.Equal(valid, PlatformSafety.Volume(units, min, max));

        [Theory]
        [InlineData(null, true)]
        [InlineData(0.0, false)]
        [InlineData(-1.0, false)]
        [InlineData(double.NaN, false)]
        [InlineData(11000.0, false)]
        [InlineData(9500.0, false)]
        [InlineData(500.0, false)]
        [InlineData(2000.0, true)]
        [InlineData(10000.0, true)]
        public void CloseDoesNotBecomeUnintendedFullCloseOrDust(double? units, bool valid) => Assert.Equal(valid, PlatformSafety.PartialClose(units, 10000, 1000, 1000));

        [Theory]
        [InlineData(1.0, true, 2000.0, 0.1, 200.0)]
        [InlineData(10.0, false, 2000.0, 0.1, 10.0)]
        public void PercentDistanceIsConvertedToPips(double distance, bool percent, double price, double pip, double expected) => Assert.Equal(expected, PlatformSafety.MinimumPips(distance, percent, price, pip), 8);
    }
}
