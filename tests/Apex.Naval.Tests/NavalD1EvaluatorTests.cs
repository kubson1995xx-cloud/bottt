using Apex.Core.Naval;
using Xunit;

namespace Apex.Naval.Tests;

// Fixtures deliberately synthetic: no trades, fills, money or market-history claims.
public class NavalD1EvaluatorTests
{
    internal static readonly DateTimeOffset Start = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
    internal static NavalSeries Series(string asset = "ETH", int count = 200, decimal last = 120m)
    {
        var bars = Enumerable.Range(0, count).Select(i => new NavalBar(Start.AddDays(i),
            100m, i == count - 1 ? Math.Max(130m, last) : 110m,
            i == count - 1 ? Math.Min(80m, last) : 90m, i == count - 1 ? last : 100m, 10m, true)).ToArray();
        return new NavalSeries(asset + "-USD", asset, "USD", "SYNTHETIC", "MID", bars);
    }
    internal static NavalEvaluationRequest Request(bool longPosition = false) =>
        new(NavalFleet.Alpha, Series(), Series("BTC"), Start.AddDays(200), longPosition, new NavalSettings());
    internal static NavalDecision Evaluate(NavalEvaluationRequest r) => new NavalD1Evaluator().Evaluate(r);
    private static NavalSeries Change(NavalSeries s, int index, Func<NavalBar, NavalBar> change)
    {
        var bars = s.Bars.ToArray(); bars[index] = change(bars[index]); return s with { Bars = bars };
    }

    [Fact]
    public void EntryUsesExactlyPrevious40ClosedDays()
    {
        var r = Request();
        r = r with { Instrument = Change(r.Instrument, 158, b => b with { High = 999m }) };
        var d = Evaluate(r);
        Assert.Equal(NavalAction.EntrySignal, d.Action);
        Assert.Equal(110m, d.EntryHigh);
        Assert.Equal(90m, d.ExitLow);
        // The 40th preceding day is inside; day 41 and the signal high are outside.
        r = r with { Instrument = Change(r.Instrument, 159, b => b with { High = 121m }) };
        Assert.Equal(NavalAction.Wait, Evaluate(r).Action);
    }

    [Fact]
    public void EntryStrictEqualityDoesNotSignal()
    {
        var r = Request() with { Instrument = Series(last: 110m) };
        Assert.Equal(NavalAction.Wait, Evaluate(r).Action);
    }

    [Fact]
    public void ExitUsesPrevious30AndIgnoresBtcEntryFilter()
    {
        var r = Request(true) with { Instrument = Series(last: 80m), Btc = null };
        r = r with { Instrument = Change(r.Instrument, 168, b => b with { Low = 1m }) };
        var d = Evaluate(r);
        Assert.Equal(NavalAction.ExitSignal, d.Action);
        Assert.Equal(90m, d.ExitLow);
        Assert.Equal(NavalAction.ExitSignal, Evaluate(r with { Btc = Series("BTC", 199) }).Action);
        Assert.Equal(NavalAction.ExitSignal, Evaluate(r with { Btc = Change(Series("BTC"), 0, b => b with { Volume = -1m }) }).Action);
        r = r with { Instrument = Change(r.Instrument, 169, b => b with { Low = 70m }) };
        Assert.Equal(NavalAction.Hold, Evaluate(r).Action);
    }

    [Fact]
    public void ExitStrictEqualityHoldsWithoutBtc()
    {
        Assert.Equal(NavalAction.Hold, Evaluate(Request(true) with { Instrument = Series(last: 90m), Btc = null }).Action);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("199")]
    [InlineData("equality")]
    [InlineData("stale")]
    [InlineData("wrongBase")]
    [InlineData("wrongQuote")]
    public void BtcFilterRequires200AlignedClosedDays(string fault)
    {
        var r = Request();
        NavalSeries? btc = fault switch
        {
            "missing" => null,
            "199" => Series("BTC") with { Bars = Series("BTC").Bars.Skip(1).ToArray() },
            "equality" => Series("BTC", last: 100m),
            "stale" => Series("BTC", 199),
            "wrongBase" => Series("ETH"),
            "wrongQuote" => Series("BTC") with { QuoteAsset = "USDT" },
            _ => throw new Exception()
        };
        var d = Evaluate(r with { Btc = btc });
        Assert.Equal(NavalAction.Wait, d.Action);
        Assert.Equal(fault switch
        {
            "missing" => "MissingBtcData", "199" => "InsufficientBtcHistory",
            "equality" => "BtcFilterBlocked", "stale" => "StaleBtcData",
            _ => "InvalidBtcData:Metadata"
        }, d.Reason);
    }

    [Fact]
    public void EthEntryUsesBtcRatherThanInstrumentAverage()
    {
        var d = Evaluate(Request());
        Assert.Equal(NavalAction.EntrySignal, d.Action);
        Assert.Equal(100.1m, d.BtcSma);
        Assert.Equal(NavalAction.Wait, Evaluate(Request() with { Btc = Series("BTC", last: 80m) }).Action);
    }

    [Fact]
    public void FutureMutationDoesNotChangeDecisionOrIdentity()
    {
        var r = Request();
        var baseline = Evaluate(r);
        Assert.Equal(NavalAction.EntrySignal, baseline.Action);
        // A future bar's contents (even invalid OHLCV/confirmation) are not known as of cutoff.
        var future = new NavalBar(Start.AddDays(200), -1m, -2m, -3m, -4m, -5m, false);
        r = r with
        {
            Instrument = r.Instrument with { Bars = r.Instrument.Bars.Append(future).ToArray() },
            Btc = r.Btc! with { Bars = r.Btc!.Bars.Append(future with { Close = 999999m }).ToArray() }
        };
        Assert.Equal(baseline, Evaluate(r));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("gap")]
    [InlineData("order")]
    [InlineData("offset")]
    [InlineData("midday")]
    [InlineData("high")]
    [InlineData("low")]
    [InlineData("open")]
    [InlineData("close")]
    [InlineData("volume")]
    [InlineData("unconfirmed")]
    [InlineData("source")]
    [InlineData("basis")]
    public void RejectsBrokenDailyData(string fault)
    {
        var r = Request(); var s = r.Instrument;
        s = fault switch
        {
            "duplicate" => Change(s, 100, b => b with { OpenUtc = Start.AddDays(99) }),
            "gap" => s with { Bars = s.Bars.Where((_, i) => i != 100).ToArray() },
            "order" => s with { Bars = s.Bars.Take(100).Concat(s.Bars.Skip(100).Reverse()).ToArray() },
            "offset" => Change(s, 100, b => b with { OpenUtc = b.OpenUtc.ToOffset(TimeSpan.FromHours(1)) }),
            "midday" => Change(s, 100, b => b with { OpenUtc = b.OpenUtc.AddHours(1) }),
            "high" => Change(s, 100, b => b with { High = 99m }),
            "low" => Change(s, 100, b => b with { Low = 101m }),
            "open" => Change(s, 100, b => b with { Open = 0m }),
            "close" => Change(s, 100, b => b with { Close = -1m }),
            "volume" => Change(s, 100, b => b with { Volume = -1m }),
            "unconfirmed" => Change(s, 100, b => b with { Confirmed = false }),
            "source" => s with { Source = "" },
            "basis" => s with { PriceBasis = "" },
            _ => throw new Exception()
        };
        var d = Evaluate(r with { Instrument = s });
        Assert.Equal(NavalAction.Wait, d.Action);
        Assert.StartsWith("InvalidInstrumentData", d.Reason);
    }

    [Fact]
    public void InstrumentGapBlocksExitEvenWithoutBtc()
    {
        var r = Request(true) with { Instrument = Series(last: 80m), Btc = null };
        Assert.Equal(NavalAction.Wait, Evaluate(r with { Instrument = r.Instrument with { Bars = r.Instrument.Bars.Skip(1).Where((_, i) => i != 100).ToArray() } }).Action);
    }

    [Fact]
    public void NeverCreatesShort()
    {
        var d = Evaluate(Request() with { Instrument = Series(last: 80m) });
        Assert.Equal(NavalAction.Wait, d.Action);
        Assert.Equal(NavalAction.Hold, Evaluate(Request(true)).Action);
    }

    [Fact]
    public void ProfilesContainExactAssetsAndAreImmutable()
    {
        var alpha = NavalFleetProfiles.Get(NavalFleet.Alpha);
        var bravo = NavalFleetProfiles.Get(NavalFleet.Bravo);
        Assert.Equal(new[] { "BTC", "ETH" }, alpha.Assets);
        Assert.Equal(new[] { "BTC", "ETH", "SOL", "XRP", "DOGE", "ADA", "LTC", "LINK", "BCH", "AVAX" }, bravo.Assets);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)alpha.Assets)[0] = "SOL");
        Assert.Equal(2, alpha.DefaultMaxPositions); Assert.Equal(2, bravo.DefaultMaxPositions);
        Assert.Equal(NavalAction.Wait, Evaluate(Request() with { Instrument = Series("SOL") }).Action);
        Assert.Equal(NavalAction.EntrySignal, Evaluate(Request() with { Fleet = NavalFleet.Bravo, Instrument = Series("SOL") }).Action);
    }

    [Theory]
    [InlineData(0, 30, 200)]
    [InlineData(40, -1, 200)]
    [InlineData(40, 30, 0)]
    [InlineData(10001, 30, 200)]
    public void InvalidSettingsFailClosed(int entry, int exit, int btc)
    {
        var d = Evaluate(Request() with { Settings = new NavalSettings(entry, exit, btc) });
        Assert.Equal(NavalAction.Wait, d.Action); Assert.Equal("InvalidSettings", d.Reason);
    }

    [Fact]
    public void EarliestExecutionIsAfterSignalCloseNotAtReferenceFill()
    {
        var r = Request(); var d = Evaluate(r);
        Assert.Equal(r.CutoffUtc.AddTicks(1), d.EarliestExecutionUtc);
        Assert.Equal(NavalAction.Wait, Evaluate(r with { CutoffUtc = r.CutoffUtc.AddTicks(-1) }).Action);
        Assert.Equal(NavalAction.EntrySignal, Evaluate(r with { CutoffUtc = r.CutoffUtc.AddHours(3) }).Action);
        Assert.Equal(NavalAction.Wait, Evaluate(r with { CutoffUtc = r.CutoffUtc.AddDays(1) }).Action);
        Assert.Equal("StaleInstrumentData", Evaluate(r with { CutoffUtc = r.CutoffUtc.AddDays(1) }).Reason);
    }

    [Fact]
    public void StableIdIsNamespacedAndBindsConfigurationFleetAndAsOfPrefix()
    {
        var r = Request(); var d = Evaluate(r);
        Assert.StartsWith("naval-d1-v1:Alpha:ETH-USD:", d.StableId);
        Assert.Matches("^[0-9a-f]{64}$", d.ConfigIdentity);
        Assert.Matches("^[0-9a-f]{64}$", d.AsOfIdentity);
        Assert.Equal(d, Evaluate(r));
        Assert.NotEqual(d.StableId, Evaluate(r with { Fleet = NavalFleet.Bravo }).StableId);
        Assert.NotEqual(d.StableId, Evaluate(r with { HasLong = true }).StableId);
        var differentConfig = Evaluate(r with { Settings = new NavalSettings(39, 30, 200) });
        Assert.NotEqual(d.ConfigIdentity, differentConfig.ConfigIdentity);
        Assert.NotEqual(d.StableId, differentConfig.StableId);
        Assert.NotEqual(d.AsOfIdentity, Evaluate(r with { Instrument = Change(r.Instrument, 20, b => b with { Volume = 11m }) }).AsOfIdentity);
    }

    [Fact]
    public void BtcSmaStrictEqualityUsesExactArithmeticNotOrderDependentOnlineRounding()
    {
        var btc = Series("BTC");
        btc = btc with { Bars = btc.Bars.Select((b, i) =>
        {
            decimal close = i == 199 ? 100m : (i * 37) % 199 + 1;
            return b with { Open = close, High = close + 1, Low = Math.Max(0.5m, close - 1), Close = close };
        }).ToArray() };
        // 1..199 permuted once sums to 19900; plus final 100 gives exact SMA 100.
        var d = Evaluate(Request() with { Btc = btc });
        Assert.Equal(100m, d.BtcSma);
        Assert.Equal(NavalAction.Wait, d.Action);
        Assert.Equal("BtcFilterBlocked", d.Reason);
    }

    [Fact]
    public void InvalidCutoffFailsClosed()
    {
        Assert.Equal("InvalidCutoff", Evaluate(Request() with { CutoffUtc = Start.ToOffset(TimeSpan.FromHours(1)) }).Reason);
    }
}
