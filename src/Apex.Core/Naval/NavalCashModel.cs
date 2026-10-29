using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace Apex.Core.Naval;

public enum NavalEnvironment { Offline, OkxDemo, Live }
public sealed record NavalAccountId(string Venue, NavalEnvironment Environment, string AccountId, string QuoteCurrency);
public sealed record NavalPortfolioId(string PortfolioId, string ReportingCurrency);
public sealed record NavalCashLimits(decimal TradePct = 0.5m, decimal DailyPct = 1.5m, decimal DrawdownPct = 5m,
    decimal MaxLeverage = 1m, int CentralMax = 3, int AlphaMax = 2, int BravoMax = 2);
public sealed record NavalEntryIntent(string CommandId, string SignalId, string Instrument, decimal Quantity,
    decimal EntryPriceCeiling, decimal StopPrice, decimal TakeProfitPrice);
public enum NavalReservationStage { Reserved, Confirmed, Uncertain, Rejected, Terminal }
public enum NavalMutationStatus { Applied, Duplicate, Refused, Stopped }
public sealed record NavalRiskResult(NavalMutationStatus Status, string Code, string? ReservationId, long DurableRevision);
public interface INavalFleetRisk { NavalRiskResult TryReserve(NavalEntryIntent intent); }
public sealed record NavalInstrument(string Instrument, string BaseCurrency, string QuoteCurrency,
    decimal Quantum, decimal MinimumQuantity, decimal TickSize);
public sealed record NavalSeed(NavalAccountId Account, decimal Allocation);
public sealed record NavalFeeSchedule(decimal EntryRate = .001m, bool EntryInBase = false,
    decimal ExitRate = .001m, bool ExitInBase = false, decimal StopSlippageRate = 0m);
internal sealed record NavalSignal(string SignalId, NavalAccountId Account, NavalFleet Fleet, string Instrument,
    string RuleIdentity, DateTimeOffset EarliestUtc, DateTimeOffset ExpiresUtc);
public sealed class NavalPortfolioDefinition
{
    public NavalPortfolioId Id { get; }
    public NavalCashLimits Limits { get; }
    public IReadOnlyList<NavalSeed> Members { get; }
    public IReadOnlyList<NavalInstrument> Catalog { get; }
    public NavalFeeSchedule Fees { get; }
    internal IReadOnlyList<NavalSignal> Signals { get; }
    internal string Digest => NavalJson.Hash(new { Id, Limits, Members, Catalog, Fees, FreshnessSeconds = 30 });
    internal NavalPortfolioDefinition(NavalPortfolioId id, NavalCashLimits limits, IEnumerable<NavalSeed> members,
        IEnumerable<NavalInstrument> catalog, NavalFeeSchedule fees, IEnumerable<NavalSignal>? signals = null)
    {
        Id = id; Limits = limits; Members = Array.AsReadOnly(members.ToArray());
        Catalog = Array.AsReadOnly(catalog.ToArray()); Fees = fees;
        Signals = Array.AsReadOnly((signals ?? Array.Empty<NavalSignal>()).ToArray());
    }
}
public sealed record NavalCashAccount(NavalAccountId Account, decimal Seed, decimal Cash);
public sealed record NavalReservation
{
    public string Id { get; init; } = "";
    public NavalAccountId Account { get; init; } = null!;
    public NavalFleet Fleet { get; init; }
    public NavalEntryIntent Intent { get; init; } = null!;
    public decimal Quantity { get; init; }
    public decimal Pending { get; init; }
    public decimal Filled { get; init; }
    public decimal Inventory { get; init; }
    public decimal AllocatedCost { get; init; }
    public decimal EntryHold { get; init; }
    public decimal CloseFeeHold { get; init; }
    public decimal RiskHold { get; init; }
    public bool TerminalCoverage { get; init; }
    public NavalReservationStage Stage { get; init; }
}
public sealed record NavalAcceptedFill(string Key, string Digest, NavalFill Fill);
public sealed record NavalCommand(string CommandId, string SignalId, string Digest, NavalRiskResult Result);
public sealed record NavalAcceptedReceipt(string Key, string Digest);
public sealed record NavalPortfolioState
{
    public string Schema { get; init; } = "";
    public NavalPortfolioId Id { get; init; } = null!;
    public string ConfigDigest { get; init; } = "";
    public string StateDigest { get; init; } = "";
    public long Revision { get; init; }
    public IReadOnlyList<NavalCashAccount> Accounts { get; init; } = Array.Empty<NavalCashAccount>();
    public IReadOnlyList<NavalReservation> Reservations { get; init; } = Array.Empty<NavalReservation>();
    public IReadOnlyList<NavalAcceptedFill> Fills { get; init; } = Array.Empty<NavalAcceptedFill>();
    public IReadOnlyList<NavalCommand> Commands { get; init; } = Array.Empty<NavalCommand>();
    public IReadOnlyList<NavalAcceptedReceipt> Receipts { get; init; } = Array.Empty<NavalAcceptedReceipt>();
    public IReadOnlyList<string> Conflicts { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, decimal> Marks { get; init; } = new Dictionary<string, decimal>();
    public long Generation { get; init; }
    public long Sequence { get; init; }
    public DateTimeOffset? ReconciledUtc { get; init; }
    public string DayKey { get; init; } = "";
    public decimal DayStartEquity { get; init; }
    public decimal PeakEquity { get; init; }
    public decimal? LiquidationEquity { get; init; }
    public bool DailyLatch { get; init; }
    public bool DrawdownLatch { get; init; }
    public bool EmergencyLatch { get; init; }
    public string Mode { get; init; } = "STOP";
    public string Reason { get; init; } = "INITIAL_STOP";
}
public sealed record NavalAccountView(NavalAccountId Account, decimal Cash, decimal Held, decimal Free);
public sealed record NavalFleetView(NavalFleet Fleet, decimal NetQuoteCashflow, decimal AllocatedCost, decimal? LiquidationValue,
    decimal? NetContribution, IReadOnlyList<NavalReservation> Reservations, IReadOnlyList<NavalFill> Fills);
public sealed record NavalPortfolioView(long DurableRevision, string Mode, string Reason, string Quality,
    bool Ready, decimal Cash, decimal Held, decimal Free, decimal? LiquidationEquity, decimal? DailyRemaining,
    decimal? DrawdownRemaining, int ActiveCount, bool DailyLatch, bool DrawdownLatch, bool EmergencyLatch,
    IReadOnlyList<NavalAccountView> Accounts, IReadOnlyList<NavalReservation> Reservations,
    IReadOnlyList<NavalFleetView> Fleets);
internal static class NavalJson
{
    internal static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = false };
    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    internal static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(Serialize(value), Options)!;
    internal static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(value))));
}
