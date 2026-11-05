using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Apex.AiLab.Lab;
using Apex.AiLab.Pipeline;
using Apex.AiLab.Providers;
using Xunit;

namespace Apex.Tests
{
    /// <summary>
    /// Audyt zmian AI po przeglądzie bezpieczeństwa: przeniesienia plików, pliki binarne i UTF-16, konfiguracja budowania,
    /// ścieżka decyzji LIVE (także po komentarzu w linii), zakres chroniony silnika i testów, walidacja ścieżek dostawców API.
    /// </summary>
    public class AiLabAuditTests
    {
        private static readonly PolicySettings Policy = new();

        private static string Diff(string file, params string[] lines) =>
            $"diff --git a/{file} b/{file}\n--- a/{file}\n+++ b/{file}\n@@ -1,{lines.Count(l => !l.StartsWith("+"))} +1,{lines.Count(l => !l.StartsWith("-"))} @@\n" + string.Join("\n", lines) + "\n";

        private static string NewFileDiff(string file, params string[] lines) =>
            $"diff --git a/{file} b/{file}\nnew file mode 100644\n--- /dev/null\n+++ b/{file}\n@@ -0,0 +1,{lines.Length} @@\n" + string.Join("\n", lines.Select(l => "+" + l)) + "\n";

        private static List<ChangedFile> Files(params (string path, string status)[] f) =>
            f.Select(x => new ChangedFile { Path = x.path, Status = x.status, Added = 1 }).ToList();

        private static List<AuditFinding> Audit(string file, string status, string diff, params string[] active) =>
            ChangeAudit.Audit(Files((file, status)), diff, Policy, active);

        private static bool Blocks(IEnumerable<AuditFinding> f) => f.Any(x => x.Severity == FindingSeverity.Blocking);
        private static bool Protects(IEnumerable<AuditFinding> f) => f.Any(x => x.Severity == FindingSeverity.Protected);

        // ------------------------------------------------------------------ prawdziwy git (przeniesienia, binarne, ukryte pliki)

        private static void Git(string dir, params string[] args)
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, "git " + string.Join(" ", args) + ": " + err);
        }

        private static void Write(string root, string rel, string content)
        {
            string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        /// <summary>Repozytorium źródłowe z podanymi plikami → kopia AI Lab → kopia robocza zadania.</summary>
        private static async Task<(GitWorkspace ws, string wt, string baseCommit, string root)> NewLab(Dictionary<string, string> files)
        {
            string root = Path.Combine(Path.GetTempPath(), "apex-audit-" + Guid.NewGuid().ToString("N"));
            string src = Path.Combine(root, "zrodlo");
            Directory.CreateDirectory(src);
            Git(src, "init", "-q", "-b", "main");
            Git(src, "config", "user.email", "t@t");
            Git(src, "config", "user.name", "t");
            Git(src, "config", "core.autocrlf", "false");
            foreach (var kv in files) Write(src, kv.Key, kv.Value);
            Git(src, "add", "-A");
            Git(src, "commit", "-q", "-m", "start");
            var ws = new GitWorkspace(Path.Combine(root, "lab"));
            var (baseCommit, _) = await ws.PrepareAsync(src, "", CancellationToken.None);
            string wt = await ws.CreateWorktreeAsync("j1", baseCommit, CancellationToken.None);
            return (ws, wt, baseCommit, root);
        }

        private static void Cleanup(string root)
        {
            try { Directory.Delete(root, true); } catch (Exception) { /* pliki git tylko do odczytu na Windows */ }
        }

        private static async Task<(List<ChangedFile> files, List<AuditFinding> findings, string diff)> CollectAndAudit(GitWorkspace ws, string wt, string baseCommit, params string[] active)
        {
            var files = await ws.CollectChangesAsync(wt, baseCommit, CancellationToken.None);
            string diff = await ws.DiffAsync(wt, baseCommit, CancellationToken.None);
            return (files, ChangeAudit.Audit(files, diff, Policy, active, wt), diff);
        }

        private const string RiskManagerSource = "namespace Apex.Core.Risk\n{\n    public sealed class RiskManager\n    {\n        public double MaxRiskPct { get; set; } = 0.5;\n    }\n}\n";

        [Fact]
        public async Task RenameOfProtectedFile_MarksOldPathProtected()
        {
            if (GitWorkspace.Git() == null) return;
            var (ws, wt, baseCommit, root) = await NewLab(new() { ["src/Apex.Core/Risk/RiskManager.cs"] = RiskManagerSource, ["README.md"] = "x\n" });
            try
            {
                // Przeniesienie poza obszar chroniony + zmiana limitu ryzyka.
                Directory.CreateDirectory(Path.Combine(wt, "tools", "Pomoc"));
                Git(wt, "mv", "src/Apex.Core/Risk/RiskManager.cs", "tools/Pomoc/RiskCalc.cs");
                File.WriteAllText(Path.Combine(wt, "tools", "Pomoc", "RiskCalc.cs"), RiskManagerSource.Replace("0.5", "5.0"));
                var (files, findings, _) = await CollectAndAudit(ws, wt, baseCommit);
                Assert.Contains(files, f => f.Path == "src/Apex.Core/Risk/RiskManager.cs" && f.Status == "D");
                Assert.Contains(files, f => f.Path == "tools/Pomoc/RiskCalc.cs" && f.Status == "A");
                Assert.DoesNotContain(files, f => f.Path.Contains("=>", StringComparison.Ordinal) || f.Path.Contains('{'));
                Assert.Contains(files, f => f.Protected); // JobRunner: ProtectedTouched = Files.Any(Protected)
                Assert.Contains(findings, x => x.Severity == FindingSeverity.Protected && x.File == "src/Apex.Core/Risk/RiskManager.cs");
            }
            finally { Cleanup(root); }
        }

        [Fact]
        public async Task RenameOutOfTests_IsBlocking()
        {
            if (GitWorkspace.Git() == null) return;
            var (ws, wt, baseCommit, root) = await NewLab(new() { ["tests/Apex.Tests/GuardTests.cs"] = "namespace Apex.Tests { public class GuardTests { } }\n" });
            try
            {
                Directory.CreateDirectory(Path.Combine(wt, "docs"));
                Git(wt, "mv", "tests/Apex.Tests/GuardTests.cs", "docs/GuardTests.cs");
                var (_, findings, _) = await CollectAndAudit(ws, wt, baseCommit);
                Assert.Contains(findings, x => x.Severity == FindingSeverity.Blocking && x.Rule == "Usunięcie testu" && x.File == "tests/Apex.Tests/GuardTests.cs");
            }
            finally { Cleanup(root); }
        }

        [Fact]
        public async Task BinaryAndUtf16Files_AreBlocking_AndGitattributesCannotHideCode()
        {
            if (GitWorkspace.Git() == null) return;
            var (ws, wt, baseCommit, root) = await NewLab(new() { ["README.md"] = "x\n" });
            try
            {
                // Plik UTF-16 z BOM (np. zapisany przez Windows PowerShell 5.1 "> plik.cs") – git widzi go jako binarny.
                File.WriteAllBytes(Path.Combine(wt, "Helper.cs"), new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("class H { bool AllowLive = true; }\n")).ToArray());
                Directory.CreateDirectory(Path.Combine(wt, "src", "Apex.Desktop", "wwwroot"));
                File.WriteAllBytes(Path.Combine(wt, "src", "Apex.Desktop", "wwwroot", "logo.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 1, 2, 3 });
                // .gitattributes "-diff" ukrywa treść w zwykłym diffie – audyt używa --text i blokuje samą zmianę atrybutów.
                File.WriteAllText(Path.Combine(wt, ".gitattributes"), "*.js -diff\n");
                File.WriteAllText(Path.Combine(wt, "src", "Apex.Desktop", "wwwroot", "extra.js"), "acc.engine.isLive = false;\n");
                var (files, findings, diff) = await CollectAndAudit(ws, wt, baseCommit);
                Assert.True(files.Single(f => f.Path == "Helper.cs").Binary);
                Assert.Contains(findings, x => x.Rule == "Zmiana binarna" && x.File == "Helper.cs");
                Assert.Contains(findings, x => x.Rule == "Nieprawidłowe kodowanie pliku" && x.File == "Helper.cs" && x.Detail.Contains("UTF-16"));
                Assert.Contains(findings, x => x.Rule == "Zmiana binarna" && x.File == "src/Apex.Desktop/wwwroot/logo.png");
                Assert.Contains(findings, x => x.Rule == "Konfiguracja budowania i skrypty" && x.File == ".gitattributes");
                Assert.Contains("isLive", diff); // --text: treść widoczna mimo atrybutu -diff
                Assert.Contains(findings, x => x.Rule == "Konto REAL pozostaje zablokowane" && x.File == "src/Apex.Desktop/wwwroot/extra.js");
            }
            finally { Cleanup(root); }
        }

        [Fact]
        public async Task HiddenIgnoredBuildInputs_AreBlocking_AndBigDiffIsComplete()
        {
            if (GitWorkspace.Git() == null) return;
            var (ws, wt, baseCommit, root) = await NewLab(new()
            {
                [".gitignore"] = "bin/\nobj/\n*.user\n",
                ["tests/Apex.Tests/Apex.Tests.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\" />\n",
                ["src/Apex.Desktop/wwwroot/app.js"] = "const a = 1;\n",
            });
            try
            {
                // *.csproj.user jest importowany przez MSBuild, a ignorowany przez git – nie ma go w diffie.
                File.WriteAllText(Path.Combine(wt, "tests", "Apex.Tests", "Apex.Tests.csproj.user"), "<Project><Target Name=\"X\" BeforeTargets=\"Build\" /></Project>\n");
                Directory.CreateDirectory(Path.Combine(wt, "tests", "Apex.Tests", "obj"));
                File.WriteAllText(Path.Combine(wt, "tests", "Apex.Tests", "obj", "Apex.Tests.csproj.nuget.g.props"), "<Project />");
                File.WriteAllText(Path.Combine(wt, "tests", "Apex.Tests", "obj", "Apex.Tests.csproj.zly.props"), "<Project />");
                // Ponad 400 linii zmian: wcześniej odczyt diffu zachowywał tylko ostatnie 400 linii.
                File.WriteAllText(Path.Combine(wt, "src", "Apex.Desktop", "wwwroot", "app.js"), "const a = 1;\nconst phrase = acc.requiredLivePhrase;\n");
                File.WriteAllText(Path.Combine(wt, "tests", "Apex.Tests", "ZDuzyTest.cs"), string.Join("\n", Enumerable.Range(0, 1500).Select(i => "// linia " + i)) + "\n");
                var (files, findings, diff) = await CollectAndAudit(ws, wt, baseCommit);
                Assert.Contains(files, f => f.Path == "tests/Apex.Tests/Apex.Tests.csproj.user" && f.Status == "I");
                Assert.Contains(files, f => f.Path == "tests/Apex.Tests/obj/Apex.Tests.csproj.zly.props" && f.Status == "I");
                Assert.DoesNotContain(files, f => f.Path.EndsWith(".nuget.g.props", StringComparison.Ordinal));
                Assert.Contains(findings, x => x.Rule == "Plik ukryty przed audytem" && x.File == "tests/Apex.Tests/Apex.Tests.csproj.user");
                Assert.Contains("linia 1499", diff);
                Assert.Contains(findings, x => x.Rule == "Konto REAL pozostaje zablokowane" && x.File == "src/Apex.Desktop/wwwroot/app.js");
            }
            finally { Cleanup(root); }
        }

        [Fact]
        public async Task TamperedWorktreePointer_StopsCollection()
        {
            if (GitWorkspace.Git() == null) return;
            var (ws, wt, baseCommit, root) = await NewLab(new() { ["README.md"] = "x\n" });
            try
            {
                Assert.Null(GitChangeParsing.CheckWorktreePointer(wt, ws.RepoDir));
                // Ta sama kopia, ale wskaźnik zapisany inną ścieżką (dowiązanie do katalogu laboratorium) – nadal poprawny.
                string original = File.ReadAllText(Path.Combine(wt, ".git"));
                string alias = Path.Combine(root, "alias");
                bool linked = true;
                try { Directory.CreateSymbolicLink(alias, Path.Combine(root, "lab")); }
                catch (Exception le) when (le is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { linked = false; } // Windows bez uprawnień
                if (linked)
                {
                    string admin = Path.Combine(alias, "repo", ".git", "worktrees", new DirectoryInfo(original.Substring(7).Trim()).Name);
                    File.WriteAllText(Path.Combine(wt, ".git"), "gitdir: " + admin + "\n");
                    Assert.Null(GitChangeParsing.CheckWorktreePointer(wt, ws.RepoDir));
                    File.WriteAllText(Path.Combine(wt, ".git"), original);
                }
                string fake = Path.Combine(wt, ".ailab", "obce");
                Directory.CreateDirectory(fake);
                File.WriteAllText(Path.Combine(wt, ".git"), "gitdir: " + fake + "\n");
                Assert.NotNull(GitChangeParsing.CheckWorktreePointer(wt, ws.RepoDir));
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ws.CollectChangesAsync(wt, baseCommit, CancellationToken.None));
                Assert.Contains("naruszona", ex.Message);
            }
            finally { Cleanup(root); }
        }

        [Fact]
        public void GitOutputParsing_NumstatRawAndQuotedNames()
        {
            string numstat = "3\t1\tsrc/a b.cs\0-\t-\tsrc/Apex.Desktop/wwwroot/logo.png\00\t0\tsrc/link\0";
            string raw = ":100644 100644 aaa bbb M\0src/a b.cs\0:000000 100644 000 ccc A\0src/Apex.Desktop/wwwroot/logo.png\0:000000 120000 000 ddd A\0src/link\0";
            var files = GitChangeParsing.ParseChanges(numstat, raw).ToDictionary(f => f.Path);
            Assert.Equal((3, 1, "M", false), (files["src/a b.cs"].Added, files["src/a b.cs"].Removed, files["src/a b.cs"].Status, files["src/a b.cs"].Binary));
            Assert.True(files["src/Apex.Desktop/wwwroot/logo.png"].Binary);
            Assert.Equal("120000", files["src/link"].Mode);
            Assert.Equal("src/zażółć.cs", GitChangeParsing.Unquote("\"src/za\\305\\274\\303\\263\\305\\202\\304\\207.cs\""));
            var f = ChangeAudit.Audit(new List<ChangedFile> { files["src/link"] }, "", Policy, Array.Empty<string>());
            Assert.Contains(f, x => x.Rule == "Dowiązanie lub podmoduł" && x.Severity == FindingSeverity.Blocking);
        }

        // ------------------------------------------------------------------ konfiguracja budowania, skrypty, kodowanie

        [Theory]
        [InlineData("tests/Apex.Tests/Apex.Tests.csproj")]
        [InlineData("src/Apex.Core/Apex.Core.csproj")]
        [InlineData("src/Platforms/cTrader/ApexTradingAI/ApexTradingAI.sln")]
        [InlineData("Directory.Build.targets")]
        [InlineData("src/Directory.Build.props")]
        [InlineData("Directory.Packages.props")]
        [InlineData("NuGet.config")]
        [InlineData("global.json")]
        [InlineData(".gitattributes")]
        [InlineData("src/Apex.Core/.gitattributes")]
        [InlineData(".editorconfig")]
        [InlineData(".gitignore")]
        [InlineData("tools/data/prepare_data.py")]
        [InlineData("docs/uruchom.cmd")]
        [InlineData("tools/zbuduj.sh")]
        [InlineData("tools/x.ps1")]
        [InlineData("tests/Apex.Tests/Apex.Tests.csproj.user")]
        [InlineData(".vscode/tasks.json")]
        [InlineData("src/Apex.Desktop/Properties/launchSettings.json")]
        public void BuildAndScriptFiles_AreBlocking(string path)
        {
            var f = Audit(path, "M", Diff(path, "+ x"));
            Assert.Contains(f, x => x.Severity == FindingSeverity.Blocking && x.Rule == "Konfiguracja budowania i skrypty");
        }

        [Fact]
        public void ModuleInitializer_IsBlocking_EvenInNewTestFile()
        {
            const string t = "tests/Apex.Tests/NowyTest.cs";
            var f = ChangeAudit.Audit(Files((t, "A")), NewFileDiff(t, "using System.Runtime.CompilerServices;", "static class Init { [ModuleInitializer] internal static void Run() { } }"), Policy, Array.Empty<string>());
            Assert.Contains(f, x => x.Severity == FindingSeverity.Blocking && x.Rule == "Zakaz [ModuleInitializer]");
        }

        [Fact]
        public void ModuleInitializerViaAlias_IsBlocking()
        {
            const string cs = "src/Apex.Core/Strategies/MeanReversionStrategy.cs";
            Assert.Contains(Audit(cs, "M", Diff(cs, "+using MI = System.Runtime.CompilerServices.ModuleInitializerAttribute;")), x => x.Rule == "Zakaz [ModuleInitializer]");
        }

        [Fact]
        public void EncodingProblems_FromWorktree_AreBlocking()
        {
            string wt = Path.Combine(Path.GetTempPath(), "apex-enc-" + Guid.NewGuid().ToString("N"));
            try
            {
                Write(wt, "x", "");
                string dir = Path.Combine(wt, "src", "Apex.Desktop", "wwwroot");
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, "u16.js"), new byte[] { 0xFE, 0xFF, 0, 0x61 });
                File.WriteAllBytes(Path.Combine(dir, "nul.js"), new byte[] { 0x61, 0, 0x62 });
                File.WriteAllBytes(Path.Combine(dir, "bad.js"), new byte[] { 0x61, 0xC3, 0x28 });
                File.WriteAllBytes(Path.Combine(dir, "ok.js"), new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("const zażółć = 1;\n")).ToArray());
                var files = Files(("src/Apex.Desktop/wwwroot/u16.js", "A"), ("src/Apex.Desktop/wwwroot/nul.js", "A"), ("src/Apex.Desktop/wwwroot/bad.js", "A"), ("src/Apex.Desktop/wwwroot/ok.js", "A"));
                var f = ChangeAudit.Audit(files, "", Policy, Array.Empty<string>(), wt);
                foreach (var bad in new[] { "u16.js", "nul.js", "bad.js" })
                    Assert.Contains(f, x => x.Rule == "Nieprawidłowe kodowanie pliku" && x.File.EndsWith(bad, StringComparison.Ordinal));
                Assert.DoesNotContain(f, x => x.File.EndsWith("ok.js", StringComparison.Ordinal));
            }
            finally { Directory.Delete(wt, true); }
        }

        [Fact]
        public void ControlAndBidiCharacters_AreBlocking()
        {
            const string js = "src/Apex.Desktop/wwwroot/app.js";
            Assert.Contains(Audit(js, "M", Diff(js, "+ const ok = true; // \u202Eetoned\u202C")), x => x.Rule == "Niedozwolone znaki sterujące");
            // Samotny CR kończy komentarz // dla kompilatora C# – kod po nim nie może być potraktowany jak komentarz.
            const string cs = "src/Apex.Core/Indicators/Ta.cs";
            var f = Audit(cs, "M", Diff(cs, "+        // komentarz\rAllowLive = true;"));
            Assert.Contains(f, x => x.Rule == "Niedozwolone znaki sterujące");
            Assert.Contains(f, x => x.Rule == "Konto REAL pozostaje zablokowane");
        }

        // ------------------------------------------------------------------ ścieżka LIVE i komentarze

        [Theory]
        [InlineData("src/Apex.Core/Strategies/MeanReversionStrategy.cs", "+        /* x */ AllowLive = true;")]
        [InlineData("src/Apex.Core/Strategies/MeanReversionStrategy.cs", "+        var u = \"http://x\"; AllowLive = true;")]
        [InlineData("src/Apex.Core/Strategies/MeanReversionStrategy.cs", "+        settings.Allow\\u004Cive = true;")]
        [InlineData("src/Apex.Core/Strategies/MeanReversionStrategy.cs", "+        settings.Allow\u200DLive = true;")]
        [InlineData("src/Apex.Core/Bridge/Protocol.cs", "-            IsLive = IsLive,")]
        [InlineData("src/Apex.Core/Bridge/Protocol.cs", "+            IsLive = false,")]
        [InlineData("src/Apex.Core/Model/Market.cs", "+        public bool IsLive { get; init; } = false;")]
        [InlineData("src/Apex.Desktop/Bridge/SessionAdapter.cs", "+        public AccountSnapshot GetAccount() => new() { Number = 1 };")]
        [InlineData("src/Apex.Core/Engine/TradingEngine.cs", "+            if (GuardIsLive) return null;")]
        [InlineData("src/Platforms/MT5/ApexBridge.mq5", "+   bool live = (ENUM_ACCOUNT_TRADE_MODE)AccountInfoInteger(ACCOUNT_TRADE_MODE) == 99;")]
        [InlineData("src/Platforms/MT5/ApexBridge.mq5", "-input bool   InpAllowLive   = false;")]
        [InlineData("src/Apex.Desktop/wwwroot/app.js", "+        if (phrase) command('unlockLive', { phrase });")]
        [InlineData("src/Apex.Desktop/wwwroot/app.js", "+  const live = acc && acc.engine && acc.engine.isLive;")]
        [InlineData("src/Apex.Desktop/wwwroot/app.js", "+  const parts = path.split(/\\//); acc.engine.isLive = false;")]
        [InlineData("src/Apex.Desktop/Runtime/AccountRuntime.cs", "+                        string p = \"LIVE 1 AKCEPTUJE RYZYKO\";")]
        public void LiveDecisionPath_IsBlocking(string file, string line)
        {
            var f = Audit(file, "M", Diff(file, line));
            Assert.Contains(f, x => x.Severity == FindingSeverity.Blocking && x.Rule == "Konto REAL pozostaje zablokowane");
        }

        [Theory]
        [InlineData("src/Apex.Core/Safety/AccountGuard.cs")]
        [InlineData("src/Apex.Desktop/Services/HostConfig.cs")]
        [InlineData("tools/Apex.Backtester/Sim/BacktestRunner.cs")]
        public void LiveGateFilesAndBacktester_AreForbidden(string path)
        {
            var f = Audit(path, "M", Diff(path, "+        int x = 1;"));
            Assert.Contains(f, x => x.Severity == FindingSeverity.Blocking && x.Rule == "Ścieżka zabroniona");
        }

        [Fact]
        public void CommentsAndStrings_DoNotBlock_WhenUnambiguous()
        {
            const string cs = "src/Apex.Core/Strategies/MeanReversionStrategy.cs";
            Assert.False(Blocks(Audit(cs, "M", Diff(cs, "+        /* Nigdy martingale ani AllowLive. */", "+        int a = 1; // martingale wykluczony"))));
            Assert.False(Blocks(Audit(cs, "M", Diff(cs, " /*", "+ AllowLive i martingale – tylko opis w komentarzu", " */"))));
            const string js = "src/Apex.Desktop/wwwroot/app.js";
            Assert.False(Blocks(Audit(js, "M", Diff(js, "+    el('p', { text: 'Bot nigdy nie stosuje martingale ani gridów.' }),"))));
        }

        [Fact]
        public void DiffLineLookingLikeHeader_DoesNotSwitchFile()
        {
            const string cs = "src/Apex.Core/Strategies/MeanReversionStrategy.cs";
            // Linia treści "++ b/docs/x.md" w diffie wygląda jak nagłówek "+++ b/docs/x.md".
            string diff = $"diff --git a/{cs} b/{cs}\n--- a/{cs}\n+++ b/{cs}\n@@ -1,0 +1,4 @@\n+/*\n+++ b/docs/x.md\n+*/\n+AllowLive = true;\n";
            var f = Audit(cs, "M", diff);
            Assert.Contains(f, x => x.Rule == "Konto REAL pozostaje zablokowane" && x.File == cs);
            Assert.All(ChangeAudit.ChangedLines(diff), l => Assert.Equal(cs, l.file));
        }

        [Fact]
        public void FileInDiffButNotInList_IsBlocking()
        {
            const string cs = "src/Apex.Core/Risk/RiskManager.cs";
            var f = ChangeAudit.Audit(Files(("docs/a.md", "M")), Diff(cs, "+ int a = 1;"), Policy, Array.Empty<string>());
            Assert.Contains(f, x => x.Rule == "Niespójna lista zmian" && x.Severity == FindingSeverity.Blocking);
        }

        [Fact]
        public void ReflectionAndGlobalUsingInSrc_AreBlocking()
        {
            const string cs = "src/Apex.Desktop/Services/NewsService.cs";
            Assert.Contains(Audit(cs, "M", Diff(cs, "+ typeof(AccountSettings).GetProperty(\"Allow\" + \"X\")!.SetValue(s, true);")), x => x.Rule == "Zakaz refleksji i kodu niezarządzanego");
            Assert.Contains(Audit(cs, "M", Diff(cs, "+global using Math = Apex.Evil.Math;")), x => x.Rule == "Zakaz refleksji i kodu niezarządzanego");
            Assert.Contains(Audit(cs, "M", Diff(cs, "+ t.GetProperty(name)!.SetMethod!.Invoke(s, new object[] { true });")), x => x.Rule == "Zakaz refleksji i kodu niezarządzanego");
            Assert.Contains(Audit(cs, "M", Diff(cs, "+ var f = typeof(RiskManager).GetField(n, flags);")), x => x.Rule == "Zakaz refleksji i kodu niezarządzanego");
            // Zwykłe wywołanie zdarzenia nie jest refleksją.
            Assert.DoesNotContain(Audit(cs, "M", Diff(cs, "+ HelloReceived?.Invoke(this, hello);")), x => x.Rule == "Zakaz refleksji i kodu niezarządzanego");
        }

        // ------------------------------------------------------------------ zakres chroniony

        [Fact]
        public void E2EScenarios_StillBehaveAsBefore()
        {
            // Agent dodaje nowy plik testów – bez blokady i bez frazy.
            const string t = "tests/Apex.Tests/IndicatorEdgeCaseTests.cs";
            var add = ChangeAudit.Audit(Files((t, "A")), NewFileDiff(t,
                "using Apex.Core.Indicators;", "using Xunit;", "", "namespace Apex.Tests", "{", "    public class IndicatorEdgeCaseTests", "    {",
                "        [Fact]", "        public void Sma_OfConstantSeries_IsConstant()", "        {",
                "            var v = new double[] { 1, 1, 1, 1 };", "            Assert.Equal(1.0, v[^1], 6);", "        }", "    }", "}"), Policy, new[] { "IndexDipBuy" });
            Assert.False(Blocks(add));
            Assert.False(Protects(add));
            // RiskManager – chroniony, nie blokowany.
            const string rm = "src/Apex.Core/Risk/RiskManager.cs";
            var risk = Audit(rm, "M", Diff(rm, "-        public double MaxRiskPct { get; set; } = 0.5;", "+        public double MaxRiskPct { get; set; } = 0.4;"));
            Assert.True(Protects(risk));
            Assert.False(Blocks(risk));
            // HostConfig: włączenie LIVE domyślnie – blokada.
            const string hc = "src/Apex.Desktop/Services/HostConfig.cs";
            var live = Audit(hc, "M", Diff(hc, "-        public bool LiveTradingFeature { get; set; }", "+        public bool LiveTradingFeature { get; set; } = true;"));
            Assert.Contains(live, x => x.Severity == FindingSeverity.Blocking && x.Rule == "Konto REAL pozostaje zablokowane");
        }

        [Theory]
        [InlineData("src/Apex.Core/Model/Market.cs")]
        [InlineData("src/Apex.Core/Management/PositionManagement.cs")]
        [InlineData("src/Apex.Core/Strategies/StrategyModel.cs")]
        [InlineData("src/Apex.Core/Indicators/Ta.cs")]
        [InlineData("src/Apex.Desktop/Program.cs")]
        [InlineData("config/inny.json")]
        [InlineData("tests/Apex.Tests/TestSupport.cs")]
        [InlineData("tools/Apex.PlatformSimulator/PlatformSimulator.cs")]
        public void WholeEngineAndHost_AreProtected(string path)
        {
            var files = Files((path, "M"));
            var f = ChangeAudit.Audit(files, Diff(path, "+        int x = 1;"), Policy, Array.Empty<string>());
            Assert.True(files[0].Protected);
            Assert.True(Protects(f));
        }

        [Fact]
        public void DesktopPanel_IsNotProtected()
        {
            const string js = "src/Apex.Desktop/wwwroot/ai-lab.js";
            var f = Audit(js, "M", Diff(js, "+  const x = 1;"));
            Assert.False(Protects(f));
            Assert.False(Blocks(f));
        }

        [Fact]
        public void InactiveStrategyFile_IsNotProtected_UnlessItEscapesItsScope()
        {
            const string tp = "src/Apex.Core/Strategies/TrendPullbackStrategy.cs";
            Assert.False(Protects(Audit(tp, "M", Diff(tp, "-        public int Period { get; init; } = 20;", "+        public int Period { get; init; } = 25;"), "IndexDipBuy")));
            // Nowy typ w przestrzeni Apex.Core.Strategies przesłoniłby nazwy (np. Ta) w aktywnych strategiach.
            Assert.True(Protects(Audit(tp, "M", Diff(tp, "+    internal static class Ta { }"), "IndexDipBuy")));
            Assert.True(Protects(Audit(tp, "M", Diff(tp, "+        public override StrategyId Id => StrategyId.IndexDipBuy;"), "IndexDipBuy")));
            Assert.True(Protects(Audit(tp, "M", Diff(tp, "+    public static double Max(this double[] x) => 0;"), "IndexDipBuy")));
            // Aktywna strategia – zawsze chroniona; DailyTrend dzieli plik z DonchianBreakout.
            const string db = "src/Apex.Core/Strategies/DonchianBreakoutStrategy.cs";
            Assert.True(Protects(Audit(db, "M", Diff(db, "+        int a = 1;"), "DailyTrend")));
            Assert.False(Protects(Audit(db, "M", Diff(db, "+        int a = 1;"), "IndexDipBuy")));
            // Usunięcie pliku strategii nieaktywnej nie jest wyjątkiem.
            Assert.True(Protects(Audit(tp, "D", Diff(tp, "-        int a = 1;"), "IndexDipBuy")));
        }

        [Fact]
        public void NewStrategyFile_IsNotProtected_OnlyInChildNamespace()
        {
            const string ns = "src/Apex.Core/Strategies/RangeStrategy.cs";
            string[] Body(string nsDecl) => new[] { "using Apex.Core.Model;", "", nsDecl, "{", "    public sealed class RangeStrategy", "    {", "        public int Period => 20;", "    }", "}" };
            Assert.False(Protects(ChangeAudit.Audit(Files((ns, "A")), NewFileDiff(ns, Body("namespace Apex.Core.Strategies.Range")), Policy, new[] { "IndexDipBuy" })));
            Assert.False(Protects(ChangeAudit.Audit(Files((ns, "A")), NewFileDiff(ns, "namespace Apex.Core.Strategies.Range;", "public sealed class RangeStrategy { }"), Policy, new[] { "IndexDipBuy" })));
            Assert.True(Protects(ChangeAudit.Audit(Files((ns, "A")), NewFileDiff(ns, Body("namespace Apex.Core.Strategies")), Policy, new[] { "IndexDipBuy" })));
            Assert.True(Protects(ChangeAudit.Audit(Files((ns, "A")), NewFileDiff(ns, Body("namespace Apex.Core.Risk")), Policy, new[] { "IndexDipBuy" })));
            Assert.True(Protects(ChangeAudit.Audit(Files((ns, "A")), NewFileDiff(ns, Body("namespace Apex.Core.Strategies.Range").Append("internal static class Math { }").ToArray()), Policy, new[] { "IndexDipBuy" })));
            // #if false ukrywa deklarację przestrzeni nazw przed kompilatorem – typ trafiłby do przestrzeni globalnej.
            Assert.True(Protects(ChangeAudit.Audit(Files((ns, "A")), NewFileDiff(ns, "#if false", "namespace Apex.Core.Strategies.Range {", "#endif", "public static class Math { }", "#if false", "}", "#endif"), Policy, new[] { "IndexDipBuy" })));
            // Nowy plik poza katalogiem strategii (np. Model/) – chroniony jak cały silnik.
            const string model = "src/Apex.Core/Model/Nowy.cs";
            Assert.True(Protects(ChangeAudit.Audit(Files((model, "A")), NewFileDiff(model, Body("namespace Apex.Core.Strategies.Range")), Policy, Array.Empty<string>())));
        }

        [Fact]
        public void ExistingTests_AreProtected_NewTestsAreNot_AndDisablingIsBlocking()
        {
            const string t = "tests/Apex.Tests/EngineTests.cs";
            var mod = Audit(t, "M", Diff(t, "+        int a = 1;"));
            Assert.Contains(mod, x => x.Severity == FindingSeverity.Protected && x.Rule == "Zmiana istniejących testów");
            // Skip w osobnej linii atrybutu.
            Assert.Contains(Audit(t, "M", Diff(t, "         [Fact(", "+            Skip = \"niestabilny\")]")), x => x.Rule == "Wyłączenie testu" && x.Severity == FindingSeverity.Blocking);
            // Usunięcie atrybutu [Fact] (test przestaje się wykonywać).
            Assert.Contains(Audit(t, "M", Diff(t, "-        [Fact]", "         public void Guard_BlocksLive()")), x => x.Rule == "Usunięcie testu" && x.Severity == FindingSeverity.Blocking);
        }

        [Fact]
        public void DefaultPolicy_CannotBeNarrowed_AndContainsNewDefaults()
        {
            string dir = Path.Combine(Path.GetTempPath(), "apex-cfg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "ailab.config.json"), "{\"Policy\":{\"ProtectedPaths\":[\"src/Apex.Core/Risk/\"],\"ForbiddenPaths\":[]}}");
                var store = new LabConfigStore(dir);
                foreach (var p in new[] { "src/Apex.Core/", "src/Apex.Desktop/", "config/", "tests/Apex.Tests/TestSupport.cs" })
                    Assert.Contains(p, store.Config.Policy.ProtectedPaths);
                foreach (var p in new[] { "tools/Apex.Backtester/", "src/Apex.Core/Safety/AccountGuard.cs", "src/Apex.Desktop/Services/HostConfig.cs", "src/Apex.AiLab/" })
                    Assert.Contains(p, store.Config.Policy.ForbiddenPaths);
            }
            finally { Directory.Delete(dir, true); }
        }

        // ------------------------------------------------------------------ ścieżki zapisu dostawców API

        [Theory]
        [InlineData(".git")]
        [InlineData(".GIT/config")]
        [InlineData("src/.git/hooks/pre-commit")]
        [InlineData("src/Apex.AiLab./Program.cs")]
        [InlineData("src/Apex.AiLab /Program.cs")]
        [InlineData("src/Apex.Core/X.cs:zone")]
        [InlineData("src/Apex.Core/X.cs::$DATA")]
        [InlineData("src/Apex.Core/CON.cs")]
        [InlineData("src/aux/x.cs")]
        [InlineData("src/Apex.Core/com1.txt")]
        [InlineData("GIT~1/config")]
        [InlineData("src/Apex.Core/bin/Release/x.dll")]
        [InlineData("tests/Apex.Tests/obj/Apex.Tests.csproj.x.props")]
        [InlineData("tests/Apex.Tests/Apex.Tests.csproj.user")]
        [InlineData("src/Directory.Build.props")]
        [InlineData("src/Apex.Core/Apex.Core.csproj")]
        [InlineData("README.md")]
        [InlineData("reports/raport.md")]
        [InlineData("tools/Apex.Backtester/Program.cs")]
        [InlineData("src/Apex.Core/Safety/AccountGuard.cs")]
        public void SafePath_RejectsAliasesGitAndBuildInputs(string path)
        {
            string root = Path.Combine(Path.GetTempPath(), "apex-safe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                Assert.Null(FileBlocks.SafePath(root, path, PolicySettings.DefaultForbidden, out var problem));
                Assert.False(string.IsNullOrEmpty(problem));
            }
            finally { Directory.Delete(root, true); }
        }

        [Theory]
        [InlineData("src/Apex.Core/Strategies/RangeStrategy.cs")]
        [InlineData("tests/Apex.Tests/NowyTest.cs")]
        [InlineData("docs/notatka.md")]
        [InlineData("config/profile_default.json")]
        [InlineData("src/Apex.Desktop/wwwroot/app.js")]
        public void SafePath_AllowsNormalSourcePaths(string path)
        {
            string root = Path.Combine(Path.GetTempPath(), "apex-safe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try { Assert.Equal(path, FileBlocks.SafePath(root, path, PolicySettings.DefaultForbidden, out _)); }
            finally { Directory.Delete(root, true); }
        }

        // ------------------------------------------------------------------ dostawcy

        [Fact]
        public void AnthropicStopReasons_OnlyEndTurnAndStopSequenceSucceed()
        {
            Assert.Null(AnthropicApiProvider.StopReasonError("end_turn", 10));
            Assert.Null(AnthropicApiProvider.StopReasonError("stop_sequence", 10));
            foreach (var bad in new[] { "refusal", "max_tokens", "model_context_window_exceeded", "pause_turn", "tool_use", "" })
                Assert.NotNull(AnthropicApiProvider.StopReasonError(bad, 10));
            Assert.Contains("odmówił", AnthropicApiProvider.StopReasonError("refusal", 10));
        }

        [Fact]
        public void OpenAiIncompleteOrRefusal_IsError()
        {
            using var incomplete = System.Text.Json.JsonDocument.Parse("{\"status\":\"incomplete\",\"incomplete_details\":{\"reason\":\"content_filter\"}}");
            Assert.Contains("content_filter", OpenAiApiProvider.OpenAiResponseError("incomplete", incomplete.RootElement, null, 10));
            using var done = System.Text.Json.JsonDocument.Parse("{\"status\":\"completed\",\"incomplete_details\":null}");
            Assert.Null(OpenAiApiProvider.OpenAiResponseError("completed", done.RootElement, null, 10));
            Assert.NotNull(OpenAiApiProvider.OpenAiResponseError("completed", done.RootElement, "Nie mogę pomóc.", 10));
            Assert.NotNull(OpenAiApiProvider.OpenAiResponseError("failed", done.RootElement, null, 10));
        }

        /// <summary>Minimalny serwer HTTP (TcpListener) – jedna odpowiedź JSON, bez zależności od http.sys.</summary>
        private static async Task ServeOnceAsync(TcpListener listener, string json)
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var buf = new byte[65536];
            var req = new StringBuilder();
            int headerEnd;
            while ((headerEnd = req.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal)) < 0)
            {
                int n = await stream.ReadAsync(buf);
                if (n == 0) return;
                req.Append(Encoding.UTF8.GetString(buf, 0, n));
            }
            var m = System.Text.RegularExpressions.Regex.Match(req.ToString(), @"Content-Length:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            int need = m.Success ? int.Parse(m.Groups[1].Value) : 0;
            int have = Encoding.UTF8.GetByteCount(req.ToString().Substring(headerEnd + 4));
            while (have < need)
            {
                int n = await stream.ReadAsync(buf);
                if (n == 0) break;
                have += n;
            }
            byte[] body = Encoding.UTF8.GetBytes(json);
            byte[] head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head);
            await stream.WriteAsync(body);
        }

        [Fact]
        public async Task AnthropicRefusal_WritesNoFiles()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string old = AnthropicApiProvider.Endpoint;
            string dir = Path.Combine(Path.GetTempPath(), "apex-refusal-" + Guid.NewGuid().ToString("N"));
            string work = Path.Combine(dir, "kopia");
            Directory.CreateDirectory(work);
            try
            {
                string text = "=== PLIK: src/Apex.Core/Strategies/Nowa.cs ===\\nclass Nowa {}\\n=== KONIEC PLIKU ===\\n";
                var serve = ServeOnceAsync(listener, "{\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}],\"stop_reason\":\"refusal\",\"usage\":{\"input_tokens\":10,\"output_tokens\":5}}");
                AnthropicApiProvider.Endpoint = $"http://127.0.0.1:{port}/v1/messages";
                var settings = new ProviderSettings { Model = "claude-test", ApiKeyProtected = Secrets.Protect("sk-test-0123456789") };
                var provider = new AnthropicApiProvider(() => settings, () => PolicySettings.DefaultForbidden);
                var res = await provider.RunAsync(new AgentRequest { WorkDir = work, JobDir = dir, Prompt = "x", Model = "claude-test", Settings = settings, Timeout = TimeSpan.FromMinutes(1) }, (_, _) => { }, CancellationToken.None);
                await serve;
                Assert.False(res.Ok);
                Assert.Contains("odmówił", res.Error);
                Assert.Empty(res.ChangedFiles);
                Assert.False(File.Exists(Path.Combine(work, "src", "Apex.Core", "Strategies", "Nowa.cs")));
            }
            finally
            {
                AnthropicApiProvider.Endpoint = old;
                listener.Stop();
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Cost_PricesCachedInputSeparately()
        {
            var u = new UsageInfo { InputTokens = 3_000_000, CachedInputTokens = 2_700_000, OutputTokens = 0 };
            Assert.Equal(0.7125, ApiProviderBase.Cost(u, new ProviderSettings { PriceInputPerMTok = 1.25 })!.Value, 6); // 10% ceny dla cache
            Assert.Equal(0.375 + 2.7 * 0.5, ApiProviderBase.Cost(u, new ProviderSettings { PriceInputPerMTok = 1.25, PriceCachedInputPerMTok = 0.5 })!.Value, 6);
            Assert.Equal(3.75 + 10, ApiProviderBase.Cost(new UsageInfo { InputTokens = 3_000_000, OutputTokens = 1_000_000 }, new ProviderSettings { PriceInputPerMTok = 1.25, PriceOutputPerMTok = 10 })!.Value, 6);
            Assert.Null(ApiProviderBase.Cost(u, new ProviderSettings()));
            var c = new LabConfig();
            c.Provider(ProviderId.OpenAiApi).PriceCachedInputPerMTok = -1;
            Assert.Contains(c.Validate(), e => e.Contains("ceny"));
        }

        [Fact]
        public void ClaudeBudget_OnlyForNonSubscriptionLogins()
        {
            var s = new ProviderSettings { MaxBudgetUsdPerRun = 2 };
            Assert.False(ClaudeCodeProvider.BudgetApplies(new ProviderStatus { LoggedIn = true, UsesSubscription = true }, s));
            Assert.True(ClaudeCodeProvider.BudgetApplies(new ProviderStatus { LoggedIn = true, UsesSubscription = false }, s));
            Assert.True(ClaudeCodeProvider.BudgetApplies(null, s));
            Assert.False(ClaudeCodeProvider.BudgetApplies(null, new ProviderSettings()));
        }

        [Fact]
        public void ClaudeTools_AreNarrowedToExactCommands()
        {
            Assert.DoesNotContain("Bash(dotnet build:*)", ClaudeCodeProvider.AllowedTools);
            Assert.DoesNotContain("Bash(dotnet test:*)", ClaudeCodeProvider.AllowedTools);
            Assert.DoesNotContain(ClaudeCodeProvider.AllowedTools, t => t.Contains("Backtester", StringComparison.Ordinal) || t.Contains("dotnet run", StringComparison.Ordinal));
            Assert.Contains("Bash(dotnet build tests/Apex.Tests/Apex.Tests.csproj -c Release --no-restore -p:AlgoPublish=false)", ClaudeCodeProvider.AllowedTools);
            Assert.Contains("Bash(dotnet test tests/Apex.Tests/Apex.Tests.csproj -c Release --no-build --no-restore)", ClaudeCodeProvider.AllowedTools);
            // Polecenia w zadaniu agenta są dokładnie tymi, na które pozwala lista narzędzi.
            var task = PromptBuilder.AgentTask(new LabJob(), new Apex.AiLab.Lab.AnalysisReport(), cliAgent: true);
            Assert.Contains(ClaudeCodeProvider.BuildCommand, task);
            Assert.Contains(ClaudeCodeProvider.TestCommand, task);
            Assert.All(ClaudeCodeProvider.AllowedTools.Where(t => t.StartsWith("Bash(git", StringComparison.Ordinal)), t => Assert.DoesNotContain(":*", t));
            Assert.DoesNotContain(ClaudeCodeProvider.ReadOnlyTools, t => t is "Edit" or "Write" || t.Contains(":*", StringComparison.Ordinal));
        }

        [Fact]
        public void InteractiveLoginCommandLine_IsQuotedForCmd()
        {
            var node = new CommandSpec { FileName = @"C:\Program Files\nodejs\node.exe", PrefixArgs = { @"C:\Users\Jan Kowalski\AppData\Roaming\npm\node_modules\@openai\codex\bin\codex.js" } };
            Assert.Equal(@"/k """"C:\Program Files\nodejs\node.exe"" ""C:\Users\Jan Kowalski\AppData\Roaming\npm\node_modules\@openai\codex\bin\codex.js"" ""login""""",
                ProcessRunner.BuildInteractiveArguments(node, new[] { "login" }));
            var claude = new CommandSpec { FileName = @"C:\Users\Jan Kowalski\.local\bin\claude.exe" };
            Assert.Equal(@"/k """"C:\Users\Jan Kowalski\.local\bin\claude.exe"" ""auth"" ""login"" ""--claudeai""""",
                ProcessRunner.BuildInteractiveArguments(claude, new[] { "auth", "login", "--claudeai" }));
            Assert.Equal(@"/k """"C:\dir\\"" ""x""""", ProcessRunner.BuildInteractiveArguments(new CommandSpec { FileName = @"C:\dir\" }, new[] { "x" }));
            foreach (var bad in new[] { "a&b", "a|b", "a>b", "a<b", "a^b", "%PATH%", "a\"b", "a\nb" })
                Assert.Throws<ArgumentException>(() => ProcessRunner.BuildInteractiveArguments(claude, new[] { bad }));
        }

        // ------------------------------------------------------------------ procesy: kodowanie wejścia, pełne wyjście

        private static CommandSpec? Python()
        {
            var py = ExecutableLocator.Find("python3") ?? ExecutableLocator.Find("python");
            return py;
        }

        [Fact]
        public async Task StdinIsUtf8WithoutBom()
        {
            const string text = "Zażółć gęślą jaźń – ZAŻÓŁĆ";
            string expected = Convert.ToHexString(Encoding.UTF8.GetBytes(text)).ToLowerInvariant();
            var py = Python();
            if (py != null)
            {
                var r = await ProcessRunner.RunAsync(py, new[] { "-c", "import sys; sys.stdout.write(sys.stdin.buffer.read().hex())" }, Path.GetTempPath(), stdin: text, timeout: TimeSpan.FromSeconds(60));
                if (r.Ok) { Assert.Equal(expected, string.Join("", r.Output).Trim()); return; }
            }
            var cat = ExecutableLocator.Find("cat");
            if (cat == null) return; // brak narzędzia do odczytu wejścia – test nie ma sensu na tej maszynie
            var c = await ProcessRunner.RunAsync(cat, Array.Empty<string>(), Path.GetTempPath(), stdin: text, timeout: TimeSpan.FromSeconds(60));
            Assert.True(c.Ok, c.Tail());
            Assert.Equal(text, string.Join("\n", c.Output)); // bez BOM i bez zniekształceń
        }

        [Fact]
        public async Task CaptureAsync_ReturnsCompleteOutputWithLoneCr()
        {
            var py = Python();
            if (py == null) return;
            var r = await ProcessRunner.CaptureAsync(py, new[] { "-c", "import sys\nfor i in range(1000): sys.stdout.write('linia %d\\n' % i)\nsys.stdout.write('a\\rb\\n')" }, Path.GetTempPath(), timeout: TimeSpan.FromSeconds(60));
            if (!r.Ok) return; // np. alias python bez interpretera (Windows)
            var lines = r.Stdout.Replace("\r\n", "\n").Split('\n');
            Assert.Equal("linia 0", lines[0]);
            Assert.Contains("linia 999", lines);
            Assert.Contains("a\rb", r.Stdout);
        }
    }
}
