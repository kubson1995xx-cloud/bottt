using Apex.Core.Bridge;
using Apex.Core.Model;
namespace Apex.Core.Engine;

public static class HistoryMapping
{
    public static ClosedPositionInfo ToInfo(this ClosedDto d) => new()
    {
        ClosingDealId = d.ClosingDealId, PositionId = d.Id, Symbol = d.Symbol, Label = d.Label, Comment = d.Comment,
        Direction = BridgeProtocol.ParseSide(d.Side) ?? TradeDirection.Long, Volume = d.Units, EntryPrice = d.Entry, ExitPrice = d.Exit,
        EntryTimeUtc = BridgeProtocol.FromUnixMs(d.EntryTime), ExitTimeUtc = BridgeProtocol.FromUnixMs(d.ExitTime),
        GrossProfit = d.Gross, Commission = d.Commission, Swap = d.Swap, NetProfit = d.Net, PlatformReason = d.Reason,
    };
}
