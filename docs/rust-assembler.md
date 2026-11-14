# Rust assembler

The native assembler lives in `rust/koh-asm`. It builds with Rust 1.85 or newer,
uses edition 2024, and uses Serde for the managed host protocol. It replaces the
C# assembler and CLI. The C# language server consumes native syntax and semantic
metadata through `Koh.Native`; the compiler, linker, debugger, emulator, and
VS Code extension remain managed. The historical C# implementation is built only
from a separate, pinned checkout for differential validation.

## Build and run

From the repository root:

```sh
cargo build --release --package koh-asm --locked
cargo run --release --package koh-asm -- input.asm -o output.kobj
cargo run --release --package koh-asm -- input.asm --format rgbds -o output.o
dotnet msbuild build.proj -t:BuildSample2048Rust
```

The executable is `target/release/koh-asm` on Unix and
`target/release/koh-asm.exe` on Windows. Building it does not require .NET or
RGBDS. The executable assembles source into an object; linking a cartridge ROM
is a separate operation.

The CLI accepts one input path, `-o`/`--output`, `-f`/`--format`,
`-h`/`--help`, and `--version`. Formats are `kobj` (the default), `rgbds`, and
its alias `o`. Without `-o`, the input extension becomes `.kobj` or `.o`.
Assembly failures produce file and line diagnostics and a nonzero exit code.
Output is written through a temporary file in the destination directory.

## Implementation

| Module | Responsibility |
| --- | --- |
| `source.rs` | Lines, comments, arguments, labels, token substitution |
| `resolver.rs` | Explicit file mappings and inherited include search policy |
| `expr.rs` | Parsed expressions, constant arithmetic, string and math builtins |
| `encode.rs` | SM83 instructions and operand patches |
| `assembler.rs` | Expansion, includes, directives, symbols, sections, patch resolution |
| `draft.rs`, `lower.rs` | Private construction records and name/reference validation |
| `model.rs` | Format-independent symbols, expressions, patch semantics and provenance |
| `object.rs` | Loss-checked KOBJ v4 and RGBDS adapters |
| `analysis.rs`, `service.rs` | Native LSP metadata and versioned JSON service |
| `main.rs` | CLI, diagnostics, output publication |

`koh_asm::assemble(source, file, allow_imports)` exposes assembly to Rust callers.
Successful assembly returns the canonical Rust object graph; failures return diagnostics.
Use `assemble_report` or `assemble_with_overlays_report` to also receive structured
warnings alongside the result. Warnings remain available on assembly failure;
the CLI and native service forward them with warning severity.
`object::write_kobj` and `object::write_rgbds` serialize that model. Unresolved
references remain patches for the linker.

Assembly has no object-format parameter. Import permission controls whether
undefined names become explicit imports or diagnostics. The model retains full
expressions, typed symbol/section references, patch width/range semantics and a
PC context independent of the storage section. Both adapters validate the graph;
KOBJ emission fails when its restricted relocation encoding would lose meaning.
See [object architecture and format decision](rust-assembler-objects.md).

Editor syntax metadata does not determine assembly success. The canonical engine
controls diagnostics for active source and macro expansion; project analysis
resolves imports against actual exported definitions from the other roots.

The historical macro unique-suffix report was independently investigated; the
reconstructed failure did not reproduce. See [evidence and reproducer](macro-unique-investigation.md).

## Compatibility boundary

The implementation covers SM83 instruction encoding and the assembly workflow:
sections, labels, constants, macros, conditional assembly, loops, data,
includes, character maps, and relocation records. Coverage is established by
tests for particular behaviors, not by accepting a directive name alone.
It is not yet a claim that every C# assembler fixture or every RGBDS program is
supported.

Numeric expressions follow the independent
[RGBDS 1.0.1 language manual](https://raw.githubusercontent.com/gbdev/rgbds/v1.0.1/man/rgbasm.5):
signed 32-bit wrapping arithmetic, exponentiation before unary operators,
shifts and bitwise operators before addition, and division rounded down.
This intentionally differs from the current C# evaluator's precedence and
truncating division in some expressions. Parenthesize expressions when sharing
source that relies on those differences.

The expression module implements `HIGH`, `LOW`, `DEF`, `ISCONST`, `BANK`,
`SIZEOF`, `STARTOF`, `BITWIDTH`, `TZCOUNT`, fixed-point math and trigonometry,
string comparison/search, byte access, concatenation, slicing, replacement,
case conversion, and common `STRFMT` formats. Fixed-point precision defaults
to 16; literals accept a `q` suffix and math functions accept an explicit final
precision. `STRSUB` remains a C# compatibility extension using a one-based
start; RGBDS `STRSLICE` uses zero-based indexes.

`context.rs` folds file and active-character-map functions at their source
location, including `READFILE`, `INCHARMAP`, `CHARLEN`, `STRCHAR`, `REVCHAR`,
`CHARCMP`, `CHARSIZE`, and `CHARVAL`. Character maps use the longest matching
entry. `BYTELEN` and `STRBYTE` operate on UTF-8 source bytes, following RGBDS
1.0.1; this differs from the C# evaluator's mapped-byte behavior.
`READFILE` resolves paths relative to the containing source file and rejects
non-UTF-8 contents. Option stacks (`PUSHO`/`POPO`), `OPT Q.N`, and padding options
are implemented. Full `STRFMT` precision syntax remains unsupported. Raw quoted
strings, EQUS replay, formatted interpolation, and macro expansion are covered
by assembler integration tests. Direct triple-quoted `DB` expressions are not
implemented and produce an explicit expression diagnostic.

Block comments separate tokens without terminating a statement at an internal
newline. Nested openers warn, and unterminated comments fail assembly. Literal
contents and physical source locations are preserved.

Koh retains per-byte current-address evaluation in `DS count, @` and permits
macro-local labels without a global anchor. These are documented differences
from RGBDS 1.0.1. Bare `WARN` retains Koh's explicit `WARN directive` message;
RGBDS requires a message expression. `SECTION UNION` also retains Koh's implicit
first union member;
its combination metadata survives in RGBDS output.

The v4 adapter rejects floating alignment, union/fragment combination metadata,
complex relocations and constraints the C# linker cannot enforce. It also rejects
case-equivalent names and non-ASCII symbol names to avoid misresolution by the
case-insensitive C# linker. Unicode section-name collision detection is
conservative. The canonical model and RGBDS adapter retain case-sensitive names.

UTC/version builtins are provided; `SOURCE_DATE_EPOCH` gives reproducible UTC
values. Malformed values are diagnosed. Local-timezone builtins are unavailable.
These version builtins identify the pinned language reference, not a claim of
complete RGBDS compatibility.

## Verification

```sh
cargo fmt --all -- --check
cargo check --workspace --all-targets --locked
cargo test --workspace --locked
cargo build --release --package koh-asm --locked
```

Run builds serially in each target directory. To isolate another run, set
`CARGO_TARGET_DIR` to a separate directory. The Rust workflow runs formatting,
checking, tests, and release builds on Linux, Windows, and macOS. A separate
Linux job builds RGBDS from the `v1.0.1` tag and runs tests with
`KOH_RGBDS_TEST=1`; those comparisons require `rgbasm` and `rgblink` on `PATH`.

For the C#/Rust artifact and fixture comparison:

```sh
python3 scripts/verify-rust-assembler.py --output /tmp/koh-asm-verification
```

This requires a supported .NET 10 SDK and RGBDS 1.0.1. It builds the C#
verification helper and release Rust executable before comparing artifacts.
Use `--no-build --rust /absolute/path/to/koh-asm` only with fresh builds.
The Linux reference job runs this comparison and uploads its output directory.

Tests must distinguish instruction bytes, serialized object structure, linking,
and runtime behavior. Passing Rust unit tests does not prove cartridge execution
or complete C# compatibility. The CI definition describes intended checks;
successful hosted runs and full differential results must be reported separately.

Before removal, the historical C# assembler suite passed 1,082 tests. Native
validation now includes 148 Rust tests with the RGBDS checks enabled and 207
native-backed LSP tests. The final managed fast suite passed 588 tests, with
two native-window tests skipped. All 812 managed compiler tests passed after
removal.
The 2048 gameplay verifier also passed all 11 checks on a Rust-built, C#-linked
64 KiB ROM. The independent relocation tests check linked bytes and actual failures for missing symbols,
signed overflow and invalid high-memory addresses. Hosted Windows/macOS results
remain separate evidence.

## Historical reference

`scripts/assembler_reference.py` checks out commit
`bf0a5fc1792b6022328af080cd50f7b4d7e143c5` outside this source tree and builds
the small driver under `tools/rust-asm-verify/reference`. Set
`KOH_CSHARP_REFERENCE_ROOT` to choose a different checkout location. The script
rejects a different commit or tracked modifications. Current production projects
do not reference that checkout. The historical test corpus is extracted there;
it is a behavioral reference, not a correctness oracle.

The managed bridge starts `koh-asm --serve-json` and exchanges versioned JSON
lines. `KOH_ASM` selects an executable explicitly; otherwise the bridge checks
its application directory, local Cargo outputs, and PATH. Source and binary
overlays remain separate. Native diagnostics and UTF-16 source spans feed the
existing language-server features.
Requests carry explicit resolver decisions and per-file search roots, preserving
entrypoint-based resolution for nested `INCLUDE`, `INCBIN`, and bounded `READFILE`.
