# Integrated native credential component provenance

Imported without source edits from `/home/ubuntu/projects/apex-okx-credential-vault-work/Vault/`:
- `LinuxDemoCredentialVault.cs`
- `SystemdCredentialCryptor.cs`
- `VaultDirectory.cs`

Their upstream `VERIFICATION.json`, `REPORT.md`, `INTEGRATION.md` and retained original 60-case executable evidence remain in that independent workspace. This integration also compiles the original three harness sources (Program.cs, AdditionalTests.cs, Platform.cs) in `tests/Apex.Okx.VaultHarness`, referencing the **production runner assembly** rather than the original standalone Vault.dll. Internal access is the existing Apex.Tests friend identity; no CLI/config/environment path or provider override was added.

Final delivery evidence is `/home/ubuntu/apex-deliveries/okx-demo-fee0/persistent-runner/`. Source manifests record exact SHA256 and compare imported sources with upstream originals. Production CLI uses fixed roots, process hardening and the shared financial-run lock; component directory flock is additional serialization, not revocation of a loaded credential bundle.

The separate CLI harness references the real runner entry via its internal composition seam. It must fail if that seam is absent: never fall back to production Main in a synthetic test. Only help/identity/offline-input checks and safe missing-vault refusals may execute actual production Main without a friend root. The test scripts deny IPv4/IPv6 sockets via kernel seccomp; simulated HTTP uses the unchanged real signer/parser/discovery with loaded-owner material.
