using System;
using Apex.Core.Model;

namespace Apex.Core.Management;

public enum PartialCloseStage { Reserved, AwaitingEvidence, Rejected, Confirmed, Skipped, LegacyUnknown }

/// <summary>Part of the single authoritative plan/account commit, not an independent ledger.</summary>
public sealed class PartialCloseIntent
{
    public string ActionId { get; set; } = "";
    public long PositionId { get; set; }
    public string SignalId { get; set; } = "";
    public string Label { get; set; } = "";
    public string Symbol { get; set; } = "";
    public int Attempt { get; set; }
    public double ObservedUnits { get; set; }
    public double RequestedUnits { get; set; }
    public double ExpectedRemaining { get; set; }
    public double VolumeStep { get; set; }
    public decimal ClosedBefore { get; set; }
    public long ObservationSequence { get; set; }
    public DateTime ReservedUtc { get; set; }
    public DateTime RetryAfterUtc { get; set; }
    public PartialCloseStage Stage { get; set; }
    public string Reason { get; set; } = "";
    public ReductionFillEvidence? Confirmation { get; set; }
}
