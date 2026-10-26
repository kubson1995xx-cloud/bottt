using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Adaptive;
using Apex.Core.Bridge;
using Apex.Core.Engine;
using Apex.Core.Model;
using Apex.Core.Performance;
using Apex.Core.Regime;
using Apex.Core.Risk;
using Apex.Core.Safety;
using Apex.Desktop.Bridge;
using Apex.Desktop.Services;

namespace Apex.Desktop.Runtime
{
    /// <summary>
    /// Środowisko pracy jednego konta: osobny silnik APEX na własnym wątku, kolejka poleceń z panelu,
    /// migawka stanu dla API, historia kapitału, dziennik i stan trwały. Silnik nie jest współdzielony między kontami.
    /// </summary>
    public sealed class AccountRuntime : IDisposable
    {
        private readonly ConfigStore _config;
        private readonly NewsCalendar _news;
        private readonly AppLog _log;
        private readonly string _dir;
        private readonly SessionAdapter _adapter;
        private readonly FileJournal _journal;
        private readonly FileStore _store;
        private readonly ConcurrentQueue<Action> _commands = new();
        private readonly object _lifecycle = new();
        private readonly ConcurrentDictionary<long, Action> _cancelCommands = new();
        private long _commandSequence;
        private bool _emergencyLatched;
        private volatile bool _storageFault;
        private volatile bool _sessionReady;
        private AccountDto? _identity;
        private readonly AutoResetEvent _wake = new(false);
        private readonly Thread _thread;
        private volatile bool _stop;
        private TradingEngine? _engine;
        private List<UniverseCandidate> _universe = new();
        private readonly List<(DateTime t, double eq)> _equity = new();
        private DateTime _lastEquitySample;
        private string? _testSymbol;
        private volatile object? _status;
        private string _lastBuildError = "";
        private readonly DecisionRecorder _decisions = new(800);

        public AccountRuntime(string key, string platform, ConfigStore config, NewsCalendar news, AppLog log)
        {
            Key = key;
            Platform = platform;
            _config = config;
            _news = news;
            _log = log.For(key);
            var safe = new string(key.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
            _dir = Path.Combine(config.DataDir, "konta", safe);
            Directory.CreateDirectory(_dir);
            _journal = new FileJournal(Path.Combine(_dir, "dziennik"));
            _store = new FileStore(Path.Combine(_dir, "stan"));
            _adapter = new SessionAdapter(Commission, () => !_stop && _sessionReady && !_storageFault && !_store.HasFault && !_emergencyLatched);
            LoadEquity();
            _thread = new Thread(Loop) { IsBackground = true, Name = "APEX " + key };
            _thread.Start();
        }

        public string Key { get; }
        public string Platform { get; }
        public SessionAdapter Adapter => _adapter;
        public object? Status => _status;
        public FileJournal Journal => _journal;
        public IReadOnlyList<UniverseCandidate> Universe => _universe;

        private AccountSettings Settings => _config.Account(Key);

        private CommissionOverride? Commission()
        {
            var acc = Settings.Commission;
            if (acc != null) return acc;
            return Platform.Equals("MT5", StringComparison.OrdinalIgnoreCase) || Platform.Equals("MetaTrader5", StringComparison.OrdinalIgnoreCase)
                ? _config.Config.Mt5DefaultCommission
                : null;
        }

        // ------------------------------------------------------------------ sesje

        public bool MatchesIdentity(AccountDto a)
        {
            lock (_lifecycle) return _identity == null || (_identity.Number == a.Number && _identity.Broker == a.Broker
                && _identity.Currency == a.Currency && _identity.IsLive == a.IsLive && _identity.Hedging == a.Hedging);
        }

        public void Attach(BridgeSession s)
        {
            lock (_lifecycle)
            {
                if (_stop) return;
                if (s.Account == null || !MatchesIdentity(s.Account)) throw new InvalidOperationException("Zmiana tożsamości konta.");
                _identity ??= s.Account;
                _sessionReady = false;
                _adapter.Attach(s);
            }
            Enqueue(() =>
            {
                if (!ReferenceEquals(_adapter.Session, s)) return;
                if (_engine == null) TryBuildEngine();
                else { _engine.CmdStop(); _engine.SaveState(DateTime.UtcNow, true); Subscribe(); }
                _sessionReady = true;
            });
        }

        public void Detach(BridgeSession s)
        {
            _adapter.Detach(s);
            lock (_lifecycle) { if (!_stop) _wake.Set(); }
        }

        public void OnSymbols(BridgeSession s) => Enqueue(() =>
        {
            if (!ReferenceEquals(_adapter.Session, s)) return;
            if (_engine == null) TryBuildEngine();
            else { RecomputeUniverse(); Subscribe(); }
        });

        public void OnClosed(BridgeSession s, ClosedDto c)
        {
            // Uprawnienie zdarzenia oceniamy w chwili odbioru, nie po późniejszym reconnect.
            lock (_lifecycle)
            {
                if (_stop || !ReferenceEquals(_adapter.Session, s)) return;
                Enqueue(() => ApplyClosed(c));
            }
        }

        public void OnClosed(ClosedDto c) => Enqueue(() => ApplyClosed(c));

        private void ApplyClosed(ClosedDto c)
        {
            _engine?.OnPositionClosed(new ClosedPositionInfo
            {
                PositionId = c.Id,
                Symbol = c.Symbol,
                Label = c.Label,
                Comment = c.Comment,
                Direction = BridgeProtocol.ParseSide(c.Side) ?? TradeDirection.Long,
                Volume = c.Units,
                EntryPrice = c.Entry,
                ExitPrice = c.Exit,
                EntryTimeUtc = BridgeProtocol.FromUnixMs(c.EntryTime),
                ExitTimeUtc = BridgeProtocol.FromUnixMs(c.ExitTime),
                GrossProfit = c.Gross,
                Commission = c.Commission,
                Swap = c.Swap,
                NetProfit = c.Net,
                PlatformReason = c.Reason,
            });
        }

        // ------------------------------------------------------------------ polecenia

        private void Enqueue(Action a)
        {
            lock (_lifecycle)
            {
                if (_stop) return;
                _commands.Enqueue(a);
                _wake.Set();
            }
        }

        /// <summary>Polecenie oczekujące ponad 30 s jest anulowane. Rozpoczęte polecenie zwraca faktyczny wynik, nie pozorny timeout.</summary>
        public Task<string> Command(Func<TradingEngine?, string> f) => Command(f, CancellationToken.None);

        public Task<string> Command(Func<TradingEngine?, string> f, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<string>(cancellationToken);
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            int claimed = 0;
            long id = Interlocked.Increment(ref _commandSequence);
            var commandSession = _adapter.Session;
            CancellationTokenRegistration cancellation = default;
            lock (_lifecycle)
            {
                if (_stop) return Task.FromException<string>(new ObjectDisposedException(nameof(AccountRuntime)));
                _cancelCommands[id] = () =>
                {
                    if (Interlocked.CompareExchange(ref claimed, 1, 0) == 0)
                        tcs.TrySetException(new ObjectDisposedException(nameof(AccountRuntime)));
                };
                cancellation = cancellationToken.Register(() =>
                {
                    if (Interlocked.CompareExchange(ref claimed, 1, 0) == 0)
                    {
                        _cancelCommands.TryRemove(id, out _);
                        tcs.TrySetCanceled(cancellationToken);
                    }
                });
                Enqueue(() =>
                {
                    if (Interlocked.CompareExchange(ref claimed, 1, 0) != 0) return;
                    _cancelCommands.TryRemove(id, out _);
                    try { tcs.TrySetResult(ReferenceEquals(commandSession, _adapter.Session)
                        ? f(_engine) : "Polecenie anulowane: zmieniło się połączenie konta."); }
                    catch (Exception ex) { tcs.TrySetResult("Błąd: " + ex.Message); }
                });
            }
            _ = Expire();
            return tcs.Task;

            async Task Expire()
            {
                try
                {
                    await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false);
                    if (!tcs.Task.IsCompleted && Interlocked.CompareExchange(ref claimed, 1, 0) == 0)
                    {
                        _cancelCommands.TryRemove(id, out _);
                        tcs.TrySetException(new TimeoutException("Polecenie anulowane przed wykonaniem."));
                    }
                }
                finally { cancellation.Dispose(); }
            }
        }

        public string ExecuteAction(TradingEngine? e, string action, JsonElement args)
        {
            // Zatrzymanie awaryjne zapisujemy nawet bez gotowego silnika i przed operacjami brokera.
            if (action == "unlockLive") return "Handel LIVE jest wyłączony globalnie i trwale w wersji DEMO.";
            if (action == "emergency")
            {
                _emergencyLatched = true;
                string? error = null;
                try { _store.Set("desktop-control", "{\"EmergencyStopped\":true}"); }
                catch (Exception ex) { _storageFault = true; error = ex.Message; }
                var result = e?.CmdEmergencyStop(DateTime.UtcNow, "przycisk AWARYJNY STOP w panelu")
                    ?? "AWARYJNE ZATRZYMANIE zapisane – oczekiwanie na silnik.";
                return error == null ? result : "Błąd trwałego zapisu Emergency Stop: " + error;
            }
            if (action == "stop" && e == null) return "Bot zatrzymany – silnik nie jest gotowy.";
            if (e == null) return "Silnik konta nie jest jeszcze gotowy (czekam na dane z mostu).";
            e.SetGuard(EvaluateGuard());
            var now = DateTime.UtcNow;
            switch (action)
            {
                case "start": return _storageFault || _store.HasFault
                    ? "Nie można uruchomić: błąd trwałego stanu – wymagane uzgodnienie i restart." : e.CmdStart();
                case "stop":
                    var stopped = e.CmdStop();
                    e.SaveState(now, true);
                    return stopped;
                case "pause": return e.CmdPauseEntries();
                case "resetEmergency":
                    _store.Set("desktop-control", "{\"EmergencyStopped\":false}");
                    _emergencyLatched = false;
                    return e.CmdResetEmergency(now);
                case "closeAll": return e.CmdCloseAllOwn(now);
                case "resumeSuspended": return e.CmdResumeSuspended();
                case "executionTest":
                    {
                        if (_storageFault || _store.HasFault) return "Test zablokowany: błąd trwałego stanu.";
                        string? sym = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("symbol", out var s) ? s.GetString() : null;
                        return e.CmdExecutionTest(now, string.IsNullOrWhiteSpace(sym) ? _testSymbol : sym);
                    }
                case "setRisk":
                    {
                        if (!TryNum(args, "value", out double v)) return "Brak wartości.";
                        string r = e.CmdSetRisk(v);
                        return r;
                    }
                case "setLimits":
                    {
                        var copy = e.Config.Risk.Clone();
                        if (TryNum(args, "maxDailyLossPct", out var d)) copy.MaxDailyLossPct = d;
                        if (TryNum(args, "maxDrawdownPct", out var dd)) copy.MaxDrawdownPct = dd;
                        if (TryNum(args, "maxOpenPositions", out var mp)) copy.MaxOpenPositions = (int)mp;
                        if (TryNum(args, "maxEffectiveLeverage", out var lv)) copy.MaxEffectiveLeverage = lv;
                        if (TryNum(args, "riskPerTradePct", out var rp)) copy.RiskPerTradePct = rp;
                        var err = copy.Validate();
                        if (err != null) return "Odrzucono: " + err;
                        _config.Update(c => { if (!c.Accounts.TryGetValue(Key, out var a)) c.Accounts[Key] = a = new AccountSettings(); a.Risk = copy.Clone(); });
                        var target = e.Config.Risk;
                        target.MaxDailyLossPct = copy.MaxDailyLossPct;
                        target.MaxDrawdownPct = copy.MaxDrawdownPct;
                        target.MaxOpenPositions = copy.MaxOpenPositions;
                        target.MaxEffectiveLeverage = copy.MaxEffectiveLeverage;
                        e.CmdSetRisk(copy.RiskPerTradePct);
                        return $"Limity zapisane: ryzyko {copy.RiskPerTradePct:F2}%, dzienny {copy.MaxDailyLossPct:F2}%, DD {copy.MaxDrawdownPct:F2}%, pozycje {copy.MaxOpenPositions}, dźwignia {copy.MaxEffectiveLeverage:F1}x";
                    }
                case "toggleStrategy":
                    {
                        if (!args.TryGetProperty("strategy", out var st) || !Enum.TryParse<StrategyId>(st.GetString(), out var sid)) return "Nieznana strategia.";
                        bool en = args.TryGetProperty("enabled", out var enEl) && enEl.ValueKind == JsonValueKind.True;
                        return e.CmdToggleStrategy(sid, en);
                    }
                case "toggleClass":
                    {
                        if (!args.TryGetProperty("assetClass", out var ac) || !Enum.TryParse<AssetClass>(ac.GetString(), out var cls)) return "Nieznana klasa.";
                        bool en = args.TryGetProperty("enabled", out var enEl) && enEl.ValueKind == JsonValueKind.True;
                        return e.CmdToggleClass(cls, en);
                    }
                case "setUniverse":
                    {
                        var u = JsonSerializer.Deserialize<UniverseSettings>(JsonSerializer.Serialize(Settings.Universe ?? new UniverseSettings(), ConfigStore.Json), ConfigStore.Json)!;
                        if (args.TryGetProperty("mode", out var m)) u.Mode = m.GetString() == "manual" ? "manual" : "auto";
                        if (args.TryGetProperty("symbols", out var list) && list.ValueKind == JsonValueKind.Array)
                            u.Manual = list.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).Take(100).ToList();
                        _config.Update(c => { if (!c.Accounts.TryGetValue(Key, out var a)) c.Accounts[Key] = a = new AccountSettings(); a.Universe = u; });
                        RecomputeUniverse();
                        Subscribe();
                        return $"Lista instrumentów: tryb {u.Mode}, wybrano {_universe.Count(x => x.Selected)}.";
                    }
                case "setCommission":
                    {
                        if (!args.TryGetProperty("kind", out var k) || !TryNum(args, "value", out var cv) || cv < 0 || cv > 100) return "Nieprawidłowa prowizja.";
                        var co = new CommissionOverride { Kind = k.GetString() ?? "UsdPerLot", Value = cv };
                        if (SpecMapper.ParseCommission(co.Kind) == CommissionKind.Unknown) return "Nieznany typ prowizji.";
                        _config.Update(c => { if (!c.Accounts.TryGetValue(Key, out var a)) c.Accounts[Key] = a = new AccountSettings(); a.Commission = co; });
                        return $"Prowizja ustawiona: {co.Kind} {co.Value}";
                    }
                case "lockLive":
                    {
                        _config.Update(c => { if (c.Accounts.TryGetValue(Key, out var a)) { a.AllowLive = false; a.LiveConfirmation = ""; } });
                        return "Konto LIVE zablokowane.";
                    }
                default:
                    return "Nieznane polecenie.";
            }
        }

        private static bool TryNum(JsonElement args, string name, out double v)
        {
            v = 0;
            if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el)) return false;
            if (el.ValueKind == JsonValueKind.Number) { v = el.GetDouble(); return !double.IsNaN(v) && !double.IsInfinity(v); }
            if (el.ValueKind == JsonValueKind.String) return double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
            return false;
        }

        // ------------------------------------------------------------------ budowa silnika

        private AccountGuardResult EvaluateGuard()
        {
            var acc = _adapter.GetAccount();
            var cfg = _config.Config;
            if (!_adapter.IsConnected) return new AccountGuardResult { Allowed = false, IsLive = acc.IsLive, Reason = "Brak świeżych migawek konta i pozycji." };
            var guardSettings = new AccountGuardSettings
            {
                AllowLive = false,
                LiveConfirmation = "",
                AllowedAccounts = Array.Empty<long>(),
                AllowedBrokers = cfg.AllowedBrokers,
            };
            // To uprawnienie do znanego konta DEMO, nie zgoda na nowe ryzyko.
            // Kapitał/persistencja blokują wejścia osobno, nie awaryjne pełne zamknięcia.
            return AccountGuard.Evaluate(acc, guardSettings, requireTradingCapital: false);
        }

        private StrategyProfile LoadProfile()
        {
            string? path = _config.Config.ProfilePath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    var p = StrategyProfile.FromJson(File.ReadAllText(path!));
                    var errors = StrategyBook.Validate(p);
                    if (errors.Count == 0) return p;
                    _log.Error("Profil strategii odrzucony (" + string.Join("; ", errors) + ") – używam profilu wbudowanego.");
                }
                catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
                {
                    _log.Error("Nie można wczytać profilu strategii: " + ex.Message + " – używam profilu wbudowanego.");
                }
            }
            return DefaultProfile.Load();
        }

        private void TryBuildEngine()
        {
            var s = _adapter.Session;
            if (_storageFault || s == null || s.Account == null || !s.SymbolsReceived || !s.HasFreshSnapshots) return;
            try
            {
                ValidatePersistentState(s.Account.Number);
                var guard = EvaluateGuard();
                var acc = _adapter.GetAccount();
                var profile = LoadProfile();
                var book = new StrategyBook(profile, acc.IsLive);
                var cfg = _config.Config;
                var accSettings = Settings;
                var risk = (accSettings.Risk ?? cfg.Risk).Clone();
                if (risk.Validate() != null) risk = new RiskSettings();
                var engineCfg = new EngineConfig
                {
                    Label = cfg.Label,
                    Risk = risk,
                    Scoring = new Apex.Core.Scanner.ScoringSettings { MinScore = accSettings.MinScore ?? cfg.MinScore },
                    News = cfg.News,
                };
                var engine = new TradingEngine(engineCfg, book, _adapter, _adapter, _adapter, _store, _log, _journal, _news);
                engine.Decisions = _decisions; // pasywny rejestrator dla AI Decision Center (tylko odczyt)
                engine.SetGuard(guard);
                engine.Initialize(DateTime.UtcNow);
                if (_emergencyLatched) engine.CmdEmergencyStop(DateTime.UtcNow, "odtworzony AWARYJNY STOP");
                else if (engine.Mode == EngineMode.Running) engine.CmdStop();
                engine.SaveState(DateTime.UtcNow, true);
                _engine = engine;
                RecomputeUniverse();
                Subscribe();
                _log.Info($"Silnik konta gotowy: {(acc.IsLive ? "LIVE" : "DEMO")} {acc.Number} ({acc.BrokerName}), strategie aktywne dla klas: {string.Join(", ", book.Classes)}. Strażnik: {guard.Reason}");
                _lastBuildError = "";
            }
            catch (Exception ex)
            {
                _lastBuildError = ex.Message;
                _log.Error("Nie udało się uruchomić silnika konta: " + ex.Message);
            }
        }

        private void ValidatePersistentState(long account)
        {
            try
            {
                var control = _store.Get("desktop-control");
                if (control != null)
                {
                    using var doc = JsonDocument.Parse(control);
                    _emergencyLatched = doc.RootElement.GetProperty("EmergencyStopped").GetBoolean();
                }
                string label = new string(_config.Config.Label.Where(char.IsLetterOrDigit).ToArray());
                string key = $"APEX {label} {account}";
                if (key.Length > 50) key = key.Substring(0, 50);
                var json = _store.Get(key);
                if (json == null) return;
                var state = JsonSerializer.Deserialize<EngineState>(json, ConfigStore.Json);
                using var parsed = JsonDocument.Parse(json);
                if (state == null || state.AccountNumber != account || state.SchemaVersion != 1
                    || !Enum.IsDefined(state.Mode) || state.Risk == null || state.Ledger == null || state.Plans == null
                    || state.Suspended == null || state.RecentTrades == null || state.DisabledStrategies == null || state.DisabledClasses == null
                    || state.Ledger.Any(e => e == null || string.IsNullOrWhiteSpace(e.SignalId) || string.IsNullOrWhiteSpace(e.Symbol) || !Enum.IsDefined(e.State))
                    || state.Ledger.Select(e => e.SignalId).Distinct(StringComparer.Ordinal).Count() != state.Ledger.Count
                    || state.Plans.Any(p => p == null || p.PositionId <= 0 || string.IsNullOrWhiteSpace(p.Symbol))
                    || state.Plans.Select(p => p.PositionId).Distinct().Count() != state.Plans.Count
                    || state.RecentTrades.Any(t => t == null) || state.Suspended.Any(t => t == null)
                    || !parsed.RootElement.TryGetProperty("AccountNumber", out _)
                    || !parsed.RootElement.TryGetProperty("Risk", out _)
                    || !parsed.RootElement.TryGetProperty("Ledger", out _))
                    throw new InvalidDataException("Uszkodzony lub obcy stan konta; brak automatycznego odtwarzania z kopii.");
            }
            catch
            {
                _storageFault = true;
                throw;
            }
        }

        private void RecomputeUniverse()
        {
            var s = _adapter.Session;
            if (s == null || _engine == null) return;
            var settings = Settings.Universe ?? _config.Config.Universe;
            var classes = new HashSet<AssetClass>(_engine.Book.Classes);
            _universe = UniverseSelector.Select(s.Symbols, settings, classes, new HashSet<AssetClass>());
            _engine.SetUniverse(_universe.Where(u => u.Selected).Select(u =>
            {
                var dto = s.Symbol(u.Symbol);
                var legs = InstrumentClassifier.CurrencyLegs(u.AssetClass, u.Symbol, dto?.BaseAsset, dto?.QuoteAsset);
                return new UniverseEntry { Symbol = u.Symbol, AssetClass = u.AssetClass, BaseCurrency = legs.Base, QuoteCurrency = legs.Quote };
            }));
            // Test nie omija dopuszczonego uniwersum silnika ani klasyfikacji instrumentów.
            _testSymbol = _universe.Where(u => u.Selected)
                .OrderBy(u => u.AssetClass == AssetClass.Forex ? 0 : 1)
                .Select(u => u.Symbol).FirstOrDefault();
        }

        private void Subscribe()
        {
            var s = _adapter.Session;
            if (s == null || !s.IsAlive) return;
            var symbols = _universe.Where(u => u.Selected).Select(u => u.Symbol).ToList();
            if (_testSymbol != null && !symbols.Contains(_testSymbol)) symbols.Add(_testSymbol);
            var tfs = new HashSet<string> { "H1", "H4", "D1" };
            if (_engine != null)
                foreach (var st in _engine.Book.All)
                {
                    tfs.Add(st.SignalTf.ToString());
                    if (st.FilterTf.HasValue) tfs.Add(st.FilterTf.Value.ToString());
                }
            var msg = new SubscribeMsg
            {
                Symbols = symbols,
                Tfs = tfs.ToList(),
                History = tfs.ToDictionary(t => t, t => t switch { "D1" => 420, "H4" => 900, "H1" => 1500, _ => 1500 }),
            };
            s.Send(msg);
            _log.Info($"Subskrypcja: {symbols.Count} instrumentów, interwały {string.Join(",", msg.Tfs)}.");
        }

        // ------------------------------------------------------------------ pętla

        private void Loop()
        {
            while (!_stop)
            {
                _wake.WaitOne(TimeSpan.FromSeconds(2));
                while (!_stop && _commands.TryDequeue(out var cmd))
                {
                    try { cmd(); }
                    catch (Exception ex) { _log.Error("Polecenie: " + ex.Message); }
                }
                if (_stop) break;
                try
                {
                    var now = DateTime.UtcNow;
                    if (_engine != null)
                    {
                        _engine.SetGuard(EvaluateGuard());
                        _engine.OnTimer(now);
                        SampleEquity(now);
                    }
                    else if (_adapter.Session?.SymbolsReceived == true)
                    {
                        TryBuildEngine();
                    }
                    _status = BuildStatus(now);
                }
                catch (Exception ex)
                {
                    _log.Error("Pętla konta: " + ex.Message);
                }
            }
            SaveAtShutdown();
        }

        private void SaveAtShutdown()
        {
            _engine?.CmdStop();
            _engine?.SaveState(DateTime.UtcNow, true);
        }

        private void SampleEquity(DateTime now)
        {
            var acc = _adapter.GetAccount();
            if (!acc.IsValid || !_adapter.IsConnected) return;
            if ((now - _lastEquitySample).TotalMinutes < 5 && _equity.Count > 0) return;
            _lastEquitySample = now;
            lock (_equity)
            {
                _equity.Add((now, acc.Equity));
                if (_equity.Count > 4000) _equity.RemoveRange(0, _equity.Count - 4000);
            }
            try
            {
                AtomicFile.Write(Path.Combine(_dir, "kapital.json"),
                    JsonSerializer.Serialize(_equity.Select(x => new[] { BridgeProtocol.ToUnixMs(x.t), Math.Round(x.eq, 2) }).ToList()));
            }
            catch (IOException) { }
        }

        private void LoadEquity()
        {
            try
            {
                string p = Path.Combine(_dir, "kapital.json");
                if (!File.Exists(p)) return;
                var arr = JsonSerializer.Deserialize<List<double[]>>(File.ReadAllText(p));
                if (arr == null) return;
                foreach (var x in arr) if (x.Length == 2) _equity.Add((BridgeProtocol.FromUnixMs((long)x[0]), x[1]));
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException) { }
        }

        private object BuildStatus(DateTime now)
        {
            var s = _adapter.Session;
            var st = _engine?.GetStatus(now);
            var trades = _journal.AllRecent().Where(t => t.Strategy != StrategyId.ExecutionTest).ToList();
            var stats = PerformanceAnalyzer.Compute("razem", trades, st?.Equity > 0 ? st.Equity - trades.Sum(t => t.NetProfit) : 10000);
            List<object> eq;
            lock (_equity) eq = _equity.Skip(Math.Max(0, _equity.Count - 600)).Select(x => (object)new { t = BridgeProtocol.ToUnixMs(x.t), v = Math.Round(x.eq, 2) }).ToList();
            var profileEntries = _engine?.Book.Profile.Entries.Select(e => (object)new
            {
                strategy = e.Strategy.ToString(),
                name = e.Strategy.PolishName(),
                assetClass = e.AssetClass.ToString(),
                assetClassName = e.AssetClass.PolishName(),
                status = e.Enabled ? e.Status.ToString() : "Rejected",
                evidence = e.Evidence,
            }).ToList() ?? new List<object>();

            return new
            {
                key = Key,
                platform = Platform,
                connected = _adapter.IsConnected,
                session = s == null ? null : new
                {
                    id = s.Id,
                    transport = s.Transport,
                    bridgeVersion = s.BridgeVersion,
                    connectedUtc = s.ConnectedUtc,
                    lastMessageUtc = s.LastMessageUtc,
                    symbols = s.Symbols.Count,
                    quotes = s.QuoteCount,
                    bridgeLog = s.RecentLog(10),
                },
                account = s?.Account,
                positions = s?.Positions ?? Array.Empty<PositionDto>(),
                engineReady = _engine != null,
                buildError = _lastBuildError,
                engine = st,
                modeText = st != null ? TradingEngine.ModeText(st.Mode) : "OCZEKIWANIE NA DANE",
                equity = eq,
                stats = new
                {
                    trades = stats.Trades,
                    winRate = stats.WinRate,
                    profitFactor = double.IsInfinity(stats.ProfitFactor) ? 999 : stats.ProfitFactor,
                    expectancyR = stats.ExpectancyR,
                    netProfit = stats.NetProfit,
                    maxDrawdownPct = stats.MaxDrawdownPct,
                    sharpe = stats.Sharpe,
                    avgWin = stats.AvgWin,
                    avgLoss = stats.AvgLoss,
                },
                recentTrades = _journal.RecentTrades(30).Select(t => new
                {
                    id = t.PositionId, symbol = t.Symbol, strategy = t.Strategy.PolishName(), dir = t.Direction == TradeDirection.Long ? "KUP" : "SPRZEDAJ",
                    entry = t.EntryTimeUtc, exit = t.ExitTimeUtc, net = t.NetProfit, r = t.RMultiple, reason = t.ExitReason, entryReason = t.EntryReason,
                }).ToList(),
                events = _journal.RecentEvents(20),
                profile = profileEntries,
                profileVersion = _engine?.Book.Profile.Version ?? "",
                universe = _universe.Select(u => new { symbol = u.Symbol, assetClass = u.AssetClass.ToString(), className = u.AssetClass.PolishName(), selected = u.Selected, reason = u.Reason, description = u.Description }).ToList(),
                universeMode = (Settings.Universe ?? _config.Config.Universe).Mode,
                executionTest = _engine?.LastExecutionTest ?? Array.Empty<string>(),
                testSymbol = _testSymbol,
                commission = Commission(),
                liveFeature = false,
                requiredLivePhrase = (string?)null,
            };
        }

        // ------------------------------------------------------------------ AI Decision Center (tylko odczyt)

        private static readonly StrategyId[] AllStrategies =
        {
            StrategyId.TrendPullback, StrategyId.DonchianBreakout, StrategyId.MeanReversion,
            StrategyId.MomentumImpulse, StrategyId.IndexDipBuy, StrategyId.DailyTrend,
        };

        /// <summary>Migawka dla AI Decision Center: nowe zdarzenia decyzyjne, skaner rynku i liczniki strategii.</summary>
        public object DecisionsSnapshot(long since)
        {
            var (events, lastSeq) = _decisions.Since(since);
            var s = _adapter.Session;
            var engine = _engine;
            var st = _status;
            var scanner = _decisions.Scanner().ToDictionary(x => x.Symbol, StringComparer.OrdinalIgnoreCase);
            var ownSymbols = new HashSet<string>(_adapter.GetPositions().Where(p => engine != null && p.Label == engine.Config.Label).Select(p => p.Symbol), StringComparer.OrdinalIgnoreCase);
            var instruments = new List<object>();
            foreach (var u in _universe.Where(x => x.Selected))
            {
                scanner.TryGetValue(u.Symbol, out var sc);
                var q = s?.Quote(u.Symbol);
                double[] closes = Array.Empty<double>();
                double changePct = double.NaN;
                if (s != null && s.Bars.TryGet(u.Symbol, Tf.H1, DateTime.UtcNow, out var fs, out int i) && i >= 1)
                {
                    int from = Math.Max(0, i - 47);
                    closes = fs.Close.Skip(from).Take(i - from + 1).ToArray();
                    int j = Math.Max(0, i - 24);
                    if (fs.Close[j] > 0) changePct = (fs.Close[i] / fs.Close[j] - 1) * 100;
                }
                instruments.Add(new
                {
                    symbol = u.Symbol,
                    assetClass = u.AssetClass.ToString(),
                    className = u.AssetClass.PolishName(),
                    bid = q?.q.B,
                    ask = q?.q.A,
                    marketOpen = q?.q.O ?? false,
                    quoteAgeSec = q.HasValue ? (DateTime.UtcNow - q.Value.received).TotalSeconds : (double?)null,
                    closes,
                    change24h = double.IsNaN(changePct) ? (double?)null : Math.Round(changePct, 3),
                    hasPosition = ownSymbols.Contains(u.Symbol),
                    lastVerdict = sc?.LastVerdict.ToString(),
                    lastStage = sc?.LastStage.ToString(),
                    lastOutcome = sc?.LastOutcome,
                    lastStrategy = sc?.LastStrategy.ToString(),
                    lastEvaluationUtc = sc?.LastEvaluationUtc,
                    evaluations = sc?.Evaluations ?? 0,
                    regime = sc?.Regime,
                });
            }
            var counters = _decisions.Counters();
            var profile = engine?.Book.Profile.Entries ?? new List<ProfileEntry>();
            var strategies = AllStrategies.Select(id =>
            {
                var entries = profile.Where(e => e.Strategy == id).ToList();
                var activeClasses = engine == null ? new List<string>() : engine.Book.Classes.Where(c => engine.Book.For(c).Any(x => x.Id == id)).Select(c => c.PolishName()).ToList();
                bool userDisabled = engine != null && !engine.IsStrategyEnabled(id);
                string status = activeClasses.Count > 0 ? (userDisabled ? "UserDisabled" : entries.Any(e => e.Enabled && e.Status == ValidationStatus.Validated) ? "Validated" : "Experimental") : "Rejected";
                counters.TryGetValue(id, out var k);
                return new
                {
                    id = id.ToString(),
                    code = id.Code(),
                    name = id.PolishName(),
                    status,
                    activeClasses,
                    evidence = entries.FirstOrDefault(e => e.Enabled && e.Status != ValidationStatus.Rejected)?.Evidence ?? entries.FirstOrDefault()?.Evidence ?? "",
                    counters = k ?? new StrategyCounters(),
                };
            }).ToList();
            return new
            {
                lastSeq,
                startedUtc = _decisions.StartedUtc,
                lastCycleUtc = _decisions.LastCycleUtc,
                cycles = _decisions.Cycles,
                connected = _adapter.IsConnected,
                engineReady = engine != null,
                events,
                instruments,
                strategies,
                scoring = engine == null ? null : new
                {
                    minScore = engine.Config.Scoring.MinScore,
                    weights = new
                    {
                        mtfAlignment = engine.Config.Scoring.WeightMtfAlignment,
                        trendStrength = engine.Config.Scoring.WeightTrendStrength,
                        momentum = engine.Config.Scoring.WeightMomentum,
                        volatility = engine.Config.Scoring.WeightVolatility,
                        cost = engine.Config.Scoring.WeightCost,
                        rewardRisk = engine.Config.Scoring.WeightRewardRisk,
                        regimeFit = engine.Config.Scoring.WeightRegimeFit,
                    },
                    totalWeight = engine.Config.Scoring.TotalWeight,
                },
                risk = st,
            };
        }

        /// <summary>Szczegóły instrumentu do wykresu: świece H1 i EMA liczone z tych samych danych co w silniku.</summary>
        public object? InstrumentDetail(string symbol)
        {
            var s = _adapter.Session;
            if (s == null || !_universe.Any(u => u.Selected && string.Equals(u.Symbol, symbol, StringComparison.OrdinalIgnoreCase))) return null;
            var now = DateTime.UtcNow;
            object? Series(Tf tf, int count)
            {
                if (!s.Bars.TryGet(symbol, tf, now, out var fs, out int i) || i < 1) return null;
                int from = Math.Max(0, i - count + 1);
                var ema50 = fs.Ema(50);
                var ema200 = fs.Ema(200);
                var reg = MarketRegimeDetector.Assess(fs, i, new RegimeSettings());
                var bars = new List<double?[]>();
                for (int j = from; j <= i; j++)
                    bars.Add(new double?[] { BridgeProtocol.ToUnixMs(fs.Bars.Times[j]), fs.Open[j], fs.High[j], fs.Low[j], fs.Close[j], Clean(ema50[j]), Clean(ema200[j]) });
                return new { tf = tf.ToString(), bars, regime = RegimeSnapshot.From(reg, tf), atr = Clean(Apex.Core.Indicators.Ta.At(fs.Atr(14), i)) };
            }
            return new { symbol, h1 = Series(Tf.H1, 160), h4 = Series(Tf.H4, 120), d1 = Series(Tf.D1, 120) };
        }

        private static double? Clean(double v) => double.IsNaN(v) || double.IsInfinity(v) ? null : Math.Round(v, 8);

        public void Dispose()
        {
            lock (_lifecycle)
            {
                if (_stop) return;
                _stop = true;
                foreach (var cancel in _cancelCommands.Values) cancel();
                _cancelCommands.Clear();
                _wake.Set();
            }
            // Samo zatrzymanie pętli nie anuluje operacji oczekujących na blokadę transportu.
            var session = _adapter.Session;
            if (session != null) _ = session.CloseAsync("zatrzymanie środowiska konta");
            if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(5));
            // Zapis należy wyłącznie do wątku silnika; nigdy równolegle po nieudanym Join.
        }
    }
}
