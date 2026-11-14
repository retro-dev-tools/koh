# Rust assembler migration

## Current scope change: remove managed assembler

The user now explicitly requires deleting the old C# assembler and CLI in this
same PR. Their implementation and test projects are now removed (93 files,
32,050 lines); the historical reference builds from a separate pinned checkout.
Earlier instructions to retain them are superseded. No PR has been
opened or pushed. Preserve the managed linker/compiler/emulator/debugger and
editor functionality by migrating consumers to Rust-backed interop.

New unit ownership:
- Rust analysis/protocol: `rust/koh-asm/src/analysis.rs`, `service.rs`; native
  lexical/semantic analysis and a versioned JSON-lines service.
- Managed interop/LSP: `src/Asm/Koh.Native/**`, `src/Asm/Koh.Lsp/**`,
  `tests/Asm/Koh.Lsp.Tests/**`; DTOs and native process adapter, no C# parser,
  binder, encoder, or expression evaluator. Existing editor behavior retained.
- Build/consumer migration: root project/solution/CI/release/scripts, boot-ROM
  targets, common/linker/debugger tests and benchmarks.
- Lead: engine overlays/service assembly, protocol integration, historical C#
  reference isolation, deletion, verification and one final PR.

JSON-lines protocol v1 (UTF-8 transport, offsets/lengths in UTF-16 code units):
request `{version:1,op:"parse"|"analyze"|"assemble",sources:[{path,text}],
roots:[path],allowUndefined:bool,format:"kobj"|"rgbds",
binaryFiles:[{path,data:[byte...]}]}`.
Each response is one JSON line:
`{version:1,ok:bool,documents:[{path,tokens,nodes,scopes,includes}],symbols,
diagnostics,data:[byte...]}`. Parse/analyze data may be empty. Token fields:
`{id,kind,text,start,length,parent}`; node fields:
`{id,kind,start,length,children:[{token:id}|{node:id}]}`; node0 is root.
Kinds use descriptive names matching the editor's existing SyntaxKind vocabulary;
this is a transport adapter, not the internal object model.
Scopes: `{start,end,name}`. Includes: `{path,start,length}` (literal include text).
Symbols: `{id,name,kind,visibility,value,section,owner,definition:{path,start,length},
references:[{path,start,length}],macroArity}`. IDs are strings, kind is
Label/Constant/StringConstant/Macro/CharMap, visibility Local/Exported/Imported.
Diagnostics: `{path,start,length,severity,message}` (Error/Warning/Info).
`assemble` returns object bytes in data, using the same native assembler/adapters.
`koh-asm --serve-json` reads requests until EOF, without stdout status/print noise.
Sources are an overlay; physical reads remain available for omitted includes.
Unknown protocol versions/operations/malformed requests return explicit errors.

The reference baseline is git commit bf0a5fc; move differential C# builds into a
separate pinned checkout, never retain its implementation in the final tree.

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

## Removal validation in progress

- Rust112 tests pass with `KOH_RGBDS_TEST=1`; log `/tmp/koh-native-removal-rust-tests.log`.
- Native-backed LSP203 tests pass; log `/tmp/koh-native-lsp-final-tests.log`.
- Historical helper builds; macro reproduction5 expected outcomes after reference isolation.
- Migrated Boot build produces DMG256byte and CGB2304byte images.
- Remaining: READFILE overlays, consumer runtime tests, full solution/build/sample
  revalidation, differential corpus with isolated reference, review, commit/push/PR.
