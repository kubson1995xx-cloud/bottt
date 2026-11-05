using Apex.Adapters.Okx;
using Xunit;
namespace Apex.Adapters.Okx.Tests;
public class OkxSnapshotTests
{
    [Fact]
    public async Task BookendsAndReconciliationAreObservations()
    { using var f = new OkxHttpFixture(); f.CaptureScript(); var request = f.SnapshotRequest(); await using var c = f.Client(); var s = await new OkxSnapshotReader(c).CaptureAsync(request, default); Assert.Equal(OkxCollectionStatus.Collected, s.CollectionStatus); Assert.Equal(OkxCoverage.Unknown, s.Coverage); Assert.Contains("UnprovenEventContinuity", s.Reasons); Assert.Single(s.Fills); Assert.Single(s.Bills); var usd = Assert.Single(s.CurrencyDiagnostics); Assert.Equal(-10m, usd.BillChange); Assert.Equal(0m, usd.CashDifference); Assert.DoesNotContain("CashBillMismatch", s.Reasons); Assert.Contains("CashBillReconciliationUnavailable", s.Reasons); Assert.Equal(OkxHttpFixture.Uid, s.Uid); Assert.Equal("okx-demo-read-snapshot-v1", s.Schema); Assert.NotNull(s.ConfigAfter); Assert.Throws<InvalidOperationException>(() => s.ExportRedactedDiagnostics(false)); var export = s.ExportRedactedDiagnostics(true); Assert.DoesNotContain(s.Uid!, export); Assert.DoesNotContain(f.Secrets.Binding, export); Assert.DoesNotContain("192.0.2.4", export); f.Done(); }
    [Fact]
    public async Task DisconnectRecaptureIsNewGeneration()
    { using var f = new OkxHttpFixture(); f.CaptureScript(failFills: true); var request = f.SnapshotRequest(); await using var c = f.Client(); var reader = new OkxSnapshotReader(c); var a = await reader.CaptureAsync(request, default); Assert.Equal(OkxCollectionStatus.Incomplete, a.CollectionStatus); Assert.Empty(a.Fills); f.Done(); f.CaptureScript(); var b = await reader.CaptureAsync(request, default); Assert.NotEqual(a.Generation, b.Generation); Assert.Equal(OkxCollectionStatus.Collected, b.CollectionStatus); Assert.Single(b.Fills); f.Done(); }
    [Fact]
    public async Task MovedBookendsRemainIncomplete()
    { using var f = new OkxHttpFixture(); f.CaptureScript(moved: true); var request = f.SnapshotRequest(); await using var c = f.Client(); var s = await new OkxSnapshotReader(c).CaptureAsync(request, default); Assert.Equal(OkxCollectionStatus.Incomplete, s.CollectionStatus); Assert.Contains("SnapshotMoved", s.Reasons); }
    [Fact]
    public async Task PrivateSnapshotExportIsExplicitAndRedacted()
    { using var f = new OkxHttpFixture(); f.CaptureScript(); var req = f.SnapshotRequest(); await using var c = f.Client(); var s = await new OkxSnapshotReader(c).CaptureAsync(req, default); Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Serialize(s)); }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlapIsIdempotentAndChangedEconomicsConflict(bool changed)
    {
        using var f = new OkxHttpFixture(); var to = f.Clock.Now; var from = to.AddDays(-8); var q = "begin=" + from.ToUnixTimeMilliseconds() + "&end=" + to.ToUnixTimeMilliseconds();
        f.Bootstrap(); f.Add("/api/v5/account/instruments?instType=SPOT", OkxHttpFixture.Envelope(OkxHttpFixture.Instrument)); f.Add("/api/v5/account/balance", OkxHttpFixture.Envelope(OkxHttpFixture.Balance())); f.Add("/api/v5/trade/orders-pending?limit=100", OkxHttpFixture.Envelope());
        foreach (var path in new[] { "/api/v5/trade/fills", "/api/v5/trade/fills-history" }) { f.Add(path + "?" + q + "&instType=SPOT&limit=100", OkxHttpFixture.Envelope(OkxHttpFixture.Fill(price: changed && path.EndsWith("history") ? "11" : "10"))); f.Add(path + "?after=bill-B&" + q + "&instType=SPOT&limit=100", OkxHttpFixture.Envelope()); }
        foreach (var path in new[] { "/api/v5/account/bills", "/api/v5/account/bills-archive" }) { f.Add(path + "?" + q + "&limit=100", OkxHttpFixture.Envelope(OkxHttpFixture.Bill())); f.Add(path + "?after=bill-B&" + q + "&limit=100", OkxHttpFixture.Envelope()); }
        f.Add("/api/v5/account/balance", OkxHttpFixture.Envelope(OkxHttpFixture.Balance())); f.Add("/api/v5/trade/orders-pending?limit=100", OkxHttpFixture.Envelope()); f.Add("/api/v5/account/config", OkxHttpFixture.Envelope(OkxHttpFixture.Config()));
        await using var c = f.Client(); var s = await new OkxSnapshotReader(c).CaptureAsync(new(from, to, Array.Empty<OkxOrderRef>()), default); Assert.Single(s.Fills); Assert.Single(s.Bills); Assert.Equal(changed ? OkxCoverage.Conflict : OkxCoverage.Unknown, s.Coverage); Assert.Equal(changed ? OkxCollectionStatus.Incomplete : OkxCollectionStatus.Collected, s.CollectionStatus); if (changed) Assert.Contains("EconomicIdentityConflict", s.Reasons); f.Done();
    }
    [Theory]
    [InlineData("ABC", "-0.00000001", 0.99999999, -10)]
    [InlineData("USD", "+0.00000001", 1, -9.99999999)]
    [InlineData("THIRD", "-0.00000001", 1, -10)]
    public async Task FeeDiagnosticIsSignedAndNotPostedTwice(string ccy, string fee, double baseValue, double quoteValue)
    { using var f = new OkxHttpFixture(); f.CaptureScript(fill: OkxHttpFixture.Fill(fee: fee, ccy: ccy)); var req = f.SnapshotRequest(); await using var c = f.Client(); var s = await new OkxSnapshotReader(c).CaptureAsync(req, default); var cash = Assert.Single(s.FillCashflows); Assert.Equal((decimal)baseValue, cash.BaseChange); Assert.Equal((decimal)quoteValue, cash.QuoteChange); Assert.True(cash.BillLinked); if (ccy == "THIRD") Assert.Contains("ThirdCurrencyFee", s.Reasons); Assert.Contains("MissingBalanceCurrency", s.Reasons); }
    [Fact]
    public async Task ChangedConfigBookendIsConflictAndNeverAuthority()
    { using var f = new OkxHttpFixture(); f.CaptureScript(); var last = f.Steps.ToArray(); f.Steps.Clear(); foreach (var step in last.Take(last.Length - 1)) f.Steps.Enqueue(step); f.Add("/api/v5/account/config", OkxHttpFixture.Envelope(OkxHttpFixture.Config().Replace("main-1", "main-2"))); var req = f.SnapshotRequest(); await using var c = f.Client(); var s = await new OkxSnapshotReader(c).CaptureAsync(req, default); Assert.Equal(OkxCoverage.Conflict, s.Coverage); Assert.Contains("AccountConfigChanged", s.Reasons); Assert.Equal(OkxReadStatus.Failed, s.ConfigAfter!.Status); Assert.DoesNotContain(typeof(OkxReadSnapshot).GetMembers(), m => m.Name is "Complete" or "Receipt" or "Permit" or "Start"); }
    [Fact]
    public async Task CaptureBudgetIncludesAllPagesAndBookends()
    { using var f = new OkxHttpFixture(); f.CaptureScript(); var req = f.SnapshotRequest(); f.Inspect = r => { if (r.RequestUri!.AbsolutePath == "/api/v5/account/balance") f.Clock.Now += TimeSpan.FromSeconds(121); }; await using var c = f.Client(); var s = await new OkxSnapshotReader(c).CaptureAsync(req, default); Assert.Equal(OkxCollectionStatus.Incomplete, s.CollectionStatus); Assert.Contains("CaptureTimeout", s.Reasons); Assert.Equal(4, f.Sent.Count); }
    [Theory]
    [InlineData(2)]
    [InlineData(21)]
    public async Task InvalidUnresolvedReferencesNeverSend(int count)
    { using var f = new OkxHttpFixture(); await using var c = f.Client(); var refs = Enumerable.Repeat(new OkxOrderRef("123-Ab", "same"), count).ToArray(); var s = await new OkxSnapshotReader(c).CaptureAsync(new(f.Clock.Now.AddMinutes(-1), f.Clock.Now, refs), default); Assert.Equal(OkxCollectionStatus.Failed, s.CollectionStatus); Assert.Contains("InvalidRequest", s.Reasons); Assert.Empty(f.Sent); }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetentionAndUnavailableArchiveAreNotSilent(bool unavailable)
    {
        using var f = new OkxHttpFixture(); f.CaptureScript(); var to = f.Clock.Now; var from = unavailable ? to.AddDays(-8) : to.AddMonths(-4); var steps = f.Steps.ToArray(); f.Steps.Clear();
        foreach (var step in steps)
        {
            var path = step.Path.Replace("begin=1793361540000", "begin=" + from.ToUnixTimeMilliseconds()); f.Steps.Enqueue((path, step.Reply));
            if (step.Path == "/api/v5/trade/fills?after=bill-B&begin=1793361540000&end=1793361600000&instType=SPOT&limit=100") { var a = "/api/v5/trade/fills-history?begin=" + from.ToUnixTimeMilliseconds() + "&end=1793361600000&instType=SPOT&limit=100"; for (int i = 0; i < (unavailable ? 3 : 1); i++) f.Add(a, unavailable ? "{}" : OkxHttpFixture.Envelope(), unavailable ? System.Net.HttpStatusCode.ServiceUnavailable : System.Net.HttpStatusCode.OK); }
            if (step.Path == "/api/v5/account/bills?after=bill-B&begin=1793361540000&end=1793361600000&limit=100") f.Add("/api/v5/account/bills-archive?begin=" + from.ToUnixTimeMilliseconds() + "&end=1793361600000&limit=100", OkxHttpFixture.Envelope());
        }
        await using var c = f.Client(); var s = await new OkxSnapshotReader(c).CaptureAsync(new(from, to, Array.Empty<OkxOrderRef>()), default); Assert.Equal(OkxCoverage.Gap, s.Coverage); Assert.Contains(unavailable ? "ArchiveUnavailable" : "RequestedBeyondRetention", s.Reasons); Assert.Equal(unavailable ? OkxCollectionStatus.Incomplete : OkxCollectionStatus.Collected, s.CollectionStatus); f.Done();
    }
    [Fact]
    public async Task ForeignPendingLiabilityMissingLinksAndUnknownBillAreVisible()
    {
        using var f = new OkxHttpFixture(); f.CaptureScript(bill: OkxHttpFixture.Bill(type: "future-type").Replace("trade-A", "other-trade")); var req = f.SnapshotRequest(); var steps = f.Steps.ToArray(); f.Steps.Clear();
        foreach (var step in steps)
        {
            if (step.Path == "/api/v5/account/balance") f.Add(step.Path, OkxHttpFixture.Envelope(OkxHttpFixture.Balance(liab: "1")));
            else if (step.Path == "/api/v5/trade/orders-pending?limit=100") { f.Add(step.Path, OkxHttpFixture.Envelope(OkxHttpFixture.Order(type: "SWAP", mode: "cross"))); f.Add("/api/v5/trade/orders-pending?after=ord-9999999999999999999999&limit=100", OkxHttpFixture.Envelope()); }
            else f.Steps.Enqueue(step);
        }
        await using var c = f.Client(); var s = await new OkxSnapshotReader(c).CaptureAsync(req, default); foreach (var reason in new[] { "ForeignPendingExposure", "LiabilityOrBorrowUnknown", "MissingFillBillLink", "UnknownBillType", "MissingBalanceCurrency" }) Assert.Contains(reason, s.Reasons); Assert.Equal(OkxCoverage.Unknown, s.Coverage); f.Done();
    }
    [Fact]
    public async Task DisconnectBetweenPagesRecapturesOverlapAndLateUniqueFill()
    {
        using var f = new OkxHttpFixture(); f.CaptureScript(); var req = f.SnapshotRequest(); var steps = f.Steps.ToArray(); f.Steps.Clear();
        foreach (var step in steps) if (step.Path.StartsWith("/api/v5/trade/fills?after=", StringComparison.Ordinal)) for (int i = 0; i < 3; i++) f.AddError(step.Path, new HttpRequestException("synthetic interrupted page")); else f.Steps.Enqueue(step);
        await using var c = f.Client(); var reader = new OkxSnapshotReader(c); var a = await reader.CaptureAsync(req, default); Assert.Equal(OkxCollectionStatus.Incomplete, a.CollectionStatus); Assert.Single(a.Fills); f.Done();
        f.CaptureScript(fill: string.Join(',', OkxHttpFixture.Fill("bill-L", "late-trade", fee: "+0.00000001", ccy: "USD"), OkxHttpFixture.Fill(), OkxHttpFixture.Fill())); var b = await reader.CaptureAsync(req, default); Assert.NotEqual(a.Generation, b.Generation); Assert.Equal(2, b.Fills.Length); Assert.Equal(OkxCollectionStatus.Collected, b.CollectionStatus); Assert.Equal(OkxCoverage.Unknown, b.Coverage); f.Done();
    }
    [Fact]
    public async Task TerminalDetailIsAnObservationNotAnExecutionEnumeration()
    {
        using var f = new OkxHttpFixture(); f.CaptureScript(); var req = f.SnapshotRequest() with { UnresolvedOrders = new[] { new OkxOrderRef("123-Ab", "ord-9999999999999999999999") } }; var steps = f.Steps.ToArray(); f.Steps.Clear(); bool inserted = false;
        foreach (var step in steps) { f.Steps.Enqueue(step); if (!inserted && step.Path == "/api/v5/trade/orders-pending?limit=100") { inserted = true; f.Add("/api/v5/trade/order?instId=123-Ab&ordId=ord-9999999999999999999999", OkxHttpFixture.Envelope(OkxHttpFixture.Order(state: "filled"))); } }
        await using var c = f.Client(); var s = await new OkxSnapshotReader(c).CaptureAsync(req, default); Assert.Equal("filled", Assert.Single(s.OrderDetails).State); Assert.Single(s.Fills); Assert.Equal(OkxCoverage.Unknown, s.Coverage); Assert.Contains("UnprovenEventContinuity", s.Reasons); f.Done();
    }

    [Fact]
    public void SnapshotCannotBeFabricatedByPublicConstructor()
    { Assert.Empty(typeof(OkxReadSnapshot).GetConstructors()); }
    [Fact]
    public async Task NonUtcAndFutureIntervalsNeverCollect()
    { using var f = new OkxHttpFixture(); await using var c = f.Client(); var invalid = await new OkxSnapshotReader(c).CaptureAsync(new(f.Clock.Now.AddMinutes(-1).ToOffset(TimeSpan.FromHours(1)), f.Clock.Now, Array.Empty<OkxOrderRef>()), default); Assert.Equal(OkxCollectionStatus.Failed, invalid.CollectionStatus); Assert.Empty(f.Sent); f.Bootstrap(); var future = await new OkxSnapshotReader(c).CaptureAsync(new(f.Clock.Now.AddMinutes(-1), f.Clock.Now.AddSeconds(1), Array.Empty<OkxOrderRef>()), default); Assert.Contains("InvalidInterval", future.Reasons); Assert.Equal(2, f.Sent.Count); }

    [Fact]
    public async Task TimedOutPartialSnapshotStillHasUniqueEconomicIdentities()
    {
        using var f = new OkxHttpFixture(); f.CaptureScript(fill: string.Join(',', OkxHttpFixture.Fill(), OkxHttpFixture.Fill())); var req = f.SnapshotRequest(); f.Inspect = r => { if (r.RequestUri!.AbsolutePath == "/api/v5/account/bills") f.Clock.Now += TimeSpan.FromSeconds(121); }; await using var c = f.Client(); var s = await new OkxSnapshotReader(c).CaptureAsync(req, default); Assert.Equal(OkxCollectionStatus.Incomplete, s.CollectionStatus); Assert.Single(s.Fills); Assert.Contains("CaptureTimeout", s.Reasons);
    }

}
