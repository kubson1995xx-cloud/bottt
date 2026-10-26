using System;

namespace Apex.Core.Model
{
    /// <summary>Kierunek transakcji. Wartość liczbowa = znak ekspozycji.</summary>
    public enum TradeDirection
    {
        Long = 1,
        Short = -1,
    }

    /// <summary>Klasa aktywów rozpoznana przez InstrumentClassifier.</summary>
    public enum AssetClass
    {
        Unknown = 0,
        Forex,
        Metal,
        Energy,
        Index,
        Stock,
        Etf,
        Commodity,
        Bond,
        Crypto,
    }

    /// <summary>Interwały obsługiwane przez silnik (podzbiór interwałów cTrader).</summary>
    public enum Tf
    {
        M5 = 5,
        M15 = 15,
        M30 = 30,
        H1 = 60,
        H4 = 240,
        D1 = 1440,
    }

    /// <summary>Reżim rynku wykrywany przez MarketRegimeDetector.</summary>
    public enum MarketRegime
    {
        /// <summary>Za mało danych lub dane niewiarygodne – nie handlujemy.</summary>
        Unknown = 0,
        StrongUptrend,
        StrongDowntrend,
        WeakUptrend,
        WeakDowntrend,
        Range,
        /// <summary>Gwałtowne, nieuporządkowane ruchy (np. skok zmienności) – nie handlujemy.</summary>
        Chaotic,
    }

    public enum VolatilityState
    {
        Unknown = 0,
        Low,
        Normal,
        High,
    }

    /// <summary>Identyfikatory strategii. Wartości tekstowe trafiają do komentarza zlecenia, więc są krótkie i stałe.</summary>
    public enum StrategyId
    {
        Unknown = 0,
        TrendPullback = 1,
        DonchianBreakout = 2,
        MeanReversion = 3,
        MomentumImpulse = 4,
        IndexDipBuy = 5,
        DailyTrend = 6,
        /// <summary>Techniczny test ścieżki zleceń na koncie DEMO (nie jest strategią handlową).</summary>
        ExecutionTest = 99,
    }

    public enum OrderStatus
    {
        /// <summary>Zlecenie wykonane, pozycja istnieje.</summary>
        Filled,
        /// <summary>Serwer jednoznacznie odrzucił zlecenie.</summary>
        Rejected,
        /// <summary>Brak jednoznacznej odpowiedzi (timeout, rozłączenie, błąd techniczny) – wymagana weryfikacja stanu.</summary>
        Uncertain,
    }

    public enum BrokerErrorKind
    {
        None = 0,
        TechnicalError,
        BadVolume,
        NoMoney,
        MarketClosed,
        Disconnected,
        EntityNotFound,
        Timeout,
        UnknownSymbol,
        InvalidStopLossTakeProfit,
        InvalidRequest,
        NoTradingPermission,
        Other,
    }

    public enum EngineMode
    {
        /// <summary>Pełna praca: nowe wejścia + zarządzanie.</summary>
        Running,
        /// <summary>Wstrzymane nowe wejścia, zarządzanie otwartymi pozycjami działa.</summary>
        PausedNewEntries,
        /// <summary>Bot zatrzymany przez użytkownika (zarządzanie SL nadal po stronie brokera).</summary>
        Stopped,
        /// <summary>Awaryjne zatrzymanie – wymaga ręcznego zresetowania.</summary>
        EmergencyStopped,
    }

    public static class TfExtensions
    {
        public static TimeSpan Duration(this Tf tf) => TimeSpan.FromMinutes((int)tf);

        public static string Label(this Tf tf) => tf.ToString();
    }

    public static class StrategyIdExtensions
    {
        public static string Code(this StrategyId id) => id switch
        {
            StrategyId.TrendPullback => "TP",
            StrategyId.DonchianBreakout => "BO",
            StrategyId.MeanReversion => "MR",
            StrategyId.MomentumImpulse => "MO",
            StrategyId.IndexDipBuy => "DB",
            StrategyId.DailyTrend => "DT",
            StrategyId.ExecutionTest => "XT",
            _ => "XX",
        };

        public static bool TryParseCode(string code, out StrategyId id)
        {
            switch (code)
            {
                case "TP": id = StrategyId.TrendPullback; return true;
                case "BO": id = StrategyId.DonchianBreakout; return true;
                case "MR": id = StrategyId.MeanReversion; return true;
                case "MO": id = StrategyId.MomentumImpulse; return true;
                case "DB": id = StrategyId.IndexDipBuy; return true;
                case "DT": id = StrategyId.DailyTrend; return true;
                case "XT": id = StrategyId.ExecutionTest; return true;
                default: id = default; return false;
            }
        }

        public static string PolishName(this StrategyId id) => id switch
        {
            StrategyId.TrendPullback => "Trend – korekta",
            StrategyId.DonchianBreakout => "Wybicie Donchian",
            StrategyId.MeanReversion => "Powrót do średniej",
            StrategyId.MomentumImpulse => "Impuls momentum",
            StrategyId.IndexDipBuy => "Kupno spadków (indeksy)",
            StrategyId.DailyTrend => "Trend dzienny (D1)",
            StrategyId.ExecutionTest => "Test wykonania (DEMO)",
            _ => id.ToString(),
        };
    }

    public static class AssetClassExtensions
    {
        public static string PolishName(this AssetClass c) => c switch
        {
            AssetClass.Forex => "Forex",
            AssetClass.Metal => "Metale",
            AssetClass.Energy => "Energia",
            AssetClass.Index => "Indeksy",
            AssetClass.Stock => "Akcje",
            AssetClass.Etf => "ETF",
            AssetClass.Commodity => "Towary",
            AssetClass.Bond => "Obligacje",
            AssetClass.Crypto => "Krypto (wykluczone)",
            _ => "Nieznane",
        };
    }
}
