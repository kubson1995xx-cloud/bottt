# APEX DEMO Autonomy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove audited continuity/safety and backtester defects, prove strategy behavior on actual production parameters, perform honest new research where genuine data is available, and ship a newly verified Windows DEMO package.

**Architecture:** Keep existing C# Core/Desktop/cTrader architecture. Broker-authoritative history reconciles into an idempotent local ledger; durable risk-reducing intents and account-bound execution certification govern automation. Research remains separate from live execution and never automatically changes strategy validation.

**Tech Stack:** .NET8 SDK/Core net6, xUnit, cTrader.Automate1.0.21, vanilla JS/linkedom, Windows PowerShell5.1, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-10-26-apex-autonomy-design.md`

## Global Constraints
- Wyłącznie Pepperstone cTrader DEMO. REAL zablokowany w każdej dostarczonej ścieżce.
- Prawdziwa transakcja DEMO wymaga osobnej przyszłej zgody użytkownika. Nie uruchamiać brokerowego START/testu w ramach implementacji.
- Bez AI Lab, treningu, LLM podejmującego BUY/SELL i automatycznych aktualizacji. Archiwum nie jest produktem.
- Zachować domyślne 0,5% ryzyka, 1,5% limitu dnia, 5% DD, x5, obowiązkowe SL/TP i Emergency Stop.
- Nie aktywować odrzuconych strategii ani oznaczać Validated bez autentycznych dowodów. Nie używać dawnych CSV jako wyników nowej wersji.
- Model/provider kodu i rozumowania: gpt-6.1-sol / openai-codex, bez fallbacku.
- Żadnych sekretów w repo. Commity etapowe i aktualny HANDOFF z dowodami i ograniczeniami.

## Review Focus
- Crash between dedupe/state save and CSV append must recover exactly one full economic closed record, not lose net or double count it.
- History pagination/reconnect with duplicate timestamps, partial deals and foreign labels must block new risk until an authoritative complete snapshot is committed.
- Restart during uncertain partial/full close must never repeat volume reduction blindly or clear the durable obligation prematurely.
- Journal compaction must preserve uncertain reservations and prevent replay of terminal old opens even after long uptime/restart.
- Missing FX, walk-forward boundary positions and changed production parameters must invalidate or purge misleading research instead of quietly producing optimistic results.

## Task 1: Broker history, idempotent accounting and restart-safe close

**Files:**
- Modify Core `Engine/{Interfaces,EngineConfig,TradingEngine}.cs`, `Model/Market.cs`, `Bridge/Protocol.cs`, `Performance/Performance.cs` as required.
- Modify Desktop `Bridge/{BridgeSession,SessionAdapter,BridgeServer}.cs`, `Runtime/{AccountRuntime,AccountManager}.cs`, `Services/FileServices.cs`.
- Modify cTrader `ApexBridge/ApexBridge/ApexBridge.cs`, `ApexTradingAI/ApexTradingAI/ApexTradingAI.cs`; add focused shared history mapper under `Shared/` if needed.
- Modify simulator gateway/protocol for compatible local integration (no real broker).
- Test new `tests/Apex.Tests/RecoveryTests.cs`, `HistoryIntegrationTests.cs` and shared `src/Platforms/cTrader/Tests/HistoryTests.cs`; existing affected suites.

**Interfaces:**
- Consumes current `IBrokerGateway`, `ITradeJournal`, `ClosedPositionInfo`, account identity, exact-label ownership and `EngineState`.
- Produces an explicitly documented optional history-capability contract (or protocol version) with complete snapshot readiness, an idempotent closed-record key and durable pending-close/outbox state. Consumers Task2 must use the resulting actual types/methods documented in task1 report, never bypass synchronization. Prefer position-aggregate terminal closed records from authoritative cTrader deals; preserve partial-deal cost sums. Check SDK members in installed XML before coding.

- [ ] Write regression `DuplicateClosedEventAndReplayCountOnceAcrossRestart` exercising actual TradingEngine and durable journal; assert one final CSV row and one PnL credit across duplicate, replay and restart. RED first.
- [ ] Write `ReplayOfflineClosureRestoresNetCommissionSwap`, `PartialDealsAggregateOnlyWhenPositionFullyClosed`, `ForeignAndIncompleteHistoryCannotCertifySync`, `HistorySnapshotSurvivesReconnectAndChunkBoundaries`, `JournalCrashBeforeAndAfterAppendRecoversExactlyOnce`, `CorruptRecoveryStateBlocksNewRisk`, `FullCloseIntentRetriesAfterRestart` and `FullCloseStillWorksOnStorageFault`. Use actual stores/serializer and local WebSocket fixture where applicable.
- [ ] Run focused tests to observe expected RED; save TRX/logs outside repo under `/home/ubuntu/.hermes/cache/scratch/apex-autonomy/task1/`.
- [ ] Implement dedupe, durable idempotent journal/outbox, authoritative SDK history retrieval and aggregation, complete host sync handshake and new-risk barrier, state migration and durable full-close intent/retry. History must not be removed or certified complete before durable acceptance. SDK history must only be read on platform thread. No fake R when entry plan missing. Recovery after error/reconnect stays STOP; protective close remains possible.
- [ ] Add shared executable mapper tests and genuine host-process synthetic-frame integration covering history/position/SL/TP/net state and reconnect; no real broker or trade.
- [ ] Run full Core/host suite, platform suite and solution build. Compare original existing tests against new safety semantics, adapt fixtures honestly instead of disabling gates.
- [ ] Update HANDOFF and protocol documentation for new sync/compatibility behavior. Commit all task1 changes. Report RED/GREEN commands/logs, true counts, SDK members, APIs produced, replay bounds and any concern.

## Task 2: Account certification, safe partial close, journal compaction and stale-data barriers

**Files:**
- Modify Core `Engine/{EngineConfig,TradingEngine}.cs`, `Management/PositionManagement.cs`; cTrader shared `CommandJournal.cs` and both cBot entrypoints; Desktop runtime/status and panel `wwwroot/app.js` as needed.
- Test `tests/Apex.Tests/AutomationSafetyTests.cs`, `PartialRecoveryTests.cs`, shared `CommandJournalTests.cs`, `tests/panel/safety.test.cjs`; compatible fixture helpers.

**Interfaces:**
- Consumes Task1 actual recovery/history readiness contracts and durable close ledger.
- Produces `EngineStatus` readiness/certification fields for panel and durable account/execution-version PASS; implementation must document actual names and migration. Backtest exception must be structurally isolated and incapable of being set by bridge/client input.

- [ ] Write `FirstStartRequiresVerifiedDemoExecutionPass`, `PassPersistsOnlyForMatchingAccountAndExecutionVersion`, `IncompleteTestNeverCertifies`, `BacktestCanStartWithoutRealTrade`, `EmergencyCloseNeverRequiresCertificate`; observe RED.
- [ ] Write partial tests for rejection→retry, uncertain→volume reconciliation, restart after confirmation-before-save, minimum-volume terminal state and no repeated volume reduction. Observe expected RED before product edits.
- [ ] Write journal regression >20000 terminal ops with safe compaction, old open replay refusal, id/fingerprint conflict, uncertain/live reservations retained, restart, corrupt/failed compaction, historical v1 migration. Test actual journal class, not source strings.
- [ ] Add broker-connected/stale snapshot regression for reconnect during Running and during test; stale quote/history/account cannot reopen risk and no expired candidate revived. Actual Desktop and Native boundary must publish broker connectivity honestly, not infer it from local heartbeat only.
- [ ] Implement durable PASS only after open/protection/modify/close all verified; UI shows required sync/test and disables START, same check in engine. Keep technical test usable before certificate and full close/emergency usable without it.
- [ ] Implement durable partial intent/confirmation and conservative uncertain handling; completion flag after confirmed economic reduction only. Implement safe bounded journal lifecycle with replay boundary/tombstones, never evict active/uncertain reservations to make room. Explain operational boundaries in docs.
- [ ] Run Core/platform/panel complete suites and build both cBots. Update HANDOFF and user-facing exact STOP/reconnect/test semantics. Commit and report every new gate/interface.

## Task 3: Correct backtester accounting, FX, fees and walk-forward

**Files:**
- Modify `tools/Apex.Backtester/{Program.cs,Data/Catalog.cs,Data/MarketDataStore.cs,Sim/SimWorld.cs,Research/ResearchPipeline.cs}` and runner as needed.
- Create `tests/Apex.Backtester.Tests/Apex.Backtester.Tests.csproj` and focused executable regression tests, reference actual backtester source/project; add to DEMO solution, package script and Windows CI gates.

**Interfaces:**
- Consumes Core risk/fee/strategy APIs after Task2. Produces corrected backtester, configurable/reproducible research runner with independent fold boundaries, cost/data provenance and machine-readable reports. Document actual CLI flags rather than inventing future calls.

- [ ] RED tests `EquityEqualsBalancePlusPositionNetForCommissionAndFx`, partial→full cashflow consistency, fee floor per operation with broker minimum, all cost scenarios consistent. Use deterministic simple real production objects, not old trade CSV assertions.
- [ ] RED tests missing/corrupt/stale/not-yet-known FX rejects credible simulation rather than fallback1; same-currency needs no external rate.
- [ ] RED tests an IS-entry/OOS-exit result cannot affect selection, no OOS results used as training, independent folds don't carry later state, neighbors penalty not reward negative objectives through missing data.
- [ ] Implement one coherent cashflow/mark-to-liquidation model, explicit FX validity errors/data coverage, per-side minimum in catalog/size/fill and independent correctly purged WF. No relaxing risk/cost to manufacture trades or success.
- [ ] Support bounded research date ranges and auditable provenance/config for actual acquired data where current hardcoded ranges prevent honest reruns. Validate market OHLC/timestamps/gaps/coverage and distinguish warmup from evaluation.
- [ ] Run new complete suite, Core/platform/panel, solution build; update CI/package script to require the new suite and record counts in package metadata. Commit and document changed semantics; old results explicitly obsolete.

## Task 4: Six strategy regression suites and new evidence-based research

**Files:**
- Create `tests/Apex.Tests/StrategyBehaviorTests.cs` (may split by family), modify only necessary production strategy bugs with RED proof.
- Research tools/data adapters only if needed for actual acquired formats; no changes to `DefaultProfile` validation/Enabled absent proven results and explicit recorded decision.
- New reports `reports/autonomy/` metadata and summaries (do not commit massive market datasets); data under scratch/cache with reproducibility manifest.

**Interfaces:**
- Consumes Task3 research CLI/test project plus authentic dataset/provenance at `/home/ubuntu/.hermes/cache/scratch/apex-autonomy-data/DATA-HANDOFF.md` if available.
- Produces exact per-strategy tests, source-data manifest/hash, versioned new IS/OOS/holdout trade/equity/statistics outputs, or exact retrieval/coverage blockers. No stale historical result as proof.

- [ ] For each TrendPullback, DonchianBreakout, MeanReversion, MomentumImpulse, DailyTrend write BUY and SELL positive/negative/boundary, SL/TP and strategy exits. For IndexDipBuy assert BUY/no short using actual `DefaultProfile.Load()` parameters SMA100/RSI2<5/SL3ATR/10bars plus executed protective TP3R.
- [ ] Add future-bar perturbation invariance and real manager/core integration for exits and protection. Tests added for existing correct behavior may pass initially; every actual bug fix still requires observed RED first, do not distort strategy semantics simply to create RED.
- [ ] Run complete suites and fix real defects narrowly; never auto-enable Rejected or mark Validated based on green unit tests.
- [ ] Validate downloaded real data provenance/coverage/cost assumptions; run all six families in justified feasible multi-class scope with separate IS/OOS/untouched holdout and corrected live-equivalent TP. Report n, net, WR, PF, DD, costs, uncertainty and missing coverage. Don't synthesize missing candles/currencies or label MID model as actual broker bid/ask.
- [ ] If full evidence is unavailable, finish reachable tests/pipeline and record exactly which strategies/classes remain unvalidated and why. Keep active strategy set unchanged unless credible new proof supports a deliberately documented promotion (default no promotion).
- [ ] Commit tests and compact new reports; HANDOFF includes reproducible commands and actual artifact paths/hashes. Full research data remains outside Git.

## Task 5: Independent review, Windows integration and final package

**Files:**
- Update Windows scripts/workflow if task gates require it; docs `INSTALACJA_WINDOWS`, `PROCEDURA_DEMO_PEPPERSTONE`, `WYNIKI_TESTOW`, `OGRANICZENIA_I_STATUS`, README and HANDOFF. Preserve Polish panel and no AI Lab.
- Focused regression tests for final review findings; artifact output outside tracked source.

**Interfaces:**
- Consumes all task reports and exact integrated commit; produces tested ZIP, manifest/hash and final truthful handoff linked to Windows run and active strategies.

- [ ] Generate whole-branch review package from a886a15 to candidate, fresh independent reviewer, resolve Critical/Important findings via RED→GREEN tests and fresh suite evidence; record decisions.
- [ ] Run full solution build, all product suites including new backtester, panel/check and actual synthetic host E2E with recovery/replay, certification and SL/TP/net synchronization. No real broker commands.
- [ ] Commit/push authorized working branch/review branch, verify remote SHA, run Windows Actions for actual candidate; verify published EXE smoke and product gates, obtain artifact/download and recheck ZIP/manifest/hash against latest bytes. Do not confuse CI YAML with completed run.
- [ ] Deliver new Windows ZIP with simple local pairing, first-run read-only sync and explicit separately approved execution test; keep READY/START gated until that test. Broker acceptance remains open.
- [ ] Final report: implemented changes/tests, new OOS results or limitations, active strategies, costs/DD/n, pending real cTrader/Windows11 checks. No guaranteed profitability, no AI Lab/REAL and no unapproved transaction.
