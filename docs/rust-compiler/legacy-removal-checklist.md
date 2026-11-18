# Legacy compiler removal checkpoint

Removal is pending replacement acceptance. This list records remaining consumers
so deleting the compiler directory cannot silently strand an active tool.

- Delete `src/Compiler/Koh.Compiler` and the migrated
  `tests/Compiler/Koh.Compiler.Tests` project after the disposition ledger closes.
- Remove their entries from `Koh.slnx`, `Koh.Ci.slnf`, `Koh.Fast.slnf` and root
  build targets. Keep the existing noncompiler component tests.
- Recheck `Directory.Packages.props` ownership before removing Mono.Cecil and
  other compiler-only packages.
- Completed: remove the direct compiler references from both Superopt projects
  and replace the MIR import with the existing opcode table. All16 original tool
  tests plus three boundary regressions pass; no old MIR is copied. See the
  [ownership, independent filter and evidence](superopt-boundary.md). Final
  solution/cross-platform validation remains required.
- Reconcile root README, CONTRIBUTING, AGENTS, CLAUDE and HANDOFF instructions
  with the actual native commands and dependency boundaries. Historical design
  documents must be identified as historical rather than advertised as current.
- Recheck SDK tasks, discovery, release archives, CI, editor launch/debug paths
  and all project references for surviving compiler routes.
- Verify the final absence of the old source tree, production invocation paths
  and compiler-only dependencies, then run applicable native and noncompiler
  .NET checks. A text search alone does not prove working integration.

The additional Superopt consumers are outside the565-method legacy compiler
source ledger. Their test ownership and disposition must be recorded separately;
the565 count must not be presented as a repository-wide test-completion count.
