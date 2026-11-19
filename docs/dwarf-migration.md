# KDBG to DWARF migration

Requested on 2026-11-18 as the prerequisite to the GPUI migration.
Status: production writer, managed debugger and SDK/editor consumers now use
DWARF. KDBG implementation removed. Real C# debugging, NativeAOT and hosted
platform acceptance remain open.

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
Assembly expansion provenance uses the documented vendor extension below.
It preserves nested call locations without pretending macros are subroutines.

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

Next: C# consumption boundary.
`Koh.Native/NativeBuild.targets` already builds/copies `koh-link`; evaluate
that native parsing path against NativeAOT/packaging before choosing.
LibObjectFile's README advertises DWARF 4; do not assume DWARF 5 compatibility.
Prove managed consumption and real debugging before switching producers.

## Reader checkpoint (2026-11-18)

`read_dwarf` uses gimli and returns the existing linked symbol/source records.
`koh-link --read-debug-info` reads ELF bytes from stdin and emits JSON records;
invalid input exits unsuccessfully without a partial JSON response. This is
the intended native parsing boundary for the managed debugger, not a second
persisted debug format. The managed caller has not been connected yet.

The reader validates the SM83 ELF header/note, duplicate/required sections,
DWARF version/address encoding, symbol tags/scopes, RAM address expressions,
file indices and monotonically ordered, bank-bounded source ranges. Input and
decoded records/strings are limited to 64 MiB. It accepts absent line tables
for genuinely symbol-only/empty units, preserves unknown-line gaps and signed
32-bit constants, and rejects unsupported constructs rather than dropping them.
This is a reader of Koh's emitted DWARF profile, not arbitrary external DWARF.

`cargo test -p koh-link --locked` completed with exit 0:
`/tmp/koh-dwarf-reader-tests.log` records 18 library + 5 CLI + 9 DWARF tests,
all passing, 0 failed/ignored. Tests include every truncated prefix of a valid
file, wrong header/note/version, stdin CLI success/failure, full-width signed
constants, absent line data, CPU-address boundaries and Unix/Windows paths.

The compiled CLI parsed the independently verified probe ELF: exit 0,
`/tmp/koh-dwarf-reader-probe.json` contains bank 2, address 16384, count 3,
`source.cs:7` and symbol `entry`. A fresh LLVM verifier run exited 0 and ends
`No errors.` in `/tmp/koh-dwarf-reader-independent.log`.

Focused Clippy on the library, binary and DWARF integration test passed with
warnings denied. Broader all-target Clippy is not clean: existing
`koh-objects/src/model.rs:226` collapsible-if and `koh-link/src/tests.rs:312`
clone-to-slice findings remain outside this increment. No suppressions added.
Real debugger, SDK, NativeAOT and three-platform cutover remain unverified.

The writer/reader checkpoints above are historical. The current producer and
managed-consumer state is recorded below.

## Assembly expansion checkpoint (2026-11-18)

Expansion ancestry now round-trips on source ranges and symbol definitions,
including repeated identical ranges with different ancestry. The representation
uses DWARF's [vendor extension mechanism](https://dwarfstd.org/doc/DWARF5.pdf)
(sections 1.3.13 and 7.1), inside `.debug_info`; no separate binary format.
Ordinary source lines, symbols, constants and RAM locations remain standard
DWARF records and do not depend on understanding the extension.

- `DW_TAG_KOH_expansion = 0x4b00`: each entry stores `DW_AT_call_file`,
  `DW_AT_call_line`, and `DW_AT_call_column`. Children form an outer-to-inner
  expansion chain, bounded to 256 entries.
- A range's outer frame is a child of the compilation unit. It also stores
  `DW_AT_low_pc`, a length-valued `DW_AT_high_pc`, `DW_AT_decl_file` and
  `DW_AT_decl_line` identifying its exact source range.
- `DW_AT_KOH_source_range = 0x2b00`: a `DW_FORM_udata` index on that outer
  frame identifies the zero-based decoded nonempty, known-line source range
  within the unit. This disambiguates even identical overlapping ranges.
  The reader checks the referenced range against its address/size/file/line.
- A symbol's outer expansion frame is its child; the symbol's declaration
  attributes supply the source location. Nested frames carry no source-range
  index. No `DW_TAG_inlined_subroutine` or fabricated call frame is emitted.
- Zero-length source ranges are omitted, as before. Expansion ancestry attached
  to an unknown source line is rejected explicitly rather than discarded.

Completed `cargo test -p koh-link --locked`, exit 0; re-read
`/tmp/koh-dwarf-macro-tests.log`: 18 library +5 CLI +12 DWARF tests passed,
zero failed/ignored. Focused Clippy with warnings denied also passed.

The existing real assembly 2048 sample was assembled to `/tmp/koh-dwarf-2048.o`
with the fresh native assembler, then linked and written/read as DWARF by a
temporary probe using the fresh libraries. Every field compared equal for all
514 symbols and 3,951 source ranges, including all 3,710 expanded ranges.
`/tmp/koh-dwarf-2048-link.log` records the exact metadata roundtrip, exit 0.
The native reader CLI independently read that ELF, exit 0. LLVM verification
exited 0 and ends `No errors.` in `/tmp/koh-dwarf-2048-verify.log`.

The probe's ROM exactly matches the normal CLI-linked ROM, SHA256
`8c15e8a8c853f1b2f061021b2b7912995382ba25f79d5fd7db6c2427525f97af`.
An independent readelf probe decoded the vendor entries and normal source line
at banked `0x24000` without an error. These are producer/reader checks, not
emulator execution, managed debugger or GUI acceptance.

## Managed debugger and producer cutover (2026-11-18)

`koh-link -d/--debug` writes a DWARF ELF file. SDK and assembly sample outputs,
editor build tasks, DAP defaults and compiler test consumers use `.elf` sidecars.
The KDBG writer, reader, format/kind definitions and synthetic builder are
removed. No compatibility reader or alternate KDBG output is retained.
Existing `.kdbg` files must be rebuilt; they are not accepted by the new reader.
Historical validation notes retain references to the old baseline.

`Koh.Objects` now holds format-neutral debug records; `NativeLinker.ReadDebugInfo`
invokes the existing bounded native parser, exchanging binary stdin and JSON
records with bounded stdout/stderr and a process timeout. The debugger builds
new maps before replacing live ones, clears maps for symbol-free loads, and
keeps signed constants out of address-based lookups/code breakpoints. Source
ranges use full-width counts with a checked CPU-address boundary. The reader
preserves wide banks, while the current emulator explicitly rejects banks
above 255 without truncating them.

Completed local validation, with final logs re-read:

- `dotnet build Koh.Ci.slnf -c Release -m:1 --no-restore -nr:false`: exit 0,
  zero warnings/errors, all seven C# sample ROMs built. Log:
  `/tmp/koh-dwarf-cutover-solution-build.log`.
- Serial fast suite: exit 0, 574 total, 572 succeeded, 0 failed, 2 existing
  opt-in SDL window skips. Includes 42 debugger tests and source/ROM/DAP
  regressions. Log: `/tmp/koh-dwarf-cutover-fast-tests-final.log`.
- `cargo test -p koh-link --locked`: exit 0, 35 passing tests. Workspace
  all-target/all-feature check exits 0 with the existing unused cube-helper
  warning. Logs: `/tmp/koh-dwarf-cutover-rust.log` and
  `/tmp/koh-dwarf-cutover-workspace-check.log`.
- Extension compile and tests under an owned Xvfb server: exit 0, 44 passing.
  Log: `/tmp/koh-dwarf-editor-tests-xvfb.log`. This is extension harness proof,
  not a watched full VS Code debugging session. The temporary X server stopped.
- All seven SDK `.elf` outputs independently parsed with nonempty symbol and
  source maps. Log: `/tmp/koh-dwarf-sdk-sidecars.log`. LLVM independently
  verifies the real JRPG debug ELF, exit 0, ending `No errors.` in
  `/tmp/koh-dwarf-sdk-jrpg-verify.log`.
- Workspace Rustfmt, focused Clippy with warnings denied, CSharpier and diff
  checks pass. No new warning suppression or NuGet audit opt-out.
- Added selected MIT notices for gimli, fnv and stable_deref_trait. Their Cargo
  archives match lockfile digests; all three copied texts exactly match the
  package sources and the emulator build output. This does not close unrelated
  first-party or managed-payload release notice work.

The first broad run caught an incorrect assertion that assembly `ds 600` must
produce one range. Assembly retains per-byte provenance; the corrected test
checks exact coverage of every byte, independent of fragmentation. A separate
single-range fixture proves that the managed reader/map retain a 600-byte
range and its exclusive endpoint. The final suite above includes both.

Local restore initially blocked on a stopped orphan Aspire template-search
process holding a shared NuGet lock. A command-scoped `NUGET_SCRATCH` directory
allowed restore to complete; no host process/configuration or audit settings
were changed. All successful .NET commands used SDK 10.0.115 and bounded
processor/test concurrency.

### Real compiler/debugger acceptance

The selected `symbols::` integration run completed with exit 0:
`37 tests run: 37 passed (1 slow), 619 skipped`, in 244.779 seconds.
The 619 cases were outside the selected filter. Completed log:
`/tmp/koh-dwarf-source-debugger-tests-final.log`.

Coverage includes native C# source breakpoints, machine state, finally unwinding,
corrupt/unknown stack rejection and real SDK 2048 Debug/Release builds with
framework caller recovery. The Debug SDK case completed in 172.910 seconds
while rebuilding its cold native tool cache. The test host's injected symbol
fixture now uses the neutral debug record constructor.

Hosted run `44390929517` at `22f9ef4` passed 1479/1480 Rust fast cases.
The remaining assembler macro test still interpreted debug output with legacy
KDBG offsets. It now compares decoded DWARF ranges and complete macro ancestry
against the linked records. The full assembler suite then passed 149 tests,
exit 0; completed log: `/tmp/koh-dwarf-macro-acceptance.log`. Rustfmt check
also exited 0. Hosted acceptance on the corrected head remains pending.

Hosted Rust checks subsequently passed at `a481017` (run `44393108491`).
Fresh Linux NativeAOT engine verification now also passes: all nine process
cases, including DWARF source breakpoint resolution and stepping. See
[engine session](engine-session.md) for the executable hash and completed logs.

The windowless engine and GPUI interface are implemented. Physical KohUI removal,
desktop packaging and final platform acceptance are tracked in
[GPUI migration](gpui-migration.md).
