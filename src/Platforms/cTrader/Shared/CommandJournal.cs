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
        // Terminal results have a small hot cache; permanent tombstones retain exact payload,
        // signal identity and positive recovery evidence. No replay barrier expires by age.
        internal const int ResultCacheLimit = 256;
        internal const int ReservationLimit = 20000;
        internal const int ReplayCapacity = 100000;
        private const int MaxBytes = 64 * 1024 * 1024;
        public sealed class Snapshot
        {
            public int SchemaVersion { get; set; } = 2;
            public long Generation { get; set; }
            public string Checksum { get; set; } = "";
            public Dictionary<string, Entry> Entries { get; set; } = new(StringComparer.Ordinal);
            public Dictionary<string, Entry> Tombstones { get; set; } = new(StringComparer.Ordinal);
        }
        private Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private Dictionary<string, Entry> _tombstones = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Entry> _signals = new(StringComparer.Ordinal);
        private readonly Action<string> _persist;
        private bool _faulted;
        private long _generation;
        private string? _executingId;
        internal int Count => _entries.Count + _tombstones.Count;
        internal int CachedCount => _entries.Count;
        internal bool Healthy => !_faulted;
        internal CommandJournal(string? saved, Action<string> persist)
        {
            _persist = persist;
            if (string.IsNullOrEmpty(saved)) return;
            try
            {
                if (System.Text.Encoding.UTF8.GetByteCount(saved) > MaxBytes) throw new JsonException("journal byte capacity");
                using var doc = JsonDocument.Parse(saved);
                if (doc.RootElement.TryGetProperty("schemaVersion", out var version))
                {
                    if (version.GetInt32() != 2 || !doc.RootElement.TryGetProperty("entries", out _) || !doc.RootElement.TryGetProperty("tombstones", out _))
                        throw new JsonException("unknown/incomplete journal schema");
                    var snapshot = JsonSerializer.Deserialize<Snapshot>(saved, BridgeProtocol.Json)!;
                    if (snapshot.Generation <= 0 || snapshot.Checksum != Digest(snapshot)) throw new JsonException("journal integrity/generation");
                    _generation = snapshot.Generation; _entries = snapshot.Entries; _tombstones = snapshot.Tombstones;
                }
                else _entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(saved, BridgeProtocol.Json)!; // legacy v1 dictionary
                if (_entries == null || _tombstones == null || _entries.Count + _tombstones.Count > ReplayCapacity
                    || _entries.Keys.Intersect(_tombstones.Keys, StringComparer.Ordinal).Any()) throw new JsonException("journal bounds/duplicate identity");
                foreach (var e in _entries.Concat(_tombstones))
                {
                    if (string.IsNullOrWhiteSpace(e.Key) || e.Key.Length > 160 || e.Value?.Result == null || string.IsNullOrEmpty(e.Value.Fingerprint)
                        || e.Value.Result.Id != e.Key || (e.Value.Result.Status != "filled" && e.Value.Result.Status != "rejected" && e.Value.Result.Status != "uncertain")
                        || _tombstones.ContainsKey(e.Key) && e.Value.Result.Status == "uncertain") throw new JsonException("invalid journal entry");
                    if (e.Value.Signal != null && !_signals.TryAdd(e.Value.Signal, e.Value)) throw new JsonException("duplicate signal reservation");
                }
                if (_entries.Values.Count(e => e.Result.Status == "uncertain") > ReservationLimit) throw new JsonException("reservation capacity");
            }
            catch (Exception) { _faulted = true; _entries = new(StringComparer.Ordinal); _tombstones = new(StringComparer.Ordinal); _signals.Clear(); }
        }
        internal ResultMsg Run(string id, string fingerprint, string? signal, Func<ResultMsg> execute)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 160 || string.IsNullOrEmpty(fingerprint)) return Reject(id, "InvalidRequest", "brak identyfikatora polecenia");
            if (Find(id) is Entry prior)
                return prior.Fingerprint == fingerprint ? Copy(prior.Result, id) : Reject(id, "InvalidRequest", "identyfikator użyty dla innego polecenia");
            if (signal != null && _signals.TryGetValue(signal, out var signalPrior))
                return signalPrior.Fingerprint == fingerprint ? Copy(signalPrior.Result, id) : Reject(id, "InvalidRequest", "sygnał już zarezerwowany z innymi parametrami");
            if (_faulted || _entries.Count + _tombstones.Count >= ReplayCapacity || _entries.Values.Count(e => e.Result.Status == "uncertain") >= ReservationLimit)
                return Reject(id, "TechnicalError", "rejestr poleceń niedostępny lub pełny – wymagane uzgodnienie stanu");
            var entry = new Entry { Fingerprint = fingerprint, Signal = signal, Result = Uncertain(id, "zarezerwowano – wynik wymaga uzgodnienia") };
            _entries.Add(id, entry);
            if (signal != null) _signals.Add(signal, entry);
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
            if (entry.Result.ReductionFill is ReductionFillEvidence reduction)
            {
                if (result.Status == "rejected" || result.ReductionFill != null && result.ReductionFill != reduction)
                { _faulted = true; return Copy(entry.Result, id); }
                result = Copy(result, id); result.ReductionFill = reduction;
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
                reservation.ReductionFill = result.ReductionFill;
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
        internal bool RecordReductionFill(string id, ReductionFillEvidence fill)
        {
            if (_faulted) return false;
            if (_executingId != id || !_entries.TryGetValue(id, out var entry) || fill.ActionId != id || fill.PositionId <= 0
                || !double.IsFinite(fill.BeforeUnits) || !double.IsFinite(fill.ClosedUnits) || !double.IsFinite(fill.RemainingUnits)
                || fill.BeforeUnits <= 0 || fill.ClosedUnits <= 0 || fill.RemainingUnits <= 0)
            { _faulted = true; return false; }
            if (entry.Result.ReductionFill != null)
            { if (entry.Result.ReductionFill == fill) return true; _faulted = true; return false; }
            entry.Result.PositionId = fill.PositionId; entry.Result.ReductionFill = fill;
            try { Save(); return true; } catch (Exception) { _faulted = true; return false; }
        }
        // Pełne zamknięcie konkretnego ID pozycji nie dodaje ryzyka i jest idempotentne po stronie brokera.
        // Wywołujący MUSI ponownie sprawdzić DEMO i dokładną etykietę. Nie używać do partial close/modify/open.
        internal ResultMsg RunFullClose(string id, Func<ResultMsg> execute)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 160) return Reject(id, "InvalidRequest", "brak identyfikatora polecenia");
            try { return Copy(execute(), id); }
            catch (Exception ex) { return Uncertain(id, "niepewne pełne zamknięcie: " + ex.Message); }
        }

        // Read-only positive lookup; absence is NOT a rejection and never authorizes resend.
        private Entry? Find(string id) => _entries.TryGetValue(id, out var e) ? e : _tombstones.TryGetValue(id, out e) ? e : null;
        internal ResultMsg? Lookup(string id) => Find(id) is Entry entry ? Copy(entry.Result, id) : null;
        internal OrderRequest? LookupOpeningRequest(string id)
        {
            if (Find(id) is not Entry entry || BridgeProtocol.ReadType(entry.Fingerprint) != "open") return null;
            var c = BridgeProtocol.Deserialize<OpenCmd>(entry.Fingerprint);
            var direction = BridgeProtocol.ParseSide(c?.Side);
            return c == null || direction == null ? null : new OrderRequest { ExecutionId = id, SignalId = c.SignalId ?? "", Symbol = c.Symbol,
                Direction = direction.Value, VolumeUnits = c.Units, StopLossPips = c.SlPips, TakeProfitPips = c.TpPips, Label = c.Label,
                Comment = c.Comment, MaxSlippagePips = c.MaxSlipPips };
        }
        private void Save()
        {
            if (_faulted) throw new InvalidOperationException("journal quarantined until restart");
            var hot = new Dictionary<string, Entry>(_entries, StringComparer.Ordinal);
            var cold = new Dictionary<string, Entry>(_tombstones, StringComparer.Ordinal);
            var terminal = hot.Where(e => e.Value.Result.Status != "uncertain").ToArray();
            foreach (var e in terminal.Take(Math.Max(0, terminal.Length - ResultCacheLimit)))
            {
                var minimal = Copy(e.Value.Result, e.Key); minimal.Message = null; // diagnostics only; recovery survives
                cold.Add(e.Key, new Entry { Fingerprint = e.Value.Fingerprint, Signal = e.Value.Signal, Result = minimal });
                hot.Remove(e.Key);
            }
            var snapshot = new Snapshot { Generation = checked(_generation + 1), Entries = hot, Tombstones = cold };
            snapshot.Checksum = Digest(snapshot);
            var json = JsonSerializer.Serialize(snapshot, BridgeProtocol.Json);
            if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxBytes) throw new InvalidOperationException("journal byte capacity");
            _persist(json); // caller atomically replaces + flushes the single key; ambiguous throw freezes ALL writes
            _generation = snapshot.Generation; _entries = hot; _tombstones = cold;
            _signals.Clear();
            foreach (var entry in hot.Values.Concat(cold.Values)) if (entry.Signal != null) _signals.Add(entry.Signal, entry);
        }
        private static string Digest(Snapshot s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { s.SchemaVersion, s.Generation, s.Entries, s.Tombstones }, BridgeProtocol.Json))));
        private static ResultMsg Copy(ResultMsg r, string id) => new() { Id = id, Status = r.Status, PositionId = r.PositionId, Price = r.Price, OpeningFill = r.OpeningFill, ReductionFill = r.ReductionFill, Error = r.Error, Message = r.Message };
        private static ResultMsg Uncertain(string id, string message) => new() { Id = id, Status = "uncertain", Error = "TechnicalError", Message = message };
        private static ResultMsg Reject(string id, string error, string message) => new() { Id = id, Status = "rejected", Error = error, Message = message };
    }
}
