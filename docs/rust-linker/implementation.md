# Rust linker migration

The requested result is one complete PR against master. The CIL frontend, SM83 instruction backend, emulator, debugger and UI remain in C#. The assembler and linker share `koh-objects`; ELF32/SM83 is their only object interchange.

## Decisions

- Extract the assembler's typed object model rather than introduce another Rust model.
- Use standard ELF sections, symbol tables and RELA records; extensions carry only placement constraints, complex expression bytecode and source provenance.
- Identify provisional SM83 objects with EM_NONE plus mandatory Koh ABI identification. No unrelated processor machine ID.
- Keep the compiler in process with MSBuild, but invoke native tools with argument arrays. A documented JSON command boundary constructs ELF from compiler output without a C# ELF implementation.
- Native assembler results retain their original ELF image; C# semantic inspection does not reserialize or reduce the relocation model.
- Remove all C# linker production code and both obsolete object serializers, after auditing affected tests.

## Implementation and checks

- [x] Audit existing tests before modifying or deleting them: [matrix](test-disposition.md).
- [x] Extract shared model and implement ELF reader/writer, validated expression bytecode and malformed-input tests.
- [x] Implement placement, symbol resolution, relocation evaluation, ROM and debug output in Rust.
- [x] Integrate assembler ELF output and compiler native command boundary.
- [x] Correct compiler symbol section/bank associations without changing generated instructions.
- [x] Migrate .NET integration tests, retaining independently meaningful assertions.
- [x] Replace build, SDK, boot ROM, extension, sample and release references.
- [x] Remove C# linker and obsolete formats; inspect source and freshly built outputs.
- [x] Run Rust, .NET, extension, sample and emulator verification; record direct statuses and completed logs.
- [x] Review final diff and open exactly one comprehensive PR; do not merge.

Draft [PR #54](https://github.com/retro-dev-tools/koh/pull/54) was opened at the
user's explicit request during validation. See [validation evidence](validation.md)
for completed local checks and the PR's current-head checks for hosted results.

Run builds serially per output directory. Parallel units use separate Cargo target directories. Main checkout and existing assembler worktree are preserved. Linux results do not establish Windows/macOS runtime compatibility.
