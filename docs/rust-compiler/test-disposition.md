# Compiler test disposition status

The source audit accounts for every one of the baseline's **565 test methods**:

| Owned source groups | Methods | Source-level disposition |
| --- | ---: | --- |
| Frontends excluding art, plus SoftFloatDifferentialTests | 230 | [Frontend audit](frontend-audit.md) |
| Backends, Ir, Sm83 | 290 | [Backend/ABI audit](backend-audit.md) |
| Samples and TileSheetConverterTests | 45 | [Integration audit](integration-audit.md) |
| Total | 565 | No missing test declarations in the source-to-document cross-check |

These documents name every method and its proposed PORT/REPLACE decision, its
behavioral requirement, independent justification or missing oracle, and target
Rust grouping. No compiler-owned tests are deferred. No feature removal has been
approved; obsolete implementation assertions can disappear only after stronger
native acceptance replaces them. The integration audit separately identifies
active noncompiler debugger/emulator/tools ownership and removal dependencies.

The mechanical baseline also contains 390 Arguments attributes and 1,524
Assert.That call sites. Parameterization and loops mean neither method counts nor
call-site counts equal the number of behavioral scenarios. Counting independently
justified native scenarios remains part of each port, not a number invented from
attribute totals. See the [per-file inventory](test-inventory.md).

The legacy compiler project was then run with .NET SDK 10.0.115 on Linux:
**814 succeeded, 0 failed, 0 skipped**, exit 0. This is a parameter-expanded
execution count, not 814 independently justified language rules. Command:
`dotnet test --project tests/Compiler/Koh.Compiler.Tests/Koh.Compiler.Tests.csproj -c Release -- --maximum-parallel-tests 1`.
Completed log: `/tmp/koh-compiler-legacy-baseline.log`; HTML artifact:
`tests/Compiler/Koh.Compiler.Tests/bin/Release/net10.0/TestResults/Koh.Compiler.Tests-linux-net10.0-report.html`.
The baseline establishes that the existing fixtures pass on this checkout;
it does not make their expectations correctness oracles.

**Migration completed: zero legacy methods retired or fully replaced.** The new
native reader/IR tests and initial integer ROM cases are partial new coverage, not
full substitutes for the old DLL-to-ROM suite. All legacy tests remain. Proposed Rust filenames in the
audit are not implemented or discoverable merely because they are documented.

## Corrections required in acceptance

Primitive mutable-array coverage is now partly implemented in
[arrays.md](arrays.md): four Rust driver cases execute 40 real Roslyn ROM
scenarios and 16 explicit rejection probes. Twelve element-type verifier cases,
five malformed IR operations, two null-merge orderings and six native/int16
opcode mismatch cases are separately discoverable by nextest. These cover parts
of the array-length/memory audit groups; RVA/aggregate/framework/sample behavior
remains open. No complete legacy method has been retired by this increment.

- Require an independent expected result as well as Debug/Release agreement.
- Require bounded program completion before inspecting output memory.
- Test mutable array identity and writes; compiler-folded ROM bytes do not prove
  ordinary mutable array semantics.
- Guard frame/stack/heap capacity before writing, including recursive references
  to still-live ancestor locals.
- Test interrupted arithmetic and bank changes against documented preservation
  rules, including narrow helpers using shared scratch.
- Validate known method/file/line PDB mappings through actual debugger stops.
- Keep curated sample assets unchanged; distinguish generated PNG existence from
  pixel/scene acceptance and distinguish emulator runs from physical hardware.

The audit findings above come from source inspection. Native execution evidence
is recorded separately in [the SM83 ABI/slice](sm83-abi.md) and
[progress](progress.md); it does not reproduce all identified legacy risks or
establish completion of the final supported subset.
