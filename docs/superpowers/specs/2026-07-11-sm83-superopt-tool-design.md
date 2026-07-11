# SM83 superoptimizer — productionized offline tool (`tools/Koh.Superopt`)

Status: **design approved, implementing.**

Productionizes the emulator-oracle superoptimizer PoC (PR #20, landed in the test
project) now that item #1 — the SM83 MIR machine-instruction layer — has landed on
master (`src/Koh.Compiler/Backends/Sm83/Mir/`). The PoC's own "Home" note called for
exactly this: a dedicated `tools/Koh.Superopt` project referencing the emulator (and,
now that it exists, the MIR layer) without inverting the compiler→emulator layering.

## Why now

The PoC hand-rolled three things because the MIR layer did not yet exist. It now does,
so this tool derives them structurally instead:

| PoC hand-rolled | This tool derives from |
|---|---|
| curated 12-op alphabet + hand-coded byte/cycle costs | `Sm83OpcodeLength` + `Sm83InstructionTable` (cycle cost) |
| per-op effects implicit in the test cases | `MirEffects` (reg/flag read·write, mem, control) via `MirDecoder` |
| a caller-passed `Live` set | `MirEffects` write/read footprints |
| "memory out of scope" hand-wave | `MemRead`/`MemWrite`/`Control` → a *principled* rejection filter |

## Scope

Two slices on this branch. **Slice 1 (miner) first**, then **slice 2 (verifier)**
reusing the same oracle.

### Slice 1 — discovery miner

Enumerate short, straight-line SM83 sequences over a table-derived alphabet, up to a
length bound, and for each input pattern find the cheapest candidate the oracle judges
equivalent under the input's live-out. Emit a **human-readable report** of candidate
rewrites (`input → rewrite`, bytes/cycles saved, required live-out) for a human to fold
into the existing MIR peephole by hand. No compile-time search, no auto-injection.

### Slice 2 — rule verifier / regression guard

Reuse the oracle to certify a list of rewrite rules — including the peephole's existing
hand-written ones — against emulator ground truth, so a bad or bit-rotted rule shows up
as a test failure rather than a miscompile. Same oracle, different driver.

## Home and layering

New console project `tools/Koh.Superopt`, referencing:
- `Koh.Emulator.Core` — the equivalence oracle (`GameBoySystem`).
- `Koh.Compiler` — the MIR layer (`MirDecoder`, `MirEffects`, `Sm83OpcodeLength`).
- `Koh.Core` — `Sm83InstructionTable` for cycle cost.

The emulator dependency lives here, in the tool, never in `Koh.Compiler` — so the
compiler→emulator layering is preserved. The `Sm83Oracle` is **ported** (not
referenced) from the test project, because that copy is `internal` to the test assembly;
the test-project PoC stays as-is for its own regression coverage.

## Components

Each is small and independently testable.

1. **`Sm83Oracle`** — concrete-execution equivalence. Bakes a byte sequence into a
   minimal ROM-only cartridge at the code entry, sets register state, single-steps until
   control leaves the sequence, reads state back. `AreEquivalent(a, b, live)` runs both
   over randomized inputs and compares only live-out registers/flags. Ported from the
   PoC; upgraded to take its live-out from MIR and to enforce the memory/control
   rejection below.
2. **`Cost`** — bytes first (ROM is the scarce SM83 resource), then cycles, from
   `Sm83OpcodeLength` + `Sm83InstructionTable`. Replaces the PoC's hand-coded costs.
3. **`Enumerator`** — bounded enumeration over a table-derived straight-line alphabet
   (8-bit reg moves, ALU, a few immediates), up to length *L*. Skips any op with
   `MemRead`/`MemWrite` or `Control != Fallthrough`, so every candidate is straight-line
   and register-only.
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
before auto-trusting a mined rule.` — the PoC's and this slice's acceptance rigor. A
mined rule is a *candidate for human review*, not yet a rule the compiler trusts blind.

## Testing

`tests/Koh.Superopt.Tests` (TUnit, mirroring `src`/`tools`):
- **Regression (from the PoC):** rediscovers `LD A,0 → XOR A` when flags are dead;
  **declines** it when flags are live-out; shrinks `LD A,B; LD A,B → LD A,B`; the oracle
  distinguishes flag-live vs flag-dead.
- **New:** `Cost` matches `Sm83InstructionTable` for a sampled opcode set; the enumerator
  rejects a memory op (e.g. `LD (HL),A`) and a control op (e.g. `JR`).

## Non-goals (YAGNI)

Inline compile-time superoptimization; auto-injecting mined rules into the peephole;
memory/`(HL)` modeling; symbolic or exhaustive verification; LLM-rewrite verification.
Each is a documented future step, not this branch.
