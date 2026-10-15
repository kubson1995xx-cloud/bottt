using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Apex.AiLab.Lab
{
    public enum ProviderId
    {
        /// <summary>Codex CLI – logowanie kontem ChatGPT (subskrypcja) lub kluczem OpenAI API.</summary>
        Codex,
        /// <summary>Claude Code CLI – logowanie subskrypcją Claude lub kontem Anthropic Console.</summary>
        ClaudeCode,
        /// <summary>Bezpośrednio OpenAI API (klucz) – tryb łatek bez wykonywania poleceń.</summary>
        OpenAiApi,
        /// <summary>Bezpośrednio Anthropic API (klucz) – tryb łatek bez wykonywania poleceń.</summary>
        AnthropicApi,
    }

    public enum ScheduleKind
    {
        Off,
        EveryHours,
        Daily,
        Weekly,
    }

    public sealed class ProviderSettings
    {
        public bool Enabled { get; set; }
        /// <summary>Identyfikator modelu. Pusty = domyślny model narzędzia (CLI) – dla API wymagany.</summary>
        public string Model { get; set; } = "";
        /// <summary>Klucz API zaszyfrowany (Windows DPAPI, tylko bieżący użytkownik). Tylko dla OpenAiApi/AnthropicApi.</summary>
        public string? ApiKeyProtected { get; set; }
        /// <summary>Cena za 1 mln tokenów wejściowych (USD) – wpisywana przez użytkownika; 0 = nieznana.</summary>
        public double PriceInputPerMTok { get; set; }
        public double PriceOutputPerMTok { get; set; }
        public int TimeoutMinutes { get; set; } = 45;
        /// <summary>Ręczna ścieżka do programu (codex/claude), gdy nie ma go w PATH.</summary>
        public string? ExecutablePath { get; set; }
        /// <summary>Claude Code: limit kosztu jednego uruchomienia przy rozliczeniu API (0 = brak).</summary>
        public double MaxBudgetUsdPerRun { get; set; }
        /// <summary>API: maksymalna długość odpowiedzi (tokeny).</summary>
        public int MaxOutputTokens { get; set; } = 32000;
        /// <summary>API: liczba rund poprawek po błędzie kompilacji/testów.</summary>
        public int MaxRepairRounds { get; set; } = 1;
    }

    public sealed class ScheduleSettings
    {
        public ScheduleKind Kind { get; set; } = ScheduleKind.Daily;
        public int EveryHours { get; set; } = 24;
        /// <summary>Godzina i minuta czasu lokalnego (Daily/Weekly).</summary>
        public int Hour { get; set; } = 6;
        public int Minute { get; set; } = 30;
        public DayOfWeek WeeklyDay { get; set; } = DayOfWeek.Saturday;
        /// <summary>Agent AI uruchamiany z harmonogramu tylko wtedy, gdy analiza wykryje problem (oszczędza limity).</summary>
        public bool AgentOnlyWithFindings { get; set; } = true;
        public bool TriggerOnStrategyDegradation { get; set; } = true;
        public bool TriggerOnMarketChange { get; set; } = true;
        public bool TriggerOnExecutionErrors { get; set; } = true;
        /// <summary>Minimalny odstęp między automatycznymi uruchomieniami agenta.</summary>
        public int MinHoursBetweenAgentRuns { get; set; } = 24;
        /// <summary>Co ile minut zapisywać migawkę reżimów rynku (wykrywanie zmian warunków).</summary>
        public int MarketSnapshotMinutes { get; set; } = 60;
        /// <summary>Od kiedy automatyzacja jest włączona (pierwszy termin harmonogramu liczony od tej chwili).</summary>
        public DateTime? EnabledSinceUtc { get; set; }
    }

    public sealed class BudgetSettings
    {
        public int MaxAgentRunsPerDay { get; set; } = 2;
        public int MaxAgentRunsPerWeek { get; set; } = 7;
        public long MaxTokensPerDay { get; set; } = 3_000_000;
        /// <summary>Miesięczny limit kosztów dla rozliczeń API (wymaga wpisanych cen). 0 = brak limitu kwotowego.</summary>
        public double MaxUsdPerMonth { get; set; } = 20;
        /// <summary>Po komunikacie o wyczerpaniu limitu subskrypcji – brak prób do czasu odnowienia.</summary>
        public bool PauseOnSubscriptionLimit { get; set; } = true;
    }

    public sealed class RepoSettings
    {
        /// <summary>Repozytorium źródłowe APEX (z katalogiem .git). AI Lab pracuje na WŁASNEJ kopii – źródło nie jest zmieniane.</summary>
        public string SourceRepoPath { get; set; } = "";
        /// <summary>Gałąź bazowa w repozytorium źródłowym; pusta = bieżąca gałąź (HEAD).</summary>
        public string BaseBranch { get; set; } = "";
        /// <summary>Katalog danych historycznych dla backtestu (pliki *_H1.csv, *_M15.csv). Pusty = bez backtestu.</summary>
        public string BacktestDataDir { get; set; } = "";
        public bool RunBacktests { get; set; } = true;
        public int BuildTimeoutMinutes { get; set; } = 20;
        public int TestTimeoutMinutes { get; set; } = 25;
        public int BacktestTimeoutMinutes { get; set; } = 30;
        /// <summary>Liczba zachowywanych kopii roboczych zadań (starsze katalogi są usuwane; gałęzie zostają).</summary>
        public int KeepWorktrees { get; set; } = 3;
    }

    public sealed class PolicySettings
    {
        /// <summary>
        /// Moduły chronione: RiskEngine, limity bezpieczeństwa, strażnik konta/LIVE, wykonanie zleceń, aktywne strategie (profil), mosty.
        /// Zmiany są dozwolone wyłącznie jako PROPOZYCJE – wdrożenie wymaga dodatkowego, wpisanego potwierdzenia.
        /// </summary>
        public List<string> ProtectedPaths { get; set; } = DefaultProtected.ToList();

        /// <summary>Ścieżki, których AI nie może zmieniać nigdy (moduł AI Lab – agent nie modyfikuje własnych zabezpieczeń).</summary>
        public List<string> ForbiddenPaths { get; set; } = DefaultForbidden.ToList();

        public static readonly string[] DefaultProtected =
        {
            "src/Apex.Core/Risk/",
            "src/Apex.Core/Safety/",
            "src/Apex.Core/Execution/",
            "src/Apex.Core/Adaptive/DefaultProfile.cs",
            "src/Apex.Core/Adaptive/StrategyBook.cs",
            "config/profile_default.json",
            "src/Apex.Core/Engine/TradingEngine.cs",
            "src/Apex.Core/Engine/EngineConfig.cs",
            "src/Apex.Desktop/Services/HostConfig.cs",
            "src/Apex.Desktop/Runtime/AccountRuntime.cs",
            "src/Apex.Desktop/Bridge/",
            "src/Platforms/",
        };

        public static readonly string[] DefaultForbidden =
        {
            "src/Apex.AiLab/",
            ".git/",
            ".github/",
            "tools/windows/",
        };
    }

    /// <summary>Konfiguracja AI Lab (plik ailab.config.json w katalogu danych AI Lab).</summary>
    public sealed class LabConfig
    {
        public int HttpPort { get; set; } = 5079;
        public string ApiToken { get; set; } = "";
        /// <summary>Główny przełącznik automatyzacji (harmonogram i wyzwalacze). Domyślnie wyłączony do czasu konfiguracji.</summary>
        public bool AutomationEnabled { get; set; }
        /// <summary>Uruchamiaj AI Lab razem z APEX (skrypt "Uruchom APEX.cmd").</summary>
        public bool Autostart { get; set; }
        public ProviderId PrimaryProvider { get; set; } = ProviderId.Codex;
        public ProviderId? FallbackProvider { get; set; } = ProviderId.ClaudeCode;
        /// <summary>Opcjonalny drugi model do niezależnego przeglądu zmian (audyt AI).</summary>
        public ProviderId? ReviewProvider { get; set; }
        public Dictionary<ProviderId, ProviderSettings> Providers { get; set; } = new()
        {
            [ProviderId.Codex] = new ProviderSettings { Enabled = true },
            [ProviderId.ClaudeCode] = new ProviderSettings { Enabled = true },
            [ProviderId.OpenAiApi] = new ProviderSettings(),
            [ProviderId.AnthropicApi] = new ProviderSettings { Model = "claude-opus-5-5" },
        };
        public ScheduleSettings Schedule { get; set; } = new();
        public BudgetSettings Budget { get; set; } = new();
        public RepoSettings Repo { get; set; } = new();
        public PolicySettings Policy { get; set; } = new();

        public ProviderSettings Provider(ProviderId id)
        {
            if (!Providers.TryGetValue(id, out var p)) Providers[id] = p = new ProviderSettings();
            return p;
        }

        /// <summary>Walidacja ustawień zmienianych z panelu. Zwraca listę błędów (PL).</summary>
        public List<string> Validate()
        {
            var e = new List<string>();
            if (Schedule.EveryHours < 1 || Schedule.EveryHours > 24 * 14) e.Add("Częstotliwość: od 1 godziny do 14 dni.");
            if (Schedule.Hour < 0 || Schedule.Hour > 23 || Schedule.Minute < 0 || Schedule.Minute > 59) e.Add("Nieprawidłowa godzina harmonogramu.");
            if (Schedule.MinHoursBetweenAgentRuns < 1 || Schedule.MinHoursBetweenAgentRuns > 24 * 30) e.Add("Minimalny odstęp agenta: 1 godz. – 30 dni.");
            if (Schedule.MarketSnapshotMinutes < 15 || Schedule.MarketSnapshotMinutes > 24 * 60) e.Add("Migawki rynku: co 15 min – 24 godz.");
            if (Budget.MaxAgentRunsPerDay < 0 || Budget.MaxAgentRunsPerDay > 50) e.Add("Limit uruchomień dziennie: 0–50.");
            if (Budget.MaxAgentRunsPerWeek < 0 || Budget.MaxAgentRunsPerWeek > 200) e.Add("Limit uruchomień tygodniowo: 0–200.");
            if (Budget.MaxTokensPerDay < 0) e.Add("Limit tokenów nie może być ujemny.");
            if (Budget.MaxUsdPerMonth < 0 || Budget.MaxUsdPerMonth > 10_000) e.Add("Limit kosztów miesięcznie: 0–10 000 USD.");
            foreach (var kv in Providers)
            {
                var p = kv.Value;
                if (p.TimeoutMinutes < 5 || p.TimeoutMinutes > 240) e.Add($"{kv.Key}: limit czasu 5–240 min.");
                if (p.PriceInputPerMTok < 0 || p.PriceOutputPerMTok < 0) e.Add($"{kv.Key}: ceny nie mogą być ujemne.");
                if (p.MaxRepairRounds < 0 || p.MaxRepairRounds > 3) e.Add($"{kv.Key}: rundy poprawek 0–3.");
                if (p.MaxOutputTokens < 1000 || p.MaxOutputTokens > 128_000) e.Add($"{kv.Key}: długość odpowiedzi 1000–128000 tokenów.");
                if (!string.IsNullOrEmpty(p.Model) && (p.Model.Length > 80 || p.Model.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '.' or '_' or ':' or '/' or '[' or ']'))))
                    e.Add($"{kv.Key}: nieprawidłowy identyfikator modelu.");
            }
            if (FallbackProvider == PrimaryProvider) FallbackProvider = null;
            return e;
        }
    }

    /// <summary>Wczytywanie i atomowy zapis konfiguracji AI Lab.</summary>
    public sealed class LabConfigStore
    {
        private readonly object _lock = new();

        public static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        public LabConfigStore(string labDir)
        {
            LabDir = labDir;
            Directory.CreateDirectory(labDir);
            FilePath = Path.Combine(labDir, "ailab.config.json");
            Config = Load();
        }

        public string LabDir { get; }
        public string FilePath { get; }
        public LabConfig Config { get; private set; }

        private LabConfig Load()
        {
            LabConfig cfg;
            if (File.Exists(FilePath))
            {
                try { cfg = JsonSerializer.Deserialize<LabConfig>(File.ReadAllText(FilePath), Json) ?? new LabConfig(); }
                catch (JsonException)
                {
                    File.Copy(FilePath, FilePath + ".uszkodzony-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), true);
                    cfg = new LabConfig();
                }
            }
            else cfg = new LabConfig();
            foreach (ProviderId id in Enum.GetValues(typeof(ProviderId))) cfg.Provider(id);
            if (string.IsNullOrWhiteSpace(cfg.ApiToken) || cfg.ApiToken.Length < 32) cfg.ApiToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            // Lista chroniona może być rozszerzana, ale nie zawężana poniżej wartości domyślnych.
            foreach (var p in PolicySettings.DefaultProtected) if (!cfg.Policy.ProtectedPaths.Contains(p)) cfg.Policy.ProtectedPaths.Add(p);
            foreach (var p in PolicySettings.DefaultForbidden) if (!cfg.Policy.ForbiddenPaths.Contains(p)) cfg.Policy.ForbiddenPaths.Add(p);
            Save(cfg);
            return cfg;
        }

        public void Update(Action<LabConfig> change)
        {
            lock (_lock)
            {
                change(Config);
                Save(Config);
            }
        }

        private void Save(LabConfig cfg)
        {
            lock (_lock)
            {
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, Json));
                File.Move(tmp, FilePath, true);
                RestrictToCurrentUser(FilePath);
            }
        }

        public static void RestrictToCurrentUser(string path)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return; // %LOCALAPPDATA% jest już prywatny dla użytkownika
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Szyfrowanie kluczy API: Windows DPAPI (tylko bieżący użytkownik). Na innych systemach – plik z uprawnieniami 600.</summary>
    public static class Secrets
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("APEX-AiLab-v1");

        public static string Protect(string plain)
        {
            var bytes = Encoding.UTF8.GetBytes(plain);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser));
            return "plain:" + Convert.ToBase64String(bytes);
        }

        public static string? Unprotect(string? stored)
        {
            if (string.IsNullOrEmpty(stored)) return null;
            try
            {
                if (stored.StartsWith("dpapi:", StringComparison.Ordinal) && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored.Substring(6)), Entropy, DataProtectionScope.CurrentUser));
                if (stored.StartsWith("plain:", StringComparison.Ordinal))
                    return Encoding.UTF8.GetString(Convert.FromBase64String(stored.Substring(6)));
            }
            catch (CryptographicException) { }
            catch (FormatException) { }
            return null;
        }

        /// <summary>Maskowanie do wyświetlenia (np. "sk-…a1b2").</summary>
        public static string Mask(string? plain) =>
            string.IsNullOrEmpty(plain) ? "" : plain.Length <= 8 ? "••••" : plain.Substring(0, 3) + "…" + plain.Substring(plain.Length - 4);
    }
}
