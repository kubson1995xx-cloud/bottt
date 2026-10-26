# Metodologia testów strategii APEX

> **ARCHIWALNA METODOLOGIA/WYNIKI v1.** Backtester v2 zmienił cashflow, FX, minima i niezależność WF; ten opis historycznych okresów nie waliduje v2. Okres OANDA był już używany, nie jest nietkniętym holdoutem. Aktualne CLI, kontrakt danych i ograniczenia: [BACKTESTER.md](BACKTESTER.md).

Ten dokument opisuje zasady testów **ustalone przed obejrzeniem wyników** danej rundy.
Historyczną implementacją był `tools/Apex.Backtester/Research/ResearchPipeline.cs`; obecna v2 ma semantykę opisaną w BACKTESTER.md.

## 1. Dane

| Źródło | Instrumenty | Okres | Rozdzielczość |
|---|---|---|---|
| OANDA (publiczne repozytorium `FutureSharks/financial-data`) | EURUSD, GBPUSD, AUDUSD, USDCAD, AUDJPY, EURJPY, XAUUSD, WTI (SpotCrude), NatGas, US500, NAS100, US2000, UK100, JPN225 (kwot. USD), AUS200, FRA40, NETH25, DE10YB, UK10YB, USB10Y, USB02Y, Corn, Wheat, Soybeans, Sugar | 2005-01 – 2020-05 | M1 (ceny MID) |
| histdata.com (to samo repozytorium) | GER40 (DAX), EUSTX50 | 2010-11 – 2018-12 | M1 |

- Przygotowanie: `tools/data/prepare_data.py` (M1 → M15 i H1, kontrola jakości: duplikaty, niepoprawne świece, rażąco błędne ticki). Raport jakości: `data/bars/quality.csv` (0 niepoprawnych świec, 0 odrzuconych ticków).
- H4 i D1 są agregowane z H1 z granicą dnia jak u brokerów cTrader (17:00 czasu nowojorskiego, czas serwera = NY + 7h).
- **Ograniczenia danych:** ceny MID (bez rzeczywistego spreadu), brak danych akcji i ETF, brak danych po 2020-05, brak historycznego kalendarza wiadomości. Survivorship bias nie dotyczy indeksów/FX/surowców w takim stopniu jak akcji (instrumenty nie znikają z rynku), ale wybór instrumentów do testu był ograniczony dostępnością danych.

## 2. Model kosztów (konserwatywny)

- Spread modelowany: ask = mid + spread/2, bid = mid − spread/2; ×3 w godzinach 21–23 UTC (rollover).
- Prowizja FX i złoto: 3,5 USD za lot za stronę (konto typu Razor). CFD indeksy/surowce/obligacje: koszt w spreadzie.
- Poślizg wejścia: 0,25 × spread + 0,01 × ATR(H1); poślizg wyjścia przez SL: 0,5 × spread + 0,02 × ATR(H1); luki cenowe realizowane po cenie otwarcia (gorszej).
- Finansowanie (swap): 2,5% rocznie od wartości nominalnej, **płacą obie strony** (bez korzyści z dodatniego carry), potrójny swap w środę (FX/metale) lub piątek (CFD).
- Wejście po cenie otwarcia świecy następującej po sygnale (brak look-ahead). SL/TP sprawdzane na ścieżce M15 wewnątrz świecy H1. Gdy w jednej świecy padają SL i TP – zakładamy SL.
- Scenariusze podwyższonych kosztów: ×1,5 (spread i prowizja ×1,5, poślizg ×2, swap ×1,5) i ×2 (×2, ×3, ×2).

## 3. Podział czasu

- **Okres badawczy:** 2005-01-01 – 2017-12-31 (wszystkie decyzje projektowe).
- **Okres odłożony (holdout):** 2018-01-01 – 2020-05-31 – tylko do końcowego sprawdzenia portfela.
- **Walk-forward:** 4 lata in-sample → 1 rok out-of-sample, przesuwane co rok; lata OOS 2009–2017 (9 okien).

## 4. Procedura dla każdej pary strategia × klasa aktywów

1. Każda kombinacja siatki parametrów testowana w izolacji (stałe ryzyko 0,1% na transakcję – wynik mierzony w R po kosztach).
2. W każdym oknie IS wybór parametrów maksymalizujących t-statystykę średniego R **wygładzoną po sąsiadach w siatce** (preferencja stabilnego "płaskowyżu" zamiast pojedynczego szczytu); minimum 30 transakcji IS.
3. Wynik OOS = sklejone transakcje z kolejnych lat OOS przy parametrach wybranych wyłącznie na danych wcześniejszych.
4. Parametry docelowe = najczęściej wybierane w walk-forward (remis → domyślne).

### Kryteria włączenia (wszystkie naraz)

| Kryterium | Próg |
|---|---|
| liczba transakcji OOS | ≥ 60 |
| średni wynik OOS | ≥ +0,05 R |
| t-statystyka OOS | ≥ 1,65 (jednostronnie ~95%) |
| zyskowne lata OOS | ≥ 55% |
| odporność na parametry: kombinacje siatki z dodatnim E[R] w całym okresie badawczym | ≥ 60% |
| E[R] przy kosztach ×1,5 (parametry docelowe) | > 0 |

Strategia, która nie spełnia kryteriów, **pozostaje w kodzie, ale jest wyłączona** w profilu domyślnym (bot nie otwiera nią pozycji).

## 5. Portfel, holdout, Monte Carlo

- Portfel z włączonymi strategiami i pełnym zarządzaniem ryzykiem (0,5%/transakcję, limit dzienny 1,5%, max DD 5%, max 3 pozycje, dźwignia efektywna ≤ 5, limity korelacji i klas) – okres badawczy oraz holdout.
- Analiza wyników wg przedziałów oceny okazji (czy wyższa ocena = lepszy wynik).
- Monte Carlo: 10 000 losowań ze zwracaniem z listy transakcji portfela → rozkład maksymalnego obsunięcia i wyniku.

## 6. Rundy badań (rejestr – wszystkie przetestowane hipotezy są raportowane)

### Runda 1 (zarejestrowana przed uruchomieniem, 2026-10-15)
Rodziny: Trend–korekta (H1/H4), Wybicie Donchian (H4/D1), Powrót do średniej (H1/H4), Impuls momentum (H1/H4), Kupno spadków na indeksach (D1, RSI(2)).
Klasy: Forex, Metale, Energia, Indeksy, Obligacje, Towary (kupno spadków – tylko indeksy). Razem 25 par strategia × klasa, 24–27 kombinacji parametrów na parę.

### Runda 2 (zarejestrowana po rundzie 1, PRZED jej uruchomieniem)
Uzasadnienie: w rundzie 1 straty malały monotonicznie wraz z interwałem (M15 → H1 → H4), a jedyną parą spełniającą kryteria była strategia dzienna. Literatura (Moskowitz, Ooi, Pedersen 2012 – time-series momentum; zasady "Turtle") wskazuje na trend following na danych dziennych.
Hipoteza: **Trend dzienny (DailyTrend)** – wybicie kanału Donchiana na D1, wyjście trailing ATR lub przebicie kanału wyjścia.
Siatka: kanał {20, 55, 100} × SL {2, 3} ATR × trailing {3, 5} ATR × filtr trendu D1 {brak, nie przeciw trendowi} = 24 kombinacje; wszystkie 6 klas aktywów.
Kryteria: identyczne jak w rundzie 1. Holdout użyty ponownie – raport zaznacza to jako drugie użycie.

### Uwaga o wielokrotnym testowaniu
Łącznie testowanych jest ~31 par strategia × klasa. Przy progu t ≥ 1,65 można oczekiwać ok. 1–2 fałszywie pozytywnych wyników wyłącznie przez przypadek, dlatego wynik holdout oraz test przy kosztach ×1,5 są traktowane jako obowiązkowe dodatkowe potwierdzenie, a pojedyncze "przejście" kryteriów nie jest dowodem przyszłej rentowności.
