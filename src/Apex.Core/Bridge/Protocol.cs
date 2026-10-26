using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Apex.Core.Model;


namespace Apex.Core.Bridge
{
    /// <summary>
    /// APEX Bridge Protocol v1 – jeden prosty format JSON dla wszystkich platform (cTrader, MetaTrader 5, kolejne).
    /// Każdy komunikat to obiekt JSON z polem "type". Transport: WebSocket (ramki tekstowe) lub TCP (jeden JSON na linię).
    /// Most (wtyczka na platformie) łączy się z APEX Desktop, przesyła dane rynkowe/konta i wykonuje polecenia handlowe.
    /// Pełny opis: docs/PROTOKOL_MOSTU.md.
    /// </summary>
    public static class BridgeProtocol
    {
        public const int Version = 1;
        public const int MaxMessageBytes = 8 * 1024 * 1024;

        public static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
            PropertyNameCaseInsensitive = true,
        };

        public static string Serialize<T>(T msg) => JsonSerializer.Serialize(msg, Json);

        public static string? ReadType(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString()
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public static T? Deserialize<T>(string json) where T : class
        {
            try { return JsonSerializer.Deserialize<T>(json, Json); }
            catch (JsonException) { return null; }
        }

        public static string SideOf(TradeDirection d) => d == TradeDirection.Long ? "buy" : "sell";

        public static TradeDirection? ParseSide(string? s) => s?.ToLowerInvariant() switch
        {
            "buy" or "long" => TradeDirection.Long,
            "sell" or "short" => TradeDirection.Short,
            _ => null,
        };

        public static DateTime FromUnixMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
        public static DateTime FromUnixSec(long s) => DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime;
        public static long ToUnixMs(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        public static long ToUnixSec(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

        public static BrokerErrorKind ParseError(string? e)
        {
            if (string.IsNullOrEmpty(e)) return BrokerErrorKind.None;
            return Enum.TryParse<BrokerErrorKind>(e, true, out var k) ? k : BrokerErrorKind.Other;
        }
    }

    // ------------------------------------------------------------------ most → APEX

    public sealed class AccountDto
    {
        public long Number { get; set; }
        public string Broker { get; set; } = "";
        public string Currency { get; set; } = "";
        public bool IsLive { get; set; }
        public bool Hedging { get; set; } = true;
        public double Balance { get; set; }
        public double Equity { get; set; }
        public double Margin { get; set; }
        public double FreeMargin { get; set; }
        public double? MarginLevel { get; set; }

        public AccountSnapshot ToSnapshot(DateTime utc) => new()
        {
            Number = Number,
            IsLive = IsLive,
            BrokerName = Broker,
            Currency = Currency,
            Balance = Balance,
            Equity = Equity,
            Margin = Margin,
            FreeMargin = FreeMargin,
            MarginLevel = MarginLevel,
            IsHedging = Hedging,
            TimeUtc = utc,
        };
    }

    public sealed class HelloMsg
    {
        public string Type { get; set; } = "hello";
        public int Protocol { get; set; } = BridgeProtocol.Version;
        public string Platform { get; set; } = "";
        public string BridgeVersion { get; set; } = "";
        public string? HistoryCapability { get; set; }
        public string? ScopedCapability { get; set; }
        public string Label { get; set; } = "";
        public string BrokerEpoch { get; set; } = "";
        public bool? BrokerConnected { get; set; }
        public string Key { get; set; } = "";
        public List<PlatformStateInventory>? DeploymentInventory { get; set; }
        public AccountDto Account { get; set; } = new();
        public long Time { get; set; }
    }

    public sealed class SymbolDto
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string BaseAsset { get; set; } = "";
        public string QuoteAsset { get; set; } = "";
        public int Digits { get; set; }
        public double PipSize { get; set; }
        public double TickSize { get; set; }
        /// <summary>Wartość 1 pipsa dla 1 jednostki wolumenu w walucie konta.</summary>
        public double PipValue { get; set; }
        public double LotSize { get; set; }
        public double VolumeMin { get; set; }
        public double VolumeStep { get; set; }
        public double VolumeMax { get; set; }
        public double MinStopPips { get; set; }
        public bool TradingEnabled { get; set; } = true;
        public bool CloseOnly { get; set; }
        public string CommissionKind { get; set; } = "Unknown";
        public double CommissionValue { get; set; }
        /// <summary>Opcjonalne v1: minimum za jedną stronę w walucie konta; brak pól = starszy most bez minimum.</summary>
        public double? MinCommissionAccount { get; set; }
        /// <summary>false = minimum niezweryfikowane; nie wolno ominąć tego przez nadpisanie prowizji bazowej.</summary>
        public bool? MinCommissionKnown { get; set; }
        public double UsdToAccount { get; set; } = 1;
        public double QuoteToAccount { get; set; } = 1;
        public bool IsCustom { get; set; }
    }

    public sealed class SymbolsMsg
    {
        public string Type { get; set; } = "symbols";
        public List<SymbolDto> Items { get; set; } = new();
        public bool Final { get; set; } = true;
    }

    public sealed class BarsMsg
    {
        public string Type { get; set; } = "bars";
        public string Symbol { get; set; } = "";
        public string Tf { get; set; } = "H1";
        /// <summary>[czas otwarcia (unix s, UTC), open, high, low, close, wolumen] – wyłącznie ZAMKNIĘTE świece.</summary>
        public List<double[]> Bars { get; set; } = new();
        /// <summary>true = pełna historia (zastępuje dotychczasowe dane), false = dopisanie nowych świec.</summary>
        public bool Snapshot { get; set; }
    }

    public sealed class QuoteDto
    {
        public string S { get; set; } = "";
        public double B { get; set; }
        public double A { get; set; }
        /// <summary>Czas notowania (unix ms).</summary>
        public long T { get; set; }
        /// <summary>Aktualna wartość pipsa (na jednostkę, waluta konta) – zmienia się z kursem przeliczeniowym.</summary>
        public double? Pv { get; set; }
        /// <summary>Czy rynek otwarty.</summary>
        public bool O { get; set; } = true;
        /// <summary>Sekundy do zamknięcia sesji (-1 = nieznane).</summary>
        public long Ttc { get; set; } = -1;
    }

    public sealed class QuotesMsg
    {
        public string Type { get; set; } = "quotes";
        public List<QuoteDto> Items { get; set; } = new();
    }

    public sealed class AccountMsg
    {
        public string Type { get; set; } = "account";
        public AccountDto Account { get; set; } = new();
        public BrokerStateMsg? BrokerState { get; set; }
    }

    public sealed class PositionDto
    {
        public long Id { get; set; }
        public string Symbol { get; set; } = "";
        public string Side { get; set; } = "buy";
        public double Units { get; set; }
        public double Entry { get; set; }
        public long EntryTime { get; set; }
        public double? Sl { get; set; }
        public double? Tp { get; set; }
        public string Label { get; set; } = "";
        public string Comment { get; set; } = "";
        public double Net { get; set; }
        public double Gross { get; set; }
        public double Commission { get; set; }
        public double Swap { get; set; }
        public double Price { get; set; }
        public double Margin { get; set; }

        public PositionInfo ToInfo() => new()
        {
            Id = Id,
            Symbol = Symbol,
            Direction = BridgeProtocol.ParseSide(Side) ?? TradeDirection.Long,
            VolumeUnits = Units,
            EntryPrice = Entry,
            EntryTimeUtc = BridgeProtocol.FromUnixMs(EntryTime),
            StopLoss = Sl,
            TakeProfit = Tp,
            Label = Label,
            Comment = Comment,
            NetProfit = Net,
            GrossProfit = Gross,
            Commissions = Commission,
            Swap = Swap,
            CurrentPrice = Price,
            Margin = Margin,
        };
    }

    public sealed class PositionsMsg
    {
        public string Type { get; set; } = "positions";
        public List<PositionDto> Items { get; set; } = new();
        public List<PendingOrderDto>? PendingOrders { get; set; }
        public ScopeFrame? Scoped { get; set; }
    }

    public sealed record PlatformStateInventory(string Label, bool Healthy, int Entries, bool Missing);
    public sealed record PendingOrderDto(long Id, string Label, string Symbol = "", string Side = "", double Units = 0, double Target = 0, double? Sl = null, double? Tp = null, string Comment = "");

    // DATA only; the local owner/Core authority is never serialized.
    public sealed record ScopeFrame
    {
        public const string Capability = "scoped-recovery-v1";
        public Guid BaselineId { get; init; }
        public string Label { get; init; } = "";
        public string ExecutionPath { get; init; } = "desktop-bridge";
        public string SafetyVersion { get; init; } = "scoped-demo-v1";
        public string TransportGeneration { get; init; } = "";
        public string BrokerEpoch { get; init; } = "";
        public bool? BrokerConnected { get; init; }
        public long? PositionRevision { get; init; }
        public long? PendingOrderRevision { get; init; }
        public string SnapshotId { get; init; } = "";
    }

    public sealed class BrokerStateMsg
    {
        public string Type { get; set; } = "brokerState";
        public string BrokerEpoch { get; set; } = "";
        public bool? BrokerConnected { get; set; }
    }

    public sealed class ClosedDto
    {
        public long? ClosingDealId { get; set; }
        public long Id { get; set; }
        public string Symbol { get; set; } = "";
        public string Label { get; set; } = "";
        public string Comment { get; set; } = "";
        public string Side { get; set; } = "buy";
        public double Units { get; set; }
        public double Entry { get; set; }
        public double Exit { get; set; }
        public long EntryTime { get; set; }
        public long ExitTime { get; set; }
        public double Gross { get; set; }
        public double Commission { get; set; }
        public double Swap { get; set; }
        public double Net { get; set; }
        /// <summary>StopLoss | TakeProfit | Closed | StopOut</summary>
        public string Reason { get; set; } = "Closed";

    }

    public sealed class HistoryRequest
    {
        public string Type { get; set; } = "historyRequest";
        public string RequestId { get; set; } = "";
        public string Label { get; set; } = "";
        public Guid BaselineId { get; set; }
        public List<string> RecoverOperations { get; set; } = new();
    }

    public sealed class HistoryPage
    {
        public const string Capability = "closing-deals-v1";
        public string Type { get; set; } = "historyPage";
        public string RequestId { get; set; } = "";
        public int Page { get; set; }
        public int Pages { get; set; }
        public int Total { get; set; }
        public string Scope { get; set; } = "available-history-v1";
        public string Coverage { get; set; } = "unknown";
        public long CapturedAt { get; set; }
        public string? Error { get; set; }
        public AccountDto? Account { get; set; }
        public List<PositionDto>? Positions { get; set; }
        public List<ClosedDto> Deals { get; set; } = new();
        public ScopeFrame? Scoped { get; set; }
        public List<PendingOrderDto>? PendingOrders { get; set; }
        public List<OpeningDealEvidence>? OpeningObservations { get; set; }
        public List<ResultMsg>? RecoveredResults { get; set; }
    }

    public sealed class ClosedMsg
    {
        public string Type { get; set; } = "closed";
        public ClosedDto Position { get; set; } = new();
    }

    public sealed class ResultMsg
    {
        public string Type { get; set; } = "result";
        public string Id { get; set; } = "";
        /// <summary>filled | rejected | uncertain</summary>
        public string Status { get; set; } = "";
        public long? PositionId { get; set; }
        public double? Price { get; set; }
        /// <summary>Optional immutable original actual opening fill; absent means unknown, not zero.</summary>
        public OpeningFillEvidence? OpeningFill { get; set; }
        public string? Error { get; set; }
        public string? Message { get; set; }
        public BrokerStateMsg? BrokerState { get; set; }
    }

    /// <summary>One lossless result boundary for Desktop and Native, including uncertain fills.
    /// Evidence is transported, not authorized: only Core's future scope validator can admit it.</summary>
    public static class ProtocolResult
    {
        public static OrderOutcome ToOutcome(ResultMsg result) => new()
        {
            Status = result.Status?.ToLowerInvariant() switch
            {
                "filled" => OrderStatus.Filled,
                "rejected" => OrderStatus.Rejected,
                _ => OrderStatus.Uncertain,
            },
            PositionId = result.PositionId,
            FillPrice = result.Price,
            OpeningFill = result.OpeningFill,
            Error = BridgeProtocol.ParseError(result.Error),
            Message = result.Message ?? result.Error ?? "stan niepewny",
        };

        public static ResultMsg FromOutcome(string id, OrderOutcome outcome) => new()
        {
            Id = id,
            Status = outcome.Status == OrderStatus.Filled ? "filled" : outcome.Status == OrderStatus.Rejected ? "rejected" : "uncertain",
            PositionId = outcome.PositionId,
            Price = outcome.FillPrice,
            OpeningFill = outcome.OpeningFill,
            Error = outcome.Error.ToString(),
            Message = outcome.Message,
        };
    }

    public sealed class MarginResultMsg
    {
        public string Type { get; set; } = "marginResult";
        public string Id { get; set; } = "";
        public double? Margin { get; set; }
    }

    public sealed class LogMsg
    {
        public string Type { get; set; } = "log";
        public string Level { get; set; } = "info";
        public string Message { get; set; } = "";
    }

    // ------------------------------------------------------------------ APEX → most

    public sealed class WelcomeMsg
    {
        public string Type { get; set; } = "welcome";
        public bool Accepted { get; set; }
        public string Reason { get; set; } = "";
        public string HostVersion { get; set; } = "";
        public string Label { get; set; } = "APEX";
        public string? ScopedCapability { get; set; }
        public Guid BaselineId { get; set; }
        public string TransportGeneration { get; set; } = "";
    }

    public sealed class SubscribeMsg
    {
        public string Type { get; set; } = "subscribe";
        public List<string> Symbols { get; set; } = new();
        public List<string> Tfs { get; set; } = new();
        public Dictionary<string, int> History { get; set; } = new();
    }

    public sealed class OpenCmd
    {
        public string Type { get; set; } = "open";
        public string Id { get; set; } = "";
        public string? SignalId { get; set; }
        public string Symbol { get; set; } = "";
        public string Side { get; set; } = "buy";
        public double Units { get; set; }
        public double SlPips { get; set; }
        public double? TpPips { get; set; }
        public string Label { get; set; } = "";
        public string Comment { get; set; } = "";
        public double? MaxSlipPips { get; set; }
        public ScopeFrame? Scoped { get; set; }
    }

    public sealed class ModifyCmd
    {
        public string Type { get; set; } = "modify";
        public string Id { get; set; } = "";
        public long PositionId { get; set; }
        public double Sl { get; set; }
    }

    public sealed class CloseCmd
    {
        public string Type { get; set; } = "close";
        public string Id { get; set; } = "";
        public long PositionId { get; set; }
        public double? Units { get; set; }
    }

    public sealed class MarginCmd
    {
        public string Type { get; set; } = "margin";
        public string Id { get; set; } = "";
        public string Symbol { get; set; } = "";
        public string Side { get; set; } = "buy";
        public double Units { get; set; }
    }

    public sealed class SimpleMsg
    {
        public string Type { get; set; } = "ping";
        public string? Reason { get; set; }
    }
}
