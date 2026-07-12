# SM83 superoptimizer — productionized offline tool (`tools/Koh.Superopt`)

Status: **landed** — miner in `tools/Koh.Superopt`, 13 tool tests green.

## What landed

- `tools/Koh.Superopt` console project referencing `Koh.Emulator.Core` (oracle) and
  `Koh.Compiler` (MIR) — the emulator dependency lives in the tool, not the compiler.
- `Sm83Oracle` (concrete-execution equivalence, ported from the PoC and upgraded to
  report T-cycles), `Sm83Alphabet` (a hand-curated straight-line/register-only op set,
  validated against the MIR decoder), `Enumerator` (bounded sequence enumeration),
  `Miner` (behavior-bucketing discovery), `RewriteFormatting`, and a `Program` report
  driver. `dotnet run --project tools/Koh.Superopt` prints mined rewrites under
  `Live.All` and flags-dead `Live.AllRegs`, rediscovering `LD A,0 → XOR A` (and larger
  wins) exactly where liveness permits.
- Cost is measured, not tabulated: bytes from the sequence length, T-cycles from
  `StepResult.TCyclesRan`.

Design and plan: this file and `docs/superpowers/plans/2026-07-11-sm83-superopt-tool.md`.

Productionizes the emulator-oracle superoptimizer PoC (PR #20). See
`docs/superpowers/specs/2026-07-09-sm83-superoptimizer.md` for the PoC's own history
and "Next steps" — this tool is that spec's next step, now that item #1 (the SM83 MIR
machine-instruction layer, `src/Koh.Compiler/Backends/Sm83/Mir/`) has landed.

## Why now

The PoC hand-rolled its instruction effects (reads/writes/memory) implicitly in test
cases. This tool derives them structurally instead, via `MirEffects` (through
`MirDecoder`): a "memory out of scope" hand-wave becomes a principled rejection filter
(`MemRead`/`MemWrite`/`Control` disqualify a candidate). The instruction alphabet
itself stays hand-curated (`Sm83Alphabet`, marked with a `ponytail:` note) — enough to
rediscover canonical peephole wins without exploding the search space.

## Scope

Discovery only: enumerate short, straight-line SM83 sequences over the curated
alphabet, up to a length bound, and for each input pattern find the cheapest candidate
the oracle judges equivalent under the input's live-out. Emit a **human-readable
report** of candidate rewrites (`input → rewrite`, bytes/cycles saved, required
live-out) for a human to fold into the existing MIR peephole by hand. No compile-time
search, no auto-injection.

## Home and layering

New console project `tools/Koh.Superopt`, referencing:
- `Koh.Emulator.Core` — the equivalence oracle (`GameBoySystem`).
- `Koh.Compiler` — the MIR layer (`MirDecoder`, `MirEffects`) used to validate the
  alphabet and reject memory/control-flow candidates.

The emulator dependency lives here, in the tool, never in `Koh.Compiler` — so the
compiler→emulator layering is preserved. The `Sm83Oracle` is **ported** (not
referenced) from the test project, because that copy is `internal` to the test
assembly.

## Components

Each is small and independently testable.

1. **`Sm83Oracle`** — concrete-execution equivalence. Bakes a byte sequence into a
   minimal ROM-only cartridge at the code entry, sets register state, single-steps until
   control leaves the sequence, reads state back. `AreEquivalent(a, b, live)` runs both
   over randomized inputs and compares only live-out registers/flags. Ported from the
   PoC; upgraded to measure T-cycles and to enforce the memory/control rejection below.
2. **`Sm83Alphabet`** — a curated straight-line, register-only op set (reg/reg moves,
   accumulator ALU, INC/DEC A, a couple of immediates), each entry checked against
   `MirEffects` so membership is a verified property, not a hand assertion.
3. **`Enumerator`** — bounded enumeration over the alphabet, up to length *L*.
4. **`Miner`** — for each input pattern, compute its live-out and the cheapest equivalent
   candidate; collect `(input, best, live-out, savingsBytes, savingsCycles)`.
5. **`Program`** — runs the miner over a bounded search and prints the report table.

## Soundness boundary

The register-state oracle is sound **only** for straight-line, memory-free sequences.
`MirEffects` makes that boundary exact: any seed or candidate with a memory or
control-flow effect is rejected up front, rather than silently mis-judged. Concrete
random testing is sound-for-refutation (one disagreeing input disproves equivalence) and
a strong acceptance filter.

`// ponytail: random-input filter only; add an exhaustive small-window or symbolic check
before auto-trusting a mined rule.` — the PoC's and this tool's acceptance rigor. A
mined rule is a *candidate for human review*, not yet a rule the compiler trusts blind.

## Testing

`tests/Koh.Superopt.Tests` (TUnit, mirroring `src`/`tools`):
- **Regression (from the PoC):** rediscovers `LD A,0 → XOR A` when flags are dead;
  **declines** it when flags are live-out; shrinks `LD A,B; LD A,B → LD A,B`; the oracle
  distinguishes flag-live vs flag-dead.
- **New:** the enumerator/alphabet reject a memory op (e.g. `LD (HL),A`) and a control op
  (e.g. `JR`) via `MirEffects`.

## Non-goals (YAGNI)

Inline compile-time superoptimization; auto-injecting mined rules into the peephole;
memory/`(HL)` modeling; symbolic or exhaustive verification; LLM-rewrite verification;
a declarative rule verifier (cut in review — no consumer; the peephole expresses rules
imperatively over `MirEffects`, not as a `(From, To, Live)` list). Each is a documented
future step, not this tool.
