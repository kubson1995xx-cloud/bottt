using System;

namespace Apex.Core.Model;

/// <summary>Positive SDK execution witness, frozen only after successful exact partial close.
/// Volume decrease alone is never this witness. The scoped closing union must still balance.</summary>
public sealed record ReductionFillEvidence
{
    public string ActionId { get; init; } = "";
    public long PositionId { get; init; }
    public string SignalId { get; init; } = "";
    public string Label { get; init; } = "";
    public string Symbol { get; init; } = "";
    public double BeforeUnits { get; init; }
    public double ClosedUnits { get; init; }
    public double RemainingUnits { get; init; }
}
