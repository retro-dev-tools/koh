# Rust assembler migration

Prior SDL3 session remains documented in `docs/sdl3.md` and the baseline history.
That session's final whole-solution run was stopped by the user; its historical
validation claims are not renewed by this assembler work. No SDL changes are
part of this migration. Original checkout remains on feat/sdl3-renderer.

Base: origin/master bf0a5fc. Original checkout and screenshot edit are untouched.

One Cargo crate at `rust/koh-asm`, library `koh_asm`, executable `koh-asm`.
Independent implementation units:
- objects: `model.rs`, `object.rs`: section/symbol/patch records and both serializers.
- expressions: `expr.rs`: parsed expressions, constant evaluation, relocation representation.
- encoding: `encode.rs`: complete documented SM83 instruction encoding.
- lead: expansion, assembler driver, CLI, integration, differential checks and documentation.

## Revised object architecture

The user required critical reassessment before continuing feature work. The
initial KOBJ-shaped public model is replaced by `model.rs`: typed symbol/section
IDs, complete relocation operators, explicit patch width/semantics, independent
PC context, RAM size separate from bytes, and source/expansion provenance.
`draft.rs` is private mutable construction state; `lower.rs` binds every name
to validated graph entries. `assemble(source,file,allow_imports)` has no format
parameter. `object::write_kobj`/`write_rgbds` are loss-checked adapters.

See `docs/rust-assembler-objects.md` for the KOBJ4/KOBJ5/RGBDS decision. V4 remains
for the current C# linker; speculative v5 is deferred until reader/evaluator
support can ship together. In-memory expressions are never degraded to v4.

The historical macro unique-suffix corruption did not reproduce against fresh
C# sources matching bf0a5fc and RGBDS1.0.1 for reconstructed WAIT/sample/nested
cases. Exact historical failing macro is unavailable. Durable script and report:
`scripts/reproduce-macro-unique.py`, `docs/macro-unique-investigation.md`.

Latest architecture run: `KOH_RGBDS_TEST=1 cargo test --workspace`, exit0, 67
tests passed; actual log `/tmp/koh-rust-architecture-test.log`. Independent tests
verify HIGH+1/LOW-word/full operators, LOAD PC sections, missing imports and
signed/HRAM link failures. C# linker produces byte-identical64KiB2048 ROMs from
C#/Rust KOBJ. The broad migration is still incomplete: extracted positive C#
fixture corpus has unresolved compatibility gaps. The C# RGBDS sample output
also fails rgblink with149 JR errors; native/reference validation remains to be
completed. Do not describe the migration as production-ready or open a complete
PR based only on the architecture checks.

Verification: each unit owns module-local unit tests. Do not run Cargo builds concurrently in the shared target directory; use `CARGO_TARGET_DIR=/tmp/koh-rust-<unit>` for isolated unit checks. Lead runs final Cargo and C# integration checks. RGBDS reference pinned to 1.0.1. No commits or pushes by unit agents.
