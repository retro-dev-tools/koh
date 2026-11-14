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

**Migration completed: zero legacy methods retired or fully replaced.** The new
native assembly-reader tests are infrastructure coverage, not full substitutes for
the old DLL-to-ROM cases. All legacy tests remain. Proposed Rust filenames in the
audit are not implemented or discoverable merely because they are documented.

## Corrections required in acceptance

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

All findings so far come from source inspection. They are reasons for independent
tests and ABI choices, not claims of newly reproduced runtime failures or Rust
correctness.
