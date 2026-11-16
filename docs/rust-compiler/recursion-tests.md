# Recursion test migration

All six methods in `Frontends/CilRecursionTests.cs` have complete native ports.
No feature is retired; legacy definitions remain pending full replacement acceptance.

| Source method | Contract and oracle | Disposition / Rust coverage |
| --- | --- | --- |
| DirectRecursion_Fibonacci_ComputesExpectedValue | Two self-calls preserve the first live result; Fib(10)=55 | PORT, complete / `driver::recursion::recursive_cil_preserves_live_values_mutual_calls_and_depth_500` |
| DirectRecursion_Fibonacci_DebugAndReleaseProduceIdenticalObservableState | Both IL shapes independently return55 and write SCX55 | PORT, complete / same four rstest cases |
| MutualRecursion_IsEvenIsOdd_ComputesExpectedValues | IsEven(10)=true and IsEven(7)=false; SCX1/SCY0 | PORT, complete / same cases |
| MutualRecursion_DebugAndReleaseProduceIdenticalObservableState | Both IL shapes independently satisfy parity and actual MMIO | PORT, complete / same cases |
| DeepRecursion_ExercisesRelocatedWramCallStack | Sum(500) succeeds with66 modulo256; storage strategy is not a language contract | PORT, complete / exact `Recursion::Deep` body, accumulator loop |
| DeepRecursion_DebugAndReleaseProduceIdenticalObservableState | Both configurations independently succeed with66 | PORT, complete / same four native cases |

The original six methods contain14 TUnit assertion sites, nine parameter-expanded
executions and24 expanded assertions, all covered by the complete group. Compiler success and IR
verification remain prerequisites to native object emission. The native four cases
run32 ROMs across Debug/Release × DMG/CGB, comparing full I32 returns and actual
SCY/SCX/BGP/WY bytes. Helpers occupy distinct ROM banks; canonical ELF round-trips,
restored SP/frame guards and untouched heap/CGB bank2 are checked. Registers are
explicitly initialized; no emulator constructor default supplies an oracle.

## Corrected depth failure

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

The [accumulator optimization](accumulator-recursion.md) now reduces retained
state for this pure modular shape without changing the ABI or borrowing heap.
The exact original Sum(500) body succeeds with66 in all four configurations.
No smaller depth, expected overflow or ignored test replaces success. General
effectful recursion still uses the guarded stack; arbitrary depth500 is not promised.

Additional depth500 entries independently check signed8=-65, unsigned16=54178,
wrapping I32=8001E932 and both halves of I64=800000000001E932. SM83 hand-IR tests
compare original/optimized execution with modular arithmetic at depths0/17 and
optimized depth500, including full64-bit results. A mixed-width signed-widening
continuation stays recursive and returns0/−1/−3, preserving its constant type.

The initial successful Fibonacci/mutual selection passes4/4 in22.987s, exit0
(`/tmp/koh-recursion-bounded.log`). That increment closed only four contracts.
After removing diagnostic instrumentation and formatting, the final selection
including existing bank-restoration regressions passes8/8 in44.783s, exit0
(`/tmp/koh-recursion-final.log`). Rustfmt and CSharpier checks pass. No production
compiler or emulator code changed in that earlier increment.

The first accumulator IR/actual-Sum selection passes19/19 in24.298s
(`/tmp/koh-accumulator-first-green.log`). Expanded signed/wide fixtures,
exclusions and executable widening regression pass22/22 in34.306s
(`/tmp/koh-accumulator-widths.log`), all exit0. These selected results do not
establish full migration or final cross-platform acceptance.

Final PDB/source/debugger/CLI-stack/backend selection passes14/14 in122.102s,
exit0 (`/tmp/koh-accumulator-debug-stack-final.log`). Optimized Sum retains its own
source ranges through ELF, linked bank addresses and KDBG. Existing source
breakpoint/inspection and corrupt-stack rejection cases also pass. A broader
614-case selection first had597 passes and one obsolete pure-sum overflow
expectation; its corrected backend/CLI tests are included in the final14.
The subsequent full workspace selection includes the remaining16 cases;
its completion must be checked separately.
