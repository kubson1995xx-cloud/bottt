using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Apex.Core.Adaptive;
using Apex.Core.Execution;
using Apex.Core.Indicators;
using Apex.Core.Management;
using Apex.Core.Model;
using Apex.Core.Performance;
using Apex.Core.Regime;
using Apex.Core.Risk;
using Apex.Core.Safety;
using Apex.Core.Scanner;
using Apex.Core.Strategies;

namespace Apex.Core.Engine
{
    /// <summary>Informacja o zamkniętej pozycji przekazywana przez platformę.</summary>
    public sealed class ClosedPositionInfo
    {
        public long PositionId { get; init; }
        public string Symbol { get; init; } = "";
        public string Label { get; init; } = "";
        public string Comment { get; init; } = "";
        public TradeDirection Direction { get; init; }
        public double Volume { get; init; }
        public double EntryPrice { get; init; }
        public double ExitPrice { get; init; }
        public DateTime EntryTimeUtc { get; init; }
        public DateTime ExitTimeUtc { get; init; }
        public double GrossProfit { get; init; }
        public double Commission { get; init; }
        public double Swap { get; init; }
        public double NetProfit { get; init; }
        /// <summary>Powód po stronie platformy: StopLoss / TakeProfit / Closed / StopOut.</summary>
        public string PlatformReason { get; init; } = "";
    }

    /// <summary>
    /// Silnik handlowy APEX – ta sama klasa działa w cBocie cTrader, w backteście offline i w testach integracyjnych.
    /// Cykl (OnTimer): połączenie → konto → limity ryzyka → zabezpieczenie pozycji → weryfikacja niepewnych zleceń →
    /// zarządzanie pozycjami → skanowanie instrumentów → ranking okazji → wykonanie najlepszych → zapis stanu.
    /// </summary>
    public sealed class TradingEngine
    {
        private readonly EngineConfig _cfg;
        private StrategyBook _book;
        private readonly IBrokerGateway _broker;
        private readonly IMarketData _market;
        private readonly IFeatureProvider _features;
        private readonly IKeyValueStore _store;
        private readonly IEngineLog _log;
        private readonly ITradeJournal _journal;
        private readonly NewsCalendar? _news;

        private EngineState _state = new();
        private readonly ExecutionLedger _ledger = new();
        private RiskManager _risk;
        private readonly Dictionary<long, TradePlan> _plans = new();
        private readonly List<UniverseEntry> _universe = new();
        private readonly Dictionary<string, UniverseEntry> _universeBySymbol = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _lastEval = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PoolItem> _pool = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PendingInfo> _pendingInfo = new(StringComparer.Ordinal);
        private readonly Dictionary<long, string> _closeReasons = new();
        private readonly Dictionary<long, PendingClose> _pendingCloses = new();
        private readonly HashSet<string> _suspended = new(StringComparer.Ordinal);
        private readonly SpreadTracker _spreads = new();
        private readonly RingLog _recentSignals = new(12);
        private readonly RingLog _recentErrors = new(8);
        private readonly StrategyHealthMonitor _health = new();
        private readonly Dictionary<string, DateTime> _symbolCooldown = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _dataWarned = new(StringComparer.OrdinalIgnoreCase);

        private bool _initialized;
        private bool _connected = true;
        private bool _dirty;
        private DateTime _lastSaveUtc;
        private int _consecutiveErrors;
        private int _consecutiveExceptions;
        private AccountSnapshot? _lastAccount;
        private DateTime _lastPruneUtc;

        private static readonly JsonSerializerOptions StateJson = new()
        {
            Converters = { new JsonStringEnumConverter() },
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        public TradingEngine(EngineConfig cfg, StrategyBook book, IBrokerGateway broker, IMarketData market, IFeatureProvider features,
            IKeyValueStore store, IEngineLog log, ITradeJournal journal, NewsCalendar? news = null)
        {
            var err = cfg.Validate();
            if (err != null) throw new ArgumentException("Nieprawidłowa konfiguracja: " + err);
            _cfg = cfg;
            _book = book;
            _broker = broker;
            _market = market;
            _features = features;
            _store = store;
            _log = log;
            _journal = journal;
            _news = news;
            _risk = new RiskManager(cfg.Risk);
        }

        public EngineMode Mode => _state.Mode;
        public string ModeReason => _state.ModeReason;
        public bool GuardAllowed { get; private set; }
        public bool GuardIsLive { get; private set; }
        public string GuardMessage { get; private set; } = "Konto niezweryfikowane";
        public IReadOnlyList<UniverseEntry> Universe => _universe;
        public RiskManager Risk => _risk;
        public ExecutionLedger Ledger => _ledger;
        public IReadOnlyDictionary<long, TradePlan> Plans => _plans;
        public IReadOnlyList<TradeRecord> RecentTrades => _state.RecentTrades;
        public StrategyBook Book => _book;
        public EngineConfig Config => _cfg;

        private sealed class PoolItem
        {
            public SignalCandidate Candidate = null!;
            public DateTime FirstSeenUtc;
        }

        private sealed class PendingInfo
        {
            public SignalCandidate Candidate = null!;
            public double RiskAmount;
            public double Score;
            public double Units;
        }

        private sealed class PendingClose
        {
            public string Reason = "";
            public int Attempts;
            public DateTime NextTryUtc;
        }

        // ------------------------------------------------------------------ konfiguracja

        public void SetGuard(AccountGuardResult r)
        {
            GuardAllowed = r.Allowed;
            GuardIsLive = r.IsLive;
            GuardMessage = r.Reason;
        }

        public void SetUniverse(IEnumerable<UniverseEntry> entries)
        {
            _universe.Clear();
            _universeBySymbol.Clear();
            foreach (var e in entries)
            {
                if (e.AssetClass == AssetClass.Crypto || e.AssetClass == AssetClass.Unknown) continue; // podwójna blokada
                if (_universeBySymbol.ContainsKey(e.Symbol)) continue;
                _universe.Add(e);
                _universeBySymbol[e.Symbol] = e;
            }
        }

        public void ReplaceBook(StrategyBook book)
        {
            _book = book;
            _lastEval.Clear();
        }

        private string StoreKey(long account)
        {
            var sb = new StringBuilder();
            foreach (char c in _cfg.Label) if (char.IsLetterOrDigit(c)) sb.Append(c);
            string key = $"APEX {sb} {account}";
            return key.Length > 50 ? key.Substring(0, 50) : key;
        }

        // ------------------------------------------------------------------ start i odtwarzanie stanu

        /// <summary>Wczytuje zapisany stan i uzgadnia go z faktycznymi pozycjami u brokera.</summary>
        public void Initialize(DateTime utc)
        {
            var acc = _broker.GetAccount();
            _lastAccount = acc;
            if (!_cfg.IsBacktest) LoadState(acc.Number);
            _state.AccountNumber = acc.Number;
            _risk = new RiskManager(_cfg.Risk, _state.Risk);
            if (_state.RiskPerTradeOverride.HasValue)
            {
                _cfg.Risk.RiskPerTradePct = _state.RiskPerTradeOverride.Value;
                if (_cfg.Risk.Validate() != null) { _cfg.Risk.RiskPerTradePct = 0.5; _state.RiskPerTradeOverride = null; }
            }
            foreach (var s in _state.Suspended) _suspended.Add(s);
            _initialized = true;
            Reconcile(utc, "start");
            _risk.Update(acc, utc);
            _dirty = true;
            SaveState(utc, force: true);
            _log.Info($"Silnik uruchomiony. Tryb: {ModeText(_state.Mode)}. Pozycje bota: {_plans.Count}. Wpisy rejestru: {_ledger.Entries.Count}.");
        }

        private void LoadState(long account)
        {
            string? json = null;
            try { json = _store.Get(StoreKey(account)); }
            catch (Exception ex) { _log.Warn("Nie można odczytać zapisanego stanu: " + ex.Message); }
            if (string.IsNullOrWhiteSpace(json))
            {
                _state = new EngineState { AccountNumber = account };
                return;
            }
            try
            {
                var st = JsonSerializer.Deserialize<EngineState>(json!, StateJson);
                if (st == null || st.AccountNumber != account)
                {
                    _log.Warn("Zapisany stan dotyczy innego konta – start z czystym stanem.");
                    _state = new EngineState { AccountNumber = account };
                    return;
                }
                _state = st;
                _ledger.Restore(st.Ledger);
                _plans.Clear();
                foreach (var p in st.Plans) _plans[p.PositionId] = p;
                _log.Info($"Wczytano stan z {st.SavedUtc:yyyy-MM-dd HH:mm} UTC.");
            }
            catch (Exception ex)
            {
                // Uszkodzony stan: zachowujemy się bezpiecznie – start w trybie wstrzymanych wejść.
                _log.Error("Uszkodzony zapis stanu (" + ex.Message + ") – start z czystym stanem, nowe wejścia wstrzymane do decyzji użytkownika.");
                _state = new EngineState { AccountNumber = account, Mode = EngineMode.PausedNewEntries, ModeReason = "uszkodzony zapis stanu" };
            }
        }

        public void SaveState(DateTime utc, bool force = false)
        {
            if (_cfg.IsBacktest || !_dirty) return;
            if (!force && (utc - _lastSaveUtc).TotalSeconds < 5) return;
            try
            {
                _state.Ledger = _ledger.Entries.ToList();
                _state.Plans = _plans.Values.ToList();
                _state.Risk = _risk.State;
                _state.Suspended = _suspended.ToList();
                _state.SavedUtc = utc;
                if (_state.RecentTrades.Count > 300) _state.RecentTrades = _state.RecentTrades.Skip(_state.RecentTrades.Count - 300).ToList();
                _store.Set(StoreKey(_state.AccountNumber), JsonSerializer.Serialize(_state, StateJson));
                _store.Flush();
                _dirty = false;
                _lastSaveUtc = utc;
            }
            catch (Exception ex)
            {
                AddError("Zapis stanu nieudany: " + ex.Message);
            }
        }

        private bool IsOwn(PositionInfo p) => string.Equals(p.Label, _cfg.Label, StringComparison.Ordinal);

        private List<PositionInfo> OwnPositions() => _broker.GetPositions().Where(IsOwn).ToList();

        /// <summary>
        /// Uzgodnienie stanu z brokerem: przejęcie własnych pozycji bez planu (odtworzenie), usunięcie planów pozycji,
        /// które już nie istnieją. Pozycje innych botów i ręczne NIE są dotykane.
        /// </summary>
        public void Reconcile(DateTime utc, string why)
        {
            var own = OwnPositions();
            var ids = new HashSet<long>(own.Select(p => p.Id));
            foreach (var pos in own)
            {
                if (_plans.ContainsKey(pos.Id)) continue;
                var plan = ReconstructPlan(pos, utc);
                _plans[pos.Id] = plan;
                _log.Warn($"[{why}] Przejęto pozycję bota #{pos.Id} {pos.Symbol} {pos.Direction} bez zapisanego planu – plan odtworzony ({plan.Strategy}).");
                if (ExecutionLedger.TryParseComment(pos.Comment, out _, out var sid) && _ledger.Get(sid) is { } e && e.State != SignalState.Executed)
                    _ledger.MarkExecuted(sid, pos.Id, utc);
                _dirty = true;
                CloseOrphanTest(pos, utc);
            }
            foreach (var id in _plans.Keys.ToList())
            {
                if (ids.Contains(id)) continue;
                var plan = _plans[id];
                _log.Warn($"[{why}] Pozycja #{id} {plan.Symbol} nie istnieje (zamknięta podczas przerwy) – plan usunięty.");
                _journal.Event($"Pozycja #{id} {plan.Symbol} zamknięta podczas nieobecności bota (szczegóły w historii cTrader).");
                _plans.Remove(id);
                _dirty = true;
            }
        }

        /// <summary>Pozycja testu wykonania bez aktywnego testu (np. po restarcie) jest zamykana.</summary>
        private void CloseOrphanTest(PositionInfo pos, DateTime utc)
        {
            if (_test != null) return;
            if (ExecutionLedger.TryParseComment(pos.Comment, out var st, out _) && st == StrategyId.ExecutionTest && _initialized)
            {
                _log.Warn($"Osierocona pozycja testu wykonania #{pos.Id} {pos.Symbol} – zamykam.");
                RequestClose(pos.Id, "osierocona pozycja testu wykonania", utc);
            }
        }

        private TradePlan ReconstructPlan(PositionInfo pos, DateTime utc)
        {
            StrategyId sid = StrategyId.TrendPullback;
            string signal = "";
            if (ExecutionLedger.TryParseComment(pos.Comment, out var parsed, out var sg)) { sid = parsed; signal = sg; }
            var cls = _universeBySymbol.TryGetValue(pos.Symbol, out var ue) ? ue.AssetClass : AssetClass.Unknown;
            var strat = _book.ExitStrategyFor(sid, cls);
            double stop = pos.StopLoss ?? 0;
            double r = stop > 0 ? Math.Abs(pos.EntryPrice - stop) : 0;
            return new TradePlan
            {
                PositionId = pos.Id,
                SignalId = signal,
                Symbol = pos.Symbol,
                Strategy = sid,
                Variant = "odtworzony",
                Direction = pos.Direction,
                SignalTf = strat.SignalTf,
                EntryPrice = pos.EntryPrice,
                InitialStop = stop,
                InitialRisk = r,
                InitialVolume = pos.VolumeUnits,
                EntryTimeUtc = pos.EntryTimeUtc,
                BestPrice = pos.EntryPrice,
                LastManagedBarTime = utc.AddHours(-1),
                Exit = strat is StrategyBase sb ? sb.Exit : new ExitPlan(),
                Reconstructed = true,
            };
        }

        // ------------------------------------------------------------------ główny cykl

        public void OnTimer(DateTime utc)
        {
            if (!_initialized) return;
            try
            {
                Cycle(utc);
                _consecutiveExceptions = 0;
            }
            catch (Exception ex)
            {
                _consecutiveExceptions++;
                AddError($"Błąd cyklu silnika: {ex.GetType().Name}: {ex.Message}");
                if (_consecutiveExceptions >= 5 && _state.Mode == EngineMode.Running)
                    SetMode(EngineMode.PausedNewEntries, "powtarzające się błędy wewnętrzne");
            }
            SaveState(utc);
        }

        private void Cycle(DateTime utc)
        {
            bool connected = _broker.IsConnected;
            if (!connected)
            {
                if (_connected) AddError("Utracono połączenie z serwerem – wstrzymano wszystkie operacje.");
                _connected = false;
                return;
            }
            if (!_connected)
            {
                _connected = true;
                _log.Info("Połączenie przywrócone – uzgadnianie stanu pozycji.");
                Reconcile(utc, "ponowne połączenie");
            }

            var acc = _broker.GetAccount();
            if (!acc.IsValid) { AddError("Nieprawidłowe dane konta – pomijam cykl."); return; }
            if (_state.AccountNumber != 0 && acc.Number != _state.AccountNumber)
            {
                GuardAllowed = false;
                GuardMessage = $"Zmiana konta ({_state.AccountNumber} → {acc.Number}) – handel zablokowany.";
                AddError(GuardMessage);
                return;
            }
            _lastAccount = acc;
            if (!GuardAllowed) return;

            foreach (var ev in _risk.Update(acc, utc))
            {
                _log.Warn(ev);
                _journal.Event(ev);
                _dirty = true;
            }
            if (_risk.State.MaxDrawdownHit && _state.Mode != EngineMode.EmergencyStopped)
            {
                if (_cfg.Risk.CloseAllOnMaxDrawdown) CloseAllOwn(utc, "przekroczone maksymalne obsunięcie");
                SetMode(EngineMode.EmergencyStopped, "przekroczone maksymalne obsunięcie kapitału");
            }
            if (_risk.State.DailyLimitHit && _cfg.Risk.CloseAllOnDailyLimit && _plans.Count > 0)
                CloseAllOwn(utc, "przekroczony dzienny limit straty");

            var own = OwnPositions();
            EnsurePlans(own, utc);
            ProtectPositions(own, utc);
            ResolvePending(own, utc);
            ProcessPendingCloses(utc);
            ProcessExecutionTest(utc);

            if (_state.Mode == EngineMode.Running || _state.Mode == EngineMode.PausedNewEntries)
                ManagePositions(own, utc);

            SampleSpreads(utc);

            if (_state.Mode == EngineMode.Running && !_risk.NewEntriesBlocked(out _))
            {
                ScanForSignals(utc);
                ProcessPool(utc, acc);
            }
            else if (_pool.Count > 0)
            {
                _pool.Clear();
            }

            if (utc - _lastPruneUtc > TimeSpan.FromHours(6))
            {
                _ledger.Prune(utc, TimeSpan.FromDays(10));
                _lastPruneUtc = utc;
            }
        }

        private void EnsurePlans(List<PositionInfo> own, DateTime utc)
        {
            foreach (var pos in own)
            {
                if (_plans.ContainsKey(pos.Id)) continue;
                // Pozycja bota, której nie znamy: albo wynik niepewnego zlecenia (obsłuży ResolvePending), albo odtworzenie.
                if (ExecutionLedger.TryParseComment(pos.Comment, out _, out var sid) && _pendingInfo.ContainsKey(sid)) continue;
                _plans[pos.Id] = ReconstructPlan(pos, utc);
                _log.Warn($"Wykryto pozycję bota #{pos.Id} {pos.Symbol} bez planu – przejęto zarządzanie.");
                _dirty = true;
                CloseOrphanTest(pos, utc);
            }
        }

        // ------------------------------------------------------------------ bezpieczeństwo pozycji

        private void ProtectPositions(List<PositionInfo> own, DateTime utc)
        {
            foreach (var pos in own)
            {
                if (pos.StopLoss.HasValue) continue;
                if (_pendingCloses.ContainsKey(pos.Id)) continue;
                var spec = _market.GetSpec(pos.Symbol);
                var q = _market.GetQuote(pos.Symbol);
                _plans.TryGetValue(pos.Id, out var plan);
                double atr = plan?.EntryAtr ?? 0;
                if (spec == null || q == null || !q.Value.IsValid)
                {
                    AddError($"Pozycja #{pos.Id} {pos.Symbol} bez SL i bez danych – próba zamknięcia.");
                    RequestClose(pos.Id, "brak SL i brak danych rynkowych", utc);
                    continue;
                }
                double sl = PositionManagerRules.EmergencyStop(plan, pos, spec, q.Value, atr);
                var res = _broker.ModifyStopLoss(pos.Id, sl);
                if (res.Status == OrderStatus.Filled)
                {
                    _log.Warn($"Pozycja #{pos.Id} {pos.Symbol} nie miała SL – ustawiono SL {sl}.");
                    if (plan != null && plan.InitialStop <= 0)
                    {
                        plan.InitialStop = sl;
                        plan.InitialRisk = Math.Abs(pos.EntryPrice - sl);
                        _dirty = true;
                    }
                }
                else
                {
                    AddError($"Nie udało się ustawić SL dla #{pos.Id} {pos.Symbol} ({res.Message}) – zamykam pozycję.");
                    RequestClose(pos.Id, "nie można ustawić SL", utc);
                }
            }
        }

        private void RequestClose(long positionId, string reason, DateTime utc)
        {
            _closeReasons[positionId] = reason;
            var res = _broker.ClosePosition(positionId, null);
            if (res.Status == OrderStatus.Filled || res.Error == BrokerErrorKind.EntityNotFound)
            {
                _pendingCloses.Remove(positionId);
                return;
            }
            RegisterBrokerError(res);
            if (!_pendingCloses.TryGetValue(positionId, out var pc)) _pendingCloses[positionId] = pc = new PendingClose { Reason = reason };
            pc.Attempts++;
            pc.NextTryUtc = utc.AddSeconds(Math.Min(300, 15 * pc.Attempts));
            AddError($"Zamknięcie #{positionId} nieudane ({res.Message}) – ponowię próbę.");
        }

        private void ProcessPendingCloses(DateTime utc)
        {
            if (_pendingCloses.Count == 0) return;
            var existing = new HashSet<long>(OwnPositions().Select(p => p.Id));
            foreach (var kv in _pendingCloses.ToList())
            {
                if (!existing.Contains(kv.Key)) { _pendingCloses.Remove(kv.Key); continue; }
                if (utc < kv.Value.NextTryUtc) continue;
                RequestClose(kv.Key, kv.Value.Reason, utc);
            }
        }

        public void CloseAllOwn(DateTime utc, string reason)
        {
            foreach (var pos in OwnPositions())
                RequestClose(pos.Id, reason, utc);
            _log.Warn($"Zamykanie wszystkich pozycji bota: {reason}.");
            _journal.Event($"Zamknięcie wszystkich pozycji bota: {reason}");
        }

        // ------------------------------------------------------------------ niepewne zlecenia

        private void ResolvePending(List<PositionInfo> own, DateTime utc)
        {
            foreach (var e in _ledger.Pending)
            {
                var match = own.FirstOrDefault(p => ExecutionLedger.TryParseComment(p.Comment, out _, out var sid) && sid == e.SignalId);
                if (match != null)
                {
                    _ledger.MarkExecuted(e.SignalId, match.Id, utc);
                    if (!_plans.ContainsKey(match.Id))
                    {
                        if (_pendingInfo.TryGetValue(e.SignalId, out var info))
                            _plans[match.Id] = CreatePlan(match, info.Candidate, info.RiskAmount, info.Score, utc);
                        else
                            _plans[match.Id] = ReconstructPlan(match, utc);
                        _journal.TradeOpened(_plans[match.Id], match.VolumeUnits, "potwierdzona po weryfikacji");
                    }
                    _pendingInfo.Remove(e.SignalId);
                    _log.Info($"Weryfikacja: zlecenie {e.SignalId} ({e.Symbol}) zostało wykonane – pozycja #{match.Id}.");
                    _dirty = true;
                }
                else if (_broker.IsConnected && (utc - e.TimeUtc).TotalSeconds > _cfg.VerificationTimeoutSeconds)
                {
                    _ledger.MarkConsumed(e.SignalId, "brak pozycji po weryfikacji", utc);
                    _pendingInfo.Remove(e.SignalId);
                    _log.Warn($"Weryfikacja: zlecenie {e.SignalId} ({e.Symbol}) NIE zostało wykonane – sygnał wycofany (bez ponawiania).");
                    _dirty = true;
                }
            }
        }

        // ------------------------------------------------------------------ zarządzanie pozycjami

        private void ManagePositions(List<PositionInfo> own, DateTime utc)
        {
            foreach (var pos in own)
            {
                if (!_plans.TryGetValue(pos.Id, out var plan)) continue;
                if (_pendingCloses.ContainsKey(pos.Id)) continue;
                if (plan.Strategy == StrategyId.ExecutionTest) continue; // sterowane przez test wykonania
                try
                {
                    ManageOne(pos, plan, utc);
                }
                catch (Exception ex)
                {
                    AddError($"Zarządzanie #{pos.Id} {pos.Symbol}: {ex.Message}");
                }
            }
        }

        private void ManageOne(PositionInfo pos, TradePlan plan, DateTime utc)
        {
            if (!_features.TryGet(pos.Symbol, plan.SignalTf, utc, out var fs, out int i) || i < 1) return;
            var barTime = fs.Bars.Times[i];
            if (barTime <= plan.LastManagedBarTime) return;
            if (barTime < plan.EntryTimeUtc - plan.SignalTf.Duration()) return;

            var spec = _market.GetSpec(pos.Symbol);
            if (spec == null) return;
            var q = _market.GetQuote(pos.Symbol) ?? new Quote(fs.Close[i], fs.Close[i], utc);
            var cls = _universeBySymbol.TryGetValue(pos.Symbol, out var ue) ? ue.AssetClass : spec.AssetClass;
            var strat = _book.ExitStrategyFor(plan.Strategy, cls);

            if (plan.InitialRisk <= 0 && pos.StopLoss.HasValue)
            {
                plan.InitialStop = pos.StopLoss.Value;
                plan.InitialRisk = Math.Abs(plan.EntryPrice - pos.StopLoss.Value);
            }
            if (plan.EntryAtr <= 0) plan.EntryAtr = Ta.At(fs.Atr(14), i);

            var input = BuildInput(pos.Symbol, spec, strat, fs, i, utc);
            var stratExit = input == null ? StrategyExit.None : strat.CheckExit(input, plan.Direction, plan.EntryPrice, plan.BarsHeld + 1);
            int filterDir = input?.FilterRegime.TrendDirection ?? 0;
            bool filterStrong = input != null && (input.FilterRegime.Regime == MarketRegime.StrongUptrend || input.FilterRegime.Regime == MarketRegime.StrongDowntrend);

            var actions = PositionManagerRules.Evaluate(new ManagementInput
            {
                Plan = plan,
                Position = pos,
                Spec = spec,
                Quote = q,
                BarClose = fs.Close[i],
                BarHigh = fs.High[i],
                BarLow = fs.Low[i],
                BarTime = barTime,
                CurrentAtr = Ta.At(fs.Atr(14), i),
                StrategyExit = stratExit,
                FilterTrendDirection = filterDir,
                FilterRegimeStrong = filterStrong,
            });
            _dirty = true;

            foreach (var a in actions)
            {
                switch (a.Kind)
                {
                    case ManagementActionKind.Close:
                        _log.Info($"#{pos.Id} {pos.Symbol}: zamknięcie – {a.Reason}");
                        RequestClose(pos.Id, a.Reason, utc);
                        return;
                    case ManagementActionKind.PartialClose:
                        {
                            var r = _broker.ClosePosition(pos.Id, a.CloseUnits);
                            if (r.Status == OrderStatus.Filled) _log.Info($"#{pos.Id} {pos.Symbol}: {a}");
                            else { RegisterBrokerError(r); AddError($"#{pos.Id} częściowe zamknięcie nieudane: {r.Message}"); }
                            break;
                        }
                    case ManagementActionKind.ModifyStop:
                        {
                            var r = _broker.ModifyStopLoss(pos.Id, a.NewStop!.Value);
                            if (r.Status == OrderStatus.Filled) _log.Info($"#{pos.Id} {pos.Symbol}: {a}");
                            else { RegisterBrokerError(r); AddError($"#{pos.Id} zmiana SL nieudana: {r.Message}"); }
                            break;
                        }
                }
            }
        }

        private StrategyInput? BuildInput(string symbol, InstrumentSpec spec, IStrategy strat, FeatureSet fs, int i, DateTime utc)
        {
            var sigRegime = MarketRegimeDetector.Assess(fs, i, _cfg.Regime);
            FeatureSet? ff = null;
            int fi = -1;
            var filterRegime = RegimeAssessment.Unknown("brak filtra");
            if (strat.FilterTf.HasValue)
            {
                if (!_features.TryGet(symbol, strat.FilterTf.Value, utc, out var f2, out fi)) return null;
                ff = f2;
                filterRegime = MarketRegimeDetector.Assess(f2, fi, _cfg.Regime);
            }
            return new StrategyInput
            {
                Symbol = symbol,
                Spec = spec,
                Signal = fs,
                SignalIndex = i,
                Filter = ff,
                FilterIndex = fi,
                SignalRegime = sigRegime,
                FilterRegime = filterRegime,
                DecisionTimeUtc = fs.Bars.Times[i] + fs.Bars.TimeFrame.Duration(),
            };
        }

        // ------------------------------------------------------------------ skanowanie

        private void SampleSpreads(DateTime utc)
        {
            if (_cfg.IsBacktest) return;
            foreach (var u in _universe)
            {
                var q = _market.GetQuote(u.Symbol);
                if (q == null || !q.Value.IsValid) continue;
                if (!_market.IsMarketOpen(u.Symbol, utc, out _)) continue;
                _spreads.Add(u.Symbol, q.Value.Spread);
            }
        }

        private void ScanForSignals(DateTime utc)
        {
            var ownSymbols = new HashSet<string>(_plans.Values.Select(p => p.Symbol), StringComparer.OrdinalIgnoreCase);
            foreach (var u in _universe)
            {
                if (_state.DisabledClasses.Contains(u.AssetClass)) continue;
                var strategies = _book.For(u.AssetClass);
                if (strategies.Count == 0) continue;
                for (int si = 0; si < strategies.Count; si++)
                {
                    var strat = strategies[si];
                    if (_state.DisabledStrategies.Contains(strat.Id)) continue;
                    if (_suspended.Contains(StrategyHealthMonitor.Key(strat.Id, u.AssetClass))) continue;
                    // Strategie eksperymentalne – wyłącznie konta DEMO (druga warstwa ochrony obok StrategyBook).
                    if (GuardIsLive && _book.IsExperimental(strat.Id, u.AssetClass)) continue;
                    var last = _features.LastClosedBarTime(u.Symbol, strat.SignalTf, utc);
                    if (last == null) continue;
                    string key = u.Symbol + "|" + si + "|" + strat.SignalTf;
                    if (_lastEval.TryGetValue(key, out var prev) && prev >= last.Value) continue;
                    _lastEval[key] = last.Value;

                    if (ownSymbols.Contains(u.Symbol) || _ledger.HasPendingFor(u.Symbol)) continue;
                    try
                    {
                        EvaluateStrategy(u, strat, utc);
                    }
                    catch (Exception ex)
                    {
                        AddError($"Strategia {strat.Id} na {u.Symbol}: {ex.Message}");
                    }
                }
            }
        }

        private void EvaluateStrategy(UniverseEntry u, IStrategy strat, DateTime utc)
        {
            if (!_features.TryGet(u.Symbol, strat.SignalTf, utc, out var fs, out int i)) return;
            if (i + 1 < strat.RequiredSignalBars) return;
            var decisionTime = fs.Bars.Times[i] + strat.SignalTf.Duration();
            // Świeca sprzed długiej przerwy w danych (np. po rozłączeniu) – nie handlujemy starego sygnału.
            if (utc - decisionTime > TimeSpan.FromMinutes(Math.Max(20, (int)strat.SignalTf / 2.0)) && !_cfg.IsBacktest)
            {
                if (_market.IsMarketOpen(u.Symbol, utc, out _)) WarnData(u.Symbol, utc, $"dane {strat.SignalTf} nieaktualne (ostatnia świeca zamknięta {decisionTime:HH:mm} UTC)");
                return;
            }
            string? dataErr = ValidateRecentBars(fs, i, 30);
            if (dataErr != null) { WarnData(u.Symbol, utc, dataErr); return; }

            var spec = _market.GetSpec(u.Symbol);
            if (spec == null) return;
            var input = BuildInput(u.Symbol, spec, strat, fs, i, utc);
            if (input == null) return;
            var c = strat.Evaluate(input);
            if (c == null) return;
            if (_ledger.IsKnown(c.SignalId) || _pool.ContainsKey(c.SignalId)) return;
            _pool[c.SignalId] = new PoolItem { Candidate = c, FirstSeenUtc = utc };
            _journal.Signal($"{utc:yyyy-MM-dd HH:mm} SYGNAŁ {c.Symbol} {c.Direction} {c.Strategy} – {c.Reason}");
        }

        private static string? ValidateRecentBars(FeatureSet fs, int i, int n)
        {
            for (int j = Math.Max(0, i - n + 1); j <= i; j++)
            {
                if (!fs.Bars[j].IsValid) return $"niepoprawna świeca {fs.Bars.Times[j]:yyyy-MM-dd HH:mm}";
                if (j > 0 && fs.Bars.Times[j] <= fs.Bars.Times[j - 1]) return "nieuporządkowane czasy świec";
            }
            return null;
        }

        private void WarnData(string symbol, DateTime utc, string msg)
        {
            if (_dataWarned.TryGetValue(symbol, out var t) && utc - t < TimeSpan.FromHours(1)) return;
            _dataWarned[symbol] = utc;
            AddError($"{symbol}: {msg} – instrument pominięty.");
        }

        // ------------------------------------------------------------------ ranking i wykonanie

        private sealed class Ranked
        {
            public SignalCandidate C = null!;
            public InstrumentSpec Spec = null!;
            public Quote Quote;
            public SizingResult Sizing = null!;
            public ScoreBreakdown Score = null!;
            public double MaxCorr = double.NaN;
            public string? MaxCorrWith;
            public UniverseEntry U = null!;
        }

        private void ProcessPool(DateTime utc, AccountSnapshot acc)
        {
            if (_pool.Count == 0) return;
            if (!_cfg.IsBacktest)
            {
                var oldest = _pool.Values.Min(p => p.FirstSeenUtc);
                if ((utc - oldest).TotalSeconds < _cfg.CollectionWindowSeconds) return;
            }

            var own = OwnPositions();
            var ranked = new List<Ranked>();
            foreach (var kv in _pool.ToList())
            {
                var c = kv.Value.Candidate;
                string? reject = Prequalify(c, utc, acc, own, out var r, out bool keep);
                if (reject != null)
                {
                    if (!keep)
                    {
                        _pool.Remove(kv.Key);
                        LogSignal(c, null, "odrzucony: " + reject);
                    }
                    continue;
                }
                ranked.Add(r!);
                _pool.Remove(kv.Key);
            }

            int opened = 0;
            foreach (var r in ranked.OrderByDescending(x => x.Score.Total))
            {
                if (opened >= _cfg.MaxNewPositionsPerCycle) { LogSignal(r.C, r.Score, "pominięty: limit nowych pozycji w cyklu"); continue; }
                own = OwnPositions();
                var openRisks = BuildOpenRisks(own);
                // Korelacja liczona względem aktualnych pozycji (także otwartych w tym cyklu).
                (r.MaxCorr, r.MaxCorrWith) = MaxCorrelation(r.C, own, utc);
                if (!double.IsNaN(r.MaxCorr))
                    r.Score = OpportunityScorer.Score(r.C, r.Sizing.CostToRisk, r.MaxCorr, _cfg.Scoring);
                if (r.Score.Total < _cfg.Scoring.MinScore) { LogSignal(r.C, r.Score, $"odrzucony: ocena {r.Score.Total:F1} < {_cfg.Scoring.MinScore}"); continue; }

                double margin = _broker.EstimateMargin(r.C.Symbol, r.C.Direction, r.Sizing.Units);
                var proposal = new TradeProposal
                {
                    Symbol = r.C.Symbol,
                    AssetClass = r.U.AssetClass,
                    Direction = r.C.Direction,
                    RiskAmount = r.Sizing.RiskAmount,
                    Notional = r.Sizing.Notional,
                    EstimatedMargin = margin,
                    BaseCurrency = r.U.BaseCurrency,
                    QuoteCurrency = r.U.QuoteCurrency,
                    MaxSignedCorrelation = r.MaxCorr,
                    MaxCorrelationWith = r.MaxCorrWith,
                };
                var check = _risk.EvaluateNewTrade(proposal, acc, openRisks);
                if (!check.Allowed) { LogSignal(r.C, r.Score, "zablokowany przez ryzyko: " + check); continue; }
                if (Execute(r, utc)) opened++;
                acc = _broker.GetAccount();
            }
        }

        private string? Prequalify(SignalCandidate c, DateTime utc, AccountSnapshot acc, List<PositionInfo> own, out Ranked? ranked, out bool keep)
        {
            ranked = null;
            keep = false;
            if (utc > c.DecisionTimeUtc + c.Validity) return "sygnał wygasł";
            if (_ledger.IsKnown(c.SignalId)) return "sygnał już wykorzystany";
            if (own.Any(p => string.Equals(p.Symbol, c.Symbol, StringComparison.OrdinalIgnoreCase)) || _ledger.HasPendingFor(c.Symbol))
                return "pozycja bota już istnieje";
            if (_symbolCooldown.TryGetValue(c.Symbol, out var cd) && utc < cd) return "instrument w okresie karencji po błędzie";
            if (!_universeBySymbol.TryGetValue(c.Symbol, out var u)) return "instrument poza listą";
            if (u.AssetClass == AssetClass.Crypto) return "kryptowaluty wykluczone";
            var spec = _market.GetSpec(c.Symbol);
            if (spec == null) return "brak specyfikacji instrumentu";
            if (spec.AssetClass == AssetClass.Crypto || InstrumentClassifier.IsCrypto(spec.Name, spec.Description, spec.BaseAsset, spec.QuoteAsset))
                return "kryptowaluty wykluczone";
            var specErr = spec.Validate();
            if (specErr != null) return "specyfikacja: " + specErr;

            if (!_market.IsMarketOpen(c.Symbol, utc, out var ttc))
            {
                keep = c.SignalTf >= Tf.H4; // sygnały D1/H4 mogą poczekać na otwarcie rynku (w oknie ważności)
                return "rynek zamknięty";
            }
            if (ttc < TimeSpan.FromMinutes(_cfg.NoEntryMinutesBeforeClose)) return "zbyt blisko zamknięcia sesji";

            var qn = _market.GetQuote(c.Symbol);
            if (qn == null || !qn.Value.IsValid) return "brak poprawnych notowań";
            var q = qn.Value;
            if (!_cfg.IsBacktest && (utc - q.TimeUtc).TotalSeconds > _cfg.MaxQuoteAgeSeconds) return "notowania nieaktualne";

            if (_news != null && _cfg.News.Enabled)
            {
                if (_news.IsBlackout(spec, utc, _cfg.News, out var evt))
                    return $"okno wiadomości: {evt!.Currency} {evt.Title} {evt.TimeUtc:HH:mm} UTC";
                if (_cfg.News.RequireCalendar && !_news.IsFresh(utc, _cfg.News)) return "brak aktualnego kalendarza wiadomości";
            }

            double entry = q.EntryPrice(c.Direction);
            if (c.Atr <= 0) return "brak ATR";
            double drift = Math.Abs(entry - c.ReferencePrice) / c.Atr;
            if (drift > c.MaxDriftAtr) return $"cena odeszła od sygnału o {drift:F2} ATR";

            double spread = q.Spread;
            if (spread > _cfg.Risk.MaxSpreadToAtr * c.Atr) return $"spread {spread / c.Atr:P0} ATR – zbyt wysoki";
            if (_spreads.Count(c.Symbol) >= 20)
            {
                double med = _spreads.Median(c.Symbol);
                if (med > 0 && spread > _cfg.Risk.SpreadSpikeMultiple * med) return $"anomalia spreadu ({spread / med:F1}× mediany)";
            }

            double slip = _cfg.Risk.SlippageAllowanceAtr * c.Atr;
            double budget = _risk.RiskBudget(acc);
            var sizing = PositionSizer.Size(spec, budget, c.StopDistance, entry, spread, slip);
            if (!sizing.Ok) return "wielkość pozycji: " + sizing.Reason;
            if (sizing.CostToRisk > _cfg.Risk.MaxCostToRisk) return $"koszt transakcji {sizing.CostToRisk:P0} ryzyka > {_cfg.Risk.MaxCostToRisk:P0}";

            var score = OpportunityScorer.Score(c, sizing.CostToRisk, double.NaN, _cfg.Scoring);
            ranked = new Ranked { C = c, Spec = spec, Quote = q, Sizing = sizing, Score = score, U = u };
            return null;
        }

        private (double, string?) MaxCorrelation(SignalCandidate c, List<PositionInfo> own, DateTime utc)
        {
            if (own.Count == 0) return (double.NaN, null);
            if (!_features.TryGet(c.Symbol, _cfg.CorrelationTf, utc, out var fa, out int ia)) return (double.NaN, null);
            double best = double.NaN;
            string? with = null;
            foreach (var p in own)
            {
                if (!_features.TryGet(p.Symbol, _cfg.CorrelationTf, utc, out var fb, out int ib)) continue;
                double corr = CorrelationCalculator.Compute(fa, ia, fb, ib, _cfg.CorrelationLookback);
                if (double.IsNaN(corr)) continue;
                double signed = corr * (int)c.Direction * (int)p.Direction;
                if (double.IsNaN(best) || signed > best) { best = signed; with = p.Symbol; }
            }
            return (best, with);
        }

        private List<OpenRisk> BuildOpenRisks(List<PositionInfo> own)
        {
            var list = new List<OpenRisk>();
            foreach (var p in own)
            {
                var spec = _market.GetSpec(p.Symbol);
                var q = _market.GetQuote(p.Symbol);
                _universeBySymbol.TryGetValue(p.Symbol, out var u);
                double price = q != null && q.Value.IsValid ? q.Value.ExitPrice(p.Direction) : p.CurrentPrice;
                double worst = spec != null ? RiskManager.WorstCaseLoss(spec, p, price) : double.PositiveInfinity;
                double commission = spec != null ? spec.CommissionOneSide(p.VolumeUnits, price) : 0;
                list.Add(new OpenRisk
                {
                    Symbol = p.Symbol,
                    AssetClass = u?.AssetClass ?? spec?.AssetClass ?? AssetClass.Unknown,
                    Direction = p.Direction,
                    WorstCaseLoss = worst + (double.IsNaN(commission) ? 0 : commission),
                    Notional = spec != null ? PositionSizer.Notional(spec, p.VolumeUnits, price) : 0,
                    BaseCurrency = u?.BaseCurrency,
                    QuoteCurrency = u?.QuoteCurrency,
                    HasStopLoss = p.StopLoss.HasValue,
                });
            }
            return list;
        }

        private bool Execute(Ranked r, DateTime utc)
        {
            var c = r.C;
            string sid = c.SignalId;
            if (!_ledger.TryReserve(sid, c.Symbol, utc)) return false;
            _dirty = true;

            double slPips = Math.Round(r.Spec.PriceToPips(c.StopDistance), 1);
            double? tpPips = c.TakeProfitDistance.HasValue ? Math.Round(r.Spec.PriceToPips(c.TakeProfitDistance.Value), 1) : (double?)null;
            double? maxSlip = _cfg.MaxEntrySlippageAtr > 0 ? Math.Max(1, Math.Round(r.Spec.PriceToPips(_cfg.MaxEntrySlippageAtr * c.Atr), 1)) : (double?)null;
            var req = new OrderRequest
            {
                Symbol = c.Symbol,
                Direction = c.Direction,
                VolumeUnits = r.Sizing.Units,
                StopLossPips = slPips,
                TakeProfitPips = tpPips,
                Label = _cfg.Label,
                Comment = ExecutionLedger.BuildComment(c.Strategy, sid),
                SignalId = sid,
                MaxSlippagePips = maxSlip,
            };
            if (!(slPips > 0)) { _ledger.MarkConsumed(sid, "nieprawidłowy SL", utc); return false; }

            _pendingInfo[sid] = new PendingInfo { Candidate = c, RiskAmount = r.Sizing.RiskAmount, Score = r.Score.Total, Units = r.Sizing.Units };
            var outcome = _broker.OpenMarket(req);
            switch (outcome.Status)
            {
                case OrderStatus.Filled:
                    {
                        _consecutiveErrors = 0;
                        _ledger.MarkExecuted(sid, outcome.PositionId ?? 0, utc);
                        var pos = OwnPositions().FirstOrDefault(p => p.Id == outcome.PositionId);
                        if (pos == null)
                        {
                            // Serwer potwierdził, ale pozycji nie widać – sprawdzimy w kolejnym cyklu.
                            _ledger.Get(sid)!.State = SignalState.PendingVerification;
                            AddError($"{c.Symbol}: zlecenie potwierdzone, ale pozycja niewidoczna – weryfikacja.");
                            return true;
                        }
                        var plan = CreatePlan(pos, c, r.Sizing.RiskAmount, r.Score.Total, utc);
                        _plans[pos.Id] = plan;
                        _pendingInfo.Remove(sid);
                        LogSignal(c, r.Score, $"WYKONANY #{pos.Id} wol. {pos.VolumeUnits} SL {slPips} pips ryzyko {r.Sizing.RiskAmount:F2}");
                        _journal.TradeOpened(plan, pos.VolumeUnits, $"ocena {r.Score.Total:F1}; {r.Score.Describe()}");
                        if (!pos.StopLoss.HasValue)
                        {
                            AddError($"#{pos.Id} {c.Symbol} otwarta bez SL – natychmiastowe zabezpieczenie.");
                            ProtectPositions(new List<PositionInfo> { pos }, utc);
                        }
                        return true;
                    }
                case OrderStatus.Rejected:
                    _ledger.MarkConsumed(sid, outcome.Message, utc);
                    _pendingInfo.Remove(sid);
                    RegisterBrokerError(outcome);
                    _symbolCooldown[c.Symbol] = utc.AddMinutes(30);
                    LogSignal(c, r.Score, $"ODRZUCONY przez serwer: {outcome.Error} {outcome.Message}");
                    return false;
                default:
                    RegisterBrokerError(outcome);
                    LogSignal(c, r.Score, $"NIEPEWNA odpowiedź ({outcome.Error}) – weryfikuję stan przed kolejnym działaniem");
                    return false;
            }
        }

        private TradePlan CreatePlan(PositionInfo pos, SignalCandidate c, double riskAmount, double score, DateTime utc)
        {
            double stop = pos.StopLoss ?? (pos.EntryPrice - (int)c.Direction * c.StopDistance);
            return new TradePlan
            {
                PositionId = pos.Id,
                SignalId = c.SignalId,
                Symbol = c.Symbol,
                Strategy = c.Strategy,
                Variant = c.StrategyVariant,
                Direction = c.Direction,
                SignalTf = c.SignalTf,
                EntryPrice = pos.EntryPrice,
                InitialStop = stop,
                InitialRisk = Math.Abs(pos.EntryPrice - stop),
                InitialVolume = pos.VolumeUnits,
                RiskAmount = riskAmount,
                EntryTimeUtc = pos.EntryTimeUtc == default ? utc : pos.EntryTimeUtc,
                EntryAtr = c.Atr,
                Score = score,
                EntryReason = c.Reason,
                Regime = c.PrimaryRegimeName,
                BestPrice = pos.EntryPrice,
                LastManagedBarTime = c.SignalBarTime,
                Exit = c.Exit,
            };
        }

        private void RegisterBrokerError(OrderOutcome o)
        {
            if (o.Status == OrderStatus.Filled) return;
            bool technical = OrderOutcome.IsUncertainError(o.Error);
            if (!technical) return;
            _consecutiveErrors++;
            if (_consecutiveErrors >= _cfg.MaxConsecutiveErrors && _state.Mode == EngineMode.Running)
                SetMode(EngineMode.PausedNewEntries, $"{_consecutiveErrors} kolejnych błędów serwera");
        }

        private void LogSignal(SignalCandidate c, ScoreBreakdown? s, string outcome)
        {
            string line = $"{c.DecisionTimeUtc:MM-dd HH:mm} {c.Symbol} {(c.Direction == TradeDirection.Long ? "KUP" : "SPRZEDAJ")} {c.Strategy.Code()} " +
                          (s != null ? $"ocena {s.Total:F0} " : "") + "– " + outcome;
            _recentSignals.Add(line);
            _journal.Signal(line + (s != null ? " | " + s.Describe() : "") + " | " + c.Reason);
        }

        // ------------------------------------------------------------------ zamknięcia pozycji

        public void OnPositionClosed(ClosedPositionInfo info)
        {
            if (!string.Equals(info.Label, _cfg.Label, StringComparison.Ordinal)) return;
            _plans.TryGetValue(info.PositionId, out var plan);
            _pendingCloses.Remove(info.PositionId);
            string reason;
            if (_closeReasons.TryGetValue(info.PositionId, out var r)) reason = r;
            else reason = info.PlatformReason switch
            {
                "StopLoss" => plan != null && (plan.BreakEvenDone || (plan.Exit?.TrailAtrMultiple ?? 0) > 0 && plan.MfeR >= (plan.Exit?.TrailStartR ?? 99))
                    ? "SL (break-even/trailing)" : "Stop Loss",
                "TakeProfit" => "Take Profit",
                "StopOut" => "Stop-out (brak marginu)",
                _ => "zamknięta ręcznie lub poza botem",
            };
            _closeReasons.Remove(info.PositionId);

            var cls = _universeBySymbol.TryGetValue(info.Symbol, out var u) ? u.AssetClass : AssetClass.Unknown;
            var rec = new TradeRecord
            {
                PositionId = info.PositionId,
                SignalId = plan?.SignalId ?? "",
                Symbol = info.Symbol,
                AssetClass = cls,
                Strategy = plan?.Strategy ?? StrategyId.TrendPullback,
                Variant = plan?.Variant ?? "",
                TimeFrame = plan?.SignalTf ?? Tf.H1,
                Direction = info.Direction,
                EntryTimeUtc = info.EntryTimeUtc,
                ExitTimeUtc = info.ExitTimeUtc,
                EntryPrice = info.EntryPrice,
                ExitPrice = info.ExitPrice,
                Volume = plan?.InitialVolume ?? info.Volume,
                InitialRiskMoney = plan?.RiskAmount ?? 0,
                GrossProfit = info.GrossProfit,
                Commission = info.Commission,
                Swap = info.Swap,
                NetProfit = info.NetProfit,
                EntryReason = plan?.EntryReason ?? "",
                ExitReason = reason,
                Regime = plan?.Regime ?? "",
                Score = plan?.Score ?? 0,
                Parameters = plan?.Exit?.Describe() ?? "",
                BarsHeld = plan?.BarsHeld ?? 0,
            };
            _journal.TradeClosed(rec);
            _state.RecentTrades.Add(rec);
            _state.RealizedTotal += info.NetProfit;
            _state.TradesTotal++;
            _plans.Remove(info.PositionId);
            _dirty = true;
            _log.Info($"Zamknięto #{info.PositionId} {info.Symbol}: {info.NetProfit:F2} ({rec.RMultiple:F2}R) – {reason}");

            if (_cfg.UseHealthMonitor)
            {
                foreach (var msg in _health.Evaluate(_state.RecentTrades, _suspended))
                {
                    _log.Warn(msg);
                    _journal.Event(msg);
                }
            }
        }

        // ------------------------------------------------------------------ polecenia panelu

        private void SetMode(EngineMode m, string reason)
        {
            if (_state.Mode == m && _state.ModeReason == reason) return;
            _state.Mode = m;
            _state.ModeReason = reason;
            _dirty = true;
            string msg = $"Tryb: {ModeText(m)} – {reason}";
            if (m == EngineMode.EmergencyStopped || m == EngineMode.PausedNewEntries) _log.Warn(msg); else _log.Info(msg);
            _journal.Event(msg);
        }

        public string CmdStart()
        {
            if (!GuardAllowed) return "Nie można uruchomić: " + GuardMessage;
            if (_state.Mode == EngineMode.EmergencyStopped) return "Najpierw zresetuj awaryjne zatrzymanie.";
            if (_risk.State.MaxDrawdownHit) return "Najpierw zresetuj blokadę maksymalnego obsunięcia.";
            _consecutiveErrors = 0;
            SetMode(EngineMode.Running, "uruchomiono przez użytkownika");
            return "Bot uruchomiony.";
        }

        public string CmdStop()
        {
            if (_state.Mode == EngineMode.EmergencyStopped) return "Bot jest w stanie awaryjnego zatrzymania.";
            SetMode(EngineMode.Stopped, "zatrzymano przez użytkownika");
            return "Bot zatrzymany (pozycje chronione przez SL/TP u brokera).";
        }

        public string CmdPauseEntries()
        {
            if (_state.Mode != EngineMode.Running) return "Wstrzymanie możliwe tylko w trybie pracy.";
            SetMode(EngineMode.PausedNewEntries, "wstrzymano nowe transakcje (użytkownik)");
            return "Nowe transakcje wstrzymane, zarządzanie pozycjami aktywne.";
        }

        public string CmdEmergencyStop(DateTime utc, string reason)
        {
            CloseAllOwn(utc, "AWARYJNE ZATRZYMANIE: " + reason);
            _pool.Clear();
            SetMode(EngineMode.EmergencyStopped, reason);
            SaveState(utc, true);
            return "AWARYJNE ZATRZYMANIE – zamykanie pozycji bota.";
        }

        public string CmdResetEmergency(DateTime utc)
        {
            if (_state.Mode != EngineMode.EmergencyStopped) return "Bot nie jest w stanie awaryjnym.";
            if (_risk.State.MaxDrawdownHit && _lastAccount != null) _risk.ResetDrawdownLatch(_lastAccount.Equity);
            SetMode(EngineMode.Stopped, "zresetowano stan awaryjny – naciśnij START");
            SaveState(utc, true);
            return "Stan awaryjny zresetowany. Bot zatrzymany – naciśnij START, aby wznowić.";
        }

        public string CmdCloseAllOwn(DateTime utc)
        {
            CloseAllOwn(utc, "polecenie użytkownika");
            return "Zamykanie wszystkich pozycji bota (pozycje ręczne i innych botów nie są dotykane).";
        }

        public string CmdSetRisk(double pct)
        {
            var copy = _cfg.Risk.Clone();
            copy.RiskPerTradePct = Math.Round(pct, 2);
            var err = copy.Validate();
            if (err != null) return "Odrzucono: " + err;
            _cfg.Risk.RiskPerTradePct = copy.RiskPerTradePct;
            _state.RiskPerTradeOverride = copy.RiskPerTradePct;
            _dirty = true;
            _journal.Event($"Zmieniono ryzyko na transakcję: {copy.RiskPerTradePct:F2}%");
            return $"Ryzyko na transakcję: {copy.RiskPerTradePct:F2}%";
        }

        public string CmdToggleStrategy(StrategyId id, bool enabled)
        {
            if (enabled) _state.DisabledStrategies.Remove(id);
            else if (!_state.DisabledStrategies.Contains(id)) _state.DisabledStrategies.Add(id);
            _dirty = true;
            return $"{id.PolishName()}: {(enabled ? "włączona" : "wyłączona")}";
        }

        public string CmdToggleClass(AssetClass c, bool enabled)
        {
            if (c == AssetClass.Crypto) return "Kryptowaluty są trwale wykluczone.";
            if (enabled) _state.DisabledClasses.Remove(c);
            else if (!_state.DisabledClasses.Contains(c)) _state.DisabledClasses.Add(c);
            _dirty = true;
            return $"{c.PolishName()}: {(enabled ? "włączone" : "wyłączone")}";
        }

        public string CmdResumeSuspended()
        {
            int n = _suspended.Count;
            _suspended.Clear();
            _dirty = true;
            return $"Przywrócono {n} wstrzymanych par strategia/klasa.";
        }

        public bool IsStrategyEnabled(StrategyId id) => !_state.DisabledStrategies.Contains(id);
        public bool IsClassEnabled(AssetClass c) => !_state.DisabledClasses.Contains(c);

        // ------------------------------------------------------------------ test wykonania (tylko DEMO)

        private sealed class ExecTest
        {
            public string Symbol = "";
            public string SignalId = "";
            public long PositionId;
            public int Step;
            public DateTime StepTime;
            public DateTime Started;
            public double InitialStop;
            public TradeDirection Direction;
            public readonly List<string> Log = new();
        }

        private ExecTest? _test;
        public IReadOnlyList<string> LastExecutionTest { get; private set; } = Array.Empty<string>();

        /// <summary>
        /// Test ścieżki zleceń na koncie DEMO: otwarcie minimalnej pozycji z SL i TP → przesunięcie SL → zamknięcie.
        /// Każdy krok jest weryfikowany na podstawie faktycznego stanu pozycji u brokera. Niedostępny na koncie LIVE.
        /// </summary>
        public string CmdExecutionTest(DateTime utc, string? symbol)
        {
            if (!GuardAllowed) return "Test niedostępny: " + GuardMessage;
            if (GuardIsLive) return "Test wykonania jest dostępny WYŁĄCZNIE na koncie DEMO.";
            if (_state.Mode == EngineMode.EmergencyStopped) return "Bot w stanie awaryjnym.";
            if (_test != null) return "Test już trwa.";
            var own = OwnPositions();
            var candidates = string.IsNullOrWhiteSpace(symbol)
                ? _universe.OrderBy(u => u.AssetClass == AssetClass.Forex ? 0 : 1).Select(u => u.Symbol)
                : new[] { symbol!.Trim() };
            foreach (var sym in candidates)
            {
                if (own.Any(p => string.Equals(p.Symbol, sym, StringComparison.OrdinalIgnoreCase))) continue;
                var spec = _market.GetSpec(sym);
                var q = _market.GetQuote(sym);
                if (spec == null || spec.Validate() != null || q == null || !q.Value.IsValid) continue;
                if (spec.AssetClass == AssetClass.Crypto) continue;
                if (!_market.IsMarketOpen(sym, utc, out _)) continue;
                double atr = 0;
                if (_features.TryGet(sym, Tf.H1, utc, out var fs, out int i)) atr = Ta.At(fs.Atr(14), i);
                if (!(atr > 0)) atr = q.Value.Mid * 0.002;
                double slPips = Math.Max(spec.MinStopDistancePips + 1, Math.Round(spec.PriceToPips(1.5 * atr), 1));
                double tpPips = Math.Round(slPips * 2, 1);
                string sid = SignalIdGenerator.Create(sym, StrategyId.ExecutionTest, TradeDirection.Long, utc);
                var t = new ExecTest { Symbol = sym, SignalId = sid, Started = utc, StepTime = utc, Direction = TradeDirection.Long };
                t.Log.Add($"{utc:HH:mm:ss} START testu na {sym}: wolumen min. {spec.VolumeMin}, SL {slPips} pips, TP {tpPips} pips");
                if (!_ledger.TryReserve(sid, sym, utc)) return "Test z tym identyfikatorem już był wykonany – spróbuj za minutę.";
                var res = _broker.OpenMarket(new OrderRequest
                {
                    Symbol = sym, Direction = TradeDirection.Long, VolumeUnits = spec.VolumeMin, StopLossPips = slPips, TakeProfitPips = tpPips,
                    Label = _cfg.Label, Comment = ExecutionLedger.BuildComment(StrategyId.ExecutionTest, sid), SignalId = sid,
                });
                if (res.Status == OrderStatus.Rejected)
                {
                    _ledger.MarkConsumed(sid, res.Message, utc);
                    t.Log.Add($"{utc:HH:mm:ss} BŁĄD: serwer odrzucił zlecenie ({res.Error}: {res.Message})");
                    LastExecutionTest = t.Log.ToList();
                    return "Test nieudany: zlecenie odrzucone – " + res.Message;
                }
                t.Step = 1;
                t.PositionId = res.PositionId ?? 0;
                t.Log.Add($"{utc:HH:mm:ss} Krok 1: odpowiedź serwera {res.Status} (pozycja #{t.PositionId}) – weryfikuję stan.");
                _test = t;
                LastExecutionTest = t.Log.ToList();
                _journal.Event("TEST WYKONANIA: start na " + sym);
                return $"Test wykonania rozpoczęty na {sym}. Postęp w panelu.";
            }
            return "Brak instrumentu spełniającego warunki testu (otwarty rynek, poprawna specyfikacja, brak pozycji bota).";
        }

        private void ProcessExecutionTest(DateTime utc)
        {
            var t = _test;
            if (t == null) return;
            var own = OwnPositions();
            var pos = own.FirstOrDefault(p => ExecutionLedger.TryParseComment(p.Comment, out var sidStrat, out var sid) && sidStrat == StrategyId.ExecutionTest && sid == t.SignalId);
            void Finish(string verdict)
            {
                t.Log.Add($"{utc:HH:mm:ss} {verdict} (czas {(utc - t.Started).TotalSeconds:F0} s)");
                LastExecutionTest = t.Log.ToList();
                _journal.Event("TEST WYKONANIA: " + verdict);
                _log.Info("Test wykonania: " + verdict);
                _test = null;
            }
            switch (t.Step)
            {
                case 1:
                    if (pos != null)
                    {
                        _ledger.MarkExecuted(t.SignalId, pos.Id, utc);
                        t.PositionId = pos.Id;
                        if (!_plans.ContainsKey(pos.Id))
                            _plans[pos.Id] = new TradePlan { PositionId = pos.Id, SignalId = t.SignalId, Symbol = pos.Symbol, Strategy = StrategyId.ExecutionTest, Direction = pos.Direction, EntryPrice = pos.EntryPrice, InitialStop = pos.StopLoss ?? 0, InitialRisk = pos.StopLoss.HasValue ? Math.Abs(pos.EntryPrice - pos.StopLoss.Value) : 0, InitialVolume = pos.VolumeUnits, EntryTimeUtc = pos.EntryTimeUtc, EntryReason = "test wykonania", Exit = new ExitPlan() };
                        if (!pos.StopLoss.HasValue) { t.Log.Add($"{utc:HH:mm:ss} BŁĄD: pozycja bez SL – zamykam."); RequestClose(pos.Id, "test: brak SL", utc); Finish("TEST NIEZALICZONY – pozycja otwarta bez SL"); return; }
                        t.InitialStop = pos.StopLoss.Value;
                        t.Log.Add($"{utc:HH:mm:ss} Krok 1 OK: pozycja #{pos.Id} otwarta po {pos.EntryPrice}, SL {pos.StopLoss}, TP {pos.TakeProfit}, wolumen {pos.VolumeUnits}");
                        t.Step = 2;
                        t.StepTime = utc;
                    }
                    else if ((utc - t.StepTime).TotalSeconds > _cfg.VerificationTimeoutSeconds)
                    {
                        _ledger.MarkConsumed(t.SignalId, "test: brak pozycji", utc);
                        Finish("TEST NIEZALICZONY – pozycja nie pojawiła się po otwarciu");
                    }
                    break;
                case 2:
                    if (pos == null) { Finish("TEST PRZERWANY – pozycja zniknęła przed zmianą SL (np. trafiony SL/TP lub zamknięta ręcznie)"); return; }
                    if ((utc - t.StepTime).TotalSeconds < 20) return;
                    {
                        double newSl = pos.EntryPrice - 0.75 * (pos.EntryPrice - t.InitialStop);
                        var spec = _market.GetSpec(pos.Symbol);
                        if (spec != null) newSl = spec.RoundPrice(newSl);
                        var r = _broker.ModifyStopLoss(pos.Id, newSl);
                        t.Log.Add($"{utc:HH:mm:ss} Krok 2: zmiana SL {t.InitialStop} → {newSl}: {r.Status} {r.Message}");
                        t.InitialStop = newSl;
                        t.Step = 3;
                        t.StepTime = utc;
                    }
                    break;
                case 3:
                    if (pos == null) { Finish("TEST PRZERWANY – pozycja zniknęła przed weryfikacją SL"); return; }
                    if (pos.StopLoss.HasValue && Math.Abs(pos.StopLoss.Value - t.InitialStop) <= Math.Abs(t.InitialStop) * 1e-6 + 1e-9)
                    {
                        t.Log.Add($"{utc:HH:mm:ss} Krok 2 OK: SL potwierdzony u brokera ({pos.StopLoss}).");
                        t.Step = 4;
                        t.StepTime = utc;
                    }
                    else if ((utc - t.StepTime).TotalSeconds > _cfg.VerificationTimeoutSeconds)
                    {
                        t.Log.Add($"{utc:HH:mm:ss} UWAGA: SL u brokera = {pos.StopLoss}, oczekiwano {t.InitialStop} – zamykam pozycję.");
                        t.Step = 4;
                    }
                    break;
                case 4:
                    if (pos == null) { Finish("TEST ZALICZONY – pozycja zamknięta"); return; }
                    if ((utc - t.StepTime).TotalSeconds < 20) return;
                    t.Log.Add($"{utc:HH:mm:ss} Krok 3: zamykanie pozycji #{pos.Id}.");
                    RequestClose(pos.Id, "test wykonania – zamknięcie", utc);
                    t.Step = 5;
                    t.StepTime = utc;
                    break;
                case 5:
                    if (pos == null) Finish("TEST ZALICZONY – otwarcie, zmiana SL i zamknięcie potwierdzone u brokera");
                    else if ((utc - t.StepTime).TotalSeconds > _cfg.VerificationTimeoutSeconds) Finish("TEST NIEZALICZONY – pozycja nadal otwarta po próbie zamknięcia (ponawiam zamykanie w tle)");
                    break;
            }
            if (_test != null) LastExecutionTest = t.Log.ToList();
        }

        // ------------------------------------------------------------------ status

        public static string ModeText(EngineMode m) => m switch
        {
            EngineMode.Running => "PRACA",
            EngineMode.PausedNewEntries => "WSTRZYMANE NOWE WEJŚCIA",
            EngineMode.Stopped => "ZATRZYMANY",
            EngineMode.EmergencyStopped => "AWARYJNE ZATRZYMANIE",
            _ => m.ToString(),
        };

        public EngineStatus GetStatus(DateTime utc)
        {
            var acc = _lastAccount;
            var own = new List<string>();
            double lev = 0;
            try
            {
                var positions = OwnPositions();
                foreach (var p in positions)
                {
                    _plans.TryGetValue(p.Id, out var plan);
                    double rNow = plan != null && plan.InitialRisk > 0 ? (int)p.Direction * (p.CurrentPrice - plan.EntryPrice) / plan.InitialRisk : 0;
                    own.Add($"{p.Symbol} {(p.Direction == TradeDirection.Long ? "KUP" : "SPRZ")} {p.VolumeUnits:G6} | {p.NetProfit:F2} ({rNow:F2}R) | {(plan != null ? plan.Strategy.Code() : "?")} | SL {(p.StopLoss.HasValue ? p.StopLoss.Value.ToString("G8", CultureInfo.InvariantCulture) : "BRAK!")}");
                }
                if (acc != null && acc.Equity > 0) lev = BuildOpenRisks(positions).Sum(o => Math.Abs(o.Notional)) / acc.Equity;
            }
            catch (Exception ex)
            {
                own.Add("błąd odczytu pozycji: " + ex.Message);
            }

            var rs = _risk.State;
            double equity = acc?.Equity ?? 0;
            double daily = rs.DayStartEquity > 0 ? equity - rs.DayStartEquity : 0;
            double total = rs.InitialEquity > 0 ? equity - rs.InitialEquity : 0;
            string news = _news == null || !_cfg.News.Enabled ? "wyłączony"
                : _news.LastUpdateUtc == DateTime.MinValue ? "brak danych"
                : $"{_news.Events.Count} wydarzeń, aktualizacja {_news.LastUpdateUtc:MM-dd HH:mm} UTC" + (_news.IsFresh(utc, _cfg.News) ? "" : " (NIEAKTUALNY)");
            var next = _news?.Upcoming(utc, 1, _cfg.News.MinImpact).FirstOrDefault();
            if (next != null) news += $"; następne: {next.Currency} {next.Title} {next.TimeUtc:MM-dd HH:mm} UTC";

            return new EngineStatus
            {
                Mode = _state.Mode,
                ModeReason = _state.ModeReason,
                IsLive = GuardIsLive,
                GuardAllowed = GuardAllowed,
                GuardMessage = GuardMessage,
                Connected = _connected,
                AccountNumber = acc?.Number ?? 0,
                Broker = acc?.BrokerName ?? "",
                Currency = acc?.Currency ?? "",
                Balance = acc?.Balance ?? 0,
                Equity = equity,
                DailyPnl = daily,
                DailyPnlPct = rs.DayStartEquity > 0 ? daily / rs.DayStartEquity * 100 : 0,
                TotalPnl = total,
                TotalPnlPct = rs.InitialEquity > 0 ? total / rs.InitialEquity * 100 : 0,
                DrawdownPct = _risk.DrawdownPct,
                RiskPerTradePct = _cfg.Risk.RiskPerTradePct,
                MaxDailyLossPct = _cfg.Risk.MaxDailyLossPct,
                MaxDrawdownPct = _cfg.Risk.MaxDrawdownPct,
                EffectiveLeverage = lev,
                MaxEffectiveLeverage = _cfg.Risk.MaxEffectiveLeverage,
                DailyLimitHit = rs.DailyLimitHit,
                MaxDrawdownHit = rs.MaxDrawdownHit,
                OpenPositions = own,
                OwnPositionCount = own.Count,
                MaxPositions = _cfg.Risk.MaxOpenPositions,
                UniverseByClass = _universe.GroupBy(x => x.AssetClass).ToDictionary(g => g.Key, g => g.Count()),
                RecentSignals = _recentSignals.Items,
                RecentErrors = _recentErrors.Items,
                ActiveStrategies = _book.Classes.SelectMany(c => _book.For(c).Select(s => $"{c.PolishName()}: {s.Id.PolishName()} ({s.SignalTf}{(s.FilterTf.HasValue ? "/" + s.FilterTf : "")})")).ToList(),
                Suspended = _suspended.ToList(),
                DisabledStrategies = _state.DisabledStrategies.ToList(),
                DisabledClasses = _state.DisabledClasses.ToList(),
                NewsStatus = news,
                TradesTotal = _state.TradesTotal,
                RealizedTotal = _state.RealizedTotal,
            };
        }

        private void AddError(string msg)
        {
            _recentErrors.Add($"{DateTime.UtcNow:HH:mm:ss} {msg}");
            _log.Error(msg);
        }
    }
}
