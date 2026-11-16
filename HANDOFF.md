# Native compiler migration handoff

Full goal: one draft PR55 against master; C# authors/Roslyn, Rust CIL/IR/SM83
compiler and ELF linker, existing C# emulator. Do not merge or narrow acceptance.
Worktree /home/minorum/projekty/koh-rust-compiler, feat/rust-cil-compiler.
Before this increment pushed ca835c21949352b6f177e35c4374e882bf581d36.
Preserve main checkout unrelated samples/csharp/gb-jrpg/screenshots/battle.png.

## Rectangular-array increment

Rank2 zero-based array identity survives typed IR, cells and fields. Validated
runtime-provided constructor/Get/Set/Address and System.Array queries lower in
Rust. Ordinary checked heap stores count/rows/columns plus row-major payload;
mutable RVA copies, original-index checks, null checks, capacity and SVBK
restoration. Struct/reference/long fields and borrowed element references work.
No production emulator changes. Review rectangle_review found no critical or
important bug; added its requested bound/size/query metadata rejection probes.

Tests and completed logs:
- /tmp/koh-rectangle-final.log:30/30 pass183.953s exit0, session75244 terminal.
- /tmp/koh-rectangle-widths.log:8/8 pass46.495s exit0, session14447 terminal;
  explicit16/64-bit widths and original55 read before alias mutation.
- /tmp/koh-rectangle-shape-query.log:2/2 pass11.361s exit0, session3837 terminal;
  13metadata mutations per Debug/Release case.
- /tmp/koh-rectangle-allocator-regression.log:353tests349pass4fail190.642s
  exit100; only failures obsolete rank2 rejection assertions. Replaced with
  return4/exactheap/bank checks and rank3 rejection;4/4pass49.521s exit0 in
  /tmp/koh-rectangle-vector-corrected.log. Do not call original353run green.
- Formatting Rust and two C# fixtures succeeds; diffcheck succeeds.
- Workspace alltargets/allfeatures check passes9.88s exit0, existing cube-helper
  warning only; /tmp/koh-rectangle-check.log, session5096 terminal.

51/565 legacy source methods have full behavioral replacement,514 outstanding,
zero physically retired. Rectangular source method replaces private ROM-folded
initializer representation with ordinary mutable array execution. Separate
string-reference array source method remains outstanding in this file's audit.
See docs/rust-compiler/{rectangular-arrays,test-disposition,progress}.md.

## Next real sample dependency

Ordinary Release SDK build of unchanged JRPG with fresh native compiler advances
past World rank2 maps, then fails Villager::Interact/callback path at method
0x0600002a IL0005: Reference class has no base type. No ROM/gameplay claim.
/tmp/koh-jrpg-rectangle-build.log exit1,45.16s; session63851 terminal.
Next reproduce callback/delegate metadata in a tiny fixture and fix root cause,
then progress actual JRPG. Keep aspirational sample distinction explicit.

## Whole-goal gates still open

PDB matching/source mappings through ELF/KDBG; actual source breakpoints and
native frame/bank-aware stack inspection; interrupts/reentrancy/bankeddata;
remaining language/runtime/generic/float/delegate/interface coverage; all565
source-test dispositions; C# compiler/test/Mono.Cecil deletion; final packaging,
comparable performance and Linux/Windows/macOS full acceptance. No blanket
feature retirement or compiler-owned DEFER. Current API/emulator behavior is
never an independent oracle. See prior progress.md for hosted/local evidence.

## Commands and discipline

KOH_DOTNET=/home/minorum/.local/share/koh-dotnet-10.0.115/dotnet
RUSTUP_TOOLCHAIN=stable /usr/bin/cargo, target-dir /tmp/koh-header-target, -j1.
PATH/DOTNET_ROOT that dotnet for hooks. Serial builds per output directory.
Timeout is not terminal: poll recorded live handles. Read complete logs and
actual exit codes. Final default cargo nextest run --workspace --all-features;
rustdoc separately cargo test --doc. Preserve unrelated dirty files.

Latest hardware-state followup ca835c2 waits nextVBlank before LCD disable.
Pan Docs LCDC91 is postbootPC0100, not cold-reset proof. Existing PPU91 plus raw
CPU/bootmapping needs independent emulator audit; no production reset change.
