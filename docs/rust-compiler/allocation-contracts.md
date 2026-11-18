# Allocation-independent backend contracts

This batch closes23 source methods: all20 in `Sm83BackendTests`, the one
`Sm83EmitterTests` method, and two of three `Sm83OpsTests` methods. Their53
assertion sites are reviewed below:7 methods PORT,16 REPLACE. The separate
[128-bit acceptance matrix](int128.md#source-test-disposition) now closes the
remaining helper method and all six assertions. Nothing is physically retired
or deferred.

## Decisions and independent expectations

The native ABI uses dynamic stack frames and deterministic spill slots. Correct
values, distinct live storage, balanced calls and source locations are contracts.
Specific E/HL/L assignments, static C000 addresses, two-slot limits, residency
toggles and emitter class decomposition are not game-facing requirements.
Recreating those mechanisms would add a second compiler model without improving
the product. Removed private assertions are identified in the matrix.

`sm83::allocation_choices_preserve_arithmetic_and_live_producer_graphs` uses
typed graphs from `tests/support/allocation_contracts.rs`. Each input runs in
parameter and constant forms before/after optimization. The four width cases
contain14/16/18/22 vectors (280 ROMs). They retain all original numeric results,
add real cross-byte carry/borrow boundaries and full-width wrapping, and preserve
both positive0102030405060708 and negative all-ones64-bit constant patterns.

| Source method | Original sites | Native requirement/replacement | Decision |
|---|---:|---|---|
| `Emits_ExpectedBytes_ForConstantFolding` | 1 | `(3+4)+100=107`; replace E/static-slot instruction bytes with execution before/after optimization. | REPLACE |
| `Residency_AssignsShortLivedByteValueToRegister` | 4 | Same107 graph; remove register/slot membership and E choice. | REPLACE |
| `Residency_IsDisabledByDefault` | 1 | Same107 graph; remove a retired allocation-option default. | REPLACE |
| `Residency_ComputesCorrectResultThroughRegister` | 1 | `10+20+5+100=135` through dependent values. | PORT |
| `Residency_SkipsValueUsedByNonGentleOp` | 1 | `(3+4)<<1=14`; remove register exclusion, retain the clobbering operation's dataflow. | REPLACE |
| `Residency_AssignsSixteenBitValueToHL` | 2 | `(300+40)+100=440`; remove HL/no-slot membership. | REPLACE |
| `Residency_ComputesCorrectSixteenBitResultThroughHL` | 1 | Full16-bit440; observe the new return-buffer ABI. | PORT |
| `Residency_GivesSimultaneouslyLiveValuesDistinctRegisters` | 3 | `(3+4)+(5+6)=18`; keep both producers live, remove register count/inequality. | REPLACE |
| `Residency_CoalescesChainIntoOneRegister` | 4 | `10+20+5+7+0=42`; remove three E assignments. | REPLACE |
| `Residency_CoalescesSixteenBitChainInHL` | 3 | `300+40+100+200=640`; remove two HL assignments. | REPLACE |
| `Residency_UsesHalfOfHLUnderBytePressure` | 2 | Four independent live sums produce51; remove L membership. | REPLACE |
| `Residency_ReceivesLeafParametersInRegisters` | 5 | Distinct runtime arguments pass through an actual caller/callee; remove register/no-slot assignments. | REPLACE |
| `Residency_ParameterPassingIsOffWithoutOptIn` | 3 | Same actual caller/callee ABI; remove the retired opt-in and WRAM-slot assignments. | REPLACE |
| `Residency_RejectsParameterWhenEntryIsLoopHeader` | 2 | Live function arguments survive bounded header backedges and shifts; replace allocator eligibility and the old entry-header IR shape. | REPLACE |
| `Residency_LeafCallPassesArgsInRegisters` | 1 | Caller passes40/2 to callee and receives42; new byte wrap255+1=0. | PORT |
| `Allocation_ReusesSlotsForNonOverlappingValues` | 1 | Ten dependent increments produce10, plus a wrapping input; remove the C# ABI's two-byte frame limit. | REPLACE |
| `LineMap_MapsInstructionsToSourceLines` | 3 | Existing real-PDB `driver::pdb::source_ranges_survive_elf_linking_and_preserve_rom_execution` checks exact supplied file/lines in ELF and linked mappings, and produces KDBG. Replace manually attached game.cs10/11 with Roslyn's actual mapped source coordinates. | PORT |
| `Runs_ConstantChain_InEmulator` | 1 | `(3+4)+100=107`. | PORT |
| `Runs_RegisterPathSubtraction_InEmulator` | 1 | `(10+20)-(5+6)=19`, both sums simultaneously live. | PORT |
| `Runs_BitwiseOps_InEmulator` | 1 | `((F0|0F)&3C)^03=3F`. | PORT |
| `ArithmeticEmitter_LowersI8Add` | 2 | Actual caller/callee byte addition42 and wrap0, plus carry vectors; remove eleven-byte/prefix and private emitter construction assertions. | REPLACE |
| `AluOpcodes_UseCarryFormForHighBytes` | 7 | Add/subtract across byte boundaries through64 bits; bitwise expression3F. Replace helper-specific immediate/register opcode selection with values requiring correct carry/borrow propagation. | REPLACE |
| `Normalize_SwapsAndSignsPredicates` | 3 | Execute unsigned greater, signed less-or-equal and equality across16/32/64 bits, including negative/positive ordering and equal operands. Remove the normalization tuple. | REPLACE |

Arithmetic, modular width and independently specified comparison truth values
justify these expectations; legacy output is not an oracle. Every executable
graph is verified, emitted as canonical ELF, linked with Rust and run in the
existing C# emulator. The harness checks the full returned value, SP and memory
guards. Register allocation choices cannot stand in for those observations.

## Additional behavior and IR invariants

`scalar_leaf_call_passes_byte_arguments_and_full_return_buffer` adds four ROMs,
covering two runtime argument pairs before/after optimization. Both caller and
callee are compiler-emitted functions using the documented stack ABI.

`loop_backedges_preserve_arguments_across_clobbering_operations` adds eight ROMs:
input127 returns128 after one/three iterations;3 returns12 after four;255 wraps
to0 after two. The argument remains live beside a loop counter and shifts.
The new IR has a dedicated entry block and disallows incoming entry edges.
An exploratory direct entry-backedge graph was correctly rejected by verification;
the fixture now uses a preheader and real header block. Existing
`ir::malformed_ir_is_diagnostic` includes the entry-edge rejection. The old
allocator's private eligibility shape is not a mandate to weaken that invariant.

`predicate_normalization_preserves_order_signedness_and_equality` adds168 ROMs:
14 truth vectors at three widths, constant/runtime inputs before/after optimization.
Signed less-or-equal is represented coherently as not-greater in the new IR.
Total new executions:460 ROMs in nine rstest/test cases. Existing liveness,
comparison, signed-widening and real-PDB cases are reused and rerun.

## 128-bit behavior

`Sm83OpsTests.ByteOf_SignExtendsBeyond64Bits` is now **REPLACE complete**.
The source audit found real legacy Int128/UInt128 mappings and operator lowering
in `CilMethodLowerer.cs`, plus real C# cases in `CilLiteralTests.cs`. Consequently
its six assertions required native128-bit behavior; calling them private-only
would have hidden a useful capability. `PositiveExtend` and `NegativeOne` now
check all16 bytes through real C# and executed ROMs, including all six original
observations. See the [source matrix](int128.md#source-test-disposition).
The UInt128 mention in `Koh.GameBoy/SoftFloat.cs` is only a stale comment, not
evidence of current usage. The native128-bit implementation replaces this
requirement without treating64-bit coverage as sufficient proof.

## Verification

The final selection, including case-count guards and15 existing malformed-IR
cases, passed40/40 in14.775s, exit0
(`/tmp/koh-allocation-contracts-acceptance.log`). Source cross-checks count
20/1/3 methods and41/2/16 assertion sites in the three source files; six sites
remain pending with the128-bit method.

```sh
cargo +stable nextest run -p koh-compile --test sm83 --test driver --test ir --locked --no-fail-fast -E 'test(allocation_choices_) | test(scalar_leaf_call_) | test(predicate_normalization_) | test(loop_backedges_) | test(comparison_branches_) | test(recursive_signed_widening_) | test(source_ranges_survive_) | test(live_values_survive_) | test(malformed_ir_is_diagnostic)'
```
