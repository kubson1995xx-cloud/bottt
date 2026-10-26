# Task4 frozen preregistration — before any strategy outcomes

Base: 351e4897480f11dce981cf664c4cfe503e2a05d7. Offline only; no activation or profile edits. This document is frozen by its own Git commit before research. All outputs go to `/home/ubuntu/.hermes/cache/scratch/apex-autonomy/task4-strategies-research/`.

## Sources and adapter
Same-provider native Dukascopy BID H1, pinned downloader 33a6c6e3de97fab9904afc80a3a2fac8b46d8bfb, local acquisition handoff `/home/ubuntu/.hermes/cache/scratch/apex-autonomy-new-data/DATA-HANDOFF.md`. Full files under its `output/`:
- EURUSD_H1_BID.csv SHA256 860d716745586a638e75992f62269d58beaa97e3d17f8df76ea4617d469818a0
- XAUUSD_H1_BID.csv SHA256 21377b316d617a471cf75391f45a7d642d3f95b35f8d30ab22ead0e0fff51b23
- US500_H1_BID.csv SHA256 8adfa9186949ec2a3e08774998229ee022ea8b1639c0a456020abcb99733df13

Independently recount/hash/check all used rows; adapter changes only timestamp/header/filename, preserves five numeric fields verbatim, no dropped/fabricated candles. Manifest quoteBasis=BID. Product converts BID to *modeled* MID with catalog spread/2 and rollover, not observed MID or broker ASK. Raw volume is provider best-price volume, not tick count or traded volume; no claim of broker equivalence. Existing handoff has no verified historical holiday/DST/session calendar; observed gaps alone cannot justify closures. Declare no gap without additional existing verifiable session evidence. Preserve loader failures; do not relax it. Native H1/minute disagreements remain unresolved. Sparse M15 is excluded.

## Scope, periods and fixed parameters
Core classes: Forex EURUSD, Metal XAUUSD, Index US500. Evaluate TrendPullback, DonchianBreakout, MeanReversion, MomentumImpulse, DailyTrend on all three; IndexDipBuy on US500 only (16 family/symbol pairs). Other five newer symbols, Energy, Commodity, Equity/ETF and Bonds excluded: no comparable long same-provider data/calendar/FX evidence, not presumed validated.

Use real DefaultProfile parameters via per-pair offline profile copies; research-only enabled Experimental copies never alter production Rejected/default flags. Fixed independent accounts:
- warmup-from 2012-01-01, IS [2014-01-01,2021-01-01);
- warmup-from 2019-01-01, OOS [2021-01-01,2024-01-01);
- warmup-from 2022-01-01, chronological holdout-*candidate* [2024-01-01,2026-01-01).
Two-year warmup supports D1 requirements, subject to actual counts. Earlier overlapping data are reused historical re-evaluation. 2021–2025 is not certified untouched; no untouched-holdout claim. Never change splits after failures/losses. No source splice or FX fallback.

## Costs and bounded computation
Use corrected Task3 CLI, intrabar=h1, USD account, catalog base spreads/commission/slippage/swap/minimum, fx-max-age-hours=2, unchanged risk defaults 0.5%/1.5%/5%/5x and manager protective TP3R. These are modeled assumptions, NOT verified Pepperstone historical charges. Base scenario for all 48 fixed accounts; elevated/double only in preregistered WF pipeline if loader coverage permits. No lower-cost rescue runs.

After fixed attempts, run one isolated rolling WF attempt per pair, evaluation [2014-01-01,2024-01-01), warmup 2012, is-years=4, max-combos=2 (default first plus exactly first pipeline neighbor, descriptive truncated exploration, NOT full stability grid), max-runs=160 per invocation, parallel=1. Maximum 16 research invocations / 2560 isolated accounts, plus 48 fixed invocations. The candidate 2024–2025 period must not enter WF selection. If no feasible loader coverage exists, CLI failures are the research evidence; no gap laundering, synthetic market simulation or parameter retuning. No further runs to seek profit.

## Required accounting and interpretation
Enumerate every attempted invocation, arguments, status/errors and source/build hashes. Successful accounts: n, natural vs boundary positions, gross/net/commission/swap, WR/PF, closed-trade DD vs H1/end-sampled full-account equity DD, cashflows incl partial operations, start/end reconciliation, uncertainty and coverage/warmup. Independently funded DD must not be summed as portfolio DD. WF: preserve selection provenance, natural eligibility n>=30, full-account money incl boundary losses; no eligible training/zero trades are not validation. Failed runs have unavailable metrics, NEVER zero profit. Report exact blockers per family/class. No promotion, default change, broker connection, Windows acceptance, push or broad PhaseC claim.
