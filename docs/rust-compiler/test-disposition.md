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

**Native behavioral replacements: thirty-four legacy methods; zero physically retired.**
The switch, alignment, tile-copy, initialization and value-copy groups are detailed below.
The remaining531 methods still have
partial coverage or outstanding dependencies. Legacy definitions temporarily
remain with the live C# compiler; the final migration deletes that test project.
Proposed Rust filenames in source audits are not implemented or discoverable
merely because they are documented.

## Runtime fill replacements

Two `Sm83/MemRuntimeTests.cs` methods now map to
`driver::memory_fill_exact_extents_alignment_and_cost`: four Debug/Release ×
DMG/CGB cases, each executing four alignments plus a bus-wrap ROM. The original
group has7 assertion sites and316 individual byte assertions across the two
methods; those are oracle observations, not316 language features.

| Source method | Behavioral requirement and independent justification | Decision and native evidence |
| --- | --- | --- |
| Fill_VariousCounts | Zero count changes nothing; positive count changes exactly its byte extent with the requested value | PORT with stronger whole-bank poison/guard snapshots for0/1/2/3/4/5/7/15/16/17/255/256/257/300 lengths and four alignments; no legacy heap address assumption |
| Fill_CrossesA256ByteBoundary | A byte counter boundary must not truncate or extend a fill | PORT: exact255/256/257/300 regions plus unchanged following gaps and DFFF endpoint guard; comparison comes from Rust-generated expected bytes |

The normal fill matrix has16 ROM scenarios with15 fills each; four additional
ROMs validate exact wrapped bus writes. Separate four-case pinned-bank tests
execute safe zero/nonbanking fills and reject direct/transitive known SVBK
ranges. Two desktop cases check56 extents each, six IR cases check typing and
preserved dependencies, and four backend cases check scalar truncation, large
offsets and stack guards. All are nextest-discoverable. See
[contract, review, measurements and completed logs](runtime-memory.md).

The other8 methods in this source group remain partial/outstanding: they
include Copy semantics/performance and a combined frontend check. Nothing is
DEFERred or physically deleted by this increment.

## Cube replacement in progress

`Samples/Cube3dTests.cs` has3 methods,6 parameter-expanded cases and8 assertion
sites. The native `cubes` target supplies8 real double-buffered/full-frame
Debug/Release × DMG/CGB cases,12 independent viewport-validator cases and11
GDMA-window boundary cases. They are discoverable by cargo-nextest, not skipped
or feature-gated. Double-buffered acceptance passes in all four configurations after the
[VBlank correction](vblank-window.md). No additional legacy method is counted
complete: full-frame coverage remains required.

| Source method | Behavioral requirement and independent justification | Decision, native target and remaining work |
| --- | --- | --- |
| Demo_CompilesWithoutDiagnostics | Actual shipped sources and referenced library compile; compiler class names and diagnostic containers are private details | REPLACE with Roslyn/native compilation and ELF/linking in `cubes::cube_boots_and_animates`; both variants included, full-frame acceptance still required |
| Demo_LinksToBootableRom | Cartridge header and startup execute on the supported hardware model | REPLACE weak NOP/JP/two-logo-byte checks with real DMG/CGB boot-ROM unmapping and bounded game execution; same target, full-frame still required |
| Demo_RunOnHardwareRendersARealCube | Authored shaded projection is visible and animates; expectations derive from sample geometry/palette and SM83/PPU behavior | PORT with stronger two-capture palette, viewport, size, connectivity, animation and full DMA-window checks; double-buffered passes, full-frame DMG Debug animation remains required |

The7 `CilCanvasTests.cs` methods (36 assertion sites) remain partial: the real
cube exercises rasterization/presentation, but cannot replace their exact plane
bytes, isolated span/line cases, LCD-off and page-flip assertions. No compiler
coverage is DEFERred. See [transfer design](canvas-transfers.md) and
[VBlank correction](vblank-window.md).

## Raw arena replacements

Four methods in `Frontends/CilReferenceTests.cs` now map to
`driver::raw_arena_shares_capacity_without_resetting_managed_arrays`, four
Debug/Release × DMG/CGB nextest cases. The original group has12 assertion sites
and seven parameter-expanded executions; these counts do not mean12 independent
semantics. The replacement executes52 ROM scenarios, eight malformed-contract
checks, a separate desktop runtime case and five typed-IR cases. See
[the new lifetime/capacity contract](raw-arena.md).

| Source method | Public requirement and independent justification | Decision and native evidence |
| --- | --- | --- |
| MemAlloc_IsACompilerOwnedIntrinsic_AndAllocatesAtTheExpectedAddress | Writable raw allocation and MMIO round trip; hardware bus behavior justifies bytes, not the old private DE00 heap top | REPLACE: actual BGP42 read independently by the emulator, distinct block snapshots and linker-reserved bank1 bounds; old DDFC address is intentionally retired |
| MemAlloc_DebugAndReleaseProduceIdenticalObservableState | Roslyn configurations preserve program meaning | PORT with stronger oracle: both configurations must independently return42 and match the expected entire raw-memory image, rather than merely agree with each other |
| Arena_AllocatesDistinctBlocks | Two positive-size live blocks are writable and nonaliasing | PORT: bytes11/22 appear in distinct regions of an otherwise unchanged poisoned arena; observable sum and return state checked |
| Arena_ResetReclaimsEverything | Reset permits raw address reuse | PORT with corrected lifetime: repeated address and new writes work while a live managed array retains length/contents; reset does not free managed references |

The original diagnostics/verifier assertions are replaced by successful native
compilation, execution and negative Rust IR typing checks. No compiler-owned test
is deferred or physically deleted by this increment.

## CPU control and speed switching

Two `Samples/CgbHalTests.cs` methods are PORT to the four nextest cases in
`driver::cpu_control_and_speed_switch_execute`: `TryEnableDoubleSpeed_FlipsKey1Bit7_InCgbMode`
(four original assertion sites: returned success, double-speed state, KEY1 bit7,
disarming) and `TryEnableDoubleSpeed_DoesNothingHarmful_InDmgMode` (three sites:
false result, normal speed, CPU not stopped). Expectations derive from KEY1/STOP
hardware semantics, not the C# compiler's calling convention. New tests add
Debug/Release, repeated calls, IE/pending-request preservation and an IME probe
before shutdown. See [contract and evidence](cpu-control.md). Five other methods
in this source group remain partial/outstanding; no C# tests are deleted yet.

## Tile-sheet and SDK replacements

Both methods in `Frontends/TileSheetConverterTests.cs` (nine original assertion
sites) are PORT to `sdk.rs::sdk_uses_native_compiler_and_tracks_outputs`, two
Debug/Release nextest cases. They preserve public authoring requirements, not
the converter's managed class location or source formatting.

| Source method | Requirement and independent justification | Native assertion |
| --- | --- | --- |
| SyntheticPng_RoundTripsToPlanesPaletteAndSource | One8×8 tile, luminance-ordered RGB555 bookends, exact2bpp rows, configurable namespace and named members; justified by PNG pixels and GB encoding | Checksum-valid PNG goes through MSBuild; real C# code consumes Some.Ns.Art members, checks palette/count and weighted data; existing emulator exposes all16 expected bytes in WRAM and result42 |
| FiveColorSheet_IsARejectedAuthoringError | One tile palette has four color slots; excess colors require an authoring error | Real dotnet build diagnoses five distinct colors and preserves the previous ROM; no silent quantization |

No compiler-owned case is deferred. Generated source must still be included on
unchanged builds; harmless whitespace or namespace syntax is not an assertion.
The old malformed Adler32 fixture is replaced with valid PNG checksums rather
than preserving its permissiveness as a contract.

The same Rust target covers the SDK script's assembly/reference, output path,
no-op, CGB/debug flags, tool timestamps/selection and failed-link preservation
requirements, plus compiler selection, changed reference bodies, unsupported IL,
stale-debug cleanup and output aliases. A real native CLI's nonzero rejection
replaces the Unix-only mock exit7; the exit status is still asserted. SDK run
routing has a separate real graphics/headless case. See [SDK boundary](sdk.md).
The Python verification script is retired after migrating its nine build-phase
requirements; native CI runs the Rust target through workspace nextest.

## Graphics showcase replacements

Four methods in `Samples/GbGfxDemoTests.cs` (12 original assertion call sites)
now have complete replacements in
`driver.rs::full_graphics_showcase_boots_and_animates`, discoverable as four
Debug/Release × DMG/CGB nextest cases. The unchanged sample runs through actual
Koh boot ROMs and the existing emulator. Final focused execution passed4/4,
alongside4 sprite cases (`/tmp/koh-presentation-final-focused.log`, exit0).

| Source method | Requirement and independent justification | Decision and replacement |
| --- | --- | --- |
| Demo_CompilesWithoutDiagnostics | Real sample compiles and verifies; CIL/IR validity is required, C# verifier identity is incidental | REPLACE: native CLI success, verified IR and canonical ELF consumed by Rust linker |
| Demo_LinksToBootableRom | A valid cartridge boots; two particular entry opcodes and two logo bytes are weak implementation proxies | REPLACE: actual DMG/CGB boot handoff, independent header suite and corrupted-logo rejection |
| WindowMap_SpellsScoreViaFontBasePlusOffset | Visible SCORE glyphs after at least two completed updates; sample text and font encoding define the expected bytes | PORT: exact six glyph bytes and chronological positive score increments of17 |
| Lcdc_HasBgObjAndWindowEnabled | LCD/BG/OBJ/window enable bits are hardware-visible display requirements | PORT: host requires LCDC=F3 before observing ongoing scroll/sprite/HUD progress |

These are public game/toolchain contracts. No compiler-owned test is deferred.
The remaining five graphics-demo methods are partial/outstanding: independent
quarter-turn geometry does not yet replace exact sprite/score phase assertions,
and the short observation window does not prove a60-iteration palette toggle.
The new full-map Fill/Clear checks strengthen hardware coverage but do not close
the separate scattered-span/DMG-drip source methods without their own audit.
See [presentation decisions and limits](presentation.md).

## OAM DMA test replacements

Both OamDmaTrigger methods in `CilSpritePaletteHardwareTests.cs` are PORT to the
raw-register branch of `driver.rs::oam_dma_runs_from_relocated_hram_and_preserves_banks`.
Six original assertion sites expand to488 checks across3 executions: frontend/
verifier, FF46 readback, completed bus lock, all160 bytes, and Debug/Release
equivalence. Native tests use independent expected bytes in both configurations
on both targets and preserve exact source/guard checks. The CPU deliberately
stops at the raw trigger while the existing DMA device is advanced separately;
that probe does not claim safe continued ROM execution during DMA.

The blocking intrinsic additionally executes from relocated HRAM, including
FFFE at its final byte, with exact OAM, heap, bank and stack expectations. A
short-wait mutation must fail, and metadata/IR cases reject invalid contracts.
All cases are Rust/nextest; no compiler test is deferred. Other methods in the
source group remain outstanding. See [OAM contract and evidence](oam-dma.md).

## String test replacements

All7 methods in `Frontends/CilStringLiteralTests.cs` (8 Arguments attributes,
22 assertion sites) map to Rust driver string execution/metadata cases and
String/optimizer IR tests. Four PORT the independently justified order/length/
foreach/call contracts; three REPLACE internal ASCII blob assumptions and the
unjustified Unicode rejection. Exact UTF-16 pool bytes, independent ordered heap
output, actual Text.Draw/DrawToWindow tile output and null/bounds traps strengthen
the contract. See [strings](strings.md) and the per-method frontend matrix.
These tests are discoverable by nextest; no compiler-owned case is deferred.

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

### LCD-on tile loading

`CilTileSetTests.Load_LcdOn_ChunksAcrossVblanksAndNeverWritesDuringMode3` is now
**PORT complete**, discoverable through the four existing
`driver::actual_tileset_load_copies_vram_and_preserves_banks` cases. They retain
the original32 distinct bytes1..32 at tile7, add Release and CGB aligned/unaligned
paths, guard every attempted CPU write with the existing emulator's mode-3 hook,
and require each GDMA burst to fit the remaining vblank dots. Every byte and
poisoned bank is checked. A1920-byte CGB case proves multiple bounded bursts.
This public hardware contract is independently justified by PPU bus ownership
and exact supplied data, rather than legacy code shape or compiler output.
The initial native version failed these guards; the runtime ordering/burst fix
and final proof scopes are in [LCD-on timing](lcd-on-transfers.md). One additional
legacy method is replaced behaviorally; its C# definition remains temporarily.

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

### Unmanaged aggregate coverage

The new native layout/IR/backend and real Roslyn fixtures are described in
[aggregates.md](aggregates.md). Discoverable targets are `layout`, `ir`, `sm83`
and `driver`; C# sources are fixture inputs, not compiler test implementations.

| Source group | Requirement and independent justification | Disposition and native target |
| --- | --- | --- |
| `CilStructTests.StructFieldRoundTrip_WholeCopyDoesNotAlias` | C# value assignment copies storage; later mutation must not alias. | PORT: `driver::aggregate_csharp_values_and_native_fields_preserve_banks`, nested/explicit value copies and unchanged original fields. |
| `CilStructTests.ByValStructParameter_CalleeMutationNotVisibleToCaller` | A by-value parameter has independent storage. | PORT: same driver, `Change(Packet)` mutates four scalar widths and returns a distinct value while the caller's original remains unchanged. |
| `CilStructTests.RefStructParameter_MutatesCallersStorage` | Byrefs alias original storage. | Partial PORT: actual Sprite implicit-this/out methods and overlapping `Copy(ref Packet,ref Packet)`; retain pending whole-source retirement audit. |
| `CilStructTests.NestedStructsAndArraysOfStructs_FieldsRoundTrip` | Nested fields and aggregate array elements retain identity. | PORT completed: nested fields and aggregate arrays now execute; see the completed nested/value-array group below. |
| `CilStructReturnTests` | Value results, chaining, recursion, generic structs and concrete enumerators. | Partial PORT: ordinary factories, calls, nested/static results implemented; remaining shapes still outstanding. |
| `CilSpritesTests`, `CilSpritesCgbTests`, `CilSpritePaletteHardwareTests` | Real sprite rendering, CGB attributes and hardware state. | Partial PORT: actual handle methods and exact shadow storage implemented; OAM flush, rendered sprites and palette scenarios remain outstanding. |
| Legacy layout/private IR assumptions | Preserve language layouts, ABI and diagnostics rather than class identity. | REPLACE: bounded metadata mutations, canonical typed descriptors, field bounds/provenance, escape rejection and machine execution. |

Both complete value-copy replacements preserve successful frontend lowering,
verified IR, and independently expected Debug/Release execution. Their old BGP
verdict channel is replaced by a return checksum plus whole-memory guards;
register I/O is independently covered by the existing hardware fixtures.
Final verified counts are recorded in progress.md after the complete run.
No generic/array/sprite group is marked complete from a smaller passing subset.

### Value constructors

`driver::aggregate_csharp_values_and_native_fields_preserve_banks` now also
executes real nongeneric value construction in Debug/Release × DMG/CGB. The
fixture checks default versus explicit parameterless construction, scalar/ref/
value arguments, chaining, static initialization, recursive factories, repeated
calls, padding and nested readonly addresses. This is a partial PORT of the
factory/instance/recursive portions of `CilStructReturnTests`; generic returns,
enumerators and remaining whole-source expectations are still outstanding.
No additional complete legacy method is claimed from this increment.

`driver::value_constructor_metadata_and_private_storage_diagnose` adds two
rstest cases with12 explicit rejection probes: malformed flags, nonconstructor
target, attempted access to compiler-private locals, direct readonly stores,
generic construction and class construction. These REPLACE implementation-shape
assumptions with metadata/ABI isolation and visible diagnostics, independently
justified by ECMA-335 constructor/field semantics and the documented subset.
They are discoverable by cargo-nextest; no compiler-owned test is deferred.

Constructor progress also exposed diagnostic fixtures that assumed newobj itself
was unsupported. Keep their original exception-allocation/missing-reference
checks; add direct-throw variants for unsupported opcode and module-initializer
body execution. Numeric opcode diagnostics for unresolved calls remain checked.
This replaces incidental instruction ordering with separate reference, execution
and output-preservation assertions; it does not weaken rejection behavior.
The prior full run's four diagnostic failures all pass the final17-case focused
regression. Clean corrected-head full/hosted validation remains pending.

### Completed nested/value-array replacement

| Original method | Requirement and independent justification | Decision and native location |
| --- | --- | --- |
| `Frontends/CilStructTests.NestedStructsAndArraysOfStructs_FieldsRoundTrip` | Nested Line/Point fields and a three-element Point array retain field values, zero-initialize untouched fields, and produce BGP28 from1+2+3+4+5+6+7. ECMA value/array semantics and SM83 MMIO justify the result independently. | PORT: `driver::aggregate_arrays_copy_alias_scale_and_preserve_banks`, `StructArrays/Program.cs::NestedValues`, Debug/Release × DMG/CGB. |

The original method expands to six assertions (frontend errors/verifier in both
configurations and two expected BGP results). Native compilation requires clean
lowering and verified IR, then the real ROM writes/reads BGP28 and returns its
independent checksum. Whole-bank snapshots additionally check count and every
Point field byte, untouched memory and CGB bank2; successful return checks SP and
guards. All four Rust cases are discoverable by nextest. The old definition stays
only until the legacy compiler test project is removed.

The coherent array group also adds48 observable ROM scenarios across the four
Rust cases: value snapshots/ref mutation, odd3/5-byte strides,64-byte calls/copies,
alignment, pins, full capacity and explicit null/bounds/overflow faults. Nine new
IR cases check aggregate array identity, canonical sizes, pin identity and rejected
aggregate RVA initialization; one additional CPU case checks a zero-offset null
field. These partly replace broader legacy storage/ABI assumptions without
claiming those larger groups complete. No compiler coverage is deferred.

At the aggregate-array checkpoint, compiler discovery was515 cases; execution531 ROM scenarios
(436 returns,95 traps), four full-frame comparisons. Eleven of565 complete legacy
replacements,554 still need completion auditing; none physically retired.
