using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Apex.Core.Bridge;
using Apex.Core.Engine;
using Apex.Core.Risk;

namespace Apex.Desktop.Services
{
    /// <summary>Ustawienia pojedynczego konta (klucz: platforma:broker:numer).</summary>
    public sealed class AccountSettings
    {
        /// <summary>Pole historyczne, zachowane dla zgodności plików. Wersja DEMO ignoruje zezwolenie na LIVE.</summary>
        public bool AllowLive { get; set; }
        public string LiveConfirmation { get; set; } = "";
        public RiskSettings? Risk { get; set; }
        public double? MinScore { get; set; }
        /// <summary>Ręczna prowizja (wymagana dla MT5 – platforma nie udostępnia prowizji przez API).</summary>
        public CommissionOverride? Commission { get; set; }
        public UniverseSettings? Universe { get; set; }
        public bool AutoStart { get; set; }
    }

    /// <summary>Konfiguracja aplikacji (plik apex.config.json w katalogu danych).</summary>
    public sealed class HostConfig
    {
        public int HttpPort { get; set; } = 5077;
        public int BridgeTcpPort { get; set; } = 5078;
        public string PairingKey { get; set; } = "";
        public string ApiToken { get; set; } = "";
        public string Label { get; set; } = "APEX";
        /// <summary>
        /// Pole historyczne konfiguracji. Nawet true nie odblokowuje LIVE w wersji DEMO.
        /// </summary>
        public bool LiveTradingFeature { get; set; }
        public List<string> AllowedBrokers { get; set; } = new();
        public RiskSettings Risk { get; set; } = new();
        public double MinScore { get; set; } = 50;
        public NewsSettings News { get; set; } = new();
        public string NewsUrl { get; set; } = "https://nfs.faireconomy.media/ff_calendar_thisweek.json";
        public UniverseSettings Universe { get; set; } = new();
        /// <summary>Ścieżka do własnego profilu strategii (JSON). Puste = profil wbudowany (zweryfikowany backtestem).</summary>
        public string? ProfilePath { get; set; }
        public Dictionary<string, AccountSettings> Accounts { get; set; } = new();
        /// <summary>Domyślna prowizja dla MT5, gdy konto nie ma własnego ustawienia (null = handel zablokowany do czasu konfiguracji).</summary>
        public CommissionOverride? Mt5DefaultCommission { get; set; }
        public bool OpenBrowserOnStart { get; set; } = true;
    }

    /// <summary>Wczytywanie i bezpieczny zapis konfiguracji (zapis atomowy przez plik tymczasowy).</summary>
    public sealed class ConfigStore
    {
        // Set only by the separate friend test executable, never read from host config/JSON/CLI.
        internal bool TrustedSyntheticComposition { get; set; }
        private readonly object _lock = new();
        public static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
            PropertyNameCaseInsensitive = true,
        };

        public ConfigStore(string dataDir)
        {
            DataDir = dataDir;
            Directory.CreateDirectory(dataDir);
            Path_ = System.IO.Path.Combine(dataDir, "apex.config.json");
            Config = Load();
        }

        public string DataDir { get; }
        public string Path_ { get; }
        public HostConfig Config { get; private set; }

        private HostConfig Load()
        {
            HostConfig cfg;
            if (File.Exists(Path_))
            {
                try { cfg = JsonSerializer.Deserialize<HostConfig>(File.ReadAllText(Path_), Json)
                    ?? throw new InvalidDataException("Pusty obiekt konfiguracji."); }
                catch (JsonException ex)
                {
                    throw new InvalidDataException("Uszkodzony plik konfiguracji – zachowano oryginał; wymagane sprawdzenie.", ex);
                }
                if (cfg.Risk == null || cfg.News == null || cfg.Universe == null || cfg.Accounts == null || cfg.AllowedBrokers == null)
                    throw new InvalidDataException("Niekompletna konfiguracja – wymagane sprawdzenie pliku.");
            }
            else cfg = new HostConfig();

            bool changed = false;
            if (string.IsNullOrWhiteSpace(cfg.PairingKey)) { cfg.PairingKey = NewPairingKey(); changed = true; }
            if (string.IsNullOrWhiteSpace(cfg.ApiToken) || cfg.ApiToken.Length < 32) { cfg.ApiToken = NewToken(); changed = true; }
            if (cfg.Risk.Validate() != null) { cfg.Risk = new RiskSettings(); changed = true; }
            Config = cfg;
            if (changed || !File.Exists(Path_)) Save();
            return cfg;
        }

        public void Save()
        {
            lock (_lock)
            {
                AtomicFile.Write(Path_, JsonSerializer.Serialize(Config, Json));
            }
        }

        public void Update(Action<HostConfig> change)
        {
            lock (_lock)
            {
                // Zmiana jest widoczna w pamięci dopiero po udanym trwałym zapisie.
                var candidate = JsonSerializer.Deserialize<HostConfig>(JsonSerializer.Serialize(Config, Json), Json)!;
                change(candidate);
                AtomicFile.Write(Path_, JsonSerializer.Serialize(candidate, Json));
                Config = candidate;
            }
        }

        public AccountSettings Account(string key)
        {
            lock (_lock)
            {
                if (!Config.Accounts.TryGetValue(key, out var a))
                {
                    Update(c => c.Accounts[key] = new AccountSettings());
                    a = Config.Accounts[key];
                }
                return a;
            }
        }

        public static string NewPairingKey()
        {
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var bytes = RandomNumberGenerator.GetBytes(12);
            var chars = new char[14];
            int j = 0;
            for (int i = 0; i < 12; i++)
            {
                if (i > 0 && i % 4 == 0) chars[j++] = '-';
                chars[j++] = alphabet[bytes[i] % alphabet.Length];
            }
            return new string(chars, 0, j);
        }

        public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        public static string DefaultDataDir()
        {
            string? env = Environment.GetEnvironmentVariable("APEX_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(env)) return env!;
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDir)) baseDir = AppContext.BaseDirectory;
            return System.IO.Path.Combine(baseDir, "APEX Trading AI");
        }
    }
}
