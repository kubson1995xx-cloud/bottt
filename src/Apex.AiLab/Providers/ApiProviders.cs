using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
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
Nie usuwaj plików. Nie używaj innych formatów (diff, łatki). Jeśli uznasz, że zmiana nie jest uzasadniona danymi – nie zmieniaj plików i wyjaśnij to w podsumowaniu.
Dozwolone ścieżki: src/, tests/, docs/, config/, tools/ (bez bin/, obj/, .git, plików projektów i skryptów). Pliki zapisywane są w UTF-8.";

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

        /// <summary>Katalogi główne, w których dostawca API może zapisywać pliki.</summary>
        public static readonly string[] AllowedRoots = { "src", "tests", "docs", "config", "tools" };

        /// <summary>
        /// Walidacja ścieżki: względna, w repozytorium, tylko w <see cref="AllowedRoots"/>, bez "..", segmentów .git (także pliku .git
        /// kopii roboczej), składni strumieni ADS (":"), segmentów z końcową kropką/spacją, nazw urządzeń Windows i nazw 8.3,
        /// bez bin/ i obj/ (pliki ignorowane przez git omijają diff), bez plików budowania i skryptów, bez ścieżek zabronionych –
        /// sprawdzanych PO pełnej normalizacji. Zwraca znormalizowaną ścieżkę lub null.
        /// </summary>
        public static string? SafePath(string repoRoot, string relPath, IEnumerable<string> forbidden, out string? problem)
        {
            problem = null;
            string p = relPath.Replace('\\', '/').Trim();
            string Reject(string why) => $"Odrzucono ścieżkę {relPath}: {why}";
            if (p.Length == 0 || p.StartsWith("/", StringComparison.Ordinal) || Regex.IsMatch(p, @"^[A-Za-z]:") || p.Split('/').Any(seg => seg == ".." || seg == "."))
            {
                problem = $"Odrzucono ścieżkę spoza repozytorium: {relPath}";
                return null;
            }
            if (p.Any(c => char.IsControl(c) || c is ':' or '"' or '<' or '>' or '|' or '?' or '*'))
            {
                problem = Reject("niedozwolone znaki (m.in. \":\" – strumienie danych NTFS)");
                return null;
            }
            var segs = p.Split('/');
            foreach (var seg in segs)
            {
                if (seg.Length == 0) { problem = Reject("pusty segment ścieżki"); return null; }
                if (seg.EndsWith(".", StringComparison.Ordinal) || seg.EndsWith(" ", StringComparison.Ordinal) || seg.StartsWith(" ", StringComparison.Ordinal))
                { problem = Reject("segment kończący się kropką lub spacją (alias w Windows)"); return null; }
                if (seg.Equals(".git", StringComparison.OrdinalIgnoreCase)) { problem = Reject("ścieżki .git są niedozwolone"); return null; }
                if (Regex.IsMatch(seg, @"~\d")) { problem = Reject("nazwa w formacie 8.3 (alias w Windows)"); return null; }
                if (Regex.IsMatch(seg.Split('.')[0], @"^(?i:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[0-9¹²³]|LPT[0-9¹²³])$"))
                { problem = Reject("zarezerwowana nazwa urządzenia Windows"); return null; }
                if (seg.Equals("bin", StringComparison.OrdinalIgnoreCase) || seg.Equals("obj", StringComparison.OrdinalIgnoreCase) || seg.Equals(".vs", StringComparison.OrdinalIgnoreCase) || seg.Equals("TestResults", StringComparison.OrdinalIgnoreCase))
                { problem = Reject("katalogi bin/, obj/ i wyniki budowania są ignorowane przez git i omijają audyt"); return null; }
            }
            if (segs.Length < 2 || !AllowedRoots.Contains(segs[0], StringComparer.Ordinal))
            {
                problem = Reject("dozwolone są tylko pliki w " + string.Join(", ", AllowedRoots.Select(r => r + "/")));
                return null;
            }
            if (Pipeline.ChangeAudit.IsBuildOrScriptFile(p) || p.EndsWith(".user", StringComparison.OrdinalIgnoreCase))
            {
                problem = Reject("AI nie może zmieniać konfiguracji budowania ani skryptów");
                return null;
            }
            string root = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(root, p));
            var cmp = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!full.StartsWith(root, cmp))
            {
                problem = $"Odrzucono ścieżkę spoza repozytorium: {relPath}";
                return null;
            }
            // Ścieżka po normalizacji systemu musi być identyczna z podaną (inaczej Windows mógłby ją zinterpretować inaczej niż audyt).
            string normalized = Path.GetRelativePath(root, full).Replace('\\', '/');
            if (!string.Equals(normalized, p, StringComparison.Ordinal))
            {
                problem = Reject("ścieżka zmienia się po normalizacji (" + normalized + ")");
                return null;
            }
            foreach (var f in forbidden)
            {
                string fn = f.Replace('\\', '/').TrimStart('/');
                if (fn.Length > 0 && (normalized.StartsWith(fn, StringComparison.OrdinalIgnoreCase) || p.StartsWith(fn, StringComparison.OrdinalIgnoreCase)))
                {
                    problem = $"Odrzucono zmianę w ścieżce zabronionej: {p}";
                    return null;
                }
            }
            // Żaden istniejący element ścieżki nie może być dowiązaniem (zapis poza kopią roboczą).
            string cur = root;
            foreach (var seg in segs)
            {
                cur = Path.Combine(cur, seg);
                var info = new FileInfo(cur);
                if ((info.Exists || Directory.Exists(cur)) && (File.GetAttributes(cur) & FileAttributes.ReparsePoint) != 0)
                {
                    problem = Reject("element ścieżki jest dowiązaniem");
                    return null;
                }
                if (!info.Exists && !Directory.Exists(cur)) break;
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
                // Częściowa odpowiedź tylko do wglądu (katalog zadania, nie kopia robocza) – żadne pliki nie są stosowane.
                if (!string.IsNullOrEmpty(call.text))
                    try { File.WriteAllText(Path.Combine(req.JobDir, $"api-odpowiedz-przerwana-{DateTime.UtcNow:HHmmss}.txt"), call.text); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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

        /// <summary>
        /// Szacunkowy koszt z cen wpisanych przez użytkownika. Tokeny wejściowe z pamięci podręcznej (część InputTokens) są tańsze:
        /// cena PriceCachedInputPerMTok, a gdy nie jest podana – 10% ceny wejścia (typowy cennik OpenAI/Anthropic).
        /// </summary>
        public static double? Cost(UsageInfo u, ProviderSettings s)
        {
            if (s.PriceInputPerMTok <= 0 && s.PriceOutputPerMTok <= 0) return null;
            long cached = Math.Clamp(u.CachedInputTokens, 0, Math.Max(0, u.InputTokens));
            long fresh = Math.Max(0, u.InputTokens) - cached;
            double cachedPrice = s.PriceCachedInputPerMTok > 0 ? s.PriceCachedInputPerMTok : s.PriceInputPerMTok * 0.1;
            return fresh / 1e6 * s.PriceInputPerMTok + cached / 1e6 * cachedPrice + Math.Max(0, u.OutputTokens) / 1e6 * s.PriceOutputPerMTok;
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
            string? refusal = null;
            if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
                foreach (var item in output.EnumerateArray())
                    if (CodexEvents.Str(item, "type") == "message" && item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                        foreach (var c in content.EnumerateArray())
                        {
                            string ct2 = CodexEvents.Str(c, "type");
                            if (ct2 == "output_text") sb.Append(CodexEvents.Str(c, "text"));
                            else if (ct2 == "refusal") refusal = CodexEvents.Str(c, "refusal");
                        }
            string status = CodexEvents.Str(root, "status");
            return (sb.ToString(), usage, OpenAiResponseError(status, root, refusal, sb.Length), resp.StatusCode);
        }

        /// <summary>
        /// Sukces wyłącznie dla zakończonej odpowiedzi (status "completed" – lub brak pola status), bez odmowy i bez incomplete_details.
        /// Każde inne zakończenie = błąd, a AI Lab nie zapisuje żadnych plików z częściowej odpowiedzi.
        /// </summary>
        public static string? OpenAiResponseError(string status, JsonElement root, string? refusal, int textLength)
        {
            if (refusal != null) return "model odmówił wykonania zadania" + (refusal.Length > 0 ? ": " + CodexEvents.Trim(refusal, 300) : "") + " – nie zapisano żadnych plików";
            string reason = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("incomplete_details", out var det) && det.ValueKind == JsonValueKind.Object ? CodexEvents.Str(det, "reason") : "";
            if (status == "incomplete" || reason.Length > 0)
                return reason is "max_output_tokens" or ""
                    ? "odpowiedź niekompletna (limit długości) – zwiększ maks. długość odpowiedzi; nie zapisano żadnych plików"
                    : "odpowiedź niekompletna (" + reason + ") – nie zapisano żadnych plików";
            if (status.Length > 0 && status != "completed") return "odpowiedź przerwana (status: " + status + ") – nie zapisano żadnych plików";
            if (textLength == 0) return "pusta odpowiedź modelu (status: " + status + ")";
            return null;
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
            return (sb.ToString(), usage, StopReasonError(stop, sb.Length), resp.StatusCode);
        }

        /// <summary>
        /// Sukces wyłącznie dla stop_reason "end_turn" lub "stop_sequence". Każdy inny powód (refusal, max_tokens,
        /// model_context_window_exceeded, pause_turn, tool_use, brak pola) = błąd – bloki plików z częściowej odpowiedzi nie są zapisywane.
        /// </summary>
        public static string? StopReasonError(string stop, int textLength)
        {
            if (stop is "end_turn" or "stop_sequence")
                return textLength == 0 ? "pusta odpowiedź modelu (stop_reason: " + stop + ")" : null;
            return stop switch
            {
                "max_tokens" => "odpowiedź ucięta (limit długości) – zwiększ maks. długość odpowiedzi; nie zapisano żadnych plików",
                "refusal" => "model odmówił wykonania zadania (stop_reason: refusal) – nie zapisano żadnych plików",
                "" => "odpowiedź bez stop_reason – traktowana jako przerwana; nie zapisano żadnych plików",
                _ => "odpowiedź przerwana (stop_reason: " + stop + ") – nie zapisano żadnych plików",
            };
        }
    }
}
