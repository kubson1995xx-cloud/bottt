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
        public List<SimCashflow> Cashflows { get; } = new();
        public List<(DateTime Time, double Equity)> DailyEquity { get; } = new();
        public double StartEquity { get; set; }
        public double EndEquity { get; set; }
        public double MaxEquityDrawdownPct { get; set; }
        public List<string> Events { get; } = new();
        public int Cycles { get; set; }
        public DateTime EffectiveStartUtc { get; set; }
        public DateTime LastExecutableOpenUtc { get; set; }
        public DateTime EffectiveEndUtc { get; set; }
        public HashSet<long> BoundaryPositionIds { get; } = new();
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
        public static void ValidateFxCoverage(IEnumerable<SymbolData> symbols, FxRates fx, DateTime from, DateTime to)
        {
            var errors = new List<string>();
            foreach (var data in symbols)
                foreach (var t in data.H1.Times.Where(t => t >= from && t < to))
                    try { _ = fx.ToUsd(data.Def.QuoteCcy, t); }
                    catch (InvalidOperationException e) { if (errors.Count < 100) errors.Add($"{data.Def.Symbol}: {e.Message}"); }
            if (errors.Count > 0) throw new InvalidOperationException("Invalid FX coverage (first 100): " + string.Join("; ", errors));
        }

        public static BacktestResult Run(RunSpec spec)
        {
            if (spec.From >= spec.To || spec.From.Ticks % TimeSpan.TicksPerHour != 0 || spec.To.Ticks % TimeSpan.TicksPerHour != 0 || !double.IsFinite(spec.StartEquity) || spec.StartEquity <= 0)
                throw new InvalidOperationException("Invalid evaluation range/start equity");
            // Preflight even if the strategy would produce no trades.
            ValidateFxCoverage(spec.Symbols, spec.Fx, spec.From, spec.To);
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
            if (times.Count == 0) throw new InvalidOperationException("No evaluation bars in requested range");
            result.EffectiveStartUtc = times.Min;
            result.LastExecutableOpenUtc = times.Max;

            world.SetTime(times.Min);
            engine.Initialize(times.Min);
            engine.CmdStart(); // Symulacja jawnie uruchamia wejścia; produkcja po restarcie pozostaje zatrzymana.
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
            // Complete the final evaluation candle, but never process prices after the exclusive cutoff.
            var end = prev.AddHours(1) < spec.To ? prev.AddHours(1) : spec.To;
            result.EffectiveEndUtc = end;
            world.ProcessIntrabar(prev, end);
            world.SetTime(end);
            world.ApplyFinancing(end);
            world.OnClosed = closed => { result.BoundaryPositionIds.Add(closed.PositionId); engine.OnPositionClosed(closed); };
            world.CloseAllAtEnd();
            result.EndEquity = world.GetAccount().Equity;
            if (result.EndEquity > peak) peak = result.EndEquity;
            if (peak > 0) maxDd = Math.Max(maxDd, (peak - result.EndEquity) / peak);
            result.DailyEquity.Add((end, result.EndEquity));
            result.MaxEquityDrawdownPct = maxDd * 100;
            result.Cashflows.AddRange(world.Cashflows);
            return result;
        }
    }
}
