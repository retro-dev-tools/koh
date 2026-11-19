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

## Configuration and core workspace increment

`cargo build -p koh-asm --locked`: exit `0`, local worker built from this
worktree. Log `/tmp/koh-lsp-local-asm.log`.

`cargo test -p koh-lsp --locked`, without KOH_ASM override: exit `0`, 27 passed,
zero failed/ignored. Completed log `/tmp/koh-lsp-workspace-final.log` was re-read
and its per-binary summaries summed. Strict Clippy with `-D warnings`: exit `0`,
`/tmp/koh-lsp-workspace-final-clippy.log`.

Covers configuration validation and reload, include syntax/graph/discovery,
UTF-16 positions and escaped file URIs, configured project isolation,
root-owned macros in an open include, overlays and close-to-disk restoration,
heuristic edge changes, shared include ownership and per-root include search.
Large unread worker writes also time out on two successive child instances.

RED/GREEN: `/tmp/koh-lsp-incbin-red.log` reported label offset12 rather than2
when unsaved text incorrectly replaced physical INCBIN data. Removed that
binary overlay; the full suite passes. `/tmp/koh-lsp-nested-roots.log` failed
ownership for a shared include with `../` in worker source provenance.
Boundary probe confirmed logical `a/../shared.inc` response paths; normalizing
file identity comparisons fixes ownership without rewriting provenance.
`/tmp/koh-lsp-nested-roots-green.log` confirms the focused fix.

83 managed source cases mapped to current native assertions;124 remain pending.
This is not complete feature parity. Editor requests/publishing still await
implementation; production remains managed. YAML parsing uses serde_yaml_ng
0.10.0 (upstream metadata Rust1.64) and URL handling reuses the workspace's
existing url2.5 dependency. Whole-target Rust1.88 validation remains open.

## Completed workspace parity pass

`cargo test -p koh-lsp --locked`: exit `0`,37 passed, zero failed/ignored;
`/tmp/koh-lsp-task2-final.log` re-read and summaries summed. Strict Clippy
exit `0`; `/tmp/koh-lsp-task2-clippy.log` re-read.

An additional RED/GREEN test (`/tmp/koh-lsp-unrelated-{red,green}.log`) pins the
existing invariant that editing one configured project must not rebuild
unrelated projects. Reuse unaffected project analyses; rebuild all owners of
an edited shared include. Separate tests cover heuristic ownership after close,
overlay text/disk restoration, and missing relative includes.

Worker bridge parity also proves warning severity/messages/UTF16 spans for
analyze and assemble, instruction bytes, virtual/dynamic includes, separate
text/binary snapshots, physical/virtual READFILE byte limits and explicit nested
resolution mappings. Logs `/tmp/koh-lsp-{bridge,resolver}-parity.log`.

Current inventory:125 mapped replacements,1 obsolete private C# adapter test,
81 pending editor/classification cases. Obsolete test asserted delegation to a
VirtualFileResolver seam; native workspace has no such adapter. Explicit worker
resolution-policy assertions retain its useful behavior. No feature removal.

Tasks1–2 complete. Native stdio remains lifecycle-only until Task3 connects the
workspace/editor methods. Production publishing is still managed.
