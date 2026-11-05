using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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

        // Katalogi chronione w całości, z wbudowanymi (niekonfigurowalnymi) wyjątkami. Bardziej szczegółowy wpis listy chronionej wygrywa z wyjątkiem.
        private const string CoreRoot = "src/Apex.Core/";
        private const string StrategiesDir = "src/Apex.Core/Strategies/";
        private const string StrategiesNamespace = "Apex.Core.Strategies";
        private const string DesktopRoot = "src/Apex.Desktop/";
        private const string DesktopWebRoot = "src/Apex.Desktop/wwwroot/";

        /// <summary>
        /// Ścieżka decyzji o dopuszczeniu konta LIVE/REAL: konfiguracja i strażnik, mapowanie IsLive z mostów, migawka konta,
        /// parametry mostów (AllowLive, ACCOUNT_TRADE_MODE), odblokowanie LIVE w panelu (polecenie i fraza). Dodanie LUB usunięcie
        /// linii z takim odwołaniem blokuje zmianę bezwarunkowo – także w JS panelu i w literałach napisów (klucze JSON, nazwy poleceń).
        /// </summary>
        private static readonly Regex LivePattern = new(
            @"(?i)\b(?:LiveTradingFeature|liveFeature|InpAllowLive|AllowLive|LiveConfirmation|RequiredLivePhrase|AllowedAccounts|LiveBlocked|unlockLive|lockLive|GuardIsLive|IsLive|EvaluateGuard|AccountSnapshot|AccountDto|ToSnapshot|ACCOUNT_TRADE_MODE\w*|AccountGuard\w*)\b|AKCEPTUJE\s+RYZYKO",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex CryptoPattern = new(
            @"AssetClass\s*\.\s*Crypto\b|\bIsCrypto\b|\bCrypto(?:Tickers|Keywords|WholeWords)\b|(?i:[""'`]crypto[""'`])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>Reguły sprawdzane na kodzie RAZEM z literałami napisów (komentarze usunięte).</summary>
        private static readonly (Regex re, string rule, string detail)[] WithStringPatterns =
        {
            (LivePattern, "Konto REAL pozostaje zablokowane", "zmiana logiki dopuszczenia konta LIVE"),
            (CryptoPattern, "Kryptowaluty wykluczone", "odwołanie do klasy lub klasyfikacji Crypto"),
        };

        /// <summary>Reguły sprawdzane na samym kodzie (bez komentarzy i bez treści zwykłych literałów napisów).</summary>
        private static readonly (Regex re, string rule, string detail)[] BlockingCodePatterns =
        {
            (new Regex(@"(?i)martingale"), "Zakaz martingale", "kod odwołuje się do martingale"),
            (new Regex(@"(?i)averag(e|ing)[ _-]?down|u[sś]rednian\w*\s+(w\s+d[oó][lł]|pozycji)"), "Zakaz uśredniania", "uśrednianie stratnej pozycji"),
            (new Regex(@"(?i)\bgrid\b\s*(trad|order|level|step|strateg)"), "Zakaz gridu", "logika grid tradingu"),
            (new Regex(@"(?i)(volume|lot|units|wolumen)\w*\s*\*=\s*2|(double|podw)\w*\s*(the\s*)?(volume|lot|wolumen)"), "Zakaz zwiększania pozycji po stracie", "podwajanie wolumenu"),
            (new Regex(@"AccessRights\s*\.\s*FullAccess"), "Zakaz pełnego dostępu cBota", "AccessRights.FullAccess"),
        };

        // Także ModuleInitializerAttribute (np. przez alias using) – bez \b na końcu.
        private static readonly Regex ModuleInitializer = new(@"\bModuleInitializer", RegexOptions.Compiled);

        /// <summary>
        /// Refleksja, kod niezarządzany i dyrektywy globalne w src/ – pozwalają zmienić zachowanie modułów chronionych lub ścieżki LIVE
        /// bez dotykania ich plików (np. zapis prywatnego pola strażnika, global using podmieniający typ w całym projekcie).
        /// </summary>
        private static readonly Regex SrcEscapePattern = new(
            @"\bBindingFlags\b|\bSystem\s*\.\s*Reflection\b|\bSetValue\s*\(|\bGetValue\s*\(|\bInvokeMember\b|\bActivator\s*\.|\bType\s*\.\s*GetType\s*\(|" +
            @"(?:\btypeof\s*\([^)]*\)|\.GetType\s*\(\s*\))\s*\.\s*Get[A-Z]\w*\s*\(|\b(?:MethodInfo|PropertyInfo|FieldInfo|MemberInfo|ConstructorInfo|TypeInfo)\b|" +
            @"\b(?:SetMethod|GetMethod|GetSetMethod|GetGetMethod|CreateDelegate|MakeGenericMethod|GetRuntime\w+|Declared(?:Members|Fields|Properties|Methods))\b|\.Invoke\s*\([^)]*\bnew\s+object\b|" +
            @"\bAssembly\s*\.\s*(?:Load\w*|GetExecutingAssembly|GetCallingAssembly|GetEntryAssembly)\b|\bunsafe\b|\bstackalloc\b|" +
            @"\b(?:DllImport|LibraryImport|UnmanagedCallersOnly|UnsafeAccessor)\b|\bMarshal\s*\.|\bUnsafe\s*\.|\bRuntimeHelpers\b|\bdelegate\s*\*|" +
            @"\bDynamicMethod\b|\bILGenerator\b|\bExpression\s*\.\s*(?:Field|Property|PropertyOrField|Assign|Lambda|Call)\b|" +
            @"\bEnvironment\s*\.\s*(?:Exit|FailFast|SetEnvironmentVariable)\b|\bAppDomain\b|\bglobal\s+using\b|\bextern\s+alias\b|\[\s*(?:assembly|module)\s*:",
            RegexOptions.Compiled);

        private static readonly Regex CoreNetwork = new(@"\b(HttpClient|WebClient|WebRequest|TcpClient|Socket|Process\s*\.\s*Start)\b", RegexOptions.Compiled);

        /// <summary>Wyłączenie testu: argument Skip w atrybucie (także w kolejnej linii po [Fact(/[Theory().</summary>
        private static readonly Regex SkipArgument = new(@"\bSkip\s*=", RegexOptions.Compiled);
        private static readonly Regex TestAttribute = new(@"\[\s*(?:Xunit\s*\.\s*)?(?:Fact|Theory)(?:Attribute)?\b", RegexOptions.Compiled);

        /// <summary>
        /// Deklaracje i konstrukcje, które z pliku strategii nieaktywnej (wyjątek od ochrony) mogłyby wpłynąć na INNE typy:
        /// nowe typy (przesłanianie nazw w przestrzeni Apex.Core.Strategies), przestrzenie nazw, partial, metody rozszerzające,
        /// atrybuty zestawu, dyrektywy preprocesora.
        /// </summary>
        private static readonly Regex ScopeEscape = new(
            @"\b(?:class|struct|interface|enum|record)\s+@?[A-Za-z_]\w*|\bdelegate\s+@?[A-Za-z_]|\bnamespace\b|\bglobal\s+using\b|\bextern\s+alias\b|\bpartial\b|" +
            @"\[\s*(?:assembly|module)\s*:|\(\s*this\s+@?[A-Za-z_]|^\s*#\s*(?:define|undef|if|elif|else|endif|line)\b|\bModuleInitializer",
            RegexOptions.Compiled);

        private static readonly Regex StrategyIdRef = new(@"\bStrategyId\s*\.\s*(\w+)", RegexOptions.Compiled);

        /// <summary>Znaki sterujące i kierunkowe (Trojan Source), nietypowe końce linii, BOM w środku pliku.</summary>
        private static readonly Regex ControlChars = new(@"[\u0000-\u0008\u000B\u000D\u000E-\u001F\u007F\u0085\u2028\u2029\u202A-\u202E\u2066-\u2069\uFEFF]", RegexOptions.Compiled);

        public static List<AuditFinding> Audit(IReadOnlyList<ChangedFile> files, string diff, PolicySettings policy, IEnumerable<string> activeStrategies, string? worktree = null)
        {
            var findings = new List<AuditFinding>();
            var protectedPaths = policy.ProtectedPaths.Select(Norm).Where(x => x.Length > 0).ToList();
            var activeFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in activeStrategies)
                if (StrategyFiles.TryGetValue(s, out var f)) { protectedPaths.Add(f); activeFiles.Add(f); }
            var forbidden = policy.ForbiddenPaths.Select(Norm).Where(x => x.Length > 0).ToList();

            // 1. Treść zmian (diff): reguły na liniach dodanych i usuniętych, z uwzględnieniem komentarzy i literałów.
            var diffs = DiffParser.Parse(diff ?? "");
            var addedCode = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var fd in diffs)
            {
                string p = Norm(fd.Path);
                if (fd.Binary) continue; // zmiana binarna – blokowana niżej, treść bez znaczenia
                AuditContent(fd, p, findings, addedCode, worktree);
            }

            // Plik w diffie, którego nie ma na liście zmian, ominąłby reguły ścieżek – niespójność = blokada.
            var listed = new HashSet<string>(files.Select(f => Norm(f.Path)), StringComparer.Ordinal);
            foreach (var fd in diffs)
                if (!listed.Contains(Norm(fd.Path)))
                    findings.Add(Block("Niespójna lista zmian", Norm(fd.Path), "Plik występuje w diffie, ale nie na liście zmienionych plików – audyt nie jest wiarygodny."));

            var diffByPath = new Dictionary<string, DiffParser.FileDiff>(StringComparer.Ordinal);
            foreach (var fd in diffs) diffByPath[Norm(fd.Path)] = fd;

            // 2. Ścieżki, rodzaj i kodowanie plików.
            foreach (var f in files)
            {
                string p = Norm(f.Path);
                diffByPath.TryGetValue(p, out var fd);
                string status = string.IsNullOrEmpty(f.Status) ? "M" : f.Status.Substring(0, 1).ToUpperInvariant();

                string? nameProblem = PathProblem(f.Path);
                if (nameProblem != null)
                    findings.Add(Block("Nieprawidłowa nazwa pliku", p, nameProblem));
                if (forbidden.Any(x => p.StartsWith(x, StringComparison.OrdinalIgnoreCase)))
                    findings.Add(Block("Ścieżka zabroniona", p, "AI nie może zmieniać tego obszaru (m.in. własnych zabezpieczeń AI Lab, backtestera, strażnika konta LIVE)."));
                if (IsBuildOrScriptFile(p))
                    findings.Add(Block("Konfiguracja budowania i skrypty", p, "AI nie może zmieniać konfiguracji budowania/skryptów (projekty, MSBuild, NuGet, skrypty, atrybuty git, ustawienia edytorów)."));
                if (status == "I")
                    findings.Add(Block("Plik ukryty przed audytem", p, "Plik pominięty przez .gitignore, a wpływający na kompilację (np. *.user, obj/*.props) – nie trafia do diffu, więc nie może być sprawdzony."));
                if (f.Binary || (fd?.Binary ?? false))
                    findings.Add(Block("Zmiana binarna", p, "Zmian binarnych nie da się sprawdzić regułami audytu (np. plik UTF-16 lub atrybuty git ukrywające treść)."));
                string mode = f.Mode.Length > 0 ? f.Mode : fd?.NewMode ?? "";
                if (mode is "120000" or "160000")
                    findings.Add(Block("Dowiązanie lub podmoduł", p, "Dowiązania symboliczne i podmoduły git są niedozwolone – ich treść omija audyt."));
                if (worktree != null && status != "D" && status != "I")
                {
                    string? enc = EncodingProblem(worktree, p);
                    if (enc != null) findings.Add(Block("Nieprawidłowe kodowanie pliku", p, enc + " – dozwolony wyłącznie UTF-8 (bez znaków NUL)."));
                }

                // Ochrona (fraza ZATWIERDZAM ZMIANY CHRONIONE).
                string? protectedDetail = ProtectedReason(p, status, protectedPaths, activeFiles, fd, addedCode);
                if (protectedDetail != null)
                {
                    f.Protected = true;
                    bool isTestEdit = protectedDetail == TestEditDetail;
                    findings.Add(new AuditFinding
                    {
                        Severity = FindingSeverity.Protected,
                        Rule = isTestEdit ? "Zmiana istniejących testów" : "Moduł chroniony",
                        File = p,
                        Detail = protectedDetail,
                    });
                }
                if (status == "D" && p.StartsWith("tests/", StringComparison.OrdinalIgnoreCase))
                    findings.Add(Block("Usunięcie testu", p, "Nie wolno usuwać testów (także przez przeniesienie pliku poza tests/)."));
                if (status == "D" && !p.StartsWith("tests/", StringComparison.OrdinalIgnoreCase))
                    findings.Add(new AuditFinding { Severity = FindingSeverity.Warning, Rule = "Usunięcie pliku", File = p, Detail = "Usunięcie pliku wymaga uważnego przeglądu." });
            }

            int total = files.Sum(f => f.Added + f.Removed);
            if (total > 2000)
                findings.Add(new AuditFinding { Severity = FindingSeverity.Warning, Rule = "Duża zmiana", Detail = $"{total} zmienionych linii – trudna do przeglądu; rozważ odrzucenie i mniejszy zakres." });
            if (files.Count > 0 && !files.Any(f => Norm(f.Path).StartsWith("tests/", StringComparison.OrdinalIgnoreCase)) && files.Any(f => Norm(f.Path).StartsWith("src/", StringComparison.OrdinalIgnoreCase) && f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
                findings.Add(new AuditFinding { Severity = FindingSeverity.Warning, Rule = "Brak nowych testów", Detail = "Zmieniono kod bez dodania lub zmiany testów." });

            // Duplikaty (ta sama reguła i plik) scalamy.
            return findings.GroupBy(x => (x.Severity, x.Rule, x.File)).Select(g => g.First()).OrderByDescending(x => x.Severity).ToList();
        }

        private const string TestEditDetail = "Zmiana istniejącego testu może osłabić weryfikację – wdrożenie wymaga osobnego potwierdzenia.";

        /// <summary>Reguły treści dla jednego pliku z diffu.</summary>
        private static void AuditContent(DiffParser.FileDiff fd, string p, List<AuditFinding> findings, Dictionary<string, List<string>> addedCode, string? worktree)
        {
            bool isTest = p.StartsWith("tests/", StringComparison.OrdinalIgnoreCase);
            bool isDoc = p.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || p.StartsWith("docs/", StringComparison.OrdinalIgnoreCase) || p.StartsWith("reports/", StringComparison.OrdinalIgnoreCase);
            bool isSrcCSharp = p.StartsWith("src/", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
            bool isCore = p.StartsWith(CoreRoot, StringComparison.OrdinalIgnoreCase);
            var lang = SourceScanner.LangOf(p);
            var added = new List<string>();
            addedCode[p] = added;
            int factsAdded = 0, factsRemoved = 0;
            // Linie dodane analizujemy na pełnym pliku po zmianie (stan leksera od początku pliku, nie od początku fragmentu diffu).
            ScannedLine[]? fileScan = worktree != null && lang is not (SourceLang.None or SourceLang.Text) ? ScanWorktreeFile(worktree, p, lang) : null;

            foreach (var hunk in fd.Hunks)
            {
                // Stan leksera na początku fragmentu nie jest znany – zakładamy kod (ostrożnie: więcej linii traktowanych jak kod).
                var oldSide = new SourceScanner(lang);
                var newSide = new SourceScanner(lang);
                int newLine = hunk.NewStart;
                foreach (var line in hunk.Lines)
                {
                    if (line.Kind == ' ') { oldSide.Line(line.Text); newSide.Line(line.Text); newLine++; continue; }
                    bool isAdded = line.Kind == '+';
                    var s = isAdded ? newSide.Line(line.Text) : oldSide.Line(line.Text);
                    if (isAdded && fileScan != null)
                        s = newLine >= 1 && newLine <= fileScan.Length && fileScan[newLine - 1].Raw == line.Text
                            ? fileScan[newLine - 1]
                            : new ScannedLine(line.Text, line.Text, line.Text, false, false); // niezgodność z plikiem – bez wyjątków dla komentarzy
                    if (isAdded)
                    {
                        added.Add(s.CodeOnly);
                        if (!isDoc)
                        {
                            // BOM na początku pliku (pierwsza linia) jest dopuszczalny; w środku – nie.
                            string text = newLine == 1 && line.Text.StartsWith("\uFEFF", StringComparison.Ordinal) ? line.Text.Substring(1) : line.Text;
                            if (ControlChars.IsMatch(text))
                                findings.Add(Block("Niedozwolone znaki sterujące", p, "Znaki sterujące/kierunkowe Unicode (Trojan Source) lub nietypowe końce linii: " + Short(Visible(line.Text))));
                        }
                        newLine++;
                    }
                    if (isDoc || lang == SourceLang.None) continue;

                    if (isTest)
                    {
                        if (isAdded && Matches(SkipArgument, s.CodeOnly))
                            findings.Add(Block("Wyłączenie testu", p, "Dodano Skip do testu: " + Short(line.Text)));
                        if (TestAttribute.IsMatch(s.CodeOnly)) { if (isAdded) factsAdded++; else factsRemoved++; }
                    }
                    if (isAdded && lang != SourceLang.Text && Matches(ModuleInitializer, s.CodeOnly))
                        findings.Add(Block("Zakaz [ModuleInitializer]", p, "Kod uruchamiany automatycznie przy ładowaniu modułu: " + Short(line.Text)));
                    if (isTest) continue; // testy: reguły LIVE/krypto/martingale nie dotyczą (osobny zestaw, nie trafia do wydania)

                    foreach (var (re, rule, detail) in WithStringPatterns)
                        if (Matches(re, s.WithStrings))
                            // Usunięcie linii z odwołaniem do LIVE/krypto też jest blokowane (osłabienie zabezpieczeń); dodanie – również.
                            findings.Add(Block(rule, p, (isAdded ? "+ " : "− ") + detail + ": " + Short(line.Text)));
                    foreach (var (re, rule, detail) in BlockingCodePatterns)
                        if (Matches(re, s.CodeOnly))
                            findings.Add(Block(rule, p, (isAdded ? "+ " : "− ") + detail + ": " + Short(line.Text)));
                    if (isAdded && isSrcCSharp && Matches(SrcEscapePattern, s.CodeOnly))
                        findings.Add(Block("Zakaz refleksji i kodu niezarządzanego", p, "Refleksja, kod niebezpieczny lub dyrektywa globalna mogą obejść ochronę modułów: " + Short(line.Text)));
                    if (isAdded && isCore && Matches(CoreNetwork, s.CodeOnly))
                        findings.Add(Block("Silnik bez sieci i procesów", p, "Nowe połączenie sieciowe lub proces w silniku: " + Short(line.Text)));
                }
            }
            if (isTest && factsRemoved > factsAdded)
                findings.Add(Block("Usunięcie testu", p, $"Usunięto atrybuty [Fact]/[Theory] ({factsRemoved} usuniętych, {factsAdded} dodanych) – nie wolno wyłączać testów."));
        }

        /// <summary>Analiza leksykalna całego pliku z kopii roboczej (null, gdy nie da się go odczytać jako UTF-8).</summary>
        private static ScannedLine[]? ScanWorktreeFile(string worktree, string p, SourceLang lang)
        {
            try
            {
                string root = Path.GetFullPath(worktree).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string full = Path.GetFullPath(Path.Combine(root, p));
                var cmp = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                var fi = new FileInfo(full);
                if (!full.StartsWith(root, cmp) || !fi.Exists || (fi.Attributes & FileAttributes.ReparsePoint) != 0) return null;
                string text = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(full));
                var sc = new SourceScanner(lang);
                return text.Split('\n').Select(l => sc.Line(l.EndsWith("\r", StringComparison.Ordinal) ? l.Substring(0, l.Length - 1) : l)).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or DecoderFallbackException)
            {
                return null;
            }
        }

        /// <summary>Zwraca opis powodu ochrony albo null, gdy plik nie jest chroniony.</summary>
        private static string? ProtectedReason(string p, string status, List<string> protectedPaths, HashSet<string> activeFiles,
            DiffParser.FileDiff? fd, Dictionary<string, List<string>> addedCode)
        {
            const string detail = "RiskEngine / limity / bezpieczeństwo / wykonanie / silnik / aktywne strategie / mosty – wdrożenie wymaga osobnego potwierdzenia.";
            // Najbardziej szczegółowy pasujący wpis (najdłuższy).
            string? best = null;
            foreach (var e in protectedPaths)
            {
                bool match = e.EndsWith("/", StringComparison.Ordinal) ? p.StartsWith(e, StringComparison.OrdinalIgnoreCase) : string.Equals(p, e, StringComparison.OrdinalIgnoreCase);
                if (match && (best == null || e.Length > best.Length)) best = e;
            }
            if (best != null)
            {
                if (string.Equals(best, CoreRoot, StringComparison.OrdinalIgnoreCase))
                {
                    addedCode.TryGetValue(p, out var added);
                    // Wyjątek (a): modyfikacja pliku strategii, której wszystkie strategie są nieaktywne w profilu.
                    if (status == "M" && StrategyFiles.Values.Contains(p, StringComparer.OrdinalIgnoreCase) && !activeFiles.Contains(p))
                    {
                        string? escape = fd == null ? "brak treści zmiany w diffie" : InactiveStrategyEditProblem(p, added ?? new List<string>());
                        return escape == null ? null : "Plik strategii nieaktywnej, ale zmiana wykracza poza jej kod (" + escape + ") – wymaga potwierdzenia.";
                    }
                    // Wyjątek (b): nowy plik w src/Apex.Core/Strategies/ (nowa, niezarejestrowana strategia).
                    if (status == "A" && p.StartsWith(StrategiesDir, StringComparison.OrdinalIgnoreCase))
                    {
                        if (p.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return null;
                        if (!p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return "Nowy plik w katalogu strategii innego typu niż .cs – wymaga potwierdzenia.";
                        string? problem = fd == null ? "brak treści pliku w diffie" : NewStrategyFileProblem(added ?? new List<string>());
                        return problem == null ? null : "Nowy plik w katalogu strategii może wpływać na istniejący kod (" + problem + ") – wymaga potwierdzenia. Nowe strategie umieszczaj w przestrzeni nazw " + StrategiesNamespace + ".<Nazwa>.";
                    }
                }
                if (string.Equals(best, DesktopRoot, StringComparison.OrdinalIgnoreCase) && p.StartsWith(DesktopWebRoot, StringComparison.OrdinalIgnoreCase))
                    return null; // panel (HTML/JS/CSS) – zmiany LIVE w JS i tak blokuje reguła treści
                return detail;
            }
            if (p.StartsWith("tests/", StringComparison.OrdinalIgnoreCase) && status is "M" or "T")
                return TestEditDetail;
            return null;
        }

        /// <summary>Wyjątek (a): dodane linie nie mogą deklarować typów/przestrzeni nazw ani odwoływać się do innych strategii.</summary>
        private static string? InactiveStrategyEditProblem(string p, List<string> added)
        {
            foreach (var code in added)
            {
                var m = ScopeEscape.Match(code);
                if (m.Success) return "deklaracja: " + Short(m.Value);
                foreach (Match r in StrategyIdRef.Matches(code))
                {
                    string id = r.Groups[1].Value;
                    if (!StrategyFiles.TryGetValue(id, out var file) || !string.Equals(file, p, StringComparison.OrdinalIgnoreCase))
                        return "odwołanie do innej strategii: StrategyId." + id;
                }
            }
            return null;
        }

        private static readonly Regex Token = new(@"@?[A-Za-z_][A-Za-z0-9_]*(?:\s*\.\s*@?[A-Za-z_][A-Za-z0-9_]*)*|[{};]", RegexOptions.Compiled);
        // Dyrektywy warunkowe mogłyby ukryć przed analizą deklarację przestrzeni nazw (#if false … namespace … #endif → typ w przestrzeni globalnej).
        private static readonly Regex NewFileEscape = new(@"\bglobal\s+using\b|\bextern\s+alias\b|\[\s*(?:assembly|module)\s*:|\bModuleInitializer|^\s*#\s*(?:if|elif|else|endif|define|undef|line)\b", RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>
        /// Wyjątek (b): nowy plik strategii nie może zmieniać znaczenia istniejącego kodu – wszystkie typy w przestrzeni nazw
        /// PODRZĘDNEJ wobec Apex.Core.Strategies (typ w Apex.Core.Strategies lub w przestrzeni globalnej przesłoniłby nazwy używane
        /// przez aktywne strategie), bez global using i atrybutów zestawu.
        /// </summary>
        private static string? NewStrategyFileProblem(List<string> added)
        {
            string code = string.Join("\n", added);
            var esc = NewFileEscape.Match(code);
            if (esc.Success) return "niedozwolona konstrukcja: " + Short(esc.Value);
            var toks = Token.Matches(code).Select(m => Regex.Replace(m.Value, @"\s+", "")).ToList();
            int depth = 0;
            bool fileScoped = false, any = false;
            var nsDepth = new Stack<int>();
            for (int k = 0; k < toks.Count; k++)
            {
                string t = toks[k];
                if (t == "{") { depth++; continue; }
                if (t == "}")
                {
                    depth--;
                    if (depth < 0) return "niezrównoważone nawiasy";
                    if (nsDepth.Count > 0 && depth < nsDepth.Peek()) nsDepth.Pop();
                    continue;
                }
                if (t == ";") continue;
                if (t == "namespace")
                {
                    string name = k + 1 < toks.Count ? toks[k + 1].TrimStart('@') : "";
                    if (fileScoped || nsDepth.Count > 0) return "zagnieżdżona przestrzeń nazw";
                    if (!name.StartsWith(StrategiesNamespace + ".", StringComparison.Ordinal) || name.Length <= StrategiesNamespace.Length + 1)
                        return "przestrzeń nazw " + (name.Length > 0 ? name : "?") + " (dozwolona tylko podrzędna wobec " + StrategiesNamespace + ")";
                    string next = k + 2 < toks.Count ? toks[k + 2] : "";
                    if (next == ";") { if (depth != 0) return "przestrzeń nazw wewnątrz bloku"; fileScoped = true; any = true; k += 2; continue; }
                    if (next == "{") { depth++; nsDepth.Push(depth); any = true; k += 2; continue; }
                    return "nieczytelna deklaracja przestrzeni nazw";
                }
                if (t is "class" or "struct" or "interface" or "enum" or "record" or "delegate" && !fileScoped && nsDepth.Count == 0)
                    return "typ poza przestrzenią nazw";
            }
            if (depth != 0) return "niezrównoważone nawiasy";
            return any ? null : "brak deklaracji przestrzeni nazw";
        }

        /// <summary>Pliki konfiguracji budowania, skryptów i narzędzi – AI nie może ich zmieniać (wykonanie kodu przy kompilacji, publikacja .algo).</summary>
        public static bool IsBuildOrScriptFile(string path)
        {
            string p = Norm(path);
            var segs = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length == 0) return false;
            if (segs.Take(segs.Length - 1).Any(s => EditorDirs.Contains(s))) return true;
            string name = segs[^1];
            if (BuildNames.Contains(name)) return true;
            if (name.StartsWith("Directory.Build.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Directory.Solution.", StringComparison.OrdinalIgnoreCase)) return true;
            return BuildExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        }

        private static readonly HashSet<string> EditorDirs = new(StringComparer.OrdinalIgnoreCase) { ".vscode", ".vs", ".idea", ".devcontainer", ".config", ".github", ".husky" };

        private static readonly HashSet<string> BuildNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "NuGet.config", "global.json", ".gitattributes", ".gitignore", ".gitmodules", ".editorconfig", ".globalconfig", "launchSettings.json",
            "dotnet-tools.json", "packages.config", "packages.lock.json", "Directory.Packages.props", "runtimeconfig.template.json", "xunit.runner.json",
        };

        private static readonly string[] BuildExtensions =
        {
            ".csproj", ".vbproj", ".fsproj", ".proj", ".shproj", ".projitems", ".sln", ".slnx", ".slnf", ".props", ".targets", ".rsp", ".user",
            ".runsettings", ".nuspec", ".ps1", ".psm1", ".psd1", ".cmd", ".bat", ".sh", ".py", ".vbs", ".code-workspace",
        };

        /// <summary>Nazwy plików, które na Windows są aliasami (urządzenia, końcowa kropka/spacja, strumienie ADS, nazwy 8.3) lub nie są zwykłymi ścieżkami.</summary>
        private static string? PathProblem(string raw)
        {
            if (raw.Length == 0) return "pusta ścieżka";
            if (raw.Any(c => char.IsControl(c) || c is '"' or '\\' or ':' or '<' or '>' or '|' or '?' or '*'))
                return "znaki niedozwolone w nazwie pliku (sterujące, cudzysłów, \\, :, <, >, |, ?, *)";
            foreach (var seg in raw.Split('/'))
            {
                if (seg.Length == 0 || seg == "." || seg == "..") return "nieprawidłowy segment ścieżki";
                if (seg.EndsWith(".", StringComparison.Ordinal) || seg.EndsWith(" ", StringComparison.Ordinal) || seg.StartsWith(" ", StringComparison.Ordinal))
                    return "segment kończący się kropką lub spacją (alias ścieżki w Windows)";
                if (seg.Equals(".git", StringComparison.OrdinalIgnoreCase)) return "segment .git";
                if (Regex.IsMatch(seg, @"~\d")) return "nazwa w formacie 8.3 (alias ścieżki w Windows)";
                if (IsDeviceName(seg)) return "zarezerwowana nazwa urządzenia Windows";
            }
            return null;
        }

        internal static bool IsDeviceName(string segment)
        {
            string stem = segment.Split('.')[0].TrimEnd(' ');
            return Regex.IsMatch(stem, @"^(?i:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[0-9¹²³]|LPT[0-9¹²³])$");
        }

        /// <summary>Kodowanie pliku po zmianie (z kopii roboczej): BOM UTF-16/UTF-32, bajty NUL, nieprawidłowy UTF-8.</summary>
        private static string? EncodingProblem(string worktree, string p)
        {
            byte[] bytes;
            try
            {
                string root = Path.GetFullPath(worktree).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string full = Path.GetFullPath(Path.Combine(root, p));
                var cmp = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!full.StartsWith(root, cmp)) return "ścieżka poza kopią roboczą";
                var fi = new FileInfo(full);
                if (!fi.Exists) return null;
                if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) return "dowiązanie zamiast pliku";
                bytes = File.ReadAllBytes(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return "nie można odczytać pliku (" + ex.Message + ")";
            }
            if (bytes.Length >= 4 && ((bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0) || (bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF)))
                return "kodowanie UTF-32";
            if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
                return "kodowanie UTF-16";
            if (Array.IndexOf(bytes, (byte)0) >= 0) return "bajty NUL w pliku";
            try { _ = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { return "nieprawidłowe sekwencje UTF-8"; }
            return null;
        }

        /// <summary>Dopasowanie także po zdekodowaniu ucieczek Unicode (\u004C) i usunięciu znaków formatujących (kompilator C# je pomija).</summary>
        private static bool Matches(Regex re, string text) => re.IsMatch(text) || re.IsMatch(SourceScanner.Decode(text));

        /// <summary>Linie zmienione w diffie: (plik, treść bez znaku +/-, czy dodana). Parser uwzględnia liczniki fragmentów (@@).</summary>
        public static IEnumerable<(string file, string line, bool added)> ChangedLines(string diff)
        {
            foreach (var fd in DiffParser.Parse(diff ?? ""))
                foreach (var h in fd.Hunks)
                    foreach (var l in h.Lines)
                        if (l.Kind is '+' or '-') yield return (fd.Path, l.Text, l.Kind == '+');
        }

        private static AuditFinding Block(string rule, string file, string detail) =>
            new() { Severity = FindingSeverity.Blocking, Rule = rule, File = file, Detail = detail };

        private static string Norm(string p) => p.Replace('\\', '/').TrimStart('/');

        private static string Visible(string s) => Regex.Replace(s, @"[\p{Cc}\p{Cf}\u2028\u2029]", m => $"<U+{(int)m.Value[0]:X4}>");

        private static string Short(string s)
        {
            s = s.Trim();
            return s.Length > 160 ? s.Substring(0, 160) + "…" : s;
        }
    }

    /// <summary>Parser diffu unified (git): pliki, tryby, fragmenty z licznikami linii – linia "+++ x" w treści nie zmienia pliku.</summary>
    internal static class DiffParser
    {
        internal sealed class DiffLine
        {
            public char Kind;
            public string Text = "";
        }

        internal sealed class Hunk
        {
            public int NewStart = 1;
            public List<DiffLine> Lines { get; } = new();
        }

        internal sealed class FileDiff
        {
            public string OldPath = "";
            public string NewPath = "";
            public string HeaderPath = "";
            public string NewMode = "";
            public bool Binary;
            public List<Hunk> Hunks { get; } = new();

            public string Path => NewPath.Length > 0 && NewPath != "/dev/null" ? NewPath
                                : OldPath.Length > 0 && OldPath != "/dev/null" ? OldPath
                                : HeaderPath;
        }

        private static readonly Regex HunkHeader = new(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.Compiled);

        public static List<FileDiff> Parse(string diff)
        {
            var list = new List<FileDiff>();
            FileDiff? cur = null;
            Hunk? hunk = null;
            int oldLeft = 0, newLeft = 0;
            foreach (var rawLine in diff.Split('\n'))
            {
                // CRLF w treści pliku: obcinamy jeden końcowy CR (pozostałe CR zostają – wykrywa je reguła znaków sterujących).
                string line = rawLine.EndsWith("\r", StringComparison.Ordinal) ? rawLine.Substring(0, rawLine.Length - 1) : rawLine;
                if (hunk != null && (oldLeft > 0 || newLeft > 0))
                {
                    char kind = line.Length == 0 ? ' ' : line[0];
                    string text = line.Length == 0 ? "" : line.Substring(1);
                    if (kind == '\\') continue; // "\ No newline at end of file"
                    if (kind is ' ' or '+' or '-')
                    {
                        if (kind != '+') oldLeft--;
                        if (kind != '-') newLeft--;
                        hunk.Lines.Add(new DiffLine { Kind = kind, Text = text });
                        if (oldLeft <= 0 && newLeft <= 0) hunk = null;
                        continue;
                    }
                    hunk = null; // nieoczekiwana linia – koniec fragmentu, dalej jako nagłówek
                }
                if (line.StartsWith("diff --git ", StringComparison.Ordinal))
                {
                    cur = new FileDiff { HeaderPath = HeaderPath(line.Substring(11)) };
                    list.Add(cur);
                    hunk = null;
                    continue;
                }
                if (line.StartsWith("--- ", StringComparison.Ordinal) && hunk == null)
                {
                    if (cur == null || cur.Hunks.Count > 0) { cur = new FileDiff(); list.Add(cur); }
                    cur.OldPath = LabelPath(line.Substring(4), "a/");
                    continue;
                }
                if (line.StartsWith("+++ ", StringComparison.Ordinal) && hunk == null && cur != null)
                {
                    cur.NewPath = LabelPath(line.Substring(4), "b/");
                    continue;
                }
                if (line.StartsWith("@@ ", StringComparison.Ordinal) && cur != null)
                {
                    var m = HunkHeader.Match(line);
                    if (!m.Success) continue;
                    oldLeft = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 1;
                    newLeft = m.Groups[4].Success ? int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) : 1;
                    hunk = new Hunk { NewStart = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) };
                    cur.Hunks.Add(hunk);
                    continue;
                }
                if (cur == null) continue;
                if (line.StartsWith("new file mode ", StringComparison.Ordinal)) cur.NewMode = line.Substring(14).Trim();
                else if (line.StartsWith("new mode ", StringComparison.Ordinal)) cur.NewMode = line.Substring(9).Trim();
                else if (line.StartsWith("Binary files ", StringComparison.Ordinal) || line.StartsWith("GIT binary patch", StringComparison.Ordinal)) cur.Binary = true;
            }
            return list.Where(f => f.Path.Length > 0).ToList();
        }

        /// <summary>Ścieżka z linii ---/+++: git dopisuje TAB po nazwie ze spacją; nazwy nietypowe są w cudzysłowie (C-style).</summary>
        private static string LabelPath(string s, string prefix)
        {
            if (s.EndsWith("\t", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 1);
            if (s == "/dev/null") return s;
            if (s.StartsWith("\"", StringComparison.Ordinal)) s = GitChangeParsing.Unquote(s);
            return s.StartsWith(prefix, StringComparison.Ordinal) ? s.Substring(prefix.Length) : s;
        }

        /// <summary>Ścieżka z "diff --git a/X b/X" (gdy brak linii ---/+++, np. pusty nowy plik lub sama zmiana trybu).</summary>
        private static string HeaderPath(string rest)
        {
            if (rest.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = 1;
                while (end < rest.Length && !(rest[end] == '"' && rest[end - 1] != '\\')) end++;
                string a = GitChangeParsing.Unquote(rest.Substring(0, Math.Min(end + 1, rest.Length)));
                return a.StartsWith("a/", StringComparison.Ordinal) ? a.Substring(2) : a;
            }
            if (rest.StartsWith("a/", StringComparison.Ordinal) && (rest.Length - 5) % 2 == 0 && rest.Length > 5)
            {
                string p = rest.Substring(2, (rest.Length - 5) / 2);
                if (rest == "a/" + p + " b/" + p) return p;
            }
            return "";
        }
    }

    public enum SourceLang
    {
        /// <summary>Dokumentacja i pliki bez reguł treści.</summary>
        None,
        /// <summary>Tekst (JSON, HTML, CSS…): reguły na całej linii, bez usuwania komentarzy.</summary>
        Text,
        CSharp,
        Js,
        /// <summary>MQL5 / C / C++.</summary>
        CLike,
    }

    /// <summary>Linia po analizie leksykalnej.</summary>
    public readonly struct ScannedLine
    {
        public ScannedLine(string raw, string kept, string bare, bool commentsClean, bool stringsClean)
        {
            Raw = raw;
            Kept = kept;
            Bare = bare;
            CommentsClean = commentsClean;
            StringsClean = stringsClean;
        }

        public string Raw { get; }
        /// <summary>Linia bez komentarzy (literały napisów zachowane).</summary>
        public string Kept { get; }
        /// <summary>Linia bez komentarzy i bez treści zwykłych literałów napisów zamkniętych w tej linii.</summary>
        public string Bare { get; }
        /// <summary>Usunięte komentarze są jednoznaczne (bez cudzysłowów, "*/", niejednoznacznych "/" w JS) – inaczej reguły patrzą na całą linię.</summary>
        public bool CommentsClean { get; }
        /// <summary>Wyczyszczone literały są jednoznaczne (brak niezamkniętych napisów, brak wyrażeń regularnych JS w linii).</summary>
        public bool StringsClean { get; }

        /// <summary>Tekst dla reguł obejmujących literały (LIVE, krypto): bez komentarzy, gdy są jednoznaczne; inaczej cała linia.</summary>
        public string WithStrings => CommentsClean ? Kept : Raw;
        /// <summary>Tekst dla reguł kodu: bez komentarzy i literałów, gdy analiza jest jednoznaczna; w razie wątpliwości – więcej tekstu.</summary>
        public string CodeOnly => CommentsClean ? (StringsClean ? Bare : Kept) : Raw;
    }

    /// <summary>
    /// Prosty lekser C#, JS i MQL5: rozpoznaje komentarze (//, /* */) i literały napisów (w tym @"", $"" ze wstawkami, """ raw,
    /// szablony JS `${}`, wyrażenia regularne JS), aby słowa w komentarzach nie blokowały zmian, a kod po komentarzu
    /// (np. "/* x */ AllowLive = true;") był sprawdzany. W razie niejednoznaczności wynik jest ostrożny (więcej tekstu do reguł).
    /// </summary>
    public sealed class SourceScanner
    {
        private enum K { Code, Hole, Block, Str, Chr, Verbatim, Raw, Interp, InterpVerbatim, InterpRaw, JsStr, Template }

        private sealed class Frame
        {
            public K Kind;
            public char Quote;
            public int Quotes;
            public int Dollars = 1;
            public int Depth;
            public bool Clean = true;
            /// <summary>Indeks początku treści literału w bieżącej linii; -1 = literał z poprzedniej linii (nie jest czyszczony).</summary>
            public int Start = -1;
        }

        private static readonly char[] QuoteChars = { '"', '\'', '`' };
        private static readonly HashSet<string> RegexKeywords = new(StringComparer.Ordinal)
        {
            "return", "typeof", "case", "do", "else", "in", "of", "new", "delete", "void", "throw", "instanceof", "yield", "await",
        };

        private readonly SourceLang _lang;
        private readonly List<Frame> _st = new() { new Frame { Kind = K.Code } };
        private char _prevSig;
        private string _prevWord = "";

        public SourceScanner(SourceLang lang) => _lang = lang;

        public static SourceLang LangOf(string path)
        {
            string p = path.Replace('\\', '/');
            string ext = Path.GetExtension(p).ToLowerInvariant();
            if (ext == ".md" || p.StartsWith("docs/", StringComparison.OrdinalIgnoreCase) || p.StartsWith("reports/", StringComparison.OrdinalIgnoreCase)) return SourceLang.None;
            return ext switch
            {
                ".cs" or ".csx" => SourceLang.CSharp,
                ".js" or ".mjs" or ".cjs" or ".ts" or ".jsx" or ".tsx" => SourceLang.Js,
                ".mq5" or ".mqh" or ".mq4" or ".c" or ".h" or ".cpp" or ".hpp" => SourceLang.CLike,
                _ => SourceLang.Text,
            };
        }

        public ScannedLine Line(string text)
        {
            if (_lang is SourceLang.Text or SourceLang.None) return new ScannedLine(text, text, text, true, true);
            // Samotne CR, U+2028/2029/0085 kompilator traktuje jak koniec linii (koniec komentarza //).
            var segs = text.Split(new[] { '\r', '\u2028', '\u2029', '\u0085' });
            if (segs.Length == 1) return Physical(text);
            var kept = new List<string>();
            var bare = new List<string>();
            bool cc = true, sc = true;
            foreach (var s in segs)
            {
                var r = Physical(s);
                kept.Add(r.Kept);
                bare.Add(r.Bare);
                cc &= r.CommentsClean;
                sc &= r.StringsClean;
            }
            return new ScannedLine(text, string.Join(" ", kept), string.Join(" ", bare), cc, sc);
        }

        private ScannedLine Physical(string text)
        {
            foreach (var f in _st) f.Start = -1;
            // Dyrektywa preprocesora (C#, MQL5): cała linia jako kod – bez analizy cudzysłowów (np. #region Don't).
            if (_lang != SourceLang.Js && _st.Count == 1 && text.TrimStart().StartsWith("#", StringComparison.Ordinal))
                return new ScannedLine(text, text, text, true, true);
            var kept = text.ToCharArray();
            var bare = text.ToCharArray();
            bool cc = true, sc = true, slash = false;
            int n = text.Length, i = 0;
            while (i < n)
            {
                var f = _st[^1];
                char c = text[i];
                char d = i + 1 < n ? text[i + 1] : '\0';
                switch (f.Kind)
                {
                    case K.Block:
                        if (c == '*' && d == '/')
                        {
                            Blank(kept, bare, i, 2);
                            i += 2;
                            if (!f.Clean) cc = false;
                            _st.RemoveAt(_st.Count - 1);
                            continue;
                        }
                        if (c is '"' or '\'' or '`') f.Clean = false;
                        Blank(kept, bare, i, 1);
                        i++;
                        continue;

                    case K.Code:
                    case K.Hole:
                        if (c == '/' && d == '/')
                        {
                            string rest = text.Substring(i);
                            if (_st.Count != 1 || slash || rest.IndexOfAny(QuoteChars) >= 0 || rest.Contains("*/", StringComparison.Ordinal)) cc = false;
                            Blank(kept, bare, i, n - i);
                            i = n;
                            continue;
                        }
                        if (c == '/' && d == '*')
                        {
                            bool clean = _st.Count == 1 && !slash;
                            _st.Add(new Frame { Kind = K.Block, Clean = clean });
                            Blank(kept, bare, i, 2);
                            i += 2;
                            continue;
                        }
                        if (_lang == SourceLang.Js && c == '/')
                        {
                            slash = true; // dzielenie albo wyrażenie regularne – literały i komentarze w tej linii są niejednoznaczne
                            if (RegexAllowed())
                            {
                                int end = ScanRegex(text, i);
                                if (end < 0) { sc = false; cc = false; i = n; continue; }
                                i = end;
                                _prevSig = ')';
                                _prevWord = "";
                                continue;
                            }
                            Sig(text, i);
                            i++;
                            continue;
                        }
                        if (TryOpenLiteral(text, ref i)) continue;
                        if (f.Kind == K.Hole)
                        {
                            if (c == '{') f.Depth++;
                            else if (c == '}')
                            {
                                if (f.Depth == 0)
                                {
                                    if (Run(text, i, '}') >= f.Dollars) { _st.RemoveAt(_st.Count - 1); i += f.Dollars; continue; }
                                }
                                else f.Depth--;
                            }
                        }
                        Sig(text, i);
                        i++;
                        continue;

                    case K.Str:
                    case K.Chr:
                    case K.JsStr:
                        if (c == '\\') { i += 2; continue; }
                        if (c == f.Quote) { Close(f, i, bare, blank: true); i++; continue; }
                        i++;
                        continue;

                    case K.Verbatim:
                        if (c == '"')
                        {
                            if (d == '"') { i += 2; continue; }
                            Close(f, i, bare, blank: true);
                            i++;
                            continue;
                        }
                        i++;
                        continue;

                    case K.Raw:
                        if (c == '"')
                        {
                            int r = Run(text, i, '"');
                            if (r >= f.Quotes) { Close(f, i, bare, blank: true); i += f.Quotes; continue; }
                            i += r;
                            continue;
                        }
                        i++;
                        continue;

                    case K.Interp:
                    case K.InterpVerbatim:
                        if (f.Kind == K.Interp && c == '\\') { i += 2; continue; }
                        if (c == '"')
                        {
                            if (f.Kind == K.InterpVerbatim && d == '"') { i += 2; continue; }
                            Close(f, i, bare, blank: false);
                            i++;
                            continue;
                        }
                        if (c == '{')
                        {
                            if (d == '{') { i += 2; continue; }
                            _st.Add(new Frame { Kind = K.Hole, Dollars = 1 });
                            i++;
                            continue;
                        }
                        if (c == '}' && d == '}') { i += 2; continue; }
                        i++;
                        continue;

                    case K.InterpRaw:
                        if (c == '"')
                        {
                            int r = Run(text, i, '"');
                            if (r >= f.Quotes) { Close(f, i, bare, blank: false); i += f.Quotes; continue; }
                            i += r;
                            continue;
                        }
                        if (c == '{')
                        {
                            int r = Run(text, i, '{');
                            if (r >= f.Dollars) _st.Add(new Frame { Kind = K.Hole, Dollars = f.Dollars });
                            i += r;
                            continue;
                        }
                        i++;
                        continue;

                    case K.Template:
                        if (c == '\\') { i += 2; continue; }
                        if (c == '`') { Close(f, i, bare, blank: false); i++; continue; }
                        if (c == '$' && d == '{') { _st.Add(new Frame { Kind = K.Hole, Dollars = 1 }); i += 2; continue; }
                        i++;
                        continue;
                }
            }

            // Koniec linii.
            var top = _st[^1];
            if (top.Kind == K.Block && !top.Clean) cc = false;
            bool continued = text.Length - text.TrimEnd('\\').Length is int bs && bs % 2 == 1;
            bool singleLine = top.Kind switch
            {
                K.Str => _lang == SourceLang.CSharp || !continued,
                K.Chr => true,
                K.JsStr => !continued,
                K.Interp => true,
                _ => false,
            };
            if (singleLine)
            {
                // Niezamknięty literał jednoliniowy: lekser stracił synchronizację – reset do kodu i brak zaufania do czyszczenia.
                sc = false;
                cc = false;
                _st.RemoveRange(1, _st.Count - 1);
                _st[0].Kind = K.Code;
            }
            if (_lang == SourceLang.Js && slash) sc = false;
            return new ScannedLine(text, new string(kept), new string(bare), cc, sc);
        }

        private bool TryOpenLiteral(string text, ref int i)
        {
            int n = text.Length;
            char c = text[i];
            if (_lang == SourceLang.CSharp)
            {
                int j = i, dollars = 0;
                bool at = false;
                while (j < n && (text[j] == '$' || (text[j] == '@' && !at)))
                {
                    if (text[j] == '$') dollars++; else at = true;
                    j++;
                }
                if (j < n && text[j] == '"')
                {
                    int q = Run(text, j, '"');
                    if (q >= 3)
                    {
                        _st.Add(new Frame { Kind = dollars > 0 ? K.InterpRaw : K.Raw, Quotes = q, Dollars = Math.Max(1, dollars), Start = j + q });
                        i = j + q;
                        return true;
                    }
                    var kind = at ? (dollars > 0 ? K.InterpVerbatim : K.Verbatim) : (dollars > 0 ? K.Interp : K.Str);
                    _st.Add(new Frame { Kind = kind, Quote = '"', Start = j + 1 });
                    i = j + 1;
                    return true;
                }
                if (c == '\'')
                {
                    _st.Add(new Frame { Kind = K.Chr, Quote = '\'', Start = i + 1 });
                    i++;
                    return true;
                }
                return false;
            }
            if (_lang == SourceLang.CLike)
            {
                if (c is '"' or '\'')
                {
                    _st.Add(new Frame { Kind = c == '"' ? K.Str : K.Chr, Quote = c, Start = i + 1 });
                    i++;
                    return true;
                }
                return false;
            }
            if (_lang == SourceLang.Js)
            {
                if (c is '"' or '\'')
                {
                    _st.Add(new Frame { Kind = K.JsStr, Quote = c, Start = i + 1 });
                    i++;
                    return true;
                }
                if (c == '`')
                {
                    _st.Add(new Frame { Kind = K.Template, Quote = c, Start = i + 1 });
                    i++;
                    return true;
                }
            }
            return false;
        }

        private void Close(Frame f, int end, char[] bare, bool blank)
        {
            if (blank && f.Start >= 0)
                for (int k = f.Start; k < end && k < bare.Length; k++) bare[k] = ' ';
            _st.RemoveAt(_st.Count - 1);
            _prevSig = '"';
            _prevWord = "";
        }

        private void Sig(string text, int i)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) return;
            bool word = char.IsLetterOrDigit(c) || c is '_' or '$';
            if (word) _prevWord = i > 0 && _prevSig != '\0' && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] is '_' or '$') ? _prevWord + c : c.ToString();
            else _prevWord = "";
            _prevSig = c;
        }

        private bool RegexAllowed()
        {
            if (_prevSig == '\0') return true;
            if ("(,=:[!&|?{};+-*%<>~^".IndexOf(_prevSig) >= 0) return true;
            if (char.IsLetterOrDigit(_prevSig) || _prevSig is '_' or '$') return RegexKeywords.Contains(_prevWord);
            return false;
        }

        private static int ScanRegex(string text, int start)
        {
            bool cls = false;
            for (int j = start + 1; j < text.Length; j++)
            {
                char ch = text[j];
                if (ch == '\\') { j++; continue; }
                if (cls) { if (ch == ']') cls = false; continue; }
                if (ch == '[') { cls = true; continue; }
                if (ch == '/')
                {
                    j++;
                    while (j < text.Length && char.IsLetter(text[j])) j++;
                    return j;
                }
            }
            return -1;
        }

        private static int Run(string text, int i, char ch)
        {
            int r = 0;
            while (i + r < text.Length && text[i + r] == ch) r++;
            return r;
        }

        private static void Blank(char[] kept, char[] bare, int i, int len)
        {
            for (int k = i; k < i + len && k < kept.Length; k++) { kept[k] = ' '; bare[k] = ' '; }
        }

        /// <summary>
        /// Postać do dopasowania: bez znaków formatujących Unicode (Cf – C# pomija je w identyfikatorach) i ze zdekodowanymi
        /// ucieczkami \uXXXX, \UXXXXXXXX, \u{…}, \xHH (identyfikator Allow\u004Cive to dla kompilatora AllowLive).
        /// </summary>
        public static string Decode(string s)
        {
            var noFormat = new StringBuilder(s.Length);
            foreach (char ch in s)
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.Format) noFormat.Append(ch);
            string t = noFormat.ToString();
            if (t.IndexOf('\\') < 0) return t;
            return Regex.Replace(t, @"\\u\{([0-9A-Fa-f]{1,6})\}|\\U([0-9A-Fa-f]{8})|\\u([0-9A-Fa-f]{4})|\\x([0-9A-Fa-f]{1,4})", m =>
            {
                string hex = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value;
                if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp) || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF)) return m.Value;
                return char.ConvertFromUtf32(cp);
            });
        }
    }

    /// <summary>Odczyt wyników poleceń git używanych przez <see cref="GitWorkspace"/> (wyjście -z, nazwy w cudzysłowie, pliki ignorowane).</summary>
    public static class GitChangeParsing
    {
        /// <summary>
        /// Łączy "git diff --numstat -z" (liczby linii, "-" = binarny) i "git diff --raw -z" (status, tryb pliku).
        /// Oba z --no-renames, więc każdy rekord ma jedną ścieżkę.
        /// </summary>
        public static List<ChangedFile> ParseChanges(string numstatZ, string rawZ)
        {
            var files = new Dictionary<string, ChangedFile>(StringComparer.Ordinal);
            ChangedFile Get(string path)
            {
                if (!files.TryGetValue(path, out var cf)) files[path] = cf = new ChangedFile { Path = path };
                return cf;
            }
            foreach (var rec in numstatZ.Split('\0'))
            {
                string r = rec.TrimStart('\n');
                if (r.Length == 0) continue;
                var p = r.Split('\t', 3);
                if (p.Length < 3 || p[2].Length == 0) continue;
                var cf = Get(p[2]);
                cf.Binary = p[0] == "-" || p[1] == "-";
                int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int a);
                int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int d);
                cf.Added = a;
                cf.Removed = d;
            }
            // ":stary_tryb nowy_tryb stary_sha nowy_sha STATUS\0ścieżka\0"
            var parts = rawZ.Split('\0');
            for (int k = 0; k + 1 < parts.Length; k++)
            {
                string meta = parts[k].TrimStart('\n');
                if (!meta.StartsWith(":", StringComparison.Ordinal)) continue;
                var m = meta.Substring(1).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (m.Length < 5 || m[4].Length == 0) continue;
                var cf = Get(parts[k + 1]);
                cf.Status = m[4].Substring(0, 1);
                cf.Mode = m[1] == "000000" ? "" : m[1];
                k++;
            }
            return files.Values.ToList();
        }

        /// <summary>
        /// Pliki ignorowane przez git ("ls-files -z --others --ignored --exclude-standard --directory"), które wpływają na kompilację
        /// lub testy, a nie trafiają do diffu: np. *.csproj.user (MSBuild importuje je automatycznie), obj/*.props i obj/*.targets
        /// inne niż wygenerowane przez NuGet, pliki .cs lub .json w katalogach ignorowanych. Wyniki budowania (bin/, obj/, .algo) pomijamy.
        /// </summary>
        public static List<string> HiddenBuildInputs(string worktree, string lsFilesZ)
        {
            var res = new List<string>();
            foreach (var entry in lsFilesZ.Split('\0'))
            {
                string e = entry.TrimStart('\n');
                if (e.Length == 0) continue;
                bool dir = e.EndsWith("/", StringComparison.Ordinal);
                var segs = e.TrimEnd('/').Split('/');
                if (segs.Any(s => s.Equals(".ailab", StringComparison.Ordinal) || s.Equals(".vs", StringComparison.OrdinalIgnoreCase) || s.Equals("TestResults", StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (!dir)
                {
                    if (IsBuildRelevant(e)) res.Add(e);
                    continue;
                }
                string last = segs[^1];
                if (last.Equals("bin", StringComparison.OrdinalIgnoreCase)) continue;
                string full = Path.Combine(worktree, e.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    if (last.Equals("obj", StringComparison.OrdinalIgnoreCase))
                    {
                        // MSBuild importuje obj/<projekt>.*.props|targets – dozwolone tylko pliki wygenerowane przez NuGet.
                        foreach (var f in Directory.EnumerateFiles(full, "*", SearchOption.TopDirectoryOnly))
                        {
                            string name = Path.GetFileName(f);
                            bool import = name.EndsWith(".props", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".targets", StringComparison.OrdinalIgnoreCase);
                            bool nuget = name.EndsWith(".nuget.g.props", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".nuget.g.targets", StringComparison.OrdinalIgnoreCase);
                            if (import && !nuget) res.Add(e + name);
                        }
                        continue;
                    }
                    int count = 0;
                    foreach (var f in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                    {
                        string rel = e + Path.GetRelativePath(full, f).Replace('\\', '/');
                        if (rel.Split('/').Any(s => s.Equals("bin", StringComparison.OrdinalIgnoreCase) || s.Equals("obj", StringComparison.OrdinalIgnoreCase))) continue;
                        if (IsBuildRelevant(rel)) res.Add(rel);
                        if (++count > 5000) { res.Add(e); break; } // zbyt wiele plików – sam katalog jako podejrzany
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    res.Add(e); // nie da się sprawdzić – ostrożnie traktujemy katalog jako ukryty plik wejściowy
                }
            }
            return res.Distinct(StringComparer.Ordinal).ToList();
        }

        private static bool IsBuildRelevant(string path)
        {
            string name = path.Split('/')[^1];
            if (name.EndsWith(".algo", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".algo.metadata", StringComparison.OrdinalIgnoreCase)) return false;
            if (ChangeAudit.IsBuildOrScriptFile(path)) return true;
            string ext = Path.GetExtension(name).ToLowerInvariant();
            return ext is ".cs" or ".csx" or ".json" or ".resx" or ".dll" or ".mq5" or ".mqh" or ".js";
        }

        /// <summary>
        /// Sprawdza plik .git kopii roboczej (zanim uruchomimy w niej git): musi być zwykłym plikiem wskazującym katalog
        /// repo/.git/worktrees/&lt;nazwa&gt; laboratorium. Podmieniony wskaźnik przekierowałby git do repozytorium z konfiguracją agenta.
        /// Zwraca opis problemu albo null.
        /// </summary>
        public static string? CheckWorktreePointer(string worktree, string repoDir)
        {
            const string broken = "Kopia robocza została naruszona: plik .git nie wskazuje repozytorium AI Lab – audyt przerwany";
            try
            {
                string dotGit = Path.Combine(worktree, ".git");
                var fi = new FileInfo(dotGit);
                if (!fi.Exists) return broken + (Directory.Exists(dotGit) ? " (.git jest katalogiem)." : " (brak pliku .git).");
                if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) return broken + " (.git jest dowiązaniem).";
                string content = File.ReadAllText(dotGit).Trim();
                if (!content.StartsWith("gitdir:", StringComparison.Ordinal) || content.IndexOf('\n') >= 0) return broken + ".";
                string target = content.Substring(7).Trim();
                string full = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(worktree, target)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string parent = Path.GetDirectoryName(full) ?? "";
                string expected = Path.GetFullPath(Path.Combine(repoDir, ".git", "worktrees"));
                if (SamePath(parent, expected) || SamePath(RealPath(parent), RealPath(expected))) return null;
                // Inna postać tej samej ścieżki (np. nazwy 8.3 w Windows, dowiązania): sprawdzamy tożsamość katalogu znacznikiem
                // zapisanym w katalogu administracyjnym laboratorium i odczytanym przez ścieżkę ze wskaźnika.
                string admin = Path.Combine(expected, Path.GetFileName(full));
                if (Path.GetFileName(full).Length == 0 || !Directory.Exists(admin) || !Directory.Exists(full)) return broken + ".";
                string token = Guid.NewGuid().ToString("N");
                string marker = Path.Combine(admin, "apex-ailab-" + token);
                try
                {
                    File.WriteAllText(marker, token);
                    string seen = Path.Combine(full, "apex-ailab-" + token);
                    return File.Exists(seen) && File.ReadAllText(seen) == token ? null : broken + ".";
                }
                finally
                {
                    try { File.Delete(marker); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return broken + " (" + ex.Message + ").";
            }
        }

        private static bool SamePath(string a, string b)
        {
            var cmp = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), cmp);
        }

        /// <summary>Ścieżka z rozwiązanymi dowiązaniami (git zapisuje ścieżki rzeczywiste).</summary>
        private static string RealPath(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                string root = Path.GetPathRoot(full) ?? "";
                string cur = root;
                foreach (var seg in full.Substring(root.Length).Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string next = Path.Combine(cur, seg);
                    var di = new DirectoryInfo(next);
                    if (di.Exists && di.LinkTarget != null)
                    {
                        var t = di.ResolveLinkTarget(returnFinalTarget: true);
                        if (t != null) next = t.FullName;
                    }
                    cur = next;
                }
                return cur;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return path;
            }
        }

        /// <summary>Dekoduje nazwę w cudzysłowie w stylu C (git: \", \\, \t, \n, \ooo – bajty UTF-8).</summary>
        public static string Unquote(string s)
        {
            if (s.Length < 2 || s[0] != '"') return s;
            var bytes = new List<byte>();
            for (int i = 1; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') break;
                if (c != '\\' || i + 1 >= s.Length) { bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString())); continue; }
                char e = s[++i];
                if (i + 2 < s.Length && IsOctal(e) && IsOctal(s[i + 1]) && IsOctal(s[i + 2]))
                {
                    bytes.Add((byte)Convert.ToInt32(s.Substring(i, 3), 8));
                    i += 2;
                    continue;
                }
                bytes.Add(e switch
                {
                    'a' => 7,
                    'b' => 8,
                    't' => 9,
                    'n' => 10,
                    'v' => 11,
                    'f' => 12,
                    'r' => 13,
                    _ => (byte)e,
                });
            }
            return Encoding.UTF8.GetString(bytes.ToArray());

            static bool IsOctal(char ch) => ch >= '0' && ch <= '7';
        }
    }
}
