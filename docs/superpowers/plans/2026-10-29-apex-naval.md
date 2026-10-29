# APEX NAVAL COMMAND Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Zachować aktualny APEX i etapowo dodać bezpieczne OKX DEMO oraz zdarzeniowe centrum dowodzenia; w pierwszym wykonaniu dostarczyć wyłącznie sprawdzony offline replay sygnałów.

**Architecture:** Rozbudowa istniejącego C# Core i ASP.NET hosta, bez nowego silnika. Nowy frontend równoległy `/naval/`, istniejący `/` jako fallback. OKX ma odrębną kompozycję/authority, floty wspólny serwerowy risk, trwałe rozliczenie przed publikacją wyniku.

**Tech Stack:** Core net6/C#10, host/tools/tests net8, xUnit, React/TypeScript/Vite, R3F/Three lokalne assets, istniejące Node/PowerShell/Windows CI; Python opcjonalny research, nie execution.

**Spec:** `docs/superpowers/specs/2026-10-29-apex-naval-design.md`; pełne zlecenie `.../2026-10-29-apex-naval-mission.md`.

## Global Constraints
- Baza `819cba9ba12c14b44577508af83417f70c2e5a86` (nowsza niż a886a15); gałąź `feature/apex-naval-command`. Zachować zaakceptowane recovery/certification/partial/backtester, nie odtwarzać ukończonych prac.
- Tylko opcjonalne OKX backtest/DEMO; REAL/LIVE bezwarunkowo zablokowane, żadnej transakcji DEMO bez osobnej zgody. Bez withdrawal/transfer/borrow/derivatives/short.
- Zachować cTrader/MT5 Crypto refusal, stary polski panel, profil CFD, istniejące risk defaults i mandatory SL/TP. Nowa ścieżka ≤0,5% trade /1,5% daily /5% DD, cash spot ≤1×, centralny max3, Bravo max2 wliczając pending.
- Brak AI Lab/LLM/training/updater; sekrety nigdy w repo/frontend/logach.
- Wersja reguł naval-d1-v1: D1 UTC, long-only close>previous40high, close<previous30low; BTC close>SMA200 tylko nowe wejścia; równość nie wyzwala; wykonanie nie wcześniej niż kolejny tick/open. Szczegóły ochrony/rankingu w spec.
- Nie produkować fikcyjnych danych/statystyk. Syntetyczne fixtures tylko jawne testy; sygnał nie jest fill ani P&L. Brak FX/coverage/cost/fees oznacza unknown/refusal.
- gpt-6.1-sol/openai-codex bez fallbacku. Read-only/bounded testy proporcjonalne do delty; istniejące niezmienione dowody używać ponownie.

## Review Focus
- Future/incomplete/stale BTC lub instrument, granice 40/30/200 i niezależność wyjścia od filtra: Task1 testuje prefix-invariance, brak warmup i niepotwierdzone dane.
- Sygnał/request/ack mylony z fill/settled zyskiem: Task2 output wyłącznie decyzje; Task6 stream duplicate/partial/gap/restart i jawny quality.
- Dwie floty lub pending konsumują to samo saldo/symbol: Task4 testuje atomowe wspólne rezerwacje i cross-fleet ownership.
- Regionalny OKX REST wspólny dla LIVE/DEMO, klucz o złym środowisku lub redirect: Task5 testuje demo header/authority/endpoint deny/no fallback przy każdej próbie.
- Mobile/3D awaria lub konto A odpowiada po zmianie na B: Task6/7 testują role backend, stale/generation/epoch, context loss i niezależny fallback/emergency.

## Zakres wykonania teraz
**Task1–2 + niezależny review nowej granicy, tylko offline.** Task3–8 to jawnie nieukończona roadmapa z bramkami; nie stanowią obietnicy ich dostarczenia w tym samym przebiegu. Użytkownik wprost upoważnił po planie rozpocząć pierwszy bezpieczny etap. Nie instalować/uruchamiać usług ani nie podłączać rachunku w ramach tego slice.

## Task 1: Moduł sygnałów w istniejącym Core, bez execution
**Files:** Create `src/Apex.Core/Naval/NavalModel.cs`, `NavalFleetProfiles.cs`, `NavalD1Evaluator.cs`; Create `tests/Apex.Naval.Tests/Apex.Naval.Tests.csproj`, `NavalD1EvaluatorTests.cs`. Nie zmieniać StrategyBook, StrategyId, TradingEngine, RiskManager, cBotów, profilu ani panelu.

**Interfaces:**
- `NavalD1Evaluator.Evaluate(NavalEvaluationRequest request) : NavalDecision` — czysta ocena, brak gateway i side effects.
- Modele: `NavalFleet` (Alpha/Bravo), `NavalAction` (Wait/Hold/EntrySignal/ExitSignal), `NavalBar` (UTC open, decimal OHLCV, confirmed), `NavalSeries` (instrument/base/quote/source/priceBasis/bars), `NavalSettings` (40/30/200 default + walidacja), `NavalEvaluationRequest` (fleet/instrument/BTC/cutoff/hasLong/settings), `NavalDecision` (action/reason/cutoff/earliestExecutionUtc/levels/stableId/config identity).
- `NavalFleetProfiles.Get(NavalFleet fleet)` → niezmienny manifest ALFA BTC/ETH lub BRAVO BTC/ETH/SOL/XRP/DOGE/ADA/LTC/LINK/BCH/AVAX z parametrami badawczymi i domyślnym limitem pozycji (2). To deklaracja, nie produkcyjny admission.
- Zasilanie jawnie lokalnym as-of prefiksem, walidacja kolejnych UTC D1. Same-time BTC wymagane dla wejść, nie dla poprawnego wyjścia. Nie istnieje funkcja Submit/Fill ani wyliczenie pieniędzy.

- [x] Testy pierwsze: `EntryUsesExactlyPrevious40ClosedDays` (signal high nie w kanale, ekstremum poza40 pominięte, strict equality WAIT), `ExitUsesPrevious30AndIgnoresBtcEntryFilter` (wyjście posiadanego long bez/ze złym BTC), `BtcFilterRequires200AlignedClosedDays` (ETH wykorzystuje BTC, 199/equality/stale odmawia), `FutureMutationDoesNotChangeDecisionOrIdentity` (przyszłe OHLC obu serii), `RejectsBrokenDailyData` (duplicate/gap/order/UTC/OHLCV/unconfirmed), `NeverCreatesShort` (spadek flat ≠ sell), `ProfilesContainExactAssetsAndAreImmutable`, `InvalidSettingsFailClosed`, `EarliestExecutionIsAfterSignalCloseNotAtReferenceFill` (brak executed price/P&L).
- [x] Uruchomić oczekiwany RED na behavioral assertions przed logiką. Minimalne kontrakty mogą umożliwić kompilację; zawsze-Wait shell nie jest dostawą. Zachować RED log/TRX poza repo.
- [x] Implementować minimalny czysty evaluator, walidację, profile i stabilne ID/config digest; decimal/invariant culture, UTC, brak timestampów zegara w tożsamości, żadnych przyszłych OHLC w as-of identity.
- [x] Run `/home/ubuntu/.dotnet/dotnet test tests/Apex.Naval.Tests -c Release --logger trx --results-directory <scratch>/green-core`; Expected wszystkie nowe testy PASS, bez warning/error/skip. Zachować manifest inputów i wynik, nie zgadywać licznika.
- [x] Commit nowych modułów/testów po dowodzie; bez push. Bieżące API/model properties opisać w raporcie dla Task2.

## Task 2: Uruchamialny replay lokalnego JSON i dowód bezpieczeństwa
**Files:** Create `tools/Apex.Naval.Replay/{Apex.Naval.Replay.csproj,Program.cs,ReplayApplication.cs}`; Create `tests/Apex.Naval.Tests/ReplayApplicationTests.cs`; Create `docs/NAVAL_OFFLINE.md`; controlled fixtures pod `tests/Apex.Naval.Tests/fixtures/synthetic/` jeśli potrzebne. Oddzielny projekt net8 tool referencjonuje Core, nie Desktop/adaptery. Nie dodawać narzędzia offline do produkcyjnego pakietu jako bota.

**Interfaces:**
- Consumes Task1 `NavalEvaluationRequest`/`NavalDecision`.
- `ReplayApplication.Run(string[] args, TextWriter output, TextWriter error) : int` i `Program.Main` korzystają z tej samej ścieżki.
- CLI `--input <local.json> --output <local.json>`. Wersjonowany JSON `naval-replay-v1`, tablica żądań na jawne cutoffy, wynik OFFLINE/NO_EXECUTION + input SHA256/config/as-of identity. Schema dokładnie udokumentowana i fixture używa tej samej ścieżki. Nie zakładać holdingu/filla po EntrySignal; informacja o long jest jawna w żądaniu. Bez HTTP, credentials, URI download, broker host i wyboru live.

- [x] Najpierw testy `CliWritesOnlyOfflineDecisions`, `CliRejectsUnknownArgumentsAndWrongSchema`, `CliRejectsRemoteInputAndMissingFile`, `CliDeterministicAcrossRuns`, `CliRejectsOutputOverwritingInput`, `NoExecutionRouteOrFinancialClaims`; realne pliki i rzeczywisty evaluator/serializer, nie source-string jako jedyny dowód. Expected RED przed implementacją CLI.
- [x] Implementować walidację parametrów i schema, odczyt jednego snapshotu input bytes/hash, deterministyczny zapis/no overwrite input, atomowy output tam gdzie praktyczne. Invalid file/config/data nie daje success/fake prices; Wait z powodem dla niedostępności wskaźników. Czytelny stderr, nonzero dla invalid command/schema. Nie logować pełnego nieznanego wejścia.
- [x] Run nowy cały projekt testowy; Expected PASS. Uruchomić actual CLI na jawnej synthetic fixture dla ALFA/BRAVO, sprawdzić treść pliku output i exit code; drugi run identyczny wynik, brak zleceń i pól finansowych. Sprawdzić CLI error-path exit nonzero.
- [x] Ponieważ nowe Core source zmienia DLL, zbudować istniejące solution/SDK consumers i wykonać jeden affected Core/Desktop gate po zintegrowaniu, nie wiele powtórek. Platform/panel bez zmian mogą używać aktualnego Windows dowodu 819cba9; jawnie odróżnić reuse od nowego runu.
- [x] Niezależny reviewer ocenia wyłącznie nowy slice i jego granicę time/data/no-execution oraz zgodność spec. Każdy Important fix RED→GREEN, końcowy nowy suite dla zmienionych bajtów. Nie zlecać od nowa audytu całej autonomii.
- [x] HANDOFF: source revision, pliki, prawdziwe komendy/liczniki, artefakty/digests, ograniczenia i następny nieukończony etap. Commit, git status. Bez deklaracji UI/OKX/Windows nowej wersji, gdy tylko offline gotowe.

## Task 3 (ROADMAP): Rzeczywiste dane i wiarygodny backtest spot
Create `Apex.Adapters.Okx` read-only public client/dataset import, osobny spot cost/fill model w istniejącym backtesterze; korzystać z nowego DataContract i poprawionego FX/walk-forward. Paginacja history-candles/1Dutc/confirm, discovery10, manifest source/hash/gaps/listing/coverage. Prerejestracja dat/cost/param przed badaniem; testy brak przyszłości/next-open/gap SL-first/fee base-quote/rebates/missingFX. Odbiór: genuine-data raport wszystkich aktywów/flot (także failures/zero trades), net/cost/PF/DD/n z zakresem, OOS nie udaje untouched po ponownym użyciu. Bez aktywacji strategii z unit tests.

## Task 4 (ROADMAP): Fleet coordinator i model cash
W Core jawne venue/env/fleet IDs i cross-symbol context; profile opt-in i oddzielny admission dla cash, nie globalne CryptoAllowed. Jedna atomowa rezerwacja portfolio risk obejmuje obie floty i pending. Testy jednoczesny start, maxBravo2/central3, cash reserve+fees, same symbol owner, daily/DD latches/restart, foreign/unknown exposure fail-closed. Fleet ledger projekcją istniejącego trwałego authority, nie druga niezależna księga. Odbiór: nie da się przejść ponad centralny budżet przez dwa konta/floty/komendy ani zmienić CFD guardów.

## Task 5 (ROADMAP): OKX DEMO private read/recovery, później osobny test
Adapter REST/WS regionalnie pinowany, demo auth/header/permissions i endpoint allowlist, spot/cash/inventory, decimal kwanty, orders/fills/bills→durable dedupe. Testy fixed signed request (bez sekretów produkcji), timeout unknown/no blind retry, WS dupe/gap/terminal zero-fill, restart pagination/coverage, base fee/inventory. Read-only start, no auto-resume. Dopiero dostępne konto/bezpiecznie podane demo credentials i osobna zgoda pozwalają na genuine open/protection/modify/close/settlement. Odbiór connectivity/read nie jest trade E2E; wspólny host REST nie jest dowodem Demo. Subkonta tylko gdy account/region faktycznie pozwala.

## Task 6 (ROADMAP): Trwałe zdarzenia, bezpieczny read model, React shell
C# nowy Naval DTO/stream/controller i backend roles; `frontend/naval/` React/TS/Vite, `/naval/` bootstrap/hash routing, zachowany `/`. Snapshot watermark/cursor/epoch/gap, string IDs, money+currency, no pairing data. Testy fetch generation/account mismatch, restart cursor, duplicate/out-of-order/partial/settled, operator intent receipts i backend refusals. Pięć funkcjonalnych widoków podłączonych do actual danych, unknown/stale jawne. Odbiór typecheck/build/browserCSP/deep links/old panel, bez mock-only delivery.

## Task 7 (ROADMAP): Radar, floty 3D i lifecycle animacji
R3F/Three scene tylko read model; geometria/model assets lokalne/licencjonowane, 2D fallback, reduced motion/mute/minimal, bounded queue/DPR/FPS/disposal. Torpeda=submitted, fill tylko autentyczny fill event, wynik tylko settled ledger. Testy reject/partial/uncertain/disconnect/account-switch/GPU loss/replay bez podwójnego sukcesu. Benchmark docelowy desktop i telefon ze zdefiniowanym budżetem po poznaniu urządzeń, bez zmyślonego FPS. Odbiór pięć widoków i faktyczne lifecycle, nie statyczna makieta.

## Task 8 (ROADMAP): Integracja, Windows/VPS i odbiór
Świeży Windows build+tests+EXE smoke+manifest z frontend assets i kompatybilnymi cBotami; CI branch triggers jawnie obejmują nową gałąź dopiero przy dostawie, nie wymuszać push teraz. Linux net8 service/systemd private data/STOP po restarcie, prywatny mobile gateway/TLS/role/no public bridge. Testy crash/retention/storage/stale/safety/performance; independently review zmienione high-risk granice. Odbiór actual Windows CI artifact SHA/CRC/manifest oraz Linux target smoke, instrukcje PL i osobne genuine demo E2E. Brak brokera/Windows11/MT5/realnych danych nie może zostać przepisany na PASS przez nowy panel.
