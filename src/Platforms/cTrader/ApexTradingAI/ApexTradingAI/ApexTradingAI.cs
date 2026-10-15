using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Apex.Core.Adaptive;
using Apex.Core.Bridge;
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

        [Parameter("Zezwól na konto LIVE", DefaultValue = false, Group = "Bezpieczeństwo")]
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
            var acc = Snapshot();
            _guard = AccountGuard.Evaluate(acc, new AccountGuardSettings
            {
                AllowLive = AllowLive,
                LiveConfirmation = LiveConfirmation,
                AllowedAccounts = AccountGuard.ParseAccountList(AllowedAccounts),
                AllowedBrokers = AccountGuard.ParseList(AllowedBrokers),
            });
            Print("APEX: " + _guard.Reason);
            if (ShowPanel && !IsBacktesting) _panel = new ChartPanel(this);
            if (!_guard.Allowed)
            {
                _panel?.ShowBlocked(_guard.Reason, Account.IsLive ? AccountGuard.RequiredLivePhrase(Account.Number) : null);
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
            _adapter = new CTraderAdapter(this, _store);
            IKeyValueStore kv = IsBacktesting ? new MemoryStore() : new LocalStorageStore(this);
            _engine = new TradingEngine(cfg, book, _adapter, _adapter, _store, kv, new PrintLog(this), new PrintJournal(this), NewsFilter ? _news : null);
            _engine.SetGuard(_guard);

            BuildUniverse(book);
            _engine.Initialize(Server.TimeInUtc);
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
            for (int g = 0; g < 10 && bars.Count < want + 1; g++) if (bars.LoadMoreHistory() <= 0) break;
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
            double gross = 0, comm = 0, swap = 0, net = 0, exit = p.CurrentPrice;
            DateTime exitTime = Server.TimeInUtc;
            var deals = History.FindByPositionId(p.Id);
            foreach (var h in deals) { gross += h.GrossProfit; comm += h.Commissions; swap += h.Swap; net += h.NetProfit; exit = h.ClosingPrice; exitTime = h.ClosingTime; }
            if (deals.Length == 0) { gross = p.GrossProfit; comm = p.Commissions; swap = p.Swap; net = p.NetProfit; }
            _engine.OnPositionClosed(new ClosedPositionInfo
            {
                PositionId = p.Id,
                Symbol = p.SymbolName,
                Label = p.Label ?? "",
                Comment = p.Comment ?? "",
                Direction = p.TradeType == TradeType.Buy ? TradeDirection.Long : TradeDirection.Short,
                Volume = p.VolumeInUnits,
                EntryPrice = p.EntryPrice,
                ExitPrice = exit,
                EntryTimeUtc = DateTime.SpecifyKind(p.EntryTime, DateTimeKind.Utc),
                ExitTimeUtc = DateTime.SpecifyKind(exitTime, DateTimeKind.Utc),
                GrossProfit = gross,
                Commission = comm,
                Swap = swap,
                NetProfit = net,
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

    internal sealed class CTraderAdapter : IBrokerGateway, IMarketData
    {
        private readonly Robot _r;
        private readonly BarStore _store;
        private readonly Dictionary<string, SymbolDto> _dtos = new(StringComparer.OrdinalIgnoreCase);

        public CTraderAdapter(Robot r, BarStore store)
        {
            _r = r;
            _store = store;
        }

        public void Register(string symbol, SymbolDto dto) => _dtos[symbol] = dto;

        private static string CommissionKind(SymbolCommissionType t) => t switch
        {
            SymbolCommissionType.UsdPerMillionUsdVolume => "UsdPerMillionUsdVolume",
            SymbolCommissionType.UsdPerOneLot => "UsdPerLot",
            SymbolCommissionType.PercentageOfTradingVolume => "PercentOfVolume",
            SymbolCommissionType.QuoteCurrencyPerOneLot => "QuoteCurrencyPerLot",
            _ => "Unknown",
        };

        private static double Conv(Robot r, string from)
        {
            try { return r.AssetConverter.Convert(1.0, from, r.Account.Asset); }
            catch (Exception) { return 0; }
        }

        public static SymbolDto ToDto(Symbol s, Robot r) => new()
        {
            Name = s.Name,
            Description = s.Description ?? "",
            BaseAsset = s.BaseAsset?.Name ?? "",
            QuoteAsset = s.QuoteAsset?.Name ?? "",
            Digits = s.Digits,
            PipSize = s.PipSize,
            TickSize = s.TickSize,
            PipValue = s.PipValue,
            LotSize = s.LotSize,
            VolumeMin = s.VolumeInUnitsMin,
            VolumeStep = s.VolumeInUnitsStep,
            VolumeMax = s.VolumeInUnitsMax,
            MinStopPips = s.MinDistanceType == SymbolMinDistanceType.Pips ? s.MinStopLossDistance : 0,
            TradingEnabled = s.IsTradingEnabled && s.TradingMode == SymbolTradingMode.FullAccess,
            CloseOnly = s.TradingMode == SymbolTradingMode.CloseOnly,
            CommissionKind = CommissionKind(s.CommissionType),
            CommissionValue = s.Commission,
            UsdToAccount = Conv(r, "USD"),
            QuoteToAccount = s.QuoteAsset != null ? Conv(r, s.QuoteAsset.Name) : 0,
            IsCustom = s.IsCustom,
        };

        public bool IsConnected => _r.IsBacktesting || _r.Server.IsConnected;

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
            if (!(q.StopLossPips > 0)) return OrderOutcome.Rejected(BrokerErrorKind.InvalidStopLossTakeProfit, "brak SL");
            var sym = _r.Symbols.GetSymbol(q.Symbol);
            if (sym == null) return OrderOutcome.Rejected(BrokerErrorKind.UnknownSymbol, "nieznany symbol");
            double vol = sym.NormalizeVolumeInUnits(q.VolumeUnits, RoundingMode.Down);
            if (vol < sym.VolumeInUnitsMin) return OrderOutcome.Rejected(BrokerErrorKind.BadVolume, "wolumen poniżej minimum");
            var tt = q.Direction == TradeDirection.Long ? TradeType.Buy : TradeType.Sell;
            TradeResult r = q.MaxSlippagePips is double slip && slip > 0
                ? _r.ExecuteMarketRangeOrder(tt, sym.Name, vol, slip, tt == TradeType.Buy ? sym.Ask : sym.Bid, q.Label, q.StopLossPips, q.TakeProfitPips, q.Comment)
                : _r.ExecuteMarketOrder(tt, sym.Name, vol, q.Label, q.StopLossPips, q.TakeProfitPips, q.Comment);
            return Map(r, null);
        }

        public OrderOutcome ModifyStopLoss(long positionId, double stopLoss)
        {
            var p = _r.Positions.FirstOrDefault(x => x.Id == positionId);
            if (p == null) return OrderOutcome.Rejected(BrokerErrorKind.EntityNotFound, "brak pozycji");
            return Map(p.ModifyStopLossPrice(stopLoss), stopLoss);
        }

        public OrderOutcome ClosePosition(long positionId, double? units)
        {
            var p = _r.Positions.FirstOrDefault(x => x.Id == positionId);
            if (p == null) return OrderOutcome.Rejected(BrokerErrorKind.EntityNotFound, "brak pozycji");
            if (units.HasValue && units.Value < p.VolumeInUnits)
                return Map(_r.ClosePosition(p, p.Symbol.NormalizeVolumeInUnits(units.Value, RoundingMode.Down)), p.CurrentPrice);
            return Map(_r.ClosePosition(p), p.CurrentPrice);
        }

        public double EstimateMargin(string symbol, TradeDirection direction, double units)
        {
            try
            {
                var s = _r.Symbols.GetSymbol(symbol);
                return s == null ? double.NaN : s.GetEstimatedMargin(direction == TradeDirection.Long ? TradeType.Buy : TradeType.Sell, units);
            }
            catch (Exception) { return double.NaN; }
        }

        public InstrumentSpec? GetSpec(string symbol)
        {
            if (!_dtos.TryGetValue(symbol, out var dto)) return null;
            var s = _r.Symbols.GetSymbol(symbol);
            return SpecMapper.ToSpec(dto, s?.PipValue, null);
        }

        public Quote? GetQuote(string symbol)
        {
            var s = _r.Symbols.GetSymbol(symbol);
            if (s == null || double.IsNaN(s.Bid) || double.IsNaN(s.Ask)) return null;
            return new Quote(s.Bid, s.Ask, _r.Server.TimeInUtc);
        }

        public bool IsMarketOpen(string symbol, DateTime utc, out TimeSpan timeToClose)
        {
            timeToClose = TimeSpan.Zero;
            var s = _r.Symbols.GetSymbol(symbol);
            if (s == null) return false;
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
        public LocalStorageStore(Robot r) => _r = r;
        public string? Get(string key) { var v = _r.LocalStorage.GetString(key, LocalStorageScope.Type); return string.IsNullOrEmpty(v) ? null : v; }
        public void Set(string key, string value) => _r.LocalStorage.SetString(key, value, LocalStorageScope.Type);
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
    internal sealed class PrintJournal : ITradeJournal
    {
        private readonly Robot _r;
        public PrintJournal(Robot r) => _r = r;
        public void TradeOpened(TradePlan plan, double volume, string details) => _r.Print($"APEX OTWARCIE #{plan.PositionId} {plan.Symbol} {plan.Direction} {volume} {plan.Strategy} SL {plan.InitialStop} | {plan.EntryReason}");
        public void TradeClosed(TradeRecord record) => _r.Print("APEX ZAMKNIĘCIE " + record.ToCsv());
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
            _body.Text = reason + (phrase != null ? $"\nAby świadomie dopuścić konto LIVE: włącz \"Zezwól na konto LIVE\", dodaj numer konta do listy dozwolonych i wpisz frazę: {phrase}" : "");
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
