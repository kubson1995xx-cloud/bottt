using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Apex.AiLab.Lab;

namespace Apex.AiLab.Pipeline
{
    /// <summary>Treść zadania dla agentów AI (po polsku). Zasady bezpieczeństwa są powtarzane w każdym zadaniu.</summary>
    public static class PromptBuilder
    {
        public const string Rules =
@"## Zasady (obowiązkowe)
1. Pracujesz w izolowanej kopii repozytorium APEX Trading AI. Twoja zmiana NIE trafi do działającego bota bez akceptacji użytkownika.
   Po Twojej pracy AI Lab niezależnie wykona: audyt zmian, kompilację, wszystkie testy (także integracyjne) i backtest portfela.
2. Kryptowaluty są całkowicie wykluczone. Konto REAL/LIVE pozostaje zablokowane – nie zmieniaj niczego, co dotyczy dopuszczenia konta LIVE
   (AccountGuard, HostConfig, IsLive/AllowLive/LiveTradingFeature, AccountSnapshot, ACCOUNT_TRADE_MODE, odblokowanie LIVE w panelu).
   Każda dodana lub usunięta linia z takim odwołaniem (także w JS panelu) powoduje odrzucenie zmiany.
3. Nigdy: martingale, grid, uśrednianie stratnych pozycji, zwiększanie pozycji po stracie, transakcje bez SL, ukryta dźwignia.
4. Moduły chronione (zmiana wymaga osobnego, wpisanego potwierdzenia użytkownika): cały src/Apex.Core (RiskEngine, limity, strażnik,
   wykonanie, model rynku, silnik, aktywne strategie wg profilu), src/Apex.Desktop (poza panelem wwwroot), mosty src/Platforms,
   katalog config/, tests/Apex.Tests/TestSupport.cs, symulator platformy oraz każda zmiana ISTNIEJĄCYCH plików testów.
   Zwykłe zatwierdzenie (bez frazy) wystarcza tylko dla: zmian plików strategii nieaktywnych w profilu (bez nowych typów i przestrzeni nazw), nowych plików strategii
   w src/Apex.Core/Strategies/ z przestrzenią nazw podrzędną (np. Apex.Core.Strategies.Nowa), NOWYCH plików testów, panelu i dokumentacji.
   Zmieniaj moduły chronione tylko wtedy, gdy dane jednoznacznie tego wymagają.
5. Zmiany zawsze odrzucane: src/Apex.AiLab, tools/windows, tools/Apex.Backtester (backtester ocenia zmiany), AccountGuard.cs, HostConfig.cs,
   pliki projektów i budowania (*.csproj, *.sln, *.props, *.targets, Directory.Build.*, NuGet.config, global.json, .gitattributes,
   .gitignore, .editorconfig), skrypty (*.ps1, *.cmd, *.bat, *.sh, *.py), pliki binarne, dowiązania, kodowanie inne niż UTF-8,
   [ModuleInitializer], refleksja, kod unsafe i global using w src/. Nie usuwaj i nie wyłączaj testów (także Skip).
   Każda zmiana logiki = nowe testy w NOWYCH plikach testów.
6. Uczciwość statystyczna: nie dopasowuj parametrów do okresu odłożonego (2018–2020.05), nie nadawaj strategiom statusu Validated,
   nie twórz fikcyjnych wyników. Zmiana parametrów strategii musi wynikać z procedury walk-forward opisanej w docs/METODOLOGIA_TESTOW.md.
   Jeżeli dane nie uzasadniają zmiany – NIE zmieniaj kodu, tylko opisz wnioski.
7. Preferuj małe, czytelne zmiany: poprawki błędów, odporność, diagnostykę, testy. Styl kodu jak w otoczeniu, komentarze po polsku.";

        public static string AgentTask(LabJob job, AnalysisReport analysis, bool cliAgent)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Zadanie dla agenta AI – APEX AI Lab");
            sb.AppendLine();
            sb.AppendLine($"Wyzwalacz: {TriggerName(job.Trigger)}{(job.TriggerDetail.Length > 0 ? " – " + job.TriggerDetail : "")}.");
            sb.AppendLine("Cel: przeanalizuj poniższy raport i – jeśli dane to uzasadniają – przygotuj ulepszenie systemu (skuteczność, odporność, diagnostyka),");
            sb.AppendLine("z testami. Jeśli zmiana nie jest uzasadniona, nie zmieniaj plików i wyjaśnij dlaczego.");
            sb.AppendLine();
            sb.AppendLine(Rules);
            sb.AppendLine();
            if (cliAgent)
            {
                sb.AppendLine("## Jak pracować");
                sb.AppendLine("- Zacznij od README.md, docs/METODOLOGIA_TESTOW.md i reports/RAPORT_BADAN.md.");
                sb.AppendLine($"- Pakiety NuGet są już przywrócone: kompiluj `{Providers.ClaudeCodeProvider.BuildCommand}`,");
                sb.AppendLine($"  testuj `{Providers.ClaudeCodeProvider.TestCommand}` (opcjonalnie z `--filter <wyrażenie>`).");
                sb.AppendLine("  Używaj dokładnie tych poleceń – inne warianty (dodatkowe właściwości MSBuild, uruchamianie backtestera) są zablokowane.");
                sb.AppendLine("  Sieć może być niedostępna. Backtest wykonuje AI Lab po Twojej pracy.");
                sb.AppendLine("- Nie wykonuj commitów – AI Lab sam zapisze zmiany na gałęzi zadania.");
                sb.AppendLine("- Na końcu odpowiedz podsumowaniem: pierwsza linia = krótki tytuł zmiany; dalej co, dlaczego, oczekiwany efekt, ryzyka, dodane testy.");
                sb.AppendLine();
            }
            sb.AppendLine("## Raport analizy (dane rzeczywiste z APEX Desktop)");
            sb.AppendLine(analysis.Markdown);
            return sb.ToString();
        }

        /// <summary>Kontekst dla dostawców API: wybrane pliki repozytorium + lista plików.</summary>
        public static string ApiContext(string repoDir, AnalysisReport analysis, int maxChars)
        {
            var sb = new StringBuilder();
            sb.AppendLine("## Struktura repozytorium (wybrane katalogi)");
            foreach (var f in ListFiles(repoDir).Take(400)) sb.AppendLine("- " + f);
            sb.AppendLine();
            var files = new List<string>
            {
                "docs/METODOLOGIA_TESTOW.md", "reports/RAPORT_BADAN.md", "src/Apex.Core/Strategies/StrategyModel.cs",
                "src/Apex.Core/Regime/MarketRegimeDetector.cs", "src/Apex.Core/Scanner/OpportunityScorer.cs",
            };
            // Pliki strategii, których dotyczą wnioski analizy, potem pozostałe strategie.
            foreach (var kv in ChangeAudit.StrategyFiles)
                if (analysis.Markdown.Contains(kv.Key, StringComparison.OrdinalIgnoreCase) && !files.Contains(kv.Value)) files.Add(kv.Value);
            foreach (var f in ChangeAudit.StrategyFiles.Values.Distinct()) if (!files.Contains(f)) files.Add(f);
            files.AddRange(new[] { "src/Apex.Core/Management/PositionManagement.cs", "tests/Apex.Tests/TestSupport.cs", "tests/Apex.Tests/CoreUnitTests.cs" });
            sb.AppendLine("## Zawartość plików");
            foreach (var rel in files)
            {
                string full = Path.Combine(repoDir, rel);
                if (!File.Exists(full)) continue;
                string content = File.ReadAllText(full);
                if (sb.Length + content.Length > maxChars) { sb.AppendLine($"(pominięto {rel} – limit kontekstu)"); continue; }
                sb.AppendLine($"=== ISTNIEJĄCY PLIK: {rel} ===");
                sb.AppendLine(content);
                sb.AppendLine("=== KONIEC ===");
            }
            return sb.ToString();
        }

        public static string RepairTask(string originalSummary, BuildResult? build, TestResult? tests, string repoDir, IEnumerable<string> changedFiles)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Poprawka – Twoja poprzednia zmiana nie przeszła weryfikacji");
            sb.AppendLine(Rules);
            sb.AppendLine();
            sb.AppendLine("## Poprzednie podsumowanie");
            sb.AppendLine(originalSummary);
            if (build != null && !build.Ok)
            {
                sb.AppendLine("## Błędy kompilacji");
                foreach (var e in build.Errors.Take(30)) sb.AppendLine("- " + e);
            }
            if (tests != null && tests.Failed > 0)
            {
                sb.AppendLine("## Niezaliczone testy");
                foreach (var t in tests.FailedNames.Take(30)) sb.AppendLine("- " + t);
            }
            sb.AppendLine("## Aktualna treść zmienionych plików");
            foreach (var rel in changedFiles.Distinct())
            {
                string full = Path.Combine(repoDir, rel);
                if (!File.Exists(full)) continue;
                sb.AppendLine($"=== ISTNIEJĄCY PLIK: {rel} ===");
                sb.AppendLine(File.ReadAllText(full));
                sb.AppendLine("=== KONIEC ===");
            }
            sb.AppendLine("Popraw błędy, zachowując zasady. Jeśli poprawka nie jest możliwa bez łamania zasad – nie zmieniaj plików i wyjaśnij.");
            return sb.ToString();
        }

        public static string ReviewTask(LabJob job, string diff)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Niezależny przegląd zmiany (APEX AI Lab)");
            sb.AppendLine("Oceń poniższą zmianę pod kątem: poprawności, ryzyka finansowego, zgodności z zasadami, nadmiernego dopasowania do historii,");
            sb.AppendLine("wpływu na RiskEngine i bezpieczeństwo. Wypisz konkretne problemy (plik, linia, dlaczego) i werdykt: AKCEPTUJ / ODRZUĆ / WYMAGA POPRAWEK.");
            sb.AppendLine("Nie zmieniaj żadnych plików.");
            sb.AppendLine();
            sb.AppendLine(Rules);
            sb.AppendLine();
            sb.AppendLine("## Podsumowanie autora zmiany");
            sb.AppendLine(job.AgentSummary);
            sb.AppendLine("## Wyniki weryfikacji AI Lab");
            if (job.Build != null) sb.AppendLine("- Kompilacja: " + (job.Build.Ok ? "OK" : "BŁĄD"));
            if (job.Tests != null) sb.AppendLine($"- Testy: {job.Tests.Passed}/{job.Tests.Total}");
            if (job.Backtest != null) sb.AppendLine("- Backtest: " + (job.Backtest.Ran ? job.Backtest.Verdict + " – " + string.Join(" ", job.Backtest.Notes) : job.Backtest.Reason));
            foreach (var f in job.Findings) sb.AppendLine($"- Audyt [{f.Severity}] {f.Rule}: {f.File} {f.Detail}");
            sb.AppendLine("## Diff");
            sb.AppendLine("```diff");
            sb.AppendLine(diff.Length > 120_000 ? diff.Substring(0, 120_000) + "\n… (ucięto)" : diff);
            sb.AppendLine("```");
            return sb.ToString();
        }

        public static string TriggerName(TriggerKind k) => k switch
        {
            TriggerKind.Manual => "uruchomienie ręczne",
            TriggerKind.Schedule => "harmonogram",
            TriggerKind.StrategyDegradation => "pogorszenie wyników strategii",
            TriggerKind.MarketChange => "zmiana warunków rynkowych",
            TriggerKind.ExecutionErrors => "błędy wykonania",
            _ => k.ToString(),
        };

        private static IEnumerable<string> ListFiles(string root)
        {
            foreach (var top in new[] { "src", "tests", "tools", "config", "docs", "reports" })
            {
                string dir = Path.Combine(root, top);
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                    if (rel.Contains("/bin/") || rel.Contains("/obj/") || rel.StartsWith("reports/trades", StringComparison.Ordinal) || rel.EndsWith(".csv", StringComparison.Ordinal)) continue;
                    yield return rel;
                }
            }
        }
    }
}
