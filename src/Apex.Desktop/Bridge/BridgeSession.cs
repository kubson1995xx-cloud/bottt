using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Bridge;
using Apex.Core.Model;
using Apex.Core.Engine;

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
    public sealed partial class BridgeSession
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
        private readonly CancellationTokenSource _lifetime = new();
        private int _state;
        private DateTime _accountReceivedUtc;
        private DateTime _positionsReceivedUtc;
        public string HistoryRequestId { get; private set; } = "";
        private BrokerHistorySnapshot _history = new();
        private readonly List<ClosedDto> _historyRows = new();
        private readonly Dictionary<int, string> _historyPages = new();
        private HistoryPage? _historyFirst;
        private bool _historyFailed;
        private DateTime _historyStarted;
        private DateTime _historyReceived;
        private string _historyLabel = "";
        private long _positionsRevision;
        private long _historyRevision;
        private long _acceptedRevision;
        private DateTime _historyCaptured;
        public BrokerHistorySnapshot History
        {
            get
            {
                lock (_lock)
                {
                    if (!IsAlive || _historyFailed || !_history.Complete || DateTime.UtcNow - _historyReceived >= TimeSpan.FromSeconds(30)
                        || DateTime.UtcNow - _historyCaptured >= TimeSpan.FromSeconds(30)) return new();
                    if (_acceptedRevision != _positionsRevision || _acceptedPendingRevision != _pendingRevision)
                        return new() { AccountNumber = _history.AccountNumber, Generation = Id, Synchronizing = true };
                    return _history;
                }
            }
        }
        private IEnumerable<(long, string, string, double, double, long, double?, double?, string, string)> OwnState(IEnumerable<PositionDto> positions) =>
            positions.Where(x => ScopedCapable || x.Label == _historyLabel).OrderBy(x => x.Id)
                .Select(x => (x.Id, x.Symbol, x.Side, x.Units, x.Entry, x.EntryTime, x.Sl, x.Tp, x.Label, x.Comment));
        public bool HistoryCapable { get; private set; }
        private long _acceptedPendingRevision;
        public Task RequestHistoryAsync(string label)
        {
            lock (_lock)
            {
                HistoryRequestId = Guid.NewGuid().ToString("N");
                _historyLabel = label;
                _historyRows.Clear(); _historyPages.Clear(); _historyFirst = null; _historyFailed = false;
                _historyStarted = DateTime.UtcNow;
                _historyRevision = _positionsRevision; // fence before capture/page 0, not on its receipt
                _requestPendingRevision = _pendingRevision;
                _requestBrokerEpoch = BrokerEpoch;
            }
            return SendAsync(new HistoryRequest { RequestId = HistoryRequestId, Label = label, BaselineId = _baselineId, RecoverOperations = RecoverOperations.ToList() });
        }
        public void EnsureHistory(string label)
        {
            lock (_lock)
            {
                if (!HistoryCapable || HistoryRequestId.Length == 0 || !IsAlive || DateTime.UtcNow - _historyStarted < TimeSpan.FromSeconds(10)) return;
                _ = RequestHistoryAsync(label).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
        private void ReceiveHistory(HistoryPage m, string json)
        {
            BrokerHistorySnapshot completed;
            ScopedSnapshot? scoped = null;
            lock (_lock)
            {
                if (!HistoryCapable || m.RequestId != HistoryRequestId || _historyFailed) return;
                void Fail() { _historyFailed = true; _history = new(); _acceptedScope = null; }
                if (_historyPages.TryGetValue(m.Page, out var prior)) { if (prior != json) Fail(); return; }
                if (m.Error != null || m.Scope != "available-history-v1" || m.Total < 0 || m.Total > 100000
                    || m.Pages < 1 || m.Pages > 1000 || m.Page != _historyPages.Count || m.Page >= m.Pages
                    || m.Deals == null || m.Deals.Count > 500 || m.CapturedAt <= 0
                    || BridgeProtocol.FromUnixMs(m.CapturedAt) > DateTime.UtcNow
                    || DateTime.UtcNow - BridgeProtocol.FromUnixMs(m.CapturedAt) > TimeSpan.FromSeconds(30)
                    || DateTime.UtcNow - _historyStarted > TimeSpan.FromSeconds(30))
                { Fail(); return; }
                if (_historyFirst == null) _historyFirst = m;
                var first = _historyFirst;
                if (m.Total != first.Total || m.Pages != first.Pages || m.Scope != first.Scope || m.Coverage != first.Coverage || m.CapturedAt != first.CapturedAt || m.Scoped != first.Scoped)
                { Fail(); return; }
                _historyPages.Add(m.Page, json); _historyRows.AddRange(m.Deals);
                if (_historyRows.Count > m.Total) { Fail(); return; }
                if (m.Page != m.Pages - 1) return;
                if (_historyRows.Count != m.Total || m.Account == null || m.Positions == null || _account == null
                    || m.Account.Number != _account.Number || m.Account.Broker != _account.Broker || m.Account.Currency != _account.Currency
                    || m.Account.IsLive != _account.IsLive || m.Account.Hedging != _account.Hedging
                    || m.Positions.Count > 10000 || m.Positions.Select(x => x.Id).Distinct().Count() != m.Positions.Count)
                { Fail(); return; }
                if (_historyRevision != _positionsRevision || _requestPendingRevision != _pendingRevision || !OwnState(m.Positions).SequenceEqual(OwnState(_positions)))
                { _historyStarted = DateTime.MinValue; return; } // expected race: retry, never republish frozen live state
                try { scoped = CompleteScope(m); }
                catch (InvalidOperationException) { Fail(); return; }
                _history = new BrokerHistorySnapshot
                {
                    AccountNumber = m.Account.Number, Generation = Id, Complete = true, Scope = m.Scope,
                    Positions = m.Positions.Select(x => x.ToInfo()).ToArray(),
                    // SDK enumeration does not certify broker completeness. Synthetic local fixtures
                    // alone have a complete in-memory source; no cTrader claim can enable risk.
                    RequiredCoverage = _trustedSyntheticComposition && m.Coverage == "required",
                    Closed = _historyRows.Select(x => x.ToInfo()).ToArray(),
                };
                _historyReceived = DateTime.UtcNow;
                _historyCaptured = BridgeProtocol.FromUnixMs(m.CapturedAt);
                _acceptedRevision = _positionsRevision;
                _acceptedPendingRevision = _pendingRevision;
                _acceptedScope = scoped;
                completed = _history;
            }
            HistoryCompleted?.Invoke(this, completed);
            if (scoped != null) ScopedCompleted?.Invoke(this, scoped);
        }

        private readonly bool _trustedSyntheticComposition;
        public BridgeSession(IBridgeTransport transport) : this(transport, false) { }
        internal BridgeSession(IBridgeTransport transport, bool trustedSyntheticComposition)
        {
            _trustedSyntheticComposition = trustedSyntheticComposition;
            _transport = transport;
            ConnectedUtc = DateTime.UtcNow;
            LastMessageUtc = DateTime.UtcNow;
        }

        public string Id { get; } = Guid.NewGuid().ToString("N").Substring(0, 12);
        public SessionState State { get => (SessionState)Volatile.Read(ref _state); set => Volatile.Write(ref _state, (int)value); }
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
        public event Action<BridgeSession, BrokerHistorySnapshot>? HistoryCompleted;

        public AccountDto? Account { get { lock (_lock) return _account == null ? null : CloneAccount(_account); } }
        public DateTime AccountReceivedUtc { get { lock (_lock) return _accountReceivedUtc; } }
        public bool HasFreshSnapshots
        {
            get
            {
                lock (_lock)
                    return IsAlive && DateTime.UtcNow - _accountReceivedUtc < TimeSpan.FromSeconds(30)
                        && DateTime.UtcNow - _positionsReceivedUtc < TimeSpan.FromSeconds(30);
            }
        }
        public List<string> RecentLog(int count) { lock (_lock) return BridgeLog.Take(count).ToList(); }
        private static AccountDto CloneAccount(AccountDto a) => new()
        {
            Number = a.Number, Broker = a.Broker, Currency = a.Currency, IsLive = a.IsLive, Hedging = a.Hedging,
            Balance = a.Balance, Equity = a.Equity, Margin = a.Margin, FreeMargin = a.FreeMargin, MarginLevel = a.MarginLevel,
        };
        private static bool ValidAccount(string json, AccountDto? a)
        {
            if (a == null || a.Number <= 0 || string.IsNullOrWhiteSpace(a.Broker) || string.IsNullOrWhiteSpace(a.Currency)
                || !double.IsFinite(a.Balance) || !double.IsFinite(a.Equity)
                || !double.IsFinite(a.Margin) || a.Margin < 0 || !double.IsFinite(a.FreeMargin)
                || (a.MarginLevel.HasValue && !double.IsFinite(a.MarginLevel.Value))) return false;
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("account", out var account)
                && account.ValueKind == JsonValueKind.Object && account.TryGetProperty("isLive", out var live)
                && (live.ValueKind == JsonValueKind.True || live.ValueKind == JsonValueKind.False);
        }
        private void RejectIdentity()
        {
            State = SessionState.Rejected;
            _ = CloseAsync("nieprawidłowe dane lub zmiana tożsamości konta");
        }

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
                if (!ValidAccount(json, hello.Account)) { RejectIdentity(); return; }
                Platform = Sanitize(hello.Platform, 20);
                BridgeVersion = Sanitize(hello.BridgeVersion, 20);
                HistoryCapable = hello.HistoryCapability == HistoryPage.Capability;
                ScopedCapable = hello.ScopedCapability == ScopeFrame.Capability;
                PeerLabel = hello.Label;
                DeploymentInventory = hello.DeploymentInventory?.ToArray();
                BrokerEpoch = hello.BrokerEpoch;
                BrokerConnected = hello.BrokerConnected == true && BrokerEpoch.Length > 0;
                lock (_lock) { _account = CloneAccount(hello.Account); _accountReceivedUtc = DateTime.UtcNow; }
                HelloReceived?.Invoke(this, hello);
                return;
            }
            if (State != SessionState.Active) return;

            switch (type)
            {
                case "brokerState":
                    { var m = BridgeProtocol.Deserialize<BrokerStateMsg>(json); if (m != null) ReceiveBroker(m); break; }
                case "historyPage":
                    { var m = BridgeProtocol.Deserialize<HistoryPage>(json); if (m != null) ReceiveHistory(m, json); break; }
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
                        if (m?.BrokerState != null) ReceiveBroker(m.BrokerState);
                        if (!ValidAccount(json, m?.Account)) { RejectIdentity(); return; }
                        lock (_lock)
                        {
                            // Zmiana numeru konta w trakcie sesji = przełączenie konta na platformie – sesja zostaje zablokowana.
                            var a = m!.Account;
                            if (_account == null || a.Number != _account.Number || a.IsLive != _account.IsLive
                                || a.Broker != _account.Broker || a.Currency != _account.Currency || a.Hedging != _account.Hedging)
                            {
                                RejectIdentity();
                                return;
                            }
                            _account = CloneAccount(a);
                            _accountReceivedUtc = DateTime.UtcNow;
                        }
                        break;
                    }
                case "positions":
                    {
                        var m = BridgeProtocol.Deserialize<PositionsMsg>(json);
                        if (m == null) return;
                        if (m.Items == null) return;
                        lock (_lock)
                        {
                            if (m.PendingOrders != null && !_pendingOrders.OrderBy(x => x.Id).SequenceEqual(m.PendingOrders.OrderBy(x => x.Id)))
                            { _pendingRevision++; _historyStarted = DateTime.MinValue; }
                            _pendingOrders = m.PendingOrders ?? new();
                            if (ScopedCapable && _baselineId != Guid.Empty && !ValidScope(m.Scoped))
                            { _acceptedScope = null; _history = new(); }
                            _liveScope = m.Scoped;
                            if (!OwnState(_positions).SequenceEqual(OwnState(m.Items)))
                            { _positionsRevision++; _historyStarted = DateTime.MinValue; }
                            _positions = m.Items; _positionsReceivedUtc = DateTime.UtcNow;
                        }
                        break;
                    }
                case "closed":
                    {
                        var m = BridgeProtocol.Deserialize<ClosedMsg>(json);
                        lock (_lock) { _positionsRevision++; _historyStarted = DateTime.MinValue; }
                        if (m?.Position != null) PositionClosed?.Invoke(this, m.Position);
                        break;
                    }
                case "result":
                case "marginResult":
                    {
                        var result = BridgeProtocol.Deserialize<ResultMsg>(json);
                        if (result?.BrokerState != null) ReceiveBroker(result.BrokerState);
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
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await _sendLock.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                timeout.Token.ThrowIfCancellationRequested();
                await _transport.SendAsync(BridgeProtocol.Serialize(msg), timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch { _ = CloseAsync("błąd wysyłania"); throw; }
            finally { _sendLock.Release(); }
        }

        public void Send<T>(T msg)
        {
            try { SendAsync(msg).GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is OperationCanceledException || ex is System.IO.IOException || ex is System.Net.WebSockets.WebSocketException || ex is InvalidOperationException) { }
        }

        public string NextId() => $"{Id}-{Interlocked.Increment(ref _requestSeq)}";

        /// <summary>Termin obejmuje oczekiwanie na blokadę, wysyłanie i odpowiedź.
        /// Po rozpoczęciu wysyłania brak potwierdzenia oznacza NIEPEWNOŚĆ, nigdy pewne odrzucenie.</summary>
        public (bool sent, string? response) Request<T>(T msg, string id, TimeSpan timeout) =>
            RequestAsync(msg, id, timeout).GetAwaiter().GetResult();

        // Wyłącznie redukcja: ponowna autoryzacja konkretnego ID po oczekiwaniu na transport.
        public (bool sent, string? response) RequestOwnedFullClose(long positionId, string label, Func<bool> authorized, TimeSpan timeout)
        {
            string id = NextId();
            bool Own() { if (positionId <= 0 || !authorized()) return false;
                lock (_lock) return _positions.Any(p => p.Id == positionId && string.Equals(p.Label, label, StringComparison.Ordinal)); }
            return RequestAsync(new CloseCmd { Id = id, PositionId = positionId, Units = null }, id, timeout, Own).GetAwaiter().GetResult();
        }

        private async Task<(bool sent, string? response)> RequestAsync<T>(T msg, string id, TimeSpan timeout, Func<bool>? authorized = null)
        {
            if (!HasFreshSnapshots || Account?.IsLive != false || authorized?.Invoke() == false) return (false, null);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            deadline.CancelAfter(timeout);
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(id, tcs)) return (false, null);
            bool acquired = false, started = false;
            try
            {
                await _sendLock.WaitAsync(deadline.Token).ConfigureAwait(false);
                acquired = true;
                deadline.Token.ThrowIfCancellationRequested();
                if (!HasFreshSnapshots || Account?.IsLive != false || authorized?.Invoke() == false) return (false, null);
                started = true;
                await _transport.SendAsync(BridgeProtocol.Serialize(msg), deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
                _sendLock.Release();
                acquired = false;
                return (true, await tcs.Task.WaitAsync(deadline.Token).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex is System.IO.IOException || ex is System.Net.WebSockets.WebSocketException || ex is InvalidOperationException)
            {
                if (started) _ = CloseAsync("niepewny wynik wysłania – wymagane ponowne połączenie");
                return (started, null);
            }
            finally
            {
                if (acquired) _sendLock.Release();
                _pending.TryRemove(id, out _);
            }
        }

        public Task CloseAsync(string reason)
        {
            lock (_closeGate)
            {
                if (_closeTask != null) return _closeTask;
                MarkClosed();
                return _closeTask = _transport.CloseAsync(reason);
            }
        }

        private readonly object _closeGate = new();
        private Task? _closeTask;

        public void MarkClosed()
        {
            if (State != SessionState.Rejected) State = SessionState.Closed;
            _lifetime.Cancel();
            foreach (var kv in _pending) kv.Value.TrySetCanceled();
        }
    }
}
