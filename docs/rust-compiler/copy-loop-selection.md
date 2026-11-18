# Pointer-copy instruction selection

This increment targets the last outstanding source-audit method,
`Sm83LoopCodegenTests.TwoPointerCopyLoop_MeetsCeiling`. Its contract initializes
32 source bytes, copies them through advancing native pointers, observes every
destination byte and requires at most740 T-cycles per byte. The original method
has two lexical assertion sites, expanding to33 assertions. Its numeric ceiling
is retained.

## Measurement and independent observations

The Rust-driven `RewriteContracts.CopyCost` fixture retains the source
initialization and pointer-copy loops. The test host poisons source and
destination independently before execution. Rust compares all32 source bytes
and all32 destination bytes against literal0..31 expectations and checks three
surrounding sentinels. The host observes memory after execution; no generated CIL
comparison loop supplies the acceptance oracle.

The earlier fixture had a compiled mismatch accumulator after the end marker.
Removing that observer changes total execution cost, not the marked source
window. The measurement-only baseline is58640 total/57780 window T-cycles.
Comparing the old137528 total against the new result would conflate observer
removal with compiler improvement. All following measurements use the corrected
host-side observation boundary.

| Change | Total T-cycles | Marked source window |
|---|---:|---:|
| Measurement-only baseline |58640|57780|
| Native16 local updates |42256|41396|
| Local-read rather than constant-definition barrier |33680|32820|
| Indexed byte store selection |26000|25140|
| Loop-invariant local forwarding |23312|22452|

The final targeted result is728.5 total T-cycles per byte, including startup,
wrapper, markers and return. The permanent Rust assertion uses total execution
cost against740*32, with no estimated overhead subtraction. The existing counted
loop remains18440 total T-cycles for100 iterations against its190-cycle ceiling.

## Selected operations and safety conditions

Native16 local increments/decrements update the low byte and conditionally the
high byte. Increment propagates low-byte wrap; decrement tests the old low byte
for borrow. Both preserve full16-bit wrapping semantics. Selection requires
single-use intermediate values and no intervening effects after the actual
local read. A constant defined earlier does not extend that read/write interval.
Escaping intermediate values and alias writes retain ordinary lowering.

Indexed byte stores recognize a raw native base plus a proven byte-sized index
whose low byte is also the stored value. Constant bases use direct address
formation; local bases retain a stable snapshot across a pure interval. Carry
into the high address byte is explicit. The actual store operand remains live,
so frame-slot reuse cannot replace it with an expired index value. Escaping
addresses, changed local bases and different stored values use general lowering.
Managed-reference checks are not bypassed.

Cross-block local forwarding distinguishes an unvisited predecessor from a
visited predecessor with an unknown value. The fixed point retains a value only
when all reachable predecessors agree on its SSA identity. This exposes unchanged
pointer bases inside loops without inventing entry values or treating changing
backedges as invariant. Address-exposed and pinned locals remain excluded under
the existing private-local memory contract in [the IR specification](ir.md).

## Regression evidence

New parameterized tests are discoverable through cargo-nextest:

- `sm83::native_local_updates_preserve_carry_borrow_aliases_and_large_offsets`:
  16 cases,256 original/optimized ROM observations, including carry, borrow,
  escaping values, alias writes and frame offsets beyond127.
- `sm83::indexed_byte_stores_preserve_pages_values_snapshots_and_escaping_operands`:
  16 cases,192 ROM observations, including page crossings, poisoned targets,
  changed bases, escaping operands and different stored values.
- `ir::loop_local_flow_eliminates_only_invariant_unaliased_pointer_reads`:
  three invariant/changing/aliased cases. The invariant case failed before the
  dataflow fix; the other cases already passed.
- `sm83::loop_local_flow_preserves_initial_values_backedges_and_alias_writes`:
  three cases,72 ROM observations, including zero iterations and16-bit wrap.
- `driver::real_loop_cost_baselines`: actual Roslyn assemblies, external C#
  emulator execution, exact memory and stack observations, and retained timing
  ceilings.

The final targeted run passed41/41 in13.648s, exit0
(`/tmp/koh-copy-flow-first.log`). The pre-fix local-flow run failed exactly the
invariant-elimination assertion:5 passed/1 failed, exit100
(`/tmp/koh-loop-flow-before.log`). The740-cycle assertion was made permanent
after the targeted measurement. Full workspace validation is pending; this
document does not yet close the source disposition or claim migration completion.
