using System;
using System.IO;
using Xunit;

namespace Apex.Tests
{
    // Supplemental wiring checks only. Executable safety policy/journal tests live in
    // src/Platforms/cTrader/Tests; neither suite executes the cTrader broker runtime.
    public class PlatformContractTests
    {
        private static string Source(string path)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Platforms"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, "src", "Platforms", path));
        }

        [Theory]
        [InlineData("cTrader/ApexBridge/ApexBridge/ApexBridge.cs", "Account.IsLive && !AllowLive")]
        [InlineData("MT5/ApexBridge.mq5", "live && !InpAllowLive")]
        public void NoPlatformLiveBypass(string path, string bypass) => Assert.DoesNotContain(bypass, Source(path));

        [Fact]
        public void BridgeDoesNotStampTimerTimeOntoQuote() =>
            Assert.DoesNotContain("T = now", Source("cTrader/ApexBridge/ApexBridge/ApexBridge.cs"));

        [Fact]
        public void BridgeDoesNotOwnNativeOrPrefixNeighbourLabels() =>
            Assert.DoesNotContain("p.Label.StartsWith(LabelPrefix", Source("cTrader/ApexBridge/ApexBridge/ApexBridge.cs"));

        [Fact]
        public void NativeDoesNotStampReadTimeOntoQuote() =>
            Assert.DoesNotContain("new Quote(s.Bid, s.Ask, _r.Server.TimeInUtc)", Source("cTrader/ApexTradingAI/ApexTradingAI/ApexTradingAI.cs"));
    }
}
