# Rust linker migration

Base: master 9a2cfa8. Main checkout's modified JRPG screenshot is unrelated and preserved.

## Ownership

- Audit unit: read affected tests and write docs/rust-linker/test-disposition.md only; no test mutations before the audit exists.
- Parent: architecture, ELF specification, shared Rust object crate and integration coordination.
- Further units receive explicit ownership after the audit and shared interfaces are established.

## Shared interfaces

- ELF unit owns rust/koh-objects, Cargo.toml/Cargo.lock, assembler production integration and ELF ABI document. Move existing assembler model unchanged to koh_objects and re-export from assembler model; make memory_region public. Public read_elf(&[u8]) -> Result<Object,String>, write_elf(&Object) -> Result<Vec<u8>,String>. No duplicated model. ELF unit coordinates extension bytecodes with C# writer owner.
- Link unit owns rust/koh-link only. Depend on koh-objects path ../koh-objects; library links &[Object], CLI reads ELF. Preserve raw/size, sym, kdbg and add CGB flags. Existing model is the authoritative interface. Parent integrates C# and build/CI.
- No builds in shared target directory except parent. Agents use CARGO_TARGET_DIR=/tmp/koh-elf-target or /tmp/koh-link-target.

## Verification

Run builds serially per output directory. Capture direct exit status and reread completed logs. Rust: cargo test --workspace --locked. .NET: focused tests, then constrained fast suite. Extension: npm test. Runtime and platform claims require actual evidence.

## Completion

One PR against master only after full migration, removal of C# linker and KOBJ, sample and integration verification. No transitional PR.
