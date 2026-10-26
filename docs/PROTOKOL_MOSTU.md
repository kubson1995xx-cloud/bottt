# APEX Bridge Protocol v1

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
W obecnej wersji oznacza to **STOP i blokadę START oraz technicznego test-open w cTrader**, dopóki
osobna, zweryfikowana procedura zakresu/baseline nie zostanie wdrożona. Nie istnieje przełącznik
operatora ani pole JSON, które po stronie ścieżki cTrader podnosi to do potwierdzonego coverage.
Tylko lokalny źródłowy zbiór symulatora (`platform:"Symulator"`) i zaufany natywny runtime backtest
mogą mieć pełne pokrycie swoich syntetycznych danych. To nie odbiór Pepperstone.

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
- `uncertain` (lub brak odpowiedzi w 15 s) – stan nieznany. APEX **nie ponawia** zlecenia, tylko weryfikuje stan pozycji (po komentarzu z identyfikatorem sygnału) przez 90 s.

Kody `error` (wielkość liter bez znaczenia, nieznany kod = `Other`): `None, TechnicalError, BadVolume, NoMoney, MarketClosed, Disconnected, EntityNotFound, Timeout, UnknownSymbol, InvalidStopLossTakeProfit, InvalidRequest, NoTradingPermission, Other`. Błędy techniczne (`TechnicalError, Timeout, Disconnected, Other`) liczą się do limitu kolejnych błędów – po 5 bot wstrzymuje nowe wejścia.

### `marginResult`, `log`
`{"type":"marginResult","id":"…","margin":173.4}` – szacowany depozyt (null = nieznany → brak wejścia).
`{"type":"log","level":"info|warn|error","message":"…"}` – komunikaty widoczne w panelu.

## APEX → most

| Typ | Pola | Wymagania wobec mostu |
|---|---|---|
| `welcome` | `accepted, reason, hostVersion, label` | przy `accepted:false` zakończ połączenie |
| `subscribe` | `symbols[], tfs[], history{tf:liczba}` | wyślij historię (`bars` snapshot), potem bieżące notowania i nowe świece |
| `open` | `id, symbol, side, units, slPips, tpPips?, label, comment, maxSlipPips?` | **odrzuć, jeśli `slPips` ≤ 0**; ustaw SL/TP w zleceniu; etykieta i komentarz bez zmian; konto LIVE odrzuć, jeśli w moście nie zezwolono |
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
6. Przetestuj na symulatorze/DEMO: test wykonania w panelu (Diagnostyka → Test wykonania) musi zakończyć się „TEST ZALICZONY”.
