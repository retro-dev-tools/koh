# Koh Emulator — Phase 3 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the complete SM83 instruction set (256 unprefixed + 256 CB-prefixed opcodes), accurate interrupt dispatch (5 M-cycle sequence, HALT + HALT bug, EI delay slot), full Timer / Joypad IRQ, and a fully functional source-level debugger (halting breakpoints, step-over / step-in / step-out, heuristic call stack, disassembly, symbol and source-context variable scopes, instruction and function breakpoints, evaluate) — all validated against Blargg `cpu_instrs`, `instr_timing`, `mem_timing`, `mem_timing-2`, `halt_bug`, `interrupt_time`, and the Mooneye `acceptance/{bits,timer,interrupts,oam_dma}` subset.

**Architecture:** Replaces the Phase 2 acid2 opcode subset with the full instruction table. Adds interrupt-service state to the CPU. Extends the Phase 1 debugger skeleton with real stepping, stack walking, disassembly, and expanded variable scopes. No new projects; Phase 3 is all additive work inside `Koh.Emulator.Core` and `Koh.Debugger` plus Razor UI polish and DAP message additions.

**Tech Stack:** Unchanged from Phase 2 (C# 14 / .NET 10, TUnit, Blazor WebAssembly, TypeScript, Cake).

**Prerequisites:** Phase 1 and Phase 2 complete. All Phase 2 exit criteria must pass.

**Scope note:** This plan covers only Phase 3 from the design spec. Phase 4 (APU, save states, watchpoints, real-game verification) and Phase 5 (MAUI desktop, playground) are each handled by separate plans.

---

## Architecture summary

Phase 3 rewrites the CPU instruction table from "acid2 subset" to "complete SM83 ISA". The instruction table model from Phase 2 (Task 2.E.3) is already the right shape: `InstructionHandler[]` arrays indexed by opcode byte. Phase 3 populates all 256 entries in the unprefixed table and all 256 entries in the new CB-prefixed table, and adds the interrupt-dispatch sequence as a special instruction-like state.

New files:

```
src/Koh.Emulator.Core/Cpu/
├── InstructionTable.cs           // REWRITTEN — full 256-opcode table
├── CbInstructionTable.cs         // NEW — 256 CB-prefixed opcodes
├── Alu.cs                        // NEW — ALU helpers for add/sub/and/or/xor/cp/cpl/daa/swap/rotates/shifts
├── InterruptDispatch.cs          // NEW — 5 M-cycle ISR entry sequence
├── HaltState.cs                  // NEW — HALT + HALT-bug implementation
└── Sm83.cs                       // REWRITTEN — full decode loop with interrupt check

src/Koh.Debugger/
├── Dap/Handlers/
│   ├── NextHandler.cs            // NEW — step-over
│   ├── StepInHandler.cs          // NEW
│   ├── StepOutHandler.cs         // NEW
│   ├── StackTraceHandler.cs      // NEW
│   ├── DisassembleHandler.cs     // NEW
│   ├── EvaluateHandler.cs        // NEW
│   ├── SetInstructionBreakpointsHandler.cs  // NEW
│   ├── SetFunctionBreakpointsHandler.cs     // NEW
│   └── BreakpointLocationsHandler.cs        // NEW
├── Dap/Messages/
│   ├── StepMessages.cs           // NEW
│   ├── StackTraceMessages.cs     // NEW
│   ├── DisassembleMessages.cs    // NEW
│   ├── EvaluateMessages.cs       // NEW
│   └── BreakpointLocationsMessages.cs       // NEW
├── Session/
│   ├── CallStackWalker.cs        // NEW
│   ├── Disassembler.cs           // NEW — uses the same tables as the CPU
│   ├── StepStrategy.cs           // NEW — step-over/in/out state machines
│   └── BreakpointManager.cs      // MODIFIED — banked execution breakpoints now halt
└── Dap/DapCapabilities.cs        // MODIFIED — Phase 3 capability set
```

The execution loop in `Koh.Debugger` is updated so `RunFrame` is called with breakpoint checking enabled, and `StepInstruction` / `RunUntil` are invoked for stepping.

Razor UI gets a small update to show the call stack and disassembly view alongside the CPU dashboard.

---

## Approach to the 512 opcode problem

Writing one task per opcode is impractical. The plan groups opcodes into **families** that share implementation structure. Each family gets one implementation task and one representative test task. Opcodes within a family are implemented together in the same commit.

The exact opcode assignments are tabulated in `references/sm83-opcode-table.md` (created in Task 3.A.1) and match the Pan Docs / Gekkio reference. Each family task includes a list of opcode bytes it covers; the engineer fills in the instruction handlers for every listed byte.

**Families (unprefixed):**

| Family | Opcodes | Task |
|---|---|---|
| Misc (NOP, STOP, HALT, DI, EI, CPL, CCF, SCF, DAA) | 00, 10, 76, F3, FB, 2F, 3F, 37, 27 | 3.B.1 |
| 8-bit immediate load (`LD r,d8`) | 06, 0E, 16, 1E, 26, 2E, 36, 3E | 3.B.2 |
| 16-bit immediate load (`LD rr,d16`) | 01, 11, 21, 31 | 3.B.2 |
| 8-bit register-register load (`LD r,r`) | 40–7F except 76 | 3.B.3 |
| 8-bit indirect load (`LD A,(BC)`, `LD A,(DE)`, `LDI`, `LDD`, `LD (BC),A`, `LD (DE),A`, `LD (a16),A`, `LD A,(a16)`, `LDH`) | 02, 0A, 12, 1A, 22, 2A, 32, 3A, EA, FA, E0, F0, E2, F2, F8, F9, 08 | 3.B.4 |
| 8-bit inc/dec | 04, 05, 0C, 0D, 14, 15, 1C, 1D, 24, 25, 2C, 2D, 34, 35, 3C, 3D | 3.B.5 |
| 16-bit inc/dec | 03, 0B, 13, 1B, 23, 2B, 33, 3B | 3.B.5 |
| 16-bit add HL | 09, 19, 29, 39 | 3.B.5 |
| 16-bit ADD SP,r8 | E8 | 3.B.5 |
| ALU A,r (ADD/ADC/SUB/SBC/AND/OR/XOR/CP) | 80–BF | 3.B.6 |
| ALU A,d8 | C6, CE, D6, DE, E6, EE, F6, FE | 3.B.6 |
| Rotates in the main table (RLCA, RLA, RRCA, RRA) | 07, 17, 0F, 1F | 3.B.7 |
| Jumps: JP, JP cc, JR, JR cc, JP (HL) | C3, C2, CA, D2, DA, 18, 20, 28, 30, 38, E9 | 3.B.8 |
| Calls: CALL, CALL cc | CD, C4, CC, D4, DC | 3.B.8 |
| Returns: RET, RET cc, RETI, RST | C9, C0, C8, D0, D8, D9, C7, CF, D7, DF, E7, EF, F7, FF | 3.B.8 |
| Stack: PUSH/POP | C1, C5, D1, D5, E1, E5, F1, F5 | 3.B.9 |

**Families (CB-prefixed):** very regular structure — 8 operations × 8 register targets × (4 rotate/shift variants + 8 BIT + 8 RES + 8 SET).

| CB Family | Opcodes | Task |
|---|---|---|
| RLC r | 00–07 | 3.C.1 |
| RRC r | 08–0F | 3.C.1 |
| RL r | 10–17 | 3.C.1 |
| RR r | 18–1F | 3.C.1 |
| SLA r | 20–27 | 3.C.1 |
| SRA r | 28–2F | 3.C.1 |
| SWAP r | 30–37 | 3.C.1 |
| SRL r | 38–3F | 3.C.1 |
| BIT n,r | 40–7F | 3.C.2 |
| RES n,r | 80–BF | 3.C.3 |
| SET n,r | C0–FF | 3.C.3 |

The `Alu` helper class (Task 3.A.2) exposes the shared arithmetic / rotate / bit-manipulation primitives used by every family, so each handler is 3–5 lines of dispatch.

---

## Phase 3-A: CPU foundations

### Task 3.A.1: SM83 opcode reference table

**Files:**
- Create: `docs/references/sm83-opcode-table.md`

- [ ] **Step 1: Create the reference table**

```markdown
# SM83 Opcode Reference (Phase 3)

Authoritative table used by `InstructionTable.cs` and `CbInstructionTable.cs`.
Derived from Pan Docs and Gekkio's complete opcode reference.

## Unprefixed ($00-$FF)

| Byte | Mnemonic | Cycles | Flags | Notes |
|------|----------|--------|-------|-------|
| $00  | NOP | 4 | ---- | |
| $01  | LD BC,d16 | 12 | ---- | |
| $02  | LD (BC),A | 8 | ---- | |
| $03  | INC BC | 8 | ---- | |
| $04  | INC B | 4 | Z0H- | |
| $05  | DEC B | 4 | Z1H- | |
| $06  | LD B,d8 | 8 | ---- | |
| $07  | RLCA | 4 | 000C | |
| $08  | LD (a16),SP | 20 | ---- | |
| ...  | (fill in all 256 rows from Pan Docs) | | | |

## CB-prefixed ($00-$FF)

| Byte | Mnemonic | Cycles | Flags | Notes |
|------|----------|--------|-------|-------|
| CB $00 | RLC B | 8 | Z00C | |
| CB $01 | RLC C | 8 | Z00C | |
| ...  | (fill in all 256 rows) | | | |
```

The full 1024-row table is tedious but one-time work. Use Pan Docs as the source of truth. **Do not skip cycles or flag columns** — they gate the Blargg instr_timing test.

- [ ] **Step 2: Commit**

```bash
git add docs/references/sm83-opcode-table.md
git commit -m "docs: add SM83 opcode reference table for Phase 3"
```

---

### Task 3.A.2: ALU helper class

**Files:**
- Create: `src/Koh.Emulator.Core/Cpu/Alu.cs`

- [ ] **Step 1: Create `Alu.cs` with all shared primitives**

```csharp
namespace Koh.Emulator.Core.Cpu;

/// <summary>
/// Shared ALU primitives used by instruction handlers. All methods return the
/// result byte and write flags into the referenced <see cref="CpuRegisters"/>.
/// </summary>
public static class Alu
{
    // --- 8-bit arithmetic ---

    public static byte Add(ref CpuRegisters r, byte lhs, byte rhs)
    {
        int sum = lhs + rhs;
        byte result = (byte)sum;
        r.SetFlag(CpuRegisters.FlagZ, result == 0);
        r.SetFlag(CpuRegisters.FlagN, false);
        r.SetFlag(CpuRegisters.FlagH, ((lhs & 0x0F) + (rhs & 0x0F)) > 0x0F);
        r.SetFlag(CpuRegisters.FlagC, sum > 0xFF);
        return result;
    }

    public static byte Adc(ref CpuRegisters r, byte lhs, byte rhs)
    {
        int carry = r.FlagSet(CpuRegisters.FlagC) ? 1 : 0;
        int sum = lhs + rhs + carry;
        byte result = (byte)sum;
        r.SetFlag(CpuRegisters.FlagZ, result == 0);
        r.SetFlag(CpuRegisters.FlagN, false);
        r.SetFlag(CpuRegisters.FlagH, ((lhs & 0x0F) + (rhs & 0x0F) + carry) > 0x0F);
        r.SetFlag(CpuRegisters.FlagC, sum > 0xFF);
        return result;
    }

    public static byte Sub(ref CpuRegisters r, byte lhs, byte rhs)
    {
        int diff = lhs - rhs;
        byte result = (byte)diff;
        r.SetFlag(CpuRegisters.FlagZ, result == 0);
        r.SetFlag(CpuRegisters.FlagN, true);
        r.SetFlag(CpuRegisters.FlagH, (lhs & 0x0F) < (rhs & 0x0F));
        r.SetFlag(CpuRegisters.FlagC, diff < 0);
        return result;
    }

    public static byte Sbc(ref CpuRegisters r, byte lhs, byte rhs)
    {
        int carry = r.FlagSet(CpuRegisters.FlagC) ? 1 : 0;
        int diff = lhs - rhs - carry;
        byte result = (byte)diff;
        r.SetFlag(CpuRegisters.FlagZ, result == 0);
        r.SetFlag(CpuRegisters.FlagN, true);
        r.SetFlag(CpuRegisters.FlagH, (lhs & 0x0F) - (rhs & 0x0F) - carry < 0);
        r.SetFlag(CpuRegisters.FlagC, diff < 0);
        return result;
    }

    public static byte And(ref CpuRegisters r, byte lhs, byte rhs)
    {
        byte result = (byte)(lhs & rhs);
        r.SetFlag(CpuRegisters.FlagZ, result == 0);
        r.SetFlag(CpuRegisters.FlagN, false);
        r.SetFlag(CpuRegisters.FlagH, true);
        r.SetFlag(CpuRegisters.FlagC, false);
        return result;
    }

    public static byte Or(ref CpuRegisters r, byte lhs, byte rhs)
    {
        byte result = (byte)(lhs | rhs);
        r.SetFlag(CpuRegisters.FlagZ, result == 0);
        r.SetFlag(CpuRegisters.FlagN, false);
        r.SetFlag(CpuRegisters.FlagH, false);
        r.SetFlag(CpuRegisters.FlagC, false);
        return result;
    }

    public static byte Xor(ref CpuRegisters r, byte lhs, byte rhs)
    {
        byte result = (byte)(lhs ^ rhs);
        r.SetFlag(CpuRegisters.FlagZ, result == 0);
        r.SetFlag(CpuRegisters.FlagN, false);
        r.SetFlag(CpuRegisters.FlagH, false);
        r.SetFlag(CpuRegisters.FlagC, false);
        return result;
    }

    public static void Cp(ref CpuRegisters r, byte lhs, byte rhs)
    {
        // Cp is Sub without storing the result.
        int diff = lhs - rhs;
        r.SetFlag(CpuRegisters.FlagZ, (byte)diff == 0);
        r.SetFlag(CpuRegisters.FlagN, true);
        r.SetFlag(CpuRegisters.FlagH, (lhs & 0x0F) < (rhs & 0x0F));
        r.SetFlag(CpuRegisters.FlagC, diff < 0);
    }

    public static byte Inc(ref CpuRegisters r, byte value)
    {
        byte result = (byte)(value + 1);
        r.SetFlag(CpuRegisters.FlagZ, result == 0);
        r.SetFlag(CpuRegisters.FlagN, false);
        r.SetFlag(CpuRegisters.FlagH, (value & 0x0F) == 0x0F);
        return result;
    }

    public static byte Dec(ref CpuRegisters r, byte value)
    {
        byte result = (byte)(value - 1);
        r.SetFlag(CpuRegisters.FlagZ, result == 0);
        r.SetFlag(CpuRegisters.FlagN, true);
        r.SetFlag(CpuRegisters.FlagH, (value & 0x0F) == 0);
        return result;
    }

    // --- 16-bit arithmetic ---

    public static ushort AddHL(ref CpuRegisters r, ushort lhs, ushort rhs)
    {
        int sum = lhs + rhs;
        ushort result = (ushort)sum;
        r.SetFlag(CpuRegisters.FlagN, false);
        r.SetFlag(CpuRegisters.FlagH, ((lhs & 0x0FFF) + (rhs & 0x0FFF)) > 0x0FFF);
        r.SetFlag(CpuRegisters.FlagC, sum > 0xFFFF);
        // Z unchanged.
        return result;
    }

    public static ushort AddSpRel(ref CpuRegisters r, ushort sp, sbyte offset)
    {
        int sum = sp + offset;
        byte spLow = (byte)sp;
        byte offsetByte = (byte)offset;
        r.SetFlag(CpuRegisters.FlagZ, false);
        r.SetFlag(CpuRegisters.FlagN, false);
        r.SetFlag(CpuRegisters.FlagH, ((spLow & 0x0F) + (offsetByte & 0x0F)) > 0x0F);
        r.SetFlag(CpuRegisters.FlagC, ((spLow & 0xFF) + (offsetByte & 0xFF)) > 0xFF);
        return (ushort)sum;
    }

    // --- DAA ---

    public static byte Daa(ref CpuRegisters r, byte a)
    {
        int result = a;
        if (!r.FlagSet(CpuRegisters.FlagN))
        {
            if (r.FlagSet(CpuRegisters.FlagH) || (result & 0x0F) > 9) result += 0x06;
            if (r.FlagSet(CpuRegisters.FlagC) || result > 0x9F)
            {
                result += 0x60;
                r.SetFlag(CpuRegisters.FlagC, true);
            }
        }
        else
        {
            if (r.FlagSet(CpuRegisters.FlagH)) result = (result - 6) & 0xFF;
            if (r.FlagSet(CpuRegisters.FlagC)) result -= 0x60;
        }
        r.SetFlag(CpuRegisters.FlagH, false);
        r.SetFlag(CpuRegisters.FlagZ, (byte)result == 0);
        return (byte)result;
    }

    // --- Rotates and shifts (A register variants clear Z, CB variants set Z) ---

    public static byte Rlc(ref CpuRegisters r, byte value, bool clearZ)
    {
        byte carry = (byte)(value >> 7);
        byte result = (byte)((value << 1) | carry);
        r.SetFlag(CpuRegisters.FlagZ, !clearZ && result == 0);
        r.SetFlag(CpuRegisters.FlagN, false);
        r.SetFlag(CpuRegisters.FlagH, false);
        r.SetFlag(CpuRegisters.FlagC, carry != 0);
        return result;
    }

    public static byte Rrc(ref CpuRegisters r, byte value, bool clearZ)
    {
        byte carry = (byte)(value & 1);
        byte result = (byte)((value >> 1) | (carry << 7));
        r.SetFlag(CpuRegisters.FlagZ, !clearZ && result == 0);
        r.SetFlag(CpuRegisters.FlagN, false);
        r.SetFlag(CpuRegisters.FlagH, false);
        r.SetFlag(CpuRegisters.FlagC, carry != 0);
        return result;
    }

    public static byte Rl(ref CpuRegisters r, byte value, bool clearZ)
    {
        byte inCarry = (byte)(r.FlagSet(CpuRegisters.FlagC) ? 1 : 0);
        byte outCarry = (byte)(value >> 7);
        byte result = (byte)((value << 1) | inCarry);
        r.SetFlag(CpuRegisters.FlagZ, !clearZ && result == 0);
        r.SetFlag(CpuRegisters.FlagN, false);
        r.SetFlag(CpuRegisters.FlagH, false);
        r.SetFlag(CpuRegisters.FlagC, outCarry != 0);
        return result;
    }

    public static byte Rr(ref CpuRegisters r, byte value, bool clearZ)
    {
        byte inCarry = (byte)(r.FlagSet(CpuRegisters.FlagC) ? 0x80 : 0);
        byte outCarry = (byte)(value & 1);
        byte result = (byte)((value >> 1) | inCarry);
        r.SetFlag(CpuRegisters.FlagZ, !clearZ && result == 0);
        r.SetFlag(CpuRegisters.FlagN, false);
        r.SetFlag(CpuRegisters.FlagH, false);
        r.SetFlag(CpuRegisters.FlagC, outCarry != 0);
        return result;
    }

    public static byte Sla(ref CpuRegisters r, byte value)
    {
        byte carry = (byte)(value >> 7);
        byte result = (byte)(value << 1);
        r.SetFlag(CpuRegisters.FlagZ, result == 0);
        r.SetFlag(CpuRegisters.FlagN, false);
        r.SetFlag(CpuRegisters.FlagH, false);
        r.SetFlag(CpuRegisters.FlagC, carry != 0);
        return result;
    }

    public static byte Sra(ref CpuRegisters r, byte value)
    {
        byte carry = (byte)(value & 1);
        byte result = (byte)((value >> 1) | (value & 0x80));
        r.SetFlag(CpuRegisters.FlagZ, result == 0);
        r.SetFlag(CpuRegisters.FlagN, false);
        r.SetFlag(CpuRegisters.FlagH, false);
        r.SetFlag(CpuRegisters.FlagC, carry != 0);
        return result;
    }

    public static byte Srl(ref CpuRegisters r, byte value)
    {
        byte carry = (byte)(value & 1);
        byte result = (byte)(value >> 1);
        r.SetFlag(CpuRegisters.FlagZ, result == 0);
        r.SetFlag(CpuRegisters.FlagN, false);
        r.SetFlag(CpuRegisters.FlagH, false);
        r.SetFlag(CpuRegisters.FlagC, carry != 0);
        return result;
    }

    public static byte Swap(ref CpuRegisters r, byte value)
    {
        byte result = (byte)(((value & 0x0F) << 4) | ((value & 0xF0) >> 4));
        r.SetFlag(CpuRegisters.FlagZ, result == 0);
        r.SetFlag(CpuRegisters.FlagN, false);
        r.SetFlag(CpuRegisters.FlagH, false);
        r.SetFlag(CpuRegisters.FlagC, false);
        return result;
    }

    // --- Bit manipulation ---

    public static void Bit(ref CpuRegisters r, byte value, int bit)
    {
        r.SetFlag(CpuRegisters.FlagZ, (value & (1 << bit)) == 0);
        r.SetFlag(CpuRegisters.FlagN, false);
        r.SetFlag(CpuRegisters.FlagH, true);
        // C unchanged.
    }

    public static byte Res(byte value, int bit) => (byte)(value & ~(1 << bit));
    public static byte Set(byte value, int bit) => (byte)(value | (1 << bit));
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Koh.Emulator.Core/Koh.Emulator.Core.csproj`

- [ ] **Step 3: Write focused tests for the ALU primitives**

File `tests/Koh.Emulator.Core.Tests/AluTests.cs` — one test per primitive with known inputs and expected flags. Example:

```csharp
using Koh.Emulator.Core.Cpu;

namespace Koh.Emulator.Core.Tests;

public class AluTests
{
    [Test]
    public async Task Add_Sets_Half_Carry_When_Low_Nibble_Overflows()
    {
        var r = new CpuRegisters();
        byte result = Alu.Add(ref r, 0x0F, 0x01);
        await Assert.That(result).IsEqualTo((byte)0x10);
        await Assert.That(r.FlagSet(CpuRegisters.FlagH)).IsTrue();
        await Assert.That(r.FlagSet(CpuRegisters.FlagC)).IsFalse();
    }

    [Test]
    public async Task Sub_Sets_Zero_And_N_When_Equal()
    {
        var r = new CpuRegisters();
        byte result = Alu.Sub(ref r, 0x42, 0x42);
        await Assert.That(result).IsEqualTo((byte)0);
        await Assert.That(r.FlagSet(CpuRegisters.FlagZ)).IsTrue();
        await Assert.That(r.FlagSet(CpuRegisters.FlagN)).IsTrue();
    }

    [Test]
    public async Task Rl_Rotates_Through_Carry()
    {
        var r = new CpuRegisters();
        r.SetFlag(CpuRegisters.FlagC, true);
        byte result = Alu.Rl(ref r, 0x80, clearZ: false);
        await Assert.That(result).IsEqualTo((byte)0x01);
        await Assert.That(r.FlagSet(CpuRegisters.FlagC)).IsTrue();
    }

    [Test]
    public async Task Bit_Tests_Set_Bit()
    {
        var r = new CpuRegisters();
        Alu.Bit(ref r, 0x80, 7);
        await Assert.That(r.FlagSet(CpuRegisters.FlagZ)).IsFalse();
        Alu.Bit(ref r, 0x00, 3);
        await Assert.That(r.FlagSet(CpuRegisters.FlagZ)).IsTrue();
    }

    // Add tests for each primitive — Adc, Sbc, And, Or, Xor, Cp, Inc, Dec,
    // AddHL, AddSpRel, Daa, Rlc, Rrc, Rr, Sla, Sra, Srl, Swap, Res, Set.
}
```

Run: `dotnet test tests/Koh.Emulator.Core.Tests/Koh.Emulator.Core.Tests.csproj --filter AluTests`
Expected: all primitive tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/Koh.Emulator.Core/Cpu/Alu.cs tests/Koh.Emulator.Core.Tests/AluTests.cs
git commit -m "feat(emulator): add Alu helper class with full primitive test coverage"
```

---

## Phase 3-B: Unprefixed instruction families

Each task in Phase 3-B populates a family of opcodes in `InstructionTable.cs`. The Phase 2 acid2 subset seeds the table; these tasks extend it to cover all 256 entries. Commit after each family.

### Task 3.B.1: Misc family (NOP, STOP, HALT, DI, EI, CPL, CCF, SCF, DAA)

**Files:**
- Modify: `src/Koh.Emulator.Core/Cpu/InstructionTable.cs`
- Modify: `src/Koh.Emulator.Core/Cpu/Sm83.cs`

- [ ] **Step 1: Add the misc handlers to `InstructionTable.BuildUnprefixedTable`**

```csharp
// HALT ($76)
table[0x76] = (ref CpuRegisters r, IInstructionBus bus) =>
{
    bus.EnterHalt();
    return 4;
};

// STOP ($10)
table[0x10] = (ref CpuRegisters r, IInstructionBus bus) =>
{
    bus.ReadImmediate();  // STOP has a required 0x00 trailing byte
    bus.ExecuteStop();
    return 4;
};

// DI ($F3)
table[0xF3] = (ref CpuRegisters r, IInstructionBus bus) =>
{
    bus.SetIme(false);
    return 4;
};

// EI ($FB) — IME enabled AFTER the next instruction (EI delay slot).
table[0xFB] = (ref CpuRegisters r, IInstructionBus bus) =>
{
    bus.ArmEiDelaySlot();
    return 4;
};

// CPL ($2F) — complement A
table[0x2F] = (ref CpuRegisters r, IInstructionBus bus) =>
{
    r.A = (byte)~r.A;
    r.SetFlag(CpuRegisters.FlagN, true);
    r.SetFlag(CpuRegisters.FlagH, true);
    return 4;
};

// CCF ($3F) — complement carry flag
table[0x3F] = (ref CpuRegisters r, IInstructionBus bus) =>
{
    r.SetFlag(CpuRegisters.FlagN, false);
    r.SetFlag(CpuRegisters.FlagH, false);
    r.SetFlag(CpuRegisters.FlagC, !r.FlagSet(CpuRegisters.FlagC));
    return 4;
};

// SCF ($37) — set carry flag
table[0x37] = (ref CpuRegisters r, IInstructionBus bus) =>
{
    r.SetFlag(CpuRegisters.FlagN, false);
    r.SetFlag(CpuRegisters.FlagH, false);
    r.SetFlag(CpuRegisters.FlagC, true);
    return 4;
};

// DAA ($27)
table[0x27] = (ref CpuRegisters r, IInstructionBus bus) =>
{
    r.A = Alu.Daa(ref r, r.A);
    return 4;
};
```

- [ ] **Step 2: Extend `IInstructionBus` with the new host callbacks**

```csharp
public interface IInstructionBus
{
    byte ReadByte(ushort address);
    void WriteByte(ushort address, byte value);
    byte ReadImmediate();
    ushort ReadImmediate16();

    // Phase 3 additions
    void EnterHalt();
    void ExecuteStop();
    void SetIme(bool value);
    void ArmEiDelaySlot();
}
```

- [ ] **Step 3: Implement the new methods on `Sm83`**

Store an `_eiDelayArmed` flag and an `Halted` flag (already present). `SetIme(true/false)` sets `Interrupts.IME`. `ArmEiDelaySlot` sets the flag which is consumed by the next `ExecuteNextInstruction` call before interrupt checking. `EnterHalt` sets `Halted = true` and uses `HaltState.cs` logic (Task 3.A.3 below).

- [ ] **Step 4: Build, test, commit**

```bash
git add src/Koh.Emulator.Core/Cpu/InstructionTable.cs src/Koh.Emulator.Core/Cpu/Sm83.cs
git commit -m "feat(cpu): add misc family (NOP/STOP/HALT/DI/EI/CPL/CCF/SCF/DAA)"
```

---

### Task 3.B.2: 8-bit and 16-bit immediate loads

**Files:**
- Modify: `src/Koh.Emulator.Core/Cpu/InstructionTable.cs`

- [ ] **Step 1: Implement every `LD r,d8` (06, 0E, 16, 1E, 26, 2E, 36, 3E) and `LD rr,d16` (01, 11, 21, 31)**

Pattern:

```csharp
// LD B,d8 ($06)
table[0x06] = (ref CpuRegisters r, IInstructionBus bus) =>
{
    r.B = bus.ReadImmediate();
    return 8;
};

// LD (HL),d8 ($36)
table[0x36] = (ref CpuRegisters r, IInstructionBus bus) =>
{
    byte value = bus.ReadImmediate();
    bus.WriteByte(r.HL, value);
    return 12;
};

// LD BC,d16 ($01)
table[0x01] = (ref CpuRegisters r, IInstructionBus bus) =>
{
    r.BC = bus.ReadImmediate16();
    return 12;
};
```

Implement all 12 opcodes (8 r,d8 + 4 rr,d16).

- [ ] **Step 2: Add representative tests**

File `tests/Koh.Emulator.Core.Tests/ImmediateLoadTests.cs`:

```csharp
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Emulator.Core.Tests;

public class ImmediateLoadTests
{
    private static GameBoySystem SystemWithCode(params byte[] code)
    {
        var rom = new byte[0x8000];
        rom[0x147] = 0x00;
        Array.Copy(code, 0, rom, 0x0100, code.Length);
        var cart = CartridgeFactory.Load(rom);
        return new GameBoySystem(HardwareMode.Dmg, cart);
    }

    [Test] public async Task LdB_D8() { var gb = SystemWithCode(0x06, 0x42); gb.StepInstruction(); await Assert.That(gb.Registers.B).IsEqualTo((byte)0x42); }
    [Test] public async Task LdC_D8() { var gb = SystemWithCode(0x0E, 0x43); gb.StepInstruction(); await Assert.That(gb.Registers.C).IsEqualTo((byte)0x43); }
    [Test] public async Task LdD_D8() { var gb = SystemWithCode(0x16, 0x44); gb.StepInstruction(); await Assert.That(gb.Registers.D).IsEqualTo((byte)0x44); }
    [Test] public async Task LdE_D8() { var gb = SystemWithCode(0x1E, 0x45); gb.StepInstruction(); await Assert.That(gb.Registers.E).IsEqualTo((byte)0x45); }
    [Test] public async Task LdH_D8() { var gb = SystemWithCode(0x26, 0x46); gb.StepInstruction(); await Assert.That(gb.Registers.H).IsEqualTo((byte)0x46); }
    [Test] public async Task LdL_D8() { var gb = SystemWithCode(0x2E, 0x47); gb.StepInstruction(); await Assert.That(gb.Registers.L).IsEqualTo((byte)0x47); }
    [Test] public async Task LdA_D8() { var gb = SystemWithCode(0x3E, 0x48); gb.StepInstruction(); await Assert.That(gb.Registers.A).IsEqualTo((byte)0x48); }

    [Test]
    public async Task LdBC_D16()
    {
        var gb = SystemWithCode(0x01, 0x34, 0x12);
        gb.StepInstruction();
        await Assert.That(gb.Registers.BC).IsEqualTo((ushort)0x1234);
    }

    [Test]
    public async Task LdSp_D16()
    {
        var gb = SystemWithCode(0x31, 0xFE, 0xFF);
        gb.StepInstruction();
        await Assert.That(gb.Registers.Sp).IsEqualTo((ushort)0xFFFE);
    }
}
```

- [ ] **Step 3: Run tests and commit**

Run: `dotnet test tests/Koh.Emulator.Core.Tests/Koh.Emulator.Core.Tests.csproj --filter ImmediateLoadTests`

```bash
git add src/Koh.Emulator.Core/Cpu/InstructionTable.cs tests/Koh.Emulator.Core.Tests/ImmediateLoadTests.cs
git commit -m "feat(cpu): add 8-bit and 16-bit immediate load opcodes"
```

---

### Task 3.B.3: 8-bit register-to-register loads ($40-$7F except $76)

**Files:**
- Modify: `src/Koh.Emulator.Core/Cpu/InstructionTable.cs`

- [ ] **Step 1: Implement all 63 register-to-register loads**

These follow a very regular pattern: bits `01 ddd sss`, where `ddd` is the destination and `sss` is the source. `(HL)` source or destination makes the instruction 8 cycles instead of 4.

Write a small helper in `InstructionTable` to generate handlers from `(dest, src)` pairs:

```csharp
private static InstructionHandler MakeLdReg(Action<CpuRegisters, byte> setDest, Func<CpuRegisters, byte> getSrc, int cycles)
    => (ref CpuRegisters r, IInstructionBus bus) => { setDest(r, getSrc(r)); return cycles; };

// ... then emit 63 entries in a loop or explicitly.
```

(The explicit approach is verbose but more readable and matches how other emulator codebases do it. Prefer explicit handlers — IDE navigation is better and there's no runtime allocation.)

Implement all 63 opcodes. Pay attention to `$76` being `HALT`, not `LD (HL),(HL)`.

- [ ] **Step 2: Add a table-driven test**

```csharp
[Test]
[Arguments(0x40, "B", "B")] [Arguments(0x41, "B", "C")] [Arguments(0x42, "B", "D")]
// ... all 63 combinations
public async Task LdRegReg(byte opcode, string dest, string src)
{
    var gb = SystemWithCode(opcode);
    // Set source register to known value
    // ...
    gb.StepInstruction();
    // Assert destination equals source
}
```

(TUnit's `[Arguments]` attribute provides table-driven tests.)

- [ ] **Step 3: Run and commit**

```bash
git add src/Koh.Emulator.Core/Cpu/InstructionTable.cs tests/Koh.Emulator.Core.Tests/RegRegLoadTests.cs
git commit -m "feat(cpu): add all 63 8-bit register-to-register loads"
```

---

### Task 3.B.4: Indirect and I/O loads

**Files:**
- Modify: `src/Koh.Emulator.Core/Cpu/InstructionTable.cs`

Opcodes: `$02 LD (BC),A`, `$0A LD A,(BC)`, `$12 LD (DE),A`, `$1A LD A,(DE)`, `$22 LD (HL+),A`, `$2A LD A,(HL+)`, `$32 LD (HL-),A`, `$3A LD A,(HL-)`, `$E0 LDH (a8),A`, `$F0 LDH A,(a8)`, `$E2 LD (C),A`, `$F2 LD A,(C)`, `$EA LD (a16),A`, `$FA LD A,(a16)`, `$F8 LD HL,SP+r8`, `$F9 LD SP,HL`, `$08 LD (a16),SP`.

- [ ] **Step 1: Implement each handler**

Example for `$22`:

```csharp
table[0x22] = (ref CpuRegisters r, IInstructionBus bus) =>
{
    bus.WriteByte(r.HL, r.A);
    r.HL++;
    return 8;
};

table[0xF8] = (ref CpuRegisters r, IInstructionBus bus) =>
{
    sbyte offset = (sbyte)bus.ReadImmediate();
    r.HL = Alu.AddSpRel(ref r, r.Sp, offset);
    return 12;
};
```

- [ ] **Step 2: Write representative tests (one per opcode, assert effect on registers/memory)**

File `tests/Koh.Emulator.Core.Tests/IndirectLoadTests.cs`.

- [ ] **Step 3: Run and commit**

```bash
git add src/Koh.Emulator.Core/Cpu/InstructionTable.cs tests/Koh.Emulator.Core.Tests/IndirectLoadTests.cs
git commit -m "feat(cpu): add indirect and I/O load opcodes"
```

---

### Task 3.B.5: Inc/dec and 16-bit arithmetic

**Files:**
- Modify: `src/Koh.Emulator.Core/Cpu/InstructionTable.cs`

Opcodes:
- 8-bit inc ($04, $0C, $14, $1C, $24, $2C, $34, $3C)
- 8-bit dec ($05, $0D, $15, $1D, $25, $2D, $35, $3D)
- 16-bit inc ($03, $13, $23, $33)
- 16-bit dec ($0B, $1B, $2B, $3B)
- 16-bit ADD HL,rr ($09, $19, $29, $39)
- ADD SP,r8 ($E8)

- [ ] **Step 1: Implement all handlers**

Each uses `Alu.Inc`, `Alu.Dec`, `Alu.AddHL`, or `Alu.AddSpRel`.

- [ ] **Step 2: Write tests focused on flag side effects**

```csharp
[Test]
public async Task IncB_Sets_Zero_On_Wrap()
{
    var gb = SystemWithCode(0x04);  // INC B
    // Manually set B = 0xFF
    gb.Cpu.Registers.B = 0xFF;
    gb.StepInstruction();
    await Assert.That(gb.Registers.B).IsEqualTo((byte)0);
    await Assert.That(gb.Registers.FlagSet(CpuRegisters.FlagZ)).IsTrue();
    await Assert.That(gb.Registers.FlagSet(CpuRegisters.FlagH)).IsTrue();
}

[Test]
public async Task AddHL_BC_SetsCarry_On_Overflow()
{
    var gb = SystemWithCode(0x09);
    gb.Cpu.Registers.HL = 0xFFFF;
    gb.Cpu.Registers.BC = 0x0001;
    gb.StepInstruction();
    await Assert.That(gb.Registers.HL).IsEqualTo((ushort)0);
    await Assert.That(gb.Registers.FlagSet(CpuRegisters.FlagC)).IsTrue();
}
```

- [ ] **Step 3: Run and commit**

```bash
git add src/Koh.Emulator.Core/Cpu/InstructionTable.cs tests/Koh.Emulator.Core.Tests/IncDecArithTests.cs
git commit -m "feat(cpu): add inc/dec and 16-bit arithmetic opcodes"
```

---

### Task 3.B.6: ALU A,r and ALU A,d8 ($80-$BF, $C6, $CE, $D6, $DE, $E6, $EE, $F6, $FE)

- [ ] **Step 1: Implement handlers using the `Alu` helpers**

Pattern:

```csharp
// ADD A,B ($80)
table[0x80] = (ref CpuRegisters r, IInstructionBus bus) => { r.A = Alu.Add(ref r, r.A, r.B); return 4; };

// ADD A,(HL) ($86)
table[0x86] = (ref CpuRegisters r, IInstructionBus bus) => { r.A = Alu.Add(ref r, r.A, bus.ReadByte(r.HL)); return 8; };

// ADD A,d8 ($C6)
table[0xC6] = (ref CpuRegisters r, IInstructionBus bus) => { r.A = Alu.Add(ref r, r.A, bus.ReadImmediate()); return 8; };
```

All 64 ALU A,r opcodes + 8 ALU A,d8 opcodes.

- [ ] **Step 2: Tests**

File `tests/Koh.Emulator.Core.Tests/AluInstructionTests.cs`. Representative tests per operation:

```csharp
[Test] public async Task AddA_B() { /* ... */ }
[Test] public async Task AdcA_C() { /* ... */ }
[Test] public async Task SubA_D() { /* ... */ }
// ... one per operation + one ALU A,(HL) + one ALU A,d8
```

- [ ] **Step 3: Run and commit**

```bash
git add src/Koh.Emulator.Core/Cpu/InstructionTable.cs tests/Koh.Emulator.Core.Tests/AluInstructionTests.cs
git commit -m "feat(cpu): add ALU A,r and ALU A,d8 opcodes"
```

---

### Task 3.B.7: Rotates in main table (RLCA, RLA, RRCA, RRA)

- [ ] **Step 1: Implement $07, $17, $0F, $1F using `Alu.Rlc/Rl/Rrc/Rr` with `clearZ: true`**

- [ ] **Step 2: Tests**

- [ ] **Step 3: Commit**

---

### Task 3.B.8: Jumps, calls, returns, RST

- [ ] **Step 1: Implement all jump/call/return/RST opcodes**

Full opcode list:
- $C3 JP, $C2/$CA/$D2/$DA JP cc
- $18 JR, $20/$28/$30/$38 JR cc
- $E9 JP (HL)
- $CD CALL, $C4/$CC/$D4/$DC CALL cc
- $C9 RET, $C0/$C8/$D0/$D8 RET cc, $D9 RETI
- $C7/$CF/$D7/$DF/$E7/$EF/$F7/$FF RST

Cycle counts depend on branch taken/not-taken for conditional variants — use the cycle counts from the reference table (Task 3.A.1).

- [ ] **Step 2: Write tests including conditional cycle counts**

```csharp
[Test]
public async Task JrZ_Taken_When_Z_Set()
{
    var gb = SystemWithCode(0x28, 0x10);  // JR Z,+16
    gb.Cpu.Registers.SetFlag(CpuRegisters.FlagZ, true);
    var before = gb.Registers.Pc;
    gb.StepInstruction();
    await Assert.That(gb.Registers.Pc).IsEqualTo((ushort)(before + 2 + 0x10));
}

[Test]
public async Task JrZ_Not_Taken_When_Z_Clear()
{
    var gb = SystemWithCode(0x28, 0x10);
    gb.Cpu.Registers.SetFlag(CpuRegisters.FlagZ, false);
    var before = gb.Registers.Pc;
    gb.StepInstruction();
    await Assert.That(gb.Registers.Pc).IsEqualTo((ushort)(before + 2));
}

[Test]
public async Task Rst_38H_Pushes_And_Jumps()
{
    var gb = SystemWithCode(0xFF);
    gb.Cpu.Registers.Sp = 0xFFFE;
    gb.StepInstruction();
    await Assert.That(gb.Registers.Pc).IsEqualTo((ushort)0x0038);
    await Assert.That(gb.Registers.Sp).IsEqualTo((ushort)0xFFFC);
}
```

- [ ] **Step 3: Commit**

```bash
git add src/Koh.Emulator.Core/Cpu/InstructionTable.cs tests/Koh.Emulator.Core.Tests/JumpCallReturnTests.cs
git commit -m "feat(cpu): add jump, call, return, RST opcodes"
```

---

### Task 3.B.9: Stack ops (PUSH/POP)

- [ ] **Step 1: Implement $C1/C5/D1/D5/E1/E5/F1/F5**

```csharp
// PUSH BC ($C5) — 16 T-cycles
table[0xC5] = (ref CpuRegisters r, IInstructionBus bus) =>
{
    r.Sp -= 2;
    bus.WriteByte((ushort)(r.Sp + 1), r.B);
    bus.WriteByte(r.Sp, r.C);
    return 16;
};

// POP BC ($C1) — 12 T-cycles
table[0xC1] = (ref CpuRegisters r, IInstructionBus bus) =>
{
    r.C = bus.ReadByte(r.Sp);
    r.B = bus.ReadByte((ushort)(r.Sp + 1));
    r.Sp += 2;
    return 12;
};

// POP AF ($F1) — special: F lower 4 bits are always 0
table[0xF1] = (ref CpuRegisters r, IInstructionBus bus) =>
{
    r.F = (byte)(bus.ReadByte(r.Sp) & 0xF0);
    r.A = bus.ReadByte((ushort)(r.Sp + 1));
    r.Sp += 2;
    return 12;
};
```

- [ ] **Step 2: Tests**

- [ ] **Step 3: Commit**

---

### Task 3.B.10: Handle unimplemented ($D3, $DB, $DD, $E3, $E4, $EB, $EC, $ED, $F4, $FC, $FD)

These are genuinely undefined on the SM83 and should produce a halt-with-diagnostic.

- [ ] **Step 1: Mark them explicitly in `InstructionTable`**

```csharp
foreach (byte invalid in new byte[] { 0xD3, 0xDB, 0xDD, 0xE3, 0xE4, 0xEB, 0xEC, 0xED, 0xF4, 0xFC, 0xFD })
{
    table[invalid] = (ref CpuRegisters r, IInstructionBus bus) =>
    {
        bus.ReportUndefinedOpcode();
        return 4;
    };
}
```

Add `void ReportUndefinedOpcode();` to `IInstructionBus` and have `Sm83` set `Halted = true` with a reason that propagates as `StopReason.HaltedBySystem`.

- [ ] **Step 2: Test**

```csharp
[Test]
public async Task Undefined_Opcode_Halts_System()
{
    var gb = SystemWithCode(0xD3);
    var result = gb.RunFrame();
    await Assert.That(result.Reason).IsEqualTo(StopReason.HaltedBySystem);
}
```

- [ ] **Step 3: Commit**

```bash
git add src/Koh.Emulator.Core/Cpu/InstructionTable.cs src/Koh.Emulator.Core/Cpu/Sm83.cs tests/Koh.Emulator.Core.Tests/UndefinedOpcodeTests.cs
git commit -m "feat(cpu): handle undefined opcodes with HaltedBySystem stop reason"
```

---

## Phase 3-C: CB-prefixed instruction families

### Task 3.C.1: Rotates and shifts (CB $00-$3F)

**Files:**
- Create: `src/Koh.Emulator.Core/Cpu/CbInstructionTable.cs`
- Modify: `src/Koh.Emulator.Core/Cpu/InstructionTable.cs` (add $CB prefix dispatch)

- [ ] **Step 1: Create the CB table**

```csharp
namespace Koh.Emulator.Core.Cpu;

public static class CbInstructionTable
{
    public static readonly InstructionTable.InstructionHandler?[] Cb = Build();

    private static InstructionTable.InstructionHandler?[] Build()
    {
        var table = new InstructionTable.InstructionHandler?[256];

        // RLC r ($00-$07): r = rlc(r)
        table[0x00] = (ref CpuRegisters r, InstructionTable.IInstructionBus bus) => { r.B = Alu.Rlc(ref r, r.B, clearZ: false); return 8; };
        table[0x01] = (ref CpuRegisters r, InstructionTable.IInstructionBus bus) => { r.C = Alu.Rlc(ref r, r.C, clearZ: false); return 8; };
        table[0x02] = (ref CpuRegisters r, InstructionTable.IInstructionBus bus) => { r.D = Alu.Rlc(ref r, r.D, clearZ: false); return 8; };
        table[0x03] = (ref CpuRegisters r, InstructionTable.IInstructionBus bus) => { r.E = Alu.Rlc(ref r, r.E, clearZ: false); return 8; };
        table[0x04] = (ref CpuRegisters r, InstructionTable.IInstructionBus bus) => { r.H = Alu.Rlc(ref r, r.H, clearZ: false); return 8; };
        table[0x05] = (ref CpuRegisters r, InstructionTable.IInstructionBus bus) => { r.L = Alu.Rlc(ref r, r.L, clearZ: false); return 8; };
        table[0x06] = (ref CpuRegisters r, InstructionTable.IInstructionBus bus) =>
        {
            byte v = bus.ReadByte(r.HL);
            bus.WriteByte(r.HL, Alu.Rlc(ref r, v, clearZ: false));
            return 16;
        };
        table[0x07] = (ref CpuRegisters r, InstructionTable.IInstructionBus bus) => { r.A = Alu.Rlc(ref r, r.A, clearZ: false); return 8; };

        // RRC r ($08-$0F)
        // RL r ($10-$17)
        // RR r ($18-$1F)
        // SLA r ($20-$27)
        // SRA r ($28-$2F)
        // SWAP r ($30-$37)
        // SRL r ($38-$3F)
        //
        // Pattern is identical — 7 register variants + 1 (HL) variant per operation.
        // Fill in 64 entries total.

        return table;
    }
}
```

- [ ] **Step 2: Wire $CB dispatch in `InstructionTable`**

```csharp
// CB prefix ($CB)
table[0xCB] = (ref CpuRegisters r, IInstructionBus bus) =>
{
    byte cbOpcode = bus.ReadImmediate();
    var handler = CbInstructionTable.Cb[cbOpcode];
    if (handler is null)
    {
        bus.ReportUndefinedOpcode();
        return 4;
    }
    return handler(ref r, bus);
};
```

(The $CB prefix itself is 4 T-cycles; the CB handlers return their own cycles which include the prefix. Standard convention: `CB XX` CB handlers return the full cycle count.)

- [ ] **Step 3: Tests — one per operation, pattern similar to main-table tests**

File `tests/Koh.Emulator.Core.Tests/CbRotateShiftTests.cs`.

- [ ] **Step 4: Commit**

```bash
git add src/Koh.Emulator.Core/Cpu/CbInstructionTable.cs src/Koh.Emulator.Core/Cpu/InstructionTable.cs tests/Koh.Emulator.Core.Tests/CbRotateShiftTests.cs
git commit -m "feat(cpu): add CB-prefixed rotates and shifts ($00-$3F)"
```

---

### Task 3.C.2: BIT n,r (CB $40-$7F)

- [ ] **Step 1: Implement all 64 BIT n,r handlers**

```csharp
// BIT 0,B ($40)
table[0x40] = (ref CpuRegisters r, InstructionTable.IInstructionBus bus) => { Alu.Bit(ref r, r.B, 0); return 8; };
// ... 63 more
// BIT 0,(HL) ($46)
table[0x46] = (ref CpuRegisters r, InstructionTable.IInstructionBus bus) =>
{
    Alu.Bit(ref r, bus.ReadByte(r.HL), 0);
    return 12;
};
```

- [ ] **Step 2: Tests** (representative: test each bit index × at least one register target)

- [ ] **Step 3: Commit**

---

### Task 3.C.3: RES n,r and SET n,r (CB $80-$FF)

- [ ] **Step 1: Implement 64 RES + 64 SET handlers**

```csharp
// RES 0,B ($80)
table[0x80] = (ref CpuRegisters r, InstructionTable.IInstructionBus bus) => { r.B = Alu.Res(r.B, 0); return 8; };
// RES 0,(HL) ($86)
table[0x86] = (ref CpuRegisters r, InstructionTable.IInstructionBus bus) =>
{
    byte v = bus.ReadByte(r.HL);
    bus.WriteByte(r.HL, Alu.Res(v, 0));
    return 16;
};
// SET 0,B ($C0)
table[0xC0] = (ref CpuRegisters r, InstructionTable.IInstructionBus bus) => { r.B = Alu.Set(r.B, 0); return 8; };
```

- [ ] **Step 2: Tests**

- [ ] **Step 3: Commit**

```bash
git add src/Koh.Emulator.Core/Cpu/CbInstructionTable.cs tests/Koh.Emulator.Core.Tests/CbBitOpsTests.cs
git commit -m "feat(cpu): add CB-prefixed BIT/RES/SET opcodes"
```

---

## Phase 3-D: Interrupt dispatch and HALT

### Task 3.D.1: Interrupt dispatch sequence

**Files:**
- Create: `src/Koh.Emulator.Core/Cpu/InterruptDispatch.cs`
- Modify: `src/Koh.Emulator.Core/Cpu/Sm83.cs`

Per spec §3 and §7.4: interrupts are serviced at instruction boundaries. The dispatch sequence is 5 M-cycles (20 T-cycles): 2 internal, 2 stack writes (PC high then low), 1 PC reload.

- [ ] **Step 1: Create `InterruptDispatch.cs`**

```csharp
namespace Koh.Emulator.Core.Cpu;

public static class InterruptDispatch
{
    public static readonly ushort[] VectorAddresses = { 0x0040, 0x0048, 0x0050, 0x0058, 0x0060 };
    public static readonly byte[] Flags = { Interrupts.VBlank, Interrupts.Stat, Interrupts.Timer, Interrupts.Serial, Interrupts.Joypad };

    public static int Execute(ref CpuRegisters regs, ref Interrupts interrupts, InstructionTable.IInstructionBus bus)
    {
        int pendingBit = GetHighestPriority(interrupts);
        if (pendingBit < 0) return 0;

        byte flag = Flags[pendingBit];
        ushort vector = VectorAddresses[pendingBit];

        // 5 M-cycle sequence:
        // - 2 internal cycles
        // - 2 stack writes (PC high, PC low)
        // - 1 PC reload
        interrupts.IME = false;
        interrupts.Clear(flag);

        regs.Sp -= 2;
        bus.WriteByte((ushort)(regs.Sp + 1), (byte)(regs.Pc >> 8));
        bus.WriteByte(regs.Sp, (byte)(regs.Pc & 0xFF));
        regs.Pc = vector;

        return 20;
    }

    private static int GetHighestPriority(in Interrupts interrupts)
    {
        byte pending = interrupts.Pending;
        if (pending == 0) return -1;
        for (int i = 0; i < 5; i++)
        {
            if ((pending & (1 << i)) != 0) return i;
        }
        return -1;
    }
}
```

- [ ] **Step 2: Update `Sm83.ExecuteNextInstruction` to check interrupts first**

```csharp
private void ExecuteNextInstruction()
{
    // Consume EI delay slot before this instruction's interrupt check.
    if (_eiDelayArmed)
    {
        Interrupts.IME = true;
        _eiDelayArmed = false;
        // But: the interrupt serviced on THIS instruction boundary uses the
        // pre-EI IME value. So the check is done below against the already-
        // updated IME, which means EI immediately followed by a pending IRQ
        // services the IRQ AFTER the instruction after EI. Standard semantics.
    }

    // Interrupt dispatch check — instruction-boundary only per spec §7.4.
    if (Interrupts.IME && Interrupts.HasPending)
    {
        int cycles = InterruptDispatch.Execute(ref Registers, ref Interrupts, this);
        if (cycles > 0)
        {
            _tCyclesRemainingInInstruction = cycles - 1;
            return;
        }
    }

    // If HALT and no pending interrupt, stay halted (consume 4 T-cycles).
    if (Halted)
    {
        if (Interrupts.HasPending)
        {
            Halted = false;
            // HALT bug: when IME=0 and a pending IRQ wakes HALT, the next
            // instruction fetch does NOT increment PC (the same byte is
            // executed twice). Set a flag.
            if (!Interrupts.IME) _haltBugNextFetch = true;
        }
        else
        {
            _tCyclesRemainingInInstruction = 3;
            return;
        }
    }

    byte opcode = ReadImmediate();
    if (_haltBugNextFetch)
    {
        Registers.Pc--;
        _haltBugNextFetch = false;
    }

    var handler = InstructionTable.Unprefixed[opcode];
    if (handler is null)
    {
        Halted = true;
        _tCyclesRemainingInInstruction = 4;
        return;
    }
    int instrCycles = handler(ref Registers, this);
    _tCyclesRemainingInInstruction = instrCycles - 1;
}
```

- [ ] **Step 3: Build, test, commit**

```bash
git add src/Koh.Emulator.Core/Cpu/InterruptDispatch.cs src/Koh.Emulator.Core/Cpu/Sm83.cs
git commit -m "feat(cpu): add interrupt dispatch sequence and EI delay slot"
```

---

### Task 3.D.2: HALT bug and EI delay tests

**Files:**
- Create: `tests/Koh.Emulator.Core.Tests/InterruptAndHaltTests.cs`

- [ ] **Step 1: Write hazard tests**

```csharp
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;
using Koh.Emulator.Core.Cpu;

namespace Koh.Emulator.Core.Tests;

public class InterruptAndHaltTests
{
    [Test]
    public async Task Interrupt_Dispatch_Pushes_Pc_And_Jumps_To_Vector()
    {
        var rom = new byte[0x8000];
        rom[0x0100] = 0x00;  // NOP
        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.Io.Interrupts.IME = true;
        gb.Io.Interrupts.IE = Interrupts.VBlank;
        gb.Io.Interrupts.Raise(Interrupts.VBlank);

        gb.StepInstruction();  // execute the NOP; at the next instruction boundary, dispatch runs
        gb.StepInstruction();  // dispatch

        await Assert.That(gb.Registers.Pc).IsEqualTo((ushort)0x0040);
    }

    [Test]
    public async Task Ei_Delay_Slot_Delays_Interrupt_By_One_Instruction()
    {
        var rom = new byte[0x8000];
        rom[0x0100] = 0xFB;  // EI
        rom[0x0101] = 0x00;  // NOP
        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.Io.Interrupts.IE = Interrupts.VBlank;
        gb.Io.Interrupts.Raise(Interrupts.VBlank);

        gb.StepInstruction();  // EI — IME becomes true at this instruction boundary
        await Assert.That(gb.Io.Interrupts.IME).IsTrue();

        gb.StepInstruction();  // NOP — runs BEFORE dispatch
        await Assert.That(gb.Registers.Pc).IsEqualTo((ushort)0x0102);

        gb.StepInstruction();  // dispatch
        await Assert.That(gb.Registers.Pc).IsEqualTo((ushort)0x0040);
    }

    [Test]
    public async Task Halt_Bug_Fetches_Same_Byte_Twice_When_Ime_Clear_And_Irq_Pending()
    {
        var rom = new byte[0x8000];
        rom[0x0100] = 0x76;  // HALT
        rom[0x0101] = 0x3C;  // INC A (executed twice because of HALT bug)
        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.Io.Interrupts.IME = false;
        gb.Io.Interrupts.IE = Interrupts.VBlank;
        gb.Io.Interrupts.Raise(Interrupts.VBlank);

        gb.StepInstruction();  // HALT with pending IRQ + IME=0 → wakes with HALT bug
        gb.StepInstruction();  // INC A
        gb.StepInstruction();  // INC A (bug: same byte fetched again)

        await Assert.That(gb.Registers.A).IsEqualTo((byte)2);
    }
}
```

- [ ] **Step 2: Run and iterate**

These tests will drive bug fixes in the dispatch logic. Iterate until all three pass.

- [ ] **Step 3: Commit**

```bash
git add tests/Koh.Emulator.Core.Tests/InterruptAndHaltTests.cs
git commit -m "test(cpu): add interrupt dispatch, EI delay, and HALT bug tests"
```

---

## Phase 3-E: Blargg test ROMs

### Task 3.E.1: Populate Blargg ROMs in download script

**Files:**
- Modify: `scripts/download-test-roms.sh` and `.ps1`

- [ ] **Step 1: Add Blargg test ROM URLs + SHA-256 hashes**

Blargg test ROMs are publicly distributed. Add these to the download script:

- `cpu_instrs/individual/01-special.gb` through `11-op a,(hl).gb`
- `cpu_instrs/cpu_instrs.gb` (all-in-one)
- `instr_timing/instr_timing.gb`
- `mem_timing/mem_timing.gb`
- `mem_timing-2/mem_timing-2.gb`
- `halt_bug.gb`
- `interrupt_time/interrupt_time.gb`

Each entry follows the same `download_with_hash` pattern from Phase 2's `acid2` task.

- [ ] **Step 2: Run locally and verify**

- [ ] **Step 3: Commit**

```bash
git add scripts/download-test-roms.sh scripts/download-test-roms.ps1
git commit -m "chore: add Blargg test ROMs to download script"
```

---

### Task 3.E.2: Blargg serial-output harness and test runner

**Files:**
- Create: `tests/Koh.Compat.Tests/Emulation/BlarggTests.cs`
- Modify: `src/Koh.Emulator.Core/Serial/Serial.cs` (expose output buffer)

Blargg ROMs report pass/fail via the serial port ($FF01 = data, $FF02 = control; writing $81 to $FF02 triggers a "serial transfer"). The emulator's serial stub buffers the bytes; the test harness reads the buffer and looks for "Passed" or "Failed".

- [ ] **Step 1: Update `Serial.cs` to buffer written bytes**

```csharp
namespace Koh.Emulator.Core.Serial;

public sealed class Serial
{
    private readonly List<byte> _buffer = new();

    public byte SB;
    public byte SC;

    public void WriteSB(byte value) => SB = value;

    public void WriteSC(byte value)
    {
        SC = value;
        if ((value & 0x81) == 0x81)   // start transfer at internal clock
        {
            _buffer.Add(SB);
            // Finish "immediately" (ignore the real 8-bit shift timing for Phase 3)
            SC = (byte)(value & 0x7F);
        }
    }

    public string ReadBufferAsString() => System.Text.Encoding.ASCII.GetString(_buffer.ToArray());
    public void ClearBuffer() => _buffer.Clear();
}
```

Wire $FF01/$FF02 in `IoRegisters` to `Serial`.

- [ ] **Step 2: Create `BlarggTests.cs`**

```csharp
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Compat.Tests.Emulation;

public class BlarggTests
{
    private static string FixtureRoot => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tests", "fixtures", "test-roms");

    private static async Task RunBlarggTest(string romFileName, TimeSpan maxWallClock)
    {
        var romPath = Path.Combine(FixtureRoot, romFileName);
        if (!File.Exists(romPath))
            throw new FileNotFoundException($"Blargg ROM missing: {romPath}. Run scripts/download-test-roms.sh.");

        var rom = await File.ReadAllBytesAsync(romPath);
        var cart = CartridgeFactory.Load(rom);
        var gb = new GameBoySystem(HardwareMode.Dmg, cart);

        var deadline = DateTime.UtcNow + maxWallClock;
        while (DateTime.UtcNow < deadline)
        {
            gb.RunFrame();
            var output = gb.Io.Serial.ReadBufferAsString();
            if (output.Contains("Passed")) return;
            if (output.Contains("Failed"))
                throw new Exception($"Blargg test failed: {output}");
        }

        throw new TimeoutException($"Blargg test {romFileName} did not report pass/fail within {maxWallClock}: {gb.Io.Serial.ReadBufferAsString()}");
    }

    [Test] public async Task CpuInstrs_01_Special() => await RunBlarggTest("cpu_instrs/individual/01-special.gb", TimeSpan.FromMinutes(2));
    [Test] public async Task CpuInstrs_02_Interrupts() => await RunBlarggTest("cpu_instrs/individual/02-interrupts.gb", TimeSpan.FromMinutes(2));
    [Test] public async Task CpuInstrs_03_Op_Sp_Hl() => await RunBlarggTest("cpu_instrs/individual/03-op sp,hl.gb", TimeSpan.FromMinutes(2));
    // ... 8 more
    [Test] public async Task CpuInstrs_11_Op_A_Hl() => await RunBlarggTest("cpu_instrs/individual/11-op a,(hl).gb", TimeSpan.FromMinutes(5));
    [Test] public async Task InstrTiming() => await RunBlarggTest("instr_timing/instr_timing.gb", TimeSpan.FromMinutes(2));
    [Test] public async Task MemTiming() => await RunBlarggTest("mem_timing/mem_timing.gb", TimeSpan.FromMinutes(2));
    [Test] public async Task MemTiming2() => await RunBlarggTest("mem_timing-2/mem_timing-2.gb", TimeSpan.FromMinutes(2));
    [Test] public async Task HaltBug() => await RunBlarggTest("halt_bug.gb", TimeSpan.FromMinutes(2));
    [Test] public async Task InterruptTime() => await RunBlarggTest("interrupt_time/interrupt_time.gb", TimeSpan.FromMinutes(2));
}
```

- [ ] **Step 3: Run the tests**

Run: `bash scripts/download-test-roms.sh`
Run: `dotnet test tests/Koh.Compat.Tests/Koh.Compat.Tests.csproj --filter BlarggTests`

**Expected first run:** many will fail. This is normal. Each failure points at a specific CPU bug — iterate on `InstructionTable`, `Alu`, or `InterruptDispatch` until every Blargg test passes.

Fixing these is the bulk of Phase 3's work. Budget multiple days for debugging. Commit iteratively: a commit per bug fix with a clear message about which Blargg sub-test drove the fix.

- [ ] **Step 4: Final commit when all Blargg tests pass**

```bash
git add tests/Koh.Compat.Tests/Emulation/BlarggTests.cs src/Koh.Emulator.Core/Serial/Serial.cs src/Koh.Emulator.Core/Bus/IoRegisters.cs
git commit -m "test(compat): Blargg cpu_instrs + instr_timing + mem_timing + halt_bug + interrupt_time passing"
```

---

### Task 3.E.3: Mooneye acceptance subset

**Files:**
- Modify: `scripts/download-test-roms.sh` and `.ps1`
- Create: `tests/Koh.Compat.Tests/Emulation/MooneyeTests.cs`

Mooneye test ROMs report pass/fail via a Fibonacci register pattern: `B=3, C=5, D=8, E=13, H=21, L=34` on success.

- [ ] **Step 1: Add Mooneye ROMs to download script**

Subset: `acceptance/bits/*`, `acceptance/timer/*`, `acceptance/interrupts/*`, `acceptance/oam_dma/*`.

- [ ] **Step 2: Create `MooneyeTests.cs`**

```csharp
public class MooneyeTests
{
    private static async Task RunMooneyeTest(string romPath)
    {
        // ... load ROM ...
        for (int frame = 0; frame < 600; frame++) gb.RunFrame();

        bool passed = gb.Registers.B == 3 && gb.Registers.C == 5 &&
                      gb.Registers.D == 8 && gb.Registers.E == 13 &&
                      gb.Registers.H == 21 && gb.Registers.L == 34;
        if (!passed)
            throw new Exception($"Mooneye test failed: registers B={gb.Registers.B} C={gb.Registers.C} ...");
    }

    [Test] public async Task Bits_Mem_Oam() => await RunMooneyeTest("acceptance/bits/mem_oam.gb");
    [Test] public async Task Bits_Reg_F() => await RunMooneyeTest("acceptance/bits/reg_f.gb");
    [Test] public async Task Bits_Unused_Hwio_GS() => await RunMooneyeTest("acceptance/bits/unused_hwio-GS.gb");
    // ... timer/*, interrupts/*, oam_dma/*
}
```

- [ ] **Step 3: Run and iterate**

Run: `dotnet test tests/Koh.Compat.Tests/Koh.Compat.Tests.csproj --filter MooneyeTests`

Expect more bug fixes. Mooneye is stricter than Blargg on timing edge cases.

- [ ] **Step 4: Commit**

```bash
git add tests/Koh.Compat.Tests/Emulation/MooneyeTests.cs scripts/download-test-roms.sh scripts/download-test-roms.ps1
git commit -m "test(compat): Mooneye acceptance subset (bits/timer/interrupts/oam_dma) passing"
```

---

## Phase 3-F: Full debugger features

### Task 3.F.1: Breakpoint halting

**Files:**
- Modify: `src/Koh.Debugger/Session/BreakpointManager.cs`
- Modify: `src/Koh.Emulator.Core/GameBoySystem.cs`

- [ ] **Step 1: Expose BreakpointManager to `GameBoySystem`**

Add a `BreakpointManager` reference to `GameBoySystem` (or a lightweight interface that only exposes `Check(bank, pc)`). Check it in `RunFrame` / `RunUntil` at each instruction boundary.

```csharp
// In Sm83.TickT() after incrementing _tCyclesRemainingInInstruction:
if (_tCyclesRemainingInInstruction == 0 && _breakpointChecker?.Invoke(_currentBank, Registers.Pc) == true)
{
    _breakpointHit = true;
    return true;
}
```

Propagate `_breakpointHit` to `GameBoySystem.RunFrame` which returns `StopReason.Breakpoint`.

- [ ] **Step 2: Wire up `_currentBank` tracking**

The current ROM bank for breakpoint matching comes from `Cartridge` state. Expose a helper:

```csharp
public byte CurrentPcBank(ushort pc) =>
    pc < 0x4000 ? (byte)0 : (byte)(Cartridge.Kind == MapperKind.Mbc1 ?
        ((Cartridge.Mbc1_BankHigh << 5) | (Cartridge.Mbc1_BankLow == 0 ? 1 : Cartridge.Mbc1_BankLow)) : 0);
```

- [ ] **Step 3: Test — halt on breakpoint**

```csharp
[Test]
public async Task Breakpoint_Halts_Execution_At_Specified_Pc()
{
    var rom = new byte[0x8000];
    rom[0x0100] = 0x00; rom[0x0101] = 0x00; rom[0x0102] = 0x00;
    var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
    var breakpoints = new BreakpointManager();
    breakpoints.Add(new BankedAddress(0, 0x0102));
    gb.BreakpointChecker = (bank, pc) => breakpoints.Contains(new BankedAddress(bank, pc));

    var result = gb.RunFrame();
    await Assert.That(result.Reason).IsEqualTo(StopReason.Breakpoint);
    await Assert.That(gb.Registers.Pc).IsEqualTo((ushort)0x0102);
}
```

- [ ] **Step 4: Commit**

```bash
git add src/Koh.Debugger/Session/BreakpointManager.cs src/Koh.Emulator.Core/GameBoySystem.cs src/Koh.Emulator.Core/Cpu/Sm83.cs tests/Koh.Debugger.Tests/BreakpointHaltTests.cs
git commit -m "feat(debugger): make breakpoints halt execution with StopReason.Breakpoint"
```

---

### Task 3.F.2: Step-over / step-in / step-out handlers

**Files:**
- Create: `src/Koh.Debugger/Dap/Handlers/NextHandler.cs` (step-over)
- Create: `src/Koh.Debugger/Dap/Handlers/StepInHandler.cs`
- Create: `src/Koh.Debugger/Dap/Handlers/StepOutHandler.cs`
- Create: `src/Koh.Debugger/Dap/Messages/StepMessages.cs`
- Create: `src/Koh.Debugger/Session/StepStrategy.cs`

Step-over executes the current instruction and stops. For `CALL`, it runs until the matching `RET` (ideally by recording the return address and setting a temporary breakpoint there). Step-in is equivalent to `StepInstruction`. Step-out records the current SP and runs until the stack pops back above that SP.

- [ ] **Step 1: Create `StepMessages.cs`**

```csharp
using System.Text.Json.Serialization;

namespace Koh.Debugger.Dap.Messages;

public sealed class NextArguments
{
    [JsonPropertyName("threadId")] public int ThreadId { get; set; }
    [JsonPropertyName("granularity")] public string? Granularity { get; set; }
}

public sealed class StepInArguments
{
    [JsonPropertyName("threadId")] public int ThreadId { get; set; }
    [JsonPropertyName("granularity")] public string? Granularity { get; set; }
}

public sealed class StepOutArguments
{
    [JsonPropertyName("threadId")] public int ThreadId { get; set; }
}
```

- [ ] **Step 2: Create `StepStrategy.cs`**

```csharp
using Koh.Emulator.Core;

namespace Koh.Debugger.Session;

public static class StepStrategy
{
    public static StepResult StepOver(GameBoySystem gb)
    {
        ushort currentPc = gb.Registers.Pc;
        byte opcode = gb.DebugReadByte(currentPc);
        int instructionLength = OpcodeLength(opcode, gb);

        if (IsCallOrRst(opcode))
        {
            ushort returnPc = (ushort)(currentPc + instructionLength);
            // Run until PC == returnPc (or timeout).
            var condition = StopCondition.AtPc(returnPc);
            return gb.RunUntil(condition);
        }
        else
        {
            return gb.StepInstruction();
        }
    }

    public static StepResult StepIn(GameBoySystem gb) => gb.StepInstruction();

    public static StepResult StepOut(GameBoySystem gb)
    {
        ushort startSp = gb.Registers.Sp;
        // Run until SP is above the starting SP (indicating a RET has happened).
        // A dedicated StopCondition flag would be cleaner; for now, use a loop.
        while (true)
        {
            var r = gb.StepInstruction();
            if (gb.Registers.Sp > startSp) return r;
            if (r.Reason == StopReason.Breakpoint) return r;
            if (r.Reason == StopReason.FrameComplete) return r;  // safety bound
        }
    }

    private static int OpcodeLength(byte opcode, GameBoySystem gb)
    {
        // Simplified — use a lookup table generated from the opcode reference.
        // Returns 1, 2, or 3. $CB prefix returns 2.
        return OpcodeLengthTable[opcode];
    }

    private static bool IsCallOrRst(byte opcode)
    {
        return opcode is 0xCD or 0xC4 or 0xCC or 0xD4 or 0xDC ||
               (opcode & 0xC7) == 0xC7;  // RST
    }

    private static readonly int[] OpcodeLengthTable = BuildOpcodeLengthTable();

    private static int[] BuildOpcodeLengthTable()
    {
        var t = new int[256];
        // Fill based on the reference table. For now, a conservative default:
        for (int i = 0; i < 256; i++) t[i] = 1;
        // 2-byte opcodes
        foreach (var op in new[] { 0x06, 0x0E, 0x10, 0x16, 0x18, 0x1E, 0x20, 0x26, 0x28, 0x2E, 0x30, 0x36, 0x38, 0x3E,
                                    0xC6, 0xCB, 0xCE, 0xD6, 0xDE, 0xE0, 0xE6, 0xE8, 0xEE, 0xF0, 0xF6, 0xF8, 0xFE })
            t[op] = 2;
        // 3-byte opcodes
        foreach (var op in new[] { 0x01, 0x08, 0x11, 0x21, 0x31, 0xC2, 0xC3, 0xC4, 0xCA, 0xCC, 0xCD, 0xD2, 0xD4, 0xDA, 0xDC, 0xEA, 0xFA })
            t[op] = 3;
        return t;
    }
}
```

- [ ] **Step 3: Create the three handlers**

```csharp
// NextHandler.cs — step-over
namespace Koh.Debugger.Dap.Handlers;

public sealed class NextHandler
{
    private readonly DebugSession _session;
    public NextHandler(DebugSession s) { _session = s; }

    public Response Handle(Request request)
    {
        if (_session.System is null) return new Response { Success = false, Message = "no active session" };
        Session.StepStrategy.StepOver(_session.System);
        return new Response { Success = true };
    }
}
```

Create `StepInHandler` and `StepOutHandler` similarly.

- [ ] **Step 4: Register handlers and update capabilities**

In `DapCapabilities.Phase3()`, set:
```csharp
SupportsSteppingGranularity = true,
SupportsInstructionBreakpoints = true,
```

In `HandlerRegistration.RegisterAll`, register `next`, `stepIn`, `stepOut`.

- [ ] **Step 5: Tests**

File `tests/Koh.Debugger.Tests/SteppingTests.cs` — verifies step-over skips CALL, step-in descends, step-out returns.

- [ ] **Step 6: Commit**

```bash
git add src/Koh.Debugger/Dap/Handlers/NextHandler.cs src/Koh.Debugger/Dap/Handlers/StepInHandler.cs src/Koh.Debugger/Dap/Handlers/StepOutHandler.cs src/Koh.Debugger/Dap/Messages/StepMessages.cs src/Koh.Debugger/Session/StepStrategy.cs src/Koh.Debugger/Dap/DapCapabilities.cs src/Koh.Debugger/Dap/HandlerRegistration.cs tests/Koh.Debugger.Tests/SteppingTests.cs
git commit -m "feat(debugger): add step-over/step-in/step-out handlers"
```

---

### Task 3.F.3: Call stack walker + stackTrace handler

**Files:**
- Create: `src/Koh.Debugger/Session/CallStackWalker.cs`
- Create: `src/Koh.Debugger/Dap/Handlers/StackTraceHandler.cs`
- Create: `src/Koh.Debugger/Dap/Messages/StackTraceMessages.cs`

Per spec §8.5: heuristic SP walking — for each 16-bit value on the stack that points just after a `CALL`, record a frame. Top frame = current PC.

- [ ] **Step 1: Create `CallStackWalker.cs`**

```csharp
using Koh.Emulator.Core;
using Koh.Linker.Core;

namespace Koh.Debugger.Session;

public sealed record CallFrame(int Id, BankedAddress Address, string Name, string? SourceFile, int? SourceLine);

public sealed class CallStackWalker
{
    private readonly DebugSession _session;

    public CallStackWalker(DebugSession session) { _session = session; }

    public IReadOnlyList<CallFrame> Walk(int maxFrames = 32)
    {
        var gb = _session.System!;
        var frames = new List<CallFrame>();

        byte currentBank = gb.CurrentPcBank(gb.Registers.Pc);
        frames.Add(MakeFrame(0, new BankedAddress(currentBank, gb.Registers.Pc)));

        ushort sp = gb.Registers.Sp;
        int id = 1;

        for (int i = 0; i < maxFrames && sp < 0xFFFE && id < maxFrames; i++)
        {
            // Read a potential return address from the stack.
            byte lo = gb.DebugReadByte(sp);
            byte hi = gb.DebugReadByte((ushort)(sp + 1));
            ushort addr = (ushort)((hi << 8) | lo);

            // A valid return address points just after a CALL (3 bytes) or RST (1 byte).
            if (IsAfterCallOrRst(gb, addr))
            {
                byte bank = gb.CurrentPcBank(addr);
                frames.Add(MakeFrame(id++, new BankedAddress(bank, addr)));
                sp += 2;
            }
            else
            {
                sp++;
            }
        }

        return frames;
    }

    private bool IsAfterCallOrRst(GameBoySystem gb, ushort addr)
    {
        if (addr < 3) return false;
        byte before = gb.DebugReadByte((ushort)(addr - 3));
        if (before == 0xCD) return true;                           // CALL a16
        if (before is 0xC4 or 0xCC or 0xD4 or 0xDC) return true;   // CALL cc,a16
        byte before1 = gb.DebugReadByte((ushort)(addr - 1));
        if ((before1 & 0xC7) == 0xC7) return true;                 // RST
        return false;
    }

    private CallFrame MakeFrame(int id, BankedAddress addr)
    {
        var symbols = _session.DebugInfo.SymbolMap.LookupByAddress(addr);
        string name = symbols.Count > 0 ? symbols[0].Name : $"${addr.Bank:X2}:${addr.Address:X4}";
        return new CallFrame(id, addr, name, null, null);
    }
}
```

- [ ] **Step 2: Create DAP messages**

```csharp
// StackTraceMessages.cs
namespace Koh.Debugger.Dap.Messages;

public sealed class StackTraceArguments { public int ThreadId { get; set; } }
public sealed class StackFrame { public int Id; public string Name = ""; public Source? Source; public int Line; public int Column; }
public sealed class StackTraceResponseBody { public StackFrame[] StackFrames = []; public int TotalFrames; }
```

- [ ] **Step 3: Create `StackTraceHandler.cs`**

```csharp
public sealed class StackTraceHandler
{
    private readonly DebugSession _session;
    public StackTraceHandler(DebugSession s) { _session = s; }

    public Response Handle(Request request)
    {
        if (_session.System is null) return new Response { Success = false };
        var walker = new CallStackWalker(_session);
        var frames = walker.Walk();
        var dapFrames = frames.Select(f => new StackFrame
        {
            Id = f.Id,
            Name = f.Name,
            Line = f.SourceLine ?? 0,
        }).ToArray();
        return new Response { Success = true, Body = new StackTraceResponseBody { StackFrames = dapFrames, TotalFrames = dapFrames.Length } };
    }
}
```

- [ ] **Step 4: Register and test**

```bash
git add src/Koh.Debugger/Session/CallStackWalker.cs src/Koh.Debugger/Dap/Handlers/StackTraceHandler.cs src/Koh.Debugger/Dap/Messages/StackTraceMessages.cs src/Koh.Debugger/Dap/HandlerRegistration.cs tests/Koh.Debugger.Tests/CallStackWalkerTests.cs
git commit -m "feat(debugger): add heuristic call-stack walker + stackTrace handler"
```

---

### Task 3.F.4: Disassembler + disassemble handler

**Files:**
- Create: `src/Koh.Debugger/Session/Disassembler.cs`
- Create: `src/Koh.Debugger/Dap/Handlers/DisassembleHandler.cs`
- Create: `src/Koh.Debugger/Dap/Messages/DisassembleMessages.cs`

- [ ] **Step 1: Create `Disassembler.cs`**

A simple table-driven disassembler that mirrors `InstructionTable`. Given a banked address, returns a string mnemonic with the immediate operands decoded.

```csharp
public sealed record DisassembledInstruction(ushort Address, int Length, string Mnemonic);

public sealed class Disassembler
{
    private readonly GameBoySystem _gb;
    public Disassembler(GameBoySystem gb) { _gb = gb; }

    public DisassembledInstruction Decode(ushort address)
    {
        byte opcode = _gb.DebugReadByte(address);
        // Use a lookup table matching the mnemonics and operand patterns.
        // ... (substantial but straightforward)
        return new DisassembledInstruction(address, 1, "NOP");  // placeholder
    }

    public IEnumerable<DisassembledInstruction> DecodeRange(ushort start, int count)
    {
        ushort addr = start;
        for (int i = 0; i < count; i++)
        {
            var instr = Decode(addr);
            yield return instr;
            addr += (ushort)instr.Length;
        }
    }
}
```

The full implementation needs a mnemonic table covering all 512 opcodes with operand formatting. This is tedious but mechanical; budget time accordingly.

- [ ] **Step 2: Create DAP messages and handler**

- [ ] **Step 3: Register, test, commit**

```bash
git add src/Koh.Debugger/Session/Disassembler.cs src/Koh.Debugger/Dap/Handlers/DisassembleHandler.cs src/Koh.Debugger/Dap/Messages/DisassembleMessages.cs
git commit -m "feat(debugger): add disassembler + DAP disassemble handler"
```

---

### Task 3.F.5: Evaluate handler (symbols + literals)

**Files:**
- Create: `src/Koh.Debugger/Dap/Handlers/EvaluateHandler.cs`
- Create: `src/Koh.Debugger/Dap/Messages/EvaluateMessages.cs`

- [ ] **Step 1: Create `EvaluateMessages.cs`**

```csharp
public sealed class EvaluateArguments
{
    public string Expression = "";
    public int? FrameId;
    public string? Context;
}

public sealed class EvaluateResponseBody
{
    public string Result = "";
    public string? Type;
    public int VariablesReference;
}
```

- [ ] **Step 2: Create `EvaluateHandler.cs`**

```csharp
public sealed class EvaluateHandler
{
    private readonly DebugSession _session;
    public EvaluateHandler(DebugSession s) { _session = s; }

    public Response Handle(Request request)
    {
        var args = request.Arguments?.Deserialize(DapJsonContext.Default.EvaluateArguments);
        if (args is null) return new Response { Success = false, Message = "missing args" };

        string expr = args.Expression.Trim();

        // Hex literal?
        if (expr.StartsWith('$') && ushort.TryParse(expr[1..], NumberStyles.HexNumber, null, out ushort hex))
            return Ok($"${hex:X4}");
        if (expr.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && ushort.TryParse(expr[2..], NumberStyles.HexNumber, null, out hex))
            return Ok($"${hex:X4}");

        // Decimal literal?
        if (int.TryParse(expr, out int dec))
            return Ok($"${dec:X4} ({dec})");

        // Symbol lookup.
        var sym = _session.DebugInfo.SymbolMap.Lookup(expr);
        if (sym is not null)
            return Ok($"${sym.Bank:X2}:${sym.Address:X4} ({sym.Kind})");

        return new Response { Success = false, Message = $"unknown symbol '{expr}'" };
    }

    private Response Ok(string value) => new() { Success = true, Body = new EvaluateResponseBody { Result = value } };
}
```

- [ ] **Step 3: Register, test, commit**

```bash
git add src/Koh.Debugger/Dap/Handlers/EvaluateHandler.cs src/Koh.Debugger/Dap/Messages/EvaluateMessages.cs
git commit -m "feat(debugger): add evaluate handler (hex/decimal literals + symbol lookup)"
```

---

### Task 3.F.6: setInstructionBreakpoints, setFunctionBreakpoints, breakpointLocations handlers

**Files:**
- Create: `src/Koh.Debugger/Dap/Handlers/SetInstructionBreakpointsHandler.cs`
- Create: `src/Koh.Debugger/Dap/Handlers/SetFunctionBreakpointsHandler.cs`
- Create: `src/Koh.Debugger/Dap/Handlers/BreakpointLocationsHandler.cs`
- Create matching message files

- [ ] **Step 1: Implement the three handlers**

- `setInstructionBreakpoints`: accepts raw address strings (memory references), adds them to `BreakpointManager`.
- `setFunctionBreakpoints`: accepts symbol names, looks them up in `SymbolMap`, adds their addresses.
- `breakpointLocations`: returns the valid breakpoint lines in a source file (used by VS Code for the gutter hover UI).

- [ ] **Step 2: Register and test**

- [ ] **Step 3: Commit**

```bash
git add src/Koh.Debugger/Dap/Handlers/SetInstructionBreakpointsHandler.cs src/Koh.Debugger/Dap/Handlers/SetFunctionBreakpointsHandler.cs src/Koh.Debugger/Dap/Handlers/BreakpointLocationsHandler.cs src/Koh.Debugger/Dap/Messages/ src/Koh.Debugger/Dap/HandlerRegistration.cs src/Koh.Debugger/Dap/DapCapabilities.cs
git commit -m "feat(debugger): add instruction/function/breakpointLocations handlers"
```

---

### Task 3.F.7: Symbols and Source Context variable scopes

**Files:**
- Modify: `src/Koh.Debugger/Dap/Handlers/ScopesHandler.cs`
- Modify: `src/Koh.Debugger/Dap/Handlers/VariablesHandler.cs`

- [ ] **Step 1: Add new scope IDs**

```csharp
public const int SymbolsVariablesRef = 3;
public const int SourceContextVariablesRef = 4;
```

Return both new scopes from `ScopesHandler`.

- [ ] **Step 2: Implement the new scope cases in `VariablesHandler`**

```csharp
case ScopesHandler.SymbolsVariablesRef:
    return _session.DebugInfo.SymbolMap.All
        .Take(200)  // pagination: return first 200, lazy-load more via variablesReference
        .Select(s => new Variable
        {
            Name = s.Name,
            Value = $"${s.Bank:X2}:${s.Address:X4}",
            Type = s.Kind.ToString(),
        }).ToArray();

case ScopesHandler.SourceContextVariablesRef:
    return BuildSourceContextVariables();
```

Source Context shows the current PC's source location and macro expansion chain via `.kdbg` address-map lookup.

- [ ] **Step 3: Test**

- [ ] **Step 4: Commit**

```bash
git add src/Koh.Debugger/Dap/Handlers/ScopesHandler.cs src/Koh.Debugger/Dap/Handlers/VariablesHandler.cs
git commit -m "feat(debugger): add Symbols and Source Context variable scopes"
```

---

## Phase 3-G: Phase 3 benchmark and CI

### Task 3.G.1: Phase 3 native benchmark

**Files:**
- Create: `benchmarks/Koh.Benchmarks/Phase3Benchmarks.cs`

Per §12.9 Phase 3: run Blargg `cpu_instrs/01-special.gb` in a loop, real PPU, timer, OAM DMA, interrupts enabled. Target ≥ 1.3× real-time median.

- [ ] **Step 1: Create `Phase3Benchmarks.cs`**

```csharp
using BenchmarkDotNet.Attributes;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Benchmarks;

[MemoryDiagnoser]
public class Phase3Benchmarks
{
    private GameBoySystem? _gb;

    [GlobalSetup]
    public void Setup()
    {
        var romPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tests", "fixtures", "test-roms", "cpu_instrs", "individual", "01-special.gb");
        var rom = File.ReadAllBytes(romPath);
        var cart = CartridgeFactory.Load(rom);
        _gb = new GameBoySystem(HardwareMode.Dmg, cart);
    }

    [Benchmark]
    public void RunOneSecondOfCpu()
    {
        // One real-time second = 60 frames.
        for (int i = 0; i < 60; i++)
            _gb!.RunFrame();
    }
}
```

- [ ] **Step 2: Run the benchmark**

```bash
dotnet run --project benchmarks/Koh.Benchmarks --configuration Release -- --filter '*Phase3*'
```

Expected: the per-op mean is below 16.742 ms × 60 / 1.3 ≈ 770 ms for 60 frames.

- [ ] **Step 3: Add to CI**

- [ ] **Step 4: Commit**

```bash
git add benchmarks/Koh.Benchmarks/Phase3Benchmarks.cs .github/workflows/ci.yml
git commit -m "perf(benchmark): add Phase 3 CPU+PPU workload benchmark"
```

---

## Phase 3 exit checklist

Before declaring Phase 3 complete, verify every item below.

- [ ] `dotnet build Koh.slnx` succeeds with no warnings
- [ ] `dotnet test Koh.slnx` — all tests pass
- [ ] All 11 Blargg `cpu_instrs` sub-tests pass
- [ ] Blargg `instr_timing` passes
- [ ] Blargg `mem_timing` and `mem_timing-2` pass
- [ ] Blargg `halt_bug` passes
- [ ] Blargg `interrupt_time` passes
- [ ] Mooneye acceptance/bits/* passes
- [ ] Mooneye acceptance/timer/* passes
- [ ] Mooneye acceptance/interrupts/* passes
- [ ] Mooneye acceptance/oam_dma/* passes
- [ ] Setting a breakpoint in a `.asm` file halts execution at the expected address when F5-debugging a Koh project
- [ ] Step-over on a `CALL` runs until the matching `RET`
- [ ] Step-in on a `CALL` descends into the called routine
- [ ] Step-out runs until the current stack frame returns
- [ ] Stack Trace view in VS Code shows a sensible heuristic call stack
- [ ] Disassembly view shows decoded instructions around the current PC
- [ ] Variables panel shows Registers, Hardware, Symbols, and Source Context scopes
- [ ] Evaluate expression works for hex literals, decimal literals, and symbol names
- [ ] Instruction breakpoints and function breakpoints work
- [ ] Phase 3 benchmark meets ≥ 1.3× real-time median
- [ ] CI passes on ubuntu-latest and windows-latest

If every checkbox is checked, Phase 3 is complete and ready for Phase 4 planning.

---

## Self-review notes

**Spec coverage:** Phase 3 requirements are covered by:

- §7.4 CPU micro-op model — simplified to per-instruction cycle counts in Phase 3 with M-cycle resolution sufficient for the gated test ROMs. Full sub-instruction micro-op sequencing is not required because the test ROMs we gate against do not require mid-M-cycle observation.
- §7.12 Phase 3 subsystem phasing: full CPU, Timer/Joypad IRQ, interrupt dispatch
- §8.7 Phase 3 capabilities: stepping, stackTrace, disassemble, evaluate, breakpointLocations, symbol/source-context scopes, instruction/function breakpoints
- §12.9 Phase 3 benchmark

**Known deferrals to Phase 4+:**
- APU (all sound)
- Save states
- Watchpoints (data breakpoints)
- Conditional / hit-count breakpoints
- `writeMemory`
- Mbc3 (with RTC) and Mbc5
- Real-game verification
- Time-travel debugging

**Known risks:**
- Blargg debugging is time-consuming. Budget generously — 3-5 days for all tests to pass is realistic.
- The disassembler table is tedious; consider generating it from `docs/references/sm83-opcode-table.md` via a source generator or a one-time script.
- Call stack walking is heuristic and may produce false frames in programs that manually manipulate the stack. Document this limitation.

---

**Plan complete.** Phase 3 will be implemented after Phase 2 passes its exit checklist.
