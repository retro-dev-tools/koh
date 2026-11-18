# Struct, pointer and switch source contracts

All12 `Frontends/CilStructTests.cs` methods now have native replacements. This
increment closes11 methods:10 PORT decisions and one REPLACE. The nested/value-array
method was already closed in `StructArrays::NestedValues` and is not counted twice.

## Independent semantics

[C# value types](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/value-types)
copy their values on assignment. The Point copy therefore keeps10/20 in the
original and15/20 in its modified copy, with sum65. The fixture checks every field
as well as the sum. [Parameter semantics](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/method-parameters)
make ref writes visible to the caller and by-value writes private to the callee.
The by-value callee now returns its modified sum37 while the caller still has10/20
and sum30, preventing a test that merely omits an unobserved mutation.

Raw byte pointers use valid LCD-off VRAM addresses. The replacement calls the
existing VBlank-safe `Lcd.Off` rather than inheriting the old fixture's immediate
LCDC write during an unspecified scanline. Whole VRAM-bank snapshots retain all
untouched neighbors. Stackalloc bytes1/2/3/4 are explicitly written before reads;
no unspecified stack initialization is made a requirement. Ref byte41 becomes42;
ref Point10/20 becomes17/20. The byte-backed enum comparison retains200.

The old private `SwitchInstruction` expectation is replaced by dynamic dispatch
results. Actual Roslyn Debug/Release helpers contain CIL opcode45; native IR may
represent or optimize that control flow differently. Real MMIO selectors retain
the original chosen case6→66 and default42→77. Two switch tables additionally
cover every case0–9, negative/out-of-range values and int32 extrema with exact
28-byte result packets. No output-node shape, pass order or constant-folding
prohibition is imposed.

## Source disposition matrix

Native target: `driver::struct_contracts::struct_pointer_and_switch_contracts_execute`.

| Source method | Decision/root | Retained behavior |
|---|---|---|
| StructFieldRoundTrip_WholeCopyDoesNotAlias | PORT, Copy | Independent fields and sum65 |
| NestedStructsAndArraysOfStructs_FieldsRoundTrip | Previously PORT complete, existing aggregate-array driver | Nested fields, array bytes and BGP28; no additional credit |
| PointerWriteToVram_ReadBack | PORT, Pointer | Actual8000 byte42 and BGP42 |
| PointerArithmetic_OffsetWriteReadBack | PORT, Offsets | Actual8003/8004 bytes11/22 and BGP51 |
| StackAllocBuffer_RoundTrips | PORT, Stack | Four explicitly initialized bytes and sum10 |
| RefParameter_MutatedByCallee | PORT, RefByte | Caller observes42 |
| RefStructParameter_MutatesCallersStorage | PORT, RefStruct | Caller observes17/20 and sum37 |
| ByValStructParameter_CalleeMutationNotVisibleToCaller | PORT, ByValue | Caller10/20/sum30, callee result37 |
| SwitchDispatch_JumpTable_SelectsCorrectCase | PORT, Selected/SwitchCoverage | Dynamic case6→66 and complete dispatch values |
| SwitchDispatch_LowersToRealSwitchInstruction | REPLACE, same roots and CIL fixture check | Genuine input CIL switch plus executable/default/full-width behavior replaces private C# IR structure |
| SwitchDispatch_OutOfRange_FallsThroughToDefault | PORT, Default/SwitchCoverage | Dynamic42→77, negative/high boundaries |
| Enum_UnderlyingIntegerRoundTrips | PORT, Enum | Byte-backed comparison result200 |

The source has six literal Assert.That sites: four in the shared helper, two in
the private-switch test. Ten newly closed helper-driven methods each expand to
six assertions (both modes' frontend/verifier checks and expected results), plus
four parameterized private-switch assertions:64 original expanded assertions.
Shared call sites are not falsely counted as distinct declarations per fixture.

Four rstest cases compile actual DLLs in Debug/Release and execute11 roots on
DMG/CGB:44 ROMs. Compilation verifies IR; ELF round-trip and Rust linking precede
execution in the existing C# emulator. The same resolver is reused across roots.
Both return values and actual BGP register values are checked. Complete packets,
VRAM/OAM snapshots, alternate WRAM sentinels, restored SP and stack guards extend
the original assertions. No production compiler/game-library/emulator changes.

## Evidence

Focused4/4 passed16.875s, exit0 (`/tmp/koh-struct-contract-first.log`). The final
neighboring21/21 passed54.439s, exit0 (`/tmp/koh-struct-contract-final.log`),842
explicitly unselected. This includes aggregate layouts/copies, static byref aliases,
struct returns, existing real-CIL switch tables and the full-width SM83 switch case.
These are selected Linux results, not full workspace or three-host acceptance.

Ledger547/565 complete (546 native replacements, one obsolete private-parser
DELETE),18 outstanding, zero legacy definitions physically retired.

All-feature discovery reports2131 workspace/1954 compiler cases, zero ignored and
all four new cases (`/tmp/koh-struct-contract-discovery.json`, exit0). Separate
workspace rustdoc (currently zero cases), rustfmt, CSharpier and diff checks passed.
The pre-existing cubes `build_host` warning remains. Discovery is not full execution.

```sh
cargo nextest run -p koh-compile --test driver --test sm83 --locked -E 'test(struct_contracts::) | test(aggregate_csharp_values) | test(aggregate_arrays) | test(struct_returns::) | test(static_fields_and_byref_aliases) | test(roslyn_switch_tables) | test(cil_switch_executes_full_width)'
```
