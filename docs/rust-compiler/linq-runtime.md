# Native LINQ target runtime

The Rust compiler recognizes the trusted framework `System.Linq.Enumerable`
declaration and redirects supported signatures to `KohRuntime` metadata in
Koh.GameBoy. It checks closed parameter and return types before lowering the
helper's ordinary CIL. Array sources use array adapters; other sources use the
typed enumerable protocol. Only reached implementations are emitted. There is
no host execution, managed compiler fallback, query placeholder, or BCL runtime
embedding.

The target implementation is internal C# game runtime code, compiled by Rust,
like the software floating-point runtime. Metadata reading, binding, lowering,
verification, optimization and machine-code generation remain Rust. Authors
continue to use normal C# `System.Linq` calls.

## Contract

- `Where` and `Select` use simple typed delegates. Queries are deferred, retain
  their source and captured state, and create a fresh cursor for each enumeration.
  They can escape a method, be stored, and cross a referenced assembly boundary.
- `Count`, `Any`, and their predicate forms, plus `All`, are supported. Count
  returns checked Int32. Any and All short circuit. Empty Count is zero, empty
  Any is false, and empty All is true. Typed cursor disposal cascades on normal
  exhaustion and early consumer disposal. Disposing an unopened query cursor
  does not open its source.
- `Sum` supports Int32, Int64, Single and Double, including selector overloads.
  Integer accumulation is checked. Single accumulates in Double before the
  final conversion. Empty sums return zero.
- `Min` and `Max` support signed and unsigned 8/16/32/64-bit integers, target
  native integers (16 bits), Single and Double, including numeric selectors.
  Non-nullable empty extrema fault. Numeric comparisons retain the first equal
  value. Floating minimum propagates the first NaN; maximum skips NaNs when a
  numeric element exists and retains the last NaN for an all-NaN sequence.
- Null sources and delegates fault before query construction. Null faults use
  existing abort code 04, checked overflow code 02, and empty extrema code 0B.
  These are fatal target traps, not catchable managed exceptions; exceptional
  unwinding is not claimed.

Queries, cursors, closures and cached delegates consume the existing bounded
monotonic arena. Repeated enumeration allocates fresh cursors. There is no GC
and no promise of allocation-free queries or suitability for per-frame use.
Exhausting the arena faults through its existing capacity checks.

Indexed delegates, comparers, nullable numeric overloads, materialization such
as `ToArray`, and other unlisted operators are unsupported and must diagnose at
the original call site. Nongeneric cursor Current and Reset are not part of the
supported typed protocol. Count and Any do not promise framework-specific
selector elision optimizations.

## Runtime metadata boundary

Runtime providers are static CIL methods on nongeneric declaring types. A helper
may have method parameters when its GenericParam metadata and signature arity
agree. Runtime keys are unique across supplied images; a reference declaration
without an implementation body is insufficient. Closed nominal signatures must
match the selected framework overload, with only the explicit enumerable-to-array
adapter substitution. Helper method names are not dispatch keys. Floating-point
operation providers retain their separate nongeneric raw-bit signature checks.

Malformed runtime declarations identify their assembly and method even when
found by global attribute validation before a CIL call site is visited. Reached
unsupported overloads and signature mismatches retain the caller's IL offset.

## Evidence and remaining acceptance

`tests/driver/linq.rs` drives Roslyn Debug/Release assemblies through native
compilation, ELF serialization/deserialization, Rust linking and the C# emulator
on DMG/CGB. The first expanded run passed 8/8 cases, 92 ROM executions, in
63.648 seconds (exit 0, `/tmp/koh-linq-expanded.log`). Assertions cover original
BGP values 24/9/3, deferred effects, repeated enumeration, early disposal,
cross-assembly queries, every numeric width, restored SP, memory guards and
unchanged packet tails. This is local Linux evidence, not current cross-platform
acceptance or a performance benchmark.

The subsequent failure selection passed 12/12 cases in 69.522 seconds, exit 0
(`/tmp/koh-linq-faults.log`): 120 ROM executions and 16 unsupported-overload
diagnostic checks. Null source/predicate/selector, empty array/query extrema,
and Int32/Int64 Sum overflow abort with the specified code before writing the
result packet. Indexed Where/Select, ToArray and comparer Max reject with an
operator name and original IL offset. These tests reuse the same resolver after
failed bindings before compiling valid faulting programs.

The affected IR, CIL lowering, SM83 and reference-resolution targets then passed
768/768 tests, zero skipped, in 207.441 seconds, exit 0
(`/tmp/koh-linq-core-regression.log`). Rust formatting, CSharpier on the three
changed C# files, and `git diff --check` also passed.

The floating-edge extension passed four Debug/Release × DMG/CGB cases in69.930s,
exit0 (`/tmp/koh-linq-floating-edges.log`). It adds12 ROM executions checking exact
Single/Double NaN payloads, numeric maxima despite NaNs, first-equal signed zeros,
and early-minimum versus exhausted-maximum disposal and selector side effects.

All four ordinary SDK cases passed (`/tmp/koh-linq-acceptance.log`):
77.457/93.244/5.117/6.793s. Projects and outputs contain spaces; each case runs two
`dotnet build` invocations and requires unchanged ROM content and modification
time on the second build. The SDK ROM equals independently relinking its canonical
ELF object. Root/runtime source mappings, KDBG presence and cartridge mode are
checked. Each ROM returns42 with BGP24, Count3, Max10, restored SP and packet guards.
Cold SDK native-tool builds account for the two slow cases; these are correctness
test durations, not compiler performance measurements.

That combined run was not green: its four debugger assertions assumed the display
frame name was a method name. Actual breakpoint PC, source line and unwound frames
were already correct; the debugger displays source file/line labels. Replacement
assertions check function symbols separately from frame source coordinates.
The corrected driver selection passed30/30 in308.833s, exit0
(`/tmp/koh-linq-final-driver.log`), including18 LINQ cases and12 existing
Single/Double/software-runtime cases. Four real debugger sessions verify selector
source breakpoints, exact stopped PC, disassembly bytes, root function symbols,
runtime source frames and the caller's source line after unwinding. Metadata
cases additionally reject inconsistent generic arity and invalid fault signatures.
Null-source execution leaves the entire poisoned heap unchanged before aborting.
The full compiler migration remains incomplete; these are local Linux results.

The final mixed-selector extension passed4/4 in73.614s, exit0
(`/tmp/koh-linq-mixed-final.log`). Its96 numeric/deferred ROM executions include
byte-to-Int32/Int64/Double sums and struct-to-numeric reductions. Across the current
LINQ targets,144 ROM executions plus four actual debugger sessions cover both
build modes and hardware variants; the selected existing float tests are separate.

## Source-test disposition

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilLinqTests.cs`.
Five methods, ten assertion sites, seven expanded cases and sixteen expanded
assertions. Their useful contracts have native replacements; the legacy source
remains only until the whole compiler project is removed. No compiler test is
deferred and no new retired-feature decision is hidden in this group.

| Source method | Behavioral contract and independent justification | Public contract or detail | Decision; nextest target |
| --- | --- | --- | --- |
| WhereSelectSum_VerifiesCleanAndComputesExpectedValue | Filter1..5 by>2, double3/4/5, sum6+8+10=24; accepted CIL must verify | Source semantics; replace C# verifier entry point | PORT; `driver::linq::linq_pipeline_reductions_execute`, Pipeline, both modes and hardware variants |
| WhereSelectSum_DebugAndReleaseProduceIdenticalObservableState | Both independently produce actual BGP24, rather than merely agreeing | Observable contract | REPLACE; same target with independent expected value in every case |
| MaxDirectlyOnArray_VerifiesCleanAndComputesExpectedValue | Maximum of3/9/1/7 is9; accepted CIL must verify | Source semantics; replace C# verifier entry point | PORT; same target, Max |
| MaxDirectlyOnArray_DebugAndReleaseProduceIdenticalObservableState | Both independently write BGP9 | Observable contract | REPLACE; same target, Max in both modes |
| MaxAfterPipeline_IsDiagnosedNotThrown | Positive filter preserves1/2/3, whose maximum is3; blanket rejection has no language justification | Former implementation restriction | REPLACE; same target, PipelineMax; unsupported overload diagnostics move to `linq_failures_and_unsupported_overloads_are_explicit` |

All targets are Rust rstest cases discoverable by cargo-nextest. The five driver
groups have18 cases; `sdk::linq::sdk_builds_deferred_linq_from_standard_references`
adds four. `linq_deferred_queries_and_numeric_widths_execute` strengthens coverage
with effects, escaped queries, numeric widths, exact floating bits and mixed-width
selectors. `linq_metadata_contracts_and_debug_provenance` checks16 rejected metadata/
reference bindings, metadata-key dispatch despite renamed helpers, fresh-resolver
determinism, and matching root/runtime/library PDB mappings through ELF/KDBG.
This closes five source-method dispositions, advancing the ledger514/565 with51
outstanding; physical deletion remains the final whole-migration gate.

The final metadata check also clears the fault intrinsic's MethodDef static bit
while retaining its static signature. Both modes reject that malformed declaration
with static-call and IL context; no production change was needed. The final
metadata target passed2/2 in13.634s, exit0
(`/tmp/koh-linq-static-intrinsic-check.log`).

Reproduce the complete LINQ selection with the supported .NET SDK installed:

```sh
cargo nextest run -p koh-compile --test driver --test sdk --locked -E 'test(linq)'
```

Workspace all-features discovery reports2083 cases,1906 compiler cases and zero
ignored (`/tmp/koh-linq-discovery.json`, exit0);18 driver LINQ and four SDK LINQ
cases are present. Discovery is not a full test run. The existing cubes
`support::build_host` dead-code warning remains; no current-head cross-platform
or full-workspace green claim is made.
