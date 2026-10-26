using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Apex.Core.Bridge;
using Apex.Core.Model;

namespace Apex.Platforms.CTrader
{
    // Main-thread only. Reserve+flush BEFORE any broker mutation; never evict reservations.
    // Unknown outcomes (including a crash between send and acknowledgement) are never retried.
    internal sealed class CommandJournal
    {
        public sealed class Entry
        {
            public string Fingerprint { get; set; } = "";
            public string? Signal { get; set; }
            public ResultMsg Result { get; set; } = new();
        }
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private readonly Action<string> _persist;
        private bool _faulted;
        private string? _executingId;
        internal CommandJournal(string? saved, Action<string> persist)
        {
            _persist = persist;
            if (string.IsNullOrEmpty(saved)) return;
            try
            {
                var entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(saved, BridgeProtocol.Json);
                if (entries == null || entries.Any(e => string.IsNullOrEmpty(e.Key) || e.Value?.Result == null || string.IsNullOrEmpty(e.Value.Fingerprint) ||
                    e.Value.Result.Id != e.Key || (e.Value.Result.Status != "filled" && e.Value.Result.Status != "rejected" && e.Value.Result.Status != "uncertain")))
                    throw new JsonException("Nieprawidłowy rejestr poleceń");
                foreach (var e in entries) _entries.Add(e.Key, e.Value);
            }
            catch (Exception) { _faulted = true; }
        }
        internal ResultMsg Run(string id, string fingerprint, string? signal, Func<ResultMsg> execute)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 160 || string.IsNullOrEmpty(fingerprint)) return Reject(id, "InvalidRequest", "brak identyfikatora polecenia");
            if (_entries.TryGetValue(id, out var prior))
                return prior.Fingerprint == fingerprint ? Copy(prior.Result, id) : Reject(id, "InvalidRequest", "identyfikator użyty dla innego polecenia");
            if (signal != null)
            {
                prior = _entries.Values.FirstOrDefault(e => e.Signal == signal);
                if (prior != null) return prior.Fingerprint == fingerprint ? Copy(prior.Result, id) : Reject(id, "InvalidRequest", "sygnał już zarezerwowany z innymi parametrami");
            }
            if (_faulted || _entries.Count >= 20000) return Reject(id, "TechnicalError", "rejestr poleceń niedostępny lub pełny – wymagane uzgodnienie stanu");
            var entry = new Entry { Fingerprint = fingerprint, Signal = signal, Result = Uncertain(id, "zarezerwowano – wynik wymaga uzgodnienia") };
            _entries.Add(id, entry);
            try { Save(); }
            catch (Exception) { _faulted = true; return Reject(id, "TechnicalError", "nie zapisano rezerwacji – nie wysłano do brokera"); }
            ResultMsg result;
            try { _executingId = id; result = execute(); }
            catch (Exception ex)
            {
                var unknown = Copy(entry.Result, id);
                unknown.Message = "wyjątek wykonania: " + ex.Message;
                return unknown; // includes any durable opening checkpoint, still uncertain
            }
            finally { _executingId = null; }
            // A failed checkpoint is an ambiguous write. Never overwrite it with a later success.
            if (_faulted) return Copy(entry.Result, id);
            if (entry.Result.OpeningFill is OpeningFillEvidence original)
            {
                if (result.Status == "rejected" || (result.PositionId.HasValue && result.PositionId != original.PositionId)
                    || (result.Price.HasValue && result.Price != original.EntryPrice)
                    || (result.OpeningFill != null && BridgeProtocol.Serialize(result.OpeningFill) != BridgeProtocol.Serialize(original)))
                { _faulted = true; return Copy(entry.Result, id); }
                result = Copy(result, id);
                result.OpeningFill = original;
                result.PositionId ??= original.PositionId;
                result.Price ??= original.EntryPrice;
            }
            var reservation = entry.Result;
            entry.Result = Copy(result, id);
            try { Save(); }
            catch (Exception)
            {
                // The observation is not durably confirmed here, but must not be discarded on
                // its way to a host that can persist it. Keep uncertainty and NEVER execute again.
                reservation.PositionId = result.PositionId;
                reservation.Price = result.Price;
                reservation.OpeningFill = result.OpeningFill;
                reservation.Message = "wykonanie mogło nastąpić, ale nie zapisano wyniku";
                entry.Result = reservation;
                _faulted = true;
                return Copy(reservation, id);
            }
            return Copy(entry.Result, id);
        }

        /// <summary>Checkpoint at the SDK fill boundary, before protection repair/full close.
        /// Reservation stays uncertain until the complete operation result is committed. Called
        /// only by the currently executing command; it never grants permission to send or retry.</summary>
        internal bool RecordOpeningFill(string id, OpeningFillEvidence fill)
        {
            if (_faulted) return false;
            if (_executingId != id || !_entries.TryGetValue(id, out var entry) || fill == null
                || fill.Request == null || fill.Request.ExecutionId != id || fill.PositionId <= 0
                || !double.IsFinite(fill.InitialVolumeUnits) || fill.InitialVolumeUnits <= 0)
            { _faulted = true; return false; }
            if (entry.Result.OpeningFill != null)
            {
                if (BridgeProtocol.Serialize(entry.Result.OpeningFill) == BridgeProtocol.Serialize(fill)) return true;
                _faulted = true; return false; // original evidence is immutable, never rebase
            }
            entry.Result.PositionId = fill.PositionId;
            entry.Result.Price = fill.EntryPrice;
            entry.Result.OpeningFill = fill;
            try { Save(); return true; }
            catch (Exception) { _faulted = true; return false; }
        }
        // Pełne zamknięcie konkretnego ID pozycji nie dodaje ryzyka i jest idempotentne po stronie brokera.
        // Wywołujący MUSI ponownie sprawdzić DEMO i dokładną etykietę. Nie używać do partial close/modify/open.
        internal ResultMsg RunFullClose(string id, Func<ResultMsg> execute)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 160) return Reject(id, "InvalidRequest", "brak identyfikatora polecenia");
            try { return Copy(execute(), id); }
            catch (Exception ex) { return Uncertain(id, "niepewne pełne zamknięcie: " + ex.Message); }
        }

        private void Save() => _persist(JsonSerializer.Serialize(_entries, BridgeProtocol.Json));
        private static ResultMsg Copy(ResultMsg r, string id) => new() { Id = id, Status = r.Status, PositionId = r.PositionId, Price = r.Price, OpeningFill = r.OpeningFill, Error = r.Error, Message = r.Message };
        private static ResultMsg Uncertain(string id, string message) => new() { Id = id, Status = "uncertain", Error = "TechnicalError", Message = message };
        private static ResultMsg Reject(string id, string error, string message) => new() { Id = id, Status = "rejected", Error = error, Message = message };
    }
}
