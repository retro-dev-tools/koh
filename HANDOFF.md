# PR #55 handoff

Current unit: native MemoryFill, following profiling commit411f3ed. Root owns
edits and serial builds. Base for this unit is pushed2ebfc4c. Existing API shapes
are evidence, not conventions. No local test/build live at this update.

CGB first200 profile: Mem.Fill8733956T, FillSpan6646432T
(/tmp/koh-cube-profile-first.log). Byte-loop fill ROM1434496Debug/1276400Release;
word-loop attempt814812Debug/722476Release stillfailed350000T budget.
New metadata memfill void(byte*,byte,ushort) -> effectful IR -> native52T/byte,
no source loop/compiler fallback. Actual testROM91492T allalignments/modes/configs.
Four temporary stack bytes, low8/low16 formals, ascending16bit wrapping writes.
Desktop body is ordinary byte C#, safe unaligned. Legacy C# compiler rejects
new metadata; explicitly accepted because it must be deleted and SDK is Rust-only.

Focused fill20/20 passed68.415s /tmp/koh-fill-all-contracts.log; additional
wrap4/4 passed34.553s /tmp/koh-fill-wrap.log. Covers exactfullpoisonbanks,
zero/tails/255/256/257/300, four alignments, DFFF guard, fivebadmetadata shapes,
desktop112fills, sixIRcases and fourbackendlargeoffset/truncation/stack cases.
Executable wrap exactlyFFFE:A7,FFFF:A7,0000:A7,0001:A7; no extra0002.
Pin red /tmp/koh-fill-pinned-red.log acceptedSVBKFF6F+2. Fixed using known
constant/conversion graph and modulo16bit spancheck, including transitivecalls.
First literal-only fix failed because pointer was OpConvert; corrected20green.
Dynamic rawpointer capacity/bank safety remains caller-owned, same as raw
StoreMemory. Review memory_fill_review verified graphlinear, masks andstack;
no remainingCritical/Important issues, no reviewer builds/edits. Instance/generic/
calling-convention metadata mutations are not claimed as new coverage.

Broader /tmp/koh-fill-graphics-regression.log completed308passed/1failed,
466.164s,exit100;82 unrelateddrivercasesfiltered. All207IR,70backend,22selected
drivercasespass. Cube has9passes/1fail: sixvalidatorcases, threegamecasespass.
CGB Debug/Release and DMGRelease now pass both structuralcaptures. DMGDebug
first1100 remainsblank; late2000capture / VRAMsafe checksremainrequired.
Cube /tmp/koh-cube-native-fill.log9/10,59.485s,exit100. No assertion/budgetrelaxation.

Final post-hook fill24/24 passed125.887s,exit0:
/tmp/koh-fill-final-posthook.log. Includes new local-memory aliases and4 IR
conversion/sign/zero/pointer-truncation cases;361 unrelated casesfiltered.
ActualSDKgraphics dotnetbuild/run passed87.842s; post-hookproductionselection
30/31 passed273.776s,exit100 /tmp/koh-fill-postcommit-sdk.log, soleDMGDebugcube.
Nativeproductioncodeunchanged since thatSDKcheck; later additions are tests/docs.
Push logicalincrements and update draft PR55 checklist34/565complete,
531partial/outstanding (0deleted).
Then test/fix Ppu.WaitVBlank: currentcodepollsonlyLY==144 (one456-dotline);
profileDMGDebug49518400T inWait, firstcapturePCinWait. Test nextblank latency /
fullVBlankwindow, notlargerframebudgets. Source start/stopandGDMA timingmust
remain safe; existingemulator/core/debuggernotrewritten. Fullframe/racingbeam
need actualnative structuralacceptance afterdoublebuffer is fullygreen.

The full native Rust CIL-to-SM83 migration remains ACTIVE in one draft PR55
against master. Never merge, split PRs, shrink acceptance or declare completion
for an increment. Existing public APIs are evidence, not a baseline/convention.
Full objective: use get_goal if needed; the user request's acceptance is intact.

Worktree: /home/minorum/projekty/koh-rust-compiler, feat/rust-cil-compiler.
Main worktree has unrelated battle.png edits: preserve SHA256
fb818583b6de0e55a9e61ff2768157a702621f4bb21bf91fa1d21b19376c1454.
Base HEAD2ebfc4c63b4bc1a08a2866a49519d4906dfe071c was remote-verified.
Increments7f7b8a7 small-magnitude multiply and063c695 shared canvas/tile transfers.
Test commit2ebfc4c exposes stronger actual Cube tests
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

Current2ebfc4c runs43750920221 (Rust) and43750920286 (managed) verified live
after push. Poll these exact runs; no current-head cross-platform result yet.

PR https://github.com/retro-dev-tools/koh/pull/55 stays draft. Update its concise
live checklist and evidence after pushes. Rough45% is not measured coverage.
Test matrix34/565 complete,531partial/outstanding, zero physically retired.
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
