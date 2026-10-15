using System;
using System.Collections.Generic;
using System.Linq;
using Apex.Core.Bridge;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    /// <summary>
    /// APEX Bridge dla cTrader. Łączy konto cTrader z aplikacją APEX Desktop:
    /// przesyła specyfikacje instrumentów, zamknięte świece, notowania, stan konta i pozycje,
    /// a wykonuje WYŁĄCZNIE polecenia otwarcia/zmiany SL/zamknięcia pozycji z etykietą APEX.
    /// Zabezpieczenia po stronie platformy (niezależne od APEX Desktop):
    ///  - konto LIVE jest blokowane, dopóki parametr "Zezwól na LIVE" = Tak,
    ///  - zlecenie bez SL jest odrzucane,
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

        [Parameter("Zezwól na konto LIVE", DefaultValue = false, Group = "Bezpieczeństwo")]
        public bool AllowLive { get; set; }

        [Parameter("Prefiks etykiety pozycji APEX", DefaultValue = "APEX", Group = "Bezpieczeństwo")]
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
        private readonly HashSet<string> _barHandlers = new();
        private TextBlock? _status;
        private bool _blocked;

        protected override void OnStart()
        {
            _status = new TextBlock { Text = "APEX Bridge – uruchamianie…", Margin = new Thickness(10), FontSize = 13, ForegroundColor = Color.White, BackgroundColor = Color.FromArgb(200, 18, 22, 32), Padding = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            Chart.AddControl(_status);

            if (Account.IsLive && !AllowLive)
            {
                _blocked = true;
                SetStatus($"ZABLOKOWANO: konto LIVE {Account.Number}. Most działa tylko na DEMO, chyba że świadomie włączysz parametr \"Zezwól na konto LIVE\".");
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
                _ws?.Dispose();
                _welcomed = false;
                _ws = new WebSocketClient(new WebSocketClientOptions { KeepAliveInterval = TimeSpan.FromSeconds(20), Timeout = TimeSpan.FromSeconds(10) });
                _ws.TextReceived += args => BeginInvokeOnMainThread(() => Handle(args.Text));
                _ws.Disconnected += _ => BeginInvokeOnMainThread(() => { _welcomed = false; SetStatus("rozłączono – ponawiam połączenie…"); _nextReconnect = DateTime.UtcNow.AddSeconds(5); });
                _ws.Connect(new Uri(ServerUrl));
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
            if (_ws == null || _ws.State != WebSocketClientState.Open)
            {
                if (DateTime.UtcNow >= _nextReconnect) Connect();
                return;
            }
            if (!_welcomed) return;
            SendQuotes();
            if ((DateTime.UtcNow - _lastAccount).TotalSeconds >= 2)
            {
                _lastAccount = DateTime.UtcNow;
                Send(new AccountMsg { Account = AccountDto() });
                SendPositions();
            }
        }

        protected override void OnStop()
        {
            try
            {
                Send(new SimpleMsg { Type = "bye", Reason = "cBot zatrzymany" });
                _ws?.Close(WebSocketClientCloseStatus.NormalClosure);
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------ komunikaty z APEX

        private void Handle(string json)
        {
            string? type = BridgeProtocol.ReadType(json);
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
                            return;
                        }
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
                case "open": { var c = BridgeProtocol.Deserialize<OpenCmd>(json); if (c != null) OnOpen(c); break; }
                case "modify": { var c = BridgeProtocol.Deserialize<ModifyCmd>(json); if (c != null) OnModify(c); break; }
                case "close": { var c = BridgeProtocol.Deserialize<CloseCmd>(json); if (c != null) OnClose(c); break; }
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

        private static string CommissionKind(SymbolCommissionType t) => t switch
        {
            SymbolCommissionType.UsdPerMillionUsdVolume => "UsdPerMillionUsdVolume",
            SymbolCommissionType.UsdPerOneLot => "UsdPerLot",
            SymbolCommissionType.PercentageOfTradingVolume => "PercentOfVolume",
            SymbolCommissionType.QuoteCurrencyPerOneLot => "QuoteCurrencyPerLot",
            _ => "Unknown",
        };

        private double Convert1(string from)
        {
            try { return AssetConverter.Convert(1.0, from, Account.Asset); }
            catch (Exception) { return 0; }
        }

        private void SendSymbols()
        {
            var names = Symbols.ToArray();
            var items = new List<SymbolDto>();
            double usdToAcc = Convert1("USD");
            for (int i = 0; i < names.Length; i += 100)
            {
                Symbol[] infos;
                try { infos = Symbols.GetSymbols(names.Skip(i).Take(100).ToArray()); }
                catch (Exception ex) { Print("APEX Bridge: błąd odczytu symboli – " + ex.Message); continue; }
                foreach (var s in infos)
                {
                    if (s == null || s.IsArchived) continue;
                    double minStop = s.MinDistanceType == SymbolMinDistanceType.Pips ? s.MinStopLossDistance : 0;
                    items.Add(new SymbolDto
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
                        MinStopPips = minStop,
                        TradingEnabled = s.IsTradingEnabled && s.TradingMode == SymbolTradingMode.FullAccess,
                        CloseOnly = s.TradingMode == SymbolTradingMode.CloseOnly,
                        CommissionKind = CommissionKind(s.CommissionType),
                        CommissionValue = s.Commission,
                        UsdToAccount = usdToAcc,
                        QuoteToAccount = s.QuoteAsset != null ? Convert1(s.QuoteAsset.Name) : 0,
                    });
                }
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

        private void OnSubscribe(SubscribeMsg m)
        {
            int count = 0;
            foreach (var name in m.Symbols.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (count >= MaxSymbols) break;
                Symbol? sym;
                try { sym = Symbols.GetSymbol(name); }
                catch (Exception) { sym = null; }
                if (sym == null) { Send(new LogMsg { Level = "warn", Message = "Nieznany symbol: " + name }); continue; }
                _symbols[name] = sym;
                count++;
                foreach (var tfName in m.Tfs)
                {
                    var tf = ToTf(tfName);
                    if (tf == null) continue;
                    int want = m.History.TryGetValue(tfName, out var h) ? Math.Min(h, 5000) : 500;
                    Bars bars;
                    try { bars = MarketData.GetBars(tf, name); }
                    catch (Exception ex) { Send(new LogMsg { Level = "warn", Message = $"Brak świec {name} {tfName}: {ex.Message}" }); continue; }
                    int guard = 0;
                    while (bars.Count < want + 1 && guard++ < 10)
                    {
                        int loaded = bars.LoadMoreHistory();
                        if (loaded <= 0) break;
                    }
                    _bars[(name, tfName)] = bars;
                    // Ostatnia świeca w Bars jest w trakcie tworzenia – wysyłamy wyłącznie zamknięte (0..Count-2).
                    var list = new List<double[]>();
                    int last = bars.Count - 2;
                    for (int i = Math.Max(0, last - want + 1); i <= last; i++) list.Add(ToArr(bars[i]));
                    Send(new BarsMsg { Symbol = name, Tf = tfName, Snapshot = true, Bars = list });
                    string key = name + "|" + tfName;
                    if (_barHandlers.Add(key))
                    {
                        string n = name, t = tfName;
                        bars.BarOpened += args => OnBarOpened(n, t, args.Bars);
                    }
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
            long now = BridgeProtocol.ToUnixMs(Server.TimeInUtc);
            foreach (var kv in _symbols)
            {
                var s = kv.Value;
                bool open;
                long ttc;
                try
                {
                    open = s.MarketHours.IsOpened();
                    var t = s.MarketHours.TimeTillClose();
                    ttc = open ? (long)t.TotalSeconds : -1;
                }
                catch (Exception) { open = false; ttc = -1; }
                if (double.IsNaN(s.Bid) || double.IsNaN(s.Ask) || s.Bid <= 0) continue;
                items.Add(new QuoteDto { S = kv.Key, B = s.Bid, A = s.Ask, T = now, Pv = s.PipValue, O = open, Ttc = ttc });
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

        private bool IsOwn(Position p) => !string.IsNullOrEmpty(p.Label) && p.Label.StartsWith(LabelPrefix, StringComparison.Ordinal);

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
                foreach (var h in deals)
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

        private void Reply(string id, TradeResult r, long? positionId, double? price)
        {
            SendPositions();
            Send(new ResultMsg { Id = id, Status = StatusOf(r), PositionId = positionId, Price = price, Error = r.Error?.ToString(), Message = r.IsSuccessful ? "" : r.ToString() });
        }

        private void Reject(string id, string error, string message) =>
            Send(new ResultMsg { Id = id, Status = "rejected", Error = error, Message = message });

        private bool LiveBlocked(string id)
        {
            if (Account.IsLive && !AllowLive)
            {
                Reject(id, "NoTradingPermission", "Konto LIVE zablokowane w moście (parametr \"Zezwól na konto LIVE\").");
                return true;
            }
            return false;
        }

        private void OnOpen(OpenCmd c)
        {
            if (LiveBlocked(c.Id)) return;
            if (string.IsNullOrEmpty(c.Label) || !c.Label.StartsWith(LabelPrefix, StringComparison.Ordinal)) { Reject(c.Id, "InvalidRequest", "nieprawidłowa etykieta"); return; }
            if (!(c.SlPips > 0)) { Reject(c.Id, "InvalidStopLossTakeProfit", "zlecenie bez SL jest niedozwolone"); return; }
            Symbol? sym = _symbols.TryGetValue(c.Symbol, out var s0) ? s0 : null;
            try { sym ??= Symbols.GetSymbol(c.Symbol); } catch (Exception) { }
            if (sym == null) { Reject(c.Id, "UnknownSymbol", "nieznany symbol"); return; }
            var side = BridgeProtocol.ParseSide(c.Side);
            if (side == null) { Reject(c.Id, "InvalidRequest", "nieznany kierunek"); return; }
            double vol = sym.NormalizeVolumeInUnits(c.Units, RoundingMode.Down);
            if (vol < sym.VolumeInUnitsMin || vol <= 0) { Reject(c.Id, "BadVolume", "wolumen poniżej minimum"); return; }
            var tt = side == Apex.Core.Model.TradeDirection.Long ? TradeType.Buy : TradeType.Sell;
            TradeResult r;
            if (c.MaxSlipPips is double slip && slip > 0)
            {
                // Zlecenie z limitem poślizgu względem bieżącej ceny.
                double basePrice = tt == TradeType.Buy ? sym.Ask : sym.Bid;
                r = ExecuteMarketRangeOrder(tt, sym.Name, vol, slip, basePrice, c.Label, c.SlPips, c.TpPips, c.Comment);
            }
            else
            {
                r = ExecuteMarketOrder(tt, sym.Name, vol, c.Label, c.SlPips, c.TpPips, c.Comment);
            }
            if (r.IsSuccessful && r.Position != null && r.Position.StopLoss == null)
            {
                // Broker przyjął pozycję bez SL – natychmiastowa próba ustawienia, w razie porażki zamknięcie.
                var p = r.Position;
                double sl = p.TradeType == TradeType.Buy ? p.EntryPrice - c.SlPips * sym.PipSize : p.EntryPrice + c.SlPips * sym.PipSize;
                var m = p.ModifyStopLossPrice(sl);
                if (!m.IsSuccessful) ClosePosition(p);
            }
            Reply(c.Id, r, r.Position?.Id, r.Position?.EntryPrice);
        }

        private Position? FindOwn(long id, string replyId)
        {
            var p = Positions.FirstOrDefault(x => x.Id == id);
            if (p == null) { Reject(replyId, "EntityNotFound", "pozycja nie istnieje"); return null; }
            if (!IsOwn(p)) { Reject(replyId, "InvalidRequest", "to nie jest pozycja APEX – most jej nie dotknie"); return null; }
            return p;
        }

        private void OnModify(ModifyCmd c)
        {
            if (LiveBlocked(c.Id)) return;
            var p = FindOwn(c.PositionId, c.Id);
            if (p == null) return;
            var r = p.ModifyStopLossPrice(c.Sl);
            Reply(c.Id, r, p.Id, c.Sl);
        }

        private void OnClose(CloseCmd c)
        {
            var p = FindOwn(c.PositionId, c.Id);
            if (p == null) return;
            TradeResult r;
            if (c.Units.HasValue && c.Units.Value < p.VolumeInUnits)
            {
                double vol = p.Symbol.NormalizeVolumeInUnits(c.Units.Value, RoundingMode.Down);
                if (vol <= 0) { Reject(c.Id, "BadVolume", "wolumen częściowego zamknięcia za mały"); return; }
                r = ClosePosition(p, vol);
            }
            else r = ClosePosition(p);
            Reply(c.Id, r, p.Id, p.CurrentPrice);
        }

        private void OnMargin(MarginCmd c)
        {
            double? margin = null;
            try
            {
                var sym = _symbols.TryGetValue(c.Symbol, out var s0) ? s0 : Symbols.GetSymbol(c.Symbol);
                var side = BridgeProtocol.ParseSide(c.Side) == Apex.Core.Model.TradeDirection.Short ? TradeType.Sell : TradeType.Buy;
                if (sym != null) margin = sym.GetEstimatedMargin(side, c.Units);
            }
            catch (Exception) { margin = null; }
            Send(new MarginResultMsg { Id = c.Id, Margin = margin });
        }
    }
}
