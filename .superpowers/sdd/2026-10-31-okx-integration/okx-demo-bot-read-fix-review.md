# Focused review — DEMO BOT read fix

## Zakres i wniosek
Autorski przegląd w osobnym przejściu, nie niezależny odbiór rodzica. Zmiana dotyczy wyłącznie jawnego testu salda, jego wskazówek i dowodów dostawy. Nie znaleziono materialnego obejścia dopuszczenia handlu w tym diffie. Status Windows/artifact jest zapisywany w raporcie wykonania, nie zakładany z kompilacji.

## Granica transportu
- `OkxDemoBalanceTest.RunAsync` przed utworzeniem transportu odmawia LIVE, wadliwego digest/bindingu i niezgodnego credential binding/provenance. Istniejący runtime wciąż wymaga zapisanej konfiguracji, DEMO i registry fingerprint; handler/control nie zmieniono.
- Jedyną instancją klienta jest istniejący strukturalnie GET-only `OkxExecutionObservationClient`. Wywołania to wyłącznie `ReadTimeAsync`, `ReadAccountAsync`, `ReadBalancesAsync`. Stały regionalny HTTPS, DEMO header, podpisy, diagnostyka, przekierowania, limity odpowiedzi i wspólne kwoty pozostają w niezmienionym adapterze. Brak WS, `OkxExecutionClient`, Connect/Observe authority, właściciela finansowego/store, baseline, Arm i START.
- Prywatny `ReadRequest` żyje tylko w tym żądaniu. Wymaga dokładnego DEMO scope/context, lokalnego fingerprint/provenance/bindingu, aktywnego tokena, UTC i monotonicznego budżetu 35 s. Po wyjściu zostaje zamknięty. Nie jest dostępny przez payload HTTP ani publikowany jako authority. Transport osobno sprawdza fingerprint rzeczywistego materiału przed podpisem i ostateczny fence przed send.
- Wykorzystano `OpenSigningMaterial(scope)`. Usunięto nieużywany `OpenDemoReadMaterial` po wyszukaniu wszystkich call sites; nie ma już dodatkowej konwersji string w tej warstwie. Istniejące napisy nagłówków i kopie wewnętrzne HTTP/CLR nie są gwarantowanie wymazywane — żadnej nowej obietnicy erasure.

## Semantyka obserwacji
- Publiczny czas jest sprawdzany przed pierwszym prywatnym GET: pojedynczy prawidłowy timestamp, RTT ≤2 s, skew midpoint ≤5 s. Błędy pozostają kodami, nie surowymi msg/exception.
- Uwierzytelniony dokładny UID i main UID muszą pasować przed balance. Poziom cash `1`, niepuste IP i wyłączone autoLoan/enableSpotBorrow/spotBorrowAutoRepay. Dokładnie `read_only` albo zestaw `read_only,trade` (obie kolejności); duplikaty, spacje, Trade-only, Withdraw i nieznane uprawnienia odrzucone. To nie sprawdzenie dedykowanego execution policy, fee policy ani gotowości strategii. Istniejące admission pozostaje niezmienione.
- Kwoty pochodzą z dokładnie jednego detail wybranej waluty. Brak wartości pozostaje null. Stare `uTime` nie jest podszywane pod świeżość salda; `ObservedUtc` to receive timestamp. Obserwacja jest oddzielna od kapitału/P&L; kod sukcesu nadal kończy się `READ_ONLY_STOP`.
- Linkowany token i 35 s pozostają, adapter ma dodatkowe 10 s per request. STOP/emergency/disconnect nadal anulują pending bez kolejki i blokują spóźnioną publikację przez generation/token. Kontrolera nie zmieniono.

## Dowody i kwalifikacje
- Oryginalne DLL i closure zachowano przed buildem. Reprodukcja RED: BOT odmówiony jako `UnsafeAccountConfig`; 1 FAIL, 6 PASS.
- Pierwszy scoped GREEN 36/36. Finalny lokalny zestaw ma 37/37 dla nowego testu; cała próba 84 PASS / 7 FAIL, bo omyłkowo objęła siedem jawnie Windows-only native DPAPI/ACL przypadków na Linux. To błąd wyboru lane, nie przypisywany produktowi ani ukrywany. Windows ma wykonać natywne przypadki. Panel 29/29 PASS.
- Fixture config rozszerzono o istniejące wymagane pola parsera observation; oryginalne wartości UID/main UID/permissions/cash/available zachowano. Pełnego pliku fixture nie nazywamy identycznym z RED. Własny peer sprawdza prawdziwy HMAC i dokładne GET-y, a nie udaje brokera.
- Nowe testy obejmują safe read-only/BOT, UID/main UID, permissions, borrowing, IP/cash, time/skew/RTT, nullable/currency/cardinality/API/malformed kwoty, timeout/cancel, material/fence substitution, final pre-send cancellation, zamknięcie/budżet i wspólne kwoty dwóch klientów.
- Stary accepted reader, adapter, account/execution admission, activation, control i secure Windows approval są niezmienione. Chroniony timeout i niezmienione engine/platform/backtester mają być ponownie wykorzystane z wiązaniem wejść zamiast udawania świeżego full suite.

## Ograniczenia obowiązujące po fixie
Pełne pozytywne chronione zatwierdzenie Windows → produkcyjny formularz → DPAPI/config/runtime → restart oraz prywatny user-triggered DEMO pozostają zewnętrzne i niezweryfikowane. Jeden niezmienny scope na środowisko nadal nie ma rotacji przez UI. Wskazówki każą zapisać docelowy klucz BOT raz, nie usuwać danych i nie traktować jego Trade ani sukcesu GET jako uprawnienia do handlu.

## Końcowe źródła i wykonany artifact
- Produkt: `68181a21a40a326b40d76680924f839a4bbdab8b`; tooling/build: `1de3ad60839d16ef24cd7b3f400e5c6cc3e3a03e`. Późniejsze zmiany tego raportu nie zmieniają źródeł produktu.
- Dodatkowy `same-wire/` ma identyczny Program.cs i identyczne pełne account.json/balance.json dla oryginalnych zachowanych DLL i finalnych DLL. Adaptacja `#if` dotyczy tylko rzeczywistej starej/nowej sygnatury factory. RED exit1/config1/balance0/UnsafeAccountConfig → GREEN exit0/config1/balance1/cash100/available90; oba rzeczywiste HMAC i GET-only true. To usuwa niepewność wynikającą z dodatkowych wymaganych pól parsera w fixture.
- Windows run `41357352828` SUCCESS, wszystkie fresh Source78/extracted113/Package6/Browser2/affected90/EnrollmentSource2/UiRepair2/Inventory1/Upgrade1/Navigation1 PASS. Rzeczywisty native helper wykazał trzy maskowane pola, DPAPI i config/runtime reopen, bez authority/network i bez secure-positive. Self-contained smoke PASS na Windows Server2025, nie Windows11 użytkownika.
- ZIP SHA `075e3618ca65f99e6186682f151d6cd0c9f73ea93df60434f33239f039babad8`, EXE SHA `77949565c8d9190612298797649c0e8ede7411a8aaf614e3903d5eafb02daa14`. Niezależna ekstrakcja CRC/manifest PASS. Wszystkie 531 wejść build manifestu zgodne z lokalnymi exact lub jawnie CRLF bajtami.
- Dwa wcześniejsze Windows CI failure są setup/reuse, nie behavioral GREEN: brak historycznego obiektu w shallow checkout, następnie brak dokładnie jednego wcześniej naprawionego testu cancel w allowliście. Naprawiono tylko tooling. Pierwotny local91 (84 PASS/7 błędne OS) pozostaje zachowany; wszystkie siedem natywnych przypadków przeszło w Windows affected90.
- Niezmienione accepted reader/adapter/admission/approval: 31 source/test wejść związanych z poprzednim ZIP, trzy pliki starego timeout proof przez SHA. Engine/platform/backtester testy wewnątrz ZIP są jawnie REUSED, a nie nowym full suite. Nie powtórzono 120 s modala.

**Końcowy verdict autora:** wąski fix i Windows artifact zweryfikowane; parent ma sprawdzić diff/RED/GREEN/source/binary binding. Nie jest to pełny odbiór enrollment ani gotowego tradera. Brak kolejnego pełnego review child, prywatnego brokera, transakcji lub obchodzenia zabezpieczeń.
