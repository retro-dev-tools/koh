# Rust LSP handoff

User approved design and selected assembler worker process. Goal active.
Worktree `/home/minorum/projekty/koh-rust-lsp`, branch `feat/rust-lsp`,
base master `065cf05`. No push/merge authorization. No agents requested.

Native Cargo target, bounded LSP framing, stdio lifecycle and persistent
worker client implemented. Worker I/O runs on an exchange thread so blocked
writes and reads are covered by the request deadline. Protocol failures stop
and reap the child. Stderr inherits server stderr, never LSP stdout.

Configuration, file URI/UTF16 positions, graph/discovery and core workspace now
implemented. Configured/heuristic/invalid modes, unsaved overlays, isolation,
deterministic shared ownership and root-first nested includes have native checks.
INCBIN always uses disk; source path identity comparisons normalize ../ without
rewriting native provenance. Workspace recreates a worker after protocol failure.

49/49 native tests pass, strict Clippy exit0. Completed logs in evidence.md.
Managed oracle207/207 pass.169 disposition rows mapped,1 obsolete private adapter,
33 classification/hint/signature cases pending. Native tests
run without KOH_ASM using freshly built target/debug/koh-asm. Production remains
managed. Native main now serves sync/diagnostics, hover, definition, references,
prepare-rename/rename through Server. Real stdio child/worker tests pass.

Task2 parity review complete. Editing unrelated configured files reuses cached
project analysis; editing shared includes rebuilds both owners. READFILE,
custom nested resolver mappings, warnings and native instruction/data bytes
covered through the real worker for physical/virtual fixtures.

Next: finish Task3 semantic tokens/classification, inlay hints, macro signatures,
document symbols and completion. Then native
packaging and managed retirement. Plan docs/superpowers/plans/2026-11-19-rust-lsp.md.
Full scope and acceptance in design.md. No broad compiler/emulator redesign.

Build assembler once with cargo build -p koh-asm --locked, then cargo test -p
koh-lsp --locked. Cargo /usr/lib/rust/1.99.0/bin/cargo;
direct cargo-clippy with CARGO and RUSTC set to matching binaries works.
Direct rustfmt --edition 2024 avoids nightly cargo-fmt shim.

Main checkout unrelated battle.png preserved. Initial migration docs also
remain untracked there; use this branch's approved copies as authoritative.
Do not overwrite main HANDOFF.md, which belongs to the prior engine migration.

New native modules: symbols.rs resolves native symbol IDs and occurrences;
server.rs routes protocol, validates params, publishes diagnostic notifications;
instructions.rs preserves all46 instruction hover descriptions. Main uses Server.
Worker remains the only parser/semantic analyzer. Do not replace this with a
direct assembler dependency. Current URI handling is file-only; review the
extension's untitled selector before final editor acceptance. No live test known.
