# Accumulator recursion optimization

## Decision and scope

The depth500 sum cannot fit its existing native frames in WRAM0. Expanding into
WRAM1 would consume game heap and require a banked-stack/debug/interrupt ABI.
Replacing the whole calling convention is unnecessary for a pure scalar sum.
Use a direct IR optimization for a restricted associative recursion shape; leave
other recursion on the existing checked stack. This changes neither object format
nor ABI and does not establish general depth500 support for arbitrary functions.

Recognize one self-call site, integer-only parameters/locals/results, and a
straight continuation that returns a normalized addition of that result and a
scalar already available before the call. Only nontrapping integer operations,
comparisons, conversions and initialized local storage may appear in the function.
Exclude addresses, memory, allocation, other calls, division/remainder and faults.
Every return must use the same normalization, or the full integer domain.

For normalization N modulo2^w followed by the selected zero/sign extension,
N(N(a)+b)=N(a+b). Addition is associative and commutative in that domain. Therefore
accumulating pre-call contributions and combining the base result preserves the
returned value. Inputs and contributions are evaluated before the loop backedge;
the original entry initializes locals on every iteration. A fresh entry initializes
the accumulator once. Preserve original source offsets and verify the resulting
CFG, types, dominance and use/definition invariants before accepting it.
The fresh entry also conservatively initializes old scalar locals;
the original entry still assigns their original values each iteration. The IR's
[definite-assignment rule](local-assignment.md) no longer requires these additional
entry stores. Moved
continuation constants must already have the return type. Mixed-width widening
is left recursive rather than erasing its source width.

Resource stack overflow may disappear for recognized recursion; it is not an
arithmetic exception. No observable memory operations or language faults may be
reordered. General recursion, Fibonacci and effectful functions remain unchanged.

## Implementation and validation plan

1. Retain the red actual Roslyn Sum(500) Debug/Release ROM tests (observed both
   fail in `/tmp/koh-recursion-ir-red.log`, exit100).
2. Add parameterized IR eligibility/exclusion tests for widths, normalization,
   incompatible base cases, faulting operations, aliasing and multiple calls.
3. Implement a small internal pass after current CFG/local simplification; build
   a candidate, remove unreachable continuation blocks, and verify before mutation.
4. Execute depth500 and independent signed/wide integer programs in the existing
   emulator; retain Fibonacci/mutual and bank/frame/debug regressions.
5. Record limitations, update dispositions only after successful observable
   execution, commit and push to the same draft PR55.

## Evidence

Original Sum(500) now returns66 on Debug/Release × DMG/CGB. Additional Roslyn
fixtures cover signed8, unsigned16, wrapping32 and both halves of64-bit addition.
Seventeen IR cases check normalization, entry initialization, mixed widths,
incompatible bases, subtraction, division, addresses, memory, other/multiple
calls and fault terminators. SM83 execution checks original/optimized modular
results and the signed-widening exclusion. No old compiler oracle or emulator
core change is involved.

The previous native `Calls::Deep` test expected a pure Sum(10000) to overflow;
the new optimization instead makes that a long-running loop. Stack-guard coverage
now uses the non-associative recurrence F(n)=n−F(n−1): depth10 independently
returns5, while depth10000 still traps before crossing WRAM0 bounds. CLI and
direct backend paths retain both assertions. This replaces an incidental cost
assumption, not a required language fault or useful success case.
