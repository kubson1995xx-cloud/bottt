# APEX Bridge Protocol v1

## Aktualny scoped runtime (Phase C)

Główna ścieżka: **ApexBridge + APEX Desktop**, dedykowane i wyłączne konto DEMO **hedged**.
`coverage:"unknown"` pozostaje niezmienione. `ScopedReady` oznacza uzgodnienie wyłącznie trwałego
zakresu aplikacji, nie kompletność brokera i nie zgodę START. **Strategie są zablokowane do Task2 PASS**.
Native `ApexTradingAI` jest obecnie jawnie tylko odczyt/redukcja, bez setupu/permitu/test-open.
Natywny backtest korzysta z osobnej zaufanej kompozycji tylko dla rzeczywistego SDK `IsBacktesting`.

1. `hello` negocjuje równocześnie `historyCapability:"closing-deals-v1"` i
   `scopedCapability:"scoped-recovery-v1"`. Wymaga jawnych `account.isLive` i `account.hedging`,
   dokładnego `label`, `brokerEpoch` i `brokerConnected`; `deploymentInventory[]` zawiera
   `label,healthy,entries,missing` ze znanych platformowych dzienników/katalogu, nie deklarację coverage.
   Nazwa `Symulator`, JSON coverage ani `IsBacktest` nie nadają uprawnienia symulacji produkcji.
2. `welcome` dodaje `scopedCapability,baselineId,transportGeneration`. `baselineId` przed setupem
   to pusty GUID. Host nie zmienia parametru cBota automatycznie; pokaże dokładną etykietę do skopiowania.
3. SDK `Server.Connected`/`Disconnected` natychmiast zmieniają losową startup-epokę/licznik,
   również gdy outage mieści się pomiędzy timerami. `brokerState` niesie `brokerEpoch,brokerConnected`;
   ten sam obiekt jest w `account.brokerState` i `result.brokerState`. WebSocket alive nie oznacza broker alive.
4. `positions` dodaje jawne **wszystkie** `pendingOrders[]` oraz `scoped`. Pending order:
   `id,label,symbol,side,units,target,sl,tp,comment`. Brak tablicy nie oznacza pustej tablicy.
   `scoped` (`ScopeFrame`) ma zawsze jawne:
   `baselineId,label,executionPath:"desktop-bridge",safetyVersion:"scoped-demo-v1",transportGeneration,
   brokerEpoch,brokerConnected,positionRevision,pendingOrderRevision,snapshotId`.
   W live frame `snapshotId` jest pusty; obie rewizje obejmują wszystkie obiekty i SL/TP/tożsamość,
   nie zmienny mark-to-market. Frame nie jest permittem.
5. `historyRequest` dodaje `baselineId` i `recoverOperations[]` (oryginalne Core operation IDs).
   Na wątku SDK zamrażane są konto, wszystkie pozycje/zlecenia, dostępne deale, opening graph
   `Position.Deals` i `History.ClosingDeal.Closing` oraz dodatnio skorelowane wyniki platformowego
   journalu. Każda strona ma identyczny `scoped` z losowym niepustym `snapshotId` i ten sam capture UTC.
   Ostatnia dodaje `pendingOrders,openingObservations,recoveredResults` (jawne tablice również gdy puste).
   Opening observations mają dokładne pola `OpeningDealEvidence`; wyniki zachowują oryginalne
   `openingFill.request.executionId`. Lookup nie wysyła ponownie open; brak danych nie jest rejection.
6. Limity: 100000 closing rows, 1000 stron, 500 rows/stronę (SDK nadaje 250), 10000 pozycji i pending
   orders, 100000 opening observations, 1000 recovered operations, 8 MiB/frame. Overflow/brak strony/
   konflikt/missing wymaganych metadanych blokuje, bez ucinania dowodu. Capture/receipt/request max 30 s;
   cykliczne pobranie co 10 s zachowuje ostatnią poprawną receipt tylko do jej oryginalnego deadline.
7. Account owner autoryzuje odbiór przy aktualnym attachment, zamraża dane, nadaje kolejność powyżej
   trwałego `LastScopedObservationSequence`, i wywołuje internal Core `EstablishBaseline/ReconcileScoped`.
   Stara kompletna receipt może dokończyć księgowanie po reconnect, nigdy odblokować nowej generacji.
8. Core zapisuje admission/opID/request/risk/plan przed gateway i przekazuje opaque, nieserializowalne
   `ScopedDispatchAuthorization`. SessionAdapter recheck po oczekiwaniu na send lock. `open.id` to
   oryginalny precommitted operation ID; `open.scoped` to dokładny zaakceptowany ScopeFrame.
   SDK po trwałym journal reserve ponownie sprawdza własny capture, pełną stałą tożsamość konta,
   label/baseline/transport/epoch/revisions/freshness i SL/TP bezpośrednio przed SDK open. Zmiana = zero open.

Setup w polskim panelu: **Połączenia platform → Przygotuj zakres DEMO (read-only)**. Jawne potwierdzenie
dedykowanego DEMO/hedging/wyłącznego pisarza nie zatwierdza transakcji. Inventory obejmuje dotychczasowy
config, wszystkie pliki kont/stany/control/dzienniki/etykiety oraz znany katalog platformy. Nowa instalacja
otrzymuje przed pierwszym zapisem znacznik pełnej tożsamości `scope-bootstrap.json`; pozwala on wrócić
do setupu po zwykłym restarcie, nie odtwarzać brakującego stanu ani omijać legacy. Core zachowuje risk latches.
Po rezerwacji skopiuj wygenerowaną etykietę do parametru cBota **„Dokładna etykieta pozycji APEX”**,
zatrzymaj/uruchom most i sparuj ponownie. Świeży zerowy inventory ustanawia zakres atomowo, bez open.
Osobny **Test wykonania (DEMO)** wymaga nowego potwierdzenia transakcji (`confirmDemoTrade:true`).
Setup używa `setupScope` / `args.confirmDedicatedDemo:true`; żadne z tych działań nie uruchamia strategii.

Zakres nie rozwiązuje utraty wszystkich danych/zgodnego rollbacku, ukrytych dowolnych writerów ani
omijanych przez SDK otwarć/zamknięć. Katalog zna dotychczas zarejestrowane etykiety i legacy APEX/APEX-N;
SDK nie oferuje enumeracji dowolnych dawnych namespace'ów LocalStorage ani retencji całej historii.
Nie zakładaj, że nowy pusty katalog danych/nowy terminal anuluje zobowiązania starej instalacji.
Brak/corrupt/legacy/niepewne otwarcie/deficyt pozostają do uzgodnienia; nigdy nie kasuj plików lub nie
rotuj etykiety w celu „naprawy”. Pełny dokładnie własny close/Emergency pozostają dostępne bez scope/PASS.

Jeden format dla wszystkich platform. Każdy komunikat to obiekt JSON z polem `type`, nazwy pól w camelCase.
Implementacje wzorcowe: `src/Platforms/cTrader/ApexBridge` (C#), `src/Platforms/MT5/ApexBridge.mq5` (MQL5), symulator `tools/Apex.PlatformSimulator`.
Definicje typów: `src/Apex.Core/Bridge/Protocol.cs`.

## Wymagana zdolność odzyskiwania `closing-deals-v1` (Task1)

Wersja bazowego transportu pozostaje `protocol:1`, lecz nowe ryzyko wymaga opcjonalnej zdolności
`hello.historyCapability:"closing-deals-v1"`. Stary most (w tym niezmieniony MT5) może dostarczyć
stan/pozycje i przyjmować pełne zamknięcie, ale **nie certyfikuje synchronizacji i nie otwiera nowego ryzyka**.
`closed` jest odtąd tylko wskazówką do ponownego pobrania historii, nigdy samodzielnym dowodem wyniku.

1. Po `welcome` host wysyła `{"type":"historyRequest","requestId":"<losowy token>","label":"APEX"}`.
   Token jest nowy dla każdego pobrania i sesji. Most cTrader obsługuje żądanie wyłącznie na wątku platformy.
2. Most zamraża dostępną kolekcję History i pozycje/konto, sortuje raw deale po `(exitTime,closingDealId,id)`
   i wysyła `historyPage`: `requestId,page,pages,total,scope,coverage,capturedAt,deals,account,positions`.
   `page` od 0, `scope:"available-history-v1"`; `capturedAt` jednakowy UNIX ms dla całej migawki.
   `account` i **wszystkie pozycje**, również obce, są obowiązkowe w ostatniej stronie.
   Implementacje wysyłają po 250 dealów; host odrzuca >500/stronę, >100000/całość i >1000 stron.
   Pusta kolekcja to jedna strona `total:0,deals:[]`, nie brak odpowiedzi.
3. Każdy element `deals` ma pola `ClosedDto` oraz **closingDealId**. To surowy deal zamykający, nie suma
   pozycji. Kanoniczny klucz SDK: dodatni `HistoricalTrade.ClosingDealId:Int32` jawnie poszerzony do Int64,
   z `positionId`, dokładną etykietą i pełną tożsamością konta w stanie właściciela. Nie zamieniamy go
   niejawnie z `ClosingDeal.Id:Int64`. Częściowe zamknięcia są osobnymi dealami; `net` zawiera już
   signed commission/swap. Nie doliczać ich ponownie.
4. Host publikuje snapshot dopiero po pełnej sekwencji stron i zgodnym count/tożsamości. Brak strony,
   sprzeczny duplikat, obcy/stary token, error, przekroczenie limitów lub >30 s od capture nie certyfikuje
   kompletności. Identyczny duplikat strony jest no-op. Kolejne aktualizacje wolumenu własnej pozycji
   lub hint `closed` unieważniają readiness i wymagają świeżej migawki. Pobranie odnawiane co 10 s;
   brak świeżego kompletnego pobrania przez 30 s blokuje nowe ryzyko.

### Ważne: kompletność transmisji nie jest kompletnością brokera

SDK Automate **1.0.21** udostępnia `History.Count`, indexer, `FindByPositionId(int)` i
`FindByClosingDealId(int)`, ale **nie dokumentuje** load-more/paginacji serwera ani flagi zakresu
pełnej historii brokera. Zatem oba prawdziwe cBoty raportują `coverage:"unknown"` / `RequiredCoverage=false`.
Bez ustanowionego scoped baseline oznacza to blokadę test-open. Nowy scoped runtime opisany wyżej
może dopuścić jawny test techniczny bez zmiany coverage. Nie istnieje przełącznik operatora ani JSON,
który podnosi coverage cTrader do kompletnego. Zaufana kompozycja symulacyjna i rzeczywisty natywny
runtime backtest mają odrębną structural authority; sama nazwa platformy jej nie nadaje.

Bez pełnego coverage nie importujemy przypadkowej historii sprzed instalacji ani wyników pozycji
odtworzonej z bieżącego SL. Dostępne deale można trwale rozliczać dla zachowanego oryginalnego
planu wejścia; finalizacja wymaga nieobecności pozycji i sumy closed volume równej zapisanej initial
volume. Gdy tego nie da się udowodnić, plan/intencja pozostają oczekujące, nie powstaje wymyślone R.
Pełny close konkretnego dokładnie własnego ID w uwierzytelnionym DEMO pozostaje dostępny także przy
CoverageUnknown lub awarii dysku; nie dotyczy to partial/modify/open.

### Trwała akceptacja i migracja

Autorytatywny `EngineState` v2 przechowuje raw deale, sfinalizowane pozycje, liczniki, plany/intencje
oraz outbox w pojedynczym zapisie. CSV jest atomową, kluczowaną projekcją; błąd eksportu zostawia
durable outbox do ponowienia. Dokładnie-jeden wynik dotyczy lokalnego księgowania, nie gwarancji
wykonania sieciowego brokera. Klucze nie wygasają wraz z oknem UI 300/500 wpisów.
V1 bez wyników może przejść do v2 z zachowaniem emergency/reservations/plans. V1 z wynikami albo
CSV bez pasującego stanu ma nieznany overlap i wymaga ręcznego uzgodnienia; nie dodajemy obu zbiorów,
nie usuwamy oryginalnego dowodu. Natywny LocalStorage używa pojedynczej koperty identity/generation/
SHA-256; uszkodzona, obca albo niejednoznaczna koperta blokuje, nie odtwarza się z dowolnej starej kopii.
Restart/reconnect nigdy nie uruchamia strategii; zapisane full-close intents ponawiają się z backoff
15…300 s do autorytatywnego rozliczenia. Bez połączenia lub trwałego zapisu nie ma gwarancji likwidacji.

## Dowód opening fill

Dowód wchodzi w trwały manifest i scoped proof opisane wyżej. Nie jest samodzielnym permittem,
certyfikatem brokera ani trwałym PASS Task2. Faktyczny test DEMO wymaga osobnego zatwierdzenia.

`open.signalId` jest opcjonalnym, dokładnym identyfikatorem Core, niezależnym od komentarza
brokera. Odpowiedź `result` może zawierać `openingFill` także przy `uncertain`:

- `request`: oryginalne `executionId`, `signalId`, symbol, kierunek (`1` long / `-1` short),
  **żądany** `volumeUnits`, `stopLossPips`, `takeProfitPips`, label, comment i maxSlippagePips;
- `positionId`, **rzeczywisty zaobserwowany pierwotny** `initialVolumeUnits`, `volumeStep`,
  `entryPrice`, `entryTimeUtc`;
- `openingDeals[]`: `dealId:Int64`, `orderId`, `positionId`, `volumeUnits`, `price`, `timeUtc`,
  symbol, direction, label, comment. Czasy w tym obiekcie to ISO8601 UTC (nie UNIX ms).

Dowód jest zamrażany z `Position.Deals` na wątku platformy bezpośrednio po odpowiedzi SDK,
**przed** naprawą ochrony lub jej awaryjnym close, a następnie zapisywany jako checkpoint
niepewnego wyniku w journalu platformy. Odpowiedź końcowa, kopia, restart i powtórka sygnału
z innym commandId zachowują oryginalny executionId i dowód. Błąd zapisu nie zmienia się w `filled`
i nie pozwala na drugie wykonanie. Przy niejednoznacznym zapisie obserwacja może pozostać tylko
w pamięci/odpowiedzi — nie jest to gwarancja trwałości fizycznego LocalStorage.

Minimalna obsługa: dokładnie jeden widoczny, wypełniony opening deal zgodny z żądaniem i quantum.
Brak grafu, multi-fill/addition, niezgodna tożsamość/czas/cena, wzrost remaining, niepoprawny quantum
lub niewystarczająca precyzja oznacza **brak dowodu**, nie podstawienie żądanego ani remaining volume.
Opening `dealId` nigdy nie zastępuje kanonicznego klucza **closingDealId:Int32→Int64**.
Deserializacja `openingFill` sama nie nadaje własności ani prawa do handlu; Core musi jeszcze
powiązać go z trwałym wcześniejszym admission i uzgodnić `C+O=V0` w bieżącym zakresie/generacji.
SDK nie gwarantuje pełnej retencji grafu; nie można wykryć dowolnych ukrytych zewnętrznych mutacji.
Obsługiwana polityka to dedykowane, wyłączne konto **DEMO hedging**, bez obcych otwartych
pozycji i pending orders. Wykryty obcy obiekt blokuje nowe ryzyko; nie jest automatycznie likwidowany.

## Transport

| Transport | Adres | Ramkowanie |
|---|---|---|
| WebSocket | `ws://127.0.0.1:5077/bridge` | jedna wiadomość tekstowa = jeden JSON |
| TCP | `127.0.0.1:5078` | jeden JSON na linię (`\n`), UTF-8 |

- Serwer nasłuchuje wyłącznie na 127.0.0.1. Żądania WebSocket z nagłówkiem `Origin` (przeglądarki) są odrzucane.
- Maksymalny rozmiar komunikatu: 8 MB.
- Most musi wysłać `hello` w ciągu 15 s, inaczej połączenie jest zamykane.
- Sesja uznawana jest za żywą, jeśli w ciągu ostatnich 30 s przyszedł jakikolwiek komunikat (most wysyła notowania co ~1 s oraz konto i pozycje co ~2 s).
- Czasy: milisekundy UNIX (UTC). Wyjątek: świece – sekundy UNIX czasu otwarcia (UTC).

## Most → APEX

### `hello` (pierwszy komunikat)
```json
{"type":"hello","protocol":1,"platform":"cTrader","bridgeVersion":"1.0","key":"KLUCZ-PAROWANIA",
 "account":{"number":5123456,"broker":"Pepperstone","currency":"USD","isLive":false,"hedging":true,
            "balance":10000,"equity":10000,"margin":0,"freeMargin":10000,"marginLevel":null},"time":1760000000000}
```
Odpowiedź: `welcome`. Odrzucenie, gdy: zła wersja protokołu, zły klucz (porównanie w stałym czasie), brak numeru konta lub to samo konto jest już połączone przez żywy most.

### `symbols`
`{"type":"symbols","items":[SymbolDto…],"final":true}`. Pola SymbolDto:

`name, description, baseAsset, quoteAsset, digits, pipSize, tickSize, pipValue` (wartość 1 pipsa dla 1 jednostki w walucie konta), `lotSize, volumeMin, volumeStep, volumeMax` (w jednostkach), `minStopPips, tradingEnabled, closeOnly, commissionKind` (`None | UsdPerMillionUsdVolume | UsdPerLot | PercentOfVolume | QuoteCurrencyPerLot | Unknown`), `commissionValue` (jedna strona), `usdToAccount, quoteToAccount, isCustom`.

Prowizja `Unknown` oznacza brak handlu instrumentem (koszt niezweryfikowany). Klasyfikacja instrumentu (w tym wykluczanie kryptowalut) odbywa się po stronie APEX.

### `bars` (tylko ZAMKNIĘTE świece)
`{"type":"bars","symbol":"EURUSD","tf":"H1","snapshot":true,"bars":[[openTimeSec,o,h,l,c,v],…]}`. `snapshot:true` zastępuje historię; `false` dopisuje nowe świece. Interwały: `M15, H1, H4, D1`. Dla D1/H4 granica dnia jak u brokera.

### `quotes`
`{"type":"quotes","items":[{"s":"EURUSD","b":1.0850,"a":1.0851,"t":1760000000000,"pv":10.0,"o":true,"ttc":3600}]}`. `b`/`a` to bid/ask, `t` czas, `pv` bieżąca wartość pipsa (opcjonalnie), `o` – rynek otwarty, `ttc` – sekundy do zamknięcia sesji (−1 = nieznane).

### `account`
`{"type":"account","account":{…jak w hello…}}`, co ~2 s.

### `positions`
`{"type":"positions","items":[{"id":123,"symbol":"US500","side":"buy","units":1,"entry":5200.1,"entryTime":…,"sl":5150,"tp":null,"label":"APEX","comment":"APEX|DB|a1b2c3|v1","net":12.3,"gross":12.3,"commission":0,"swap":0,"price":5212.4,"margin":173.4}]}`.
Wysyłane są **wszystkie** pozycje konta, także ręczne i innych botów. APEX zarządza wyłącznie pozycjami ze swoją etykietą.

### `closed`
`{"type":"closed","position":{"id":…,"symbol":…,"label":…,"comment":…,"side":…,"units":…,"entry":…,"exit":…,"entryTime":…,"exitTime":…,"gross":…,"commission":…,"swap":…,"net":…,"reason":"StopLoss|TakeProfit|Closed|StopOut"}}`.

### `result` (odpowiedź na polecenie)
`{"type":"result","id":"<id polecenia>","status":"filled|rejected|uncertain","positionId":123,"price":1.0851,"error":"NoMoney","message":"…"}`.
- `filled` – wykonane i potwierdzone przez serwer brokera.
- `rejected` – serwer odrzucił; zlecenie na pewno nie zostało wykonane.
- `uncertain` (lub brak odpowiedzi w 15 s) – trwałe nierozstrzygnięte zobowiązanie. APEX **nie ponawia** open. Ani 90 s, ani pusta dostępna historia nie zwalniają rezerwacji; wymagany pozytywny journal/witness dla dokładnego operation/request.

Kody `error` (wielkość liter bez znaczenia, nieznany kod = `Other`): `None, TechnicalError, BadVolume, NoMoney, MarketClosed, Disconnected, EntityNotFound, Timeout, UnknownSymbol, InvalidStopLossTakeProfit, InvalidRequest, NoTradingPermission, Other`. Błędy techniczne (`TechnicalError, Timeout, Disconnected, Other`) liczą się do limitu kolejnych błędów – po 5 bot wstrzymuje nowe wejścia.

### `marginResult`, `log`
`{"type":"marginResult","id":"…","margin":173.4}` – szacowany depozyt (null = nieznany → brak wejścia).
`{"type":"log","level":"info|warn|error","message":"…"}` – komunikaty widoczne w panelu.

## APEX → most

| Typ | Pola | Wymagania wobec mostu |
|---|---|---|
| `welcome` | `accepted, reason, hostVersion, label` | przy `accepted:false` zakończ połączenie |
| `subscribe` | `symbols[], tfs[], history{tf:liczba}` | wyślij historię (`bars` snapshot), potem bieżące notowania i nowe świece |
| `open` | `id,signalId,symbol,side,units,slPips,tpPips,label,comment,maxSlipPips?,scoped` | wymagane dodatnie skończone SL i TP, aktualny scoped capture i exact identity; REAL zawsze odrzucony, bez wyjątku parametru |
| `modify` | `id, positionId, sl` | tylko pozycje z etykietą APEX |
| `close` | `id, positionId, units?` | tylko pozycje z etykietą APEX; `units` = częściowe zamknięcie |
| `margin` | `id, symbol, side, units` | odpowiedz `marginResult` |
| `ping` | – | dowolny komunikat w odpowiedzi |

Każde polecenie z `id` wymaga dokładnie jednej odpowiedzi `result` (lub `marginResult`) z tym samym `id`.

## Identyfikacja pozycji

Komentarz zlecenia: `APEX|<kod strategii>|<id sygnału>|v1`. Kody: `TP` trend–korekta, `BO` wybicie, `MR` powrót do średniej, `MO` impuls, `DB` kupno spadków, `DT` trend dzienny, `XT` test wykonania.
Identyfikator sygnału jest deterministyczny (FNV-1a z symbolu, strategii, kierunku i czasu świecy). Dzięki temu po restarcie lub niepewnej odpowiedzi ten sam sygnał nie zostanie wysłany drugi raz.

## Minimalna implementacja nowego mostu – lista kontrolna

1. Połącz, wyślij `hello` z kluczem, poczekaj na `welcome`.
2. Wyślij `symbols`, `account`, `positions`.
3. Po `subscribe` wyślij historię świec i zacznij wysyłać `quotes` (co ~1 s), `account` i `positions` (co ~2 s), nowe zamknięte świece i `closed`.
4. Obsłuż `open/modify/close/margin` z odpowiedzią `result`. Nigdy nie otwieraj pozycji bez SL.
5. Przy rozłączeniu łącz się ponownie co kilka sekund.
6. Najpierw testuj syntetycznie bez brokera. Faktyczny test DEMO wymaga osobnej zgody; tekst procedury testowej nie jest trwałym PASS Task2 i nie odblokowuje START.
