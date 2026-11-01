# Naprawa testu DEMO dla klucza BOT — raport wykonania

## Stan końcowy
**Dostarczono nowy zweryfikowany Windows ZIP w wąskim zakresie testu salda DEMO.** Build/CI `41357352828` SUCCESS, źródła `1de3ad60839d16ef24cd7b3f400e5c6cc3e3a03e`, produkt `68181a21a40a326b40d76680924f839a4bbdab8b`. Artefakt i polska instrukcja: `/home/ubuntu/apex-deliveries/okx-demo-bot-read-fix/delivery/`. ZIP SHA `075e3618ca65f99e6186682f151d6cd0c9f73ea93df60434f33239f039babad8`; archiwum pozostaje niezmienione. Parent pozostaje właścicielem niezależnego odbioru/HANDOFF. Secure-positive i prawdziwe prywatne DEMO nadal zewnętrzne, niezweryfikowane.

## Punkt wejścia
- Baza: `3ceb2b135952e71eb540cfadea0ff42f246f13c3`, produkt `800cab704f843d975098fd48966f8b0a64058cd6`, gałąź `feature/apex-naval-command`.
- Przeczytano brief, raport wykonania, boundary review i independent review poprzedniej dostawy. Potwierdzony problem: nowy TEST używa ścisłego czytnika `read_only`; klucz BOT `read_only,trade` uwierzytelnia config, lecz nie dochodzi do balance. Jeden niezmienny scope na środowisko uniemożliwia późniejszą zamianę klucza przez UI.
- Zakres: wyłącznie transport `OkxDemoBalanceTest` przez istniejący GET-only `OkxExecutionObservationClient`, prywatny ograniczony fence, dokładna walidacja odpowiedzi, testy, zgodne wskazówki i nowy Windows ZIP. Stary czytnik i dopuszczenie handlu pozostają bez zmian.
- `HANDOFF.md` i ewentualny `progress.md` należą do rodzica; bez edycji. Bez dzieci, innych worktree, prywatnych wywołań brokera, transakcji, deploy/merge i danych użytkownika.
- Stary ZIP `919acb78c8e34f61fe3b46edfa36058ab85e909509a8612d64d8581bc5912969` pozostaje niezmienny.
- Aktywne: minimalny odczyt kodu i przygotowanie utrwalonego RED wraz z oryginalnym DLL/input closure przed przebudową.

## Checkpoint — RED i pierwsza kompilacja
- Zachowano pierwotny istniejący runtime i 3 kluczowe źródła przed pierwszą przebudową: `/home/ubuntu/apex-deliveries/okx-demo-bot-read-fix/00-original`.
- `02-red-execution/red.trx`: 1 FAIL / 6 PASS. Dokładny przypadek BOT `read_only,trade` osiągnął porównanie oczekiwanego `DEMO_TEST_OBSERVED_READ_ONLY_STOP` z rzeczywistym `DEMO_TEST_PRIVATE_AUTH_UnsafeAccountConfig`. Runtime/test closure i source-map zachowane przed wykonaniem; nic nie nadpisano.
- Nowy lokalny request/fence + istniejący observation transport kompiluje się: 0 warning / 0 error. Nie zmieniono adaptera ani starego czytnika. Pierwszy GREEN jeszcze oczekuje.
- Nowa fixture zachowuje oryginalne wartości UID/main UID/cash/available/permission; uzupełnia odpowiedź config o pola `spotBorrowAutoRepay`, `type`, `spotRoleType`, `kycLv`, których wymaga istniejący parser observation. To jawna różnica fixture względem pierwszego RED, nie identyczność całego pliku.


## Checkpoint — scoped GREEN i rzeczywisty błędny lane
- `04-green-execution/green.trx`: 36/36 PASS nowego transportu, prawdziwe podpisy i syntetyczny handler, bez sieci brokera.
- Finalna kompilacja po usunięciu nieużywanego helpera i aktualizacji wskazówek: 0 warning/error. `06-final-execution/affected.trx`: 84 PASS / 7 FAIL. Wszystkie 37 przypadków nowego testu PASS. Siedem FAIL to omyłkowo wybrane Windows-only DPAPI/ACL testy na Linux (`Native Windows required; never silently skip`); brak podstaw do przypisania ich produktowi. Wynik nie został przemianowany na green. Windows wykona je natywnie.
- Panel: 29/29 PASS; `git diff --check` PASS. Nie powtórzono już zaliczonych 84 przypadków tylko dla zmiany koloru wyniku. Wymagany świeży Windows lane pozostaje.
- Autorski focused review zapisany w `okx-demo-bot-read-fix-review.md`. Parent dokonuje wąskiego niezależnego odbioru.
- Usunięto wyłącznie nieużywany `OpenDemoReadMaterial`, istniejący accepted reader/adapter/admission/control/activation/Windows approval bez zmian. Wskazówki podają docelowy klucz DEMO BOT i ostrzegają o braku UI rotacji.


## Checkpoint — commit i Windows CI
- Produkt/testy/wskazówki: commit `68181a21a40a326b40d76680924f839a4bbdab8b`, push na jedyną autoryzowaną gałąź `feature/apex-naval-command`; remote odczytano z identycznym SHA.
- Manualny `ui-repair-delivery` run `41356994133` jest IN PROGRESS na tym SHA. Oczekiwane: nowy self-contained ZIP/launcher, świeże affected Windows testy i real extracted-EXE browser/native smoke. Bez powtarzania niezmienionego 120 s secure-timeoutu.
- Skrypt wymaga niezmienionych provider/activation/native-wait źródeł względem zaakceptowanego `3ceb2b1` przed ponownym użyciem ich dowodów. Engine/platform/backtester reuse nadal sprawdza dokładne wejścia poprzedniej paczki.
- Początkowe `git add` odmówiło nowych raportów w ignorowanym `.superpowers`; dodano jawnie tylko te dwa raporty przez `-f`. HANDOFF pozostał nietknięty przez wykonawcę. Brak innych zmian roboczych produktu.


## Checkpoint — CI setup FAIL, narzędziowa poprawka i identyczny wire RED/GREEN
- Run `41356994133` zakończył się FAILURE przed buildem: shallow checkout nie zawierał bazowego `3ceb2b1` dla nowej kontroli reuse (`fatal: bad object`). Nie wykonano product testów ani nie utworzono ZIP. Pełny failed log zachowany w katalogu dostawy. Upload evidence również odmówił, bo guard wykonał się przed utworzeniem katalogu; nie ma ukrytego TRX.
- Minimalna poprawka tylko w autoryzowanym jobie delivery: checkout `fetch-depth: 0`, żeby realnie porównać wymagany historyczny source. Produkt pozostaje `68181a2`.
- Dodatkowy porównawczy probe `same-wire/` wykonany poza repo: ten sam plik Program.cs i te same account.json/balance.json dla zachowanych oryginalnych DLL i finalnych DLL, z kompilacyjną adaptacją wyłącznie starej/nowej sygnatury factory. Oryginał exit1: `UnsafeAccountConfig`, config GET1/balance0. Nowy exit0: `OBSERVED_READ_ONLY_STOP`, config1/balance1, cash100/available90. W obu prawdziwy HMAC i GET-only = true. JSON konta/salda ma identyczne hashe między lane; publiczny czas jest celowo bieżący. Retained-runtime i pre-run map w obu lane. To zamyka różnicę fixture pierwszego RED, nie zastępuje jego TRX.


## Checkpoint — poprawiony delivery uruchomiony
- Narzędziowy commit `448e4847815ea03be2d0977a3fd8fb2710907276`, remote zgodny. Różnica `68181a2..448e484` w src/frontend/tests jest pusta.
- Manualny run `41357155189` IN PROGRESS na `448e484`, po naprawie checkout. Zachowane poprzednie failure i same-wire RED/GREEN; żadnego product rerun nie przypisano naprawie produktu.


## Checkpoint — drugi setup FAIL i dokładna naprawa reuse
- Run `41357155189` FAILURE przed buildem: pin/CRC/manifest starego ZIP przeszły, guard zmian odmówił `tests/Apex.Tests/OkxNativeControlTests.cs`. To wcześniej zaakceptowana zmiana testu `NATIVE_CANCELLED` → `METADATA_USER_CANCELLED` z `3f0d731`, nie nowa zmiana produktu. Zachowano pełny log i mały rzeczywisty artifact evidence; nie ma testowego TRX ani nowego ZIP.
- Porównanie wszystkich 31 zmienionych wejść względem `d1f928e` wykazało dokładnie ten jeden brak allowlisty. Rozszerzono tylko tę konkretną nazwę testu w builderze; żadnego katalogowego wildcard ani dopuszczenia engine delta.
- `unchanged-boundary-reuse.json`: 31 istotnych runtime/test wejść accepted reader/adapter/admission/activation/secure approval zachowuje dokładne bajty lub jawny checkout CRLF starego ZIP `919ac...`; związano 3 stare dowody protected-timeoutu przez SHA. Historyczny runtime proof, nie nowy secure-positive.


## Checkpoint — trzeci delivery
- Commit narzędziowy `1de3ad60839d16ef24cd7b3f400e5c6cc3e3a03e`, remote zgodny; runtime nadal `68181a2`.
- Manualny Windows run `41357352828` IN PROGRESS na `1de3ad6`, po obu wąskich poprawkach setup/reuse. Żadnego nowego private call ani secure-timeout rerun.


## Końcowy checkpoint — nowy ZIP i Windows GREEN
- Run `41357352828` SUCCESS, exact source `1de3ad60839d16ef24cd7b3f400e5c6cc3e3a03e`; remote wynik, head SHA i pobrany artifact odczytano. Żadnych zmian produktu po `68181a2`. Podczas oczekiwania lokalny watcher przekroczył limit narzędzia 420 s; CI nadal działało i następny odczyt potwierdził jego rzeczywisty SUCCESS, nie failure produktu.
- Nowy ZIP: 43 775 687 bajtów, SHA `075e3618ca65f99e6186682f151d6cd0c9f73ea93df60434f33239f039babad8`; EXE `77949565c8d9190612298797649c0e8ede7411a8aaf614e3903d5eafb02daa14`. Launcher jest obecny. Bezpieczna niezależna ekstrakcja: CRC/ścieżki/kolizje/pełny manifest PASS, 52 pliki regularne. Stary ZIP `919ac...` pozostaje niezmieniony.
- Świeże Windows wyniki, nie fikcyjna suma: Source78/78; extracted host113/113; Package6/6; Browser2/2; affected enrollment/read90/90 (w tym wszystkie 37 nowych testów odczytu oraz siedem native DPAPI/ACL przypadków wcześniej błędnie uruchomionych na Linux); EnrollmentSource2/2; UiRepair2/2; Inventory1/1; Upgrade1/1; Navigation1/1. Wszystkie bez failure/skip. Self-contained smoke PASS, bez machine .NET/VC; rzeczywisty Windows Server 2025 Datacenter 10.0.26100.0, nie fizyczny Windows11.
- Native helper potwierdza trzy maskowane pola, walidację, syntetyczny roundtrip, CurrentUser DPAPI oraz config/runtime reopen, cancel/partial/no-overwrite. Jawnie `secureApprovalPositiveVerified=false`, `authorityCreated=false`, `networkCalled=false`. Nie przypisano temu pełnej produkcyjnej zgody Windows.
- 531/531 wejść sealed źródeł/konfiguracji/testów/narzędzi ma zgodność z aktualnymi bajtami: 14 raw, 517 jawny native CRLF. `apex-build.json` wiąże dokładny SHA manifestu. Final binary/EXE i test closures pozostają osobno zachowane. Historyczne engine1201/platform126/backtester168 oznaczono REUSED, nie świeżo wykonane. Niezmieniony protected timeout wykorzystano z dokładną mapą wejść i hashami, nie powtarzano 120 s modala.
- Dowody: `/home/ubuntu/apex-deliveries/okx-demo-bot-read-fix/verification-results.json`, `final-source-binding.json`, `unchanged-boundary-reuse.json`, `same-wire/`, `windows-41357352828/`. Czytelna nowa dostawa: `delivery/URUCHOMIENIE-OKX-PL.md` + ZIP/sidecar i focused review/report. Parent powinien sprawdzić wąski diff, oryginalny RED, same-wire RED/GREEN i manifesty; niezależny pełny child review nie był uruchamiany.
- Zachowano wszystkie realne wcześniejsze niepowodzenia: oryginalny behavioral RED, omyłkowe Linux-native7, shallow-checkout CI setup i brak pojedynczego accepted-test w reuse allowliście. Ostatni CI jest osobnym rzeczywistym integrated SUCCESS, nie sumą wcześniejszych targeted replay.
- Zakończono pracę nad artefaktem. Parent owns HANDOFF/progress/ostateczny odbiór. Nie wykonywano prywatnych połączeń, transakcji, bypassu zgody, merge/deploy, rotacji, nadpisania ani usunięcia danych użytkownika.

- Nadal zewnętrzne i niezweryfikowane: prawdziwe pozytywne zatwierdzenie Windows → produkcyjny formularz sekretów → zapis/restart oraz prywatny odczyt DEMO uruchomiony lokalnie przez użytkownika. Nie będą zastąpione atrapą.
