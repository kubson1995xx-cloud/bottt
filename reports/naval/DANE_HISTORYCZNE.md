# Naval — rzeczywiste dane historyczne OKX EEA, D1 UTC

## Wynik i granice

**Dostępne 10/10 wymaganych aktywów, wspólny quote `USDT`, 24 389 rzeczywistych świec.** Dataset: `/home/ubuntu/projects/apex-data/naval-okx/dataset.json`. Źródło: wyłącznie publiczny REST `https://eea.okx.com`, `source=OKX_EEA_PUBLIC`, `priceBasis=TRADE`, `bar=1Dutc`, `timeframe=UTC1D`, wyłącznie `confirm="1"`. Żądane okno: `[2020-01-01T00:00:00Z, 2026-10-29T00:00:00Z)`. Ostatnie otwarcie w CSV: `2026-10-28T00:00:00Z`; nie pobrano niezamkniętej doby 29 października do zbioru.

**To publiczne historyczne ceny transakcyjne do researchu, nie dane DEMO, nie BID/ASK/MID, nie dowód dostępności tradingu USDT w Polsce/EEA ani uprawnień konta.** Nie używano keys, auth, prywatnego katalogu konta, rachunku, zleceń, depozytów, transferów ani zmiany regionu/hosta. Nie uruchomiono backtestu, ewaluatora sygnałów ani analizy performance. Nie zmieniono zaakceptowanych Task1–2, produkcyjnego START, panelu, risk gates ani globalnej konfiguracji.

`status=available` oznacza istniejące, zweryfikowane obserwacje, **nie pełne pokrycie całego żądanego okna od 2020**. SOL, LTC i AVAX mają braki na początku tego okna. Wszystkie 10 mają kompletne pokrycie prerejestrowanego okresu `2022-01-01`–`2026-10-29 exclusive` oraz poprzednich 200 dób: osobno **1762/1762 + 200/200** na instrument. To sprawdzenie kalendarza danych, nie badanie wyniku strategii.

## 1. Dokumentacja i mała prawdziwa odpowiedź przed bulk download

Odkrycie oficjalnych adresów przez web search; odczyt dokumentacji przez:

```text
/home/ubuntu/.local/bin/crawl4ai-read "https://my.okx.com/docs-v5/en/"
```

Pełny odczyt zachowano poza Git: `docs/eea-api-crawl.txt` w katalogu datasetu; SHA256 `e13a741caf003cd48150d1a7ba8ce9e467f8dc7b2fe04db2879af591209f6da8`.

Sprawdzone sekcje oficjalnego reference [1]:

- `Production Trading Services` i `Demo Trading Services`: regionalny REST `https://eea.okx.com`; wspólny host nie stanowi authority DEMO.
- `Public Data / Get instruments`: `GET /api/v5/public/instruments?instType=SPOT` bez auth; katalog publiczny nie jest dostępnością instrumentów konkretnego konta.
- `Market Data / GET / Candlesticks history`: `GET /api/v5/market/history-candles`, limit do 300; `after` zwraca rekordy **wcześniejsze** niż wskazane `ts`; limit 20 requests / 2 seconds / IP; „recent years” bez gwarantowanego początku.
- `1D` oznacza UTC+8; `1Dutc` oznacza UTC+0. `ts` to opening Unix milliseconds.
- Dokładny układ źródła: `[ts,o,h,l,c,vol,volCcy,volCcyQuote,confirm]`. `confirm=0` nieukończona, `confirm=1` ukończona. Dla SPOT `vol` jest ilością base asset.

Przed implementacją/bulk wykonano zwykłe HTTP GET do dwóch oficjalnych endpointów, a nie ekstrakcję cen ze strony:

| Próbka | Status / wynik | fetchUTC | SHA256 raw |
|---|---|---|---|
| `https://eea.okx.com/api/v5/public/instruments?instType=SPOT` | HTTP200, code0, 1213 instrumentów | `2026-10-29T21:52:33.966810Z` | `d1b5207473902ad1602c91d82db69defa08e7532aa98dcd433921c43a1b86130` |
| `https://eea.okx.com/api/v5/market/history-candles?instId=BTC-USDT&bar=1Dutc&after=1793232000000&limit=3` | HTTP200, code0, 3 rekordy, każdy 9 pól i confirm1 | `2026-10-29T21:52:34.259867Z` | `f6a3d1f64c216404ca372663998223732519d325cc3852ecbd882737fb6937c7` |

Raw i provenance próbek: `sample/{instruments,history}.raw.json` oraz `sample/{instruments,history}.provenance.json`. Niezależny verifier później porównał wszystkie 3 próbki BTC z pobranym CSV, pole po polu, bez zmiany stringów decimal.

Publiczny katalog zawiera wszystkie dokładnie wymagane pary `BTC/ETH/SOL/XRP/DOGE/ADA/LTC/LINK/BCH/AVAX-USDT`, każda `instType=SPOT`, `baseCcy` i `quoteCcy` zgodne z instId. Wybrano jawnie **USDT wyłącznie do wspólnej jednostki researchu**. Nie przeliczano USDT na USD i nie założono kursu 1. Nie wnioskowano o regionalnej dostępności tradingu z pola `state=live`.

## 2. Zakres, duplikaty i rzeczywiste braki

Daty poniżej są datami **otwarcia UTC**. Koniec wszystkich serii: `2026-10-28T00:00:00Z`.

| Base / instrument | Status | Początek UTC | Liczba świec | Brakujące doby żądanego okna | Luki wewnętrzne | Duplikaty |
|---|---|---|---:|---:|---:|---:|
| BTC / BTC-USDT | available | 2020-01-01 | 2493 | 0 | 0 | 0 |
| ETH / ETH-USDT | available | 2020-01-01 | 2493 | 0 | 0 | 0 |
| SOL / SOL-USDT | available | 2020-10-01 | 2219 | 274 | 0 | 0 |
| XRP / XRP-USDT | available | 2020-01-01 | 2493 | 0 | 0 | 0 |
| DOGE / DOGE-USDT | available | 2020-01-01 | 2493 | 0 | 0 | 0 |
| ADA / ADA-USDT | available | 2020-01-01 | 2493 | 0 | 0 | 0 |
| LTC / LTC-USDT | available | 2020-01-02 | 2492 | 1 | 0 | 0 |
| LINK / LINK-USDT | available | 2020-01-01 | 2493 | 0 | 0 | 0 |
| BCH / BCH-USDT | available | 2020-01-01 | 2493 | 0 | 0 | 0 |
| AVAX / AVAX-USDT | available | 2020-09-23 | 2227 | 266 | 0 | 0 |

Dokładne brakujące prefiksy:

- SOL: `2020-01-01`–`2020-09-30` włącznie, 274 dni.
- LTC: `2020-01-01`, 1 dzień.
- AVAX: `2020-01-01`–`2020-09-22` włącznie, 266 dni.

Dla tych trzech paginacja doszła do pustej odpowiedzi (`sourceExhausted`). Pozostałe serie sięgnęły żądanego początku (`reachedRequestedStart`). Braków nie nazwano automatycznie „przed listingiem”: bieżący `listTime` nie dowodzi początku całej udostępnionej historii. Nie dopisywano świec, carry-forward ani interpolacji. **541 brakujących obserwacji instrument-doba łącznie**, wyłącznie na początku; **0 wewnętrznych, 0 końcowych luk, 0 duplikatów w żądanym oknie**. Powtórzenie próbek czy reuse cache nie dodaje obserwacji do tych liczb.

### Pokrycie per instrument / rok

Każda komórka: rzeczywiste unikalne świece / doby żądanego okna w danym roku. Rok 2026 kończy się 28 października, nie 31 grudnia. Dla **każdej z 70 par instrument/rok duplikaty=0**; jawne zera i procenty znajdują się w `coverage.years` manifestu i metadata.

| Base | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 do 28.10 |
|---|---:|---:|---:|---:|---:|---:|---:|
| BTC | 366/366 | 365/365 | 365/365 | 365/365 | 366/366 | 365/365 | 301/301 |
| ETH | 366/366 | 365/365 | 365/365 | 365/365 | 366/366 | 365/365 | 301/301 |
| SOL | 92/366 | 365/365 | 365/365 | 365/365 | 366/366 | 365/365 | 301/301 |
| XRP | 366/366 | 365/365 | 365/365 | 365/365 | 366/366 | 365/365 | 301/301 |
| DOGE | 366/366 | 365/365 | 365/365 | 365/365 | 366/366 | 365/365 | 301/301 |
| ADA | 366/366 | 365/365 | 365/365 | 365/365 | 366/366 | 365/365 | 301/301 |
| LTC | 365/366 | 365/365 | 365/365 | 365/365 | 366/366 | 365/365 | 301/301 |
| LINK | 366/366 | 365/365 | 365/365 | 365/365 | 366/366 | 365/365 | 301/301 |
| BCH | 366/366 | 365/365 | 365/365 | 365/365 | 366/366 | 365/365 | 301/301 |
| AVAX | 100/366 | 365/365 | 365/365 | 365/365 | 366/366 | 365/365 | 301/301 |

Pokrycie 2020: SOL 25,136612%, LTC 99,726776%, AVAX 27,322404%; pozostałe 100%. Wszystkie późniejsze roczne fragmenty żądanego okna: 100%.

## 3. CURRENT metadata i niepewność historycznego listing provenance

Wszystkie 10 bieżących rekordów katalogu podało identyczne `listTime="1611907686000"`, tj. `2021-01-29T08:08:06Z`, mimo że endpoint zwrócił również wcześniejsze świece. Zachowano dokładnie oba fakty; **nie poprawiano ani dat, ani danych**. Nie udowodniono, że `listTime` jest pierwotnym globalnym listingiem lub że przed tą datą istniał ten sam regionalny produkt. Liczby otwarć świec przed bieżącym `listTime` są zapisane jako `coverage.observationsBeforeCurrentListTime`: BTC/ETH/XRP/DOGE/ADA/LINK/BCH po395, LTC394, SOL121, AVAX129. Są to porównania opening timestamp, nie twierdzenie, że każda z tych świec w całości poprzedza listing.

Nie wykonano żadnego client-side provider splice: każdy historyczny rekord pochodzi z tego samego hosta, endpointu i dokładnego instId. Wewnętrzna genealogia wcześniejszej historii po stronie dostawcy nie została niezależnie dowiedziona. Prerejestrowane primary 2022+ i 200-dniowy warmup od `2021-06-15` leżą już po wspomnianym bieżącym listTime. Nie daje to gwarancji kosztów lub historycznej wykonalności.

Bieżące wartości z katalogu — **oryginalne stringi**, nie historyczne reguły lotów:

| Base | lotSize ← lotSz | minSize ← minSz | tickSize ← tickSz |
|---|---|---|---|
| BTC | `0.00000001` | `0.00001` | `0.1` |
| ETH | `0.000001` | `0.0001` | `0.01` |
| SOL | `0.000001` | `0.01` | `0.01` |
| XRP | `0.000001` | `1` | `0.0001` |
| DOGE | `0.000001` | `10` | `0.00001` |
| ADA | `0.0001` | `10` | `0.0001` |
| LTC | `0.000001` | `0.01` | `0.01` |
| LINK | `0.000001` | `0.1` | `0.001` |
| BCH | `0.000001` | `0.01` | `0.01` |
| AVAX | `0.000001` | `0.1` | `0.001` |

`metadataTemporalBasis=CURRENT_SNAPSHOT_NOT_HISTORICAL`. Pełne źródłowe rekordy instrumentów, w tym state/tradeQuoteCcyList/expTime/groupId i inne zwrócone pola, są zachowane w per-instrument metadata wraz z provenance katalogu. Publiczne `tradeQuoteCcyList` też nie zastępuje prywatnej weryfikacji konta.

## 4. Kontrakt i trwałość danych poza Git

Katalog trwały: `/home/ubuntu/projects/apex-data/naval-okx` — nie scratch, repo ani package. Prawo do redystrybucji danych nie zostało ustalone, dlatego raw/CSV nie trafiają do Git.

- `dataset.json`: `schemaVersion=naval-market-data-v1`, root `source`, `quoteAsset`, `priceBasis=TRADE`, `cutoffUtc`, `timeframe=UTC1D`, `bar=1Dutc`, `confirmedOnly=true`, `csvColumns`, `normalizerSha256` i dokładnie10 wpisów `instruments` w wymaganej kolejności.
- Instrument: `baseAsset,instrument,quoteAsset,source,priceBasis,timeframe,status,csvPath,sha256,startUtc,endUtc,rows,gaps,listTime,metadataPath,lotSize,minSize,tickSize,coverage,duplicates`. Dla braku/failure: `status=unavailable`, konkretne `unavailableReason`, brak udawanego CSV; żaden wymagany base nie znika z listy.
- `normalized/<BASE>-USDT-1Dutc.csv`: dokładnie `timestamp,open,high,low,close,volume`; timestamp otwarcia ISO UTC z `Z`, ściśle rosnąco; liczby to **oryginalne decimal strings** pól `o,h,l,c,vol`, bez float conversion. `volume` to base units. Wszystkie `csvPath` i `metadataPath` są względne do `dataset.json`.
- `gaps` opisuje wyłącznie luki **pomiędzy** obserwowanymi początkiem/końcem. Braki poza tym zakresem nie znikają: osobno `coverage.leadingMissingDays`, `trailingMissingDays`, `missingDays` i `years`.
- `metadata/<BASE>-USDT.json`: tożsamość base/quote/source/TRADE/UTC1D/confirm1, pełne CURRENT instrument metadata, lista dokładnych requests z URL/status/fetchUtc/rawPath/bytes/SHA256, statystyki normalizacji i pokrycia, powód zakończenia paginacji.
- `raw/`, `provenance/`: niezmienione bytes odpowiedzi HTTP oraz per-attempt metadane; `checkpoints/`: successful GET URL → raw hash/provenance. Reuse weryfikuje hash, URL, status i API payload. Uszkodzenie cache powoduje odmowę, nie cichy redownload lub zmianę źródła.
- `sample/`, `docs/`, logi RED/GREEN/download, `verify_dataset.py`, `verification.json` i `verification.log`: trwały ślad testów i niezależnego readback.

Przebieg bulk: **92 publiczne GET** (1 katalog +91 history pages, w tym puste terminalne strony), wszystkie HTTP200/code0, fetchUTC od `2026-10-29T21:54:30.724918Z` do `2026-10-29T21:55:19.001284Z`. Dodatkowe dwie wcześniejsze odpowiedzi próbek zapisano osobno. Ponowne rzeczywiste uruchomienie downloadera: **0 network requests, 92 verified cache hits**; zatem checkpoint reuse faktycznie wykonano, nie tylko przetestowano mockiem. W tym pobraniu retry nie był potrzebny.

Downloader ma stały host i allowlistę wyłącznie dwóch publicznych GET, odmawia redirectów (również na inny host OKX), nie ma credentials/auth ani provider fallbacku. Throttle 0,25s przed każdym żądaniem, do4 prób dla błędu transportu/429/5xx/50011, backoff1/2/4s. Regionalne403/451 i inne nienaprawialne błędy nie są ponawiane na alternatywnym regionie. Paginacja idzie po minimalnym **surowym** opening ts; krótka strona nie kończy pobierania, brak postępu/payload conflict powoduje odmowę. Unclosed/cutoff bars wykluczane, brakujących świec nie tworzy się.

## 5. Testy, niezależna weryfikacja i reproducibility

Nowe pliki kodu/testów:

- `tools/data/naval_okx_data.py` — wyłącznie Python stdlib, CLI download/resume.
- `tests/data/test_naval_okx_data.py` — syntetyczne fixtures wyłącznie testowe; nie są danymi researchu.

Testy napisane i uruchomione przed implementacją: początkowy **RED14/14** (brak implementacji). Dalsze RED dla brakującego code hash i jawnych rocznych duplikatów zachowano w logach. Końcowo **16/16 PASS**, exit0, zero skips. Zakres: zachowanie oryginalnych decimal strings, sortowanie, confirm/cutoff, błędne/nonfinite/misaligned OHLCV, duplikaty i konflikty, realne luki bez fillowania, puste coverage, cursor i krótka strona, exhaustion/stall/future, wybór10/quote, raw/cache/hash, throttle i retry/403, bounded transport retries/API failures, odmowa private/foreign route i redirect, manifest10/relative paths/CURRENT metadata/partial failure/code hash.

Komendy z root repo:

```bash
python -m unittest discover -s tests/data -p 'test_naval_okx_data.py' -v
python tools/data/naval_okx_data.py
python /home/ubuntu/projects/apex-data/naval-okx/verify_dataset.py
```

Ostatni verifier **nie importuje downloadera i nie wykonuje sieci/backtestu**. Odczytał i przeliczył 10 CSV i ich SHA256, 92 raw responses/hash/URL/status, powiązania CURRENT metadata, każdy wyeksportowany wiersz przeciw oryginalnemu źródłu, UTC, OHLCV, kolejność, duplikaty, roczne coverage, luki oraz primary/warmup. Sprawdził też zgodność3 małych prawdziwych próbek BTC z bulk. Wynik: **PASS, 24389 rows, missing541 / internal0 / duplicates0**, primary1762 + warmup200 na każde10. `verification.json` zawiera również per-instrument CSV/metadata hash i liczby.

Nie powtarzano zaakceptowanych .NET Task1–2 ani nie uruchamiano pełnego backtestu. Ten raport potwierdza wyłącznie downloader/dataset i jego walidację, nie wynik strategii, historyczne fees/slippage lub realne execution.

### Identyfikatory bajtów

- Source downloader SHA256: `8b0e75ab6520322afe5d08b634dc96c5e1c34fb39e71f95e8df6d88f5421670a`.
- Tests SHA256: `f07200dac45eed87f9b89ac2e46c384e3ef9e8779d9777326297e1835b1e72ae`.
- `dataset.json` SHA256 po potwierdzonym cache reuse: `692ea89d11281307d416068e5e78302d1f791cf60a17c65451ac3ba0a74b3703`.

Nie wykonano commit/push. Backend/import/cost model i późniejsze badanie stanowią osobny zakres Task3; same dane nie kończą całej roadmapy Task3.

## Źródła

[1] https://my.okx.com/docs-v5/en/ — oficjalny OKX EEA API V5 reference, odczyt Crawl4AI 2026-10-29. Sekcje Overview/Production & Demo Trading Services; Public Data/Get instruments; Market Data/GET Candlesticks history; pola1Dutc/ts/confirm/vol i paginacja after. Trwały odczyt i hash wskazane wyżej.

[2] Własne, zapisane odpowiedzi **oficjalnego publicznego API** `https://eea.okx.com/api/v5/public/instruments?instType=SPOT` oraz `https://eea.okx.com/api/v5/market/history-candles?...` — konkretne pełne URL/status/fetchUTC/raw SHA256 w `sample/`, `provenance/`, `metadata/`, nie snippets z wyszukiwarki. Dokładne request URLs są identyfikatorami pobrania, nie obietnicą niezmienności późniejszej odpowiedzi dostawcy.
