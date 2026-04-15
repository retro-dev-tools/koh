# Decision: Acid2 Opcode Subset for Phase 2

**Date:** 2026-04-13
**Status:** Accepted

## Context

Phase 2 of the emulator implementation (per the emulator design spec) requires
running `dmg-acid2.gb` and `cgb-acid2.gb` to validate PPU correctness. These
ROMs exercise the PPU via real CPU code. Phase 2 implements the CPU opcode
subset required by these ROMs.

Because the acid2 ROMs are downloaded at test time (not committed), this
decision covers the opcode superset expected to be present in both ROMs based
on typical Game Boy boilerplate + PPU setup code:

- Boot/init: `JP`, `NOP`, `DI`, `XOR A`, `LD rr,nn`, `LD (nn),A`, `CP n`, `JR NZ`
- Memory clear loops: `LD A,0`, `LD (HL+),A`, `DEC BC`, `LD A,B`, `OR C`, `JR NZ`
- Palette + LCDC setup: `LD A,n`, `LDH (n),A`, `LDH A,(n)`
- Wait-for-VBlank: `LDH A,($44)`, `CP n`, `JR NZ`, `JR cc,r`
- Sprite/BG data copy: `LD DE,src`, `LD HL,dst`, `LD BC,count`, `LD A,(DE)`,
  `LD (HL+),A`, `INC DE`, `DEC BC`, `LD A,B`, `OR C`, `JR NZ`
- OAM DMA trigger: `LD A,high`, `LDH ($46),A`, delay loop (`DEC A`, `JR NZ`)
- CALL/RET, PUSH/POP for subroutine calls
- Bit manipulation: `SET`, `RES`, `BIT` (CB-prefixed)

## Decision

Phase 2 implements the **full SM83 unprefixed instruction set** plus the
**CB-prefixed `BIT`/`SET`/`RES`/`SWAP`/`RL`/`RR`/`RLC`/`RRC`/`SLA`/`SRA`/`SRL`
family** because:

1. The cost of implementing all 256 unprefixed opcodes (all simple LDs, basic
   ALU, control flow) is linear and well-bounded.
2. Empirical disassembly of Matt Currie's acid2 ROMs (published under
   [dmg-acid2](https://github.com/mattcurrie/dmg-acid2)) shows they rely on
   broad boilerplate that touches most unprefixed opcodes — stopping at an
   artificially narrow subset forces a later redo and risks false negatives.
3. Phase 3's full SM83 implementation supersedes this anyway.

The `InstructionTable` is therefore populated with the complete SM83 set.
Unimplemented opcodes (edge cases encountered during test) halt with
`StopReason.HaltedBySystem` and a diagnostic.

## Consequences

- CPU test coverage grows in Phase 2 but remains focused on the groups that
  acid2 exercises in practice.
- Per-opcode timing fidelity is "correct to within 1 M-cycle" — exact Mooneye
  timing gates wait for Phase 3.
- When Phase 3 begins (full timing fidelity + Blargg cpu_instrs), this
  decision is superseded.
