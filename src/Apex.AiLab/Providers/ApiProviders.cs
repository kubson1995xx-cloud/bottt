using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Apex.AiLab.Lab;

namespace Apex.AiLab.Providers
{
    /// <summary>
    /// Format odpowiedzi dostawców API: pełna treść każdego zmienianego pliku w blokach.
    /// AI Lab sam zapisuje pliki – po walidacji ścieżek (tylko wewnątrz repozytorium, bez ścieżek zabronionych).
    /// Model nie wykonuje żadnych poleceń.
    /// </summary>
    public static class FileBlocks
    {
        public const string Instructions =
@"FORMAT ODPOWIEDZI (obowiązkowy):
Dla każdego zmienianego lub nowego pliku podaj jego PEŁNĄ nową treść:
=== PLIK: ścieżka/względem/repozytorium.cs ===
<cała treść pliku>
=== KONIEC PLIKU ===
Na końcu:
=== PODSUMOWANIE ===
<tytuł zmiany w pierwszej linii, potem: co zmieniono, dlaczego, oczekiwany efekt, ryzyka, jakie testy dodano>
=== KONIEC ===
Nie usuwaj plików. Nie używaj innych formatów (diff, łatki). Jeśli uznasz, że zmiana nie jest uzasadniona danymi – nie zmieniaj plików i wyjaśnij to w podsumowaniu.";

        public sealed class ParsedResponse
        {
            public List<(string Path, string Content)> Files { get; } = new();
            public string Summary { get; set; } = "";
            public List<string> Problems { get; } = new();
        }

        private static readonly Regex Header = new(@"^===\s*PLIK:\s*(.+?)\s*===\s*$", RegexOptions.Compiled);

        public static ParsedResponse Parse(string text)
        {
            var res = new ParsedResponse();
            var lines = text.Replace("\r\n", "\n").Split('\n');
            int i = 0;
            while (i < lines.Length)
            {
                var m = Header.Match(lines[i].Trim());
                if (m.Success)
                {
                    string path = m.Groups[1].Value.Trim().Trim('`', '"', '\'');
                    var body = new List<string>();
                    i++;
                    bool closed = false;
                    while (i < lines.Length)
                    {
                        if (Regex.IsMatch(lines[i].Trim(), @"^===\s*KONIEC PLIKU\s*===$")) { closed = true; i++; break; }
                        body.Add(lines[i]);
                        i++;
                    }
                    if (!closed) { res.Problems.Add($"Niezamknięty blok pliku {path} – pominięto."); continue; }
                    // Model czasem otacza treść blokiem ``` – usuwamy wyłącznie zewnętrzne ogrodzenie.
                    if (body.Count >= 2 && body[0].TrimStart().StartsWith("```", StringComparison.Ordinal) && body[^1].Trim() == "```")
                    {
                        body.RemoveAt(body.Count - 1);
                        body.RemoveAt(0);
                    }
                    res.Files.Add((path, string.Join("\n", body) + "\n"));
                    continue;
                }
                if (Regex.IsMatch(lines[i].Trim(), @"^===\s*PODSUMOWANIE\s*===$"))
                {
                    var sb = new StringBuilder();
                    i++;
                    while (i < lines.Length && !Regex.IsMatch(lines[i].Trim(), @"^===\s*KONIEC\s*===$")) { sb.AppendLine(lines[i]); i++; }
                    res.Summary = sb.ToString().Trim();
                }
                i++;
            }
            if (res.Summary.Length == 0 && res.Files.Count == 0) res.Summary = text.Trim();
            return res;
        }

        /// <summary>Walidacja ścieżki: względna, w repozytorium, bez "..", bez ścieżek zabronionych. Zwraca znormalizowaną ścieżkę lub null.</summary>
        public static string? SafePath(string repoRoot, string relPath, IEnumerable<string> forbidden, out string? problem)
        {
            problem = null;
            string p = relPath.Replace('\\', '/').Trim();
            if (p.Length == 0 || p.StartsWith("/", StringComparison.Ordinal) || Regex.IsMatch(p, @"^[A-Za-z]:") || p.Split('/').Any(seg => seg == ".." || seg == "."))
            {
                problem = $"Odrzucono ścieżkę spoza repozytorium: {relPath}";
                return null;
            }
            foreach (var f in forbidden)
                if (p.StartsWith(f, StringComparison.OrdinalIgnoreCase))
                {
                    problem = $"Odrzucono zmianę w ścieżce zabronionej: {p}";
                    return null;
                }
            string full = Path.GetFullPath(Path.Combine(repoRoot, p));
            string root = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.Ordinal))
            {
                problem = $"Odrzucono ścieżkę spoza repozytorium: {relPath}";
                return null;
            }
            return p;
        }

        public static List<string> Apply(string repoRoot, ParsedResponse r, IEnumerable<string> forbidden, List<string> problems)
        {
            var applied = new List<string>();
            var forb = forbidden.ToList();
            foreach (var (path, content) in r.Files)
            {
                string? safe = SafePath(repoRoot, path, forb, out var problem);
                if (safe == null) { problems.Add(problem!); continue; }
                string full = Path.Combine(repoRoot, safe);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, content, new UTF8Encoding(false));
                applied.Add(safe);
            }
            return applied;
        }
    }

    /// <summary>Wspólna logika dostawców API: kontekst z plików repozytorium, zapytanie, zapis plików, koszt.</summary>
    public abstract class ApiProviderBase : IAiProvider
    {
        protected readonly Func<ProviderSettings> Settings;
        protected readonly Func<IEnumerable<string>> Forbidden;
        protected static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

        protected ApiProviderBase(Func<ProviderSettings> settings, Func<IEnumerable<string>> forbidden)
        {
            Settings = settings;
            Forbidden = forbidden;
        }

        public abstract ProviderId Id { get; }
        public abstract string Name { get; }
        public bool ExecutesCommands => false;
        protected abstract string KeyHelp { get; }

        public Task<ProviderStatus> StatusAsync(CancellationToken ct)
        {
            var s = Settings();
            string? key = Secrets.Unprotect(s.ApiKeyProtected);
            return Task.FromResult(new ProviderStatus
            {
                Id = Id,
                Name = Name,
                Installed = true,
                LoggedIn = !string.IsNullOrEmpty(key),
                AuthMode = string.IsNullOrEmpty(key) ? "brak klucza" : "Klucz API (zaszyfrowany)",
                KeyMasked = Secrets.Mask(key),
                Detail = string.IsNullOrEmpty(key) ? KeyHelp : string.IsNullOrWhiteSpace(s.Model) ? "Wybierz model." : "Model: " + s.Model,
            });
        }

        /// <summary>Wysyła zapytanie; zwraca (tekst, użycie, błąd, kod HTTP).</summary>
        protected abstract Task<(string text, UsageInfo usage, string? error, HttpStatusCode? status)> CallAsync(string key, string model, string system, string user, int maxTokens, CancellationToken ct);

        public async Task<AgentResult> RunAsync(AgentRequest req, AgentProgress progress, CancellationToken ct)
        {
            var res = new AgentResult();
            var s = req.Settings;
            string? key = Secrets.Unprotect(s.ApiKeyProtected);
            if (string.IsNullOrEmpty(key)) { res.Error = Name + ": brak klucza API."; return res; }
            if (string.IsNullOrWhiteSpace(req.Model)) { res.Error = Name + ": nie wybrano modelu."; return res; }
            string system = req.ReadOnly
                ? "Jesteś recenzentem zmian w systemie tradingowym APEX. Odpowiadasz po polsku, zwięźle, punktami."
                : "Jesteś doświadczonym inżynierem systemów tradingowych pracującym nad APEX Trading AI (C#/.NET). Odpowiadasz po polsku.\n\n" + FileBlocks.Instructions;
            progress("stage", $"{Name}: wysyłanie zapytania do modelu {req.Model}…");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(req.Timeout);
            var started = DateTime.UtcNow;
            (string text, UsageInfo usage, string? error, HttpStatusCode? status) call;
            try
            {
                call = await CallAsync(key, req.Model, system, req.Prompt, s.MaxOutputTokens, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                res.Error = ct.IsCancellationRequested ? "Przerwano." : $"Przekroczono limit czasu ({req.Timeout.TotalMinutes:F0} min).";
                return res;
            }
            catch (HttpRequestException ex)
            {
                res.Error = Name + ": błąd połączenia – " + ex.Message;
                return res;
            }
            call.usage.DurationSec = (DateTime.UtcNow - started).TotalSeconds;
            call.usage.CostUsd = Cost(call.usage, s);
            res.Usage = call.usage;
            progress("usage", $"Odpowiedź: {call.usage.InputTokens:N0} tokenów wejścia, {call.usage.OutputTokens:N0} wyjścia" + (call.usage.CostUsd.HasValue ? $", szac. koszt {call.usage.CostUsd.Value:F2} USD" : "") + ".");
            if (call.error != null)
            {
                if (call.status == HttpStatusCode.TooManyRequests || LimitDetector.IsLimit(call.error))
                {
                    res.Usage.LimitReached = true;
                    res.Usage.LimitMessage = call.error;
                    res.Usage.LimitResetUtc = LimitDetector.ResetTime(call.error, DateTime.UtcNow) ?? DateTime.UtcNow.AddMinutes(30);
                }
                res.Error = Name + ": " + call.error;
                return res;
            }
            File.WriteAllText(Path.Combine(req.JobDir, req.ReadOnly ? "api-przeglad.txt" : $"api-odpowiedz-{DateTime.UtcNow:HHmmss}.txt"), call.text);
            if (req.ReadOnly)
            {
                res.Summary = call.text.Trim();
                res.Ok = true;
                return res;
            }
            var parsed = FileBlocks.Parse(call.text);
            var problems = new List<string>(parsed.Problems);
            res.ChangedFiles = FileBlocks.Apply(req.WorkDir, parsed, Forbidden(), problems);
            foreach (var p in problems) progress("warning", p);
            foreach (var f in res.ChangedFiles) progress("file", "Zapisano plik: " + f);
            res.Summary = parsed.Summary;
            res.Ok = true;
            return res;
        }

        public static double? Cost(UsageInfo u, ProviderSettings s)
        {
            if (s.PriceInputPerMTok <= 0 && s.PriceOutputPerMTok <= 0) return null;
            return u.InputTokens / 1e6 * s.PriceInputPerMTok + u.OutputTokens / 1e6 * s.PriceOutputPerMTok;
        }

        protected static string ErrorText(string body, HttpStatusCode code)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var e))
                {
                    string msg = e.ValueKind == JsonValueKind.Object ? CodexEvents.Str(e, "message") : e.ToString();
                    string type = e.ValueKind == JsonValueKind.Object ? (CodexEvents.Str(e, "type") + " " + CodexEvents.Str(e, "code")).Trim() : "";
                    return $"HTTP {(int)code} {type}: {msg}".Trim();
                }
            }
            catch (JsonException) { }
            return $"HTTP {(int)code}: {CodexEvents.Trim(body, 300)}";
        }
    }

    /// <summary>OpenAI Responses API (klucz API, rozliczenie za tokeny).</summary>
    public sealed class OpenAiApiProvider : ApiProviderBase
    {
        public OpenAiApiProvider(Func<ProviderSettings> s, Func<IEnumerable<string>> forbidden) : base(s, forbidden) { }

        public override ProviderId Id => ProviderId.OpenAiApi;
        public override string Name => "OpenAI API";
        protected override string KeyHelp => "Wklej klucz z platform.openai.com (Projekt → API keys). Rozliczenie za tokeny, niezależne od subskrypcji ChatGPT.";

        public static string Endpoint { get; set; } = "https://api.openai.com/v1/responses";

        protected override async Task<(string, UsageInfo, string?, HttpStatusCode?)> CallAsync(string key, string model, string system, string user, int maxTokens, CancellationToken ct)
        {
            var body = JsonSerializer.Serialize(new
            {
                model,
                instructions = system,
                input = new object[] { new { role = "user", content = user } },
                max_output_tokens = maxTokens,
            });
            using var msg = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            msg.Headers.Add("Authorization", "Bearer " + key);
            using var resp = await Http.SendAsync(msg, ct).ConfigureAwait(false);
            string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var usage = new UsageInfo();
            if (!resp.IsSuccessStatusCode) return ("", usage, ErrorText(text, resp.StatusCode), resp.StatusCode);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("usage", out var us))
            {
                usage.InputTokens = CodexEvents.Long(us, "input_tokens");
                usage.OutputTokens = CodexEvents.Long(us, "output_tokens");
                if (us.TryGetProperty("input_tokens_details", out var det)) usage.CachedInputTokens = CodexEvents.Long(det, "cached_tokens");
            }
            var sb = new StringBuilder();
            if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
                foreach (var item in output.EnumerateArray())
                    if (CodexEvents.Str(item, "type") == "message" && item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                        foreach (var c in content.EnumerateArray())
                            if (CodexEvents.Str(c, "type") == "output_text") sb.Append(CodexEvents.Str(c, "text"));
            string status = CodexEvents.Str(root, "status");
            if (sb.Length == 0) return ("", usage, "pusta odpowiedź modelu (status: " + status + ")", resp.StatusCode);
            return (sb.ToString(), usage, status == "incomplete" ? "odpowiedź niekompletna (limit długości) – zwiększ maks. długość odpowiedzi" : null, resp.StatusCode);
        }
    }

    /// <summary>Anthropic Messages API (klucz API, rozliczenie za tokeny).</summary>
    public sealed class AnthropicApiProvider : ApiProviderBase
    {
        public AnthropicApiProvider(Func<ProviderSettings> s, Func<IEnumerable<string>> forbidden) : base(s, forbidden) { }

        public override ProviderId Id => ProviderId.AnthropicApi;
        public override string Name => "Anthropic API";
        protected override string KeyHelp => "Wklej klucz z console.anthropic.com (Settings → API keys). Rozliczenie za tokeny, niezależne od subskrypcji Claude.";

        public static string Endpoint { get; set; } = "https://api.anthropic.com/v1/messages";

        protected override async Task<(string, UsageInfo, string?, HttpStatusCode?)> CallAsync(string key, string model, string system, string user, int maxTokens, CancellationToken ct)
        {
            var body = JsonSerializer.Serialize(new
            {
                model,
                max_tokens = maxTokens,
                system,
                messages = new object[] { new { role = "user", content = user } },
            });
            using var msg = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            msg.Headers.Add("x-api-key", key);
            msg.Headers.Add("anthropic-version", "2023-06-01");
            using var resp = await Http.SendAsync(msg, ct).ConfigureAwait(false);
            string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var usage = new UsageInfo();
            if (!resp.IsSuccessStatusCode) return ("", usage, ErrorText(text, resp.StatusCode), resp.StatusCode);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("usage", out var us))
            {
                usage.CachedInputTokens = CodexEvents.Long(us, "cache_read_input_tokens");
                usage.InputTokens = CodexEvents.Long(us, "input_tokens") + usage.CachedInputTokens + CodexEvents.Long(us, "cache_creation_input_tokens");
                usage.OutputTokens = CodexEvents.Long(us, "output_tokens");
            }
            var sb = new StringBuilder();
            if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                foreach (var c in content.EnumerateArray())
                    if (CodexEvents.Str(c, "type") == "text") sb.Append(CodexEvents.Str(c, "text"));
            string stop = CodexEvents.Str(root, "stop_reason");
            if (sb.Length == 0) return ("", usage, "pusta odpowiedź modelu (stop_reason: " + stop + ")", resp.StatusCode);
            return (sb.ToString(), usage, stop == "max_tokens" ? "odpowiedź ucięta (limit długości) – zwiększ maks. długość odpowiedzi" : null, resp.StatusCode);
        }
    }
}
