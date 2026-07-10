# Register residency and a register calling convention

Status: **all steps landed (1–5).** The reusable `IrLiveness` analysis, its unification into
`FunctionAllocation`, a conservative register-resident value class (bytes in `C`/`D`/`E`/`H`/`L`, 16-bit
values in `HL`, with chain coalescing), a register calling convention (leaf parameters received in
registers), and bytewise `H:L` allocation are all implemented, emulator-validated, and on by default for
plain (non-recursive, non-interrupt) functions. See the "Staged plan" section below for what each did.

## Why this is the biggest lever

Koh's SM83 backend is, by design, a **memory machine, not a register machine**
(`Sm83Backend.cs`: *"every parameter and every value-producing instruction gets fixed
WRAM storage, and every operation flows through the accumulator … deliberately poor
code"*). `FunctionAllocation` graph-colours **WRAM byte slots**, not CPU registers, and
parameters are passed in WRAM.

That means the single largest item in the SM83 optimization review — Krause/SDCC
**optimal register allocation** — has *nothing to allocate* in Koh today: there are no
register-resident values to be optimal about. Every operation round-trips WRAM through
`A`. Letting short-lived values and loop-carried variables live in `BC`/`DE`/`HL`, and
passing leaf-call arguments in registers, is the change that (a) removes the dominant
source of memory traffic on a register-poor core and (b) makes the review's actual
research applicable.

Two properties make Koh an unusually good fit for that research:

- The C# frontend emits **structured control flow** (no arbitrary `goto`; phis are added
  only by `Mem2RegPass`). Krause's polynomial-time optimality rests on exactly this —
  structured programs have small treewidth.
- `FunctionAllocation` already reasons **byte-wise** (it colours byte ranges and forces a
  wide result to interfere with its own operands), which is halfway to Krause's *bytewise*
  allocation where `HL` is two independent 8-bit slots `H:L`.

## First increment (this PR): shared liveness

A register allocator's required input is **liveness** — which values are simultaneously
live, hence cannot share a register. That analysis already exists in Koh but is trapped
inside `FunctionAllocation.ComputeInterference`: private, filtered to the values it
colours, and fused with backend-specific interference rules.

This PR extracts it as **`Ir/Analysis/IrLiveness.cs`** — a reusable, general SSA liveness
(backward dataflow, phi-edge semantics) over *every* trackable value (instruction results
and parameters), with tests covering straight-line, diamond, and loop-carried cases. It is
the substrate both a future register allocator and a slimmed WRAM colourer build on, and it
is deliberately additive: the correctness-critical backend is untouched in this PR.

## Staged plan for the codegen change

1. **Land the MIR layer (item #1). — done.** Real register allocation needs machine-level liveness
   (registers *and* flags) and a place to rewrite instructions. `MirEffects` provides the footprint.
2. **Unify liveness. — done.** `FunctionAllocation.ComputeInterference` now consumes `IrLiveness`,
   with the backend interference rules (wide-result-vs-operands, wide-phi-vs-incomings) layered on top;
   the inlined dataflow copy is deleted. Byte-identical, guarded by the e2e suite.
3. **Register-resident value class. — done (conservative first cut).** `FunctionAllocation` may assign
   a value a CPU register instead of a WRAM slot. The candidate rule is deliberately narrow so it is
   provably correct on an accumulator machine whose emitters clobber registers freely: a value produced
   by a *gentle* ALU op (`ADD/SUB/AND/OR/XOR`, 1 or 2 bytes) whose entire live range — definition through
   last use, all in one block — contains only other gentle ALU ops. Those emitters touch only `A`/`B`, so
   a byte value parked in `E` or a 16-bit value in the `HL` pair is provably untouched across its range;
   `E` and `HL` are disjoint and are the two registers the gentle path never uses. The interference graph
   is the second guard, applied per physical register, so each holds one live resident at a time (the
   coalescing case — `v2 = v1 + c` — shares the register). Disabled for interrupt handlers and recursive
   functions (their prologue/epilogue register constraints are not yet modelled); wider values (i32/i64)
   stay in WRAM. Emitter changes: `LoadByteToA` sources a resident from its register and `StoreResultByte`
   sinks a producer's result into it. Validated end-to-end on the emulator (`Sm83BackendTests`).

4. **Register calling convention. — done.** A parameter whose uses are all a gentle prefix of the entry
   block is *received* in a register instead of a WRAM param slot: `EmitCall` places the argument in the
   callee's parameter register, and the callee keeps it resident. Safe because an argument is never itself
   a caller-resident (it feeds the non-gentle call), so placing several in distinct registers cannot clobber
   one another; and the far-call thunk preserves `B`–`L` (it only touches `A`/`F`), so this composes with
   ROM banking. Off for the entry function (no caller to set its registers) and, like all residency, for
   recursive/interrupt functions. Validated end-to-end on the emulator (`add(40,2) == 42`).
5. **Bytewise / aliasing-aware allocation. — done.** Two refinements landed together:
   (a) residency interference is now liveness-only (the WRAM colourer's wide-result rule does not apply to
   full-register residents), so chains coalesce — a byte chain collapses to one register and a 16-bit chain
   coalesces in `HL`; and (b) `HL` is modelled as `H:L` (Krause SCOPES 2015): `H` and `L` join the byte
   register pool, so under pressure a byte occupies half the pair while the other half is free. Physical
   aliasing falls out of the `Sm83Register` flags enum — `HL == H | L`, so a conflict is a bitwise-overlap
   test. The byte pool is `E`,`D`,`C`,`L`,`H` (`H`/`L` last, to leave `HL` free for 16-bit residents when
   possible). Validated on the emulator (byte/16-bit chain coalescing, and a four-live-byte case that spills
   into `L`).

## Risks

- The backend's accumulator model is woven through every emitter; register residency touches
  the whole codegen path, so it must land incrementally behind the emulator harness, never as
  one rewrite.
- Interrupt handlers, the recursive software-stack path, and the far-call thunk model each
  impose their own register constraints (documented in `CLAUDE.md`) that the allocator must
  respect. These are why step 3 starts with the most conservative candidate set.
