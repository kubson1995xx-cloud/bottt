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
        /// <summary>Zezwolenie na LIVE dla tego konta. Działa tylko gdy globalne LiveTradingFeature = true (edycja pliku).</summary>
        public bool AllowLive { get; set; }
        public string LiveConfirmation { get; set; } = "";
        public RiskSettings? Risk { get; set; }
        public double? MinScore { get; set; }
        /// <summary>Ręczna prowizja (wymagana dla MT5 – platforma nie udostępnia prowizji przez API).</summary>
        public CommissionOverride? Commission { get; set; }
        public UniverseSettings? Universe { get; set; }
        public bool AutoStart { get; set; } = true;
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
        /// GŁÓWNY WYŁĄCZNIK HANDLU NA KONTACH LIVE. Domyślnie false. Celowo NIE da się go zmienić z panelu –
        /// wymaga świadomej edycji pliku konfiguracyjnego. Dopiero potem w panelu można odblokować konkretne konto LIVE frazą.
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
                try { cfg = JsonSerializer.Deserialize<HostConfig>(File.ReadAllText(Path_), Json) ?? new HostConfig(); }
                catch (JsonException ex)
                {
                    // Uszkodzony plik – kopia zapasowa i bezpieczne ustawienia domyślne (LIVE wyłączone).
                    File.Copy(Path_, Path_ + ".uszkodzony-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), true);
                    Console.Error.WriteLine("Uszkodzony plik konfiguracji: " + ex.Message);
                    cfg = new HostConfig();
                }
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
                string tmp = Path_ + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Config, Json));
                File.Move(tmp, Path_, true);
            }
        }

        public void Update(Action<HostConfig> change)
        {
            lock (_lock)
            {
                change(Config);
                Save();
            }
        }

        public AccountSettings Account(string key)
        {
            lock (_lock)
            {
                if (!Config.Accounts.TryGetValue(key, out var a))
                {
                    a = new AccountSettings();
                    Config.Accounts[key] = a;
                    Save();
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
