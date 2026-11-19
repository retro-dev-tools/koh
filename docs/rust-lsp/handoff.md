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

59/59 native tests pass, strict Clippy exit0. Completed logs in evidence.md.
Managed oracle207/207 pass.206 disposition rows mapped,1 obsolete private adapter,
zero pending. Native tests
run without KOH_ASM using freshly built target/debug/koh-asm. Production remains
managed. Native main now serves all original methods through Server; real
stdio child/worker checks cover sync/diagnostics/hover/lifecycle. Full editor
requests also have direct protocol checks against real worker analyses.

Task2 parity review complete. Editing unrelated configured files reuses cached
project analysis; editing shared includes rebuilds both owners. READFILE,
custom nested resolver mappings, warnings and native instruction/data bytes
covered through the real worker for physical/virtual fixtures.

Next: Task4 native publishing, managed server/test retirement and real VS Code
acceptance, then Rust1.88/platform/package evidence and final review.
Plan docs/superpowers/plans/2026-11-19-rust-lsp.md.
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
direct assembler dependency. File/untitled URIs work, including URI preservation
and no disk writes for untitled buffers. features.rs owns hints, signatures,
outline/completion; tokens.rs owns explicit classification and bounded line
splitting. Protocol-v1 kind fixture retained under tests/fixtures. No live test.

Before Task4, inspect build.proj, NativeBuild.targets, ci.yml and release workflow.
They still publish managed LSP. Reuse existing RID mapping for a native
GetNativeLanguageServerPath/root publish target; ship koh-lsp plus koh-asm.
Add actual extension LanguageClient integration. Retire only replaced managed
server/tests; preserve other Koh.Native users. Do not push or merge.
