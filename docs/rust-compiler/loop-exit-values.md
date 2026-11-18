# Loop exit and saved-value contracts

Six methods in `Sm83/Sm83LoopCodegenTests.cs` assert language-visible values,
not a required register-allocation policy. Their six lexical assertion sites
now have native PORT replacements in the ordinary Roslyn `RewriteContracts`
fixture, `tests/support/rewrites.rs` and the `tests/sm83.rs` rstest runner.

| Source method | Fixture | Independently expected original behavior |
| --- | --- | --- |
| `InductionValueUsedAfterLoopExit` | `LoopExit` | C100 receives50 after50 increments |
| `InductionValueUsedAfterLoopExit_WithInterveningClobber_StillCorrect` | `LoopCall`, `Touch` | C100 receives55; caller also retains50 across the call |
| `NestedLoops` | `LoopNested` | Ten by ten increments store100 |
| `EarlyBreakExitEdge` | `LoopBreak` | Limit100/break42 stores42 |
| `InductionValueSavedBeforeIncrement_UsedAfterIt` | `LoopOld` | Ten successive stores are0..9, never1..10 |
| `InductionValueSavedBeforeIncrement_ReadAfterLoopExit` | `LoopLast` | C101 receives9 while final counter is10 |

All original values remain in the input matrix. Other inputs check zero trips,
one trip, a break at zero, a break equal to the limit, and intermediate breaks.
Nested counts include zero outer/inner trips and16x16/17x17, whose explicit byte
truncation produces0/33. The Rust oracle computes nested totals by multiplication
and exits from bounds, rather than copying the compiler's control-flow graph.

The test checks full return values and exact physical access traces, including
every old-value store. Packed return values independently expose the counter
after the call and after the last-value loop; memory-only checks cannot hide a
corrupted live counter. Existing stack/guard checks remain active.

Six inputs per shape, runtime and bound arguments, before/after optimization,
Debug/Release CIL produce288 added ROM executions. Each variant verifies typed
IR and program calls, emits canonical ELF, links through Rust and executes in
the existing C# emulator. No production changes, private allocator assertions or
timing substitutions are introduced. The separate190/740-dot ceilings and other
loop-codegen methods remain open.

```sh
KOH_DOTNET=/path/to/dotnet cargo nextest run -p koh-compile \
  --lib --test sm83 --locked \
  -E 'binary(koh_compile) | test(machine_rewrites_preserve)'
```

Completed compiler-library/fixture selection:222/222 passed in27.956s, exit0;
207 explicitly unselected. Log: `/tmp/koh-loop-values.log`. Stable Rust fmt,
CSharpier and diff checks passed. The full rewrite fixture now executes2448 ROMs.
