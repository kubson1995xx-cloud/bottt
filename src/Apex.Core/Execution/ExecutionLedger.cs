using System;
using System.Collections.Generic;
using System.Linq;
using Apex.Core.Model;

namespace Apex.Core.Execution
{
    public enum SignalState
    {
        /// <summary>Zlecenie wysłane, odpowiedź niepewna – czekamy na weryfikację faktycznego stanu pozycji.</summary>
        PendingVerification,
        /// <summary>Pozycja otwarta.</summary>
        Executed,
        /// <summary>Sygnał wykorzystany bez otwarcia (odrzucony / niepotwierdzony) – nie powtarzamy go.</summary>
        Consumed,
    }

    public sealed class LedgerEntry
    {
        public string SignalId { get; set; } = "";
        public string Symbol { get; set; } = "";
        public SignalState State { get; set; }
        public DateTime TimeUtc { get; set; }
        public long? PositionId { get; set; }
        public string Note { get; set; } = "";
        public int Attempts { get; set; }
    }

    /// <summary>
    /// Rejestr sygnałów i zleceń – podstawa ochrony przed wielokrotnym otwarciem tej samej pozycji.
    /// Każdy sygnał ma deterministyczny identyfikator; raz wysłany sygnał nigdy nie jest wysyłany ponownie,
    /// a symbol z niepewnym zleceniem jest blokowany do czasu weryfikacji stanu pozycji u brokera.
    /// Rejestr jest zapisywany trwale, więc ochrona działa także po restarcie cTrader.
    /// </summary>
    public sealed class ExecutionLedger
    {
        private readonly Dictionary<string, LedgerEntry> _entries = new(StringComparer.Ordinal);

        public IReadOnlyCollection<LedgerEntry> Entries => _entries.Values;

        public bool IsKnown(string signalId) => _entries.ContainsKey(signalId);

        public LedgerEntry? Get(string signalId) => _entries.TryGetValue(signalId, out var e) ? e : null;

        public bool HasPendingFor(string symbol) =>
            _entries.Values.Any(e => e.State == SignalState.PendingVerification && string.Equals(e.Symbol, symbol, StringComparison.OrdinalIgnoreCase));

        public IReadOnlyList<LedgerEntry> Pending => _entries.Values.Where(e => e.State == SignalState.PendingVerification).ToList();

        /// <summary>Rezerwuje sygnał przed wysłaniem zlecenia. Zwraca false jeśli sygnał był już użyty.</summary>
        public bool TryReserve(string signalId, string symbol, DateTime utc)
        {
            if (_entries.ContainsKey(signalId)) return false;
            _entries[signalId] = new LedgerEntry { SignalId = signalId, Symbol = symbol, State = SignalState.PendingVerification, TimeUtc = utc, Attempts = 1 };
            return true;
        }

        public void MarkExecuted(string signalId, long positionId, DateTime utc)
        {
            if (_entries.TryGetValue(signalId, out var e))
            {
                e.State = SignalState.Executed;
                e.PositionId = positionId;
                e.TimeUtc = utc;
            }
        }

        public void MarkConsumed(string signalId, string note, DateTime utc)
        {
            if (_entries.TryGetValue(signalId, out var e))
            {
                e.State = SignalState.Consumed;
                e.Note = note;
                e.TimeUtc = utc;
            }
        }

        public void Restore(IEnumerable<LedgerEntry> entries)
        {
            _entries.Clear();
            foreach (var e in entries)
                if (!string.IsNullOrEmpty(e.SignalId)) _entries[e.SignalId] = e;
        }

        /// <summary>Usuwa wpisy starsze niż <paramref name="maxAge"/> (oprócz oczekujących na weryfikację).</summary>
        public int Prune(DateTime utc, TimeSpan maxAge)
        {
            var old = _entries.Values.Where(e => e.State != SignalState.PendingVerification && utc - e.TimeUtc > maxAge).Select(e => e.SignalId).ToList();
            foreach (var k in old) _entries.Remove(k);
            return old.Count;
        }

        /// <summary>Komentarz zlecenia: APEX|kod strategii|id sygnału|wersja – pozwala odnaleźć pozycję po niepewnej odpowiedzi.</summary>
        public static string BuildComment(StrategyId strategy, string signalId) => $"APEX|{strategy.Code()}|{signalId}|v1";

        public static bool TryParseComment(string? comment, out StrategyId strategy, out string signalId)
        {
            strategy = default;
            signalId = "";
            if (string.IsNullOrEmpty(comment)) return false;
            var parts = comment!.Split('|');
            if (parts.Length < 3 || parts[0] != "APEX") return false;
            if (!StrategyIdExtensions.TryParseCode(parts[1], out strategy)) return false;
            signalId = parts[2];
            return signalId.Length > 0;
        }
    }
}
