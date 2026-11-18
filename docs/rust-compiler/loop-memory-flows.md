# Pointer and multi-loop memory contracts

Eleven remaining behavioral methods in `Sm83/Sm83LoopCodegenTests.cs` now have
native PORT replacements in ordinary Roslyn `RewriteContracts` methods, with
independent results and exact physical access traces in `support/rewrites.rs`
and the `sm83.rs` rstest runner.

| Source method | Fixture | Original behavior retained |
| --- | --- | --- |
| `PointerCopyLoop_RegressionSafe` | `FlowCopy` | Sixteen bytes0..15 from C100 to C200 |
| `SinglePointerFillLoop` | `FlowFill` | Twenty AA bytes at C100 |
| `PointerUsedAfterLoopExit` | `FlowFillExit` | Ten AA bytes and FF at final C10A pointer |
| `NonQualifyingMiddleInstruction_NegativeAdmission_StillCorrect` | `FlowCall`, `IncrementOne` | Sixteen bytes1..16 despite calls inside the copy |
| `OverlappingRegionCopy_ForwardSemanticsPreserved` | `FlowOverlap` | Offset2/count4 produces10,20,10,20 at C102..C105 |
| `TwoSequentialI8CountedLoops_DistinctCounters` | `FlowCounters` | Independent10/20 at C100/C200 |
| `TwoI8CountedLoopsInIfElseBranches` | `FlowCounterChoice` | False arm produces20, with true arm also exercised |
| `TwoSequentialFillLoops_DistinctBuffers` | `FlowFills` | Ten11 bytes at C100 and ten22 at C200 |
| `TwoLoopsInIfElseBranches` | `FlowCopyChoice` | False arm copies1..10 from C200 to C210 |
| `TwoSequentialCopyLoops_DistinctBuffers` | `FlowCopies` | Original two ten-byte copies to C200/C400 with distinct0/100 prefixes |
| `CopyLoopFollowedByFillLoop` | `FlowCopyFill` | Ten copied bytes0..9 at C200 and ten33 bytes at C300 |

The eleven methods contain19 lexical assertion sites. Their public contract is
ordered memory effects and final values, not private register residency or
admission predicates. The same source-level assignments and control flow justify
the expectations independently of either compiler.

The branch selector uses observed WRAM C020 instead of the test wrapper's C000
output protocol. Both arms are now initialized deterministically: the old
pointer-choice test only asserted its false arm and left the true source
uninitialized. Adding a known80 prefix there establishes useful independent
acceptance rather than preserving an unspecified initial-memory assumption.
Distinct pointer loops remain textually separate inside each multi-loop method.

The overlap oracle maintains a byte array and applies each read/write in order,
so destination-ahead aliases propagate prior writes instead of becoming memmove.
Extra inputs cover zero/one counts, identical pointers, offset1/2/4 overlap,
unequal sequential lengths, both branch arms and32-byte copies/fills. Original
counts/addresses/values all remain. Full returned pointer pairs also check
loop-exit liveness; exact traces verify every byte, extent and access order.

Seventy-eight shape/input pairs run with runtime/bound arguments, before/after
optimization, in Debug/Release:624 added ROMs. The whole rewrite fixture now
executes3072 ROMs. Every variant verifies typed IR/program calls, emits canonical
ELF, links with Rust and executes in the existing C# emulator with result/SP/
guard checks. The observer now includes C400 destinations through C4FF and holds
256 events because the longest new trace has192. Strict full-trace equality is
unchanged. No production compiler, emulator, API or timing assertions change.

Only the two original timing-ceiling methods remain open in this20-method group;
neither correctness replacement is credited as passing190/740-dot performance.

Validation: initial2/2 passed40.566s; the final matrix explicitly restores the
original10/20 counter pair and includes32/32 copies. Compiler-library/rewrites/
immediate/predicate/cost selection233/233 passed48.951s, exit0,207 explicitly
unselected (`/tmp/koh-loop-flows-final.log`).

```sh
KOH_DOTNET=/path/to/dotnet cargo nextest run -p koh-compile \
  --lib --test sm83 --locked \
  -E 'binary(koh_compile) | test(machine_rewrites_preserve) | test(terminal_comparisons) | test(literal_alu) | test(real_loop_cost)'
```
