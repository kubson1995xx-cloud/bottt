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

        private static double FiniteSum(IEnumerable<double> values)
        {
            double total = 0;
            foreach (double value in values)
            {
                total += value;
                if (!double.IsFinite(value) || !double.IsFinite(total)) throw new InvalidOperationException("nieskończona suma finansowa/wolumen");
            }
            return total;
        }

        private static void ValidateFinancialState(EngineState state)
        {
            if (!double.IsFinite(state.RealizedTotal)
                || new[] { state.Risk.DayStartEquity, state.Risk.PeakEquity, state.Risk.InitialEquity }.Any(x => !double.IsFinite(x))
                || state.Plans.Any(p => new[] { p.InitialVolume, p.RiskAmount, p.InitialRisk, p.EntryPrice, p.InitialStop }.Any(x => !double.IsFinite(x))
                    || p.InitialVolume < 0 || p.RiskAmount < 0 || (p.CoveredLifetimeVolume.HasValue && (!double.IsFinite(p.CoveredLifetimeVolume.Value) || p.CoveredLifetimeVolume.Value <= 0)))
                || state.ClosedRecords.Concat(state.ClosedOutbox).Concat(state.RecentTrades).Any(r => r == null || !r.HasFiniteEconomics()))
                throw new InvalidOperationException("nieprawidłowe kwoty/wolumen w stanie finansowym");
            FiniteSum(state.ClosingDeals.Select(x => x.NetProfit));
            FiniteSum(state.ClosingDeals.Select(x => x.GrossProfit));
            FiniteSum(state.ClosingDeals.Select(x => x.Commission));
            FiniteSum(state.ClosingDeals.Select(x => x.Swap));
            foreach (var group in state.ClosingDeals.GroupBy(x => x.PositionId)) FiniteSum(group.Select(x => x.Volume));
            FiniteSum(state.ClosedRecords.Select(x => x.NetProfit));
            FiniteSum(state.ClosedRecords.Select(x => x.GrossProfit));
            FiniteSum(state.ClosedRecords.Select(x => x.Commission));
            FiniteSum(state.ClosedRecords.Select(x => x.Swap));
        }

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
            // Preserve the existing offline backtester callback contract. Its simulator emits a
            // terminal aggregate, has no durable history capability and never uses a real broker.
            // Production bridge/native compositions implement IBrokerHistory and cannot enter here.
            if (_cfg.IsBacktest && _broker is not IBrokerHistory)
            {
                if (!_state.AccountedPositions.Add(info.PositionId)) return;
                var record = BuildClosedRecord(info); _journal.TradeClosed(record);
                _state.RecentTrades.Add(record); _state.ClosedRecords.Add(record);
                _state.RealizedTotal += info.NetProfit; _state.TradesTotal++;
                _plans.Remove(info.PositionId); _pendingCloses.Remove(info.PositionId); _closeReasons.Remove(info.PositionId);
                EvaluateHealth();
                return;
            }
            SynchronizeHistory(info.ExitTimeUtc);
        }

        /// <summary>Durably accept a completed snapshot captured by the authorized session at
        /// receipt time. This does not grant readiness/START to a replaced connection.</summary>
        public bool ReplayHistory(BrokerHistorySnapshot snapshot, DateTime utc)
        {
            if (!snapshot.Complete || snapshot.Positions == null || snapshot.AccountNumber != _state.AccountNumber) return false;
            return AcceptHistory(snapshot.Closed, snapshot.Positions, utc, snapshot.RequiredCoverage);
        }

        private void SynchronizeHistory(DateTime utc)
        {
            if (_stateReadFault) { BlockHistory("stan odzyskiwania wymaga ręcznego uzgodnienia"); return; }
            if (!_cfg.IsBacktest && (_journal is not IRecoveryJournal durable || !durable.IsHealthy))
            { BlockHistory("dziennik odzyskiwania niedostępny"); return; }
            if (!_broker.IsConnected) { BlockHistory("oczekiwanie na połączenie"); return; }
            if (_cfg.IsBacktest && _broker is not IBrokerHistory) { HistoryReady = true; return; }
            if (_broker is not IBrokerHistory history) { BlockHistory("most nie obsługuje wymaganej historii"); return; }
            try
            {
                var snapshot = history.GetHistory();
                if (snapshot.Synchronizing && snapshot.AccountNumber == _state.AccountNumber && snapshot.Generation == _historyGeneration)
                { HistoryReady = false; HistoryMessage = "uzgadnianie bieżącej rewizji pozycji"; return; }
                if (!snapshot.Complete || snapshot.Positions == null || snapshot.AccountNumber != _state.AccountNumber || string.IsNullOrWhiteSpace(snapshot.Generation))
                { BlockHistory("niepełna migawka historii"); return; }
                if (_historyGeneration.Length > 0 && snapshot.Generation != _historyGeneration)
                    BlockHistory("nowa generacja połączenia – wymagany START");
                _historyGeneration = snapshot.Generation;
                if (!ReplayHistory(snapshot, utc)) return;
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

        private bool AcceptHistory(IReadOnlyList<ClosedPositionInfo> rows, IReadOnlyList<PositionInfo> positions, DateTime utc, bool requiredCoverage)
        {
            if (_stateReadFault) return false;
            try
            {
                // Snapshot all mutable engine state into a candidate. Do not publish counters,
                // dedupe keys or plan removal until the single authoritative state write + flush.
                var next = JsonSerializer.Deserialize<EngineState>(JsonSerializer.Serialize(_state, StateJson), StateJson)!;
                next.Plans = JsonSerializer.Deserialize<List<Apex.Core.Management.TradePlan>>(JsonSerializer.Serialize(_plans.Values, StateJson), StateJson)!;
                next.PendingCloses = _pendingCloses.ToDictionary(x => x.Key, x => x.Value);
                next.Ledger = _ledger.Entries.ToList(); next.Risk = _risk.State;
                ValidateFinancialState(next);
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
                    if (!requiredCoverage && (!_plans.TryGetValue(row.PositionId, out var tracked) || tracked.Reconstructed)) continue;
                    known.Add(key, row); next.ClosingDeals.Add(row); next.RealizedTotal += row.NetProfit; changed = true;
                }
                var open = positions.ToDictionary(x => x.Id);
                // Reconstructed remaining volume is not measured original volume/risk. Only a
                // trusted complete source can establish closed-so-far + remaining for this lifecycle.
                if (requiredCoverage)
                    foreach (var reconstructed in next.Plans.Where(x => x.Reconstructed))
                        if (open.TryGetValue(reconstructed.PositionId, out var remaining))
                        {
                            var priorDeals = next.ClosingDeals.Where(x => x.PositionId == reconstructed.PositionId).ToList();
                            if (remaining.Label != _cfg.Label || remaining.Symbol != reconstructed.Symbol || remaining.Direction != reconstructed.Direction
                                || priorDeals.Any(x => x.Symbol != remaining.Symbol || x.Direction != remaining.Direction))
                                throw new InvalidOperationException("niespójny cykl odtworzonej pozycji");
                            double lifetime = FiniteSum(priorDeals.Select(x => x.Volume).Append(remaining.VolumeUnits));
                            if (reconstructed.CoveredLifetimeVolume is double priorVolume
                                && Math.Abs(lifetime - priorVolume) > Math.Max(1e-8, priorVolume * 1e-8))
                                throw new InvalidOperationException("niespójny pełny wolumen odtworzonej pozycji");
                            if (!reconstructed.CoveredLifetimeVolume.HasValue)
                            { reconstructed.CoveredLifetimeVolume = lifetime; changed = true; }
                        }
                foreach (var group in next.ClosingDeals.GroupBy(x => x.PositionId))
                {
                    if (next.AccountedPositions.Contains(group.Key) || open.ContainsKey(group.Key)) continue;
                    var deals = group.OrderBy(x => x.ExitTimeUtc).ThenBy(x => x.ClosingDealId).ToList();
                    double volume = FiniteSum(deals.Select(x => x.Volume));
                    var plan = next.Plans.FirstOrDefault(x => x.PositionId == group.Key);
                    double? expected = plan == null ? null : plan.Reconstructed ? plan.CoveredLifetimeVolume : plan.InitialVolume;
                    if (plan?.Reconstructed == true && !requiredCoverage) continue;
                    if (expected.HasValue && Math.Abs(volume - expected.Value) > Math.Max(1e-8, expected.Value * 1e-8)) continue;
                    if (!requiredCoverage && !_plans.ContainsKey(group.Key)) continue;
                    var first = deals[0]; var last = deals[deals.Count - 1];
                    var aggregate = new ClosedPositionInfo
                    {
                        PositionId = group.Key, Symbol = first.Symbol, Label = first.Label, Comment = first.Comment,
                        Direction = first.Direction, Volume = volume,
                        EntryPrice = FiniteSum(deals.Select(x => x.EntryPrice * x.Volume)) / volume,
                        ExitPrice = FiniteSum(deals.Select(x => x.ExitPrice * x.Volume)) / volume,
                        EntryTimeUtc = deals.Min(x => x.EntryTimeUtc), ExitTimeUtc = deals.Max(x => x.ExitTimeUtc),
                        GrossProfit = FiniteSum(deals.Select(x => x.GrossProfit)), Commission = FiniteSum(deals.Select(x => x.Commission)),
                        Swap = FiniteSum(deals.Select(x => x.Swap)), NetProfit = FiniteSum(deals.Select(x => x.NetProfit)), PlatformReason = last.PlatformReason,
                    };
                    var record = BuildClosedRecord(aggregate);
                    next.AccountedPositions.Add(group.Key); next.ClosedRecords.Add(record); next.ClosedOutbox.Add(record);
                    next.RecentTrades.Add(record); next.TradesTotal++;
                    next.Plans.RemoveAll(x => x.PositionId == group.Key); next.PendingCloses.Remove(group.Key); changed = true;
                }
                ValidateFinancialState(next); // before tolerance-based consistency or authoritative publication
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
                    foreach (var plan in next.Plans)
                        if (_plans.TryGetValue(plan.PositionId, out var livePlan)) livePlan.CoveredLifetimeVolume = plan.CoveredLifetimeVolume;
                    foreach (var id in _plans.Keys.Where(next.AccountedPositions.Contains).ToList())
                    { _plans.Remove(id); _pendingCloses.Remove(id); _closeReasons.Remove(id); }
                    _dirty = true;
                    EvaluateHealth();
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

        private void EvaluateHealth()
        {
            if (!_cfg.UseHealthMonitor) return;
            foreach (var message in _health.Evaluate(_state.RecentTrades, _suspended))
            { _log.Warn(message); _journal.Event(message); }
        }

        private bool ExportClosed(DateTime utc)
        {
            try
            {
                if (!_projectionReconciled && _journal is IRecoveryJournal recovery)
                {
                    recovery.Reconcile(_state.ClosedRecords);
                    _projectionReconciled = true;
                }
                if (_state.ClosedOutbox.Count == 0) return true;
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
