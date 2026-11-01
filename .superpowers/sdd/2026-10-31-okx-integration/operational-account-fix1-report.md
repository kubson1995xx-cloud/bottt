# Operational account findings 1 and 2 — fix checkpoint

## Final measured result — implementation complete, independent review pending

Only the two reviewed product paths changed. The original `OperationalPeer` and all original operational tests remain byte-identical to base.

- **P1:** `NavalBrokerBootstrap.RequireFresh` no longer expires a currency's last-change `SourceUtc` after 30 seconds. It still rejects future `SourceUtc`; mapper/bookends/digest persist the original value unchanged. Original server/capture UTC and monotonic deadlines, adapter request/receive guards and before/after approval/persistence checks are unchanged. No response/currency timestamp is synthesized or refreshed.
- **P2:** ordinary null/wrong-policy/wrong-IP inspection refuses by clearing connection authority, revoking activation/bootstrap and awaiting existing transport/pump drains, then retains `PERMISSION_SCOPE_UNPROVEN`. No new framework, order, UI, START or financial scope.
- **Behavioral RED:** on original product bytes, the two-day-idle fixture reaches `Assert.True(connected)` with `CONNECTION_REFUSED`, after two identical fresh balance bookends. All three scope-inspection cases reach the intended tuple assertion: expected `(false,false,2,2)` versus actual `(true,true,2,0)` for connected / CanObserve / socket count / closed count. Original happy path and future/moved bookend controls pass in that same run. This is executable evidence, unlike the earlier source-only review.
- **Unchanged original operands GREEN:** the complete new test file is identical between RED, targeted GREEN and integrated GREEN (SHA-256 `162e2d7b8166b4ffe8452c9ae93997c22073e628908faaeebf60a09b4c178b66`). Four original failing test IDs map to Passed in both GREEN TRXs. Idle account creates the real FileStore owner with exactly 1234.56 USDC in STOP, stores original two-day-old provenance in its digest and repeats that same operand at the post-approval recheck. Each inspection refusal now proves IsConnected false, CanObserve false and two closed sockets, followed by successful fresh authorized connection of a different service under the same runtime.

### Actual gates and artifacts

Evidence root: `.superpowers/sdd/2026-10-31-okx-integration/operational-fix1-evidence/`.

| Gate | Actual result |
| --- | --- |
| `01-original-operands-red` | 4 expected Failed, 3 Passed; actual child exit 1 |
| `02-original-operands-green` | 37 Passed: new regressions, original operational and composition controls |
| `03-settled-core-desktop` | 1125 Passed, zero failed/skipped; portable `Gate!=NativeWindows` |
| `04-settled-backtester` | 168 Passed, zero failed/skipped |
| `05-settled-desktop-build` | exit 0, 0 warnings / 0 errors |
| `06-settled-ctrader-sdk` | actual ApexTradingAI SDK build, exit 0, 0 warnings / 0 errors |
| `07-settled-bridge-sdk` | actual ApexBridge SDK build, exit 0, 0 warnings / 0 errors |

SDK **8.0.427**, cached pin, all commands `--no-restore`. These are fresh affected-Core gates, not the earlier 1119/168/native results relabeled. Existing 23 operational cases, original four drain IDs and UTC/monotonic/after-replacement expiry controls pass within the integrated 1125, not as additive totals. Unchanged adapter/native/full Windows suites were not repeated.

- Every gate contains actual `identity.json`, `process.log`, nonempty 308-entry before/after source/config maps, source snapshots, 337-entry dependency map and actual binary hashes. Test gates contain `results/results.trx` and parsed `trx-results.json`.
- `verification.json` verifies original peer/source identity, four unchanged RED→GREEN IDs, original drain IDs, all settled source/dependency maps and current binary hashes. Core/Desktop copies consumed by tests agree with final built binaries. `settled-binary-map.json` resolves each lane/path into `settled-binaries-by-sha256/`, retaining **489** unique final payloads (**128907816 bytes**). This is binary retention, not a distributable application package.
- Retention limit: RED DLL hashes were captured contemporaneously, but the RED DLL payloads were not copied before GREEN rebuilt them. Original RED source snapshots, original test operands, TRX, logs and binary maps are retained; no claim of retained RED executable payloads.
- Historical gate helper records actual child failure in summary but itself returned zero. Results above use actual child exit/TRX, never wrapper exit. The scoped wrapper now propagates the child exit for future recovery; no product rerun was needed for this helper-only correction.

### Scope and exact unmet gates

No accounts/network, secrets outside synthetic fixtures, trades, dependencies, push, CI, services or other worktrees. Parent-owned HANDOFF/progress/acceptance untouched. Tests exercised in-process protocol-shaped synthetic peers, not OKX.

**Next and only acceptance gate for this fix: narrow independent review of findings 1 and 2 against the retained evidence and exact diff.** No fresh native execution or Windows package acceptance is claimed or requested here. Existing unavailable protected production providers, user provisioning/approval, broker strategy/orders/economics/protection/recovery, panel START and packaged user flow remain the previously disclosed wider delivery gaps, not work included in these fixes. LIVE OFF; no real account/trading readiness claim.

## Commit recovery / stop point

- Local implementation commit: `197a2dac751469a47eedd1a9f69cadf3ed5a2db7` (`fix(okx): admit idle cash and drain refused account inspection`). Six explicitly staged files only: the two product paths, new targeted test file, this report and the two scoped evidence helpers. Parent HANDOFF was excluded.
- Postcommit verifier exited 0: committed product/test bytes match all settled source maps; actual test/build binary hashes remain unchanged. This is a postcommit hash verification, **not** a postcommit rebuild/test rerun. Full product/test diff is retained at `operational-fix1-evidence/committed.diff`.
- No background test/build or child worker remains. A final report-only checkpoint commit records this recovery state; obtain its identity with `git log -1` rather than treating that documentation commit as a rebuild.
- Stop for narrow independent review of P1/P2. Do not repeat unaffected suites or resume wider feature delivery inside this fix task.

Checkpoint history below records the completed progression; its old “next” lines are historical, not active work.


## Entry / recovery contract
- Base: `b1982a89094fb36b3eecdc3d3584f0d09d1bf265`, branch `feature/apex-naval-command`, checkout `/home/ubuntu/projects/apex-trading-ai`.
- Read the full operational account review, implementation report and binding lean-delivery priority. Only parent-owned `HANDOFF.md` was dirty; do not touch it or progress/acceptance records.
- Scope: P1 idle balance last-change versus response freshness, and P2 ordinary permission-inspection refusal revoke/drain. Preserve the original passing peer and all unrelated boundaries.
- Exact inherited `gpt-6.1-sol` / `openai-codex`; no children, network/account calls, trades, dependencies, pushes, CI, services or other worktrees. Cached SDK 8.0.427, no restore.
- Next checkpoint: retain source/config map and original targeted operands before behavioral RED; verify failures reach expected Connect and retained connection/drain assertions before production changes.
- After each failed gate or commit, append exact source, artifacts, result and next missing criterion here. Existing unaffected adapter/native evidence is retained, not rerun or relabeled.

No fix or test completion is claimed at entry. Stop after actual source-bound verification and a local scoped commit for narrow independent review.

## Checkpoint 01-original-operands-red
- `.superpowers/sdd/2026-10-31-okx-integration/operational-fix1-evidence/01-original-operands-red`: `{"exit": 1, "source_delta": [], "binary_files": 222, "dependency_files": 337, "dependency_delta": [], "counts": {"Failed": 4, "Passed": 3}}`. SDK 8.0.427, exact argv/cwd/source snapshots and original TRX retained. Next: inspect reached result and continue missing gate.

## Checkpoint 02-original-operands-green
- `.superpowers/sdd/2026-10-31-okx-integration/operational-fix1-evidence/02-original-operands-green`: `{"exit": 0, "source_delta": [], "binary_files": 222, "dependency_files": 337, "dependency_delta": [], "counts": {"Passed": 37}}`. SDK 8.0.427, exact argv/cwd/source snapshots and original TRX retained. Next: inspect reached result and continue missing gate.

## Checkpoint 03-settled-core-desktop
- `.superpowers/sdd/2026-10-31-okx-integration/operational-fix1-evidence/03-settled-core-desktop`: `{"exit": 0, "source_delta": [], "binary_files": 222, "dependency_files": 337, "dependency_delta": [], "counts": {"Passed": 1125}}`. SDK 8.0.427, exact argv/cwd/source snapshots and original TRX retained. Next: inspect reached result and continue missing gate.

## Checkpoint 04-settled-backtester
- `.superpowers/sdd/2026-10-31-okx-integration/operational-fix1-evidence/04-settled-backtester`: `{"exit": 0, "source_delta": [], "binary_files": 188, "dependency_files": 337, "dependency_delta": [], "counts": {"Passed": 168}}`. SDK 8.0.427, exact argv/cwd/source snapshots and original TRX retained. Next: inspect reached result and continue missing gate.

## Checkpoint 05-settled-desktop-build
- `.superpowers/sdd/2026-10-31-okx-integration/operational-fix1-evidence/05-settled-desktop-build`: `{"exit": 0, "source_delta": [], "binary_files": 371, "dependency_files": 337, "dependency_delta": []}`. SDK 8.0.427, exact argv/cwd/source snapshots and original TRX retained. Next: inspect reached result and continue missing gate.

## Checkpoint 06-settled-ctrader-sdk
- `.superpowers/sdd/2026-10-31-okx-integration/operational-fix1-evidence/06-settled-ctrader-sdk`: `{"exit": 0, "source_delta": [], "binary_files": 10, "dependency_files": 337, "dependency_delta": []}`. SDK 8.0.427, exact argv/cwd/source snapshots and original TRX retained. Next: inspect reached result and continue missing gate.

## Checkpoint 07-settled-bridge-sdk
- `.superpowers/sdd/2026-10-31-okx-integration/operational-fix1-evidence/07-settled-bridge-sdk`: `{"exit": 0, "source_delta": [], "binary_files": 10, "dependency_files": 337, "dependency_delta": []}`. SDK 8.0.427, exact argv/cwd/source snapshots and original TRX retained. Next: inspect reached result and continue missing gate.
