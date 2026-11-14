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
- [ ] Extract shared model and implement ELF reader/writer, validated expression bytecode and malformed-input tests.
- [ ] Implement placement, symbol resolution, relocation evaluation, ROM and debug output in Rust.
- [ ] Integrate assembler ELF output and compiler native command boundary.
- [ ] Correct compiler symbol section/bank associations without changing generated instructions.
- [ ] Migrate .NET integration tests, retaining independently meaningful assertions.
- [ ] Replace build, SDK, boot ROM, extension, sample and release references.
- [ ] Remove C# linker and obsolete formats; inspect source and freshly built outputs.
- [ ] Run Rust, .NET, extension, sample and emulator verification; record direct statuses and completed logs.
- [ ] Review final diff and open exactly one comprehensive PR; do not merge.

Run builds serially per output directory. Parallel units use separate Cargo target directories. Main checkout and existing assembler worktree are preserved. Linux results do not establish Windows/macOS runtime compatibility.
