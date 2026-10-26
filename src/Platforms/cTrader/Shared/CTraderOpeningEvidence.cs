using System;
using System.Linq;
using Apex.Core.Model;
using cAlgo.API;

namespace Apex.Platforms.CTrader;

/// <summary>Platform-thread-only observation. Captures original units from the SDK opening graph,
/// never from the requested quantity or a later remaining-position poll. This initial policy
/// supports one filled opening deal only; missing/multi-fill/ambiguous graphs stay UNKNOWN.
/// Available graph evidence is not a completeness certificate or a scoped admission permit.</summary>
public static class CTraderOpeningEvidence
{
    public static OpeningFillEvidence? Capture(Position position, OrderRequest request, string executionId, double volumeStep)
    {
        try
        {
            if (position == null || request == null || string.IsNullOrWhiteSpace(executionId)
                || string.IsNullOrWhiteSpace(request.Symbol) || string.IsNullOrWhiteSpace(request.Label)
                || string.IsNullOrWhiteSpace(request.Comment) || request.Direction is not (TradeDirection.Long or TradeDirection.Short)
                || !Positive(request.VolumeUnits) || !Positive(volumeStep) || !Positive(request.StopLossPips)
                || !request.TakeProfitPips.HasValue || !Positive(request.TakeProfitPips.Value)
                || (request.MaxSlippagePips.HasValue && !Positive(request.MaxSlippagePips.Value))
                || position.Id <= 0 || position.SymbolName != request.Symbol || position.Label != request.Label
                || position.Comment != request.Comment || Direction(position.TradeType) != request.Direction)
                return null;
            var graph = position.Deals;
            if (graph == null || graph.Count == 0 || graph.Count > 256) return null;
            // Unknown impacts may conceal another opening; don't just filter them out.
            if (graph.Any(d => d == null || !d.PositionImpact.HasValue)) return null;
            var openings = graph.Where(d => d.PositionImpact == DealPositionImpact.Opening).ToArray();
            if (openings.Length != 1) return null;
            var d = openings[0];
            double units = d.VolumeInUnits;
            double remaining = position.VolumeInUnits;
            double entryPrice = position.EntryPrice;
            if (d.Id <= 0 || d.OrderId <= 0 || d.PositionId != position.Id || d.Status != DealStatus.Filled
                || !Positive(units) || units > request.VolumeUnits || !double.IsFinite(remaining) || remaining < 0 || remaining > units
                || !Positive(entryPrice) || !d.ExecutionPrice.HasValue || d.ExecutionPrice.Value != entryPrice
                || d.SymbolName != request.Symbol || d.Label != request.Label || d.Comment != request.Comment
                || Direction(d.TradeType) != request.Direction || d.ExecutionTime == default || position.EntryTime == default
                || Utc(d.ExecutionTime) != Utc(position.EntryTime))
                return null;
            // Decimal arithmetic never uses a relative epsilon capable of hiding a legitimate
            // volume quantum. Unsupported representation/overflow is unknown, not rounded proof.
            decimal quantum = (decimal)volumeStep;
            if (quantum <= 0 || !Representable(request.VolumeUnits, volumeStep) || !Representable(units, volumeStep)
                || !Representable(remaining, volumeStep) || (decimal)request.VolumeUnits % quantum != 0
                || (decimal)units % quantum != 0 || (decimal)remaining % quantum != 0) return null;
            return new OpeningFillEvidence(new OpeningRequestEvidence
            {
                ExecutionId = executionId, SignalId = request.SignalId, Symbol = request.Symbol, Direction = request.Direction,
                VolumeUnits = request.VolumeUnits, StopLossPips = request.StopLossPips, TakeProfitPips = request.TakeProfitPips,
                Label = request.Label, Comment = request.Comment, MaxSlippagePips = request.MaxSlippagePips,
            }, position.Id, units, volumeStep, entryPrice, Utc(position.EntryTime), new[]
            {
                new OpeningDealEvidence
                {
                    DealId = d.Id, OrderId = d.OrderId, PositionId = d.PositionId, VolumeUnits = units, Price = d.ExecutionPrice.Value,
                    TimeUtc = Utc(d.ExecutionTime), Symbol = d.SymbolName, Direction = Direction(d.TradeType), Label = d.Label, Comment = d.Comment,
                },
            });
        }
        catch (Exception)
        {
            // API data may be unavailable/late even after execution; keep the broker outcome,
            // but never substitute request/remaining units or silently retry the opening.
            return null;
        }
    }

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
    private static bool Representable(double units, double quantum) => (double)(decimal)units == units
        && Math.BitIncrement(units) - units < quantum;
    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
    private static TradeDirection Direction(TradeType value) => value == TradeType.Buy ? TradeDirection.Long : TradeDirection.Short;
}
