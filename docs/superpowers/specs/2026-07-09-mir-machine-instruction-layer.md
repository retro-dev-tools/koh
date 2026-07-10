# MIR: a typed SM83 machine-instruction layer

Status: **foundation landed** (decoder + effects + tests); backend/peephole migration is follow-up.

## Motivation

Today the SM83 backend emits a flat byte buffer with no instruction boundaries
(`Sm83Backend.*Emitter` call `Emitter.U8`/`U16` directly), and `Sm83Peephole`
recovers boundaries after the fact by re-decoding that buffer through a private
opcode-length table. That works for the one rewrite it does (`LD A,0 → XOR A`)
but it is fragile and semantics-blind: the peephole has to hand-reason about flag
liveness with a bespoke forward scan, and there is nothing a superoptimizer
(SOTA item #5) could use to decide two instruction sequences equivalent.

This is the gating refactor identified in the SM83 optimization review as item
**#1**: a machine-instruction (MIR) layer between the IR and the raw bytes.

## What landed

`src/Koh.Compiler/Backends/Sm83/Mir/`:

- **`MirModel.cs`** — `Sm83Register` and `Sm83Flags` (bit sets, so a footprint is a
  mask and a register pair decomposes into its byte halves), `MirControl` (control-flow
  class), and `MirEffects` (the read/write footprint of one instruction: registers,
  flags, memory, control, plus a `SideEffect` flag for effects not otherwise captured —
  the interrupt-master toggle of `DI`/`EI`/`RETI` — so a consumer never deletes or
  reorders them as if inert).
- **`MirInstruction.cs`** — a decoded instruction (raw bytes + offset + effects) and
  `MirProgram` (a decoded run that re-encodes losslessly via `ToBytes()`).
- **`MirDecoder.cs`** — lifts a byte region into `MirInstruction`s, computing each
  instruction's `MirEffects` **structurally** from the opcode. The regular blocks
  (`LD r,r'` 0x40–0x7F, ALU 0x80–0xBF, all CB-prefixed) fall out of bit-field
  arithmetic; the irregular rows are handled per family; illegal/unmodeled opcodes — and
  any truncated tail (e.g. a region ending on a lone `0xCB`) — lift to
  `MirEffects.Opaque` (reads/writes everything) so decoding is **total** and any consumer
  treats them as a barrier. Instruction lengths come from the shared
  `Sm83OpcodeLength` table, which `Sm83Peephole` now also uses, so the length data lives
  in one place rather than being duplicated.

Tested in `tests/Koh.Compiler.Tests/Backends/MirDecoderTests.cs` (19 cases): lossless
round-trip, per-family effect correctness (XOR A vs. LD A,0 flag difference, ADC
carry-in, INC preserving carry, BIT not writing its register, HL+ post-increment),
**family-wide effect invariants** over the whole LD/ALU/CB/INC-DEC ranges, control-flow
classification, MMIO forms, totality over all 256 + 256 opcodes, the truncated-CB and
`DI`/`EI`/`RETI` side-effect regressions, and confirmation that `NOP` stays inert.

## Migration path (follow-up PRs)

1. **Peephole onto MIR.** The length table is already shared (`Sm83OpcodeLength`); the
   remaining step is to replace `Sm83Peephole`'s hand-rolled flag-liveness scan with
   `MirDecoder.Decode` + `MirEffects`. Flag-dead detection becomes
   "scan forward until `FlagWrite` covers the flags, stop at a `MemRead`/control boundary" —
   the same logic, but reading the effect footprint instead of re-deriving it. This also
   makes it cheap to add the review's other rules (`ld a,[hl+]` folding, reload elimination,
   tail-call `call→jp`).
2. **Emit through MIR.** Have the backend build `MirInstruction`s (or a thin builder over
   them) instead of raw bytes, then lower once at the end. This gives machine-level liveness
   for free and is the prerequisite for real register allocation (item #2).
3. **Superoptimizer substrate (item #5).** `MirEffects` plus the emulator oracle is enough to
   check a candidate sequence equivalent to a window of emitted code over the live registers
   and flags.

## Non-goals for the foundation

- Not wired into the backend or peephole yet — this PR only adds the layer and its tests, so
  it is inert until a consumer adopts it. That keeps the correctness-critical byte emission
  untouched while the substrate is reviewed.
- No operand disassembly text (the debugger already disassembles); MIR carries *effects*,
  which is the unique thing the existing table lacks.
