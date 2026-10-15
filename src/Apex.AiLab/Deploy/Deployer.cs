using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Apex.AiLab.Lab;
using Apex.AiLab.Pipeline;

namespace Apex.AiLab.Deploy
{
    public sealed class ReleaseInfo
    {
        public string Version { get; set; } = "";
        public string JobId { get; set; } = "";
        public string Title { get; set; } = "";
        public string Path { get; set; } = "";
        public string ExePath { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
        public string Commit { get; set; } = "";
    }

    public sealed class ReleaseHistory
    {
        public List<ReleaseInfo> Releases { get; set; } = new();
        /// <summary>Wersja aktywna po następnym uruchomieniu APEX (null = instalacja pierwotna).</summary>
        public string? Current { get; set; }
    }

    /// <summary>
    /// Wdrożenie WYŁĄCZNIE po decyzji użytkownika: połączenie gałęzi zadania z gałęzią wdrożeń, zbudowanie nowego wydania
    /// APEX Desktop w osobnym folderze i wskazanie go jako aktywnego dla skryptu startowego. Działający proces nie jest zmieniany –
    /// nowa wersja zaczyna działać po ponownym uruchomieniu APEX przez użytkownika. Poprzednie wydania zostają (wycofanie jednym kliknięciem).
    /// </summary>
    public sealed class Deployer
    {
        public const string ProtectedPhrase = "ZATWIERDZAM ZMIANY CHRONIONE";
        public const string WarningPhrase = "ZATWIERDZAM MIMO OSTRZEŻEŃ";

        private readonly LabConfigStore _cfg;
        private readonly LabStore _store;
        private readonly GitWorkspace _ws;
        private readonly string _releasesDir;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public Deployer(LabConfigStore cfg, LabStore store, GitWorkspace ws, string apexDataDir)
        {
            _cfg = cfg;
            _store = store;
            _ws = ws;
            _releasesDir = Path.Combine(apexDataDir, "wydania");
            Directory.CreateDirectory(_releasesDir);
        }

        public string ReleasesDir => _releasesDir;
        private string HistoryPath => Path.Combine(_releasesDir, "historia.json");
        private string PointerPath => Path.Combine(_releasesDir, "aktualne.txt");

        public ReleaseHistory History() => LabStore.ReadJson<ReleaseHistory>(HistoryPath) ?? new ReleaseHistory();

        /// <summary>Wymagane potwierdzenie (fraza) dla propozycji – null, gdy wystarczy zwykłe potwierdzenie w oknie dialogowym.</summary>
        public static string? RequiredPhrase(LabJob job) =>
            job.ProtectedTouched ? ProtectedPhrase : job.Status == JobStatus.NeedsAttention ? WarningPhrase : null;

        public async Task<string> ApproveAsync(string jobId, string? confirmation, string? note, CancellationToken ct)
        {
            if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false)) return "Inne wdrożenie jest w toku.";
            try
            {
                var job = _store.Job(jobId);
                if (job == null) return "Nie znaleziono propozycji.";
                if (job.Status is not (JobStatus.ReadyForApproval or JobStatus.NeedsAttention))
                    return "Tej propozycji nie można wdrożyć (status: " + job.Status + ").";
                if (job.Findings.Any(f => f.Severity == FindingSeverity.Blocking)) return "Propozycja ma ustalenia blokujące – wdrożenie niemożliwe.";
                string? phrase = RequiredPhrase(job);
                if (phrase != null && !string.Equals((confirmation ?? "").Trim(), phrase, StringComparison.Ordinal))
                    return $"Wymagane potwierdzenie: wpisz dokładnie \"{phrase}\".";

                job.Status = JobStatus.Deploying;
                job.StatusText = "Przygotowywanie wydania…";
                job.DecisionUtc = DateTime.UtcNow;
                job.UserDecisionNote = note;
                _store.SaveJob(job);
                _store.AddEvent(job.Id, "stage", "Użytkownik zatwierdził zmianę – łączenie z gałęzią wdrożeń i budowa wydania.");

                var (ok, commitOrError) = await _ws.MergeForDeployAsync(job.Id, job.BaseCommit, ct).ConfigureAwait(false);
                if (!ok) return Fail(job, commitOrError);

                string version = DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + "-" + job.Id.Substring(job.Id.Length - 4);
                string outDir = Path.Combine(_releasesDir, version);
                var dotnet = Verification.Dotnet();
                if (dotnet == null) return Fail(job, "Brak .NET SDK – nie można zbudować wydania.");
                string rid = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win-x64" : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx-x64" : "linux-x64";
                var args = new[]
                {
                    "publish", "src/Apex.Desktop/Apex.Desktop.csproj", "-c", "Release", "-r", rid, "--self-contained", "true",
                    "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:IsTransformWebConfigDisabled=true", "-o", Path.Combine(outDir, "APEX-Desktop"),
                };
                _store.AddEvent(job.Id, "command", "▶ dotnet publish APEX Desktop (" + rid + ")");
                var r = await ProcessRunner.RunAsync(dotnet, args, _ws.DeployDir, timeout: TimeSpan.FromMinutes(30), ct: ct).ConfigureAwait(false);
                if (!r.Ok) return Fail(job, "Budowa wydania nieudana: " + r.Tail(8));

                var notes = new List<string>();
                // cBoty cTrader – nowe pliki .algo, gdy zmiana dotyczy silnika lub mostów (instalacja w cTrader ręcznie, dwuklikiem).
                if (job.Files.Any(f => f.Path.StartsWith("src/Apex.Core/", StringComparison.Ordinal) || f.Path.StartsWith("src/Platforms/", StringComparison.Ordinal)))
                {
                    foreach (var sln in new[] { "src/Platforms/cTrader/ApexBridge/ApexBridge.sln", "src/Platforms/cTrader/ApexTradingAI/ApexTradingAI.sln" })
                    {
                        if (!File.Exists(Path.Combine(_ws.DeployDir, sln))) continue;
                        var b = await ProcessRunner.RunAsync(dotnet, new[] { "build", sln, "-c", "Release", "-nologo" }, _ws.DeployDir, timeout: TimeSpan.FromMinutes(15), ct: ct).ConfigureAwait(false);
                        if (!b.Ok) { notes.Add("Nie udało się zbudować " + Path.GetFileName(sln) + "."); continue; }
                    }
                    Directory.CreateDirectory(Path.Combine(outDir, "cTrader"));
                    foreach (var algo in Directory.EnumerateFiles(Path.Combine(_ws.DeployDir, "src", "Platforms"), "*.algo", SearchOption.AllDirectories).Where(p => p.Contains(Path.DirectorySeparatorChar + "Release" + Path.DirectorySeparatorChar)))
                        File.Copy(algo, Path.Combine(outDir, "cTrader", Path.GetFileName(algo)), true);
                    if (job.Files.Any(f => f.Path.StartsWith("src/Platforms/", StringComparison.Ordinal) || f.Path.StartsWith("src/Apex.Core/", StringComparison.Ordinal)))
                        notes.Add("Zmiana dotyczy silnika lub mostów: jeśli używasz wersji natywnej cTrader lub mostu, zainstaluj nowe pliki .algo z folderu wydania (dwuklik) i uruchom instancje ponownie.");
                }

                string exe = Path.Combine(outDir, "APEX-Desktop", RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ApexDesktop.exe" : "ApexDesktop");
                if (!File.Exists(exe)) return Fail(job, "Wydanie nie zawiera pliku APEX Desktop.");
                var hist = History();
                hist.Releases.Add(new ReleaseInfo { Version = version, JobId = job.Id, Title = job.Title, Path = outDir, ExePath = exe, CreatedUtc = DateTime.UtcNow, Commit = commitOrError });
                hist.Current = version;
                LabStore.WriteJson(HistoryPath, hist);
                File.WriteAllText(PointerPath, exe);

                notes.Insert(0, $"Wydanie {version} jest gotowe. Działający APEX Desktop NIE został zmieniony – zamknij go i uruchom ponownie skrótem „Uruchom APEX.cmd”, aby aktywować nową wersję. Pozycje są chronione przez SL u brokera, a stan silnika zostanie wczytany z dysku.");
                job.Deployment = new DeploymentInfo
                {
                    ApprovedUtc = DateTime.UtcNow,
                    Version = version,
                    ReleasePath = outDir,
                    ExePath = exe,
                    MergeCommit = commitOrError,
                    ProtectedConfirmed = job.ProtectedTouched,
                    Notes = notes,
                };
                job.Status = JobStatus.Deployed;
                job.StatusText = "Wdrożono jako wydanie " + version + " – aktywne po ponownym uruchomieniu APEX.";
                _store.SaveJob(job);
                _store.AddEvent(job.Id, "stage", job.StatusText);
                await _ws.RemoveWorktreeAsync(job.Id, CancellationToken.None).ConfigureAwait(false);
                return notes[0];
            }
            finally
            {
                _gate.Release();
            }
        }

        private string Fail(LabJob job, string error)
        {
            job.Status = JobStatus.ReadyForApproval;
            job.StatusText = "Wdrożenie nieudane: " + error;
            _store.SaveJob(job);
            _store.AddEvent(job.Id, "error", job.StatusText);
            return job.StatusText;
        }

        public async Task<string> RejectAsync(string jobId, string? note, CancellationToken ct)
        {
            var job = _store.Job(jobId);
            if (job == null) return "Nie znaleziono propozycji.";
            if (job.Status is JobStatus.Running or JobStatus.Deploying or JobStatus.Deployed) return "Tej propozycji nie można teraz odrzucić.";
            job.Status = JobStatus.RejectedByUser;
            job.StatusText = "Odrzucona przez użytkownika" + (string.IsNullOrWhiteSpace(note) ? "." : ": " + note);
            job.UserDecisionNote = note;
            job.DecisionUtc = DateTime.UtcNow;
            _store.SaveJob(job);
            _store.AddEvent(job.Id, "stage", job.StatusText);
            await _ws.RemoveWorktreeAsync(job.Id, ct).ConfigureAwait(false);
            return "Propozycja odrzucona (gałąź " + job.Branch + " zachowana do wglądu).";
        }

        /// <summary>Wycofanie: aktywne staje się poprzednie wydanie lub instalacja pierwotna.</summary>
        public string Rollback()
        {
            var hist = History();
            if (hist.Current == null) return "Aktywna jest instalacja pierwotna – nie ma czego wycofywać.";
            var idx = hist.Releases.FindIndex(r => r.Version == hist.Current);
            var currentRel = idx >= 0 ? hist.Releases[idx] : null;
            var previous = idx > 0 ? hist.Releases[idx - 1] : null;
            hist.Current = previous?.Version;
            LabStore.WriteJson(HistoryPath, hist);
            if (previous != null) File.WriteAllText(PointerPath, previous.ExePath);
            else if (File.Exists(PointerPath)) File.Delete(PointerPath);
            if (currentRel != null)
            {
                var job = _store.Job(currentRel.JobId);
                if (job != null)
                {
                    job.Status = JobStatus.RolledBack;
                    job.StatusText = "Wycofano wydanie " + currentRel.Version + ".";
                    _store.SaveJob(job);
                    _store.AddEvent(job.Id, "warning", job.StatusText);
                }
            }
            return (previous != null ? $"Przywrócono wydanie {previous.Version}." : "Przywrócono instalację pierwotną.") +
                   " Uruchom ponownie APEX Desktop (Uruchom APEX.cmd), aby zmiana zaczęła działać.";
        }
    }
}
