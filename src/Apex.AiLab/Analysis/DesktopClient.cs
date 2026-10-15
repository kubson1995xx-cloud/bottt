using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Apex.AiLab.Analysis
{
    /// <summary>
    /// Klient APEX Desktop – WYŁĄCZNIE ODCZYT (status, dziennik transakcji, reżim instrumentu).
    /// AI Lab nie wysyła do silnika żadnych poleceń handlowych.
    /// </summary>
    public sealed class DesktopClient
    {
        private readonly string _apexDataDir;
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

        public DesktopClient(string apexDataDir)
        {
            _apexDataDir = apexDataDir;
        }

        private (string baseUrl, string token)? Endpoint()
        {
            string cfg = Path.Combine(_apexDataDir, "apex.config.json");
            if (!File.Exists(cfg)) return null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
                string token = doc.RootElement.TryGetProperty("ApiToken", out var t) ? t.GetString() ?? "" : "";
                int port = doc.RootElement.TryGetProperty("HttpPort", out var p) && p.TryGetInt32(out int pp) ? pp : 5077;
                // Zawsze pętla zwrotna; port z konfiguracji APEX Desktop.
                return token.Length == 0 ? null : ($"http://127.0.0.1:{port}", token);
            }
            catch (JsonException) { return null; }
            catch (IOException) { return null; }
        }

        private async Task<string?> GetAsync(string path, CancellationToken ct)
        {
            var ep = Endpoint();
            if (ep == null) return null;
            using var req = new HttpRequestMessage(HttpMethod.Get, ep.Value.baseUrl + path);
            req.Headers.Add("X-Apex-Token", ep.Value.token);
            try
            {
                using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) { return null; }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return null; }
        }

        public async Task<JsonDocument?> StatusAsync(CancellationToken ct)
        {
            string? s = await GetAsync("/api/status", ct).ConfigureAwait(false);
            if (s == null) return null;
            try { return JsonDocument.Parse(s); } catch (JsonException) { return null; }
        }

        public Task<string?> JournalCsvAsync(string accountKey, CancellationToken ct) =>
            GetAsync("/api/accounts/" + Uri.EscapeDataString(accountKey) + "/journal.csv", ct);

        public async Task<JsonDocument?> InstrumentAsync(string accountKey, string symbol, CancellationToken ct)
        {
            string? s = await GetAsync("/api/accounts/" + Uri.EscapeDataString(accountKey) + "/instrument/" + Uri.EscapeDataString(symbol), ct).ConfigureAwait(false);
            if (s == null) return null;
            try { return JsonDocument.Parse(s); } catch (JsonException) { return null; }
        }
    }

    /// <summary>Wiersz dziennika transakcji (transakcje.csv) – parser odporny na kolejność kolumn.</summary>
    public sealed class JournalTrade
    {
        public string Symbol { get; set; } = "";
        public string AssetClass { get; set; } = "";
        public string Strategy { get; set; } = "";
        public DateTime ExitUtc { get; set; }
        public double NetProfit { get; set; }
        public double R { get; set; }
        public string ExitReason { get; set; } = "";
        public string Regime { get; set; } = "";

        public static List<JournalTrade> ParseCsv(string? csv)
        {
            var list = new List<JournalTrade>();
            if (string.IsNullOrWhiteSpace(csv)) return list;
            var lines = csv.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2) return list;
            var head = lines[0].Split(';').Select((h, i) => (h.Trim(), i)).ToDictionary(x => x.Item1, x => x.i, StringComparer.OrdinalIgnoreCase);
            string Col(string[] f, string name) => head.TryGetValue(name, out int i) && i < f.Length ? f[i] : "";
            double Num(string[] f, string name) => double.TryParse(Col(f, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
            foreach (var line in lines.Skip(1))
            {
                var f = line.Split(';');
                if (f.Length < 5) continue;
                DateTime.TryParse(Col(f, "exit_time_utc"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var exit);
                list.Add(new JournalTrade
                {
                    Symbol = Col(f, "symbol"),
                    AssetClass = Col(f, "asset_class"),
                    Strategy = Col(f, "strategy"),
                    ExitUtc = exit,
                    NetProfit = Num(f, "net_profit"),
                    R = Num(f, "r_multiple"),
                    ExitReason = Col(f, "exit_reason"),
                    Regime = Col(f, "regime"),
                });
            }
            return list;
        }
    }
}
