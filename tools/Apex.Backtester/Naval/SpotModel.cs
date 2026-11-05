using System;

namespace Apex.Backtester.Naval;

// Wyłącznie założenia badawcze; nie są tierem opłat rachunku OKX.
public enum SpotSide { Buy, Sell }
public sealed record SpotCostModel(decimal FeeRate, decimal HalfSpreadBps, decimal SlippageBps)
{
    public static SpotCostModel Named(string name) => name switch
    {
        "base" => new(.001m, 2m, 5m), "elevated" => new(.0015m, 3m, 7.5m),
        "double" => new(.002m, 4m, 10m), _ => throw new ArgumentException("Unknown cost: " + name)
    };
    public void Validate()
    {
        if (FeeRate < 0 || FeeRate >= 1 || HalfSpreadBps < 0 || SlippageBps < 0
            || HalfSpreadBps + SlippageBps >= 10000) throw new ArgumentException("Invalid modeled cost");
    }
    public decimal Price(SpotSide side, decimal reference)
    {
        Validate();
        if (reference <= 0 || !Enum.IsDefined(side)) throw new ArgumentException("Invalid price/side");
        return reference * (1m + (side == SpotSide.Buy ? 1 : -1) * (HalfSpreadBps + SlippageBps) / 10000m);
    }
}
public sealed record SpotFill(string Id, string PositionId, string Instrument, string BaseAsset, string QuoteAsset,
    DateTimeOffset TimeUtc, SpotSide Side, decimal Quantity, decimal ReferencePrice, decimal ExecutedPrice,
    decimal Fee, string FeeCurrency, string Reason = "", bool Boundary = false, decimal InitialRisk = 0);
public sealed record SpotCashflow(SpotFill Fill, decimal QuoteDelta, decimal BaseDelta, decimal FeeQuote,
    decimal PriceImpact);
public sealed record SpotTrade(string PositionId, string Instrument, DateTimeOffset EntryUtc, DateTimeOffset ExitUtc,
    decimal GrossReference, decimal GrossExecution, decimal Fees, decimal PriceImpact, decimal Net,
    decimal InitialRisk, bool Boundary, string ExitReason)
{
    public decimal? R => InitialRisk > 0 ? Net / InitialRisk : null;
}
public sealed record SpotSize(decimal Quantity, decimal Risk, decimal CashRequired);
public static class SpotSizing
{
    public static SpotSize Calculate(decimal equity, decimal cash, decimal referenceOpen, decimal stop,
        decimal lotSize, decimal minSize, SpotCostModel cost)
    {
        cost.Validate();
        if (equity < 0 || cash < 0 || referenceOpen <= 0 || stop <= 0 || stop >= referenceOpen
            || lotSize <= 0 || minSize <= 0) throw new ArgumentException("Invalid sizing operands");
        var buy = cost.Price(SpotSide.Buy, referenceOpen);
        var sell = cost.Price(SpotSide.Sell, stop);
        var debit = buy * (1 + cost.FeeRate);
        // SL reference bez gap; gap może przekroczyć budżet, co raportujemy, nie maskujemy.
        var unitRisk = debit - sell * (1 - cost.FeeRate);
        var quantity = Math.Floor(Math.Min(equity * .005m / unitRisk, cash / debit) / lotSize) * lotSize;
        if (quantity < minSize) quantity = 0;
        return new(quantity, quantity * unitRisk, quantity * debit);
    }
}
