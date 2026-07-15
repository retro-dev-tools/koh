# General loop-carried register residency (beating the old frontend)

## Context

The CIL frontend is functionally at parity with the deleted `CSharpFrontend`, but four
perf/render tests are over budget (`Copy_MarginalCostPerByte` 886 vs 360, `TwoPointerCopyLoop`
795 vs 740, both `Demo_RunOnHardwareRendersARealCube`). All four share one root cause and it is
**perf, not correctness** — the ROMs produce correct output, just too slowly.

Root cause: the SM83 backend keeps loop-carried values in registers via two hand-matched
*shape recognizers* — Layer-1 (a byte induction variable held **in a WRAM alloca**) and Layer-2
(a pointer walk held **in a WRAM alloca**), both in `Sm83FunctionAllocation.cs`. They were
calibrated to `CSharpFrontend`'s IR. CIL-lowered IR is equivalent but shaped differently, so the
recognizers miss and loop values round-trip WRAM every iteration (~2–3× slower). Two narrow
recognizer-widenings already landed (a `trunc(zext(x))` peephole and a bitcast-pointer admission);
this doc replaces the remaining shape-matching with **one general mechanism**.

Directive: implement general/universal optimizations, use the four tests as the benchmark, and aim
to **beat** the old frontend's numbers — not merely match them.

## Design: uniform SSA + width-agnostic loop-carried phi residency

Two moves, replacing both ad-hoc layers with one rule.

### 1. Uniform SSA promotion (`Mem2RegPass`)

Promote **all** register-sized scalar allocas, not just integers — add `IrTypeKind.Pointer`
(register-sized: 16-bit on SM83). Every loop-carried local (counter *and* walking pointer) then
arrives at the backend as a phi. `Reaching` already synthesizes a correctly-typed zero for a
read-before-write slot (a null pointer for a pointer type), and Roslyn's definite-assignment rule
means valid CIL never reaches it. Arrays/structs stay in memory (not register-sized).

*This move alone regresses* (measured): a promoted pointer becomes a **wide phi**, and today a wide
phi is realized as a byte-by-byte WRAM parallel copy (`EmitPhiCopies`), which is *more* memory
traffic than the alloca round-trip it replaced. Move 2 is what makes move 1 a win.

### 2. Generalize the residency overlay to any-width loop-carried phi

Today (`Sm83FunctionAllocation.SelectLoopInductionResidents`): a loop-carried **i8** phi whose
back-edge value is a gentle ALU op gets a byte register; the phi keeps its WRAM slot (dual
placement) and `EmitPhiCopies`' back-edge copy becomes a no-op because the phi and its back-edge
value share the register. Generalize this overlay to width 2:

- Accept a loop-carried phi of size 2 (i16 / pointer), assigning a **register pair** (`HL` or `DE`)
  from the loop pool instead of a single byte register. `HL` is preferred for a phi whose in-loop
  use is a dereference (fuses to `ld a,(hl+)` / `ld (hl+),a`); `DE` otherwise (needs an explicit
  `inc de`). This subsumes Layer-2, whose whole purpose was exactly this HL/DE pointer assignment —
  but now keyed on the SSA phi, not a memory load/gep/store-back shape.
- `EmitPhiCopies`: a resident phi's edge copy is a no-op / register step for *any* width, not just
  i8 (the wide-phi byte-by-byte WRAM path runs only for a non-resident wide phi).
- A resident pointer phi's in-loop dereference fuses with the post-increment addressing mode, the
  same fusion Layer-2 does today — but sourced from the phi's register pair.
- The phi's WRAM slot is refreshed as a side effect (a resident value's store path already writes
  its slot), so any read of the value after the loop still works with no exit-edge sync — the same
  property Layer-2 already relies on.

The result: a copy loop's `src`, `dst`, and byte counter are three loop-carried phis competing for
registers; `src`→`HL`, `dst`→`DE`, counter→a byte reg yields the tightest SM83 copy
(`ld a,(hl+)` / `ld (de),a` / `inc de`), which is how the target is *beaten*, not just met.

### Why this is safe to attempt (perf, not soundness)

Every ROM is already correct. A residency change that fails to fire leaves a value in WRAM — slow,
never wrong. The one unsafe move is changing IR shape for all programs (move 1), guarded by the
full suite. The register-pair assignment must respect the existing interference rule (a wide
result/phi must not partially overlap a source mid-copy — see the `ComputeInterference` /
wide-phi-interference note at `Sm83FunctionAllocation.cs:1238,1308`).

## Execution (incremental, benchmark-gated)

Each step keeps the full suite green (`dotnet msbuild build.proj -t:Test`) and is measured against
the four benchmark tests; a step that regresses any ceiling is reverted, not accepted.

1. Generalize `EmitPhiCopies` so a resident phi of width 2 is a no-op/register step (currently only
   i8). No behavior change yet — nothing produces resident wide phis.
2. Extend `SelectLoopInductionResidents` to admit a size-2 loop-carried phi and assign an `HL`/`DE`
   pair; port Layer-2's deref/post-increment fusion to source from the phi's pair. Retire Layer-2's
   memory-shape recognizer once the phi path covers its cases.
3. Enable pointer promotion in `Mem2RegPass` (move 1). Re-measure: the two-pointer loop and
   `Mem.Copy` should now hold `HL`/`DE` and drop toward / below the old ceilings.
4. Confirm the two cube render-budget tests pass (they are downstream: a fast `Mem.Copy` lands
   rendered content inside the fixed frame window).
5. Sweep the other loop-codegen tests for any new over-budget case (the same general mechanism
   should help, not just the benchmark four) and confirm none of the ~2180 suite regressed.

## Acceptance

- `Copy_MarginalCostPerByte` ≤ 360 (target: beat ~302), `Copy_CostPerByte` ≤ 1000,
  `CountedConstAddrStoreLoop` ≤ 190, `TwoPointerCopyLoop` ≤ 740 — none of these ceilings raised.
- Both `Demo_RunOnHardwareRendersARealCube` render ≥ 2 shades.
- Full suite green; no ceiling in `Sm83LoopCodegenTests` / `MemRuntimeTests` loosened.
- `samples/gb-2048-cs` and `samples/gb-3d` build and pass their verify harnesses.
