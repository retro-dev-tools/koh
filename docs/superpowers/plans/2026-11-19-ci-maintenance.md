# CI/CD maintenance

Goal: dependable contributor feedback and deliberate, testable releases using
the existing GitHub Actions and root build targets.

Design: retain Linux PR checks, full master/manual platform validation and
focused cross-platform LSP checks. Harden the existing workflows rather than
introducing a release framework or reusable-workflow hierarchy. Keep draft
releases and manual publication. Add package-only manual runs, fail early on
invalid tags and mismatched extension versions, and restrict write tokens to
draft creation. Licensing remains a publication blocker requiring owner review.

Scope: four workflows, action-update automation, release preflight/tests,
portable Unix installer checksums,
and the existing CI guide. No runtime redesign, registry publication, paid
services, branch-protection mutation, or actual release publication.

Implementation and verification:

- [x] Pin actions, disable persisted checkout credentials, bound release jobs,
  and add weekly grouped Actions updates.
- [x] Test and implement release preflight; add manual package-only runs,
  extension checksums, restricted artifact downloads and job permissions.
- [x] Add workflow linting and release-helper tests to Linux CI; check managed
  formatting baseline before making it mandatory.
- [x] Document local commands, release steps, required settings and diagnostics.
- [x] Run actionlint, release tests, local builds/tests and package smoke checks;
  inspect hosted baseline results and review the final diff.

Review focus: fork credentials, tag injection, dry-run publication conditions,
artifact selection, existing license gaps and obsolete required-check contexts.
Do not claim hosted acceptance of unpushed changes.

## Validation and review, 2026-11-19

All results below were re-read from completed logs, with actual exit 0:

- actionlint 1.7.12 with ShellCheck 0.11.0: all four workflows; initial lint
  findings in inherited release scripts corrected before enabling the gate.
- Rust stable 1.100 formatting and CSharpier 1.3.0: 310 managed files checked.
  Four whitespace-only baseline repairs avoid a failing new formatting gate.
- `dotnet build Koh.Ci.slnf -c Release -m:1`: build succeeded, zero warnings
  and errors; all seven C# sample ROMs emitted. The matching no-build test run
  reports 297 succeeded, zero failed/skipped.
- Rust 1.88 production compatibility: all eight core libraries/binaries and
  features check successfully. All-targets initially exposed newer test-only
  `File::lock`; stable remains the test compiler, without changing that test.
- Native LSP suite: 61 passed, zero failures/ignored. Root MSBuild language-server
  publication succeeded; four stdio cases pass against its release binary.
- Official Node 22.23.5/npm 10.9.9: clean install, production audit reports
  zero vulnerabilities, VSIX packages successfully, and actual VS Code tests
  report 47 passing, one pending (desktop fixture not supplied). The real
  packaged LSP integration runs. The profile fix was preceded by four failures
  caused by a persistent local toolchain setting in the old test profile.
- Three release preflight tests pass: SemVer/injection rejection and mismatched
  package/lock versions. Workflow permission/manual-event assertions pass;
  checksum steps ran against a real VSIX, verified it, and rejected corruption.
- Eight actual release-policy cases verify stable/prerelease classification,
  including a hyphen only in SemVer build metadata. Installer checksum branches
  use stock macOS shasum or Linux sha256sum; fixture execution verifies both,
  paths with spaces and missing-input rejection. ShellCheck passes the template.

Self-review retained the existing build targets/platform cadence and targeted
LSP workflow, removed redundant version parsing, and explicitly installed Python
in release builds. No registry, signing framework or dependency abstraction was
added. Production tar/brace-expansion advisories were fixed within existing
ranges; 20 development-only npm advisories still need separate triage.
It also corrected SemVer build-metadata classification and the macOS installer's
dependency on GNU sha256sum, without changing the installation layout.

Hosted baseline run 44567744164 at unchanged master 0814e2f was still executing
native workspace tests on all three platforms at final inspection. It is not
acceptance of this branch. New workflow dispatch/upload, macOS/Windows installer
execution, complete toolchain archives and public release/update behavior have
not been exercised here. No release, tag, protection change or push was made.

Local environment gotcha: stale shared NuGet scratch locks stalled tools/builds.
A fresh `NUGET_SCRATCH` directory allowed normal tool restore, audited restore,
build and tests without disabling auditing or changing package sources.
