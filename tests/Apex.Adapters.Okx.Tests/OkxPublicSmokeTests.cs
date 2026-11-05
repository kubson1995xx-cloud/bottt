using Apex.Adapters.Okx;
using Xunit;
using Xunit.Abstractions;
namespace Apex.Adapters.Okx.Tests;
public sealed class PublicSmokeFactAttribute : FactAttribute
{
    public PublicSmokeFactAttribute() { if (System.Environment.GetEnvironmentVariable("APEX_OKX_PUBLIC_READ_SMOKE") != "1") Skip = "SKIPPED: public network smoke requires APEX_OKX_PUBLIC_READ_SMOKE=1"; }
}
public class OkxPublicSmokeTests(ITestOutputHelper output)
{
    [PublicSmokeFact]
    public async Task TimeAndSpotCatalog()
    {
        await using var client = OkxEeaDemoReadClient.CreatePublic();
        var time = await client.GetTimeAsync(default);
        var instruments = await client.GetPublicSpotInstrumentsAsync(default);
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Kind = "GENUINE_PUBLIC_ONLY", Host = "https://eea.okx.com:443", TimeStatus = time.Status.ToString(), TimeError = time.ErrorCode, TimeRequest = time.RequestUtc, TimeReceive = time.ReceiveUtc, TimeSha256 = time.ResponseSha256, ServerUtc = time.Rows.FirstOrDefault()?.Ts.Utc, InstrumentStatus = instruments.Status.ToString(), InstrumentError = instruments.ErrorCode, InstrumentRequest = instruments.RequestUtc, InstrumentReceive = instruments.ReceiveUtc, InstrumentSha256 = instruments.ResponseSha256, InstrumentCount = instruments.Rows.Length, InstrumentIds = instruments.Rows.Select(i => i.InstId).ToArray(), PrivateSmoke = "BLOCKED: securely configured credentials absent" }));
        Assert.Equal(OkxReadStatus.Observed, time.Status); Assert.NotNull(Assert.Single(time.Rows).Ts.Utc);
        Assert.Equal(OkxReadStatus.Observed, instruments.Status); Assert.NotEmpty(instruments.Rows); Assert.All(instruments.Rows, i => Assert.Equal("SPOT", i.InstType));
    }
}
