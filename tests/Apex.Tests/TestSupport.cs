using System;
using System.Collections.Generic;
using System.Linq;
using Apex.Core.Adaptive;
using Apex.Core.Bridge;
using Apex.Core.Engine;
using Apex.Core.Model;
using Apex.Core.Safety;
using Apex.Core.Strategies;

namespace Apex.Tests
{
    /// <summary>Atrapa brokera i rynku z wstrzykiwaniem awarii – do testów silnika bez platformy.</summary>
    public sealed class FakeBroker : IBrokerGateway, IMarketData
    {
        public readonly Dictionary<string, InstrumentSpec> Specs = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, double> Mid = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<PositionInfo> Positions = new();
        public readonly List<OrderRequest> Orders = new();
        public readonly List<(long id, double sl)> Modifications = new();
        public readonly List<long> Closes = new();
        public double Balance = 10_000;
        public double EquityOffset;
        public bool Live;
        public long AccountNumber = 123456;
        public bool Connected = true;
        public bool MarketOpen = true;
        public DateTime Now;
        public double SpreadPips = 0.5;
        public long NextId = 1;

        /// <summary>Tryb odpowiedzi na kolejne zlecenia otwarcia.</summary>
        public Queue<string> OpenModes = new(); // "fill", "reject", "uncertain-filled", "uncertain-none", "fill-no-sl"

        public bool IsConnected => Connected;

        public AccountSnapshot GetAccount() => new()
        {
            Number = AccountNumber,
            IsLive = Live,
            BrokerName = "Pepperstone Test",
            Currency = "USD",
            Balance = Balance,
            Equity = Balance + EquityOffset,
            Margin = 0,
            FreeMargin = Balance + EquityOffset,
            TimeUtc = Now,
        };

        public IReadOnlyList<PositionInfo> GetPositions() => Positions.ToList();

        public void AddInstrument(string symbol, AssetClass cls, double price, double pip, double pipValue, double volMin, double step, string commission = "None", double commissionValue = 0, double lot = 1)
        {
            Specs[symbol] = new InstrumentSpec
            {
                Name = symbol, AssetClass = cls, BaseAsset = symbol.Length >= 6 && cls == AssetClass.Forex ? symbol.Substring(0, 3) : symbol,
                QuoteAsset = "USD", Digits = 5, PipSize = pip, TickSize = pip / 10, PipValuePerUnit = pipValue, LotSize = lot,
                VolumeMin = volMin, VolumeStep = step, VolumeMax = 1e9, TradingEnabled = true,
                CommissionKind = SpecMapper.ParseCommission(commission), CommissionValue = commissionValue,
            };
            Mid[symbol] = price;
        }

        public InstrumentSpec? GetSpec(string symbol) => Specs.TryGetValue(symbol, out var s) ? s : null;

        public Quote? GetQuote(string symbol)
        {
            if (!Mid.TryGetValue(symbol, out var m)) return null;
            double sp = SpreadPips * Specs[symbol].PipSize;
            return new Quote(m - sp / 2, m + sp / 2, Now);
        }

        public bool IsMarketOpen(string symbol, DateTime utc, out TimeSpan timeToClose)
        {
            timeToClose = TimeSpan.FromHours(6);
            return MarketOpen;
        }

        public OrderOutcome OpenMarket(OrderRequest r)
        {
            Orders.Add(r);
            string mode = OpenModes.Count > 0 ? OpenModes.Dequeue() : "fill";
            if (mode == "reject") return OrderOutcome.Rejected(BrokerErrorKind.NoMoney, "brak środków (test)");
            if (mode == "uncertain-none") return OrderOutcome.Uncertain(BrokerErrorKind.Timeout, "timeout (test)");
            var q = GetQuote(r.Symbol)!.Value;
            double fill = q.EntryPrice(r.Direction);
            var spec = Specs[r.Symbol];
            int s = (int)r.Direction;
            var p = new PositionInfo
            {
                Id = NextId++, Symbol = r.Symbol, Direction = r.Direction, VolumeUnits = r.VolumeUnits, EntryPrice = fill, EntryTimeUtc = Now,
                StopLoss = mode == "fill-no-sl" ? null : fill - s * r.StopLossPips * spec.PipSize,
                TakeProfit = r.TakeProfitPips.HasValue ? fill + s * r.TakeProfitPips.Value * spec.PipSize : null,
                Label = r.Label, Comment = r.Comment, CurrentPrice = q.ExitPrice(r.Direction),
            };
            Positions.Add(p);
            return mode == "uncertain-filled" ? OrderOutcome.Uncertain(BrokerErrorKind.Timeout, "timeout po wykonaniu (test)") : OrderOutcome.Filled(p.Id, fill);
        }

        public OrderOutcome ModifyStopLoss(long positionId, double stopLoss)
        {
            int i = Positions.FindIndex(p => p.Id == positionId);
            if (i < 0) return OrderOutcome.Rejected(BrokerErrorKind.EntityNotFound, "brak");
            var p = Positions[i];
            Positions[i] = new PositionInfo
            {
                Id = p.Id, Symbol = p.Symbol, Direction = p.Direction, VolumeUnits = p.VolumeUnits, EntryPrice = p.EntryPrice, EntryTimeUtc = p.EntryTimeUtc,
                StopLoss = stopLoss, TakeProfit = p.TakeProfit, Label = p.Label, Comment = p.Comment, CurrentPrice = p.CurrentPrice,
            };
            Modifications.Add((positionId, stopLoss));
            return OrderOutcome.Filled(positionId, stopLoss);
        }

        public OrderOutcome ClosePosition(long positionId, double? units)
        {
            int i = Positions.FindIndex(p => p.Id == positionId);
            if (i < 0) return OrderOutcome.Rejected(BrokerErrorKind.EntityNotFound, "brak");
            Closes.Add(positionId);
            Positions.RemoveAt(i);
            return OrderOutcome.Filled(positionId, 0);
        }

        public double EstimateMargin(string symbol, TradeDirection direction, double units) => units * Mid[symbol] / 20.0;
    }

    public static class Scenarios
    {
        /// <summary>
        /// Świece D1 indeksu: długi trend wzrostowy, a na końcu kilka silnych spadków (cena wciąż nad SMA200) –
        /// warunki strategii "Kupno spadków" (RSI(2) &lt; 10, zamknięcie &lt; SMA5, zamknięcie &gt; SMA200).
        /// </summary>
        public static List<Bar> IndexDipD1(DateTime lastOpen, int count = 300)
        {
            var bars = new List<Bar>();
            double p = 4000;
            var rng = new Random(3);
            for (int i = 0; i < count; i++)
            {
                var t = lastOpen.AddDays(-(count - 1 - i));
                double o = p;
                double c;
                if (i >= count - 3) c = o - 45; // trzy mocne spadki
                else c = o + 4 + (rng.NextDouble() - 0.5) * 20;
                double h = Math.Max(o, c) + 8, l = Math.Min(o, c) - 8;
                bars.Add(new Bar(t, o, h, l, c, 1000));
                p = c;
            }
            return bars;
        }

        public static List<Bar> Flat(string symbol, DateTime lastOpen, TimeSpan step, int count, double price)
        {
            var bars = new List<Bar>();
            var rng = new Random(symbol.GetHashCode() & 0xffff);
            for (int i = 0; i < count; i++)
            {
                double o = price + (rng.NextDouble() - 0.5) * price * 0.001;
                double c = price + (rng.NextDouble() - 0.5) * price * 0.001;
                bars.Add(new Bar(lastOpen - TimeSpan.FromTicks(step.Ticks * (count - 1 - i)), o, Math.Max(o, c) * 1.0005, Math.Min(o, c) * 0.9995, c, 100));
            }
            return bars;
        }

        public static StrategyProfile DipOnlyProfile(ValidationStatus status = ValidationStatus.Experimental) => new()
        {
            Version = "test",
            Entries = new List<ProfileEntry> { new() { AssetClass = AssetClass.Index, Strategy = StrategyId.IndexDipBuy, Status = status } },
        };
    }

    /// <summary>Zestaw: silnik + atrapa brokera + magazyn świec, z sygnałem kupna spadku na US500.</summary>
    public sealed class EngineRig
    {
        public FakeBroker Broker = new();
        public BarStore Bars = new(3000);
        public MemoryStore Store;
        public List<string> Logs = new();
        public TradingEngine Engine;
        public DateTime Now;
        public DecisionRecorder? Recorder;

        private sealed class ListLog : IEngineLog
        {
            private readonly List<string> _l;
            public ListLog(List<string> l) => _l = l;
            public void Info(string m) => _l.Add("I " + m);
            public void Warn(string m) => _l.Add("W " + m);
            public void Error(string m) => _l.Add("E " + m);
        }

        public EngineRig(bool live = false, MemoryStore? store = null, ValidationStatus status = ValidationStatus.Experimental, bool withRecorder = false, Action<EngineConfig>? configure = null, bool guardAllowed = true, FakeBroker? broker = null, int d1Bars = 300)
        {
            Store = store ?? new MemoryStore();
            if (broker != null) Broker = broker;
            var lastOpen = new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc);
            Now = lastOpen.AddDays(1).AddMinutes(5);
            Broker.Now = Now;
            Broker.Live = live;
            Broker.AddInstrument("US500", AssetClass.Index, 3900, 0.1, 0.1, 0.1, 0.1);
            var d1 = Scenarios.IndexDipD1(lastOpen, d1Bars);
            Broker.Mid["US500"] = d1[^1].Close;
            Bars.Merge("US500", Tf.D1, d1, true);
            Bars.Merge("US500", Tf.H4, Scenarios.Flat("US500", Now.AddHours(-4), TimeSpan.FromHours(4), 300, d1[^1].Close), true);
            Bars.Merge("US500", Tf.H1, Scenarios.Flat("US500", Now.AddHours(-1), TimeSpan.FromHours(1), 300, d1[^1].Close), true);
            var cfg = new EngineConfig { Label = "APEX", CollectionWindowSeconds = 0, VerificationTimeoutSeconds = 60 };
            cfg.News.Enabled = false;
            configure?.Invoke(cfg);
            var book = new StrategyBook(Scenarios.DipOnlyProfile(status), live);
            Engine = new TradingEngine(cfg, book, Broker, Broker, Bars, Store, new ListLog(Logs), new NullJournal());
            if (withRecorder) { Recorder = new DecisionRecorder(); Engine.Decisions = Recorder; }
            Engine.SetUniverse(new[] { new UniverseEntry { Symbol = "US500", AssetClass = AssetClass.Index, BaseCurrency = "US500" } });
            Engine.SetGuard(new AccountGuardResult { Allowed = guardAllowed, IsLive = live, Reason = "test" });
            Engine.Initialize(Now);
            Engine.CmdStart(); // Scenariusze handlu wymagają jawnego START; silnik produkcyjny startuje zatrzymany.
        }

        /// <summary>Dodaje kolejny instrument z tym samym scenariuszem świec (np. do testu filtra kryptowalut).</summary>
        public void AddDipInstrument(string symbol, AssetClass universeClass)
        {
            var lastOpen = Now.AddDays(-1).AddMinutes(-5);
            var d1 = Scenarios.IndexDipD1(lastOpen);
            Broker.AddInstrument(symbol, universeClass, d1[^1].Close, 0.1, 0.1, 0.1, 0.1);
            Bars.Merge(symbol, Tf.D1, d1, true);
            Bars.Merge(symbol, Tf.H4, Scenarios.Flat(symbol, Now.AddHours(-4), TimeSpan.FromHours(4), 300, d1[^1].Close), true);
            Bars.Merge(symbol, Tf.H1, Scenarios.Flat(symbol, Now.AddHours(-1), TimeSpan.FromHours(1), 300, d1[^1].Close), true);
        }

        public void Tick(double seconds = 2)
        {
            Now = Now.AddSeconds(seconds);
            Broker.Now = Now;
            Engine.OnTimer(Now);
        }
    }
}
