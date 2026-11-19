# Rust LSP migration evidence

## Managed baseline

Source: master `065cf05`, 2026-11-19.

Command: `dotnet run --project tests/Asm/Koh.Lsp.Tests -c Release`.
Pinned SDK `/home/minorum/.local/share/koh-dotnet-10.0.115`, Rust 1.99.0.
Completed process exit: `0`.
Re-read log: `/tmp/koh-lsp-managed-baseline.log`.

```text
Test run summary: Passed!
  total: 207
  failed: 0
  succeeded: 207
  skipped: 0
  duration: 12s 694ms
```

Source inventory: `test-dispositions.md`, 207 methods, all pending migration.

Real managed stdio probe completed with server exit `0`: initialize, didOpen,
hover, definition, semanticTokens/full, shutdown and exit. Response assertions
required capabilities, non-null hover, nonempty definition and semantic token
data, and null shutdown result. Captured responses and notifications:
`/tmp/koh-lsp-managed-stdio-baseline.json`.

This baseline is not Rust implementation evidence. Full editor compatibility,
native replacement coverage, packaging and platform acceptance remain open.

## Native transport increment

Worktree: `/home/minorum/projekty/koh-rust-lsp`, branch `feat/rust-lsp`.
Approved worker-process design; native transport and worker client implemented.
No editor methods or workspace behavior yet; production publishing still uses
the managed server.

`cargo test -p koh-lsp --locked`, with `KOH_ASM` pointing to the baseline
assembler: exit `0`, 7 passed, zero failed/ignored. Completed log re-read:
`/tmp/koh-lsp-transport-green.log`. Covers Unicode/consecutive frames, header
validation, initialize/shutdown/exit, real worker reuse and UTF-16 spans, EOF,
bad JSON/version/response fields and a hung worker's deadline.

RED logs: `/tmp/koh-lsp-transport-red.log` (missing native implementation),
`/tmp/koh-lsp-worker-shape-red.log` (accepted incomplete worker response).
The latter failed with `Ok` response `{"version":1}` before validation was added.

Strict Clippy: `cargo-clippy clippy -p koh-lsp --all-targets --locked -- -D warnings`,
exit `0`; `/tmp/koh-lsp-transport-clippy-strict.log`.
Use explicit Rust 1.99 cargo/clippy/rustfmt paths on this host; cargo subcommand
discovery otherwise selects an unavailable rustup nightly component.
