using System.Collections.Immutable;
using System.Numerics;
namespace Apex.Adapters.Okx;

/// <summary>REST recapture is a detached, partial observation; never a recovery checkpoint or ledger.</summary>
public sealed class OkxSnapshotReader
{
    private readonly OkxEeaDemoReadClient client;
    public OkxSnapshotReader(OkxEeaDemoReadClient client) { ArgumentNullException.ThrowIfNull(client); this.client = client; }
    public async Task<OkxReadSnapshot> CaptureAsync(OkxSnapshotRequest request, CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        var start = client.Clock.GetUtcNow(); var generation = Guid.NewGuid();
        var reasons = new HashSet<string>(StringComparer.Ordinal) { "UnprovenEventContinuity", "DemoCredentialProvenanceUnattested", "EgressIpMembershipUnattested", "AlgoAndOtherAssetExposureUnknown" };
        var trace = new List<OkxPageTrace>(); var fills = new List<OkxFillDto>(); var bills = new List<OkxBillDto>(); var details = new List<OkxOrderDto>();
        ImmutableArray<OkxOrderDto> pendingA = [], pendingB = []; ImmutableArray<OkxInstrumentDto> instruments = [];
        ImmutableArray<OkxCurrencyDiagnostic> diagnostics = []; ImmutableArray<OkxFillCashflowDiagnostic> cashflows = [];
        OkxRead<OkxAccountConfigDto>? configA = null, configB = null; OkxRead<OkxBalanceDto>? balanceA = null, balanceB = null;
        var status = OkxCollectionStatus.Failed; var coverage = OkxCoverage.Unknown;
        var from = request?.FromUtc ?? default; var to = request?.ToUtc ?? default;
        OkxReadSnapshot Result()
        {
            // Every exit, including a timed-out partial capture, has unique economic identities.
            bool partialConflict = false;
            fills = Unique(fills, f => (f.InstId, f.TradeId), SameFill, ref partialConflict).OrderBy(f => f.FillTime.Utc).ThenBy(f => f.InstId, StringComparer.Ordinal).ThenBy(f => f.TradeId, StringComparer.Ordinal).ToList();
            bills = Unique(bills, b => b.BillId, SameBill, ref partialConflict).OrderBy(b => b.Ts.Utc).ThenBy(b => b.BillId, StringComparer.Ordinal).ToList();
            if (partialConflict) { coverage = OkxCoverage.Conflict; status = OkxCollectionStatus.Incomplete; reasons.Add("EconomicIdentityConflict"); }
            return new() { Generation = generation, CredentialBindingId = client.BindingId, Uid = configA?.Rows.FirstOrDefault()?.Uid, StartUtc = start, EndUtc = client.Clock.GetUtcNow(), FromUtc = from, ToUtc = to, ConfigBefore = configA, ConfigAfter = configB, BalanceBefore = balanceA, BalanceAfter = balanceB, PendingBefore = pendingA, PendingAfter = pendingB, Instruments = instruments, OrderDetails = details.ToImmutableArray(), Fills = fills.ToImmutableArray(), Bills = bills.ToImmutableArray(), Trace = trace.ToImmutableArray(), CurrencyDiagnostics = diagnostics, FillCashflows = cashflows, Reasons = reasons.Order(StringComparer.Ordinal).ToImmutableArray(), CollectionStatus = status, Coverage = coverage };
        }
        if (request is null || request.FromUtc.Offset != TimeSpan.Zero || request.ToUtc.Offset != TimeSpan.Zero || from >= to || request.UnresolvedOrders is null || request.UnresolvedOrders.Count > 20)
        { reasons.Add("InvalidRequest"); return Result(); }
        // Detach the caller's mutable request collection before the first await.
        var unresolved = request.UnresolvedOrders.ToArray();
        if (unresolved.Any(o => o is null || string.IsNullOrEmpty(o.InstId) || string.IsNullOrEmpty(o.OrdId)) || unresolved.Distinct().Count() != unresolved.Length)
        { reasons.Add("InvalidRequest"); return Result(); }
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(120), client.Clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, budget.Token);
        var ct = linked.Token; bool held = false;
        void CheckBudget() { ct.ThrowIfCancellationRequested(); if (client.Clock.GetUtcNow() - start >= TimeSpan.FromSeconds(120)) throw new OperationCanceledException(ct); }
        void ReadFailure(string? error)
        {
            reasons.Add(error ?? "ReadFailed"); status = OkxCollectionStatus.Incomplete;
            if (error == "PendingOrderIdentityConflict") { coverage = OkxCoverage.Conflict; reasons.Add("SnapshotMoved"); }
            else if (coverage != OkxCoverage.Conflict) coverage = OkxCoverage.Gap;
        }
        async Task<PageCollection<T>> Pages<T>(string endpoint, OkxPageRequest query, Func<OkxPageRequest, CancellationToken, Task<OkxRead<T>>> read, Func<T, string> cursor, Func<T, DateTimeOffset?> ts, Func<T, DateTimeOffset?>? ft = null)
        {
            CheckBudget();
            var p = await OkxReadPager.Collect(endpoint, query, async (q, t) => { CheckBudget(); return await read(q, t).ConfigureAwait(false); }, cursor, ts, ft, ct, endpoint == "/api/v5/trade/orders-pending" ? cursor : null).ConfigureAwait(false);
            trace.AddRange(p.Trace); if (p.Error != null) ReadFailure(p.Error); return p;
        }
        try
        {
            await client.Captures.WaitAsync(ct).ConfigureAwait(false); held = true;
            CheckBudget(); await client.BeginCapture(ct).ConfigureAwait(false);
            configA = await client.GetConfigAsync(ct).ConfigureAwait(false); CheckBudget();
            if (configA.Status != OkxReadStatus.Observed) { reasons.Add(configA.ErrorCode ?? "PreflightFailed"); return Result(); }
            if (!client.ServerUtc.HasValue || to > client.ServerUtc.Value) { reasons.Add("InvalidInterval"); return Result(); }
            status = OkxCollectionStatus.Collected;
            if (from < client.ServerUtc.Value.AddMonths(-3)) { coverage = OkxCoverage.Gap; reasons.Add("RequestedBeyondRetention"); }
            var catalog = await client.GetAccountSpotInstrumentsAsync(ct).ConfigureAwait(false); CheckBudget(); instruments = catalog.Rows;
            if (catalog.Status == OkxReadStatus.Failed) ReadFailure(catalog.ErrorCode);
            balanceA = await client.GetBalancesAsync(ct).ConfigureAwait(false); CheckBudget(); if (balanceA.Status == OkxReadStatus.Failed) ReadFailure(balanceA.ErrorCode);
            pendingA = (await Pages("/api/v5/trade/orders-pending", new(), client.GetPendingOrdersAsync, o => o.OrdId, o => o.UTime.Utc).ConfigureAwait(false)).Rows.ToImmutableArray();
            foreach (var order in unresolved)
            {
                CheckBudget(); var r = await client.GetOrderAsync(order, ct).ConfigureAwait(false);
                if (r.Status == OkxReadStatus.Failed) { ReadFailure(r.ErrorCode); reasons.Add("UnresolvedOrderUnknown"); }
                else if (r.Rows[0].OrdId != order.OrdId || r.Rows[0].InstId != order.InstId) { ReadFailure("OrderIdentityConflict"); coverage = OkxCoverage.Conflict; }
                else details.AddRange(r.Rows);
            }
            // Inclusive begin/end are deliberately shared by recent/archive and adjacent captures.
            var window = new OkxPageRequest(BeginUtc: from, EndUtc: to);
            var recentFills = await Pages("/api/v5/trade/fills", window, (q, t) => client.GetFillsAsync(OkxHistoryWindow.Recent, q, t), f => f.BillId, f => f.Ts.Utc, f => f.FillTime.Utc).ConfigureAwait(false);
            fills.AddRange(recentFills.Rows);
            if (from < client.ServerUtc.Value.AddDays(-3))
            {
                var archive = await Pages("/api/v5/trade/fills-history", window, (q, t) => client.GetFillsAsync(OkxHistoryWindow.Archive, q, t), f => f.BillId, f => f.Ts.Utc, f => f.FillTime.Utc).ConfigureAwait(false);
                fills.AddRange(archive.Rows); if (archive.Error != null) reasons.Add("ArchiveUnavailable");
            }
            var recentBills = await Pages("/api/v5/account/bills", window, (q, t) => client.GetBillsAsync(OkxHistoryWindow.Recent, q, t), b => b.BillId, b => b.Ts.Utc).ConfigureAwait(false); bills.AddRange(recentBills.Rows);
            if (from < client.ServerUtc.Value.AddDays(-7))
            {
                var archive = await Pages("/api/v5/account/bills-archive", window, (q, t) => client.GetBillsAsync(OkxHistoryWindow.Archive, q, t), b => b.BillId, b => b.Ts.Utc).ConfigureAwait(false);
                bills.AddRange(archive.Rows); if (archive.Error != null) reasons.Add("ArchiveUnavailable");
            }
            CheckBudget(); balanceB = await client.GetBalancesAsync(ct).ConfigureAwait(false); if (balanceB.Status == OkxReadStatus.Failed) ReadFailure(balanceB.ErrorCode);
            pendingB = (await Pages("/api/v5/trade/orders-pending", new(), client.GetPendingOrdersAsync, o => o.OrdId, o => o.UTime.Utc).ConfigureAwait(false)).Rows.ToImmutableArray();
            CheckBudget(); configB = await client.GetConfigAsync(ct).ConfigureAwait(false); CheckBudget();
            if (configB.Status == OkxReadStatus.Failed) { ReadFailure(configB.ErrorCode); if (configB.ErrorCode is "IdentityConflict" or "AccountConfigChanged") coverage = OkxCoverage.Conflict; }
            bool conflict = false;
            fills = Unique(fills, f => (f.InstId, f.TradeId), SameFill, ref conflict).OrderBy(f => f.FillTime.Utc).ThenBy(f => f.InstId, StringComparer.Ordinal).ThenBy(f => f.TradeId, StringComparer.Ordinal).ToList();
            bills = Unique(bills, b => b.BillId, SameBill, ref conflict).OrderBy(b => b.Ts.Utc).ThenBy(b => b.BillId, StringComparer.Ordinal).ToList();
            if (conflict) { coverage = OkxCoverage.Conflict; status = OkxCollectionStatus.Incomplete; reasons.Add("EconomicIdentityConflict"); }
            if (balanceA.Status == OkxReadStatus.Observed && balanceB.Status == OkxReadStatus.Observed && !SameBalance(balanceA.Rows[0], balanceB.Rows[0]) || !SamePending(pendingA, pendingB)) { status = OkxCollectionStatus.Incomplete; reasons.Add("SnapshotMoved"); }
            if (pendingA.Concat(pendingB).Concat(details).Any(o => o.InstType != "SPOT" || o.TdMode != "cash")) reasons.Add("ForeignPendingExposure");
            if (pendingA.Concat(pendingB).Concat(details).Any(o => o.Side is not ("buy" or "sell") || o.State is not ("live" or "partially_filled" or "filled" or "canceled"))) reasons.Add("UnknownOrderSemantics");
            if (instruments.Any(i => i.InstType != "SPOT" || i.State != "live")) reasons.Add("UnsafeInstrumentStatus");
            if (fills.Any(f => f.FillTime.Utc < from || f.FillTime.Utc >= to)) reasons.Add("ExecutionTimeOutsideRequestedInterval");
            if (fills.Any(f => f.InstType != "SPOT" || f.Side is not ("buy" or "sell"))) reasons.Add("UnknownFillSemantics");
            if (bills.Any(b => b.Type != "2")) reasons.Add("UnknownBillType");
            var balanceCurrencies = (balanceA.Rows.FirstOrDefault()?.Details ?? []).Select(d => d.Ccy).Intersect((balanceB.Rows.FirstOrDefault()?.Details ?? []).Select(d => d.Ccy), StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
            foreach (var fill in fills)
            {
                var instrument = instruments.FirstOrDefault(i => i.InstId == fill.InstId);
                if (!balanceCurrencies.Contains(fill.FeeCcy) || instrument != null && !balanceCurrencies.Contains(instrument.BaseCcy) || !string.IsNullOrEmpty(fill.TradeQuoteCcy) && !balanceCurrencies.Contains(fill.TradeQuoteCcy)) reasons.Add("MissingBalanceCurrency");
            }
            diagnostics = DiagnoseCash(balanceA, balanceB, bills, reasons);
            cashflows = DiagnoseFills(fills, bills, instruments, reasons);
            CheckBudget(); return Result();
        }
        catch (OperationCanceledException)
        {
            caller.ThrowIfCancellationRequested(); reasons.Add("CaptureTimeout"); status = configA?.Status == OkxReadStatus.Observed ? OkxCollectionStatus.Incomplete : OkxCollectionStatus.Failed;
            if (coverage != OkxCoverage.Conflict) coverage = OkxCoverage.Gap; return Result();
        }
        finally { if (held) client.Captures.Release(); }
    }
    private static List<T> Unique<T, K>(List<T> source, Func<T, K> key, Func<T, T, bool> same, ref bool conflict) where K : notnull
    {
        var index = new Dictionary<K, T>(); foreach (var item in source) { var k = key(item); if (index.TryGetValue(k, out var old)) { if (!same(old, item)) conflict = true; } else index.Add(k, item); }
        return index.Values.ToList();
    }
    private static bool SameFill(OkxFillDto a, OkxFillDto b) => a.InstType == b.InstType && a.InstId == b.InstId && a.TradeId == b.TradeId && a.OrdId == b.OrdId && a.ClOrdId == b.ClOrdId && a.BillId == b.BillId && a.Side == b.Side && a.FillPx.Value == b.FillPx.Value && a.FillSz.Value == b.FillSz.Value && a.Fee.Value == b.Fee.Value && a.FeeCcy == b.FeeCcy && a.FeeRate.Value == b.FeeRate.Value && a.ExecType == b.ExecType && a.FillTime.Utc == b.FillTime.Utc && a.Ts.Utc == b.Ts.Utc && a.TradeQuoteCcy == b.TradeQuoteCcy;
    private static bool SameBill(OkxBillDto a, OkxBillDto b) => a.BillId == b.BillId && a.Ccy == b.Ccy && a.Bal.Value == b.Bal.Value && a.BalChg.Value == b.BalChg.Value && a.Fee.Value == b.Fee.Value && a.OrdId == b.OrdId && a.TradeId == b.TradeId && a.InstId == b.InstId && a.Type == b.Type && a.SubType == b.SubType && a.Ts.Utc == b.Ts.Utc;
    private static bool SameBalance(OkxBalanceDto a, OkxBalanceDto b)
    {
        var x = a.Details.OrderBy(d => d.Ccy, StringComparer.Ordinal).Select(d => (d.Ccy, d.CashBal.Value, d.AvailBal.Value, d.FrozenBal.Value, d.OrdFrozen.Value, d.Eq.Value, d.Liab.Value));
        var y = b.Details.OrderBy(d => d.Ccy, StringComparer.Ordinal).Select(d => (d.Ccy, d.CashBal.Value, d.AvailBal.Value, d.FrozenBal.Value, d.OrdFrozen.Value, d.Eq.Value, d.Liab.Value)); return x.SequenceEqual(y);
    }
    private static bool SamePending(ImmutableArray<OkxOrderDto> a, ImmutableArray<OkxOrderDto> b) => a.OrderBy(o => o.OrdId, StringComparer.Ordinal).SequenceEqual(b.OrderBy(o => o.OrdId, StringComparer.Ordinal));
    private static ImmutableArray<OkxCurrencyDiagnostic> DiagnoseCash(OkxRead<OkxBalanceDto>? a, OkxRead<OkxBalanceDto>? b, List<OkxBillDto> bills, HashSet<string> reasons)
    {
        var before = a?.Rows.FirstOrDefault()?.Details ?? []; var after = b?.Rows.FirstOrDefault()?.Details ?? [];
        if (before.Concat(after).Any(d => d.Liab.Value is null or not 0m)) reasons.Add("LiabilityOrBorrowUnknown");
        var output = ImmutableArray.CreateBuilder<OkxCurrencyDiagnostic>();
        foreach (var ccy in before.Select(d => d.Ccy).Concat(after.Select(d => d.Ccy)).Concat(bills.Select(d => d.Ccy)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var first = before.FirstOrDefault(d => d.Ccy == ccy)?.CashBal.Value; var last = after.FirstOrDefault(d => d.Ccy == ccy)?.CashBal.Value;
            if (first is null || last is null) reasons.Add("MissingBalanceCurrency");
            var rows = bills.Where(d => d.Ccy == ccy).ToArray(); decimal? sum = null; bool? chain = null;
            if (rows.Length == 0) reasons.Add("MissingBillsForCurrency");
            else
            {
                sum = 0m; chain = true; foreach (var row in rows) sum = Exact(sum, row.BalChg.Value, '+', reasons);
                for (int i = 1; i < rows.Length; i++)
                {
                    if (rows[i - 1].Ts.Utc == rows[i].Ts.Utc) { chain = null; reasons.Add("AmbiguousBillChronology"); }
                    else
                    {
                        var expected = Exact(rows[i - 1].Bal.Value, rows[i].BalChg.Value, '+', reasons);
                        if (!expected.HasValue || !rows[i].Bal.Value.HasValue) chain = null;
                        else if (rows[i].Bal.Value != expected) { chain = false; reasons.Add("BillChainMismatch"); }
                    }
                }
            }
            // History ends no later than preflight; these balances are captured afterward.
            // Neither request/receive time nor uTime establishes shared event-sequence anchors.
            // Keep the two deltas independent, even when they happen to be equal.
            decimal? difference = Exact(last, first, '-', reasons);
            reasons.Add("CashBillReconciliationUnavailable");
            output.Add(new(ccy, first, last, sum, difference, chain));
        }
        return output.ToImmutable();
    }
    private static ImmutableArray<OkxFillCashflowDiagnostic> DiagnoseFills(List<OkxFillDto> fills, List<OkxBillDto> bills, ImmutableArray<OkxInstrumentDto> catalog, HashSet<string> reasons)
    {
        var output = ImmutableArray.CreateBuilder<OkxFillCashflowDiagnostic>();
        foreach (var fill in fills)
        {
            var instrument = catalog.FirstOrDefault(i => i.InstId == fill.InstId);
            if (instrument is null) { reasons.Add("MissingInstrumentForFill"); continue; }
            bool linked = bills.Any(b => b.BillId == fill.BillId && b.TradeId == fill.TradeId && b.OrdId == fill.OrdId && b.InstId == fill.InstId);
            if (!linked) reasons.Add("MissingFillBillLink");
            decimal? baseChange = null, quoteChange = null;
            string? actualQuote = string.IsNullOrEmpty(fill.TradeQuoteCcy) ? null : fill.TradeQuoteCcy;
            // Catalog quote metadata is not a conversion rate or settlement contract.
            // Only the same, explicitly returned trading quote has established price units here.
            bool knownQuoteUnits = actualQuote == instrument.QuoteCcy && instrument.TradeQuoteCcyList.Contains(actualQuote, StringComparer.Ordinal);
            if (!knownQuoteUnits) reasons.Add("AmbiguousTradeQuoteSemantics");
            if (fill.Side is "buy" or "sell")
            {
                baseChange = fill.Side == "buy" ? fill.FillSz.Value : -fill.FillSz.Value;
                if (knownQuoteUnits)
                {
                    quoteChange = Exact(fill.FillPx.Value, fill.FillSz.Value, '*', reasons);
                    if (fill.Side == "buy") quoteChange = -quoteChange; // Negation is exact for decimal's symmetric range.
                }
                if (fill.FeeCcy == instrument.BaseCcy) baseChange = Exact(baseChange, fill.Fee.Value, '+', reasons);
                else if (fill.FeeCcy == actualQuote) quoteChange = Exact(quoteChange, fill.Fee.Value, '+', reasons);
                else reasons.Add(actualQuote == null ? "FeeCurrencyRelationshipUnknown" : "ThirdCurrencyFee");
            }
            // A bill's fee is NOT added: it is a separate observation of the same economics.
            output.Add(new(fill.InstId, fill.TradeId, instrument.BaseCcy, actualQuote, baseChange, quoteChange, fill.Fee.Value, fill.FeeCcy, linked));
        }
        foreach (var bill in bills.Where(b => b.Type == "2")) if (!fills.Any(f => f.BillId == bill.BillId && f.TradeId == bill.TradeId && f.OrdId == bill.OrdId && f.InstId == bill.InstId)) reasons.Add("MissingBillFillLink");
        return output.ToImmutable();
    }

    // checked(decimal) detects overflow, but still rounds/underflows. Work in exact
    // coefficient/scale integers and construct a decimal only if no information is lost.
    private static decimal? Exact(decimal? left, decimal? right, char operation, HashSet<string> reasons)
    {
        if (!left.HasValue || !right.HasValue) return null;
        static (BigInteger N, int Scale) Parts(decimal value)
        {
            var bits = decimal.GetBits(value);
            var n = (BigInteger)(uint)bits[0] | ((BigInteger)(uint)bits[1] << 32) | ((BigInteger)(uint)bits[2] << 64);
            return (bits[3] < 0 ? -n : n, (bits[3] >> 16) & 255);
        }
        var a = Parts(left.Value); var b = Parts(right.Value);
        int scale = operation == '*' ? a.Scale + b.Scale : Math.Max(a.Scale, b.Scale);
        BigInteger n;
        if (operation == '*') n = a.N * b.N;
        else
        {
            var x = a.N * BigInteger.Pow(10, scale - a.Scale);
            var y = b.N * BigInteger.Pow(10, scale - b.Scale);
            n = operation switch { '+' => x + y, '-' => x - y, _ => throw new ArgumentOutOfRangeException(nameof(operation)) };
        }
        while (scale > 0 && n % 10 == 0) { n /= 10; scale--; }
        var magnitude = BigInteger.Abs(n); var max = (BigInteger.One << 96) - 1;
        if (scale > 28 || magnitude > max)
        {
            reasons.Add("DiagnosticArithmeticPrecisionLoss");
            if (magnitude > max * BigInteger.Pow(10, scale)) reasons.Add("DiagnosticArithmeticOverflow");
            return null;
        }
        return new decimal(unchecked((int)(uint)(magnitude & uint.MaxValue)), unchecked((int)(uint)((magnitude >> 32) & uint.MaxValue)), unchecked((int)(uint)((magnitude >> 64) & uint.MaxValue)), n.Sign < 0, (byte)scale);
    }
}
