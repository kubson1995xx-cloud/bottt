using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
namespace Apex.Adapters.Okx;

/// <summary>Bounded observations only. Neither this host nor the DEMO header attests account authority.</summary>
public sealed class OkxEeaDemoReadClient : IAsyncDisposable
{
    private const string TimePath = "/api/v5/public/time", ConfigPath = "/api/v5/account/config";
    private static readonly HashSet<string> Allowlist = new(StringComparer.Ordinal) { TimePath, ConfigPath, "/api/v5/public/instruments", "/api/v5/account/balance", "/api/v5/account/instruments", "/api/v5/trade/orders-pending", "/api/v5/trade/order", "/api/v5/trade/fills", "/api/v5/trade/fills-history", "/api/v5/account/bills", "/api/v5/account/bills-archive" };
    // Process-local coordination; entries intentionally outlive clients so disposal cannot reset quota.
    private sealed class Lane { internal readonly SemaphoreSlim Gate = new(1, 1); internal DateTimeOffset Next; }
    private static readonly ConcurrentDictionary<string, Lane> PrivateLanes = new(StringComparer.Ordinal);
    private static readonly Lane PublicLane = new();
    private readonly ConcurrentDictionary<string, Lane> lanes;
    private readonly Lane publicLane;
    private readonly HttpMessageInvoker http;
    private readonly IOkxDemoSecretProvider? secrets;
    private readonly OkxReadScope? scope;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly SemaphoreSlim operations = new(1, 1);
    internal readonly SemaphoreSlim Captures = new(1, 1);
    internal TimeProvider Clock { get; }
    internal DateTimeOffset? ServerUtc { get; private set; }
    internal string? BindingId => lease?.BindingId;
    internal string? Uid => baseline?.Uid;
    private OkxDemoCredentialLease? lease;
    private OkxAccountConfigDto? baseline;
    private Lane? validatedLane;
    private OkxRead<OkxAccountConfigDto>? bootstrapConfig;
    private bool ready, disposed;
    private string? poison;
    private OkxEeaDemoReadClient(HttpMessageHandler handler, TimeProvider clock, IOkxDemoSecretProvider? secrets, OkxReadScope? scope, Func<TimeSpan, CancellationToken, Task>? delay, bool isolated)
    {
        http = new HttpMessageInvoker(handler, true); Clock = clock; this.secrets = secrets; this.scope = scope;
        this.delay = delay ?? ((d, t) => Task.Delay(d, clock, t)); lanes = isolated ? new(StringComparer.Ordinal) : PrivateLanes; publicLane = isolated ? new() : PublicLane;
    }
    private static SocketsHttpHandler ProductionHandler() => new() { AllowAutoRedirect = false, UseCookies = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(10), ActivityHeadersPropagator = null, AutomaticDecompression = DecompressionMethods.None };
    public static OkxEeaDemoReadClient CreatePublic() => new(ProductionHandler(), TimeProvider.System, null, null, null, false);
    public static OkxEeaDemoReadClient CreateDemoRead(IOkxDemoSecretProvider secrets, OkxReadScope scope)
    {
        ArgumentNullException.ThrowIfNull(secrets); ArgumentNullException.ThrowIfNull(scope);
        if (!Token(scope.CredentialBindingId) || !Token(scope.ExpectedUid)) throw new ArgumentException("InvalidReadScope");
        return new(ProductionHandler(), TimeProvider.System, secrets, scope, null, false);
    }
    internal static OkxEeaDemoReadClient ForTests(HttpMessageHandler handler, TimeProvider clock, IOkxDemoSecretProvider? secrets = null, OkxReadScope? scope = null, Func<TimeSpan, CancellationToken, Task>? delay = null, bool sharedQuota = false) => new(handler, clock, secrets, scope, delay, !sharedQuota);
    internal async Task BeginCapture(CancellationToken ct)
    {
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try { ready = false; poison = null; bootstrapConfig = null; ServerUtc = null; }
        finally { operations.Release(); }
    }
    public Task<OkxRead<OkxTimeDto>> GetTimeAsync(CancellationToken ct) => Read<OkxTimeDto>(TimePath, null, false, 1, ct);
    public Task<OkxRead<OkxInstrumentDto>> GetPublicSpotInstrumentsAsync(CancellationToken ct) => Read<OkxInstrumentDto>("/api/v5/public/instruments", new() { { "instType", "SPOT" } }, false, null, ct);
    public Task<OkxRead<OkxAccountConfigDto>> GetConfigAsync(CancellationToken ct) => Read<OkxAccountConfigDto>(ConfigPath, null, true, 1, ct);
    public Task<OkxRead<OkxBalanceDto>> GetBalancesAsync(CancellationToken ct) => Read<OkxBalanceDto>("/api/v5/account/balance", null, true, 1, ct);
    public Task<OkxRead<OkxInstrumentDto>> GetAccountSpotInstrumentsAsync(CancellationToken ct) => Read<OkxInstrumentDto>("/api/v5/account/instruments", new() { { "instType", "SPOT" } }, true, null, ct);
    public Task<OkxRead<OkxOrderDto>> GetPendingOrdersAsync(OkxPageRequest page, CancellationToken ct) => Page<OkxOrderDto>("/api/v5/trade/orders-pending", page, false, true, ct);
    public Task<OkxRead<OkxOrderDto>> GetOrderAsync(OkxOrderRef order, CancellationToken ct)
    {
        if (order is null || !Token(order.InstId) || !Token(order.OrdId)) return Invalid<OkxOrderDto>("/api/v5/trade/order", ct);
        return Read<OkxOrderDto>("/api/v5/trade/order", new() { { "instId", order.InstId }, { "ordId", order.OrdId } }, true, 1, ct);
    }
    public Task<OkxRead<OkxFillDto>> GetFillsAsync(OkxHistoryWindow window, OkxPageRequest page, CancellationToken ct) => Enum.IsDefined(window) ? Page<OkxFillDto>(window == OkxHistoryWindow.Recent ? "/api/v5/trade/fills" : "/api/v5/trade/fills-history", page, true, false, ct) : Invalid<OkxFillDto>("/api/v5/trade/fills", ct);
    public Task<OkxRead<OkxBillDto>> GetBillsAsync(OkxHistoryWindow window, OkxPageRequest page, CancellationToken ct) => Enum.IsDefined(window) ? Page<OkxBillDto>(window == OkxHistoryWindow.Recent ? "/api/v5/account/bills" : "/api/v5/account/bills-archive", page, false, false, ct) : Invalid<OkxBillDto>("/api/v5/account/bills", ct);
    private Task<OkxRead<T>> Page<T>(string endpoint, OkxPageRequest p, bool spot, bool pending, CancellationToken ct)
    {
        if (p is null || p.Limit is < 1 or > 100 || p.After is not null && !Token(p.After) || pending && (p.BeginUtc.HasValue || p.EndUtc.HasValue) || p.BeginUtc?.Offset != null && p.BeginUtc.Value.Offset != TimeSpan.Zero || p.EndUtc?.Offset != null && p.EndUtc.Value.Offset != TimeSpan.Zero || p.BeginUtc.HasValue && p.EndUtc.HasValue && p.BeginUtc >= p.EndUtc) return Invalid<T>(endpoint, ct);
        var q = new Dictionary<string, string>(); if (spot) q.Add("instType", "SPOT"); q.Add("limit", p.Limit.ToString(CultureInfo.InvariantCulture));
        if (p.After != null) q.Add("after", p.After);
        if (p.BeginUtc.HasValue) q.Add("begin", p.BeginUtc.Value.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        if (p.EndUtc.HasValue) q.Add("end", p.EndUtc.Value.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        return Read<T>(endpoint, q, true, p.Limit, ct);
    }
    private static bool Token(string? s) => !string.IsNullOrEmpty(s) && s.Length <= 1024 && !s.Any(c => char.IsControl(c) || char.IsSurrogate(c));
    private Task<OkxRead<T>> Invalid<T>(string endpoint, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(Fail<T>(endpoint, "InvalidRequest", true, Clock.GetUtcNow())); }
    private OkxRead<T> Fail<T>(string endpoint, string code, bool priv, DateTimeOffset start, string? hash = null) => new(OkxReadStatus.Failed, [], code, endpoint, start, Clock.GetUtcNow(), priv, hash);
    private async Task<OkxRead<T>> Read<T>(string endpoint, Dictionary<string, string>? query, bool priv, int? maxRows, CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(disposed, this);
        var start = Clock.GetUtcNow();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30), Clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, timeout.Token);
        var ct = linked.Token;
        bool locked = false;
        try
        {
            await operations.WaitAsync(ct).ConfigureAwait(false); locked = true;
            if (priv)
            {
                if (secrets is null || scope is null) return Fail<T>(endpoint, "CredentialsUnavailable", true, start);
                if (HttpDiagnosticsGuard.Unsafe) return Fail<T>(endpoint, "UnsafeHttpDiagnostics", true, start);
                if (poison != null) return Fail<T>(endpoint, poison, true, start);
                var err = await EnsureReady(start, ct).ConfigureAwait(false); if (err != null) return Fail<T>(endpoint, err, true, start);
                if (endpoint == ConfigPath && bootstrapConfig != null) { var result = bootstrapConfig; bootstrapConfig = null; return (OkxRead<T>)(object)result; }
                bootstrapConfig = null;
            }
            var resultRead = await Send<T>(endpoint, query, priv, maxRows, start, ct).ConfigureAwait(false);
            if (priv && endpoint == ConfigPath && resultRead.Status == OkxReadStatus.Observed)
            {
                var config = (OkxAccountConfigDto)(object)resultRead.Rows[0]!;
                var err = Validate(config); if (err != null) { poison = err; return Fail<T>(endpoint, err, true, start, resultRead.ResponseSha256); }
            }
            if (priv && resultRead.ErrorCode is "50102" or "50101" or "50119" or "50123") { poison = resultRead.ErrorCode; ready = false; }
            return resultRead;
        }
        catch (OperationCanceledException) { caller.ThrowIfCancellationRequested(); return Fail<T>(endpoint, "Timeout", priv, start); }
        finally { if (locked) operations.Release(); }
    }
    private async Task<string?> EnsureReady(DateTimeOffset start, CancellationToken ct)
    {
        if (ready) return null;
        if (lease is null)
        {
            try { lease = await secrets!.OpenAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { return poison = "CredentialsUnavailable"; }
            if (lease is null) return poison = "CredentialsUnavailable";
            if (!string.Equals(lease.BindingId, scope!.CredentialBindingId, StringComparison.Ordinal)) { lease.Dispose(); return poison = "CredentialBindingMismatch"; }
        }
        var time = await Send<OkxTimeDto>(TimePath, null, false, 1, start, ct).ConfigureAwait(false);
        if (time.Status == OkxReadStatus.Failed) return poison = time.ErrorCode;
        var rtt = time.ReceiveUtc - time.RequestUtc;
        var server = time.Rows[0].Ts.Utc!.Value;
        var midpoint = time.RequestUtc + TimeSpan.FromTicks(rtt.Ticks / 2);
        if (rtt < TimeSpan.Zero || rtt > TimeSpan.FromSeconds(2) || (server - midpoint).Duration() > TimeSpan.FromSeconds(5)) return poison = "ClockPreflightFailed";
        ServerUtc = server;
        var config = await Send<OkxAccountConfigDto>(ConfigPath, null, true, 1, start, ct).ConfigureAwait(false);
        if (config.Status == OkxReadStatus.Failed) return poison = config.ErrorCode;
        var err = Validate(config.Rows[0]); if (err != null) return poison = err;
        baseline ??= config.Rows[0];
        validatedLane = lanes.GetOrAdd("uid:" + baseline.Uid, _ => new Lane());
        bootstrapConfig = config; ready = true; return null;
    }
    private string? Validate(OkxAccountConfigDto c)
    {
        if (!string.Equals(c.Uid, scope!.ExpectedUid, StringComparison.Ordinal)) return "IdentityConflict";
        if (c.Perm != "read_only" || string.IsNullOrWhiteSpace(c.Ip) || c.AcctLv != "1" || c.AutoLoan || c.EnableSpotBorrow) return "UnsafeAccountConfig";
        if (baseline != null && baseline != c) return "AccountConfigChanged";
        return null;
    }
    private async Task<OkxRead<T>> Send<T>(string endpoint, Dictionary<string, string>? query, bool priv, int? maxRows, DateTimeOffset logicalStart, CancellationToken ct)
    {
        if (!Allowlist.Contains(endpoint)) return Fail<T>(endpoint, "EndpointDenied", priv, logicalStart);
        string exact = endpoint;
        if (query?.Count > 0) exact += "?" + string.Join('&', query.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        var uri = new Uri("https://eea.okx.com:443" + exact, UriKind.Absolute);
        if (uri.Host != "eea.okx.com" || uri.Scheme != "https" || uri.Port != 443 || uri.PathAndQuery != exact || uri.Fragment.Length != 0) return Fail<T>(endpoint, "InvalidRequest", priv, logicalStart);
        var lane = priv ? lanes.GetOrAdd("binding:" + lease!.BindingId, _ => new Lane()) : publicLane;
        // An expected identity can only RESTRICT bootstrap traffic. It is not account
        // authority: baseline/ready are still set only after exact config validation.
        var uidLane = priv ? validatedLane ?? lanes.GetOrAdd("uid:" + scope!.ExpectedUid, _ => new Lane()) : null;
        bool uidHeld = false;
        await lane.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Always lock binding then UID, including bootstrap for different bindings.
            if (uidLane != null) { await uidLane.Gate.WaitAsync(ct).ConfigureAwait(false); uidHeld = true; }
            for (int attempt = 0; attempt < 3; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var next = uidLane != null && uidLane.Next > lane.Next ? uidLane.Next : lane.Next;
                var wait = next - Clock.GetUtcNow(); if (wait > TimeSpan.Zero) { if (Clock.GetUtcNow() + wait - logicalStart >= TimeSpan.FromSeconds(30)) return Fail<T>(endpoint, "Timeout", priv, logicalStart); await delay(wait, ct).ConfigureAwait(false); }
                if (Clock.GetUtcNow() - logicalStart >= TimeSpan.FromSeconds(30)) return Fail<T>(endpoint, "Timeout", priv, logicalStart);
                var requestTime = Clock.GetUtcNow(); lane.Next = requestTime + TimeSpan.FromMilliseconds(priv ? 600 : 100);
                if (uidLane != null) uidLane.Next = lane.Next;
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Add("x-simulated-trading", "1");
                if (priv && HttpDiagnosticsGuard.Unsafe) return Fail<T>(endpoint, "UnsafeHttpDiagnostics", true, requestTime);
                if (priv) lease!.Authenticate(request, requestTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture), request.RequestUri!.PathAndQuery);
                using var attemptTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10), Clock);
                using var attemptLinked = CancellationTokenSource.CreateLinkedTokenSource(ct, attemptTimeout.Token);
                string error = "NetworkFailure"; bool retry = false; TimeSpan? retryAfter = null; string? hash = null;
                try
                {
                    using var response = await http.SendAsync(request, attemptLinked.Token).ConfigureAwait(false);
                    int status = (int)response.StatusCode;
                    if (status is >= 300 and <= 399) return Fail<T>(endpoint, "RedirectDenied", priv, requestTime);
                    if (status == 429 || status is 502 or 503 or 504)
                    {
                        error = status == 429 ? "RateLimited" : "HttpUnavailable"; retry = true;
                        if (response.Headers.RetryAfter?.Delta is TimeSpan delta && delta >= TimeSpan.Zero) retryAfter = delta;
                        else if (response.Headers.RetryAfter?.Date is DateTimeOffset date && date > Clock.GetUtcNow()) retryAfter = date - Clock.GetUtcNow();
                    }
                    else if (!response.IsSuccessStatusCode) return Fail<T>(endpoint, status == 403 ? "HttpForbidden" : "HttpRejected", priv, requestTime);
                    else
                    {
                        var bytes = await Body(response, attemptLinked.Token).ConfigureAwait(false);
                        try
                        {
                            hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                            var parsed = OkxJson.Parse<T>(bytes, maxRows, attemptLinked.Token,
                                publicCatalog: endpoint == "/api/v5/public/instruments",
                                singleton: endpoint is TimePath or ConfigPath or "/api/v5/account/balance" or "/api/v5/trade/order");
                            attemptLinked.Token.ThrowIfCancellationRequested();
                            if (Clock.GetUtcNow() - logicalStart >= TimeSpan.FromSeconds(30)) return Fail<T>(endpoint, "Timeout", priv, requestTime, hash);
                            if (Clock.GetUtcNow() - requestTime >= TimeSpan.FromSeconds(10)) throw new OperationCanceledException(attemptLinked.Token);
                            if (parsed.Code == "0") return new(OkxReadStatus.Observed, parsed.Rows, null, endpoint, requestTime, Clock.GetUtcNow(), priv, hash);
                            error = parsed.Code switch { "50011" => "RateLimited", "50004" => "ExchangeTimeout", "50101" or "50102" or "50119" or "50123" => parsed.Code, _ => "BusinessRejected" };
                            retry = parsed.Code is "50011" or "50004";
                        }
                        finally { CryptographicOperations.ZeroMemory(bytes); }
                    }
                }
                catch (OperationCanceledException) { ct.ThrowIfCancellationRequested(); error = "Timeout"; retry = true; }
                catch (HttpRequestException e) { error = e.HttpRequestError == HttpRequestError.SecureConnectionError || HasTls(e) ? "TlsFailure" : "NetworkFailure"; retry = error != "TlsFailure"; }
                catch (IOException) { error = "NetworkFailure"; retry = true; }
                catch (BodyLimitException) { return Fail<T>(endpoint, "BodyLimit", priv, requestTime); }
                catch (Exception e) when (e is JsonException or OkxParseException or FormatException or OverflowException) { return Fail<T>(endpoint, "InvalidPayload", priv, requestTime, hash); }
                if (!retry || attempt == 2) return Fail<T>(endpoint, error, priv, requestTime, hash);
                var backoff = TimeSpan.FromSeconds(attempt + 1) + TimeSpan.FromMilliseconds(RandomNumberGenerator.GetInt32(251));
                if (retryAfter > backoff) backoff = retryAfter.Value;
                if (Clock.GetUtcNow() + backoff - logicalStart >= TimeSpan.FromSeconds(30)) return Fail<T>(endpoint, error, priv, requestTime, hash);
                await delay(backoff, ct).ConfigureAwait(false);
            }
            return Fail<T>(endpoint, "NetworkFailure", priv, logicalStart);
        }
        finally { if (uidHeld) uidLane!.Gate.Release(); lane.Gate.Release(); }
    }
    // Refuse private transport under request/header instrumentation rather than exporting secrets.
    // This does not sandbox hostile code inside the same process (which could also use reflection).
    private sealed class HttpDiagnosticsGuard : IObserver<System.Diagnostics.DiagnosticListener>
    {
        private readonly List<WeakReference<System.Diagnostics.DiagnosticListener>> listeners = [];
        private static readonly HttpDiagnosticsGuard Instance = new();
        private static readonly IDisposable Subscription = System.Diagnostics.DiagnosticListener.AllListeners.Subscribe(Instance);
        public void OnNext(System.Diagnostics.DiagnosticListener value) { if (value.Name == "HttpHandlerDiagnosticListener") lock (listeners) listeners.Add(new(value)); }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
        internal static bool Unsafe
        {
            get
            {
                GC.KeepAlive(Subscription);
                lock (Instance.listeners) if (Instance.listeners.Any(w => w.TryGetTarget(out var listener) && listener.IsEnabled())) return true;
                return System.Diagnostics.Tracing.EventSource.GetSources().Any(s => s.Name == "System.Net.Http" && s.IsEnabled());
            }
        }
    }
    private static bool HasTls(Exception e) { for (Exception? p = e; p != null; p = p.InnerException) if (p is AuthenticationException) return true; return false; }
    private sealed class BodyLimitException : Exception { }
    private static async Task<byte[]> Body(HttpResponseMessage response, CancellationToken ct)
    {
        const int limit = 8 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new BodyLimitException();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream(); byte[] chunk = new byte[8192];
        try { int count; while ((count = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) != 0) { ct.ThrowIfCancellationRequested(); if (buffer.Length + count > limit) throw new BodyLimitException(); buffer.Write(chunk, 0, count); } return buffer.ToArray(); }
        finally { CryptographicOperations.ZeroMemory(chunk); if (buffer.TryGetBuffer(out var data)) CryptographicOperations.ZeroMemory(data.AsSpan()); }
    }
    public async ValueTask DisposeAsync()
    {
        await operations.WaitAsync().ConfigureAwait(false);
        try { if (disposed) return; disposed = true; lease?.Dispose(); http.Dispose(); }
        finally { operations.Release(); }
    }
}
