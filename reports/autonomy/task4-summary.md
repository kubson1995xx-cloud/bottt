# Task4 — strategy behavior complete; genuine-data research blocked

## Outcome and safety boundary

- Base `351e4897480f11dce981cf664c4cfe503e2a05d7`; frozen preregistration commit `90f4941409a277d4af6572ae4074a37a1e15b20a`. No production source, default profile, strategy activation/status, risk, SL/TP, Emergency, REAL refusal or broker path was changed. No production bug was proven; **no production RED→GREEN fix is claimed**.
- **69 new distinct latest passing cases**: six actual strategy behavior suites **65/65**, plus real production-profile Core→SimWorld protective execution **4/4**. These are controlled unit/execution fixtures, not market research candles or performance results.
- **64 genuine-data CLI attempts: 48 fixed-profile invocations + 16 bounded WF invocations; all exit2 / success:false at strict data preflight.** No account, parameter combination, training selection, OOS, stress or candidate-holdout strategy outcome completed. Financial metrics are **unavailable**, not zero profit / zero trades. Research remains unvalidated for every family/class.
- Exact evidence root: `/home/ubuntu/.hermes/cache/scratch/apex-autonomy/task4-strategies-research/`. All failed `research/<attempt-id>/run.json`, console logs, exact argv, profile copies, adapted data and complete gap observations are retained. `reports/autonomy/task4/attempts.csv` enumerates every attempt and hashes its authoritative failure report.

## Six implemented behavior suites

| Family | New passing cases | Genuine-data scope / attempted invocations | Research status |
|---|---:|---|---|
| TrendPullback | 12 | EURUSD Forex, XAUUSD Metal, US500 Index / 12 | All blocked; unvalidated |
| DonchianBreakout | 12 | Same three classes / 12 | All blocked; unvalidated |
| MeanReversion | 12 | Same three classes / 12 | All blocked; unvalidated |
| MomentumImpulse | 12 | Same three classes / 12 | All blocked; unvalidated |
| DailyTrend | 12 | Same three classes / 12 | All blocked; unvalidated |
| IndexDipBuy | 5 + 4 execution | US500 Index only / 4 | All blocked; unvalidated; Forex/Metal intentionally inapplicable |

`tests/Apex.Tests/StrategyBehaviorTests.cs` calculates real indicators through FeatureSet. Five two-sided families exercise BUY/SELL positives, invalid/chaotic/insufficient/flat negatives, admission and exit equality boundaries, actual ATR stop / strategy TP semantics, real CheckExit→PositionManagerRules close actions and future-bar perturbation invariance with closed-time indexing. DailyTrend is instantiated as the real D1 family, not renamed H4 results. Regime assessments are explicit unit inputs; these tests do not claim real-feed regime accuracy.

IndexDipBuy loads **actual `DefaultProfile.Load()` through StrategyBook**, asserting SMA100, RSI2<5, exitSMA5, SL3ATR, max10bars; BUY/no-SELL, RSI/equality/chaos/warmup negatives, future invariance, and actual TradingEngine dispatch/SL/TP followed by strategy-SMA and tenth-bar manager closes. Its strategy TP is deliberately null; Core supplies protective **3R**.

`tests/Apex.Backtester.Tests/ProductionDipProtectionBehaviorTests.cs` executes the actual default-profile Core bracket in existing trusted SimWorld, with unchanged production risk limits. Four cases prove **TP hit, SL hit, simultaneous SL-first and adverse-gap fill**, then reconcile actual simulated account cash deltas. This is local H1 execution proof, not a broker/tick/DEMO transaction.

### All test attempts, including fixture failures

| Artifact under `tests/` in evidence root | Passed | Failed | Meaning |
|---|---:|---:|---|
| behavior-attempt1.trx | 65 | 0 | First execution; existing correct strategy behavior |
| protection-attempt1.trx | 0 | 4 | New fixture omitted backtest composition; no position |
| protection-diagnostic.trx | 0 | 1 | TP-only diagnostic: DecisionRecorder empty; no dispatch |
| protection-attempt2.trx | 4 | 0 | Test-only composition correction, all protective cases pass |

Cause: direct SimWorld lacks broker-history implementation. Core captures `_simulation = cfg.IsBacktest && trustedGateway` at construction. The fixture initially omitted IsBacktest; matching the **existing BacktestRunner trusted composition** fixed the fixture without adding/changing any production bypass. No losses/results were fabricated and no product defect was inferred from the empty-position assertion. Reasons are recorded before each test command in `tests/*-reason.txt`.

All **163 retained inputs** match accepted Task3 `fix1/final-source-hashes.json`; 3 new inputs (two test files and adapter) produce a 166-input map. Accepted **60 Backtester** and earlier **569 Core/Desktop / 126 platform / 16 panel** evidence are retained, not rerun or presented as a new integrated total. No substantial production/integration delta justified another full gate. New focused builds/tests compiled without new warnings; explicit Backtester build: 0 warnings/errors. No SDK, Windows package or panel acceptance rerun.

## Frozen genuine-data experiment and exact blockers

Only native same-provider Dukascopy BID H1 was used. Adapter `tools/data/task4_research.py` independently rehashed/recounted/validated every source row (strict order, H1 UTC alignment, finite positive consistent OHLC and nonnegative volume), changed timestamp/header/filename only, and reread **all five numeric tokens in every row verbatim**. It does not download, fill, drop candles, infer session closures or splice feeds. Hashes, annual counts, intervals and adapted manifests are in `task4/data-provenance.json`.

| Symbol | Verified H1 rows | Observed gap events (NOT outage counts) | IS warmup/eval H1 | OOS warmup/eval H1 | Candidate warmup/eval H1 |
|---|---:|---:|---|---|---|
| EURUSD | 99804 | 850 | 12488 / 43622 | 12475 / 18705 | 12465 / 12476 |
| XAUUSD | 95765 | 3093 | 12210 / 41617 | 11837 / 17712 | 11805 / 11851 |
| US500 | 70979 | 3565 | 6521 / 34922 | 11798 / 17698 | 11775 / 11802 |

Periods stayed frozen: IS `[2014,2021)` with 2012 warmup; OOS `[2021,2024)` with 2019 warmup; candidate `[2024,2026)` with 2022 warmup. Earlier overlapping history is reused; candidate years are **not certified untouched**. Two nominal years of warmup are not proof of complete D1/session history.

Every attempt failed on a precise **undeclared missing interval** before strategy evaluation. First failures by symbol/period (UTC):
- EURUSD IS/WF: `2012-01-01 00:00 → 2012-01-01 22:00`; OOS: `2019-01-01 00:00 → 22:00`; candidate: `2022-01-01 00:00 → 2022-01-02 22:00`.
- XAUUSD IS/WF: `2012-01-01 00:00 → 23:00`; OOS: `2019-01-01 00:00 → 23:00`; candidate: `2022-01-01 00:00 → 2022-01-02 23:00`.
- US500 IS/WF: `2012-01-01 00:00 → 2012-01-16 00:00`; OOS: `2019-01-01 00:00 → 23:00`; candidate: `2022-01-01 00:00 → 2022-01-02 23:00`.

These are first failures, **not the only missing intervals**. Independent US500 audit also finds `2011-09-23 07:00 → 2012-01-16 00:00` (2753 absent grid hours) and `2013-01-31 22:00 → 2013-03-01 02:00` (676). Provider instrument metadata contains start dates/descriptions, not verified historical session calendars. A holiday/weekend-looking interval is not independently authenticated merely by its shape. Therefore no session gap was declared; no blanket legitimation or loader weakening. Multi-month success and endpoints do not establish historical session coverage.

### Cost, coverage and preregistration implementation qualifications

- Base cost, H1-only, fx-max-age2h in all attempts. Catalog modeled spreads: EURUSD 0.00003, XAUUSD 0.20, US500 0.5, rollover x3; FX/metals $3.50/lot/side, minimum $0.50 per executed operation; US500 zero assumed commission/floor. Catalog slippage and annual financing 2.5% are assumptions, **not verified Pepperstone historical costs**. USD→USD identity needs no external FX and no fallback rate was used.
- BID→modeled MID conversion is declared, not actual ASK/MID or tick execution. Sparse M15, native-H1/minute inconsistencies, provider best-price volume (not tick/traded volume), historical calendars and broker/feed equivalence remain limitations. No volume-sensitive validation is claimed.
- Fixed attempts copied exact actual DefaultProfile parameters to isolated offline Experimental profiles. Embedded default and config JSON were verified semantically identical. Only copied Enabled/Status/Evidence fields differ; production Rejected/Enabled/status remain untouched. Data preflight failed **before those profiles could be evaluated**.
- Frozen budget was 48 fixed CLI invocations and 16 WF invocations, max2 combinations / max160 isolated runs / parallel1 per WF. **0 combinations/0 accounts executed.** No grid expansion, cost loosening, retuning or failed-run retry was used.
- Preserve, do not rewrite, the frozen preregistration: its phrase “48 fixed accounts” maps to 48 `portfolio` invocations; existing CLI would evaluate scoring0/50 (up to96 fixed accounts if preflight passed). Existing WF CLI uses **IsolatedConfig risk0.1%, daily10%, DD30%, leverage30**, not production0.5/1.5/5/x5. This is an implementation qualification to the preregistered intent, NOT a changed production default or a run at production risk. Both paths failed before accounts; no production-risk WF claim is possible. The 2-combo grid is pipeline-default-first plus first nondefault entry, not a full robustness grid; even it was never reached.
- **n/net/WR/PF, fees/swap, natural vs boundary positions, partial cashflows, closed-trade DD and full-account sampled equity DD are all unavailable.** No eligible/no-eligible training determination or successful zero-trade result exists. Task3 full-money/boundary-partial machinery is unchanged and its accepted behavioral proof is reused, not substituted for genuine-market outputs. No independently funded DDs are summed. Uncertainty cannot be estimated without valid outcomes.
- No comparable long continuous/calendar-verified evidence was attempted for the five additional newer symbols, Energy/Commodity/Bond/Equity/ETF; do not infer validation. IndexDipBuy was not forced into Forex/Metal. Robust research requires verified historical provider sessions plus resolution of anomalous missing spans, then a new transparently qualified experiment; not filling the present gaps or relabeling a holdout.

## Reproduction and source/build binding

From `/home/ubuntu/projects/apex-workers/autonomy`:

```sh
dotnet test tests/Apex.Tests/Apex.Tests.csproj -c Release --filter 'FullyQualifiedName~BehaviorTests'
dotnet test tests/Apex.Backtester.Tests/Apex.Backtester.Tests.csproj -c Release --filter 'FullyQualifiedName~ProductionDipProtectionBehaviorTests'
dotnet build tools/Apex.Backtester/Apex.Backtester.csproj -c Release --no-restore
python tools/data/task4_research.py --source /home/ubuntu/.hermes/cache/scratch/apex-autonomy-new-data --out /home/ubuntu/.hermes/cache/scratch/apex-autonomy/task4-strategies-research/reproduction-NEW --repo /home/ubuntu/projects/apex-workers/autonomy --execute
python /home/ubuntu/.hermes/cache/scratch/apex-autonomy/task4-strategies-research/verify.py
```

Do not rerun unchanged suites just to reproduce a report. The adapter requires a new output directory and source hashes fixed in code; `research/attempt-plan.json` and `attempts.jsonl` preserve exact original argv for all64 attempts. `task4/evidence.json` binds all four TRX, failed run index and DLLs. Source-input map SHA256: `5dd7b96115626a8e031c216a9f61bf7fc05cb3d88888f2ef94c9b4854d88d706`. Preregistration SHA256: `fd6d96af2c280c04627e3c0a27df29574808f1605c921a56e6f55097cda82fc5`.

Research Core DLL SHA256 `d707266f7481c3cb4c399c011fe8fc3dc6df7930e61c8d6681160fec5607171e`; Backtester DLL `c88977af2ff2d72e6a00e88353ceb760d65e2bb938483ef29987e047b6e304f5`. These are locally rebuilt hashes, not Task3's older binaries; source maps match. Full build/test hashes and data hashes are in committed JSON. Raw market data remain outside Git/package; redistribution rights are not assumed. Scratch may expire; committed summaries preserve blockers/provenance, not a redistributable dataset.

**Still separate/unapproved:** broad PhaseC, Windows/PS5.1/EXE installation and cTrader runtime/real DEMO acceptance. No broker connections/trades, push, children, reviewer, model/provider fallback, AI Lab/training/LLM/updater or automatic promotion. This is not whole-bot completion or profitability evidence.
