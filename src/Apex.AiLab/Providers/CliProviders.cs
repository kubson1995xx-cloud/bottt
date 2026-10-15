using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Apex.AiLab.Lab;

namespace Apex.AiLab.Providers
{
    /// <summary>Stan interaktywnego logowania (np. kod urządzenia) – wyświetlany w panelu.</summary>
    public sealed class LoginSession
    {
        public ProviderId Provider { get; set; }
        public DateTime StartedUtc { get; set; }
        public bool Running { get; set; }
        public string? Url { get; set; }
        public string? Code { get; set; }
        public List<string> Lines { get; } = new();
        public string Result { get; set; } = "";
    }

    /// <summary>
    /// Codex CLI (OpenAI). Logowanie: konto ChatGPT (subskrypcja – bez klucza API) przez "codex login",
    /// kod urządzenia "codex login --device-auth" lub klucz API "codex login --with-api-key".
    /// Praca: "codex exec --json -s workspace-write -C &lt;kopia robocza&gt;" – piaskownica Codex ogranicza zapis do kopii roboczej.
    /// </summary>
    public sealed class CodexCliProvider : IAiProvider
    {
        private readonly Func<ProviderSettings> _settings;
        private LoginSession? _login;

        public CodexCliProvider(Func<ProviderSettings> settings) => _settings = settings;

        public ProviderId Id => ProviderId.Codex;
        public string Name => "Codex (ChatGPT)";
        public bool ExecutesCommands => true;
        public LoginSession? Login => _login;

        private CommandSpec? Exe() => ExecutableLocator.Find("codex", _settings().ExecutablePath);

        public async Task<ProviderStatus> StatusAsync(CancellationToken ct)
        {
            var st = new ProviderStatus { Id = Id, Name = Name, ExecutesCommands = true };
            var exe = Exe();
            if (exe == null)
            {
                st.Detail = "Codex CLI nie jest zainstalowany. Instalacja: npm install -g @openai/codex (wymaga Node.js), potem zaloguj się kontem ChatGPT.";
                return st;
            }
            st.Installed = true;
            st.ExecutablePath = exe.Display;
            var ver = await ProcessRunner.RunAsync(exe, new[] { "--version" }, Environment.CurrentDirectory, timeout: TimeSpan.FromSeconds(20), ct: ct).ConfigureAwait(false);
            st.Version = ver.Output.LastOrDefault(l => l.Trim().Length > 0)?.Trim();
            var login = await ProcessRunner.RunAsync(exe, new[] { "login", "status" }, Environment.CurrentDirectory, timeout: TimeSpan.FromSeconds(20), ct: ct).ConfigureAwait(false);
            string text = string.Join(" ", login.Output.Concat(login.Errors).Where(l => !l.StartsWith("WARNING", StringComparison.Ordinal))).Trim();
            st.LoggedIn = login.ExitCode == 0;
            st.UsesSubscription = text.IndexOf("ChatGPT", StringComparison.OrdinalIgnoreCase) >= 0;
            st.AuthMode = !st.LoggedIn ? "niezalogowany" : st.UsesSubscription ? "Konto ChatGPT (subskrypcja)" : text.IndexOf("API key", StringComparison.OrdinalIgnoreCase) >= 0 ? "Klucz OpenAI API" : "zalogowany";
            st.Detail = text.Length > 0 ? text : (st.LoggedIn ? "Zalogowano." : "Brak logowania.");
            return st;
        }

        /// <summary>Logowanie kontem ChatGPT w przeglądarce (okno konsoli z poleceniem "codex login").</summary>
        public string StartBrowserLogin()
        {
            var exe = Exe();
            if (exe == null) return "Najpierw zainstaluj Codex CLI (npm install -g @openai/codex).";
            return ProcessRunner.StartInteractive(exe, new[] { "login" }, Environment.CurrentDirectory)
                ? "Otwarto okno logowania Codex. Zaloguj się kontem ChatGPT w przeglądarce, potem kliknij „Sprawdź”."
                : "Nie udało się uruchomić okna logowania.";
        }

        /// <summary>Logowanie kodem urządzenia – adres i kod są wyświetlane w panelu.</summary>
        public string StartDeviceLogin()
        {
            var exe = Exe();
            if (exe == null) return "Najpierw zainstaluj Codex CLI (npm install -g @openai/codex).";
            if (_login is { Running: true }) return "Logowanie już trwa.";
            var s = new LoginSession { Provider = Id, StartedUtc = DateTime.UtcNow, Running = true };
            _login = s;
            _ = Task.Run(async () =>
            {
                var r = await ProcessRunner.RunAsync(exe, new[] { "login", "--device-auth" }, Environment.CurrentDirectory,
                    onOut: l => Capture(s, l), onErr: l => Capture(s, l), timeout: TimeSpan.FromMinutes(15)).ConfigureAwait(false);
                s.Running = false;
                s.Result = r.Ok ? "Zalogowano." : r.TimedOut ? "Upłynął czas logowania." : "Logowanie nieudane: " + r.Tail(4);
            });
            return "Rozpoczęto logowanie kodem urządzenia – adres i kod pojawią się w panelu.";
        }

        private static void Capture(LoginSession s, string line)
        {
            string clean = Regex.Replace(line, @"\x1B\[[0-9;]*[A-Za-z]", "").Trim();
            if (clean.Length == 0) return;
            lock (s.Lines) { s.Lines.Add(clean); if (s.Lines.Count > 40) s.Lines.RemoveAt(0); }
            var url = Regex.Match(clean, @"https?://\S+");
            if (url.Success) s.Url ??= url.Value.TrimEnd('.', ')');
            var code = Regex.Match(clean, @"\b([A-Z0-9]{4,5}-[A-Z0-9]{4,5})\b");
            if (code.Success) s.Code = code.Groups[1].Value;
        }

        public async Task<string> LoginWithApiKeyAsync(string key, CancellationToken ct)
        {
            var exe = Exe();
            if (exe == null) return "Najpierw zainstaluj Codex CLI.";
            var r = await ProcessRunner.RunAsync(exe, new[] { "login", "--with-api-key" }, Environment.CurrentDirectory, stdin: key + "\n", timeout: TimeSpan.FromSeconds(30), ct: ct).ConfigureAwait(false);
            return r.Ok ? "Codex: zapisano klucz API (przechowywany przez Codex CLI)." : "Codex: nie udało się zapisać klucza – " + r.Tail(3);
        }

        public async Task<string> LogoutAsync(CancellationToken ct)
        {
            var exe = Exe();
            if (exe == null) return "Codex CLI nie jest zainstalowany.";
            var r = await ProcessRunner.RunAsync(exe, new[] { "logout" }, Environment.CurrentDirectory, timeout: TimeSpan.FromSeconds(20), ct: ct).ConfigureAwait(false);
            return r.Ok ? "Wylogowano z Codex." : "Wylogowanie nieudane: " + r.Tail(3);
        }

        public async Task<AgentResult> RunAsync(AgentRequest req, AgentProgress progress, CancellationToken ct)
        {
            var res = new AgentResult();
            var exe = Exe();
            if (exe == null) { res.Error = "Codex CLI nie jest zainstalowany."; return res; }
            string lastMsg = Path.Combine(req.JobDir, req.ReadOnly ? "codex-przeglad.txt" : "codex-odpowiedz.txt");
            var args = new List<string> { "exec", "--json", "--skip-git-repo-check", "-s", req.ReadOnly ? "read-only" : "workspace-write", "-C", req.WorkDir, "-o", lastMsg };
            if (!string.IsNullOrWhiteSpace(req.Model)) { args.Add("-m"); args.Add(req.Model); }
            args.Add("-"); // polecenie ze standardowego wejścia (bez limitu długości wiersza poleceń)

            string? failure = null;
            string? lastAgentMessage = null;
            var r = await ProcessRunner.RunAsync(exe, args, req.WorkDir, stdin: req.Prompt,
                onOut: line =>
                {
                    var e = CodexEvents.Parse(line);
                    if (e == null) return;
                    if (e.Usage != null) res.Usage.Add(e.Usage);
                    if (e.Error != null) failure = e.Error;
                    if (e.AgentMessage != null) lastAgentMessage = e.AgentMessage;
                    res.ChangedFiles.AddRange(e.Files);
                    progress(e.Kind, e.Text);
                },
                onErr: line => { if (LimitDetector.IsLimit(line)) failure ??= line; },
                timeout: req.Timeout, ct: ct).ConfigureAwait(false);

            res.Usage.DurationSec = r.DurationSec;
            if (File.Exists(lastMsg)) res.Summary = File.ReadAllText(lastMsg).Trim();
            if (res.Summary.Length == 0) res.Summary = lastAgentMessage ?? "";
            if (r.StartFailed) { res.Error = "Nie udało się uruchomić Codex: " + r.StartError; return res; }
            if (r.TimedOut) { res.Error = $"Przekroczono limit czasu agenta ({req.Timeout.TotalMinutes:F0} min)."; return res; }
            if (r.Cancelled) { res.Error = "Przerwano."; return res; }
            if (failure != null && LimitDetector.IsLimit(failure))
            {
                res.Usage.LimitReached = true;
                res.Usage.LimitMessage = failure;
                res.Usage.LimitResetUtc = LimitDetector.ResetTime(failure, DateTime.UtcNow);
                res.Error = "Limit użycia Codex: " + failure;
                return res;
            }
            if (r.ExitCode != 0 || failure != null)
            {
                res.Error = failure ?? ("Codex zakończył pracę z kodem " + r.ExitCode + ": " + r.Tail(6));
                return res;
            }
            res.Ok = true;
            return res;
        }
    }

    /// <summary>
    /// Claude Code CLI (Anthropic). Logowanie: subskrypcja Claude ("claude auth login") lub Anthropic Console (rozliczenie API).
    /// Praca: "claude -p --output-format stream-json" w kopii roboczej z ograniczonym zestawem narzędzi.
    /// </summary>
    public sealed class ClaudeCodeProvider : IAiProvider
    {
        private readonly Func<ProviderSettings> _settings;

        public ClaudeCodeProvider(Func<ProviderSettings> settings) => _settings = settings;

        public ProviderId Id => ProviderId.ClaudeCode;
        public string Name => "Claude Code";
        public bool ExecutesCommands => true;

        private CommandSpec? Exe() => ExecutableLocator.Find("claude", _settings().ExecutablePath);

        /// <summary>Narzędzia dozwolone agentowi: odczyt/edycja plików w kopii roboczej, kompilacja i testy .NET, podgląd git.</summary>
        public static readonly string[] AllowedTools =
        {
            "Read", "Edit", "Write", "Glob", "Grep", "TodoWrite",
            "Bash(dotnet build:*)", "Bash(dotnet test:*)", "Bash(dotnet run --project tools/Apex.Backtester:*)",
            "Bash(git status:*)", "Bash(git diff:*)", "Bash(git log:*)",
        };

        public static readonly string[] ReadOnlyTools = { "Read", "Glob", "Grep", "Bash(git diff:*)", "Bash(git log:*)", "Bash(git status:*)" };

        public static readonly string[] DisallowedTools = { "WebFetch", "WebSearch" };

        public async Task<ProviderStatus> StatusAsync(CancellationToken ct)
        {
            var st = new ProviderStatus { Id = Id, Name = Name, ExecutesCommands = true };
            var exe = Exe();
            if (exe == null)
            {
                st.Detail = "Claude Code nie jest zainstalowany. Instalacja: https://claude.com/claude-code (Windows: polecenie z dokumentacji), potem zaloguj się.";
                return st;
            }
            st.Installed = true;
            st.ExecutablePath = exe.Display;
            var ver = await ProcessRunner.RunAsync(exe, new[] { "--version" }, Environment.CurrentDirectory, timeout: TimeSpan.FromSeconds(20), ct: ct).ConfigureAwait(false);
            st.Version = ver.Output.LastOrDefault(l => l.Trim().Length > 0)?.Trim();
            var auth = await ProcessRunner.RunAsync(exe, new[] { "auth", "status", "--json" }, Environment.CurrentDirectory, timeout: TimeSpan.FromSeconds(20), ct: ct).ConfigureAwait(false);
            string json = string.Join("\n", auth.Output);
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                st.LoggedIn = root.TryGetProperty("loggedIn", out var li) && li.ValueKind == JsonValueKind.True;
                string method = CodexEvents.Str(root, "authMethod");
                string sub = CodexEvents.Str(root, "subscriptionType");
                st.UsesSubscription = method.Contains("oauth", StringComparison.OrdinalIgnoreCase) || method.Contains("claude", StringComparison.OrdinalIgnoreCase);
                st.AuthMode = !st.LoggedIn ? "niezalogowany" : st.UsesSubscription ? "Subskrypcja Claude" + (sub.Length > 0 ? " (" + sub + ")" : "") : "Anthropic API / Console";
                st.Detail = st.LoggedIn ? "Zalogowano (" + method + ")." : "Brak logowania – użyj „Zaloguj subskrypcją Claude”.";
            }
            catch (JsonException)
            {
                st.Detail = "Nie można odczytać stanu logowania: " + auth.Tail(3);
            }
            return st;
        }

        public string StartLogin(bool console)
        {
            var exe = Exe();
            if (exe == null) return "Najpierw zainstaluj Claude Code.";
            return ProcessRunner.StartInteractive(exe, new[] { "auth", "login", console ? "--console" : "--claudeai" }, Environment.CurrentDirectory)
                ? "Otwarto okno logowania Claude. Dokończ logowanie w przeglądarce, potem kliknij „Sprawdź”."
                : "Nie udało się uruchomić okna logowania.";
        }

        public async Task<string> LogoutAsync(CancellationToken ct)
        {
            var exe = Exe();
            if (exe == null) return "Claude Code nie jest zainstalowany.";
            var r = await ProcessRunner.RunAsync(exe, new[] { "auth", "logout" }, Environment.CurrentDirectory, timeout: TimeSpan.FromSeconds(20), ct: ct).ConfigureAwait(false);
            return r.Ok ? "Wylogowano z Claude Code." : "Wylogowanie nieudane: " + r.Tail(3);
        }

        public async Task<AgentResult> RunAsync(AgentRequest req, AgentProgress progress, CancellationToken ct)
        {
            var res = new AgentResult();
            var exe = Exe();
            if (exe == null) { res.Error = "Claude Code nie jest zainstalowany."; return res; }
            var args = new List<string> { "-p", "--output-format", "stream-json", "--verbose", "--no-session-persistence" };
            args.Add("--permission-mode");
            args.Add(req.ReadOnly ? "default" : "acceptEdits");
            args.Add("--allowedTools");
            args.AddRange(req.ReadOnly ? ReadOnlyTools : AllowedTools);
            args.Add("--disallowedTools");
            args.AddRange(DisallowedTools);
            if (!string.IsNullOrWhiteSpace(req.Model)) { args.Add("--model"); args.Add(req.Model); }
            if (req.Settings.MaxBudgetUsdPerRun > 0) { args.Add("--max-budget-usd"); args.Add(req.Settings.MaxBudgetUsdPerRun.ToString(System.Globalization.CultureInfo.InvariantCulture)); }

            string? resultText = null;
            bool isError = false;
            var r = await ProcessRunner.RunAsync(exe, args, req.WorkDir, stdin: req.Prompt,
                onOut: line =>
                {
                    foreach (var e in ClaudeEvents.Parse(line))
                    {
                        if (e.Usage != null) res.Usage.Add(e.Usage);
                        if (e.IsResult) { resultText = e.ResultText; isError = e.IsError; }
                        res.ChangedFiles.AddRange(e.Files);
                        progress(e.Kind, e.Text);
                    }
                },
                onErr: line => { if (LimitDetector.IsLimit(line)) { resultText ??= line; isError = true; } },
                timeout: req.Timeout, ct: ct).ConfigureAwait(false);

            res.Usage.DurationSec = r.DurationSec;
            res.Summary = (resultText ?? "").Trim();
            if (r.StartFailed) { res.Error = "Nie udało się uruchomić Claude Code: " + r.StartError; return res; }
            if (r.TimedOut) { res.Error = $"Przekroczono limit czasu agenta ({req.Timeout.TotalMinutes:F0} min)."; return res; }
            if (r.Cancelled) { res.Error = "Przerwano."; return res; }
            if (isError && LimitDetector.IsLimit(resultText))
            {
                res.Usage.LimitReached = true;
                res.Usage.LimitMessage = resultText;
                res.Usage.LimitResetUtc = LimitDetector.ResetTime(resultText, DateTime.UtcNow);
                res.Error = "Limit użycia Claude: " + resultText;
                return res;
            }
            if (r.ExitCode != 0 || isError)
            {
                res.Error = (resultText != null && resultText.Length > 0 ? resultText : "Claude Code zakończył pracę z kodem " + r.ExitCode) + (r.Errors.Count > 0 ? " | " + r.Tail(4) : "");
                return res;
            }
            res.Ok = true;
            return res;
        }
    }
}
