using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apex.AiLab.Deploy;
using Apex.AiLab.Lab;
using Apex.AiLab.Pipeline;
using Xunit;

namespace Apex.Tests
{
    /// <summary>
    /// Wdrożenia AI Lab na prawdziwym git (bez dotnet publish – budowa wydania podstawiona): wydanie = aktywne wydanie + dokładnie
    /// zatwierdzona, zaudytowana zmiana; zmiany wycofane, odrzucone i nieudane nigdy nie wracają; nieudane wdrożenie przywraca status
    /// i gałąź wdrożeń; wycofanie pomija wydania już wycofane.
    /// </summary>
    public class AiLabDeployTests
    {
        private static readonly CancellationToken None = CancellationToken.None;

        private static string Git(string dir, params string[] args)
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, "git " + string.Join(" ", args) + ": " + err.Result);
            return output.Trim();
        }

        private static bool GitOk(string dir, params string[] args)
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0;
        }

        /// <summary>Budowa wydania bez dotnet: zapisuje „exe” i kopię plików *.txt z kopii wdrożeń (żeby sprawdzić, co trafiło do wydania).</summary>
        private sealed class FakeBuilder : IReleaseBuilder
        {
            public bool FailBuild;
            public bool FailTests;
            public bool FailPublish;
            public Exception? ThrowOnVerify;
            public TaskCompletionSource<bool>? HoldVerify;
            public TaskCompletionSource<bool> VerifyStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Verifies;
            public int Publishes;

            public async Task<(BuildResult build, TestResult? tests)> VerifyAsync(string dir, string resultsDir, Action<string, string> log, CancellationToken ct)
            {
                Verifies++;
                VerifyStarted.TrySetResult(true);
                if (HoldVerify != null) await HoldVerify.Task.ConfigureAwait(false);
                if (ThrowOnVerify != null) throw ThrowOnVerify;
                if (FailBuild) return (new BuildResult { Ok = false, Errors = { "X.cs(1,1): error CS0001: test" } }, null);
                return (new BuildResult { Ok = true, Steps = { "test: OK" } }, new TestResult { Ran = true, Total = 3, Passed = FailTests ? 2 : 3, Failed = FailTests ? 1 : 0, FailedNames = FailTests ? new List<string> { "Test.X" } : new List<string>() });
            }

            public Task<ReleaseBuildOutput> PublishAsync(string dir, string outDir, IReadOnlyList<ChangedFile> shippedFiles, Action<string, string> log, CancellationToken ct)
            {
                Publishes++;
                if (FailPublish) return Task.FromResult(new ReleaseBuildOutput { Error = "Budowa wydania nieudana: test" });
                string exe = Path.Combine(outDir, "APEX-Desktop", "ApexDesktop");
                Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
                File.WriteAllText(exe, "exe");
                foreach (var f in Directory.GetFiles(dir, "*.txt")) File.Copy(f, Path.Combine(outDir, Path.GetFileName(f)));
                return Task.FromResult(new ReleaseBuildOutput { ExePath = exe });
            }
        }

        private sealed class Lab : IDisposable
        {
            public string Root = "";
            public string Src = "";
            public string BaseCommit = "";
            public LabConfigStore Cfg = null!;
            public LabStore Store = null!;
            public GitWorkspace Ws = null!;
            public Deployer Deployer = null!;
            public FakeBuilder Builder = new();

            public static async Task<Lab> CreateAsync()
            {
                var lab = new Lab { Root = Path.Combine(Path.GetTempPath(), "apex-deploy-" + Guid.NewGuid().ToString("N")) };
                lab.Src = Path.Combine(lab.Root, "zrodlo");
                Directory.CreateDirectory(lab.Src);
                Git(lab.Src, "init", "-q", "-b", "main");
                Git(lab.Src, "config", "user.email", "t@t");
                Git(lab.Src, "config", "user.name", "t");
                foreach (var f in new[] { "a", "b", "c", "d" }) File.WriteAllText(Path.Combine(lab.Src, f + ".txt"), f + "0\n");
                Git(lab.Src, "add", "-A");
                Git(lab.Src, "commit", "-q", "-m", "start");
                string labDir = Path.Combine(lab.Root, "lab");
                lab.Cfg = new LabConfigStore(labDir);
                lab.Store = new LabStore(labDir);
                lab.Ws = new GitWorkspace(labDir);
                lab.Deployer = new Deployer(lab.Cfg, lab.Store, lab.Ws, Path.Combine(lab.Root, "apex"), lab.Builder);
                lab.BaseCommit = (await lab.Ws.PrepareAsync(lab.Src, "", None)).baseCommit;
                return lab;
            }

            /// <summary>Zadanie z jednym zaudytowanym commitem (jak po JobRunner): zmiana jednego pliku na wskazanej bazie.</summary>
            public async Task<LabJob> JobAsync(string suffix, string file, string content, string? baseCommit = null,
                JobStatus status = JobStatus.ReadyForApproval, bool backtestRan = true)
            {
                string id = "20260101-000000-" + suffix;
                string b = baseCommit ?? BaseCommit;
                string wt = await Ws.CreateWorktreeAsync(id, b, None);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(wt, file))!);
                File.WriteAllText(Path.Combine(wt, file), content);
                Git(wt, "add", "-A");
                string head = await Ws.CommitAsync(wt, "zmiana " + suffix, None);
                var job = new LabJob
                {
                    Id = id, CreatedUtc = DateTime.UtcNow, Status = status, Title = "Zmiana " + suffix, Branch = "ai/" + id,
                    BaseCommit = b, HeadCommit = head,
                    Files = new List<ChangedFile> { new() { Path = file, Added = 1, Status = "M" } },
                    Backtest = new BacktestComparison { Ran = backtestRan, Reason = backtestRan ? "" : "brak danych" },
                };
                Store.SaveJob(job);
                return job;
            }

            public string WorktreeOf(LabJob job) => Path.Combine(Ws.WorkRoot, job.Id);
            public string DeployHead() => Git(Ws.DeployDir, "rev-parse", "HEAD");
            public string DeployBranch() => Git(Ws.RepoDir, "rev-parse", GitWorkspace.DeployBranch);
            public string ReadDeploy(string file) => File.ReadAllText(Path.Combine(Ws.DeployDir, file));
            public ReleaseInfo Release(LabJob job) => Deployer.History().Releases.Single(r => r.JobId == job.Id);
            public string ReadRelease(LabJob job, string file) => File.ReadAllText(Path.Combine(Release(job).Path, file));
            public string? Pointer() { string p = Path.Combine(Deployer.ReleasesDir, "aktualne.txt"); return File.Exists(p) ? File.ReadAllText(p) : null; }

            public void Dispose()
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                    Directory.Delete(Root, true);
                }
                catch (Exception) { /* pliki tymczasowe testu */ }
            }
        }

        private static bool HaveGit => GitWorkspace.Git() != null;

        [Fact]
        public async Task RolledBackRelease_DoesNotReturnInNextRelease()
        {
            if (!HaveGit) return;
            using var lab = await Lab.CreateAsync();
            var a = await lab.JobAsync("aaaa", "a.txt", "A\n");
            string msg = await lab.Deployer.ApproveAsync(a.Id, null, null, None);
            Assert.True(a.Status == JobStatus.Deployed, msg);
            Assert.Equal("A\n", lab.ReadDeploy("a.txt"));
            Assert.Equal(lab.Release(a).Commit, lab.Deployer.ActiveReleaseCommit());
            Assert.Equal(lab.BaseCommit, lab.Release(a).BaseCommit);

            string rb = lab.Deployer.Rollback();
            Assert.Contains("instalację pierwotną", rb);
            Assert.Equal(JobStatus.RolledBack, a.Status);
            Assert.True(lab.Release(a).Withdrawn);
            Assert.Null(lab.Deployer.ActiveReleaseCommit());
            Assert.Null(lab.Pointer());

            var b = await lab.JobAsync("bbbb", "b.txt", "B\n");
            Assert.Null(lab.Deployer.RequiredPhraseFor(b)); // baza wdrożenia = baza zadania – zwykłe potwierdzenie
            msg = await lab.Deployer.ApproveAsync(b.Id, null, null, None);
            Assert.True(b.Status == JobStatus.Deployed, msg);
            // Nowe wydanie = instalacja pierwotna + B; wycofana zmiana A nie wraca.
            Assert.Equal("a0\n", lab.ReadDeploy("a.txt"));
            Assert.Equal("B\n", lab.ReadDeploy("b.txt"));
            Assert.Equal("a0\n", lab.ReadRelease(b, "a.txt"));
            Assert.False(GitOk(lab.Ws.RepoDir, "merge-base", "--is-ancestor", a.HeadCommit, lab.Release(b).Commit));
            Assert.Equal(lab.Release(b).Commit, lab.DeployBranch());
            Assert.Equal(lab.Release(b).ExePath, lab.Pointer());
        }

        [Fact]
        public async Task FailedPublish_RestoresStatusAndDeployBranch_AndRejectedJobDoesNotLeak()
        {
            if (!HaveGit) return;
            using var lab = await Lab.CreateAsync();
            var a = await lab.JobAsync("aaaa", "a.txt", "A\n", status: JobStatus.NeedsAttention, backtestRan: false);
            Assert.Equal(Deployer.WarningPhrase, lab.Deployer.RequiredPhraseFor(a));
            Assert.StartsWith("Wymagane potwierdzenie", await lab.Deployer.ApproveAsync(a.Id, null, null, None));

            lab.Builder.FailPublish = true;
            string msg = await lab.Deployer.ApproveAsync(a.Id, Deployer.WarningPhrase, null, None);
            Assert.StartsWith("Wdrożenie nieudane", msg);
            // Status sprzed zatwierdzenia (nie „Gotowa do decyzji”) i nadal wymagana fraza ostrzeżenia.
            Assert.Equal(JobStatus.NeedsAttention, a.Status);
            Assert.Equal(JobStatus.NeedsAttention, lab.Store.Job(a.Id)!.Status);
            Assert.Equal(Deployer.WarningPhrase, Deployer.RequiredPhrase(a));
            Assert.Equal(Deployer.WarningPhrase, lab.Deployer.RequiredPhraseFor(a));
            Assert.False(lab.Store.AnyRunning());
            // Scalenie cofnięte: gałąź wdrożeń wróciła do bazy.
            Assert.Equal(lab.BaseCommit, lab.DeployHead());
            Assert.Equal(lab.BaseCommit, lab.DeployBranch());
            Assert.Equal("a0\n", lab.ReadDeploy("a.txt"));
            Assert.Empty(lab.Deployer.History().Releases);
            Assert.Null(lab.Pointer());

            Assert.Contains("odrzucona", await lab.Deployer.RejectAsync(a.Id, null, None));
            lab.Builder.FailPublish = false;
            var b = await lab.JobAsync("bbbb", "b.txt", "B\n");
            msg = await lab.Deployer.ApproveAsync(b.Id, null, null, None);
            Assert.True(b.Status == JobStatus.Deployed, msg);
            Assert.Equal("a0\n", lab.ReadRelease(b, "a.txt"));
            Assert.Equal("B\n", lab.ReadRelease(b, "b.txt"));
            Assert.False(GitOk(lab.Ws.RepoDir, "merge-base", "--is-ancestor", a.HeadCommit, lab.Release(b).Commit));
        }

        [Fact]
        public async Task FailedTestsOrException_DoNotLeaveDeploying_AndCanBeRetried()
        {
            if (!HaveGit) return;
            using var lab = await Lab.CreateAsync();
            var a = await lab.JobAsync("aaaa", "a.txt", "A\n");

            lab.Builder.FailBuild = true;
            string msg = await lab.Deployer.ApproveAsync(a.Id, null, null, None);
            Assert.Contains("nie kompiluje", msg);
            Assert.Equal(JobStatus.ReadyForApproval, a.Status);
            Assert.Equal(lab.BaseCommit, lab.DeployHead());

            lab.Builder.FailBuild = false;
            lab.Builder.FailTests = true;
            msg = await lab.Deployer.ApproveAsync(a.Id, null, null, None);
            Assert.Contains("Testy regresyjne scalonej wersji", msg);
            Assert.Equal(JobStatus.ReadyForApproval, a.Status);
            Assert.Equal(0, lab.Builder.Publishes); // bez testów nie ma budowy wydania
            Assert.Equal(lab.BaseCommit, lab.DeployHead());

            lab.Builder.FailTests = false;
            lab.Builder.ThrowOnVerify = new IOException("plik zajęty przez inny proces");
            msg = await lab.Deployer.ApproveAsync(a.Id, null, null, None);
            Assert.Contains("Nieoczekiwany błąd", msg);
            Assert.Equal(JobStatus.ReadyForApproval, a.Status);
            Assert.False(lab.Store.AnyRunning());
            Assert.Equal(lab.BaseCommit, lab.DeployHead());

            lab.Builder.ThrowOnVerify = null;
            msg = await lab.Deployer.ApproveAsync(a.Id, null, null, None);
            Assert.True(a.Status == JobStatus.Deployed, msg);
            Assert.Equal(lab.Release(a).Commit, lab.DeployHead());
        }

        [Fact]
        public async Task Deploy_MergesAuditedCommit_AndRefusesBranchMovedAfterAudit()
        {
            if (!HaveGit) return;
            using var lab = await Lab.CreateAsync();
            var a = await lab.JobAsync("aaaa", "a.txt", "A\n");
            // Po audycie ktoś dopisuje commit do gałęzi ai/<id> (np. kod uruchomiony w kopii roboczej).
            File.WriteAllText(Path.Combine(lab.WorktreeOf(a), "c.txt"), "NIESPRAWDZONE\n");
            Git(lab.WorktreeOf(a), "add", "-A");
            Git(lab.WorktreeOf(a), "commit", "-q", "-m", "po audycie");

            string msg = await lab.Deployer.ApproveAsync(a.Id, null, null, None);
            Assert.Contains("zaudytowano", msg);
            Assert.Equal(JobStatus.ReadyForApproval, a.Status);
            Assert.Empty(lab.Deployer.History().Releases);
            Assert.Equal(0, lab.Builder.Verifies);

            var b = await lab.JobAsync("bbbb", "b.txt", "B\n");
            msg = await lab.Deployer.ApproveAsync(b.Id, null, null, None);
            Assert.True(b.Status == JobStatus.Deployed, msg);
            string release = lab.Release(b).Commit;
            Assert.Equal(lab.BaseCommit, Git(lab.Ws.RepoDir, "rev-parse", release + "^1"));
            Assert.Equal(b.HeadCommit, Git(lab.Ws.RepoDir, "rev-parse", release + "^2")); // scalony dokładnie zaudytowany commit
            Assert.Equal("c0\n", lab.ReadRelease(b, "c.txt"));
            Assert.Equal(release, b.Deployment!.MergeCommit);
            Assert.Equal(lab.BaseCommit, b.Deployment.DeployBase);
        }

        [Fact]
        public async Task Rollback_SkipsWithdrawnReleases_AndNewReleasesBuildOnActiveRelease()
        {
            if (!HaveGit) return;
            using var lab = await Lab.CreateAsync();
            var a = await lab.JobAsync("aaaa", "a.txt", "A\n");
            Assert.Contains("gotowe", await lab.Deployer.ApproveAsync(a.Id, null, null, None));
            var v1 = lab.Release(a);

            // B sprawdzono na bazie źródłowej, a aktywne jest wydanie v1 – połączenie nie było weryfikowane: fraza ostrzeżenia.
            var b = await lab.JobAsync("bbbb", "b.txt", "B\n");
            Assert.Equal(Deployer.WarningPhrase, lab.Deployer.RequiredPhraseFor(b));
            Assert.Null(Deployer.RequiredPhrase(b));
            Assert.StartsWith("Wymagane potwierdzenie", await lab.Deployer.ApproveAsync(b.Id, null, null, None));
            string msg = await lab.Deployer.ApproveAsync(b.Id, Deployer.WarningPhrase, null, None);
            Assert.True(b.Status == JobStatus.Deployed, msg);
            var v2 = lab.Release(b);
            Assert.Equal(v1.Commit, v2.BaseCommit);
            Assert.Equal("A\n", lab.ReadRelease(b, "a.txt"));
            Assert.Equal("B\n", lab.ReadRelease(b, "b.txt"));
            Assert.Equal(v1.Commit, b.ShippedAuditBase);

            Assert.Contains("Przywrócono wydanie " + v1.Version, lab.Deployer.Rollback());
            Assert.Equal(JobStatus.RolledBack, b.Status);
            Assert.Equal(JobStatus.Deployed, a.Status);
            Assert.Equal(v1.Commit, lab.DeployHead()); // gałąź wdrożeń = aktywne wydanie
            Assert.Equal(v1.Commit, lab.Deployer.ActiveReleaseCommit());

            var c = await lab.JobAsync("cccc", "c.txt", "C\n");
            msg = await lab.Deployer.ApproveAsync(c.Id, Deployer.WarningPhrase, null, None);
            Assert.True(c.Status == JobStatus.Deployed, msg);
            Assert.Equal("A\n", lab.ReadRelease(c, "a.txt"));
            Assert.Equal("b0\n", lab.ReadRelease(c, "b.txt")); // wycofane B nie wraca
            Assert.Equal("C\n", lab.ReadRelease(c, "c.txt"));

            // Wycofanie v3 przywraca v1 (najnowsze wcześniejsze NIEwycofane), a nie wycofane v2.
            Assert.Contains("Przywrócono wydanie " + v1.Version, lab.Deployer.Rollback());
            var hist = lab.Deployer.History();
            Assert.Equal(v1.Version, hist.Current);
            Assert.Equal(v1.ExePath, lab.Pointer());
            Assert.True(hist.Releases.Single(r => r.JobId == b.Id).Withdrawn);
            Assert.True(hist.Releases.Single(r => r.JobId == c.Id).Withdrawn);
            Assert.False(hist.Releases.Single(r => r.JobId == a.Id).Withdrawn);
            Assert.Equal(JobStatus.RolledBack, b.Status);
            Assert.Equal(JobStatus.RolledBack, c.Status);
            Assert.Equal(JobStatus.Deployed, a.Status);

            Assert.Contains("instalację pierwotną", lab.Deployer.Rollback());
            Assert.Null(lab.Deployer.History().Current);
            Assert.Null(lab.Pointer());
            Assert.Contains("nie ma czego wycofywać", lab.Deployer.Rollback());
        }

        [Fact]
        public async Task Rollback_TreatsLegacyRolledBackJobsAsWithdrawn()
        {
            if (!HaveGit) return;
            using var lab = await Lab.CreateAsync();
            var a = await lab.JobAsync("aaaa", "a.txt", "A\n");
            await lab.Deployer.ApproveAsync(a.Id, null, null, None);
            var b = await lab.JobAsync("bbbb", "b.txt", "B\n");
            await lab.Deployer.ApproveAsync(b.Id, Deployer.WarningPhrase, null, None);
            var c = await lab.JobAsync("cccc", "c.txt", "C\n");
            await lab.Deployer.ApproveAsync(c.Id, Deployer.WarningPhrase, null, None);
            Assert.Equal(3, lab.Deployer.History().Releases.Count);
            // Historia sprzed poprawki: brak znacznika Withdrawn, ale zadanie B ma status „wycofane”.
            b.Status = JobStatus.RolledBack;
            lab.Store.SaveJob(b);

            Assert.Contains("Przywrócono wydanie " + lab.Release(a).Version, lab.Deployer.Rollback());
            Assert.Equal(lab.Release(a).ExePath, lab.Pointer());
        }

        [Fact]
        public async Task JobBuiltOnWithdrawnRelease_IsRefused()
        {
            if (!HaveGit) return;
            using var lab = await Lab.CreateAsync();
            var a = await lab.JobAsync("aaaa", "a.txt", "A\n");
            await lab.Deployer.ApproveAsync(a.Id, null, null, None);
            string v1 = lab.Release(a).Commit;
            // Zadanie D powstało na aktywnym wydaniu v1, które potem wycofano.
            var d = await lab.JobAsync("dddd", "d.txt", "D\n", baseCommit: v1);
            Assert.Null(lab.Deployer.RequiredPhraseFor(d));
            lab.Deployer.Rollback();

            string msg = await lab.Deployer.ApproveAsync(d.Id, Deployer.WarningPhrase, null, None);
            Assert.Contains("nie należy do aktywnej wersji", msg);
            Assert.Equal(JobStatus.ReadyForApproval, d.Status);
            Assert.Single(lab.Deployer.History().Releases);
            Assert.Null(lab.Deployer.History().Current);
            Assert.Equal(1, lab.Builder.Verifies); // weryfikacja tylko przy wdrożeniu A
        }

        [Fact]
        public async Task ShippedDiffTouchingProtectedModule_RequiresProtectedPhrase()
        {
            if (!HaveGit) return;
            using var lab = await Lab.CreateAsync();
            var a = await lab.JobAsync("aaaa", "a.txt", "A\n");
            await lab.Deployer.ApproveAsync(a.Id, null, null, None);
            string v1 = lab.Release(a).Commit;

            // Repozytorium źródłowe dostaje zmianę modułu chronionego; zadanie B (tylko b.txt) powstaje na nowej bazie.
            Directory.CreateDirectory(Path.Combine(lab.Src, "src", "Apex.Core", "Risk"));
            File.WriteAllText(Path.Combine(lab.Src, "src", "Apex.Core", "Risk", "Limit.cs"), "class Limit { }\n");
            Git(lab.Src, "add", "-A");
            Git(lab.Src, "commit", "-q", "-m", "zmiana limitu");
            string newBase = (await lab.Ws.PrepareAsync(lab.Src, "", None)).baseCommit;
            var b = await lab.JobAsync("bbbb", "b.txt", "B\n", baseCommit: newBase);
            Assert.False(b.ProtectedTouched);
            Assert.Equal(Deployer.WarningPhrase, lab.Deployer.RequiredPhraseFor(b));

            string msg = await lab.Deployer.ApproveAsync(b.Id, Deployer.WarningPhrase, null, None);
            Assert.Contains("modułów chronionych", msg);
            Assert.Contains("src/Apex.Core/Risk/Limit.cs", msg);
            Assert.Equal(JobStatus.ReadyForApproval, b.Status);
            Assert.Equal(v1, lab.DeployHead());
            Assert.True(b.ShippedProtected);
            Assert.Equal(Deployer.ProtectedPhrase, lab.Deployer.RequiredPhraseFor(b)); // panel pokaże teraz frazę chronioną

            msg = await lab.Deployer.ApproveAsync(b.Id, Deployer.ProtectedPhrase, null, None);
            Assert.True(b.Status == JobStatus.Deployed, msg);
            Assert.True(b.Deployment!.ProtectedConfirmed);
            Assert.Equal(v1, b.Deployment.DeployBase);
        }

        [Fact]
        public async Task ShippedDiffWithBlockingFinding_IsRefused()
        {
            if (!HaveGit) return;
            using var lab = await Lab.CreateAsync();
            // Zadanie zaudytowane jako czyste, ale zaudytowany commit zawiera ścieżkę zabronioną – ponowny audyt blokuje.
            var a = await lab.JobAsync("aaaa", "src/Apex.AiLab/X.cs", "class X { }\n");
            string msg = await lab.Deployer.ApproveAsync(a.Id, null, null, None);
            Assert.Contains("blokujące", msg);
            Assert.Equal(JobStatus.ReadyForApproval, a.Status);
            Assert.Equal(0, lab.Builder.Verifies);
            Assert.Equal(lab.BaseCommit, lab.DeployHead());
        }

        [Fact]
        public async Task Rollback_IsRefusedWhileDeployIsRunning()
        {
            if (!HaveGit) return;
            using var lab = await Lab.CreateAsync();
            var a = await lab.JobAsync("aaaa", "a.txt", "A\n");
            lab.Builder.HoldVerify = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var approve = lab.Deployer.ApproveAsync(a.Id, null, null, None);
            await lab.Builder.VerifyStarted.Task.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(JobStatus.Deploying, a.Status);
            Assert.Contains("Trwa wdrożenie", await lab.Deployer.RollbackAsync(None));
            Assert.Equal("Inne wdrożenie jest w toku.", await lab.Deployer.ApproveAsync(a.Id, null, null, None));
            lab.Builder.HoldVerify.SetResult(true);
            string msg = await approve;
            Assert.True(a.Status == JobStatus.Deployed, msg);
        }

        [Fact]
        public async Task LabRepo_DisablesLineEndingConversion()
        {
            if (!HaveGit) return;
            using var lab = await Lab.CreateAsync();
            Assert.Equal("false", Git(lab.Ws.RepoDir, "config", "--local", "--get", "core.autocrlf"));
            // Istniejąca kopia laboratorium z inną wartością – PrepareAsync ją poprawia.
            Git(lab.Ws.RepoDir, "config", "core.autocrlf", "true");
            await lab.Ws.PrepareAsync(lab.Src, "", None);
            Assert.Equal("false", Git(lab.Ws.RepoDir, "config", "--local", "--get", "core.autocrlf"));
        }

        [Fact]
        public async Task ChangesBetween_ListsEveryShippedPath()
        {
            if (!HaveGit) return;
            using var lab = await Lab.CreateAsync();
            string wt = await lab.Ws.CreateWorktreeAsync("20260101-000000-eeee", lab.BaseCommit, None);
            Directory.CreateDirectory(Path.Combine(wt, "katalog z spacją"));
            File.WriteAllText(Path.Combine(wt, "katalog z spacją", "zażółć.txt"), "jeden\ndwa\n");
            File.WriteAllBytes(Path.Combine(wt, "dane.bin"), new byte[] { 0, 1, 2, 0, 255 });
            File.Delete(Path.Combine(wt, "d.txt"));
            Git(wt, "mv", "c.txt", "c2.txt");
            Git(wt, "add", "-A");
            string head = await lab.Ws.CommitAsync(wt, "różne zmiany", None);

            var (files, diff) = await lab.Ws.ChangesBetweenAsync(lab.BaseCommit, head, None);
            var byPath = files.ToDictionary(f => f.Path);
            Assert.Equal(new[] { "c.txt", "c2.txt", "d.txt", "dane.bin", "katalog z spacją/zażółć.txt" }, files.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal));
            Assert.Equal(("A", 2), (byPath["katalog z spacją/zażółć.txt"].Status, byPath["katalog z spacją/zażółć.txt"].Added));
            Assert.Equal("D", byPath["d.txt"].Status);
            Assert.Equal("D", byPath["c.txt"].Status); // przeniesienie = usunięcie + dodanie (audyt widzi obie ścieżki)
            Assert.Equal("A", byPath["c2.txt"].Status);
            Assert.Equal("A", byPath["dane.bin"].Status);
            Assert.Contains(ChangeAudit.ChangedLines(diff), l => l.file == "katalog z spacją/zażółć.txt" && l.line == "dwa" && l.added);
        }

        [Fact]
        public void CBotReleaseBuild_NeverPublishesIntoCTrader()
        {
            foreach (var sln in DotnetReleaseBuilder.CBotSolutions)
            {
                var args = DotnetReleaseBuilder.CBotBuildArgs(sln);
                Assert.Contains("-p:AlgoPublish=false", args);
                Assert.DoesNotContain(args, x => x.StartsWith("-p:AlgoBuild", StringComparison.OrdinalIgnoreCase)); // .algo nadal trafia do wydania
            }
        }

        [Fact]
        public async Task LabStoreJson_SharesFileWithOtherProcesses()
        {
            string dir = Path.Combine(Path.GetTempPath(), "apex-json-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, "historia.json");
                LabStore.WriteJson(path, new ReleaseHistory { Current = "v1" });
                // Inny proces trzyma plik otwarty do zapisu (z udostępnianiem) – odczyt nie może się nie udać.
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                    Assert.Equal("v1", LabStore.ReadJson<ReleaseHistory>(path)?.Current);

                // Czytelnik trzyma plik chwilę otwarty – zapis atomowy ponawia zamianę zamiast zgłosić błąd.
                var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var release = Task.Run(async () => { await Task.Delay(150); reader.Dispose(); });
                LabStore.WriteJson(path, new ReleaseHistory { Current = "v2" });
                await release;
                Assert.Equal("v2", LabStore.ReadJson<ReleaseHistory>(path)?.Current);
                Assert.Single(Directory.GetFiles(dir)); // bez pozostawionych plików tymczasowych
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }
    }
}
