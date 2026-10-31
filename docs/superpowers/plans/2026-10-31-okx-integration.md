# OKX DEMO + LIVE Integration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Parent owns dispatch/review; this planning worker spawns no children and writes no product code.

**Goal:** Add reusable, isolated OKX Demo/Live cash-SPOT execution to the existing Naval authority while keeping private connection/activation and production trading separately gated.

**Architecture:** One environment-aware Desktop service composes an execution-side adapter and trusted Core broker session around the existing `NavalPortfolioCoordinator`/`NavalCoreStateOwner`. Extend their transaction, reconciliation and journal; do not create another financial engine. Historical/offline and GET-reader lanes remain intact.

**Tech Stack:** C#/.NET 8 (SDK `8.0.427`), existing xUnit projects, System.Text.Json/HttpClient/ClientWebSocket, Windows native ACL/CurrentUser DPAPI/credential UI, existing React/TypeScript/Vite frontend. No new framework or dependency for Task 1.

**Spec:** `docs/superpowers/specs/2026-10-31-okx-integration-design.md` (read in full with the two completed SDD reports).

## Global Constraints

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

Current base is `1f833c58cb719bde5028239bd98477f84ce986f5`, `feature/apex-naval-command`, `/home/ubuntu/projects/apex-trading-ai`. Only parent-owned HANDOFF was dirty at plan entry. Do not reset it or infer permission to commit/push/CI/account-connect from this document. Future commits require parent authorization and must explicitly stage owned paths only.

## Review Focus

1. Duplicate/case-folded/typed JSON fields ignored by permissive HostConfig deserialization must refuse before any rewrite or secret access (Task 1: `OkxComposition_MalformedConfigPreservesExistingBytes`).
2. A same-UID Demo/Live or copied DPAPI envelope must not alias financial/credential authority (Task 1: `OkxCredentials_CrossScopeRootAndEnvironmentRefuse`; Task 3: `OkxBrokerBaseline_NoSyntheticOrCrossEnvironmentSeed`).
3. STOP while quota/send-lock queued, lost response or failed post-send persistence must never leak a late open or release holds (Task 2: `OkxExecution_StopWhileQueuedAndCrashMatrix`).
4. Zero-fill canceled orders disappear before ordinary archival windows; empty history must retain unknown obligations, not certify reconciliation (Task 3: `OkxReconciliation_ExpiredZeroFillCancelRetainsHold`).
5. Acknowledged attachment/OCO with partial/net-of-fee inventory must not be advertised as continuous protection or full liquidation (Task 4: `OkxProtection_PartialCoverageAndDustRemainUnsafe`; Task 5: `OkxPublication_UnknownProtectionNeverShowsSuccess`).

---

## File ownership and execution order

Tasks 1–6 are sequential reviewable stages, not six simultaneous workers. Later tasks below are dependency/acceptance contracts, not permission to begin implementing them from this plan alone. Before each later dispatch, parent expands only that stage into bounded executable steps against accepted preceding APIs, with a fresh input map—not repeated reconnaissance.

| Owner | Responsibility / exact roots |
|---|---|
| Task 1 | `src/Apex.Desktop/Okx/` security/composition; narrow `HostConfig.cs`, `WindowsLocalSetup.cs`, `Program.cs`; new `tests/Apex.Tests/Okx*Composition*`, config/credentials/activation tests |
| Task 2 | Separate `src/Apex.Adapters.Okx/Execution/` port; Core Naval execution substate/transactions; Desktop adapter composition; adapter/Core execution tests |
| Task 3 | Core broker issuer/baseline/reconciler, production store, execution observations/WS; reconciliation tests |
| Task 4 | Confirmed feed, evaluator registration/sizing/protection/exit loop and Core broker-aware admission; risk/protocol tests |
| Task 5 | Core versioned journal/projection, Desktop v2 reads/commands, frontend contracts/views/scene and parity tests |
| Task 6 | Native activation/provisioning bridge, existing packaging/test lane and extracted Windows/native/browser acceptance |

Task 1 does **not** modify Core's Offline admission/permit, GET-only adapter, frontend, financial persistence or legacy AccountManager. Its runtime has no owner until Task 3 baseline admission. This is an intentional security prerequisite, not a funded execution stub masquerading as integration.

## Shared evidence and command recipe (future execution, not run by this task)

Work from the existing checkout. Before every RED and final gate, set a new `GATE`; never overwrite earlier results. This creates evidence only under the configured scratch root. Preserve accepted final evidence durably through the parent before scratch expires.

```bash
export REPO=/home/ubuntu/projects/apex-trading-ai
export DOTNET=/home/ubuntu/.dotnet/dotnet
export GATE="$TMPDIR/okx-task1-$(python3 -c 'import uuid; print(uuid.uuid4().hex)')"
mkdir -p "$GATE/sdk" "$GATE/empty-feed"
python3 -c 'import os,json; from pathlib import Path; Path(os.environ["GATE"],"sdk/global.json").write_text(json.dumps({"sdk":{"version":"8.0.427","rollForward":"disable"}}))'
cd "$GATE/sdk"
"$DOTNET" --version
```

Expected SDK output: `8.0.427`; stop otherwise. For native Windows use the installed SDK8 absolute executable, lane-local global.json and actual `dotnet --version` assertion; record OS truthfully. Do not execute Linux commands and label them native proof.

Save this inline snapshot before each behavioral run (including after changing tests); `REPO`/`GATE` are the variables above:

```bash
python3 - <<'PY'
import os,json,hashlib,subprocess
from pathlib import Path
r=Path(os.environ['REPO']); g=Path(os.environ['GATE'])
paths=set(subprocess.check_output(['git','-C',str(r),'ls-files','-z']).decode().split('\0'))
paths.update(subprocess.check_output(['git','-C',str(r),'ls-files','--others','--exclude-standard','-z']).decode().split('\0'))
inputs={}
for name in sorted(paths-{''}):
    if name=='HANDOFF.md' or name.startswith('.superpowers/'): continue
    p=r/name
    if p.is_file():
        b=p.read_bytes(); inputs[name]={'bytes':len(b),'sha256':hashlib.sha256(b).hexdigest()}
out={'head':subprocess.check_output(['git','-C',str(r),'rev-parse','HEAD']).decode().strip(),'inputs':inputs}
(g/'inputs.json').write_text(json.dumps(out,indent=2))
assert inputs
PY
```

Add actual fixture/config hashes and executable/asset hashes to each run's evidence before claiming its result; fixture secret values never enter evidence. Snapshot before product edits for RED, then create a different GATE/map after changes for GREEN. Preserve test process exit, full log and parsed TRX results; compilation/setup failure is not behavioral RED. No assertion-count guess. Reuse matching existing asset/full-suite evidence rather than rerun unchanged gates.

Prerequisite restore, when needed and authorized, uses existing cache and an empty feed, not network fallback:
`"$DOTNET" restore "$REPO/tests/Apex.Tests/Apex.Tests.csproj" --source "$GATE/empty-feed"`.
If an exact cached dependency is missing, stop and report; do not install/alter locks silently. Unchanged staged frontend assets must be available for host fixtures; build them only if missing/stale, using the existing offline recipe in Task 6.

### Task 1: Secure environment/account/credential/config and default-off composition

**Deliverable:** strict immutable bindings, protected secret storage and scope-bound activation policy composed under the real Desktop lifecycle. Ordinary startup performs zero OKX key decryptions/private connections, runtime STOP, no financial seed/owner. No operational native provider or broker transport is claimed here.

**Entry prerequisites:** parent review of spec/plan; reconcile current source and parent dirty paths; establish an authorized Windows source-test lane **before native DPAPI implementation** (local approved Windows runner or separately authorized CI push/dispatch). Private synthetic fixtures only. No actual credentials/account or private network needed. Interactive provider is a Stage 6 gate. If native lane is absent, portable substeps may be prepared/reviewed but Task 1 cannot be accepted complete.

**Files:**
- Create `src/Apex.Desktop/Okx/OkxNavalOptions.cs`: public JSON option classes; strict raw validator; immutable binding/path derivation.
- Create `src/Apex.Desktop/Okx/OkxExecutionCredentials.cs`: lease, provider interface, envelope validation/provenance/fingerprint.
- Create `src/Apex.Desktop/Okx/WindowsOkxCredentialStore.cs`: scoped CurrentUser DPAPI store and native-private-path admission; no generic vault framework.
- Create `src/Apex.Desktop/Okx/OkxActivationPolicy.cs`: opaque activation policy/lease and unavailable production provider.
- Create `src/Apex.Desktop/Okx/OkxNavalRuntime.cs`: stopped lifecycle and frozen composition; no transport/financial ledger.
- Modify `src/Apex.Desktop/Services/HostConfig.cs`: `HostConfig` nullable property, strict raw validation before deserialization/save, update candidate validation.
- Modify `src/Apex.Desktop/Services/WindowsLocalSetup.cs`: `ReadConfig` invokes new raw validator before legacy normalization; preserve all existing presentation checks.
- Modify `src/Apex.Desktop/Program.cs`: validate/composition after private setup, own runtime under existing cancellation/disposal; observer event `okx`; no HTTP activation/commands yet.
- Create `tests/Apex.Tests/OkxCompositionTests.cs`, `OkxConfigTests.cs`, `OkxCredentialTests.cs`, `OkxNativeCredentialTests.cs`, `OkxActivationTests.cs`, `OkxCompositionFixture.cs` (test-local seams/providers and synthetic fixtures).

**Interfaces** (new names are design decisions, not preexisting APIs; namespace `Apex.Desktop.Okx`):

```csharp
// JSON classes: nullable string properties for strict validation, never defaults to Live.
public sealed class OkxNavalOptions { public string? Schema { get; set; } public OkxScopeOptions[]? Scopes { get; set; } }
// OkxScopeOptions has exactly the eight string fields specified by spec §3.
internal sealed record OkxScopeBinding(string ScopeId, NavalAccountId Account,
    string MainUid, string CredentialBindingId, string SafetyVersion, string ConfigDigest);
internal sealed record OkxScopePaths(string Root, string Credentials, string Authority, string Publication);
internal static class OkxConfiguration {
    internal static void ValidateRaw(string json);
    internal static IReadOnlyList<OkxScopeBinding> Bind(OkxNavalOptions? options);
    internal static OkxScopePaths Paths(string admittedDataRoot, OkxScopeBinding binding);
}
internal interface IOkxExecutionSecretProvider {
    OkxExecutionCredentialLease Open(OkxScopeBinding binding, OkxScopePaths paths);
}
// Lease owns byte[] key/secret/passphrase, Binding and Fingerprint, never serializable.
// Dispose zeroes all owned bytes; expose read-only spans only during request signing.
internal sealed class OkxExecutionCredentialLease : IDisposable {
    internal OkxScopeBinding Binding { get; }
    internal string Fingerprint { get; }
    internal ReadOnlySpan<byte> ApiKey { get; }
    internal ReadOnlySpan<byte> Secret { get; }
    internal ReadOnlySpan<byte> Passphrase { get; }
    public void Dispose();
}
internal enum OkxActivationPurpose { Observe, Execute }
internal sealed record OkxActivationRequest(OkxScopeBinding Binding, string CredentialFingerprint,
    Guid Session, long Generation, OkxActivationPurpose Purpose, DateTimeOffset ExpiresUtc);
internal interface IOkxProtectedActivationProvider {
    ValueTask<bool> ConfirmAsync(OkxActivationRequest request, CancellationToken cancellation);
}
// Result belongs to this policy instance; private lease constructor, no public DTO conversion.
internal sealed class OkxActivationPolicy {
    internal ValueTask<OkxActivationLease?> RequestAsync(OkxActivationRequest request,
        CancellationToken cancellation);
    internal bool TryConsume(OkxActivationLease lease, OkxActivationRequest expected,
        DateTimeOffset now);
    internal void Revoke();
}
// ctor policy(provider, Func<DateTimeOffset> clock); lease also carries private issuer token.
internal sealed class OkxActivationLease {} // private constructor; no serializer/HTTP representation
internal sealed class OkxNavalRuntime : IAsyncDisposable {
    internal static OkxNavalRuntime Compose(string admittedDataRoot,
        IReadOnlyList<OkxScopeBinding> bindings);
    internal string Mode { get; } // always STOP in Task 1
    internal string Reason { get; } // NOT_CONFIGURED or ACTIVATION_REQUIRED
    internal ValueTask StartAsync(CancellationToken cancellation); // host lifetime only, NOT trading START
    public ValueTask DisposeAsync();
}
```

`Compose` production entry hardcodes unavailable activation provider, Windows secret-store factory (lazy, never opened at startup) and no network factory. Test-only overload accepts provider/secret spies via existing test friendship, never from JSON/args/env or `TrustedSyntheticComposition`. Bindings are immutable copies. Test names may use a `StartHostAsync` wrapper but must drive actual `Program.Run`/production process; distinguish provider-unit proof from production-path proof.

`WindowsOkxCredentialStore` has constructor `(string admittedDataRoot)` and methods `Open(binding, paths)` and internal `Create(OkxScopeBinding binding, OkxScopePaths paths, string provenance, byte[] key, byte[] secret, byte[] passphrase)`. `Create` is create-new only, validates paths/ACLs before writing, DPAPI protects under scope/root purpose, zeroes temporary plaintext and never updates config. Stage 6 protected provisioning is its only production caller. Task 1 tests call it with synthetic bytes. Unsupported OS throws a sanitized unavailable code without writes; no plaintext file fallback. Do not refactor old local-access storage.

The lease constructor is internal `(OkxScopeBinding binding, string provenance, byte[] key, byte[] secret, byte[] passphrase)` and takes ownership of supplied arrays; provider copies/clears input buffers deliberately. New `OkxCredentialUseRegistry` in `OkxExecutionCredentials.cs` has `bool TryBind(OkxScopeBinding binding, string fingerprint)` and is owned by one runtime composition, not global mutable state. It rejects a fingerprint already bound to another scope/environment and permits repeat exact binding; Task 2 invokes it before a private session. Policy and lease bind that fingerprint explicitly. Fingerprints are private comparison data, not publication fields.

**Test helper contract:** `OkxCompositionFixture.ValidConfig(NavalEnvironment env)` returns the exact spec-shaped section with synthetic UID/quote; `StartHostAsync(string rawConfig)` launches the actual host with browser/news/bridge effects disabled by existing test fixture configuration; `Inventory()` captures private bytes/ACLs privately; provider spies expose counts only; `CredentialFixture` owns random synthetic byte arrays and disposes them. New production APIs are tested directly after minimal compile-only declarations exist; compile failure is never accepted as RED.

- [ ] **Step 1: Add strict-config behavioral tests first.** In `OkxConfigTests.cs`, use existing host launch machinery, with no new runtime API required, to send unknown schema/fields, duplicate `OkxNaval`, case variants, numeric environment, null/array/scalar field values, Offline, same-environment duplicate scope, bad UID/quote and `Enabled`/`AutoStart`/URL/path/seed/fixture flags. Each asserts exit `2`, unchanged preexisting non-log inventory/config and zero secret/private-peer calls. Valid omitted section and valid explicit scope are positive host controls. Minimal compilation of new option/interface declarations is setup, not a behavioral result.

- [ ] **Step 2: Capture RED for the new raw contract.** Run (after the evidence recipe):
  `"$DOTNET" test "$REPO/tests/Apex.Tests/Apex.Tests.csproj" --no-restore --filter FullyQualifiedName~OkxConfigTests --logger "trx;LogFileName=red.trx" --results-directory "$GATE/red"`.
  Expected intended RED: malformed OKX section currently ignored and host starts instead of exit `2`. Wait for actual readiness/exit, cancel/clean up owned process; do not count fixture timeout or missing assets as RED. Record which assertions were reached.

- [ ] **Step 3: Implement config/binding in exact files above.** Add `[JsonIgnore(Condition = WhenWritingNull)] public OkxNavalOptions? OkxNaval { get; set; }` to HostConfig. Invoke `OkxConfiguration.ValidateRaw` in ConfigStore.Load before deserialization and in WindowsLocalSetup.ReadConfig; validate serialized update candidate before writing. Strict validator only constrains the new section and preserves legacy parsing/old NavalRead rules. Derive digest deterministically from ordered validated scope fields plus fixed EEA/venue/safety version; no raw secrets/dynamic clocks. Enforce spec §3 field spellings, values and cardinality. Absence yields empty list; explicit null/missing required fields refuses. Sanitized `OKX_CONFIG_INVALID`/`OKX_SCOPE_INVALID` diagnostics; do not log raw input.

- [ ] **Step 4: Capture config GREEN with the same fixtures.** Use a fresh GATE/input map and Step 2 command with `green.trx`. All new config tests PASS with the omitted/valid-scope controls and no hidden skips. Existing ConfigStore/no-login profile validation behavior must remain unchanged.

- [ ] **Step 5: Add secret/activation/lifecycle tests with compile-only new seams.** Pin these assertions (literal values in tests, not only test names):

```csharp
// OkxComposition_DefaultAndConfiguredBootStayStopped
Assert.Equal("STOP", runtime.Mode);
Assert.Equal(0, secrets.OpenCount);
Assert.Equal(0, peer.PrivateRequestCount);
Assert.False(Directory.Exists(paths.Authority));
// OkxActivation_ExactScopeSingleUseAndRevocation (friend-only fake provider)
var lease = await policy.RequestAsync(request, CancellationToken.None);
Assert.NotNull(lease); // positive control must reach provider; ordinary provider returns null
Assert.False(policy.TryConsume(lease!, request with { Generation = request.Generation + 1 }, now));
Assert.True(policy.TryConsume(lease!, request, now));
Assert.False(policy.TryConsume(lease!, request, now));
// Separate leases: wrong issuer, purpose, account/env, config, expired >120s, Revoke => false.
```

  Add `OkxCredentials_CrossScopeRootAndEnvironmentRefuse`, `OkxCredentials_LeaseErasesOnDisposeAndErrors`, `OkxCredentials_NativeDpapiAclAndCreateNew` with genuine synthetic positive envelope; wrong root, binding, purpose, environment, copied/corrupted DPAPI, broad ACL/reparse and overwrite refuse without modification. Same UID is not same binding. Check fingerprint reuse prohibition before opening a second connection (Task 1 tests validation registry; Task 2 consumes it). Native tests assert Windows rather than silently returning on Linux; run only their named native class on Windows and report unexecuted native gates explicitly.

  Add `OkxComposition_MalformedConfigPreservesExistingBytes`, `OkxComposition_ProductionIgnoresSyntheticUnlocks`, `OkxActivation_LocalAndProtectedOperatorCannotMintLease`. Main uses unavailable provider even with legacy `AllowLive`, `LiveTradingFeature`, `TrustedSyntheticComposition` fixture controls or forged HTTP fields. New HTTP activation path remains 404/denied; no UI token. Production host configured valid scope restarts still STOP, no credential opens. Dispose during startup/cancellation drains owned lifetime without creating financial state.

- [ ] **Step 6: Execute the relevant security REDs.** Capture fresh inputs for each reached boundary. Run portable classes with:
  `"$DOTNET" test "$REPO/tests/Apex.Tests/Apex.Tests.csproj" --no-restore --filter 'FullyQualifiedName~OkxCompositionTests|FullyQualifiedName~OkxActivationTests|FullyQualifiedName~OkxCredentialTests' --logger "trx;LogFileName=security-red.trx" --results-directory "$GATE/red"`.
  Intended REDs include valid fake activation cannot be consumed and lease/binding behavior absent under compile-only seams. Retain default-deny baseline passes as controls; do not manufacture RED by relaxing production guards. Native DPAPI RED must run on the established native lane against captured bytes; setup/API-availability failures are blockers, not behavior failures.

- [ ] **Step 7: Implement minimum lease, policy and stopped runtime.** Private issuer token plus reference identity/session/config/generation/purpose checks; TTL at most `120 seconds`, reject future/expired request bounds, revoke on lifecycle changes. Only protected provider callback can issue; production unavailable provider denies. Keep bool entirely inside the direct provider seam, never trusted from HTTP/pipe/config. Native store uses CurrentUser DPAPI, scoped entropy, private directories and atomic create-new; no automatic credential provisioning on boot. No Core references beyond identity types, no owner creation, no fake authenticated account. Production Compose freezes options and owns disposal; add Program observer `okx` only for source tests, no public diagnostics secret dump.

- [ ] **Step 8: Run portable/native GREEN and narrow regression gate.** Fresh map per relevant changed input, repeat original failing fixtures unchanged through corrected code; record actual parsed TRX and exits. Run portable Step 6 filter with `security-green.trx`. On Windows run `dotnet test <absolute Apex.Tests.csproj> --no-restore --filter FullyQualifiedName~OkxNativeCredentialTests --logger "trx;LogFileName=native-green.trx" --results-directory <unique-native-evidence>` with SDK pin/source map/native ACL inventory; tests must include valid decrypt control plus all refusal/no-mutation controls. Then targeted `NavalReadAccessTests`, `NavalCashDurabilityTests`, `WindowsLocal` and `Desktop` startup tests affected by these files; verify filters select actual tests before claiming coverage. Existing GET-reader inputs are unchanged; retain their earlier source-bound evidence and run their regressions later only when adapter inputs change.

- [ ] **Step 9: Review/record exact prerequisite and hand back.** Diff only listed files, no secret/private fixtures committed, all tests attributed to product/input bytes; parent reviews raw parsing order, lifecycle and fake-provider isolation. Task 1 complete only when its native store gate passed. State clearly: activation provider unavailable, no broker baseline/port, private/trade gates untouched. If commit authorized, stage the explicit files listed above and use `feat(okx): add isolated default-off naval composition`; otherwise leave owned diff for parent. Do not edit HANDOFF or commit merely because this step exists.

### Task 2: Durable execution/uncertainty and separate cash-SPOT port

**Depends on:** accepted Task 1; no production activation required for scripted peer tests.

**Files:** Create `src/Apex.Adapters.Okx/Execution/OkxExecutionContracts.cs`, `OkxExecutionClient.cs`, `OkxRequestSigner.cs`; create `src/Apex.Core/Naval/NavalExecutionState.cs`, `NavalExecutionTransactions.cs`; modify `NavalCashModel.cs`, `NavalCoreStateOwner.cs`, `NavalPortfolioCoordinator.cs` in that Core directory for versioned broker substate/transaction validation only; modify `src/Apex.Desktop/Okx/OkxNavalRuntime.cs` and `src/Apex.Desktop/Apex.Desktop.csproj` for lazy adapter reference; create `tests/Apex.Adapters.Okx.Tests/OkxExecutionTests.cs`, `tests/Apex.Tests/OkxExecutionDurabilityTests.cs`. Existing reader source/DTOs stay unchanged.

**Interfaces:**
- Consumes Task 1 binding/secret lease/activation validation. Adapter remains Core-independent: new `OkxExecutionScope(string Environment, string Uid, string MainUid, string QuoteCurrency, string CredentialBindingId)` is a transport binding only, mapped explicitly from Desktop `OkxDemo → Demo`, `Live → Live`; reject every other value.
- Adapter declares `Task<OkxWriteOutcome> SubmitAsync(OkxPreparedRequest request, CancellationToken ct)`, `CancelAsync` and `PlaceProtectionAsync`/`CancelProtectionAsync` with the same signature, and `Task<OkxOrderObservation> LookupAsync(string instrument, string? ordId, string originalClOrdId, CancellationToken ct)`. `OkxPreparedRequest` owns exact method/path/body bytes, client ID, absolute deadline and scope; `OkxWriteOutcome` is a closed discriminant `ProvenNotSent|Acknowledged|Rejected|Unknown` with original IDs and sanitized errors. No method accepts arbitrary URL/path from a caller.
- Core new `NavalExecutionIntent` stores original reservation/command/signal, binding digest, IDs, exact bytes/digest, source/catalog/revision/deadline and stage as spec §5. `NavalExecutionTransactions.Prepare(string reservationId, NavalExecutionIntent intent)` and `RecordOutcome(string intentId, NavalDispatchOutcome outcome)` return existing `NavalRiskResult`, using the coordinator's same commit/owner (not independent public mutation service). `NavalDispatchOutcome` is Core's typed mapping of adapter outcome, never fill authority.
- Dispatch helper `Task DispatchAsync(string intentId, CancellationToken ct)` belongs to Desktop runtime; final fence reads Core current revision/STOP epoch through broker session introduced Task 3. Until then only test composition may exercise dispatch; production refuses broker readiness.

**Required future test cycle:**
- [ ] RED `OkxExecution_StopWhileQueuedAndCrashMatrix`: actual store faults before reserve, after prepared commit, after Dispatching, socket loss, `50004`, response/flush/rename ambiguity; assert original client ID/payload survives reopen, holds retained, at most one original open, no ack economics. STOP/expiry/config/catalog/source mutation after wait yields zero send. Both proven-not-sent and accepted scripted controls reach the send boundary.
- [ ] RED exact signer wire vectors (UTC ms REST, method/query/body bytes), Demo `1`/Live `0` same EEA host, no redirect/arbitrary route, no futures/transfer/loan/earn/withdraw/settings, duplicate ID/not-found retain uncertainty, no mutation retry on 429/5xx. Client IDs ≤32 alphanumeric and never reused after terminal/restart.
- [ ] Minimal implementation follows spec §5; versioned broker-only substate, no Offline reclassification or default-final fields; no real network credentials.
- [ ] GREEN command groups: `dotnet test tests/Apex.Adapters.Okx.Tests/Apex.Adapters.Okx.Tests.csproj --no-restore --filter FullyQualifiedName~OkxExecutionTests` and `dotnet test tests/Apex.Tests/Apex.Tests.csproj --no-restore --filter FullyQualifiedName~OkxExecutionDurabilityTests`, using pinned SDK/unique TRX/input maps. Re-run accepted read-only adapter tests only because the assembly/project inputs changed, preserving its exact contracts.
- [ ] Independent gate accepts uncertainty/durability, not broker fills/START. Parent-authorized commit suggestion: `feat(okx): persist execution intent and uncertain dispatch`.

### Task 3: Trusted baseline, evidence issuer and bounded reconciliation

**Files:** Create `src/Apex.Core/Naval/NavalBrokerSession.cs`, `NavalBrokerReconciler.cs`, `NavalBrokerCoverage.cs`; modify `NavalEvidence.cs`, `NavalCashRiskPolicy.cs`, `NavalCoreStateOwner.cs`, `NavalPortfolioCoordinator.cs` in that directory; create `src/Apex.Desktop/Okx/OkxNavalStore.cs` implementing existing `IKeyValueStore`, independent of Backtester; extend `src/Apex.Adapters.Okx/Execution/OkxExecutionContracts.cs`, create `OkxExecutionObservations.cs`, `OkxObservationStream.cs`; modify Desktop `OkxNavalRuntime.cs`; create `tests/Apex.Tests/OkxReconciliationTests.cs`, `OkxBrokerBaselineTests.cs` and `tests/Apex.Adapters.Okx.Tests/OkxExecutionObservationTests.cs`.

**Interfaces:**
- `NavalBrokerBinding` in `NavalBrokerSession.cs` maps immutable Task 1 identity to Core data `(NavalAccountId Account, string MainUid, string ConfigDigest, string CredentialBindingId, string SafetyVersion)`; no data record grants authority.
- `NavalBrokerSession.Open(INavalStateOwner owner, NavalPortfolioDefinition definition, NavalBrokerBinding binding, INavalClock clock)` is internal and validates a broker-admitted definition/baseline. Bootstrap `NavalBrokerReconciler.InitializeBaseline(NavalBaselineCandidate candidate, NavalBaselineApproval approval)` returns existing `NavalRiskResult`; candidate/approval are Core-owned, immutable observation-derived data and opaque session-issued protected approval respectively. A public seed cannot call this. New owner initialization overload receives only the validated baseline result; Offline constructor stays unchanged.
- `NavalBrokerReconciler.Apply(NavalBrokerObservationBatch batch)` returns `NavalRiskResult`; batch contains raw signed fee/order/algo/balance/bill/window evidence but no trusted Complete/Final flag. `NavalBrokerSession.TryIssueStart()` returns opaque `NavalBrokerStartPermit?` only for current owner/revision/generation after accepted coverage; coordinator `Start(NavalBrokerStartPermit permit)` consumes that permit alongside still-valid runtime activation. Existing `Start(NavalStartPermit)` remains strictly Offline.
- Adapter `Task<OkxObservationBatch> ReadAsync(OkxObservationQuery query, CancellationToken ct)` and `IAsyncEnumerable<OkxStreamObservation> ObserveAsync(CancellationToken ct)` provide untrusted data with exact scopes/windows/cursor evidence; no Core reference/receipt factory. Baseline/protected approval is not inferred from transport completion. Core's private nested issuer token plus broker-mode rejection of offline receipt/permit prevents the broad Desktop friend seam becoming a receipt bypass.

**Required behavior / future cycles:**
- [ ] RED then GREEN `OkxBrokerBaseline_NoSyntheticOrCrossEnvironmentSeed`: valid clean baseline control commits quote cash once; synthetic config seed, copied state, wrong key/UID/currency, external order/inventory, liabilities, unknown permission, fee mismatch and missing owner marker refuse unchanged. Production store fsync/atomic ownership proof before owner open; no Backtester link.
- [ ] RED then GREEN `OkxReconciliation_ExpiredZeroFillCancelRetainsHold`, duplicate/out-of-order REST/WS fills, terminal without live event, cancel/fill/late fee, base/quote fee, rebates/third-token/corrections/conflicts, retention and gap boundaries. Assert literal cash/inventory/cost/fee holds/ownership once; cumulative accFillSz/fee never creates duplicate economics; empty/51603 never proves never-sent. Track old long-lived orders independently of cTime pages. Reconnect/restart → STOP → reconcile still STOP → explicit START only.
- [ ] Exercise real serializers/store plus private/business stream scripted peers, no network accounts. `dotnet test tests/Apex.Tests/Apex.Tests.csproj --no-restore --filter 'FullyQualifiedName~OkxReconciliationTests|FullyQualifiedName~OkxBrokerBaselineTests|FullyQualifiedName~NavalCashFinalityRegressionTests'`; adapter `--filter FullyQualifiedName~OkxExecutionObservationTests`. Use evidence recipe/unique TRX. Parent-authorized commit: `feat(okx): reconcile broker cash and execution obligations`.

### Task 4: Confirmed feed → strategies → shared risk → protection/reduction

**Files:** Create `src/Apex.Adapters.Okx/Execution/OkxMarketDataClient.cs`, `src/Apex.Core/Naval/NavalBrokerSignals.cs`, `NavalBrokerProtection.cs`, `src/Apex.Desktop/Okx/OkxNavalLoop.cs`; modify `NavalCashRiskPolicy.cs`, `NavalPortfolioCoordinator.cs`, `NavalCoreStateOwner.cs` as needed for durable registered signals/profile-bound admission, and Desktop `OkxNavalRuntime.cs`; create `tests/Apex.Tests/OkxNavalLoopTests.cs`, `OkxProtectionTests.cs`, `OkxSharedRiskTests.cs`. Reuse evaluator/fleet rules unchanged; no CFD StrategyBook edits.

**Interfaces:**
- Adapter `Task<OkxConfirmedD1Batch> ReadD1Async(string instrument, CancellationToken ct)` returns source timestamp, explicit confirmed bars and pagination/coverage; `Task<OkxExecutableQuote> ReadQuoteAsync(string instrument, CancellationToken ct)` returns bid/ask/depth/spec provenance. Unknown confirmations/constraints are errors, not defaults.
- Core `NavalBrokerSignals.Register(NavalBrokerSignalCandidate candidate)` returns `NavalRiskResult`, validates actual evaluator rule/bar/expiry/account/fleet identity and persists the signal within existing authority; reserve still consumes `NavalEntryIntent` through `ForFleet(...).TryReserve`.
- `NavalBrokerProtection.AdmitEntry(string reservationId, string reviewedProfileId)` and `PrepareReduction(string reservationId, decimal requestedBase, string commandId)` return `NavalRiskResult` and durable execution intent links, never raw port calls. Reviewed profiles are release-coded to evidence/capability digest, not a config/fixture switch. `OkxNavalLoop.TickAsync(CancellationToken ct)` consumes session + feed + existing evaluator/risk; no independent capital state.

**Required behavior / future cycles:**
- [ ] RED/GREEN actual evaluator with closed UTC-D1/source-clock fixture: current/future/missing/regressed bars, stale/crossed quote, wrong lot/tick/min/max/actual quote, unsupported USDT/USD unit maxima and signal replay refuse; valid exit survives BTC entry filter.
- [ ] RED/GREEN ALFA/BRAVO concurrent same-account symbols, pending/partial/unknown/unsettled consume central `3`/fleet `2`, `0.5/1.5/5`, local/central `≤1×`, no borrow/cross-account spending; observe literal shared holdings not only reserve status.
- [ ] RED/GREEN `OkxProtection_PartialCoverageAndDustRemainUnsafe`: actual peer child orders/fills, missing/rejected/partial-effective attachment, sibling cancel races/restart/base-fee quantity/dust and competing emergency exit. FOK candidate tests cannot unlock production: default production profile set is empty until spec §7 external gate. Assert zero production opens without reviewed exact-scope profile, and test the common state machine with friend-only protocol authority separately. No local price watcher substitutes protection.
- [ ] Commands: `dotnet test tests/Apex.Tests/Apex.Tests.csproj --no-restore --filter 'FullyQualifiedName~OkxNavalLoopTests|FullyQualifiedName~OkxProtectionTests|FullyQualifiedName~OkxSharedRiskTests|FullyQualifiedName~NavalCashCoordinatorTests'` plus adapter market-data tests in `OkxExecutionObservationTests.cs`. Parent-authorized commit: `feat(okx): wire naval feed risk and protective lifecycle`. Completion states local integration versus still-blocked venue profile separately.

### Task 5: Versioned durable publication, exact controls and five-view parity

**Files:** Modify `src/Apex.Core/Naval/NavalReadJournal.cs`, `NavalReadContracts.cs`; create `src/Apex.Core/Naval/NavalBrokerPublication.cs`; create `src/Apex.Desktop/Okx/OkxPublicationProjection.cs`, `OkxCommandEndpoints.cs`; modify `src/Apex.Desktop/Naval/NavalPublicationReader.cs`, `NavalReadService.cs`, `NavalAccessPolicy.cs`, `src/Apex.Desktop/Program.cs`; modify frontend `src/read/{contracts,decode,client,store,selectors}.ts`, `src/scene/model.ts`, `src/views/{CommandCenter,FleetOperations,TacticalRadar,TradeAnimations,AnalyticsCenter}.tsx` under `frontend/naval/`; modify `frontend/naval/src/App.tsx` only for shared command/status wiring; create `frontend/naval/tests/okx-contract.test.ts`, `okx-parity.test.ts`; create `tests/Apex.Tests/OkxPublicationTests.cs`, `OkxCommandAccessTests.cs`; narrowly update existing `NavalReadAccessTests.cs` route-negatives.

**Interfaces:**
- Core `NavalBrokerPublication.Create(EngineState committed)` returns new explicit `NavalBrokerReadPublicationV2`; no optimistic/uncommitted states. Definition includes versioned broker fact types/source clocks/lineage as spec §8, preserving original v1 types and meaning.
- Desktop `OkxPublicationProjection.Rebuild(NavalBrokerReadPublicationV2 publication, string destination)` performs atomic rebuildable export only; `NavalPublicationReader` dispatches by explicit schema rather than guessing environment.
- `OkxCommandEndpoints.Map(WebApplication app, OkxNavalRuntime runtime, INavalAccessPolicy access)` maps only the three exact spec §8 POSTs. Closed request `{ commandId: string }` routes to runtime `StartTradingAsync(string scopeId, string commandId, CancellationToken ct)`, `StopTradingAsync(...)`, `EmergencyAsync(...)`, returning typed `OkxCommandResult(Status, Code, DurableRevision)` declared in `OkxCommandEndpoints.cs`. START internally consumes held arming + Task 3 permit; no capability in request/reply. There is no activate/unlock endpoint.
- Frontend `decodeBrokerV2(input: unknown): BrokerPublicationV2` and selector functions derive all views/scene from the same exact-decimal normalized store. Separate v1 decoder unchanged; ephemeral runtime status cannot rewrite historical financial facts.

**Required behavior / future cycles:**
- [ ] RED/GREEN storage fault before/after commit/export, restart/repair/cursor gaps, unknown version/checksum-valid inexact decimal/cross-env lineage and stale overlay. `OkxPublication_UnknownProtectionNeverShowsSuccess`: unknown/unsettled no success/P&L/close animation; projected ALFA/BRAVO totals are not separately funded.
- [ ] RED/GREEN production-host anonymous/Reader/protected Operator/desktop Operator, malformed exact routes, CSRF/Origin/Fetch Metadata and expired/replayed arming; local Operator cannot activate. Break publication and still click textual emergency, observe durable STOP and exact-owned bounded cancel/reduction with strict peer. Queued submit after STOP remains fenced.
- [ ] RED/GREEN frontend v1/v2 positive, malformed/freshness fallbacks and restored Retry, five views/list/SVG/optional3D same IDs/pages/selections and original Polish panel link; scan hydrated DOM/public buffers privately for actual secrets/digests/capabilities with leak-positive controls.
- [ ] Commands: pinned `dotnet test tests/Apex.Tests/Apex.Tests.csproj --no-restore --filter 'FullyQualifiedName~OkxPublicationTests|FullyQualifiedName~OkxCommandAccessTests|FullyQualifiedName~NavalReadAccessTests'`; in `frontend/naval`, `npm run test`, `npm run build`, `npm run test:build`. Source-map both TS inputs and emitted/staged assets. Parent-authorized commit: `feat(okx): publish committed broker state and guarded controls`.

### Task 6: Native provider, integrated delivery and separately authorized private acceptance

**Files:** Create `src/Apex.Desktop/Okx/WindowsOkxActivationProvider.cs`, `WindowsOkxControlPipe.cs`, `WindowsOkxProvisioning.cs`; modify Desktop `Program.cs`, `OkxActivationPolicy.cs`, `OkxNavalRuntime.cs` for native provider/explicit local action routing (never config-driven fake authority); create `tests/Apex.Tests/OkxNativeActivationTests.cs`, `OkxNativePackageTests.cs`; extend Task 1's `tests/Apex.Tests/OkxNativeCredentialTests.cs` only when native provisioning adds behavior; extend existing `.github/workflows/windows-demo.yml`, `tools/windows/Zbuduj-APEX.ps1`, `tools/windows/Sprawdz-Pakiet.ps1`, `tools/windows/Test-FinalDelivery.ps1` and `tools/windows/Test-LocalSetupSource.ps1` for the scoped native gates. Keep existing builder/verifier/notice policy; no parallel release framework.

**Interfaces:**
- `WindowsOkxActivationProvider : IOkxProtectedActivationProvider` implements actual host-owned secure confirmation described by spec §4. Production runtime swaps unavailable provider only on supported interactive Windows, never based on `test`/fixture config. OS result must match outstanding immutable request and host SID/session; no caller success flag.
- `WindowsOkxControlPipe.RunAsync(CancellationToken ct)` accepts only `ScopeId`/purpose/action requests and returns nonsecret status; DACL current SID + reject remote, server/client PID/SID/session checks and replay/rate bounds. CLI `--okx-activate` client runs before normal instance-lock conflict but never becomes an alternate trading host; it contacts/verifies the existing host. No active host or peer mismatch means refuse.
- `WindowsOkxProvisioning.RunAsync(string scopeId, CancellationToken ct)` is explicit native-only credential entry, calls Task 1 create-new store after protected confirmation and provenance/scope review. Key material never in CLI, clipboard, HTTP, screenshots or logs; no connections during provisioning. Cancellation leaves existing config/store unchanged. Reprovisioning is a stopped, reviewed rotation flow, not overwrite.

**Required behavior / future cycles:**
- [ ] Establish interactive native provider lane before implementing native prompt/pipe code; RED/GREEN cancellation, wrong SID/session/purpose, unsupported provider/passwordless path, actual secure prompt positive, forged pipe/HTTP/CLI/config result and replay. A fake callback/headless CI result is not native confirmation. No actual user credential is collected in this planning conversation or published evidence.
- [ ] Final source/asset hashes before gates. Frontend order: `npm ci --offline --ignore-scripts --no-audit --no-fund`, `npm run typecheck`, `npm run test`, `npm run build`, `npm run test:build`; then pinned `dotnet build APEX.Trading.Demo.sln --no-restore` and actual affected/full solution tests/SDK consumers, native publish via existing builder. Resolve missing restore caches openly; no fabricated package metadata.
- [ ] Extract actual self-contained Windows candidate into a new directory, run its shipped verifier, observe default browser after successful bind; first run, genuine prior EXE upgrade, restart with no manual login/no injected credentials, custom protected mode, Reader API-only, port collision/second instance, private ACL/CurrentUser DPAPI and cancellation/write faults. All five views/optional3D/fallback/Retry and Polish panel retained. Execute original textual emergency under publication failure against protocol peer; no broker calls. Preserve source/binary/package hashes and exact Windows OS/version; hosted Windows is not user's Windows 11 desktop.
- [ ] Independent scoped review of current final bytes; source-suite, synthetic peer, native UI and extracted-package evidence separately listed. Parent-authorized commit suggestion: `feat(okx): add protected native activation and delivery gates`. Preserve candidate and nonsecret proof outside scratch, hash-check durable copy. An integration blocked by native provider or protection is not called working live trading.
- [ ] **External acceptance, not automatically run:** obtain explicit scope/approval for actual credential provisioning and private Demo observation; then recovery/continuity; then specific Demo entry/protection/cancel/exit transactions. Validate candidate profile on exact EEA account/instrument/fee mode, including partial/reject and real child fills, without claiming universal guarantee. Live observation/arming/funding/trades require their own approvals and profile evidence; Demo PASS cannot unlock Live. Never transfer/withdraw/borrow as a test. If protection/permission/units cannot meet spec, report unsupported path and retained obligations; do not convert permanent lockout into completion.

## Plan handoff boundary

Only Task 1 is ready for bounded implementation after parent review and native-lane prerequisite. Tasks 2–6 define exact dependency behavior but require dispatch-local detail/source-bound test selection, especially native-provider proof and the unproved protection profile. No reconnaissance rerun, product changes, build/tests, CI, commits or account operations were performed by the author of this plan. Self-review and pairwise preflight are retained in `.superpowers/sdd/2026-10-31-okx-integration/design-plan-report.md`.
