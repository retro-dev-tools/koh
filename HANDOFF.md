# CI/CD maintenance handoff

Current work: simplify PR #60 on `ci/maintainable-automation`, master `0814e2f`.
One justfile, three workflows, four scripts removed; PR #60 owns this work.
Plan/evidence: docs/superpowers/plans/2026-11-19-ci-maintenance.md.
Developer interface: docs/ci-validation.md. Final results are recorded on PR #60.
Root agent owns implementation, local tests, hosted runs and PR updates.
Read-only review identified stale outputs; cleanup and Windows argument forwarding
are fixed. No extra framework or helper modules were needed.
Verify using `just ci`, `just msrv`, `just package tools-v0.1.6-rc.1`,
`just extension ext-v0.1.5`; inspect actual completed logs and archive contents.
Local logs: /tmp/koh-simplify-{lint,test,actionlint}.log. Local SDK/tools under
/tmp/koh-simplify-{dotnet,tools}; set DOTNET_ROOT and a fresh NUGET_SCRATCH.
Never run concurrent builds in this checkout. Recheck active processes first.
Hosted results: https://github.com/retro-dev-tools/koh/pull/60 (test results in
the PR description; original increment extension44581083691 passed, downloaded
VSIX checksum verified). Verify the current head before reusing these results.
Push and workflow execution are authorized. No merge, protection changes,
release tags or public publication. Licensing remains a release blocker.
Earlier handoffs below are historical and require fresh verification.

# Rust LSP review handoff

Current work: native LSP migration in feat/rust-lsp. Fresh whole-branch review
complete; local checks pass. Implementation committed0a0978e; hosted platform
checks remain. User authorized push and draft PR59; independent LSP CI pending.
State, boundaries and verification: docs/rust-lsp/handoff.md.
The engine handoff below describes separate prior work.

# Rust engine migration handoff

Updated 2026-11-19. Goal remains active: emulator engine and debugger rewritten
in Rust. Progress: `[#########-] 9/10`; hosted platform acceptance remains open.

## Authoritative state

- Worktree `/home/minorum/projekty/koh-rust-engine`, branch `feat/rust-engine`.
- PR57: https://github.com/retro-dev-tools/koh/pull/57, base master `622770f`.
  Opened draft, subsequently marked ready externally. Do not redraft, merge or
  release automatically.
- Runtime source `78e84e5`: fixes review thread `PRRT_kwDORxbios65eYi5`, now
  resolved. LCD restart can leave an inactive sprite fetch outside the new
  sprite list; enforce the relationship only during Drawing. Absolute bounds
  remain. Tick-driven DMG/CGB regression RED before fix, GREEN afterward.
- Current full manual run: https://github.com/retro-dev-tools/koh/actions/runs/44521023888
  on `78e84e5`. Linux job135549501038, Windows135549501367, macOS135549501514.
  Poll these live handles; do not claim success before terminal logs.
- Earlier run44520198054 on ec5605f is superseded, not current acceptance.

## Scope and preserved state

All verification tooling is excluded pending separate design. Existing
Koh.Verify, 2048 verifier and rust-asm-verify remain unchanged. C# emulator/boot
remain for unchanged dev tools, superoptimizer and benchmarks. No managed
production fallback. LSP and C# game authoring remain supported.

Main checkout has unrelated `samples/csharp/gb-jrpg/screenshots/battle.png`:
SHA256 `fb818583b6de0e55a9e61ff2768157a702621f4bb21bf91fa1d21b19376c1454`.
Preserve it. No concurrent builds in a shared target directory.

## Completed evidence

- Full rebased compiler: 2063/2063, zero skipped, exit0;
  `/tmp/koh-engine-rebased-compiler.log`, terminal summary2028.561s. Includes
  native SDK source-to-ROM/headless launch. Later networking/PPU changes did
  not modify compiler/helper sources; hosted workspace reruns current source.
- Core after review fix:92/92, zero ignored, exit0;
  `/tmp/koh-review-ppu-green.log`. RED `/tmp/koh-review-ppu-red.log` failed with
  `invalid PPU state`; strict Clippy `/tmp/koh-review-ppu-clippy.log` exit0.
- Refreshed engine27/27, zero ignored, explicit reference/audio opt-ins:
  `/tmp/koh-engine-review-ppu-host.log`, exit0.
- Refreshed native root package `/tmp/koh-engine-review-ppu-publish.log` exit0.
  GUI SHA256 `efd37c61864ef83c8367db6dfa1a87b7150481c32d3ed52c5f614e59755575c9`;
  engine `933a3a06719b9780a2783b59d29d0686639d201012976bcb91555084da7595d4`;
  native OpenAL `7162b88c6c1fea862746f688ee0a9267a72033f5ea5b82af74d304ec5061a1dc`.
- Refreshed real packaged window passes exact LCD, integer scaling, Start,
  pause, save, clean child exit and inactive shortcuts:
  `/tmp/koh-engine-review-ppu-window.log` exit0, captures in sibling directory.
- Refreshed actual VSCode/default GPUI/native DAP45/45, zero pending:
  `/tmp/koh-engine-review-ppu-vscode.log`, VSCODE_EXIT=0. Real factory/source
  debug/disconnect passes; package hashes unchanged after test. Temporary
  node_modules symlink and `.vscode-test.review.mjs` removed after run.
- Remaining managed suite504/504 and zero-warning build:
  `/tmp/koh-engine-managed-{build,tests}.log`; hosted current matrix reruns it.
- Disposition CSV224 rows:181 retained-dev,42 mapped,1 obsolete,0 partial.
- Independent readonly review covered host/debugger/persistence/launch/CI;
  inherited temporary-breakpoint overwrite/delete is deferred, not a new bug.

## CI and next steps

One Linux PR check; full Linux/Windows/macOS on master/manual. No .NET AOT job,
classifier or aggregate wrapper. PR and full concurrency groups are separate.
User-approved CI cadence is implemented. Master protection is unchanged with
seven legacy required names. `.github/required-checks.json` prepares strict
`check (ubuntu-latest)` with app15368; docs/ci-validation.md describes the
coordinated merge-time transition. Do not apply it before authorized merge.

1. Inspect current hosted run terminal job logs and downloaded artifacts.
   Validate actual package/audio/window/VSCode outcomes on all three platforms.
2. Fix only evidenced migration failures, preserve assertions, show RED/GREEN.
   Earlier platform errors were DAP transport defects, not audio failures:
   Unix accepted sockets inherited nonblocking; Windows blocking reader held
   synchronous duplex writes. Repairs in0dac821 await hosted confirmation.
3. Finish ten compatibility gates in docs/rust-engine/design.md, update final
   evidence/review/plan/ROADMAP and PR. Docs-only commits must be distinguished
   from the exact tested runtime source. Do not rerun suites without reason.
4. Mark goal complete only after all required acceptance is proven. No release.

## Environment

Use .NET `/home/minorum/.local/share/koh-dotnet-10.0.115`,
`NUGET_SCRATCH=/tmp/koh-nuget-scratch`, DOTNET_PROCESSOR_COUNT=4 (2 constrained).
Local Rust1.99 binaries `/usr/lib/rust/1.99.0/bin/{cargo,rustc,cargo-fmt,cargo-clippy}`;
CI stable1.100. Explicit CARGO/RUSTC/RUSTFMT avoids missing-nightly shims.
Targets `/tmp/koh-engine-target`, `/tmp/koh-engine-paused-target` and worktree
`target/native-msbuild/Release/linux-x64` are free after recorded local runs.
Reference `/tmp/koh-engine-reference-e1336b0/Koh.Emulator.App`; tests need
`KOH_REFERENCE_ENGINE` plus `ALSOFT_DRIVERS=null` and reference LD_LIBRARY_PATH.
