# Rust LSP handoff

User approved design and selected assembler worker process. Goal active.
Worktree `/home/minorum/projekty/koh-rust-lsp`, branch `feat/rust-lsp`,
base master `065cf05`. No push/merge authorization. No agents requested.

Native Cargo target, bounded LSP framing, stdio lifecycle and persistent
worker client implemented. Worker I/O runs on an exchange thread so blocked
writes and reads are covered by the request deadline. Protocol failures stop
and reap the child. Stderr inherits server stderr, never LSP stdout.

7/7 native tests pass, strict Clippy exit0. Completed logs listed in evidence.md.
Managed oracle 207/207 pass. All 207 disposition rows remain pending: do not
claim feature replacement from the transport tests. Production remains managed.

Next: Task2 configuration/workspace/path handling, then editor methods, native
packaging and managed retirement. Plan docs/superpowers/plans/2026-11-19-rust-lsp.md.
Full scope and acceptance in design.md. No broad compiler/emulator redesign.

Run tests with KOH_ASM=/home/minorum/projekty/koh/target/native-msbuild/Release/host/release/koh-asm
until assembler is built in this worktree. Cargo /usr/lib/rust/1.99.0/bin/cargo;
direct cargo-clippy with CARGO and RUSTC set to matching binaries works.
Direct rustfmt --edition 2024 avoids nightly cargo-fmt shim.

Main checkout unrelated battle.png preserved. Initial migration docs also
remain untracked there; use this branch's approved copies as authoritative.
Do not overwrite main HANDOFF.md, which belongs to the prior engine migration.
