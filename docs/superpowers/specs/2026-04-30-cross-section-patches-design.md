# Cross-SECTION Patches in Koh's Linker — Design

## Why

Cross-SECTION absolute references (`jp Boot` where Boot lives in a different SECTION than the call site) currently produce wrong bytes. The assembler resolves the symbol's value at assemble time using the section's PC base — `0` for floating sections, `fixedAddress` for pinned ones — and emits the bytes immediately, leaving no patch for the linker. Result: cross-section calls jump to the wrong address.

Discovered while implementing the GB 2048 sample (`samples/gb-2048/`, branch `feat/gb-2048-sample`): the canonical pattern of `SECTION "Reset", ROM0[$0100]` with `jp Boot` to a separate `SECTION "Boot", ROM0` produced `c3 00 00` (jump to `$0000`) instead of `c3 04 01` (jump to Boot's placed address `$0104`). Banking-based designs (separate banks per game subsystem with `farcall` cross-bank dispatch) are unimplementable without this fix.

A related bug: `.sym` output double-counts placed addresses for fixed sections. `SymbolResolver` does `PlacedAddress + Value`, but for fixed sections `Value` already includes `fixedAddress` (because `Binder` calls `pc.SetActive(name, fixedAddress, type)`). Result: a label at offset 0 in `WRAM0[$C000]` reports `$8000` (= `(C000 + C000) & FFFF`) instead of `$C000`. ROM bytes are correct (RomWriter uses raw section data); only `.sym` consumers see the mismatch.

Both bugs share a root cause: **`Symbol.Value` is stored as an absolute address-at-definition-time, not a section-relative offset**. The clean fix is to make `Symbol.Value` always be a section-relative offset and have the linker compute absolute addresses uniformly.

## Goals

1. `SECTION "A", ROM0[$0100]` containing `jp B` (with `B` in another SECTION) produces a ROM with `c3` followed by `B`'s actual placed address.
2. `.sym` output reports the placed address for every label, regardless of whether its SECTION is fixed or floating.
3. Existing intra-section relative jumps (`jr .local`) and intra-section absolute references continue to work.
4. The `.kobj` format gains the minimum metadata to support cross-section resolution.

## Non-goals

- Full RPN expression serialization in `.kobj`. Patches whose operand is a complex expression (e.g. `jp Boot + 2`) remain resolved at assemble time within their own section; the simple `jp Boot` / `call X` / `ld hl, X` shape — single-symbol references — is the case we fix.
- Cross-FILE references (multi-`.kobj` linking with cross-references). Out of scope; the existing comment in `Linker.cs` already calls this out as an `--format rgbds` path.
- Reworking the section placer.

## Approach (Path b — minimal data-model correction)

Per the compiler-architect's analysis (recorded in conversation):

1. **`Symbol.Value` becomes section-relative offset uniformly.** No more `fixedAddress + offset` for fixed sections; always offset from byte 0 of the section.
2. **`ExpressionEvaluator` defers cross-section symbol lookups.** When the symbol's `Section` differs from the section currently being assembled, return null → encoder records a `PatchEntry`.
3. **`PatchEntry` gains `SymbolName`** for the simple-identifier case (covers `jp Boot`, `call X`, `ld hl, X`, etc.).
4. **`PatchEntry.PCAfterInstruction` becomes section-relative offset** (stored as offset within the section, not absolute PC).
5. **`KobjWriter`/`KobjReader` serialize `SymbolName`.** Format version bumps; reader guards old files.
6. **Linker `ApplyPatches` rewrites bytes** using `LinkerSymbol.AbsoluteAddress` (which now equals `PlacedAddress + sectionRelativeValue`, correctly).
7. **`SymbolResolver.ResolveAddresses` is unchanged** — its existing math is correct once `Value` is relative.

Acceptance criterion: the minimal repro at the top of this design produces a ROM with `c3` followed by `B`'s placed-address bytes (little-endian), AND `.sym` shows every label at its placed address.

## File-touched-by-this-design list

- `src/Koh.Core/Binding/SectionPCTracker.cs` — `LabelPC` returns section offset, not absolute PC.
- `src/Koh.Core/Binding/Binder.cs` — `Pass1Section` no longer needs to bake fixedAddress into the PC tracker base for label-address purposes (still tracks absolute PC for `$` expressions).
- `src/Koh.Core/Binding/InstructionEncoder.cs` — when emitting absolute references with a known same-section base, compute absolute = `sectionBase + symValue`. When emitting relative branches, treat `PCAfterInstruction` as section-relative. When the operand expression is a bare identifier referring to a different section, record a `PatchEntry` with `SymbolName` set.
- `src/Koh.Core/Binding/ExpressionEvaluator.cs` — knows the current section name; returns null for cross-section identifier lookups.
- `src/Koh.Core/Binding/PatchEntry.cs` — adds `SymbolName` (nullable string) and clarifies `PCAfterInstruction` semantics.
- `src/Koh.Emit/KobjWriter.cs`, `src/Koh.Emit/KobjReader.cs` — write/read `SymbolName`; bump version; guard old.
- `src/Koh.Emit/KobjFormat.cs` — version constant.
- `src/Koh.Linker.Core/Linker.cs` — `ApplyPatches` rewrites bytes for unresolved patches by resolving `SymbolName`.
- `tests/Koh.Linker.Tests/AssemblerLinkerGoldenIntegrationTests.cs` — three new tests (cross-section absolute, fixed-section sym, WRAM-section sym).
- Existing tests that check `sym.Value` as an absolute address — adjust expectations to section-relative.

## Risks

- **PCAfterInstruction semantic flip.** If old `.kobj` files in the wild used absolute PCAfterInstruction, a reader version guard prevents misinterpretation. The repo doesn't ship pre-built `.kobj`s, so this is a build-time-only concern.
- **Test fallout from `Symbol.Value` semantic change.** Any binder/symbol test that asserts `sym.Value == 0x0100` for a label at the start of `ROM0[$0100]` becomes `sym.Value == 0`. Grep first; update mechanically.
- **Edge case: complex expression on cross-section symbol.** `jp Boot + 2` where Boot is in another section. The single-symbol-name patch path can't represent this. We log a deferred-patch error in that case (existing behavior); document in the design and revisit if it actually bites the 2048 sample.
