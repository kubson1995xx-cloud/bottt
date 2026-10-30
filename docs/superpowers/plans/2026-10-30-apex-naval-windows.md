# APEX Naval Command Windows Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver a source-bound, self-contained Windows x64 ZIP whose actual EXE starts a private loopback-only host, opens the default browser and presents the existing Naval/Polish interfaces safely without broker access.

**Architecture:** Extend the existing .NET8 host and PowerShell builder/verifier. Windows private-data validation precedes all state access; empty-root local provisioning uses existing Basic/Bearer auth and a build-produced historical export, while existing configurations remain untouched. Native Windows source/process/UI/CDP gates exercise independently extracted payloads, never a simulation-enabled production entry.

**Tech Stack:** Existing C# net8.0 / ASP.NET Core, Win32 ACL/DPAPI/native modal APIs, existing React19/TypeScript/Vite/Three/Fiber pins, Windows PowerShell5.1, Node26, Python stdlib ZIP validation, existing xUnit versions and Edge CDP via .NET ClientWebSocket.

**Spec:** `docs/superpowers/specs/2026-10-30-apex-naval-windows-design.md` (read both documents).

## Global Constraints

- Base reconnaissance: `fff4baf65cc4445c5630790b668fd0f713fab006`, `feature/apex-naval-command`, `/home/ubuntu/projects/apex-trading-ai`; check current top HANDOFF before execution. Do not reset or modify other worktrees.
- Exact code/reasoning model/provider `gpt-6.1-sol` / `openai-codex`, no fallback. This document is a plan, not authorization to execute in the planning task.
- Final name `APEX-NAVAL-COMMAND-Windows-x64.zip`; primary entry `APEX-Desktop\ApexDesktop.exe`; Windows 11 Pro x64 recipient, no manual runtime/developer dependencies.
- Bind HTTP, WebSocket and TCP only `127.0.0.1`, never IPv6/wildcard/LAN; HTTP ports 1..65535, preserve optional TCP0 fixture semantics.
- Preserve all five views, old Polish panel, optional lazy3D, six strategy families, custom profiles/config, current activation state and risk 0.5% / 1.5% / 5% / 5x, SL/TP, Emergency, replay protection and unconditional REAL blocking.
- No real account connections, DEMO/LIVE transactions, broker credentials, new execution/financial authority, simulation bypass, AI Lab/training/LLM/updater, merge or deployment. Synthetic local test peers are not broker accounts/trades.
- Keep existing builder/verifier/extracted-package seam and exact frontend pins/locks. No new npm/browser dependency or packaging framework. New native Windows tests are nonshipped.
- Existing Windows configs remain preserved; only empty/new Windows roots get `windows-local-presentation-v1`. Linux defaults remain Naval-off. No anonymous credential bootstrap in the new profile.
- New data protected at creation; existing unsafe data fails closed without repairing ACLs, modifying files or deleting state. No package-local data fallback.
- Preserve failed evidence; record pre-run source/config/test/build hashes and reason before every behavioral/final gate. Reuse unchanged accepted evidence, do not rerun completed Task5A/UI/research campaigns as progress.
- Run actual `windows-latest` CI only after implementation/review on the authorized feature ref. Hosted Windows Server != physical Windows11/GPU/cTrader acceptance. Read back exact remote SHA/run/artifact/ZIP before success claims.

## Review Focus

1. Existing or interrupted setup, broad **nested** file ACL, null DACL, link substitution and unsafe ancestors: refuse before config/lock/log mutation, never silently reset data (Tasks1/3).
2. Windows KnownFolder/8.3 aliases and share semantics: reject real package containment and reparse paths without rejecting safe siblings or ordinary system/profile ancestors (Task1).
3. Already-running host or foreign listener: no browser open before successful bind and no second instance owning state; cleanup permits a later restart (Task4).
4. Browser native auth caching and role isolation: root-first authenticated redirect works after callback removal, reader cannot load HTML/modules/styles, corrupted H cannot disable textual Emergency (Tasks3/6).
5. Hosted SDK/browser concealing missing shipped dependencies or package mutation: runtime-isolated EXE, real module origins, actual default-shell call, fresh private browser profile, exact ZIP before/after inventories and shipped verifier (Tasks5/6).

---

## Execution setup and file map

Do not implement during the read-only planning assignment. At implementation start run `git status --short`, `git rev-parse HEAD`, `git worktree list` and read current HANDOFF. Any concurrent work is preserved. The user has selected the existing feature checkout; no new worktree/branch or integration decision is needed unless isolation becomes necessary.

Create only these focused product files:
- `src/Apex.Desktop/Services/WindowsPrivateData.cs`: private root lease, canonical containment and native directory creation.
- `src/Apex.Desktop/Services/WindowsPathSecurity.cs`: shared Windows-only owner/DACL/ancestor inspection and file identity checks.
- `src/Apex.Desktop/Services/WindowsLocalSetup.cs`: initialization state, public presentation validation/copy and existing-config preservation.
- `src/Apex.Desktop/Services/WindowsLocalAccess.cs`: RNG digest map and CurrentUser DPAPI storage/load.
- `src/Apex.Desktop/Services/WindowsLocalAccessPrompt.cs`: tiny Win32 native credential modal; no UI framework.
- `src/Apex.Desktop/Services/DesktopBrowser.cs`: safe URI and default-shell adapter.

Modify `Program.cs`, `Services/HostConfig.cs`, Windows branch of `Naval/NavalAccessPolicy.cs`, and `SimulationAssemblyAccess.cs` only where the new boundary needs them. Keep unrelated Core/runtime/bridge/strategy/frontend code unchanged.

Create `tests/Apex.Windows.Tests/{Apex.Windows.Tests.csproj,WindowsPrivateDataTests.cs,WindowsLocalSetupTests.cs,WindowsStartupTests.cs,WindowsPackagedProcessTests.cs,WindowsFirstRunUiTests.cs,WindowsBrowserAcceptanceTests.cs,WindowsTestHost.cs,WindowsCdpBrowser.cs}`. Keep test project out of the cross-platform solution: builder explicitly restores/builds/tests it only for native win-x64. Reuse .NET.Test.Sdk17.11.1/xunit2.9.2/runner2.8.2 already used in Apex.Tests. Add only `InternalsVisibleTo("Apex.Windows.Tests")`; no public production testing switch. Source tests carry `[Trait("Gate","Source")]`, packaged process/UI `[Trait("Gate","Package")]`, browser `[Trait("Gate","Browser")]`. Each project execution asserts Windows rather than reporting a Linux pseudo-PASS.

Extend existing `tools/windows/{Zbuduj-APEX.ps1,Sprawdz-Pakiet.ps1,TestEvidence.ps1,Smoke-Windows.ps1}`, `tests/package/{Test-BacktesterGate.ps1,Test-NavalPackage.ps1}`, `tests/panel/package.test.cjs`, `.github/workflows/windows-demo.yml`. Add `tools/windows/PRZECZYTAJ-WINDOWS.md` as source for root package `PRZECZYTAJ.md`; update `docs/{INSTALACJA_WINDOWS.md,NAVAL_READ_PUBLICATION.md}` and README. Update status/HANDOFF only when actual stage evidence exists.

### Command conventions

Commands below are **future execution**, not already-run results. Native PowerShell working directory is the checked-out repository. For targeted gates choose a new private `$E` under the runner/test owner's scratch; do not write test evidence into the sealed package. Configure `$env:DOTNET_HOST_PATH=(Get-Command dotnet).Source` before existing producer process fixtures. `.NET` commands with `--no-build --no-restore` presume the explicitly named preceding restore/build succeeded on the same inputs.

Commit steps are for the later authorized implementation executor, not the planning subagent. Stage explicit task paths only, never `git add .`; no push/CI until Task7. A scoped independent review may reject a task before later tasks proceed.

### Task 1: Enforce native private data before any startup access

**Files:** New Windows test project, `WindowsPrivateDataTests.cs`, `WindowsPrivateData.cs`, `WindowsPathSecurity.cs`; modify `Program.cs:33-49`, `HostConfig.cs:159-165`, Windows `NavalPrivatePath` in `NavalAccessPolicy.cs:224-242`, `SimulationAssemblyAccess.cs`.

**Interfaces:**
- Produces `WindowsPrivateData.Open(string requestedPath, string releaseRoot): WindowsPrivateDataLease`, with `string Path`, `bool IsNew`, `void ValidateTree()`, `Dispose()`.
- Produces `WindowsPathSecurity.Validate(FileSystemInfo target, bool privateTarget): void`. Existing `NavalPrivatePath.Check(...)` signature remains unchanged; adapt safe exception codes at its boundary.
- Program calls native guard before `Directory.CreateDirectory`, `ConfigStore`, `apex.lock`, log/account construction. `ConfigStore.DefaultDataDir` no longer falls back to executable directory on Windows when LocalAppData is missing.

- [ ] Write Source-trait tests `CreatesPrivateRootBeforeAnyFile`, `ExistingBroadNestedFileRefusesUnchanged`, `NullDaclAndForeignOwnerRefuse`, `ReparseLeafAncestorAndSwapRefuse`, `PackageAliasContainmentAndSafeSibling`, `TypicalSystemAncestorsAndInheritOnlyAcesWork`, `UnsafeDeleteChildAncestorRefuses`, `MissingLocalAppDataHasNoPackageFallback`. Assert owner=current SID, protected DACL exactly current SID/SYSTEM/Administrators for new root, no extra files/byte/ACL changes for all existing-tree negatives; test create-sibling rights separately from replace-child rights.
- [ ] On native Windows restore/build the new project, then run `dotnet test tests/Apex.Windows.Tests/Apex.Windows.Tests.csproj -c Release --filter FullyQualifiedName~WindowsPrivateDataTests --logger "trx;LogFileName=private-data-red.trx" --results-directory $E`. Missing APIs/old mutation behavior must fail at the intended assertion, not just invalid fixture setup; retain red source map/log.
- [ ] Implement protected descriptor at creation using native APIs, canonical long-path/component checks, bounded non-following tree validation and no-delete-sharing leases. Reject UNC/device/ADS/unsupported ACLs; handle inheritance-only ACEs and native TrustedInstaller ancestor owner without admitting it to private grants. Keep Unix Naval checks unchanged. Translate failures to spec codes and exit2; no raw exception leakage.
- [ ] Run same tests with `private-data-green.trx`; all pass, zero skip. Run affected `dotnet test tests/Apex.Tests/Apex.Tests.csproj -c Release --filter FullyQualifiedName~NavalReadAccess --logger "trx;LogFileName=access-native.trx" --results-directory $E` after rebuilding as required. Fix only concrete Windows fixture path/ACL incompatibilities; do not relax negative authorization.
- [ ] Review and commit explicit Task1 files: `fix: guard Windows private data before startup`. Preserve initial existing fixture failures separately from final results.

### Task 2: Produce a genuine static presentation in the existing builder

**Files:** Modify `Zbuduj-APEX.ps1`, `TestEvidence.ps1`, `Test-BacktesterGate.ps1`; test in `WindowsLocalSetupTests.cs`. Existing fixture/producer paths unchanged.

**Interfaces:**
- Produces `<publish>/presentation/naval-publication.json` and `<publish>/presentation/presentation.json` only for win-x64. Metadata schema `apex-presentation-v1`, `publicationSha256`, `inputSha256`, `coreSha256`, `backtesterSha256`, `scope` (exact `NavalReadScopeOptions` shape, `Id="presentation"`, remaining fields from committed export).
- New shared helper `Test-ApexPresentation([string]$Directory)` in `TestEvidence.ps1` returns parsed validated metadata after exact file-set/hash/schema/synthetic-classification checks. Host validates again using the existing typed parser; metadata alone is not authority.

- [ ] Add controlled tests `presentation-valid-real-export`, `presentation-corrupt-rehashed-export`, `presentation-foreign-scope`, `presentation-live-label`, `presentation-unlisted-file`. Use genuine CLI output as positive input; no invented financial fixture envelope.
- [ ] Run `powershell -NoProfile -File tests/package/Test-BacktesterGate.ps1 -Source . -Scratch "$E\presentation-red"`; expected refusal/failing assertions at missing presentation contracts, while old tests remain in place.
- [ ] Extend builder **after solution/tests and before sealing** to run `dotnet tools/Apex.Backtester/bin/Release/net8.0/Apex.Backtester.dll naval-shared-replay --input tests/Apex.Backtester.Tests/fixtures/synthetic/naval-shared-replay-v1.json --state-dir "$scratch\presentation-authority" --output "$scratch\presentation-result.json" --publication "$scratch\presentation\naval-publication.json"`. Use private scratch and create output parent with native private ACL. Derive scope/hashes from actual output and actual binaries, copy only the two public files into publish; never ship input authority/result/exporter.
- [ ] Run controlled tests with new green evidence path and typed Source test `PresentationIsGenuineHistoricalExport`; assert immutable checkpoint/scope agreement, stale/UNKNOWN/NOT_CONFIGURED semantics and no receipt/execution credentials. Do not run research campaign or modify producer/Core.
- [ ] Review and commit explicit Task2 paths: `feat: package historical Naval presentation`.

### Task 3: Provision new Windows roots without weakening existing auth

**Files:** New `WindowsLocalSetup.cs`, `WindowsLocalAccess.cs`, `WindowsLocalAccessPrompt.cs`, `WindowsLocalSetupTests.cs`; modify `Program.cs`/`HostConfig.cs` as needed. Existing `NavalAccessPolicy.Authenticate/Enter` role/auth rules remain unchanged.

**Interfaces:**
- `enum LocalSetupState { Empty, Existing, Pending, Ready }`; `WindowsLocalSetup.Classify(string dataDirectory): LocalSetupState` (ignore only the owned `apex.lock`, not arbitrary leftovers).
- `WindowsLocalSetup.Ensure(string dataDirectory, string presentationDirectory): LocalSetupResult` with `ConfigStore Store`, `bool Initialized`, `bool NeedsCredentialPrompt`. Ensure owns classification/store construction: no ConfigStore on Empty before pending marker, none on Pending, and missing config on Ready refuses. Preserve public `ConfigStore(string dataDir)`; add internal `ConfigStore(string dataDir, HostConfig initialConfig)` so setup writes the first config only after access/DPAPI/publication and with final Naval settings. Existing branch uses the unchanged public constructor after validation.
- `WindowsLocalAccess.Create(string dataDirectory, string scopeId): void` generates both secrets/digest map and protected CurrentUser DPAPI blob; `WindowsLocalAccess.Show(string dataDirectory): bool` loads privately and delegates `WindowsLocalAccessPrompt.Show(string login,string secret): bool`.
- Private file/marker/config and exact public metadata names are those in the spec. `--show-local-access` is a local-only display command, no host/browser/listener/rotation.

- [ ] Write Source tests `EmptyRootGetsProtectedPresentationProfile`, `ExistingConfigAndCustomProfileAreBytePreserved`, `PendingSetupAndMissingMapNeverDowngrade`, `CrashAtEachSetupWriteNeverCreatesLegacyBootstrap`, `DpapiCorruptionDoesNotRegenerate`, `PlaintextNeverReachesOutputOrDocuments`, `SetupCancelledKeepsReadyPrivateStateWithoutListener`, `LocalAccessDisplayDoesNotStartHost`. For every pending/access/DPAPI/publication/config/ready write interruption, restart must either load complete authenticated Ready or refuse unchanged, never create legacy HTML/token bootstrap. RNG secrets must each decode to32 bytes and be distinct; map stores only lowercase SHA256, generated config has no production accounts and preserves risk0.5/1.5/5/x5.
- [ ] Run `dotnet test tests/Apex.Windows.Tests/Apex.Windows.Tests.csproj -c Release --filter FullyQualifiedName~WindowsLocalSetupTests --logger "trx;LogFileName=setup-red.trx" --results-directory $E`; retain failures at old first-run behavior.
- [ ] Implement pending→private files→config→ready ordering and native modal. No plaintext stdout/file/JS/URL/clipboard; no silent credential reset. DPAPI user-scope failure stops safely. Existing nonempty/legacy trees are not overwritten or enrolled. No new production simulation/composition authority.
- [ ] Add `RenderIndex` exact-query handling: authenticated Operator `/?view=naval` redirects302 to literal `/naval/`; `/` still original panel. Add auth process tests for anonymous401, reader403, no-query old panel200, malformed/foreign query not an open redirect, enabled missing credentials exit2. Preserve all existing API/document/asset denials and raw-panel empty meta assertions.
- [ ] Run Source setup tests green plus affected existing Naval access/frontend host tests. Privately prove CurrentUser DPAPI decrypt works; foreign-scope/corrupt blobs fail. Do not create another OS user merely for a cross-user probe: production CryptUnprotectData context and native API contract plus corrupt/foreign blob controls are scoped evidence, not a second-user penetration test.
- [ ] Review and commit explicit Task3 files: `feat: initialize private Windows Naval access`.

### Task 4: Bind before default-browser launch and close lifecycle gaps

**Files:** New `DesktopBrowser.cs`, `WindowsStartupTests.cs`; modify `Program.cs:30-74,233-268`. Keep bridge financial behavior unchanged.

**Interfaces:**
- `DesktopBrowser.BuildUri(int port, bool navalEnabled): Uri` produces `http://127.0.0.1:<port>/?view=naval` or `/`; rejects out-of-range port.
- `DesktopBrowser.TryOpen(Uri uri): bool` uses only default-shell `ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute=true }`; internal source-test launcher injection is not client/config/environment authority.
- Existing `--data`, `APEX_DATA_DIR`, `--port`, `--no-browser`, `OpenBrowserOnStart` preserved. Same-data lock code `HOST_ALREADY_RUNNING`; HTTP bind refusal `HOST_HTTP_BIND_FAILED`, exit2.

- [ ] Write Source tests `DefaultShellNotEdgeAndUrlHasNoSecrets`, `BrowserRequestedOnlyAfterHttpStart`, `SuppressionPreservesNoBrowser`, `SecondLaunchAndOccupiedPortDoNotNavigate`, `BindFailureReleasesLockAndAccounts`, `InvalidPortNoListener`, `TcpCollisionIsDegradedNotFatal`, `RestartNeverAutoStarts`. Assert no wildcard listener/config credential change, no URL userinfo/credential query/fragment and no browser calls on failure.
- [ ] Run `dotnet test tests/Apex.Windows.Tests/Apex.Windows.Tests.csproj -c Release --filter FullyQualifiedName~WindowsStartupTests --logger "trx;LogFileName=startup-red.trx" --results-directory $E`; old prebind/Edge behavior must be the causal failure.
- [ ] Implement structured using/try/finally lifetime and `StartAsync` then bridge/news/browser then `WaitForShutdownAsync`; register cleanup before start. Handle partial construction/start failures without zombie ports or locks. No HTTP fallback-port guessing. Show nonsecret Polish manual URL if shell open fails; no raw exception. Keep UI/process termination versus financial STOP distinct.
- [ ] Run tests green. Run existing affected real production-process restart/STOP tests only after their fixtures/build are valid; record scope, do not count targeted output as a fresh full suite.
- [ ] Review and commit explicit Task4 paths: `fix: open default browser after local host readiness`.

### Task 5: Seal the exact self-contained Windows payload and honest metadata

**Files:** Existing builder/verifier/TestEvidence/controlled fixtures/`Smoke-Windows.ps1`, `tests/panel/package.test.cjs`; new `tools/windows/PRZECZYTAJ-WINDOWS.md`; docs Windows/read-publication and README.

**Interfaces:**
- Existing builder params unchanged: `-Wyjscie <new path> -Target win-x64 [-Offline]`; output ZIP is `<Wyjscie>.zip`.
- Publish stays `dotnet publish src/Apex.Desktop/Apex.Desktop.csproj -c Release --no-restore -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:PublishReadyToRun=false -p:IsTransformWebConfigDisabled=true -o <desktop>`.
- `Test-ApexWindowsPublish([string]$Directory,[string]$EvidencePath)` reads authentic runtimeconfig/pack provenance produced during publish and validates the existing tightly bounded EXE layout; verifier calls it for win-x64.
- `apex-build-v2`: Windows `navalReadDefaultEnabled=true`, `firstRunProfile="windows-local-presentation-v1"`, `existingConfigPolicy="preserve"`; Linux defaultfalse with no new initialization behavior. `windowsRuntimeTested=false`, `brokerTested=false` remain sealed. Source counts include real native Source-trait `Weryfikacja/windows.trx` only for win-x64.
- New manifested allowed members: root `PRZECZYTAJ.md`, two presentation files, `Weryfikacja/windows-publish.json`, Windows source TRX. No arbitrary extra runtime DLL/EXE directory.

- [ ] Extend genuine shipped-verifier positive/control negatives: altered runtimeconfig to shared frameworks, missing EXE/presentation, rehashed arbitrary DLL/replay EXE/access map/session/cache, exact case-alias/unlisted asset, secret sentinel in allowed innocuous file, false target/default profile and missing/zero/failure/skip native TRX. Reuse existing TAP regression mutants unchanged; never weaken parser or inventory to admit new files.
- [ ] Run `powershell -NoProfile -File tests/package/Test-BacktesterGate.ps1 -Source . -Scratch "$E\windows-layout-red"` and `node --test tests/panel/package.test.cjs`. Missing new layout/runtime proofs must fail deliberately; old positive fixtures must be updated only to real new shape, never fabricated product evidence.
- [ ] Extend builder/source restore order to explicitly restore/build new Windows test project, run `--filter Gate=Source` and parse `windows.trx` with existing `Read-ApexTestCounts`. Keep frontend-first gates/SDK consumers/notices/root-panel parity. Copy Polish root instructions; update exact verifier allowlist, runtime evidence and target-specific flags. Retain byte-for-byte parity assertions for all four original panel resources unless a narrowly reviewed source edit truly becomes necessary; authenticated redirect is in Program, not a UI rewrite.
- [ ] Update `Smoke-Windows.ps1` legacy-config smoke to create private files natively and test actual EXE with runtime roots empty, dotnet removed only from **child** PATH. Read loaded coreclr/hostpolicy and any VC++ runtime DLL locations/extraction ACL; record machine .NET/VC++ redistributable not used (ordinary OS `ucrtbase.dll` is allowed). Every required non-OS runtime module must originate from the publish/bundle, not a runner installation. No `--no-browser` smoke alone establishes autolaunch. Make the script produce sanitized result JSON outside package via optional `-Evidence <new external path>`; keep `-Pakiet` mandatory.
- [ ] Run controlled/helper tests green plus `powershell -NoProfile -File tests/package/Test-NavalNotices.ps1 -Source . -Scratch "$E\notice-fixtures"`, root `npm test`/`npm run check` as changed packaging inputs require. New PS source stays ASCII/5.1-compatible. No local Linux cross-publish counts as Windows proof.
- [ ] Review and commit explicit Task5 files: `build: seal Windows Naval self-contained package`. Instructions must explain extraction-all, EXE/native Basic/modal retrieval, data/ACL/error recovery, optional legacy existing mode, defaultbrowser, STOP/tab distinction, backup/update, unsigned files and synthetic/broker limitations. No recommendation to weaken ACLs, expose ports or bypass safety/SmartScreen blindly.

### Task 6: Exercise extracted EXE, first-run native UI and real browser

**Files:** New Windows test helper/process/UI/CDP/browser files; extend `tests/package/Test-NavalPackage.ps1`, existing `NavalReadAccessTests.cs`/`NavalReadEndpointTests.cs` test seams only as required by native execution.

**Interfaces:**
- `WindowsTestHost` constructor accepts explicit absolute package root and private data root, rejects invalid roots with **no repository fallback**. `StartAsync(string[] arguments)`, `StopAsync()`, `DisposeAsync()`, `Pid`, `Origin`; owns only its child/config/peer. Record payload SHA and loaded modules; credentials retained only in private test memory.
- `WindowsCdpBrowser.StartAsync(string privateProfileDirectory): Task<WindowsCdpBrowser>` discovers installed Edge in ProgramFiles/ProgramFiles(x86), launches isolated profile with `--remote-debugging-port=0`, reads private DevToolsActivePort, uses ClientWebSocket. `CommandAsync(string method, object parameters, string? sessionId=null)`; `DisposeAsync()` closes only owned contexts/browser/process/profile. No URL/argv credentials, external SDK or scratch imports.
- `Test-NavalPackage.ps1` existing arguments unchanged. Use the extracted **shipped** `Sprawdz-Pakiet.ps1` before/after tests (source verifier may be an additional parity check). For Windows add separate new-project `Gate=Package` (`windows-package.trx`) and `Gate=Browser` (`windows-browser.trx`) runs. External `summary.json` references those exact TRX files, existing `packaged.trx`, `archive-readback.json`, `package-before.sha256`, `package-after.sha256`, and sanitized `windows-browser.json`/`windows-process.json` assertion records. `APEX_PACKAGE_ROOT` must equal the independently extracted payload for every host.

- [ ] Write packaged tests `ExtractedExeHasNoSharedRuntimeDependency`, `FirstLaunchWithoutArgumentsCreatesPrivateDefaultData`, `ExistingConfigRestartSecondLaunchAndPortCollision`, `OnlyIpv4LoopbackListeners`, `ConfiguredSourceFailurePreservesEmergency`, `PackageBytesUnchanged`. Capture all new product/test/asset input hashes before each RED. Before missing implementation is fixed, initial first-run tests must fail on old behavior, not pass merely because old prepared configs work.
- [ ] Actual no-argument EXE test uses the hosted runner's real KnownFolder `%LOCALAPPDATA%\APEX Trading AI`, requires it absent and no existing host/port at entry, acknowledges native first-run dialog with Win32 messages in private memory, observes setup and successful default-shell request **after** ready HTTP, then closes owned children/default-data directory. Never redirect KnownFolder by assumed environment behavior or overwrite real user data. Record OS shell handler and request outcome; distinguish that from separately driven Edge rendering. If noninteractive CI prevents native UI/shell proof, fail/report that gate; do not substitute `--no-browser` or fabricated PASS.
- [ ] Privately verify credentials from generated CurrentUser DPAPI; ensure no secret/digest/Authorization bytes in stdout/stderr/public JSON/DOM/published evidence. Test native dialog again through `--show-local-access`; no listeners or rotation. Never take credential-window screenshots/HAR/raw CDP traces.
- [ ] Browser test uses actual production entry/private digest loader. Supply Operator once through CDP `Fetch.authRequired` native callback at root; remove callback and `Fetch.disable` **before** reload/five-view navigation. Prove native cached Basic, authenticated fixed redirect, old panel empty token, no page-injected auth. Separate reader context Bearer: exactly two GETs200, documents/modules/CSS403, clearing auth401; no cache inheritance.
- [ ] Compare served checkpoint to actual shipped producer export; navigate `#/command`, `#/radar`, `#/fleets`, `#/animations`, `#/analytics`, each non-UNKNOWN historical state. Verify exact nested-manifest assets and actual lazy Script request, real WebGL2 context when available, minimal/reduced/no-WebGL SVG/text fallback and capability-restored same-lane Retry. Preserve Task7 behavior; any genuine new regression is narrowly fixed/rebuilt, not waived. Software renderer proof is not physical GPU proof.
- [ ] Use a clearly synthetic local WS peer only to exercise original-panel command/decision/CSV paths: H503 → static Emergency link → native AWARYJNY STOP confirmation → only own77 full close, foreign78 untouched; durable latch and same-data restart/new hostEpoch with unchanged publication. Fail on risk-increasing/open/test/modify command. No real endpoint/account connection. Keep zero-account clean-first-run test separate from peer-driven test.
- [ ] Native command after building/restoring new project: `$env:APEX_PACKAGE_ROOT='<independently extracted package>'; $env:DOTNET_HOST_PATH=(Get-Command dotnet).Source; dotnet test tests/Apex.Windows.Tests/Apex.Windows.Tests.csproj -c Release --no-build --no-restore --filter 'Gate=Package|Gate=Browser' --logger 'trx;LogFileName=windows-packaged.trx' --results-directory $E`. Expected nonzero actual test counts, all pass/zero skip. Existing `Test-NavalPackage.ps1` still runs old selected Naval classes against EXE as a separate result, not replaced by new tests.
- [ ] Rehash package/ZIP, invoke shipped verifier, enumerate owned process exits/listener cleanup/private fixture/profile deletion, sanitize evidence then write summaries. Change `Test-NavalPackage` canonical Inventory/root slicing to long-path normalization on **both** sides if native 8.3 representation demands it; preserve traversal/case checks. No package mutation to mark tests passed.
- [ ] Review and commit explicit Task6 paths: `test: verify extracted Windows Naval startup and browser`.

### Task 7: Run genuine windows-latest delivery and close handoff

**Files:** Modify `.github/workflows/windows-demo.yml`, corresponding `tests/panel/package.test.cjs`, then actual results in HANDOFF/status reports and durable delivery storage.

**Interfaces:**
- Keep workflow path/name and `workflow_dispatch`; set `runs-on: windows-latest`, PowerShell5.1 (`shell: powershell`), setup-dotnet8/setup-node26. No automatic feature push trigger necessary. Record `Get-CimInstance Win32_OperatingSystem` caption/version/architecture and all actual tool versions.
- Builder output `"$env:RUNNER_TEMP/APEX-NAVAL-COMMAND-Windows-x64"`; success artifact name `APEX-NAVAL-COMMAND-Windows-x64`, inner ZIP/sidecar exact final names. Evidence artifact separate and uploaded on failure/success only after sanitization. Archive AI Lab remains nonshipped/nonblocking and is not new production work.

- [ ] RED source wiring test requires `windows-latest`, exact ZIP/output name, native EXE/package/CDP gates and failure-evidence paths; existing test's old windows-2022 expectation must change, not be deleted. Run `node --test tests/panel/package.test.cjs`, observe old YAML failure, then minimal YAML edit and green.
- [ ] Increase CI timeout from25 to45 minutes for additional real native browser gates, without retries/skip switches. Native builder command: `powershell -NoProfile -File tools/windows/Zbuduj-APEX.ps1 -Target win-x64 -Wyjscie "$env:RUNNER_TEMP/APEX-NAVAL-COMMAND-Windows-x64"`. It first executes complete current frontend/root/product/SDK/native Source gates, publishes/seals/extracts and runs Task6. Then run `powershell -NoProfile -File tools/windows/Smoke-Windows.ps1 -Pakiet "$env:RUNNER_TEMP/APEX-NAVAL-COMMAND-Windows-x64-extracted" -Evidence "$env:RUNNER_TEMP/APEX-NAVAL-COMMAND-Windows-x64-smoke-evidence"`; smoke writes sanitized `summary.json` there. Require exit0 and verified summaries before success artifact upload. Always-upload sanitized `*.trx`, source maps, frontend logs, actual smoke/runtime/ACL/browser summaries, archive readback and before/after inventory, not private configs/DPAPI/maps or raw credential logs.
- [ ] Scoped independent review of new startup/private/auth/presentation/package/CI delta; keep completed financial/UI stages closed. Resolve Critical/Important issues via intended RED→GREEN and affected evidence before promotion. Record source identities; no old run covers changed final bytes.
- [ ] Commit reviewed scope, then verify local ref and push **only** `feature/apex-naval-command` to `origin` (`kubson1995xx-cloud/bottt`). Read `git ls-remote origin refs/heads/feature/apex-naval-command` and compare exact SHA before dispatch. No merge, main push, deploy or account operations.
- [ ] Dispatch `gh workflow run windows-demo.yml --repo kubson1995xx-cloud/bottt --ref feature/apex-naval-command`; discover exact run ID and read `gh run view <ID> --repo kubson1995xx-cloud/bottt --json headSha,status,conclusion,url,jobs`. Check source SHA, job/step outcomes, actual TRX/TAP counters and native/browser result records; YAML or job conclusion alone is insufficient. Keep failed runs, diagnose once, rebuild/retest final changed bytes. Never retry unrelated accepted suite as an audit substitute.
- [ ] Download `gh run download <ID> --repo kubson1995xx-cloud/bottt --name APEX-NAVAL-COMMAND-Windows-x64 --dir <fresh durable path>` and sanitized evidence. Read artifact ID via `gh api repos/kubson1995xx-cloud/bottt/actions/runs/<ID>/artifacts`. Actions transport ZIP is not the product ZIP; locate exact inner `APEX-NAVAL-COMMAND-Windows-x64.zip` and adjacent SHA sidecar.
- [ ] On a fresh external extraction, verify SHA/CRC/canonical paths/full manifest/shipped verifier/source map/target/runtime/exclusions with the existing archive gate (do not execute Windows EXE on Linux). Compare downloaded bytes to CI artifact hashes, record sourceSHA/runURL/artifactID/ZIPbytes/SHA and durable copy hash. Final durable suggested directory `/home/ubuntu/projects/apex-deliveries/naval-windows-<source-short-sha>/`; ZIP/instructions/evidence outside expiring scratch. Do not overwrite Linux delivery.
- [ ] Update top HANDOFF and `reports/naval/ETAP_8A_DELIVERY.md` with actual Windows result and remaining physical Windows11/broker gates. Polish recipient summary: what starts/works, actual CI/source/packaged/browser counts separately, no runtime install, presentation truth, active strategy/profile state, private data location, limits and untested items. Deliver archive link/path, SHA sidecar and PRZECZYTAJ. Never call physical Windows11, cTrader/OKX private reads, real DEMO trade, hardware GPU or profitability accepted from hosted CI.

## Completion checklist / self-review mapping

- [ ] Tasks1/3: protected-at-creation native data plus existing/partial/unsafe failure controls and existing-config preservation, with no anonymous new-profile bootstrap.
- [ ] Tasks2/3: real producer-bound static presentation, derived scope, zero runtime accounts, historical/UNKNOWN labels; no production exporter/simulator/authority.
- [ ] Task4: default browser, post-bind ordering, suppression, second launch, collision, shutdown/restart cleanup and loopback-only listeners.
- [ ] Tasks5/6: self-contained native EXE/dependency isolation, direct no-argument first run/native UI, exact extracted payload and shipped verifier, all five views/lazy3D/fallback/retry and old-panel auth/Emergency.
- [ ] Tasks5/7: Polish instructions, final exact ZIP, secret/private/cache/source-test exclusions, manifests, genuine Windows Actions run, remote/artifact readback and durable unchanged handoff.
- [ ] Preservation: unchanged all strategy/risk/features/SDK gates, no accepted Task5A/UI/research redo, no broker/live/demo financial operation or merge/deploy.

Plan self-review: file names/signatures and traits above are consistent; every spec acceptance row has an owning task. Five review-focus input classes are covered by named tests, not deferred to a generic audit. Native environment inability remains a failed/unmet gate, never a substitute fake result. This planning task finishes after writing and checking these documents; **no product code, commit, push or CI execution now**.
