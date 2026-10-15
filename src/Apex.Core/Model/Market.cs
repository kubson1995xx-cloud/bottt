using System;

namespace Apex.Core.Model
{
    /// <summary>Bieżąca cena instrumentu.</summary>
    public readonly struct Quote
    {
        public Quote(double bid, double ask, DateTime timeUtc)
        {
            Bid = bid;
            Ask = ask;
            TimeUtc = timeUtc;
        }

        public double Bid { get; }
        public double Ask { get; }
        public DateTime TimeUtc { get; }
        public double Spread => Ask - Bid;
        public double Mid => (Ask + Bid) / 2.0;

        public bool IsValid =>
            !double.IsNaN(Bid) && !double.IsNaN(Ask) && !double.IsInfinity(Bid) && !double.IsInfinity(Ask) &&
            Bid > 0 && Ask > 0 && Ask >= Bid;

        public double EntryPrice(TradeDirection d) => d == TradeDirection.Long ? Ask : Bid;
        public double ExitPrice(TradeDirection d) => d == TradeDirection.Long ? Bid : Ask;
    }

    /// <summary>Sposób naliczania prowizji (odwzorowanie SymbolCommissionType z cTrader).</summary>
    public enum CommissionKind
    {
        None = 0,
        /// <summary>USD za milion USD wolumenu (jedna strona).</summary>
        UsdPerMillionUsdVolume,
        /// <summary>USD za 1 lot (jedna strona).</summary>
        UsdPerLot,
        /// <summary>Procent wartości transakcji (jedna strona).</summary>
        PercentOfVolume,
        /// <summary>Waluta kwotowania za 1 lot (jedna strona).</summary>
        QuoteCurrencyPerLot,
        /// <summary>Nieznany sposób – nie można zweryfikować kosztu, więc handel jest blokowany.</summary>
        Unknown,
    }

    /// <summary>
    /// Specyfikacja instrumentu w jednostkach niezależnych od platformy.
    /// Wszystkie wartości pieniężne w walucie konta. Wolumen zawsze w JEDNOSTKACH (units), nie w lotach.
    /// </summary>
    public sealed class InstrumentSpec
    {
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public AssetClass AssetClass { get; init; }
        public string BaseAsset { get; init; } = "";
        public string QuoteAsset { get; init; } = "";
        public int Digits { get; init; }
        public double PipSize { get; init; }
        public double TickSize { get; init; }

        /// <summary>Wartość 1 pipsa dla 1 jednostki wolumenu, w walucie konta.</summary>
        public double PipValuePerUnit { get; init; }

        public double LotSize { get; init; }
        public double VolumeMin { get; init; }
        public double VolumeStep { get; init; }
        public double VolumeMax { get; init; }

        /// <summary>Minimalna odległość SL od ceny, w pipsach (0 jeśli brak ograniczenia).</summary>
        public double MinStopDistancePips { get; init; }

        public bool TradingEnabled { get; init; } = true;
        public bool CloseOnly { get; init; }

        public CommissionKind CommissionKind { get; init; }

        /// <summary>Surowa wartość prowizji (znaczenie zależy od CommissionKind), jedna strona.</summary>
        public double CommissionValue { get; init; }

        /// <summary>Kurs konwersji z USD na walutę konta (potrzebny dla prowizji w USD).</summary>
        public double UsdToAccount { get; init; } = 1.0;

        /// <summary>Kurs konwersji z waluty kwotowania na walutę konta.</summary>
        public double QuoteToAccount { get; init; } = 1.0;

        /// <summary>Swap długi/krótki w pipsach za noc za jednostkę (jeśli broker podaje w pipsach), informacyjnie.</summary>
        public double SwapLongPips { get; init; }
        public double SwapShortPips { get; init; }

        /// <summary>Wartość zmiany ceny o 1.0 dla 1 jednostki, w walucie konta.</summary>
        public double ValuePerPricePerUnit => PipSize > 0 ? PipValuePerUnit / PipSize : 0;

        public double PriceToPips(double priceDistance) => PipSize > 0 ? priceDistance / PipSize : double.NaN;
        public double PipsToPrice(double pips) => pips * PipSize;

        public double RoundPrice(double price)
        {
            if (TickSize <= 0) return Math.Round(price, Digits);
            return Math.Round(Math.Round(price / TickSize) * TickSize, Digits);
        }

        /// <summary>Normalizacja wolumenu W DÓŁ do kroku brokera (nigdy w górę – to zwiększałoby ryzyko).</summary>
        public double NormalizeVolumeDown(double units)
        {
            if (double.IsNaN(units) || units <= 0 || VolumeStep <= 0) return 0;
            double steps = Math.Floor(units / VolumeStep + 1e-9);
            double v = steps * VolumeStep;
            if (VolumeMax > 0 && v > VolumeMax)
                v = Math.Floor(VolumeMax / VolumeStep + 1e-9) * VolumeStep;
            return Math.Round(v, 8);
        }

        /// <summary>Prowizja za jedną stronę transakcji dla podanego wolumenu i ceny, w walucie konta. NaN gdy nie da się policzyć.</summary>
        public double CommissionOneSide(double units, double price)
        {
            switch (CommissionKind)
            {
                case CommissionKind.None:
                    return 0;
                case CommissionKind.UsdPerMillionUsdVolume:
                    {
                        // Wolumen w USD = units * cena * (kurs waluty kwotowania do USD).
                        // QuoteToAccount / UsdToAccount = kurs waluty kwotowania do USD.
                        if (UsdToAccount <= 0) return double.NaN;
                        double quoteToUsd = QuoteToAccount / UsdToAccount;
                        double usdVolume = units * price * quoteToUsd;
                        return usdVolume / 1_000_000.0 * CommissionValue * UsdToAccount;
                    }
                case CommissionKind.UsdPerLot:
                    if (LotSize <= 0) return double.NaN;
                    return units / LotSize * CommissionValue * UsdToAccount;
                case CommissionKind.PercentOfVolume:
                    return units * price * QuoteToAccount * CommissionValue / 100.0;
                case CommissionKind.QuoteCurrencyPerLot:
                    if (LotSize <= 0) return double.NaN;
                    return units / LotSize * CommissionValue * QuoteToAccount;
                default:
                    return double.NaN;
            }
        }

        /// <summary>
        /// Walidacja specyfikacji. Zwraca null jeśli wszystko w porządku, w przeciwnym razie powód odrzucenia (PL).
        /// Brak możliwości weryfikacji wolumenu lub kosztu = brak handlu.
        /// </summary>
        public string? Validate()
        {
            if (string.IsNullOrWhiteSpace(Name)) return "brak nazwy instrumentu";
            if (!(PipSize > 0) || double.IsInfinity(PipSize)) return "nieprawidłowy rozmiar pipsa";
            if (!(TickSize > 0) || double.IsInfinity(TickSize)) return "nieprawidłowy rozmiar ticka";
            if (!(PipValuePerUnit > 0) || double.IsInfinity(PipValuePerUnit)) return "nie można ustalić wartości pipsa";
            if (!(VolumeMin > 0)) return "nieprawidłowy minimalny wolumen";
            if (!(VolumeStep > 0)) return "nieprawidłowy krok wolumenu";
            if (!(VolumeMax >= VolumeMin)) return "nieprawidłowy maksymalny wolumen";
            if (CommissionKind == CommissionKind.Unknown) return "nieznany typ prowizji – koszt niezweryfikowany";
            if (double.IsNaN(CommissionOneSide(VolumeMin, 1.0))) return "nie można policzyć prowizji";
            if (!TradingEnabled) return "handel instrumentem wyłączony";
            if (CloseOnly) return "instrument w trybie tylko-zamykanie";
            return null;
        }
    }

    /// <summary>Migawka stanu konta.</summary>
    public sealed class AccountSnapshot
    {
        public long Number { get; init; }
        public bool IsLive { get; init; }
        public string BrokerName { get; init; } = "";
        public string Currency { get; init; } = "";
        public double Balance { get; init; }
        public double Equity { get; init; }
        public double Margin { get; init; }
        public double FreeMargin { get; init; }
        public double? MarginLevel { get; init; }
        public bool IsHedging { get; init; } = true;
        public DateTime TimeUtc { get; init; }

        public bool IsValid => Balance > 0 && Equity > 0 && !double.IsNaN(Equity) && !double.IsNaN(Balance);
    }

    /// <summary>Informacja o otwartej pozycji (dowolnego pochodzenia – filtrowanie po etykiecie robi silnik).</summary>
    public sealed class PositionInfo
    {
        public long Id { get; init; }
        public string Symbol { get; init; } = "";
        public TradeDirection Direction { get; init; }
        public double VolumeUnits { get; init; }
        public double EntryPrice { get; init; }
        public DateTime EntryTimeUtc { get; init; }
        public double? StopLoss { get; init; }
        public double? TakeProfit { get; init; }
        public string Label { get; init; } = "";
        public string Comment { get; init; } = "";
        public double NetProfit { get; init; }
        public double GrossProfit { get; init; }
        public double Commissions { get; init; }
        public double Swap { get; init; }
        public double CurrentPrice { get; init; }
        public double Margin { get; init; }
    }

    /// <summary>Żądanie otwarcia pozycji rynkowej.</summary>
    public sealed class OrderRequest
    {
        public string Symbol { get; init; } = "";
        public TradeDirection Direction { get; init; }
        public double VolumeUnits { get; init; }
        public double StopLossPips { get; init; }
        public double? TakeProfitPips { get; init; }
        public string Label { get; init; } = "";
        public string Comment { get; init; } = "";
        public string SignalId { get; init; } = "";
        /// <summary>Maksymalny poślizg w pipsach (zlecenie market-range). Null = bez limitu.</summary>
        public double? MaxSlippagePips { get; init; }
    }

    /// <summary>Wynik operacji brokerskiej.</summary>
    public sealed class OrderOutcome
    {
        public OrderStatus Status { get; init; }
        public long? PositionId { get; init; }
        public BrokerErrorKind Error { get; init; }
        public string Message { get; init; } = "";
        public double? FillPrice { get; init; }

        public static OrderOutcome Filled(long positionId, double fillPrice) =>
            new() { Status = OrderStatus.Filled, PositionId = positionId, FillPrice = fillPrice };

        public static OrderOutcome Rejected(BrokerErrorKind error, string message) =>
            new() { Status = OrderStatus.Rejected, Error = error, Message = message };

        public static OrderOutcome Uncertain(BrokerErrorKind error, string message) =>
            new() { Status = OrderStatus.Uncertain, Error = error, Message = message };

        /// <summary>Błędy, przy których stan po stronie serwera jest niepewny.</summary>
        public static bool IsUncertainError(BrokerErrorKind e) =>
            e == BrokerErrorKind.Timeout || e == BrokerErrorKind.Disconnected || e == BrokerErrorKind.TechnicalError || e == BrokerErrorKind.Other;
    }
}
