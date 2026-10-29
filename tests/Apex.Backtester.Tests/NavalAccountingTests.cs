using Apex.Backtester.Naval;
using Xunit;

namespace Apex.Backtester.Tests;

public class NavalAccountingTests
{
    static readonly DateTimeOffset T = DateTimeOffset.Parse("2024-01-01T00:00:00Z");
    static SpotFill Fill(string id, SpotSide side, decimal q, decimal px, decimal fee, string currency = "USDT") =>
        new(id, "p", "BTC-USDT", "BTC", "USDT", T, side, q, px, px, fee, currency);

    [Fact]
    public void Buy_partial_final_reconciles_literal_cash_inventory_and_single_trade()
    {
        var a = new SpotAccount("USDT", 10000m);
        a.Apply(Fill("b", SpotSide.Buy, 10m, 100m, 1m));
        Assert.Equal(8999m, a.Cash); Assert.Equal(10m, a.Positions["BTC-USDT"].Quantity);
        a.Apply(Fill("s1", SpotSide.Sell, 4m, 110m, .44m));
        Assert.Equal(9438.56m, a.Cash); Assert.Equal(6m, a.Positions["BTC-USDT"].Quantity);
        Assert.Empty(a.Trades);
        a.Apply(Fill("s2", SpotSide.Sell, 6m, 90m, .54m));
        Assert.Equal(9978.02m, a.Cash); Assert.Empty(a.Positions);
        var trade = Assert.Single(a.Trades);
        Assert.Equal(-20m, trade.GrossExecution); Assert.Equal(1.98m, trade.Fees); Assert.Equal(-21.98m, trade.Net);
        Assert.Equal(a.Cash, 10000m + a.Cashflows.Sum(x => x.QuoteDelta));
        Assert.Throws<InvalidOperationException>(() => a.Apply(Fill("s2", SpotSide.Sell, 6m, 90m, .54m)));
        Assert.Equal(9978.02m, a.Cash);
    }

    [Fact]
    public void Base_fees_are_inventory_not_a_second_quote_debit()
    {
        var a = new SpotAccount("USDT", 1000m);
        a.Apply(Fill("b", SpotSide.Buy, 5m, 100m, .01m, "BTC"));
        Assert.Equal(500m, a.Cash); Assert.Equal(4.99m, a.Positions["BTC-USDT"].Quantity);
        a.Apply(Fill("s", SpotSide.Sell, 4.98m, 110m, .01m, "BTC"));
        Assert.Empty(a.Positions); Assert.Equal(1047.8m, a.Cash);
        var t = Assert.Single(a.Trades);
        Assert.Equal(47.8m, t.Net); Assert.Equal(2.1m, t.Fees); Assert.Equal(49.9m, t.GrossExecution);
    }

    [Theory]
    [InlineData("BNB", 1)] [InlineData("USDT", -1)]
    public void Unsupported_fee_currency_or_rebate_rejected_atomically(string currency, int fee)
    {
        var a = new SpotAccount("USDT", 1000m);
        Assert.Throws<InvalidOperationException>(() => a.Apply(Fill("b", SpotSide.Buy, 1m, 100m, fee, currency)));
        Assert.Equal(1000m, a.Cash); Assert.Empty(a.Cashflows); Assert.Empty(a.Positions);
    }

    [Fact]
    public void No_borrow_short_duplicate_symbol_or_over_sell()
    {
        var a = new SpotAccount("USDT", 1000m);
        Assert.Throws<InvalidOperationException>(() => a.Apply(Fill("s", SpotSide.Sell, 1, 100, 0)));
        Assert.Throws<InvalidOperationException>(() => a.Apply(Fill("b0", SpotSide.Buy, 10, 100, 1)));
        a.Apply(Fill("b", SpotSide.Buy, 5, 100, 0));
        Assert.Throws<InvalidOperationException>(() => a.Apply(Fill("b2", SpotSide.Buy, 1, 100, 0)));
        Assert.Throws<InvalidOperationException>(() => a.Apply(Fill("s2", SpotSide.Sell, 6, 100, 0)));
        Assert.Equal(500m, a.Cash); Assert.Single(a.Cashflows);
    }

    [Fact]
    public void Directional_price_impact_is_not_charged_again_as_cashfee()
    {
        var c = SpotCostModel.Named("base");
        Assert.Equal(100.07m, c.Price(SpotSide.Buy, 100m));
        Assert.Equal(99.93m, c.Price(SpotSide.Sell, 100m));
        var a = new SpotAccount("USDT", 1000m);
        a.Apply(new("b", "p", "BTC-USDT", "BTC", "USDT", T, SpotSide.Buy, 1, 100, c.Price(SpotSide.Buy, 100), .10007m, "USDT"));
        a.Apply(new("s", "p", "BTC-USDT", "BTC", "USDT", T, SpotSide.Sell, 1, 100, c.Price(SpotSide.Sell, 100), .09993m, "USDT"));
        var t = Assert.Single(a.Trades);
        Assert.Equal(999.66m, a.Cash); Assert.Equal(-.14m, t.GrossExecution);
        Assert.Equal(0m, t.GrossReference); Assert.Equal(.14m, t.PriceImpact); Assert.Equal(-.34m, t.Net);
    }

    [Fact]
    public void Sizing_rounds_down_reserves_fee_and_stop_impact_under_half_percent()
    {
        var c = SpotCostModel.Named("base");
        var s = SpotSizing.Calculate(10000m, 10000m, 100m, 95m, .01m, .01m, c);
        Assert.True(s.Quantity > 0); Assert.Equal(0m, s.Quantity % .01m);
        Assert.True(s.Risk <= 50m); Assert.True(s.CashRequired <= 10000m);
        var capped = SpotSizing.Calculate(10000m, 20m, 100m, 95m, .01m, .01m, c);
        Assert.True(capped.CashRequired <= 20m);
        Assert.Equal(0m, SpotSizing.Calculate(10000m, 1m, 100m, 95m, .01m, .1m, c).Quantity);
        Assert.Throws<ArgumentException>(() => SpotSizing.Calculate(10000m, 10000m, 100m, 101m, .01m, .01m, c));
    }
}
