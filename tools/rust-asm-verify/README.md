# Native assembler verification

From the repository root, run:

```sh
python3 scripts/verify-rust-assembler.py
```

This builds the Rust assembler and the C# behavioral reference serially, then compares `.kobj`
structure and linked ROMs, RGB9 revision 13 structure and linked ROMs, the 2048
sample, and extracted positive raw-string `Emit` fixtures from the assembler
tests. Installed `rgbasm` and `rgblink` must both be version 1.0.1. The .NET SDK
must satisfy `global.json`; configure `PATH` and `DOTNET_ROOT` when needed.

Existing binaries can be checked without rebuilding:

```sh
python3 scripts/verify-rust-assembler.py --no-build \
  --rust target/debug/koh-asm --output /tmp/koh-assembler-results
```

`--skip-corpus` runs only the generated fixtures and sample. Artifact paths and
actual pass/failure counts are printed. `corpus-results.json` retains each
fixture's status and full failure diagnostics, including C# reference rejection.
Exact-source differences are classified by `expected-differences.json`: each
entry binds a fixture SHA-256, rationale, independent regression pointer, and
reviewed decoded native graph. The graphs are regression baselines; independent
semantic fixtures and the compatibility report justify the intended behavior.
Rich-format-only cases must assemble as RGBDS, preserve their declared graph,
and link to the expected ROM. Invalid sources also require RGBDS rejection.
Raw statuses remain unchanged; classified expected differences and unclassified
failures are counted separately. Changed sources or graph values fail review.
Raw `Emit` literals without a positive `model.Success` assertion are inventoried
as excluded. Ordinary C# string literals, parameterized sources, filesystem
fixtures, and tests that check only diagnostics are outside this extraction.

Nine negative fixtures check C#, native, and RGBDS failure with source locations;
RGBDS defers undefined symbols and JR range checks to linking. Their exact exit
codes and diagnostics are retained in `negative-results.json`. The independent
`fixtures/reference-semantics.asm` also checks FOR counters, empty data widths,
word strings, character maps, signed arithmetic, fill options, and LOAD sizes
against RGBDS 1.0.1. These checks have their own counts.

Numeric user symbols are compared. UTC/version builtins, `_NARG`, and source-only
macro/string definitions are excluded from symbol-table comparison. Relocation
bytes are masked for object comparison because either assembler may fold them
earlier; complete linked ROM bytes are compared separately. Source line maps are
checked for valid ranges and source locations, without requiring identical
coalescing. Corpus fixtures compare object structure and section bytes; they are
not all linked or executed.

Native RGBDS output must match the independently assembled RGBDS reference.
A differing or rejected C# RGBDS output is reported and its linker diagnostics
retained as a reference discrepancy. For the 2048 sample, a temporary source-tree
copy converts legacy `name: MACRO` declarations to `MACRO name` and adds `DEF`
to legacy `name EQU/EQUS value` declarations; the exact
changes are inventoried in `gb2048-reference-adaptations.json`. Tracked sample
files stay untouched. Sample `.kobj` output remains a strict C#/Rust comparison.

The execution fixture runs the real Koh emulator on both Koh-linked and
RGBDS-linked native ROMs. It requires a call/return and arithmetic sequence to
write `2A 2B` at WRAM `$C000`; absence after 10,000 instructions fails. This is
an execution regression, not a complete graphical or hardware qualification.

The helper also exposes `assemble <kobj|rgbds> <source> <output>`, `dump <kobj>`,
`link <rom> <kobj>...`, and `execute <rom>` for inspecting individual failures.
`execute` checks the generated fixture's signature, so arbitrary games require
their own verifier.
