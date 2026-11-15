# Native metadata reader

This is an implementation increment in PR #55, not a completed compiler or the
final language contract. The SDK still invokes the managed compiler.

## Library decision

Evaluated the published sources and manifests of `dotnetdll 0.4.0` and
`clrmeta 0.1.0` on 2026-11-14 with `cargo info` and registry-source inspection.
`dotnetdll` provides a resolved metadata/IL model but declares GPL-3.0-or-later;
do not introduce that distribution constraint into Koh's MIT toolchain without
an explicit licensing decision. See its [manifest](https://github.com/nickbclifford/dotnetdll/blob/main/Cargo.toml).

Selected MIT [clrmeta](https://github.com/coconutbird/clrmeta), pinned to 0.1.0,
for standard metadata tables/heaps. It has only `thiserror` as a production
dependency. Reuse the already selected `object 0.40.0` PE reader. This is not a
Mono.Cecil replacement: reference binding, CIL validation/lowering, attribute
semantics and debugging remain compiler work. Do not call the library's
convenience name accessors that substitute empty strings after malformed heaps.
Read heap entries through their fallible APIs instead.

User strings use `Assembly::user_string` rather than clrmeta's `get`: the latter
converts UTF-16 to a Rust Unicode scalar string, rejecting isolated surrogate
units that a CLI string can contain, and does not read the terminal heap flag.
The small compatibility reader preserves `Vec<u16>` exactly, validates the #US
token, compressed length, complete payload and terminal flag range, and reports
the token on errors. It does not interpret that flag as a text encoding or
transcode the data. No assembly execution is involved. The compiler's eventual
literal capacity checks belong to lowering/emission, not metadata decoding.

Real Roslyn Debug/Release/PE32+ fixtures cover embedded NUL, Latin text, surrogate
pairs and isolated high/low surrogates. Raw cases cover empty strings, invalid
tokens/payloads/flags and every truncation across one-, two- and four-byte length
encodings. This reader increment does not yet add executable string operations.
String length/indexing must retain UTF-16 units: see the independent
[String.Length](https://learn.microsoft.com/en-us/dotnet/api/system.string.length?view=net-10.0),
[String.Chars](https://learn.microsoft.com/en-us/dotnet/api/system.string.chars?view=net-10.0)
and [#US encoding](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.metadata.blobbuilder.writeuserstring?view=net-10.0)
contracts. The old ASCII-only compiler behavior is not the language oracle.

The library's table parser preallocates from declared row counts and slices the
tables stream without validating its range. The native boundary checks every
stream before parsing tables, rejects duplicates/overlap/header overlap, checks
table byte requirements using the library's row-size calculation, and limits
total rows before allocation. Unsupported row layouts are rejected explicitly.
Signatures use Koh's isolated [bounded parser](signatures.md), retaining clrmeta's
type/result structs. The dependency's recursive signature parser is not called:
its allocations/nesting are unbounded and its compressed signed-integer reader
does not implement ECMA's width-dependent sign extension.

## Implemented boundary

- Borrowed PE32/PE32+ input, no assembly execution or managed runtime dependency.
- 64 MiB image and 262,144 combined metadata-row limits, with diagnostics.
- IL-only CLI header, one assembly/module, compressed metadata tables (`#~`).
- Entry-point MethodDef token, declaring-type/method names and raw method tables.
- Bounded method/field/local signatures, including definition-specific method
  conventions and static/instance flag agreement.
- Tiny/fat method bodies with bounded code ranges, maxstack, local signature,
  initlocals and extra-section presence preserved. Extra sections are not yet
  decoded; exposing their flag does not claim exception execution support.
- A library image without an entry point may be read as a reference. Choosing
  valid program roots belongs to compilation rather than inventing `Main` by name.

Uncompressed `#-`, mixed native/managed entry points, multi-module assemblies and
unknown table layouts are explicit reader diagnostics. Module and standard
export/resource row sizing use small compatibility corrections described in
[reference binding](reference-binding.md). These are reader limits;
no game-language capability is retired by this intermediate commit.

## Tests and execution

`rust/koh-compile/tests/assembly.rs` builds fixture C# with the installed .NET 10
SDK in temporary directories containing spaces. Tests inspect both Debug and
Release DLLs, entry/method identities, reference names, IL bytes and exception
section presence without running the managed fixture. `KOH_DOTNET` selects an SDK
executable; otherwise `dotnet` must be on PATH. Missing SDK/build failures fail
tests rather than silently skipping fixture validation.

Run `cargo nextest run --workspace --all-features --locked` and separately
`cargo test --doc --workspace --all-features --locked`. The committed nextest
configuration uses no retries, four test workers, a three-minute termination
limit and a single-worker Roslyn fixture group to bound SDK memory use. Fixtures
have independent temporary output directories; no generated binaries are tracked.
CI installs .NET before testing on Linux, Windows and macOS. A workflow definition
is not hosted execution evidence.

The portable PDB emitted by Roslyn is not yet consumed. Subsequent increments
implement reference resolution, typed IR, ELF/SM83 emission and component emulator
execution; complete game and SDK acceptance remain open. See [progress](progress.md).

## Independent specifications

Use [ECMA-335](https://ecma-international.org/publications-and-standards/standards/ecma-335/),
Partition II §§24–25 for metadata, CLI header and method body layout, and
Partition III for instruction semantics. Existing compiler behavior is evidence
to investigate, not an oracle.
