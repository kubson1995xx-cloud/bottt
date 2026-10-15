# Wyniki testów

Data: 2026-10-15. Środowisko: Linux, .NET 8 SDK (kompilacja i testy). Pakiet Windows (win-x64) zbudowany krzyżowo w tym samym środowisku.
Uruchomienie: `dotnet test tests/Apex.Tests` albo automatycznie w `tools\windows\Zbuduj-APEX.ps1`.

## Testy automatyczne – 154/154 zaliczonych

Pełne przebiegi po każdej zmianie: **154 zaliczonych, 0 niezaliczonych, 0 pominiętych** (ok. 53 s).

| Grupa | Przypadki | Co sprawdza |
|---|---:|---|
| Silnik (`EngineTests`) | 24 | wejście z SL i etykietą; ryzyko ≤ budżet; brak duplikatów przy powtarzanych cyklach; restart z zapisanym stanem i z utraconym stanem; niepewna odpowiedź (pozycja jest / pozycji nie ma); odrzucenie bez ponawiania; rozłączenie i powrót; natychmiastowe zabezpieczenie pozycji bez SL; LIVE: strategia eksperymentalna nie handluje, strażnik blokuje START; strategia odrzucona nie handluje; test wykonania tylko DEMO i pełny przebieg; pozycje ręczne i innych botów nietknięte (także przy awaryjnym stopie); dzienny limit; maks. obsunięcie → awaryjne zatrzymanie; za krótka historia; rynek zamknięty; kryptowaluta błędnie oznaczona przez brokera jako indeks; wstrzymanie wejść; zmiana konta |
| AI Decision Center (`DecisionRecorderTests`) | 9 | **identyczne zlecenia, zmiany SL i zamknięcia z wizualizacją i bez niej** (5 scenariuszy, w tym odrzucenie, niepewne odpowiedzi, brak SL); zapis rzeczywistej decyzji BUY z bramkami; blokada RiskEngine jako WAIT; odporność na mały bufor |
| RiskManager | 14 | domyślne limity zgodne ze specyfikacją; limit pozycji, dźwignia, budżet, najgorszy scenariusz dnia, blokada przy pozycji bez SL, koncentracja walutowa, korelacja, depozyt; blokada dzienna zdejmowana tylko w nowym dniu; blokada DD tylko ręcznie; odrzucanie absurdalnych ustawień |
| Wielkość pozycji (`SizingTests`) | 9 | ryzyko z prowizją ≤ budżet; zaokrąglanie wyłącznie w dół; odrzucenie gdy minimalny wolumen > budżet; nieznana prowizja = brak handlu; błędny SL; typy prowizji; minimalna odległość SL brokera |
| Klasyfikacja i kryptowaluty | 21 | wykrywanie kryptowalut (BTCUSD, ETHUSD, XBTEUR, DOGEUSDT, SOL/USD, LNKUSD, „DeFi token”…); brak fałszywych trafień dla EURUSD, USDRON, XAUUSD, US500, GER40, ropy, akcji, kakao; nierozpoznany = Unknown |
| Strażnik konta | 5 | DEMO dozwolone; LIVE domyślnie zablokowane; LIVE wymaga jednocześnie zgody, numeru na liście i dokładnej frazy; brak konta; lista brokerów |
| Zarządzanie pozycją | 6 | break-even; SL nigdy nie jest luzowany; limit czasu; wyjście strategii; częściowe zamknięcie raz i z poszanowaniem min. wolumenu; awaryjny SL po właściwej stronie ceny |
| Rejestr sygnałów | 4 | sygnał rezerwowany raz; czyszczenie nie usuwa oczekujących; format komentarza; deterministyczny identyfikator |
| Wskaźniki | 7 | SMA, EMA (inicjalizacja SMA), RSI (skrajne i płaskie), ATR Wildera, korelacja, ER, ranga procentowa |
| Wiadomości | 3 | parser (strefy czasowe, „All”, błędne wpisy); blokada tylko powiązanych instrumentów i tylko w oknie; puste dane |
| Wybór instrumentów | 3 | krypto nigdy nie wybrane (także w trybie ręcznym); klasy bez strategii pomijane; sufiksy brokerów |
| Protokół mostu | 4 | serializacja camelCase; odporność na błędny JSON; strony i czasy; nieznana prowizja unieważnia specyfikację |
| Profil strategii | 2 | profil domyślny poprawny i bez fałszywych statusów „Validated”; LIVE wyklucza strategie eksperymentalne |
| **Integracyjne** (prawdziwy proces APEX Desktop) | 9 | ochrona API (token, nagłówek Host, Content-Type); odrzucenie WebSocket z przeglądarki (Origin); zły klucz parowania; połączenie + wykluczenie BTCUSD z subskrypcji; duplikat konta odrzucony; ponowne połączenie po rozłączeniu; konto LIVE: START, test wykonania i odblokowanie zablokowane, 0 zleceń; **test wykonania end-to-end (otwarcie z SL/TP → zmiana SL → zamknięcie) z pozycjami obcymi nietkniętymi także po awaryjnym stopie**; uzgadnianie handshake TCP (ścieżka MT5) |

| **AI Lab – audyt zmian** (`ChangeAuditTests`) | 18 | RiskEngine, profil i pliki aktywnych strategii = chronione; strategie nieaktywne – nie; ścieżki zabronione (AI Lab, skrypty) = blokada; LIVE (`LiveTradingFeature`, `AllowLive`, `AccountGuard.Evaluate`), krypto, martingale, podwajanie wolumenu, uśrednianie, `FullAccess` = blokada; słowa w komentarzach i dokumentacji – bez blokady; `Skip` i usunięcie testu = blokada; sieć w silniku = blokada; brak testów = ostrzeżenie; zasad nie da się zawęzić plikiem konfiguracji |
| **AI Lab – dostawcy** (`ProviderParsingTests`) | 5 | zdarzenia `codex exec --json` (wątek, polecenia, pliki, wiadomość, zużycie, błąd tury, limit), strumień `claude -p --output-format stream-json` (narzędzia, wynik, koszt, limit), czasy odnowienia limitów, bloki plików API (ogrodzenia, niedokończone bloki), bezpieczne ścieżki (`..`, ścieżki bezwzględne, `.git`, AI Lab) |
| **AI Lab – harmonogram i limity** (`LabSchedulingTests`) | 6 | terminy (co N godzin / codziennie / co tydzień), limity uruchomień, tokenów i kosztów API, wstrzymanie po limicie subskrypcji, odstęp tylko dla automatu, walidacja ustawień, szyfrowanie i maskowanie kluczy, wymagane frazy |
| **AI Lab – weryfikacja** (`LabVerificationTests`) | 4 | werdykty backtestu (bez zmian / gorszy E[R] / gorsze obsunięcie / lepszy z zastrzeżeniem), odczyt wyników testów (TRX) i podsumowania backtestu, dziennik transakcji w formacie silnika, wykrywanie zmiany reżimu i zmienności z migawek |
| **AI Lab – izolowane środowisko** (`GitWorkspaceTests`) | 1 | prawdziwy git: kopia, gałąź zadania, `.ailab` poza zmianą, diff, commit, połączenie z gałęzią wdrożeń, łatka, gałąź w repozytorium źródłowym – **pliki źródła nietknięte** |

## Test end-to-end modułu AI & Auto-Updates

Prawdziwe procesy: APEX Desktop, symulator platformy, APEX AI Lab. Repozytorium źródłowe to to repozytorium, a do backtestu użyto prawdziwych danych.
Model AI zastąpiono lokalnym serwerem imitującym OpenAI Responses API, bo w tym środowisku nie ma dostępu do OpenAI/Anthropic ani logowania ChatGPT.
Cała reszta była prawdziwa: kopia git, kompilacja wszystkich projektów (w tym obu cBotów), pełne testy z integracyjnymi, backtest portfela, audyt i wydanie. Panel obsługiwał wszystko przez APEX Desktop.

| Scenariusz | Wynik |
|---|---|
| Agent dodaje testy | wszystkie 9 etapów OK; testy 156/156 (baza 154); backtest „bez zmian” (porównanie dwóch pełnych przebiegów); przegląd drugim modelem: AKCEPTUJ; koszt z cen: 0,12 USD; **Gotowa do decyzji** bez frazy |
| Agent próbuje włączyć LIVE domyślnie (`HostConfig.cs`) | **Odrzucona przez audyt** („Konto REAL pozostaje zablokowane”); zatwierdzenie odrzucone nawet z frazą |
| Agent zmienia `RiskManager.cs` | Gotowa do decyzji, ale **wymaga frazy** „ZATWIERDZAM ZMIANY CHRONIONE”; bez frazy zatwierdzenie odrzucone |
| Wersja bazowa bez działającego backtestu | **Wymaga uwagi**; wymagana fraza „ZATWIERDZAM MIMO OSTRZEŻEŃ” |
| Zatwierdzenie | wydanie zbudowane w 17 s (samodzielny plik), działający APEX nie został zmieniony; uruchomione wydanie odpowiada poprawnie (API, panel) |
| Wycofanie | powrót do instalacji pierwotnej, status „Wycofana” |
| Limit dzienny uruchomień | zadanie „Wstrzymana – limity”, agent nie uruchomiony |
| Limit dostawcy (HTTP 429, „try again in 20m”) | dostawca wstrzymany do czasu odnowienia; kolejne zadanie zatrzymane bez wywołania modelu |
| Restart AI Lab | historia zadań wczytana z dysku |
| AI Lab wyłączony | panel: „Moduł AI Lab nie działa” (HTTP 503); **Uruchom moduł AI** uruchamia proces; APEX działa bez przerwy |
| Panel (jasny i ciemny motyw) | wszystkie podzakładki bez błędów JavaScript |

Nie było możliwe w tym środowisku: prawdziwe sesje Codex (konto ChatGPT) i Claude Code oraz uruchomienie na Windows. Logowanie, wykrywanie instalacji i parsowanie zdarzeń tych narzędzi sprawdzono na ich rzeczywistych wersjach (Codex CLI 0.163.0: `--help`, `login status`, `exec --json`; Claude Code: `auth status --json`).

## Usterki znalezione przez testy i poprawione

1. **Zapis stanu przed wysłaniem zlecenia.** Wcześniej stan zapisywał się najwyżej co 5 s. Awaria tuż po wysłaniu zlecenia mogła zgubić rezerwację sygnału. Teraz rezerwacja trafia na dysk **przed** wysłaniem zlecenia, a plan pozycji zaraz po wykonaniu. Test: `Restart_WithSavedState_RestoresPlanAndDoesNotDuplicate`. RiskManager, strategie i limity pozostały bez zmian.
2. Backtester: polecenie `portfolio` z nowym katalogiem wyjściowym kończyło się błędem (brak katalogu `trades`) – poprawione; dodano `--summary` (JSON).
3. APEX Desktop: zajęty port mostu TCP zatrzymywał całą aplikację – teraz tylko komunikat w dzienniku (most MT5 niedostępny, reszta działa).
4. Klasyfikator: opis z fragmentem „defi” (np. „Definitive Healthcare”) wyklucza instrument. To świadomie pozostawione zachowanie (fail-closed), udokumentowane testem `AmbiguousDescription_IsExcluded_FailClosed`.

## Testy end-to-end z symulatorem platformy (ręczne, wcześniej w tej sesji)

APEX Desktop + `tools/Apex.PlatformSimulator` (konto DEMO, dane syntetyczne, tryb przyspieszony):
- połączenie mostu, wybór instrumentów, wykluczenie BTCUSD;
- test wykonania: **ZALICZONY** (otwarcie z SL i TP → potwierdzenie zmiany SL → zamknięcie → wpis w dzienniku);
- AI Decision Center wyświetlał rzeczywiste zdarzenia silnika (brak sygnału, nieaktualne dane, sygnał SELL odfiltrowany), bez błędów JavaScript.

## Kompilacja

| Artefakt | Wynik |
|---|---|
| Apex.Core, APEX Desktop, backtester, symulator, testy | 0 błędów |
| `ApexBridge.algo` (cTrader) | 0 ostrzeżeń, 0 błędów |
| `ApexTradingAI.algo` (cTrader natywny) | 0 ostrzeżeń, 0 błędów |
| `ApexDesktop.exe` (win-x64, samodzielny, jeden plik) | zbudowany (93 MB) |
| `ApexAiLab.exe` (win-x64, samodzielny, jeden plik) | zbudowany (97 MB) |
| `Zbuduj-APEX.ps1` | składnia poprawna; pełny przebieg (bez testów) wykonany w PowerShell 7.4 |
| `ApexBridge.mq5` (MT5) | **nie kompilowany** – brak MetaEditora w środowisku |

## Czego NIE przetestowano (wymaga Twojego komputera)

- Uruchomienia `ApexDesktop.exe` na Windows 11 i mostu w prawdziwym cTrader Desktop na koncie Pepperstone DEMO. Protokół i silnik przetestowano symulatorem, ale nie z serwerem cTrader. Pierwszym krokiem na Windows jest **test wykonania** z [`PROCEDURA_DEMO_PEPPERSTONE.md`](PROCEDURA_DEMO_PEPPERSTONE.md).
- Kompilacji i działania mostu MT5.
- Pobierania kalendarza wiadomości: w tym środowisku dostęp do serwera kalendarza był zablokowany (proxy). Parser przetestowano na danych testowych.
