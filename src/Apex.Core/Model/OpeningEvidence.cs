using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Apex.Core.Model
{
    /// <summary>Immutable execution-boundary request, not permission to send an order. The original
    /// execution ID is retained even when a journal replay returns under a different command ID.</summary>
    public sealed record OpeningRequestEvidence
    {
        public string ExecutionId { get; init; } = "";
        public string SignalId { get; init; } = "";
        public string Symbol { get; init; } = "";
        public TradeDirection Direction { get; init; }
        public double VolumeUnits { get; init; }
        public double StopLossPips { get; init; }
        public double? TakeProfitPips { get; init; }
        public string Label { get; init; } = "";
        public string Comment { get; init; } = "";
        public double? MaxSlippagePips { get; init; }
    }

    /// <summary>Separate Int64 opening namespace. Never use this ID as the canonical Int32-derived
    /// HistoricalTrade.ClosingDealId ledger key. These observations do not assert SDK completeness.</summary>
    public sealed record OpeningDealEvidence
    {
        public long DealId { get; init; }
        public int OrderId { get; init; }
        public long PositionId { get; init; }
        public double VolumeUnits { get; init; }
        public double Price { get; init; }
        public DateTime TimeUtc { get; init; }
        public string Symbol { get; init; } = "";
        public TradeDirection Direction { get; init; }
        public string Label { get; init; } = "";
        public string Comment { get; init; } = "";
    }

    /// <summary>Original actual fill observation captured before repair/close/poll. Null evidence
    /// means UNKNOWN, never requested or remaining volume. Immutable even across journal copies.
    /// A consumer must still validate scope, request, provenance and current conservation; merely
    /// deserializing this optional field creates no ownership, history coverage or admission permit.</summary>
    public sealed class OpeningFillEvidence
    {
        public OpeningRequestEvidence Request { get; }
        public long PositionId { get; }
        public double InitialVolumeUnits { get; }
        public double VolumeStep { get; }
        public double EntryPrice { get; }
        public DateTime EntryTimeUtc { get; }
        public IReadOnlyList<OpeningDealEvidence> OpeningDeals { get; }

        [JsonConstructor]
        public OpeningFillEvidence(OpeningRequestEvidence request, long positionId, double initialVolumeUnits,
            double volumeStep, double entryPrice, DateTime entryTimeUtc, IReadOnlyList<OpeningDealEvidence> openingDeals)
        {
            Request = request;
            PositionId = positionId;
            InitialVolumeUnits = initialVolumeUnits;
            VolumeStep = volumeStep;
            EntryPrice = entryPrice;
            EntryTimeUtc = entryTimeUtc;
            // Freeze the collection, not just its interface. Elements and request have init-only scalars.
            OpeningDeals = Array.AsReadOnly((openingDeals ?? Array.Empty<OpeningDealEvidence>()).ToArray());
        }
    }
}
