# Koh Completion Plan — Index

This plan was split into 3 separate plans for independent execution.

## Plans

1. **[Compiler Identity Foundation](2026-04-02-koh-compiler-identity-foundation-plan.md)** — COMPLETE
   Symbol ownership model, `SymbolId`, `ResolveSymbol`, macros in `SymbolTable`, `EXPORT` promotion. All compiler-model changes.

2. **[LSP Symbol Features](2026-04-02-koh-lsp-symbol-features-plan.md)** — COMPLETE
   `SymbolFinder`, rename, semantic tokens, inlay hints, signature help, manual verification. All LSP handler work.

3. **[Backend Polish and Performance](2026-04-02-koh-backend-polish-and-performance-plan.md)** — IN PROGRESS
   Macro arity metadata, linker constraint diagnostics, parallel parsing, Native AOT, incremental invalidation. All 6 tasks remaining.

## Dependency Order

```
Plan 1 (Compiler Identity) ──→ Plan 2 (LSP Symbol Features)    [both complete]
Plan 3 Task 1 (Macro Arity) ──→ Plan 2 Task 5 (Signature Help) [Plan 2 done; LSP uses body-scan fallback]
```

Plan 1 and Plan 2 are complete. Plan 3 is independent and can proceed in order.

## Reference

- **Spec:** `docs/superpowers/specs/2026-03-25-koh-assembler-design.md`
- **Original plan:** `docs/superpowers/plans/2026-03-25-koh-implementation-plan.md`
