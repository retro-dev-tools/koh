# Koh Compiler Platform — C# Frontend & Retargetable IR — Design

**Date**: 2026-07-05
**Status**: Draft
**Depends on**: Binding & Emit (`.kobj`), `Koh.Linker.Core`, `Koh.Core.Encoding` (SM83 table), the emulator + DAP debugger (`.kdbg`)

## Overview

Koh today is an assembler strand: RGBDS-style source → `.kobj` → linker → ROM, with an LSP and an emulator/debugger over the top. This spec adds a **compiler strand**: a higher-level language frontend (C#) that lowers through a target-independent SSA IR into the *same* `.kobj` object files the assembler already emits — so it inherits the linker, banking, `.sym`/`.kdbg` debug info, the emulator, and the DAP debugger for free.

It is built as a **compiler platform**, not a single language: frontends and backends are pluggable, and *adding either is adding a directory*. The first frontend is a systems subset of C#; the first backend is a hand-written SM83 code generator. The IR is deliberately general enough that register-rich 32-bit targets (GBA/ARM7TDMI, PSX/MIPS R3000) are *possible later*, delegating to LLVM where that is the right tool — without any of that being built now.

The audience is **general game developers new to the Game Boy**: familiar C# syntax, modern editor tooling, no requirement to learn SM83 up front.

## Goals, in priority order

1. **Reuse the bottom half of the toolchain unchanged.** A backend produces `.kobj`; everything downstream (linker, `.sym`, `.kdbg`, emulator, DAP debugger) is untouched. This is the tiebreaker for design choices throughout.
2. **A directory is the unit of extension.** Adding a frontend or backend means creating one directory under `Frontends/` or `Backends/` and one registration line — no cross-cutting edits, no reflection.
3. **Familiar syntax, honest constraints.** Write GB code in real C# syntax that compiles to tight SM83. Where C# semantics don't fit the hardware (heap, GC, 32-bit ints), either legalize transparently or reject with a clear diagnostic — never silently miscompile.
4. **Retargetable IR.** The shared IR is generic typed SSA with a per-target data layout, address spaces, and a legalization stage. Nothing SM83-specific leaks into it; the 8-bit cleverness lives in the SM83 backend.
5. **Source-level debugging from day one.** The backend threads a per-instruction line map into `.kobj`, so the existing `.kdbg` pipeline gives C#-source breakpoints and stepping in the emulator with no debugger changes.

## Non-goals (YAGNI)

- **Not full C#.** No GC, no `class`/heap allocation, no `new` on the heap, no reflection, no `async`, no delegates/closures, no exceptions, no generics (initially), no LINQ, no `string` as a heap object. See "The C# subset."
- **Not a second hardware backend now.** GBA/PSX are a design constraint on the IR, not scheduled work. The IR must not preclude them; nothing more.
- **Not LLVM-hosted.** SM83 is hand-written (LLVM is a poor fit for accumulator machines; see `docs/decisions/` rationale in the design discussion). LLVM is reserved as an *optional* implementation detail for future register-rich backends.
- **Not a replacement for the assembler.** The asm strand stays the direct-to-`.kobj` path. C# and asm interoperate via `extern` symbols; a project can mix both.
- **No RGBDS `.o` output from the compiler.** Native `.kobj` only.

## Architecture: two hourglass waists

LLVM has one waist (its IR). Koh has **two**, and the lower one already exists and is stable:

```
 Frontends (parse + lower)      C#  │  (future: Wiz-ish, BASIC)
                                    ▼
   ══════════ WAIST 1: Koh IR ══════════          ← NEW  (generic typed SSA)
        module / functions / SSA blocks / DataLayout / address spaces
                                    │
                          per-backend legalization
                                    ▼
   SM83 backend: instr. selection + register/static allocation   ← NEW (hand-written)
        reuses Koh.Core.Encoding (Sm83InstructionTable, EmitRule, OperandPattern)
                                    ▼
   ═════ WAIST 2: EmitModel / .kobj ═════          ← EXISTS, stable, tested
                                    │
   Koh.Linker.Core → ROM + .sym + .kdbg → emulator + DAP debugger   ← EXISTS
```

The assembler is, in this framing, a frontend whose "IR" is source-level instructions and whose backend is `InstructionEncoder`. It keeps its direct path; only the new higher-level languages route through Waist 1.

## The IR (`Ir/`)

A **generic, target-independent, typed SSA IR** — LLVM-*shaped*, not LLVM-*hosted*.

### Types

- Integers of arbitrary width: `i8/i16/i32/i64` (signed/unsigned is an operation property, not a type property, LLVM-style).
- `void`, `bool` (lowers to `i8`).
- Pointers carry an **address space**: `ptr addrspace(rom|wram|hram|sram|vram|far)`. Address space is the one generalization of "banking" that survives to 32-bit targets (GBA/PSX just use a flat default space).
- Aggregates: `struct` (value type, field offsets from DataLayout) and fixed-length `array`.
- No floats in v1 (software float is a later, opt-in intrinsic library).

### DataLayout (per target, `Targets/`)

Describes pointer size, endianness, alignment, and native integer widths. SM83: 16-bit pointers, little-endian, 8-bit-native. ARM7/MIPS (future): 32-bit. The IR uses `i32` freely; DataLayout + legalization decide what that costs on a given target.

### Instructions

SSA values produced by typed operations: arithmetic (`add/sub/and/or/xor/shl/…`), comparisons, `load/store` (address-space aware), `gep`-style address computation, `call` (with a `bank` attribute for far calls), `br/condbr/switch/ret`, and `phi`. Hardware access is via **intrinsics** (`@koh.gb.oam_dma`, `@koh.gb.read_io`, …) that only the relevant backend understands.

### Legalization

The stage that lets one IR feed an 8-bit *and* a 32-bit CPU. Before instruction selection, each backend rewrites ops its hardware lacks:

- SM83 has no multiply/divide → expand to routines / runtime libcalls.
- `i32`/`i16` arithmetic on SM83 → expand to multi-byte sequences.
- ARM/MIPS legalize almost nothing.

Legalization is a backend responsibility, keeping the IR clean.

### Textual form + verifier

The IR has a **textual form** (parse + print) and a `Verifier`. This is load-bearing: it lets the SM83 backend be developed and tested against *hand-written* IR with **zero frontend**, and lets frontend output be snapshot-tested independently of codegen. Phase 1 depends on it.

## The C# frontend (`Frontends/CSharp/`)

### Strategy: Roslyn parses; we gate and lower

The frontend does **not** implement a C# parser. It uses **Roslyn** (`Microsoft.CodeAnalysis.CSharp`) to parse and bind the source, walks the bound/semantic model, **rejects any construct outside the supported subset with a precise diagnostic**, and **lowers the rest to Koh IR**. This makes the frontend mostly a subset-gate + a lowering pass — a fraction of the cost of a from-scratch compiler — while giving users a fully correct C# parser and, potentially, Roslyn-backed editor tooling.

(Roslyn is not fully NativeAOT-friendly, but the *compiler CLI* does not need AOT — only the LSP and emulator do, and those are unaffected. LSP language features for the C# frontend are a later phase and can lean on Roslyn's own tooling.)

### The C# subset ("Koh C#")

Real C# syntax, constrained to what compiles to SM83 without a runtime:

**Supported**
- `static` methods and fields; `struct` value types; `enum`; `const`.
- Native integer types map to hardware widths: `byte`→`i8` unsigned, `sbyte`→`i8`, `ushort`→`i16` unsigned, `short`→`i16`, `bool`→`i8`.
- `int`/`uint` (32-bit) are **legal but legalized** — they work, they're just slower and larger. Surfacing this ("you used `int`; consider `ushort`") is a teaching diagnostic, not an error.
- Control flow: `if/else`, `while`, `for`, `do`, `switch`, `break`/`continue`, `return`.
- Fixed-size arrays, pointers/`ref`/`in`/`out`, `Span<T>` over fixed regions (later).
- String *literals* → ROM byte data via the charmap (the emitter already has `CharMapManager`); not a heap object.
- Calling `extern` assembly symbols, and being called from asm — the mixed-project interop story.

**Rejected (with diagnostics)**
- `class`/heap allocation, `new` on the heap, `GC`, finalizers.
- `interface`/virtual dispatch, `abstract`/`override` (v1).
- `async/await`, `Task`, threads.
- `try/catch/throw` (exceptions).
- Delegates, lambdas that capture, events.
- Generics (v1), reflection, `dynamic`, LINQ.

### Memory placement, banking, hardware

- **Placement attributes** on fields/methods: `[Rom]`, `[Wram]`, `[Hram]`, `[Sram]`, `[Bank(n)]`, `[Section("Name")]`. These map to IR global address spaces and to `.kobj` section headers — the same section/bank model the assembler and linker already use.
- **Banking is a backend concern.** A `[Bank(n)]` call becomes an IR `call` with a bank attribute; the SM83 backend emits the far-call trampoline. The linker already assigns banks.
- **Hardware registers** exposed as a generated `Hardware` static surface (`Hardware.LCDC`, `Hardware.KEY1`, …) backed by IR MMIO intrinsics — mirroring the `hardware.inc` defs the samples use, but type-checked.

## The SM83 backend (`Backends/Sm83/`)

- **Instruction selection reuses `Koh.Core.Encoding`** — `Sm83InstructionTable`, `EmitRule`, `OperandPattern`, `OperandPatternMatcher` are the single source of truth for encodings; the backend selects and the existing machinery encodes.
- **Register/static allocation for an accumulator machine.** SM83 has one real 8-bit accumulator (`A`), `HL` as the pointer register, and `BC`/`DE`. Locals are placed with **whole-program static allocation** (fixed WRAM/HRAM addresses via liveness/interference), NESFab-style — *not* stack frames. HRAM (`$FF80–$FFFE`) is the fast spill region.
- **Output is `.kobj`.** The backend produces `SectionData` + `SymbolData` + `PatchEntry` list and writes a `.kobj` via `KobjWriter`, exactly as `koh-asm` does. No new linker coupling; cross-object/far references become linker patches as usual.
- **Debug info comes free.** The backend attaches `LineMapEntry` ranges (C# source span → emitted bytes) to each section. The linker already turns those into `.kdbg`, so the emulator + DAP debugger step over C# source with no changes.

## Single-project layout — a directory is the extension point

One assembly, `src/Koh.Compiler/`:

```
src/Koh.Compiler/
  Koh.Compiler.csproj        # refs Koh.Core, Koh.Emit, Koh.Linker.Core
  Ir/                        # types, values, instructions, module, textual form, Verifier, passes/
  Targets/                   # DataLayout, register info, calling conventions (data, per target)
  Frontends/
    IFrontend.cs
    CSharp/                  # ← add a frontend = add a directory here
  Backends/
    IBackend.cs
    Sm83/                    # ← add a backend  = add a directory here
    # Arm7Tdmi/ (GBA, future) — may delegate to LLVM
    # MipsR3000/ (PSX, future) — may delegate to LLVM
  CompilerRegistry.cs        # frontends/backends register here
  CompilerDriver.cs          # frontend → IR → legalize → backend → .kobj
```

### Registration (AOT-safe)

`reflection`-based plugin scanning would break the NativeAOT LSP (`docs/decisions/lsp-aot-decision.md`). Two options, in order of adoption:

- **Now:** a hand-maintained `CompilerRegistry` — one line per frontend/backend. Explicit, trivial, trim-safe.
- **Later:** a source generator that scans `Frontends/*/` and `Backends/*/` at build time and emits the registration, so a new directory really is the entire gesture — still AOT-clean.

## Phasing

Ordered to attack the highest-risk piece (SM83 codegen) first, on hand-written IR, before any frontend exists.

1. **IR core + textual form + verifier.** ✅ *Landed.* `Ir/` (typed SSA value/instruction model — arithmetic, compares, conversions, `alloca`/`load`/`store`/`gep`, `call`, `phi`, `ret`/`br`/`condbr`/`switch`), `IrBuilder`, `IrPrinter` (textual form), `IrParser` (round-trips the printer), `IrVerifier` (CFG + operand-type checks), `Targets/` (SM83 `DataLayout`), interfaces, registry, driver. Covered by `Koh.Compiler.Tests` (parse/verify/round-trip stability + builder loop-with-phi + negative verifier cases).
2. **SM83 backend.** ✅ *Landed (non-optimizing).* Static-allocation codegen emitting bootable, debuggable ROMs, verified end-to-end on the emulator (64 tests). Covers:
   - **Arithmetic:** `i8`/`i16` add/sub (ADC/SBC chains), and/or/xor; **multiply/divide/remainder** via on-demand runtime routines (`__mul16`/`__udivmod16`/`__sdivmod16`, signed and unsigned); **shifts** (constant unrolled, variable looped).
   - **Comparisons:** unsigned, signed (sign-bit-flip), and eq/ne, at `i8`/`i16`.
   - **Conversions:** trunc/zext/sext.
   - **Control flow:** `br`/`condbr`/`switch`, and `phi` realized as parallel edge copies (cycle-safe via temps), with absolute-address backpatching.
   - **Calls:** disjoint per-function WRAM frames (composable, non-recursive — recursion rejected), args in param slots, returns in `A`/`HL`.
   - **Memory:** `alloca`, static- and dynamic-address `load`/`store`, constant- and runtime-index `gep` (HL-indirect), pointer parameters.
   - **Globals:** ROM data sections + RAM globals; `IrGlobal.FixedAddress` pins a global to an MMIO register (I/O page is memory-mapped, so no intrinsic node needed).
   - **Real ROM:** cartridge header at `$0100` (boot vector, Nintendo logo, valid checksum) → a bootable cartridge.
   - **Debug:** source line maps threaded to `.kdbg`.
   - **Quality:** opcodes pinned to `Sm83InstructionTable` by a cross-check test; a safe accumulator-reuse peephole; and **liveness-based WRAM slot allocation** — SSA-liveness interference graph + greedy coloring, so values with non-overlapping live ranges share WRAM bytes (a 10-temp `i8` chain collapses from 10 bytes to 1). A multi-byte result is emitted in place byte-by-byte, so it interferes with its own operands (never a *partial* slot overlap that would clobber a source mid-read); phi parallel-copies detect clobbers by allocated slot, not SSA identity, so coalesced slots stay correct. Remaining (deferred): register *residency* (keeping values in SM83 registers across instructions) and wider-than-16-bit integers.
3. **C# frontend.** ✅ *Landed (complete systems subset).* `Frontends/CSharp` uses Roslyn to parse; walks the syntax tree (no semantic model) reporting out-of-subset constructs as diagnostics and lowering the rest to IR. Locals/parameters become `alloca`s (control flow needs no phi construction); typing is C-like (8-bit math stays 8-bit). A binary op converts both operands to their common type (usual-arithmetic-conversion style — the wider width wins, and a mixed signed/unsigned pair whose sign affects the result promotes to a signed type wide enough to hold both, e.g. `sbyte` vs `byte` compares as a signed `short`; a case with no such type on the target, like anything mixed with `ushort`, is a diagnostic asking for an explicit cast), so signedness and width are never silently taken from the left operand. Covers:
   - **Types:** `byte`/`sbyte`/`ushort`/`short`/`bool`, `int`/`uint` (32-bit: add/subtract/bitwise/compare/load/store/convert; multiply/divide/remainder/shift are diagnosed, not lowered — narrow to 16 bits first), `enum` (custom base), `const`, pointers (`T*`), arrays (local, plus `static readonly T[]` ROM data tables and `static T[] = new T[n]` WRAM buffers), value-type `struct`s.
   - **Expressions:** arithmetic/bitwise/shift/compare, casts, unary `-`/`!`, `++`/`--`, short-circuit `&&`/`||`, ternary `?:`, `=` and compound assignment, method calls, `&`/`*`, `arr[i]`, `arr.Length`, `s.field`, `Enum.Member`, and full pointer support: `p + i`/`p - i`/`p++`/`p += n` lower to a `gep` (index scaled by the pointee size), `p < q`/`p == q` compare addresses, and pointer↔integer casts reinterpret via `bitcast`. Pointer size flows from one place (`IrType.SizeInBytes`, backed by the target `DataLayout`), so pointer struct fields and globals lay out correctly.
   - **Statements:** `if`/`else`, `while`, `do`/`while`, `for`, `switch` (+`break`/`continue`), `return`, local/const declarations.
   - **Program:** static methods + top-level functions, `static` fields (WRAM/ROM/const), `ref`/`out`/`in` parameters.
   - **Hardware:** a `Hardware` register surface (MMIO), `Hardware.EnableInterrupts/DisableInterrupts/Halt` intrinsics, and `[Interrupt("VBlank"|…)]` handlers emitted at the GB vectors.
   - **Debug + diagnostics:** source spans threaded to `.kdbg`; errors reported into the `DiagnosticBag` with locations, not thrown.
   
   Verified end-to-end on the emulator (Gcd, Factorial, arrays, structs, ref-swap, hardware register I/O, …) and by a full playable **2048** ROM (`samples/gb-2048-cs`) that compiles through the pipeline and whose game logic is checked in the emulator. Remaining polish: 32-bit multiply/divide/shift (add/subtract/bitwise/compare already work), strings/charmap, method overloading, banking attributes, `extern` asm interop. Out by design: classes/GC/generics/async/LINQ/recursion.
4. **IR optimization passes.** Const-fold, DCE, copy/coalesce, SM83 peephole on the emitted stream. Introduce full SSA-based opt if/when the optimizer needs it.
5. **Editor tooling.** Diagnostics/hover/go-to for Koh C#, reusing the LSP architecture and/or Roslyn.
6. **Prove generality (optional, later).** A second backend (ARM7TDMI via LLVM delegation) or a second frontend — only to validate the seams.

## Open questions

- **Struct calling convention & aggregate returns** on SM83 (by-value vs. by-pointer threshold).
- **`Span<T>` / bounds** — how much of the safe-slice model survives to `i16` pointers without runtime cost.
- **Charmap selection** for string literals in C# (attribute? per-project `koh.yaml`?).
- **Source-generator vs. hand registry** cutover point (Phase 1 ships the hand registry).
- **Roslyn in the LSP** — reuse Roslyn tooling for Koh C# vs. a lighter bespoke semantic layer; decided in Phase 5.
- **Acceptance bar for codegen quality** vs. GBDK/SDCC — set after Phase 2's first real ROMs.

## What this doc is not

- Not a commitment to GBA/PSX backends — only to an IR that doesn't preclude them.
- Not a spec for the optimizer's pass pipeline (Phase 4 gets its own plan).
- Not a full grammar of the C# subset — the subset is defined by the gate + diagnostics, refined against real code in Phase 3.
