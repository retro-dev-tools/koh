# Optimizer behavior migration

All36 methods in `Ir/OptimizationTests.cs` have native REPLACE dispositions.
Their132 assertion sites comprise21 verifier checks,15 numeric return/store
observations and96 private graph/pass checks. The numeric observations remain;
Changed flags, node identity, named passes, instruction counts and private
allocation representations are replaced with verified executable contracts.
No useful source method is deleted or deferred. The C# definitions remain only
until final compiler-test deletion.

## Native acceptance

`tests/fixtures/OptimizationContracts` is ordinary C# built by Roslyn in Debug
and Release. The native `tests/sm83.rs` test
`optimization_preserves_arithmetic_aliases_and_effects` lowers actual CIL before
optimization. For28 method shapes and12 boundary input pairs it checks runtime
arguments and explicitly bound IR constants, both before and after production
optimization. Calls retain their real typed callees; every program is verified,
serialized through canonical ELF, linked by Rust and executed in the existing
C# emulator. Independent Rust wrapping arithmetic, casts, signed/unsigned
comparison and truncating division define expected results. Successful runs check
full return buffers, stack pointer and reserved guards; faults check defined codes
and guards. No CGB or cycle improvement is claimed.

There are2688 real-CIL ROM executions, including64 division-by-zero faults and
96 null-array faults. Unused checked reads still fault. Unused native bus reads
remain explicit memory operations before/after optimization and execute without
depending on the emulator's initial LCD state or the returned bus value. Calls
through an escaped local address change the later read. Distinct locals and
equal indexed pointers retain their independent/aliasing behavior; the indexed
result also observes three neighboring bytes.

Two additional rstest cases use the existing native parallel-phi fixture with a
bounded loop. One returns only the trip count, leaving the two swapping values
as a closed dead cycle. The other stores a cycle value and observes its final
value, including empty loops. Runtime/bound inputs before/after optimization give
40 more ROMs. Thus this increment executes2728 ROMs:2568 successful,160 faults.
It does not require the optimizer to preserve obsolete phi objects or allocate
unused values like the old compiler.

The old repeated/unused-load fixtures read uninitialized allocas, and one call
fixture supplies a callee without a terminator. Those private shapes do not
establish memory or call semantics. Replacements initialize storage, use actual
bus/checked reads and valid callees, then observe the effects. CIL byte arithmetic
uses int32 intermediates with explicit truncation/sign extension; native tests
retain the expected low-width results without copying legacy I8 evaluation-stack
conventions. The new byte-local case explicitly wraps after addition.

Reachability is independently tested in the same real assembly: `InlineLeaf`
includes only itself and `AddThree`; selecting the otherwise unreachable `Dead`
method produces a diagnostic. Existing transitive-reference tests retain broader
assembly-resolution coverage. This replaces a private function-list mutation
assertion without silently accepting unsupported dead code when selected.

## Source-test disposition

Every row is REPLACE, complete and discoverable by cargo-nextest. The native
locations are the tests/fixture above, `tests/support/optimization.rs` for the
independent oracle, and existing native checks explicitly named below.

| Source method | Behavioral requirement / independent basis | Native acceptance |
| --- | --- | --- |
| `ConstantFolding_FoldsIntegerArithmetic` | Fixed-width addition;20+22=42 | `Add`, runtime/bound inputs before/after optimization |
| `ConstantFolding_WrapsToOperandWidth` | Byte truncation;200+100→44 | `ByteAdd`, including full input/high-bit boundaries |
| `ConstantFolding_SignedAndUnsignedDivideDiffer` | Signed interpretation and truncation toward zero;F8/2→-4 or124 | `SignedDivide`/`UnsignedDivide`, including their full normalized returns |
| `ConstantFolding_DoesNotFoldDivideByZero` | Constant propagation must preserve arithmetic faults | Both divide shapes retain fault2 for runtime and bound-zero divisors |
| `ConstantFolding_FoldsComparisons` | Signed-1<0 is true; unsigned255<0 is false | `SignedCompare`/`UnsignedCompare`, exact0/1 results |
| `ConstantFolding_FoldsConversions` | Low-byte52, zero-extended200, sign-extended-56 | `Convert` packs all three independent observations into64 bits |
| `ConstantFolding_AppliesIdentities` | Add-zero, multiply-zero, all-ones AND, self-XOR, shift-zero, divide-one | `Identities` packs six byte results, including both zero observations |
| `DeadCodeElimination_RemovesUnusedChain` | Pure unused calculations cannot change the returned input | `DeadChain`, full-width input/return observations and guards |
| `DeadCodeElimination_KeepsUnusedLoad` | Reads with bus/fault effects cannot disappear merely because their result is unused | `UnusedBusRead` retained memory effect plus `UnusedCheckedLoad` null fault4 |
| `DeadCodeElimination_KeepsStoresAndCalls` | A called mutation remains observable | `Escaped`, with a valid `Change` callee and before/after local observations |
| `DeadCodeElimination_SweepsClosedDeadPhiCycle` | Closed unused phi values cannot change loop control/results | `optimization_preserves_dead_and_store_reachable_phi_cycles(false)` |
| `DeadCodeElimination_KeepsPhiCycleReachableFromStore` | Transitive values feeding an observed store remain correct | Same native case with `stored=true`, full values and odd/even/empty loops |
| `Optimize_FoldsAndEliminatesCascade` | Constant cascade `(1+2)*10=30` | `Cascade`, runtime/bound inputs;1/2 pair retains30 |
| `Optimize_RewritesPhiIncomingThroughRauw` | Value substitution through CFG edges preserves5 | `FoldedJoin` with bound true inputs; existing verified parallel-phi execution covers loop backedges |
| `Optimize_LeavesNonConstantCodeIntact` | Runtime operands retain modular addition | `Add`, full-width runtime vectors before/after optimization |
| `RedundantLoadElimination_ForwardsStoredValueToLoad` | Initialized local reads return their stored value | `ScalarLocal`/`RepeatedLoads`; existing native local-forwarding verifier cases |
| `RedundantLoadElimination_CollapsesRepeatedLoads` | Repeated unchanged reads retain their values | `RepeatedLoads`, initialized int32 storage, full wrapping sum |
| `RedundantLoadElimination_DoesNotForwardThroughEscapingAlloca` | Aliased writes invalidate earlier stored/read values | `IndexedAlias` and `Escaped`; both actual pointer stores and a call mutate observed storage |
| `DeadStoreElimination_RemovesStoresToWriteOnlyAlloca` | Unobserved local writes cannot change return values or unrelated storage | `WriteOnly`, full input return and guards; no private deletion-count contract |
| `DeadStoreElimination_KeepsStoresToLoadedAlloca` | An initialized value consumed by a later read remains correct | `RepeatedLoads` and `Escaped` before/after optimization |
| `SimplifyCfg_FoldsConstantBranchAndDropsUnreachableBlock` | Constant branches select the correct successor | Bound `Join`/`FoldedJoin`, with both branch alternatives |
| `SimplifyCfg_PrunesPhiIncomingFromDeadEdge` | A dead incoming edge cannot select an unrelated value | Bound `FoldedJoin` retains7 on its zero path and5 otherwise |
| `Optimize_PromotesScalarLocalToDirectDataflow` | Byte-local addition preserves explicit truncation | `ScalarLocal`, including byte overflow and high input bits |
| `StrengthReduction_MultiplyByPowerOfTwoBecomesShift` | Multiplication by8 retains low16 bits | `MultiplyPower`, with carries and wrap boundaries |
| `StrengthReduction_UnsignedDivideByPowerOfTwoBecomesShift` | Unsigned16-bit division by4 | `UnsignedPower`, including the high unsigned half |
| `StrengthReduction_UnsignedRemainderByPowerOfTwoBecomesMask` | Unsigned byte remainder by8 | `UnsignedRemainder`, including255→7 |
| `StrengthReduction_LeavesNonPowersAlone` | Byte multiplication by3 retains modular results | `MultiplyOther`; no required instruction choice |
| `StrengthReduction_SignedDivideByPositivePowerOfTwoBecomesBiasedShift` | Negative quotients round toward zero | `SignedPower`, including-1, -8 and-128 |
| `StrengthReduction_LeavesSignedDivideByNegativePowerBoundaryAlone` | Negative minimum byte denominator is not a positive power | `NegativePower`, including-128/-128→1 and positive/negative numerators yielding0 |
| `LocalCse_DeduplicatesIdenticalExpressions` | Repeated arithmetic retains its modular sum | `Duplicate`, runtime/bound full-width inputs |
| `LocalCse_DeduplicatesGepWithEqualConstantIndex` | Equal computed addresses alias; later writes win | `IndexedAlias`, full four-byte packet including neighbors |
| `LocalCse_KeepsDistinctAllocasAndLoads` | Separate storage stays independent; initialized repeated reads are sound | `Distinct` plus `RepeatedLoads`; no uninitialized-value oracle |
| `Mem2Reg_PromotesLocalAcrossIfElseIntoPhi` | Both branch stores select1/2 at the join | `Join`, including byte values above/below10 and constant-bound branches |
| `TrivialPhiElimination_ReplacesSingleIncomingPhi` | A value through a simplified join remains7 | Zero-path bound `FoldedJoin`; existing scalar/parallel-phi native tests retain general joins |
| `Inlining_SplicesLeafCalleeAndDropsTheCall` | Calling a leaf with argument substitution returns `x+3` | `InlineLeaf` with valid `AddThree`, before/after optimization; no mandatory inlining/private node reuse |
| `Optimize_RemovesUnreachableFunctions` | Only transitively reachable definitions enter the program | Exact two-function `InlineLeaf` closure; explicitly selecting `Dead` diagnoses |

## Verification

The expanded library/IR/optimizer selection passed629/629 in34.831s, exit0,
with162 explicitly unselected cases (`/tmp/koh-optimization-final.log`). It
includes complete library/IR targets and neighboring comparison, conversion and
loop contracts. Final byte-local normalization and execution/fault-count guards
passed4/4 in19.240s, exit0 (`/tmp/koh-optimization-final-guards.log`). Source counts
are recorded in `/tmp/koh-optimization-source-assertions.log`. These are focused
checks; full-workspace/platform acceptance remains outstanding.
Final explicit member/IL diagnostic assertions passed2/2 in18.779s, exit0
(`/tmp/koh-optimization-diagnostics-final.log`). Workspace discovery finds1891
cases,1714 compiler cases, zero ignored (`/tmp/koh-optimization-discovery.json`).
Rustfmt, CSharpier and diff checks passed.
