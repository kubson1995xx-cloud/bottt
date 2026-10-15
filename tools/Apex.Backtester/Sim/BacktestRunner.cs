using System;
using System.Collections.Generic;
using System.Linq;
using Apex.Backtester.Data;
using Apex.Core.Adaptive;
using Apex.Core.Engine;
using Apex.Core.Management;
using Apex.Core.Model;
using Apex.Core.Performance;
using Apex.Core.Safety;

namespace Apex.Backtester.Sim
{
    public sealed class RunSpec
    {
        public List<SymbolData> Symbols { get; init; } = new();
        public StrategyProfile Profile { get; init; } = new();
        public Func<EngineConfig> ConfigFactory { get; init; } = () => new EngineConfig();
        public CostScenario Cost { get; init; } = CostScenario.Base;
        public DateTime From { get; init; }
        public DateTime To { get; init; }
        public double StartEquity { get; init; } = 100_000;
        public FxRates Fx { get; init; } = new();
    }

    public sealed class BacktestResult
    {
        public List<TradeRecord> Trades { get; } = new();
        public List<(DateTime Time, double Equity)> DailyEquity { get; } = new();
        public double StartEquity { get; set; }
        public double EndEquity { get; set; }
        public double MaxEquityDrawdownPct { get; set; }
        public List<string> Events { get; } = new();
        public int Cycles { get; set; }
    }

    internal sealed class CollectingJournal : ITradeJournal
    {
        private readonly BacktestResult _r;
        public CollectingJournal(BacktestResult r) => _r = r;
        public void TradeOpened(TradePlan plan, double volume, string details) { }
        public void TradeClosed(TradeRecord record) => _r.Trades.Add(record);
        public void Signal(string line) { }
        public void Event(string line) { if (_r.Events.Count < 500) _r.Events.Add(line); }
    }

    /// <summary>Uruchamia silnik APEX na danych historycznych krok po kroku (co godzinę, na zamknięciach świec H1).</summary>
    public static class BacktestRunner
    {
        public static BacktestResult Run(RunSpec spec)
        {
            var result = new BacktestResult { StartEquity = spec.StartEquity };
            var world = new SimWorld(spec.Symbols, spec.Fx, spec.Cost, spec.StartEquity);
            var cfg = spec.ConfigFactory();
            cfg.IsBacktest = true;
            var book = new StrategyBook(spec.Profile);
            var engine = new TradingEngine(cfg, book, world, world, world, new MemoryStore(), new NullLog(), new CollectingJournal(result));
            world.OnClosed = engine.OnPositionClosed;
            engine.SetUniverse(spec.Symbols.Select(s =>
            {
                var legs = InstrumentClassifier.CurrencyLegs(s.Def.AssetClass, s.Def.Symbol, s.Def.BaseAsset, s.Def.QuoteCcy);
                return new UniverseEntry { Symbol = s.Def.Symbol, AssetClass = s.Def.AssetClass, BaseCurrency = legs.Base, QuoteCurrency = legs.Quote };
            }));
            engine.SetGuard(new AccountGuardResult { Allowed = true, IsLive = false, Reason = "backtest" });

            // Oś czasu: wszystkie godziny, w których otwiera się świeca H1 któregokolwiek instrumentu.
            var times = new SortedSet<DateTime>();
            foreach (var s in spec.Symbols)
                foreach (var t in s.H1.Times)
                    if (t >= spec.From && t < spec.To) times.Add(t);
            if (times.Count == 0) return result;

            world.SetTime(times.Min);
            engine.Initialize(times.Min);
            DateTime prev = times.Min;
            double peak = spec.StartEquity, maxDd = 0;
            DateTime lastDay = DateTime.MinValue;
            foreach (var t in times)
            {
                if (t > prev) world.ProcessIntrabar(prev, t);
                world.SetTime(t);
                world.ApplyFinancing(t);
                engine.OnTimer(t);
                result.Cycles++;
                prev = t;
                var acc = world.GetAccount();
                if (acc.Equity > peak) peak = acc.Equity;
                double dd = peak > 0 ? (peak - acc.Equity) / peak : 0;
                if (dd > maxDd) maxDd = dd;
                if (t.Date != lastDay)
                {
                    result.DailyEquity.Add((t.Date, acc.Equity));
                    lastDay = t.Date;
                }
            }
            world.CloseAllAtEnd();
            result.EndEquity = world.GetAccount().Equity;
            result.MaxEquityDrawdownPct = maxDd * 100;
            return result;
        }
    }
}
