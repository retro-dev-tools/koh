# RGBDS Interpolation Behavior Matrix

Verified against: **RGBDS v1.0.1** on 2026-04-08

## Failure Semantics

| Test | RGBDS Behavior | Exit Code |
|------|----------------|-----------|
| `{undefined_symbol}` in `db` string | Fatal error: "Interpolated symbol `undefined_symbol` does not exist". Assembly aborted. | 1 |
| `{undefined_symbol}` outside string (as label) | Fatal error: "Interpolated symbol `undefined_symbol` does not exist". Assembly aborted. | 1 |
| `{d:undefined}` with format specifier | Fatal error: "Interpolated symbol `undefined` does not exist". Assembly aborted. | 1 |
| Unclosed `{x` (no closing `}`) | Prints the resolved value (`$2A`) then reports fatal error: "Missing '}'". Assembly aborted. | 1 |

**Key finding:** Undefined symbol in interpolation is a **fatal error** that aborts assembly. It does NOT expand to empty string. This contradicts the spec's initial assumption.

## Interpolation Timing

| Test | RGBDS Behavior | Exit Code |
|------|----------------|-----------|
| `REDEF _X EQUS "hello"` then `PRINTLN "{s:_X}"` in macro, called twice | Prints `hello` both times. REDEF within macro body is visible to subsequent lines in the same invocation. | 0 |
| `DEF x EQU 42` then `PRINTLN "{d:x}"` | Prints `42`. Numeric symbols are interpolation-visible. | 0 |
| `DEF x EQU 1` / `REDEF x EQU 2` / `PRINTLN "{d:x}"` | Prints `2`. Sees latest REDEF value. | 0 |

**Key finding:** Both EQUS and numeric symbols are immediately visible to interpolation after definition/redefinition. Statement-sequential visibility is confirmed.

## Namespace Coexistence

| Test | RGBDS Behavior | Exit Code |
|------|----------------|-----------|
| `DEF TEXT_LINEBREAK EQU $8e` + `MACRO text_linebreak` + invoke macro + use constant | No error. Macro invocation works (`db $8e`). `PRINTLN "{d:TEXT_LINEBREAK}"` prints `142` ($8e = 142). | 0 |

**Key finding:** Macro and EQU constant with the same name (case-insensitive) coexist without conflict. Lookup is context-driven.

## Nested Interpolation

| Test | RGBDS Behavior | Exit Code |
|------|----------------|-----------|
| `DEF meaning EQUS "answer"` / `DEF answer EQU 42` / `PRINTLN "{d:{meaning}}"` | Prints `42`. Inner `{meaning}` resolves to `"answer"`, then outer `{d:answer}` resolves to `42`. | 0 |

**Key finding:** Nested interpolation works. Inner resolves first, result becomes outer symbol name.

## Implications for Koh Spec

1. **Section 1 failure behavior must be updated:** `NotFound` is a **fatal error**, not empty-string expansion. Assembly aborts on undefined interpolation.
2. **Unclosed brace** is also a fatal error, but the symbol value may have already been partially processed (RGBDS printed the value before reporting the error).
3. **Numeric interpolation is fully visible** — no special pass restrictions needed for Phase 1 EQUS-only scope.
4. **Namespace coexistence is confirmed** — Section 3 design is validated.
5. **Nested interpolation is confirmed** — Section 1 recursive expansion design is validated.
