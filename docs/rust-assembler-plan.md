# Native toolchain migration record

The earlier assembler-only plan used a pinned C# checkout and KOBJ/C# linking
as temporary validation scaffolding. That phase is historical.

The current pipeline is source -> native assembler -> ELF32 SM83 -> native
linker -> ROM, with managed compiler/LSP/debugger/emulator integration.
The shared typed object graph lives in `koh-objects`; neither compiler nor
linker depends on assembler implementation details.

Current implementation and verification decisions are described in
[rust-assembler.md](rust-assembler.md),
[the ELF ABI](rust-linker/elf32-sm83.md), and
[the preserved-test disposition](rust-linker/test-disposition.md).
The retired C# reference helper is absent. Independent RGBDS 1.0.1 oracles and
real emulator execution remain the compatibility checks.
