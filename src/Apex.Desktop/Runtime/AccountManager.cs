using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Apex.Core.Bridge;
using Apex.Core.Engine;
using Apex.Desktop.Bridge;
using Apex.Desktop.Services;

namespace Apex.Desktop.Runtime
{
    /// <summary>
    /// Rejestr połączonych mostów i kont. Weryfikuje klucz parowania, wersję protokołu oraz blokuje
    /// drugie połączenie dla tego samego konta (ochrona przed dwoma silnikami handlującymi jednym kontem).
    /// </summary>
    public sealed class AccountManager : IDisposable
    {
        public const string HostVersion = "1.0.0";

        private readonly ConfigStore _config;
        private readonly NewsCalendar _news;
        private readonly AppLog _log;
        private readonly ConcurrentDictionary<string, AccountRuntime> _runtimes = new();
        private readonly ConcurrentDictionary<string, BridgeSession> _sessions = new();
        private readonly ConcurrentQueue<string> _rejections = new();

        public AccountManager(ConfigStore config, NewsCalendar news, AppLog log)
        {
            _config = config;
            _news = news;
            _log = log;
        }

        public IReadOnlyCollection<AccountRuntime> Runtimes => _runtimes.Values.ToList();

        public AccountRuntime? Get(string key) => _runtimes.TryGetValue(key, out var r) ? r : null;

        public void Register(BridgeSession s)
        {
            _sessions[s.Id] = s;
            s.HelloReceived += OnHello;
            s.SymbolsUpdated += sess => { if (sess.AccountKey != null && _runtimes.TryGetValue(sess.AccountKey, out var r)) r.OnSymbols(sess); };
            s.PositionClosed += (sess, c) => { if (sess.AccountKey != null && _runtimes.TryGetValue(sess.AccountKey, out var r)) r.OnClosed(c); };
        }

        public void Unregister(BridgeSession s)
        {
            _sessions.TryRemove(s.Id, out _);
            if (s.AccountKey != null && _runtimes.TryGetValue(s.AccountKey, out var r))
            {
                r.Detach(s);
                if (s.State != SessionState.Rejected) _log.Warn($"Most {s.Platform} konta {s.AccountKey} rozłączony – handel na tym koncie wstrzymany do ponownego połączenia.");
            }
        }

        private static bool KeyEquals(string a, string b)
        {
            var x = Encoding.UTF8.GetBytes((a ?? "").Trim().ToUpperInvariant());
            var y = Encoding.UTF8.GetBytes((b ?? "").Trim().ToUpperInvariant());
            return x.Length == y.Length && CryptographicOperations.FixedTimeEquals(x, y);
        }

        private void Reject(BridgeSession s, string reason)
        {
            s.State = SessionState.Rejected;
            s.Send(new WelcomeMsg { Accepted = false, Reason = reason, HostVersion = HostVersion });
            _ = s.CloseAsync(reason);
            _log.Warn($"Odrzucono most {s.Platform} ({s.Remote}): {reason}");
            _rejections.Enqueue($"{DateTime.UtcNow:HH:mm:ss} {s.Platform}: {reason}");
            while (_rejections.Count > 10) _rejections.TryDequeue(out _);
        }

        private void OnHello(BridgeSession s, HelloMsg hello)
        {
            if (hello.Protocol != BridgeProtocol.Version) { Reject(s, $"nieobsługiwana wersja protokołu {hello.Protocol} (wymagana {BridgeProtocol.Version})"); return; }
            if (!KeyEquals(hello.Key, _config.Config.PairingKey)) { Reject(s, "nieprawidłowy klucz parowania"); return; }
            if (hello.Account == null || hello.Account.Number <= 0) { Reject(s, "brak danych konta"); return; }
            string platform = string.IsNullOrWhiteSpace(s.Platform) ? "Nieznana" : s.Platform;
            string key = BridgeSession.AccountKeyFor(platform, hello.Account);
            var runtime = _runtimes.GetOrAdd(key, k => new AccountRuntime(k, platform, _config, _news, _log));
            var current = runtime.Adapter.Session;
            if (current != null && !ReferenceEquals(current, s) && current.IsAlive)
            {
                Reject(s, "to konto jest już połączone przez inny most (zamknij duplikat)");
                return;
            }
            s.AccountKey = key;
            s.State = SessionState.Active;
            s.Send(new WelcomeMsg { Accepted = true, HostVersion = HostVersion, Label = _config.Config.Label });
            runtime.Attach(s);
            _log.Info($"Połączono most {platform} {s.BridgeVersion} ({s.Transport}) – konto {(hello.Account.IsLive ? "LIVE" : "DEMO")} {hello.Account.Number} @ {hello.Account.Broker}.");
        }

        public object Snapshot() => new
        {
            accounts = _runtimes.Values.Select(r => r.Status).Where(x => x != null).ToList(),
            pendingSessions = _sessions.Values.Where(s => s.State == SessionState.AwaitingHello).Select(s => new { s.Id, s.Transport, s.Remote, s.ConnectedUtc }).ToList(),
            rejections = _rejections.ToList(),
        };

        public void Dispose()
        {
            foreach (var r in _runtimes.Values) r.Dispose();
        }
    }
}
