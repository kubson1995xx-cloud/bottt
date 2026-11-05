using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Apex.AiLab.Lab;

namespace Apex.AiLab.Providers
{
    public sealed class ProviderStatus
    {
        public ProviderId Id { get; set; }
        public string Name { get; set; } = "";
        public bool Installed { get; set; }
        public string? Version { get; set; }
        public string? ExecutablePath { get; set; }
        public bool LoggedIn { get; set; }
        /// <summary>np. "Konto ChatGPT (subskrypcja)", "Klucz API", "Subskrypcja Claude".</summary>
        public string AuthMode { get; set; } = "";
        public string Detail { get; set; } = "";
        public bool UsesSubscription { get; set; }
        public bool ExecutesCommands { get; set; }
        public string? KeyMasked { get; set; }
    }

    public sealed class AgentRequest
    {
        public string JobId { get; set; } = "";
        public string WorkDir { get; set; } = "";
        public string JobDir { get; set; } = "";
        public string Prompt { get; set; } = "";
        public string Model { get; set; } = "";
        public ProviderSettings Settings { get; set; } = new();
        /// <summary>Tryb tylko do odczytu (przegląd zmian) – agent nie może modyfikować plików.</summary>
        public bool ReadOnly { get; set; }
        public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(45);
    }

    public sealed class AgentResult
    {
        public bool Ok { get; set; }
        public string Summary { get; set; } = "";
        public string Error { get; set; } = "";
        public UsageInfo Usage { get; set; } = new();
        public List<string> ChangedFiles { get; set; } = new();
    }

    /// <summary>Zdarzenie postępu agenta dla panelu: (rodzaj, tekst).</summary>
    public delegate void AgentProgress(string kind, string text);

    public interface IAiProvider
    {
        ProviderId Id { get; }
        string Name { get; }
        /// <summary>Agent CLI sam czyta pliki i uruchamia polecenia; dostawcy API zwracają pliki do zastosowania przez AI Lab.</summary>
        bool ExecutesCommands { get; }
        Task<ProviderStatus> StatusAsync(CancellationToken ct);
        Task<AgentResult> RunAsync(AgentRequest req, AgentProgress progress, CancellationToken ct);
    }

    /// <summary>Rozpoznawanie komunikatów o wyczerpaniu limitów (subskrypcja / API) i czasu ich odnowienia.</summary>
    public static class LimitDetector
    {
        private static readonly string[] Markers =
        {
            "usage limit", "hit your limit", "limit reached", "rate limit", "rate_limit", "insufficient_quota", "quota exceeded",
            "too many requests", "credits depleted", "usagelimitexceeded", "ratelimitexceeded",
        };

        public static bool IsLimit(string? message)
        {
            if (string.IsNullOrEmpty(message)) return false;
            string m = message.ToLowerInvariant();
            return Markers.Any(m.Contains) || Regex.IsMatch(m, @"\b429\b");
        }

        /// <summary>Próbuje odczytać czas odnowienia limitu. Zwraca null, gdy formatu nie da się rozpoznać.</summary>
        public static DateTime? ResetTime(string? message, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(message)) return null;
            // Claude: "Claude AI usage limit reached|1760000000"
            var epoch = Regex.Match(message, @"\|(\d{10})\b");
            if (epoch.Success && long.TryParse(epoch.Groups[1].Value, out long sec))
                return DateTimeOffset.FromUnixTimeSeconds(sec).UtcDateTime;
            // "try again in 2 hours 15 minutes" / "resets in 45 min"
            var rel = Regex.Match(message, @"(?:again|resets?)\s+in\s+(?:(\d+)\s*(?:hours?|hrs?|godz\w*|h)\b)?\s*(?:and\s+)?(?:(\d+)\s*(?:minutes?|mins?|minut\w*|m)\b)?", RegexOptions.IgnoreCase);
            if (rel.Success && (rel.Groups[1].Success || rel.Groups[2].Success))
            {
                int h = rel.Groups[1].Success ? int.Parse(rel.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                int m = rel.Groups[2].Success ? int.Parse(rel.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
                if (h + m > 0) return nowUtc.AddHours(h).AddMinutes(m);
            }
            // Codex: "... Try again at Oct 16th, 2026 3:45 PM." / "try again at 3:45 PM"
            var at = Regex.Match(message, @"(?:try again at|resets? at|resets)\s+([^.\n]+)", RegexOptions.IgnoreCase);
            if (at.Success)
            {
                string s = Regex.Replace(at.Groups[1].Value.Trim(), @"(\d+)(st|nd|rd|th)\b", "$1");
                if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces, out var local))
                {
                    var utc = local.ToUniversalTime();
                    if (utc < nowUtc.AddMinutes(-5)) utc = utc.AddDays(1); // sama godzina – następne wystąpienie
                    return utc;
                }
                var hourOnly = Regex.Match(s, @"^(\d{1,2})\s*(am|pm)", RegexOptions.IgnoreCase);
                if (hourOnly.Success)
                {
                    int hh = int.Parse(hourOnly.Groups[1].Value, CultureInfo.InvariantCulture) % 12 + (hourOnly.Groups[2].Value.ToLowerInvariant() == "pm" ? 12 : 0);
                    var localNow = nowUtc.ToLocalTime();
                    var cand = new DateTime(localNow.Year, localNow.Month, localNow.Day, hh, 0, 0, DateTimeKind.Local);
                    if (cand < localNow) cand = cand.AddDays(1);
                    return cand.ToUniversalTime();
                }
            }
            return null;
        }
    }

    /// <summary>Parser zdarzeń JSONL z "codex exec --json".</summary>
    public static class CodexEvents
    {
        public sealed class Parsed
        {
            public string Kind { get; set; } = "";
            public string Text { get; set; } = "";
            public UsageInfo? Usage { get; set; }
            public string? Error { get; set; }
            public bool TurnFailed { get; set; }
            public bool TurnCompleted { get; set; }
            public string? AgentMessage { get; set; }
            public List<string> Files { get; set; } = new();
        }

        public static Parsed? Parse(string line)
        {
            if (string.IsNullOrWhiteSpace(line) || line[0] != '{') return null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                string type = Str(root, "type");
                switch (type)
                {
                    case "thread.started":
                        return new Parsed { Kind = "stage", Text = "Agent Codex rozpoczął pracę (wątek " + Str(root, "thread_id") + ")." };
                    case "turn.started":
                        return null;
                    case "turn.completed":
                        {
                            var u = new UsageInfo();
                            if (root.TryGetProperty("usage", out var us))
                            {
                                u.InputTokens = Long(us, "input_tokens");
                                u.CachedInputTokens = Long(us, "cached_input_tokens");
                                u.OutputTokens = Long(us, "output_tokens");
                            }
                            return new Parsed { Kind = "usage", TurnCompleted = true, Usage = u, Text = $"Tura zakończona: {u.InputTokens:N0} tokenów wejścia ({u.CachedInputTokens:N0} z pamięci podręcznej), {u.OutputTokens:N0} wyjścia." };
                        }
                    case "turn.failed":
                        {
                            string msg = root.TryGetProperty("error", out var er) ? (er.ValueKind == JsonValueKind.Object ? Str(er, "message") : er.ToString()) : "nieznany błąd";
                            return new Parsed { Kind = "error", TurnFailed = true, Error = msg, Text = "Tura agenta nieudana: " + msg };
                        }
                    case "error":
                        {
                            string msg = Str(root, "message");
                            bool transient = msg.StartsWith("Reconnecting", StringComparison.OrdinalIgnoreCase);
                            return new Parsed { Kind = transient ? "warning" : "error", Error = transient ? null : msg, Text = msg };
                        }
                    case "item.started":
                    case "item.updated":
                    case "item.completed":
                        if (!root.TryGetProperty("item", out var item)) return null;
                        return ParseItem(item, type == "item.completed", type == "item.started");
                    default:
                        return null;
                }
            }
            catch (JsonException) { return null; }
        }

        private static Parsed? ParseItem(JsonElement item, bool completed, bool started)
        {
            string it = Str(item, "type");
            switch (it)
            {
                case "agent_message":
                    if (!completed) return null;
                    {
                        string t = Str(item, "text");
                        return new Parsed { Kind = "message", Text = t, AgentMessage = t };
                    }
                case "reasoning":
                    if (!completed) return null;
                    return new Parsed { Kind = "thinking", Text = Trim(Str(item, "text"), 600) };
                case "command_execution":
                    {
                        string cmd = Str(item, "command");
                        if (started) return new Parsed { Kind = "command", Text = "▶ " + cmd };
                        if (!completed) return null;
                        string code = item.TryGetProperty("exit_code", out var ec) && ec.ValueKind == JsonValueKind.Number ? ec.GetInt32().ToString(CultureInfo.InvariantCulture) : "?";
                        string output = Trim(Str(item, "aggregated_output"), 800);
                        return new Parsed { Kind = "command", Text = $"✓ {cmd} (kod {code})" + (output.Length > 0 ? "\n" + output : "") };
                    }
                case "file_change":
                    {
                        if (!completed) return null;
                        var files = new List<string>();
                        if (item.TryGetProperty("changes", out var ch) && ch.ValueKind == JsonValueKind.Array)
                            foreach (var c in ch.EnumerateArray()) files.Add($"{Str(c, "kind")} {Str(c, "path")}".Trim());
                        return new Parsed { Kind = "file", Files = files, Text = "Zmiana plików: " + string.Join(", ", files) };
                    }
                case "todo_list":
                    {
                        if (!item.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return null;
                        var lines = items.EnumerateArray().Select(x => (x.TryGetProperty("completed", out var c) && c.ValueKind == JsonValueKind.True ? "☑ " : "☐ ") + Str(x, "text"));
                        return new Parsed { Kind = "plan", Text = string.Join("\n", lines) };
                    }
                case "mcp_tool_call":
                case "web_search":
                    if (!completed) return null;
                    return new Parsed { Kind = "command", Text = it + ": " + Trim(item.ToString(), 300) };
                case "error":
                    return new Parsed { Kind = "warning", Text = Str(item, "message") };
                default:
                    return null;
            }
        }

        internal static string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ValueKind == JsonValueKind.Null ? "" : v.ToString()) : "";

        internal static long Long(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l) ? l : 0;

        internal static string Trim(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + " …";
    }

    /// <summary>Parser strumienia "claude -p --output-format stream-json".</summary>
    public static class ClaudeEvents
    {
        public sealed class Parsed
        {
            public string Kind { get; set; } = "";
            public string Text { get; set; } = "";
            public bool IsResult { get; set; }
            public bool IsError { get; set; }
            public string? ResultText { get; set; }
            public UsageInfo? Usage { get; set; }
            public List<string> Files { get; set; } = new();
        }

        public static List<Parsed> Parse(string line)
        {
            var list = new List<Parsed>();
            if (string.IsNullOrWhiteSpace(line) || line[0] != '{') return list;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                string type = CodexEvents.Str(root, "type");
                if (type == "system" && CodexEvents.Str(root, "subtype") == "init")
                {
                    list.Add(new Parsed { Kind = "stage", Text = "Agent Claude Code rozpoczął pracę (model " + CodexEvents.Str(root, "model") + ")." });
                }
                else if (type == "assistant" && root.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in content.EnumerateArray())
                    {
                        string ct = CodexEvents.Str(c, "type");
                        if (ct == "text")
                        {
                            string t = CodexEvents.Str(c, "text");
                            if (t.Trim().Length > 0) list.Add(new Parsed { Kind = "message", Text = t });
                        }
                        else if (ct == "thinking")
                        {
                            list.Add(new Parsed { Kind = "thinking", Text = CodexEvents.Trim(CodexEvents.Str(c, "thinking"), 600) });
                        }
                        else if (ct == "tool_use")
                        {
                            string name = CodexEvents.Str(c, "name");
                            var input = c.TryGetProperty("input", out var inp) ? inp : default;
                            if (name is "Edit" or "Write" or "MultiEdit" or "NotebookEdit")
                            {
                                string path = CodexEvents.Str(input, "file_path");
                                list.Add(new Parsed { Kind = "file", Files = { path }, Text = $"{name}: {path}" });
                            }
                            else if (name == "Bash")
                                list.Add(new Parsed { Kind = "command", Text = "▶ " + CodexEvents.Str(input, "command") });
                            else if (name == "TodoWrite" && input.ValueKind == JsonValueKind.Object && input.TryGetProperty("todos", out var todos) && todos.ValueKind == JsonValueKind.Array)
                                list.Add(new Parsed
                                {
                                    Kind = "plan",
                                    Text = string.Join("\n", todos.EnumerateArray().Select(td => (CodexEvents.Str(td, "status") == "completed" ? "☑ " : "☐ ") + CodexEvents.Str(td, "content"))),
                                });
                            else
                                list.Add(new Parsed { Kind = "command", Text = name + " " + CodexEvents.Trim(input.ValueKind == JsonValueKind.Undefined ? "" : input.ToString(), 200) });
                        }
                    }
                }
                else if (type == "result")
                {
                    var u = new UsageInfo();
                    if (root.TryGetProperty("usage", out var us))
                    {
                        u.InputTokens = CodexEvents.Long(us, "input_tokens") + CodexEvents.Long(us, "cache_creation_input_tokens") + CodexEvents.Long(us, "cache_read_input_tokens");
                        u.CachedInputTokens = CodexEvents.Long(us, "cache_read_input_tokens");
                        u.OutputTokens = CodexEvents.Long(us, "output_tokens");
                    }
                    if (root.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number) u.CostUsd = cost.GetDouble();
                    bool isError = root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
                    string subtype = CodexEvents.Str(root, "subtype");
                    string result = CodexEvents.Str(root, "result");
                    if (subtype.StartsWith("error", StringComparison.Ordinal)) isError = true;
                    list.Add(new Parsed
                    {
                        Kind = isError ? "error" : "usage",
                        IsResult = true,
                        IsError = isError,
                        ResultText = result,
                        Usage = u,
                        Text = isError ? "Agent zakończył się błędem: " + (result.Length > 0 ? result : subtype)
                                       : $"Zakończono: {u.InputTokens:N0} tokenów wejścia, {u.OutputTokens:N0} wyjścia" + (u.CostUsd.HasValue ? $", koszt wg narzędzia {u.CostUsd.Value:F2} USD" : "") + ".",
                    });
                }
            }
            catch (JsonException) { }
            return list;
        }
    }
}
