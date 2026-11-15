# Compiler test disposition status

The source audit accounts for every one of the baseline's **565 test methods**:

| Owned source groups | Methods | Source-level disposition |
| --- | ---: | --- |
| Frontends excluding art, plus SoftFloatDifferentialTests | 230 | [Frontend audit](frontend-audit.md) |
| Backends, Ir, Sm83 | 290 | [Backend/ABI audit](backend-audit.md) |
| Samples and TileSheetConverterTests | 45 | [Integration audit](integration-audit.md) |
| Total | 565 | No missing test declarations in the source-to-document cross-check |

These documents name every method and its proposed PORT/REPLACE decision, its
behavioral requirement, independent justification or missing oracle, and target
Rust grouping. No compiler-owned tests are deferred. No feature removal has been
approved; obsolete implementation assertions can disappear only after stronger
native acceptance replaces them. The integration audit separately identifies
active noncompiler debugger/emulator/tools ownership and removal dependencies.

The mechanical baseline also contains 390 Arguments attributes and 1,524
Assert.That call sites. Parameterization and loops mean neither method counts nor
call-site counts equal the number of behavioral scenarios. Counting independently
justified native scenarios remains part of each port, not a number invented from
attribute totals. See the [per-file inventory](test-inventory.md).

The legacy compiler project was then run with .NET SDK 10.0.115 on Linux:
**814 succeeded, 0 failed, 0 skipped**, exit 0. This is a parameter-expanded
execution count, not 814 independently justified language rules. Command:
`dotnet test --project tests/Compiler/Koh.Compiler.Tests/Koh.Compiler.Tests.csproj -c Release -- --maximum-parallel-tests 1`.
Completed log: `/tmp/koh-compiler-legacy-baseline.log`; HTML artifact:
`tests/Compiler/Koh.Compiler.Tests/bin/Release/net10.0/TestResults/Koh.Compiler.Tests-linux-net10.0-report.html`.
The baseline establishes that the existing fixtures pass on this checkout;
it does not make their expectations correctness oracles.

**Native behavioral replacements: seven legacy methods; zero physically retired.**
The switch, alignment, tile-copy and initialization groups are detailed below.
The remaining558 methods still have
partial coverage or outstanding dependencies. Legacy definitions temporarily
remain with the live C# compiler; the final migration deletes that test project.
Proposed Rust filenames in source audits are not implemented or discoverable
merely because they are documented.

## Corrections required in acceptance

Primitive mutable-array coverage is now partly implemented in
[arrays.md](arrays.md): four Rust driver cases execute 40 real Roslyn ROM
scenarios and 16 explicit rejection probes. Twelve element-type verifier cases,
five malformed IR operations, two null-merge orderings and six native/int16
opcode mismatch cases are separately discoverable by nextest. These cover parts
of the array-length/memory audit groups; aggregate/framework/sample behavior
remains open. No complete legacy method has been retired by this increment.

[RVA initialization](rva-initializers.md) adds four Rust driver cases with 24 ROM
scenarios and 12 helper-metadata rejection probes, three PE reader variants,
eleven malformed RVA/layout cases and sixteen IR identity/storage/budget/call-ABI
cases. These partly implement the literal/static/memory groups; mutable identity
and bytes replace brittle ROM/pass assumptions. Full sample/runtime coverage and
whole-method retirement remain open. All new assertions are Rust-owned and
discoverable by nextest; C# files are compiled fixtures or the emulator host.

[Enum/stack/width coverage](stack-enums.md) partly implements the integer,
indirect-memory, array, recursion and sample-behavior audit groups. Four Rust
driver cases execute40 ROM scenarios and8 allocation rejection probes; two
parameterized arithmetic cases execute50 ROM scenarios, two alias cases execute2,
and9 IR cases check allocation acceptance/rejection. Expected arithmetic and board
state come from independent values/models, rather than old compiler output.
The actual Board sample source is linked unchanged. This replaces legacy private
IR/frame-shape assumptions with observable semantics, but no complete legacy test
method is retired: full runtime, graphics and other assertions remain outstanding.

Table-switch coverage maps frontend `SwitchDispatch_JumpTable_SelectsCorrectCase`
and `SwitchDispatch_OutOfRange_FallsThroughToDefault`, backend
`Switch_SelectsCaseOrDefault`, and the behavioral part of
`SwitchDispatch_LowersToRealSwitchInstruction` to Rust-owned tests in
`tests/{lowering,ir,sm83,driver}.rs`. The first three are PORT; private representation
assertions are REPLACE. ECMA-335 control transfer and independent integer checksums
justify the new expectations. Nextest discovers18 cases, including13 ROM scenarios
and explicit malformed/resource diagnostics. Full legacy methods are not retired
until their remaining assertions and sample/runtime dependencies are accounted for.

### Completed switch replacement group

| Original method | Requirement and justification | Decision and native location |
| --- | --- | --- |
| `Frontends/CilStructTests.SwitchDispatch_JumpTable_SelectsCorrectCase` | Real Roslyn Debug/Release selects the indexed result; successful lowering and verified IR. CLI switch semantics independently justify the result. | PORT: `driver::roslyn_switch_tables_execute`, plus raw-stack cases in `lowering` and `sm83`. |
| `Frontends/CilStructTests.SwitchDispatch_OutOfRange_FallsThroughToDefault` | Out-of-range values use default in both configurations. | PORT: both Roslyn checksum cases and full-width boundary CPU cases. |
| `Frontends/CilStructTests.SwitchDispatch_LowersToRealSwitchInstruction` | Both configurations must actually exercise table-switch lowering. Private C# IR class identity is not a public contract. | REPLACE: driver asserts opcode0x45 in the actual input and executes every case/default; verifier and CPU coverage validate the resulting control flow. |
| `Backends/Sm83ControlFlowTests.Switch_SelectsCaseOrDefault` | Independent selection/default results execute on SM83. The old accumulator/static-WRAM observation is an obsolete ABI assumption. | PORT: `sm83::cil_switch_executes_full_width_selectors_and_stack_edges`, with balanced SP and guard checks. |

All four replacements are discoverable by cargo-nextest. The original group has
five method executions from three PORT methods and two parameterized REPLACE
executions; including helper Debug/Release loops, it contains19 expanded
`Assert.That` checks (12 frontend fixture checks,4 representation checks,3 backend
results). New coverage includes18 native cases and13 ROM executions. Counts are
traceability, not proof by matching totals. The original BGP output channel is
replaced by the native return-buffer checksum; BGP writes/readback independently
remain covered in `driver::gameboy_register_and_region_intrinsics_execute` and
`fixtures/Hardware`. No language or hardware capability is removed. These source
definitions will disappear with the legacy project rather than leaving a second
compiler test implementation in the final tree.

### Pinned-pointer coverage added

`driver::pinned_array_pointers_copy_tiles_and_restore_banks` adds four D/R ×
DMG/CGB cases with28 ROM scenarios and8 known-bank-write rejection probes. Actual
referenced `Mem.Copy` runs under pinned scopes; expected VRAM words, heap bytes,
separate-bank poison, return values and balanced SP independently justify the
assertions. This covers part of `MemRuntimeTests` and pointer/graphics audit
groups, not their larger counts, performance or LCD-on timing requirements.
`ir` adds9 pin storage/initialization/escape cases; `sm83` adds one test with5
checked-cleanup ROM scenarios. All14 new cases are discoverable by nextest.
No additional complete legacy method is retired by this increment. See the
[pin contract and target bank restrictions](pinned-pointers.md).

### Tile loading and static rendered frames

`driver::actual_tileset_load_copies_vram_and_preserves_banks` adds four native
cases/12 ROM scenarios, including four full-frame RGBA comparisons. This partly
ports `CilTileSetTests.Load_LcdOff_CopiesTileBytesVerbatimIntoVram` to actual library
execution with whole-bank guards, plus GDMA-path and palette/bit-plane evidence.
The later alignment increment executes the old fixture's `Video.Init` dependency
separately through the real library. LCD-on timing remains open.
Expected pixels come from independent bit-plane rules, not a screenshot produced
by either compiler. PNG encoding stays in the existing emulator; the Rust-owned
tests invoke its host as a process. See [graphics execution](graphics-execution.md).

### Alignment and real video initialization

| Original method | Requirement and independent basis | Decision and native location |
| --- | --- | --- |
| `CilGraphicsSlice2Tests.KohAligned_RoundsStaticWramGlobalCursorUpToAlignment` | DMA-compatible payload alignment after a preceding allocation; exact legacy WRAM addresses are implementation details | REPLACE complete: `driver::aligned_static_arrays_have_aligned_mutable_payloads`, four nextest cases; exact poisoned heap snapshots, mutable aliases, scalar alignment, empty/overflow boundaries. New managed headers replace legacy embedded global arrays. |
| `CilTileSetTests.Load_LcdOff_CopiesTileBytesVerbatimIntoVram` | Public copy contract and SM83 memory behavior:32 supplied bytes appear at tile3 | PORT complete: `driver::actual_tileset_load_copies_vram_and_preserves_banks`, D/R × DMG/CGB, actual library code, complete guarded bank snapshots and independent expected bytes. Video.Init dependency executes separately in the same cases. |
| `CilVideoJoypadTests.Init_ClearsBothTileMapsAndOamAndSetsDefaultDmgPalette` | Public initialization contract: maps/OAM zero, palettes E4, LCD disabled | PORT complete: same driver cases, `VideoInit` entry; every map/OAM byte checked after poison, palettes and LCDC11, backing arrays and bank preservation checked. D/R and CGB improve the original Debug/DMG-only coverage. |

All three behavioral replacements are discoverable by cargo-nextest. Original
C# definitions remain until production replacement. This adds three completed
legacy methods to the four switch replacements, without retiring other graphics,
OAM-DMA, input, timing or sample scenarios. See [alignment](alignment.md).

- Require an independent expected result as well as Debug/Release agreement.
- Require bounded program completion before inspecting output memory.
- Test mutable array identity and writes; compiler-folded ROM bytes do not prove
  ordinary mutable array semantics.
- Guard frame/stack/heap capacity before writing, including recursive references
  to still-live ancestor locals.
- Test interrupted arithmetic and bank changes against documented preservation
  rules, including narrow helpers using shared scratch.
- Validate known method/file/line PDB mappings through actual debugger stops.
- Keep curated sample assets unchanged; distinguish generated PNG existence from
  pixel/scene acceptance and distinguish emulator runs from physical hardware.

The audit findings above come from source inspection. Native execution evidence
is recorded separately in [the SM83 ABI/slice](sm83-abi.md) and
[progress](progress.md); it does not reproduce all identified legacy risks or
establish completion of the final supported subset.
