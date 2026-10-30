# APEX Naval Command · Operator read application

Production React19 / TypeScript6 / Vite8 application, hosted only by the existing Desktop ASP.NET process at `/naval/`. This is **Operator-only served read application; ReadOnly API only**. No trade/order/settings writer, role expansion, WebGL, second service, dev-server proxy or broker credential is included.

## Offline build

From the repository root, using Node26 / npm11 and the approved genuine local cache:

```sh
npm --prefix frontend/naval ci --offline --ignore-scripts --no-audit --no-fund
npm --prefix frontend/naval run typecheck
npm --prefix frontend/naval test
npm --prefix frontend/naval run build
npm --prefix frontend/naval run test:build
/home/ubuntu/.dotnet/dotnet build src/Apex.Desktop -c Release --no-restore
```

Exactly seven direct pins remain in package.json. The npm-generated lock was reconciled offline from the machine's pre-existing Hermes checkout lock, then clean-reinstalled and exercised; no fabricated registry metadata or lifecycle/native build script. Linux x64 glibc prebuilt Rolldown/lightningcss artifacts are present. Other-platform cache/build availability is not implied. A fresh machine needs genuine integrity-matching artifacts; `--offline` never falls back to the registry.

Build creates hashed local JS/CSS, validates artifact names/symlinks/entry references, writes a size/SHA256 inventory, and stages only `src/Apex.Desktop/wwwroot/naval`. Original panel hashes are tested. Desktop's existing content glob copies the tree to build/publish; .NET alone does not run npm. Missing enabled UI gets `503 NAVAL_UI_NOT_BUILT`, with a textual original-panel link. No SPA fallback.

## Authentication and routes

Authenticate through the **original `/` Operator native Basic prompt first**, then navigate to `/naval/`. Browser credentials remain in native cache; the JS client has no credential field, Authorization construction, token/meta bootstrap or credential persistence. A challenge first admitted only below `/naval/` was observed not to populate the `/api/` Basic protection space: the app correctly showed unauthorized. Do not bypass this with JS credentials. Root-first login plus reload/five views/H reads passed in Chrome145.

ReadOnly Bearer remains allowed only on the existing exact snapshot/events GETs. It cannot load Naval documents or JS/CSS. No ReadOnly browser credential delivery is invented. Disabled mode retains the old panel and no usable H endpoint.

- `#/command`: actual checkpoint mode/latches, synthetic account seed/cash, historical liquidation equity and source identity; UNKNOWN broker readiness/P&L.
- `#/radar`: static categorical ten-asset planned roster, not discovered pairs/quotes. Real records filter by explicit baseCurrency; exact unplanned instrument names remain available.
- `#/fleets`: real Alpha/Bravo checkpoint reservations/fills, requested protection and hold currencies, uncertainty/reduction warnings; no invented fleet balances/budgets.
- `#/animations`: real baseline/lifecycle records and expandable atomic session batches; offline final coverage is not settled monetary result or broker protection.
- `#/analytics`: evidence workbench, individual fees/quantities/prices and explicit baseline/session distinction; no computed P&L/equity curve/WR/PF/DD.

Navigation, account/fleet/asset filters, detail disclosure and pagination are local. Resync reads a fresh checkpoint, never reconciles/restarts the engine. Original journal/CSV link opens `/` and does not relabel CFD as Naval performance. The persistent `AWARYJNY STOP — otwórz panel operatora` link **only navigates**; the actual old-panel emergency action still requires its real confirmation. No React mutation endpoint.

## Read protocol and failure semantics

Only same-origin `/api/naval/snapshot` and `/api/naval/events` GETs, same-origin credentials/no-store, serial requests, 10s deadline and bounded backoff. Request-generation checks plus reducer session fences reject late responses even if abort is ignored. UTC output accepts up to seven fraction digits and zero-offset UTC. Counters remain canonical Int64 strings; money/quantities retain exact invariant decimal digits and are never coerced to Number. Nested shape, membership, discriminator and cursor/mirror checks fail closed.

Economics remain the full accepted checkpoint; event facts never apply cash/inventory/P&L deltas. A page requiring newer economics is staged with a same-lineage snapshot refresh, without jumping the audit cursor to its head. Audit retains512 batches independently of full checkpoint. Gap/restart clears renderable data and hydrates anew. Conflict/backward/lineage errors stay explicit, with no financial payload; H's conflict latch is not client-healed. Unauthorized/forbidden stop reads; offline and configured failure are distinct from not-configured and zero.

All accepted source content is historical synthetic, stale and without broker authority. No trading one-shot, launch, sound, win/loss animation or money tween. Only a bounded historical-publication outline is possible in a continuous visible session. Reduced motion/minimal disables it without dropping records. Hidden tabs pause reads, return resyncs without replay. Only three nonsecret preferences are persisted. SVG failure is isolated so text/lists survive; no-JS retains the static original-panel link.

## Evidence and remaining acceptance

Detailed commands, per-run input hashes, TRX, final build inventory, production Chrome screenshots and unmet gates are recorded in `.superpowers/sdd/2026-10-29-apex-naval/task-6b-react-command-report.md` and private scratch `/home/ubuntu/.hermes/cache/scratch/apex-naval-task6b/dependency-recovery/` (local, not a redistributable artifact).

Not accepted as the full mission. Parent review/acceptance remains required. The obsolete Operator `/naval`404 expectation was corrected narrowly in `e87e87b`; the retained integrated run passed 845/845. That is source-bound supporting evidence, not UI acceptance; earlier 844/845 results in the implementation report are historical. Fixround1 adds shared fleet-page IDs/counts and synchronized click/keyboard details, preserves the mission route when skipping to main, and reserves numeric baseline zero for an accepted empty checkpoint. New frontend/build/host/browser results and source hashes are in the report’s Fixround1 addendum. Windows/private broker/TLS/remote-device, ReadOnly document/auth delivery and R3F remain separate. Policy defaults are design limits, not current measured headroom or a readiness certificate.
