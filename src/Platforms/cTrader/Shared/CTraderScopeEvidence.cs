using System;
using System.Collections.Generic;
using System.Linq;
using Apex.Core.Bridge;
using Apex.Core.Model;
using cAlgo.API;

namespace Apex.Platforms.CTrader;

public static class CTraderScopeEvidence
{
    // SDK main thread only. Empty/unavailable graphs do NOT assert completeness. Every opening
    // reference actually supplied by either current positions or closing relationships is retained.
    public static IReadOnlyList<OpeningDealEvidence> CaptureOpenings(IEnumerable<Position> positions, IEnumerable<HistoricalTrade> history, string label)
    {
        var observations = new Dictionary<long, OpeningDealEvidence>();
        void Add(Deal d)
        {
            if (d == null || d.PositionImpact == null) throw new InvalidOperationException("unknown deal impact");
            if (d.PositionImpact != DealPositionImpact.Opening) return;
            if (d.Status != DealStatus.Filled || !d.ExecutionPrice.HasValue) throw new InvalidOperationException("unconfirmed opening observation");
            var frozen = new OpeningDealEvidence { DealId = d.Id, OrderId = d.OrderId, PositionId = d.PositionId, VolumeUnits = d.VolumeInUnits,
                Price = d.ExecutionPrice.Value, TimeUtc = DateTime.SpecifyKind(d.ExecutionTime, DateTimeKind.Utc), Symbol = d.SymbolName,
                Label = d.Label ?? "", Comment = d.Comment ?? "", Direction = d.TradeType == TradeType.Buy ? TradeDirection.Long : TradeDirection.Short };
            if (observations.TryGetValue(d.Id, out var prior) && prior != frozen) throw new InvalidOperationException("opening observation conflict");
            observations[d.Id] = frozen;
            if (observations.Count > 100000) throw new InvalidOperationException("opening observations overflow");
        }
        foreach (var p in positions.Where(p => p.Label == label))
            if (p.Deals != null) foreach (var deal in p.Deals) Add(deal);
        foreach (var row in history.Where(h => h.Label == label))
            if (row.ClosingDeal?.Closing != null) foreach (var deal in row.ClosingDeal.Closing) Add(deal);
        return observations.Values.OrderBy(x => x.DealId).ToArray();
    }
    public static List<PendingOrderDto> Pending(IEnumerable<PendingOrder> orders) => orders.Select(p => new PendingOrderDto(p.Id, p.Label ?? "", p.SymbolName,
        p.TradeType == TradeType.Buy ? "buy" : "sell", p.VolumeInUnits, p.TargetPrice, p.StopLoss, p.TakeProfit, p.Comment ?? "")).ToList();
}
