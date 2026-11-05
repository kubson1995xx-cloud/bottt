using System;
using System.Collections.Generic;
using System.Reflection;
using Apex.Platforms.CTrader;
using cAlgo.API;
using Xunit;

namespace Apex.Platform.Tests;
public sealed class HistoryTests
{
    public class Properties : DispatchProxy
    {
        public Dictionary<string, object?> Values = new();
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Values[method!.Name];
    }
    static HistoricalTrade Deal(int deal, int position = 42, string label = "APEX", double net = 7.5)
    {
        var h = DispatchProxy.Create<HistoricalTrade, Properties>(); var d = ((Properties)(object)h).Values;
        d["get_ClosingDealId"] = deal; d["get_PositionId"] = position; d["get_Label"] = label; d["get_Comment"] = "";
        d["get_SymbolName"] = "US500"; d["get_TradeType"] = TradeType.Buy; d["get_VolumeInUnits"] = 1.0;
        d["get_EntryPrice"] = 4000.0; d["get_ClosingPrice"] = 4010.0;
        d["get_EntryTime"] = new DateTime(2026, 10, 26, 8, 0, 0); d["get_ClosingTime"] = new DateTime(2026, 10, 26, 10, 0, 0);
        d["get_GrossProfit"] = 10.0; d["get_Commissions"] = -1.5; d["get_Swap"] = -1.0; d["get_NetProfit"] = net;
        return h;
    }
    [Fact]
    public void SdkDealsPreserveCanonicalIdentitySignedCostsAndPartialRows()
    {
        var result = CTraderHistory.Capture(new[] { Deal(2), Deal(1), Deal(1), Deal(3, label: "APEX-other") }, "APEX");
        Assert.Equal(2, result.Count); Assert.Equal(1L, result[0].ClosingDealId); Assert.Equal(2L, result[1].ClosingDealId);
        Assert.Equal(1, result[0].Units); Assert.Equal(7.5, result[0].Net); Assert.Equal(-1.5, result[0].Commission); Assert.Equal(-1, result[0].Swap);
        Assert.Equal(42, result[0].Id); Assert.Equal("buy", result[0].Side);
    }
    [Fact]
    public void ConflictingRawDealPayloadFailsClosed() => Assert.Throws<InvalidOperationException>(() =>
        CTraderHistory.Capture(new[] { Deal(1), Deal(1, net: 999) }, "APEX"));
    [Fact]
    public void EmptyAvailableHistoryHasNoFabricatedPositionFallback() => Assert.Empty(CTraderHistory.Capture(Array.Empty<HistoricalTrade>(), "APEX"));
    [Fact]
    public void NativeEnvelopeRejectsTornTamperedAndWrongIdentityButReadsLegacy()
    {
        const string payload = "{\"SchemaVersion\":2,\"AccountNumber\":42}";
        string encoded = RecoveryEnvelope.Encode("broker|42|USD|APEX", payload);
        Assert.Equal(payload, RecoveryEnvelope.Decode("broker|42|USD|APEX", encoded));
        Assert.ThrowsAny<Exception>(() => RecoveryEnvelope.Decode("other|42|USD|APEX", encoded));
        Assert.ThrowsAny<Exception>(() => RecoveryEnvelope.Decode("broker|42|USD|APEX", encoded.Replace("AccountNumber", "AccountNumbex")));
        Assert.ThrowsAny<Exception>(() => RecoveryEnvelope.Decode("broker|42|USD|APEX", encoded.Substring(0, encoded.Length - 4)));
        const string legacy = "{\"SchemaVersion\":1,\"AccountNumber\":42}";
        Assert.Equal(legacy, RecoveryEnvelope.Decode("broker|42|USD|APEX", legacy));
    }
}
