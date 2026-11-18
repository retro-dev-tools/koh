# Static storage source-test migration

Nine of the ten methods in `Frontends/CilStaticsTests.cs` now have native Rust
replacements. The remaining5000-byte allocation scenario is explicitly open.
This is test migration over the existing Rust compiler, not a new compiler/API
layer or a promise that legacy storage placement is correct.

## Independent contracts

[C# readonly fields](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/readonly)
prevent replacing a reference after initialization; they do not freeze its
object's contents. The five-byte table retains10/20/30/40/50, then a legal element
write changes30 to99. ROM-only placement without an immutability proof would
violate this contract. The readonly reference itself is not reassigned.

[C# constants](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/const)
are compile-time values. The literal-only entry returns/writes42 and needs no
static RAM in its ELF. Mutable arrays instead retain actual object identity:
assigning a new array produces zeroed elements while an alias to the old array
still sees7/9. Counter state starts zero, persists through five helper calls and
wraps255 to0 through the explicit byte conversion.

The noncapturing lambda must execute5+1=6. Deleting compiler-generated constructors
or fields is not a source requirement. Its replacement checks two actual calls,
two body side effects and one explicit owning-type initialization, with BGP6.
The compiler may retain the needed singleton/cache construction. Unreached large
array initialization must not contaminate the selected small entries.

## Source disposition matrix

Source: `tests/Compiler/Koh.Compiler.Tests/Frontends/CilStaticsTests.cs`.
The nine completed methods contain29 assertion sites, fourteen original expanded
cases and54 expanded assertions. The separate large-array method has six sites
and two expanded cases; it is excluded from these completion counts.

| Source method | Behavioral requirement / independent expectation | Contract or detail | Decision; native target |
| --- | --- | --- | --- |
| MutableStaticCounter_IncrementedAcrossCallsAndReadBack | Zero initialization, five increments, actual LCDC5; additional byte wrap to0 | Mutable state contract; replace C# IR address-space enum | PORT; `driver::static_contracts::static_storage_preserves_values_mutability_identity_and_initialization`, Counter |
| MutableStaticCounter_DebugAndReleaseProduceIdenticalObservableState | Each independently produces5, initial0 and wrapped0 | Observable contract | REPLACE; same target in both configurations and hardware variants |
| StaticReadonlyByteArray_LandsInRomWithInitialBytes | Exact authored bytes, LCDC30/BGP50, legal mutation to99 | Initial values are public; ROM placement is an invalid general assumption | REPLACE; same target, Table |
| StaticReadonlyByteArray_DebugAndReleaseProduceIdenticalObservableState | Independently expected initial and mutated values in each mode | Observable contract | REPLACE; Table |
| ConstField_FoldedByRoslynWithNoBackingStorage | Literal42 and no allocated static RAM | Compile-time value/storage contract; replace private global-name search | PORT; Constant |
| ConstField_DebugAndReleaseProduceIdenticalObservableState | Actual LCDC42 and result bytes in both modes | Observable contract | REPLACE; Constant |
| MutableStaticArray_GetsDedicatedWramGlobalNotHeap | Initial zeros, writes7/9, replacement-array zeros, old alias still7/9 | Identity/mutation contract; dedicated placement is private | REPLACE; MutableArray |
| MutableStaticArray_DebugAndReleaseProduceIdenticalObservableState | Independently verify all eight result bytes in both modes | Observable contract | REPLACE; MutableArray |
| NoCaptureLambda_DoesNotResurrectCompilerGeneratedCctor | Two results6, initialization count1, body count2 and BGP6 | Observable calls; constructor/cache elision is private | REPLACE; CachedLambda |
| MutableStaticArray_CrossesWramBoundaryAndRetainsEndpointWrites |5000 elements, endpoints7/9, aggregate result16; absolute C000/header layout is private | Useful capacity contract plus obsolete placement details | OPEN, not DEFER/DELETE; `StaticContracts::LargeArray` is a reproducible current failure, not a passing native replacement |

Two PORT and seven REPLACE decisions complete nine methods. No failing capacity
assertion was weakened into an accepted failure. Legacy source definitions remain
until the final compiler/test-project deletion. The ledger advances530/565,
529 native replacements plus one earlier obsolete-parser DELETE,35 outstanding.

## Native evidence

Four rstest cases build actual Roslyn DLLs in Debug/Release and execute five roots
each on DMG/CGB:20 ROMs. Native compilation verifies the IR, emits canonical ELF,
round-trips it and uses the Rust linker. Assertions cover explicit return42,
restored SP, complete result-packet tails, actual LCDC/BGP values, untouched OAM/
VRAM and the alternate CGB WRAM bank. Static/heap setup starts poisoned; correctness
does not depend on emulator constructor zeros or display defaults. The same resolver
is reused across entries, and Constant still has no stale static allocation.

The first selection passed4/4 in13.266s, exit0
(`/tmp/koh-static-contracts-first.log`). The final affected selection passed64/64
in94.287s, exit0,557 explicitly unselected (`/tmp/koh-static-contracts-final.log`),
including existing initialization cycles, static references, aligned/RVA arrays,
stored delegates and closed generic storage. No production compiler/emulator or
game API changes were required. These are local Linux results.
All-features workspace discovery reports2097 cases,1920 compiler cases, zero
ignored and all four new static cases (`/tmp/koh-static-contracts-discovery.json`,
exit0). Discovery is not execution. The existing cubes dead-code warning remains.
Rust formatting, CSharpier and `git diff --check` passed.

```sh
cargo nextest run -p koh-compile --test driver --locked -E 'test(static_contracts::)'
```

## Outstanding5000-byte capacity requirement

The current contiguous arena is4KiB;5000 payload bytes plus its header cannot fit.
Both new fixture DLLs were compiled through the native CLI, linked from ELF with
the Rust CLI and executed in the existing emulator. Compile/link exited0; each
ROM reached allocation fault05 with intact guards and an unchanged poisoned heap.
It did not return the required16 (`/tmp/koh-static-capacity/results.json`). That
safe failure is evidence of the remaining limitation, not acceptance of the legacy
method. No committed nextest case treats this limitation as a permanent contract.

The old absolute C000 placement and two-byte layout are unnecessary. The useful
contiguous capacity still requires a deliberate memory-layout solution or explicit
product decision: the current fixed stack/arena division is not an inherited
hardware specification. General banked storage and large-array acceptance remain
part of the full migration, with no source-method closure claimed here.
