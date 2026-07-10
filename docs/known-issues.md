# Known Koh Issues

Bugs and limitations discovered during development that have not yet been fixed in the toolchain. Each entry has a minimal reproduction, current workaround if any, and notes for the eventual fix.

## 1. `\@` macro corruption — UNFIXED

**Severity:** Medium. Workaround exists.

**Symptom:** When a macro uses `\@` (the unique-suffix idiom for generating per-invocation labels), any macro defined AFTER it in the same file becomes unusable — invoking the second macro produces parser errors that look like mid-token mangling.

**Reproduction:**

```asm
; macros.inc
WAIT_VBLANK: MACRO
.\@_wait\@:
    ldh a, [$FF44]
    cp 144
    jr c, .\@_wait\@
ENDM

farcall: MACRO
    ld a, \1
    call \2
ENDM
```

```asm
; main.asm
INCLUDE "macros.inc"

SECTION "B", WRAM0[$C000]
wBank: ds 1

SECTION "T", ROMX, BANK[1]
Target:
    ret

SECTION "M", ROM0[$0100]
Start:
    farcall 1, Target
    ret
```

`koh-asm main.asm` fails with errors like:

```
main.asm:1:1: error: Unexpected identifier 'rcall'
main.asm:N:N: error: No valid encoding for 'ld' with given operands
```

The first error mentions `rcall` — the parser has consumed the `fa` prefix of `farcall` somewhere and then sees `rcall` as the unexpected token. Removing `WAIT_VBLANK` from `macros.inc` makes `farcall` work cleanly.

The bug also reproduces with `MACRO WAIT_VBLANK ... ENDM` (modern syntax) and persists with or without `INCLUDE`.

**Workaround in `samples/gb-2048/`:** drop `WAIT_VBLANK` from `macros.inc`; rely on `WaitForVBlankFlag` (a regular subroutine that polls the VBlank-IRQ-set flag).

**Likely root cause:** state leaked from `\@` token consumption affects the lexer/macro pre-processor when it encounters the next `MACRO` definition. Worth tracing the state machine that handles `\@` and verifying it cleanly resets at `ENDM`.

**Files likely involved:** `src/Koh.Core/Binding/Binder.cs` macro-expansion path; `src/Koh.Core/Syntax/AssemblyExpander.cs`; whatever module handles `\@` substitution.

## 2. LSP rgbds-compat parser mis-tokenizes labels — UNFIXED

**Severity:** Low. Cosmetic only; build is unaffected.

**Symptom:** The LSP emits spurious "Undefined symbol" diagnostics whose offending token is a mid-string fragment that doesn't appear as a real token in the source. Examples observed during `samples/gb-2048/` development:

- `Line 8:8] Undefined symbol: ryPo` — the file contained `EntryPoint:`; "ryPo" is chars 4–7 of that label.
- `Line 14:6] Undefined symbol: .hal` — file contained `.halt:`.
- `Line 16:8] Undefined symbol: lt` — file contained `    jr .halt`.
- `Line 8:8] Undefined symbol: set"` — file contained `SECTION "Reset", ROM0[$0100]`; `set"` is a fragment of the string literal `"Reset"`.
- `Line 77:8] Undefined symbol: Bootr VBl` — no such substring exists anywhere in the file. Persistent across many edits to unrelated parts of the file.

Diagnostic source is `(rgbds)`, which is the rgbds-compat parsing layer used by the LSP for compatibility diagnostics.

`koh-asm` itself parses and assembles cleanly. Only the LSP's rgbds-compat layer is affected.

**Workaround:** ignore the LSP "Undefined symbol" warnings on these files; rely on `koh-asm`'s exit code as ground truth.

**Likely root cause:** the rgbds-compat tokenizer in the LSP is computing column indices or token spans incorrectly, possibly by misindexing into multi-byte UTF-8 (the sample contains em-dashes `—`) or by failing to advance over string literal contents. The `Bootr VBl` ghost suggests stale parse state from a previous file version is being merged with current content.

**Files likely involved:** `src/Koh.Lsp/` — the rgbds-compat diagnostic provider.

## 3. (Resolved) Cross-SECTION patch resolution

**Status:** FIXED in commits `5225a2d`..`d140bf1` on the linker-fix branch (merged into `feat/gb-2048-sample`; not yet merged to master).

Cross-section absolute references encoded as `c3 00 00` instead of the target's actual placed address. Root cause: `Symbol.Value` stored absolute address-at-definition-time, mixing section base with offset; the linker double-counted by adding `PlacedAddress`. Fixed by making `Symbol.Value` always section-relative and routing cross-section refs through the linker's `ApplyPatches`.

See `docs/superpowers/specs/2026-04-30-cross-section-patches-design.md` for the full design.
