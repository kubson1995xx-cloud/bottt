using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Apex.AiLab.Lab;

namespace Apex.AiLab.Pipeline
{
    /// <summary>
    /// Audyt zmian zaproponowanych przez AI (deterministyczny, niezależny od tego, co agent twierdzi).
    /// Blocking = zmiana nie może zostać wdrożona; Protected = wymaga dodatkowego, wpisanego potwierdzenia użytkownika.
    /// </summary>
    public static class ChangeAudit
    {
        /// <summary>Pliki strategii aktywnych w profilu (status inny niż Rejected) – ich zmiana wymaga zatwierdzenia jak moduły chronione.</summary>
        public static readonly Dictionary<string, string> StrategyFiles = new(StringComparer.OrdinalIgnoreCase)
        {
            ["TrendPullback"] = "src/Apex.Core/Strategies/TrendPullbackStrategy.cs",
            ["DonchianBreakout"] = "src/Apex.Core/Strategies/DonchianBreakoutStrategy.cs",
            ["DailyTrend"] = "src/Apex.Core/Strategies/DonchianBreakoutStrategy.cs",
            ["MeanReversion"] = "src/Apex.Core/Strategies/MeanReversionStrategy.cs",
            ["MomentumImpulse"] = "src/Apex.Core/Strategies/MomentumImpulseStrategy.cs",
            ["IndexDipBuy"] = "src/Apex.Core/Strategies/IndexDipBuyStrategy.cs",
        };

        private static readonly (Regex re, string rule, string detail)[] BlockingCodePatterns =
        {
            (new Regex(@"(?i)martingale"), "Zakaz martingale", "kod odwołuje się do martingale"),
            (new Regex(@"(?i)averag(e|ing)[ _-]?down|u[sś]rednian\w*\s+(w\s+d[oó][lł]|pozycji)"), "Zakaz uśredniania", "uśrednianie stratnej pozycji"),
            (new Regex(@"(?i)\bgrid\b\s*(trad|order|level|step|strateg)"), "Zakaz gridu", "logika grid tradingu"),
            (new Regex(@"(?i)(volume|lot|units|wolumen)\w*\s*\*=\s*2|(double|podw)\w*\s*(the\s*)?(volume|lot|wolumen)"), "Zakaz zwiększania pozycji po stracie", "podwajanie wolumenu"),
            (new Regex(@"AccessRights\s*\.\s*FullAccess"), "Zakaz pełnego dostępu cBota", "AccessRights.FullAccess"),
            (new Regex(@"\b(LiveTradingFeature|AllowLive|LiveConfirmation|RequiredLivePhrase)\b|AccountGuard\s*\.\s*Evaluate"), "Konto REAL pozostaje zablokowane", "zmiana logiki dopuszczenia konta LIVE"),
            (new Regex(@"AssetClass\s*\.\s*Crypto"), "Kryptowaluty wykluczone", "nowe odwołanie do klasy Crypto"),
        };

        private static readonly Regex SkipTest = new(@"\[\s*(Fact|Theory)\s*\(.*Skip\s*=", RegexOptions.Compiled);
        private static readonly Regex CoreNetwork = new(@"\b(HttpClient|WebClient|WebRequest|TcpClient|Socket|Process\s*\.\s*Start)\b", RegexOptions.Compiled);

        public static List<AuditFinding> Audit(IReadOnlyList<ChangedFile> files, string diff, PolicySettings policy, IEnumerable<string> activeStrategies)
        {
            var findings = new List<AuditFinding>();
            var protectedPaths = policy.ProtectedPaths.Select(Norm).ToList();
            foreach (var s in activeStrategies)
                if (StrategyFiles.TryGetValue(s, out var f)) protectedPaths.Add(f);
            var forbidden = policy.ForbiddenPaths.Select(Norm).ToList();

            foreach (var f in files)
            {
                string p = Norm(f.Path);
                if (forbidden.Any(x => p.StartsWith(x, StringComparison.OrdinalIgnoreCase)))
                    findings.Add(new AuditFinding { Severity = FindingSeverity.Blocking, Rule = "Ścieżka zabroniona", File = p, Detail = "AI nie może zmieniać tego obszaru (m.in. własnych zabezpieczeń AI Lab)." });
                if (protectedPaths.Any(x => x.EndsWith("/", StringComparison.Ordinal) ? p.StartsWith(x, StringComparison.OrdinalIgnoreCase) : string.Equals(p, x, StringComparison.OrdinalIgnoreCase)))
                {
                    f.Protected = true;
                    findings.Add(new AuditFinding { Severity = FindingSeverity.Protected, Rule = "Moduł chroniony", File = p, Detail = "RiskEngine / limity / bezpieczeństwo / wykonanie / aktywne strategie – wdrożenie wymaga osobnego potwierdzenia." });
                }
                if (f.Status == "D" && p.StartsWith("tests/", StringComparison.OrdinalIgnoreCase))
                    findings.Add(new AuditFinding { Severity = FindingSeverity.Blocking, Rule = "Usunięcie testu", File = p, Detail = "Nie wolno usuwać testów." });
                if (f.Status == "D" && !p.StartsWith("tests/", StringComparison.OrdinalIgnoreCase))
                    findings.Add(new AuditFinding { Severity = FindingSeverity.Warning, Rule = "Usunięcie pliku", File = p, Detail = "Usunięcie pliku wymaga uważnego przeglądu." });
            }

            foreach (var (file, line, added) in ChangedLines(diff))
            {
                string p = Norm(file);
                bool isTest = p.StartsWith("tests/", StringComparison.OrdinalIgnoreCase);
                bool isDoc = p.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || p.StartsWith("docs/", StringComparison.OrdinalIgnoreCase) || p.StartsWith("reports/", StringComparison.OrdinalIgnoreCase);
                bool isCode = p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".mq5", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".js", StringComparison.OrdinalIgnoreCase);
                if (isTest && added && SkipTest.IsMatch(line))
                    findings.Add(new AuditFinding { Severity = FindingSeverity.Blocking, Rule = "Wyłączenie testu", File = p, Detail = "Dodano Skip do testu: " + Short(line) });
                if (isDoc || isTest || !isCode) continue;
                if (IsComment(line)) continue;
                foreach (var (re, rule, detail) in BlockingCodePatterns)
                {
                    if (!re.IsMatch(line)) continue;
                    // Usunięcie linii z odwołaniem do LIVE/krypto też jest blokowane (osłabienie zabezpieczeń); dodanie – również.
                    findings.Add(new AuditFinding { Severity = FindingSeverity.Blocking, Rule = rule, File = p, Detail = (added ? "+ " : "− ") + detail + ": " + Short(line) });
                }
                if (added && p.StartsWith("src/Apex.Core/", StringComparison.OrdinalIgnoreCase) && CoreNetwork.IsMatch(line))
                    findings.Add(new AuditFinding { Severity = FindingSeverity.Blocking, Rule = "Silnik bez sieci i procesów", File = p, Detail = "Nowe połączenie sieciowe lub proces w silniku: " + Short(line) });
            }

            int total = files.Sum(f => f.Added + f.Removed);
            if (total > 2000)
                findings.Add(new AuditFinding { Severity = FindingSeverity.Warning, Rule = "Duża zmiana", Detail = $"{total} zmienionych linii – trudna do przeglądu; rozważ odrzucenie i mniejszy zakres." });
            if (files.Count > 0 && !files.Any(f => Norm(f.Path).StartsWith("tests/", StringComparison.OrdinalIgnoreCase)) && files.Any(f => Norm(f.Path).StartsWith("src/", StringComparison.OrdinalIgnoreCase) && f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
                findings.Add(new AuditFinding { Severity = FindingSeverity.Warning, Rule = "Brak nowych testów", Detail = "Zmieniono kod bez dodania lub zmiany testów." });

            // Duplikaty (ta sama reguła i plik) scalamy.
            return findings.GroupBy(x => (x.Severity, x.Rule, x.File)).Select(g => g.First()).OrderByDescending(x => x.Severity).ToList();
        }

        /// <summary>Linie zmienione w diffie: (plik, treść bez znaku +/-, czy dodana).</summary>
        public static IEnumerable<(string file, string line, bool added)> ChangedLines(string diff)
        {
            string file = "";
            foreach (var raw in diff.Replace("\r", "").Split('\n'))
            {
                if (raw.StartsWith("+++ ", StringComparison.Ordinal))
                {
                    string f = raw.Substring(4).Trim();
                    file = f.StartsWith("b/", StringComparison.Ordinal) ? f.Substring(2) : f;
                    continue;
                }
                if (raw.StartsWith("--- ", StringComparison.Ordinal))
                {
                    string f = raw.Substring(4).Trim();
                    if (f != "/dev/null") file = f.StartsWith("a/", StringComparison.Ordinal) ? f.Substring(2) : f;
                    continue;
                }
                if (raw.StartsWith("diff --git", StringComparison.Ordinal) || raw.StartsWith("@@", StringComparison.Ordinal)) continue;
                if (raw.StartsWith("+", StringComparison.Ordinal)) yield return (file, raw.Substring(1), true);
                else if (raw.StartsWith("-", StringComparison.Ordinal)) yield return (file, raw.Substring(1), false);
            }
        }

        private static bool IsComment(string line)
        {
            string t = line.TrimStart();
            return t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("///", StringComparison.Ordinal) || t.StartsWith("*", StringComparison.Ordinal) || t.StartsWith("/*", StringComparison.Ordinal);
        }

        private static string Norm(string p) => p.Replace('\\', '/').TrimStart('/');

        private static string Short(string s)
        {
            s = s.Trim();
            return s.Length > 160 ? s.Substring(0, 160) + "…" : s;
        }
    }
}
