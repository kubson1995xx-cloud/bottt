# NAVAL offline — sygnały D1 i lokalny replay

**To nie jest bot ani backtest finansowy.** Moduł nie łączy się z brokerem, OKX ani siecią, nie wysyła zleceń i nie wylicza pozycji, filli, P&L, kosztów ani rentowności. Nie odblokowuje Crypto/cTrader, REAL/LIVE, START ani certyfikacji DEMO. Dotychczasowy panel, profile, risk i guards pozostają bez zmian.

## Uruchomienie

Wymagany SDK .NET 8; Core pozostaje biblioteką `net6.0`. Z katalogu repozytorium:

```bash
dotnet build tools/Apex.Naval.Replay -c Release
dotnet test tests/Apex.Naval.Tests -c Release --logger trx
dotnet run --project tools/Apex.Naval.Replay -c Release --no-build -- \
  --input tests/Apex.Naval.Tests/fixtures/synthetic/naval-replay-v1.json \
  --output ./naval-synthetic-result.json
```

Na Windows można wpisać ostatnią komendę w jednym wierszu, bez `\`. Plik wyjściowy **nie może już istnieć**; drugi run należy skierować do innej nazwy. Kolejność par `--input`/`--output` jest dowolna, każda występuje dokładnie raz. Nie ma flag konta, klucza, brokera, execution, live ani pobierania danych.

Exit code `0` oznacza poprawnie zapisany raport decyzji offline — także uczciwe `Wait`. Exit code `2` oznacza niepoprawny argument/schema/config/dane użytego prefiksu lub problem pliku. Szczegóły wejścia i wyjątki systemowe nie trafiają do stderr. Błędne żądanie nie publikuje częściowego raportu. Cały wynik jest serializowany przed zapisem do nowego pliku tymczasowego w katalogu docelowym, flush i rename bez nadpisania. Istniejący plik wyjściowy pozostaje nietknięty.

Akceptowane są wyłącznie ścieżki lokalne; URI (również `file://`), UNC i aliasy symlink/reparse są odrzucane. Katalog docelowy musi istnieć. Twardy link do inputu jest istniejącym outputem i także nie zostanie nadpisany. To narzędzie do zaufanego lokalnego katalogu, nie sandbox przeciw równoczesnej wrogiej podmianie katalogów ani dowód atomowości/durability dowolnego filesystemu. Lokalnie zamontowany filesystem jest odpowiedzialnością operatora.

## Fixture: wyłącznie synthetic

`tests/Apex.Naval.Tests/fixtures/synthetic/naval-replay-v1.json` zawiera **wygenerowane, kontrolowane OHLCV**, nie historię giełdową. Nie używać jej jako dowodu strategii inwestycyjnej. Cztery niezależne żądania dają:

| Flota | Instrument | Jawny long | Oczekiwana decyzja |
|---|---|---:|---|
| Alpha (ALFA) | ETH-USD | false | EntrySignal |
| Bravo (BRAVO) | SOL-USD | false | EntrySignal |
| Alpha | ETH-USD | true | ExitSignal, bez BTC |
| Bravo | ADA-USD | false | Wait / MissingBtcData |

`EntrySignal` **nie zmienia** następnego żądania w holding. Każde żądanie zawiera niezależne `hasLong`; nie ma automatycznej symulacji portfela.

## Schemat wejścia `naval-replay-v1`

JSON UTF-8, nazwy pól case-sensitive, **wszystkie wymienione pola wymagane**. Nieznane lub powtórzone pola na dowolnym poziomie są błędem. Liczby muszą być liczbami JSON mieszczącymi się w .NET `decimal` (nie stringami). Brak komentarzy/trailing comma. Daty ISO 8601 z jawnym `Z` lub `+00:00`, nigdy domniemana lokalna strefa. Enum tylko dokładne stringi `Alpha`/`Bravo`.

Poniższy kompletny minimalny dokument jest poprawny strukturalnie, ale puste `bars` oznacza `Wait / MissingInstrumentData`, nie gotowy sygnał:

```json
{
  "schemaVersion": "naval-replay-v1",
  "datasetLabel": "SYNTHETIC EXAMPLE — NOT MARKET HISTORY",
  "requests": [
    {
      "fleet": "Alpha",
      "instrument": {
        "instrument": "ETH-USD",
        "baseAsset": "ETH",
        "quoteAsset": "USD",
        "source": "SYNTHETIC",
        "priceBasis": "MID",
        "bars": []
      },
      "btc": null,
      "cutoffUtc": "2025-07-20T00:00:00Z",
      "hasLong": false,
      "settings": { "entryDays": 40, "exitDays": 30, "btcSmaDays": 200 }
    }
  ]
}
```

Element tablicy `bars` ma dokładnie te pola:

```json
{
  "openUtc": "2025-07-19T00:00:00Z",
  "open": 100,
  "high": 130,
  "low": 80,
  "close": 120,
  "volume": 10,
  "confirmed": true
}
```

| Poziom/pole | Znaczenie |
|---|---|
| root `schemaVersion` | dokładnie `naval-replay-v1` |
| root `datasetLabel` | jawna etykieta pochodzenia; niepusty tekst max 256 znaków bez control chars; przenoszony do outputu |
| root `requests` | 1–1000 niezależnych żądań w kolejności wyjścia |
| request `fleet` | `Alpha` lub `Bravo`; profil jest manifestem badawczym, nie produkcyjnym admission |
| request `instrument` | obiekt ocenianej serii opisany poniżej |
| request `btc` | taki sam obiekt serii benchmarku BTC albo jawne `null` |
| request `cutoffUtc` | jawny as-of; może być intraday, offset zero |
| request `hasLong` | boolean, informacja dostarczona przez operatora, nie stan symulatora |
| request `settings` | obiekt z trzema obowiązkowymi integer lookbacks, każdy 1–10000; default reguły 40/30/200 |
| series `instrument` | identyfikator lokalnej serii, np. ETH-USD; nie potwierdza istnienia pary na giełdzie |
| series `baseAsset`, `quoteAsset` | jawne waluty; BTC musi mieć base BTC i taki sam quote jak instrument |
| series `source`, `priceBasis` | jawne provenance i semantyka np. MID/BID/ASK; nie są certyfikatem jakości ani konwersją walut |
| series `bars` | tablica 0–100000 świec; dane nie są sortowane ani naprawiane |
| bar `openUtc` | początek doby UTC 00:00; świeca zamyka się po jednej dobie |
| bar `open/high/low/close` | dodatnie decimal; low ≤ open/close ≤ high; high ≥ low |
| bar `volume` | decimal ≥ 0, bez domyślnego znaczenia quote turnover/liquidity |
| bar `confirmed` | wymagany boolean; użyta świeca musi mieć true |

Limit całego snapshotu: 32 MiB; max depth JSON 32. CLI weryfikuje strukturę wszystkich świec (również przyszłych), natomiast evaluator używa i waliduje wartości OHLCV/confirmed tylko dla zamkniętych świec as-of. Przyszłe wartości mogą się zmienić bez wpływu na decyzję/ID; ich niepoprawny typ JSON nadal jest błędem schematu. CLI wymaga UTC także dla znaczników czasu przyszłych świec. Puste/nieobecne historie i stale dane dają jawne Wait; niepoprawny użyty OHLCV/gap/order/unconfirmed/config jest błędem CLI (Core zwraca Wait z `Invalid...`).

## Reguła i granice czasu

Wersja `naval-d1-v1`:

- Entry: `Close[t] > max(High[t-40..t-1])`; równość blokuje. Signal high nie należy do kanału.
- Exit posiadanego long: `Close[t] < min(Low[t-30..t-1])`; równość = Hold. Signal low nie należy do kanału. Brak short, pyramiding ani re-entry posiadanego long.
- Entry-only filtr: BTC `Close[t] > SMA(Close[t-199..t])` z tej samej doby/quote. Brak/alignment/199 dni/equality odmawia wejścia. SMA jest sumowana przed pojedynczym dzieleniem, nie online mean zależnym od kolejności zaokrągleń. Przepełnienie agregatu decimal daje `InvalidBtcData:NumericRange`, nigdy sygnał z obniżoną precyzją.
- Prawidłowy exit i Hold nie wymagają BTC; uszkodzone BTC nie blokuje wyjścia. Luka w danych instrumentu uniemożliwia wiarygodny kanał — Wait, nie zmyślony exit. Ochronne SL/TP należą do przyszłej ścieżki execution, nie tego modułu.
- Dopuszczone są tylko świece zamknięte `openUtc + 1 dzień <= cutoffUtc`. Użyty prefiks musi być unikalny, uporządkowany, ciągły i potwierdzony, bez naprawy luk. Ostatnia świeca musi zamknąć się o północy daty UTC cutoffu. W następnym dniu bez nowej świecy dane są stale.
- `earliestExecutionUtc` pojawia się tylko dla EntrySignal/ExitSignal i wynosi zamknięcie sygnału + **1 tick .NET (100 ns)**. To ścisła dolna granica „po zamknięciu”, **nie znany tick, dostępna cena, next open ani fill**. Przy późniejszym cutoffie nie obiecuje realizacji wstecz. Faktyczna przyszła realizacja wymaga osobnego adaptera, aktualnej ceny, risk/SL/TP i approval.

Manifesty: ALFA BTC/ETH; BRAVO BTC/ETH/SOL/XRP/DOGE/ADA/LTC/LINK/BCH/AVAX. Oba domyślnie max 2 pozycje; ATR14, SL2×ATR i TP3R to wyłącznie metadane badawcze. Ewaluator nie liczy ATR, sizingu, rankingu, płynności, rezerwacji ani centralnego limitu ekspozycji i nie przyznaje admission.

## Wynik i tożsamość

Root output ma dokładnie: `schemaVersion` = `naval-replay-result-v1`, `mode` = `OFFLINE`, `execution` = `NO_EXECUTION`, `datasetLabel`, `inputSha256`, `rulesVersion` = `naval-d1-v1`, `decisions`.

Każda decyzja: `fleet`, `instrument`, `action` (`Wait/Hold/EntrySignal/ExitSignal`), `reason`, `cutoffUtc`, `earliestExecutionUtc` (timestamp lub null), `entryHigh`, `exitLow`, `btcSma` (decimal lub null), `configIdentity`, `asOfIdentity`, `stableId`. Poziomy są odniesieniami sygnału, **nie executed prices**. Null oznacza nieobliczone/niedostępne, nie zero.

- `inputSha256`: SHA256 dokładnie jednego snapshotu bajtów inputu; uwzględnia whitespace i przyszłe świece.
- `configIdentity`: SHA256 tokenów wersji i lookbacks (Core domyślnie 40/30/200, CLI wymaga ich jawnie).
- `asOfIdentity`: SHA256 wersji, floty, cutoffu, hasLong i hashy obu metadanych/serii wyłącznie as-of. Obejmuje również starsze dane użytego prefiksu, poza kanałami; nie zawiera przyszłych OHLCV ani czasu systemowego. Zmiana BTC może zmienić identity exitu, ale nie jego poprawne działanie.
- `stableId`: `naval-d1-v1:<fleet>:<URI-escaped instrument>:<SHA256>`, digest wiąże konfigurację/as-of/action/reason. To tożsamość decyzji badawczej, nie broker order ID ani certyfikat deduplikacji wykonania.

Hashe lowercase hex, UTF-8, length-prefixed invariant tokens; decimal kanonizowany `G29`, UTC timestamp `O`. Powtórzenie tego samego dokumentu w różnych nazwach outputu daje identyczne bajty (brak zegara/global state). Zmiana przyszłego OHLC zmienia input hash, lecz nie decyzję ani jej as-of ID.

## Granica odbioru

Testy xUnit wykonują prawdziwy evaluator/serializer oraz realny lokalny filesystem. Fixture CLI to smoke ścieżki `Program.Main → ReplayApplication.Run → NavalD1Evaluator`, nie rzeczywisty rynek. Lokalny PASS nie zastępuje istniejących affected gates, solution/SDK build, Windows, niezależnego review ani zatwierdzonej realnej transakcji DEMO. Następne etapy (public data, spot costs/FX/OOS, OKX recovery/risk, UI, broker DEMO) pozostają osobno nieodebrane.
