# Legacy compiler removal checkpoint

Removal is integrated on the PR branch after native2239/2239 and separate
doctests passed. This list distinguishes completed retirement from final
platform/product/release acceptance.

- [x] Delete `src/Compiler/Koh.Compiler` and the migrated
  `tests/Compiler/Koh.Compiler.Tests` project; all565 source dispositions complete.
- [x] Remove their entries from `Koh.slnx`, `Koh.Ci.slnf`, `Koh.Fast.slnf` and root
  build targets; retain existing noncompiler component tests.
- [x] Remove direct compiler-only Mono.Cecil/Roslyn package versions after checking
  ownership, plus the unused Testcontainers version. Test-runner coverage has its
  own independent Cecil payload and is not a compiler fallback.
- [x] Remove the direct compiler references from both Superopt projects
  and replace the MIR import with the existing opcode table. All16 original tool
  tests plus three boundary regressions pass; no old MIR is copied. See the
  [ownership, independent filter and evidence](superopt-boundary.md). Final
  solution/cross-platform validation remains required.
- [x] Reconcile root README, CONTRIBUTING, AGENTS, CLAUDE and HANDOFF instructions
  with the actual native commands and dependency boundaries. Historical design
  documents must be identified as historical rather than advertised as current.
- [x] Verify absent legacy trees and project/target/workflow references. Preserve
  ignored old build outputs outside the checkout. Native unfiltered tests and
  the retained .NET suite pass; all seven C# sample ROMs build through the SDK.
- [x] Validate Linux staged compiler/linker, canonical ELF/ROM/KDBG and NativeAOT
  emulator execution from paths with spaces, including archive extraction and
  exact Cargo dependency notices. [Evidence](packaged-tools.md).
- [x] Finish full three-OS implementation validation at3d15f2d: all14 CI jobs
  passed, including native SDK/debug tests, editor tests and artifact smoke checks.
- [ ] Finish release review; see the [acceptance audit](acceptance.md) for exact
  verification scope, historical-runtime measurement limits and notice gaps.

The additional Superopt consumers are outside the565-method legacy compiler
source ledger. Their test ownership and disposition must be recorded separately;
the565 count must not be presented as a repository-wide test-completion count.
