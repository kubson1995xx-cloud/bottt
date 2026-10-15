using System;
using System.Collections.Generic;
using System.Linq;
using Apex.Core.Model;

namespace Apex.Backtester.Data
{
    /// <summary>
    /// Definicja instrumentu w backteście wraz z modelem kosztów.
    /// Spready i prowizje odpowiadają typowym (nie najlepszym) warunkom konta typu "Razor" u brokerów cTrader
    /// (FX/metale: prowizja 3,5 USD za lot za stronę). Dane źródłowe to ceny MID, więc spread jest modelowany:
    /// ask = mid + spread/2, bid = mid − spread/2, z poszerzeniem ×3 w godzinach rollover (21–23 UTC).
    /// </summary>
    public sealed class InstrumentDef
    {
        public string Symbol { get; init; } = "";
        public AssetClass AssetClass { get; init; }
        public string BaseAsset { get; init; } = "";
        public string QuoteCcy { get; init; } = "USD";
        public double PipSize { get; init; }
        public int Digits { get; init; }
        /// <summary>Typowy spread w jednostkach ceny.</summary>
        public double Spread { get; init; }
        /// <summary>Prowizja USD za lot za stronę (0 = brak prowizji, koszt w spreadzie).</summary>
        public double CommissionUsdPerLot { get; init; }
        public double LotSize { get; init; } = 1;
        public double VolumeMin { get; init; } = 1;
        public double VolumeStep { get; init; } = 1;
        public double Leverage { get; init; } = 20;
        /// <summary>Roczny koszt finansowania pozycji (płacą obie strony – założenie konserwatywne).</summary>
        public double FinancingRate { get; init; } = 0.025;
        public string Note { get; init; } = "";
    }

    public static class Catalog
    {
        private static InstrumentDef Fx(string s, string quote, double pip, int digits, double spreadPips, double lev = 30) => new()
        {
            Symbol = s, AssetClass = AssetClass.Forex, BaseAsset = s.Substring(0, 3), QuoteCcy = quote, PipSize = pip, Digits = digits,
            Spread = spreadPips * pip, CommissionUsdPerLot = 3.5, LotSize = 100_000, VolumeMin = 1000, VolumeStep = 1000, Leverage = lev,
        };

        private static InstrumentDef Cfd(string s, AssetClass c, string quote, double pip, int digits, double spread, double lev, double volMin = 0.1,
            double commission = 0, double lot = 1, string note = "") => new()
        {
            Symbol = s, AssetClass = c, BaseAsset = s, QuoteCcy = quote, PipSize = pip, Digits = digits, Spread = spread,
            Leverage = lev, VolumeMin = volMin, VolumeStep = volMin, CommissionUsdPerLot = commission, LotSize = lot, Note = note,
        };

        public static readonly IReadOnlyList<InstrumentDef> All = new List<InstrumentDef>
        {
            Fx("EURUSD", "USD", 0.0001, 5, 0.3),
            Fx("GBPUSD", "USD", 0.0001, 5, 0.5),
            Fx("AUDUSD", "USD", 0.0001, 5, 0.4),
            Fx("USDCAD", "CAD", 0.0001, 5, 0.6),
            Fx("AUDJPY", "JPY", 0.01, 3, 0.8, 20),
            Fx("EURJPY", "JPY", 0.01, 3, 0.8, 20),
            new InstrumentDef { Symbol = "XAUUSD", AssetClass = AssetClass.Metal, BaseAsset = "XAU", QuoteCcy = "USD", PipSize = 0.01, Digits = 2, Spread = 0.20,
                CommissionUsdPerLot = 3.5, LotSize = 100, VolumeMin = 1, VolumeStep = 1, Leverage = 20 },
            Cfd("SpotCrude", AssetClass.Energy, "USD", 0.01, 2, 0.03, 10, 1),
            Cfd("NatGas", AssetClass.Energy, "USD", 0.001, 3, 0.008, 10, 10),
            Cfd("US500", AssetClass.Index, "USD", 0.1, 1, 0.5, 20),
            Cfd("NAS100", AssetClass.Index, "USD", 0.1, 1, 1.5, 20),
            Cfd("US2000", AssetClass.Index, "USD", 0.1, 1, 0.4, 20),
            Cfd("UK100", AssetClass.Index, "GBP", 0.1, 1, 1.0, 20),
            Cfd("JPN225", AssetClass.Index, "USD", 1, 0, 8, 20, note: "kwotowanie w USD (źródło OANDA)"),
            Cfd("AUS200", AssetClass.Index, "AUD", 0.1, 1, 1.2, 20),
            Cfd("FRA40", AssetClass.Index, "EUR", 0.1, 1, 1.0, 20),
            Cfd("NETH25", AssetClass.Index, "EUR", 0.01, 2, 0.25, 20),
            Cfd("GER40", AssetClass.Index, "EUR", 0.1, 1, 1.4, 20, note: "DAX, źródło histdata 2010–2018"),
            Cfd("EUSTX50", AssetClass.Index, "EUR", 0.1, 1, 1.6, 20, note: "Euro Stoxx 50, źródło histdata 2010–2018"),
            Cfd("DE10YB", AssetClass.Bond, "EUR", 0.01, 3, 0.03, 5),
            Cfd("UK10YB", AssetClass.Bond, "GBP", 0.01, 3, 0.04, 5),
            Cfd("USB10Y", AssetClass.Bond, "USD", 0.01, 3, 0.03, 5),
            Cfd("USB02Y", AssetClass.Bond, "USD", 0.01, 3, 0.02, 5),
            Cfd("Corn", AssetClass.Commodity, "USD", 0.01, 3, 0.008, 10, 10),
            Cfd("Wheat", AssetClass.Commodity, "USD", 0.01, 3, 0.010, 10, 10),
            Cfd("Soybeans", AssetClass.Commodity, "USD", 0.01, 3, 0.014, 10, 10),
            Cfd("Sugar", AssetClass.Commodity, "USD", 0.0001, 5, 0.0004, 10, 100),
        };

        public static InstrumentDef Get(string symbol) => All.First(d => d.Symbol == symbol);

        public static IEnumerable<InstrumentDef> ByClass(AssetClass c) => All.Where(d => d.AssetClass == c);
    }
}
