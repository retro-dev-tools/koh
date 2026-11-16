# Recursion test migration

The six methods in `Frontends/CilRecursionTests.cs` split into four completed
behavioral ports and two outstanding success requirements. No feature is retired.

| Source method | Contract and oracle | Disposition / Rust coverage |
| --- | --- | --- |
| DirectRecursion_Fibonacci_ComputesExpectedValue | Two self-calls preserve the first live result; Fib(10)=55 | PORT, complete / `driver::recursion::recursive_cil_preserves_live_values_and_mutual_results` |
| DirectRecursion_Fibonacci_DebugAndReleaseProduceIdenticalObservableState | Both IL shapes independently return55 and write SCX55 | PORT, complete / same four rstest cases |
| MutualRecursion_IsEvenIsOdd_ComputesExpectedValues | IsEven(10)=true and IsEven(7)=false; SCX1/SCY0 | PORT, complete / same cases |
| MutualRecursion_DebugAndReleaseProduceIdenticalObservableState | Both IL shapes independently satisfy parity and actual MMIO | PORT, complete / same cases |
| DeepRecursion_ExercisesRelocatedWramCallStack | Sum(500) succeeds and returns66 modulo256 | PORT, outstanding / retained `Recursion::Deep` reproducer; native stack overflow observed |
| DeepRecursion_DebugAndReleaseProduceIdenticalObservableState | Both configurations succeed with the independently expected66 | PORT, outstanding / same capacity dependency |

The original six methods contain14 TUnit assertion sites, nine parameter-expanded
executions and24 expanded assertions. The four completed methods account for10
sites, six expanded executions and17 expanded assertions. Compiler success and IR
verification remain prerequisites to native object emission. The native four cases
run eight ROMs across Debug/Release × DMG/CGB, comparing full I32 returns and actual
SCY/SCX/BGP/WY bytes. Helpers occupy distinct ROM banks; canonical ELF round-trips,
restored SP/frame guards and untouched heap/CGB bank2 are checked. Registers are
explicitly initialized; no emulator constructor default supplies an oracle.

## Unresolved depth requirement

The exact original byte-returning `Sum(int)` body and depth500 are retained in
`tests/fixtures/Recursion/Program.cs`, entry `Program::Deep`. Initial execution
failed at PC01BB; linked `__koh_abort` was01BA. Fibonacci and mutual entries had
already completed successfully. The reproduced run is recorded in
`/tmp/koh-recursion-diagnose.log` (exit100), not presented as passing acceptance.

Inspection of the generated banked unwind symbols for method06000004 (`Sum`)
shows Debug frame size0031=49 bytes and call-site delta0039=57 bytes
(`/tmp/koh-recursion-banked-layout.log`). Five hundred such frames alone require
24,500 bytes, before call/return storage, exceeding the current C100..CFFF
3,840-byte native stack. The ABI also transports this byte-returning CIL method
as I32 arguments/results. Raising an instruction budget cannot correct capacity.

These two requirements remain open. No smaller-depth test, overflow expectation,
ignored test or changed result replaces them. A correction must reduce retained
recursive state or otherwise implement a justified storage/execution strategy,
with independent live-value, alias, bank, bounds and debug-unwind validation.
An accumulator/loop transformation would additionally need to establish modular
arithmetic equivalence and preserve side effects and fault order; it cannot be
applied to arbitrary recursive functions merely because this sum is associative.

The initial successful Fibonacci/mutual selection passes4/4 in22.987s, exit0
(`/tmp/koh-recursion-bounded.log`). This closes only the four listed contracts.
After removing diagnostic instrumentation and formatting, the final selection
including existing bank-restoration regressions passes8/8 in44.783s, exit0
(`/tmp/koh-recursion-final.log`). Rustfmt and CSharpier checks pass. No production
compiler or emulator code changes in this increment.
