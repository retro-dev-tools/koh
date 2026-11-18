---
name: koh-csharp-subset
description: Check Koh's native CIL contract before authoring or porting C# games, or diagnosing unsupported IL.
---

# C# game authoring

Roslyn compiles ordinary C# into DLL/PDB. The Rust compiler reads metadata and
CIL; it does not parse source, run the assembly or embed a general managed
runtime. Source syntax alone cannot establish whether all reachable IL is
supported. Debug and Release shapes both need observable validation.

Read the relevant current contract before changing a game:

- [Runtime boundaries](../../../docs/rust-compiler/runtime-boundaries.md):
  unboxed values, exact variance identities, bounded interrupts and ROM data.
- [IR semantics](../../../docs/rust-compiler/ir.md) and
  [SM83 ABI](../../../docs/rust-compiler/sm83-abi.md): integer widths, conversions,
  memory/aliasing, frames, calls and capacity guards.
- [UTF-16 strings](../../../docs/rust-compiler/strings.md),
  [closed generics](../../../docs/rust-compiler/closed-types.md),
  [class layout](../../../docs/rust-compiler/class-layout.md),
  [virtual dispatch](../../../docs/rust-compiler/virtual-dispatch.md),
  [structured cleanup/iterators](../../../docs/rust-compiler/finally-iterators.md)
  and [LINQ runtime](../../../docs/rust-compiler/linq-runtime.md).
- [Banked code](../../../docs/rust-compiler/rom-banking.md),
  [RAM layout](../../../docs/rust-compiler/ram-layout.md) and
  [interrupts](../../../docs/rust-compiler/irq-calls.md).

Treat historical compiler results and the current public API as evidence, not
specification. Do not restore static frames, old register-return conventions,
ASCII-only strings or special iterator rewrites from deleted compiler notes.
Do not change a valid unsupported shape into a malformed-metadata diagnosis.
Never work around a compiler failure with placeholder results or omitted code.

Use `cargo nextest run --workspace --all-features --locked` for native acceptance
and `cargo test --workspace --all-features --doc --locked` for doctests. Game
tests belong in Rust even when they invoke Roslyn or the C# emulator/debugger.
Check [progress](../../../docs/rust-compiler/progress.md) for evidence and pending
gates; a green subset or successful build does not prove whole-game behavior.
