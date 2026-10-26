using System;
using System.Collections.Generic;
using System.Linq;
using Apex.Core.Bridge;
using cAlgo.API;
namespace Apex.Platforms.CTrader;

/// <summary>Called ONLY on the platform thread. Reads the available SDK collection, not a
/// broker-completeness API (none is documented in Automate 1.0.21). Never invents a fallback.</summary>
public static class CTraderHistory
{
    public static IReadOnlyList<ClosedDto> Capture(IEnumerable<HistoricalTrade> history, string label)
    {
        var unique = new Dictionary<(long position, long deal), ClosedDto>();
        int scanned = 0;
        foreach (var h in history)
        {
            if (++scanned > 100000) throw new InvalidOperationException("available history exceeds 100000 rows");
            if (!string.Equals(h.Label, label, StringComparison.Ordinal)) continue;
            // Deliberately choose HistoricalTrade.ClosingDealId:Int32; do not silently replace
            // it with ClosingDeal.Id:Int64. Protocol v1 canonical form is the positive widened int.
            long deal = h.ClosingDealId;
            if (deal <= 0 || h.PositionId <= 0 || !double.IsFinite(h.VolumeInUnits) || h.VolumeInUnits <= 0
                || !double.IsFinite(h.NetProfit) || !double.IsFinite(h.Commissions) || !double.IsFinite(h.Swap)
                || !double.IsFinite(h.GrossProfit) || !double.IsFinite(h.EntryPrice) || h.EntryPrice <= 0
                || !double.IsFinite(h.ClosingPrice) || h.ClosingPrice <= 0)
                throw new InvalidOperationException("invalid SDK closing deal");
            var row = new ClosedDto
            {
                ClosingDealId = deal, Id = h.PositionId, Symbol = h.SymbolName, Label = h.Label ?? "", Comment = h.Comment ?? "",
                Side = h.TradeType == TradeType.Buy ? "buy" : "sell", Units = h.VolumeInUnits,
                Entry = h.EntryPrice, Exit = h.ClosingPrice,
                EntryTime = BridgeProtocol.ToUnixMs(DateTime.SpecifyKind(h.EntryTime, DateTimeKind.Utc)),
                ExitTime = BridgeProtocol.ToUnixMs(DateTime.SpecifyKind(h.ClosingTime, DateTimeKind.Utc)),
                Gross = h.GrossProfit, Commission = h.Commissions, Swap = h.Swap, Net = h.NetProfit, Reason = "History (reason unknown)",
            };
            if (unique.TryGetValue((row.Id, deal), out var old))
            {
                if (BridgeProtocol.Serialize(old) != BridgeProtocol.Serialize(row)) throw new InvalidOperationException("conflicting SDK closing deal");
            }
            else unique.Add((row.Id, deal), row);
        }
        return unique.Values.OrderBy(x => x.ExitTime).ThenBy(x => x.ClosingDealId).ThenBy(x => x.Id).ToArray();
    }
}
