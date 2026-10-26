using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;

namespace Apex.Platforms.CTrader
{
    // Platform boundary policy, independent of the cTrader runtime for executable tests.
    internal static class PlatformSafety
    {
        // cTrader LocalStorage allows <=50 alphanumeric/inner-space characters, not dots or label punctuation.
        internal static string StorageKey(string scope, string broker, long account, string label)
        {
            var identity = JsonSerializer.Serialize(new[] { scope, broker, account.ToString(CultureInfo.InvariantCulture), label });
            return "APEX " + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).Substring(0, 40);
        }

        internal static bool Fresh(DateTime marketTime, DateTime now, double maximumAgeSeconds) => marketTime != default && (now - marketTime).TotalSeconds <= maximumAgeSeconds && (marketTime - now).TotalSeconds <= 5;

        internal static bool RepairOrClose(Func<bool> repair, Action close)
        {
            try { if (repair()) return true; }
            catch (Exception) { } // A known unprotected position still needs a close attempt.
            close();
            return false;
        }

        // Legacy parameter is retained only so saved cBot settings remain loadable.
        internal static bool DemoAllowed(bool isLive, bool allowLive) => !isLive;
        internal static bool OwnLabel(string? label, string expected) => !string.IsNullOrWhiteSpace(expected) && string.Equals(label, expected, StringComparison.Ordinal);
        internal static bool Positive(double value) => double.IsFinite(value) && value > 0;
        internal static bool Protection(double sl, double? tp) => Positive(sl) && tp.HasValue && Positive(tp.Value);
        internal static bool Volume(double units, double min, double max) => Positive(units) && Positive(min) && double.IsFinite(max) && units >= min && units <= max;
        internal static bool PartialClose(double? units, double current, double min, double step)
        {
            if (!units.HasValue) return Positive(current);
            double v = units.Value;
            if (!Volume(v, min, current) || !Positive(step)) return false;
            if (v == current) return true;
            double aligned = Math.Floor(v / step + 1e-9) * step;
            return Math.Abs(aligned - v) < step * 1e-8 && current - v >= min;
        }
        internal static bool SignalMatches(string? comment, string? signal)
        {
            var parts = comment?.Split('|');
            return parts != null && parts.Length >= 3 && parts[0] == "APEX" && Apex.Core.Model.StrategyIdExtensions.TryParseCode(parts[1], out _)
                && !string.IsNullOrEmpty(signal) && parts[2] == signal;
        }
        // Same exact operand/quantum rule as Core; Bridge links protocol/model, not Engine.
        private static decimal ExactUnits(double units, double step)
        {
            if (!double.IsFinite(units) || units < 0 || !Positive(step)) throw new InvalidOperationException("invalid quantum");
            decimal q = (decimal)step, u = (decimal)units;
            if (q <= 0 || (double)q != step || (double)u != units || Math.BitIncrement(units) - units >= step || u % q != 0)
                throw new InvalidOperationException("unrepresentable quantum");
            return u;
        }
        internal static Apex.Core.Model.ReductionFillEvidence? ReductionWitness(bool successful, string action, long position,
            string signal, string label, string symbol, double before, double units, double after, double step)
        {
            if (!successful || string.IsNullOrWhiteSpace(action) || string.IsNullOrWhiteSpace(signal)) return null;
            try
            {
                decimal b = ExactUnits(before, step), c = ExactUnits(units, step),
                    a = ExactUnits(after, step);
                return c > 0 && a > 0 && b == c + a ? new Apex.Core.Model.ReductionFillEvidence { ActionId = action, PositionId = position,
                    SignalId = signal, Label = label, Symbol = symbol, BeforeUnits = before, ClosedUnits = units, RemainingUnits = after } : null;
            }
            catch (Exception) { return null; }
        }
        internal static double MinimumPips(double distance, bool percentage, double price, double pip) => percentage ? price * distance / 100.0 / pip : distance;
        internal static double PipValue(double pipSize, double quoteToAccount) => Positive(pipSize) && Positive(quoteToAccount) ? pipSize * quoteToAccount : double.NaN;
    }
}
