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
using Apex.Core.Adaptive;

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
        /// <summary>Commit wydania (scalenie na gałęzi apex-wdrozone).</summary>
        public string Commit { get; set; } = "";
        /// <summary>Baza wdrożenia: commit wydania aktywnego w chwili budowy albo baza zadania (instalacja pierwotna).</summary>
        public string BaseCommit { get; set; } = "";
        /// <summary>Wydanie wycofane przez użytkownika – nigdy nie wraca jako „poprzednia wersja”.</summary>
        public bool Withdrawn { get; set; }
        public DateTime? WithdrawnUtc { get; set; }
    }

    public sealed class ReleaseHistory
    {
        public List<ReleaseInfo> Releases { get; set; } = new();
        /// <summary>Wersja aktywna po następnym uruchomieniu APEX (null = instalacja pierwotna).</summary>
        public string? Current { get; set; }
    }

    /// <summary>Wynik budowy wydania.</summary>
    public sealed class ReleaseBuildOutput
    {
        /// <summary>Opis błędu (null = wydanie zbudowane).</summary>
        public string? Error { get; set; }
        public string ExePath { get; set; } = "";
        public List<string> Notes { get; set; } = new();
    }

    /// <summary>
    /// Kompilacja, testy i budowa wydania z kopii wdrożeń. Wydzielone z <see cref="Deployer"/>, aby logikę gałęzi wdrożeń,
    /// linii wydań i wycofań dało się testować bez wielominutowego dotnet publish.
    /// </summary>
    public interface IReleaseBuilder
    {
        /// <summary>Kompilacja i testy regresyjne scalonego drzewa (testy tylko po udanej kompilacji).</summary>
        Task<(BuildResult build, TestResult? tests)> VerifyAsync(string dir, string resultsDir, Action<string, string> log, CancellationToken ct);

        /// <summary>Budowa wydania APEX Desktop (i cBotów, gdy wdrażana różnica dotyczy silnika lub mostów) do <paramref name="outDir"/>.</summary>
        Task<ReleaseBuildOutput> PublishAsync(string dir, string outDir, IReadOnlyList<ChangedFile> shippedFiles, Action<string, string> log, CancellationToken ct);
    }

    /// <summary>Budowa wydania przez .NET SDK (domyślna).</summary>
    public sealed class DotnetReleaseBuilder : IReleaseBuilder
    {
        private readonly Func<RepoSettings> _repo;

        public DotnetReleaseBuilder(Func<RepoSettings> repo)
        {
            _repo = repo;
        }

        public static readonly string[] CBotSolutions = { "src/Platforms/cTrader/ApexBridge/ApexBridge.sln", "src/Platforms/cTrader/ApexTradingAI/ApexTradingAI.sln" };

        /// <summary>
        /// Budowa cBota do folderu wydania: plik .algo powstaje (AlgoBuild), ale NIE jest kopiowany do cTrader (AlgoPublish=false –
        /// parametr globalny ma pierwszeństwo przed projektem i Directory.Build.*). Instalacja w cTrader pozostaje ręczna (dwuklik).
        /// </summary>
        public static string[] CBotBuildArgs(string sln) => new[] { "build", sln, "-c", "Release", "-nologo", "-p:AlgoPublish=false" };

        public async Task<(BuildResult build, TestResult? tests)> VerifyAsync(string dir, string resultsDir, Action<string, string> log, CancellationToken ct)
        {
            var v = new Verification(_repo(), log);
            var build = await v.BuildAsync(dir, ct).ConfigureAwait(false);
            if (!build.Ok) return (build, null);
            return (build, await v.TestAsync(dir, resultsDir, ct).ConfigureAwait(false));
        }

        public async Task<ReleaseBuildOutput> PublishAsync(string dir, string outDir, IReadOnlyList<ChangedFile> shippedFiles, Action<string, string> log, CancellationToken ct)
        {
            var res = new ReleaseBuildOutput();
            var dotnet = Verification.Dotnet();
            if (dotnet == null) { res.Error = "Brak .NET SDK – nie można zbudować wydania."; return res; }
            string rid = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win-x64" : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx-x64" : "linux-x64";
            var args = new[]
            {
                "publish", "src/Apex.Desktop/Apex.Desktop.csproj", "-c", "Release", "-r", rid, "--self-contained", "true",
                "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:IsTransformWebConfigDisabled=true", "-o", Path.Combine(outDir, "APEX-Desktop"),
            };
            log("command", "▶ dotnet publish APEX Desktop (" + rid + ")");
            var r = await ProcessRunner.RunAsync(dotnet, args, dir, timeout: TimeSpan.FromMinutes(30), ct: ct).ConfigureAwait(false);
            if (!r.Ok) { res.Error = "Budowa wydania nieudana: " + r.Tail(8); return res; }

            // cBoty cTrader – nowe pliki .algo, gdy zmiana dotyczy silnika lub mostów (instalacja w cTrader ręcznie, dwuklikiem).
            if (shippedFiles.Any(f => f.Path.StartsWith("src/Apex.Core/", StringComparison.Ordinal) || f.Path.StartsWith("src/Platforms/", StringComparison.Ordinal)))
            {
                foreach (var sln in CBotSolutions)
                {
                    if (!File.Exists(Path.Combine(dir, sln))) continue;
                    var cargs = CBotBuildArgs(sln);
                    log("command", "▶ dotnet " + string.Join(" ", cargs));
                    var b = await ProcessRunner.RunAsync(dotnet, cargs, dir, timeout: TimeSpan.FromMinutes(15), ct: ct).ConfigureAwait(false);
                    if (!b.Ok) res.Notes.Add("Nie udało się zbudować " + Path.GetFileName(sln) + ".");
                }
                Directory.CreateDirectory(Path.Combine(outDir, "cTrader"));
                string platforms = Path.Combine(dir, "src", "Platforms");
                if (Directory.Exists(platforms))
                    foreach (var algo in Directory.EnumerateFiles(platforms, "*.algo", SearchOption.AllDirectories).Where(p => p.Contains(Path.DirectorySeparatorChar + "Release" + Path.DirectorySeparatorChar)))
                        File.Copy(algo, Path.Combine(outDir, "cTrader", Path.GetFileName(algo)), true);
                res.Notes.Add("Zmiana dotyczy silnika lub mostów: jeśli używasz wersji natywnej cTrader lub mostu, zainstaluj nowe pliki .algo z folderu wydania (dwuklik) i uruchom instancje ponownie.");
            }
            res.ExePath = Path.Combine(outDir, "APEX-Desktop", RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ApexDesktop.exe" : "ApexDesktop");
            return res;
        }
    }

    /// <summary>
    /// Wdrożenie WYŁĄCZNIE po decyzji użytkownika: zaudytowany commit zadania jest scalany z commitem AKTYWNEGO wydania
    /// (gałąź apex-wdrozone zawsze startuje od niego, więc zmiany odrzucone, nieudane i wycofane nie wracają), scalona wersja
    /// jest ponownie audytowana, kompilowana i testowana, a dopiero potem budowana jako nowe wydanie APEX Desktop w osobnym folderze
    /// i wskazywana jako aktywna dla skryptu startowego. Działający proces nie jest zmieniany – nowa wersja zaczyna działać po ponownym
    /// uruchomieniu APEX przez użytkownika. Poprzednie wydania zostają (wycofanie jednym kliknięciem).
    /// </summary>
    public sealed class Deployer
    {
        public const string ProtectedPhrase = "ZATWIERDZAM ZMIANY CHRONIONE";
        public const string WarningPhrase = "ZATWIERDZAM MIMO OSTRZEŻEŃ";

        private readonly LabConfigStore _cfg;
        private readonly LabStore _store;
        private readonly GitWorkspace _ws;
        private readonly IReleaseBuilder _builder;
        private readonly string _releasesDir;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public Deployer(LabConfigStore cfg, LabStore store, GitWorkspace ws, string apexDataDir, IReleaseBuilder? builder = null)
        {
            _cfg = cfg;
            _store = store;
            _ws = ws;
            _builder = builder ?? new DotnetReleaseBuilder(() => cfg.Config.Repo);
            _releasesDir = Path.Combine(apexDataDir, "wydania");
            Directory.CreateDirectory(_releasesDir);
        }

        public string ReleasesDir => _releasesDir;
        private string HistoryPath => Path.Combine(_releasesDir, "historia.json");
        private string PointerPath => Path.Combine(_releasesDir, "aktualne.txt");

        public ReleaseHistory History() => LabStore.ReadJson<ReleaseHistory>(HistoryPath) ?? new ReleaseHistory();

        /// <summary>
        /// Historia do zmiany (wdrożenie, wycofanie). Nieczytelny plik NIE może zostać potraktowany jak pusta historia –
        /// zapis nadpisałby listę wydań i cel wycofania.
        /// </summary>
        private ReleaseHistory LoadHistoryForUpdate()
        {
            for (int attempt = 1; ; attempt++)
            {
                if (!File.Exists(HistoryPath)) return new ReleaseHistory();
                var h = LabStore.ReadJson<ReleaseHistory>(HistoryPath);
                if (h != null) return h;
                if (attempt >= 4)
                    throw new InvalidOperationException("Nie można odczytać historii wydań (" + HistoryPath + ") – plik jest zablokowany lub uszkodzony. Wdrożenia i wycofania są wstrzymane do czasu jego naprawy.");
                Thread.Sleep(100 * attempt);
            }
        }

        /// <summary>
        /// Commit aktywnego wydania (null = aktywna instalacja pierwotna). Na nim powinny powstawać kopie robocze nowych zadań,
        /// aby weryfikacja dotyczyła tego, co zostanie wdrożone.
        /// </summary>
        public string? ActiveReleaseCommit()
        {
            var h = History();
            if (h.Current == null) return null;
            var r = h.Releases.FirstOrDefault(x => x.Version == h.Current);
            return r == null || string.IsNullOrWhiteSpace(r.Commit) ? null : r.Commit;
        }

        /// <summary>Baza wdrożenia: commit aktywnego wydania, a gdy aktywna jest instalacja pierwotna – baza zadania.</summary>
        private static (string? commit, string label, string? error) DeployBaseFor(ReleaseHistory hist, LabJob job)
        {
            if (hist.Current == null)
                return string.IsNullOrWhiteSpace(job.BaseCommit)
                    ? (null, "", "Propozycja nie ma zapisanego commitu bazowego – wdrożenie niemożliwe.")
                    : (job.BaseCommit, "instalacji pierwotnej (baza zadania)", null);
            var cur = hist.Releases.FirstOrDefault(r => r.Version == hist.Current);
            if (cur == null || string.IsNullOrWhiteSpace(cur.Commit))
                return (null, "", $"Historia wydań jest niespójna: aktywne wydanie {hist.Current} nie ma zapisanego commitu. Wycofaj je przed kolejnym wdrożeniem.");
            return (cur.Commit, "wydania " + cur.Version, null);
        }

        /// <summary>
        /// Wymagane potwierdzenie (fraza) bez znajomości aktywnego wydania – null, gdy wystarczy zwykłe potwierdzenie w oknie dialogowym.
        /// Panel powinien używać <see cref="RequiredPhraseFor"/>, które zwraca dokładnie to, czego zażąda <see cref="ApproveAsync"/>.
        /// </summary>
        public static string? RequiredPhrase(LabJob job) =>
            job.ProtectedTouched ? ProtectedPhrase : VerificationIncomplete(job) ? WarningPhrase : null;

        /// <summary>Wymagana fraza z uwzględnieniem aktywnego wydania (do pokazania PRZED zatwierdzeniem).</summary>
        public string? RequiredPhraseFor(LabJob job) => ConfirmationFor(job).phrase;

        /// <summary>Wymagana fraza i jej powód (tekst dla panelu).</summary>
        public (string? phrase, string reason) ConfirmationFor(LabJob job)
        {
            string? deployBase = null;
            var hist = File.Exists(HistoryPath) ? LabStore.ReadJson<ReleaseHistory>(HistoryPath) : new ReleaseHistory();
            if (hist != null) deployBase = DeployBaseFor(hist, job).commit;
            return PhraseFor(job, deployBase);
        }

        private static (string? phrase, string reason) PhraseFor(LabJob job, string? deployBase)
        {
            if (job.ProtectedTouched)
                return (ProtectedPhrase, "Zmiana dotyczy modułów chronionych (RiskEngine, limity, bezpieczeństwo, wykonanie lub aktywne strategie).");
            if (job.ShippedProtected && deployBase != null && SameCommit(job.ShippedAuditBase, deployBase))
                return (ProtectedPhrase, "Wdrażana różnica względem aktywnego wydania dotyka modułów chronionych (RiskEngine, limity, bezpieczeństwo, wykonanie lub aktywne strategie).");
            if (deployBase == null || !SameCommit(deployBase, job.BaseCommit))
                return (WarningPhrase, "Aktywne wydanie różni się od wersji, na której sprawdzono zmianę – połączenia nie zweryfikowano w laboratorium. Przed wydaniem zostanie ponownie zaudytowane, skompilowane i przetestowane (bez backtestu).");
            if (VerificationIncomplete(job))
                return (WarningPhrase, "Weryfikacja jest niepełna (np. bez backtestu).");
            return (null, "");
        }

        /// <summary>Niepełna weryfikacja – z utrwalonych faktów, nie tylko z bieżącego statusu (nieudane wdrożenie nie może zdjąć ostrzeżenia).</summary>
        private static bool VerificationIncomplete(LabJob job) =>
            job.Status == JobStatus.NeedsAttention || job.StatusBeforeDeploy == JobStatus.NeedsAttention || job.Backtest is { Ran: false };

        private static bool SameCommit(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            a = a.Trim();
            b = b.Trim();
            if (a.Length == b.Length) return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            return Math.Min(a.Length, b.Length) >= 7 && (a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase));
        }

        private static string Short(string sha) => sha.Length > 10 ? sha.Substring(0, 10) : sha;

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
                if (string.IsNullOrWhiteSpace(job.HeadCommit)) return "Propozycja nie ma zapisanego, zaudytowanego commitu – wdrożenie niemożliwe.";
                ReleaseHistory hist;
                try { hist = LoadHistoryForUpdate(); }
                catch (InvalidOperationException ex) { return ex.Message; }
                var (deployBase, baseLabel, baseError) = DeployBaseFor(hist, job);
                if (deployBase == null) return baseError ?? "Nie można ustalić bazy wdrożenia.";
                string given = (confirmation ?? "").Trim();
                string? phrase = PhraseFor(job, deployBase).phrase;
                if (phrase != null && !string.Equals(given, phrase, StringComparison.Ordinal))
                    return $"Wymagane potwierdzenie: wpisz dokładnie \"{phrase}\".";

                var previous = job.Status;
                bool activated = false;
                try
                {
                    job.StatusBeforeDeploy = previous;
                    job.Status = JobStatus.Deploying;
                    job.StatusText = "Przygotowywanie wydania…";
                    job.DecisionUtc = DateTime.UtcNow;
                    job.UserDecisionNote = note;
                    _store.SaveJob(job);
                    _store.AddEvent(job.Id, "stage", $"Użytkownik zatwierdził zmianę – scalanie zaudytowanego commitu {Short(job.HeadCommit)} z gałęzią wdrożeń na bazie {baseLabel} ({Short(deployBase)}).");
                    void Log(string kind, string text) => _store.AddEvent(job.Id, kind, text);

                    // Commity wcześniejszych wydań muszą przetrwać przestawienie gałęzi wdrożeń (cel wycofania).
                    foreach (var r in hist.Releases.Where(r => !string.IsNullOrWhiteSpace(r.Commit)))
                        await _ws.MarkReleaseAsync(r.Version, r.Commit, ct).ConfigureAwait(false);

                    // 1. Propozycja nie może przemycić wydania spoza aktywnej linii (wycofanego lub zastąpionego).
                    string? lineage = await LineageProblemAsync(hist, deployBase, job, ct).ConfigureAwait(false);
                    if (lineage != null) return await FailAsync(job, previous, null, lineage).ConfigureAwait(false);

                    // 2. Kopia wdrożeń = baza wdrożenia + zaudytowany commit (nic więcej).
                    var (merged, mergeOrError) = await _ws.MergeForDeployAsync(job.Id, job.BaseCommit, ct, deployBase, job.HeadCommit).ConfigureAwait(false);
                    if (!merged) return await FailAsync(job, previous, deployBase, mergeOrError).ConfigureAwait(false);
                    string merge = mergeOrError;

                    // 3. Ponowny audyt tego, co faktycznie trafi do wydania (różnica względem aktywnej wersji).
                    var (files, diff) = await _ws.ChangesBetweenAsync(deployBase, merge, ct).ConfigureAwait(false);
                    try { File.WriteAllText(Path.Combine(_store.JobDir(job.Id), "wdrozenie.diff"), diff); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    var findings = ChangeAudit.Audit(files, diff, _cfg.Config.Policy, await ActiveStrategiesAsync(deployBase, ct).ConfigureAwait(false));
                    job.ShippedAuditBase = deployBase;
                    job.ShippedFindings = findings;
                    job.ShippedProtected = files.Any(f => f.Protected);
                    _store.SaveJob(job);
                    int blocking = findings.Count(f => f.Severity == FindingSeverity.Blocking);
                    Log(blocking > 0 ? "error" : "stage", $"Audyt wdrażanej różnicy względem {baseLabel}: {files.Count} plików (+{files.Sum(f => f.Added)}/−{files.Sum(f => f.Removed)}), blokujących {blocking}, chronionych {files.Count(f => f.Protected)}.");
                    if (files.Count == 0) return await FailAsync(job, previous, deployBase, "Scalona wersja nie różni się od bazy wdrożenia – nie ma czego wdrażać.").ConfigureAwait(false);
                    if (blocking > 0)
                        return await FailAsync(job, previous, deployBase, "Audyt wdrażanej różnicy wykrył ustalenia blokujące: " + string.Join("; ", findings.Where(f => f.Severity == FindingSeverity.Blocking).Select(f => f.Rule + (f.File.Length > 0 ? " (" + f.File + ")" : "")).Distinct().Take(5)) + ".").ConfigureAwait(false);
                    if (job.ShippedProtected && !string.Equals(given, ProtectedPhrase, StringComparison.Ordinal))
                        return await FailAsync(job, previous, deployBase, $"Wdrażana różnica względem {baseLabel} dotyka modułów chronionych ({string.Join(", ", files.Where(f => f.Protected).Select(f => f.Path).Take(5))}). Zatwierdź ponownie, wpisując dokładnie \"{ProtectedPhrase}\".").ConfigureAwait(false);

                    // 4. Kompilacja i testy regresyjne scalonej wersji – zanim cokolwiek zostanie zbudowane jako wydanie.
                    SetStatus(job, "Kompilacja i testy regresyjne scalonej wersji…");
                    var (build, tests) = await _builder.VerifyAsync(_ws.DeployDir, Path.Combine(_store.JobDir(job.Id), "testy-wdrozenie"), Log, ct).ConfigureAwait(false);
                    if (!build.Ok)
                        return await FailAsync(job, previous, deployBase, "Scalona wersja się nie kompiluje: " + string.Join("; ", build.Errors.Take(3).DefaultIfEmpty(string.Join("; ", build.Steps)))).ConfigureAwait(false);
                    if (tests == null || !tests.Ran || tests.Failed > 0)
                        return await FailAsync(job, previous, deployBase, $"Testy regresyjne scalonej wersji niezaliczone ({tests?.Passed ?? 0}/{tests?.Total ?? 0})" + (tests?.FailedNames.Count > 0 ? ": " + string.Join("; ", tests.FailedNames.Take(5)) : "") + ".").ConfigureAwait(false);
                    Log("stage", $"Scalona wersja: kompilacja OK, testy {tests.Passed}/{tests.Total} zaliczone.");

                    // 5. Budowa wydania w nowym folderze.
                    string version = NewVersion(hist, job);
                    string outDir = Path.Combine(_releasesDir, version);
                    SetStatus(job, "Budowa wydania " + version + "…");
                    var pub = await _builder.PublishAsync(_ws.DeployDir, outDir, files, Log, ct).ConfigureAwait(false);
                    if (pub.Error != null || string.IsNullOrEmpty(pub.ExePath) || !File.Exists(pub.ExePath))
                    {
                        TryDeleteDir(outDir);
                        return await FailAsync(job, previous, deployBase, pub.Error ?? "Wydanie nie zawiera pliku APEX Desktop.").ConfigureAwait(false);
                    }

                    // 6. Aktywacja: historia + wskaźnik dla skryptu startowego (z przywróceniem przy błędzie zapisu).
                    var fresh = LoadHistoryForUpdate();
                    if (fresh.Current != hist.Current)
                    {
                        TryDeleteDir(outDir);
                        return await FailAsync(job, previous, deployBase, "Aktywne wydanie zmieniło się w trakcie wdrożenia – zatwierdź propozycję ponownie.").ConfigureAwait(false);
                    }
                    await _ws.MarkReleaseAsync(version, merge, ct).ConfigureAwait(false);
                    Activate(fresh, new ReleaseInfo
                    {
                        Version = version, JobId = job.Id, Title = job.Title, Path = outDir, ExePath = pub.ExePath, CreatedUtc = DateTime.UtcNow,
                        Commit = merge, BaseCommit = deployBase,
                    });
                    activated = true;

                    // 7. Wydanie jest aktywne – dalsze błędy (zapis stanu zadania, sprzątanie) niczego już nie cofają.
                    var notes = pub.Notes.ToList();
                    notes.Insert(0, $"Wydanie {version} jest gotowe. Działający APEX Desktop NIE został zmieniony – zamknij go i uruchom ponownie skrótem „Uruchom APEX.cmd”, aby aktywować nową wersję. Pozycje są chronione przez SL u brokera, a stan silnika zostanie wczytany z dysku.");
                    try
                    {
                        job.Deployment = new DeploymentInfo
                        {
                            ApprovedUtc = DateTime.UtcNow,
                            Version = version,
                            ReleasePath = outDir,
                            ExePath = pub.ExePath,
                            MergeCommit = merge,
                            DeployBase = deployBase,
                            ProtectedConfirmed = string.Equals(given, ProtectedPhrase, StringComparison.Ordinal),
                            MergedBuildSteps = build.Steps.ToList(),
                            MergedTests = tests,
                            Notes = notes,
                        };
                        job.Status = JobStatus.Deployed;
                        job.StatusText = "Wdrożono jako wydanie " + version + " – aktywne po ponownym uruchomieniu APEX.";
                        _store.SaveJob(job);
                        _store.AddEvent(job.Id, "stage", job.StatusText);
                    }
                    catch (Exception) { /* wydanie aktywne; stan zadania w pamięci jest poprawny */ }
                    try { await _ws.RemoveWorktreeAsync(job.Id, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception) { /* sprzątanie nie zmienia wyniku */ }
                    return notes[0];
                }
                catch (Exception ex) when (!activated)
                {
                    // Żaden wyjątek nie może zostawić zadania w stanie „Deploying” ani scalenia w gałęzi wdrożeń.
                    return await FailAsync(job, previous, deployBase, "Nieoczekiwany błąd: " + ex.Message).ConfigureAwait(false);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        private void SetStatus(LabJob job, string text)
        {
            job.StatusText = text;
            _store.SaveJob(job);
            _store.AddEvent(job.Id, "stage", text);
        }

        /// <summary>
        /// Nieudane wdrożenie: gałąź wdrożeń wraca do bazy (żadne scalenie nie zostaje na przyszłość), a zadanie – do statusu sprzed
        /// zatwierdzenia (np. „Wymaga uwagi” nadal wymaga frazy ostrzeżenia).
        /// </summary>
        private async Task<string> FailAsync(LabJob job, JobStatus previous, string? resetTo, string error)
        {
            string extra = "";
            if (resetTo != null)
            {
                try
                {
                    var (ok, err) = await _ws.ResetDeployAsync(resetTo, CancellationToken.None).ConfigureAwait(false);
                    if (!ok) extra = " Uwaga: kopii wdrożeń nie przywrócono (" + err + ") – zostanie wyczyszczona przy następnym wdrożeniu.";
                }
                catch (Exception ex) { extra = " Uwaga: kopii wdrożeń nie przywrócono (" + ex.Message + ") – zostanie wyczyszczona przy następnym wdrożeniu."; }
            }
            job.Status = previous;
            job.StatusText = "Wdrożenie nieudane: " + error + extra;
            try { _store.SaveJob(job); }
            catch (Exception) { /* status w pamięci jest przywrócony */ }
            try { _store.AddEvent(job.Id, "error", job.StatusText); }
            catch (Exception) { }
            return job.StatusText;
        }

        /// <summary>
        /// Każde wydanie osiągalne z propozycji lub z bazy wdrożenia musi należeć do aktywnej wersji (być nią lub jej przodkiem).
        /// Inaczej wdrożenie przywróciłoby wydanie wycofane lub zastąpione – bez decyzji użytkownika.
        /// </summary>
        private async Task<string?> LineageProblemAsync(ReleaseHistory hist, string deployBase, LabJob job, CancellationToken ct)
        {
            string? active = hist.Current == null ? null : hist.Releases.FirstOrDefault(r => r.Version == hist.Current)?.Commit;
            foreach (var rel in hist.Releases)
            {
                if (string.IsNullOrWhiteSpace(rel.Commit)) continue;
                if (!string.IsNullOrWhiteSpace(active) && await _ws.IsAncestorAsync(rel.Commit, active!, ct).ConfigureAwait(false)) continue;
                if (await _ws.IsAncestorAsync(rel.Commit, job.HeadCommit, ct).ConfigureAwait(false) || await _ws.IsAncestorAsync(rel.Commit, deployBase, ct).ConfigureAwait(false))
                    return $"Propozycja zawiera wydanie {rel.Version}, które nie należy do aktywnej wersji (zostało wycofane lub zastąpione). Jej wdrożenie przywróciłoby zmiany, których ta decyzja nie obejmuje – uruchom zadanie ponownie na aktywnej wersji.";
            }
            return null;
        }

        /// <summary>Strategie aktywne w profilu scalonej wersji LUB bazy wdrożenia (brak lub błąd profilu = wszystkie chronione).</summary>
        private async Task<List<string>> ActiveStrategiesAsync(string deployBase, CancellationToken ct)
        {
            string merged = Path.Combine(_ws.DeployDir, "config", "profile_default.json");
            var sources = new[]
            {
                File.Exists(merged) ? File.ReadAllText(merged) : null,
                await _ws.ShowFileAsync(deployBase, "config/profile_default.json", ct).ConfigureAwait(false),
            };
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var json in sources)
            {
                if (json == null) return ChangeAudit.StrategyFiles.Keys.ToList();
                try
                {
                    var p = StrategyProfile.FromJson(json);
                    set.UnionWith(p.Entries.Where(e => e.Enabled && e.Status != ValidationStatus.Rejected).Select(e => e.Strategy.ToString()));
                }
                catch (Exception) { return ChangeAudit.StrategyFiles.Keys.ToList(); }
            }
            return set.ToList();
        }

        private string NewVersion(ReleaseHistory hist, LabJob job)
        {
            string v0 = DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + "-" + (job.Id.Length > 4 ? job.Id.Substring(job.Id.Length - 4) : job.Id);
            string v = v0;
            for (int n = 2; hist.Releases.Any(r => r.Version == v) || Directory.Exists(Path.Combine(_releasesDir, v)); n++) v = v0 + "-" + n;
            return v;
        }

        private void Activate(ReleaseHistory hist, ReleaseInfo rel)
        {
            string? previousCurrent = hist.Current;
            hist.Releases.Add(rel);
            hist.Current = rel.Version;
            try
            {
                LabStore.WriteJson(HistoryPath, hist);
            }
            catch
            {
                hist.Releases.Remove(rel);
                hist.Current = previousCurrent;
                throw;
            }
            try
            {
                WriteTextAtomic(PointerPath, rel.ExePath);
            }
            catch
            {
                // Wskaźnik dla skryptu startowego nie zapisany – historia wraca do poprzedniego stanu.
                hist.Releases.Remove(rel);
                hist.Current = previousCurrent;
                try { LabStore.WriteJson(HistoryPath, hist); } catch (Exception) { }
                throw;
            }
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

        /// <summary>Wycofanie: aktywne staje się najnowsze wcześniejsze wydanie, którego nie wycofano, lub instalacja pierwotna.</summary>
        public string Rollback() => RollbackAsync(CancellationToken.None).GetAwaiter().GetResult();

        /// <summary>Wycofanie aktywnego wydania (wersja asynchroniczna; gałąź wdrożeń wraca do commitu przywróconego wydania).</summary>
        public async Task<string> RollbackAsync(CancellationToken ct)
        {
            if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false)) return "Trwa wdrożenie – wycofanie będzie możliwe po jego zakończeniu.";
            try
            {
                ReleaseHistory hist;
                try { hist = LoadHistoryForUpdate(); }
                catch (InvalidOperationException ex) { return ex.Message; }
                if (hist.Current == null) return "Aktywna jest instalacja pierwotna – nie ma czego wycofywać.";
                int idx = hist.Releases.FindIndex(r => r.Version == hist.Current);
                var currentRel = idx >= 0 ? hist.Releases[idx] : null;
                // Zgodność ze starszą historią: wydanie zadania oznaczonego jako wycofane traktujemy jak wycofane.
                foreach (var r in hist.Releases)
                    if (!r.Withdrawn && r != currentRel && _store.Job(r.JobId)?.Status == JobStatus.RolledBack) r.Withdrawn = true;
                ReleaseInfo? previous = null;
                for (int i = idx - 1; i >= 0; i--)
                {
                    var r = hist.Releases[i];
                    if (r.Withdrawn || string.IsNullOrWhiteSpace(r.ExePath) || !File.Exists(r.ExePath)) continue;
                    previous = r;
                    break;
                }
                string? oldCurrent = hist.Current;
                if (currentRel != null)
                {
                    currentRel.Withdrawn = true;
                    currentRel.WithdrawnUtc = DateTime.UtcNow;
                }
                hist.Current = previous?.Version;
                try
                {
                    LabStore.WriteJson(HistoryPath, hist);
                }
                catch (Exception ex)
                {
                    return "Wycofanie nieudane – nie zapisano historii wydań: " + ex.Message;
                }
                try
                {
                    if (previous != null) WriteTextAtomic(PointerPath, previous.ExePath);
                    else DeleteWithRetry(PointerPath);
                }
                catch (Exception ex)
                {
                    if (currentRel != null) { currentRel.Withdrawn = false; currentRel.WithdrawnUtc = null; }
                    hist.Current = oldCurrent;
                    try { LabStore.WriteJson(HistoryPath, hist); } catch (Exception) { }
                    return "Wycofanie nieudane – nie zapisano wskaźnika aktywnego wydania: " + ex.Message;
                }

                if (currentRel != null)
                {
                    var job = _store.Job(currentRel.JobId);
                    if (job != null)
                    {
                        job.Status = JobStatus.RolledBack;
                        job.StatusText = "Wycofano wydanie " + currentRel.Version + ".";
                        TrySave(job, "warning");
                    }
                }
                if (previous != null)
                {
                    var job = _store.Job(previous.JobId);
                    if (job != null && job.Status == JobStatus.Deployed)
                    {
                        job.StatusText = "Wdrożono jako wydanie " + previous.Version + " – ponownie aktywne po wycofaniu wydania " + oldCurrent + " (po ponownym uruchomieniu APEX).";
                        TrySave(job, "stage");
                    }
                    // Gałąź wdrożeń = commit aktywnego wydania (i tak jest ustawiana od nowa przed każdym wdrożeniem).
                    if (!string.IsNullOrWhiteSpace(previous.Commit))
                    {
                        try { await _ws.ResetDeployAsync(previous.Commit, ct).ConfigureAwait(false); }
                        catch (Exception) { /* zostanie przestawiona przy następnym wdrożeniu */ }
                    }
                }
                return (previous != null ? $"Przywrócono wydanie {previous.Version}." : "Przywrócono instalację pierwotną.") +
                       " Uruchom ponownie APEX Desktop (Uruchom APEX.cmd), aby zmiana zaczęła działać.";
            }
            finally
            {
                _gate.Release();
            }
        }

        private void TrySave(LabJob job, string kind)
        {
            try
            {
                _store.SaveJob(job);
                _store.AddEvent(job.Id, kind, job.StatusText);
            }
            catch (Exception) { /* stan w pamięci jest aktualny */ }
        }

        // ------------------------------------------------------------------ pliki

        private static bool Transient(Exception ex) =>
            ex is UnauthorizedAccessException || (ex is IOException && ex is not FileNotFoundException && ex is not DirectoryNotFoundException);

        /// <summary>Zapis atomowy wskaźnika (plik tymczasowy + zamiana) z ponowieniami, gdy inny proces chwilowo czyta plik.</summary>
        private static void WriteTextAtomic(string path, string text)
        {
            string tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                File.WriteAllText(tmp, text);
                for (int attempt = 1; ; attempt++)
                {
                    try { File.Move(tmp, path, true); return; }
                    catch (Exception ex) when (Transient(ex) && attempt < 6) { Thread.Sleep(25 * attempt * attempt); }
                }
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private static void DeleteWithRetry(string path)
        {
            for (int attempt = 1; ; attempt++)
            {
                try { if (File.Exists(path)) File.Delete(path); return; }
                catch (Exception ex) when (Transient(ex) && attempt < 6) { Thread.Sleep(25 * attempt * attempt); }
            }
        }

        private static void TryDeleteDir(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
