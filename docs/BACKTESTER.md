# Backtester v2 — offline, bez aktywacji strategii

`backtest-v2.1-complete-account-evidence` zachowuje rachunek v2 i natural-outcome selection, naprawia utratę boundary money evidence w research i obsługuje zadeklarowane przerwy na granicach kalendarza. Research JSON/CSV v2 bez pełnych kont nie jest kompletnym raportem finansowym; nowy schemat celowo rozdziela natural stats od rachunku. **Dawne CSV/profil/raporty w `reports/` są historyczne i NIE dowodzą wyników v2.1.** Okres OANDA 2005–2020 był używany wcześniej: nie jest nietkniętym holdoutem. To narzędzie nie łączy się z brokerem, nie aktywuje strategii w produkcji i nie zastępuje odbioru cTrader DEMO/Windows.

## Rzeczywiste CLI

Wymagany .NET 8. `help` nie uruchamia obliczeń:

```sh
dotnet run --project tools/Apex.Backtester -c Release -- help
```

Przykład **interfejsu** do późniejszego przebiegu na uprzednio zweryfikowanych danych (nie deklaracja, że te dane/manifests istnieją):

```sh
dotnet run --project tools/Apex.Backtester -c Release -- smoke \
  --data /path/to/bars --manifest /path/to/manifest.json --out /path/to/new-results \
  --symbol EURUSD --warmup-from 2022-01-01 --from 2023-01-01 --to 2024-01-01 \
  --cost base --intrabar h1 --fx-max-age-hours 2
```

- Wszystkie polecenia `smoke`, `research`, `portfolio` wymagają `--data`, `--manifest`, `--out`, `--warmup-from`, `--from`, `--to`. Daty UTC `YYYY-MM-DD`; zakres oceny `[from,to)`, warmup musi poprzedzać ocenę. Limit długości danych: 50 lat.
- `smoke`: rzeczywisty Core/StrategyBook/risk/SimWorld, domyślna rodzina TrendPullback; **zero transakcji jest poprawnym wynikiem**, nie dowodem przewagi. `--symbols EURUSD,GBPUSD` zamiast `--symbol`; domyślnie EURUSD. Nie zmienia aktywnego profilu aplikacji.
- `research`: sześć istniejących rodzin w klasach wybranych symboli. `--only TrendPullback` lub `--only Forex` (lista po przecinku, logiczne OR). Wymaga granic 1 stycznia i co najmniej jednego pełnego roku OOS po IS. `--is-years 4` (1–20), `--max-combos 100` (1–100, domyślna kombinacja pierwsza), `--max-runs 1000` (1–100000), `--parallel 1` (1–64). Budżet zlicza izolowane uruchomienia symbolu; wyczerpanie oznacza błąd, a nie udane częściowe badanie. Częściowa siatka pozostaje częściową siatką, nie pełnym badaniem stabilności. `--tag NAME` ograniczone do liter ASCII/cyfr/`-`/`_`.
- `portfolio`: jawne `--profiles a.json,b.json`; tylko istniejące włączone wpisy z plików, bez modyfikacji tych plików. Jeden wskazany zakres, dwa historycznie istniejące warianty scoringu 0/50; brak automatycznego holdoutu. Produkcyjne parametry ryzyka zachowane. Hash i treść wczytanych profili w raporcie.
- `--cost base|elevated|double`: istniejące mnożniki kosztów. Dodatkowe stress-testy research mnożą wybrany scenariusz (nie zastępują go tańszym).
- Nieznane, powtórzone i bezwartościowe flagi są błędem. Brak danych/niepoprawne dane/FX → exit2 i `run.json` z `success:false`, nie pominięty profit. Używać nowego katalogu wyjścia; `run.json` jest autorytatywnym statusem danego wywołania, stare CSV w ponownie użytym katalogu nie stają się jego wynikiem.
- `run.json`: wersja semantyki, SHA-256 wykonywanych DLL backtestera/Core, rzeczywiste argumenty i konfiguracja silnika/kosztów/instrumentów, źródło/hashe danych, basis/konwersja, zakresy, warmup/liczba świec/luki, wyniki lub pełny błąd. `smoke` zapisuje również `trades.csv`; research zapisuje CSV WF, tabelę, log i `research_candidates[_tag].json`. **Kandydaci mają `Enabled=false`; nie jest tworzony nowy `profile_validated.json`.**

## Kontrakt danych (jawny manifest, nie domysł)

CSV: dokładny nagłówek `time,open,high,low,close,volume`; sześć kolumn; UTC `yyyy-MM-dd HH:mm`. OHLC dodatnie i skończone, high/low spójne, volume skończony i nieujemny. Czas ściśle rosnący, bez duplikatów, wyrównany do interwału. Błędne wiersze nie są pomijane. Czytnik sprawdza cały plik, następnie wybiera `[warmup-from,to)`.

Manifest JSON, wymagane pola:

```json
{
  "source": "rzeczywiste źródło, wersja/download/provenance; nie zmyślony feed brokera",
  "timezone": "UTC",
  "files": [
    {
      "file": "EURUSD_H1.csv",
      "sha256": "64_HEX_DIGITS_REPLACE_WITH_ACTUAL_FILE_SHA256",
      "quoteBasis": "BID",
      "sessionGaps": [
        { "from": "2023-01-06T22:00:00Z", "to": "2023-01-08T22:00:00Z", "reason": "opis udokumentowanej przerwy sesyjnej źródła" }
      ]
    }
  ]
}
```

Powyżej przykład schematu, **nie prawidłowy gotowy hash ani potwierdzenie godzin sesji**. Każda konwersja FX wymaga również odpowiedniego pliku/manifestu: EURUSD, GBPUSD, AUDUSD, USDCAD lub EURUSD+EURJPY dla JPY. Nazwy muszą odpowiadać katalogowi. Hash i parser używają tego samego snapshotu bajtów. Dane rynkowe i manifests pozyskania pozostają poza Git/pakietem do ustalenia praw redystrybucji.

- Każda przerwa pomiędzy świecami w wybranym zakresie musi dokładnie pasować do jawnej przerwy `[previous close,next open)` z powodem. Także leading/trailing closure może przecinać `warmup-from` lub roczny cutoff: po przycięciu do żądanego zakresu deklaracja musi dokładnie pokryć brakujący odcinek, a jej pełne granice muszą odpowiadać sąsiednim świecom obecnym w pliku. Deklaracja nie może obejmować świec ani maskować dodatkowych braków. Inaczej jest **missing interval**, nie automatycznie „sesja”. Deklaracja użytkownika nie stanowi niezależnej weryfikacji kalendarza; źródło/reason musi ją uzasadniać. Nie dopisujemy świec. Evidence zachowuje pełne deklaracje i `requestedFrom/To`, oddzielnie rzeczywiste `first/endExclusive`; pusty zakres bez świec nadal odmawia.
- `quoteBasis` wyłącznie `MID`, `BID`, `ASK`. Symulator konsumuje MID. BID → **modelowany** MID przez dodanie połowy bazowego spreadu katalogu; ASK → odjęcie. Rollover x3 o 21/22 UTC. To nie zaobserwowany MID ani historyczny spread Pepperstone. Konwersja jest zapisana w raporcie. Scenariusze poszerzają spread wokół tak ustalonego MID.
- `--intrabar h1` (domyślnie): jawnie H1-only, pliki M15 ignorowane. `m15`: wymagane wszystkie cztery prawidłowe M15 w każdej H1. Sparse M15 nie może maskować SL z brakującego kwadransa; nie jest nazywany ciągłym i nie ma cichego mieszanego fallbacku.
- Warmup: tylko wskaźniki ze świec zamkniętych; żadnych zleceń, salda, cooldownów, drawdown/risk state z warmup. Liczba H1 warmup jest jawna. Użytkownik musi dobrać dostateczny warmup także dla D1; krótki warmup może dać zero sygnałów, nie wolno interpretować tego jako pełnego testu strategii.

## Rachunek USD, FX i koszty

- Jedna konwencja: **entry commission jest natychmiast odejmowana od Balance**. Otwarta pozycja `NetProfit` = gross po aktualnej konwersji + pozostały swap − rezerwa pojedynczej operacji wyjścia. Historycznie opłacona prowizja wejścia nie jest ponownie odejmowana od open net. `Equity = Balance + Σ(open NetProfit)`; mark zawiera modelowany poślizg likwidacji.
- Partial/full close: Balance otrzymuje tylko gross − nową prowizję wyjścia + przypisany swap. Prowizja wejścia jest proporcjonalnie przypisana do statystyki zamkniętej części, nie płacona ponownie. Finalny rekord pozycji dopiero po pełnym close zawiera sumę gross, wszystkich prowizji i swapów, także partial. Suma finalnych net rekoncyliuje start/end po likwidacji.
- Założenia (NIE potwierdzone opłaty brokera): FX/metale $3.50/lot/strona, minimum **$0.50 za wykonaną operację**; spread-only CFD jawne minimum0. Scenariusz mnoży zarówno stawkę, jak i floor. Ten sam floor trafia do `InstrumentSpec.CommissionOneSide`, rzeczywistego `PositionSizer`, entry, każdego partial i final close. Sizing pierwotnego SL rezerwuje wejście+jedno wyjście; dodatkowe częściowe redukcje mogą zapłacić dodatkowe minima. Nie udajemy, że pierwotny roundtrip finansuje dowolną liczbę partiali.
- FX do USD: wyłącznie dodatnie skończone ostatnie **zamknięcie H1 znane w danej chwili**; brak, przyszłe, przeterminowane, niepoprawne lub niefinitywne po konwersji → jawny błąd. Domyślne max-age2h od zamknięcia, `--fx-max-age-hours` 1–168 zapisane w raporcie (większy limit jest dodatkowym założeniem, nie naprawą danych). Tożsamość USD→USD dokładnie1 bez zewnętrznych kursów. Żadnego fallback1. Kontrola także dla instrumentów bez transakcji.
- `BacktestResult` / smoke raportuje `effectiveStartUtc` (pierwszy open oceny), `lastExecutableOpenUtc` oraz `effectiveEndUtc` (ostatnie przetworzone zamknięcie / czas likwidacji). Logiczne `[from,to)` i roczne IS/OOS pozostają bez zmian. Jeśli cutoff wypada w zadeklarowanej przerwie, izolowany run likwiduje na ostatnim wcześniejszym close, ze spread/slippage/FX znanymi wtedy, nie w dniu późniejszego cutoff ani po przyszłym open; nie symuluje dodatkowego finansowania po effective end. Dla wielosymbolowego runnera czasy opisują wspólną oś godzin, nie obietnicę równoczesnej sesji wszystkich symboli.
- SL/TP w H1/M15: przy obu trafieniach najpierw SL, luki konserwatywnie po open. Intrabar timestamp jest końcem świecy, nie zmyślonym tickiem. Ostatnia świeca oceny jest przetwarzana; pozostałe pozycje są likwidowane po ostatniej cenie zamkniętej nie później niż cutoff + spread/slippage/fee, bez używania następnego OOS open. Equity DD jest próbkowany na H1 i końcu, nie tickowy/intrabar worst-case DD.

## Walk-forward

Każdy fold uruchamia nowe światy/silniki/magazyny: rolling `is-years` IS → rok OOS. Selekcja korzysta wyłącznie z entry w IS i **exit ściśle przed IS cutoff**, z prawidłowym R i n≥30. Wymuszone likwidacje graniczne nie są wynikami treningowymi ani naturalnymi OOS trade outcomes; pozostają jawnie rozliczone w cash/equity. OOS startuje flat, wybrane parametry są zamrożone, brak odczytu przyszłych wyników przy wyborze. Historyczne ceny sprzed folda mogą rozgrzać wskaźniki, ale nie przenoszą stanu konta/strategii. Kolejny rolling IS może używać już historycznie znanych poprzednich OOS — to nie jest nietknięty holdout.

Brak własnej próby nie uprawnia sąsiadów do wyboru kandydata. Brakujące wyniki sąsiadów mają score gorszy od najgorszego obserwowanego (także ujemnego), nie zero podnoszące stratną ocenę. Brak eligible IS → brak wyboru/OOS, nie fikcyjnie zweryfikowany default. Retrospektywna pełna siatka, stress i częstość wyborów są opisowe; nie sterują poprzednimi wyborami. Parametry majority-choice i research criteria nie są nową walidacją produkcyjną.

### Pełne konta izolowane vs natural outcomes (v2.1)

- `RunIsolated` zwraca `IsolatedRun`: pełny `BacktestResult Account` i odrębne `NaturalTrades`. `RunComboClass` przekazuje do IS selector wyłącznie `NaturalTrades`; żaden boundary exit, także wcześniejszy od logicznego cutoff przez holiday, nie wraca do selekcji. Parametry/ryzyko/grid bez zmian.
- Każdy ukończony run natychmiast zapisuje `isolated[_tag]/run-NNNNNN.json`: runId, symbol, combo key/label i pełny entry, strategy/class, purpose (`training-is`, `selected-oos`, `default-oos`, retrospektywne grid/stress), foldYear, logiczne from/to i wszystkie mnożniki kosztów. `run.json.result.isolatedEvidence` indeksuje te pliki. Nieudany/budget-exceeded cały research nadal nie jest sukcesem mimo istniejących ukończonych kont.
- `account` zawiera **wszystkie** końcowe trade records (agregujące partials), boundaryPositionIds, gross/commission/swap/net, start/end equity, H1/end-sampled max equity DD, effective start/last executable open/end i daily/end equity samples. `cashflows` zawiera entry fee oraz każdą partial/final reduction: positionId/time/kind/units/remainingUnits, rzeczywisty modelowany fill, znany wtedy FX, gross, signed commission (ujemny koszt), zrealizowany swap, delta USD i balanceAfter. Entry fee nie jest drugi raz rozdzielana w cash deltas; final trade attribution nie jest dodatkowym cashflow. Spread/slippage są w cenach fill/gross, nie dodatkowym naliczeniem obok nich. Finansowanie naliczone do effective end jest rozliczone przez reductions, nie jako drugi debit.
- `naturalOutcomes` w pojedynczym koncie i `naturalOos` / `naturalOosByYear` w podsumowaniu są wyraźnie **natural-only** (w tym PF/net/costs/closed-trade DD). `selectedOosAccounts` osobno sumuje kompletne niezależnie finansowane konta wybranych foldów, również straty i koszty graniczne. Sumy start/end nie są jednym reinwestowanym portfelem; nie agregujemy sampled DD do fikcyjnego portfolio DD. Jest `sampledDrawdownByRun` i runIds.
- Rzeczywisty exporter `ExportWalkForwardEvidence` zapisuje `trades/wf_natural_STRATEGY_CLASS.csv` oraz **kompletny** `wf_account_STRATEGY_CLASS.csv` z `run_id;boundary_liquidation` i każdym trade. runId łączy z dokładnym JSON (symbol/combo/fold/range/cost/partial ledger). CSV money jest zaokrąglone do centów; pełna precyzja i rekoncyliacja `start + sum(cash delta) = start + sum(trade net) = end` są w JSON. Stara nazwa `wf_*` nie jest nowym kompletnym raportem. Nowy pusty output directory nadal wymagany dla audytu.

## Bramki i ograniczenia dowodów

`tests/Apex.Backtester.Tests` odwołuje się do rzeczywistego projektu narzędzia i przez niego Core; testuje realne obiekty/fille oraz rzeczywisty runner/strategie, nie stare CSV lub wymyślone odpowiedzi brokera. Jest w DEMO solution i twardej bramce `tools/windows/Zbuduj-APEX.ps1` / Windows CI. Pakiet wymaga `backtester.trx`, `backtesterTestsRun=true` i rzeczywistych liczników `testCounts.bot/platform/backtester`; verifier odrzuca brak/zero/niezgodne liczniki/błędy. Testy kontrolowanych fixtures integralności nie są dowodem prawdziwego EXE ani transakcji.

Lokalny Linux build/test lub PowerShell7 nie zastępuje Windows PowerShell5.1 CI, Windows EXE smoke, instalacji Windows11 ani osobno autoryzowanego testu brokerskiego DEMO. Strategiczne badania na autentycznych danych, prawa/dane/kalendarze/broker costs i pozostały szeroki odbiór należą do kolejnych zadań; Task3 nie deklaruje rentowności.
