# PR #55 handoff

## Goal and checkout

Finish the entire native Rust CIL compiler replacement in one draft PR55 against
master. Do not merge, narrow acceptance, or call it complete from a green subset.
C# remains the game language; keep the existing C# emulator. Existing APIs are
evidence, never a design baseline. Update consumers/tests/docs for justified
changes. Preserve useful asset tooling and the SDK build/run/debug experience.

Worktree: /home/minorum/projekty/koh-rust-compiler, feat/rust-cil-compiler.
Remote PR head:6eda360ab4e9b4207cdfeda5b071b350b8855010.
Local documentation commit4387e5c records rectangular-array requirements; not yet
pushed. Bounded-copy/gameplay increment is ready for its logical commit below.
No build/test process is currently live. Preserve unrelated main-checkout
samples/csharp/gb-jrpg/screenshots/battle.png, SHA256
fb818583b6de0e55a9e61ff2768157a702621f4bb21bf91fa1d21b19376c1454.

## Ready increment

Metadata-driven vblankcopy lowers to typed IR and native SM83 code. Nonempty
calls disable interrupts, require LCD-on (fault9 otherwise), and copy a bounded
ascending prefix; empty calls preserve IME and access no hardware/buffer.
Count truncates to16 bits, returned copied count zero-extends to32. The byte gate
accepts LY144..150, reserving instruction latency so stores end by LY151.
MapWriter.FlushRun is a private intrinsic stub; public graphics APIs and CGB GDMA
are unchanged. No compiler helper name is hard-coded.

Review found late LY151 sampling could write at LY152. The phase test reproduced
LY152/dot4, then the guard was fixed. Read-only re-review reports no remaining
implementation blocker. Large-frame coverage now includes live stackalloc200,
64-bit data, endpoint guards and an assertion on actual >127-address encoding.
Four final driver cases cover6160 phase/count/frame probes on Debug/Release and
DMG/CGB, checking exact reads/writes, untouched VRAM, partial counts, IME/SP/SVBK.

Four actual2048v2 SDK game cases pass: complete source set, canonical ELF/linker
ROM equality, real Koh boot ROMs, title, Start, eight moves, independent board/
score/spawn rules, exact tile bytes and board pixels, HUD and zero mode3 writes.
Eight Rust reference-rule cases also pass. Capture budgets and sample art were
preserved. Observer C# hosts only step/apply input/snapshot the existing emulator;
all game/CPU expectations are Rust-owned. Two source methods/three legacy
scenarios now have complete stronger replacements:50/565,515 outstanding,
zero physically retired. See docs/rust-compiler/test-disposition.md.

## Verification evidence

Full command: cargo nextest run --workspace --all-features --target-dir
/tmp/koh-header-target -j1 --locked --no-fail-fast, with the dotnet environment
below. Completed session66357, exit100:1086 run,1084 passed,2 failed,0 skipped,
1729.809s. Log /tmp/koh-vblank-workspace.log. DO NOT call that command green.
The two new LCD-off tests incorrectly assumed the emulator initially disabled
LCD; Ppu.cs initializes LCDC=0x91. Their IR now explicitly disables LCD, with the
original fault9 assertions unchanged. Corrected2/2 pass10.475s, exit0:
/tmp/koh-vblank-lcd-off-corrected.log, terminal session61358.

All production/other source hashes stayed unchanged; only tests/sm83.rs changed
for that setup fix.19 final tested source hashes are recorded at
/tmp/koh-vblank-tested-hashes.json. Compare before/after the commit hook.
Workspace all-features/all-targets check succeeds11.16s, existing cube-helper
warning only: /tmp/koh-vblank-check.log, terminal17181. Separate workspace
rustdoc succeeds (four crates, zero cases): /tmp/koh-vblank-doc.log.
Final SDK cases:56.818/56.162/19.374/19.293s. Final phase cases:
17.213/17.377/17.436/17.791s. Prior selected26/26 pass221.337s predates final
large-offset/zero-LCD additions; use completed full/focused evidence above.

Pushed6eda360 native CI43799726969 verified from actual job logs:
Linux1063pass1054.414s (133076706764), macOS1063pass1216.587s (133076706709),
Windows1061pass1385.779s (133076706675), zero skips; separate rustdoc passes.
PR55 body records those results, remains draft/unmerged/head6eda360. They
predate current dirty VBlank code. Managed CI43799726853 still fails JRPG at
rank2 byte[,] World::IsWalkable IL0013; actual Ubuntu job133076706253 confirms.

## Immediate next actions

1. Review staged scope, commit bounded-copy/gameplay unit, verify19 hashes after
   CSharpier, push both logical commits to the same PR55 branch, update the short
   live checklist with50/565 and honest full/focused results. Do not merge.
2. Implement rectangular arrays from docs/rust-compiler/rectangular-arrays.md;
   do not flatten JRPG just to evade the missing feature. Port both MdArray
   source methods with independent expectations, including mutable readonly
   field referents; legacy ROM-fold/header bytes are not contracts.
3. Rebuild actual JRPG to expose the next dependency, then finish its gameplay,
   interface/delegate execution and separate deferred scene-transition checks.

Remaining whole-migration gates include the justified full language/runtime
contract (including required arithmetic widths/software floats/generic shapes),
interrupts/reentrancy/banked data/capacity, matching Portable PDB to ELF/KDBG,
actual source breakpoints and native stack inspection, every feasible compiler
behavioral-test migration, C# compiler/test/dependency deletion, release archives,
comparable performance measurements and final Linux/Windows/macOS validation.
Do not equate KDBG-file existence with source/stack debugging. The existing DAP
stack walker assumes legacy topology; compiler frames/bank cookies need proof.

## Commands and discipline

KOH_DOTNET=/home/minorum/.local/share/koh-dotnet-10.0.115/dotnet
RUSTUP_TOOLCHAIN=stable /usr/bin/cargo, target-dir /tmp/koh-header-target, -j1.
Use PATH/DOTNET_ROOT for that dotnet during hooks. Never build concurrently in
one output directory. A timeout is not terminal; poll the same live session.
Read completed logs and actual exit codes; do not use piped status. Keep only
scope-related staging and preserve unrelated files. Default full native runner
is cargo nextest run --workspace --all-features; rustdoc runs separately.

Historical architecture, ABI, test mapping and earlier results live in
ROADMAP.md and docs/rust-compiler/{progress,sm83-abi,test-disposition}.md.
