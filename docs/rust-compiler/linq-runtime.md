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

The original five LINQ source tests remain open in the disposition ledger until
the complete acceptance unit lands. NaN/signed-zero payload tests, metadata
mutation tests, source/PDB/KDBG provenance and ordinary SDK builds still require
LINQ-specific validation. The full compiler migration remains incomplete.
