# APEX / Naval — źródłowe notatki OKX API, opcjonalny adapter DEMO SPOT

Stan odczytu: 2026-10-29, około 20:42 UTC. Zakres: wyłącznie oficjalne materiały OKX, cztery strony odczytane przez `/home/ubuntu/.local/bin/crawl4ai-read "URL"`; wyszukiwarka tylko do odkrywania adresów. Crawl zadziałał dla wszystkich stron, bez browser fallback. Nie wykonywano wywołań API giełdy, logowania, operacji na kluczach, tworzenia kont/subkont ani zleceń.

**Granice zlecenia:** to research, nie zgoda na uruchomienie adaptera ani choćby jedną faktyczną transakcję DEMO. Tylko backtest / ewentualne późniejsze DEMO, nigdy LIVE, withdrawal, lewar, pożyczki lub short. Osobny cTrader DEMO i jego istniejący zakaz krypto pozostają nienaruszone. Poniżej rozróżniono fakty dokumentacyjne od zaleceń projektowych.

## 1. Polska / EEA, właściwe hosty, twarda izolacja DEMO

- Dokumentacja EEA, sekcje `Production Trading Services` i `Demo Trading Services`: **REST obu środowisk ma ten sam host `https://eea.okx.com`**. W DEMO wymagane są klucz utworzony w panelu Demo Trading oraz nagłówek `x-simulated-trading: 1`. Sam host REST nie dowodzi, że operacja jest symulowana.[1]
- EEA DEMO WS: `wss://wseeapap.okx.com/ws/v5/public`, `wss://wseeapap.okx.com/ws/v5/private`, `wss://wseeapap.okx.com/ws/v5/business`. Dla porównania, produkcyjne EEA WS używa `wseea.okx.com`; ten host powinien być niedozwolony w projektowanym execution adapterze.[1]
- FAQ potwierdza wymóg `eea.okx.com` dla użytkowników EEA oraz błąd `50119` przy niedopasowaniu domeny regionu; `50101` oznacza niedopasowanie klucza i środowiska. `50123` w DEMO dotyczy braku permission do tradingu; nie jest argumentem za próbą użycia klucza LIVE.[2]
- Uwaga redakcyjna: wewnątrz regionalnego reference pozostały przykłady SDK z globalnym `ws.okx.com` lub `wspap.okx.com`. Nie kopiować ich hostów: normatywną konfiguracją dla tego planu jest regionalna tabela `Demo Trading Services`.[1]
- Oficjalny komunikat o Polsce deklaruje uruchomienie lokalnej giełdy oraz spot (publikacja 17.06.2025, aktualizacja 21.10.2025), ale zawiera zastrzeżenie, że nie wszystkie produkty/usługi są dostępne we wszystkich regionach.[4]
- **Niepewność:** publiczne strony potwierdzają ofertę spot w Polsce i opis DEMO/API EEA, ale nie potwierdzają uprawnień konkretnego polskiego konta, bieżącej dostępności całego DEMO, subkont ani kompletu instrumentów. FAQ dodatkowo mówi o wymogu AUM >100 USD dla tworzenia API key w niektórych obszarach, bez jednoznacznego potwierdzenia zastosowania do Polski/DEMO. Nie zakładać konieczności wpłaty, nie wykonywać jej.[2][4]

**Zalecenie projektowe:** fail-closed: konfiguracja wyłącznie `Demo` / offline backtest; brak wariantu LIVE i brak automatycznego fallbacku regionalnego. Dla prywatnego REST każdorazowo wymusić nagłówek DEMO i demo credentials, bo wspólny host nie wystarcza. Przy blokadzie regionalnej lub konta zatrzymać integrację i potwierdzić dostępność z oficjalnym supportem; bez VPN, fałszywej rezydencji lub zmiany regionu w celu obejścia. Publiczne dane historyczne do backtestu mogą stanowić odrębny, read-only dataset; nie może to otwierać produkcyjnej ścieżki execution.

## 2. Auth i minimalne scopes, bez withdrawal

### Fakty z `API key permissions`, `REST Authentication`, `WebSocket / Login`

- Scopes: `Read` — account info, bills, order history; `Trade` — składanie/anulowanie zleceń, **również funding transfer i ustawienia wymagające zapisu**; `Withdraw` — wypłaty. Nie utożsamiać `Trade` z wąskim „order-only”.[1]
- Publiczny research/backtest bez danych prywatnych nie wymaga tworzenia klucza; do późniejszego odczytu prywatnego minimalnie `Read`. Ewentualne późniejsze execution DEMO wymaga osobnej zgody i permission `Trade` dla demo key, obok potrzebnego odczytu. `Read` sam nie umożliwia zleceń.[1][2]
- Prywatny REST: `OK-ACCESS-KEY`, `OK-ACCESS-SIGN`, `OK-ACCESS-TIMESTAMP`, `OK-ACCESS-PASSPHRASE`; body JSON z `Content-Type: application/json`; w DEMO dodatkowo `x-simulated-trading: 1`.[1]
- Podpis: Base64(HMAC-SHA256(secret, `timestamp + method + requestPath + body`)); `method` uppercase, query GET jest częścią `requestPath`, nie body. REST timestamp ISO 8601 UTC z milisekundami; odchylenie od czasu serwera >30 sekund jest odrzucane.[1]
- `GET /api/v5/public/time` zwraca `ts` jako Unix milliseconds. **Niespójność dokumentacji:** FAQ wspomina UTC+8 dla server time, ale reference definiuje epoch milliseconds i REST auth jako UTC. Nie dodawać +8h do epoch; traktować tę wzmiankę FAQ jako wymagającą ostrożności, nie podstawę implementacji konwersji.[1][2]
- WS login: `op=login`, `args.apiKey`, `passphrase`, `timestamp`, `sign`; WS timestamp jest Unix **seconds**, a podpis używa `timestamp + 'GET' + '/users/self/verify'`. Nie przenosić formatu REST timestamp wprost do WS.[1]
- OKX zaleca IP allowlist (do 20 adresów). Demo key nie podlega opisanej regule wygasania po 14 dniach nieaktywności dla nieprzypiętych kluczy z write permissions; brak expiry nie zastępuje rotacji i ochrony sekretów.[1]
- DEMO nie wspiera m.in. `withdraw`, `deposit`, `purchase/redemption`; nie jest to powód, aby przyznawać `Withdraw` „na wszelki wypadek”.[1]

**Zalecenie projektowe:** nigdy nie przyznawać `Withdraw`. Transport z allowlistą potrzebnych endpointów; odrzucić transfery, deposit/withdraw, pożyczki i zmiany lewara nawet przy szerokim `Trade`. Nie logować kluczy, passphrase, podpisów ani sekretów. Na obecnym etapie żadnych kluczy nie tworzono ani nie odczytywano.

## 3. SPOT cash, jednostki sizingu, fees i fill ledger

### Tryb bez lewara

- `GET /api/v5/account/config`: `acctLv="1"` oznacza `Spot mode`; `autoLoan=false` oznacza brak automatycznych pożyczek. Reference zaznacza, że pierwsze ustawienie account mode odbywa się w Web/App.[1]
- `tdMode="cash"` to non-margin. `SPOT` w Spot mode używa `cash`; inne account modes mogą używać cross, zatem samo `instType=SPOT` nie jest dostateczną barierą bezlewarowości. Dla SPOT nie wysyła się `posSide`; `reduceOnly` nie jest zabezpieczeniem SPOT cash.[1]

**Zalecenie projektowe:** odczyt i walidacja Spot mode, brak auto-borrow, wyłącznie `SPOT` + `cash`; żadnej automatycznej zmiany trybu konta. Sell tylko do posiadanego, wolnego inventory; bez short, `MARGIN`, `SWAP`, `FUTURES`, `OPTION`, cross/isolated. Żadnej faktycznej walidacji konta nie wykonano.

### Sizing — sekcja `POST / Place order` (opis kontraktu, nie wykonana operacja)

- Limit buy/sell: `sz` jest ilością base currency, `px` ceną. Market SPOT: `sz` jest denominowane zgodnie z `tgtCcy`, czyli `base_ccy` lub `quote_ccy`; domyślnie buy=`quote_ccy`, sell=`base_ccy`. Ustawiać jawnie, nie polegać na domyślnych wartościach.[1]
- `banAmend=true` dla SPOT market odrzuca zlecenie przy niewystarczającym saldzie, zamiast domyślnego redukowania `sz` przez system (`false`). Reference opisuje również dodatkową rezerwę quote do 5% przy market risk check oraz best-effort sizing w zmienności; nie obiecywać dokładnego otrzymanego wolumenu.[1]
- Metadata: `tickSz`, `lotSz`, `minSz`, `maxLmtSz`, `maxMktSz`, `maxLmtAmt`, `maxMktAmt`. Dla SPOT `lotSz` i `minSz` są w base currency. Uwaga: opis `maxMktSz` w reference mówi o jednostce USDT dla SPOT/MARGIN; nie reinterpretować automatycznie jako base na parach EUR/USDC — to punkt do dodatkowej walidacji przed implementacją sizingu.[1]
- Regionalnie istotne: `tradeQuoteCcy` musi odpowiadać jednej z wartości `tradeQuoteCcyList` z `GET /api/v5/account/instruments`; w niektórych regionach pominięcie pola może powodować `51000`. Nie utożsamiać nazwy instrumentu, quote metadata i rzeczywistej waluty rozliczenia bez kontroli.[1]

**Zalecenie projektowe:** arytmetyka decimal, zaokrąglanie ilości w dół do lotów, jawne jednostki i waluta quote, walidacja ticków/minimum/maksimum oraz rezerwa fees/slippage. Nie zakładać, że istnieje uniwersalne „minimum notional”; źródłowe `minSz` to minimum ilości base. Weryfikować realne fills zamiast zaksięgowania requested `sz`.

### Fees i ledger

- `GET /api/v5/account/trade-fee?instType=SPOT&instId=...`: reference zaleca konkretny `instId` dla właściwych stawek. Aktualne pola `feeGroup[].maker`, `feeGroup[].taker`, `feeGroup[].groupId`; mapowanie grupy przez instruments. Ogólne top-level `maker`, `taker`, `makerU`, `takerU`, `makerUSDC`, `takerUSDC` są oznaczone deprecated. Nie hardkodować przykładowej stawki.[1]
- REST fills (`GET /api/v5/trade/fills`, `/fills-history`): `instId`, `ordId`, `clOrdId`, `tradeId`, `billId`, `side`, `fillPx`, `fillSz`, `fillTime`, `ts`, `execType`, `fee`, `feeCcy`, `feeRate`, `tradeQuoteCcy`. `fee<0` oznacza koszt, `fee>0` rebate. `execType=T/M` oznacza taker/maker. `fillTime` to moment wykonania, `ts` moment wygenerowania rekordu — sortować wykonania po `fillTime`.[1]
- WS `orders`: `fillPx`, `fillSz`, `fillFee`, `fillFeeCcy`, `fillTime`, `tradeId`, `execType` dotyczą aktualizacji; `accFillSz` jest kumulacją, `avgPx` średnią. Dla SPOT nawet market `fillSz` i `accFillSz` są w base currency niezależnie od `tgtCcy`.[1]
- `GET /api/v5/account/bills` / `/bills-archive` opisują zmiany salda: m.in. `billId`, `ccy`, `bal`, `balChg`, `fee`, `ordId`, `tradeId`, `ts`. Służą do uzgodnienia, a nie do bezwarunkowego ponownego księgowania tych samych fill fees.[1]
- `account/config.feeType`: `0` — fee w otrzymywanej walucie; `1` — fee w quote. W ledgerze nadrzędne pozostaje rzeczywiste `feeCcy` / `fillFeeCcy`, nie założenie „zawsze quote”.[1]

**Zalecenie projektowe:** trwały ledger filli i cashflow per waluta, opłata w jej rzeczywistej walucie, identyfikacja po środowisku + koncie + `instId` + `tradeId`, powiązanie `billId` przy REST. Nie sumować `accFillSz` z każdej wiadomości, nie traktować REST last-fill fields jako listy wszystkich transakcji. SPOT cost basis i wynik netto wyliczać z własnego inventory ledger; `fillPnl` nie zastępuje księgowości cash SPOT. Backtest ma jawne założenia fee/spread/slippage, DEMO nie dowodzi rentowności LIVE.

## 4. REST / WS: reconnect, idempotencja, retencja

- Kanał prywatny `orders` **nie daje początkowego snapshotu**: dane są wypychane dopiero dla nowych zleceń/aktualizacji. Subskrypcja nie odtwarza automatycznie stanu sprzed przerwy.[1]
- Heartbeat: po braku wiadomości przez N<30 sekund wysłać tekst `ping`, oczekiwać `pong`; brak odpowiedzi wymaga błędu/reconnect. Limity: 3 połączenia/s per IP; 480 operacji login/subscribe/unsubscribe na godzinę per connection; 30 połączeń dla danego limitowanego kanału per subaccount.[1]
- Możliwe duplikaty eventów, nawet z innym `uTime`; deduplikacja `tradeId` w zakresie instrumentu. Możliwy `state=filled` z `fillSz=0`; nie oznacza to „zero wykonania”. Terminalne filled/canceled deduplikować po `ordId` zgodnie z opisem kanału.[1]
- `clOrdId` do 32 case-sensitive znaków alfanumerycznych; unikalność wymagana dla pending orders, nie historycznie. Ponowne użycie po terminalnym stanie jest dozwolone; query po powtórzonym `clOrdId` zwróci tylko najnowsze dopasowanie. To nie jest wieczysty exchange-side idempotency key.[1]
- FAQ: `50004` timeout nie potwierdza ani sukcesu, ani porażki. Należy ustalić rzeczywisty wynik, nie wykonywać ślepego ponowienia potencjalnie przyjętej operacji.[2]

| Read endpoint | Okno / paginacja | Limit z reference |
|---|---|---|
| `GET /api/v5/trade/order` | szczegóły konkretnego zlecenia | 60 / 2 s, UID+instrument (poza Options) |
| `GET /api/v5/trade/orders-pending` | aktualne incomplete orders | 60 / 2 s, UID |
| `GET /api/v5/trade/orders-history` | 7 dni; uwaga retencyjna canceled incomplete orders: tylko 2 godziny; `after`/`before` po `ordId`, max 100 rekordów | 40 / 2 s, UID |
| `GET /api/v5/trade/orders-history-archive` | 3 miesiące | 20 / 2 s, UID |
| `GET /api/v5/trade/fills` | 3 dni; `after`/`before` po `billId`, `begin`/`end` po `ts`, max 100 | 60 / 2 s, UID |
| `GET /api/v5/trade/fills-history` | 3 miesiące; `instType` wymagane, analogicznie max 100 | 10 / 2 s, UID |
| `GET /api/v5/account/bills` | 7 dni, max 100, paginacja po bill ID | 5 / s, UID |
| `GET /api/v5/account/bills-archive` | 3 miesiące | 5 / 2 s, UID |

Wartości w tabeli pochodzą z odpowiednich sekcji reference; nie stanowią gwarancji, że DEMO udostępni pełne historyczne okna bez ograniczeń środowiska.[1]

**Zalecenie projektowe:** po reconnect: login/subskrypcja, bufor zmian, REST pending + order details + fills od checkpointu z nakładką, bills i balance reconciliation, deduplikacja, dopiero potem wznowienie pracy. Exponential backoff z jitter, endpoint-specific throttling, trwały checkpoint i lokalne archiwum. Przy luce przekraczającej odzyskiwalną historię lub niezgodnych saldach zatrzymać moduł; nie odtwarzać brakujących filli przez zgadywanie. Brak testu runtime oznacza brak potwierdzenia retencji DEMO i zachowania reconnect w konkretnym koncie.

## 5. Dzienne świece UTC, potwierdzenie, historia i luki

- REST `GET /api/v5/market/candles`: ostatnie 1 440 wpisów, max `limit=300`, default 100; limit 40/2 s per IP. `GET /api/v5/market/history-candles`: historia „recent years”, bez gwarancji konkretnej daty startowej; 1s ograniczone do ostatnich 3 miesięcy; max `limit=300`, default 100; 20/2 s per IP.[1]
- `bar=1D` to otwarcie UTC+8; **`bar=1Dutc` to doba UTC+0**. WS używa **`channel=candle1Dutc`** na `/ws/v5/business`, czyli DEMO EEA host `wseeapap.okx.com`; nie `candle1D` ani private endpoint.[1]
- Format REST i danych candle: `[ts,o,h,l,c,vol,volCcy,volCcyQuote,confirm]`; `ts` to opening Unix milliseconds. `confirm="0"` oznacza świecę nieukończoną, `confirm="1"` ukończoną. Dla SPOT `vol` jest w base, `volCcy` i `volCcyQuote` w quote.[1]
- `after` pobiera wcześniejsze rekordy niż zadany `ts`; `before` późniejsze, przy samym `before` zwracane są najnowsze dane. Nie zakładać znaczenia nazw odwrotnego do reference. Pierwsza świeca może być niekompletna.[1]
- Reference opisuje przeniesienie poprzedniego close jako OHLC w cyklu bez transakcji. To nie daje prawa syntetycznego wypełnienia dowolnej luki sieciowej lub brakującej historii.[1]
- Oficjalny opis DEMO jest niespójny: główna sekcja mówi o całkowicie oddzielnych danych i braku związku ruchów DEMO z LIVE; FAQ tej samej strony ogólnie opisuje symulację real-time market data. Konserwatywnie nie zakładać identyczności DEMO i LIVE ani wspólnej kompletności historii.[3]

**Zalecenie projektowe:** strategia D1 konsumuje wyłącznie `confirm=1`; przechowywać UTC open, instId, bar, pochodzenie DEMO/public historical i czas pobrania. REST backfill po każdej przerwie WS, sortowanie i deduplikacja po kluczu instrument+bar+opening time+dataset. Wykrywać brakujące doby, duplikaty, niespójne OHLC i zakres przed listingiem; oznaczać niepełność, nie tworzyć fałszywych świec. Osobno raportować rzeczywisty dostępny start/koniec i pokrycie dla każdego instrumentu, z warm-up. Nie obiecywać wieloletniego backtestu wszystkich instrumentów na danych DEMO.

## 6. Subkonta — warunkowe, nie obietnica

- Oficjalny Demo FAQ §7: DEMO nie wspiera tworzenia ani zarządzania subkontami; subkonto tworzy się w środowisku live, a potem może być używane w DEMO i przełączane między środowiskami.[3]
- API FAQ: key musi pochodzić z zamierzonego subaccountu, z właściwymi permission/passphrase/domain; nie mieszać kluczy konta głównego i subkonta. FAQ zastrzega możliwy regionalny wymóg AUM >100 USD także dla subkont.[2]

**Wniosek projektowy / niepewność:** nie obiecywać osobnego subkonta DEMO dla każdej strategii ani możliwości tworzenia subkont w DEMO. Publiczny opis nie weryfikuje dostępności ani liczby subkont dla konkretnego polskiego użytkownika. Utworzenie subkonta w live jest zmianą zewnętrzną poza zakresem tej zgody — nie wykonywać. Można rozważyć wewnętrzne, logiczne portfolio strategii i ledger z limitami wspólnego salda; nie przedstawiać tego jako izolacji giełdowej.

## 7. Dziesięć wymaganych symboli — discovery, nie gwarancja

**Lista wymagana przez użytkownika:** BTC, ETH, SOL, XRP, DOGE, ADA, LTC, LINK, BCH, AVAX. Lista jest znana; wcześniejsza notatka wykonawcy o braku nazw była błędna. Nie sprawdzono dostępności dziesięciu konkretnych par na koncie użytkownika. Liczba 10 jest wymaganiem projektu, nie wynikiem discovery konta.

- `GET /api/v5/public/instruments?instType=SPOT` służy odkryciu katalogu publicznego, a `GET /api/v5/account/instruments?instType=SPOT` zwraca dostępne instrumenty **bieżącego konta**. Potwierdzać w odpowiednim regionie i DEMO, nie tylko w globalnej liście.[1]
- Dla każdego z dziesięciu wymagań pobrać i zachować rzeczywiste `instId`, `instType`, `baseCcy`, `quoteCcy`, `tradeQuoteCcyList`, `state`, `tickSz`, `lotSz`, `minSz`, `listTime`, `expTime`, `groupId`; `state=live` oznacza **status instrumentu**, a nie zgodę/wybór produkcyjnego środowiska.[1]
- Metadata może się zmieniać; reference opisuje aktualizacje instrumentów przez WS instruments channel. Przykładowe `BTC-USDT` w docs nie gwarantuje pary ani quote dla Polski/DEMO. Nie zakładać dostępności wszystkich par USDT lub zamienności USD/USDC/EUR.[1]

**Zalecenie projektowe:** tabela akceptacji dokładnie dla dziesięciu aktywów od właściciela planu: desired asset → discovered instId + settlement quote → metadata + fee group → kompletność D1 UTC → status available/unavailable/unverified i powód. Wymagać 10/10 zweryfikowanych zgodnych par przed spełnieniem kryterium „obsługujemy 10”; brak choć jednej blokuje takie twierdzenie. Bez automatycznej zamiany aktywa, quote, regionu lub brokera i bez osłabiania zakazu krypto w cTrader.

## 8. Bramka dalszego etapu

Research dokumentacyjny zakończony; runtime konta, aktualne instrumenty, historia DEMO i subkonta **nie zostały sprawdzone**. Zlecenie użytkownika upoważnia do implementacji etapów offline i publicznego odczytu danych. Lista dziesięciu aktywów jest określona powyżej. Prywatny odczyt wymaga dostępnych demo credentials przekazanych bezpiecznie poza czatem; nie wolno ich zgadywać ani żądać uprawnień wypłaty. Faktyczne zlecenia DEMO pozostają zabronione bez nowej, wyraźnej zgody. LIVE i withdrawal nie należą do projektu.

## Ślad odczytów

- `okx-eu-api-crawl.txt`: pełny Crawl4AI odczyt reference [1]; istotne sekcje: API key permissions, REST Authentication, WS Connect/Login, Account mode, Demo Trading Services, account instruments/config/trade-fee/bills, order sizing/history/fills/orders channel, market candles/history/WS, public instruments/time.
- `okx-eu-faq-crawl.txt`: odczyt [2], m.in. regional domains, błędy 50101/50119/50123/50004, ograniczenia tworzenia kluczy i subaccount key.
- `okx-eu-demo-crawl.txt`: odczyt [3], szczególnie separate market data oraz subaccounts §7.
- `okx-poland-crawl.txt`: odczyt [4], oferta Polska oraz regional disclaimer.
- Osobny task ledger `okx-research-ledger.json`, z cytatami sprawdzonymi literalnie przeciw zapisanym odczytom. Poniższy blok źródeł generowany z ledgeru.

## Sources

[1] https://my.okx.com/docs-v5/en — OKX EEA API V5
    > "Note: x-simulated-trading: 1 needs to be added to the header of the Demo Trading request."
[2] https://www.okx.com/en-eu/help/api-faq — OKX EEA API FAQ
    > "A key with only Read permission can retrieve account data but cannot place orders."
[3] https://www.okx.com/en-eu/help/how-to-use-demo-trading — OKX EEA Demo Trading
    > "No. Demo trading doesn't support creating or managing sub-accounts."
[4] https://www.okx.com/en-eu/learn/okx-exchange-germany-poland — OKX launch Poland
    > "Not all products and services offered in all regions. Exclusions and T&Cs apply."

## Korekta architekta / trwałość notatek
Nazwy aktywów pochodzą z misji użytkownika. Notatki opisują dokumentację, nie wykonany adapter ani weryfikację konta. Pliki crawl/ledger w scratch mogą wygasnąć; trwałe są powyższe cytowania i wnioski, a przed implementacją requestów trzeba ponownie sprawdzić istotne oficjalne sekcje.
