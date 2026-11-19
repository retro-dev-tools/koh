# CI simplification: PR #60

Goal: remove automation complexity while preserving functional acceptance.

Design: a justfile calls Cargo, MSBuild and npm. Python script recipes use the
existing interpreter for portable paths, environment and archives. GitHub Actions
owns triggers, OS matrices, setup, permissions, artifact transport and draft
creation. No new helper modules, release framework or reusable workflows.

Alternatives: Make assumes a Unix environment; xtask adds a Rust utility to
maintain; MSBuild alone is awkward for native/editor/platform acceptance.
Just adds one tool and keeps the entire common task interface in one file.
No PowerShell installation or Git Bash is needed by local tasks.

Implementation:
- [x] Audit all workflows, root/native/SDK MSBuild, Cargo/npm configuration,
  developer scripts, package/installers, validation helpers and documentation.
- [x] Remove the overlapping LSP workflow; cover every PR on all three OSes.
- [x] Consolidate full acceptance, publishing and packaging in just recipes.
- [x] Delete release preflight/tests, promotion wrappers and protection payload.
- [x] Remove redundant workspace check and duplicate package smoke runs:
  nextest builds/tests the workspace; its SDK tests now use release artifacts.
- [x] Keep native notice validators: they check attribution inventories against
  actual dependency graphs, not just file existence.
- [x] Keep GUI verification/oracle scripts and their tests: they prove behavior
  beyond a successful build. Keep ROM/reference/benchmark utilities.
- [x] Keep MSBuild SDK/publish targets and direct ecosystem commands.
- [x] Use standard Go actionlint installation; remove manual download/hash logic.
- [x] Generate one checksum manifest per platform, concatenate and verify in
  the draft job. No second hash pass over checksum manifests.
- [x] Keep pinned actions, read-only build tokens, draft-only releases, audits,
  MSRV and the existing nextest deadlines/zero retries.
- [x] Shorten contributor/release documentation and repair developer installers'
  obsolete managed-LSP invocation.
- Run local acceptance, package smoke tests and actual hosted workflows;
  completed results and quantitative comparisons are recorded on PR #60.
- Independent review found stale validation/extension output directories;
  both are now cleared. Hosted Windows caught global argument forwarding;
  forwarding is now scoped to parameterized script recipes.

PR #60 additions assessed: MSRV, formatting, production audit, action pinning,
draft permissions and manual package runs solve real problems and remain.
Custom preflight tests and protection payload are removed; version guards live
at package boundaries. Manual archive installers and cache fingerprints are
removed. Native package setup is shared locally. Dependency security repairs,
temporary editor profiles, installer cleanup and Windows fixture repairs remain.

Initial verification: actionlint exit 0; just parsing/listing exit 0; local lint exit 0
(CSharpier 310 files; Clippy finished). Bad/injected toolchain and mismatched
extension versions exit 1 before builds, including with PYTHONOPTIMIZE=1.
Fourteen boundary/fresh-output assertions pass. Local VSIX packaging exits 0;
the obsolete seeded VSIX is removed, and production audit finds 0 vulnerabilities.
These checks alone are not hosted acceptance.

Hosted acceptance, downloaded artifact integrity and final limitations:
https://github.com/retro-dev-tools/koh/pull/60
