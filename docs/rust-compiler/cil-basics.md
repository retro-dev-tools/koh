# Basic CIL test replacement

All 12 methods in `tests/Compiler/Koh.Compiler.Tests/Frontends/CilLoweringTests.cs`
have complete native dispositions. The Rust group is
`driver::cil_basics::cil_core_contracts_execute_with_independent_register_and_packet_checks`,
with four rstest configurations. The typed stack-join replacement also uses both
cases of `lowering::cil_stack_join_becomes_typed_block_arguments`.

| Original method | Requirement and independent oracle | Decision / native location |
| --- | --- | --- |
| RegisterLoop_WritesExpectedValue_DebugAndRelease | Five increments produce BGP5 | PORT / default Main → RegisterLoop |
| RegisterLoop_IrShape_HasEntryFunctionAndFixedAddressGlobal | Entry selection and actual FF47 output; no required global representation | REPLACE / default-entry ROM and independent MMIO read |
| RegisterReadThenWrite_RoundTripsThroughFixedAddressGlobal | SCY7 reads back and produces BGP8 | PORT / RegisterReadWrite |
| CallToGameModuleStaticMethod_IsLoweredAsDirectCall | Three calls of AddOne produce3; helpers run across banks | PORT / StaticCall |
| Ternary_NonEmptyStackJoin_IsSpilledCorrectly | Input SCY2 selects9; added SCY6 selects1 | PORT / TernaryLow and TernaryHigh |
| Ternary_Release_ActuallyExercisesSpillAllocas | Correct typed evaluation-stack joins, independent of temporary allocation count | REPLACE / hand-CIL typed I32 join returns7/9, plus both native C# branches |
| ArithmeticAndCompareOpcodes_ProduceExpectedValue | Original arithmetic yields40, −13, −14 and all six predicate bits63 | PORT / Arithmetic |
| UnsignedAndInequalityLoops_ProduceExpectedValue | Five unsigned and four unequal iterations yield9; added high-bit comparisons distinguish unsigned interpretation | PORT / Loops |
| NarrowingConversionOpcodes_ProduceExpectedValue | 300→44;70000→4464;sum8972 gives BGP12; added sign-boundary results −128,−1,65535 | PORT / Conversions |
| UnsupportedExternalCall_ReportsDiagnostic_DoesNotThrow | Unprovided System.Console runtime reports an error with IL/member context instead of silently dropping the call or panicking | PORT / Unsupported error check |
| ArrayLength_LoopBoundSumsAllElements_DebugAndRelease | Real ldlen occurs; five values10..50 sum150 | PORT / ArrayLoop and decoded actual DLL |
| ArrayLength_OnUntraceableArray_ReadsRuntimeLengthPrefix | Parameter-derived lengths3,7,11 and SCX completion markerEE | PORT / ArrayParameters and decoded Len/LenInt |

The baseline contains 12 methods, 22 parameter-expanded executions, 25 TUnit
assertion sites and 47 expanded assertions. Ten methods PORT their behavioral
contracts; two REPLACE private C# IR assumptions. No compiler-owned case is deferred.
No source definition is physically retired before final compiler-project deletion.

## Oracle and mechanism changes

The test-only C# emulator host reports actual SCY/SCX/BGP/WY bytes through
`--native-registers`; all assertions live in Rust. Game code explicitly initializes
these registers, avoiding reliance on emulator constructor defaults. The independent
MMIO locations are FF42, FF43, FF47 and FF4A. No production emulator code changes.

Each configuration executes ten ROMs and checks 29 full-width packet values plus
four actual register bytes per ROM: 40 ROMs, 464 ordered packet bytes and 160 MMIO
bytes across Debug/Release × DMG/CGB. Values and register effects are independently
expected from fixture inputs and integer semantics. Signed arithmetic/conversion
values remain full I32, preventing a low-byte verdict from hiding upper-bit errors.
The readonly field's array elements also remain mutable; historical ROM/WRAM labels
and a length-at-payload−2 layout are not language contracts.

Entry boot execution and observed FF47 output replace entry/global object-shape
assertions. Hand-CIL tests pin a genuine nonempty stack join with a typed I32 block
argument and both outcomes, avoiding an exact Roslyn spill-allocation assumption.
The malformed branch-into-operand rejection is retained. Rust verifies IR before
and after optimization. Native runs require bounded completion, no fault, valid
result pointers, restored SP/frame guards, canonical ELF and untouched CGB bank2.
Console's unavailable assembly remains an actionable reference/runtime diagnostic;
this does not claim general Console support when arbitrary framework code is supplied.

Initial six cases pass6/6 in33.078s (`/tmp/koh-cil-basics-second.log`). The formatted
host/lowering/struct-return regression passes53/53 in62.067s
(`/tmp/koh-cil-basics-final.log`). After explicitly adding default Main discovery,
final6/6 passes27.642s (`/tmp/koh-cil-basics-entry-final.log`), all exit0. These are
selected local results, not completed compiler or cross-platform acceptance.
