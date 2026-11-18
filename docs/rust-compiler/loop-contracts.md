# Loop optimization contracts

All six methods in `Ir/LoopInvariantCodeMotionTests.cs` have native REPLACE
dispositions. Their23 assertion sites contain five verifier checks and18 private
pass/graph assertions; they contain no executed numeric or timing oracle.
Native tests preserve verified control flow and independently expected behavior,
without requiring a named LICM pass, preheader names, instruction identity or
Changed flags. The current optimizer does not claim load-hoisting alias proofs.

`tests/fixtures/LoopContracts` contains ordinary C# compiled by Roslyn in Debug
and Release. `tests/sm83.rs::loop_optimization_preserves_control_memory_and_dominance`
lowers each actual method before optimization, then checks its original IR and
IR after one and two calls to the production optimizer. All three are verified,
emitted through canonical ELF, linked by Rust and executed in the existing C#
emulator. Full64-bit observations pack the actual trip count above the complete
int32 sum. The existing execution harness checks return bytes, stack pointer on
success, reserved guards and fault codes. No CGB or cycle improvement is claimed.

Five shapes across14 input vectors and three optimization stages, in both Roslyn
modes, give420 ROM executions:408 successful and12 division faults. Inputs cover
empty/negative bounds, conditional bypass, changing induction, positive/negative
flags and seeds including both int32 extrema. The independent Rust oracle uses
wrapping arithmetic and closed-form nested contributions. Loads see a cell that
changes by3 every iteration and an additional final read. Division by zero faults
only for an entered nonempty loop; bypassed/empty loops still return normally.

| Source method | Requirement and independent justification | Native replacement |
| --- | --- | --- |
| `Licm_HoistsInvariantArithmeticIntoPreheader` | Pure invariant arithmetic retains its modular result and loop bound | `Invariant`: runtime `n+n`, repeated `seed+seed`, full sums/trip counts |
| `Licm_DoesNotHoistLoopVariantComputation` | Induction-dependent arithmetic/comparisons change each iteration | `Variant` and `Nested`: live induction, bounds, independent sums/counts |
| `Licm_DoesNotHoistLoads` | Stores through an invariant address change subsequent loads | `Loaded`: actual address-taken local, per-iteration writes and final read; stronger than the original unchanged-load shape |
| `Licm_SplicesPreheaderWhenEntryEdgeIsConditional` | Conditional entry preserves bypass and edge values; newly executed faults are observable | Both flag paths for `Invariant`/`Variant`/`Loaded`, plus `Division` with zero denominator and empty/bypassed loops |
| `Licm_DoesNotOverHoistValueDefinedInSplicedInnerPreheader` | Definitions dominate uses; inner invariants can vary across outer iterations | Valid C# `Nested` with both inner paths and an outer-dependent value observed after the inner loop; existing `ir::malformed_ir_is_diagnostic::nondominating-value` rejects a non-dominating definition |
| `Licm_HoistedResultIsReusedNotRecomputed` | Repeated production optimization preserves verified behavior | All five shapes execute before/after one/two optimizer calls; no private fixed-point flag or unmeasured recomputation/cycle assertion |

The old nested fixture defines `v` in `inner_header`, then uses it in
`after_inner`, which also has an incoming edge that skips `inner_header`.
Its private hoisting expectations cannot justify that missing definition. The
replacement defines the outer-dependent value on every path where it is used,
then verifies and executes both inner-entry alternatives. This corrects the test
premise rather than accepting undefined SSA as compiler behavior. No source
method is deleted or deferred, and the legacy definitions remain until final
compiler-test deletion.

The expanded library/IR/loop/narrowing selection passed595/595 in26.982s, exit0,
with192 explicitly unselected cases (`/tmp/koh-loop-contracts-final.log`). Final
execution-count/fault-count guards passed both Roslyn cases again in9.129s,
exit0 (`/tmp/koh-loop-contracts-reviewed.log`). Source counts are recorded in
`/tmp/koh-loop-source-assertions.log`. Workspace discovery finds1887 cases,
1710 compiler cases, zero ignored (`/tmp/koh-loop-discovery.json`). Rustfmt,
CSharpier and diff checks passed. This is focused acceptance, not a current-head
full workspace or three-platform pass.
