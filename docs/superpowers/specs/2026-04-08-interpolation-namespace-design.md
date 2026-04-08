# Interpolation and Namespace Redesign

**Date:** 2026-04-08
**Status:** In progress (sections 1-2 reviewed, remaining sections pending)

## Problem Statement

Two compiler compatibility bugs prevent Koh from building real RGBDS projects (e.g., Azure Dreams GBC):

**Bug 1: Symbol namespace collision.** Koh stores macros and EQU constants in the same case-insensitive symbol table. `DEF TEXT_LINEBREAK EQU $8e` and `MACRO text_linebreak` collide because they share a namespace. RGBDS keeps macros and constants in separate declaration spaces.

**Bug 2: Eager interpolation.** `{symbol}` interpolation in macro bodies is resolved all at once before any statement executes. In the `dict_entry` macro:

```asm
REDEF _DICT_TEXT EQUS REVCHAR(\1, \2, \3)   ; line 1: define symbol
db "{_DICT_TEXT}", TEXT_END                   ; line 2: use it
```

Koh resolves `{_DICT_TEXT}` before line 1 runs, so the symbol doesn't exist yet. RGBDS processes statements sequentially — the `REDEF` executes before the `db` line is lexed.

This also causes a silent data corruption bug: on the 2nd+ call, `{_DICT_TEXT}` resolves to the previous call's stale value.

### Cascade effect

Both bugs cascade into section overflow errors. Bug 1 prevents charmap macros from being defined. Bug 2 prevents dictionary entries from emitting charmap-compressed data. Raw ASCII fallback produces much larger output, overflowing ROM bank size limits.

## Design Principles

- Interpolation is a **lexical source-expansion mechanism** (like RGBDS's `peek()`-level text insertion), not a syntax tree feature
- Interpolation must resolve against the **current symbol state at the point the statement is being lexed**, not eagerly over a whole macro body
- The **binder remains the semantic coordinator** — the expander does not mutate symbol state or emit bytes
- Macros and constants occupy **separate declaration spaces** and do not collide

## RGBDS Interpolation Semantics (reference)

Based on RGBDS v0.9.1 specification:

- `{symbol}` pastes the symbol's contents as if they were part of the source file
- Works **everywhere**, not just inside strings — labels, instructions, directive arguments
- `{fmt:symbol}` supports format specifiers: sign, prefix, padding, width, fraction, precision, type (d/u/x/X/b/o/f/s)
- Nested interpolation: `{{sym}}` resolves inner first, result becomes outer symbol name
- Only identifiers, not expressions — `{1+2}` is invalid
- `\{` inside string literals suppresses interpolation
- Outside strings, `{` always triggers interpolation
- Recursion depth capped (65536 default in RGBDS)

---

## Section 1: Character Source Abstraction

### ICharSource

```
ICharSource
    Peek() -> SourceChar
    Read() -> SourceChar
    EnterStringMode()
    ExitStringMode()

SourceChar { int Value; SourceOrigin Origin; }  // Value = -1 for EOF
```

`EnterStringMode()` / `ExitStringMode()` is the explicit contract between lexer and source. The lexer calls `EnterStringMode()` when it encounters an opening `"` and `ExitStringMode()` at the closing `"`. This is narrow, explicit coupling — the minimum needed to handle `\{` correctly.

**Peek() contract:** `Peek()` is side-effect-free from the caller's perspective. Repeated `Peek()` calls return the same `SourceChar` until `Read()` advances. Interpolation recognition may require internal buffering, but that is invisible to the caller.

### SourceOrigin

```
SourceOrigin
    FilePath: string
    Offset: int
    ExpansionFrame: ExpansionFrame?

ExpansionFrame
    TriggerSpan: TextSpan (the {...} in source)
    ExpandedText: string (what it resolved to)
    Parent: ExpansionFrame? (for nested interpolation)
```

### Token span policy for expanded text

For tokens originating entirely from raw source: normal `TextSpan(start, length)` based on `SourceOrigin.Offset`.

For tokens whose first character comes from an expansion frame: primary location is the **interpolation trigger span** (the `{...}` in the original source), not a synthetic span over expanded text. Expansion provenance is available through `SourceOrigin.ExpansionFrame` for diagnostics that need to explain "this came from expanding `{_DICT_TEXT}`."

### StringCharSource

Wraps plain text. `EnterStringMode()` / `ExitStringMode()` are no-ops. No interpolation. Used for standalone parsing (tests, IDE features without semantic context).

### InterpolationAwareSource

Wraps an inner `ICharSource`. Owns:

**Expansion frame stack:** Each resolved interpolation pushes a frame containing expanded text. `Read()` consumes from the topmost frame first. When a frame is exhausted, it pops. Text from expansion frames IS subject to further interpolation (re-expansion), matching RGBDS.

**String mode flag:** Set by `EnterStringMode()` / `ExitStringMode()`. When in string mode and the source encounters `\{`, it emits the `\` and `{` as literal characters without triggering interpolation. When NOT in string mode, `{` always triggers interpolation.

**Interpolation parser:** A dedicated method, not inline scanning. On encountering `{`:

1. Read characters, tracking nesting depth. Nested `{` increments depth and triggers recursive interpolation of the inner content.
2. At the **top-level depth only**, `:` separates format spec from symbol name. Nested `:` characters are part of inner interpolation content.
3. At the **top-level depth only**, `}` closes the interpolation.
4. Pass parsed name and format to the resolver.
5. Depth limit (64) — on overflow, emit diagnostic and treat `{` as literal character.

**Resolver:** `IInterpolationResolver` (see below).

**Diagnostic sink:** Passed at construction. The source reports only interpolation-local failures: malformed brace syntax, depth overflow. The resolver reports semantic failures through the same sink: unknown symbol, invalid format.

### IInterpolationResolver

```
InterpolationResult Resolve(string name, InterpolationFormat? format)

InterpolationResult:
    Success(string expandedText)   -- insert this text into the source stream
    NotFound(string name)          -- symbol doesn't exist
    Error(string message)          -- format failure, wrong symbol kind, etc.
```

The resolver is responsible for:
- Looking up the symbol (EQUS, numeric, etc.)
- Applying format specifiers to numeric values
- Returning the final **text** to insert

The source only pushes `Success.expandedText` as a new expansion frame. It never formats values itself.

### InterpolationFormat

Parsed from `{fmt:name}` per RGBDS spec:

```
InterpolationFormat
    Sign: '+' | ' ' | none
    Exact: bool (#)
    Align: '-' | none
    Pad: '0' | none
    Width: int?
    FracDigits: int? (after '.')
    FixedPrec: int? (after 'q')
    Type: 'd' | 'u' | 'x' | 'X' | 'b' | 'o' | 'f' | 's'
```

### Failure behavior (exact rules)

**These rules must be verified against RGBDS before implementation.** The following are initial assumptions that require concrete RGBDS repros to confirm:

- `NotFound` -> source emits diagnostic via sink, expands to **empty string**, continues lexing. The original `{...}` text is consumed, not preserved.
- `Error` -> source emits diagnostic via sink, expands to **empty string**, continues lexing.
- Malformed syntax (no closing `}`) -> source emits diagnostic, treats the `{` as a literal character (does not consume further).
- Depth overflow -> source emits diagnostic, treats the `{` as a literal character.

### Lexer changes

The lexer replaces its internal `string _text` / `int _position` with `ICharSource`. Two points of coupling with the source:

1. **String scanning:** When the lexer enters string scanning, it calls `_source.EnterStringMode()` before scanning the body, and `_source.ExitStringMode()` after the closing `"`. This is the only place the lexer interacts with interpolation mechanics.

2. **Span construction:** Tokens use `TextSpan(startOffset, length)` from `SourceChar.Origin.Offset` for raw-source tokens. Expanded tokens use the interpolation trigger span as primary location.

---

## Section 2: Sequential Replay with Lazy Interpolation

### The core fix

Remove eager whole-body interpolation from `TextReplayService.ParseForReplay`. The line:

```csharp
text = _interpolation.Resolve(text);
```

is deleted entirely. Interpolation no longer happens at the text-replay level.

### Where interpolation moves to

The `Binder` already drives the pipeline: it calls the expander, iterates expanded nodes, and processes them sequentially. The binder's main loop processes replay units in declaration order and mutates symbol state after each one.

The change: before the expander returns nodes, the **lexing step** for each replay unit goes through `InterpolationAwareSource` instead of pre-resolved text. The resolver reads from the EQUS constants and symbol table that the binder has been updating.

### Revised flow

1. Macro is invoked. Expander substitutes `\1..\9`, `\#`, `_NARG` in the body text (purely textual, no semantic lookup).
2. Expander replays the macro body **incrementally as structural replay units** — not as one fully-interpolated blob.
3. For each replay unit:
   a. Wrap its text in `InterpolationAwareSource(text, resolver)` where `resolver` reads from current EQUS/symbol state
   b. Lex through the source (interpolations resolve now, against current state)
   c. Parse the tokens into syntax nodes
   d. Return the expanded node(s) to the binder
4. The binder processes the returned node(s) — defines symbols, emits bytes, updates charmaps.
5. Symbol state is now updated. The next replay unit's `InterpolationAwareSource` will see it.

### Ownership boundaries

- **Expander:** structural expansion (macro invocation, block handling, parameter substitution). Does NOT mutate symbol state or emit bytes.
- **InterpolationAwareSource:** resolves `{...}` during lexing, using a resolver that reads current state. Does NOT own the state.
- **Binder:** processes expanded nodes, defines symbols, emits bytes, updates charmaps. Owns semantic state mutation. Drives the overall loop.
- **Coordinator contract:** the binder iterates nodes from the expander. The expander yields them incrementally. Between yields, the binder processes and updates state. The next yield from the expander sees updated state through the shared resolver.

### What is a "replay unit"?

A structural unit as determined by the expander's existing block-tracking logic. These are not homogeneous:

- **Simple statement:** one source line (or continuation-joined lines) — executable immediately
- **Complete block:** `IF`...`ENDC`, `REPT`...`ENDR`, `FOR`...`ENDR` with their full nested bodies — may contain nested executable units
- **Macro definition:** `MACRO`...`ENDM` — stored, not executed until invoked

Replay-unit boundaries come from **pre-interpolation macro-body structure**, not from interpolated tokenization. This avoids circularity: you do not need to interpolate text to decide where a replay unit ends.

### Incremental yield model

Currently `Expand()` returns `List<ExpandedNode>` — all at once. This changes to an iterator pattern with lazy evaluation: the expander's `ExpandBodyList` `yield return`s each expanded node instead of appending to a list.

**Critical contracts:**

- The enumerator is **single-pass and stateful**. It must not be enumerated twice. Multiple enumeration would re-trigger expansion under different state.
- The resolver is consulted **at replay time** (during each `MoveNext()`), not at iterator construction time. The enumerator must not snapshot or cache interpolation-visible state when created.
- A dedicated replay enumerator type (rather than bare `IEnumerable<ExpandedNode>`) is recommended to prevent accidental `.ToList()` materialization changing semantics.

### Pass visibility

Interpolation during Pass 1 sees definitions established so far in Pass 1. EQUS constants and numeric symbols updated by preceding replay units are visible. Symbols whose values are not yet final (forward references) may need explicit handling — the pass visibility contract must be defined during implementation to match RGBDS behavior.

---

## Section 3: Symbol Namespace Separation

*To be designed in next session.*

Fix: macros and EQU constants must not collide. `TEXT_LINEBREAK` (EQU) and `text_linebreak` (MACRO) coexist because they are different symbol kinds resolved in different contexts.

## Section 4: Testing Strategy

*To be designed in next session.*

## Section 5: Migration Path

*To be designed in next session.*
