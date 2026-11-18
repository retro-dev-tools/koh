# Memory, calls and control-flow acceptance

The remaining20 methods in `Backends/Sm83ControlFlowTests.cs` contain30
`Assert.That` sites. All20 now have native replacements:17 PORT and3 REPLACE.
Together with the [34 scalar methods](control-flow-scalars.md), this closes the
54-method source group. Nothing is deferred or physically deleted here.

## Source dispositions

`driver::control_flow_memory::memory_calls_and_cartridge_boot_preserve_observable_state`
compiles the real `ControlFlowMemory` C# fixture with Roslyn in Debug/Release,
then executes each entry in DMG/CGB mode. Every run checks the full int32 return,
balanced SP and reserved-memory guards. Nonallocating entries preserve the whole
heap; CGB runs also preserve the other WRAM bank.

| Source method | Independent requirement and native entry | Decision |
|---|---|---|
| `Alloca_Store_Load_RoundTrips` | Address-taken byte storage aliases its local; store42, increment through pointer, read43. `Local`. | PORT |
| `Gep_IndexesArrayElements` | Distinct stack bytes at0/2 retain10/20; sum30. `Indexed`. | PORT |
| `Call_PassesArgs_ReturnsI8` | Three byte arguments10/20/12 return42. `Arguments`. | PORT |
| `Call_ReturnsI16_InHL` | Full ushort result300+250=550 crosses a call. `WordReturn`; HL is a private old ABI detail. | PORT |
| `Call_Nested_ChainOfFrames` | Two nested increments preserve live caller input40 and return42. `Nested`. | PORT |
| `Recursion_IsSupported` | Terminating direct and mutual recursion preserve each pending addend and seed36, returning42. `Recursive`/`Mutual` replace verifier-empty/nonempty-emission assertions. | REPLACE |
| `Switch_SelectsCaseOrDefault` | Inputs1/2/7 select11/22/99. `CaseOne`/`CaseTwo`/`CaseDefault`; existing raw-CIL switch execution also checks actual opcode0x45 and stack edges. | PORT |
| `Header_IsBootableCartridge` | Valid entry transfer, full logo, mapper/mode and independently recomputed checksums. Every entry; fixed0150/ROM-only choices are replaced below. | REPLACE |
| `Boots_FromEntryVector` | PC0100 reaches startup and game code, returning42. `Boot`. | PORT |
| `Mmio_StoreReachesHardwareRegister` | SCY atFF42 reads back the stored byte0x37. `Mmio`; no LCDC constructor assumption. | PORT |
| `Global_RomTable_IndexedRead` | Direct ROM byte-table element2 of[5,10,15,20] is15. `RomByte`; an ELF ROM0 fixture retains physical read-only storage. | PORT |
| `Global_RomI16Constant` | Direct ROM word[E8,03] reads1000 little-endian. `RomWord`. | PORT |
| `Global_WramState_MutatedAcrossCalls` | Two calls share a counter initialized to0 and return2. `Shared`. | PORT |
| `Gep_DynamicIndex_I8Array` | Runtime index2 addresses the third contiguous byte30. `DynamicByte`. | PORT |
| `Gep_DynamicIndex_I16Array_ScalesBySize` | Runtime index1 scales by two and returns200 from[100,200,300]. `DynamicWord`. | PORT |
| `Pointer_Parameter_Dereferences` | A callee dereferences its caller's live byte77. `PointerArgument`. | PORT |
| `Store_ThroughDynamicPointer_RoundTrips` | Runtime index3 stores/reads99; new adjacent-element checks also retain10/20/30. `DynamicStore`. | PORT |
| `PhiSwap_RealizedInParallel` | Two backedges swap1/2 twice and return1. `sm83::parallel_loop_parameters_preserve_mixed_width_cycles` Native case. | PORT |
| `SumLoop_RunsToCompletion` | Sum0..9 is45. Existing `sm83::constructed_sum_loop_executes_before_and_after_optimization` explicitly includes10 at Native/I32/I64 widths. | PORT |
| `PhiCopyCycle_MixedWidth_DoesNotCorruptWiderValue` | Full64-bit cyclic transfers preserve0102030405060708 and1102030405060708 beside an int32 counter. The I64 phi case replaces allocator-specific overlapping slots, unread filler values and an indirect success-count observation. | REPLACE |

All numeric expectations follow arithmetic, aliasing, layout and parallel-edge
semantics; passing legacy output is not their justification. No public game API
or compiler algorithm changes in this increment.

## Stronger observations and intentional replacements

The two ROM entries read fixed-address bytes from an additional canonical ELF
ROM0 data object at3FF0, round-tripped through the shared object library and
placed by the Rust linker. The linked ROM must contain the exact six bytes.
This tests direct byte/word loads from real ROM without retaining the private
C# `IrGlobal` representation. `ByteTable`/`WordTable` additionally compile normal
C# initialized arrays: their physical `.rodata` bytes are checked, then runtime
reads return15/1000. These mutable arrays are not mislabeled as direct ROM loads.

[Pan Docs' cartridge header definition](https://github.com/gbdev/pandocs/blob/master/src/The_Cartridge_Header.md)
independently specifies the logo, mapper/mode fields and checksum algorithms.
The tests check all48 logo bytes and recompute both checksums without calling
linker helpers. Entry bytes point to the actual relocated `__koh_start`;0150 is
not required by hardware. MBC5 (19) follows Koh's banked-call ABI, replacing the
old ROM-only fixture choice. Execution starts at0100 in the existing emulator,
then reaches the compiler exit with the expected result and SP. This validates
post-boot entry, not execution of a hardware boot ROM or physical cartridges.

Explicit typed block-parameter graphs retain actual phi cycles. Native, I32 and
I64 cases each execute0/1/2/3 swaps before/after optimization:24 ROMs. Odd/even
results check both original values, including the changed high byte of the old
64-bit fixture. A sequential overwrite would fail the two-swap result. The
counter is I32 alongside the wide pair; old C# slot placement is not reproduced.
Existing sum graphs cover0/1/2/10/257/400 at three widths before/after
optimization. Existing raw-CIL switch cases include full-width/default selectors,
duplicate targets, an empty table, a loop and retained evaluation-stack values.

## Verification

Seven new rstest cases execute84 real-C# ROMs plus24 explicit phi ROMs. The
focused selection also reruns three existing sum cases and one raw-CIL switch
case. All11 passed in18.752s, exit0, after CSharpier's final whitespace pass
(`/tmp/koh-control-flow-memory-formatted.log`). Workspace discovery lists1,835
cases, including1,658 compiler cases with zero ignored; all seven new cases are
present. The existing cubes target reports an unused `build_host` helper warning.
The source audit independently
counted54 methods and30 assertion sites in the remaining20. Initial test code
used the wrong integer type for the linker's signed symbol address; correcting
that comparison changed no compiler behavior or expected value.

```sh
cargo +stable nextest run -p koh-compile --test driver --test sm83 --locked --no-fail-fast -E 'test(control_flow_memory::) | test(parallel_loop_parameters_) | test(constructed_sum_loop_) | test(cil_switch_executes_)'
```

This closes one test-disposition group. It does not complete the full compiler
migration, establish cross-platform acceptance, or authorize legacy deletion.
