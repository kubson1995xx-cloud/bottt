# APEX NAVAL COMMAND — zlecenie i granice misji

Źródło: bezpośrednie zlecenie użytkownika 2026-10-29. Początkowy odczyt dotyczył `a886a152b2e9bcdf6a6599bfdc1e76dd94681336`, repo `kubson1995xx-cloud/bottt`. W audycie odnaleziono nowszą istniejącą bazę `819cba9ba12c14b44577508af83417f70c2e5a86` na `apex-demo/autonomy` i do niej przesunięto fast-forward osobną gałąź `feature/apex-naval-command`, bez resetowania istniejących gałęzi. Audyt i dalsza implementacja mają uwzględniać już ukończone recovery/accounting/certification tej nowszej bazy. Ten dokument zapisuje wymagania; projekt i plan znajdują się w sąsiednich plikach `2026-10-29-apex-naval-design.md` oraz `../plans/2026-10-29-apex-naval.md`.

## Autoryzacja i zakazy
- Użytkownik polecił rzeczywisty audyt, zapisanie planu i następnie rozpoczęcie pierwszego bezpiecznego etapu implementacji bez prawdziwych pieniędzy; nie wymaga ponownego pytania o zgodę na ten sam zakres.
- Nie pisać nowego silnika od zera. Zachować C#/.NET, cTrader, historię, zabezpieczenia i polski panel awaryjny. Nowa warstwa wizualna nie może być warunkiem bezpieczeństwa zleceń.
- Nowe zezwolenie na krypto jest ograniczone do opcjonalnego OKX backtest/DEMO. Nie zmienia starego zakazu krypto w cTrader/MT5 ani nie upoważnia do LIVE, wypłat lub transakcji brokerskich. Faktyczna transakcja DEMO wymaga osobnej zgody.
- Brak uczenia, decyzji LLM i automatycznych aktualizacji. Brak obietnic rentowności i fikcyjnych danych/wyników.
- Istniejące ryzyko: 0,5% na transakcję, 1,5% straty dziennej, 5% DD; nowe krypto bez lewara. Nie zwiększać istniejących limitów; floty muszą przechodzić także centralny budżet ryzyka.

## Funkcje docelowe
1. Opcjonalny oficjalny adapter OKX: historyczne i aktualne dane, saldo, zlecenia/wykonania/opłaty, pozycje/zasoby, synchronizacja restart/reconnect, wirtualne portfele strategii; rzeczywiste subkonta tylko gdy region/API/uprawnienia to umożliwiają. Instrumenty bazowe BTC, ETH, SOL, XRP, DOGE, ADA, LTC, LINK, BCH, AVAX; konkretne pary odkrywane z API, nie gwarantowane.
2. Dwie niezależnie konfigurowalne floty w istniejącym APEX: ALFA BTC/ETH; BRAVO wszystkie 10, domyślnie maksymalnie 2 aktywne pozycje. Wybicie ponad high poprzednich 40 zakończonych D1, wyjście poniżej low poprzednich 30 D1, filtr BTC SMA200, sizing ryzyko/zmienność, ranking i płynność. Zdefiniować short, filtr, luki, moment egzekucji i brak lookahead. Bez przejmowania stopów -30/-40% ani lewara 2x.
3. APEX NAVAL COMMAND: React/TypeScript, Three.js/React Three Fiber po weryfikacji zgodności z hostem. Granat/czerń/turkus/morska zieleń, ostrzeżenia pomarańcz/czerwień, czytelne radary i okręty, nie tylko statyczna makieta.
4. Pięć widoków: Command Center, Tactical Radar, Fleet Operations, Trade Animations, Analytics Center. Radar ma stan świeżości danych; szczegóły celu zawierają cenę/trend/sygnał/ryzyko/pozycję/historię decyzji. Floty mają osobne budżety/status/ustawienia i wspólne limity.
5. Zdarzenia animacji: SCANNING, SIGNAL DETECTED, TARGET LOCKED (nie fill), ORDER SUBMITTED (nie fill), ORDER FILLED (potwierdzony), POSITION ACTIVE, POSITION CLOSED, RESULT (dopiero uzgodnione rozliczenie). Obsługa partial/reject/cancel/unknown/reconnect; wyciszenie, reduced motion, tryb minimalistyczny.
6. Analytics: equity, brutto/netto, prowizje i koszty, win rate/PF/DD/średnie, pozycje i strategie, backtest vs DEMO, eksport CSV. Zrealizowany i niezrealizowany wynik oddzielnie. Brak fikcyjnych statystyk; demo UI musi być jednoznacznie oznaczone i odcięte od komend handlowych.
7. Silnik na VPS, Windows 11 jako klient, bezpieczny podgląd telefonu, restart usług bez samoczynnej zgody na handel, jedno źródło danych. Awaria przeglądarki/animacji nie wpływa na zarządzanie zleceniami. Przełącznik BACKTEST/DEMO/LIVE z LIVE zablokowanym serwerowo.
8. Prawda brokerska dla sald i wykonań, trwały audyt, deduplikacja, SL/TP, Emergency Stop, poprawne częściowe rozliczenia i brak podwójnego P&L. Sekrety poza repo i panelem, zero uprawnień wypłaty.

## Etapy i wymagany pierwszy raport
A audyt; B backend OKX/strategie/backtesty; C rzeczywisty panel; D animacje zdarzeniowe; E synchronizacja/restart/wydajność; F świeży pakiet Windows i instrukcja VPS. Każdy etap dokumentowany w HANDOFF, z testami proporcjonalnymi do zmiany i niezależnym przeglądem skomplikowanych granic.

Raport po audycie odpowiada: co zachować, co przebudować, izolacja OKX/cTrader, C#↔panel, architektura animacji, etapy i kryteria. Po zapisaniu planu wykonać pierwszy etap offline; nie nazywać całej misji ukończoną po dostarczeniu fundamentu.

## Niezamknięte odbiory bazowego produktu
Windows CI na `926afaa` i EXE smoke bez brokera istnieją. Nadal brak potwierdzonego Windows 11 użytkownika + Pepperstone DEMO E2E; MT5 nieodebrane; przewaga/rentowność nieudowodniona. OKX nie istnieje w bazie. Nowy panel ani nowe testy offline nie zamykają tych bramek.
