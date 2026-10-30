# OKX EEA — czytnik REST tylko do odczytu (Task5A)

**READ_ONLY / DEMO_REQUESTED / NO_BROKER_AUTHORITY.** To odizolowana biblioteka .NET 8, nie integracja uruchamiająca handel. Nie przyznaje kapitału, nie wystawia receipt/permit, nie odblokowuje START i nie zmienia odmowy non-Offline w Core. Nie ma zależności od Core, backtestera ani platform brokerskich.

## Co można odczytać

Jedyny adres to `https://eea.okx.com:443`. Produkcyjny handler jest własnością klienta: domyślna walidacja TLS/hosta, brak przekierowań, cookies i proxy. Nie można podać własnego adresu, HttpClient, handlera, metody ani surowego query w publicznym API.

Wszystkie żądania to GET bez body, z jednym `x-simulated-trading: 1`:

| Odczyt | Ścieżka `/api/v5/...` | Zakres |
|---|---|---|
| Czas | `public/time` | Publiczny; Unix ms, bez przesunięcia UTC+8 |
| Publiczny katalog | `public/instruments?instType=SPOT` | Publiczny; nie potwierdza dostępności na koncie |
| Konfiguracja | `account/config` | UID, uprawnienia, tryb konta, IP, borrow flags |
| Saldo | `account/balance` | Niefiltrowane saldo konta trading, nie funding ani wszystkie aktywa |
| Katalog konta | `account/instruments?instType=SPOT` | W tym `tradeQuoteCcyList`; nie zakładamy USDT ani listy dziesięciu aktywów |
| Zwykłe otwarte zlecenia | `trade/orders-pending` | Bez instType/instId/state; nie ukrywa obcej ekspozycji |
| Znane zlecenie | `trade/order` | Dokładne `instId` oraz `ordId`, nie samo clOrdId |
| Wykonania | `trade/fills`, `trade/fills-history` | SPOT; kursor billId, nie tradeId |
| Zmiany salda | `account/bills`, `account/bills-archive` | Bez filtrów waluty, typu i instrumentu; kursor billId |

Brak metod place/amend/cancel/algo, transfer, withdraw, deposit, borrow i settings. Ścieżki GET pod `/trade` są obserwacją, nie wykonaniem transakcji. Nie ma WebSocket, orders-history, fee-tier lookup ani trwałego recovery checkpoint.

## Użycie publiczne

```csharp
using Apex.Adapters.Okx;

await using var client = OkxEeaDemoReadClient.CreatePublic();
var time = await client.GetTimeAsync(cancellationToken);
var catalog = await client.GetPublicSpotInstrumentsAsync(cancellationToken);
if (time.Status != OkxReadStatus.Observed ||
    catalog.Status != OkxReadStatus.Observed)
{
    // Wyświetl wyłącznie ErrorCode/Endpoint/licznik; nie surowe body ani request.
    return;
}
```

Publiczny odczyt nie otwiera dostawcy sekretów i nie wysyła nagłówków `OK-ACCESS-*`. Prywatna metoda na takim kliencie zwraca `CredentialsUnavailable` przed siecią. Pusta lista sukcesu nie jest zastępstwem błędu.

Publiczny katalog może nie zawierać pola kontowego `tradeQuoteCcyList`: wtedy lista pozostaje pusta/nieznana; nie jest odgadywana z nazwy instrumentu. Prywatny katalog wymaga rzeczywistej listy w odpowiedzi.

## Prywatny odczyt: obecnie BLOCKED

**Nie ma bezpiecznie skonfigurowanych credentials, dlatego nie wykonano prywatnego smoke.** Ten etap nie tworzy konta/subkonta, klucza ani dostawcy sekretów. Nie wpisuj sekretów w czat, CLI, env, fixture, konfigurację projektu lub raport. Nie wpłacaj środków i nie przyznawaj Trade/Withdraw „dla testu”.

Przyszła, osobno autoryzowana lokalna kompozycja ma dostarczyć `IOkxDemoSecretProvider` do `CreateDemoRead(provider, new OkxReadScope(bindingId, expectedUid))`. `OkxDemoCredentialLease.FromSecretStore` to port dla zaufanego magazynu sekretów, nie formularz do wprowadzania kluczy. Jedna lease jest otwierana leniwie i zamrożona na życie klienta. Owned kopia bajtów sekretu jest zerowana podczas Dispose; nie można zagwarantować wymazania stringów .NET ani kopii utrzymywanych przez dostawcę.

Przed innymi prywatnymi odczytami klient pobiera czas i podpisaną konfigurację. Wymaga zgodnego BindingId i dokładnego ExpectedUid, `perm="read_only"`, niepustego IP, `acctLv="1"`, `autoLoan=false` i `enableSpotBorrow=false`. Brak flagi, nieznane/szersze uprawnienia lub zmiana konfiguracji powodują odmowę. Nie dostosowuje ustawień konta.

Operator musi poza tym potwierdzić pochodzenie klucza z Demo Trading, uprawnienie Read-only i stały dozwolony egress IP. Wspólny host DEMO/LIVE, wysłany nagłówek ani zwrócony config **nie poświadczają** Demo-key provenance lub rzeczywistego członkostwa IP na allowliście. Brak fallbacku regionalnego, LIVE, zmiany klucza i przyznawania uprawnień po błędzie.

Klient nie loguje żądań, nagłówków, `msg` serwera ani tekstu/inner exceptions dostawcy. Wykryta aktywna instrumentacja `HttpHandlerDiagnosticListener` lub `System.Net.Http` EventSource blokuje prywatny odczyt kodem `UnsafeHttpDiagnostics`, również przed kolejną próbą. Nie włączaj zewnętrznego logowania requestów/sekretów. To zabezpieczenie przed normalną instrumentacją, nie sandbox przeciwko wrogiemu kodowi w tym samym procesie ani wyścigowi dołączania takiego kodu podczas wysyłki.

## Limity i błędy

- Prywatne wysyłki są serializowane w procesie po BindingId i po zwalidowanym UID, minimum 600 ms między startami. Publiczne minimum 100 ms. Brak koordynacji z innymi procesami/aplikacjami.
- Próba HTTP: 10 s; logiczny odczyt wraz z kolejką i retry: 30 s; capture: 120 s. Body maksymalnie 8 MiB, JSON depth 64, strona maksymalnie 100 wierszy, 20 stron na endpoint/pass, 20 unikalnych unresolved references.
- Do trzech prób GET: connection failure, timeout próby, HTTP 429/502/503/504 oraz OKX 50011/50004. Nowy timestamp i podpis dla każdej próby. Backoff 1 s, następnie 2 s, z jitter 0–250 ms; większy poprawny Retry-After tylko w pozostałym budżecie.
- Brak retry po anulowaniu, redirect, błędzie TLS, JSON, tożsamości, uprawnień lub odrzuceniu biznesowym. HTTP error nie staje się sukcesem przez body z code0. HTTP200/code!=0 nie jest sukcesem.
- `50101`, `50119`, `50123`: nie zmieniaj środowiska, regionu ani uprawnień. `50102`: batch jest zatruty; dopiero nowy jawny capture powtarza preflight. Nie zmieniamy zegara systemowego. Preflight wymaga RTT ≤2 s oraz offsetu względem midpoint ≤5 s — to konserwatywne bramki projektu.
- Anulowanie przez caller rzuca `OperationCanceledException` także przy providerze, limiterze, HTTP, body, stronicowaniu i delay. Timeout zwraca rozróżniony błąd, nigdy Observed z pustą listą.

Parametry są typowane, query ma porządek ordinal i escaping RFC3986; podpis obejmuje dokładne wysłane `PathAndQuery`, UTC z milisekundami, `GET` i pusty body. Nie ma API `before`. Pending odrzuca begin/end. Tokeny zapytań są ograniczone do 1024 znaków i nie przyjmują control/surrogate characters. To walidacja wejścia, nie interpretacja ekonomiczna ID.

## Jak czytać snapshot

`OkxSnapshotReader.CaptureAsync` otrzymuje offset-zero UTC `[FromUtc, ToUtc)`, `from < to <= czas z preflight`, oraz unikalne unresolved `(InstId, OrdId)`. Każde wywołanie ma nowy losowy Generation. Poprzedni wynik nigdy nie jest używany jako świeży.

Kolejność: config/time → katalog konta → balance A + pełne pending A → znane order details → recent fills/bills + wymagane archive z inkluzywnym overlap → balance B + pending B + config B. Kursor pending to ordId, fills/bills to billId. Krótka strona nie kończy odczytu; koniec to obserwowana pusta strona lub przekroczona granica ts. Powtórzony kursor, cap i końcowy błąd pozostawiają jawnie częściowy wynik. Strony zapisują okno/kursor, liczbę wierszy, hash body oraz zakres ts/fillTime — tylko w pamięci. Nie ma automatycznego archiwum prywatnych body na dysku.

`Collected` znaczy „zaplanowane odczyty się zakończyły”, nie pełne pokrycie finansowe. Nawet stabilne bookends i poprawne równania pozostawiają `Coverage.Unknown` i `UnprovenEventContinuity`. `Gap` sygnalizuje przerwę/retencję, `Conflict` sprzeczną tożsamość/ekonomię. Różne saldo/pending A/B daje `Incomplete` i `SnapshotMoved`. Retencja ponad trzy miesiące kalendarzowe to Gap; retencja DEMO także wewnątrz tego okresu nie jest dowiedziona.

- ID i waluty są nieprzycinanymi, case-sensitive stringami; nigdy long/double. Exact decimal jest oddzielone od Raw; blank/null to unknown, nie zero. Nadmiar precyzji nie jest zaokrąglany.
- Fill jest unikalny po `(EEA, DemoRequested, uid, instId, tradeId)`, bill po `(EEA, DemoRequested, uid, billId)`. Ten sam klucz z inną ekonomią jest Conflict. Sortowanie fill po fillTime nie dowodzi kompletności; endpoint filtruje po ts.
- `CurrencyDiagnostics` porównuje cash A/B, sumę balChg i chronologię bal. `FillCashflows` opisuje signed base/quote cashflow i fee we właściwej walucie. Bill fee nie jest dodawane drugi raz. Rebate pozostaje dodatni; third-currency fee pozostaje osobną niebezpieczną obserwacją.
- Brak waluty, nieznany typ bill, liability/borrow, obce non-SPOT/cash pending i brak linków są jawnie oznaczone. Nie wyliczamy FX, opening inventory, P&L ani equity z totalEq.
- Brak balance row/order, pusty endpoint, not-found, terminal `filled` i accFillSz nie dowodzą zerowego inventory, braku fill, kompletności opłat, ochrony SL/TP ani bezpiecznego release.

Wynik jest detached/immutable, nie ma publicznego konstruktora snapshotu, członka Complete/receipt/permit ani konwersji do Core. Prywatny eksport jest jawny: `snapshot.ExportRedactedDiagnostics(optIn: true)` zwraca wyłącznie metadane, reason codes i liczniki bez UID/IP/binding, kursora oraz ekonomii. Zwykła serializacja snapshotu i import JSON są blokowane; nie serializuj oddzielnie DTO konta do logów.

## Weryfikacja lokalna

```sh
/home/ubuntu/.dotnet/dotnet test tests/Apex.Adapters.Okx.Tests/Apex.Adapters.Okx.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName!~OkxPublicSmoke' --logger trx
/home/ubuntu/.dotnet/dotnet build src/Apex.Adapters.Okx/Apex.Adapters.Okx.csproj -c Release --no-restore
```

Dedykowany publiczny smoke jest domyślnie **SKIPPED**, nie synthetic PASS. Włączenie `APEX_OKX_PUBLIC_READ_SMOKE=1` i filtra `FullyQualifiedName~OkxPublicSmoke` wykonuje tylko publiczny czas oraz katalog przez CreatePublic. Sukces publiczny nie jest prywatną weryfikacją DEMO ani dostępności instrumentów konta.

Oficjalne źródła regionalne, datę odczytu i hash dowodów zawiera `task-5a-okx-read-brief.md`. Raport implementacji i pomiarów: `.superpowers/sdd/2026-10-29-apex-naval/task-5a-okx-read-report.md`. Dalsze bramki: niezależny review rodzica, zaufana kompozycja/secure provisioning, osobno zatwierdzony prywatny read smoke, przyszły issuer/ciągłość WS/Core integration. Transakcja DEMO wymaga odrębnej wyraźnej zgody; LIVE pozostaje zablokowane.
