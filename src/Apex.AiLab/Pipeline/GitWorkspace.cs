using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apex.AiLab.Lab;

namespace Apex.AiLab.Pipeline
{
    /// <summary>
    /// Izolowane środowisko pracy: AI Lab ma WŁASNĄ kopię repozytorium (lab/repo) pobraną z repozytorium źródłowego użytkownika.
    /// Każde zadanie dostaje osobną kopię roboczą (git worktree) na gałęzi ai/&lt;id&gt;. Repozytorium źródłowe nie jest zmieniane
    /// (wyjątek: jawne polecenie użytkownika "wypchnij gałąź do repozytorium źródłowego", które dodaje tylko gałąź).
    /// </summary>
    public sealed class GitWorkspace
    {
        private readonly string _labDir;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public GitWorkspace(string labDir)
        {
            _labDir = labDir;
            Directory.CreateDirectory(WorkRoot);
        }

        public string RepoDir => Path.Combine(_labDir, "repo");
        public string WorkRoot => Path.Combine(_labDir, "kopie");
        public string DeployDir => Path.Combine(_labDir, "wdrozenie");
        public const string DeployBranch = "apex-wdrozone";

        public static CommandSpec? Git() => ExecutableLocator.Find("git");

        public async Task<ProcessResult> RunGit(string dir, CancellationToken ct, params string[] args)
        {
            var git = Git() ?? throw new InvalidOperationException("Nie znaleziono programu git. Zainstaluj Git for Windows (winget install Git.Git).");
            return await ProcessRunner.RunAsync(git, args, dir, timeout: TimeSpan.FromMinutes(10), ct: ct).ConfigureAwait(false);
        }

        private async Task<string> GitOut(string dir, CancellationToken ct, params string[] args)
        {
            var r = await RunGit(dir, ct, args).ConfigureAwait(false);
            if (!r.Ok) throw new InvalidOperationException($"git {string.Join(" ", args)}: {r.Tail(6)}");
            return string.Join("\n", r.Output).Trim();
        }

        /// <summary>Tworzy lub aktualizuje kopię laboratorium. Zwraca commit bazowy (gałąź bazowa repozytorium źródłowego).</summary>
        public async Task<(string baseCommit, string baseRef)> PrepareAsync(string sourceRepo, string baseBranch, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(sourceRepo) || !Directory.Exists(Path.Combine(sourceRepo, ".git")))
                throw new InvalidOperationException("Nie ustawiono repozytorium źródłowego APEX (folder z katalogiem .git) – ustaw je w zakładce AI & Auto-Updates.");
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!Directory.Exists(Path.Combine(RepoDir, ".git")))
                {
                    Directory.CreateDirectory(_labDir);
                    var r = await RunGit(_labDir, ct, "clone", "--no-hardlinks", sourceRepo, RepoDir).ConfigureAwait(false);
                    if (!r.Ok) throw new InvalidOperationException("Nie udało się skopiować repozytorium: " + r.Tail(6));
                }
                else
                {
                    await GitOut(RepoDir, ct, "remote", "set-url", "origin", sourceRepo).ConfigureAwait(false);
                    await GitOut(RepoDir, ct, "fetch", "origin", "--prune").ConfigureAwait(false);
                }
                await GitOut(RepoDir, ct, "config", "user.name", "APEX AI Lab").ConfigureAwait(false);
                await GitOut(RepoDir, ct, "config", "user.email", "ailab@apex.local").ConfigureAwait(false);
                // Pliki zadania agenta nie trafiają do zmian.
                string exclude = Path.Combine(RepoDir, ".git", "info", "exclude");
                Directory.CreateDirectory(Path.GetDirectoryName(exclude)!);
                string ex = File.Exists(exclude) ? File.ReadAllText(exclude) : "";
                if (!ex.Contains(".ailab/", StringComparison.Ordinal)) File.AppendAllText(exclude, "\n.ailab/\n");

                string branch = baseBranch;
                if (string.IsNullOrWhiteSpace(branch))
                    branch = await GitOut(sourceRepo, ct, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false);
                if (branch == "HEAD") throw new InvalidOperationException("Repozytorium źródłowe ma odłączony HEAD – wskaż gałąź bazową w ustawieniach.");
                string baseRef = "origin/" + branch;
                string commit = await GitOut(RepoDir, ct, "rev-parse", baseRef).ConfigureAwait(false);
                return (commit, baseRef);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<string> CreateWorktreeAsync(string jobId, string baseCommit, CancellationToken ct)
        {
            string path = Path.Combine(WorkRoot, jobId);
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await RunGit(RepoDir, ct, "worktree", "prune").ConfigureAwait(false);
                var r = await RunGit(RepoDir, ct, "worktree", "add", "-b", "ai/" + jobId, path, baseCommit).ConfigureAwait(false);
                if (!r.Ok) throw new InvalidOperationException("Nie udało się utworzyć kopii roboczej: " + r.Tail(6));
                return path;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Zbiera zmiany względem bazy (bez katalogu .ailab) i zwraca listę plików z liczbą linii.</summary>
        public async Task<List<ChangedFile>> CollectChangesAsync(string worktree, string baseCommit, CancellationToken ct)
        {
            await GitOut(worktree, ct, "add", "-A").ConfigureAwait(false);
            var files = new Dictionary<string, ChangedFile>(StringComparer.Ordinal);
            string numstat = await GitOut(worktree, ct, "diff", "--cached", "--numstat", baseCommit).ConfigureAwait(false);
            foreach (var line in numstat.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = line.Split('\t');
                if (p.Length < 3) continue;
                var cf = new ChangedFile { Path = p[2].Trim() };
                int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int a);
                int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int d);
                cf.Added = a;
                cf.Removed = d;
                files[cf.Path] = cf;
            }
            string status = await GitOut(worktree, ct, "diff", "--cached", "--name-status", baseCommit).ConfigureAwait(false);
            foreach (var line in status.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = line.Split('\t');
                if (p.Length < 2) continue;
                string path = p[^1].Trim();
                if (!files.TryGetValue(path, out var cf)) files[path] = cf = new ChangedFile { Path = path };
                cf.Status = p[0].Substring(0, 1);
            }
            return files.Values.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        }

        public Task<string> DiffAsync(string worktree, string baseCommit, CancellationToken ct) =>
            GitOut(worktree, ct, "diff", "--cached", "--no-color", "--unified=3", baseCommit);

        public async Task<string> CommitAsync(string worktree, string message, CancellationToken ct)
        {
            var r = await RunGit(worktree, ct, "commit", "-q", "-m", message).ConfigureAwait(false);
            if (!r.Ok) throw new InvalidOperationException("Commit nieudany: " + r.Tail(6));
            return await GitOut(worktree, ct, "rev-parse", "HEAD").ConfigureAwait(false);
        }

        /// <summary>Usuwa kopię roboczą (gałąź zostaje – zmianę można odtworzyć).</summary>
        public async Task RemoveWorktreeAsync(string jobId, CancellationToken ct)
        {
            string path = Path.Combine(WorkRoot, jobId);
            if (!Directory.Exists(path)) return;
            await RunGit(RepoDir, ct, "worktree", "remove", "--force", path).ConfigureAwait(false);
            if (Directory.Exists(path))
            {
                try { Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                await RunGit(RepoDir, ct, "worktree", "prune").ConfigureAwait(false);
            }
        }

        /// <summary>Usuwa najstarsze kopie robocze ponad limit (z wyjątkiem wskazanych).</summary>
        public async Task PruneWorktreesAsync(int keep, ISet<string> protectIds, CancellationToken ct)
        {
            if (!Directory.Exists(WorkRoot)) return;
            var dirs = new DirectoryInfo(WorkRoot).GetDirectories().OrderByDescending(d => d.CreationTimeUtc).ToList();
            foreach (var d in dirs.Skip(Math.Max(1, keep)))
                if (!protectIds.Contains(d.Name)) await RemoveWorktreeAsync(d.Name, ct).ConfigureAwait(false);
        }

        /// <summary>Łączy gałąź zadania z gałęzią wdrożeń w osobnej kopii roboczej. Konflikt = przerwanie (bez zmian).</summary>
        public async Task<(bool ok, string commitOrError)> MergeForDeployAsync(string jobId, string baseCommit, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!Directory.Exists(DeployDir))
                {
                    var exists = await RunGit(RepoDir, ct, "rev-parse", "--verify", DeployBranch).ConfigureAwait(false);
                    var add = exists.Ok
                        ? await RunGit(RepoDir, ct, "worktree", "add", DeployDir, DeployBranch).ConfigureAwait(false)
                        : await RunGit(RepoDir, ct, "worktree", "add", "-b", DeployBranch, DeployDir, baseCommit).ConfigureAwait(false);
                    if (!add.Ok) return (false, "Nie udało się przygotować kopii wdrożeń: " + add.Tail(6));
                }
                var m = await RunGit(DeployDir, ct, "merge", "--no-ff", "-m", "Wdrożenie zmiany AI Lab " + jobId + " (zatwierdzone przez użytkownika)", "ai/" + jobId).ConfigureAwait(false);
                if (!m.Ok)
                {
                    await RunGit(DeployDir, ct, "merge", "--abort").ConfigureAwait(false);
                    return (false, "Konflikt podczas łączenia zmian – zmiana nie została wdrożona: " + m.Tail(6));
                }
                return (true, await GitOut(DeployDir, ct, "rev-parse", "HEAD").ConfigureAwait(false));
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<string> ExportPatchAsync(string jobId, string baseCommit, string file, CancellationToken ct)
        {
            string patch = await GitOut(RepoDir, ct, "format-patch", "--stdout", baseCommit + "..ai/" + jobId).ConfigureAwait(false);
            File.WriteAllText(file, patch + "\n");
            return file;
        }

        /// <summary>Dodaje gałąź ai/&lt;id&gt; do repozytorium źródłowego (bez zmiany bieżącej gałęzi i plików użytkownika).</summary>
        public async Task<(bool ok, string message)> PushBranchToSourceAsync(string jobId, CancellationToken ct)
        {
            var r = await RunGit(RepoDir, ct, "push", "origin", "ai/" + jobId + ":refs/heads/ai/" + jobId).ConfigureAwait(false);
            return r.Ok ? (true, $"Gałąź ai/{jobId} dodana do repozytorium źródłowego.") : (false, "Nie udało się wypchnąć gałęzi: " + r.Tail(6));
        }
    }
}
