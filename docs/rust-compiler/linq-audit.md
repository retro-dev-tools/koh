# LINQ semantic audit and current blockers

Status: implementation incomplete. No source-test closure or executable LINQ
support is claimed by this audit. All five original methods remain outstanding;
the source ledger stays 509/565, with 56 outstanding.

## Original assertions

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilLinqTests.cs`.
Five methods contain ten assertion sites, seven expanded cases and sixteen
expanded assertions. Frontend diagnostic and verifier checks accompany actual
BGP observations in the two parameterized execution methods.

| Source method | Requirement | Decision and independent expectation |
| --- | --- | --- |
| WhereSelectSum_VerifiesCleanAndComputesExpectedValue | Filter, project, reduce in Debug/Release | PORT: 3,4,5 become 6,8,10; sum 24 |
| WhereSelectSum_DebugAndReleaseProduceIdenticalObservableState | Optimization-mode parity | REPLACE parity alone with independent BGP 24 in each mode |
| MaxDirectlyOnArray_VerifiesCleanAndComputesExpectedValue | Integer maximum in Debug/Release | PORT: maximum of 3,9,1,7 is 9 |
| MaxDirectlyOnArray_DebugAndReleaseProduceIdenticalObservableState | Optimization-mode parity | REPLACE parity alone with independent BGP 9 in each mode |
| MaxAfterPipeline_IsDiagnosedNotThrown | Former compiler boundary | REPLACE: filtering 1,2,3 by positivity leaves maximum 3; rejection has no semantic justification |

None is yet backed by a completed native LINQ acceptance test. This table does
not retire the old project or count planned Rust tests as migrated.

## Empirical native probe

At compiler commit `7191ce6`, rebuild the debug CLI with:

```sh
cargo build -p koh-compile --bin koh-compile --locked
```

Create a net10.0 library with ordinary SDK/Roslyn, no Koh API dependency:

```csharp
using System.Collections.Generic;
using System.Linq;
public static class Program
{
    private static IEnumerable<int> Source()
    {
        yield return 1;
        yield return 2;
        yield return 3;
    }
    public static int Sum() => new[] {1,2,3,4,5}.Where(x => x > 2).Select(x => x * 2).Sum();
    public static int Max() => new[] {3,9,1,7}.Max();
    public static int PipelineMax() => new[] {1,2,3}.Where(x => x > 0).Max();
    public static int Count() => new[] {1,2,3}.Count();
    public static int Any() => new[] {1,2,3}.Any() ? 42 : 0;
    public static int IteratorSum() => Source().Sum();
    public static int IteratorPipeline() => Source().Where(x => x > 0).Select(x => x * 2).Sum();
}
```

Build with `dotnet build -c Release`, then invoke each entry:

```sh
koh-compile Audit.dll --entry Program::Sum --no-pdb -o Sum.o \
  -r "$runtime/System.Runtime.dll" \
  -r "$runtime/System.Private.CoreLib.dll" \
  -r "$runtime/System.Linq.dll"
```

Here `$runtime` is the Microsoft.NETCore.App runtime directory reported by
`dotnet --list-runtimes`; use the same installation as the fixture build.
With SDK 10.0.115/runtime 10.0.15, the five array entries all exited 1 without an
object, reporting `CIL operand type mismatch: Array(Scalar(I32), Vector), expected
Object(...)` at the call site. This is a missing supported conversion for valid
CIL, not evidence that the source program is malformed.

The iterator source bypasses that conversion. `IteratorSum` then exited 1 without
an object at `System.Linq.Enumerable::Sum IL_0001`, reporting
`Closed type constraint binding exceeds64 levels`. The current type closer
validates constraints before interning the closed instance; recursive constraint
graphs therefore need investigation. Increasing the depth limit is not a fix.
Any solution must reject invalid recursive constraints and remain correct when a
resolver is reused after a failed closure; an unchecked provisional cache entry
must not make a later invalid instantiation succeed.

`IteratorPipeline` exited 1 for missing `System.Collections` with this deliberately
minimal reference set. That is a genuine missing-reference diagnostic, not proof
that Where itself is unsupported. Do not use it as a lowering failure oracle.
Supplying the same runtime's `System.Collections.dll` advances this probe to
`System.Linq.Enumerable::Where IL_0000: Reference class has no base type`, exit 1,
recorded in `/tmp/koh-linq-audit-complete-references.log`. This layout-resolution
failure also needs a minimal independent fixture before choosing a workaround.

### Framework root layout correction

The layout failure was reproduced directly on the real CoreLib `System.Object`
definition, without LINQ or query rewriting. Class layout recognized an Object
*base reference* but did not recognize Object's own base-less definition. It now
recognizes that definition through the existing framework assembly identity/key
checks and requires its base to be null. Ordinary direct-Object descendants keep
their existing layout. An unrelated same-named `System.Object` with a null base
still rejects; a full-name match alone cannot grant framework root semantics.

Two Debug/Release rstest cases in
`driver::reference_contracts::framework_object_definition_has_a_root_layout`
check the real two-byte object header, absent base/fields, class identity and the
untrusted name-collision mutation. The pre-fix run failed on the real root with
`Reference class has no base type` (`/tmp/koh-framework-root-red.log`, exit100).
The focused post-fix selection passed14/14 in18.398s, exit0, with559 explicitly
unselected (`/tmp/koh-framework-root-green.log`), including existing framework
interface, real Math.Max and iterator ROM cases. This fixes the shared layout
bug; it does not establish LINQ execution or resolve recursive constraints.

Expanded layout/reference/generic/interface coverage passed148/148 in136.815s,
exit0,489 explicitly unselected (`/tmp/koh-framework-root-regression.log`). A fresh
CLI build exited0 (`/tmp/koh-framework-root-cli-build.log`). The identical
iterator pipeline probe now advances into Where's body and rejects at IL_0000
with `Uninitialized locals require definite-assignment lowering`, exit1, no
object (`/tmp/koh-framework-root-linq-probe.log`). That next supported-CIL gap
needs definite-assignment analysis, not unconditional zero initialization or a
blanket promise that ordinary framework LINQ now works.

The subsequent [local-assignment implementation](local-assignment.md) removes that
blanket gate with bounded verification and actual nonzeroed C# ROM tests. A fresh
iterator pipeline probe now reaches generic declaring storage at Where IL_0020,
exit1/no object. Recursive constraints and array/interface adaptation remain open;
the five LINQ methods still have no completed native acceptance replacements.

The subsequent [generic callback scope correction](delegates.md#generic-declaring-and-method-scopes)
also fixes ordinary Func fields, invocation, captures and method groups in generic
classes. Thirty-two independent callback ROM executions cover that behavior.
The same iterator pipeline now reaches Where IL_0040 and rejects with
`castclass requires a supported reference class`, exit1/no object
(`/tmp/koh-generic-callback-linq-probe.log`): the framework body tests whether its
source is an array, outside current reference-class RTTI. This does not justify
classifying valid array casts as malformed or promise that complete framework
LINQ bodies are a sensible Game Boy runtime. Array/interface adaptation, recursive
constraints and actual query implementation remain required decisions.

[Canonical foreign callback arguments](foreign-callback-scopes.md) subsequently
remove the single-scope limitation for referenced game/runtime types, including
nested callbacks. A fresh CLI probe still stops at the array type test in Where
IL_0040, exit1/no object. This is prerequisite compiler work, not LINQ acceptance.

Fresh `cargo nextest list --workspace --all-features --locked --message-format
json` discovery reports2019 workspace cases,1842 compiler cases and zero ignored
(`/tmp/koh-framework-root-discovery.json`, exit0). Existing cubes dead-code warning
remains. Discovery is not a full test run; no current-head three-platform claim
is made. Source dispositions remain509/565,56 outstanding.

Local evidence: `/tmp/koh-linq-audit-native-build.log` reports successful native
build; `/tmp/koh-linq-audit-build.log` and
`/tmp/koh-linq-audit-build-iterator.log` report zero build warnings/errors, exit 0.
Per-entry exit codes, diagnostics and output absence were reread from
`/tmp/koh-linq-audit-results.json` and
`/tmp/koh-linq-audit-iterator-results.json`. These are compiler rejection probes,
not ROM execution or green regression results.

## Independently required semantics

The .NET 10 reference implementations provide evidence of the required distinctions:
[Where](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Linq/src/System/Linq/Where.cs),
[Sum](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Linq/src/System/Linq/Sum.cs),
[Max](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Linq/src/System/Linq/Max.cs).
Their implementation machinery is not a required Koh architecture.

- Where checks null arguments before returning a deferred query. Enumeration
  applies predicates in source order; repeated enumeration must not consume a
  single shared cursor. Stored, returned and nested queries need real values.
- Integer Sum uses checked accumulation, not wrapping arithmetic. Its result
  type follows the selected overload; a selector can change the element width.
  Single-precision Sum uses a double-precision accumulator before its final cast.
- Non-nullable numeric maximum needs an empty-sequence failure distinct from
  reading element zero. Negative-only inputs, signed boundaries and duplicate
  maxima must be checked independently.
- Count returns Int32, regardless of the smaller physical Game Boy array limit.
  Any/All need short-circuit behavior and defined empty-sequence results.
- Consumer disposal, normal exhaustion, early break and predicate/selector
  side effects need observable tests. Fatal Koh traps do not establish support
  for catchable managed exceptions or exceptional cleanup.

The old compiler uses an I16 Count accumulator, unchecked Sum addition, the
original array element type to choose a Sum accumulator, unconditional element-zero
seeding for extrema, and zero-valued compile-time pipeline placeholders. These
are inspection findings requiring replacement tests, not behaviors to preserve.
Only its numeric examples and useful source operations are acceptance evidence.

## Implementation decision boundary

First distinguish ordinary CIL support gaps from target-runtime requirements.
Do not add a second compiler model or reproduce placeholder/provenance fusion.
Ordinary managed target-runtime helpers, compiled entirely by Rust like the
existing software-float runtime, are an option if full framework implementations
require unjustified hardware machinery. Such a choice must include metadata
identity/signature validation, actual deferred query objects, array adapters,
bounded allocation costs, and documented unsupported overloads. Generic helpers
also require a deliberate runtime metadata contract; relaxing the existing
global nongeneric restriction without an actual caller is not an implementation.

Acceptance still requires Rust-driven Roslyn fixtures in both modes, ELF/linker
round trips, emulator observations including original BGP values and failure
packets, reference reachability and SDK/PDB checks. No new support, optimization,
allocation or performance claim follows from this audit alone.
