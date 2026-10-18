using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Apex.AiLab.Lab
{
    public sealed class CommandSpec
    {
        public string FileName { get; init; } = "";
        public List<string> PrefixArgs { get; init; } = new();
        public string Display => string.Join(" ", new[] { FileName }.Concat(PrefixArgs));
    }

    public sealed class ProcessResult
    {
        public int ExitCode { get; set; } = -1;
        public bool TimedOut { get; set; }
        public bool Cancelled { get; set; }
        public bool StartFailed { get; set; }
        public string StartError { get; set; } = "";
        public List<string> Output { get; } = new();
        public List<string> Errors { get; } = new();
        public double DurationSec { get; set; }
        public bool Ok => ExitCode == 0 && !TimedOut && !Cancelled && !StartFailed;
        /// <summary>Ostatnie linie diagnostyczne – najpierw strumień błędów (tam jest przyczyna), potem wyjście.</summary>
        public string Tail(int lines = 30)
        {
            var err = Errors.Where(l => l.Trim().Length > 0).ToList();
            var src = err.Count > 0 ? err : Output.Where(l => l.Trim().Length > 0).ToList();
            return string.Join("\n", src.TakeLast(lines));
        }
    }

    /// <summary>Uruchamianie procesów (git, dotnet, codex, claude) z limitem czasu, przerwaniem i zabiciem całego drzewa procesów.</summary>
    public static class ProcessRunner
    {
        public static async Task<ProcessResult> RunAsync(CommandSpec cmd, IEnumerable<string> args, string workDir, string? stdin = null,
            IDictionary<string, string?>? env = null, Action<string>? onOut = null, Action<string>? onErr = null,
            TimeSpan? timeout = null, CancellationToken ct = default, int keepLines = 400)
        {
            var res = new ProcessResult();
            var sw = Stopwatch.StartNew();
            var psi = new ProcessStartInfo(cmd.FileName)
            {
                WorkingDirectory = workDir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                // Bez tego Windows koduje wejście stroną kodową konsoli (np. 852) – codex odrzuca polecenie jako nieprawidłowy UTF-8,
                // a Claude dostaje zniekształcone polskie znaki. UTF8Encoding(false): bez BOM na początku strumienia.
                StandardInputEncoding = Utf8NoBom,
            };
            foreach (var a in cmd.PrefixArgs) psi.ArgumentList.Add(a);
            foreach (var a in args) psi.ArgumentList.Add(a);
            ApplyEnvironment(psi, env);

            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var outDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var errDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) { outDone.TrySetResult(true); return; }
                lock (res.Output) { res.Output.Add(e.Data); if (res.Output.Count > keepLines) res.Output.RemoveAt(0); }
                try { onOut?.Invoke(e.Data); } catch (Exception) { /* obserwator nie może przerwać procesu */ }
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) { errDone.TrySetResult(true); return; }
                lock (res.Errors) { res.Errors.Add(e.Data); if (res.Errors.Count > keepLines) res.Errors.RemoveAt(0); }
                try { onErr?.Invoke(e.Data); } catch (Exception) { }
            };
            try
            {
                if (!p.Start()) { res.StartFailed = true; res.StartError = "nie udało się uruchomić procesu"; return res; }
            }
            catch (Win32Exception ex)
            {
                res.StartFailed = true;
                res.StartError = ex.Message;
                return res;
            }
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            try
            {
                if (stdin != null) await p.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
                p.StandardInput.Close();
            }
            catch (IOException) { }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeout.HasValue) cts.CancelAfter(timeout.Value);
            try
            {
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                await Task.WhenAny(Task.WhenAll(outDone.Task, errDone.Task), Task.Delay(5000)).ConfigureAwait(false);
                res.ExitCode = p.ExitCode;
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested) res.Cancelled = true; else res.TimedOut = true;
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (Win32Exception) { }
                try { p.WaitForExit(5000); } catch (InvalidOperationException) { }
            }
            res.DurationSec = sw.Elapsed.TotalSeconds;
            return res;
        }

        /// <summary>UTF-8 bez znacznika BOM (wejście procesów potomnych, dokładny odczyt wyjścia).</summary>
        public static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

        private static void ApplyEnvironment(ProcessStartInfo psi, IDictionary<string, string?>? env)
        {
            psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            psi.Environment["DOTNET_NOLOGO"] = "1";
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            if (env != null)
                foreach (var kv in env)
                {
                    if (kv.Value == null) psi.Environment.Remove(kv.Key);
                    else psi.Environment[kv.Key] = kv.Value;
                }
        }

        /// <summary>
        /// Uruchamia proces i zwraca PEŁNE wyjście standardowe bajt w bajt (UTF-8), bez dzielenia na linie i bez limitu liczby linii.
        /// Do danych, które muszą być kompletne i dokładne (diff audytu, listy plików git -z): <see cref="RunAsync"/> zachowuje tylko
        /// ostatnie linie i dzieli tekst także na samotnym CR – część zmiany mogłaby ominąć audyt.
        /// </summary>
        public static async Task<CaptureResult> CaptureAsync(CommandSpec cmd, IEnumerable<string> args, string workDir,
            IDictionary<string, string?>? env = null, TimeSpan? timeout = null, CancellationToken ct = default, long maxBytes = 512L * 1024 * 1024)
        {
            var res = new CaptureResult();
            var sw = Stopwatch.StartNew();
            var psi = new ProcessStartInfo(cmd.FileName)
            {
                WorkingDirectory = workDir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardErrorEncoding = Encoding.UTF8,
                StandardInputEncoding = Utf8NoBom,
            };
            foreach (var a in cmd.PrefixArgs) psi.ArgumentList.Add(a);
            foreach (var a in args) psi.ArgumentList.Add(a);
            ApplyEnvironment(psi, env);

            using var p = new Process { StartInfo = psi };
            try
            {
                if (!p.Start()) { res.StartFailed = true; res.StartError = "nie udało się uruchomić procesu"; return res; }
            }
            catch (Win32Exception ex)
            {
                res.StartFailed = true;
                res.StartError = ex.Message;
                return res;
            }
            try { p.StandardInput.Close(); } catch (IOException) { }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeout.HasValue) cts.CancelAfter(timeout.Value);
            var output = new MemoryStream();
            var readOut = Task.Run(async () =>
            {
                var buf = new byte[81920];
                var stream = p.StandardOutput.BaseStream;
                int n;
                while ((n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), cts.Token).ConfigureAwait(false)) > 0)
                {
                    if (output.Length + n > maxBytes) { res.TooLarge = true; cts.Cancel(); break; }
                    output.Write(buf, 0, n);
                }
            });
            var readErr = p.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                // Po zakończeniu procesu czekamy na koniec strumieni (ograniczenie czasu: potomek mógłby trzymać potok otwarty).
                var both = Task.WhenAll(readOut, readErr);
                if (await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None)).ConfigureAwait(false) != both)
                    res.TimedOut = true; // wyjście niekompletne – wynik nie może być użyty
                else
                    await both.ConfigureAwait(false);
                res.ExitCode = p.ExitCode;
            }
            catch (OperationCanceledException)
            {
                if (!res.TooLarge) { if (ct.IsCancellationRequested) res.Cancelled = true; else res.TimedOut = true; }
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (Win32Exception) { }
                try { p.WaitForExit(5000); } catch (InvalidOperationException) { }
            }
            // Bufor czytamy tylko po zakończeniu odczytu (przy przerwaniu wynik i tak jest nieważny – Ok = false).
            if (readOut.IsCompleted) res.Stdout = Utf8NoBom.GetString(output.GetBuffer(), 0, (int)output.Length);
            if (readErr.IsCompletedSuccessfully) res.Stderr = readErr.Result;
            res.DurationSec = sw.Elapsed.TotalSeconds;
            return res;
        }

        /// <summary>Uruchomienie w nowym oknie konsoli (Windows) – dla logowania wymagającego interakcji użytkownika.</summary>
        public static bool StartInteractive(CommandSpec cmd, IEnumerable<string> args, string workDir)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // ShellExecute cmd.exe otwiera własne okno konsoli (bez pośrednictwa "start"). Wiersz poleceń budujemy sami:
                    // ArgumentList cytuje po zasadach CRT (\"), których cmd nie rozumie – ścieżka ze spacją psuła logowanie.
                    var psi = new ProcessStartInfo("cmd.exe")
                    {
                        UseShellExecute = true,
                        WorkingDirectory = workDir,
                        Arguments = BuildInteractiveArguments(cmd, args),
                    };
                    Process.Start(psi);
                    return true;
                }
                var p = new ProcessStartInfo(cmd.FileName) { UseShellExecute = false, WorkingDirectory = workDir };
                foreach (var a in cmd.PrefixArgs.Concat(args)) p.ArgumentList.Add(a);
                Process.Start(p);
                return true;
            }
            catch (Win32Exception) { return false; }
            catch (ArgumentException) { return false; }
        }

        /// <summary>
        /// Argumenty dla cmd.exe: <c>/k "&lt;program&gt; &lt;argumenty&gt;"</c>. Każdy element jest w cudzysłowie (bez ucieczek CRT),
        /// a całość w dodatkowej parze cudzysłowów, którą cmd /k usuwa (zachowanie dla więcej niż dwóch cudzysłowów).
        /// Znaki specjalne cmd (&amp; | &lt; &gt; ^ % ") i znaki sterujące są odrzucane – nie da się ich bezpiecznie przekazać.
        /// </summary>
        public static string BuildInteractiveArguments(CommandSpec cmd, IEnumerable<string> args)
        {
            var parts = new List<string>();
            foreach (var a in new[] { cmd.FileName }.Concat(cmd.PrefixArgs).Concat(args))
            {
                if (a.Length == 0 || a.Any(ch => ch is '&' or '|' or '<' or '>' or '^' or '%' or '"' || char.IsControl(ch)))
                    throw new ArgumentException("Niedozwolony znak w poleceniu logowania: " + a);
                // Końcowe ukośniki przed zamykającym cudzysłowem podwajamy (inaczej program odczyta \" jako znak cudzysłowu).
                int trailing = a.Length - a.TrimEnd('\\').Length;
                parts.Add("\"" + a + new string('\\', trailing) + "\"");
            }
            return "/k \"" + string.Join(" ", parts) + "\"";
        }
    }

    /// <summary>Wynik <see cref="ProcessRunner.CaptureAsync"/>: pełne wyjście standardowe procesu.</summary>
    public sealed class CaptureResult
    {
        public int ExitCode { get; set; } = -1;
        public bool TimedOut { get; set; }
        public bool Cancelled { get; set; }
        public bool StartFailed { get; set; }
        /// <summary>Wyjście przekroczyło limit rozmiaru – proces przerwano, wynik niekompletny.</summary>
        public bool TooLarge { get; set; }
        public string StartError { get; set; } = "";
        public string Stdout { get; set; } = "";
        public string Stderr { get; set; } = "";
        public double DurationSec { get; set; }
        public bool Ok => ExitCode == 0 && !TimedOut && !Cancelled && !StartFailed && !TooLarge;

        public string Problem(int maxLines = 6) =>
            StartFailed ? StartError
            : TooLarge ? "wynik przekracza dopuszczalny rozmiar"
            : TimedOut ? "przekroczony limit czasu"
            : Cancelled ? "przerwano"
            : string.Join("\n", Stderr.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).TakeLast(maxLines));
    }

    /// <summary>Wyszukiwanie programów (git, dotnet, codex, claude) w PATH i typowych lokalizacjach instalacji.</summary>
    public static class ExecutableLocator
    {
        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        public static CommandSpec? Find(string name, string? overridePath = null)
        {
            if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)) return Resolve(overridePath!, name);
            foreach (var dir in SearchDirs())
            {
                foreach (var ext in Extensions())
                {
                    string candidate = Path.Combine(dir, name + ext);
                    if (File.Exists(candidate)) return Resolve(candidate, name);
                }
            }
            return null;
        }

        private static IEnumerable<string> Extensions() => IsWindows ? new[] { ".exe", ".cmd", ".bat" } : new[] { "" };

        private static IEnumerable<string> SearchDirs()
        {
            var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (IsWindows)
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                dirs.Add(Path.Combine(appData, "npm"));
                dirs.Add(Path.Combine(home, ".local", "bin"));
                dirs.Add(Path.Combine(local, "Programs", "Git", "cmd"));
                dirs.Add(@"C:\Program Files\Git\cmd");
                dirs.Add(@"C:\Program Files\dotnet");
                dirs.Add(@"C:\Program Files\nodejs");
            }
            else
            {
                dirs.Add(Path.Combine(home, ".local", "bin"));
                dirs.Add(Path.Combine(home, ".dotnet"));
                dirs.Add("/usr/local/bin");
                dirs.Add("/usr/bin");
            }
            return dirs.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Skrypty npm na Windows (codex.cmd) uruchamiamy bezpośrednio przez node + plik JS pakietu – unikamy pośrednictwa cmd.exe
        /// i problemów z cytowaniem argumentów.
        /// </summary>
        private static CommandSpec Resolve(string path, string name)
        {
            if (IsWindows && (path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
            {
                string dir = Path.GetDirectoryName(path)!;
                // Claude Code z npm zawiera natywny plik exe – uruchamiamy go bezpośrednio.
                string nativeClaude = Path.Combine(dir, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
                if (name == "claude" && File.Exists(nativeClaude)) return new CommandSpec { FileName = nativeClaude };
                string? js = name switch
                {
                    "codex" => Path.Combine(dir, "node_modules", "@openai", "codex", "bin", "codex.js"),
                    "claude" => Path.Combine(dir, "node_modules", "@anthropic-ai", "claude-code", "cli.js"),
                    _ => null,
                };
                var node = Find("node");
                if (js != null && File.Exists(js) && node != null)
                    return new CommandSpec { FileName = node.FileName, PrefixArgs = node.PrefixArgs.Concat(new[] { js }).ToList() };
            }
            return new CommandSpec { FileName = path };
        }
    }
}
