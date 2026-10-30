using System.Net;
using Apex.Adapters.Okx;
using Xunit;

namespace Apex.Adapters.Okx.Tests;

// Synthetic HTTP only: these regressions exercise the real transport/parser/capture.
public class OkxFixRound1Tests
{
    [Fact]
    public async Task I1_DistinctBindingsSameExpectedUidRestrictBootstrapStarts()
    {
        using var f = new OkxHttpFixture();
        var uid = "quota-" + Guid.NewGuid().ToString("N");
        var clients = new List<OkxEeaDemoReadClient>();
        try
        {
            for (int i = 0; i < 6; i++)
            {
                var secrets = new PublicTestSecrets();
                f.Bootstrap(OkxHttpFixture.Config().Replace(OkxHttpFixture.Uid, uid));
                var client = OkxEeaDemoReadClient.ForTests(f, f.Clock, secrets, new(secrets.Binding, uid), f.Clock.Delay, true);
                clients.Add(client);
                Assert.Equal(OkxReadStatus.Observed, (await client.GetConfigAsync(default)).Status);
            }
            var starts = f.Sent.Where(s => s.Private).ToArray();
            Assert.Equal(6, starts.Length);
            Assert.All(starts.Zip(starts.Skip(1)), pair => Assert.True(pair.Second.At - pair.First.At >= TimeSpan.FromMilliseconds(600)));
            f.Done();
        }
        finally { foreach (var client in clients) await client.DisposeAsync(); }
    }

    [Fact]
    public async Task I1_DistinctBindingBootstrapWaitsForInFlightValidatedUidRead()
    {
        using var f = new OkxHttpFixture();
        var uid = "inflight-" + Guid.NewGuid().ToString("N");
        var secretsA = new PublicTestSecrets(); var secretsB = new PublicTestSecrets();
        f.Bootstrap(OkxHttpFixture.Config().Replace(OkxHttpFixture.Uid, uid));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Steps.Enqueue(("/api/v5/account/balance", async (_, ct) =>
        {
            entered.SetResult(); await release.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(OkxHttpFixture.Envelope(OkxHttpFixture.Balance())) };
        }));
        f.Bootstrap(OkxHttpFixture.Config().Replace(OkxHttpFixture.Uid, uid));
        await using var a = OkxEeaDemoReadClient.ForTests(f, f.Clock, secretsA, new(secretsA.Binding, uid), f.Clock.Delay, true);
        await using var b = OkxEeaDemoReadClient.ForTests(f, f.Clock, secretsB, new(secretsB.Binding, uid), f.Clock.Delay, true);
        Assert.Equal(OkxReadStatus.Observed, (await a.GetConfigAsync(default)).Status);
        var read = a.GetBalancesAsync(default); await entered.Task;
        var bootstrap = b.GetConfigAsync(default);
        try
        {
            // The scripted time response completes inline, so bootstrap has reached its quota await.
            Assert.False(bootstrap.IsCompleted);
            Assert.Equal(2, f.Sent.Count(s => s.Private));
            Assert.Null(b.Uid); // ExpectedUid is only a restriction, not a validated identity.
        }
        finally { release.TrySetResult(); await read; await bootstrap; }
        f.Done();
    }

    [Fact]
    public async Task I1_ExpectedUidDoesNotAuthorizeMismatchedConfig()
    {
        using var f = new OkxHttpFixture(); f.Bootstrap();
        await using var c = OkxEeaDemoReadClient.ForTests(f, f.Clock, f.Secrets, new(f.Secrets.Binding, "different-uid"), f.Clock.Delay, true);
        var r = await c.GetBalancesAsync(default);
        Assert.Equal("IdentityConflict", r.ErrorCode); Assert.Empty(r.Rows); Assert.Null(c.Uid);
        Assert.Equal(2, f.Sent.Count); f.Done();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task I2_PendingOverlapDedupesIdenticalButPreservesConflictingEvidence(bool changed)
    {
        using var f = new OkxHttpFixture(); f.CaptureScript();
        Rewrite(f, path => path == "/api/v5/trade/orders-pending?limit=100", path =>
        {
            f.Add(path, OkxHttpFixture.Envelope(OkxHttpFixture.Order("A"), OkxHttpFixture.Order("B")));
            f.Add("/api/v5/trade/orders-pending?after=B&limit=100", OkxHttpFixture.Envelope(OkxHttpFixture.Order("A", changed ? "partially_filled" : "live"), OkxHttpFixture.Order("C")));
            f.Add("/api/v5/trade/orders-pending?after=C&limit=100", OkxHttpFixture.Envelope());
        });
        var s = await Capture(f);
        foreach (var pass in new[] { s.PendingBefore, s.PendingAfter })
        {
            Assert.Equal(changed ? 4 : 3, pass.Length);
            Assert.Equal(changed ? 2 : 1, pass.Count(o => o.OrdId == "A"));
            if (changed) Assert.Equal(new[] { "live", "partially_filled" }, pass.Where(o => o.OrdId == "A").Select(o => o.State));
        }
        Assert.Equal(changed ? OkxCollectionStatus.Incomplete : OkxCollectionStatus.Collected, s.CollectionStatus);
        Assert.Equal(changed ? OkxCoverage.Conflict : OkxCoverage.Unknown, s.Coverage);
        if (changed)
        {
            Assert.Contains("PendingOrderIdentityConflict", s.Reasons); Assert.Contains("SnapshotMoved", s.Reasons);
            Assert.Equal(2, s.Trace.Count(t => t.ErrorCode == "PendingOrderIdentityConflict"));
        }
        else Assert.DoesNotContain("PendingOrderIdentityConflict", s.Reasons);
    }

    [Theory]
    [InlineData("EUR")]
    [InlineData("")]
    public async Task I3_ActualQuoteMustNotInventCatalogCurrencyOrConversion(string actual)
    {
        using var f = new OkxHttpFixture();
        var fill = OkxHttpFixture.Fill(fee: "-0.1", ccy: "EUR").Replace("\"tradeQuoteCcy\":\"USD\"", "\"tradeQuoteCcy\":\"" + actual + "\"");
        f.CaptureScript(fill: fill, bill: OkxHttpFixture.Bill(change: "-10.1").Replace("\"ccy\":\"USD\"", "\"ccy\":\"EUR\""));
        Rewrite(f, p => p == "/api/v5/account/instruments?instType=SPOT", p => f.Add(p, OkxHttpFixture.Envelope(OkxHttpFixture.Instrument.Replace("[\"USD\"]", "[\"USD\",\"EUR\"]"))));
        // All known actual currencies are present, catalog USD is deliberately absent.
        var balances = "{\"uTime\":\"1793361600000\",\"details\":[{\"ccy\":\"ABC\",\"cashBal\":\"1\",\"liab\":\"0\"},{\"ccy\":\"EUR\",\"cashBal\":\"90\",\"liab\":\"0\"}]}";
        Rewrite(f, p => p == "/api/v5/account/balance", p => f.Add(p, OkxHttpFixture.Envelope(balances)));
        var s = await Capture(f); var d = Assert.Single(s.FillCashflows);
        Assert.Equal(actual.Length == 0 ? null : actual, d.QuoteCurrency); Assert.Null(d.QuoteChange);
        Assert.Equal(1m, d.BaseChange); Assert.Equal(-0.1m, d.SignedFee); Assert.True(d.BillLinked);
        Assert.Contains("AmbiguousTradeQuoteSemantics", s.Reasons);
        Assert.DoesNotContain("ThirdCurrencyFee", s.Reasons);
        if (actual.Length != 0) Assert.DoesNotContain("MissingBalanceCurrency", s.Reasons);
    }

    [Theory]
    [InlineData("90", "90")]
    [InlineData("100", "90")]
    public async Task I4_HistoricalBillChainIsNotCurrentCaptureReconciliation(string first, string last)
    {
        using var f = new OkxHttpFixture(); f.CaptureScript(); int n = 0;
        Rewrite(f, p => p == "/api/v5/account/balance", p => f.Add(p, OkxHttpFixture.Envelope(OkxHttpFixture.Balance(n++ == 0 ? first : last))));
        var s = await Capture(f); var d = Assert.Single(s.CurrencyDiagnostics);
        Assert.Equal(-10m, d.BillChange); Assert.True(d.ChainConsistent);
        Assert.DoesNotContain("CashBillMismatch", s.Reasons);
        Assert.Contains("CashBillReconciliationUnavailable", s.Reasons);
        Assert.Equal(first == last ? 0m : -10m, d.CashDifference);
    }

    [Theory]
    [InlineData("0.0000000000000000000000000001", "0.1", "0", "USD")]
    [InlineData("10", "1", "0.0000000000000000000000000001", "USD")]
    [InlineData("10", "10", "0.0000000000000000000000000001", "ABC")]
    public async Task I5_InexactDerivedFillEconomicsAreUnknown(string price, string size, string fee, string ccy)
    {
        using var f = new OkxHttpFixture();
        f.CaptureScript(fill: OkxHttpFixture.Fill(price: price, fee: fee, ccy: ccy).Replace("\"fillSz\":\"1\"", "\"fillSz\":\"" + size + "\""));
        var s = await Capture(f); var d = Assert.Single(s.FillCashflows);
        if (ccy == "ABC") Assert.Null(d.BaseChange); else Assert.Null(d.QuoteChange);
        Assert.Contains("DiagnosticArithmeticPrecisionLoss", s.Reasons); Assert.True(d.BillLinked);
    }

    [Fact]
    public async Task I5_InexactCashSubtractionAndBillAdditionAreUnknown()
    {
        using var f = new OkxHttpFixture();
        f.CaptureScript(bill: string.Join(',', OkxHttpFixture.Bill("bill-A", "79228162514264337593543950335", "79228162514264337593543950335").Replace("1793361590000", "1793361589000"), OkxHttpFixture.Bill(change: "0.1", balance: "79228162514264337593543950335")));
        int n = 0;
        Rewrite(f, p => p == "/api/v5/account/balance", p => f.Add(p, OkxHttpFixture.Envelope(OkxHttpFixture.Balance(n++ == 0 ? "0.1" : "79228162514264337593543950335"))));
        var s = await Capture(f); var d = Assert.Single(s.CurrencyDiagnostics);
        Assert.Null(d.BillChange); Assert.Null(d.CashDifference); Assert.Null(d.ChainConsistent);
        Assert.Contains("DiagnosticArithmeticPrecisionLoss", s.Reasons); Assert.DoesNotContain("BillChainMismatch", s.Reasons);
    }

    [Theory]
    [InlineData("0.0000000000000000000000000001", "10", "0", "-0.000000000000000000000000001")]
    [InlineData("10", "1", "0.00000000000000000000000001", "-9.99999999999999999999999999")]
    public async Task I5_RepresentableDerivedEconomicsRemainExact(string price, string size, string fee, string expected)
    {
        using var f = new OkxHttpFixture();
        f.CaptureScript(fill: OkxHttpFixture.Fill(price: price, fee: fee, ccy: "USD").Replace("\"fillSz\":\"1\"", "\"fillSz\":\"" + size + "\""));
        var s = await Capture(f); var d = Assert.Single(s.FillCashflows);
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), d.QuoteChange);
        Assert.DoesNotContain("DiagnosticArithmeticPrecisionLoss", s.Reasons);
    }

    [Theory]
    [InlineData("{\"code\":\"0\",\"data\":[{\"ts\":\"\\uD800\"}]}")]
    [InlineData("{\"code\":\"0\",\"data\":[{\"ts\":\"1793361600000\",\"future\":\"\\uDC00\"}]}")]
    [InlineData("{\"code\":\"0\",\"data\":[{\"ts\":\"1793361600000\",\"\\uD800\":\"x\"}]}")]
    public async Task I6_InvalidEscapedUnicodeIsFailedWithoutRetry(string body)
    {
        using var f = new OkxHttpFixture(); f.Add("/api/v5/public/time", body); await using var c = f.Client(false);
        var r = await c.GetTimeAsync(default);
        Assert.Equal(OkxReadStatus.Failed, r.Status); Assert.Equal("InvalidPayload", r.ErrorCode); Assert.Empty(r.Rows);
        Assert.DoesNotContain("UTF-16", r.ToString()); Assert.Single(f.Sent); f.Done();
    }

    [Fact]
    public async Task I6_InvalidUnicodePrivateCaptureIsIncomplete()
    {
        using var f = new OkxHttpFixture(); f.CaptureScript();
        Rewrite(f, p => p == "/api/v5/account/balance", p => f.Add(p, OkxHttpFixture.Envelope(OkxHttpFixture.Balance().Replace("1793361600000", "\\uD800"))));
        var s = await Capture(f);
        Assert.Equal(OkxCollectionStatus.Incomplete, s.CollectionStatus); Assert.Contains("InvalidPayload", s.Reasons);
        Assert.Empty(s.BalanceBefore!.Rows); Assert.Empty(s.BalanceAfter!.Rows);
    }

    [Fact]
    public async Task I6_UnrelatedHandlerProgrammerExceptionStillEscapes()
    {
        using var f = new OkxHttpFixture(); f.AddError("/api/v5/public/time", new InvalidOperationException("programmer-error"));
        await using var c = f.Client(false);
        Assert.Equal("programmer-error", (await Assert.ThrowsAsync<InvalidOperationException>(() => c.GetTimeAsync(default))).Message);
        Assert.Single(f.Sent); f.Done();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public async Task M1_PendingEmptyPageIsObservedAtEitherLimit(int limit)
    {
        using var f = new OkxHttpFixture(); f.Bootstrap(); f.Add("/api/v5/trade/orders-pending?limit=" + limit, OkxHttpFixture.Envelope());
        await using var c = f.Client(); var r = await c.GetPendingOrdersAsync(new(Limit: limit), default);
        Assert.Equal(OkxReadStatus.Observed, r.Status); Assert.Null(r.ErrorCode); Assert.Empty(r.Rows); f.Done();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task M1_OrderDetailRequiresExactlyOneRow(int count)
    {
        using var f = new OkxHttpFixture(); f.Bootstrap();
        f.Add("/api/v5/trade/order?instId=123-Ab&ordId=known", OkxHttpFixture.Envelope(Enumerable.Repeat(OkxHttpFixture.Order("known"), count).ToArray()));
        await using var c = f.Client(); var r = await c.GetOrderAsync(new("123-Ab", "known"), default);
        Assert.Equal(count == 1 ? OkxReadStatus.Observed : OkxReadStatus.Failed, r.Status);
        Assert.Equal(count == 1 ? 1 : 0, r.Rows.Length); f.Done();
    }

    private static void Rewrite(OkxHttpFixture f, Func<string, bool> match, Action<string> replacement)
    {
        var steps = f.Steps.ToArray(); f.Steps.Clear();
        foreach (var step in steps) if (match(step.Path)) replacement(step.Path); else f.Steps.Enqueue(step);
    }

    private static async Task<OkxReadSnapshot> Capture(OkxHttpFixture f)
    {
        var request = f.SnapshotRequest(); await using var c = f.Client();
        var s = await new OkxSnapshotReader(c).CaptureAsync(request, default); f.Done(); return s;
    }
}
