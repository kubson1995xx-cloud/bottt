using Apex.Adapters.Okx;
using Xunit;
namespace Apex.Adapters.Okx.Tests;
public class OkxPaginationTests
{
    [Fact]
    public async Task UsesEndpointCursor()
    { using var f = new OkxHttpFixture(); f.Bootstrap(); f.Add("/api/v5/trade/fills?instType=SPOT&limit=100", OkxHttpFixture.Envelope(OkxHttpFixture.Fill())); f.Add("/api/v5/trade/fills?after=bill-B&instType=SPOT&limit=100", OkxHttpFixture.Envelope()); await using var c = f.Client(); var p = await OkxReadPager.Collect("/api/v5/trade/fills", new(), (q, t) => c.GetFillsAsync(OkxHistoryWindow.Recent, q, t), x => x.BillId, x => x.Ts.Utc, x => x.FillTime.Utc, default); Assert.Null(p.Error); Assert.Single(p.Rows); Assert.Equal(2, p.Trace.Count); Assert.True(p.Trace[1].Exhausted); f.Done(); }
    [Fact]
    public async Task ShortPageIsNotExhaustion()
    { using var f = new OkxHttpFixture(); f.Bootstrap(); f.Add("/api/v5/trade/orders-pending?limit=100", OkxHttpFixture.Envelope(OkxHttpFixture.Order("A"), OkxHttpFixture.Order("B"))); f.Add("/api/v5/trade/orders-pending?after=B&limit=100", OkxHttpFixture.Envelope(OkxHttpFixture.Order("C"))); f.Add("/api/v5/trade/orders-pending?after=C&limit=100", OkxHttpFixture.Envelope()); await using var c = f.Client(); var p = await OkxReadPager.Collect("/api/v5/trade/orders-pending", new(), c.GetPendingOrdersAsync, x => x.OrdId, x => x.UTime.Utc, null, default); Assert.Null(p.Error); Assert.Equal(3, p.Rows.Count); Assert.Equal(3, p.Trace.Count); f.Done(); }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoundsAndConflict(bool cap)
    { using var f = new OkxHttpFixture(); f.Bootstrap(); for (var i = 0; i < (cap ? 20 : 2); i++) f.Add("/api/v5/trade/orders-pending?" + (i == 0 ? "" : "after=" + (cap ? (i - 1).ToString() : "A") + "&") + "limit=100", OkxHttpFixture.Envelope(OkxHttpFixture.Order(cap ? i.ToString() : "A"))); await using var c = f.Client(); var p = await OkxReadPager.Collect("/api/v5/trade/orders-pending", new(), c.GetPendingOrdersAsync, x => x.OrdId, x => x.UTime.Utc, null, default); Assert.Equal(cap ? "PageLimit" : "CursorCycle", p.Error); Assert.Equal(cap ? 20 : 2, p.Trace.Count); Assert.DoesNotContain(p.Trace, t => t.Exhausted); f.Done(); }
    [Fact]
    public async Task FailedFinalPageKeepsEarlierRowsExplicitlyPartial()
    { using var f = new OkxHttpFixture(); f.Bootstrap(); f.Add("/api/v5/trade/fills?instType=SPOT&limit=100", OkxHttpFixture.Envelope(OkxHttpFixture.Fill())); f.Add("/api/v5/trade/fills?after=bill-B&instType=SPOT&limit=100", "{\"code\":\"50101\",\"data\":[]}"); await using var c = f.Client(); var p = await OkxReadPager.Collect("/api/v5/trade/fills", new(), (q, t) => c.GetFillsAsync(OkxHistoryWindow.Recent, q, t), f => f.BillId, f => f.Ts.Utc, f => f.FillTime.Utc, default); Assert.Single(p.Rows); Assert.Equal("50101", p.Error); Assert.Equal("bill-B", p.Trace[1].Query.After); Assert.False(p.Trace[1].Exhausted); }
    [Fact]
    public async Task TsBoundaryIsNotFillTimeBoundary()
    { using var f = new OkxHttpFixture(); var begin = f.Clock.Now.AddSeconds(-20); var fillTime = f.Clock.Now; f.Bootstrap(); var path = "/api/v5/trade/fills?begin=" + begin.ToUnixTimeMilliseconds() + "&instType=SPOT&limit=100"; f.Add(path, OkxHttpFixture.Envelope(OkxHttpFixture.Fill(ts: begin.AddMilliseconds(-1).ToUnixTimeMilliseconds().ToString(), fillTime: fillTime.ToUnixTimeMilliseconds().ToString()))); await using var c = f.Client(); var p = await OkxReadPager.Collect("/api/v5/trade/fills", new(BeginUtc: begin), (q, t) => c.GetFillsAsync(OkxHistoryWindow.Recent, q, t), f => f.BillId, f => f.Ts.Utc, f => f.FillTime.Utc, default); Assert.Null(p.Error); Assert.True(Assert.Single(p.Trace).BoundaryReached); Assert.False(p.Trace[0].Exhausted); Assert.Equal(fillTime, p.Trace[0].LatestFillTime); f.Done(); }

    [Fact]
    public async Task MissingCursorAndOversizedPageFailWithoutSuccessRows()
    { foreach (bool missing in new[] { true, false }) { using var f = new OkxHttpFixture(); f.Bootstrap(); var row = OkxHttpFixture.Fill().Replace("\"billId\":\"bill-B\",", missing ? "" : "\"billId\":\"bill-B\","); f.Add("/api/v5/trade/fills?instType=SPOT&limit=100", OkxHttpFixture.Envelope(missing ? new[] { row } : Enumerable.Repeat(row, 101).ToArray())); await using var c = f.Client(); var p = await OkxReadPager.Collect("/api/v5/trade/fills", new(), (q, t) => c.GetFillsAsync(OkxHistoryWindow.Recent, q, t), f => f.BillId, f => f.Ts.Utc, f => f.FillTime.Utc, default); Assert.Equal("InvalidPayload", p.Error); Assert.Empty(p.Rows); Assert.False(Assert.Single(p.Trace).Exhausted); } }

}
