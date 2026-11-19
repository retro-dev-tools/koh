# Rust LSP migration

Status: approved by the user on 2026-11-19.

## Boundary

Replace the managed `koh-lsp` executable with a Rust Cargo workspace member
`rust/koh-lsp`. Preserve the executable name, stdio LSP transport and VS Code
launch contract. Keep the assembler worker process, as selected by the user.
Do not call assembler algorithms in-process or introduce a managed fallback.

The Rust server owns document synchronization, project configuration, include
discovery, project isolation, symbol queries and LSP response construction.
It invokes the existing `koh-asm --serve-json` worker protocol for parsing and
semantic analysis. Reuse that protocol and its analysis model; do not implement
another assembler parser. Worker lifecycle, transport errors and malformed
responses must produce controlled failures without contaminating LSP stdout.

## Compatibility

Preserve initialization and shutdown, full document synchronization,
diagnostics, hover, definitions, references, document symbols, completion,
semantic tokens and their legend, prepare-rename/rename, inlay hints and macro
signature help. Preserve UTF-16 positions, CRLF handling, source provenance,
local-label scope and project ownership.

Preserve `koh.yaml` version 1 configuration, configured/heuristic/invalid modes,
configuration reload, include discovery, unsaved document overlays and
cross-project isolation. Invalid configuration must not silently enable
heuristic discovery. Preserve disk/include resolver behavior and platform path
handling. Keep existing C# game authoring and unrelated toolchain components.

## Change surfaces

- `src/Asm/Koh.Lsp`: replace all production server behavior.
- `tests/Asm/Koh.Lsp.Tests`: map each existing case to native coverage before
  retiring managed tests; 207 test attribute declarations found at baseline,
  not an executed case count.
- `src/Asm/Koh.Native` and `rust/koh-asm/src/service.rs`: protocol references;
  retain bridge users outside the migrated LSP.
- Cargo workspace and lockfile: server target and required dependencies.
- `build.proj`, solution/filter files and package references: remove retired
  LSP entries and publish the native executable.
- `.github/workflows/{ci,release-toolchain}.yml`: build, test and package the
  native server plus the assembler worker on supported platforms.
- `editors/vscode`: retain executable discovery; verify actual client startup,
  requests and shutdown with the packaged native server.
- `ROADMAP.md` and migration documentation: record progress and evidence.

## Implementation order

1. Capture managed test dispositions and representative stdio responses.
2. Implement native stdio lifecycle and assembler worker client with transport
   and failure tests.
3. Port configuration, discovery, overlays and workspace ownership.
4. Port editor methods and map their existing test cases.
5. Switch build/publish/CI paths and remove replaced managed server/tests.
6. Validate packaged editor workflow and supported-platform execution.

Commit independently working increments. Do not merge, push or publish a
release without authorization. Preserve the existing unrelated battle image.

## Acceptance gates

- Every existing test case has a reviewed replacement or justified obsolete
  disposition; no silent feature removal.
- Native tests prove protocol framing, worker errors, Unicode positions,
  overlays, includes, project isolation and all advertised methods.
- A real stdio session proves initialize, document edits, editor requests,
  diagnostics, shutdown and exit through the worker process.
- Build and release packaging include executable `koh-lsp` and `koh-asm`
  without managed LSP runtime requirements.
- VS Code starts the packaged server and exercises navigation and diagnostics.
- Relevant native, managed and extension regressions pass, with completed
  logs and actual exit codes recorded. Platform claims require platform runs.
- Retired managed LSP code and build references are removed after replacement
  verification. Unrelated managed tools remain supported.

No new LSP features, debugger work, assembler redesign or release publication
belongs to this migration.
