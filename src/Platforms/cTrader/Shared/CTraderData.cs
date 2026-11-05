using System;
using Apex.Core.Bridge;
using cAlgo.API;
using cAlgo.API.Internals;

namespace Apex.Platforms.CTrader
{
    // All methods must be called on the cTrader main thread, including AssetConverter/GetTicks.
    internal static class CTraderData
    {
        internal static double Convert1(Robot r, string from)
        {
            if (string.Equals(from, r.Account.Asset?.Name, StringComparison.OrdinalIgnoreCase)) return 1;
            try
            {
                double value = r.AssetConverter.Convert(1.0, from, r.Account.Asset);
                return PlatformSafety.Positive(value) ? value : double.NaN;
            }
            catch (Exception) { return double.NaN; }
        }

        internal static SymbolDto Describe(Symbol s, Robot r) => Describe(s, from => Convert1(r, from));

        internal static SymbolDto Describe(Symbol s, Func<string, double> convert)
        {
            double usd = convert("USD");
            double quote = s.QuoteAsset != null ? convert(s.QuoteAsset.Name) : double.NaN;
            string kind = s.CommissionType switch
            {
                SymbolCommissionType.UsdPerMillionUsdVolume => "UsdPerMillionUsdVolume",
                SymbolCommissionType.UsdPerOneLot => "UsdPerLot",
                SymbolCommissionType.PercentageOfTradingVolume => "PercentOfVolume",
                SymbolCommissionType.QuoteCurrencyPerOneLot => "QuoteCurrencyPerLot",
                _ => "Unknown",
            };
            double minimum = s.MinCommission;
            double minimumAccount = minimum == 0 ? 0 : double.NaN;
            if (double.IsFinite(minimum) && minimum > 0)
            {
                // SDK 1.0.21: Asset uses MinCommissionAsset; QuoteAsset uses the symbol quote asset.
                // No other interpretation is supported. Missing asset/rate stays explicitly unknown.
                try
                {
                    string? asset = s.MinCommissionType switch
                    {
                        SymbolMinCommissionType.Asset => s.MinCommissionAsset?.Name,
                        SymbolMinCommissionType.QuoteAsset => s.QuoteAsset?.Name,
                        _ => null,
                    };
                    if (!string.IsNullOrWhiteSpace(asset))
                    {
                        double rate = convert(asset);
                        if (PlatformSafety.Positive(rate)) minimumAccount = minimum * rate;
                    }
                }
                catch (Exception) { minimumAccount = double.NaN; }
            }
            bool minimumKnown = minimum == 0 || PlatformSafety.Positive(minimumAccount);
            if (!minimumKnown || !double.IsFinite(s.Commission) || s.Commission < 0) kind = "Unknown";
            bool converted = PlatformSafety.Positive(quote) && PlatformSafety.Positive(usd);
            if (!converted) kind = "Unknown"; // SpecMapper must not silently replace a missing FX rate with 1.
            double minStop = PlatformSafety.MinimumPips(s.MinStopLossDistance,
                s.MinDistanceType == SymbolMinDistanceType.Percentage, Math.Max(s.Bid, s.Ask), s.PipSize);
            return new SymbolDto
            {
                Name = s.Name, Description = s.Description ?? "", BaseAsset = s.BaseAsset?.Name ?? "", QuoteAsset = s.QuoteAsset?.Name ?? "",
                Digits = s.Digits, PipSize = s.PipSize, TickSize = s.TickSize,
                // Symbol.PipValue is frozen at cBot start (SDK contract); derive the current account-currency value.
                PipValue = PlatformSafety.PipValue(s.PipSize, quote),
                LotSize = s.LotSize, VolumeMin = s.VolumeInUnitsMin, VolumeStep = s.VolumeInUnitsStep, VolumeMax = s.VolumeInUnitsMax,
                MinStopPips = double.IsFinite(minStop) && minStop >= 0 ? minStop : double.MaxValue,
                TradingEnabled = converted && s.IsTradingEnabled && s.TradingMode == SymbolTradingMode.FullAccess,
                CloseOnly = s.TradingMode == SymbolTradingMode.CloseOnly, CommissionKind = kind, CommissionValue = s.Commission,
                MinCommissionAccount = minimumKnown ? minimumAccount : null, MinCommissionKnown = minimumKnown,
                UsdToAccount = usd, QuoteToAccount = quote, IsCustom = s.IsCustom,
            };
        }

        internal static QuoteDto? Quote(Symbol s, Ticks ticks, Robot r) => Quote(s, ticks, r.IsBacktesting || r.Server.IsConnected, from => Convert1(r, from));

        internal static QuoteDto? Quote(Symbol s, Ticks ticks, bool connected, Func<string, double> convert)
        {
            if (ticks.Count == 0) return null;
            var tick = ticks.LastTick;
            if (!PlatformSafety.Positive(tick.Bid) || !PlatformSafety.Positive(tick.Ask) || tick.Ask < tick.Bid || tick.Time == default) return null;
            bool open = false;
            long ttc = -1;
            try
            {
                open = connected && s.MarketHours.IsOpened();
                if (open) ttc = Math.Max(0, (long)s.MarketHours.TimeTillClose().TotalSeconds);
            }
            catch (Exception) { open = false; }
            return new QuoteDto
            {
                S = s.Name, B = tick.Bid, A = tick.Ask, T = BridgeProtocol.ToUnixMs(tick.Time),
                Pv = PlatformSafety.PipValue(s.PipSize, s.QuoteAsset != null ? convert(s.QuoteAsset.Name) : double.NaN),
                O = open, Ttc = ttc,
            };
        }

        internal static bool StopsAllowed(Symbol s, double sl, double? tp)
        {
            double price = Math.Max(s.Ask, s.Bid);
            bool percentage = s.MinDistanceType == SymbolMinDistanceType.Percentage;
            return PlatformSafety.Protection(sl, tp) && sl >= PlatformSafety.MinimumPips(s.MinStopLossDistance, percentage, price, s.PipSize) &&
                tp!.Value >= PlatformSafety.MinimumPips(s.MinTakeProfitDistance, percentage, price, s.PipSize);
        }

        internal static bool Protected(Position p) => p.StopLoss is double sl && PlatformSafety.Positive(sl) && p.TakeProfit is double tp && PlatformSafety.Positive(tp) &&
            (p.TradeType == TradeType.Buy ? sl < p.EntryPrice && tp > p.EntryPrice : sl > p.EntryPrice && tp < p.EntryPrice);
    }
}
