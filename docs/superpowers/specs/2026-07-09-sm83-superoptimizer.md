# SM83 bounded superoptimizer (emulator-oracle equivalence)

Status: **proof-of-concept landed** (oracle + bounded search + tests, in the test project).

## Motivation

The SM83 optimization review names a bounded superoptimizer for short, loop-free
sequences as the clearest greenfield opportunity for Koh — and notes the usual
blocker is needing "an SM83 semantics/emulator oracle for equivalence checking."
Koh already ships that oracle: the `GameBoySystem` emulator, already used as the
end-to-end test harness. That is the piece most projects would have to build first.

## What landed

In `tests/Koh.Compiler.Tests/Superopt/` (test project, because it drives the
emulator — see "Home" below):

- **`Sm83Oracle`** — a concrete-execution equivalence oracle. It bakes a byte
  sequence into a minimal ROM-only cartridge at the code entry, sets the register
  state, single-steps until control leaves the sequence, and reads the state back.
  `AreEquivalent(a, b, live)` runs both over a batch of randomized inputs and
  compares only the live-out registers/flags. Concrete random testing is
  sound-for-refutation (one disagreeing input disproves equivalence) and a strong
  acceptance filter.
- **`Sm83Superoptimizer`** — a bounded enumerative search: over a small instruction
  alphabet, up to a length bound, return the cheapest candidate (bytes, then cycles)
  the oracle judges equivalent, else the original.

Tests (`SuperoptimizerTests`, 5 cases) show it:
- rediscovers `LD A,0 → XOR A` from first principles when flags are dead;
- **declines** that rewrite when flags are live-out (liveness-respecting);
- shrinks a redundant `LD A,B; LD A,B` to `LD A,B`;
- and that the oracle itself distinguishes the flag-live vs. flag-dead cases.

## Home and productionization

- **Why the test project.** The oracle depends on `Koh.Emulator.Core`. Making
  `Koh.Compiler` depend on the emulator would invert the compiler→emulator layering,
  so the PoC lives with the other emulator-driven tests. A production tool belongs in
  a dedicated `tools/Koh.Superopt` project that references the emulator (and, once
  item #1 lands, the MIR layer) without touching the compiler's dependencies.
- **Next steps:** drive candidate generation and the cost model from the MIR layer
  (item #1) instead of a curated alphabet; fold in memory/`(HL)` effects; and follow
  the random-input filter with an exhaustive small-window or symbolic check before
  accepting a rewrite. It can also verify LLM-proposed rewrites offline, sidestepping
  the "no SM83 training corpus" problem the review raises.
