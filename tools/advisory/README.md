# Native Windows advisory prerequisite (Task 1 only)

This is a source/CI prerequisite, **not** an updated APEX EXE, installer, active
entry filter, accepted user-PC login, or real-inference proof. Trading/UI and
WindowsLocal v2 are untouched. APEX remains the only executor.

## Source and dependency boundary

`upstream-pins.json` records genuine pinned Vibe-Trading, TradingAgents and Hermes
Git revisions, trees and reviewed manifest/entrypoint SHA256 values. Source is
fetched from those public repositories with CRLF conversion disabled. No installed
Linux upstream, campaign ledger, broker/vault, Linux Hermes config or tokens are
read into the native runtime.

The proof uses CPython **3.14.0 x64**. Vibe's native backend/AgentLoop import slice
and TradingAgents' manifest dependencies have separate environments: pandas 2.x
and >=3.0.6 cannot be merged. Hermes has its own trusted, tool-free environment.
Only binary PyPI wheels are installed. Locks were generated from real native pip
resolution reports, not handwritten/fabricated. See `locks/provenance.json` and
the retained source-bound CI artifacts. Every fresh native gate downloads exact
hash-locked wheels and installs offline with `--no-index --require-hashes`, then
runs `pip check`. No upstream setup.py/build hooks are executed.

Hermes's full-core plain-pip resolution is contradictory: browser-harness 0.1.13
pins Pillow 12.3.0 while Hermes pins Pillow 13.0.0 (upstream uv uses overrides).
This **tool-free** prerequisite omits only browser-harness and preserves all other
core constraints, including the Pillow security pin. This is not a claim that the
complete Hermes browser/desktop installer is validated. The real conflict and
failed payload are retained; no dependency metadata is falsified.

`vendor/vibe_identity.py` is the existing reviewed isolated EUR compatibility
adapter, with original licenses, notice, source/delta/hash provenance. Installed
upstream files are never patched. Computation changes only public loader seams:
260 deterministic synthetic OHLCV rows for BTC-EUR, explicitly labelled synthetic,
feed the genuine Vibe indicator/VaR and TradingAgents verified snapshot functions.
AgentLoop/GraphSetup imports are separate from graph inference, which is pending.
No market, strategy-performance, consensus or profitable-trading claim follows.

## Execute native proof

On an authorized Windows CI runner, from this repository:

```powershell
./tools/windows/Test-WindowsAdvisoryPrerequisite.ps1 -Phase green
```

The script uses a unique runner evidence root and retains `source/`, `build/`,
`closure/`, `evidence/`: actual OS/interpreter, manifests, source and runtime maps,
wheel archives, locks, installed reports, Python base/venv payloads, import paths
and hashes, computations, native gateway bootstrap/no-auth result and exit logs.
`red` is a calibration phase and must fail; it is not a passing prerequisite.
The narrow owned publication helper transforms remote accepted workflow bytes
using a separate Git index; it never stages or commits main or invokes old APEX
full-delivery gates.

Offline tests (not native runtime acceptance):

```text
python -m unittest discover -s tools/advisory/tests -v
```

The child environment is a positive allowlist including Windows OS essentials,
not an OS sandbox. Same-user filesystem/network access is not claimed isolated.
The transport uses the supported canonical Hermes gateway client, exact
`gpt-6.1-sol` / `openai-codex`, safe mode, zero tools and one turn. It rejects
configured fallback/custom routes, unexpected session routes/tool authority,
failed/empty/tool streams and bounds bootstrap/inference/interrupt.

## Separate operator-only local sign-in/inference gate

After **local Windows** prerequisite setup creates a local contract, the developer
entry is:

```powershell
python tools/advisory/local_smoke.py --contract '<local proof root>/closure/runtime-contract.json' --home '<dedicated local Windows Hermes home>' --login
python tools/advisory/local_smoke.py --contract '<same local contract>' --home '<same local Windows Hermes home>' --infer --seconds 45
```

The first command invokes Hermes's supported `auth add openai-codex --browser`.
The second is one genuine tool-free public echo request through the canonical
transport, with exact session metadata before/after and observed usage only.
It refuses in CI and on Linux. Do not transfer CI/Linux tokens or paste tokens
into chat, arguments, environment or custom files. A CI contract's absolute paths
are evidence, not a relocatable user installation. Task 5 owns the operator setup
wizard/ACL delivery. The official references consulted are
<https://hermes-agent.nousresearch.com/docs/user-guide/windows-native> and
<https://hermes-agent.nousresearch.com/docs/integrations/providers>.

Missing local subscription auth is an expected typed diagnostic, **not** inference
PASS. Native bootstrap, supported login entry, real user sign-in, exact-route
inference, genuine upstream graph outputs and later APEX consumption/admission
are separate gates. This task stops before trading/UI integration.
