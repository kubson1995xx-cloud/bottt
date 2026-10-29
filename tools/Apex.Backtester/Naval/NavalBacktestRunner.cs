using System;
using System.Collections.Generic;
using System.Linq;
using Apex.Core.Naval;

namespace Apex.Backtester.Naval;
public sealed record NavalRunSpec(NavalDataset Dataset, NavalFleet Fleet, DateTimeOffset From, DateTimeOffset To,
    NavalSettings Settings, SpotCostModel Cost, decimal InitialCash = 10000m, IReadOnlyList<string>? Assets = null);
public sealed record NavalSignal(NavalDecision Decision, decimal? Atr, decimal? Rank, decimal? Turnover);
public sealed record NavalRefusal(DateTimeOffset TimeUtc, string Instrument, string Reason);
public sealed record NavalEquity(DateTimeOffset TimeUtc, decimal Cash, decimal ClosedBalance, decimal LiquidationEquity,
    decimal EquityDrawdownPct, decimal ClosedBalanceDrawdownPct, decimal IntrabarLowStress);
public sealed record NavalRunResult(NavalRunSpec Spec, SpotAccount Account, IReadOnlyList<NavalSignal> Decisions,
    IReadOnlyList<NavalRefusal> Refusals, IReadOnlyList<NavalEquity> Equity, bool Halted, int MaxConcurrentPositions);

public static class NavalBacktestRunner
{
    sealed record Protection(decimal Stop, decimal Target);
    public static NavalRunResult Run(NavalRunSpec spec)
    {
        var profile = NavalFleetProfiles.Get(spec.Fleet);
        var assets = (spec.Assets ?? profile.Assets).ToArray();
        if (assets.Length == 0 || assets.Distinct(StringComparer.Ordinal).Count() != assets.Length
            || assets.Any(x => !profile.Assets.Contains(x)) || spec.Settings.EntryDays < 1 || spec.Settings.ExitDays < 1
            || spec.Settings.BtcSmaDays != 200 || spec.Settings.EntryDays > 10000 || spec.Settings.ExitDays > 10000)
            throw new ArgumentException("Invalid fleet/settings/assets");
        spec.Cost.Validate();
        spec.Dataset.ValidateAssets(assets, spec.From, spec.To, Math.Max(200, Math.Max(spec.Settings.EntryDays, spec.Settings.ExitDays)));
        var instruments = assets.ToDictionary(x => spec.Dataset.Instruments[x].Series.Instrument, x => spec.Dataset.Instruments[x], StringComparer.Ordinal);
        var features = instruments.ToDictionary(x => x.Key, x => new NavalFeatures(x.Value.Series));
        var btc = spec.Dataset.Instruments["BTC"].Series;
        var account = new SpotAccount(spec.Dataset.QuoteAsset, spec.InitialCash);
        var protections = new Dictionary<string, Protection>(StringComparer.Ordinal);
        var signals = new List<NavalSignal>(); var refusals = new List<NavalRefusal>(); var equity = new List<NavalEquity>();
        var evaluator = new NavalD1Evaluator(); var pending = Array.Empty<NavalSignal>();
        var riskPeak = spec.InitialCash; var closePeak = spec.InitialCash; var balancePeak = spec.InitialCash;
        var previousClose = spec.InitialCash; var dayStart = previousClose; bool halted = false; int maxActive = 0, nextId = 0;
        var marks = new Dictionary<string, decimal>(StringComparer.Ordinal);
        void CheckRisk()
        {
            var e = account.LiquidationEquity(marks, spec.Cost);
            riskPeak = Math.Max(riskPeak, e);
            if (e <= dayStart * .985m || e <= riskPeak * .95m) halted = true;
        }
        void Refuse(DateTimeOffset t, string symbol, string reason) => refusals.Add(new(t, symbol, reason));
        void Sell(string symbol, DateTimeOffset time, decimal reference, string reason, bool boundary = false)
        {
            var p = account.Positions[symbol]; var entry = p.Entry;
            var px = spec.Cost.Price(SpotSide.Sell, reference);
            account.Apply(new("fill-" + (++nextId), entry.PositionId, symbol, entry.BaseAsset, entry.QuoteAsset,
                time, SpotSide.Sell, p.Quantity, reference, px, p.Quantity * px * spec.Cost.FeeRate,
                entry.QuoteAsset, reason, boundary));
            protections.Remove(symbol); CheckRisk();
        }
        equity.Add(new(spec.From, account.Cash, spec.InitialCash, spec.InitialCash, 0, 0, spec.InitialCash));
        for (var day = spec.From; day < spec.To; day = day.AddDays(1))
        {
            var bars = instruments.ToDictionary(x => x.Key, x => features[x.Key].At(day));
            dayStart = previousClose;
            foreach (var b in bars) marks[b.Key] = b.Value.Open;
            CheckRisk();
            // 1. Protective gaps, 2. channel exits, 3. ranked entry admission.
            foreach (var symbol in account.Positions.Keys.OrderBy(s => s, StringComparer.Ordinal).ToArray())
            {
                var b = bars[symbol]; var p = protections[symbol];
                if (b.Open <= p.Stop) Sell(symbol, day, b.Open, "StopGap");
                else if (b.Open >= p.Target) Sell(symbol, day, p.Target, "TakeProfitGap");
            }
            foreach (var s in pending.Where(s => s.Decision.Action == NavalAction.ExitSignal).OrderBy(s => s.Decision.Instrument, StringComparer.Ordinal))
                if (account.Positions.ContainsKey(s.Decision.Instrument)) Sell(s.Decision.Instrument, day.AddTicks(1), bars[s.Decision.Instrument].Open, "Channel");
            foreach (var s in pending.Where(s => s.Decision.Action == NavalAction.EntrySignal).OrderByDescending(s => s.Rank).ThenBy(s => s.Decision.Instrument, StringComparer.Ordinal))
            {
                var symbol = s.Decision.Instrument; var instrument = instruments[symbol]; var b = bars[symbol];
                var time = day.AddTicks(1);
                if (halted) { Refuse(time, symbol, "RiskHalt"); continue; }
                if (account.Positions.ContainsKey(symbol)) { Refuse(time, symbol, "DuplicateSymbol"); continue; }
                if (account.Positions.Count >= profile.DefaultMaxPositions) { Refuse(time, symbol, "MaxPositions"); continue; }
                if (!s.Turnover.HasValue || s.Turnover.Value < 5000000m) { Refuse(time, symbol, "Liquidity"); continue; }
                var signalClose = features[symbol].At(day.AddDays(-1)).Close;
                if (!s.Atr.HasValue || s.Atr <= 0 || Math.Abs(b.Open - signalClose) > .5m * s.Atr.Value)
                { Refuse(time, symbol, "EntryDriftOrAtr"); continue; }
                var executed = spec.Cost.Price(SpotSide.Buy, b.Open);
                var stop = Math.Floor((executed - profile.ResearchStopAtr * s.Atr.Value) / instrument.TickSize) * instrument.TickSize;
                var target = Math.Floor((executed + profile.ResearchTakeProfitR * (executed - stop)) / instrument.TickSize) * instrument.TickSize;
                if (stop <= 0 || stop >= b.Open || target <= executed) { Refuse(time, symbol, "ProtectionLevels"); continue; }
                var size = SpotSizing.Calculate(account.LiquidationEquity(marks, spec.Cost), account.Cash,
                    b.Open, stop, instrument.LotSize, instrument.MinSize, spec.Cost);
                if (size.Quantity == 0) { Refuse(time, symbol, "SizeBelowMinimum"); continue; }
                var id = "position-" + (++nextId);
                account.Apply(new("fill-" + (++nextId), id, symbol, instrument.Series.BaseAsset, account.QuoteAsset,
                    time, SpotSide.Buy, size.Quantity, b.Open, executed, size.Quantity * executed * spec.Cost.FeeRate,
                    account.QuoteAsset, s.Decision.StableId, InitialRisk: size.Risk));
                protections.Add(symbol, new(stop, target)); maxActive = Math.Max(maxActive, account.Positions.Count); CheckRisk();
            }
            // OHLC nie dowodzi czasu/porządku intrabar: SL-first, wycena dostępna na close.
            var closeTime = day.AddDays(1);
            var stress = account.Cash + account.Positions.Sum(p => p.Value.Quantity * spec.Cost.Price(SpotSide.Sell, bars[p.Key].Low) * (1 - spec.Cost.FeeRate));
            foreach (var symbol in account.Positions.Keys.OrderBy(s => s, StringComparer.Ordinal).ToArray())
            {
                var b = bars[symbol]; var p = protections[symbol];
                if (b.Low <= p.Stop) Sell(symbol, closeTime, p.Stop, "Stop");
                else if (b.High >= p.Target) Sell(symbol, closeTime, p.Target, "TakeProfit");
            }
            foreach (var b in bars) marks[b.Key] = b.Value.Close;
            CheckRisk();
            if (closeTime == spec.To)
                foreach (var symbol in account.Positions.Keys.OrderBy(s => s, StringComparer.Ordinal).ToArray()) Sell(symbol, closeTime, bars[symbol].Close, "Boundary", true);
            previousClose = account.LiquidationEquity(marks, spec.Cost);
            var balance = spec.InitialCash + account.Trades.Sum(t => t.Net);
            closePeak = Math.Max(closePeak, previousClose); balancePeak = Math.Max(balancePeak, balance);
            equity.Add(new(closeTime, account.Cash, balance, previousClose, 100m * (closePeak - previousClose) / closePeak,
                100m * (balancePeak - balance) / balancePeak, stress));
            var today = new List<NavalSignal>();
            if (closeTime < spec.To)
                foreach (var pair in instruments.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    var prefixLength = Math.Max(201, Math.Max(spec.Settings.EntryDays, spec.Settings.ExitDays) + 1);
                    var d = evaluator.Evaluate(new(spec.Fleet, features[pair.Key].Prefix(closeTime, prefixLength),
                        NavalFeatures.Prefix(btc, closeTime, prefixLength), closeTime, account.Positions.ContainsKey(pair.Key), spec.Settings));
                    var atr = features[pair.Key].Atr(day); var turnover = features[pair.Key].Turnover(day);
                    var rank = atr > 0 && d.EntryHigh.HasValue ? (bars[pair.Key].Close - d.EntryHigh.Value) / atr : (decimal?)null;
                    var signal = new NavalSignal(d, atr, rank, turnover); today.Add(signal); signals.Add(signal);
                }
            pending = today.ToArray();
        }
        return new(spec, account, signals.AsReadOnly(), refusals.AsReadOnly(), equity.AsReadOnly(), halted, maxActive);
    }
}
