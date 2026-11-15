# PR #55 handoff

The full native Rust CIL-to-SM83 migration remains ACTIVE in one draft PR55
against master. Never merge, split PRs, shrink acceptance or declare completion
for an increment. Existing public APIs are evidence, not a baseline/convention.
Full objective: use get_goal if needed; the user request's acceptance is intact.

Worktree: /home/minorum/projekty/koh-rust-compiler, feat/rust-cil-compiler.
Main worktree has unrelated battle.png edits: preserve SHA256
fb818583b6de0e55a9e61ff2768157a702621f4bb21bf91fa1d21b19376c1454.
Last pushed b77361e; local increments7f7b8a7 small-magnitude multiply and063c695
shared canvas/tile transfers. This test commit exposes stronger actual Cube tests
and serial nextest configuration in the same PR. Root owns edits and builds.
Read-only multiply_transfer_review found no math/transfer defect; its concurrent
cargo-test build concern is fixed by a local Roslyn build lock. No reviewer
builds, edits or active subagents. No local build/test live at this update.

## Verification and open cube failure

- Division b77361e: backend50/50, post-hook focused9/9; I32 30000/31
  51268T→4532T, I64 now6212T. docs/rust-compiler/word-division.md.
- Multiply red -30*-127: I32 47080T, I64 185896T, both fail20000T budget.
  /tmp/koh-multiply-cost-red.log. Shared-helper green I32 3828T/I64 6260T;
  /tmp/koh-multiply-shared.log passed9/9 in27.012s.
- Initial inline multiply made actual Cube.Transform exceed16KiB, explicit
  diagnostic /tmp/koh-cube-fast-math.log. Per-function/width helper fixes it.
  Operands scratch0/8, result16, sign24; helper offsets+2 for CALL, entry
  temporary guard4 for return+scratchpush. Full modular widths preserved.
- Complete backend66/66 + real tile transfer4/4 passed70/70 in221.574s,
  exit0 /tmp/koh-multiply-transfer-regression.log;90 unrelated cases filtered.
  Covers dispatch255/256, MIN/high bits, wide fallback, large frames,
  forty chained products fitting a bank, stack exhaustion, prior arithmetic.
- Canvas now shares TileSet.CopyToVram. CPU value/destination and CGB registers
  prepared before WaitVBlank; complete tile sizes; LCD-on GDMA bound1024.
  Previous DMG red806Release/826Debug mode3 writes. Old weaker4 animation/VRAM
  probes now pass49.586s /tmp/koh-cube-shared-transfer.log.
- Stronger cubes.rs checks BOTH early/late captures: palette/alpha/dimensions,
  centered96x80 viewport, multiple face shades, size/center, one8-connected
  silhouette, animation, real boot/frame limits and zero mode3 writes.
  Six synthetic validator cases pass. Real4-case result1passed/3failed,
  51.034s, exit100 /tmp/koh-cube-shared-math.log:
  DMGRelease passes; CGBRelease/Debug and DMGDebug first images are blank.
  Limits unchanged CGB200/500, DMG1100/2000 after boot. DO NOT relax them.
- /tmp/koh-cube-first-owner.log traces CGB first200 PC51EC bank3 to
  Canvas.FillTriangle+85. Boot is complete, allocation492managed/1935raw
  succeeds, mode3zero. Further performance investigation is required.
  CubeHost reports first PC/bank as fields10/11 (12 total) for diagnostics.
  The sample source is linked unmodified by fixture Cube.csproj. Full-frame
  and racing-beam still need native structural acceptance.
- Exact post-hook /tmp/koh-cube-multiply-postcommit.log:21 cases,18passed,
  3failed,78.830s,exit100;55 unrelated backend cases filtered. All11 selected
  math/stack cases pass,6 validator cases pass,DMGRelease cube passes; the3
  remaining cube cases fail their first-capture geometry. No local process live.
- Next: profile remaining startup/render cost, fix early-capture timing.
  Do not claim the weaker inequality test establishes a rendered cube.
  No emulator/core/debugger rewrite; no new C# parser or compiler fallback.

## CI, PR and closure

PR https://github.com/retro-dev-tools/koh/pull/55 stays draft. Update its concise
live checklist and evidence after pushes. Rough45% is not measured coverage.
Test matrix32/565 complete,533partial/outstanding, zero physically retired.
Cube source group3methods/6cases/8assertion sites remains partial; isolated
Canvas7methods/36sites also need exact raster/plane/page tests. All compiler
tests must finish migration; none may be deferred to the emulator PR.

b77361e native run43748940025: Linux132918364479 actual logs806/806 passed,
769.867s, zero skipped; separate doctests (no cases), artifact graphics1/1
(two other SDK cases intentionally filtered). References job132918364266
success. macOS132918364416 and Windows132918364469 were live at last query.
Managed run43748939949 failed; inspect primary logs before describing causes.
Earlier4e7212d native all3hosts passed794Linux/macOS,792Windows. Two Unix
assembler cases explain difference. Do not transfer that proof to newer heads.
Newly committed cube acceptance will be red until early captures are fixed;
that is intentional exposure of unfinished required work, not waived coverage.

Still required: classes2048v2/JRPG, floats/IRQs/reentrancy/banked data and final
capacity contract, all sample behavior, PortablePDB→ELF→KDBG/source breakpoints/
stacks, complete test dispositions, delete C# compiler and compiler TUnit
project/packages, final artifact/AOT/release checks, comparable compile time/
memory/binary measurements and final Linux/Windows/macOS acceptance.
Koh.GameBoy remains C# authoring library; tile-sheet build task preserved.
ELF32/SM83 only; no KOBJ path, no hidden C# compiler route. Old source remains
pending verified replacement, not final acceptance.

## Commands and discipline

Use RUSTUP_TOOLCHAIN=stable /usr/bin/cargo, target-dir /tmp/koh-header-target,
-j1, KOH_DOTNET=/home/minorum/.local/share/koh-dotnet-10.0.115/dotnet.
Run builds serially. A write_stdin result with session_id is STILL LIVE.
Record actual terminal exit and re-read completed log. No pipeline status.
Default: cargo nextest run --workspace --all-features; separate cargo test --doc.
Cube target: --test cubes; all4 real cases plus6 checker cases are required.
Commit hook PATH/DOTNET_ROOT=/home/minorum/.local/share/koh-dotnet-10.0.115.
No destructive cleanup or unrelated staging. Preserve sample assets.
Vault handoff is brief project-level knowledge; code details stay in this file.
