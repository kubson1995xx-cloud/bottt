using System;
using System.Collections.Generic;
using Apex.Core.Indicators;
using Apex.Core.Management;
using Apex.Core.Model;
using Apex.Core.Performance;

namespace Apex.Core.Engine
{
    /// <summary>Dostęp do brokera (cTrader na żywo, symulator w backteście, atrapa w testach).</summary>
    public interface IBrokerGateway
    {
        bool IsConnected { get; }
        AccountSnapshot GetAccount();
        /// <summary>Wszystkie otwarte pozycje na koncie (silnik sam filtruje własne po etykiecie).</summary>
        IReadOnlyList<PositionInfo> GetPositions();
        OrderOutcome OpenMarket(OrderRequest request);
        OrderOutcome ModifyStopLoss(long positionId, double stopLoss);
        OrderOutcome ClosePosition(long positionId, double? units);
        /// <summary>Szacowany depozyt dla nowej pozycji w walucie konta; NaN gdy nieznany.</summary>
        double EstimateMargin(string symbol, TradeDirection direction, double units);
    }

    /// <summary>Optional capability. Complete means the entire AVAILABLE history collection
    /// and matching open-position snapshot were captured on the platform thread. Generation changes
    /// after reconnect; failure/incomplete snapshots must never authorize new risk.</summary>
    public interface IBrokerHistory
    {
        BrokerHistorySnapshot GetHistory();
    }

    public sealed class BrokerHistorySnapshot
    {
        public long AccountNumber { get; init; }
        public string Generation { get; init; } = "";
        public bool Complete { get; init; }
        public bool RequiredCoverage { get; init; }
        public string Scope { get; init; } = "available-history-v1";
        public IReadOnlyList<PositionInfo>? Positions { get; init; }
        public IReadOnlyList<ClosedPositionInfo> Closed { get; init; } = Array.Empty<ClosedPositionInfo>();
    }

    /// <summary>TradeClosed must durably accept the per-account terminal position key, idempotently,
    /// or throw. ClosedRecords includes all accepted keys, not merely the UI's recent window.</summary>
    public interface IRecoveryJournal : ITradeJournal
    {
        bool IsHealthy => true;
        IReadOnlyList<TradeRecord> ClosedRecords { get; }
    }

    /// <summary>Dane rynkowe: specyfikacje, ceny, godziny handlu.</summary>
    public interface IMarketData
    {
        InstrumentSpec? GetSpec(string symbol);
        Quote? GetQuote(string symbol);
        /// <summary>Czy rynek jest otwarty; <paramref name="timeToClose"/> = czas do zamknięcia sesji (TimeSpan.MaxValue gdy nieznany).</summary>
        bool IsMarketOpen(string symbol, DateTime utc, out TimeSpan timeToClose);
    }

    /// <summary>Dostęp do wskaźników na zamkniętych świecach (na żywo: okno ostatnich świec; backtest: pełna historia).</summary>
    public interface IFeatureProvider
    {
        /// <summary>Czas otwarcia ostatniej ZAMKNIĘTEJ świecy (szybkie sprawdzenie, czy pojawiła się nowa świeca).</summary>
        DateTime? LastClosedBarTime(string symbol, Tf tf, DateTime utc);

        /// <summary>Zestaw wskaźników i indeks ostatniej zamkniętej świecy na moment <paramref name="utc"/>.</summary>
        bool TryGet(string symbol, Tf tf, DateTime utc, out FeatureSet features, out int index);
    }

    public interface IEngineLog
    {
        void Info(string message);
        void Warn(string message);
        void Error(string message);
    }

    /// <summary>Trwały magazyn klucz–wartość (cTrader LocalStorage / plik / pamięć).</summary>
    public interface IKeyValueStore
    {
        string? Get(string key);
        void Set(string key, string value);
        void Flush();
    }

    public interface ITradeJournal
    {
        void TradeOpened(TradePlan plan, double volume, string details);
        void TradeClosed(TradeRecord record);
        void Signal(string line);
        void Event(string line);
    }

    public sealed class NullJournal : IRecoveryJournal
    {
        public IReadOnlyList<TradeRecord> ClosedRecords => Array.Empty<TradeRecord>();
        public void TradeOpened(TradePlan plan, double volume, string details) { }
        public void TradeClosed(TradeRecord record) { }
        public void Signal(string line) { }
        public void Event(string line) { }
    }

    public sealed class MemoryStore : IKeyValueStore
    {
        private readonly Dictionary<string, string> _d = new();
        public string? Get(string key) => _d.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value) => _d[key] = value;
        public void Flush() { }
    }

    public sealed class NullLog : IEngineLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
    }

    /// <summary>Instrument zakwalifikowany do skanowania.</summary>
    public sealed class UniverseEntry
    {
        public string Symbol { get; init; } = "";
        public AssetClass AssetClass { get; init; }
        public string? BaseCurrency { get; init; }
        public string? QuoteCurrency { get; init; }
    }
}
