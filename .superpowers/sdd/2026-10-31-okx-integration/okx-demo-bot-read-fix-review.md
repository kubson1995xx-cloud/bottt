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
