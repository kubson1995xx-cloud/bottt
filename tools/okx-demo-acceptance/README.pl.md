# Jednorazowy APEX / OKX EEA DEMO — odbiór feeType=0

**To nie jest uruchomienie strategii. LIVE jest niedostępne w tej kompozycji.**
Kod i testy offline nie dowodzą prawdziwego wykonania na rachunku. Przed pierwszym prywatnym wejściem rodzic musi zakończyć niezależny przegląd tej spójnej granicy finansowej i wejścia sekretów. Zgoda użytkownika na jeden test DEMO już istnieje; ponowne wpisanie kluczy jest wymogiem technicznym, nie kolejną zgodą na każdą operację.

## Jednorazowy zapis i późniejsze użycie

Po niezależnym odbiorze, w bezpośredniej interaktywnej sesji SSH jako **ubuntu** na tym serwerze:

```sh
/home/ubuntu/.local/bin/apex-okx-demo-acceptance --save-credentials
```

Dokładnie trzy ukryte pola: **API Key, Secret Key, Passphrase**. Bez gwiazdek; Enter zatwierdza, Ctrl+C przerywa. Klucze tylko z **Demo Trading → Demo Trading API**, Read + Trade, bez Withdraw; ten sam zestaw i obowiązująca IP allowlist. Nigdy nie wklejaj ich do czatu, argv, środowiska ani plików tekstowych.

Zapis nie łączy się z OKX i nie tworzy ownera/stanu finansowego/zleceń. Natywne `systemd-creds --user --with-key=host` szyfruje rekord AES-256-GCM związany z użytkownikiem i hostem: `/home/ubuntu/apex-private/okx-demo-credentials/credentials.cred` (katalog 0700, plik 0600). Pierwszy zapis nie nadpisuje istniejącego rekordu.

Po zapisie agent/użytkownik może bez TTY i bez ponownego wpisywania wykonać:

```sh
# Tylko lokalny odczyt/validacja szyfrogramu, NIE potwierdzenie kluczy w OKX:
/home/ubuntu/.local/bin/apex-okx-demo-acceptance --credentials-status
# Wyłącznie discovery: time/config/balance/instruments GET; bez ownera i zleceń:
/home/ubuntu/.local/bin/apex-okx-demo-acceptance --check-connection
# Wyłącznie wcześniej autoryzowany, skończony test DEMO opisany niżej:
/home/ubuntu/.local/bin/apex-okx-demo-acceptance
```

**Wywołanie bez argumentów może przejść do testowych zleceń DEMO**, jeżeli wszystkie istniejące bramki przejdą. `--check-connection` nigdy tego nie robi. Diagnostyka zapisuje osobny prywatny raport `connection-reports/<runId>.json` z tożsamością DLL i etapami GET; nie zmienia `last-report.json`, baseline ani markerów finansowych. Ukończona diagnostyka nie jest ukończonym testem transakcji ani pełną zgodą finansową.

Jawne zarządzanie, zawsze bez sieci/handlu:

```sh
/home/ubuntu/.local/bin/apex-okx-demo-acceptance --replace-credentials
/home/ubuntu/.local/bin/apex-okx-demo-acceptance --forget-credentials
/home/ubuntu/.local/bin/apex-okx-demo-acceptance --help
/home/ubuntu/.local/bin/apex-okx-demo-acceptance --identity
```

`--replace-credentials` pyta o trzy nowe ukryte pola bez wyświetlania starych. Błąd przed commitem zachowuje stary rekord; `COMMIT_UNCERTAIN` oznacza zakaz ślepego retry i kontrolę statusu pod tą samą blokadą. `--forget-credentials` usuwa wyłącznie własny szyfrogram, nie raport/ledger/markery/klucz hosta. To nie fizyczne secure erase ani odwołanie API key w OKX. Aktywny proces finite trzyma wspólną blokadę: zarządzanie odmawia, nie zabija go i nie unieważnia potajemnie jego już załadowanych kluczy.

Brak/uszkodzenie/niedostępność vault kończy się STOP, bez fallbacku do TTY, automatycznej rotacji ani prywatnego retry. Istniejący stan finansowy odmawia nowego testu **przed odszyfrowaniem i siecią**. Nie resetuj go ani markerów, aby powtórzyć zakup. Zapis kluczy nie daje LIVE/START, nie uruchamia schedulera/strategii i nie pozwala na dodatkowy zakup po Unknown.

Szyfrowanie chroni plik at-rest, nie przed root, procesem tego samego UID, pełnym przejęciem hosta, kopiami pamięci/native/framework, rollbackiem ważnego szyfrogramu lub nagrywaniem klienta SSH. Bez gwarancji TPM/secure erase. Odbiór prywatny OKX oraz Windows pozostają oddzielnymi bramkami.

## Jedyna dopuszczona sekwencja

1. Świeże podpisane discovery i kontrola czasu/DEMO/EEA, UID/main UID, cash-SPOT, Read + Trade, IP allowlist i wyłączonych pożyczek. Rachunek może mieć wiele walut. Nominacja sprawdza finansowane EUR, następnie USDC, każdą walutę osobno: rzeczywisty dostępny kapitał, obsługiwany nieposiadany base, własną grupę opłat instrumentu i najmniejszy poprawny round trip. Pierwsza waluta z wykonalnym kandydatem wygrywa; w jej obrębie minimalny debet, potem porządek identyfikatora. Inne waluty nie finansują testu.
2. Istniejący mapper i obie prywatne subskrypcje zapisują **pełny** snapshot w sealed baseline tego samego durable Naval owner/FileStore. Wyłącznie typowany, związany z sesją wariant finite-demo pozwala wyłączyć pozostałe salda z alokacji. Nie są equity/kapitałem ryzyka ani inventory APEX; nie wolno ich wydawać ani sprzedawać. Finite katalog zawiera jeden wybrany instrument Bravo z własnym harmonogramem opłat, bez wymagania posiadania/handlu BTC lub wspólnej grupy innych aktywów. Zwykły Desktop/START i jego katalog pozostają bez zmian. Nigdy reset Desktop/cTrader/innych katalogów.
3. Testowy base musi być początkowo nieposiadany (brak wiersza albo jawne zero bez zobowiązań). Ilość to **najmniejszy lot, którego własna ilość po prowizji bazowej pozwala sprzedać co najmniej minSz**, nie zwiększanie po błędzie. Minimum absolutne, minimum round-trip, oczekiwany pył i faktyczne ilości wynikają z zachowanych parametrów i rezerwacji. Debet pojedynczego kupna ≤ **10 jednostek rzeczywistego EUR albo USDC**, bez ich sumowania lub kursu 1:1. Limity ryzyka odnoszą się wyłącznie do nominowanej alokacji quote, nie do całego wielowalutowego rachunku.
4. Najpierw jeden własny **post-only buy z żądanym SL/TP**, następnie jedna dokładna anulacja. ACK nie zwalnia rezerwacji. Dopiero potwierdzony terminalny `canceled`, zero fill/fee i zgodne świeże salda dopuszczają kolejny krok. Jakikolwiek fill, Unknown, obca aktywność lub brak dowodu zatrzymuje planowany drugi zakup. Fill post-only może pozostawić niechronioną resztę; raport pokazuje ją wprost, bez zgadywania likwidacji.
5. Jeden FOK buy z żądanym SL/TP, indywidualne fille i rzeczywiste signed fee/currency → istniejąca księgowość netto. feeType=0: prowizja kupna w base, sprzedaży w quote. feeType=1: oba w quote. Brak/zła/trzecia waluta opłaty, rebate, przekroczenie harmonogramu i konflikt nie są zerem ani przewalutowaniem.
6. Istniejący workflow wyjścia: anulowanie **własnej** ochrony, obserwacja dokładnego terminalnego algo, świeża cena, jeden FOK sell do własnej ilości netto zaokrąglonej w dół do lotu. Zachowane prywatne limity i bieżące pasmo cenowe. Dla limitów USD używany jest oddzielny, timestampowany indeks BASE-USD (nie fikcyjna para po delistingu i nie peg USDC/USD); limity USDT mają osobną książkę referencyjną.
7. Końcowe uzgodnienie indywidualnych filli, bills, oryginalnych order IDs i świeżych sald. **STOP, rozbrojenie, scheduler nigdy nie uruchomiony.** Pył pozostaje dokładną, własną ekspozycją, nie „zerem”. Jego protection gap nadal blokuje START; finalność rozliczonej sprzedaży jest osobnym faktem.

Nie ma automatycznego retry mutacji, nowego/replacement buy po Unknown ani automatycznego wznowienia po restarcie. Odczyt uzgadniający ma maksymalnie dwie próby na etap. Plan nowego ryzyka ma trzy minuty od utworzenia; cały proces ma sześć minut, wpisanie pól dwie minuty, pojedynczy transport własne skończone budżety. Każde zlecenie używa nigdy niepowtarzanego, trwałego clOrdId, pre-send intent i finalnego fence DEMO/konto/sesja/rozmiar/STOP. Limity Naval **0,5% / 1,5% / 5%, cash ≤1×, SL/TP** pozostają.

Ochrona jest warunkowa po fill; nie jest gwarancją atomiczności ani ceny. Po anulowaniu ochrony i błędzie sprzedaży mogą pozostać niechronione aktywa. **STOP nie oznacza likwidacji.** Po błędzie raport i stan są podstawą ręcznego przeglądu dokładnych ID; ewentualne ograniczone recovery nie polega na ponownym uruchomieniu zakupu.

OCO może mieć zaobserwowany rozmiar gross, nie post-fee net. Runner nie zakłada gwarancji net: zachowuje rzeczywisty stan/rozmiar i `PROTECTION_GAP`, anuluje wyłącznie własny algorytm przed własną sprzedażą. Nieposiadany początkowo base jest obowiązkowy, aby gross ochrona nie mogła użyć starych aktywów.

### Kandydat balance-schema: tylko diagnostyka, nie zgodność REST

Rzeczywisty run lokalizuje `UNSUPPORTED_PROPERTY` w `balance.details`, lecz odrzucona nazwa i body nie zostały zachowane. Świeży oficjalny przykład i tabela REST mają wszystkie 48 pól już obsługiwanych przez parser; nie wykazano brakującego pola ani podstaw do rozluźnienia schematu.

Przy tej jednej odmowie opcjonalne `Payload.UnsupportedProperties` opisuje **wyłącznie pierwszy odrzucony obiekt details**, nie całe konto: `Name`, `DocumentedType`, faktyczny rodzaj JSON `Actual` i `Count`. Zamknięty zestaw nazw obejmuje tylko `coinUsdPrice` i `maxSpotInUseAmt`, udokumentowane w przykładzie i tabeli kanału **account WebSocket**, a nie w REST balance. Te nazwy nadal są niedopuszczalne w REST; ich typ `String` jest informacją dokumentacyjną, nie walidacją wartości ani prawem do przyjęcia. Pozostałe nazwy, również inne wielkości liter, są grupowane jako `<unknown>` według rodzaju JSON. Nie kopiujemy nazw arbitralnych, wartości, indeksów wierszy ani zagnieżdżonej treści. Maksymalnie 18 grup; bez truncation.

Następny pojedynczy, rodzicielski `--check-connection` może ustalić, czy pierwszy odrzucony obiekt zawiera te dokładne znane nazwy oraz ile ma innych nieobsługiwanych właściwości danego typu. **Nie potwierdzi automatycznie zgodności REST i nie odkryje arbitralnej nieznanej nazwy.** Dalsza odmowa `<unknown>` pozostawia przyczynę nierozwiązaną. Sukces GET, zgoda finansowa i transakcje pozostają osobnymi bramkami. Kandydat jest w nowym immutable katalogu balance-schema; nie instaluje ani nie zmienia produkcyjnego launchera i nie wymaga ponownego wpisywania zapisanych kluczy.

### Konkretne odmowy rachunku
- `DISCOVERY_INVALID_PAYLOAD`: `discoveryDiagnostic.Diagnostic` zachowuje zamknięty endpoint, faktycznie odebrany `HttpStatus` (albo null przed odpowiedzią), zweryfikowany `OkxCode` (albo null), `Phase` i `LastCompletedStage`. `ConfigAuthenticated` oznacza ukończone ścisłe parsowanie podpisanego config; `AccountSafetyValidated` oznacza przejście lokalnej kontroli konta, nie zgody finansowej ani Trade. `Payload` podaje wyłącznie zamknięte `Reason`, `Path`, `Expected`, `Actual`: znane nazwy schematu i `[]`, nieznane nazwy jako `<unknown>`, klasy rodzaju/krotności, nigdy wartości odpowiedzi. Etapy rozróżniają UTF-8, składnię JSON, duplikaty/tekst, envelope, schema, wartości oraz semantykę czasu/UID/sald/instrumentów. Nie zapisujemy exception messages/paths, UID, IP, poświadczeń, fingerprint ani raw body. Błąd zatrzymuje kolejne GET i nie tworzy ownera. Brak danych w starym raporcie nie dowodzi, który endpoint zawiódł; utraconej odpowiedzi nie odtwarzamy i nie zgadujemy przyczyny. HTTP 2xx nie jest samo w sobie dowodem OKX `0`.
- `DISCOVERY_UNSAFE_ACCOUNT`: zachowany kod ogólny. `discoveryDiagnostic.Diagnostic.AccountSafety` w prywatnym raporcie zawiera wszystkie osiągnięte powody: `PERMISSION_NOT_ALLOWED`, `ACCOUNT_LEVEL_NOT_1`, `AUTO_LOAN_ENABLED`, `SPOT_BORROW_ENABLED`, `SPOT_BORROW_AUTO_REPAY_ENABLED`. Zamknięty `PermissionProfile`, poziom `1`–`4` albo `UNAVAILABLE` i trzy flagi opisują wyłącznie poprawnie sparsowany config po kontroli UID. Nieznane teksty nie są kopiowane. Endpoint, rzeczywisty status HTTP i OKX `0` pochodzą z tego odczytu, nie z domysłu. Odmowa zachowuje te fakty także przy `Observation=null`, przed balance/instruments/ownerem/zleceniami. To nie diagnozuje wstecz starych raportów z `Diagnostic=null` ani nie upoważnia do zmiany ustawień konta.
- `FINITE_NO_FUNDED_EUR_OR_USDC`: brak znanego dodatniego dostępnego EUR/USDC.
- `FINITE_NO_UNHELD_SUPPORTED_BASE`: brak obsługiwanego nieposiadanego base; posiadany BTC nie jest automatycznie sprzedawany.
- `FINITE_BALANCE_INVALID_OR_ENCUMBERED`: duplikat/nieznana wartość, zobowiązanie, początkowe zamrożenie lub niepoprawny timestamp; brak domyślnego zera.
- `FINITE_NO_FEASIBLE_ROUNDTRIP_FOR_FUNDED_QUOTES`: prywatne `selection.operands` zawiera poszczególne instrumenty, rzeczywiste parametry, minimalny lot/debet, cap/depth lub brak gotówki/ryzyka. Nie sumujemy niewystarczających walut.
- `FINITE_NOMINATED_SPEC_OR_FEES_CHANGED`: świeży baseline różni się od nominacji; brak automatycznej zamiany instrumentu po utworzeniu ownera.
- `FINITE_EXCLUDED_BALANCE_CHANGED` / `BROKER_BALANCE_DISCREPANCY`: świeże pełne salda nie zgadzają się ze snapshotem i wyłącznie raz zaksięgowanymi własnymi fillami.

Parser nie usuwa wierszy zerowych. Wszystkie początkowe wiersze, także zero, muszą pozostać obecne. Nowy wiersz testowego base jest dozwolony tylko dla dodatniego udowodnionego własnego inventory. Nie normalizujemy brakujących/extra walut do zera. Oryginalne `uTime` to czas zmiany salda, a nie zegar świeżości odczytu. Wyłączone salda muszą pozostać ekonomicznie identyczne; własne zamrożenie quote jest ograniczone do dokładnego aktywnego pending buy, a własne base do nabytego inventory. Nieznane zamrożenie lub obca aktywność zatrzymuje nowe ryzyko.

## Raport i granice prywatności

Prywatny wynik: `/home/ubuntu/apex-private/okx-demo-acceptance/last-report.json`.

Raport zawiera run ID/czasy/complete, dokładne ordId/clOrdId/tradeId, stany, ceny/ilości/signed fee/waluty, początkowe i ostatnio obserwowane salda, rozdzielone gross/fee/net legs, anulacje, pył/pending/protection/Unknown, numery HTTP/OKX i hashe faktycznych DLL. UID i salda zostają w prywatnym raporcie, nie w Git ani publicznym dowodzie. Brak odpowiedzi/parsowalnego kodu ma wartość unknown, nigdy domyślne zero. Kopiowanie raportu poza prywatny katalog wymaga usunięcia prywatnych danych.

Launcher czyści środowisko, wyłącza diagnostykę .NET/core dumps; proces ustawia nondumpable i odmawia aktywnego swapu/debug tracing. Zapis/zamiana wymaga prawdziwego controlling SSH TTY z foreground process group; późniejsze użycie zapisanych poświadczeń nie wymaga TTY. Brak proxy, redirectów, dowolnych signed endpointów, funding/withdraw/borrow. Prywatne ścieżki są sprawdzane przed otwarciem, bez naprawiania nieznanych uprawnień; symlink/hardlink/FIFO lub zbyt szerokie tryby odmawiają. Całe własne bufory wejściowe są zerowane. Nie obiecujemy erasure wszystkich kopii framework/TLS, ochrony przed root/tym samym UID ani nagrywającym klientem SSH. SIGKILL/awaria hosta nie daje gwarancji wykonania finally lub przywrócenia TTY.

Poprzedni `/home/ubuntu/.local/bin/apex-okx-demo-test` i `/home/ubuntu/projects/apex-okx-ssh-probe/probe.py` pozostają niezmienione i GET-only.

## Warstwy i odtworzenie offline

`launcher → LinuxInput.Harden + LinuxDemoCredentialVault → EphemeralCredentials (transfer własności bez kopii/stringów) → produkcyjny OkxEnrollmentDiscoveryClient → [--check-connection: raport READ-ONLY/STOP i koniec] → oddzielny finite runtime/account service → produkcyjne REST/WS → OkxBrokerObservationMapper → NavalBrokerBootstrap/Session/CoreStateOwner/FileStore → OkxOperationalTradingService/FiniteDemoSequence → risk reservation + durable IDs/intents + EnterBrokerDispatch → produkcyjny signer/parser/transport → istniejąca reconciliation/economics/protection/ExitOwned → TransactionReport`.

Nie ćwiczy to Windows GUI/CredUI/DPAPI, EXE użytkownika ani scheduler/strategii. Testy sieciowe offline zastępują tylko przewód syntetycznym HTTP/WS peerem; rzeczywista kompozycja launchera nie używa `ForTests` ani peerów.

```sh
/home/ubuntu/.dotnet/dotnet restore tools/Apex.Okx.DemoAcceptance/Apex.Okx.DemoAcceptance.csproj
/home/ubuntu/.dotnet/dotnet build tools/Apex.Okx.DemoAcceptance/Apex.Okx.DemoAcceptance.csproj --no-restore
python3 tools/okx-demo-acceptance/test_privacy.py \
  tools/Apex.Okx.DemoAcceptance/bin/Debug/net8.0/Apex.Okx.DemoAcceptance.dll \
  /home/ubuntu/apex-deliveries/okx-demo-fee0/privacy-recheck.json
```

`--offline-input-check` i `--offline-input-error` są wyłącznie testem wejścia procesu: nigdy nie tworzą komponentów brokerskich, nie robią sieci i nie zapisują stanu. Nie potrafią odblokować produkcyjnej symulacji/START.
