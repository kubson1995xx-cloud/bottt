# Naval — skorygowany audyt delty bazy 819cba9

## Werdykt i zakres

**Zachować nową księgę, recovery, scoped admission, certyfikat i partial gates. Nie projektować ich ponownie jako „brakujących”.** Nie są jednak gotowym adapterem OKX, koordynatorem ALFA/BRAVO ani publiczną telemetrią rozliczeń.

- Baza: `819cba9ba12c14b44577508af83417f70c2e5a86`, gałąź `feature/apex-naval-command`; porównanie z `a886a152b2e9bcdf6a6599bfdc1e76dd94681336`.
- To ograniczona analiza zmienionych granic, nie ponowny broad review przyjętych Task2/Task3. Najwyższy rozdział `HANDOFF.md:3-15` zastępuje historyczne checkpointy; jego oczekiwanie Windows rozstrzyga już poniższy rzeczywisty run.
- **Ź** = potwierdzone inspekcją aktualnej ścieżki; **T** = także wynik zachowanego testu na tej bazie; **U** = integracja/odbiór niezweryfikowany; **B** = brak w sprawdzanej implementacji. T nie oznacza transakcji brokerskiej ani rentowności.
- Nie uruchamiano testów, buildów, silnika ani usług. Bez brokera, kluczy, LIVE i zleceń, także DEMO. Zmieniany jest tylko ten raport; inne dokumenty należą do rodzica.

## 1. Jawne odwołanie ustaleń z a886

Raporty scratch `apex-naval-engine-audit.md` i `apex-naval-ui-audit.md` opisują **starą bazę**. Ich stare numery linii nie są dowodami dla 819cba9. Odwołuję następujące twierdzenia jako bieżące:

| Stare ustalenie | Stan nowej bazy i źródło |
|---|---|
| Brak historii/recovery i deduplikacji finansowej | **Ź/T:** istnieją `IBrokerHistory`, raw closing-deal ledger, terminal keys i replay; produkcyjny `closed` jest hintem, nie samodzielnym księgowaniem. `src/Apex.Core/Engine/Interfaces.cs:34-54`; `src/Apex.Core/Engine/HistoryRecovery.cs:55-83`; `src/Apex.Core/Engine/ScopedRecovery.cs:365-420`. |
| CSV i wynik aktualizowane bez autorytatywnego commitu/outbox | **Ź/T:** stan v5 obejmuje księgę i eksport; candidate jest utrwalany przed publikacją, projekcja rekoncyliowana także przy pustym outbox. `src/Apex.Core/Engine/EngineConfig.cs:53-83`; `src/Apex.Core/Engine/HistoryRecovery.cs:203-226`; `src/Apex.Core/Engine/HistoryRecovery.cs:244-269`. |
| PartialDone ustawiane przed wykonaniem, intencje close tylko w RAM | **Ź/T:** trwałe partial intents, witness i conservation; `PendingCloses` jest w stanie. `src/Apex.Core/Engine/PartialExecution.cs:100-149`; `src/Apex.Core/Engine/PartialExecution.cs:161-180`; `src/Apex.Core/Engine/EngineConfig.cs:76-90`. |
| Nieobecny pending po czasie uznawany za niewykonany | **Ź/T:** brak pozycji nie rozładowuje zobowiązania; outcome wymaga pozytywnego dowodu. `src/Apex.Core/Engine/TradingEngine.cs:719-741`; `src/Apex.Core/Engine/ScopedRecovery.cs:258-292`. |
| START nie wymaga trwałego execution PASS | **Ź/T:** wymaga zgodnego certyfikatu, historii i ryzyka; kontrola istnieje też przy rzeczywistym dispatch. `src/Apex.Core/Engine/TradingEngine.cs:1425-1439`; `src/Apex.Core/Engine/ScopedRecovery.cs:175-197`. |
| Symulator zamienia nieznany FX na 1 | **Ź/T:** fallback usunięty; kurs brakujący/przyszły/stale/niefinitywny odmawia; tylko USD ma identity=1. `tools/Apex.Backtester/Sim/SimWorld.cs:112-118`; `tools/Apex.Backtester/Data/MarketDataStore.cs:129-163`. |
| Journal ma wyłącznie niekompaktowalny limit 20 000 | **Ź/T:** 20 000 dotyczy uncertain; terminal hot cache 256 i trwałe tombstones, 100 000 ID / 64 MiB. `src/Platforms/cTrader/Shared/CommandJournal.cs:20-32`; `src/Platforms/cTrader/Shared/CommandJournal.cs:189-208`. |

Odwołanie nie oznacza brokerowego odbioru tych mechanizmów. Nadal aktualne są ograniczenia publicznego API/telemetrii, brak flot/OKX/React oraz nieadekwatność modelu CFD jako gotowego modelu giełdowego spot.

## 2. Zmienione granice — co zachować, co naprawdę potwierdzono

### Durable ledger / outbox / historia

- **Ź/T:** klucz dealu to position + brokerowy `ClosingDealId`; konflikt payloadu odmawia. Partial net zwiększa realized raz, a terminalna pozycja daje jeden agregat/statystyczny trade. Dla scope wymagane dokładne `C + O = V0` w quantum, nie względna tolerancja dużego wolumenu. `src/Apex.Core/Engine/HistoryRecovery.cs:11-25`; `src/Apex.Core/Engine/ScopedRecovery.cs:365-416`.
- **Ź/T:** `CommitScope` zapisuje/flushuje jedną autorytatywną rewizję przed zmianą pamięci; ambiguous write blokuje dalsze zapisy zamiast nadpisania starszym stanem. CSV jest kluczowaną, atomowo publikowaną projekcją, bez wycinania dawnych kluczy do okna UI. `src/Apex.Core/Engine/ScopedRecovery.cs:62-87`; `src/Apex.Desktop/Services/FileServices.cs:158-190`.
- **Ź/T:** migracja nie błogosławi legacy overlap jako nowej pustej epoki; brak/wadliwe pola zachowują blokadę. `src/Apex.Core/Engine/TradingEngine.cs:268-329`.
- **T:** w pobranym `bot.trx` PASS m.in. `DuplicateClosedEventAndReplayCountOnceAcrossRestart`, `ReplayOfflineClosureRestoresNetCommissionSwap`, `JournalCrashBeforeAndAfterAppendRecoversExactlyOnce`, `DedupeKeysSurviveBothRecentDisplayWindowsAndRestart`. Definicje: `tests/Apex.Tests/RecoveryTests.cs:40`, `tests/Apex.Tests/RecoveryTests.cs:89`, `tests/Apex.Tests/RecoveryTests.cs:196`, `tests/Apex.Tests/RecoveryTests.cs:380`.
- **U:** fizyczna trwałość cTrader LocalStorage, timing SDK, retencja, rollback całego magazynu i hidden writers nie wynikają z CRC/checksum ani testów plikowych. Finite snapshots/tombstones mają koszt i limit; nie deklarować nieskończonego działania bez obsługi magazynu (`HANDOFF.md:15`; `src/Platforms/cTrader/Shared/CommandJournal.cs:81-87`).

### Coverage ≠ scoped admission

- **Ź:** `Complete` oznacza transport dostępnej kolekcji, nie broker-wide coverage. Mapper używa jawnie `HistoricalTrade.ClosingDealId` Int32, nie zamiennie `Deal.Id` Int64; nie tworzy ekonomicznego fallbacku. `src/Apex.Core/Engine/Interfaces.cs:34-54`; `src/Platforms/cTrader/Shared/CTraderHistory.cs:8-43`.
- **Ź/T:** MAIN ma działającą ścieżkę read-only setup: nowa dedykowana instalacja, DEMO hedged, zero wszystkich pozycji/pending, inventory i losowa exact label, ręczne re-pair. Nie jest już prawdą „CoverageUnknown zawsze uniemożliwia każdy świeży start”. `src/Apex.Desktop/Runtime/ScopedAccountRuntime.cs:107-140`.
- **Ź/T:** ScopedReady dotyczy tylko własnej dedykowanej epoki. Nie ma adopcji obcych/nieznanych obiektów; pending orders i kolizje blokują, actual opening witness zachowuje oryginalny fill. `src/Apex.Core/Engine/ScopedRecovery.cs:245-281`; `src/Apex.Core/Engine/ScopedRecovery.cs:326-363`.
- **Ź/T:** owner-issued permit wiąże identity, rewizje, epoki i pierwotny monotonic deadline; zapisy/refresh nie odnawiają wieku. Po send-lock ponowna autoryzacja, przy SDK fence po journal flush. `src/Apex.Core/Engine/ScopedRecovery.cs:106-167`; `src/Apex.Core/Engine/ScopedRecovery.cs:217-235`; `src/Apex.Desktop/Bridge/BridgeSession.cs:456-479`; `src/Platforms/cTrader/Shared/ScopedPlatformState.cs:55-70`.
- **T:** `ScopedHostProcessTests.ProductionEntryBootstrapsCoverageUnknownScopeThenRunsOnlyExplicitTechnicalRequest` oraz przypadki epoch/restart/corrupt-startup w `bot.trx` PASS. To rzeczywisty proces Desktop z syntetycznym peerem, nie połączenie z cTrader.
- **U:** wyłączny pisarz jest warunkiem operacyjnym, nie dowodem wykrycia każdego zewnętrznego klienta. Native produkcyjnie nadal read/reduction-only, bez setup/permit i open/test parity. `src/Platforms/cTrader/ApexTradingAI/ApexTradingAI/ApexTradingAI.cs:136-143`; `src/Platforms/cTrader/ApexTradingAI/ApexTradingAI/ApexTradingAI.cs:447`.

### Execution certificate i partial gates

- **Ź/T:** PASS wiąże konto DEMO/hedging, baseline/exact label, cTrader/DesktopBridge i wersje. Walidacja wymaga original fill, obserwowanych SL/TP, potwierdzonego tight SL, żądanego full-close i terminalnej trwałej ekonomii. `src/Apex.Core/Engine/ExecutionCertification.cs:45-68`; `src/Apex.Core/Engine/ExecutionCertification.cs:124-135`; `src/Apex.Core/Engine/ExecutionCertification.cs:149-181`.
- **Ź/T:** test jest osobną świadomą czynnością `confirmDemoTrade`; sam setup/PASS nie uruchamia strategii. Restart przerywa test i pozostawia STOP. Zaufana symulacja nie tworzy produkcyjnego PASS; nazwa platformy/JSON/mutable IsBacktest nie są authority. `src/Apex.Desktop/Runtime/AccountRuntime.cs:293-299`; `src/Apex.Core/Engine/TradingEngine.cs:246-251`; `src/Apex.Core/Engine/ExecutionCertification.cs:79-92`; `src/Apex.Desktop/Program.cs:27-44`.
- **Ź/T:** partial zapisuje action przed wysłaniem; Reserved/AwaitingEvidence tylko lookup, bez ślepego resend. Confirmed dopiero po exact reduction witness, późniejszej migawce i union dealów. Minimum/dust=Skipped, legacy=LegacyUnknown, nie sukces. `src/Apex.Core/Engine/PartialExecution.cs:45-71`; `src/Apex.Core/Engine/PartialExecution.cs:74-180`.
- **Ź/T:** capability partial zachowuje oryginalny receipt/deadline i jest sprawdzana przez Core→Desktop→SDK po flush. Generic partial odmawia. Full protective close jest celowo osobną ścieżką, nadal DEMO/exact-owned. `src/Apex.Desktop/Bridge/SessionAdapter.cs:112-138`; `src/Platforms/cTrader/ApexBridge/ApexBridge/ApexBridge.cs:640-681`.
- **T:** PASS w `bot.trx` obejmuje `DurablePassActualEvidencePublishesOnlyAfterTerminalEconomicsThenPersistsAcrossRestart`, restart boundaries partial i `PartialDispatchFenceTests`; `platform.trx` obejmuje journal compaction/replay, epoch/revision i native refusal. **U:** nie potwierdza to autentycznego SDK fill timing ani crash po SDK sukcesie przed pierwszym trwałym checkpointem (`HANDOFF.md:60-70`).

### Backtester FX / cashflow / walk-forward

- **Ź/T:** entry fee jest od razu w Balance; open net rezerwuje koszt wyjścia; partial/final przypisują entry fee i swap tylko raz. `tools/Apex.Backtester/Sim/SimWorld.cs:198-209`; `tools/Apex.Backtester/Sim/SimWorld.cs:294-298`; `tools/Apex.Backtester/Sim/SimWorld.cs:345-363`.
- **Ź/T:** FX tylko zamknięty, znany na cutoff, dodatni/skończony i nieprzeterminowany; preflight także przy braku transakcji. `tools/Apex.Backtester/Data/MarketDataStore.cs:129-163`; `tools/Apex.Backtester/Sim/BacktestRunner.cs:55-76`.
- **Ź/T:** IS odrzuca exit na/po cutoff; selector bez stanu, missing neighbors nie premiują ujemnego wyniku. Oddzielne runy IS/OOS mają świeży world/engine/store. `tools/Apex.Backtester/Research/WalkForward.cs:8-39`; `tools/Apex.Backtester/Research/ResearchPipeline.cs:438-459`; `tools/Apex.Backtester/Sim/BacktestRunner.cs:71-96`.
- **Ź/T:** natural-only selection jest oddzielona od pełnego money evidence: boundary liquidations, wcześniejsze partials, fees i equity pozostają w raporcie konta. Suma niezależnych kont nie udaje compounded portfolio/DD. Logical cutoff i effective liquidation time są osobne. `tools/Apex.Backtester/Research/IsolatedRun.cs:25-59`; `tools/Apex.Backtester/Sim/BacktestRunner.cs:118-131`.
- **Ź/T:** loader hashuje ten sam snapshot bajtów co parsuje, wymaga jawnego price basis i dokładnych session-gap declarations; nie dopisuje świec. BID/ASK→MID to jawny model. `tools/Apex.Backtester/Data/DataContract.cs:83-135`.
- **T:** w `backtester.trx` PASS dla accounting/invalid FX/partial-final/swap, cutoff leakage, izolacji runnera i complete boundary exports. Definicje m.in. `tests/Apex.Backtester.Tests/AccountingFxTests.cs:29-87`; `tests/Apex.Backtester.Tests/WalkForwardTests.cs:39-64`.
- **U/B dla OKX:** to nadal model CFD, USD i D1 New York+7h, nie giełdowy UTC spot/cash. USDT/USDC nie są obsługiwane; potrzebne własne instrument units, kalendarz, fee per fill, FX i provenance. `tools/Apex.Backtester/Data/MarketDataStore.cs:46-84`; `tools/Apex.Backtester/Data/MarketDataStore.cs:148-163`; `tools/Apex.Backtester/Sim/SimWorld.cs:198-208`.
- **Bez wyniku finansowego:** przeliczono CSV istniejących prób: 64 = 48 fixed + 16 WF; wszystkie exit2/success=False na missing intervals. To nie 64 zaliczone badania ani zero profit/no-trade. `reports/autonomy/task4/attempts.csv:1-65`. N/net/WR/PF/DD/OOS niedostępne, brak przewagi (`reports/autonomy/task4-summary.md:50-67`).

## 3. Obecny API / telemetry risk — nadal oddzielna praca Naval

| Status | Potwierdzona granica / konsekwencja |
|---|---|
| Ź: nowy status scope | API publikuje scope/certificate/deficits/epoch/nextAction; wykorzystać w read modelu, nie wracać do logowego PASS. `src/Apex.Desktop/Runtime/AccountRuntime.cs:672-678`; `src/Apex.Desktop/Runtime/ScopedAccountRuntime.cs:142-160`. |
| B: trwały stream UI | DecisionRecorder nadal mutuje ostatnią wersję sygnału; `Since` tnie do 300 i zwraca globalny seq bez gap/hasMore. Restart zeruje seq. Księgowy outbox NIE jest event bus dla animacji. `src/Apex.Core/Engine/DecisionTrace.cs:154-165`; `src/Apex.Core/Engine/DecisionTrace.cs:208-255`. |
| Ź: ryzyko prezentacji | Stary poll nie sprawdza startedUtc/request generation po await ani nie anuluje fetch przy zmianie konta; stare odpowiedzi/animacje są ryzykiem źródłowym, bez nowej reprodukcji. `src/Apex.Desktop/wwwroot/decision-center.js:701-759`. |
| Ź: brak pełnego settled feed | Publiczne routes to snapshoty/decisions/instrument/commands/CSV, nie raw-deal timeline z kursorem. Status ma realized total, lecz daily/totalPnl pozostają equity deltas. `src/Apex.Desktop/Program.cs:114-199`; `src/Apex.Core/Engine/TradingEngine.cs:1788-1836`. |
| Ź: okna danych | AllRecent/statystyki to max500, recentTrades30; endpoint CSV nadal eksportuje bufor, nie całą księgę. To ograniczenie prezentacji, NIE utrata trwałych dedupe keys. `src/Apex.Desktop/Services/FileServices.cs:182-214`; `src/Apex.Desktop/Runtime/AccountRuntime.cs:638-645`; `src/Apex.Desktop/Runtime/AccountRuntime.cs:692-697`; `src/Apex.Desktop/Program.cs:193-198`. |
| Ź: komenda ≠ fill | POST zwraca message, także przy semantycznej odmowie; brak publicznego command receipt/idempotency lifecycle dla UI. Animacja nie może uznać HTTP200 za FillConfirmed/PositionFinalized. `src/Apex.Desktop/Program.cs:139-164`. |
| Ź: security lokalne | Loopback, Host allowlist, token, CSP istnieją. Ten sam token pozwala czytać i sterować; HTML bootstrap ujawnia go lokalnemu klientowi, status ujawnia pairing key/dataDir. Nie jest to gotowa rola read-only/mobile. `src/Apex.Desktop/Program.cs:55-109`; `src/Apex.Desktop/Program.cs:114-123`. |

Nowy frontend potrzebuje osobnego minimalnego DTO bez sekretów, epoch+snapshot watermark, cursor/gap/reset, account/fleet/venue identity i typowanych etapów decision→submitted→fill→protection→settled. Nie wyliczać cashflow z samej zmiany units ani animować powtórnego zysku przy replay. To **wymagania przyszłe**, nie brak autorytatywnego księgowania C#.

VPS/mobile: pozostawić listener loopback; osobna autoryzacja urządzenia/użytkownika, TLS/tunel/gateway i serwerowy podział read/operator. Nie wystawiać `/bridge` jako UI streamu. Dostarczony host może działać bez przeglądarki, lecz sama ta cecha nie potwierdza Linux service/deployment ani zdalnego cTrader (`src/Apex.Desktop/Program.cs:201-234`).

## 4. Co nadal brak i minimalny bezpieczny następny etap

- **B:** inspekcja `src/` nie znalazła Naval/OKX/FleetId; `package.json:1-12` ma tylko stary harness, bez React/R3F. Obecne routes nie zawierają `/naval` (`src/Apex.Desktop/Program.cs:101-199`). Zachować `/` jako polski fallback; React/TS/R3F dopiero równoległy klient, nie nowy authority.
- **B:** ALFA/BRAVO, ich wspólne rezerwacje i cash-spot risk nie wynikają z dwóch istniejących silników/etykiet. Scoped dedicated account odmawia obcych pozycji; nie obchodzić tego, tworząc drugiego writera. ALFA/BRAVO to domeny strategii, nie automatycznie nazwy venue cTrader/OKX. `src/Apex.Core/Engine/ScopedRecovery.cs:337-357`.
- Zachować defaulty 0,5/1,5/5/x5 i protective SL/TP cTrader. Walidator dopuszcza szersze nastawy, co nie jest zgodą na podniesienie limitów Naval; brak lewara w nowym crypto wymaga modelu cash, nie cap=0. `src/Apex.Core/Risk/RiskManager.cs:11-26`; `src/Apex.Core/Risk/RiskManager.cs:43-54`.
- Zachować unconditional LIVE refusal i Crypto block cTrader. Oddzielny przyszły OKX DEMO musi mieć własną tożsamość/authority; nie przenosić cTrader certyfikatu i nie kasować globalnie blokad. `src/Apex.Core/Safety/AccountGuard.cs:57-64`; `src/Apex.Core/Engine/TradingEngine.cs:1127-1130`; `src/Apex.Core/Engine/ExecutionCertification.cs:46-51`.
- Obecny DailyTrend/Donchian nie jest Naval40/30/BTC SMA200: ma dodatkowe first-break/body/regime/short, defaults55/20. Zachować starą strategię; osobny czysty evaluator. `src/Apex.Core/Strategies/DonchianBreakoutStrategy.cs:52-84`; `src/Apex.Core/Strategies/DonchianBreakoutStrategy.cs:114-144`.
- **Etap pierwszy:** nowe czyste typy i evaluator w `src/Apex.Core/Naval` + read-only CLI z plikami zamkniętych D1/as-of BTC, deterministycznymi decyzjami i testami cutoff/future-invariance. Bez enrollment w StrategyBook/runtime, gateway/brokera/sieci/kluczy/zleceń, bez P&L/sizingu udającego kompletny spot. To jeszcze nie implementacja flot/rentowności/frontendu.
- Później: fleet/central admission z rezerwacjami; model spot i pełne dane; telemetry/read API i frontend; opcjonalny adapter DEMO po osobnym odbiorze. Nie duplikować istniejącej księgi ani ponownie otwierać przyjętych Task2/Task3 bez konkretnej zmienionej granicy.

## 5. Zachowany dowód wykonania — exact819cba9, bez rerunu

- Odczyt `gh run view` potwierdził [40360216699](https://github.com/kubson1995xx-cloud/bottt/actions/runs/40360216699), completed/success, **headSha zgodne z bazą raportu**; job `121703319703`.
- Pobrano `APEX-Test-Evidence`, artifact `12668650741`, do scratch `/home/ubuntu/.hermes/cache/scratch/apex-naval-819cba9-evidence/`; parsowano rzeczywiste `UnitTestResult`, nie tylko summary/HANDOFF.
- TRX `APEX Windows DEMO/Weryfikacja/`: **bot634 PASS, platform126 PASS, backtester64 PASS**, każde zero failed/skipped; liczniki XML zgodne z wynikami. To osobne suite, nie suma historycznych targeted runs.
- Log jobu `windows-job.log`: build0 warnings/errors (170-172), 10 controlled package fixtures (195), panel16 PASS/0 FAIL (303-304), **prawdziwy Windows EXE smoke PASS bez kont/zleceń/brokera** (333). Runner windows-2022/PS5.1: `.github/workflows/windows-demo.yml:17-35`.
- Archiwalny AI Lab: **180 PASS /1 FAIL**, `TamperedWorktreePointer_StopsCollection`; nie nazywać go zielonym. Nie jest dostarczany ani bramką produktu (`.github/workflows/windows-demo.yml:36-44`).
- Nie pobierano/nie weryfikowano ponownie ZIP produktu; brak nowej deklaracji CRC/manifest tego pakietu. Run/EXE smoke nie dowodzą Windows11 użytkownika, cTrader/OKX E2E, LocalStorage durability, mobilnego WebGL ani przewagi strategii.
- Porównanie roboczych śledzonych wejść produktu/testów/config/build z HEAD nie wykazało zmian. Obcy nieśledzony mission doc pozostawiono nietknięty; nie deklaruję czystości całego współdzielonego checkoutu. Nie zmieniano kodu, konfiguracji ani stanu Git.
