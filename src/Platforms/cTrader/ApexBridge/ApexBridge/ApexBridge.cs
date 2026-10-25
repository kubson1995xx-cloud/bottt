using System;
using System.Collections.Generic;
using System.Linq;
using Apex.Core.Bridge;
using Apex.Core.Safety;
using Apex.Platforms.CTrader;
using System.Text.Json;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    /// <summary>
    /// APEX Bridge dla cTrader. Łączy konto cTrader z aplikacją APEX Desktop:
    /// przesyła specyfikacje instrumentów, zamknięte świece, notowania, stan konta i pozycje,
    /// a wykonuje WYŁĄCZNIE polecenia otwarcia/zmiany SL/zamknięcia pozycji z etykietą APEX.
    /// Zabezpieczenia po stronie platformy (niezależne od APEX Desktop):
    ///  - konto LIVE jest bezwarunkowo blokowane (parametr historyczny nie odblokowuje REAL),
    ///  - zlecenie bez poprawnych SL i TP jest odrzucane,
    ///  - pozycje ręczne i innych botów nigdy nie są modyfikowane ani zamykane,
    ///  - kryptowaluty nie są wysyłane do APEX (filtr po nazwie/aktywach także w APEX Desktop).
    /// </summary>
    [Robot(AccessRights = AccessRights.None, TimeZone = TimeZones.UTC)]
    public class ApexBridge : Robot
    {
        [Parameter("Klucz parowania (z panelu APEX)", DefaultValue = "", Group = "Połączenie")]
        public string PairingKey { get; set; } = "";

        [Parameter("Adres APEX Desktop", DefaultValue = "ws://127.0.0.1:5077/bridge", Group = "Połączenie")]
        public string ServerUrl { get; set; } = "ws://127.0.0.1:5077/bridge";

        [Parameter("Historyczny LIVE (ignorowany – tylko DEMO)", DefaultValue = false, Group = "Bezpieczeństwo")]
        public bool AllowLive { get; set; }

        [Parameter("Dokładna etykieta pozycji APEX", DefaultValue = "APEX", Group = "Bezpieczeństwo")]
        public string LabelPrefix { get; set; } = "APEX";

        [Parameter("Maks. subskrybowanych instrumentów", DefaultValue = 60, MinValue = 1, MaxValue = 200, Group = "Wydajność")]
        public int MaxSymbols { get; set; }

        private const string Version = "1.0.0";
        private WebSocketClient? _ws;
        private bool _welcomed;
        private DateTime _nextReconnect = DateTime.MinValue;
        private DateTime _lastAccount = DateTime.MinValue;
        private readonly Dictionary<string, Symbol> _symbols = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(string, string), Bars> _bars = new();
        private readonly Dictionary<(string, string), Action<BarOpenedEventArgs>> _barHandlers = new();
        private readonly Dictionary<string, Ticks> _ticks = new(StringComparer.OrdinalIgnoreCase);
        private CommandJournal? _journal;
        private DateTime _connectedAt;
        private DateTime _lastMetadata;
        private long _accountNumber;
        private string _accountBroker = "";
        private string _accountCurrency = "";
        private TextBlock? _status;
        private bool _blocked;

        protected override void OnStart()
        {
            _status = new TextBlock { Text = "APEX Bridge – uruchamianie…", Margin = new Thickness(10), FontSize = 13, ForegroundColor = Color.White, BackgroundColor = Color.FromArgb(200, 18, 22, 32), Padding = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            Chart.AddControl(_status);

            if (!PlatformSafety.DemoAllowed(Account.IsLive, AllowLive))
            {
                _blocked = true;
                SetStatus($"ZABLOKOWANO: konto REAL {Account.Number}. Ta wersja obsługuje wyłącznie DEMO.");
                Print("APEX Bridge: konto LIVE – most zatrzymany (bezpieczeństwo).");
                Stop();
                return;
            }
            if (string.IsNullOrWhiteSpace(PairingKey))
            {
                SetStatus("Brak klucza parowania. Skopiuj go z panelu APEX (Połączenia platform) do parametrów cBota.");
                Print("APEX Bridge: brak klucza parowania.");
                Stop();
                return;
            }
            if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri) || uri.Scheme != "ws" || uri.Host != "127.0.0.1" || uri.AbsolutePath != "/bridge" || !string.IsNullOrEmpty(uri.UserInfo))
            { Print("APEX Bridge: dozwolony jest tylko lokalny adres ws://127.0.0.1:PORT/bridge."); Stop(); return; }
            if (string.IsNullOrWhiteSpace(LabelPrefix)) { Print("APEX Bridge: etykieta nie może być pusta."); Stop(); return; }
            _accountNumber = Account.Number;
            _accountBroker = Account.BrokerName ?? "";
            _accountCurrency = Account.Asset?.Name ?? "";
            string storageKey = PlatformSafety.StorageKey("Bridge", _accountBroker, _accountNumber, LabelPrefix);
            string? saved;
            try { saved = LocalStorage.GetString(storageKey, LocalStorageScope.Type); }
            catch (Exception) { saved = "NIEDOSTEPNY"; }
            _journal = new CommandJournal(saved, value =>
            {
                LocalStorage.SetString(storageKey, value, LocalStorageScope.Type);
                LocalStorage.Flush(LocalStorageScope.Type);
            });
            Positions.Opened += _ => SendPositions();
            Positions.Modified += _ => SendPositions();
            Positions.Closed += OnPositionClosed;
            Timer.Start(TimeSpan.FromSeconds(1));
            Connect();
        }

        private void SetStatus(string s)
        {
            if (_status != null) _status.Text = "APEX Bridge " + Version + " – " + s;
        }

        // ------------------------------------------------------------------ połączenie

        private void Connect()
        {
            try
            {
                _welcomed = false;
                var previous = _ws;
                _ws = null; // Discard callbacks already queued by the old transport.
                previous?.Dispose();
                var socket = new WebSocketClient(new WebSocketClientOptions { KeepAliveInterval = TimeSpan.FromSeconds(20), Timeout = TimeSpan.FromSeconds(10) });
                _ws = socket;
                socket.TextReceived += args =>
                {
                    // Capture only CLR data off-thread; all cTrader/network state is touched on main.
                    var receivedAt = DateTime.UtcNow;
                    var json = args.Text;
                    BeginInvokeOnMainThread(() =>
                    {
                    if (!ReferenceEquals(_ws, socket) || socket.State != WebSocketClientState.Open || _blocked) return;
                    if (BridgeProtocol.ReadType(json) == "open" && !PlatformSafety.Fresh(receivedAt, DateTime.UtcNow, 5))
                    {
                        var expired = BridgeProtocol.Deserialize<OpenCmd>(json);
                        if (expired != null) Send(Reject(expired.Id, "Timeout", "polecenie otwarcia wygasło w kolejce wątku platformy"));
                        return;
                    }
                    try { Handle(json); }
                    catch (Exception ex) { Print("APEX Bridge: nieprawidłowy komunikat: " + ex.Message); }
                    });
                };
                socket.Disconnected += _ => BeginInvokeOnMainThread(() =>
                {
                    if (!ReferenceEquals(_ws, socket)) return;
                    _welcomed = false;
                    SetStatus("rozłączono – ponawiam połączenie…");
                    _nextReconnect = DateTime.UtcNow.AddSeconds(5);
                });
                socket.Connect(new Uri(ServerUrl));
                _connectedAt = DateTime.UtcNow;
                Send(new HelloMsg
                {
                    Platform = "cTrader",
                    BridgeVersion = Version,
                    Key = PairingKey.Trim(),
                    Account = AccountDto(),
                    Time = BridgeProtocol.ToUnixMs(Server.TimeInUtc),
                });
                SetStatus("połączono, oczekiwanie na akceptację APEX…");
            }
            catch (Exception ex)
            {
                SetStatus("brak połączenia z APEX Desktop (" + ex.Message + ") – ponowię za 5 s");
                _nextReconnect = DateTime.UtcNow.AddSeconds(5);
            }
        }

        private void Send<T>(T msg)
        {
            try
            {
                if (_ws != null && _ws.State == WebSocketClientState.Open) _ws.Send(BridgeProtocol.Serialize(msg));
            }
            catch (Exception ex)
            {
                Print("APEX Bridge: błąd wysyłania – " + ex.Message);
            }
        }

        protected override void OnTimer()
        {
            if (_blocked) return;
            if (Account.IsLive || Account.Number != _accountNumber || (Account.BrokerName ?? "") != _accountBroker || (Account.Asset?.Name ?? "") != _accountCurrency)
            { _blocked = true; _welcomed = false; _ws?.Dispose(); Print("APEX Bridge: zmiana tożsamości konta lub konto REAL – zatrzymano."); Stop(); return; }
            if (_ws == null || _ws.State != WebSocketClientState.Open)
            {
                if (DateTime.UtcNow >= _nextReconnect) Connect();
                return;
            }
            if (!_welcomed)
            {
                if (DateTime.UtcNow - _connectedAt > TimeSpan.FromSeconds(15))
                { _ws.Dispose(); _ws = null; _nextReconnect = DateTime.UtcNow.AddSeconds(5); }
                return;
            }
            SendQuotes();
            if ((DateTime.UtcNow - _lastMetadata).TotalSeconds >= 30)
            {
                _lastMetadata = DateTime.UtcNow;
                // Refresh rates/costs without triggering a new universe/subscription on the host.
                if (_symbols.Count == 0) SendSymbols(); // Retry initially unavailable FX/metadata instead of remaining empty forever.
                else Send(new SymbolsMsg { Items = _symbols.Values.Select(s => CTraderData.Describe(s, this)).ToList(), Final = false });
            }
            SendHeartbeat();
        }

        private void SendHeartbeat()
        {
            if (!_welcomed || (DateTime.UtcNow - _lastAccount).TotalSeconds < 2) return;
            _lastAccount = DateTime.UtcNow;
            Send(new AccountMsg { Account = AccountDto() });
            SendPositions();
        }

        protected override void OnStop()
        {
            try
            {
                Send(new SimpleMsg { Type = "bye", Reason = "cBot zatrzymany" });
                _welcomed = false;
                ClearSubscriptions();
                _ws?.Close(WebSocketClientCloseStatus.NormalClosure);
                _ws?.Dispose();
                _ws = null;
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------ komunikaty z APEX

        private void Handle(string json)
        {
            string? type = BridgeProtocol.ReadType(json);
            if (type != "welcome" && (!_welcomed || _blocked)) return;
            switch (type)
            {
                case "welcome":
                    {
                        var w = BridgeProtocol.Deserialize<WelcomeMsg>(json);
                        if (w == null) return;
                        if (!w.Accepted)
                        {
                            SetStatus("APEX odrzucił połączenie: " + w.Reason);
                            Print("APEX Bridge: odrzucono – " + w.Reason);
                            _blocked = true;
                            _welcomed = false;
                            _ws?.Dispose();
                            Stop();
                            return;
                        }
                        if (!PlatformSafety.OwnLabel(w.Label, LabelPrefix))
                        { Print("APEX Bridge: etykieta hosta nie zgadza się z dokładną etykietą instancji."); _blocked = true; _ws?.Dispose(); Stop(); return; }
                        _welcomed = true;
                        SetStatus($"połączono z APEX {w.HostVersion} – konto {(Account.IsLive ? "LIVE" : "DEMO")} {Account.Number}");
                        SendSymbols();
                        Send(new AccountMsg { Account = AccountDto() });
                        SendPositions();
                        break;
                    }
                case "subscribe":
                    {
                        var m = BridgeProtocol.Deserialize<SubscribeMsg>(json);
                        if (m != null) OnSubscribe(m);
                        break;
                    }
                case "open": { var c = BridgeProtocol.Deserialize<OpenCmd>(json); if (c != null) ExecuteCommand(c.Id, c, c.Label + "|" + c.Comment, () => OnOpen(c)); break; }
                case "modify": { var c = BridgeProtocol.Deserialize<ModifyCmd>(json); if (c != null) ExecuteCommand(c.Id, c, null, () => OnModify(c)); break; }
                case "close": { var c = BridgeProtocol.Deserialize<CloseCmd>(json); if (c != null) ExecuteCommand(c.Id, c, null, () => OnClose(c)); break; }
                case "margin": { var c = BridgeProtocol.Deserialize<MarginCmd>(json); if (c != null) OnMargin(c); break; }
                case "ping": Send(new SimpleMsg { Type = "pong" }); break;
            }
        }

        // ------------------------------------------------------------------ dane

        private AccountDto AccountDto() => new()
        {
            Number = Account.Number,
            Broker = Account.BrokerName ?? "",
            Currency = Account.Asset?.Name ?? "",
            IsLive = Account.IsLive,
            Hedging = Account.AccountType == AccountType.Hedged,
            Balance = Account.Balance,
            Equity = Account.Equity,
            Margin = Account.Margin,
            FreeMargin = Account.FreeMargin,
            MarginLevel = Account.MarginLevel,
        };

        private void SendSymbols()
        {
            var names = Symbols.ToArray();
            var items = new List<SymbolDto>();
            for (int i = 0; i < names.Length; i += 100)
            {
                Symbol[] infos;
                try { infos = Symbols.GetSymbols(names.Skip(i).Take(100).ToArray()); }
                catch (Exception ex) { Print("APEX Bridge: błąd odczytu symboli – " + ex.Message); continue; }
                foreach (var s in infos)
                {
                    if (s == null || s.IsArchived) continue;
                    if (InstrumentClassifier.IsCrypto(s.Name, s.Description, s.BaseAsset?.Name, s.QuoteAsset?.Name)) continue;
                    items.Add(CTraderData.Describe(s, this));
                }
                SendHeartbeat();
            }
            for (int i = 0; i < items.Count; i += 200)
                Send(new SymbolsMsg { Items = items.Skip(i).Take(200).ToList(), Final = i + 200 >= items.Count });
            if (items.Count == 0) Send(new SymbolsMsg { Items = new List<SymbolDto>(), Final = true });
            Send(new LogMsg { Level = "info", Message = $"Wysłano {items.Count} symboli." });
        }

        private static TimeFrame? ToTf(string tf) => tf switch
        {
            "M5" => TimeFrame.Minute5,
            "M15" => TimeFrame.Minute15,
            "M30" => TimeFrame.Minute30,
            "H1" => TimeFrame.Hour,
            "H4" => TimeFrame.Hour4,
            "D1" => TimeFrame.Daily,
            _ => null,
        };

        private void ClearSubscriptions()
        {
            foreach (var kv in _barHandlers)
                if (_bars.TryGetValue(kv.Key, out var bars)) bars.BarOpened -= kv.Value;
            _barHandlers.Clear();
            _bars.Clear();
            _symbols.Clear();
            _ticks.Clear();
        }

        private void OnSubscribe(SubscribeMsg m)
        {
            ClearSubscriptions();
            int count = 0;
            foreach (var name in (m.Symbols ?? new()).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (count >= MaxSymbols) break;
                Symbol? sym;
                try { sym = Symbols.GetSymbol(name); }
                catch (Exception) { sym = null; }
                if (sym == null || InstrumentClassifier.IsCrypto(sym.Name, sym.Description, sym.BaseAsset?.Name, sym.QuoteAsset?.Name)) continue;
                try { _ticks[name] = MarketData.GetTicks(name); }
                catch (Exception ex) { Send(new LogMsg { Level = "warn", Message = "Brak ticków " + name + ": " + ex.Message }); continue; }
                _symbols[name] = sym;
                count++;
                foreach (var tfName in (m.Tfs ?? new()).Distinct())
                {
                    var tf = ToTf(tfName);
                    if (tf == null) continue;
                    int want = m.History != null && m.History.TryGetValue(tfName, out var h) ? Math.Clamp(h, 1, 5000) : 500;
                    Bars bars;
                    try
                    {
                        bars = MarketData.GetBars(tf, name);
                        for (int guard = 0; bars.Count < want + 1 && guard < 50; guard++)
                        {
                            if (bars.LoadMoreHistory() <= 0) break;
                            SendHeartbeat(); // Long history loads must not starve the host's 30s liveness timer.
                        }
                    }
                    catch (Exception ex) { Send(new LogMsg { Level = "warn", Message = $"Brak świec {name} {tfName}: {ex.Message}" }); continue; }
                    _bars[(name, tfName)] = bars;
                    var list = new List<double[]>();
                    int last = bars.Count - 2; // Never transmit the forming bar.
                    for (int i = Math.Max(0, last - want + 1); i <= last; i++) list.Add(ToArr(bars[i]));
                    Send(new BarsMsg { Symbol = name, Tf = tfName, Snapshot = true, Bars = list });
                    if (list.Count < want) Send(new LogMsg { Level = "warn", Message = $"Niepełna historia {name} {tfName}: {list.Count}/{want}" });
                    string n = name, t = tfName;
                    Action<BarOpenedEventArgs> handler = args => OnBarOpened(n, t, args.Bars);
                    _barHandlers[(name, tfName)] = handler;
                    bars.BarOpened += handler;
                }
            }
            SendQuotes();
            Send(new LogMsg { Level = "info", Message = $"Subskrypcja: {count} instrumentów." });
        }

        private static double[] ToArr(Bar b) => new[] { (double)BridgeProtocol.ToUnixSec(DateTime.SpecifyKind(b.OpenTime, DateTimeKind.Utc)), b.Open, b.High, b.Low, b.Close, (double)b.TickVolume };

        private void OnBarOpened(string symbol, string tf, Bars bars)
        {
            if (!_welcomed || bars.Count < 2) return;
            Send(new BarsMsg { Symbol = symbol, Tf = tf, Snapshot = false, Bars = new List<double[]> { ToArr(bars[bars.Count - 2]) } });
        }

        private void SendQuotes()
        {
            if (_symbols.Count == 0) return;
            var items = new List<QuoteDto>(_symbols.Count);
            foreach (var kv in _symbols)
            {
                if (!_ticks.TryGetValue(kv.Key, out var ticks)) continue;
                var quote = CTraderData.Quote(kv.Value, ticks, this);
                if (quote != null) items.Add(quote); // t is the broker tick's time, not timer/receipt time.
            }
            Send(new QuotesMsg { Items = items });
        }

        private PositionDto ToDto(Position p) => new()
        {
            Id = p.Id,
            Symbol = p.SymbolName,
            Side = p.TradeType == TradeType.Buy ? "buy" : "sell",
            Units = p.VolumeInUnits,
            Entry = p.EntryPrice,
            EntryTime = BridgeProtocol.ToUnixMs(DateTime.SpecifyKind(p.EntryTime, DateTimeKind.Utc)),
            Sl = p.StopLoss,
            Tp = p.TakeProfit,
            Label = p.Label ?? "",
            Comment = p.Comment ?? "",
            Net = p.NetProfit,
            Gross = p.GrossProfit,
            Commission = p.Commissions,
            Swap = p.Swap,
            Price = p.CurrentPrice,
            Margin = p.Margin,
        };

        private void SendPositions()
        {
            if (!_welcomed) return;
            Send(new PositionsMsg { Items = Positions.Select(ToDto).ToList() });
        }

        private bool IsOwn(Position p) => PlatformSafety.OwnLabel(p.Label, LabelPrefix);

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            var p = args.Position;
            if (!IsOwn(p)) { SendPositions(); return; }
            // Suma wszystkich transakcji zamykających (także częściowych) tej pozycji.
            double gross = 0, comm = 0, swap = 0, net = 0, exit = p.CurrentPrice, units = 0;
            DateTime exitTime = Server.TimeInUtc;
            try
            {
                var deals = History.FindByPositionId(p.Id);
                foreach (var h in deals.OrderBy(h => h.ClosingTime))
                {
                    gross += h.GrossProfit; comm += h.Commissions; swap += h.Swap; net += h.NetProfit; units += h.VolumeInUnits;
                    exit = h.ClosingPrice; exitTime = h.ClosingTime;
                }
                if (deals.Length == 0) { gross = p.GrossProfit; comm = p.Commissions; swap = p.Swap; net = p.NetProfit; units = p.VolumeInUnits; }
            }
            catch (Exception) { gross = p.GrossProfit; comm = p.Commissions; swap = p.Swap; net = p.NetProfit; units = p.VolumeInUnits; }
            string reason = args.Reason switch
            {
                PositionCloseReason.StopLoss => "StopLoss",
                PositionCloseReason.TakeProfit => "TakeProfit",
                PositionCloseReason.StopOut => "StopOut",
                _ => "Closed",
            };
            SendPositions();
            Send(new ClosedMsg
            {
                Position = new ClosedDto
                {
                    Id = p.Id, Symbol = p.SymbolName, Label = p.Label ?? "", Comment = p.Comment ?? "", Side = p.TradeType == TradeType.Buy ? "buy" : "sell",
                    Units = units, Entry = p.EntryPrice, Exit = exit, EntryTime = BridgeProtocol.ToUnixMs(DateTime.SpecifyKind(p.EntryTime, DateTimeKind.Utc)),
                    ExitTime = BridgeProtocol.ToUnixMs(DateTime.SpecifyKind(exitTime, DateTimeKind.Utc)), Gross = gross, Commission = comm, Swap = swap, Net = net, Reason = reason,
                },
            });
        }

        // ------------------------------------------------------------------ polecenia handlowe

        private static string StatusOf(TradeResult r)
        {
            if (r.IsSuccessful) return "filled";
            var e = r.Error;
            return e == ErrorCode.Timeout || e == ErrorCode.Disconnected || e == ErrorCode.TechnicalError || e == null ? "uncertain" : "rejected";
        }

        private static ResultMsg Result(string id, TradeResult r, long? positionId, double? price) =>
            new() { Id = id, Status = StatusOf(r), PositionId = positionId, Price = price, Error = r.Error?.ToString(), Message = r.IsSuccessful ? "" : r.ToString() };

        private static ResultMsg Reject(string id, string error, string message) =>
            new() { Id = id, Status = "rejected", Error = error, Message = message };

        private bool SafeAccount => !_blocked && !Account.IsLive && Account.Number == _accountNumber &&
            (Account.BrokerName ?? "") == _accountBroker && (Account.Asset?.Name ?? "") == _accountCurrency;

        private void ExecuteCommand<T>(string id, T command, string? signal, Func<ResultMsg> execute)
        {
            if (!SafeAccount || !_welcomed || !Server.IsConnected)
            { Send(Reject(id, "NoTradingPermission", "brak aktywnej sesji DEMO/brokera")); return; }
            using var doc = JsonDocument.Parse(BridgeProtocol.Serialize(command));
            var fields = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var field in doc.RootElement.EnumerateObject())
                if (field.Name != "id") fields.Add(field.Name, field.Value);
            var response = _journal?.Run(id, BridgeProtocol.Serialize(fields), signal, execute) ?? Reject(id, "TechnicalError", "brak rejestru poleceń");
            SendPositions();
            Send(response);
        }

        private ResultMsg OnOpen(OpenCmd c)
        {
            if (!SafeAccount) return Reject(c.Id, "NoTradingPermission", "konto REAL lub zmiana konta");
            if (!PlatformSafety.OwnLabel(c.Label, LabelPrefix) || string.IsNullOrWhiteSpace(c.Comment)) return Reject(c.Id, "InvalidRequest", "nieprawidłowa etykieta lub brak identyfikatora sygnału");
            if (!PlatformSafety.Protection(c.SlPips, c.TpPips)) return Reject(c.Id, "InvalidStopLossTakeProfit", "wymagane skończone dodatnie SL i TP");
            Symbol? sym = _symbols.TryGetValue(c.Symbol, out var s0) ? s0 : null;
            if (sym == null) return Reject(c.Id, "UnknownSymbol", "symbol nie jest subskrybowany");
            if (InstrumentClassifier.IsCrypto(sym.Name, sym.Description, sym.BaseAsset?.Name, sym.QuoteAsset?.Name)) return Reject(c.Id, "InvalidRequest", "kryptowaluty wykluczone");
            var side = BridgeProtocol.ParseSide(c.Side);
            if (side == null) return Reject(c.Id, "InvalidRequest", "nieznany kierunek");
            if (!sym.IsTradingEnabled || sym.TradingMode != SymbolTradingMode.FullAccess || !sym.MarketHours.IsOpened()) return Reject(c.Id, "MarketClosed", "handel niedostępny");
            if (!_ticks.TryGetValue(c.Symbol, out var ticks)) return Reject(c.Id, "Disconnected", "brak ticków");
            var quote = CTraderData.Quote(sym, ticks, this);
            if (quote == null || !PlatformSafety.Fresh(BridgeProtocol.FromUnixMs(quote.T), DateTime.UtcNow, 30))
                return Reject(c.Id, "Disconnected", "brak świeżego notowania brokera");
            var metadata = CTraderData.Describe(sym, this);
            if (metadata.CommissionKind == "Unknown" || !metadata.TradingEnabled) return Reject(c.Id, "InvalidRequest", "koszt lub konwersja waluty niezweryfikowane");
            if (!CTraderData.StopsAllowed(sym, c.SlPips, c.TpPips)) return Reject(c.Id, "InvalidStopLossTakeProfit", "SL/TP poniżej minimum brokera");
            if (!PlatformSafety.Volume(c.Units, sym.VolumeInUnitsMin, sym.VolumeInUnitsMax)) return Reject(c.Id, "BadVolume", "nieprawidłowy wolumen w jednostkach");
            double vol = sym.NormalizeVolumeInUnits(c.Units, RoundingMode.Down);
            if (Math.Abs(vol - c.Units) > sym.VolumeInUnitsStep * 1e-8) return Reject(c.Id, "BadVolume", "wolumen niezgodny z krokiem brokera");
            if (c.MaxSlipPips.HasValue && !PlatformSafety.Positive(c.MaxSlipPips.Value)) return Reject(c.Id, "InvalidRequest", "nieprawidłowy limit poślizgu");
            // Defence for a first run after upgrade/loss of a journal: never re-open a broker-visible signal.
            var prior = Positions.FirstOrDefault(x => x.Label == c.Label && x.Comment == c.Comment);
            if (prior != null) return new ResultMsg { Id = c.Id, Status = prior.StopLoss.HasValue && prior.TakeProfit.HasValue ? "filled" : "uncertain", PositionId = prior.Id, Price = prior.EntryPrice,
                Message = "sygnał już istnieje u brokera – nie otwarto ponownie" };
            if (History.Any(x => x.Label == c.Label && x.Comment == c.Comment)) return Reject(c.Id, "InvalidRequest", "sygnał już wykonany w historii brokera");
            var tt = side == Apex.Core.Model.TradeDirection.Long ? TradeType.Buy : TradeType.Sell;
            TradeResult r = c.MaxSlipPips is double slip
                ? ExecuteMarketRangeOrder(tt, sym.Name, vol, slip, tt == TradeType.Buy ? sym.Ask : sym.Bid, c.Label, c.SlPips, c.TpPips, c.Comment)
                : ExecuteMarketOrder(tt, sym.Name, vol, c.Label, c.SlPips, c.TpPips, c.Comment);
            if (r.IsSuccessful && r.Position != null && !CTraderData.Protected(r.Position))
            {
                var position = r.Position;
                double sl = position.EntryPrice + (tt == TradeType.Buy ? -1 : 1) * c.SlPips * sym.PipSize;
                double tp = position.EntryPrice + (tt == TradeType.Buy ? 1 : -1) * c.TpPips!.Value * sym.PipSize;
                string closeResult = "";
                bool repaired = PlatformSafety.RepairOrClose(
                    () => ModifyPosition(position, sl, tp, ProtectionType.Absolute).IsSuccessful && CTraderData.Protected(position),
                    () => closeResult = ClosePosition(position).ToString());
                if (!repaired)
                    return new ResultMsg { Id = c.Id, Status = "uncertain", PositionId = position.Id, Error = "InvalidStopLossTakeProfit", Message = "broker nie potwierdził SL/TP; awaryjne zamknięcie: " + closeResult };
            }
            if (r.IsSuccessful && r.Position == null) return new ResultMsg { Id = c.Id, Status = "uncertain", Error = "TechnicalError", Message = "brak pozycji w potwierdzeniu brokera" };
            return Result(c.Id, r, r.Position?.Id, r.Position?.EntryPrice);
        }

        private Position? FindOwn(long id) => Positions.FirstOrDefault(x => x.Id == id && IsOwn(x));

        private ResultMsg OnModify(ModifyCmd c)
        {
            if (!SafeAccount) return Reject(c.Id, "NoTradingPermission", "konto REAL lub zmiana konta");
            var position = FindOwn(c.PositionId);
            if (position == null) return Reject(c.Id, "EntityNotFound", "brak własnej pozycji o dokładnej etykiecie");
            if (!PlatformSafety.Positive(c.Sl)) return Reject(c.Id, "InvalidStopLossTakeProfit", "SL nie może być usunięty ani nieprawidłowy");
            if ((position.TradeType == TradeType.Buy && (c.Sl >= position.Symbol.Bid || (position.StopLoss.HasValue && c.Sl < position.StopLoss.Value))) ||
                (position.TradeType == TradeType.Sell && (c.Sl <= position.Symbol.Ask || (position.StopLoss.HasValue && c.Sl > position.StopLoss.Value))))
                return Reject(c.Id, "InvalidStopLossTakeProfit", "SL po złej stronie lub zwiększający ryzyko");
            var r = position.ModifyStopLossPrice(c.Sl); // cTrader preserves the existing TP.
            return Result(c.Id, r, position.Id, position.StopLoss);
        }

        private ResultMsg OnClose(CloseCmd c)
        {
            if (!SafeAccount) return Reject(c.Id, "NoTradingPermission", "konto REAL lub zmiana konta");
            var position = FindOwn(c.PositionId);
            if (position == null) return Reject(c.Id, "EntityNotFound", "brak własnej pozycji o dokładnej etykiecie");
            if (!PlatformSafety.PartialClose(c.Units, position.VolumeInUnits, position.Symbol.VolumeInUnitsMin, position.Symbol.VolumeInUnitsStep))
                return Reject(c.Id, "BadVolume", "wolumen zamknięcia nieprawidłowy lub zostawia niedozwoloną resztę");
            TradeResult r = c.Units.HasValue && c.Units.Value < position.VolumeInUnits
                ? ClosePosition(position, c.Units.Value) : ClosePosition(position);
            double? fill = r.IsSuccessful ? History.FindByPositionId(position.Id).OrderBy(h => h.ClosingTime).LastOrDefault()?.ClosingPrice : null;
            return Result(c.Id, r, position.Id, fill);
        }

        private void OnMargin(MarginCmd c)
        {
            double? margin = null;
            try
            {
                var sym = _symbols.TryGetValue(c.Symbol, out var s) ? s : null;
                var side = BridgeProtocol.ParseSide(c.Side);
                if (SafeAccount && Server.IsConnected && sym != null && side.HasValue && PlatformSafety.Volume(c.Units, sym.VolumeInUnitsMin, sym.VolumeInUnitsMax))
                {
                    double normalized = sym.NormalizeVolumeInUnits(c.Units, RoundingMode.Down);
                    if (Math.Abs(normalized - c.Units) <= sym.VolumeInUnitsStep * 1e-8)
                    {
                        double value = sym.GetEstimatedMargin(side == Apex.Core.Model.TradeDirection.Short ? TradeType.Sell : TradeType.Buy, normalized);
                        if (double.IsFinite(value) && value >= 0) margin = value;
                    }
                }
            }
            catch (Exception) { margin = null; }
            Send(new MarginResultMsg { Id = c.Id, Margin = margin });
        }
    }
}
