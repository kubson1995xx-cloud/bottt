# Task4 — wspólny kapitał, odbiór wyłącznie offline

## Końcowa dyspozycja
**ZAAKCEPTOWANY OFFLINE**, kod `e31e36d`, po niezależnym review i scoped re-review (`task-4-fix1-review.md`). Oba Important są ADDRESSED, bez nowych Critical/Important z poprawki. Parent wykonał retained verifier z exit 0: 8/8 plików i 40 artefaktów zgodnych, rzeczywiste RED→GREEN, CLI i persisted state potwierdzone. Nie powtarzano niezmienionych suite. Końcowe affected: 684/684 Core/Desktop i 136/136 Backtester, solution/SDK bez błędów i ostrzeżeń.

Odbiór dotyczy jednego zaufanego syntetycznego lifecycle na rezerwację, Linux, lokalnego współpracującego filesystemu oraz authority Offline-only. **Nie obejmuje brokera, Windows runtime, utraty zasilania, migracji starego Naval ani rentowności.** Starsze niezaakceptowane reservations bez `ReductionUnsettled` wymagają osobno ocenionego recovery, nie resetu. Task5A może rozpocząć izolowany read-only adapter bez zmiany authority.

Poniższy opis kandydata i wcześniejszych dowodów jest zachowany jako historia; nie nadpisuje tej dyspozycji.

## Status
Kod `9541009` na `feature/apex-naval-command`, baza `365cdea`. Implementacja i lokalne bramki zakończone; **niezależny przegląd zażądał dwóch poprawek Important**. Task4 pozostaje nieodebrany. Review odtworzył (1) overwrite authority przez output containment przy trailing separator state-dir; (2) premature slot/symbol release po nonfinal reduction, bo finality wejścia błędnie uznawano za końcową finality zamknięcia. Oba przypadki mają poprawkę w `e31e36d` i potwierdzone regresje RED→GREEN; **scoped re-review nadal trwa, Task4 nie jest jeszcze odebrany**. Parent zweryfikował 8/8 hashy poprawki i actual TRX: targeted 50/50 Core i 14/14 command, affected 684/684 Core/Desktop i 136/136 Backtester; build solution/SDK bez błędów i ostrzeżeń. Nowe pole `ReductionUnsettled` jest wymagane w reservations: stare niezaakceptowane snapshots bez niego odmawiają load bez zmiany bajtów i nie mają automatycznej migracji. Poniższe starsze zielone wyniki są dowodem historycznym kandydata `9541009`, nie końcowych bajtów poprawki. To nie brokerowy test ani zgoda na aktywację flot.

## Dostarczone działanie
- Jeden `NavalPortfolioCoordinator` i autorytatywny stan kapitału, nie oddzielne salda finansowane dla obu flot. Fleet/account-scoped handles konsumują te same cash/risk/slot holds.
- Atomowe rezerwacje przed przyznaniem admission; central max3, ALFA2, BRAVO2, uwzględnione pending/partial/uncertain; jeden właściciel instrumentu na konto między flotami.
- Account-local cash; inne konto nie finansuje brakującego salda pierwszego. Wspólne centralne limity0,5% trade/1,5% daily/5%DD, cash-SPOT≤1x. Nastawy można tylko zaostrzyć. Obecny CFD RiskManager/default5x pozostaje bez zmian.
- Entry/exit fees w quote/base, częściowe wykonania i rozliczenie inventory, deduplikacja filli/receiptów, trwałe command/signal tombstones. Timeout nie uwalnia środków ani nie ponawia zlecenia.
- Potwierdzenie terminalne wymaga kompletnego dowodu fill/fee; terminalny order wejściowy nie znaczy zamkniętej pozycji. Konflikt/stale/gap/ambiguous persistence blokuje nowe ryzyko.
- Jeden pełny EngineState commit Set+Flush przed publikacją; nullable `EngineState.Naval`, schemaCFD5 bez zmiany. Owner odmawia użycia CFD-populated envelope, ma wyłączną dzierżawę katalogu i nie reseeduje brakującego/uszkodzonego uprzedniego stanu.
- Restart zachowuje ekonomię i zobowiązania, lecz zaczyna STOP/UNKNOWN. Nowa generacja, pełne uzgodnienie i osobny jawny START; żadnego auto-resume.
- `naval-shared-replay` w istniejącym backtesterze faktycznie wykonuje kontrolowane kroki obu flot na tej samej authority i otwiera zachowany stan ponownie. Nie tworzy osobnego `SpotAccount` obok tej księgi.

## Granica authority
Cała kompozycja jest strukturalnie **Offline-only**. Nie ma gatewaya, HTTP, kluczy, brokerowych zleceń, private API, DEMO issuera ani LIVE. JSON/enum/flag nie mogą utworzyć nie-Offline definicji. Publikowane wyniki: `OFFLINE_SHARED_CAPITAL / SYNTHETIC_NOT_RESEARCH / NO_BROKER_AUTHORITY`.

Pre-existing Core friend access jest assembly-wide i obejmuje także ApexDesktop, nie wyłącznie testy/backtester. Nie rozszerzono go; Desktop nie komponuje tego modułu, ale niezależny review ma zweryfikować tę granicę zamiast ufać samemu `internal`.

## Zweryfikowane dowody lokalne
Wykonawca uruchomił, parent odczytał rzeczywiste UnitTestResult i sprawdził15/15 source/project/doc/fixture SHA256:
- nowe Core NavalCash **46/46 PASS**;
- nowe executable replay **4/4 PASS**;
- pełna affected Core/Desktop **680/680 PASS**, zero skipped;
- pełna affected Backtester **126/126 PASS**, zero skipped;
- rozwiązanie i rzeczywiste SDK consumers/.algo **0 warnings/errors**.
To osobne suite, nie sztuczna suma. Nie powtarzano kampanii historycznej ani zaakceptowanych niezmienionych audytów.

W TDD zachowano assertion RED: always-refuse26cases (25fail/1pass), actualcommand0vs2, potem konkretne regresje konfliktów/latefills/coherentmarks/daybaseline oraz per-accountnotional. Pierwszy integrated679/126 został jawnie zastąpiony po nowym bugfix przez680/126; wcześniejszy run nie jest dowodem późnych bajtów.

Actual CLI20initial +9continuationsteps, oba exit0. Parent odczytał plik autorytatywny: **revision27,3zaakceptowane fills,cash10039.160,zero active/holds**. Kontrolowana arytmetyka: seed10000, buy4@100 +quoteentryfee0.400 →cash9599.600 i closefeehold0.520; fullsell4@110 minusfee0.440 →final10039.160. Powtórzenie filli po restarcie nie kredytuje cash drugi raz. To jawnie synthetic fixture, nie zysk z rynku.

Cross-process lease refusal wykonany przez osobny proces. Linux proces/flush/rename sprawdzone; Windows natywne locking/write-through i fizyczna awaria zasilania nieodebrane.

## Identyfikatory i instrukcje
- `docs/NAVAL_SHARED_OFFLINE.md`: dokładne API/schema/CLI.
- `.superpowers/sdd/2026-10-29-apex-naval/task-4-risk-report.md`: pełny raport wykonawcy.
- `.superpowers/sdd/2026-10-29-apex-naval/task-4-risk-review.md`: niezależny raport po zakończeniu, nie zakładać jego werdyktu.
- Logi/TRX/source manifest i actualCLI `/home/ubuntu/.hermes/cache/scratch/apex-naval-task4/` (scratch może wygasnąć).
- Source manifestSHA256 `ded89d12e9cbf50343390425789c7c73c15931457a178c604675315ccb1ce2d5`.
- TestedCoreDLL `6204a8b164ef6c428ae8d2b4fdeb5b231f644e7a5bf87743d264ee28556c261f`.
- TestedBacktesterDLL `348d69e92df3f45b6e109503baf8bc899a4fcea7353afa5bc00db3cee087329f`.
- ReadbackstateSHA256 `79934e409b31b70412ad64a42ba1778ade6369ba0044ecdad5d7026dedee4d03`.

## Co pozostaje
1. Niezależny review nowego shared-capital/evidence/persistence/authority boundary i ewentualne poprawki z regresjami.
2. Celowo brak operatorowego resolvera konfliktów/awaryjnych economics: taki stan pozostaje STOP/UNKNOWN, nie jest automatycznie naprawiany resetem.
3. Brak prawdziwego broker order/bill mapowania, coverage, thirdcurrencyFX, wielohostowego koordynatora, uprawnień DEMO i wykonania. Synthetic complete observations nie są dowodem historii brokera.
4. Task5A projektuje read-only OKX DEMO transport/dane, bez writeendpointów i bez odblokowania Offline authority. Przy braku bezpiecznie skonfigurowanych demo credentials prywatny smoke pozostaje blocked, nie fikcyjny PASS.
5. FrontendReact/animacje/mobilny gateway i świeży pakiet Windows/VPS nadal osobne przyszłe odbiory. Wyniki Task3 i brak potwierdzonej przewagi nie zostały zmienione ani podniesione do Validated.
