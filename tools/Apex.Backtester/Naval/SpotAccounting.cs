using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Apex.Backtester.Naval;

public sealed class SpotPosition
{
    public SpotFill Entry { get; }
    public decimal Quantity { get; internal set; }
    public decimal CostBasis => Quantity * Entry.ExecutedPrice;
    internal decimal Net, Fees, Impact;
    internal SpotPosition(SpotFill entry, decimal quantity) { Entry = entry; Quantity = quantity; }
}

// Jeden ledger quote/base. Cost basis nie jest drugim debetem cash.
public sealed class SpotAccount
{
    readonly Dictionary<string, SpotPosition> positions = new(StringComparer.Ordinal);
    readonly HashSet<string> fillIds = new(StringComparer.Ordinal);
    readonly HashSet<string> positionIds = new(StringComparer.Ordinal);
    readonly List<SpotCashflow> cashflows = new();
    readonly List<SpotTrade> trades = new();
    public string QuoteAsset { get; }
    public decimal InitialCash { get; }
    public decimal Cash { get; private set; }
    public IReadOnlyDictionary<string, SpotPosition> Positions { get; }
    public IReadOnlyList<SpotCashflow> Cashflows => cashflows.AsReadOnly();
    public IReadOnlyList<SpotTrade> Trades => trades.AsReadOnly();
    public SpotAccount(string quoteAsset, decimal initialCash)
    {
        if (string.IsNullOrWhiteSpace(quoteAsset) || initialCash <= 0) throw new ArgumentException("Invalid account");
        QuoteAsset = quoteAsset; Cash = InitialCash = initialCash;
        Positions = new ReadOnlyDictionary<string, SpotPosition>(positions);
    }
    public decimal LiquidationEquity(IReadOnlyDictionary<string, decimal> marks, SpotCostModel cost)
        => Cash + positions.Sum(p => p.Value.Quantity * cost.Price(SpotSide.Sell, marks[p.Key]) * (1 - cost.FeeRate));

    public void Apply(SpotFill f)
    {
        if (string.IsNullOrWhiteSpace(f.Id) || string.IsNullOrWhiteSpace(f.PositionId)
            || string.IsNullOrWhiteSpace(f.Instrument) || string.IsNullOrWhiteSpace(f.BaseAsset)
            || f.BaseAsset == f.QuoteAsset || f.QuoteAsset != QuoteAsset || f.TimeUtc.Offset != TimeSpan.Zero
            || !Enum.IsDefined(f.Side) || f.Quantity <= 0 || f.ReferencePrice <= 0 || f.ExecutedPrice <= 0
            || f.Fee < 0 || f.InitialRisk < 0 || (f.FeeCurrency != f.BaseAsset && f.FeeCurrency != QuoteAsset)
            || fillIds.Contains(f.Id)) throw new InvalidOperationException("Invalid/duplicate fill or unsupported fee currency/rebate");
        var quoteFee = f.FeeCurrency == QuoteAsset ? f.Fee : 0m;
        var baseFee = f.FeeCurrency == f.BaseAsset ? f.Fee : 0m;
        var feeValue = quoteFee + baseFee * f.ExecutedPrice;
        var buy = f.Side == SpotSide.Buy;
        var quoteDelta = (buy ? -1 : 1) * f.Quantity * f.ExecutedPrice - quoteFee;
        var baseDelta = (buy ? f.Quantity : -f.Quantity) - baseFee;
        var impact = f.Quantity * (buy ? f.ExecutedPrice - f.ReferencePrice : f.ReferencePrice - f.ExecutedPrice);
        positions.TryGetValue(f.Instrument, out var position);
        if (Cash + quoteDelta < 0 || (buy && (position != null || baseDelta <= 0 || positionIds.Contains(f.PositionId)))
            || (!buy && (position == null || position.Entry.PositionId != f.PositionId
                || position.Entry.BaseAsset != f.BaseAsset || position.Quantity + baseDelta < 0
                || f.TimeUtc < position.Entry.TimeUtc))) throw new InvalidOperationException("Cash/inventory/position admission refused");
        // Wszystkie odmowy przed mutacją. Duplicate fill nie może ponownie uznać cash.
        if (buy)
        {
            position = new SpotPosition(f, baseDelta);
            positions.Add(f.Instrument, position); positionIds.Add(f.PositionId);
        }
        else position!.Quantity += baseDelta;
        position!.Net += quoteDelta; position.Fees += feeValue; position.Impact += impact;
        Cash += quoteDelta; fillIds.Add(f.Id);
        cashflows.Add(new(f, quoteDelta, baseDelta, feeValue, impact));
        if (!buy && position.Quantity == 0)
        {
            var gross = position.Net + position.Fees;
            trades.Add(new(f.PositionId, f.Instrument, position.Entry.TimeUtc, f.TimeUtc,
                gross + position.Impact, gross, position.Fees, position.Impact, position.Net,
                position.Entry.InitialRisk, f.Boundary, f.Reason));
            positions.Remove(f.Instrument);
        }
    }
}
