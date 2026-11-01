# Naprawa testu DEMO dla klucza BOT — raport wykonania

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

- Nadal zewnętrzne i niezweryfikowane: prawdziwe pozytywne zatwierdzenie Windows → produkcyjny formularz sekretów → zapis/restart oraz prywatny odczyt DEMO uruchomiony lokalnie przez użytkownika. Nie będą zastąpione atrapą.
