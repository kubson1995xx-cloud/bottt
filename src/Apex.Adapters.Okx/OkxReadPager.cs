namespace Apex.Adapters.Okx;
internal sealed record PageCollection<T>(IReadOnlyList<T> Rows, IReadOnlyList<OkxPageTrace> Trace, string? Error);
internal static class OkxReadPager
{
    internal static async Task<PageCollection<T>> Collect<T>(string endpoint, OkxPageRequest request, Func<OkxPageRequest, CancellationToken, Task<OkxRead<T>>> read, Func<T, string> cursor, Func<T, DateTimeOffset?> ts, Func<T, DateTimeOffset?>? fillTime, CancellationToken ct)
    {
        var rows = new List<T>(); var trace = new List<OkxPageTrace>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        if (request.After != null) seen.Add(request.After);
        var q = request with { Limit = 100 };
        for (int i = 0; i < 20; i++)
        {
            ct.ThrowIfCancellationRequested(); var sentQuery = q; var page = await read(sentQuery, ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested();
            var stamps = page.Rows.Select(ts).Where(v => v.HasValue).Select(v => v!.Value).ToArray();
            var fills = fillTime == null ? [] : page.Rows.Select(fillTime).Where(v => v.HasValue).Select(v => v!.Value).ToArray();
            bool empty = page.Status == OkxReadStatus.Observed && page.Rows.IsEmpty;
            // Boundary is on record ts, NOT economic execution time. Equality deliberately overlaps captures.
            bool boundary = page.Status == OkxReadStatus.Observed && q.BeginUtc.HasValue && stamps.Any(t => t < q.BeginUtc.Value);
            string? error = page.ErrorCode;
            if (page.Status == OkxReadStatus.Observed && !empty && !boundary)
            {
                string next = cursor(page.Rows[^1]);
                if (string.IsNullOrEmpty(next)) error = "MissingCursor";
                else if (!seen.Add(next)) error = "CursorCycle";
                else if (i == 19) error = "PageLimit";
                else q = q with { After = next };
            }
            trace.Add(new(endpoint, sentQuery, i + 1, page.Rows.Length, page.ResponseSha256, stamps.Length == 0 ? null : stamps.Min(), stamps.Length == 0 ? null : stamps.Max(), fills.Length == 0 ? null : fills.Min(), fills.Length == 0 ? null : fills.Max(), empty, boundary, error));
            if (page.Status == OkxReadStatus.Observed) rows.AddRange(page.Rows);
            if (error != null || page.Status == OkxReadStatus.Failed) return new(rows.AsReadOnly(), trace.AsReadOnly(), error ?? "ReadFailed");
            if (empty || boundary) return new(rows.AsReadOnly(), trace.AsReadOnly(), null);
        }
        return new(rows.AsReadOnly(), trace.AsReadOnly(), "PageLimit");
    }
}
