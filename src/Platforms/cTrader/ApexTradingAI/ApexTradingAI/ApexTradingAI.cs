using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Apex.Core.Adaptive;
using Apex.Core.Bridge;
using Apex.Platforms.CTrader;
using Apex.Core.Engine;
using Apex.Core.Management;
using Apex.Core.Model;
using Apex.Core.Performance;
using Apex.Core.Risk;
using Apex.Core.Safety;
using Apex.Core.Scanner;
using cAlgo.API;
using cAlgo.API.Internals;
using ApexBar = Apex.Core.Model.Bar;

namespace cAlgo.Robots
{
    /// <summary>
    /// APEX Trading AI – natywny cBot (silnik w cTrader). Ten sam silnik co aplikacja APEX Desktop,
    /// uruchamiany bez aplikacji zewnętrznej; umożliwia też backtest w oficjalnym backtesterze cTrader.
    /// UWAGA: nie uruchamiaj jednocześnie natywnego cBota i APEX Desktop na tym samym koncie.
    /// </summary>
    [Robot(AccessRights = AccessRights.None, TimeZone = TimeZones.UTC, AddIndicators = false)]
    public class ApexTradingAI : Robot
    {
        // ---------------------------------------------------------------- parametry
        [Parameter("Ryzyko na transakcję (%)", DefaultValue = 0.5, MinValue = 0.05, MaxValue = 2.0, Step = 0.05, Group = "Ryzyko")]
        public double RiskPct { get; set; }

        [Parameter("Maks. dzienna strata (%)", DefaultValue = 1.5, MinValue = 0.1, MaxValue = 10, Step = 0.1, Group = "Ryzyko")]
        public double MaxDailyLossPct { get; set; }

        [Parameter("Maks. obsunięcie (%)", DefaultValue = 5.0, MinValue = 0.5, MaxValue = 30, Step = 0.5, Group = "Ryzyko")]
        public double MaxDrawdownPct { get; set; }

        [Parameter("Maks. liczba pozycji", DefaultValue = 3, MinValue = 1, MaxValue = 20, Group = "Ryzyko")]
        public int MaxPositions { get; set; }

        [Parameter("Maks. dźwignia efektywna (x)", DefaultValue = 5.0, MinValue = 0.5, MaxValue = 30, Step = 0.5, Group = "Ryzyko")]
        public double MaxLeverage { get; set; }

        [Parameter("Minimalna ocena okazji (0-100)", DefaultValue = 50, MinValue = 0, MaxValue = 100, Group = "Strategie")]
        public double MinScore { get; set; }

        [Parameter("Instrumenty (puste = automatycznie)", DefaultValue = "", Group = "Instrumenty")]
        public string Instruments { get; set; } = "";

        [Parameter("Wykluczone instrumenty", DefaultValue = "", Group = "Instrumenty")]
        public string Blacklist { get; set; } = "";

        [Parameter("Maks. liczba instrumentów", DefaultValue = 25, MinValue = 1, MaxValue = 100, Group = "Instrumenty")]
        public int MaxInstruments { get; set; }

        [Parameter("Filtr wiadomości makro", DefaultValue = true, Group = "Wiadomości")]
        public bool NewsFilter { get; set; }

        [Parameter("Adres kalendarza (JSON)", DefaultValue = "https://nfs.faireconomy.media/ff_calendar_thisweek.json", Group = "Wiadomości")]
        public string NewsUrl { get; set; } = "";

        [Parameter("Etykieta pozycji bota", DefaultValue = "APEX-N", Group = "Bezpieczeństwo")]
        public string Label { get; set; } = "APEX-N";

        [Parameter("Historyczny LIVE (ignorowany – tylko DEMO)", DefaultValue = false, Group = "Bezpieczeństwo")]
        public bool AllowLive { get; set; }

        [Parameter("Potwierdzenie LIVE (fraza)", DefaultValue = "", Group = "Bezpieczeństwo")]
        public string LiveConfirmation { get; set; } = "";

        [Parameter("Dozwolone konta (numery, przecinek)", DefaultValue = "", Group = "Bezpieczeństwo")]
        public string AllowedAccounts { get; set; } = "";

        [Parameter("Dozwoleni brokerzy (np. Pepperstone)", DefaultValue = "", Group = "Bezpieczeństwo")]
        public string AllowedBrokers { get; set; } = "";

        [Parameter("Pokaż panel", DefaultValue = true, Group = "Panel")]
        public bool ShowPanel { get; set; }

        // ---------------------------------------------------------------- stan
        private TradingEngine? _engine;
        private CTraderAdapter? _adapter;
        private readonly BarStore _store = new(2000);
        private readonly NewsCalendar _news = new();
        private ChartPanel? _panel;
        private DateTime _lastRun = DateTime.MinValue;
        private DateTime _lastNews = DateTime.MinValue;
        private readonly HashSet<string> _barHandlers = new();
        private AccountGuardResult _guard = new();

        protected override void OnStart()
        {
            if (!PlatformSafety.DemoAllowed(Account.IsLive, AllowLive))
            { Print("APEX: konto REAL bezwarunkowo zablokowane – wersja wyłącznie DEMO."); Stop(); return; }
            var acc = Snapshot();
            _guard = AccountGuard.Evaluate(acc, new AccountGuardSettings
            {
                AllowLive = false,
                LiveConfirmation = LiveConfirmation,
                AllowedAccounts = AccountGuard.ParseAccountList(AllowedAccounts),
                AllowedBrokers = AccountGuard.ParseList(AllowedBrokers),
            });
            Print("APEX: " + _guard.Reason);
            if (ShowPanel && !IsBacktesting) _panel = new ChartPanel(this);
            if (!_guard.Allowed)
            {
                _panel?.ShowBlocked(_guard.Reason, null);
                Print("APEX: handel zablokowany – cBot zatrzymany.");
                Stop();
                return;
            }

            var risk = new RiskSettings
            {
                RiskPerTradePct = RiskPct,
                MaxDailyLossPct = MaxDailyLossPct,
                MaxDrawdownPct = MaxDrawdownPct,
                MaxOpenPositions = MaxPositions,
                MaxEffectiveLeverage = MaxLeverage,
            };
            var err = risk.Validate();
            if (err != null) { Print("APEX: nieprawidłowe ustawienia ryzyka – " + err); Stop(); return; }

            var cfg = new EngineConfig
            {
                Label = string.IsNullOrWhiteSpace(Label) ? "APEX-N" : Label.Trim(),
                Risk = risk,
                Scoring = new ScoringSettings { MinScore = MinScore },
                News = new NewsSettings { Enabled = NewsFilter && !IsBacktesting },
                IsBacktest = IsBacktesting,
                CollectionWindowSeconds = IsBacktesting ? 0 : 20,
            };
            var book = new StrategyBook(DefaultProfile.Load(), Account.IsLive);
            _adapter = new CTraderAdapter(this, _store, cfg.Label);
            IKeyValueStore kv = IsBacktesting ? new MemoryStore() : new LocalStorageStore(this, cfg.Label);
            _engine = new TradingEngine(cfg, book, _adapter, _adapter, _store, kv, new PrintLog(this), new PrintJournal(this, cfg.Label), NewsFilter ? _news : null);
            _engine.SetGuard(_guard);

            BuildUniverse(book);
            _engine.Initialize(Server.TimeInUtc);
            if (IsBacktesting) _engine.CmdStart();
            Positions.Closed += OnPositionClosed;
            if (!IsBacktesting)
            {
                if (NewsFilter) RefreshNews();
                Timer.Start(TimeSpan.FromSeconds(2));
            }
            _panel?.Attach(_engine);
            Print($"APEX uruchomiony: {(Account.IsLive ? "LIVE" : "DEMO")} {Account.Number}, instrumentów: {_engine.Universe.Count}, strategie dla klas: {string.Join(", ", book.Classes)}");
        }

        private AccountSnapshot Snapshot() => new()
        {
            Number = Account.Number,
            IsLive = Account.IsLive,
            BrokerName = Account.BrokerName ?? "",
            Currency = Account.Asset?.Name ?? "",
            Balance = Account.Balance,
            Equity = Account.Equity,
            Margin = Account.Margin,
            FreeMargin = Account.FreeMargin,
            MarginLevel = Account.MarginLevel,
            IsHedging = Account.AccountType == AccountType.Hedged,
            TimeUtc = Server.TimeInUtc,
        };

        // ---------------------------------------------------------------- instrumenty i dane

        private void BuildUniverse(StrategyBook book)
        {
            var dtos = new List<SymbolDto>();
            var names = Symbols.ToArray();
            for (int i = 0; i < names.Length; i += 100)
            {
                Symbol[] batch;
                try { batch = Symbols.GetSymbols(names.Skip(i).Take(100).ToArray()); }
                catch (Exception) { continue; }
                foreach (var s in batch) if (s != null && !s.IsArchived) dtos.Add(CTraderAdapter.ToDto(s, this));
            }
            var manual = AccountGuard.ParseList(Instruments).ToList();
            var settings = new UniverseSettings
            {
                Mode = manual.Count > 0 ? "manual" : "auto",
                Manual = manual,
                Blacklist = AccountGuard.ParseList(Blacklist).ToList(),
                MaxTotal = MaxInstruments,
            };
            var selected = UniverseSelector.Select(dtos, settings, new HashSet<AssetClass>(book.Classes), new HashSet<AssetClass>()).Where(u => u.Selected).ToList();
            var tfs = new HashSet<Tf> { Tf.H1, Tf.H4, Tf.D1 };
            foreach (var st in book.All) { tfs.Add(st.SignalTf); if (st.FilterTf.HasValue) tfs.Add(st.FilterTf.Value); }
            var entries = new List<UniverseEntry>();
            foreach (var u in selected)
            {
                var dto = dtos.First(d => d.Name == u.Symbol);
                var legs = InstrumentClassifier.CurrencyLegs(u.AssetClass, u.Symbol, dto.BaseAsset, dto.QuoteAsset);
                entries.Add(new UniverseEntry { Symbol = u.Symbol, AssetClass = u.AssetClass, BaseCurrency = legs.Base, QuoteCurrency = legs.Quote });
                _adapter!.Register(u.Symbol, dto);
                foreach (var tf in tfs) LoadBars(u.Symbol, tf);
            }
            _engine!.SetUniverse(entries);
        }

        private static TimeFrame ToTimeFrame(Tf tf) => tf switch
        {
            Tf.M5 => TimeFrame.Minute5,
            Tf.M15 => TimeFrame.Minute15,
            Tf.M30 => TimeFrame.Minute30,
            Tf.H1 => TimeFrame.Hour,
            Tf.H4 => TimeFrame.Hour4,
            _ => TimeFrame.Daily,
        };

        private void LoadBars(string symbol, Tf tf)
        {
            Bars bars;
            try { bars = MarketData.GetBars(ToTimeFrame(tf), symbol); }
            catch (Exception ex) { Print($"APEX: brak świec {symbol} {tf}: {ex.Message}"); return; }
            int want = tf == Tf.D1 ? 420 : 1500;
            for (int g = 0; g < 50 && bars.Count < want + 1; g++) if (bars.LoadMoreHistory() <= 0) break;
            if (bars.Count < want + 1) Print($"APEX: niepełna historia {symbol} {tf}: {Math.Max(0, bars.Count - 1)}/{want}");
            var list = new List<ApexBar>(bars.Count);
            for (int i = 0; i < bars.Count - 1; i++) list.Add(Convert(bars[i])); // ostatnia świeca jest w trakcie tworzenia
            _store.Merge(symbol, tf, list, true);
            if (_barHandlers.Add(symbol + "|" + tf))
                bars.BarOpened += args =>
                {
                    var b = args.Bars;
                    if (b.Count >= 2) _store.Merge(symbol, tf, new[] { Convert(b[b.Count - 2]) }, false);
                };
        }

        private static ApexBar Convert(cAlgo.API.Bar b) =>
            new(DateTime.SpecifyKind(b.OpenTime, DateTimeKind.Utc), b.Open, b.High, b.Low, b.Close, b.TickVolume);

        // ---------------------------------------------------------------- cykl

        protected override void OnTimer()
        {
            if (IsBacktesting || _engine == null) return;
            RunCycle();
            if (NewsFilter && (DateTime.UtcNow - _lastNews).TotalHours >= 4) RefreshNews();
        }

        protected override void OnTick()
        {
            // W backteście cTrader cykl silnika jest wywoływany co minutę czasu symulacji.
            if (!IsBacktesting || _engine == null) return;
            if ((Server.TimeInUtc - _lastRun).TotalSeconds >= 60) RunCycle();
        }

        private void RunCycle()
        {
            _lastRun = Server.TimeInUtc;
            _engine!.OnTimer(Server.TimeInUtc);
            _panel?.Refresh(_engine.GetStatus(Server.TimeInUtc));
        }

        private void RefreshNews()
        {
            _lastNews = DateTime.UtcNow;
            if (string.IsNullOrWhiteSpace(NewsUrl) || !NewsUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                var r = Http.Get(NewsUrl);
                if (r.IsSuccessful)
                {
                    var events = NewsCalendar.ParseFfJson(r.Body);
                    if (events.Count > 0) _news.SetEvents(events, DateTime.UtcNow, "kalendarz");
                    Print($"APEX: kalendarz wiadomości – {events.Count} wydarzeń.");
                }
                else Print("APEX: nie udało się pobrać kalendarza wiadomości (kod " + r.StatusCode + ").");
            }
            catch (Exception ex) { Print("APEX: kalendarz wiadomości niedostępny – " + ex.Message); }
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            var p = args.Position;
            if (_engine == null || p.Label != _engine.Config.Label) return;

            _engine.OnPositionClosed(new ClosedPositionInfo
            {
                PositionId = p.Id,
                Symbol = p.SymbolName,
                Label = p.Label ?? "",
                Comment = p.Comment ?? "",
                Direction = p.TradeType == TradeType.Buy ? TradeDirection.Long : TradeDirection.Short,
                EntryTimeUtc = DateTime.SpecifyKind(p.EntryTime, DateTimeKind.Utc),
                ExitTimeUtc = Server.TimeInUtc,
                PlatformReason = args.Reason.ToString(),
            });
        }

        protected override void OnStop()
        {
            _engine?.SaveState(Server.TimeInUtc, true);
        }

        protected override double GetFitness(GetFitnessArgs args)
        {
            // Kryterium optymalizacji w cTrader: zysk netto skorygowany o obsunięcie (nie sam zysk) i minimalna liczba transakcji.
            if (args.TotalTrades < 30) return -1000 + args.TotalTrades;
            double dd = Math.Max(0.5, args.MaxEquityDrawdownPercentages);
            return args.NetProfit / Account.Balance * 100.0 / dd;
        }

        internal void RunCommand(Func<TradingEngine, string> f)
        {
            if (_engine == null) return;
            string msg = f(_engine);
            Print("APEX: " + msg);
            _panel?.Message(msg);
            _panel?.Refresh(_engine.GetStatus(Server.TimeInUtc));
        }
    }

    // ======================================================================== adaptery cTrader

    internal sealed class CTraderAdapter : IBrokerGateway, IBrokerHistory, IMarketData
    {
        private readonly Robot _r;
        private readonly BarStore _store;
        private readonly Dictionary<string, SymbolDto> _dtos = new(StringComparer.OrdinalIgnoreCase);

        private readonly string _label;
        private readonly long _account;
        private readonly string _broker;
        private readonly string _currency;
        private readonly CommandJournal _journal;
        private readonly Dictionary<string, Ticks> _ticks = new(StringComparer.OrdinalIgnoreCase);

        public CTraderAdapter(Robot r, BarStore store, string label)
        {
            _r = r;
            _store = store;
            _label = label;
            _account = r.Account.Number;
            _broker = r.Account.BrokerName ?? "";
            _currency = r.Account.Asset?.Name ?? "";
            string key = PlatformSafety.StorageKey("Native", _broker, _account, label);
            string? saved;
            try { saved = r.IsBacktesting ? null : r.LocalStorage.GetString(key, LocalStorageScope.Type); }
            catch (Exception) { saved = "NIEDOSTEPNY"; }
            _journal = new CommandJournal(saved, value =>
            {
                if (r.IsBacktesting) return;
                r.LocalStorage.SetString(key, value, LocalStorageScope.Type);
                r.LocalStorage.Flush(LocalStorageScope.Type);
            });
        }

        private bool SafeAccount => !_r.Account.IsLive && _r.Account.Number == _account && (_r.Account.BrokerName ?? "") == _broker && (_r.Account.Asset?.Name ?? "") == _currency;
        public void Register(string symbol, SymbolDto dto)
        {
            _dtos[symbol] = dto;
            try { _ticks[symbol] = _r.MarketData.GetTicks(symbol); }
            catch (Exception ex) { _r.Print("APEX: brak ticków " + symbol + ": " + ex.Message); }
        }

        public static SymbolDto ToDto(Symbol s, Robot r) => CTraderData.Describe(s, r);

        private bool _wasConnected;
        private string _generation = Guid.NewGuid().ToString("N");
        public bool IsConnected
        {
            get
            {
                bool connected = SafeAccount && (_r.IsBacktesting || _r.Server.IsConnected);
                if (connected && !_wasConnected) _generation = Guid.NewGuid().ToString("N");
                _wasConnected = connected; return connected;
            }
        }
        public BrokerHistorySnapshot GetHistory()
        {
            // Engine callbacks run on cTrader's main thread. Never enumerate History in a
            // network/task callback. SDK has no documented completeness/paging operation.
            if (!IsConnected) return new();
            var rows = CTraderHistory.Capture(_r.History, _label);
            return new BrokerHistorySnapshot
            {
                AccountNumber = _account, Generation = _generation, Complete = true,
                RequiredCoverage = _r.IsBacktesting, Closed = rows.Select(x => x.ToInfo()).ToArray(), Positions = GetPositions(),
            };
        }

        public AccountSnapshot GetAccount() => new()
        {
            Number = _r.Account.Number,
            IsLive = _r.Account.IsLive,
            BrokerName = _r.Account.BrokerName ?? "",
            Currency = _r.Account.Asset?.Name ?? "",
            Balance = _r.Account.Balance,
            Equity = _r.Account.Equity,
            Margin = _r.Account.Margin,
            FreeMargin = _r.Account.FreeMargin,
            MarginLevel = _r.Account.MarginLevel,
            TimeUtc = _r.Server.TimeInUtc,
            IsHedging = _r.Account.AccountType == AccountType.Hedged,
        };

        public IReadOnlyList<PositionInfo> GetPositions() => _r.Positions.Select(p => new PositionInfo
        {
            Id = p.Id,
            Symbol = p.SymbolName,
            Direction = p.TradeType == TradeType.Buy ? TradeDirection.Long : TradeDirection.Short,
            VolumeUnits = p.VolumeInUnits,
            EntryPrice = p.EntryPrice,
            EntryTimeUtc = DateTime.SpecifyKind(p.EntryTime, DateTimeKind.Utc),
            StopLoss = p.StopLoss,
            TakeProfit = p.TakeProfit,
            Label = p.Label ?? "",
            Comment = p.Comment ?? "",
            NetProfit = p.NetProfit,
            GrossProfit = p.GrossProfit,
            Commissions = p.Commissions,
            Swap = p.Swap,
            CurrentPrice = p.CurrentPrice,
            Margin = p.Margin,
        }).ToList();

        private static OrderOutcome Map(TradeResult r, double? price)
        {
            if (r.IsSuccessful) return OrderOutcome.Filled(r.Position?.Id ?? 0, price ?? r.Position?.EntryPrice ?? 0);
            var e = r.Error;
            var kind = e switch
            {
                ErrorCode.BadVolume => BrokerErrorKind.BadVolume,
                ErrorCode.NoMoney => BrokerErrorKind.NoMoney,
                ErrorCode.MarketClosed => BrokerErrorKind.MarketClosed,
                ErrorCode.Disconnected => BrokerErrorKind.Disconnected,
                ErrorCode.EntityNotFound => BrokerErrorKind.EntityNotFound,
                ErrorCode.Timeout => BrokerErrorKind.Timeout,
                ErrorCode.UnknownSymbol => BrokerErrorKind.UnknownSymbol,
                ErrorCode.InvalidStopLossTakeProfit => BrokerErrorKind.InvalidStopLossTakeProfit,
                ErrorCode.InvalidRequest => BrokerErrorKind.InvalidRequest,
                ErrorCode.NoTradingPermission => BrokerErrorKind.NoTradingPermission,
                _ => BrokerErrorKind.TechnicalError,
            };
            return OrderOutcome.IsUncertainError(kind) ? OrderOutcome.Uncertain(kind, r.ToString()) : OrderOutcome.Rejected(kind, r.ToString());
        }

        public OrderOutcome OpenMarket(OrderRequest q)
        {
            if (!SafeAccount || !IsConnected) return OrderOutcome.Rejected(BrokerErrorKind.NoTradingPermission, "wymagane aktywne konto DEMO");
            if (!PlatformSafety.OwnLabel(q.Label, _label) || string.IsNullOrWhiteSpace(q.Comment)) return OrderOutcome.Rejected(BrokerErrorKind.InvalidRequest, "nieprawidłowa etykieta/sygnał");
            string id = !string.IsNullOrEmpty(q.ExecutionId) ? q.ExecutionId : string.IsNullOrEmpty(q.SignalId) ? q.Comment : q.SignalId;
            string fingerprint = BridgeProtocol.Serialize(new { q.Symbol, q.Direction, q.VolumeUnits, q.StopLossPips, q.TakeProfitPips, q.Label, q.Comment, q.MaxSlippagePips });
            var result = _journal.Run(id, fingerprint, _label + "|" + q.Comment, () =>
            {
                var outcome = OpenProtected(q, id);
                return ProtocolResult.FromOutcome(id, outcome);
            });
            return ProtocolResult.ToOutcome(result);
        }

        private OrderOutcome OpenProtected(OrderRequest q, string executionId)
        {
            if (!PlatformSafety.Protection(q.StopLossPips, q.TakeProfitPips)) return OrderOutcome.Rejected(BrokerErrorKind.InvalidStopLossTakeProfit, "wymagane skończone dodatnie SL i TP");
            var sym = _r.Symbols.GetSymbol(q.Symbol);
            if (sym == null || !_dtos.ContainsKey(q.Symbol)) return OrderOutcome.Rejected(BrokerErrorKind.UnknownSymbol, "symbol nie jest zarejestrowany");
            var dto = ToDto(sym, _r);
            if (InstrumentClassifier.IsCrypto(sym.Name, sym.Description, sym.BaseAsset?.Name, sym.QuoteAsset?.Name) || dto.CommissionKind == "Unknown" || !dto.TradingEnabled)
                return OrderOutcome.Rejected(BrokerErrorKind.InvalidRequest, "instrument lub koszt niezweryfikowany");
            var quote = GetQuote(q.Symbol);
            if (!quote.HasValue || !quote.Value.IsValid || !PlatformSafety.Fresh(quote.Value.TimeUtc, _r.Server.TimeInUtc, 30))
                return OrderOutcome.Rejected(BrokerErrorKind.Disconnected, "brak świeżego ticka brokera");
            if (!sym.MarketHours.IsOpened()) return OrderOutcome.Rejected(BrokerErrorKind.MarketClosed, "rynek zamknięty");
            if (!CTraderData.StopsAllowed(sym, q.StopLossPips, q.TakeProfitPips)) return OrderOutcome.Rejected(BrokerErrorKind.InvalidStopLossTakeProfit, "SL/TP poniżej minimum brokera");
            if (!PlatformSafety.Volume(q.VolumeUnits, sym.VolumeInUnitsMin, sym.VolumeInUnitsMax)) return OrderOutcome.Rejected(BrokerErrorKind.BadVolume, "nieprawidłowy wolumen w jednostkach");
            double vol = sym.NormalizeVolumeInUnits(q.VolumeUnits, RoundingMode.Down);
            if (Math.Abs(vol - q.VolumeUnits) > sym.VolumeInUnitsStep * 1e-8) return OrderOutcome.Rejected(BrokerErrorKind.BadVolume, "nieprawidłowy krok wolumenu");
            if (q.MaxSlippagePips.HasValue && !PlatformSafety.Positive(q.MaxSlippagePips.Value)) return OrderOutcome.Rejected(BrokerErrorKind.InvalidRequest, "nieprawidłowy poślizg");
            var prior = _r.Positions.FirstOrDefault(p => p.Label == q.Label && p.Comment == q.Comment);
            if (prior != null) return prior.StopLoss.HasValue && prior.TakeProfit.HasValue ? OrderOutcome.Filled(prior.Id, prior.EntryPrice)
                : OrderOutcome.Uncertain(BrokerErrorKind.InvalidStopLossTakeProfit, "sygnał istnieje bez pełnej ochrony – nie otwarto ponownie");
            if (_r.History.Any(h => h.Label == q.Label && h.Comment == q.Comment)) return OrderOutcome.Rejected(BrokerErrorKind.InvalidRequest, "sygnał już w historii brokera");
            var tt = q.Direction == TradeDirection.Long ? TradeType.Buy : TradeType.Sell;
            var result = q.MaxSlippagePips is double slip
                ? _r.ExecuteMarketRangeOrder(tt, sym.Name, vol, slip, tt == TradeType.Buy ? sym.Ask : sym.Bid, q.Label, q.StopLossPips, q.TakeProfitPips, q.Comment)
                : _r.ExecuteMarketOrder(tt, sym.Name, vol, q.Label, q.StopLossPips, q.TakeProfitPips, q.Comment);
            // Capture now, before repair or its emergency close mutates the broker object.
            var openingFill = result.IsSuccessful && result.Position != null
                ? CTraderOpeningEvidence.Capture(result.Position, q, executionId, sym.VolumeInUnitsStep) : null;
            if (openingFill != null) _journal.RecordOpeningFill(executionId, openingFill);
            if (result.IsSuccessful && result.Position != null && !CTraderData.Protected(result.Position))
            {
                var position = result.Position;
                double sl = position.EntryPrice + (tt == TradeType.Buy ? -1 : 1) * q.StopLossPips * sym.PipSize;
                double tp = position.EntryPrice + (tt == TradeType.Buy ? 1 : -1) * q.TakeProfitPips!.Value * sym.PipSize;
                string closeResult = "";
                bool repaired = PlatformSafety.RepairOrClose(
                    () => _r.ModifyPosition(position, sl, tp, ProtectionType.Absolute).IsSuccessful && CTraderData.Protected(position),
                    () => closeResult = _r.ClosePosition(position).ToString());
                if (!repaired)
                    return new OrderOutcome { Status = OrderStatus.Uncertain, PositionId = position.Id, FillPrice = openingFill?.EntryPrice,
                        OpeningFill = openingFill, Error = BrokerErrorKind.InvalidStopLossTakeProfit, Message = "brak SL/TP; awaryjne zamknięcie: " + closeResult };
            }
            if (result.IsSuccessful && result.Position == null) return OrderOutcome.Uncertain(BrokerErrorKind.TechnicalError, "brak pozycji w potwierdzeniu");
            return result.IsSuccessful ? OrderOutcome.Filled(result.Position!.Id, result.Position.EntryPrice, openingFill) : Map(result, null);
        }

        public OrderOutcome ModifyStopLoss(long positionId, double stopLoss)
        {
            if (!SafeAccount) return OrderOutcome.Rejected(BrokerErrorKind.NoTradingPermission, "konto REAL lub zmiana konta");
            var position = _r.Positions.FirstOrDefault(x => x.Id == positionId && PlatformSafety.OwnLabel(x.Label, _label));
            if (position == null) return OrderOutcome.Rejected(BrokerErrorKind.EntityNotFound, "brak własnej pozycji");
            if (!PlatformSafety.Positive(stopLoss) ||
                (position.TradeType == TradeType.Buy && (stopLoss >= position.Symbol.Bid || (position.StopLoss.HasValue && stopLoss < position.StopLoss.Value))) ||
                (position.TradeType == TradeType.Sell && (stopLoss <= position.Symbol.Ask || (position.StopLoss.HasValue && stopLoss > position.StopLoss.Value))))
                return OrderOutcome.Rejected(BrokerErrorKind.InvalidStopLossTakeProfit, "nieprawidłowy lub zwiększający ryzyko SL");
            return Map(position.ModifyStopLossPrice(stopLoss), position.StopLoss);
        }

        public OrderOutcome ClosePosition(long positionId, double? units)
        {
            if (!SafeAccount) return OrderOutcome.Rejected(BrokerErrorKind.NoTradingPermission, "konto REAL lub zmiana konta");
            var position = _r.Positions.FirstOrDefault(x => x.Id == positionId && PlatformSafety.OwnLabel(x.Label, _label));
            if (position == null) return OrderOutcome.Rejected(BrokerErrorKind.EntityNotFound, "brak własnej pozycji");
            if (!PlatformSafety.PartialClose(units, position.VolumeInUnits, position.Symbol.VolumeInUnitsMin, position.Symbol.VolumeInUnitsStep))
                return OrderOutcome.Rejected(BrokerErrorKind.BadVolume, "nieprawidłowy wolumen zamknięcia");
            var result = units.HasValue && units.Value < position.VolumeInUnits ? _r.ClosePosition(position, units.Value) : _r.ClosePosition(position);
            double? fill = null;
            if (result.IsSuccessful)
            {
                try { fill = _r.History.FindByPositionId(position.Id).OrderBy(h => h.ClosingTime).LastOrDefault()?.ClosingPrice; }
                catch (Exception) { } // Keep the confirmed close, but do not invent an execution price.
            }
            return result.IsSuccessful ? new OrderOutcome { Status = OrderStatus.Filled, PositionId = position.Id, FillPrice = fill } : Map(result, null);
        }

        public double EstimateMargin(string symbol, TradeDirection direction, double units)
        {
            try
            {
                if (!IsConnected) return double.NaN;
                var s = _r.Symbols.GetSymbol(symbol);
                if (s == null || !PlatformSafety.Volume(units, s.VolumeInUnitsMin, s.VolumeInUnitsMax)) return double.NaN;
                double normalized = s.NormalizeVolumeInUnits(units, RoundingMode.Down);
                if (Math.Abs(normalized - units) > s.VolumeInUnitsStep * 1e-8) return double.NaN;
                double margin = s.GetEstimatedMargin(direction == TradeDirection.Long ? TradeType.Buy : TradeType.Sell, normalized);
                return double.IsFinite(margin) && margin >= 0 ? margin : double.NaN;
            }
            catch (Exception) { return double.NaN; }
        }

        public InstrumentSpec? GetSpec(string symbol)
        {
            if (!_dtos.ContainsKey(symbol)) return null;
            var s = _r.Symbols.GetSymbol(symbol);
            if (s == null) return null;
            var dto = ToDto(s, _r); // Re-read conversion and broker constraints, never frozen startup rates.
            return SpecMapper.ToSpec(dto, dto.PipValue, null);
        }

        public Quote? GetQuote(string symbol)
        {
            var s = _r.Symbols.GetSymbol(symbol);
            if (s == null || !_ticks.TryGetValue(symbol, out var ticks)) return null;
            var quote = CTraderData.Quote(s, ticks, _r);
            return quote == null ? null : new Quote(quote.B, quote.A, BridgeProtocol.FromUnixMs(quote.T));
        }

        public bool IsMarketOpen(string symbol, DateTime utc, out TimeSpan timeToClose)
        {
            timeToClose = TimeSpan.Zero;
            var s = _r.Symbols.GetSymbol(symbol);
            if (s == null || !IsConnected) return false;
            try
            {
                if (!s.MarketHours.IsOpened()) return false;
                timeToClose = s.MarketHours.TimeTillClose();
                return true;
            }
            catch (Exception) { return false; }
        }
    }

    /// <summary>Stan silnika w LocalStorage cTrader (zakres Type – przetrwa restart cTrader i ponowne dodanie cBota).</summary>
    internal sealed class LocalStorageStore : IKeyValueStore
    {
        private readonly Robot _r;
        private readonly string _label;
        public LocalStorageStore(Robot r, string label) { _r = r; _label = label; }
        private string Identity(string key) => System.Text.Json.JsonSerializer.Serialize(new[]
            { _r.Account.BrokerName ?? "", _r.Account.Number.ToString(CultureInfo.InvariantCulture), _r.Account.Asset?.Name ?? "", _label, key });
        public string? Get(string key)
        {
            var v = _r.LocalStorage.GetString(key, LocalStorageScope.Type);
            return string.IsNullOrEmpty(v) ? null : RecoveryEnvelope.Decode(Identity(key), v);
        }
        public void Set(string key, string value) => _r.LocalStorage.SetString(key, RecoveryEnvelope.Encode(Identity(key), value), LocalStorageScope.Type);
        public void Flush() => _r.LocalStorage.Flush(LocalStorageScope.Type);
    }

    internal sealed class PrintLog : IEngineLog
    {
        private readonly Robot _r;
        public PrintLog(Robot r) => _r = r;
        public void Info(string message) => _r.Print("APEX: " + message);
        public void Warn(string message) => _r.Print("APEX UWAGA: " + message);
        public void Error(string message) => _r.Print("APEX BŁĄD: " + message);
    }

    /// <summary>Dziennik w logu cTrader (cBot działa bez uprawnień do plików – pełny dziennik CSV prowadzi APEX Desktop).</summary>
    internal sealed class PrintJournal : IRecoveryJournal
    {
        private readonly Robot _r;
        private readonly IKeyValueStore _store;
        private readonly string _key;
        private List<TradeRecord> _records;
        public bool IsHealthy { get; private set; } = true;
        public IReadOnlyList<TradeRecord> ClosedRecords => _records;
        public PrintJournal(Robot r, string label)
        {
            _r = r; _store = r.IsBacktesting ? new MemoryStore() : new LocalStorageStore(r, label);
            _key = PlatformSafety.StorageKey("Closed", r.Account.BrokerName ?? "", r.Account.Number, label);
            _records = new();
            try
            {
                string? json = _store.Get(_key);
                _records = json == null ? new() : System.Text.Json.JsonSerializer.Deserialize<List<TradeRecord>>(json, BridgeProtocol.Json) ?? throw new InvalidOperationException("invalid closed journal");
                if (_records.Any(x => x == null) || _records.Select(x => x.PositionId).Distinct().Count() != _records.Count)
                    throw new InvalidOperationException("conflicting closed journal");
            }
            catch (Exception ex) { IsHealthy = false; _r.Print("APEX: journal recovery blocked: " + ex.Message); }
        }
        public void TradeOpened(TradePlan plan, double volume, string details) => _r.Print($"APEX OTWARCIE #{plan.PositionId} {plan.Symbol} {plan.Direction} {volume} {plan.Strategy} SL {plan.InitialStop} | {plan.EntryReason}");
        public void TradeClosed(TradeRecord record)
        {
            if (!IsHealthy) throw new InvalidOperationException("journal recovery blocked");
            var prior = _records.FirstOrDefault(x => x.PositionId == record.PositionId);
            if (prior != null)
            {
                if (prior.ToCsv() != record.ToCsv()) throw new InvalidOperationException("conflicting closed projection");
                return;
            }
            var next = _records.Concat(new[] { record }).ToList();
            _store.Set(_key, System.Text.Json.JsonSerializer.Serialize(next, BridgeProtocol.Json)); _store.Flush();
            _records = next; _r.Print("APEX ZAMKNIĘCIE " + record.ToCsv());
        }
        public void Signal(string line) => _r.Print("APEX SYGNAŁ " + line);
        public void Event(string line) => _r.Print("APEX ZDARZENIE " + line);
    }

    // ======================================================================== panel na wykresie

    internal sealed class ChartPanel
    {
        private readonly ApexTradingAI _bot;
        private readonly TextBlock _title = new() { FontSize = 14, FontWeight = FontWeight.Bold, ForegroundColor = Color.White };
        private readonly TextBlock _mode = new() { FontSize = 12, ForegroundColor = Color.White };
        private readonly TextBlock _body = new() { FontSize = 12, ForegroundColor = Color.FromHex("#C9D1DC"), TextWrapping = TextWrapping.Wrap, MaxWidth = 380 };
        private readonly TextBlock _msg = new() { FontSize = 11, ForegroundColor = Color.FromHex("#FAB219"), TextWrapping = TextWrapping.Wrap, MaxWidth = 380 };
        private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        private readonly StackPanel _buttons2 = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        private DateTime _confirmUntil = DateTime.MinValue;
        private string _confirmAction = "";

        public ChartPanel(ApexTradingAI bot)
        {
            _bot = bot;
            var stack = new StackPanel { Orientation = Orientation.Vertical };
            stack.AddChild(_title);
            stack.AddChild(_mode);
            stack.AddChild(_body);
            stack.AddChild(_buttons);
            stack.AddChild(_buttons2);
            stack.AddChild(_msg);
            var border = new Border
            {
                Child = stack,
                BackgroundColor = Color.FromArgb(225, 18, 22, 32),
                BorderColor = Color.FromHex("#3987E5"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12),
                Margin = new Thickness(10),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };
            _title.Text = "APEX Trading AI";
            bot.Chart.AddControl(border);
        }

        private Button Btn(string text, Color bg, Action onClick)
        {
            var b = new Button { Text = text, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(8, 4, 8, 4), BackgroundColor = bg, ForegroundColor = Color.White, FontSize = 11, CornerRadius = new CornerRadius(6) };
            b.Click += _ => onClick();
            return b;
        }

        private void Confirm(string key, string prompt, Func<TradingEngine, string> action)
        {
            if (_confirmAction == key && DateTime.UtcNow < _confirmUntil)
            {
                _confirmAction = "";
                _bot.RunCommand(action);
            }
            else
            {
                _confirmAction = key;
                _confirmUntil = DateTime.UtcNow.AddSeconds(5);
                Message(prompt + " – kliknij ponownie w ciągu 5 s, aby potwierdzić.");
            }
        }

        public void Attach(TradingEngine e)
        {
            _buttons.AddChild(Btn("START", Color.FromHex("#2A78D6"), () => _bot.RunCommand(x => x.CmdStart())));
            _buttons.AddChild(Btn("PAUZA", Color.FromHex("#5A6475"), () => _bot.RunCommand(x => x.CmdPauseEntries())));
            _buttons.AddChild(Btn("STOP", Color.FromHex("#5A6475"), () => Confirm("stop", "Zatrzymać bota", x => x.CmdStop())));
            _buttons.AddChild(Btn("TEST DEMO", Color.FromHex("#6250D6"), () => Confirm("test", "Uruchomić test wykonania (DEMO)", x => x.CmdExecutionTest(DateTime.UtcNow, null))));
            _buttons2.AddChild(Btn("ZAMKNIJ POZYCJE BOTA", Color.FromHex("#8A3A2A"), () => Confirm("close", "Zamknąć wszystkie pozycje bota", x => x.CmdCloseAllOwn(DateTime.UtcNow))));
            _buttons2.AddChild(Btn("AWARYJNY STOP", Color.FromHex("#D03B3B"), () => Confirm("emergency", "AWARYJNE ZATRZYMANIE", x => x.CmdEmergencyStop(DateTime.UtcNow, "przycisk na wykresie"))));
            _buttons2.AddChild(Btn("RESET AWARII", Color.FromHex("#5A6475"), () => Confirm("reset", "Zresetować stan awaryjny", x => x.CmdResetEmergency(DateTime.UtcNow))));
        }

        public void ShowBlocked(string reason, string? phrase)
        {
            _mode.Text = "HANDEL ZABLOKOWANY";
            _mode.ForegroundColor = Color.FromHex("#EF6B6B");
            _body.Text = reason + "\nTa wersja obsługuje wyłącznie konto DEMO.";
        }

        public void Message(string m) => _msg.Text = m;

        public void Refresh(EngineStatus s)
        {
            var ci = CultureInfo.GetCultureInfo("pl-PL");
            _mode.Text = $"{(s.IsLive ? "LIVE" : "DEMO")} | {TradingEngine.ModeText(s.Mode)}{(string.IsNullOrEmpty(s.ModeReason) ? "" : " – " + s.ModeReason)}";
            _mode.ForegroundColor = s.IsLive ? Color.FromHex("#EF6B6B") : Color.FromHex("#2BC52B");
            var lines = new List<string>
            {
                $"Kapitał: {s.Equity.ToString("N2", ci)} {s.Currency} | saldo {s.Balance.ToString("N2", ci)}",
                $"Dziś: {s.DailyPnl.ToString("+0.00;-0.00", ci)} ({s.DailyPnlPct.ToString("+0.00;-0.00", ci)}%) | łącznie: {s.TotalPnl.ToString("+0.00;-0.00", ci)}",
                $"Obsunięcie: {s.DrawdownPct:F2}% / {s.MaxDrawdownPct:F2}% | dzienny limit {s.MaxDailyLossPct:F2}% | ryzyko {s.RiskPerTradePct:F2}%",
                $"Pozycje bota: {s.OwnPositionCount}/{s.MaxPositions} | dźwignia {s.EffectiveLeverage:F2}x | instrumenty: {string.Join(", ", s.UniverseByClass.Select(k => k.Key.PolishName() + " " + k.Value))}",
                "Strategie: " + (s.ActiveStrategies.Count > 0 ? string.Join("; ", s.ActiveStrategies) : "brak zweryfikowanych – bot pozostaje bez pozycji"),
                "Wiadomości: " + s.NewsStatus,
            };
            if (s.OpenPositions.Count > 0) lines.Add("Pozycje:\n  " + string.Join("\n  ", s.OpenPositions.Take(5)));
            if (s.RecentSignals.Count > 0) lines.Add("Sygnały:\n  " + string.Join("\n  ", s.RecentSignals.Take(3)));
            if (s.RecentErrors.Count > 0) lines.Add("Komunikaty:\n  " + string.Join("\n  ", s.RecentErrors.Take(3)));
            _body.Text = string.Join("\n", lines);
        }
    }
}
