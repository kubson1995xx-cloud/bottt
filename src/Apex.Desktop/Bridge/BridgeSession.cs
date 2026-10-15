using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Bridge;
using Apex.Core.Model;

namespace Apex.Desktop.Bridge
{
    /// <summary>Transport komunikatów (WebSocket lub TCP z JSON w liniach).</summary>
    public interface IBridgeTransport
    {
        string Kind { get; }
        string Remote { get; }
        Task SendAsync(string json, CancellationToken ct);
        Task CloseAsync(string reason);
    }

    public enum SessionState
    {
        AwaitingHello,
        Active,
        Rejected,
        Closed,
    }

    /// <summary>
    /// Sesja jednego mostu (jedno konto na jednej platformie). Przechowuje najnowszy stan przesłany przez most
    /// (konto, symbole, notowania, pozycje, świece) i realizuje zapytania z korelacją identyfikatorów.
    /// Bezpieczna wątkowo: odbiór na wątku sieciowym, odczyt z wątku silnika.
    /// </summary>
    public sealed class BridgeSession
    {
        private readonly IBridgeTransport _transport;
        private readonly object _lock = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new();
        private readonly Dictionary<string, SymbolDto> _symbols = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (QuoteDto q, DateTime received)> _quotes = new(StringComparer.OrdinalIgnoreCase);
        private List<PositionDto> _positions = new();
        private AccountDto? _account;
        private long _requestSeq;
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public BridgeSession(IBridgeTransport transport)
        {
            _transport = transport;
            ConnectedUtc = DateTime.UtcNow;
            LastMessageUtc = DateTime.UtcNow;
        }

        public string Id { get; } = Guid.NewGuid().ToString("N").Substring(0, 12);
        public SessionState State { get; set; } = SessionState.AwaitingHello;
        public string Platform { get; private set; } = "";
        public string BridgeVersion { get; private set; } = "";
        public string Transport => _transport.Kind;
        public string Remote => _transport.Remote;
        public DateTime ConnectedUtc { get; }
        public DateTime LastMessageUtc { get; private set; }
        public BarStore Bars { get; } = new(2000);
        public int MessagesReceived { get; private set; }
        public bool SymbolsReceived { get; private set; }
        public string? AccountKey { get; set; }
        public List<string> BridgeLog { get; } = new();

        public event Action<BridgeSession, HelloMsg>? HelloReceived;
        public event Action<BridgeSession>? SymbolsUpdated;
        public event Action<BridgeSession, ClosedDto>? PositionClosed;

        public AccountDto? Account { get { lock (_lock) return _account; } }

        public IReadOnlyList<PositionDto> Positions { get { lock (_lock) return _positions.ToList(); } }

        public IReadOnlyList<SymbolDto> Symbols { get { lock (_lock) return _symbols.Values.ToList(); } }

        public SymbolDto? Symbol(string name) { lock (_lock) return _symbols.TryGetValue(name, out var s) ? s : null; }

        public (QuoteDto q, DateTime received)? Quote(string name) { lock (_lock) return _quotes.TryGetValue(name, out var q) ? q : null; }

        public int QuoteCount { get { lock (_lock) return _quotes.Count; } }

        public bool IsAlive => State == SessionState.Active && (DateTime.UtcNow - LastMessageUtc) < TimeSpan.FromSeconds(30);

        public static string AccountKeyFor(string platform, AccountDto a)
        {
            string broker = new string((a.Broker ?? "").Where(char.IsLetterOrDigit).ToArray());
            if (broker.Length > 24) broker = broker.Substring(0, 24);
            return $"{platform}:{broker}:{a.Number}";
        }

        // ------------------------------------------------------------------ odbiór

        public void Handle(string json)
        {
            LastMessageUtc = DateTime.UtcNow;
            MessagesReceived++;
            string? type = BridgeProtocol.ReadType(json);
            if (type == null) return;

            if (State == SessionState.AwaitingHello)
            {
                if (type != "hello") return;
                var hello = BridgeProtocol.Deserialize<HelloMsg>(json);
                if (hello == null) return;
                Platform = Sanitize(hello.Platform, 20);
                BridgeVersion = Sanitize(hello.BridgeVersion, 20);
                lock (_lock) _account = hello.Account;
                HelloReceived?.Invoke(this, hello);
                return;
            }
            if (State != SessionState.Active) return;

            switch (type)
            {
                case "symbols":
                    {
                        var m = BridgeProtocol.Deserialize<SymbolsMsg>(json);
                        if (m == null) return;
                        lock (_lock)
                            foreach (var s in m.Items)
                                if (!string.IsNullOrWhiteSpace(s.Name)) _symbols[s.Name] = s;
                        if (m.Final)
                        {
                            SymbolsReceived = true;
                            SymbolsUpdated?.Invoke(this);
                        }
                        break;
                    }
                case "bars":
                    {
                        var m = BridgeProtocol.Deserialize<BarsMsg>(json);
                        if (m == null || !TfNames.TryParse(m.Tf, out var tf)) return;
                        var bars = new List<Bar>(m.Bars.Count);
                        foreach (var b in m.Bars)
                        {
                            if (b == null || b.Length < 5) continue;
                            bars.Add(new Bar(BridgeProtocol.FromUnixSec((long)b[0]), b[1], b[2], b[3], b[4], b.Length > 5 ? b[5] : 0));
                        }
                        Bars.Merge(m.Symbol, tf, bars, m.Snapshot);
                        break;
                    }
                case "quotes":
                    {
                        var m = BridgeProtocol.Deserialize<QuotesMsg>(json);
                        if (m == null) return;
                        var now = DateTime.UtcNow;
                        lock (_lock)
                            foreach (var q in m.Items)
                                if (!string.IsNullOrEmpty(q.S)) _quotes[q.S] = (q, now);
                        break;
                    }
                case "account":
                    {
                        var m = BridgeProtocol.Deserialize<AccountMsg>(json);
                        if (m?.Account == null) return;
                        lock (_lock)
                        {
                            // Zmiana numeru konta w trakcie sesji = przełączenie konta na platformie – sesja zostaje zablokowana.
                            if (_account != null && m.Account.Number != _account.Number)
                            {
                                State = SessionState.Rejected;
                                _ = _transport.CloseAsync("zmiana konta w trakcie sesji");
                                return;
                            }
                            _account = m.Account;
                        }
                        break;
                    }
                case "positions":
                    {
                        var m = BridgeProtocol.Deserialize<PositionsMsg>(json);
                        if (m == null) return;
                        lock (_lock) _positions = m.Items;
                        break;
                    }
                case "closed":
                    {
                        var m = BridgeProtocol.Deserialize<ClosedMsg>(json);
                        if (m?.Position != null) PositionClosed?.Invoke(this, m.Position);
                        break;
                    }
                case "result":
                case "marginResult":
                    {
                        string? id = null;
                        try
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(json);
                            if (doc.RootElement.TryGetProperty("id", out var idEl)) id = idEl.GetString();
                        }
                        catch (System.Text.Json.JsonException) { }
                        if (id != null && _pending.TryRemove(id, out var tcs)) tcs.TrySetResult(json);
                        break;
                    }
                case "log":
                    {
                        var m = BridgeProtocol.Deserialize<LogMsg>(json);
                        if (m == null) return;
                        lock (_lock)
                        {
                            BridgeLog.Insert(0, $"{DateTime.UtcNow:HH:mm:ss} [{Sanitize(m.Level, 8)}] {Sanitize(m.Message, 300)}");
                            if (BridgeLog.Count > 50) BridgeLog.RemoveAt(BridgeLog.Count - 1);
                        }
                        break;
                    }
            }
        }

        private static string Sanitize(string? s, int max)
        {
            s ??= "";
            var clean = new string(s.Where(c => !char.IsControl(c)).ToArray());
            return clean.Length > max ? clean.Substring(0, max) : clean;
        }

        // ------------------------------------------------------------------ wysyłanie

        public async Task SendAsync<T>(T msg)
        {
            string json = BridgeProtocol.Serialize(msg);
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try { await _transport.SendAsync(json, CancellationToken.None).ConfigureAwait(false); }
            finally { _sendLock.Release(); }
        }

        public void Send<T>(T msg)
        {
            try { SendAsync(msg).Wait(TimeSpan.FromSeconds(10)); }
            catch (AggregateException) { }
        }

        public string NextId() => $"{Id}-{Interlocked.Increment(ref _requestSeq)}";

        /// <summary>
        /// Wysyła zapytanie i czeka na odpowiedź z tym samym identyfikatorem.
        /// Zwraca (wysłano?, odpowiedź?). Brak odpowiedzi po wysłaniu = stan NIEPEWNY (silnik zweryfikuje pozycje).
        /// </summary>
        public (bool sent, string? response) Request<T>(T msg, string id, TimeSpan timeout)
        {
            if (!IsAlive) return (false, null);
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            try
            {
                var send = SendAsync(msg);
                if (!send.Wait(TimeSpan.FromSeconds(5)) || send.IsFaulted)
                {
                    _pending.TryRemove(id, out _);
                    return (false, null);
                }
            }
            catch (AggregateException)
            {
                _pending.TryRemove(id, out _);
                return (false, null);
            }
            bool ok;
            try { ok = tcs.Task.Wait(timeout); }
            catch (AggregateException) { ok = false; }
            _pending.TryRemove(id, out _);
            return (true, ok && tcs.Task.Status == TaskStatus.RanToCompletion ? tcs.Task.Result : null);
        }

        public Task CloseAsync(string reason)
        {
            State = State == SessionState.Rejected ? SessionState.Rejected : SessionState.Closed;
            foreach (var kv in _pending) kv.Value.TrySetCanceled();
            return _transport.CloseAsync(reason);
        }

        public void MarkClosed()
        {
            if (State != SessionState.Rejected) State = SessionState.Closed;
            foreach (var kv in _pending) kv.Value.TrySetCanceled();
        }
    }
}
