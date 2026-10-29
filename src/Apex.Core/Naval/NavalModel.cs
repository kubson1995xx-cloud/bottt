using System;
using System.Collections.Generic;

namespace Apex.Core.Naval
{
    public enum NavalFleet { Alpha, Bravo }
    public enum NavalAction { Wait, Hold, EntrySignal, ExitSignal }

    public sealed record NavalBar(DateTimeOffset OpenUtc, decimal Open, decimal High, decimal Low,
        decimal Close, decimal Volume, bool Confirmed);
    public sealed record NavalSeries(string Instrument, string BaseAsset, string QuoteAsset,
        string Source, string PriceBasis, IReadOnlyList<NavalBar> Bars);
    public sealed record NavalSettings(int EntryDays = 40, int ExitDays = 30, int BtcSmaDays = 200);
    public sealed record NavalEvaluationRequest(NavalFleet Fleet, NavalSeries Instrument, NavalSeries? Btc,
        DateTimeOffset CutoffUtc, bool HasLong, NavalSettings? Settings = null);
    public sealed record NavalDecision(NavalFleet Fleet, string Instrument, NavalAction Action, string Reason,
        DateTimeOffset CutoffUtc, DateTimeOffset? EarliestExecutionUtc, decimal? EntryHigh, decimal? ExitLow,
        decimal? BtcSma, string ConfigIdentity, string AsOfIdentity, string StableId);
    public sealed record NavalFleetProfile(NavalFleet Fleet, IReadOnlyList<string> Assets,
        int DefaultMaxPositions, NavalSettings Settings, int ResearchAtrDays, decimal ResearchStopAtr,
        decimal ResearchTakeProfitR);
}
