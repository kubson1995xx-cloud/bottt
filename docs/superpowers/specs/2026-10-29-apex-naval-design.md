# APEX NAVAL COMMAND — projekt rozbudowy

## Cel, upoważnienie, baza
Rozbudować istniejący APEX o opcjonalne OKX DEMO, dwie floty trend-following i pięć animowanych widoków, bez zastępowania silnika ani ukrywania nieodebranych integracji. Wymagania źródłowe: `2026-10-29-apex-naval-mission.md`. Użytkownik wprost zlecił audyt → plan → rozpoczęcie bezpiecznej implementacji; ten zakres nie wymaga kolejnej zgody na samą pracę offline. Poniższe nierozstrzygnięte szczegóły są decyzjami architekta, nie rzekomo zatwierdzonymi przez użytkownika parametrami inwestycyjnymi.

**Skorygowana baza:** `819cba9ba12c14b44577508af83417f70c2e5a86` z `apex-demo/autonomy`, CI Windows `40360216699 success`. Początkowo odnaleziony `a886a15` jest starszy. W gałęzi `feature/apex-naval-command` wykonano wyłącznie fast-forward, zachowując stare gałęzie i wszystkie commity. Audyty zamknięć/FX starej bazy nie są aktualną listą defektów: nowsza baza ma recovery, trwały ledger/outbox, scoped authority, certyfikat testu, trwałe partial i poprawiony backtester. Nie implementować tych zaakceptowanych etapów drugi raz. Bieżący audyt w `reports/naval/AUDYT.md`.

## Co zachować
Core net6, Desktop ASP.NET net8, cTrader bridge/native, działający polski `/`, profile i strategie CFD, risk defaults, dokładną własność pozycji, trwałe rezerwacje i księgowanie, historię, recovery, certyfikację DEMO, narzędzia badawcze, xUnit/Node/Windows gates. Pasywne DTO/telemetria są punktem rozbudowy, nie upoważnieniem do zleceń.

## Granice bezpieczeństwa
- REAL/LIVE nadal niedostępne serwerowo; widoczna etykieta w UI nie daje ścieżki odblokowania. Nie przewiduje się etapu live w tym planie.
- OKX tylko opcjonalne backtest/DEMO, domyślnie off. cTrader/MT5 nadal blokują Crypto. Nie podszywać coinów pod Forex i nie usuwać globalnie zakazu.
- Bez zmian aktywnego profilu CFD, AI Lab, LLM, uczenia i automatycznych aktualizacji.
- Ryzyko nowej ścieżki nie większe niż 0,5% trade /1,5% dzień /5% DD. Centralny limit pozycji 3 pozostaje; osobne limity flot tylko zaostrzają. Dla OKX SPOT cash, bez borrow, short, instrumentów pochodnych i efektywnego lewara >1. Nie zmieniać CFD margin guardów na potrzeby spot.
- Fizyczna transakcja DEMO wymaga osobnej zgody. Kod/adapters/CI nie są taką zgodą. Brak withdrawal i transfer endpoints, nawet jeśli scope Trade technicznie je dopuszcza.

## Reguły flot — wersja naval-d1-v1
Decyzja architekta do prerejestracji przed realnymi badaniami:
1. ALFA: BTC, ETH. BRAVO: BTC, ETH, SOL, XRP, DOGE, ADA, LTC, LINK, BCH, AVAX. Profile oddzielnie konfigurowalne; Bravo domyślnie max 2 aktywne ekspozycje, licząc również pending/uncertain reservations. Nakładanie BTC/ETH między flotami: jeden właściciel per venue/account/symbol; drugi kandydat odrzucony, nie netting dwóch książek. Rzeczywiste subkonta dopiero po sprawdzeniu uprawnień.
2. D1 oznacza UTC 00:00–00:00, wyłącznie potwierdzone i kolejne dni. Signal bar `t` jest zamknięta i znana na cutoff. Entry gdy `Close[t] > max(High[t-40..t-1])`; równość nie wyzwala. Exit posiadanego long gdy `Close[t] < min(Low[t-30..t-1])`. Brak short — sell to wyłącznie redukcja własnego inventory. Nie używać high/low świecy sygnału do budowania poprzedniego kanału.
3. Filtr nowych wejść: `BTC Close[t] > średnia(BTC Close[t-199..t])`; równość blokuje. BTC z tej samej doby i cutoff co instrument, nie SMA instrumentu. Brak 200 dni, luka, stale/niepotwierdzone dane → brak nowego wejścia. Filtr nie wymusza likwidacji long i nigdy nie blokuje prawidłowego wyjścia/redukcji. Luka w danych instrumentu uniemożliwia wiarygodne wyjście kanałowe; niezależne awaryjne SL/TP nie zależą od ewaluatora.
4. Sygnał powstaje dopiero na zamknięciu; realizacja NAJWCZEŚNIEJ na następnym dostępnym tick/otwarciu. Nie egzekwować po znanym już historycznym close. Model badawczy używa kolejnego open + jawnych kosztów; live/DEMO aktualnego bid/ask. Przy luce nie zakładać realizacji po poziomie kanału. Przeliczyć risk/SL/TP przy faktycznej cenie, zrezygnować gdy limity/drift nie pozwalają. Sygnał jest ważny do następnego zamknięcia D1; stale blokuje nowe wejścia niezależnie od tej ważności.
5. Ochrona SL/TP pozostaje obowiązkowa. Propozycja badawcza: SL 2×ATR14, ochronny TP3R, bez automatycznego podnoszenia ryzyka; parametry jawne i testowane zanim sizing/execution. Low30 nie jest jedynym wyjściem, SL/TP mogą zadziałać wcześniej. Przy obu trafieniach na jednej OHLC konserwatywnie SL-first lub dokładniejsze dane; nigdy optymistyczna kolejność. Pierwszy etap sygnałów nie wylicza jeszcze wielkości ani wyników i nie certyfikuje tej propozycji.
6. Bravo: ranking dopuszczonych sygnałów według odległości close od high40 podzielonej przez ATR14 (malejąco), tie po symbolu ordinal. Filtr płynności oparty na rzeczywistym quote turnover/spread/depth z wersjonowanym progiem; brak danych = brak admission, nie płynność zero ani domyślne PASS. Progi i koszty zamrozić przed OOS, nie dobierać po wynikach. Ranking/liquidity/admission NIE należą do pierwszego samego ewaluatora.

## OKX: izolowana kompozycja, nie drugi silnik
Docelowy moduł `Apex.Adapters.Okx` implementuje giełdowe porty C# i dostarcza dane do APEX, bez zmiany cTrader. Rozszerzenie Core o namespace venue/environment/account/fleet, cross-symbol context i centralny arbiter jest wspólne, ale authority OKX nie jest flagą w `StrategyInput` ani `IsBacktest`. Nowa kryptowalutowa ścieżka profil/strategy/admission jest jawnie komponowana po stronie hosta; istniejące cTrader guards pozostają.

Dla polskiego/EEA konta dokumentacja podaje wspólny REST `https://eea.okx.com` dla LIVE i DEMO — host sam NIE izoluje. Wymagać demo key i `x-simulated-trading: 1` w każdym prywatnym request, regionalnego DEMO WS, sprawdzonego trybu konta `SPOT`/`cash`/brak autoLoan, allowlisty endpointów, braku fallbacków LIVE/regionów/redirectów. Sekrety tylko backend, prawa pliku/secret provider, redakcja logów i IP allowlist; Read na etap prywatnego odczytu, Trade dopiero na osobno zatwierdzony test. API DEMO nie tworzy/zarządza subkontami; wirtualny portfel nie jest izolacją giełdową.

Dostępność 10 aktywów/par/quote jest per region/konto i musi być odkryta, nie zakładana jako *-USDT. Waluty nie są zamienne 1:1. Public history-candles z `1Dutc`/`confirm=1`, provenance/hash/gaps/listing/warmup. Salda, pending, order details, fills i bills są uzgadniane po restarcie. `orders` WS nie ma initial snapshot; ack/orderFilled nie daje kompletu filli. `clOrdId` nie zapewnia wiecznej idempotencji. Fill key venue/environment/account/instrument/tradeId, fee w rzeczywistej feeCcy; nie sumować accFillSz wielokrotnie ani podwójnie fill+bills.

## Jedno źródło danych, C# → panel
Wariant wybrany: istniejący ASP.NET host plus osobny frontend `/naval/`, `/` pozostaje fallback. Nie przepisywać całego hosta w Python/Node; Python jedynie opcjonalnie do weryfikacji danych/research. Nowy immutable read model z account/fleet/venue/environment string IDs, schemaVersion, epoch, snapshotVersion/asOfSequence, per-source timestamps, quality/coverage i money+currency. Nie wysyłać pairing key/logów/tokenów w publicznym DTO. Pasywny outbox/stream nie blokuje pętli tradingowej.

Początkowo read-only polling z abort/generation/account checks; następnie autoryzowany fetch-stream/SSE z nagłówkiem, nie tokenem URL i nie użyciem `/bridge`. Durable event log ma cursor/oldest/hasMore/gap/reset; po zmianie epoch lub luce snapshot/resync przed efektami. Przeglądarka nie liczy księgi, nie wycenia ryzyka i nie nadaje uprawnień. Role read-only/operator egzekwowane backendowo, nie disabled button.

## Centrum dowodzenia i animacje
React+TypeScript, Vite produkcyjny build, Three.js/R3F lazy-loaded. Hash routing pięciu ekranów minimalizuje ingerencję w routing hosta. Lokalne assets/CSS bez CDN/eval, bez osłabiania globalnego CSP. 2D SVG/Canvas radar i czytelne listy działają również bez WebGL; 3D okręty/hologramy są projekcją stanu. Granat/czerń/turkus/morska zieleń; zagrożenia pomarańcz/czerwień, tekst obok koloru. Limit DPR/FPS/particles, cleanup GPU, pause ukrytej karty, reduced-motion/minimal/mute. Brak dostarczonego assetu/licencji ≠ zgoda na cudze modele.

1. Command: stan silnika/adapterów/księgi, kapitał/P&L z walutą i jakością, risk, zdarzenia, BACKTEST/DEMO/LIVE locked, tekstowe emergency niezależne od canvas.
2. Tactical: realne quotes/oceny 10 targetów z wiekiem danych; click pokazuje instrument, trend, sygnał, risk, pozycję i decyzje. Radar stale jest wizualnie stale, skan nie udaje napływu danych.
3. Fleet: ALFA/BRAVO, własne budżety/wyniki/zlecenia/ustawienia, aktywne pozycje i wizualne grupy jednostek; brak skonfigurowania = unconfigured, nie fikcyjne P&L.
4. Trade Animations: scanning → detected → locked (tylko zatwierdzona intencja) → submitted (torpeda, nie sukces) → broker FillConfirmed → position active/protection verified → close requested/confirmed → settled result. Partial/rejected/canceled/uncertain/reconciling jako osobne stany. Restart/replay nie generuje nowego zwycięstwa, zniknięcie pozycji nie dowodzi settlement.
5. Analytics: equity a realized/unrealized osobno, gross/net/fees, WR/PF/average/DD z definicją i zakresem, trwała historia/CSV, per-fleet wyniki oraz backtest/DEMO osobno. Unknown nie zamienia się w 0/999. Demonstracja UI jawnie odcięta od execution.

## VPS / Windows / telefon
Headless .NET8 host OKX może docelowo pracować na Linux VPS z systemd, prywatnym katalogiem danych i restart supervision; restart daje STOP/reconciliation, nie START/LIVE. Windows11 klient panelu, telefon przez prywatny tunel/TLS i gateway z rolami; backend/bridge nadal loopback, żadnego publicznego 5077/5078. Obecny cTrader bridge wymaga lokalnego Windows/cTrader; Linux VPS nie zastępuje cTrader Desktop. Jeśli współdzielona kontrola wielu hostów jest potrzebna, osobny authenticated transport/ownership/central risk gate i odbiór, nie samo otwarcie socketu.

## Pierwszy bezpieczny etap, zakres twardy
Nowy `Apex.Core/Naval` zawiera tylko offline evaluator D1 i profile flot, a `tools/Apex.Naval.Replay` działa na lokalnym JSON. To moduł w istniejącym Core, nie nowa aplikacja tradingowa ani engine rewrite. Nie rejestrować nowych strategii w produkcyjnym StrategyBook/START, nie zmieniać profilu, guards, brokerów i panelu. Brak HTTP, secret provider i jakiejkolwiek funkcji wysyłania zleceń.

Kontrakt: instrument/base/quote/source/priceBasis oraz potwierdzone decimal OHLCV UTC D1, jawny cutoff, flota i informacja o istniejącym long. Wynik `EntrySignal/ExitSignal/Hold/Wait` + powód + cutoff/earliestExecutionUtc + kanały/BTC SMA + stabilna tożsamość konfiguracji/prefiksu. Nie OrderSubmitted/Filled, brak P&L/sizing. Wejście może zawierać przyszłe świece, ale ich OHLC nie wpływa na wynik ani ID. Używany prefiks wymaga kolejności, unikalności, ciągłości, prawidłowych OHLCV, UTC i zakończenia ostatniego dnia na cutoff. Exit sprawdzany przed wymaganiem dostępności filtra BTC.

CLI `--input <json> --output <json>` jawnie OFFLINE/NO_EXECUTION, przyjmuje wyłącznie lokalne pliki, drukuje decyzje dla zadanych cutoffów obu flot; nie symuluje pozycji/filli. Brak danych/invalid config → jawny błąd lub Wait, nigdy fabricated prices. Dokumentować input SHA256, schema/config; stabilne as-of ID nie zależy od przyszłości. Testowe dane tylko w katalogu jawnie `synthetic`, bez finansowych wyników i bez mylenia ich z historycznym backtestem. Testy RED→GREEN i niezależny przegląd no-lookahead/no-execution są odbiorem tego etapu.

## Kryteria końcowe, niezaliczone przez sam fundament
A: aktualny audyt bazy i plan; B1: powtarzalne offline sygnały; B2: realne public data + rzetelny model spot/fees/FX + OOS; B3: centralny risk/fleet ledger + OKX read-only/recovery; B4: osobno zatwierdzony DEMO E2E. C: pięć podłączonych widoków i fallback; D: rzeczywiste event lifecycle i degradacja 2D; E: crash/restart/duplicate/account-switch/stale/performance; F: świeże Windows CI/manifest/EXE + Linux service smoke i instrukcje. Brak e2e Pepperstone/OKX, MT5 acceptance i udowodnionej rentowności pozostaje jawny. Broker i Windows11 użytkownika nie są zastępowane lokalnym symulatorem.

## Oficjalne źródła OKX (Crawl4AI 2026-10-29)
- https://my.okx.com/docs-v5/en — EEA API, Demo Trading Services, auth, candles, orders/fills/bills.
- https://www.okx.com/en-eu/help/api-faq — permissions, regional domain, uncertain timeouts.
- https://www.okx.com/en-eu/help/how-to-use-demo-trading — ograniczenia DEMO/subkont.
- https://www.okx.com/en-eu/learn/okx-exchange-germany-poland — Polska/EEA i regional disclaimers.
Nie wykonano API/account discovery ani żadnego zlecenia; dokumentacja nie potwierdza dostępności konkretnego konta/par.