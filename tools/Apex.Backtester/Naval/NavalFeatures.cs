using System;
using System.Collections.Generic;
using System.Linq;
using Apex.Core.Naval;

namespace Apex.Backtester.Naval;

// Wilder ATR14, od najstarszego dostępnego baru. Każdy element używa tylko prefiksu.
internal sealed class NavalFeatures
{
    readonly NavalSeries series;
    readonly Dictionary<DateTimeOffset, int> indices;
    readonly decimal?[] atr;
    public NavalFeatures(NavalSeries series)
    {
        this.series = series;
        indices = series.Bars.Select((b, i) => (b.OpenUtc, i)).ToDictionary(x => x.OpenUtc, x => x.i);
        atr = new decimal?[series.Bars.Count]; decimal seed = 0, previous = 0;
        for (int i = 1; i < series.Bars.Count; i++)
        {
            var b = series.Bars[i]; var last = series.Bars[i - 1];
            var tr = Math.Max(b.High - b.Low, Math.Max(Math.Abs(b.High - last.Close), Math.Abs(b.Low - last.Close)));
            if (i <= 14) seed += tr;
            if (i == 14) previous = seed / 14;
            else if (i > 14) previous = (previous * 13 + tr) / 14;
            if (i >= 14) atr[i] = previous;
        }
    }
    public NavalBar At(DateTimeOffset day) => series.Bars[indices[day]];
    public decimal? Atr(DateTimeOffset day) => atr[indices[day]];
    public decimal? Turnover(DateTimeOffset day)
    {
        int i = indices[day];
        return i < 19 ? null : series.Bars.Skip(i - 19).Take(20).Sum(b => b.Close * b.Volume) / 20;
    }
    public NavalSeries Prefix(DateTimeOffset cutoff, int count) => Prefix(series, cutoff, count);
    public static NavalSeries Prefix(NavalSeries series, DateTimeOffset cutoff, int count)
        => series with { Bars = series.Bars.Where(b => b.OpenUtc < cutoff).TakeLast(count).ToArray() };
}
