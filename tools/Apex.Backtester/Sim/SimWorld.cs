using System;
using System.Collections.Generic;
using System.Linq;
using Apex.Backtester.Data;
using Apex.Core.Engine;
using Apex.Core.Indicators;
using Apex.Core.Model;

namespace Apex.Backtester.Sim
{
    /// <summary>Scenariusz kosztów transakcyjnych (mnożniki względem modelu bazowego).</summary>
    public sealed class CostScenario
    {
        public string Name { get; init; } = "bazowy";
        public double SpreadMult { get; init; } = 1;
        public double CommissionMult { get; init; } = 1;
        public double SlippageMult { get; init; } = 1;
        public double FinancingMult { get; init; } = 1;

        public static CostScenario Base => new();
        public static CostScenario Elevated => new() { Name = "koszty x1.5", SpreadMult = 1.5, CommissionMult = 1.5, SlippageMult = 2, FinancingMult = 1.5 };
        public static CostScenario Double => new() { Name = "koszty x2", SpreadMult = 2, CommissionMult = 2, SlippageMult = 3, FinancingMult = 2 };
    }

    internal sealed class SimPosition
    {
        public long Id;
        public string Symbol = "";
        public TradeDirection Dir;
        public double Units;
        public double InitialUnits;
        public double Entry;
        public DateTime EntryTime;
        public double? Sl;
        public double? Tp;
        public string Label = "";
        public string Comment = "";
        public double EntryCommissionPerUnit;
        public double SwapAccrued;
        public double RealizedNet;
        public double RealizedGross;
        public double RealizedCommission;
        public double RealizedSwap;
    }

    /// <summary>
    /// Symulowany broker + rynek dla backtestu. Implementuje te same interfejsy co adapter cTrader,
    /// więc silnik APEX nie wie, czy działa na żywo, czy w symulacji.
    /// Model wykonania: wejście po cenie otwarcia kolejnej świecy (ask/bid z modelowanym spreadem) + poślizg;
    /// SL/TP sprawdzane na ścieżce M15 wewnątrz świecy H1 (gdy brak M15 – na H1), przy jednoczesnym trafieniu SL i TP
    /// zakładamy najpierw SL (konserwatywnie); luki cenowe realizowane po cenie otwarcia.
    /// </summary>
    public sealed class SimWorld : IBrokerGateway, IMarketData, IFeatureProvider
    {
        private readonly Dictionary<string, SymbolData> _data;
        private readonly FxRates _fx;
        private readonly CostScenario _cost;
        private readonly List<SimPosition> _positions = new();
        private long _nextId = 1000;
        private DateTime _lastFinancingServerDate = DateTime.MinValue;

        public SimWorld(IEnumerable<SymbolData> data, FxRates fx, CostScenario cost, double startEquity)
        {
            _data = data.ToDictionary(d => d.Def.Symbol, StringComparer.OrdinalIgnoreCase);
            _fx = fx;
            _cost = cost;
            Balance = startEquity;
        }

        public DateTime Now { get; private set; }
        public double Balance { get; private set; }
        public Action<ClosedPositionInfo>? OnClosed { get; set; }
        public bool IsConnected => true;
        public int OpenCount => _positions.Count;

        public void SetTime(DateTime t) => Now = t;

        // ------------------------------------------------------------- koszty

        private double SpreadAt(InstrumentDef d, DateTime utc)
        {
            double s = d.Spread * _cost.SpreadMult;
            int h = utc.Hour;
            if (h == 21 || h == 22) s *= 3; // rollover – szerokie spready
            return s;
        }

        private double AtrH1(SymbolData sd, DateTime utc)
        {
            var fs = sd.Features[Tf.H1];
            int i = fs.IndexAtOrBefore(utc);
            if (i < 0) return 0;
            double a = Ta.At(fs.Atr(14), i);
            return double.IsNaN(a) ? 0 : a;
        }

        private double QuoteToUsd(InstrumentDef d, DateTime utc)
        {
            double r = _fx.ToUsd(d.QuoteCcy, utc);
            return double.IsNaN(r) || r <= 0 ? 1.0 : r;
        }

        private double CommissionPerUnit(InstrumentDef d) => d.CommissionUsdPerLot * _cost.CommissionMult / d.LotSize;

        // ------------------------------------------------------------- IMarketData

        public InstrumentSpec? GetSpec(string symbol)
        {
            if (!_data.TryGetValue(symbol, out var sd)) return null;
            var d = sd.Def;
            double q2u = QuoteToUsd(d, Now);
            return new InstrumentSpec
            {
                Name = d.Symbol,
                Description = d.Note,
                AssetClass = d.AssetClass,
                BaseAsset = d.BaseAsset,
                QuoteAsset = d.QuoteCcy,
                Digits = d.Digits,
                PipSize = d.PipSize,
                TickSize = d.PipSize / (d.AssetClass == AssetClass.Forex ? 10 : 1),
                PipValuePerUnit = d.PipSize * q2u,
                LotSize = d.LotSize,
                VolumeMin = d.VolumeMin,
                VolumeStep = d.VolumeStep,
                VolumeMax = 1e12,
                MinStopDistancePips = 0,
                TradingEnabled = true,
                CommissionKind = d.CommissionUsdPerLot > 0 ? CommissionKind.UsdPerLot : CommissionKind.None,
                CommissionValue = d.CommissionUsdPerLot * _cost.CommissionMult,
                UsdToAccount = 1.0,
                QuoteToAccount = q2u,
            };
        }

        public Quote? GetQuote(string symbol)
        {
            if (!_data.TryGetValue(symbol, out var sd)) return null;
            double mid;
            if (sd.IndexByOpen.TryGetValue(Now, out int i)) mid = sd.H1.Open[i];
            else
            {
                int j = sd.Features[Tf.H1].IndexAtOrBefore(Now);
                if (j < 0) return null;
                mid = sd.H1.Close[j];
            }
            double s = SpreadAt(sd.Def, Now);
            return new Quote(mid - s / 2, mid + s / 2, Now);
        }

        public bool IsMarketOpen(string symbol, DateTime utc, out TimeSpan timeToClose)
        {
            timeToClose = TimeSpan.Zero;
            if (!_data.TryGetValue(symbol, out var sd)) return false;
            if (!sd.IndexByOpen.TryGetValue(utc, out int i)) return false;
            timeToClose = sd.SessionEnd[i] - utc;
            return true;
        }

        // ------------------------------------------------------------- IFeatureProvider

        public DateTime? LastClosedBarTime(string symbol, Tf tf, DateTime utc)
        {
            if (!_data.TryGetValue(symbol, out var sd) || !sd.Features.TryGetValue(tf, out var fs)) return null;
            int i = fs.IndexAtOrBefore(utc);
            return i < 0 ? null : fs.Bars.Times[i];
        }

        public bool TryGet(string symbol, Tf tf, DateTime utc, out FeatureSet features, out int index)
        {
            features = null!;
            index = -1;
            if (!_data.TryGetValue(symbol, out var sd) || !sd.Features.TryGetValue(tf, out var fs)) return false;
            index = fs.IndexAtOrBefore(utc);
            features = fs;
            return index >= 0;
        }

        // ------------------------------------------------------------- IBrokerGateway

        public AccountSnapshot GetAccount()
        {
            double unreal = 0, margin = 0;
            foreach (var p in _positions)
            {
                var sd = _data[p.Symbol];
                double q2u = QuoteToUsd(sd.Def, Now);
                double px = ExitPriceNow(sd, p.Dir);
                unreal += (int)p.Dir * (px - p.Entry) * p.Units * q2u + p.SwapAccrued - p.EntryCommissionPerUnit * p.Units;
                margin += p.Units * px * q2u / sd.Def.Leverage;
            }
            double equity = Balance + unreal;
            return new AccountSnapshot
            {
                Number = 1,
                IsLive = false,
                BrokerName = "Symulacja",
                Currency = "USD",
                Balance = Balance,
                Equity = equity,
                Margin = margin,
                FreeMargin = equity - margin,
                MarginLevel = margin > 0 ? equity / margin * 100 : (double?)null,
                TimeUtc = Now,
            };
        }

        private double ExitPriceNow(SymbolData sd, TradeDirection d)
        {
            var q = GetQuote(sd.Def.Symbol);
            if (q == null) return 0;
            return q.Value.ExitPrice(d);
        }

        public IReadOnlyList<PositionInfo> GetPositions()
        {
            var list = new List<PositionInfo>(_positions.Count);
            foreach (var p in _positions)
            {
                var sd = _data[p.Symbol];
                double q2u = QuoteToUsd(sd.Def, Now);
                double px = ExitPriceNow(sd, p.Dir);
                double gross = (int)p.Dir * (px - p.Entry) * p.Units * q2u;
                list.Add(new PositionInfo
                {
                    Id = p.Id,
                    Symbol = p.Symbol,
                    Direction = p.Dir,
                    VolumeUnits = p.Units,
                    EntryPrice = p.Entry,
                    EntryTimeUtc = p.EntryTime,
                    StopLoss = p.Sl,
                    TakeProfit = p.Tp,
                    Label = p.Label,
                    Comment = p.Comment,
                    GrossProfit = gross,
                    Commissions = -p.EntryCommissionPerUnit * p.Units * 2,
                    Swap = p.SwapAccrued,
                    NetProfit = gross + p.SwapAccrued - p.EntryCommissionPerUnit * p.Units * 2,
                    CurrentPrice = px,
                    Margin = p.Units * px * q2u / sd.Def.Leverage,
                });
            }
            return list;
        }

        public OrderOutcome OpenMarket(OrderRequest r)
        {
            if (!_data.TryGetValue(r.Symbol, out var sd)) return OrderOutcome.Rejected(BrokerErrorKind.UnknownSymbol, "nieznany symbol");
            if (!sd.IndexByOpen.ContainsKey(Now)) return OrderOutcome.Rejected(BrokerErrorKind.MarketClosed, "rynek zamknięty");
            if (r.VolumeUnits < sd.Def.VolumeMin) return OrderOutcome.Rejected(BrokerErrorKind.BadVolume, "wolumen poniżej minimum");
            if (!(r.StopLossPips > 0)) return OrderOutcome.Rejected(BrokerErrorKind.InvalidStopLossTakeProfit, "brak SL");
            var q = GetQuote(r.Symbol)!.Value;
            double spread = q.Spread;
            double slip = _cost.SlippageMult * (0.25 * spread + 0.01 * AtrH1(sd, Now));
            int s = (int)r.Direction;
            double fill = q.EntryPrice(r.Direction) + s * slip;
            double sl = fill - s * r.StopLossPips * sd.Def.PipSize;
            double? tp = r.TakeProfitPips.HasValue ? fill + s * r.TakeProfitPips.Value * sd.Def.PipSize : null;
            var pos = new SimPosition
            {
                Id = _nextId++,
                Symbol = r.Symbol,
                Dir = r.Direction,
                Units = r.VolumeUnits,
                InitialUnits = r.VolumeUnits,
                Entry = fill,
                EntryTime = Now,
                Sl = sl,
                Tp = tp,
                Label = r.Label,
                Comment = r.Comment,
                EntryCommissionPerUnit = CommissionPerUnit(sd.Def),
            };
            _positions.Add(pos);
            return OrderOutcome.Filled(pos.Id, fill);
        }

        public OrderOutcome ModifyStopLoss(long positionId, double stopLoss)
        {
            var p = _positions.FirstOrDefault(x => x.Id == positionId);
            if (p == null) return OrderOutcome.Rejected(BrokerErrorKind.EntityNotFound, "brak pozycji");
            var sd = _data[p.Symbol];
            if (sd.IndexByOpen.ContainsKey(Now))
            {
                double px = ExitPriceNow(sd, p.Dir);
                if ((int)p.Dir * (px - stopLoss) <= 0) return OrderOutcome.Rejected(BrokerErrorKind.InvalidStopLossTakeProfit, "SL po złej stronie ceny");
            }
            p.Sl = stopLoss;
            return OrderOutcome.Filled(p.Id, stopLoss);
        }

        public OrderOutcome ClosePosition(long positionId, double? units)
        {
            var p = _positions.FirstOrDefault(x => x.Id == positionId);
            if (p == null) return OrderOutcome.Rejected(BrokerErrorKind.EntityNotFound, "brak pozycji");
            var sd = _data[p.Symbol];
            if (!sd.IndexByOpen.ContainsKey(Now)) return OrderOutcome.Rejected(BrokerErrorKind.MarketClosed, "rynek zamknięty");
            var q = GetQuote(p.Symbol)!.Value;
            double slip = _cost.SlippageMult * (0.25 * q.Spread + 0.01 * AtrH1(sd, Now));
            double px = q.ExitPrice(p.Dir) - (int)p.Dir * slip;
            double u = units.HasValue ? Math.Min(units.Value, p.Units) : p.Units;
            Realize(p, sd, u, px, Now, "Closed");
            return OrderOutcome.Filled(p.Id, px);
        }

        public double EstimateMargin(string symbol, TradeDirection direction, double units)
        {
            if (!_data.TryGetValue(symbol, out var sd)) return double.NaN;
            var q = GetQuote(symbol);
            if (q == null) return double.NaN;
            return units * q.Value.Mid * QuoteToUsd(sd.Def, Now) / sd.Def.Leverage;
        }

        // ------------------------------------------------------------- symulacja ścieżki ceny

        private void Realize(SimPosition p, SymbolData sd, double units, double price, DateTime time, string reason)
        {
            double q2u = QuoteToUsd(sd.Def, time);
            double frac = p.Units > 0 ? units / p.Units : 1;
            double gross = (int)p.Dir * (price - p.Entry) * units * q2u;
            double commission = (p.EntryCommissionPerUnit + CommissionPerUnit(sd.Def)) * units;
            double swap = p.SwapAccrued * frac;
            p.SwapAccrued -= swap;
            double net = gross - commission + swap;
            Balance += net;
            p.RealizedGross += gross;
            p.RealizedCommission += commission;
            p.RealizedSwap += swap;
            p.RealizedNet += net;
            p.Units -= units;
            if (p.Units <= 1e-9)
            {
                _positions.Remove(p);
                OnClosed?.Invoke(new ClosedPositionInfo
                {
                    PositionId = p.Id,
                    Symbol = p.Symbol,
                    Label = p.Label,
                    Comment = p.Comment,
                    Direction = p.Dir,
                    Volume = p.InitialUnits,
                    EntryPrice = p.Entry,
                    ExitPrice = price,
                    EntryTimeUtc = p.EntryTime,
                    ExitTimeUtc = time,
                    GrossProfit = p.RealizedGross,
                    Commission = -p.RealizedCommission,
                    Swap = p.RealizedSwap,
                    NetProfit = p.RealizedNet,
                    PlatformReason = reason,
                });
            }
        }

        /// <summary>Przetwarza ruch ceny w świecach H1 otwartych w przedziale [from, to) – trafienia SL/TP.</summary>
        public void ProcessIntrabar(DateTime from, DateTime to)
        {
            if (_positions.Count == 0) return;
            foreach (var p in _positions.ToList())
            {
                var sd = _data[p.Symbol];
                for (var t = from; t < to; t = t.AddHours(1))
                {
                    if (!_positions.Contains(p)) break;
                    if (!sd.IndexByOpen.TryGetValue(t, out int hi)) continue;
                    if (t < p.EntryTime) continue;
                    double spread = SpreadAt(sd.Def, t);
                    double atr = AtrH1(sd, t);
                    double stopSlip = _cost.SlippageMult * (0.5 * spread + 0.02 * atr);
                    if (sd.M15 != null && sd.M15IndexByOpen != null)
                    {
                        bool any = false;
                        for (int k = 0; k < 4 && _positions.Contains(p); k++)
                        {
                            if (!sd.M15IndexByOpen.TryGetValue(t.AddMinutes(15 * k), out int mi)) continue;
                            any = true;
                            CheckBar(p, sd, sd.M15.Open[mi], sd.M15.High[mi], sd.M15.Low[mi], spread, stopSlip, t.AddMinutes(15 * k + 15));
                        }
                        if (!any && _positions.Contains(p))
                            CheckBar(p, sd, sd.H1.Open[hi], sd.H1.High[hi], sd.H1.Low[hi], spread, stopSlip, t.AddHours(1));
                    }
                    else
                    {
                        CheckBar(p, sd, sd.H1.Open[hi], sd.H1.High[hi], sd.H1.Low[hi], spread, stopSlip, t.AddHours(1));
                    }
                }
            }
        }

        private void CheckBar(SimPosition p, SymbolData sd, double open, double high, double low, double spread, double stopSlip, DateTime barEnd)
        {
            double half = spread / 2;
            if (p.Dir == TradeDirection.Long)
            {
                double bOpen = open - half, bHigh = high - half, bLow = low - half;
                if (p.Sl.HasValue && bOpen <= p.Sl.Value) { Realize(p, sd, p.Units, bOpen - stopSlip, barEnd, "StopLoss"); return; }
                if (p.Sl.HasValue && bLow <= p.Sl.Value) { Realize(p, sd, p.Units, p.Sl.Value - stopSlip, barEnd, "StopLoss"); return; }
                if (p.Tp.HasValue && bOpen >= p.Tp.Value) { Realize(p, sd, p.Units, bOpen, barEnd, "TakeProfit"); return; }
                if (p.Tp.HasValue && bHigh >= p.Tp.Value) { Realize(p, sd, p.Units, p.Tp.Value, barEnd, "TakeProfit"); }
            }
            else
            {
                double aOpen = open + half, aHigh = high + half, aLow = low + half;
                if (p.Sl.HasValue && aOpen >= p.Sl.Value) { Realize(p, sd, p.Units, aOpen + stopSlip, barEnd, "StopLoss"); return; }
                if (p.Sl.HasValue && aHigh >= p.Sl.Value) { Realize(p, sd, p.Units, p.Sl.Value + stopSlip, barEnd, "StopLoss"); return; }
                if (p.Tp.HasValue && aOpen <= p.Tp.Value) { Realize(p, sd, p.Units, aOpen, barEnd, "TakeProfit"); return; }
                if (p.Tp.HasValue && aLow <= p.Tp.Value) { Realize(p, sd, p.Units, p.Tp.Value, barEnd, "TakeProfit"); }
            }
        }

        /// <summary>Naliczenie kosztów finansowania (swap) za każdy przekroczony rollover (17:00 Nowy Jork).</summary>
        public void ApplyFinancing(DateTime utc)
        {
            var serverDate = Aggregator.ToServer(utc).Date;
            if (_lastFinancingServerDate == DateTime.MinValue) { _lastFinancingServerDate = serverDate; return; }
            if (serverDate <= _lastFinancingServerDate) return;
            for (var d = _lastFinancingServerDate.AddDays(1); d <= serverDate; d = d.AddDays(1))
            {
                foreach (var p in _positions)
                {
                    var sd = _data[p.Symbol];
                    bool fxLike = sd.Def.AssetClass == AssetClass.Forex || sd.Def.AssetClass == AssetClass.Metal;
                    int nights = fxLike
                        ? d.DayOfWeek switch { DayOfWeek.Thursday => 3, DayOfWeek.Sunday => 0, DayOfWeek.Monday => 0, _ => 1 }
                        : d.DayOfWeek switch { DayOfWeek.Saturday => 3, DayOfWeek.Sunday => 0, DayOfWeek.Monday => 0, _ => 1 };
                    if (nights == 0 || p.EntryTime >= Aggregator.ServerToUtc(d)) continue;
                    double px = p.Entry;
                    double notional = p.Units * px * QuoteToUsd(sd.Def, utc);
                    p.SwapAccrued -= notional * sd.Def.FinancingRate * _cost.FinancingMult / 365.0 * nights;
                }
            }
            _lastFinancingServerDate = serverDate;
        }

        /// <summary>Zamyka wszystkie pozycje na końcu testu po ostatniej znanej cenie.</summary>
        public void CloseAllAtEnd()
        {
            foreach (var p in _positions.ToList())
            {
                var sd = _data[p.Symbol];
                int j = sd.Features[Tf.H1].IndexAtOrBefore(Now);
                double mid = j >= 0 ? sd.H1.Close[j] : p.Entry;
                double half = SpreadAt(sd.Def, Now) / 2;
                double px = p.Dir == TradeDirection.Long ? mid - half : mid + half;
                Realize(p, sd, p.Units, px, Now, "Closed");
            }
        }
    }
}
