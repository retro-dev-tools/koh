# Struct-return test replacement

All five methods in `tests/Compiler/Koh.Compiler.Tests/Frontends/CilStructReturnTests.cs`
have complete PORT dispositions in
`driver::struct_returns::struct_results_preserve_ordered_fields_and_frame_independence`.
Four rstest cases are discoverable by cargo-nextest. Actual Roslyn Debug/Release
DLLs/PDBs become canonical ELF, banked linked ROMs and existing-emulator execution
on DMG/CGB. No production compiler behavior changes in this test increment.

| Original method / fixture | Public requirement and independent oracle | Native entry | Decision |
| --- | --- | --- | --- |
| StructReturn_BuildConsumeReverseAndChain / LineSource | Returned values, reversed field order, independent copies and chained temporary calls; reversed40,30,20,10, sum100 and chained sum10 | Lines | PORT |
| StructReturn_FactoriesInstanceMethodsLoopsAndStatics / FactorySource | Static aggregate stores, instance returns and loop-carried results; translated point110,10, original100,−50, triangular sums15,30 | Factories | PORT |
| StructReturn_ThroughRecursion / RecursionSource | Caller-owned returned values survive recursive/repeated calls; Fibonacci pairs0,1 /1,1 /55,89 /89,144 | Recursion | PORT |
| StructReturn_FromMonomorphizedGenericMethod / GenericSource | Generic calls preserve both returned fields; inputs200/tag1 and40000/tag2 | Generic | PORT |
| StructReturn_UnlocksPatternBasedForeach / ForeachSource | Fresh by-value enumerators, ordered0,10,20,30, sum60 and second enumeration count4 | Enumeration | PORT |

The independent language basis is C# [struct value/copy semantics](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/structs)
and [foreach pattern semantics](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/statements).
The arithmetic/list expectations above follow directly from fixture inputs and
the Fibonacci recurrence; neither the removed compiler's output nor host .NET
execution supplies an acceptance oracle.

## Assertion accounting

The baseline has five methods, ten Debug/Release executions, two shared TUnit
assertion sites (20 expanded completion/verdict assertions), and22 fixture value
comparisons:7 line,7 factory,2 recursive,4 generic and2 enumeration comparisons.
All22 are preserved. The native packets expose39 full-width scalar observations:
13 line,8 factory,8 recursive,4 generic and6 enumeration values. Four configurations
execute20 ROMs and compare624 ordered bytes, plus completion, fault, pointer bounds,
SP/frame guards, source provenance and untouched CGB bank2.

Additional checks mutate Reverse's by-value input, retain four recursive results
across later calls, check every reversed field, preserve the factory receiver's
negative Y and record actual enumeration order. Values are encoded as full32-bit
integers, including sign-extended short fields, so byte truncation cannot hide a
bad field load. The original source algorithms/inputs remain covered.

The old SCX marker and SCY verdict are harness mechanisms, replaced by bounded
native completion and independently checked result packets. Register I/O remains
covered by hardware tests. Successful frontend lowering and IR verification are
retained through Rust's pre/post-optimization verifier. Legacy sret/ArgScratch
comments describe private implementation choices, not required calling conventions.
No behavioral assertion is deleted or deferred, and no legacy source is physically
removed until final compiler-project retirement.

GenericSource returns nongeneric Box values from a generic method; it does not
test constructed generic struct layouts. ForeachSource tests Range4, despite its
header also mentioning a static struct-array enumerator. Those additional shapes
are not claimed complete from comments without executable assertions.

Initial four cases pass4/4 in30.692s, exit0
(`/tmp/koh-struct-return-migration.log`). Final formatted-fixture cases pass4/4 in
25.641s, exit0 (`/tmp/koh-struct-return-final.log`). The migration remains incomplete.
