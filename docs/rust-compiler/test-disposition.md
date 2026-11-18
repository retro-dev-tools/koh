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

**Complete dispositions:481/565 methods (480 native behavioral replacements,
one obsolete private-parser DELETE decision); zero physically retired.**
All19 [narrowing methods](narrowing.md) now have native replacements for73 source
assertion sites, with3224 executed ROMs and independently checked escaping widths.
Six [interrupt/vector/dispatch methods](interrupts.md) now have complete native
replacements (11 original assertion sites).
Eleven [comparison/conversion optimizer methods](optimizer-scalar-contracts.md)
now have complete native semantic replacements (43 source assertion sites).
The switch, alignment, tile-copy, initialization and value-copy groups are detailed below.
Six [loop optimization methods](loop-contracts.md) now have native replacements
for23 source assertion sites, with420 real-Roslyn ROM executions.
All36 [general optimizer methods](optimization-semantics.md) now have native
replacements for132 assertion sites, with2728 new ROM executions and explicit
arithmetic/memory faults, aliases and transitive reachability checks.
Six [mapper-state methods](mapper-state.md) now have native replacements for17
assertion sites, with80 physical-bank ROM observations and separate production
banked-call regression coverage.
All38 [peephole methods](rewrite-legality.md) now have native dispositions for43
assertion sites: six independent ISA lengths and37 private rewrite predicates,
replaced by1536 traced ROM executions and valid control/ABI boundaries.
Four [instruction/input-boundary methods](instruction-boundaries.md) now have native
replacements for15 source assertion sites,512 SM83 encoding vectors and every
proper truncation of218 catalog CIL encodings. The other15 [machine-effect methods](machine-effects.md)
now have replacements for66 source assertion sites using13952 independent instruction
probes; all19 methods in that source group are complete.
Five [encoding/parity methods](encoding-contracts.md) now replace34 assertion sites
with33 named ISA vectors, independent512-encoding boundaries and14224 executable
instruction probes.
Five [indexed-address methods](indexed-addresses.md) now have native replacements
for7 source assertion sites, with320 added real-Roslyn ROM executions and exact
physical-address traces across calls, page boundaries and signed offsets.
Five [pointer-phi/walk methods](pointer-walks.md) now have native replacements for19
lexical assertion sites, with192 added ROMs checking both incoming bases, every
loaded/stored byte and final pointer/counter values. The stale backend copy rows
were reconciled with this matrix without counting those already-completed methods again.
Three [guarded/sequential copy methods](guarded-copies.md) now have native
replacements for six source assertion sites, with112 added ROM executions.
Six [loop exit/saved-value methods](loop-exit-values.md) now have native
replacements for six source assertion sites, with288 added ROM executions.
The [counted-store baseline](loop-costs.md) adds one native PORT disposition;
the separate190/740-dot performance requirements remain open.
Eleven [pointer/multi-loop methods](loop-memory-flows.md) now have native
replacements for19 source assertion sites, with624 added ROM executions.
All five [background/window map methods](bg-win-maps.md) now have native
replacements for50 source assertion sites, with16 real-library ROM executions.
The remaining 84 methods still have
partial coverage or outstanding dependencies. Legacy definitions temporarily
remain with the live C# compiler; the final migration deletes that test project.
Proposed Rust filenames in source audits are not implemented or discoverable
merely because they are documented.

All six `CilLiteralTests` methods now have native replacements: the four
[array/UTF-8 methods](literals.md#source-test-disposition) add16 audited assertion
sites, mutable array isolation, readonly ROM views and checked lengths. The
two128-bit methods are mapped separately below. No literal-only test stays in TUnit.

The23 [allocation-independent backend methods](allocation-contracts.md) now have
complete native replacements:20 backend methods, one emitter method and two
arithmetic/predicate helper methods. Their53 assertion sites are reviewed;
private register/slot/byte-shape assertions are replaced by actual behavior.
Nine new cases execute460 ROMs; existing liveness and real-PDB coverage is reused.
The separate [128-bit matrix](int128.md#source-test-disposition) closes the helper
and both frontend arithmetic methods:15 assertion sites, full16-byte values,
Debug/Release, real CLR oracle, reference/array/aggregate storage and fault guards.

The first34 [scalar control-flow methods](control-flow-scalars.md) now preserve
all37 numeric assertion sites through parameter/constant inputs before/after
optimization, with stronger carry/borrow/sign boundaries and live producer
chains. Three Rust width cases execute188 ROMs. The remaining20
[memory/call/boot/phi methods](control-flow-memory.md) now account for all30
assertion sites through real C# execution, physical ROM data, relocated headers
and explicit parallel-copy graphs. This closes the54-method source group.

All three [symbol-address methods](symbol-addresses.md) now have complete native
acceptance: actual instruction/relocation addresses, relocated startup/header
entry and mutable WRAM symbols. Four rstest configurations execute16 ROMs with
two placements and optional PDBs; seven original assertion sites are accounted
for without retaining fixed C000 or Main/header equality assumptions.

Five additional IR construction/verifier methods now have complete native
replacements, including deterministic canonical objects, malformed IR and36
executed loop ROMs. See the [source-by-source matrix](ir-construction.md).
The seven-method/20-assertion [IR round-trip matrix](ir-roundtrip.md) additionally
replaces useful data/entry/bitcast behavior and records the obsolete parser case.
All four [live-value preservation methods](liveness.md) now have native execution
replacements, covering15 original assertion sites and186 before/after ROMs.
The remaining five [referenced CGB HAL methods](cgb-hal.md) now have native
replacements, closing all seven original HAL methods/15 assertion sites with
the earlier speed-switch coverage.

## Floating negation replacements

Two `CilFloatTests` methods now have native replacements (1 PORT/1 REPLACE),
including all three original finite inputs in Debug/Release and both cartridge
modes. Special values extend the numeric coverage; bitwise negation replaces
the obsolete required-helper-name assertion. See the [per-method matrix](float-lowering.md#source-test-dispositions).
Three more comparison/branch/float-join methods have PORT replacements preserving
all36 expanded original numeric checks. Eighteen operand pairs, all five value
opcodes and every short/long relational branch encoding add NaN, signed-zero,
infinity and negative-ordering coverage; see the [comparison matrix](float-lowering.md#ordered-and-unordered-comparisons).
Four additional arithmetic/addition-routing/Debug-Release/remainder-diagnostic
methods now have replacements (3PORT/1REPLACE), preserving all9 arithmetic rows
and42 expanded original assertions. The [division matrix](softfloat-division.md#additional-source-dispositions)
also accounts for the9 completed float methods as34 original parameter-expanded
cases and211 explicit expanded assertions. Two additional32-bit conversion methods
now have PORT replacements retaining all8 original input rows and64 expanded
assertions. The [conversion matrix](float-conversions.md#evidence-and-disposition)
accounts for11 completed float methods,42 original parameter-expanded cases and
275 expanded assertions. The [binary64 comparison replacement](binary64.md#native-tests-and-source-disposition)
adds1 PORT method,4 original parameter rows and32 expanded assertions. The float
group now accounts for22/22 methods,74 original cases and504 expanded assertions
after binary64 arithmetic, precision and integer casts and the previously
covered binary32 byref case are fully traced; see the
[precision-cast/byref matrix](binary64.md#precision-casts).
Integer64/binary32 width coverage is also implemented without inventing legacy
closures. The [remainder implementation](floating-remainder.md) retains the
missing-key contract through explicit provider removal and adds numerical cases. Checked casts and bare internal-F shapes remain unsupported; completing this source group does not imply general .NET support.

## Software runtime replacements

All3 CilKohRuntimeTests and20 SoftFloatDifferentialTests methods are complete:
22PORT/1REPLACE. Four native cases execute24 banked AddF32 ROMs with six independent
finite IEEE vectors, full results and actual MMIO. Eight Rust-driven managed
library groups preserve all85 original rows and145 assertions. The C# host prints
raw results only; no assertions or compiler algorithms remain there. This does
not establish float/double CIL lowering or runtime-key dispatch. See the
[method/group matrix and domain distinctions](softfloat-bits-tests.md).

## Recursion replacement status

All six `CilRecursionTests` methods are PORT complete: real Roslyn Fib(10)=55,
mutual parity(10/7) and exact Sum(500)=66 independently execute across
Debug/Release × DMG/CGB. Additional signed/wide entries strengthen the group to32
ROMs checking full returns, MMIO and frame/bank/heap guards. A restricted pure
modular accumulator optimization corrects the depth failure, without a smaller
depth or fault substitution. See the [per-method matrix and proof](recursion-tests.md).

## Basic CIL replacement

All 12 `CilLoweringTests` methods are complete: ten PORT observable contracts and
two REPLACE private IR assumptions with default-entry/MMIO execution and typed
stack-join invariants. Four native configurations execute 40 ROMs, checking 464
ordered packet bytes and 160 actual MMIO bytes. Both ternary outcomes, signed
narrowing, high-bit unsigned comparisons, real ldlen and source-aware unavailable
runtime diagnostics are checked. See the [per-method matrix and assertion audit](cil-basics.md).
No compiler-owned case is deferred or physically removed yet.

## Struct return replacement

All five `CilStructReturnTests` methods are PORT complete in
`driver::struct_returns::struct_results_preserve_ordered_fields_and_frame_independence`.
Factories, chaining, copies, static stores, recursion, generic returns and pattern
enumeration preserve all22 original value comparisons. Four rstest configurations
execute20 ROMs and compare39 full-width scalar values per configuration, with
bank/frame/source checks. See the [per-method matrix and assertion accounting](struct-returns.md).
No source definition is physically removed or compiler-owned case deferred.

## Generic method replacement

Two `CilGenericTests` methods are REPLACE complete in native
`driver::generics::generic_method_instances_execute_transitively`: transitive
byte/ushort specialization and independently justified build-mode equivalence.
Private mangled-name assertions become actual BGP130, typed recursion/storage,
reference-DLL reachability, original source mappings and fresh/reused resolver
determinism. Four rstest configurations now execute48 ROMs, including closed
constraints and named
struct/class/enum/interface/delegate arguments with deliberately matching metadata
rows across separate assemblies. The remaining four arithmetic methods are now
complete in `driver::wide_arithmetic::legacy_generic_wide_expressions_match_complete_results`:
three PORT expression/extension contracts and one REPLACE build-mode-equality
assertion with independent expected bytes for both modes. See the
[per-method rationale and unfinished generic shapes](generic-methods.md).
Additional [rectangular-array generic coverage](generic-rectangles.md) extends
the same driver with cross-assembly element binding, aliases, null defaults,
byref swaps/clears and rank diagnostics. It does not claim new completed source
dispositions or change the198/565 ledger.

## Wide arithmetic replacement

All14 `CilWideArithmeticTests` methods are complete: seven PORT numeric contracts
and seven REPLACE unordered/equality-only assertions with ordered independently
expected packets. All639 original vectors and33 parameter-expanded executions
are covered by two rstest cases, each comparing actual Roslyn Debug/Release.
The expanded matrix checks9100 ordered values and48 faults across148 DMG/CGB ROM
executions. See the [per-method disposition and semantic correction](wide-arithmetic.md).
No legacy definitions are physically removed in this increment and no test is
deferred; compiler-owned C# sources still await final whole-project deletion.

## Interface replacement

`CilVirtualDispatchTests.UntraceableInterfaceCall_IsDiagnostic` is REPLACE complete.
Its former exception records a compiler limitation, not independently justified
CIL behavior. Nextest discovers four `driver::interfaces` Debug/Release × DMG/CGB
cases and two metadata cases. Each configuration executes30 successful entries
and two faults; metadata cases additionally execute three patched bindings.
This totals134 native ROM probes across configurations, not134 distinct language
rules. The old byte-return/static-reload fixture now succeeds and the external
emulator host directly checks FF42=07/FF43=EE. Matching intrinsic write/read code
alone cannot satisfy that assertion. Wider cases cover independent/explicit/
inherited mappings, interfaces in fields/arrays/delegates, aggregate and64-bit
calls, cast/isinst, null/type faults and bank/frame preservation.

Fifteen malformed metadata probes per Roslyn mode preserve useful rejection
coverage. Two unimplemented generic/boxed shapes still diagnose with
caller/IL context. Valid inherited MethodDef/MemberRef bodies execute42 even where
the tested CLR rejects them; a future CLR accepting them is permitted by the test.
The class-slot alias must return42 on both hosts. This replaces a legacy-only
negative expectation with stronger public behavior and diagnostic assertions.
The default-interface extension adds101 native ROM probes without changing the
legacy method count. Generic/static-virtual/boxing work remains open.
See [contract and remaining work](interfaces.md).

## JRPG replacements

All four `Samples/GbJrpgTests.cs` methods now have complete native
replacements in `tests/sdk/game.rs::sdk_jrpg_dialogue_and_battle_preserve_hero_state`.
Both rstest Debug/Release cases are discoverable by nextest and pass in the PR
worktree. The final edited selection and broader regression are quantified below.
See [scope](jrpg-acceptance.md).

| Source method | Independent/public requirement | Decision and replacement |
| --- | --- | --- |
| Sample_CompilesUnmodified_NoDiagnostics_ValidIr | Real C# sample and PNG-generated Art compile in both Roslyn modes | REPLACE complete: SDK builds actual copied sources/assets, native compiler verifies IR and emits canonical ELF; round-trip ELF and ROM execution replace C# verifier classes |
| Sample_RomHeader_IsCgbExclusive | Explicit CGB-only cartridge intent reaches header byte0143=C0 | PORT complete: SDK property forwarding and independently relinked compiler output both produce identical ROM with exact C0 flag |
| Sample_CgbPlaythrough_WalksTalksAndCallsBack | Timely title, HUD, terrain/NPC art, four Hero OAM slots, movement without residue, authored palettes, dialogue and callback return | PORT complete: same SDK rstest plus `tests/sdk/jrpg.rs`; independent source PNG/palette/bitplane/pixel checks, exact four-frame movement,300-frame power-on title, and seeded battle strengthen legacy observations |
| Sample_CaptureAcceptanceScreenshots | Bounded scene arrival, four actual PNG files and no tracked screenshot mutations | REPLACE complete: unique persistent TestResults/gb-jrpg artifacts, standard PNG decode equals raw framebuffer, scene/art invariants, source file-set and byte equality; no golden-image oracle |

Final focused regression passes42/42 in478.485s, exit0; final edited JRPG/HUD
selection passes9/9 in56.031s, exit0. Captures of all four scenes were inspected.
No legacy source is physically retired, no sample asset is modified and no public
API is made a permanent convention. Seed setup changes entropy, not encounter
code. Unsupported/untested branches remain explicit in the acceptance document.

## Monochrome tile expansion replacements

Both source methods in `Frontends/CilTileSetTests.cs` are PORT complete:
`Load1bpp_ExpandsSetBitsToInkClearBitsToPaper` and
`Load1bpp_MixedInkPaper_ProducesCorrectPlaneMasks`. The nextest-discoverable
`driver::mono::mono_tiles_preserve_ink_paper_wrap_and_selected_banks` uses four
rstest Debug/Release × DMG/CGB configurations, each executing five real Roslyn
entries. A per-pixel hardware-plane oracle checks all16 ink/paper pairs across
patterned rows and both whole VRAM banks, including untouched bytes. Additional
checks cover LCDon/off, bank preservation, tile255 wrapping and zero-count/null.
This independently justified public graphics behavior replaces two narrower
legacy mask examples; no private helper shape is retained. Initial4/4 matrix
passes28.122s, exit0; all four also pass in the final42-case regression above.

## Callback replacements

The following 11 source methods now have complete native behavioral replacements,
included in the total above. The final workspace/all-features regression passed
1,193/1,193 in 2,764.108s, zero skipped, exit 0; all 44 frozen source hashes still
match. Separate rustdoc passes for four crates (zero doctests). All targets below
are Rust rstest cases
in `rust/koh-compile/tests/driver/delegates.rs`, discoverable by nextest, using
real Roslyn DLLs in Debug/Release and the existing emulator on DMG/CGB.
No source method is physically retired and no compiler behavior is deferred.

Quantification from the source files: CilDelegateTests has8 methods,8 Arguments
rows and22 Assert.That call sites; CilStoredDelegateTests has3 methods,6 Arguments
rows and2 shared-helper Assert.That call sites. These24 call sites are not24
independent scenarios: helpers, loops and parameterization expand execution.
The11-method matrix below is the completion unit; literal6/13 or verdict-register
values are replaced by independently specified arithmetic outcomes and bounded
ROM completion. The private `__heap` global-name assertion, direct-call IR
assertion are intentionally removed. Slot/blob terminology in test names and
comments does not become a native ABI requirement.
They do not constrain correct closure lifetime, dispatch or returned values.

| Source method in Frontends | Behavioral contract and independent expectation | Decision and native trace |
| --- | --- | --- |
| CilDelegateTests.NoCaptureLambda_VerifiesCleanAndInvokesResolvedTarget | Noncapturing callback computes5+1=6 | PORT: `delegates_preserve_stored_targets_captures_and_virtual_binding`, FunctionTargets |
| CilDelegateTests.NoCaptureLambda_DebugAndReleaseProduceIdenticalObservableState | Both IL configurations produce the independently known value, rather than merely agreeing | REPLACE: same FunctionTargets endpoint; exact result checked separately in each configuration |
| CilDelegateTests.CapturingLambda_VerifiesCleanAndInvokesResolvedTarget | Returned closure retains39 and computes39+3=42 after its factory returns | PORT: same native target, ReturnedAndArray; mutable capture additionally exercised by CapturedTarget |
| CilDelegateTests.CapturingLambda_DebugAndReleaseProduceIdenticalObservableState | Capture lifetime and mutation preserve independently expected values in each build mode | REPLACE: ReturnedAndArray and CapturedTarget; remove equality-only oracle |
| CilDelegateTests.DelegateThroughParameter_LowersViaBlobDispatch | Passing a callback preserves target, capture and scalar return | REPLACE: FunctionTargets invokes captured and noncaptured functions through ApplyFunction; blob representation is private |
| CilDelegateTests.CallvirtOnSealedDeclaringType_DevirtualizesToDirectCall | Sealed override executes and computes42 | REPLACE: `sealed_virtual_tokens_execute_the_override`, explicit sealed callvirt token; direct-call IR shape is private |
| CilDelegateTests.VirtualMethodGroupDelegate_IsDiagnosedNotSilentlyMisresolved | Base-typed receiver invokes the derived override, producing42 rather than21 | REPLACE: InstanceTarget; obsolete unsupported diagnostic replaced by actual virtual execution |
| CilDelegateTests.VirtualMethodGroupDelegate_ResolvesToSealedOverrideWhenTokenIsSealed | Explicit sealed ldvirtftn token binds the override and produces42 | PORT: `sealed_virtual_tokens_execute_the_override`, ldvirtftn mutation of actual Roslyn DLL |
| CilStoredDelegateTests.ActionThroughCtorField_TwoTargetsOneField_DispatchPerBlob | Two constructor-stored callbacks retain distinct targets and captures | REPLACE: ConstructorFields checks42,49, then43 after capture mutation; descriptor layout is private |
| CilStoredDelegateTests.FuncThroughParameter_ReturnValuesMergeThroughSlot | Indirect callback results survive parameter passage and control flow | REPLACE: FunctionTargets plus ReturnedAndArray; exact values, normal ABI and bank guards preserve return behavior without requiring the legacy return-slot convention |
| CilStoredDelegateTests.StaticDelegateField_ReassignmentSwitchesTarget | Static field reassignment changes subsequent invocations | PORT: StaticLambdas checks1+10+10=21; StaticTarget independently checks7+35=42 |

Independent basis: C# closure/delegate semantics, ECMA-335 verified method-pointer
construction and SM83 observable memory/register behavior. Every public behavior
above has an exact expected result; private IR classes, devirtualization choices,
blob encoding and merge-slot representation are not contracts. Additional native
coverage checks null binding/invocation, metadata validation, nominal generic
identity, banks257+, wide/byref returns and live frame offsets above127. See
[callback contract and completed focused logs](delegates.md). The full feature
contract, remaining legacy groups and whole migration remain open.

## Rectangular-array replacement

`Frontends/CilMdArrayTests.RectangularArrays_RomFoldHeapCtorAndHeaderPath` maps
to the nextest-discoverable rstest cases in `driver::rectangular`, using actual
Debug/Release Roslyn assemblies and DMG/CGB ROM execution. Row-major initialized
data, parameter reads, heap writes and zero initialization are public language
semantics; private ROM-global/header bytes are legacy implementation details.
The replacement additionally checks mutation through a readonly field's
referent, null/reference-cell flows, empty dimensions, per-axis/truncation and
capacity faults, borrowed element references, wide/reference-bearing values,
System.Array queries and malformed runtime-member signatures. Exact new heap
layout is checked by `primitive_arrays_are_zeroed_checked_and_bank_safe`.

Decision: REPLACE; the obsolete immutable-ROM/private-IR assertion is removed in
favor of mutable-array execution and bank/heap guards. The separate source
method `StringArrays_ReferenceElementsAndLengths` remains outstanding here;
rectangular support does not close the entire source file. No source definitions
are physically retired until final legacy compiler/test deletion.

## 2048 scene-game replacements

The two `Samples/Gb2048V2Tests` methods contain three original scenarios: two
Roslyn configurations for compilation and one Release/DMG gameplay run. They now
map to four nextest-discoverable rstest cases in `sdk::game`, each checking both
compilation and gameplay, plus eight independent Rust rule-reference cases.

| Source method / purpose | Behavioral requirement and independent justification | Contract / decision / native target |
| --- | --- | --- |
| `Sample_CompilesUnmodified_NoDiagnostics_ValidIr` | All actual sample sources compile through ordinary SDK/Roslyn in Debug and Release; native verified compilation emits canonical ELF and a valid ROM | Public authoring/build contract. REPLACE complete: `sdk::game::sdk_2048_scene_game_obeys_rules_and_draws_tiles`, four Debug/Release × DMG/CGB cases. ELF round trip and independent linker invocation reproduce the SDK ROM. Private C# IR types and the old compiler ABI are not preserved. |
| `Sample_BootsToTitle_StartsGame_SlidesSpawnTiles` | Boot to title, Start enters a two-tile board with zero score, eight directional inputs perform valid single-merge slides and one spawn per changed move, HUD and board rendering match state | Public game behavior, independently checked against 2048 rules, literal tile art and target palette colors. REPLACE complete: the same four native cases boot the existing emulator with Koh boot ROMs, inspect typed WRAM state, check exact per-move results/score/spawns and every board pixel at nine captures per configuration. Title/HUD glyphs, uploaded tile bytes, final population and zero mode3 violations are also asserted. |

The observer shim only steps the existing emulator, applies inputs and snapshots
memory/video. All rule logic and acceptance assertions live in Rust. Metadata
field offsets are an observation adapter, not a promise to retain current private
fields or public framework design. The game sources and art remain unchanged.
Legacy static return slots, byte dispatch tags and ROM-folding assumptions are
not acceptance oracles. Win/lose/restart and JRPG behavior are separate coverage;
these two replacements do not close unrelated framework or array tests.

Final current-source SDK cases pass56.818/56.162/19.374/19.293s in the completed
full workspace run, `/tmp/koh-vblank-workspace.log`. The four final bounded-copy
cases also pass, covering6160 phase/count/frame probes. The full workspace
completed1086 tests:1084 passed and two new LCD-off setup tests failed. Both now
explicitly disable the emulator's initially enabled LCD, preserve fault9
assertions, and pass2/2 in `/tmp/koh-vblank-lcd-off-corrected.log`. Production
code stayed unchanged. This is not a green result for the original full command.

The [reference-class layout foundation](class-layout.md) supplies25 native
metadata cases within the48-case layout target, plus the existing10 reference
cases in its58-case regression. Executable object-core tests now close two class
methods below; dispatch and the real class games remain required.

## Reference-object replacements

`driver::native_object_identity_fields_and_faults` is discoverable by nextest:
four rstest configurations execute15 Roslyn programs each (Debug/Release ×
DMG/CGB,60 native ROM probes). Two entries port the following two legacy methods
and their four Roslyn configuration scenarios. Their language observations are
independently justified by ECMA-335 reference identity, field and call semantics;
their target packets and untouched bank/arena bytes are asserted directly by
Rust against existing-emulator execution. Old padding, tag size and SCX/SCY
completion conventions are implementation details, replaced by stronger memory
and checked-return observations. Other object entries improve coverage but do
not close unrelated legacy methods automatically.

| Source method / purpose | Public requirement and independent evidence | Decision / native target |
| --- | --- | --- |
| `Frontends/CilClassStringFieldTests.StringCtorParameter_StoredInClassField_ReadBackViaLengthAndIndexer` | Two distinct constructor string/ushort arguments survive field storage and later length, character and score reads | PORT complete: `ObjectCore::BannerFields`, exact packet includes8/9 lengths, Y/W/G/O characters and2048/512 scores; distinct object/string references and unchanged heap/bank2 are also checked |
| `Frontends/CilVirtualDispatchTests.InheritedFields_UsePrefixLayout_NoOverlap` | Base/derived fields do not overlap; a base alias observes and mutates the same object | PORT complete: `ObjectCore::InheritedFields`, exact42/5000/3/700 before packet, base42/5000 view and77/3 after packet, plus final heap layout and descriptor ancestry |

Final broader object regression passes376/376 in338.797s, exit0 (99 unrelated
cases filtered). After scoped C# formatting and the final class-shape verifier
check, all277 focused IR/object/static/constructor cases pass in40.210s, exit0
(126 unrelated driver cases filtered). Logs: `/tmp/koh-object-final-regression.log`
and `/tmp/koh-object-postformat.log`. Raw-to-reference loads and incompatible
static-cell aliases discovered during review have explicit negative coverage.
No compiler-owned test is deferred; the C# test project remains pending final
whole-migration deletion.

## Class virtual-call replacement

| Source method / purpose | Independently justified requirement | Decision / native evidence |
| --- | --- | --- |
| `Frontends/CilVirtualDispatchTests.VirtualCalls_TagDispatchThroughBaseTypedStatic` | Runtime receiver chooses among three overrides after static reload/reassignment; inherited implementation, parameter/return flow and loop state remain correct | PORT complete: `driver::virtual_slots_and_constructor_dispatch_execute`, `VirtualCalls::Main`, four Debug/Release × DMG/CGB cases. Rust checks individual Area/Kind packets18/227,12/130,4/163, including unsigned byte widening; managed functions and dispatchers occupy separate ROM banks, including257+. No dependency on legacy byte tags or private dispatch IR. |

This maps one source method/two Roslyn configuration scenarios to four native
cases. The same cases execute eight other entries (36 ROM probes total) covering
newslot/base calls, constructor-time zero fields, null, transitive allocation,
casts and64-bit/byref signatures. Two malformed-override cases and one generated
work-budget case add independent diagnostics. Final selected283/283 checks pass
in43.495s, exit0. Full workspace run1045/1047 passed with only two stale forged-
framework diagnostic assertions failing; their strengthened replacements pass in
that selected run. See [exact evidence and limitations](virtual-dispatch.md).
This closes no interface, scene-transition or interrupt method automatically.

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

The other8 methods now have complete native replacements below. All10 methods
in this group are mapped; nothing is DEFERred or physically deleted yet.

## Runtime copy replacements

The remaining8 `Sm83/MemRuntimeTests.cs` methods have8 assertion call sites,
including a300-iteration byte check. The native cases replace unordered vector
equivalence with exact byte positions and whole-bank guards. Expected memory
comes from fixed inputs and ordered-copy semantics, not legacy compiler output.

| Source method | Behavioral requirement and independent justification | Decision and native evidence |
| --- | --- | --- |
| Frontend_ProducesVerifiableIr | Allocation, copy and fill compose through valid lowering; the C# IR/verifier API is incidental | REPLACE: `driver::memory_copy_contract_aliases_and_bus_wrap`, `CopyFillIntegration`, verified compilation/ELF/linking plus exact ROM memory |
| Copy_CountZero_IsNoOp | Zero count leaves both buffers untouched | PORT: short-count matrix plus external bus trace requiring zero source reads and destination writes |
| Copy_SingleByte | Copy exactly one source byte and preserve surrounding bytes | PORT: `driver::memory_copy_exact_extents_alignment_and_cost`, exact whole-bank snapshots |
| Copy_OddSize | Seven-byte copies preserve the following bytes | PORT: same target includes7 and other odd sizes with16-byte gaps |
| Copy_CrossesA256ByteBoundary | Pointer/count carries must preserve all300 bytes | PORT:255/256/257/300 and3840-byte cases with nonuniform sources, four paired byte offsets and DFFF endpoint |
| Copy_OverlapDestinationBeforeSource_IsDefined | Ordered reads must preserve the defined lower-destination overlap | PORT: exact original seven-byte overlap result `[3,4,5,6,7,8,9,7,8,9]`; add reverse overlap, self-copy and aliased local cases |
| Copy_CostPerByte_IsWithinLooseCeiling | A64-byte copy meets the loose hardware execution budget | REPLACE old body/layout assumptions with64/256/1920 ROM probes; retain64000T ceiling including allocation/call overhead |
| Copy_MarginalCostPerByte_IsWithinLooseCeiling | Count growth must not produce pathological per-byte cost | PORT: retain256→1920 slope and360T/byte ceiling; measured56T/byte in all four configurations |

The copy matrix executes32 short/bulk ROMs; contract/rate tests execute56 ROMs,
including explicit bank2 access, pin restoration, both16-bit bus-wrap directions,
zero accesses and five malformed attributes. Four backend cases execute32 ROMs
for count truncation, large offsets and stack traps. Five typed/effect IR cases
and four additional copy-span/conversion cases are nextest-discoverable. Two
desktop cases execute224 copies each through the ordinary C# library body.
See [contract, measured costs and completed logs](memory-copy.md).

## Cube replacements

`Samples/Cube3dTests.cs` has3 methods,6 parameter-expanded cases and8 assertion
sites. The native `cubes` target supplies12 real double-buffered/full-frame/
racing-beam Debug/Release × DMG/CGB cases,18 independent viewport-validator cases and11
GDMA-window boundary cases. They are discoverable by cargo-nextest, not skipped
or feature-gated. Both variants now pass all four configurations after the
[VBlank correction](vblank-window.md) and [native copy](memory-copy.md).
These3 source methods are fully replaced. Racing-beam additionally has9 adversarial
trace-validator cases and actual per-row SCX checks. The standalone phase sweep
is now Rust-owned: two cases execute768 phase/geometry scenarios each, with24
adversarial frame-validator cases. See its separate [disposition matrix](cube-phase-sweep.md).

| Source method | Behavioral requirement and independent justification | Decision, native target and remaining work |
| --- | --- | --- |
| Demo_CompilesWithoutDiagnostics | Actual shipped sources and referenced library compile; compiler class names and diagnostic containers are private details | REPLACE with Roslyn/native compilation and actual ELF serialization/readback/linking in `cubes::cube_boots_and_animates`; both variants pass |
| Demo_LinksToBootableRom | Cartridge header and startup execute on the supported hardware model | REPLACE weak NOP/JP/two-logo-byte checks with real DMG/CGB boot-ROM unmapping and bounded game execution; both variants pass |
| Demo_RunOnHardwareRendersARealCube | Authored shaded projection is visible and animates; expectations derive from sample geometry/palette and SM83/PPU behavior | PORT with stronger two-capture palette, viewport, size, connectivity, animation and full DMA-window checks; all8 cases pass unchanged frame budgets |

The separate racing-beam behavior now has native execution evidence beyond those
original methods: four real builds pass unchanged budgets and physical SCX/line
traces. `driver::hblank_stream_boundaries_and_hardware_state`, the HBlank Roslyn
fixture and IR/backend cases assert exact stream reads/writes, faults, IME and
bank/stack preservation. See [contract and evidence](raster-stream.md). This
does not change the565-method completion count or defer any compiler coverage.

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
The remaining five methods now have complete PORT replacements covering24
original assertion sites (36 expanded), with exact phase/OAM/palette timelines
through79 completed updates. All four extended Debug/Release × DMG/CGB cases
pass47.303s, exit0,440 other driver cases excluded. This closes all nine showcase
methods. See the [per-method timeline matrix](showcase-timeline.md).
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
| `CilStructReturnTests` | Value results, chaining, recursion, generic methods returning structs and concrete enumerators. | PORT complete for all five methods: full-width ordered result packets and independent expectations; see [matrix](struct-returns.md). |
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
factory/instance/recursive portions of `CilStructReturnTests`. That earlier
increment was partial; the later [struct-return closure](struct-returns.md) now
accounts for all five source methods, including generic returns and enumerators.
Constructor-specific behavior remains separately audited.

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

## Closed declaring-type storage checkpoint

The native closed-type contract adds six Rust driver cases and eleven library
cases (see [evidence](closed-types.md)). They cover actual Roslyn Debug/Release
assemblies, per-instance storage, callback receivers, foreign PDB/KDBG ranges,
nominal identity and bounded substitution/constraint work. This adds native
coverage without closing broader legacy groups prematurely:198/565 dispositions
complete,367 outstanding; no compiler coverage is deferred or physically removed.

## Original 2048 replacements

All16 `Samples/CilGame2048Tests.cs` methods now have complete native replacements
(12 PORT,4 REPLACE), covering all32 original assertion sites. Eight new rstest
cases exercise real SDK game boot/input/pixels and explicit rule edge conditions
in Debug/Release × DMG/CGB. The final combined12/12 run also preserves all four
existing board/stack-buffer cases. See the [per-method matrix](original-2048.md).
Current ledger214/565 complete (213 native,1 obsolete parser DELETE),351
outstanding; zero legacy definitions physically removed. No compiler-owned
behavior is deferred.

## Legacy cube sample replacements

All three `Samples/Cube3dTests.cs` methods now have native replacements
(2 REPLACE/1 PORT), covering eight original assertion sites/16 expanded assertions.
Twelve existing Rust cube cases include all six original expanded cases and
retain both original Release render deadlines. The250-frame full-frame/DMG
gap was reproduced and fixed through compiler/runtime cost reductions, not a
weaker capture deadline. See the [source matrix](cube-phase-sweep.md#legacy-cube-sample-requirements).
All45 inventoried sample/art-integration methods now have native replacements.
This does not complete the other520 methods or overall product acceptance.
Current ledger222/565 complete (221 native,1 obsolete parser DELETE),343
outstanding; zero legacy definitions physically removed.

## Comparison/branch replacements

All seven `Sm83CompareBranchFusionTests` methods now have native replacements
(3 PORT/4 REPLACE), retaining all18 predicate rows and both original multi-use
results. Four rstest width cases execute246 before/after semantic ROMs plus12
timing calls. Nine original assertion sites/26 expanded checks are accounted
for in the [exact matrix](backend-audit.md#backendssm83comparebranchfusiontestscs-7),
including the six retired private set/store/count assertions and their stronger
behavioral replacements. No production compiler change is needed for this group.
Ledger229/565 complete (228 native+1 obsolete parser DELETE),336 outstanding;
no legacy definitions physically removed and no compiler-owned coverage deferred.
