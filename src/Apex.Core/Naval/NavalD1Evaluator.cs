using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Apex.Core.Naval
{
    /// <summary>Pure, long-only UTC D1 signal research. No execution or financial accounting.</summary>
    public sealed class NavalD1Evaluator
    {
        public const string RuleVersion = "naval-d1-v1";

        public NavalDecision Evaluate(NavalEvaluationRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var settings = request.Settings ?? new NavalSettings();
            var instrument = Prefix(request.Instrument, request.CutoffUtc);
            var btc = Prefix(request.Btc, request.CutoffUtc);
            var config = Hash(Tokens(RuleVersion, settings.EntryDays, settings.ExitDays, settings.BtcSmaDays));
            var asOf = Hash(Tokens(RuleVersion, request.Fleet, request.CutoffUtc, request.HasLong,
                SeriesIdentity(request.Instrument, instrument), SeriesIdentity(request.Btc, btc)));
            decimal? high = null, low = null, sma = null;
            DateTimeOffset? earliest = null;
            NavalDecision Result(NavalAction action, string reason)
            {
                var id = RuleVersion + ":" + request.Fleet + ":" + Uri.EscapeDataString(request.Instrument?.Instrument ?? "unknown")
                    + ":" + Hash(Tokens(config, asOf, action, reason));
                return new NavalDecision(request.Fleet, request.Instrument?.Instrument ?? "unknown", action, reason,
                    request.CutoffUtc, earliest, high, low, sma, config, asOf, id);
            }

            if (request.CutoffUtc.Offset != TimeSpan.Zero || request.CutoffUtc <= DateTimeOffset.MinValue.AddDays(1)
                || request.CutoffUtc >= DateTimeOffset.MaxValue.AddDays(-1))
                return Result(NavalAction.Wait, "InvalidCutoff");
            if (!ValidDays(settings.EntryDays) || !ValidDays(settings.ExitDays) || !ValidDays(settings.BtcSmaDays))
                return Result(NavalAction.Wait, "InvalidSettings");
            if (!Enum.IsDefined(typeof(NavalFleet), request.Fleet)) return Result(NavalAction.Wait, "InvalidFleet");
            var invalid = Validate(request.Instrument, instrument);
            if (invalid != null) return Result(NavalAction.Wait, "InvalidInstrumentData:" + invalid);
            if (!NavalFleetProfiles.Get(request.Fleet).Assets.Contains(request.Instrument.BaseAsset, StringComparer.Ordinal))
                return Result(NavalAction.Wait, "AssetNotInFleet");
            if (instrument.Length == 0) return Result(NavalAction.Wait, "MissingInstrumentData");
            var signal = instrument[instrument.Length - 1];
            var signalClose = signal.OpenUtc.AddDays(1);
            if (signalClose != new DateTimeOffset(request.CutoffUtc.UtcDateTime.Date, TimeSpan.Zero))
                return Result(NavalAction.Wait, "StaleInstrumentData");

            if (instrument.Length > settings.EntryDays)
                high = instrument.Skip(instrument.Length - settings.EntryDays - 1).Take(settings.EntryDays).Max(b => b.High);
            if (instrument.Length > settings.ExitDays)
                low = instrument.Skip(instrument.Length - settings.ExitDays - 1).Take(settings.ExitDays).Min(b => b.Low);
            // Exit never depends on the entry-only BTC filter, its availability or validity.
            if (request.HasLong)
            {
                if (!low.HasValue) return Result(NavalAction.Wait, "InsufficientExitHistory");
                if (signal.Close < low.Value)
                {
                    earliest = signalClose.AddTicks(1);
                    return Result(NavalAction.ExitSignal, "CloseBelowPreviousLow");
                }
                return Result(NavalAction.Hold, "LongWithoutExit");
            }
            if (!high.HasValue) return Result(NavalAction.Wait, "InsufficientEntryHistory");
            if (request.Btc == null) return Result(NavalAction.Wait, "MissingBtcData");
            invalid = Validate(request.Btc, btc);
            if (invalid != null) return Result(NavalAction.Wait, "InvalidBtcData:" + invalid);
            if (request.Btc.BaseAsset != "BTC" || request.Btc.QuoteAsset != request.Instrument.QuoteAsset)
                return Result(NavalAction.Wait, "InvalidBtcData:Metadata");
            if (btc.Length == 0) return Result(NavalAction.Wait, "MissingBtcData");
            if (btc[btc.Length - 1].OpenUtc != signal.OpenUtc) return Result(NavalAction.Wait, "StaleBtcData");
            if (btc.Length < settings.BtcSmaDays) return Result(NavalAction.Wait, "InsufficientBtcHistory");
            // Sum first, divide once: online averaging makes strict equality order-dependent.
            // Unrepresentable decimal aggregates fail closed rather than changing precision silently.
            try
            {
                sma = btc.Skip(btc.Length - settings.BtcSmaDays).Sum(b => b.Close) / settings.BtcSmaDays;
            }
            catch (OverflowException)
            {
                return Result(NavalAction.Wait, "InvalidBtcData:NumericRange");
            }
            if (btc[btc.Length - 1].Close <= sma.Value) return Result(NavalAction.Wait, "BtcFilterBlocked");
            if (signal.Close <= high.Value) return Result(NavalAction.Wait, "NoBreakout");
            earliest = signalClose.AddTicks(1);
            return Result(NavalAction.EntrySignal, "CloseAbovePreviousHigh");
        }

        private static bool ValidDays(int days) => days > 0 && days <= 10000;

        private static NavalBar[] Prefix(NavalSeries? series, DateTimeOffset cutoff)
        {
            if (series?.Bars == null || cutoff < DateTimeOffset.MinValue.AddDays(1)) return Array.Empty<NavalBar>();
            var lastOpen = cutoff.AddDays(-1);
            // Do not sort or repair input: wrong order/gaps in the used prefix must be refused.
            return series.Bars.Where(b => b == null || b.OpenUtc <= lastOpen).ToArray();
        }

        private static string? Validate(NavalSeries? series, NavalBar[] bars)
        {
            if (series == null || !Text(series.Instrument) || !Text(series.BaseAsset) || !Text(series.QuoteAsset)
                || !Text(series.Source) || !Text(series.PriceBasis) || series.Bars == null) return "Metadata";
            NavalBar? previous = null;
            foreach (var bar in bars)
            {
                if (bar == null) return "NullBar";
                if (bar.OpenUtc.Offset != TimeSpan.Zero || bar.OpenUtc.TimeOfDay != TimeSpan.Zero) return "UtcDaily";
                if (previous != null && bar.OpenUtc - previous.OpenUtc != TimeSpan.FromDays(1)) return "Continuity";
                if (!bar.Confirmed) return "Unconfirmed";
                if (bar.Open <= 0 || bar.Close <= 0 || bar.Low <= 0 || bar.High <= 0 || bar.Volume < 0
                    || bar.Low > bar.Open || bar.Low > bar.Close || bar.High < bar.Open || bar.High < bar.Close
                    || bar.High < bar.Low) return "Ohlcv";
                previous = bar;
            }
            return null;
        }

        private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value)
            && value.Length <= 256 && !value.Any(char.IsControl);

        // Length-prefixed invariant tokens remove delimiter ambiguity and decimal scale/culture differences.
        private static string Tokens(params object?[] values)
        {
            var text = new StringBuilder();
            foreach (var value in values)
            {
                var token = value switch
                {
                    null => "<null>",
                    decimal d => d.ToString("G29", CultureInfo.InvariantCulture),
                    DateTimeOffset t => t.ToString("O", CultureInfo.InvariantCulture),
                    IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                    _ => value.ToString() ?? ""
                };
                text.Append(token.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(token);
            }
            return text.ToString();
        }

        private static string SeriesIdentity(NavalSeries? series, NavalBar[] prefix)
        {
            if (series == null) return Tokens((object?)null);
            var text = new StringBuilder(Tokens(series.Instrument, series.BaseAsset, series.QuoteAsset, series.Source, series.PriceBasis));
            foreach (var bar in prefix)
                text.Append(bar == null ? Tokens((object?)null) : Tokens(bar.OpenUtc, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume, bar.Confirmed));
            return Hash(text.ToString());
        }

        private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}
