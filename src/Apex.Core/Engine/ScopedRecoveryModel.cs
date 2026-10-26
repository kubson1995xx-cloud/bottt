using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Apex.Core.Management;
using Apex.Core.Model;

namespace Apex.Core.Engine;

public enum RecoveryExecutionPath { Unknown, DesktopBridge, Native }
public enum BaselineStage { Proposed, Established }
public enum AdmissionStage { OutcomeUnknown, Bound, Rejected, Terminal }
public enum ScopedRecoveryStatus { Unestablished, AwaitingEvidence, Ready, Conflict }

/// <summary>Exact values, never sanitized file keys. Missing flags are not DEMO/hedged evidence.</summary>
public sealed record RecoveryIdentity
{
    public string Platform { get; init; } = "";
    public string Broker { get; init; } = "";
    public long AccountNumber { get; init; }
    public string Currency { get; init; } = "";
    public bool? IsLive { get; init; }
    public bool? IsHedging { get; init; }
    public string Label { get; init; } = "";
    public RecoveryExecutionPath ExecutionPath { get; init; }
    public string SafetyVersion { get; init; } = "";
    internal bool Valid => !string.IsNullOrWhiteSpace(Platform) && !string.IsNullOrWhiteSpace(Broker)
        && AccountNumber > 0 && !string.IsNullOrWhiteSpace(Currency) && IsLive == false && IsHedging == true
        && Label.Length is > 0 and <= 20 && ExecutionPath is RecoveryExecutionPath.DesktopBridge or RecoveryExecutionPath.Native
        && SafetyVersion == ScopedRecoveryPolicy.SafetyVersion;
}

public sealed record RecoveryBaseline
{
    public Guid BaselineId { get; init; }
    public string ScopeVersion { get; init; } = "";
    public RecoveryIdentity Identity { get; init; } = new();
    public BaselineStage Stage { get; init; }
    public DateTime EstablishedUtc { get; init; }
    public string EstablishingReceiptId { get; init; } = "";
    public long EstablishingRevision { get; init; }
}

/// <summary>Permanent pre-send evidence; never pruned with plans or signal UI retention.</summary>
public sealed record OpenAdmission
{
    public Guid BaselineId { get; init; }
    public RecoveryIdentity Identity { get; init; } = new();
    public string OperationId { get; init; } = "";
    public OpeningRequestEvidence Request { get; init; } = new();
    public string RequestFingerprint { get; init; } = "";
    public string PlanJson { get; init; } = "";
    public double RiskAmount { get; init; }
    public double RequestedVolumeStep { get; init; }
    public string RiskInputsJson { get; init; } = "";
    public string ReceiptId { get; init; } = "";
    public string TransportGeneration { get; init; } = "";
    public string BrokerGeneration { get; init; } = "";
    public long PositionRevision { get; init; }
    public long PendingOrderRevision { get; init; }
    public DateTime ReservedUtc { get; init; }
    public AdmissionStage Stage { get; init; }
    public OpeningFillEvidence? Fill { get; init; }
    public double? LastRemainingUnits { get; init; }
    public long LastObservationSequence { get; init; } = -1;
    public string OutcomeMessage { get; init; } = "";
}

public sealed record PendingOrderObservation(long Id, string Label);

/// <summary>Data only. Deserialization cannot authorize this receipt. Phase C must freeze and
/// independently fence current generations/revisions before calling the internal owner API.</summary>
public sealed record ScopedReceipt
{
    public RecoveryIdentity Identity { get; init; } = new();
    public Guid BaselineId { get; init; }
    public string RequestId { get; init; } = "";
    public string ReceiptId { get; init; } = "";
    public string TransportGeneration { get; init; } = "";
    public string BrokerGeneration { get; init; } = "";
    public bool BrokerConnected { get; init; }
    public bool TransportComplete { get; init; }
    public DateTime CapturedUtc { get; init; }
    public DateTime ReceivedUtc { get; init; }
    public long Sequence { get; init; }
    public long PositionRevision { get; init; }
    public long PendingOrderRevision { get; init; }
    public IReadOnlyList<PositionInfo>? Positions { get; init; }
    public IReadOnlyList<PendingOrderObservation>? PendingOrders { get; init; }
    public IReadOnlyList<ClosedPositionInfo>? Closed { get; init; }
    public IReadOnlyList<OpeningDealEvidence>? OpeningObservations { get; init; }
}

/// <summary>Owner-only live fence, not a transport DTO. A producer must invalidate on every
/// broker/transport event and position/order revision. Monotonic time cannot come from JSON.</summary>
internal sealed class ScopedReceiptAuthority
{
    internal readonly object Owner;
    internal readonly Func<ScopedReceipt, bool> IsCurrent;
    internal readonly Func<TimeSpan> MonotonicNow;
    internal ScopedReceiptAuthority(object owner, Func<ScopedReceipt, bool> isCurrent, Func<TimeSpan> monotonicNow)
    { Owner = owner; IsCurrent = isCurrent; MonotonicNow = monotonicNow; }
}

internal sealed class ScopedOpenPermit
{
    internal readonly object Owner;
    internal readonly ScopedReceipt Receipt;
    internal readonly long DurableRevision;
    internal readonly TimeSpan Expires;
    internal ScopedOpenPermit(object owner, ScopedReceipt receipt, long revision, TimeSpan expires)
    { Owner = owner; Receipt = receipt; DurableRevision = revision; Expires = expires; }
}

// Issued only across the synchronous Core→gateway call after durable reservation.
internal sealed class ScopedDispatchAuthorization
{
    internal readonly ScopedReceipt Receipt;
    internal readonly Func<OrderRequest, bool> IsCurrent;
    internal ScopedDispatchAuthorization(ScopedReceipt receipt, Func<OrderRequest, bool> isCurrent)
    { Receipt = receipt; IsCurrent = isCurrent; }
}
internal interface IScopedDispatchGateway
{
    OrderOutcome OpenScoped(OrderRequest request, ScopedDispatchAuthorization authorization);
}

public static class ScopedRecoveryPolicy
{
    public const string SafetyVersion = "scoped-demo-v1";
    public const string ScopeVersion = "app-managed-exposure-v1";
    internal static string Fingerprint(OpeningRequestEvidence request) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));

    // No relative epsilon: decimal must roundtrip every operand, including the quantum itself.
    internal static decimal Units(double units, double step)
    {
        if (!double.IsFinite(units) || units < 0 || !double.IsFinite(step) || step <= 0) throw new InvalidOperationException("invalid volume quantum");
        decimal q = (decimal)step, u = (decimal)units;
        if (q <= 0 || (double)q != step || (double)u != units || Math.BitIncrement(units) - units >= step || u % q != 0)
            throw new InvalidOperationException("unrepresentable volume quantum");
        return u;
    }
}
