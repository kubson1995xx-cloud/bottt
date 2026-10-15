using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Bridge;
using Apex.Core.Model;

namespace Apex.PlatformSimulator
{
    public sealed class SimOptions
    {
        public string Url { get; set; } = "ws://127.0.0.1:5077/bridge";
        public string Key { get; set; } = "";
        public long AccountNumber { get; set; } = 900001;
        public bool IsLive { get; set; }
        public string Platform { get; set; } = "Symulator";
        public string Broker { get; set; } = "APEX Symulator";
        public double Balance { get; set; } = 10_000;
        public int Seed { get; set; } = 7;
        public int QuoteIntervalMs { get; set; } = 500;
        /// <summary>Długość generowanej historii H1 (godziny). 6000 h ≈ 250 świec D1.</summary>
        public int HistoryHours { get; set; } = 6000;
        /// <summary>
        /// Tryb przyspieszonego czasu WYŁĄCZNIE do testów interfejsu: co N sekund zamykana jest kolejna świeca H1
        /// (z czasem otwarcia +1h). 0 = wyłączony (świece zamykane w rzeczywistym czasie).
        /// </summary>
        public int TimelapseSeconds { get; set; }
    }

    public sealed class SimSymbol
    {
        public string Name = "";
        public string Description = "";
        public string Base = "";
        public string Quote = "";
        public int Digits;
        public double Pip;
        public double Price;
        public double Spread;
        public double HourVol;
        public double VolMin = 1000;
        public double VolStep = 1000;
        public double Lot = 100_000;
        public string CommissionKind = "UsdPerLot";
        public double Commission = 3.5;
        public bool Open = true;
        public List<Bar> H1 = new();
    }

    public sealed class SimPosition
    {
        public long Id;
        public string Symbol = "";
        public TradeDirection Dir;
        public double Units;
        public double Entry;
        public DateTime EntryTime;
        public double? Sl;
        public double? Tp;
        public string Label = "";
        public string Comment = "";
        public double Commission;
    }

    /// <summary>
    /// Symulator platformy handlowej łączący się z APEX Desktop przez protokół mostu (WebSocket).
    /// Służy do testów integracyjnych i demonstracji panelu bez brokera. Pozwala wstrzykiwać awarie
    /// (brak odpowiedzi na zlecenie, odrzucenie, rozłączenie), aby sprawdzić odporność silnika.
    /// </summary>
    public sealed class PlatformSimulator : IAsyncDisposable
    {
        private readonly SimOptions _o;
        private readonly Random _rng;
        private readonly Dictionary<string, SimSymbol> _symbols = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<SimPosition> _positions = new();
        private readonly object _lock = new();
        private ClientWebSocket? _ws;
        private readonly SemaphoreSlim _send = new(1, 1);
        private long _nextId = 50_000;
        private double _balance;
        private HashSet<string> _subscribed = new(StringComparer.OrdinalIgnoreCase);
        private List<string> _tfs = new();

        public PlatformSimulator(SimOptions o)
        {
            _o = o;
            _rng = new Random(o.Seed);
            _balance = o.Balance;
            Add("EURUSD", "Euro vs US Dollar", "EUR", "USD", 5, 0.0001, 1.0850, 0.00008, 0.0012);
            Add("GBPUSD", "British Pound vs US Dollar", "GBP", "USD", 5, 0.0001, 1.2700, 0.00012, 0.0015);
            Add("USDJPY", "US Dollar vs Japanese Yen", "USD", "JPY", 3, 0.01, 151.20, 0.010, 0.15);
            Add("XAUUSD", "Gold vs US Dollar", "XAU", "USD", 2, 0.01, 2350.0, 0.20, 4.0, volMin: 1, lot: 100);
            Add("US500", "US 500 Index", "US500", "USD", 1, 0.1, 5200, 0.5, 8, volMin: 0.1, lot: 1, commissionKind: "None", commission: 0);
            Add("NAS100", "US Tech 100 Index", "NAS100", "USD", 1, 0.1, 18200, 1.5, 35, volMin: 0.1, lot: 1, commissionKind: "None", commission: 0);
            Add("SpotCrude", "Crude Oil WTI", "SpotCrude", "USD", 2, 0.01, 78.5, 0.03, 0.35, volMin: 1, lot: 1, commissionKind: "None", commission: 0);
            Add("BTCUSD", "Bitcoin vs US Dollar", "BTC", "USD", 2, 1, 64000, 30, 450, volMin: 0.01, lot: 1, commissionKind: "None", commission: 0);
            Add("AAPL.US", "Apple Inc", "AAPL", "USD", 2, 0.01, 190, 0.04, 0.9, volMin: 1, lot: 1, commissionKind: "UsdPerLot", commission: 0.02);
            GenerateHistory(o.HistoryHours);
        }

        public ConcurrentQueue<string> Received { get; } = new();
        public bool Accepted { get; private set; }
        public string? RejectReason { get; private set; }
        public int SubscribeCount { get; private set; }
        public IReadOnlyCollection<string> Subscribed => _subscribed;

        /// <summary>Wstrzykiwanie awarii: liczba kolejnych zleceń "open", dla których NIE odeślemy odpowiedzi (ale wykonamy).</summary>
        public int DropOpenResponses { get; set; }
        /// <summary>Liczba kolejnych zleceń "open" do odrzucenia z podanym błędem.</summary>
        public int RejectOpens { get; set; }
        public string RejectError { get; set; } = "NoMoney";
        /// <summary>Otwieraj pozycje bez SL (test zabezpieczenia).</summary>
        public bool IgnoreStopLoss { get; set; }
        public int OpenCommands;

        public List<SimPosition> Positions { get { lock (_lock) return _positions.ToList(); } }

        private void Add(string name, string desc, string b, string q, int digits, double pip, double price, double spread, double hourVol,
            double volMin = 1000, double lot = 100_000, string commissionKind = "UsdPerLot", double commission = 3.5)
        {
            _symbols[name] = new SimSymbol
            {
                Name = name, Description = desc, Base = b, Quote = q, Digits = digits, Pip = pip, Price = price, Spread = spread, HourVol = hourVol,
                VolMin = volMin, VolStep = volMin, Lot = lot, CommissionKind = commissionKind, Commission = commission,
            };
        }

        private double Gauss()
        {
            double u1 = 1.0 - _rng.NextDouble(), u2 = 1.0 - _rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        private void GenerateHistory(int bars)
        {
            var now = DateTime.UtcNow;
            var lastClosedOpen = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddHours(-1);
            foreach (var s in _symbols.Values)
            {
                double p = s.Price;
                var list = new List<Bar>();
                for (int i = bars - 1; i >= 0; i--)
                {
                    var t = lastClosedOpen.AddHours(-i);
                    double o = p;
                    double c = o + s.HourVol * Gauss() * 0.6;
                    double h = Math.Max(o, c) + Math.Abs(s.HourVol * Gauss() * 0.3);
                    double l = Math.Min(o, c) - Math.Abs(s.HourVol * Gauss() * 0.3);
                    list.Add(new Bar(t, Math.Round(o, s.Digits), Math.Round(h, s.Digits), Math.Round(l, s.Digits), Math.Round(c, s.Digits), 100));
                    p = c;
                }
                s.H1 = list;
                s.Price = p;
            }
        }

        /// <summary>Podmienia historię świec H1 instrumentu (scenariusze testowe).</summary>
        public void SetHistory(string symbol, List<Bar> h1)
        {
            lock (_lock)
            {
                var s = _symbols[symbol];
                s.H1 = h1;
                s.Price = h1[^1].Close;
            }
        }

        public void SetMarketOpen(string symbol, bool open) { lock (_lock) _symbols[symbol].Open = open; }

        public void SetPrice(string symbol, double price) { lock (_lock) _symbols[symbol].Price = price; }

        // ------------------------------------------------------------------ połączenie

        public async Task ConnectAsync(CancellationToken ct)
        {
            _ws = new ClientWebSocket();
            await _ws.ConnectAsync(new Uri(_o.Url), ct).ConfigureAwait(false);
            await SendAsync(new HelloMsg
            {
                Platform = _o.Platform,
                BridgeVersion = "sim-1.0",
                Key = _o.Key,
                Account = AccountDto(),
                Time = BridgeProtocol.ToUnixMs(DateTime.UtcNow),
            }).ConfigureAwait(false);
        }

        public async Task RunAsync(CancellationToken ct)
        {
            if (_ws == null) await ConnectAsync(ct).ConfigureAwait(false);
            var recv = Task.Run(() => ReceiveLoop(ct), ct);
            var lastAccount = DateTime.MinValue;
            var lastHour = DateTime.UtcNow.Hour;
            var lastTimelapse = DateTime.UtcNow;
            try
            {
                while (!ct.IsCancellationRequested && _ws!.State == WebSocketState.Open)
                {
                    await Task.Delay(_o.QuoteIntervalMs, ct).ConfigureAwait(false);
                    if (!Accepted) continue;
                    Tick();
                    await SendQuotes().ConfigureAwait(false);
                    var now = DateTime.UtcNow;
                    if (_o.TimelapseSeconds > 0)
                    {
                        if ((now - lastTimelapse).TotalSeconds >= _o.TimelapseSeconds)
                        {
                            lastTimelapse = now;
                            await TimelapseBar().ConfigureAwait(false);
                        }
                    }
                    else if (now.Hour != lastHour)
                    {
                        lastHour = now.Hour;
                        await CloseHourBar().ConfigureAwait(false);
                    }
                    if ((now - lastAccount).TotalSeconds >= 2)
                    {
                        lastAccount = now;
                        await SendAsync(new AccountMsg { Account = AccountDto() }).ConfigureAwait(false);
                        await SendPositions().ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            await recv.ConfigureAwait(false);
        }

        private async Task ReceiveLoop(CancellationToken ct)
        {
            var buf = new byte[256 * 1024];
            var sb = new StringBuilder();
            try
            {
                while (!ct.IsCancellationRequested && _ws!.State == WebSocketState.Open)
                {
                    var r = await _ws.ReceiveAsync(buf, ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                    if (!r.EndOfMessage) continue;
                    string json = sb.ToString();
                    sb.Clear();
                    Received.Enqueue(json);
                    await Handle(json).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
        }

        private async Task Handle(string json)
        {
            string? type = BridgeProtocol.ReadType(json);
            switch (type)
            {
                case "welcome":
                    {
                        var w = BridgeProtocol.Deserialize<WelcomeMsg>(json)!;
                        Accepted = w.Accepted;
                        RejectReason = w.Reason;
                        if (Accepted)
                        {
                            await SendAsync(new SymbolsMsg { Items = _symbols.Values.Select(SymbolDto).ToList(), Final = true }).ConfigureAwait(false);
                            await SendAsync(new AccountMsg { Account = AccountDto() }).ConfigureAwait(false);
                            await SendPositions().ConfigureAwait(false);
                        }
                        break;
                    }
                case "subscribe":
                    {
                        var m = BridgeProtocol.Deserialize<SubscribeMsg>(json)!;
                        SubscribeCount++;
                        _subscribed = new HashSet<string>(m.Symbols, StringComparer.OrdinalIgnoreCase);
                        _tfs = m.Tfs;
                        foreach (var sym in m.Symbols)
                        {
                            if (!_symbols.TryGetValue(sym, out var s)) continue;
                            foreach (var tf in m.Tfs)
                            {
                                var bars = Aggregate(s.H1, tf);
                                int n = m.History.TryGetValue(tf, out var h) ? h : 500;
                                await SendAsync(new BarsMsg { Symbol = sym, Tf = tf, Snapshot = true, Bars = bars.TakeLast(n).Select(ToArr).ToList() }).ConfigureAwait(false);
                            }
                        }
                        await SendQuotes().ConfigureAwait(false);
                        break;
                    }
                case "open": await OnOpen(BridgeProtocol.Deserialize<OpenCmd>(json)!).ConfigureAwait(false); break;
                case "modify": await OnModify(BridgeProtocol.Deserialize<ModifyCmd>(json)!).ConfigureAwait(false); break;
                case "close": await OnClose(BridgeProtocol.Deserialize<CloseCmd>(json)!).ConfigureAwait(false); break;
                case "margin":
                    {
                        var m = BridgeProtocol.Deserialize<MarginCmd>(json)!;
                        double margin = double.NaN;
                        if (_symbols.TryGetValue(m.Symbol, out var s)) margin = m.Units * s.Price * ToUsd(s) / 30.0;
                        await SendAsync(new MarginResultMsg { Id = m.Id, Margin = double.IsNaN(margin) ? null : margin }).ConfigureAwait(false);
                        break;
                    }
            }
        }

        private static double[] ToArr(Bar b) => new[] { (double)BridgeProtocol.ToUnixSec(b.OpenTime), b.Open, b.High, b.Low, b.Close, b.Volume };

        private static List<Bar> Aggregate(List<Bar> h1, string tf)
        {
            if (tf == "H1") return h1;
            int hours = tf == "H4" ? 4 : tf == "D1" ? 24 : 1;
            return h1.GroupBy(b => new DateTime(b.OpenTime.Ticks - b.OpenTime.Ticks % TimeSpan.FromHours(hours).Ticks, DateTimeKind.Utc))
                .Select(g => new Bar(g.Key, g.First().Open, g.Max(x => x.High), g.Min(x => x.Low), g.Last().Close, g.Sum(x => x.Volume)))
                .Where(b => b.OpenTime.AddHours(hours) <= DateTime.UtcNow).ToList();
        }

        // ------------------------------------------------------------------ rynek

        private double ToUsd(SimSymbol s) => s.Quote == "USD" ? 1.0 : s.Quote == "JPY" ? 1.0 / _symbols["USDJPY"].Price : 1.0;

        private SymbolDto SymbolDto(SimSymbol s) => new()
        {
            Name = s.Name, Description = s.Description, BaseAsset = s.Base, QuoteAsset = s.Quote, Digits = s.Digits, PipSize = s.Pip,
            TickSize = s.Pip / (s.Lot == 100_000 ? 10 : 1), PipValue = s.Pip * ToUsd(s), LotSize = s.Lot, VolumeMin = s.VolMin, VolumeStep = s.VolStep,
            VolumeMax = 10_000_000, MinStopPips = 0, TradingEnabled = true, CommissionKind = s.CommissionKind, CommissionValue = s.Commission,
            UsdToAccount = 1, QuoteToAccount = ToUsd(s),
        };

        private AccountDto AccountDto()
        {
            double unreal = 0, margin = 0;
            lock (_lock)
                foreach (var p in _positions)
                {
                    var s = _symbols[p.Symbol];
                    unreal += Pnl(p, s);
                    margin += p.Units * s.Price * ToUsd(s) / 30.0;
                }
            double eq = _balance + unreal;
            return new AccountDto
            {
                Number = _o.AccountNumber, Broker = _o.Broker, Currency = "USD", IsLive = _o.IsLive, Hedging = true,
                Balance = Math.Round(_balance, 2), Equity = Math.Round(eq, 2), Margin = Math.Round(margin, 2), FreeMargin = Math.Round(eq - margin, 2),
                MarginLevel = margin > 0 ? eq / margin * 100 : null,
            };
        }

        private double Bid(SimSymbol s) => Math.Round(s.Price - s.Spread / 2, s.Digits);
        private double Ask(SimSymbol s) => Math.Round(s.Price + s.Spread / 2, s.Digits);

        private double Pnl(SimPosition p, SimSymbol s)
        {
            double exit = p.Dir == TradeDirection.Long ? Bid(s) : Ask(s);
            return (int)p.Dir * (exit - p.Entry) * p.Units * ToUsd(s) - p.Commission;
        }

        private void Tick()
        {
            List<(SimPosition p, double px, string reason)> hits = new();
            lock (_lock)
            {
                foreach (var s in _symbols.Values)
                    if (s.Open) s.Price = Math.Max(s.Pip, s.Price + s.HourVol * 0.02 * Gauss());
                foreach (var p in _positions)
                {
                    var s = _symbols[p.Symbol];
                    double exit = p.Dir == TradeDirection.Long ? Bid(s) : Ask(s);
                    int d = (int)p.Dir;
                    if (p.Sl.HasValue && d * (exit - p.Sl.Value) <= 0) hits.Add((p, p.Sl.Value, "StopLoss"));
                    else if (p.Tp.HasValue && d * (exit - p.Tp.Value) >= 0) hits.Add((p, p.Tp.Value, "TakeProfit"));
                }
            }
            foreach (var (p, px, reason) in hits) _ = ClosePos(p, null, px, reason);
        }

        private async Task SendQuotes()
        {
            var items = new List<QuoteDto>();
            lock (_lock)
                foreach (var name in _subscribed)
                    if (_symbols.TryGetValue(name, out var s))
                        items.Add(new QuoteDto { S = s.Name, B = Bid(s), A = Ask(s), T = BridgeProtocol.ToUnixMs(DateTime.UtcNow), Pv = s.Pip * ToUsd(s), O = s.Open, Ttc = s.Open ? 6 * 3600 : -1 });
            if (items.Count > 0) await SendAsync(new QuotesMsg { Items = items }).ConfigureAwait(false);
        }

        private async Task CloseHourBar()
        {
            var now = DateTime.UtcNow;
            var open = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddHours(-1);
            foreach (var s in _symbols.Values)
            {
                Bar bar;
                lock (_lock)
                {
                    double o = s.H1.Count > 0 ? s.H1[^1].Close : s.Price;
                    double c = s.Price;
                    bar = new Bar(open, o, Math.Max(o, c) + s.HourVol * 0.1, Math.Min(o, c) - s.HourVol * 0.1, c, 100);
                    if (s.H1.Count == 0 || s.H1[^1].OpenTime < open) s.H1.Add(bar);
                }
                if (_subscribed.Contains(s.Name) && _tfs.Contains("H1"))
                    await SendAsync(new BarsMsg { Symbol = s.Name, Tf = "H1", Snapshot = false, Bars = new List<double[]> { ToArr(bar) } }).ConfigureAwait(false);
            }
        }

        /// <summary>Tryb przyspieszony: kolejna świeca H1 (czas otwarcia = poprzednia + 1h) dla wszystkich instrumentów.</summary>
        private async Task TimelapseBar()
        {
            foreach (var s in _symbols.Values)
            {
                Bar bar;
                lock (_lock)
                {
                    var last = s.H1[^1];
                    double o = last.Close;
                    double c = o + s.HourVol * Gauss() * 0.6;
                    double h = Math.Max(o, c) + Math.Abs(s.HourVol * Gauss() * 0.3);
                    double l = Math.Min(o, c) - Math.Abs(s.HourVol * Gauss() * 0.3);
                    bar = new Bar(last.OpenTime.AddHours(1), Math.Round(o, s.Digits), Math.Round(h, s.Digits), Math.Round(l, s.Digits), Math.Round(c, s.Digits), 100);
                    s.H1.Add(bar);
                    s.Price = c;
                }
                if (_subscribed.Contains(s.Name) && _tfs.Contains("H1"))
                    await SendAsync(new BarsMsg { Symbol = s.Name, Tf = "H1", Snapshot = false, Bars = new List<double[]> { ToArr(bar) } }).ConfigureAwait(false);
            }
        }

        /// <summary>Wysyła nową zamkniętą świecę (scenariusze testowe).</summary>
        public Task PushBar(string symbol, string tf, Bar bar)
        {
            lock (_lock) { if (tf == "H1") _symbols[symbol].H1.Add(bar); _symbols[symbol].Price = bar.Close; }
            return SendAsync(new BarsMsg { Symbol = symbol, Tf = tf, Snapshot = false, Bars = new List<double[]> { ToArr(bar) } });
        }

        // ------------------------------------------------------------------ zlecenia

        private async Task OnOpen(OpenCmd c)
        {
            Interlocked.Increment(ref OpenCommands);
            if (RejectOpens > 0)
            {
                RejectOpens--;
                await SendAsync(new ResultMsg { Id = c.Id, Status = "rejected", Error = RejectError, Message = "symulowane odrzucenie" }).ConfigureAwait(false);
                return;
            }
            if (!_symbols.TryGetValue(c.Symbol, out var s)) { await SendAsync(new ResultMsg { Id = c.Id, Status = "rejected", Error = "UnknownSymbol" }).ConfigureAwait(false); return; }
            if (!s.Open) { await SendAsync(new ResultMsg { Id = c.Id, Status = "rejected", Error = "MarketClosed" }).ConfigureAwait(false); return; }
            var dir = BridgeProtocol.ParseSide(c.Side) ?? TradeDirection.Long;
            SimPosition p;
            lock (_lock)
            {
                double fill = dir == TradeDirection.Long ? Ask(s) : Bid(s);
                p = new SimPosition
                {
                    Id = ++_nextId, Symbol = s.Name, Dir = dir, Units = c.Units, Entry = fill, EntryTime = DateTime.UtcNow, Label = c.Label, Comment = c.Comment,
                    Sl = IgnoreStopLoss ? null : Math.Round(fill - (int)dir * c.SlPips * s.Pip, s.Digits),
                    Tp = c.TpPips.HasValue ? Math.Round(fill + (int)dir * c.TpPips.Value * s.Pip, s.Digits) : null,
                    Commission = s.CommissionKind == "UsdPerLot" ? c.Units / s.Lot * s.Commission * 2 : 0,
                };
                _positions.Add(p);
            }
            await SendPositions().ConfigureAwait(false);
            if (DropOpenResponses > 0) { DropOpenResponses--; return; }
            await SendAsync(new ResultMsg { Id = c.Id, Status = "filled", PositionId = p.Id, Price = p.Entry }).ConfigureAwait(false);
        }

        private async Task OnModify(ModifyCmd c)
        {
            SimPosition? p;
            lock (_lock) p = _positions.FirstOrDefault(x => x.Id == c.PositionId);
            if (p == null) { await SendAsync(new ResultMsg { Id = c.Id, Status = "rejected", Error = "EntityNotFound" }).ConfigureAwait(false); return; }
            if (!string.Equals(p.Label, "APEX", StringComparison.Ordinal) && !p.Label.StartsWith("APEX", StringComparison.Ordinal))
            { await SendAsync(new ResultMsg { Id = c.Id, Status = "rejected", Error = "InvalidRequest", Message = "nie pozycja APEX" }).ConfigureAwait(false); return; }
            lock (_lock) p.Sl = c.Sl;
            await SendPositions().ConfigureAwait(false);
            await SendAsync(new ResultMsg { Id = c.Id, Status = "filled", PositionId = p.Id, Price = c.Sl }).ConfigureAwait(false);
        }

        private async Task OnClose(CloseCmd c)
        {
            SimPosition? p;
            lock (_lock) p = _positions.FirstOrDefault(x => x.Id == c.PositionId);
            if (p == null) { await SendAsync(new ResultMsg { Id = c.Id, Status = "rejected", Error = "EntityNotFound" }).ConfigureAwait(false); return; }
            var s = _symbols[p.Symbol];
            double px = p.Dir == TradeDirection.Long ? Bid(s) : Ask(s);
            await ClosePos(p, c.Units, px, "Closed").ConfigureAwait(false);
            await SendAsync(new ResultMsg { Id = c.Id, Status = "filled", PositionId = p.Id, Price = px }).ConfigureAwait(false);
        }

        private async Task ClosePos(SimPosition p, double? units, double px, string reason)
        {
            ClosedDto? closed = null;
            lock (_lock)
            {
                if (!_positions.Contains(p)) return;
                var s = _symbols[p.Symbol];
                double u = units.HasValue ? Math.Min(units.Value, p.Units) : p.Units;
                double gross = (int)p.Dir * (px - p.Entry) * u * ToUsd(s);
                double comm = p.Commission * u / p.Units;
                _balance += gross - comm;
                p.Commission -= comm;
                p.Units -= u;
                if (p.Units <= 1e-9)
                {
                    _positions.Remove(p);
                    closed = new ClosedDto
                    {
                        Id = p.Id, Symbol = p.Symbol, Label = p.Label, Comment = p.Comment, Side = BridgeProtocol.SideOf(p.Dir), Units = u, Entry = p.Entry, Exit = px,
                        EntryTime = BridgeProtocol.ToUnixMs(p.EntryTime), ExitTime = BridgeProtocol.ToUnixMs(DateTime.UtcNow), Gross = gross, Commission = -comm,
                        Swap = 0, Net = gross - comm, Reason = reason,
                    };
                }
            }
            await SendPositions().ConfigureAwait(false);
            if (closed != null) await SendAsync(new ClosedMsg { Position = closed }).ConfigureAwait(false);
        }

        private Task SendPositions()
        {
            List<PositionDto> items;
            lock (_lock)
                items = _positions.Select(p =>
                {
                    var s = _symbols[p.Symbol];
                    return new PositionDto
                    {
                        Id = p.Id, Symbol = p.Symbol, Side = BridgeProtocol.SideOf(p.Dir), Units = p.Units, Entry = p.Entry, EntryTime = BridgeProtocol.ToUnixMs(p.EntryTime),
                        Sl = p.Sl, Tp = p.Tp, Label = p.Label, Comment = p.Comment, Net = Math.Round(Pnl(p, s), 2), Gross = Math.Round(Pnl(p, s) + p.Commission, 2),
                        Commission = -p.Commission, Swap = 0, Price = p.Dir == TradeDirection.Long ? Bid(s) : Ask(s), Margin = p.Units * s.Price * ToUsd(s) / 30,
                    };
                }).ToList();
            return SendAsync(new PositionsMsg { Items = items });
        }

        /// <summary>Ręczna pozycja "innego bota" – silnik APEX nie może jej dotykać.</summary>
        public Task AddForeignPosition(string symbol, string label)
        {
            lock (_lock)
            {
                var s = _symbols[symbol];
                _positions.Add(new SimPosition { Id = ++_nextId, Symbol = symbol, Dir = TradeDirection.Long, Units = s.VolMin, Entry = Ask(s), EntryTime = DateTime.UtcNow, Label = label, Comment = "ręczna" });
            }
            return SendPositions();
        }

        public async Task SendAsync<T>(T msg)
        {
            if (_ws == null || _ws.State != WebSocketState.Open) return;
            var bytes = Encoding.UTF8.GetBytes(BridgeProtocol.Serialize(msg));
            await _send.WaitAsync().ConfigureAwait(false);
            try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false); }
            catch (WebSocketException) { }
            finally { _send.Release(); }
        }

        public async Task DisconnectAsync()
        {
            if (_ws != null && _ws.State == WebSocketState.Open)
            {
                try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "koniec", CancellationToken.None).ConfigureAwait(false); }
                catch (WebSocketException) { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync().ConfigureAwait(false);
            _ws?.Dispose();
        }
    }

    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            string? Arg(string n) { int i = Array.IndexOf(args, n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            var o = new SimOptions
            {
                Url = Arg("--url") ?? "ws://127.0.0.1:5077/bridge",
                Key = Arg("--key") ?? "",
                AccountNumber = long.TryParse(Arg("--account"), out var a) ? a : 900001,
                IsLive = args.Contains("--live"),
                Seed = int.TryParse(Arg("--seed"), out var seed) ? seed : 7,
                HistoryHours = int.TryParse(Arg("--history-hours"), out var hh) ? hh : 6000,
                TimelapseSeconds = int.TryParse(Arg("--timelapse"), out var tl) ? tl : 0,
            };
            Console.WriteLine($"Symulator platformy → {o.Url} (konto {o.AccountNumber}, {(o.IsLive ? "LIVE (symulowane)" : "DEMO")})" +
                (o.TimelapseSeconds > 0 ? $" – TRYB PRZYSPIESZONY: nowa świeca H1 co {o.TimelapseSeconds} s (tylko testy UI)" : ""));
            await using var sim = new PlatformSimulator(o);
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            await sim.ConnectAsync(cts.Token);
            await sim.RunAsync(cts.Token);
            Console.WriteLine(sim.Accepted ? "Zakończono." : "Odrzucono: " + sim.RejectReason);
            return 0;
        }
    }
}
