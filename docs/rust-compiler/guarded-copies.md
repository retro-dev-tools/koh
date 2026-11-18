# Guarded and sequential copy acceptance

Three public behavior methods from `Sm83/RedundantCompareLoopTests.cs` now have
native replacements. The remaining private compare-graph method stays open;
this unit does not establish the real `MapWriter.FlushRun` timing contract.

| Source method | Requirement | Decision and native location |
| --- | --- | --- |
| `GuardedStride1CopyLoop_CopiesEveryByte_WhenGuardOpen` | Eight ordered reads/writes and returned count8 at gate148 | PORT: `RewriteContracts.GuardedCopy`, `support/rewrites.rs`, `sm83.rs` |
| `GuardedStride1CopyLoop_CopiesNothing_WhenGuardClosed` | Returned count0 and untouched destination at gate200 | PORT: same fixture, with exact absence of destination accesses after sentinel initialization |
| `TwoSequentialStride1CopyLoops_BothCopyCorrectly` | Both six-byte copies retain independent source/destination pairs | PORT: `RewriteContracts.SequentialCopy`, same native runner |

These three methods contain six lexical assertion sites, including assertions
inside loops. Source assignment/control-flow semantics independently justify
their expected values; the old compiler's register choices do not.

The ordinary Roslyn fixture emits Debug and Release CIL. Ten guarded-copy inputs
retain the original148/200 cases and add143/144/152/153 boundaries, zero counts,
one-byte copies and byte255. An independent Rust oracle uses inclusive144..152
membership rather than duplicating the CIL's truncated subtraction expression.
All eight destination bytes are first initialized to EE. Exact full traces check
every initialization and subsequent access, including no out-of-window writes.

Sequential copies retain the original C100/C140 sources, C200/C220 destinations,
six-byte lengths and10/80 byte prefixes. Four gate pairs check both copies open,
only the first, only the second and neither. The return packs both actual counts;
strict trace equality also checks all twelve copied values and source order.

Each input runs with runtime arguments and bound constants, before/after Rust
optimization, in Debug/Release:112 added ROM executions. The existing runner
verifies typed IR and the complete program, emits ELF through the Rust backend,
links with the Rust linker and executes in the existing C# emulator. SP/result/
guard and exact observed-access checks remain enabled. No production compiler,
emulator or game API changes are needed; no cycle ceiling is relaxed.

```sh
KOH_DOTNET=/path/to/dotnet cargo nextest run -p koh-compile \
  --lib --test sm83 --locked \
  -E 'binary(koh_compile) | test(machine_rewrites_preserve)'
```

Compiler-library plus the complete2160-ROM rewrite fixture passed222/222 native
tests in22.468s, exit0;207 explicitly unselected. Completed log:
`/tmp/koh-guarded-copy-final.log`. Stable Rust formatting, CSharpier and diff
checks passed.

Other loop-codegen timing and real graphics acceptance remain separate gates.
