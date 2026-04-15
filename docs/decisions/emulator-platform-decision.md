# Decision: Blazor WebAssembly for Koh Emulator & Debugger

**Date:** 2026-04-10
**Status:** Accepted

## Context

The Koh project needs a first-party Game Boy / Game Boy Color emulator integrated with VS Code F5 debugging. Options considered:

1. Native executable (`koh-dbg.exe`) speaking DAP over stdio, emulator in-process
2. Blazor WebAssembly app hosted in a VS Code webview, debugger also in WASM
3. TypeScript emulator in the VS Code extension host

## Decision

Option 2: Blazor WebAssembly. The same compiled artifact runs in three modes (VS Code webview, standalone web, dev host), enabling reuse across debugging, playback, and contributor workflows.

## Consequences

### Accepted trade-offs

1. `Koh.Emulator.App` takes on Blazor as a core dependency. `Koh.Emulator.Core` stays BCL-only and AOT-compatible.
2. Blazor WASM AOT publish is required for release. Non-AOT is permitted for local development for faster iteration.
3. F5 cold-start is 1-2 seconds slower than a native binary due to Blazor runtime initialization.
4. Emulator debugging happens through `Koh.Emulator.Core.Tests` rather than by attaching a debugger to running Blazor WASM.
5. Cycle-accurate emulation is tick-driven at T-cycle granularity, with CPU bus events resolved at M-cycle boundaries.

### Relationship to LSP AOT decision

The existing `docs/decisions/lsp-aot-decision.md` documented that `Koh.Lsp` cannot be AOT-compiled due to reflection requirements in `StreamJsonRpc` and the LSP Protocol library. `Koh.Debugger` takes the same stance but through a different mechanism: Blazor WASM runtime initialization uses reflection, which is acceptable at the application-shell level while preserving AOT for the core emulator library.

## See also

- `docs/superpowers/specs/2026-04-10-emulator-debugger-design.md`
- `docs/decisions/lsp-aot-decision.md`
