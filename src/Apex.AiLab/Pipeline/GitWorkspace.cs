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
                    // Bez konwersji końców linii (Git for Windows domyślnie ma core.autocrlf=true): kopie robocze, diff i audyt
                    // działają identycznie na Windows i Linux.
                    var r = await RunGit(_labDir, ct, "clone", "-c", "core.autocrlf=false", "--no-hardlinks", sourceRepo, RepoDir).ConfigureAwait(false);
                    if (!r.Ok) throw new InvalidOperationException("Nie udało się skopiować repozytorium: " + r.Tail(6));
                }
                else
                {
                    await GitOut(RepoDir, ct, "remote", "set-url", "origin", sourceRepo).ConfigureAwait(false);
                    await GitOut(RepoDir, ct, "fetch", "origin", "--prune").ConfigureAwait(false);
                }
                // Także dla istniejących kopii laboratorium – przed utworzeniem jakiejkolwiek kopii roboczej.
                await GitOut(RepoDir, ct, "config", "core.autocrlf", "false").ConfigureAwait(false);
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

        /// <summary>
        /// Zbiera zmiany względem bazy (bez katalogu .ailab) i zwraca listę plików z liczbą linii.
        /// --no-renames: przeniesienie = usunięcie starej ścieżki + dodanie nowej, więc reguły audytu (chronione, zabronione,
        /// usunięcie testu) obejmują też starą ścieżkę. Wyjście -z (bez cytowania nazw) czytane w całości, bez limitu linii.
        /// Dodatkowo: pliki ignorowane przez git, które wpływają na kompilację (np. *.csproj.user, obj/*.props), jako status "I".
        /// </summary>
        public async Task<List<ChangedFile>> CollectChangesAsync(string worktree, string baseCommit, CancellationToken ct)
        {
            // Plik .git kopii roboczej wskazuje repozytorium git – podmieniony mógłby przekierować polecenia git (i ich konfigurację).
            string? pointerProblem = GitChangeParsing.CheckWorktreePointer(worktree, RepoDir);
            if (pointerProblem != null) throw new InvalidOperationException(pointerProblem);
            var git = Git() ?? throw new InvalidOperationException("Nie znaleziono programu git. Zainstaluj Git for Windows (winget install Git.Git).");
            async Task<string> Exact(params string[] args)
            {
                var all = new[] { "-c", "core.quotepath=false", "-c", "core.fsmonitor=false" }.Concat(args).ToArray();
                var r = await ProcessRunner.CaptureAsync(git, all, worktree, timeout: TimeSpan.FromMinutes(10), ct: ct).ConfigureAwait(false);
                if (r.Cancelled) ct.ThrowIfCancellationRequested();
                if (!r.Ok) throw new InvalidOperationException($"git {string.Join(" ", args)}: {r.Problem()}");
                return r.Stdout;
            }

            await GitOut(worktree, ct, "-c", "core.fsmonitor=false", "add", "-A").ConfigureAwait(false);
            string numstat = await Exact("diff", "--cached", "--no-renames", "--no-ext-diff", "--no-textconv", "--ignore-submodules=none", "--numstat", "-z", baseCommit).ConfigureAwait(false);
            string raw = await Exact("diff", "--cached", "--no-renames", "--no-ext-diff", "--ignore-submodules=none", "--raw", "-z", baseCommit).ConfigureAwait(false);
            var files = GitChangeParsing.ParseChanges(numstat, raw);
            string ignored = await Exact("ls-files", "-z", "--others", "--ignored", "--exclude-standard", "--directory").ConfigureAwait(false);
            foreach (var hidden in GitChangeParsing.HiddenBuildInputs(worktree, ignored))
                if (!files.Exists(f => f.Path == hidden)) files.Add(new ChangedFile { Path = hidden, Status = "I" });
            return files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Pełny diff do audytu i przeglądu: bez wykrywania przeniesień, zawsze jako tekst (atrybuty .gitattributes ani konwertery
        /// nie ukryją treści), stałe prefiksy a/ b/ niezależnie od konfiguracji użytkownika, odczyt bajt w bajt bez limitu linii.
        /// </summary>
        public async Task<string> DiffAsync(string worktree, string baseCommit, CancellationToken ct)
        {
            string? pointerProblem = GitChangeParsing.CheckWorktreePointer(worktree, RepoDir);
            if (pointerProblem != null) throw new InvalidOperationException(pointerProblem);
            var git = Git() ?? throw new InvalidOperationException("Nie znaleziono programu git. Zainstaluj Git for Windows (winget install Git.Git).");
            var args = new[]
            {
                "-c", "core.quotepath=false", "-c", "core.fsmonitor=false",
                "diff", "--cached", "--no-color", "--unified=3", "--no-renames", "--text", "--no-textconv", "--no-ext-diff",
                "--ignore-submodules=none", "--submodule=short", "--src-prefix=a/", "--dst-prefix=b/", baseCommit,
            };
            var r = await ProcessRunner.CaptureAsync(git, args, worktree, timeout: TimeSpan.FromMinutes(10), ct: ct).ConfigureAwait(false);
            if (r.Cancelled) ct.ThrowIfCancellationRequested();
            if (!r.Ok) throw new InvalidOperationException("git diff: " + r.Problem());
            return r.Stdout;
        }

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

        /// <summary>
        /// Łączy ZAUDYTOWANY commit zadania z gałęzią wdrożeń w osobnej kopii roboczej. Przed scaleniem kopia wdrożeń jest
        /// ustawiana dokładnie na bazę wdrożenia (<paramref name="deployBase"/> – commit aktywnego wydania; domyślnie
        /// <paramref name="baseCommit"/>), więc zmiany odrzucone, nieudane i wycofane nigdy nie trafiają do kolejnego wydania.
        /// Gdy podano <paramref name="auditedCommit"/>, gałąź ai/&lt;id&gt; musi wskazywać dokładnie ten commit (inaczej odmowa).
        /// Konflikt = przerwanie i powrót kopii wdrożeń do bazy.
        /// </summary>
        public async Task<(bool ok, string commitOrError)> MergeForDeployAsync(string jobId, string baseCommit, CancellationToken ct,
            string? deployBase = null, string? auditedCommit = null)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                string? tip = await ResolveCommitAsync("refs/heads/ai/" + jobId, ct).ConfigureAwait(false);
                if (tip == null) return (false, $"Brak gałęzi ai/{jobId} – nie ma czego wdrożyć.");
                if (auditedCommit != null)
                {
                    string? audited = await ResolveCommitAsync(auditedCommit, ct).ConfigureAwait(false);
                    if (audited == null || !string.Equals(audited, tip, StringComparison.OrdinalIgnoreCase))
                        return (false, $"Gałąź ai/{jobId} wskazuje commit {Short(tip)}, a zaudytowano {Short(auditedCommit)} – po audycie pojawiły się niesprawdzone zmiany. Wdrożenie wstrzymane.");
                }
                string target = string.IsNullOrWhiteSpace(deployBase) ? baseCommit : deployBase!;
                var (reset, baseOrError) = await ResetDeployCoreAsync(target, ct).ConfigureAwait(false);
                if (!reset) return (false, baseOrError);
                if (await IsAncestorAsync(tip, baseOrError, ct).ConfigureAwait(false))
                    return (false, "Ta zmiana jest już zawarta w bazie wdrożenia – nie ma czego wdrażać.");

                // Scalany jest konkretny commit (SHA), nie ruchomy wierzchołek gałęzi.
                var m = await RunGit(DeployDir, ct, "merge", "--no-ff", "--no-edit", "-m", "Wdrożenie zmiany AI Lab " + jobId + " (zatwierdzone przez użytkownika)", tip).ConfigureAwait(false);
                if (!m.Ok)
                {
                    await RunGit(DeployDir, CancellationToken.None, "merge", "--abort").ConfigureAwait(false);
                    await ResetDeployCoreAsync(baseOrError, CancellationToken.None).ConfigureAwait(false);
                    return (false, "Konflikt podczas łączenia zmian – zmiana nie została wdrożona: " + m.Tail(6));
                }
                return (true, await GitOut(DeployDir, ct, "rev-parse", "HEAD").ConfigureAwait(false));
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Ustawia gałąź wdrożeń i jej kopię roboczą dokładnie na wskazany commit (po nieudanym wdrożeniu lub wycofaniu).</summary>
        public async Task<(bool ok, string commitOrError)> ResetDeployAsync(string commit, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await ResetDeployCoreAsync(commit, ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Pełny SHA commitu (null, gdy nie istnieje).</summary>
        public async Task<string?> ResolveCommitAsync(string rev, CancellationToken ct, string? dir = null)
        {
            if (string.IsNullOrWhiteSpace(rev) || !Directory.Exists(dir ?? RepoDir)) return null;
            var r = await RunGit(dir ?? RepoDir, ct, "rev-parse", "--verify", "--quiet", rev + "^{commit}").ConfigureAwait(false);
            string sha = r.Ok ? string.Join("\n", r.Output).Trim() : "";
            return sha.Length >= 7 ? sha : null;
        }

        /// <summary>Czy <paramref name="ancestor"/> jest przodkiem (lub tym samym commitem) <paramref name="descendant"/>. Brak commitu = nie.</summary>
        public async Task<bool> IsAncestorAsync(string ancestor, string descendant, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(ancestor) || string.IsNullOrWhiteSpace(descendant)) return false;
            var r = await RunGit(RepoDir, ct, "merge-base", "--is-ancestor", ancestor, descendant).ConfigureAwait(false);
            return r.Ok;
        }

        /// <summary>
        /// Zmiany między dwoma commitami (np. aktywne wydanie → scalone wydanie): lista plików i pełny diff do ponownego audytu.
        /// Bez wykrywania zmian nazw – przeniesienie pliku to usunięcie + dodanie, więc audyt widzi obie ścieżki.
        /// </summary>
        public async Task<(List<ChangedFile> files, string diff)> ChangesBetweenAsync(string fromCommit, string toCommit, CancellationToken ct)
        {
            var files = new Dictionary<string, ChangedFile>(StringComparer.Ordinal);
            string numstat = await GitFull(RepoDir, ct, "-c", "core.quotepath=false", "diff", "--numstat", "-z", "--no-renames", fromCommit, toCommit).ConfigureAwait(false);
            foreach (var rec in numstat.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = rec.Trim('\n', '\r').Split('\t');
                if (p.Length < 3 || p[2].Length == 0) continue;
                var cf = new ChangedFile { Path = p[2] };
                int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int a);
                int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int d);
                cf.Added = a;
                cf.Removed = d;
                files[cf.Path] = cf;
            }
            string status = await GitFull(RepoDir, ct, "-c", "core.quotepath=false", "diff", "--name-status", "-z", "--no-renames", fromCommit, toCommit).ConfigureAwait(false);
            var parts = status.Split('\0');
            for (int i = 0; i + 1 < parts.Length; i += 2)
            {
                string code = parts[i].Trim('\n', '\r');
                string path = parts[i + 1];
                if (code.Length == 0 || path.Length == 0) continue;
                if (!files.TryGetValue(path, out var cf)) files[path] = cf = new ChangedFile { Path = path };
                cf.Status = code.Substring(0, 1);
            }
            string diff = await GitFull(RepoDir, ct, "-c", "core.quotepath=false", "diff", "--no-color", "--no-ext-diff", "--no-textconv", "--no-renames", "--src-prefix=a/", "--dst-prefix=b/", "--unified=3", fromCommit, toCommit).ConfigureAwait(false);
            return (files.Values.OrderBy(f => f.Path, StringComparer.Ordinal).ToList(), diff);
        }

        /// <summary>Treść pliku w danym commicie (null, gdy brak).</summary>
        public async Task<string?> ShowFileAsync(string commit, string path, CancellationToken ct)
        {
            var git = Git();
            if (git == null || string.IsNullOrWhiteSpace(commit)) return null;
            var r = await ProcessRunner.RunAsync(git, new[] { "show", commit + ":" + path }, RepoDir, timeout: TimeSpan.FromMinutes(2), ct: ct, keepLines: 1_000_000).ConfigureAwait(false);
            return r.Ok ? string.Join("\n", r.Output) : null;
        }

        /// <summary>Trwała referencja wydania (refs/apex-wydania/&lt;wersja&gt;) – commit wydania nie zniknie po przestawieniu gałęzi wdrożeń.</summary>
        public async Task<bool> MarkReleaseAsync(string version, string commit, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(commit)) return false;
            string safe = new string(version.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
            var r = await RunGit(RepoDir, ct, "update-ref", "refs/apex-wydania/" + safe, commit).ConfigureAwait(false);
            return r.Ok;
        }

        /// <summary>
        /// Kopia wdrożeń = dokładnie <paramref name="commit"/>: gałąź apex-wdrozone przestawiona, pliki śledzone przywrócone,
        /// pliki nieśledzone i wyniki poprzednich kompilacji usunięte (żadne stare .algo nie trafi do nowego wydania).
        /// </summary>
        private async Task<(bool ok, string commitOrError)> ResetDeployCoreAsync(string commit, CancellationToken ct)
        {
            if (!Directory.Exists(Path.Combine(RepoDir, ".git"))) return (false, "Brak kopii repozytorium laboratorium – najpierw uruchom zadanie agenta.");
            string? target = await ResolveCommitAsync(commit, ct).ConfigureAwait(false);
            if (target == null) return (false, $"Commit bazy wdrożenia {Short(commit)} nie istnieje w kopii laboratorium.");
            await RunGit(RepoDir, ct, "config", "core.autocrlf", "false").ConfigureAwait(false);
            string error = "";
            for (int attempt = 0; attempt < 2; attempt++)
            {
                await RunGit(RepoDir, ct, "worktree", "prune").ConfigureAwait(false);
                if (Directory.Exists(DeployDir) && (attempt > 0 || await ResolveCommitAsync("HEAD", ct, DeployDir).ConfigureAwait(false) == null))
                    await RemoveDeployDirAsync(ct).ConfigureAwait(false); // uszkodzona lub niespójna kopia – tworzona od nowa
                ProcessResult r;
                if (!Directory.Exists(DeployDir))
                    r = await RunGit(RepoDir, ct, "worktree", "add", "-f", "-B", DeployBranch, DeployDir, target).ConfigureAwait(false);
                else
                {
                    // Ewentualne przerwane scalenie po awarii, potem twarde przestawienie gałęzi.
                    await RunGit(DeployDir, ct, "merge", "--abort").ConfigureAwait(false);
                    await RunGit(DeployDir, ct, "reset", "-q", "--hard").ConfigureAwait(false);
                    r = await RunGit(DeployDir, ct, "checkout", "-q", "-f", "-B", DeployBranch, target).ConfigureAwait(false);
                }
                if (!r.Ok) { error = r.Tail(6); continue; }
                var clean = await RunGit(DeployDir, ct, "clean", "-q", "-ffdx").ConfigureAwait(false);
                if (!clean.Ok) { error = clean.Tail(6); continue; }
                string? head = await ResolveCommitAsync("HEAD", ct, DeployDir).ConfigureAwait(false);
                var st = await RunGit(DeployDir, ct, "status", "--porcelain", "--untracked-files=all").ConfigureAwait(false);
                if (string.Equals(head, target, StringComparison.OrdinalIgnoreCase) && st.Ok && st.Output.All(string.IsNullOrWhiteSpace))
                    return (true, target);
                error = "kopia wdrożeń nie odpowiada commitowi " + Short(target) + (st.Output.Count > 0 ? ": " + string.Join("; ", st.Output.Take(5)) : "");
            }
            return (false, "Nie udało się przygotować kopii wdrożeń: " + error);
        }

        private async Task RemoveDeployDirAsync(CancellationToken ct)
        {
            await RunGit(RepoDir, ct, "worktree", "remove", "--force", "--force", DeployDir).ConfigureAwait(false);
            if (Directory.Exists(DeployDir))
            {
                try
                {
                    // Pliki tylko do odczytu blokują usuwanie na Windows; dowiązań nie śledzimy (nie wychodzimy poza kopię).
                    var opts = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
                    foreach (var f in Directory.EnumerateFiles(DeployDir, "*", opts))
                        if ((File.GetAttributes(f) & FileAttributes.ReadOnly) != 0) File.SetAttributes(f, FileAttributes.Normal);
                    Directory.Delete(DeployDir, true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            await RunGit(RepoDir, ct, "worktree", "prune").ConfigureAwait(false);
        }

        /// <summary>git z pełnym wyjściem (bez obcinania do ostatnich linii) – do diffów audytu.</summary>
        private async Task<string> GitFull(string dir, CancellationToken ct, params string[] args)
        {
            var git = Git() ?? throw new InvalidOperationException("Nie znaleziono programu git. Zainstaluj Git for Windows (winget install Git.Git).");
            var r = await ProcessRunner.RunAsync(git, args, dir, timeout: TimeSpan.FromMinutes(10), ct: ct, keepLines: 2_000_000).ConfigureAwait(false);
            if (!r.Ok) throw new InvalidOperationException($"git {string.Join(" ", args)}: {r.Tail(6)}");
            return string.Join("\n", r.Output);
        }

        private static string Short(string sha) => sha.Length > 10 ? sha.Substring(0, 10) : sha;

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
