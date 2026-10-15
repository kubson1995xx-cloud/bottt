using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Apex.Core.Model;
using Apex.Core.Safety;

namespace Apex.Core.Engine
{
    public enum NewsImpact
    {
        None = 0,
        Low = 1,
        Medium = 2,
        High = 3,
    }

    public sealed class EconomicEvent
    {
        public DateTime TimeUtc { get; set; }
        public string Currency { get; set; } = "";
        public NewsImpact Impact { get; set; }
        public string Title { get; set; } = "";
    }

    public sealed class NewsSettings
    {
        public bool Enabled { get; set; } = true;
        public NewsImpact MinImpact { get; set; } = NewsImpact.High;
        public int MinutesBefore { get; set; } = 30;
        public int MinutesAfter { get; set; } = 30;
        /// <summary>Czy brak aktualnego kalendarza blokuje nowe wejścia (domyślnie nie – działa wtedy filtr zmienności/spreadu).</summary>
        public bool RequireCalendar { get; set; }
        /// <summary>Po ilu godzinach kalendarz uznajemy za nieaktualny.</summary>
        public int MaxCalendarAgeHours { get; set; } = 36;
    }

    /// <summary>
    /// Filtr wiadomości makroekonomicznych: blokuje NOWE wejścia na instrumentach powiązanych z walutą,
    /// dla której w oknie [czas − przed, czas + po] publikowana jest ważna wiadomość (np. NFP, CPI, decyzja stóp).
    /// Otwarte pozycje są chronione przez SL po stronie brokera. Nieplanowane wydarzenia obsługują filtry chaosu/spreadu.
    /// </summary>
    public sealed class NewsCalendar
    {
        private List<EconomicEvent> _events = new();

        public DateTime LastUpdateUtc { get; private set; }
        public string Source { get; private set; } = "";
        public IReadOnlyList<EconomicEvent> Events => _events;

        public void SetEvents(IEnumerable<EconomicEvent> events, DateTime updatedUtc, string source)
        {
            _events = events.Where(e => e.TimeUtc > DateTime.MinValue && !string.IsNullOrWhiteSpace(e.Currency))
                .OrderBy(e => e.TimeUtc).ToList();
            LastUpdateUtc = updatedUtc;
            Source = source;
        }

        public bool IsFresh(DateTime utc, NewsSettings s) => LastUpdateUtc > DateTime.MinValue && utc - LastUpdateUtc <= TimeSpan.FromHours(s.MaxCalendarAgeHours);

        /// <summary>Waluty/regiony, na które reaguje instrument.</summary>
        public static HashSet<string> CurrenciesFor(InstrumentSpec spec)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string norm = InstrumentClassifier.Normalize(spec.Name);
            switch (spec.AssetClass)
            {
                case AssetClass.Forex:
                case AssetClass.Metal:
                    if (!string.IsNullOrEmpty(spec.BaseAsset)) set.Add(spec.BaseAsset);
                    if (!string.IsNullOrEmpty(spec.QuoteAsset)) set.Add(spec.QuoteAsset);
                    if (norm.Length >= 6) { set.Add(norm.Substring(0, 3)); set.Add(norm.Substring(3, 3)); }
                    if (spec.AssetClass == AssetClass.Metal) set.Add("USD");
                    break;
                case AssetClass.Index:
                    set.Add(IndexCurrency(norm, spec.QuoteAsset));
                    break;
                case AssetClass.Energy:
                case AssetClass.Commodity:
                    set.Add("USD");
                    if (!string.IsNullOrEmpty(spec.QuoteAsset)) set.Add(spec.QuoteAsset);
                    break;
                default:
                    if (!string.IsNullOrEmpty(spec.QuoteAsset)) set.Add(spec.QuoteAsset);
                    break;
            }
            set.RemoveWhere(string.IsNullOrWhiteSpace);
            return set;
        }

        private static string IndexCurrency(string norm, string quote)
        {
            if (norm.StartsWith("GER") || norm.StartsWith("DE") || norm.StartsWith("FRA") || norm.StartsWith("FR") || norm.StartsWith("EU") ||
                norm.StartsWith("ESTX") || norm.StartsWith("STOXX") || norm.StartsWith("ESP") || norm.StartsWith("SPA") || norm.StartsWith("IT") ||
                norm.StartsWith("NETH") || norm.StartsWith("NL")) return "EUR";
            if (norm.StartsWith("UK") || norm.StartsWith("FTSE")) return "GBP";
            if (norm.StartsWith("JP") || norm.StartsWith("N225")) return "JPY";
            if (norm.StartsWith("AUS") || norm.StartsWith("AU")) return "AUD";
            if (norm.StartsWith("HK") || norm.StartsWith("CHINA") || norm.StartsWith("CN")) return "CNY";
            if (norm.StartsWith("SWI") || norm.StartsWith("CH20")) return "CHF";
            if (norm.StartsWith("US") || norm.StartsWith("NAS") || norm.StartsWith("SPX") || norm.StartsWith("DJ") || norm.StartsWith("NDX")) return "USD";
            return string.IsNullOrEmpty(quote) ? "USD" : quote;
        }

        public bool IsBlackout(InstrumentSpec spec, DateTime utc, NewsSettings s, out EconomicEvent? evt)
        {
            evt = null;
            if (!s.Enabled || _events.Count == 0) return false;
            var ccys = CurrenciesFor(spec);
            var from = utc.AddMinutes(-s.MinutesAfter);
            var to = utc.AddMinutes(s.MinutesBefore);
            foreach (var e in _events)
            {
                if (e.TimeUtc < from) continue;
                if (e.TimeUtc > to) break;
                if (e.Impact < s.MinImpact) continue;
                if (ccys.Contains(e.Currency) || (e.Currency == "CNY" && ccys.Contains("CNH")))
                {
                    evt = e;
                    return true;
                }
            }
            return false;
        }

        public IEnumerable<EconomicEvent> Upcoming(DateTime utc, int count, NewsImpact min) =>
            _events.Where(e => e.TimeUtc >= utc.AddMinutes(-30) && e.Impact >= min).Take(count);

        /// <summary>
        /// Parser kalendarza w formacie JSON (tablica obiektów z polami: title, country, date, impact),
        /// zgodny z publicznym kanałem ff_calendar_thisweek.json. Odporny na brakujące/nieznane pola.
        /// </summary>
        public static List<EconomicEvent> ParseFfJson(string json)
        {
            var list = new List<EconomicEvent>();
            if (string.IsNullOrWhiteSpace(json)) return list;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                string title = Str(el, "title");
                string country = Str(el, "country").Trim().ToUpperInvariant();
                string date = Str(el, "date");
                string impact = Str(el, "impact");
                if (country.Length == 0 || date.Length == 0) continue;
                if (!DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)) continue;
                list.Add(new EconomicEvent
                {
                    Title = title,
                    Currency = country == "ALL" ? "ALL" : country,
                    TimeUtc = dto.UtcDateTime,
                    Impact = ParseImpact(impact),
                });
            }
            // Wydarzenia "ALL" dotyczą wszystkich walut – rozwijamy na główne waluty.
            var expanded = new List<EconomicEvent>();
            foreach (var e in list)
            {
                if (e.Currency != "ALL") { expanded.Add(e); continue; }
                foreach (var c in new[] { "USD", "EUR", "GBP", "JPY", "CHF", "AUD", "NZD", "CAD", "CNY" })
                    expanded.Add(new EconomicEvent { Title = e.Title, Currency = c, TimeUtc = e.TimeUtc, Impact = e.Impact });
            }
            return expanded;
        }

        public static NewsImpact ParseImpact(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return NewsImpact.None;
            s = s.Trim().ToLowerInvariant();
            if (s.StartsWith("high") || s == "3" || s == "red") return NewsImpact.High;
            if (s.StartsWith("med") || s == "2" || s == "orange") return NewsImpact.Medium;
            if (s.StartsWith("low") || s == "1" || s == "yellow") return NewsImpact.Low;
            return NewsImpact.None;
        }

        private static string Str(JsonElement el, string name)
        {
            foreach (var p in el.EnumerateObject())
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                    return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString();
            return "";
        }

        /// <summary>Serializacja do pamięci trwałej (kopia zapasowa kalendarza na wypadek braku sieci po restarcie).</summary>
        public string Serialize() => JsonSerializer.Serialize(new CalendarDto { Updated = LastUpdateUtc, Source = Source, Events = _events });

        public bool TryRestore(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                var dto = JsonSerializer.Deserialize<CalendarDto>(json!);
                if (dto?.Events == null) return false;
                SetEvents(dto.Events, dto.Updated, dto.Source + " (kopia)");
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private sealed class CalendarDto
        {
            public DateTime Updated { get; set; }
            public string Source { get; set; } = "";
            public List<EconomicEvent> Events { get; set; } = new();
        }
    }
}
