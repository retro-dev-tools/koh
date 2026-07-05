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

1. **IR core + textual form + verifier.** `Ir/`, `Targets/` (SM83 DataLayout), interfaces, registry, driver skeleton. Hand-writable, printable, verifiable IR. *(This is the scaffold landed alongside this spec.)*
2. **SM83 backend MVP.** Hand-written IR → legalize → naive codegen (everything through `A`/`HL`, static locals, far-call trampolines) → `.kobj` → link → **run in the emulator**. Correctness before optimization. Golden-ROM tests against the emulator.
3. **C# frontend MVP.** Roslyn parse/bind → subset gate → lower to IR: `static` methods, `byte`/`ushort`, `if`/`while`/`for`, arrays, pointers, hardware intrinsics, `extern` asm interop. Snapshot-test IR output.
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
