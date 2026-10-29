# Naval shared capital — bounded offline execution

**OFFLINE_SHARED_CAPITAL / SYNTHETIC_NOT_RESEARCH / NO_BROKER_AUTHORITY**

This command executes controlled simulation instructions against the actual Core
`NavalPortfolioCoordinator`. It is not the independent-account `naval` research
command, a strategy validation, a broker adapter, or production START permission.
There are no URLs, credentials, orders, borrowing, FX, deposits or transfers.
`OkxDemo`, `Live`, unknown enums and unknown JSON members are rejected. The only
receipt issuer is internal/friend-only and definitions accept only `Offline`.

## Run

From the repository root, using the already installed SDK/cache:

```sh
/home/ubuntu/.dotnet/dotnet run --no-restore --project tools/Apex.Backtester -c Release -- naval-shared-replay \
  --input tests/Apex.Backtester.Tests/fixtures/synthetic/naval-shared-replay-v1.json \
  --state-dir /absolute/local/new-shared-state \
  --output /absolute/local/shared-result.json
```

All three paths are mandatory, local filesystem paths. Input and output must be
outside the authority directory and must not be the same file. Symlink/reparse
paths are refused by this CLI. Unknown/duplicate options fail with exit code 2.
Exit 0 means instructions executed/exported; individual business proposals may
be refused, and their results must be inspected. It does not mean broker-ready.

## Input schema `naval-shared-replay-v1`

The executable fixture linked above is a full example. JSON names are camelCase;
enums use exact string names. The top-level fields are:

- `schema`: exactly `naval-shared-replay-v1`.
- `portfolio`: `{portfolioId, reportingCurrency}`; opaque string identity.
- `members`: one or two `{account:{venue,environment:"Offline",accountId,
  quoteCurrency},allocation}` objects with distinct accounts, positive allocations
  and the same quote/reporting currency. These are explicitly separate synthetic
  initial funds, not imported research P&L. Identical manifests are required when
  reopening; allocations are never credited again.
- `catalog`: `{instrument,baseCurrency,quoteCurrency,quantum,minimumQuantity,
  tickSize}` for each exact symbol. No case-ambiguous symbols/base aliases; each
  base has one instrument in this slice. All quantities/prices/costs are decimals.
- `limits` (optional): `tradePct:.5,dailyPct:1.5,drawdownPct:5,maxLeverage:1,
  centralMax:3,alphaMax:2,bravoMax:2`. Values may only tighten these ceilings;
  malformed/over-ceiling values fail instead of clamping.
- `fees` (optional): `entryRate:.001,entryInBase:false,exitRate:.001,
  exitInBase:false,stopSlippageRate:0`. Rates are fractions, not percentages.
  Only nonnegative bounded base or quote fees are supported. A base sell fee
  consumes additional base inventory. Third-currency fees/rebates refuse.
- `clockUtc`: explicit initial synthetic UTC timestamp.
- `marks`: complete `{exactInstrument:positiveExecutableSellPrice}` mapping.
- `signals`: `{signalId,accountId,fleet:"Alpha"|"Bravo",instrument,ruleIdentity,
  earliestUtc,expiresUtc}`. These are explicitly synthetic eligible opt-ins, not
  production evaluator decisions. A handle cannot replace a binding or cost/risk
  assumptions. Signal identity stays consumed even after business refusal/restart.
- `steps`: ordered operations below (at most 10,000; input at most 10 MB).

Every step can replace the complete `marks` map. `captureUtc` can exercise stale
or future evidence refusal. `advance` requires positive integer `seconds` and
moves only the controlled clock, never waits for real market data.

| `op` | Additional fields / behavior |
|---|---|
| `reconcile` | Fresh complete cash, holdings, pending and marks; prepares STOP for explicit start. |
| `start` | Explicit offline intent, bound to the current owner/revision/generation; latches remain. |
| `reserve` | `accountId,fleet,commandId,signalId,instrument,quantity,entryPriceCeiling,stopPrice,takeProfitPrice`; rounds quantity down, reserves before admission. |
| `confirm` | `fills` array of individual executions, never an acknowledgment/cumulative quantity. |
| `timeout` | `referenceCommand`; holds and slot remain, new entries STOP. |
| `reject` | `referenceCommand`; complete final zero-fill proof only. |
| `terminal` | `referenceCommand`; final entry remainder cancels; any filled inventory still owns its slot/symbol. |
| `restart` | Dispose the leased owner, reopen exact persisted authority, new token/source generation; STOP. |
| `stop` | Explicit synthetic STOP, not a financial reset. |
| `advance` | Positive `seconds`; evidence does not remain executable beyond 30 seconds. |

A fill is `{referenceCommand,tradeId,reduction:false,quantity,price,fee,
feeCurrency,tradeUtc}`. For reductions set `reduction:true`. Its exact economic
identity includes account/venue/environment/instrument/trade ID. The synthetic
order ID is the deterministic reservation ID; `referenceCommand` resolves that
one order. Receipt identities are separate. No wire provider order mapping is
implemented. Exact fill/receipt replays do not post cash or refresh readiness;
changed economics under a known key stop and retain conflict evidence.

For ordinary controlled complete observations the issuer echoes the current
owner cash/holdings/pending, including explicit zero pending for terminal orders.
**This is simulation coverage, not an independent broker balance proof.** To
recover new delayed fills in a complete `reconcile`, supply `fills` plus explicit
post-batch `cash:[{accountId,cash}]` and
`observations:[{referenceCommand,inventory,pending}]`. The Core verifies them
against its candidate. It never uses an observation to overwrite its ledger.
Unknown observations, coverage gaps and absent orders cannot release obligations.

## Authority and economics

`EngineState.SchemaVersion` remains 5. The only existing Core envelope addition
is nullable/default-null `EngineState.Naval`. A dedicated scope refuses any
CFD-populated envelope. Existing CFD risk defaults (including 5×), guards,
recovery, SL/TP and production execution paths are unchanged.

The new section uses `naval-cash-risk-v1`, with fixed config/membership digest,
revision, cash accounts, reservations, allocated acquisition cost, fill economics,
receipt/fill watermarks, command/signal tombstones, coherent marks/day/peak equity,
sticky daily/DD/emergency latches, and mode/reason. `stateDigest` detects accidental
scalar/content corruption as well as structural validation. It is an integrity
checksum, **not** authentication against a hostile local filesystem user.

One directory-wide exclusive lease owns the whole `EngineState` key
`naval-authority-v1`; no per-fleet files or `SpotAccount` ledger exist here. All
operations lock the owner, read its latest revision, build/validate a detached
candidate, Set+Flush once, then publish. On any ambiguous write the owner freezes
permanently in-process. It never rolls back or overwrites the possible new state.
The file store flushes a complete candidate and atomically replaces the authority;
Linux also fsyncs the directory, Windows uses write-through replacement. There is
no backup fallback. An initialization marker makes a missing prior file a refusal,
not a new seed. Keep the **entire directory**, including hidden marker/lease files.
Do not delete/reset it to recover from uncertainty.

Reopening always gives runtime STOP/UNKNOWN even if the persisted last mode was
RUNNING. Fresh full reconciliation in a new generation then explicit start are
mandatory. An acknowledgment, timeout, old generation, sequence gap/rollback,
stale/future capture or empty snapshot cannot claim readiness/release holdings.
Daily clears only on a later UTC day with fresh reconciliation; DD/emergency do
not auto-clear. Reductions can be accounted while stopped/latched. Financial
conflicts are retained with UNKNOWN equity; this slice deliberately has no
operator conflict/DD reset or provider reconciliation resolver.

Entry reservation holds gross debit + worst entry fee and an independent quote
close-fee buffer. A fill converts the entry hold into one cash debit and net base
inventory; allocated cost is not another debit. Portfolio equity is cash plus
conservative net liquidation value, not quote balance. Pending and held risk are
counted once. Risk/slots aggregate across both accounts/fleets, but account A can
never spend B's cash. Protective levels bound admission estimates; gaps or actual
execution overruns are reported/stopped, not guaranteed losses.

The report includes shared account cash/held/free/equity, reservations, per-fleet
accepted fill groupings, allocated cost, quote cashflow and liquidation contribution.
Fleet contribution is not independently allocated equity and must not be summed
as independent research accounts or promoted as research performance.

## Continuation and expected synthetic example

Reusing the same state directory and unchanged allocation/catalog/limits/fees
opens existing capital without re-seeding. A continuation file keeps the manifest,
sets its controlled clock appropriately and supplies `reconcile`, `start`, then
further instructions. Old command/signal identities remain consumed. Replaying
known individual fills under a new receipt/source generation posts nothing.

The included fixture reserves three symbols, refuses shared BTC ownership and
BRAVO's additional proposal, partially fills ALFA, times out, restarts, reconciles,
explicitly starts, finishes the fill, rejects the two unfilled BRAVO orders,
reduces ALFA and proves terminal coverage. Its literal arithmetic starts at 10,000:
entry gross400 + quote fee.400; close buffer.520; cash9,599.600/free9,599.080;
close at110 credits439.560 once; final cash10,039.160. These are synthetic safety
expectations, not profit/research claims.

## Deliberately not delivered

No OKX DEMO issuer, LIVE authority, broker/network I/O, UI, production strategy
activation, real fee tier discovery, FX/multi-currency, transfers, multi-host lock,
DD reset, conflict resolution operator, historical campaign or profitability claim.
Only cooperating local writers on a trusted local filesystem are in scope. Linux
execution is verified in this task; native Windows replacement and real power-loss
behavior require independent platform testing. The existing research ledger and
accepted historical research results are neither modified nor pooled.
