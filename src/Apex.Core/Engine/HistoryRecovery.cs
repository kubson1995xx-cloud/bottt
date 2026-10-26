using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Apex.Core.Model;

namespace Apex.Core.Engine
{
    public sealed partial class TradingEngine
    {
        private static string DealKey(ClosedPositionInfo d) => $"{d.PositionId}:{d.ClosingDealId}";
        private static bool ValidDeal(ClosedPositionInfo? d) => d != null && d.PositionId > 0
            && d.ClosingDealId is > 0 and <= int.MaxValue && !string.IsNullOrWhiteSpace(d.Symbol)
            && d.Volume > 0 && double.IsFinite(d.Volume) && d.EntryPrice > 0 && double.IsFinite(d.EntryPrice)
            && d.ExitPrice > 0 && double.IsFinite(d.ExitPrice) && d.ExitTimeUtc >= d.EntryTimeUtc
            && double.IsFinite(d.NetProfit) && double.IsFinite(d.GrossProfit) && double.IsFinite(d.Commission) && double.IsFinite(d.Swap);

        private void BlockHistory(string reason)
        {
            HistoryReady = false;
            HistoryMessage = reason;
            if (_state.Mode != EngineMode.EmergencyStopped) SetMode(EngineMode.Stopped, reason);
        }

        /// <summary>Legacy close frames are hints only. A raw deal can be accepted once;
        /// terminal accounting still requires absence from the matching positions snapshot.</summary>
        public void OnPositionClosed(ClosedPositionInfo info)
        {
            if (!string.Equals(info.Label, _cfg.Label, StringComparison.Ordinal)) return;
            if (!info.ClosingDealId.HasValue) { SynchronizeHistory(info.ExitTimeUtc); return; }
            AcceptHistory(new[] { info }, info.ExitTimeUtc, requiredCoverage: false);
        }

        private void SynchronizeHistory(DateTime utc)
        {
            if (_stateReadFault) { BlockHistory("stan odzyskiwania wymaga ręcznego uzgodnienia"); return; }
            if (!_broker.IsConnected) { BlockHistory("oczekiwanie na połączenie"); return; }
            if (_cfg.IsBacktest && _broker is not IBrokerHistory) { HistoryReady = true; return; }
            if (_broker is not IBrokerHistory history) { BlockHistory("most nie obsługuje wymaganej historii"); return; }
            try
            {
                var snapshot = history.GetHistory();
                if (!snapshot.Complete || snapshot.AccountNumber != _state.AccountNumber || string.IsNullOrWhiteSpace(snapshot.Generation))
                { BlockHistory("niepełna migawka historii"); return; }
                if (_historyGeneration.Length > 0 && snapshot.Generation != _historyGeneration)
                    BlockHistory("nowa generacja połączenia – wymagany START");
                _historyGeneration = snapshot.Generation;
                if (!AcceptHistory(snapshot.Closed, utc, snapshot.RequiredCoverage)) return;
                if (!snapshot.RequiredCoverage)
                { BlockHistory("CoverageUnknown: SDK udostępnia kolekcję dostępną, nie dowód kompletności wymaganej historii"); return; }
                var open = OwnPositions().Select(x => x.Id).ToHashSet();
                if (_plans.Keys.Any(id => !open.Contains(id)))
                { BlockHistory("brak pełnej historii wymaganej dla nieobecnej pozycji"); return; }
                HistoryReady = _persistenceHealthy && _state.ClosedOutbox.Count == 0;
                HistoryMessage = HistoryReady ? "historia uzgodniona i trwale zaakceptowana" : "oczekiwanie na trwały eksport historii";
            }
            catch (Exception ex) { BlockHistory("błąd historii: " + ex.Message); }
        }

        private bool AcceptHistory(IReadOnlyList<ClosedPositionInfo> rows, DateTime utc, bool requiredCoverage)
        {
            if (_stateReadFault) return false;
            try
            {
                // Snapshot all mutable engine state into a candidate. Do not publish counters,
                // dedupe keys or plan removal until the single authoritative state write + flush.
                var next = JsonSerializer.Deserialize<EngineState>(JsonSerializer.Serialize(_state, StateJson), StateJson)!;
                next.Plans = _plans.Values.ToList();
                next.PendingCloses = _pendingCloses.ToDictionary(x => x.Key, x => x.Value);
                next.Ledger = _ledger.Entries.ToList(); next.Risk = _risk.State;
                var known = next.ClosingDeals.ToDictionary(DealKey);
                bool changed = false;
                foreach (var row in rows)
                {
                    if (!string.Equals(row.Label, _cfg.Label, StringComparison.Ordinal)) continue;
                    if (!ValidDeal(row)) throw new InvalidOperationException("nieprawidłowy rekord closing deal");
                    string key = DealKey(row);
                    if (known.TryGetValue(key, out var previous))
                    {
                        // Reasons are close hints, not part of immutable broker economic identity.
                        if (!SameDeal(previous, row)) throw new InvalidOperationException("konflikt danych closing deal " + key);
                        continue;
                    }
                    if (next.AccountedPositions.Contains(row.PositionId))
                        throw new InvalidOperationException("nowy deal dla uprzednio rozliczonej pozycji " + row.PositionId);
                    // Unknown broker coverage cannot import arbitrary pre-install history.
                    if (!requiredCoverage && !_plans.ContainsKey(row.PositionId) && rows.Count != 1) continue;
                    known.Add(key, row); next.ClosingDeals.Add(row); next.RealizedTotal += row.NetProfit; changed = true;
                }
                var open = _broker.GetPositions().ToDictionary(x => x.Id);
                foreach (var group in next.ClosingDeals.GroupBy(x => x.PositionId))
                {
                    if (next.AccountedPositions.Contains(group.Key) || open.ContainsKey(group.Key)) continue;
                    var deals = group.OrderBy(x => x.ExitTimeUtc).ThenBy(x => x.ClosingDealId).ToList();
                    double volume = deals.Sum(x => x.Volume);
                    if (_plans.TryGetValue(group.Key, out var plan)
                        && Math.Abs(volume - plan.InitialVolume) > Math.Max(1e-8, plan.InitialVolume * 1e-8)) continue;
                    if (!requiredCoverage && !_plans.ContainsKey(group.Key) && rows.Count != 1) continue;
                    var first = deals[0]; var last = deals[deals.Count - 1];
                    var aggregate = new ClosedPositionInfo
                    {
                        PositionId = group.Key, Symbol = first.Symbol, Label = first.Label, Comment = first.Comment,
                        Direction = first.Direction, Volume = volume,
                        EntryPrice = deals.Sum(x => x.EntryPrice * x.Volume) / volume,
                        ExitPrice = deals.Sum(x => x.ExitPrice * x.Volume) / volume,
                        EntryTimeUtc = deals.Min(x => x.EntryTimeUtc), ExitTimeUtc = deals.Max(x => x.ExitTimeUtc),
                        GrossProfit = deals.Sum(x => x.GrossProfit), Commission = deals.Sum(x => x.Commission),
                        Swap = deals.Sum(x => x.Swap), NetProfit = deals.Sum(x => x.NetProfit), PlatformReason = last.PlatformReason,
                    };
                    var record = BuildClosedRecord(aggregate);
                    next.AccountedPositions.Add(group.Key); next.ClosedRecords.Add(record); next.ClosedOutbox.Add(record);
                    next.RecentTrades.Add(record); next.TradesTotal++;
                    next.Plans.RemoveAll(x => x.PositionId == group.Key); next.PendingCloses.Remove(group.Key); changed = true;
                }
                if (changed)
                {
                    next.SavedUtc = utc;
                    if (!_cfg.IsBacktest)
                    {
                        try { _store.Set(StoreKey(next.AccountNumber), JsonSerializer.Serialize(next, StateJson)); _store.Flush(); }
                        catch
                        {
                            // A throw after publication is ambiguous. Never overwrite with the old
                            // in-memory revision: only restart/readback may select the durable revision.
                            _stateReadFault = true; _persistenceHealthy = false;
                            throw;
                        }
                    }
                    _state = next;
                    foreach (var id in _plans.Keys.Where(next.AccountedPositions.Contains).ToList())
                    { _plans.Remove(id); _pendingCloses.Remove(id); _closeReasons.Remove(id); }
                    _dirty = true;
                }
                return ExportClosed(utc);
            }
            catch (Exception ex) { BlockHistory("błąd trwałego rozliczenia historii: " + ex.Message); return false; }
        }

        private static bool SameDeal(ClosedPositionInfo a, ClosedPositionInfo b) =>
            a.PositionId == b.PositionId && a.ClosingDealId == b.ClosingDealId && a.Symbol == b.Symbol && a.Label == b.Label
            && a.Comment == b.Comment && a.Direction == b.Direction && a.Volume == b.Volume && a.EntryPrice == b.EntryPrice
            && a.ExitPrice == b.ExitPrice && a.EntryTimeUtc == b.EntryTimeUtc && a.ExitTimeUtc == b.ExitTimeUtc
            && a.GrossProfit == b.GrossProfit && a.Commission == b.Commission && a.Swap == b.Swap && a.NetProfit == b.NetProfit;

        private bool ExportClosed(DateTime utc)
        {
            if (_state.ClosedOutbox.Count == 0) return true;
            try
            {
                foreach (var record in _state.ClosedOutbox) _journal.TradeClosed(record);
                // If clearing the outbox fails, the durable old outbox safely retries a keyed sink.
                _state.ClosedOutbox.Clear(); _dirty = true;
                return TrySaveState(utc, true);
            }
            catch (Exception ex)
            {
                _persistenceHealthy = false;
                BlockHistory("eksport dziennika oczekuje na ponowienie: " + ex.Message);
                return false;
            }
        }
    }
}
