# OKX DEMO + LIVE integration design

**Status:** parent-reviewed implementation design under the user's autonomous continuation authorization; no product implementation or operational acceptance.
**Source:** `1f833c58cb719bde5028239bd98477f84ce986f5`, `feature/apex-naval-command`.
**Evidence:** [repository map](../../../.superpowers/sdd/2026-10-31-okx-integration/repo-integration-map.md) and [official contract](../../../.superpowers/sdd/2026-10-31-okx-integration/okx-official-contract.md), both COMPLETE. Protocol statements below derive from that contract, not new account observations. Those ignored SDD reports must accompany any implementation dispatch; this spec is not a replacement for their evidence.
**Plan:** `docs/superpowers/plans/2026-10-31-okx-integration.md`.

## 1. Intent and boundaries

Build one reusable EEA cash-SPOT integration for both OKX Demo and Live, extending the existing Naval financial authority rather than another engine. ALFA/BRAVO share the same coordinator, cash, risk and durable ownership within a portfolio. Demo and Live never share a financial portfolio. The initial supported deployment is one dedicated main account and one discovered actual quote currency per environment; existing offline two-member replay remains unchanged. This deliberately postpones subaccounts, cross-currency valuation and external funding import rather than inventing support.

Code authorization is not authorization to connect privately, provision real credentials, trade, fund, transfer or withdraw. This design creates capabilities; no such operations are performed by the planning task. Public smoke, private reads, recovery, Demo transactions and Live operational acceptance are separate later gates.

### Global constraints (normative)

- Preserve Windows EXE → panel with no manual login, `127.0.0.1` only, private ACL/CurrentUser DPAPI, Host/Origin/Fetch Metadata/CSRF defenses and custom protected mode.
- Preserve the original Polish panel, all five Naval views, optional 3D, list/SVG fallback and actual Retry recovery.
- Preserve cTrader/MT5 REAL/Crypto blocks and inherited CFD leverage `5×`; OKX authorization cannot unlock another venue.
- Naval limits remain risk/trade `0.5%`, daily loss `1.5%`, drawdown `5%`, central slots `3`, per-fleet slots `2`, cash SPOT `≤1×`; no borrow, margin, perps or futures.
- One existing `NavalPortfolioCoordinator` / `EngineState.Naval` authority per isolated portfolio; no Desktop balance ledger or separately funded fleet state.
- Separate Demo/Live identity, credentials, private state, intents and publication; Live OFF by default and no autoSTART.
- Keep existing Offline contracts and isolated Demo-requested GET-only `read_only` reader unchanged.
- No production fixture/config/CLI/environment switch may mint activation, broker evidence, baseline, protection acceptance or START authority.
- Before each future behavioral RED and final gate, preserve contemporaneous source/config/test/asset input hashes in a unique evidence directory; reuse unchanged accepted evidence.
- Use exact model/provider `gpt-6.1-sol` / `openai-codex`, existing feature checkout, no fallback or worktree reset.

Non-goals: StrategyBook rewrite, derivatives, AI decisions/training, generic exchange framework, multi-currency funding, automatic account repair, automatic reset/reseed, broker guarantee of stop execution price, profitability claims. No changes to the old adapter permission validator or broad Core friendship.

## 2. Chosen architecture and alternatives

```text
Existing Desktop Program / cancellation / private-root ownership
  ├─ existing bridge AccountManager + Polish panel (unchanged)
  ├─ existing historical Naval read lane (unchanged v1)
  └─ OkxNavalRuntime (one isolated instance per configured environment)
       ├─ immutable scope + DPAPI credential lease + native activation policy
       ├─ separate execution observation/write port (untrusted protocol DTOs)
       ├─ Core broker session / reconciler / scheduler
       │    └─ existing coordinator + state owner + atomic journal commit
       └─ rebuildable v2 committed projection → host → five-view consumer
```

Chosen: a small Desktop composition service, a separate execution-side adapter in the existing OKX assembly and narrowly extended Core Naval state. Rejected: attaching trade credentials to the GET reader; routing SPOT through CFD bridge/StrategyBook; introducing a second trading engine or generic event bus. The adapter remains Core-independent and cannot issue receipts. Desktop already has assembly-wide Core friendship, so `internal` alone is not a security proof: broker methods must validate a session-owned opaque issuer/permit, not accept arbitrary friend-created offline receipts.

Stage 1 freezes the scope and security boundaries without opening a financial owner. Broker definition admission and authenticated baseline arrive together in Stage 3. This refines the map's early factory suggestion: weakening the Offline-only definition check before a baseline exists would make synthetic seeds look like broker capital. Until then, runtime status is STOP/NOT_CONNECTED, not an empty funded portfolio.

## 3. Identity, configuration and credential lifecycle

### Exact namespace

Use Core environments `OkxDemo` and `Live` (do not rename the existing enum or accept numeric values). Venue is exactly `OKX`, region exactly `EEA`. A configured binding includes `ScopeId`, environment, expected `Uid` and `MainUid`, actual quote currency, opaque credential-binding ID and safety version. Initial support requires `Uid == MainUid`; ALFA/BRAVO are strategy allocations on that same account, not fake subaccounts. Configuration identity and secret metadata must match before any private request; authenticated identity must match before baseline/admission.

`apex.config.json` gains nullable `OkxNaval`: `{ Schema: "okx-naval-config-v1", Scopes: [...] }`. Omission or an empty scope list disables integration. Each scope has exactly `ScopeId`, `Environment`, `Region`, `ExpectedUid`, `ExpectedMainUid`, `QuoteCurrency`, `CredentialBindingId`, `SafetyVersion`; no URL, file path, password, `Enabled`, `AutoStart`, capital seed or activation field. Accept at most one scope per environment. Exact identifiers are ASCII alphanumeric/`-`/`_`, length 1–64; UID strings are 1–32 ASCII digits; quote is 2–12 uppercase ASCII letters/digits; safety version is exactly `okx-cash-v1`. Reject duplicate/case-variant JSON fields, unknown fields, wrong scalar types and unspecified/Offline environments before ConfigStore can normalize/save anything. Never silently repair user identifiers.

Derive private paths, never accept them from JSON: `<data>/okx/{demo|live}/{ScopeId}/` contains `credentials.dpapi`, `authority/` and `publication/`. Admit/lease canonical private directories before use; reject links, reparse points, broad ACLs, aliases to another scope/root, partial existing state and unexplained files without repairing them. Same UID text in Demo/Live is allowed as an identity value but does not permit shared credentials, state, key fingerprint or publication. Reject the same API-key fingerprint across configured bindings before the second private connection. No cross-environment fallback.

Credential envelope `okx-execution-credential-v1` contains exact immutable binding, provisioning provenance (`DemoTradingApi` or `LiveAccountApi`), key fingerprint, key/secret/passphrase bytes. This provenance is an operator attestation bound during protected provisioning, not a claim that a header proves key origin. Bind DPAPI entropy to schema + admitted root identity + scope binding; use CurrentUser with UI forbidden and zero owned plaintext buffers on dispose/failure. DPAPI is storage protection, not activation authority. Do not serialize the lease or log key fingerprints, passwords, passphrases, signatures, capability bytes or raw private bodies.

Private account config must establish expected UID/mainUID, Spot `acctLv=1`, explicit disabled borrowing/auto-loan, configured IP binding and admitted permissions. The permission taxonomy conflict cannot be solved by checking only `perm=trade`: production admission requires later protected key-scope inspection/attestation bound to fingerprint, plus API observation. If transfer/loan/earn/withdraw rights cannot be excluded under the approved key policy, mark `PERMISSION_SCOPE_UNPROVEN` and block execution. Transport denies those routes regardless; inability to obtain a sufficiently scoped key is an operational blocker, not a reason to relax the rule silently.

## 4. Protected activation separate from the no-login Operator

Normal double-click startup keeps existing no-login presentation, does not prompt, decrypt an OKX key, connect privately or resume RUNNING. Configuring a scope merely displays an inactive scope. Reader, desktop-local Operator, ordinary protected Operator and legacy `unlockLive` cannot activate it.

Chosen protected path (builder decision): an explicit local EXE action, `--okx-activate <ScopeId> --purpose observe|execute`, requests confirmation from the already-running host through a current-user-only local named pipe with remote clients rejected. It is an action request, never a bypass flag. The helper does not supply a success boolean or capability. The host validates peer SID/session and verifies its pipe server/client process binding; the host invokes and owns the native provider. Rate-limit prompts; allow one pending request; reject unknown scope/purpose before prompting. No HTTP activation route or browser-returned token.

Native provider: Windows secure-desktop credential UI (`CredUIPromptForWindowsCredentialsW` with secure prompt), unpack/verify credentials through Windows logon, require the resulting user SID and interactive session to match the host's current user/session, clear unmanaged buffers and close token handles. Display exact environment/account/purpose before consent and bind the outstanding nonce/config to the result. Cancel, wrong user, rejected credentials, timeout, unavailable secure desktop and noninteractive sessions return denial. Do not cache a Windows password. Passwordless-only users without a usable provider are explicitly unsupported pending a reviewed Windows Hello alternative; do not substitute an ordinary Yes/No dialog. Same-user malware with memory/code injection is outside the existing desktop trust boundary; HTTP-local access alone remains insufficient.

A host-private `OkxActivationLease` is nonserializable, nonexported, issuer-token-bound and single-use for connection/arming; lifetime `120 seconds` to consume. It binds scope/config digest, credential fingerprint, process session nonce, purpose and generation. `Observe` authorizes private reads only. `Execute` allows later explicit START after all other gates; it does not send an order itself. Consumption creates an in-memory session authority; STOP/restart/reconnect/identity change/hard fault revokes execution arming and queued risk-increasing permits. Observation may continue after STOP for reconciliation; reconnect requires a new protected activation. No saved activation and no saved RUNNING. Risk-reducing recovery under retained exact-owned obligations is separately admitted, never a fresh buy or authority over foreign inventory.

**Native/provider gates early:** before Task 1's new DPAPI behavior, establish an authorized native Windows source-test lane, including private fixture storage and actual SDK8 selection. Before Stage 6 provider wiring, obtain an interactive Windows lane for real OS confirmation, cancellation, wrong-SID/session and pipe tests. Headless CI/fake callbacks cannot prove secure UI acceptance. Task 1 intentionally composes an unavailable production activation provider and a friend-only in-process fake positive control; it is a secure prerequisite, not working broker activation. No CI/push permission is conferred here.

## 5. Durable execution and protocol boundary (Stage 2)

Extend the same authoritative transaction with a versioned broker substate: baseline reference, execution intents, original bytes/digest, never-reused IDs, dispatch attempts/outcomes, order/algo links, raw economic evidence references, coverage and protection obligations. Keep Offline v1 readable as Offline; broker state uses explicit v2 validation. Missing new fields never default to finality. Open incompatible state read-only/refuse with preserve-and-upgrade instructions; no auto-reseed or reclassification.

State flow: `Prepared → Dispatching → Accepted | RejectedProven | SubmitUnknown`; `Accepted` is order acknowledgement only. Fills, entry terminal coverage, protection and reduction settlement are separate facts. Persist `Prepared` plus holds and exact request before send; durably mark `Dispatching` before possible network write. Crash with Dispatching, transport cancellation after possible send, `50004`, HTTP ambiguity, failed response persistence and ambiguous storage all retain holds as unknown. A proven pre-send fence refusal may cancel an unsent intent but never fabricate a broker rejection.

Generate a never-reused ≤32-character ASCII alphanumeric `clOrdId` from the durable per-binding monotonic namespace; store namespace/counter before dispatch and refuse exhaustion/collision. Persist separate attachment/algo client IDs; do not assume parent propagation. No replacement ID on duplicate or timeout. Restore the original payload, not a request reconstructed from a new quote. `clOrdId` uniqueness is application-owned permanently; venue uniqueness is pending-only.

REST writes only initially; private/business WS provide observations. Method/path/query/body signing uses the exact bytes sent. Fixed REST host `https://eea.okx.com`, Demo header `x-simulated-trading: 1`, Live header `x-simulated-trading: 0`. Reject redirects and arbitrary paths/methods. GET detail supports `instId + clOrdId` when no ordId exists. Explicit POST allowlist is place/cancel general order and place/cancel protective algo, only as the accepted Stage 4 profile requires. No amend/batch/CAA/archive-apply in the first scope; use cancel plus reconciled replacement where safe. No transfer, settings, loans, withdrawal, earn or derivative endpoints exist on the port.

Final send fence runs after quota/send-lock waits and immediately before dispatch against current STOP epoch, lease/session, source generation, definition/catalog digest, reservation revision and absolute deadline. Include `expTime` in order writes; it is not cancellation/finality/idempotency. No automatic mutation retries, including 429/50011/5xx. Reserve recovery/cancel quotas. Validate HTTP, envelope code, per-item sCode/subCode and identity; preserve sanitized reason codes without treating `59113` as a single known cause.

## 6. Trusted broker reconciliation and accounting (Stage 3)

Transport yields untrusted typed observations. A Core broker session with private issuer creates broker evidence only after checking binding, original intents, individual fill identities, time bounds and cash/inventory conservation. The existing synthetic issuer/ExplicitOfflineIntent path remains Offline-only. Public DTOs or a serialized `complete=true` cannot mint authority.

Bootstrap one dedicated, clean account/quote: protected approval of exact baseline candidate; no pending/manual orders, base inventory, liabilities or unexplained currencies; coherent anchored quote cash, catalog, fees and permission evidence. Persist baseline digest/provenance and initial quote cash in the same owner initialization transaction, not a caller-configured seed. New account observations never reseed an existing authority. Outside orders, transfers, deposits, resets and unknown inventory stop new risk and require explicit investigated recovery; no automatic import.

Subscribe/buffer ordinary private `orders` and business `orders-algo`, then reconcile REST balances, pending/terminal orders, fills, bills and protective algos. Neither channel has an initial snapshot. Use default TLS443 regional hosts: Demo `wseeapap.okx.com`, Live `wseea.okx.com`; no `:8443`. Auth success precedes subscriptions. WS gaps/reconnect revoke readiness and require fresh REST reconciliation. No dependency on privileged WS fills.

Persist bounded window/cursor evidence, overlap digests, pending order obligations and local receipt sequence. Order cursors are ordId; fills use billId; order history bounds creation time, so individually track older pending orders. No atomic cross-stream token exists. Coverage means exact supported local obligations plus conserved baseline cashflows over named bounds, not global perpetual completeness. Stable repeated anchors alone are not proof. Empty/51603 lookup retains uncertainty; expired retention (including 2-hour zero-fill cancel history) stays STOP/manual recovery with holds retained.

Individual fills keyed by venue/environment/account/instrument/tradeId converge across REST/WS/restart. Changed economics conflict. Use fillTime plus identity tie-break, not arrival uTime. Cumulative accFillSz/order fee fields never post individual fills. Raw signed fee, fee currency, ordId/tradeId/billId and actual tradeQuoteCcy are retained. Initially support only non-rebate charges in configured base/quote currency compatible with one bound fee schedule; convert negative charged fee to nonnegative Core charge only after validating sign/semantics. Rebate, third token, unsupported zero-fee promotion, fee correction or differing instrument schedule remain unsupported STOP, not zero or synthetic FX. Bills corroborate fill cashflows, not a second fee posting.

Accepted broker economics extend existing owner validation and journal commit, not side balances. Cancel ack does not release money; terminal with partial fills plus later fees stays unsettled. Final zero inventory does not release symbol ownership until reduction finality is proved. Restart opens exact durable state STOP; reconciliation still STOP; explicit fresh START is required.

## 7. Feed, shared risk and protection (Stage 4)

Confirmed UTC-D1 candles only feed existing `NavalD1Evaluator`/fleet profiles. Stable trusted signal registration is durable and bound to rule, account, fleet, instrument, bar close, expiry and generation; production cannot import replay signals. Fresh executable bid/ask/depth and private eligible instrument catalog supply exact decimal sizing, tick/lot/minimum/maxima, trading phase and actual quote. Reject future/stale/regressed sources, crossed books, nonconfirmed candles, missing data and unsupported units. Receipt/poll time cannot refresh source age. BTC entry filtering must not disable valid exits.

Use `ForFleet(account, fleet).TryReserve` and the existing risk/ownership rules. Pending/partial/uncertain/reduction-unsettled positions consume slots/cash/risk. One account cannot spend another's idle cash; ALFA/BRAVO do not get independently seeded capital. Daily/drawdown/notional latches operate on coherent phases including fees and market movement. Add no generic strategy platform.

### Protection decision / supported-path gate

The currently evidenced production entry profile set is **empty**. This is a load-bearing external capability gap, not completed integration. Ordinary partial-fill limit/IOC entries with attached SL/TP are unsupported: docs do not prove continuous partial coverage. Received-side market buys cannot attach TP/SL; conditional-with-both is not a bracket; standalone OCO after a fill leaves a coverage gap. Even FOK + attachment does not establish zero-gap protection generation or fee-adjusted coverage, so it is only a candidate for later evidence, not an approved shortcut.

Choose a first bounded candidate: cash base-sized price-bounded FOK entry with attached cash-SPOT TP/SL and a single compatible fee/currency scope. Do not implement its production admission until the exact venue/account/environment terms prove generation timing, partial/reject behavior, net quantity, child/sibling cancellation and recovery. If impossible under mandatory continuous SL/TP, explicitly report unsupported entry and seek a separately authorized requirement/venue change; never weaken mandatory protection or use a local watcher as its replacement.

Acceptance is a reviewed release-coded protection profile bound to environment, account/instrument capability digest, fee mode, entry shape, safety version and evidence references. No editable manifest, test peer, config field or successful GET may add profiles. Native provisioning can bind an already reviewed profile but cannot invent one. Demo acceptance does not approve Live. Offline protocol tests prove local enforcement, not venue promises.

Maintain parent → attachment → algo → triggered child order → fill links. Treat missing/rejected/partial-effective/failed protection as unsafe; stop new entries, cancel outstanding entry remainder and attempt exact-owned risk reduction where safely executable. Trigger != fill, SL != guaranteed execution price. Exits and emergency serialize claims to net available inventory after base fees and outstanding orders/algos. Cancel/reconcile competing protection before discretionary sale to avoid oversell; unresolved cancellation/coverage may block a safe full sale. Dust below lot/minimum remains explicit residual exposure, never “fully closed.” Storage faults keep STOP and only use retained provable owned reduction bounds; unknown ownership or competing commitments must refuse, not blindly sell.

## 8. Publication, controls and consumer (Stage 5)

Preserve historical v1 parser/export and fixtures. Add explicit broker v2 envelope and fact vocabulary across Core journal, producer, host reader/service and frontend decoder/store/selectors/views/scene. Projection is atomic and rebuildable from committed authority; reads never take writer lease. Include environment/account lineage, revision/source clocks, readiness/coverage, pending/uncertain execution, protection, settlement and unknown reasons. Fleet totals are projections of the single authority. Runtime connection freshness is a separate ephemeral overlay with host epoch, not rewritten historical events or economic authority.

No fill, P&L, victory animation or settlement success before accepted durable evidence. Old data stays historical; unknown/stale/gap values stay UNKNOWN. Financial decimals remain exact strings. Stable lifecycle IDs survive export repair; cursor/retention gaps are explicit. All five views and optional 3D/list/SVG share the same IDs/pages/selections, with old-panel link and recovery retained.

New exact Operator routes: `POST /api/naval/okx/{scopeId}/start`, `/stop`, `/emergency`. No activation route. START is a request to consume host-held execution arming after readiness/protection checks, not a user-supplied permit. Reader is denied, body schema closed, command ID deduped, local CSRF/Origin checks retained. STOP prevents queued opens; emergency first latches STOP durably and drives bounded cancel/reduction workflow. Command service is independent of passive publication; textual emergency remains callable when reads fail. Keep legacy command behavior venue-isolated. Narrowly update old no-writer-route assertions for these exact routes only.

## 9. Six ordered reviewable stages and honest completion

| Stage | Deliverable / independent acceptance |
|---|---|
| 1 Secure composition | Immutable scope/config/credential/activation policy; default host opens no OKX secrets/private connections, remains STOP; fake protected positive control is scope-bound; native DPAPI/ACL gate separately evidenced. No broker-funded owner yet. |
| 2 Durable execution port | Immutable pre-send intent and holds, never-reused IDs, exact signer, allowlists and uncertainty/queued-send fences survive crash matrix. No economic posting from ack. |
| 3 Trusted reconciler | Exact protected baseline, persistent coverage/issuer and once-only supported fill accounting; restart/reconnect remain STOP; unknown history never frees obligations. |
| 4 Feed/risk/protection | Actual evaluator → shared risk → gated send, coherent reductions/protective fills with strict protocol peer; production candidate remains blocked unless separate protection evidence meets §7. |
| 5 Versioned publication/controls | Committed broker v2 visible truthfully through host and all views; Operator cannot activate; emergency works independently of read failure. |
| 6 Native delivery / private gates | Final affected/full tests and SDK consumers, emitted assets, actual extracted Windows EXE/browser/ACL/DPAPI/no-login upgrade, independent review. Separately authorized public/private read, recovery, Demo transaction/protection and Live acceptance—not inferred from package tests. |

Review each stage before the next; do not parallelize shared financial/config/publication edits. Details and source-bound RED/GREEN commands belong to the plan. No new worktree, CI/account calls or execution is authorized by this design artifact. A finished stage report is not full end-to-end completion.

## 10. Load-bearing gates before operational completion

1. Native Windows lane/interactive secure credential provider must actually work for the user's account; passwordless alternative is not implemented by assertion.
2. Permission provenance must settle whether an acceptable key without unwanted funding/loan/earn powers exists. Unknown means execution disabled.
3. Discover actual instruments/quote, fees and unit-compatible limits. Non-USDT maxima cannot be converted by silent stablecoin parity; unsupported constraints block that instrument/order shape.
4. Mandatory protection remains unproved for every production entry path. A constrained FOK candidate is not authorization or acceptance; impossible coverage means honest unsupported status.
5. Dedicated baseline/retention/external-activity recovery requires actual account evidence later. No clean fixture or exhausted pager proves a user's account.
6. No private connection/trading approval is present in this task. Real Demo and Live testing, provisioning, funds and transactions require separately recorded scope/approval, without secrets in chat.
