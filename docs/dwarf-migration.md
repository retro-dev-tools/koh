# KDBG to DWARF migration

Requested on 2026-11-18 as the prerequisite to the GPUI migration.
Status: initial writer implemented and checked; production remains on KDBG.

## Contract

Replace KDBG debug files with ELF32 little-endian files containing DWARF 5.
Relocatable object interchange already uses ELF32/SM83 and stays intact.
Use gimli for DWARF and the existing object crate for ELF; do not invent a
second binary debug format. Preserve compiler Portable PDB identity validation
and source provenance. No new language feature or unwinding promise is implied.

DWARF addresses use four bytes: `(bank << 16) | cpu_address`. This distinguishes
identical CPU PCs in distinct banks without confusing them with ROM offsets.
The engine's current byte bank limit remains a separate consumer constraint;
the writer must not silently truncate larger banks. Absolute signed constants
are DW_TAG_constant/DW_AT_const_value, code labels use DW_TAG_label/low_pc and
RAM labels use DW_TAG_variable/location expressions. Do not guess function
extents from neighboring label addresses.

Each source range is an explicit line-program sequence with an exclusive end.
Unmapped gaps stay unmapped. Keep source path spellings, line identity and
definition columns. Reject invalid names, overflowing addresses and ranges.
Macro expansion provenance must have a deliberate representation before
production cutover; the initial writer rejects it instead of dropping it.

Existing stack inspection uses exact-PC `__koh_unwind_v1_*` marker symbols and
the far-return stack convention. Retain these symbols during format migration;
standard call-frame information is a separate, explicitly tested increment,
not an automatic consequence of emitting DWARF. Optimized variable locations
and types do not currently exist in KDBG and are not part of parity.

## Cutover inventory

- `rust/koh-link`: replace debug writer, expose DWARF CLI output and independent
  reader/standard-tool validation; no remaining KDBG output after cutover.
- `rust/koh-objects`: reusable validated debug records/reader boundary as
  needed by native tools; preserve assembler/compiler dependency boundaries.
- `Koh.Objects`: remove KDBG format/reader/kind names and use format-neutral
  debug records. Select a maintained DWARF reader or native parsing boundary
  after checking NativeAOT and packaging; avoid a handwritten general parser.
- `Koh.Debugger`: loader, symbol map, source map, stack unwinder and DAP launch
  default. Preserve source breakpoints, functions, disassembly, bank identity,
  constants and the proven exact-PC caller contract.
- `Koh.Emulator.App`: DAP-host launch/debug file defaults.
- SDK task outputs, `build.proj`, sample build scripts and editor configuration/
  build tasks: rename `.kdbg` outputs and every consumer together.
- Replace `tests/Support/KdbgFileWriter.cs`, `DebugInfoBuilder.cs` and dependent
  linker/debugger test fixtures. Migrate compiler symbol tests and packaged
  artifact checks; do not make green tests depend on retained KDBG adapters.
- Update documentation, project maps and release contents. Audit every tracked
  `kdbg` reference and distinguish historical statements from active paths.

## Acceptance

1. Independently parse standard ELF/DWARF output, including distinct banks at
   equal CPU addresses, negative constants, RAM symbols, large/gapped ranges,
   file identity, macro provenance and malformed-input diagnostics.
2. Run real Debug/Release assembly and C# source-to-ROM-to-debugger workflows,
   including no-PDB, referenced modules, hidden points, banked/recursive caller
   inspection and source path aliases.
3. Verify VS Code launch, breakpoint, step, memory/disassembly and shutdown
   using the replacement files; preserve headless operation.
4. Build and package on Linux, Windows and macOS. Verify bundled parser/runtime
   dependencies and notices rather than relying on source-only compilation.
5. Remove production KDBG code and active references before claiming migration
   complete; then resume GPUI and complete KohUI removal.

## Writer checkpoint (2026-11-18)

`LinkResult::dwarf()` emits ELF32/SM83 with `.note.koh`, DWARF 5 information,
line tables and symbols. It bounds output estimates, validates addresses/names,
preserves signed constants and rejects macro expansions until implemented.
No CLI/SDK/default output changed yet.

Completed `cargo test -p koh-link --locked`, exit 0. Re-read
`/tmp/koh-dwarf-tests.log`: 18 library, 5 CLI and 3 DWARF tests passed;
0 failed/ignored. The new tests parse emitted files with gimli and check equal
CPU addresses in different banks, source gaps, 600-byte ranges, signed constants,
RAM locations, the SM83 note and rejected invalid/unimplemented input.

A temporary Rust probe linked the fresh library and wrote
`/tmp/koh-dwarf-probe.elf`. `llvm-dwarfdump --verify` exited 0; its completed
log `/tmp/koh-dwarf-verify.log` ends `No errors.`. `readelf -n --debug-dump=info
--debug-dump=decodedline` exited 0; `/tmp/koh-dwarf-readelf.log` shows
`source.cs` line 7 at `0x24000`, exclusive end `0x24003`, and label `entry`
at `0x24000`. These checks prove the initial writer, not debugger cutover.

Next: bounded gimli reader and C# consumption boundary.
`Koh.Native/NativeBuild.targets` already builds/copies `koh-link`; evaluate
that native parsing path against NativeAOT/packaging before choosing.
LibObjectFile's README advertises DWARF 4; do not assume DWARF 5 compatibility.
Resolve macro provenance before switching producers.
