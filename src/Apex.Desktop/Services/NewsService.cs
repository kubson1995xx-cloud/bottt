using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Engine;

namespace Apex.Desktop.Services
{
    /// <summary>
    /// Pobieranie kalendarza makroekonomicznego (wspólnego dla wszystkich kont) co 4 godziny.
    /// Kopia zapasowa na dysku pozwala działać po restarcie bez sieci. Błąd pobrania nie zatrzymuje bota –
    /// panel pokazuje ostrzeżenie, a filtry zmienności/spreadu nadal działają.
    /// </summary>
    public sealed class NewsService
    {
        private readonly NewsCalendar _calendar;
        private readonly ConfigStore _config;
        private readonly AppLog _log;
        private readonly string _cachePath;
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

        public NewsService(NewsCalendar calendar, ConfigStore config, AppLog log)
        {
            _calendar = calendar;
            _config = config;
            _log = log;
            _cachePath = Path.Combine(config.DataDir, "kalendarz.json");
            if (File.Exists(_cachePath) && _calendar.TryRestore(File.ReadAllText(_cachePath)))
                _log.Info($"Wczytano kopię kalendarza wiadomości ({_calendar.Events.Count} wydarzeń).");
        }

        public string LastError { get; private set; } = "";

        public void Start(CancellationToken ct)
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("APEX-Trading-AI/1.0");
            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    await RefreshAsync(ct).ConfigureAwait(false);
                    try { await Task.Delay(TimeSpan.FromHours(4), ct).ConfigureAwait(false); }
                    catch (TaskCanceledException) { break; }
                }
            }, ct);
        }

        public async Task RefreshAsync(CancellationToken ct)
        {
            if (!_config.Config.News.Enabled) return;
            string url = _config.Config.NewsUrl;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                LastError = "Adres kalendarza musi zaczynać się od https://";
                return;
            }
            try
            {
                string json = await Http.GetStringAsync(uri, ct).ConfigureAwait(false);
                var events = NewsCalendar.ParseFfJson(json);
                if (events.Count == 0) { LastError = "Pusty kalendarz"; return; }
                _calendar.SetEvents(events, DateTime.UtcNow, uri.Host);
                File.WriteAllText(_cachePath, _calendar.Serialize());
                LastError = "";
                _log.Info($"Kalendarz wiadomości zaktualizowany: {events.Count} wydarzeń.");
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is System.Text.Json.JsonException || ex is IOException)
            {
                LastError = ex.Message;
                _log.Warn("Nie udało się pobrać kalendarza wiadomości: " + ex.Message);
            }
        }
    }
}
