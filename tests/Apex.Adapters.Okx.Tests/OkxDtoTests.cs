using Apex.Adapters.Okx;
using Xunit;
namespace Apex.Adapters.Okx.Tests;
public class OkxDtoTests
{
    [Fact]
    public async Task ExactStringsDecimalsAndUnknowns()
    {
        using var f = new OkxHttpFixture(); f.Bootstrap(); var rows = new[] { OkxHttpFixture.Fill(), OkxHttpFixture.Fill("999999999999999999999999999999999999", "Trade-A", "+0.00000001", "USD"), OkxHttpFixture.Fill("b3", "trade-A", "-0.00000001", "THIRD") }; f.Add("/api/v5/trade/fills?instType=SPOT&limit=100", OkxHttpFixture.Envelope(rows)); await using var c = f.Client(); var result = await c.GetFillsAsync(OkxHistoryWindow.Recent, new(), default); Assert.Equal(OkxReadStatus.Observed, result.Status); Assert.Equal(3, result.Rows.Length); Assert.Equal(-0.00000001m, result.Rows[0].Fee.Value); Assert.Equal("-0.00000001", result.Rows[0].Fee.Raw); Assert.Equal(0.00000001m, result.Rows[1].Fee.Value); Assert.Equal("123-Ab", result.Rows[0].InstId); Assert.Null(result.Rows[0].FeeRate.Value); Assert.NotEqual(result.Rows[0].TradeId, result.Rows[1].TradeId); Assert.Equal("999999999999999999999999999999999999", result.Rows[1].BillId); f.Done();
    }
    [Theory]
    [InlineData("0.1234567890123456789012345678", true)]
    [InlineData("0.12345678901234567890123456789", false)]
    [InlineData("79228162514264337593543950336", false)]
    [InlineData("1e-2", false)]
    [InlineData(" 1", false)]
    [InlineData("1,2", false)]
    [InlineData("NaN", false)]
    public async Task ExactDecimalGrammar(string price, bool valid)
    { using var f = new OkxHttpFixture(); f.Bootstrap(); f.Add("/api/v5/trade/fills?instType=SPOT&limit=100", OkxHttpFixture.Envelope(OkxHttpFixture.Fill(price: price))); await using var c = f.Client(); var r = await c.GetFillsAsync(OkxHistoryWindow.Recent, new(), default); Assert.Equal(valid ? OkxReadStatus.Observed : OkxReadStatus.Failed, r.Status); if (!valid) Assert.Empty(r.Rows); }
    [Theory]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"code\":\"0\"}")]
    [InlineData("{\"code\":\"0\",\"code\":\"0\",\"data\":[]}")]
    [InlineData("{\"code\":\"0\",\"data\":[{\"ts\":1793361600000}]}")]
    [InlineData("{\"code\":\"0\",\"data\":[{\"ts\":\"1793361600000\",\"ts\":\"1\"}]}")]
    [InlineData("{\"code\":\"0\",\"data\":[]}")]
    [InlineData("{\"code\":\"0\",\"data\":[{\"ts\":\"1793361600000\"},{\"ts\":\"1793361600000\"}]}")]
    public async Task StrictEnvelope(string json)
    { using var f = new OkxHttpFixture(); f.Add("/api/v5/public/time", json); await using var c = f.Client(false); var r = await c.GetTimeAsync(default); Assert.Equal(OkxReadStatus.Failed, r.Status); Assert.Empty(r.Rows); }
    [Theory]
    [InlineData("\"fee\":\"-0.00000001\"", "\"fee\":1")]
    [InlineData("\"billId\":\"bill-B\",", "")]
    [InlineData("\"feeCcy\":\"ABC\",", "")]
    public async Task MissingOrWrongEconomicFieldsFail(string find, string replace)
    { using var f = new OkxHttpFixture(); f.Bootstrap(); f.Add("/api/v5/trade/fills?instType=SPOT&limit=100", OkxHttpFixture.Envelope(OkxHttpFixture.Fill().Replace(find, replace))); await using var c = f.Client(); Assert.Equal(OkxReadStatus.Failed, (await c.GetFillsAsync(OkxHistoryWindow.Recent, new(), default)).Status); }
    [Fact]
    public async Task PublicCatalogDoesNotInventAccountQuoteEligibility()
    { using var f = new OkxHttpFixture(); f.Add("/api/v5/public/instruments?instType=SPOT", OkxHttpFixture.Envelope(OkxHttpFixture.Instrument.Replace("\"tradeQuoteCcyList\":[\"USD\"],", ""))); await using var c = f.Client(false); var r = await c.GetPublicSpotInstrumentsAsync(default); Assert.Equal(OkxReadStatus.Observed, r.Status); Assert.Empty(Assert.Single(r.Rows).TradeQuoteCcyList); }
    [Fact]
    public async Task UnknownStringsAreRetainedWithoutSafeDefault()
    { using var f = new OkxHttpFixture(); f.Bootstrap(); f.Add("/api/v5/trade/orders-pending?limit=100", OkxHttpFixture.Envelope(OkxHttpFixture.Order(state: "future-new", type: "UNKNOWN"))); await using var c = f.Client(); var r = await c.GetPendingOrdersAsync(new(), default); Assert.Equal("future-new", Assert.Single(r.Rows).State); Assert.Equal("UNKNOWN", r.Rows[0].InstType); }
    [Fact]
    public async Task MultipleConfigSingletonsAreRejected()
    { using var f = new OkxHttpFixture(); f.Bootstrap(); var time = f.Steps.Dequeue(); f.Steps.Clear(); f.Steps.Enqueue(time); f.Add("/api/v5/account/config", OkxHttpFixture.Envelope(OkxHttpFixture.Config(), OkxHttpFixture.Config())); await using var c = f.Client(); Assert.Equal("InvalidPayload", (await c.GetBalancesAsync(default)).ErrorCode); Assert.Equal(2, f.Sent.Count); }

}
