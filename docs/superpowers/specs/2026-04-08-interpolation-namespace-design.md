# Interpolation and Namespace Redesign

**Date:** 2026-04-08
**Status:** Approved

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
    EnterDoubleQuotedStringMode()
    ExitDoubleQuotedStringMode()

SourceChar { int Value; SourceOrigin Origin; }  // Value = -1 for EOF
```

`EnterDoubleQuotedStringMode()` / `ExitDoubleQuotedStringMode()` is the explicit contract between lexer and source. The lexer calls `EnterDoubleQuotedStringMode()` when it encounters an opening `"` and `ExitDoubleQuotedStringMode()` at the closing `"`. This is narrow, explicit coupling — the minimum needed to handle `\{` correctly. Named for the specific string form it governs; if other string forms appear later with different escape rules, they get their own mode.

**Peek() contract:** `Peek()` is side-effect-free from the caller's perspective. Repeated `Peek()` calls return the same `SourceChar` until `Read()` advances. Interpolation recognition may require internal buffering, but that is invisible to the caller. This contract must be enforced — violation causes lexer bugs with lookahead.

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

For tokens whose first character comes from an expansion frame: primary location is the **interpolation trigger span** (the `{...}` in the original source), not a synthetic span over expanded text.

This is an intentional tradeoff:

- Raw-source fidelity wins over fake spans
- If expanded text yields multiple tokens, they all point to the same trigger span — this is expected and acceptable
- Expanded-token diagnostics may collapse onto the trigger span
- Provenance chain (`ExpansionFrame`) is used for richer explanations when needed (e.g., "this came from expanding `{_DICT_TEXT}` which resolved to `ty to`")

### StringCharSource

Wraps plain text. `EnterDoubleQuotedStringMode()` / `ExitDoubleQuotedStringMode()` are no-ops. No interpolation. Used for standalone parsing (tests, IDE features without semantic context).

### InterpolationAwareSource

Wraps an inner `ICharSource`. Owns:

**Expansion frame stack:** Each resolved interpolation pushes a frame containing expanded text. `Read()` consumes from the topmost frame first. When a frame is exhausted, it pops. Text from expansion frames IS subject to further interpolation (re-expansion), matching RGBDS. This is a stack, not a single buffer — nested interpolation and re-expansion both push additional frames.

**String mode flag:** Set by `EnterDoubleQuotedStringMode()` / `ExitDoubleQuotedStringMode()`. When in string mode and the source encounters `\{`, it emits the `\` and `{` as literal characters without triggering interpolation. When NOT in string mode, `{` always triggers interpolation.

**Interpolation parser:** A dedicated method, not inline scanning. On encountering `{`:

1. Read characters, tracking nesting depth. Nested `{` increments depth and triggers recursive interpolation of the inner content.
2. At the **top-level depth only**, `:` separates format spec from symbol name. Nested `:` characters are part of inner interpolation content.
3. At the **top-level depth only**, `}` closes the interpolation.
4. Pass parsed name and format to the resolver.
5. Depth limit (64) — on overflow, emit diagnostic and treat `{` as literal character.

**Resolver:** `IInterpolationResolver` (see below).

**Diagnostic sink:** Passed at construction. Error ownership is split cleanly:

- **Source reports:** malformed interpolation syntax, brace-matching failures, depth overflow
- **Resolver reports:** unknown symbol, invalid symbol kind for interpolation, format semantic failures

Both use the same diagnostic sink, but responsibility is distinct.

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

### Failure behavior

**BLOCKER: These rules must be verified against RGBDS before implementation.** Implementation must not proceed on failure semantics until confirmed with concrete RGBDS repros. The following are initial assumptions:

- `NotFound` -> source emits diagnostic via sink, expands to **empty string**, continues lexing. The original `{...}` text is consumed, not preserved. **This assumption directly affects tokenization and parsing shape — if wrong, it creates compatibility bugs.**
- `Error` -> source emits diagnostic via sink, expands to **empty string**, continues lexing.
- Malformed syntax (no closing `}`) -> source emits diagnostic, treats the `{` as a literal character (does not consume further).
- Depth overflow -> source emits diagnostic, treats the `{` as a literal character.

### Lexer changes

The lexer replaces its internal `string _text` / `int _position` with `ICharSource`. Two points of coupling with the source:

1. **String scanning:** When the lexer enters string scanning, it calls `_source.EnterDoubleQuotedStringMode()` before scanning the body, and `_source.ExitDoubleQuotedStringMode()` after the closing `"`. This is the only place the lexer interacts with interpolation mechanics.

2. **Span construction:** Tokens use `TextSpan(startOffset, length)` from `SourceChar.Origin.Offset` for raw-source tokens. Expanded tokens use the interpolation trigger span as primary location.

---

## Section 2: Sequential Replay with Lazy Interpolation

### Scope and impact

**This is an execution-model redesign, not a local fix.** Changing `Expand()` from eager list production to stateful sequential replay changes evaluation semantics of the entire expansion pipeline. Anything that currently assumes expansion is pure, repeatable, or detached from binder state will need to be revisited. This is required for RGBDS-compatible interpolation timing, but it is not a small change.

### The core fix

Remove eager whole-body interpolation from `TextReplayService.ParseForReplay`. The line:

```csharp
text = _interpolation.Resolve(text);
```

is deleted entirely. Interpolation no longer happens at the text-replay level.

### Where interpolation moves to

The `Binder` already drives the pipeline: it calls the expander, iterates expanded nodes, and processes them sequentially. The binder's main loop processes replay units in declaration order and mutates symbol state after each one.

The change: before the expander returns nodes, the **lexing step** for each replay unit goes through `InterpolationAwareSource` instead of pre-resolved text. The resolver reads from the **semantic namespaces relevant to interpolation** (EQUS constants, numeric symbols) that the binder has been updating. Interpolation resolves only against these namespaces — macro names are not interpolation values unless RGBDS explicitly defines them as such.

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

### Macro body storage

Macro bodies must preserve structural replay-unit boundaries before interpolation. This means macro bodies are stored as either:

- Per-line text records that can be replayed incrementally, OR
- A structured body representation that preserves statement/block boundaries

The current implementation stores macro bodies as raw text. **This is a design gate.** If raw macro text cannot reliably preserve replay-unit boundaries without interpolation-sensitive reparsing, macro body storage must be upgraded to structural records before the lazy replay redesign proceeds. Do not attempt to make raw text work through accumulating special cases in the expander.

### Incremental yield model

Currently `Expand()` returns `List<ExpandedNode>` — all at once. The eager accumulation model must be replaced with a **dedicated stateful replay enumerator** (not bare `IEnumerable<ExpandedNode>`). Existing `ExpandBodyList` logic may be adapted if it can preserve structural replay-unit boundaries and replay-time state visibility; otherwise it must be refactored.

**Critical contracts:**

- The enumerator is **single-pass and stateful**. It must not be enumerated twice. Multiple enumeration would re-trigger expansion under different state.
- The resolver is consulted **at replay time** (during each `MoveNext()`), not at iterator construction time. The enumerator must not snapshot or cache interpolation-visible state when created.
- The dedicated enumerator type prevents accidental `.ToList()` materialization from changing semantics. Callers must treat replay as single-consumption and state-dependent.
- Any debugging, tracing, or test helper that materializes the replay stream must preserve single-pass semantics and must not enumerate replay twice.

### Semantic visibility contract

Interpolation visibility follows strict declaration order:

- **EQUS redefinitions** become visible to interpolation immediately after the defining replay unit is processed by the binder. The next replay unit's `InterpolationAwareSource` will see the updated value.
- **Interpolation never reads future replay units.** It sees only symbol state established by replay units already processed.
- **Numeric symbol visibility** depends on pass-specific resolution rules. During Pass 1, interpolation sees numeric values defined so far in Pass 1. Whether partially-resolved or forward-referenced numeric symbols are interpolation-visible must be specified explicitly before implementation, verified against RGBDS behavior.
- **Failed/intermediate definitions** from partially processed blocks are visible only if the binder has committed them to the relevant semantic namespace before yielding to the next replay unit.
- **BLOCKER: Numeric interpolation visibility rules must be verified against RGBDS before implementation proceeds beyond EQUS-only support.** Whether partially-resolved or forward-referenced numeric symbols are interpolation-visible is a correctness-critical question that cannot be deferred.

---

## Section 3: Symbol Namespace Separation

### Problem

Koh uses a single case-insensitive `SymbolTable` for all symbol kinds. When `DefineMacro("text_linebreak")` is called and `TEXT_LINEBREAK` already exists as an EQU constant, the table reports "Symbol already defined." RGBDS allows these to coexist because they are different kinds of symbols resolved in different contexts.

### Current compiler symbol model dual role

The current `SymbolTable` class serves two distinct roles that must be distinguished:

1. **Semantic lookup namespaces** — used during assembly to resolve symbols in expressions, macro invocations, charmap directives, etc.
2. **Tooling symbol model / symbol index** — used by LSP features (rename, references, hover, outline) to find and navigate all named entities across the project.

If the current `SymbolTable` serves both roles, it must be redesigned into namespace-aware storage or split into separate components. The two roles have different requirements: semantic lookup must be context-driven and namespace-scoped; tooling must see all symbol kinds for navigation.

### Declaration spaces

The redesign explicitly separates macro, constant/label, and charmap declaration spaces because they are resolved in different contexts:

| Context | Example | Namespace |
|---------|---------|-----------|
| Identifier in macro-invocation position at statement start | `text_linebreak` | Macro |
| Identifier in expression | `TEXT_LINEBREAK` | Constant/label |
| SETCHARMAP / NEWCHARMAP argument | `azure_dreams` | Charmap |

A macro named `text_linebreak` and a constant named `TEXT_LINEBREAK` never conflict because the lookup context determines which namespace to search.

### Design

Macros, constants/labels, and charmap names occupy separate declaration spaces. Lookup is context-driven.

- **Macro invocation** resolves only against the macro namespace.
- **Expression evaluation** resolves only against the constant/label namespace.
- **Charmap directives** resolve only against the charmap namespace.

These namespaces may be implemented either as separate registries or as one namespace-aware symbol store, but they must not behave as one flat declaration space.

Macro metadata required by LSP features such as rename, references, and navigation must be preserved in the macro namespace or in a higher-level tooling symbol index. Tooling requirements do not justify keeping macros in the expression symbol namespace.

**Charmap names** already live in `CharMapManager`'s internal dictionary, separate from the constant/label namespace. No namespace-model change is currently expected there, but diagnostics and lookup consumers must still be audited for assumptions about symbol kinds coming from one place.

**Context-driven lookup must be enforced both at definition time and at use-site resolution time.** It is possible to fix duplicate checks but still leave some use-site lookup path consulting the wrong namespace.

**Diagnostics:** A macro and a constant with the same name do not conflict because they belong to different declaration spaces. Duplicate diagnostics apply only within the namespace relevant to the declaration being introduced.

**Non-goal:** Do not solve this by keeping macros in the flat symbol table and merely suppressing collisions with constants. That preserves the wrong ownership model and risks future context-leak bugs.

### Allowed implementation shapes

Either of these is acceptable:

- **Separate registries:** `MacroTable` + `SymbolTable` + `CharMapManager`, each owning their namespace. LSP features query each registry as needed.
- **One namespace-aware symbol store:** A single `SymbolRegistry` containing multiple isolated namespace maps with context-driven lookup APIs. Internal storage is separated; external API selects the namespace.

**Not acceptable:** A single flat case-insensitive symbol map with ad hoc collision exceptions.

### Changes required

1. Remove macro definitions from **duplicate-definition checks for the constant/label namespace** — macro definitions must not trigger "already defined" against constants/labels
2. Ensure macro definition and lookup uses the **macro namespace** only
3. Ensure expression evaluation uses the **constant/label namespace** only
4. Audit all duplicate-definition diagnostics so they operate **within namespace** — a macro redefining another macro is an error; a macro sharing a name with a constant is not
5. Preserve macro symbol metadata needed by LSP rename/references, either in:
   - A dedicated macro registry, or
   - A broader namespace-aware symbol model
6. Audit all symbol lookup consumers and classify them by namespace:
   - Macro invocation (expander)
   - Expression evaluation (binder/evaluator)
   - Charmap lookup (CharMapManager)
   - Tooling/rename/reference indexing (LSP SymbolFinder)

### LSP considerations

- LSP rename/reference features must not depend on the expression-symbol namespace being flat.
- Macro declarations and references must come from the macro namespace or from a higher-level symbol index built over all namespaces.
- Section 3 must preserve macro identity and source spans for tooling — removing macros from the semantic lookup path does not mean removing them from the compiler's knowledge.
- The macro namespace or tooling symbol index must preserve declaration identity and source spans sufficient for rename, references, hover, and navigation. This includes macro declaration spans and call-site reference spans.

### What does NOT change

- EQU/EQUS/label symbol resolution stays in the constant/label namespace
- Charmap name resolution stays in `CharMapManager`
- Case-insensitive comparison for macro names is preserved (matching RGBDS)
- Interpolation resolver does not consult the macro namespace (see Section 2)

---

## Section 4: Testing Strategy

### Approach

The three highest-risk unknowns in this design are all testable before full implementation:

1. **RGBDS failure semantics** — what happens when `{unknown}` is interpolated?
2. **Numeric interpolation visibility** — can you interpolate a numeric symbol defined earlier in the same macro?
3. **Replay-unit boundary preservation** — can macro bodies be split into replay units from raw text?

Write verification tests first, grouped by risk level. Tier 1 tests are blockers — they must be resolved before implementation proceeds.

### Tier 1 — RGBDS behavior verification (blockers)

These must be verified against RGBDS output before implementation proceeds. Each test should be runnable against both RGBDS and Koh to confirm identical behavior.

**Failure semantics:**
- `{undefined_symbol}` in a `db` string — does RGBDS emit empty, error, or keep literal text?
- `{undefined_symbol}` outside a string (e.g., as a label name) — same question
- `{undefined_symbol}` with a format specifier (`{d:undefined}`) — same question
- Malformed interpolation: unclosed `{sym` at end of line — how does RGBDS handle it?

**Interpolation timing:**
- `REDEF x EQUS "hello"` then `db "{x}"` in a macro — does the second invocation see the updated value?
- `DEF x EQU 42` then `PRINTLN "{d:x}"` — numeric interpolation visibility
- `DEF x EQU 1` / `REDEF x EQU 2` / `PRINTLN "{d:x}"` in sequence — sees 2, not 1

**Namespace coexistence:**
- `DEF TEXT_LINEBREAK EQU $8e` + `MACRO text_linebreak` — do they coexist without error?
- Invoking `text_linebreak` macro while `TEXT_LINEBREAK` constant exists — no conflict
- Using `TEXT_LINEBREAK` in an expression while `text_linebreak` macro exists — no conflict

**Nested interpolation:**
- `DEF meaning EQUS "answer"` / `DEF answer EQU 42` / `PRINTLN "{{meaning}}"` — verify inner resolves first, result becomes outer name

### Tier 2 — Namespace separation (Section 3)

These can be implemented immediately against Koh. They test that declaration spaces are properly separated.

- Macro and EQU constant with same name (case-insensitive) — no collision diagnostic
- Macro lookup at statement start does not find constants
- Expression evaluation does not find macros
- Charmap lookup does not find macros or constants
- Duplicate macro definition — diagnostic within macro namespace
- Duplicate constant definition — diagnostic within constant/label namespace
- LSP rename on a macro does not affect same-named constant
- LSP rename on a constant does not affect same-named macro
- LSP find-references on a macro returns only macro call sites, not constant references
- LSP hover on macro-invocation-position identifier shows macro info, not constant info

### Tier 3 — Lazy interpolation (Section 2)

These test the core bug fix: interpolation must resolve against symbol state at the point the statement is being processed, not eagerly.

- `dict_entry` pattern: `REDEF _X EQUS REVCHAR(...)` then `db "{_X}"` — second line must see first line's value
- Multiple `dict_entry` calls in sequence — each must see its own REDEF, not the previous call's stale value
- `{sym}` outside strings in macro body — resolved against state at that point in declaration order
- `IF DEF(sym)` / `{sym}` ordering — define `DEF x EQUS "val"`, then `IF DEF(x)` followed by `db "{x}"` — conditional and interpolation must both see the prior definition
- Nested macro calls with interpolation — outer macro calls inner macro which does `REDEF _X EQUS "inner"`, then outer macro's next line uses `{_X}` — must see inner's definition
- Replay enumerator cannot be enumerated twice — second enumeration must fail or be explicitly prevented
- Materializing replay stream for diagnostics/tracing preserves single-pass semantics

### Tier 4 — ICharSource / InterpolationAwareSource (Section 1)

Unit tests for the new abstraction, independent of the rest of the compiler.

- `Peek()` idempotency — repeated calls return same `SourceChar` until `Read()` advances
- `Read()` advances position; next `Peek()` returns next character
- `{sym}` expansion with mock resolver returning `Success("hello")` — stream yields `h`, `e`, `l`, `l`, `o`
- Nested `{{sym}}` expansion — inner resolves first, result becomes outer lookup name
- `\{` inside double-quoted string mode — no expansion, emits literal characters
- `{` outside string mode — always triggers interpolation
- Depth limit enforcement — exceeding limit emits diagnostic and treats `{` as literal
- Provenance tracking — `SourceOrigin.ExpansionFrame` correctly populated through expansion
- Format specifier parsing — `{#05x:sym}` correctly parsed into `InterpolationFormat` fields
- `NotFound` result — behavior matches verified RGBDS semantics from Phase 1; diagnostic behavior asserted accordingly
- Re-expansion — expanded text containing `{sym2}` triggers further interpolation
- EOF handling — `SourceChar.Value == -1`, no expansion triggered

---

## Section 5: Implementation Order

### Sequencing rationale

The sections have dependencies. Section 3 is the simplest and most self-contained. Sections 1 and 2 depend on verified RGBDS behavior. Section 2 is the highest-risk change. Implementation should proceed from lowest risk to highest, verifying assumptions at each gate.

### Phase 1: Verify RGBDS behavior (Tier 1 tests)

Before writing any production code, resolve the two blockers:

1. **Failure semantics:** Build and run the Tier 1 failure-semantics tests against RGBDS. Record exact behavior for `{undefined}`, malformed syntax, etc. Update Section 1 failure behavior rules with verified results.
2. **Numeric interpolation visibility:** Build and run the Tier 1 timing tests against RGBDS. Record whether numeric symbols are visible to interpolation during the same pass, and whether forward references are rejected or deferred. Update Section 2 semantic visibility contract with verified results.

**Deliverables:**
- A checked-in RGBDS behavior matrix (markdown file) documenting exact results for each Tier 1 test case
- Minimal repro `.asm` source files for each test, checked into the test fixtures
- Captured expected stdout/stderr/exit code for each case

**Gate:** Do not proceed to Phase 2 until both blockers are resolved, deliverables are checked in, and the spec is updated.

### Phase 2: Namespace separation (Section 3)

Implement namespace separation. This is the simplest fix and directly resolves Bug 1 (the `text_linebreak` / `TEXT_LINEBREAK` collision) without touching interpolation. Note: Bug 2 (eager interpolation, the larger behavioral break) remains after this phase. Azure Dreams will not build correctly until Phase 5 is complete.

1. Separate macro definitions from the constant/label duplicate-definition path
2. Ensure context-driven lookup at both definition and use-site resolution
3. Audit all symbol lookup consumers and classify by namespace
4. Preserve macro symbol metadata for LSP features
5. Run Tier 2 tests

**Gate:** All Tier 2 tests pass. Existing test suite still passes. LSP rename/references still work for macros.

### Phase 3: ICharSource abstraction (Section 1)

Build the character source layer. This can be developed and tested in isolation from the rest of the compiler.

1. Implement `ICharSource`, `SourceChar`, `SourceOrigin`
2. Implement `StringCharSource` (trivial wrapper, replaces current lexer internals)
3. Implement `InterpolationAwareSource` with expansion frame stack, interpolation parser, format specifier parsing
4. Implement `IInterpolationResolver` interface
5. Run Tier 4 tests against mock resolvers
6. Adapt `Lexer` to consume `ICharSource` instead of raw string — verify all existing lexer tests still pass with `StringCharSource`

**Gate:** All Tier 4 tests pass. All existing lexer tests pass with `StringCharSource`. `InterpolationAwareSource` is tested in isolation but not yet wired into the compiler.

### Phase 4: Macro body storage assessment

Before proceeding to the replay redesign, verify whether raw macro text can support replay-unit splitting.

1. Examine current macro body storage in `AssemblyExpander`
2. Attempt to split representative macro bodies into replay units from raw text, including:
   - `dict_entry` (REDEF + db with interpolation — the core bug case)
   - Macros with nested `IF`/`ENDC` blocks
   - Macros with continuation lines (`\` at EOL)
   - Macros containing nested `MACRO`/`ENDM` definitions (if supported)
   - Macros where interpolation appears in replay-unit text but not in boundary markers
3. If raw text is sufficient for all cases: document the splitting rules and proceed
4. If raw text is insufficient: design and implement structural macro body records before proceeding

**Gate:** Replay-unit boundaries can be determined from stored macro bodies without interpolation for all representative cases above. This must be demonstrated, not assumed. One happy-path macro is not sufficient to pass this gate.

### Phase 5: Lazy replay redesign (Section 2)

This is the highest-risk phase. It changes the execution model of the expansion pipeline.

1. Replace eager expansion materialization with a dedicated single-pass replay mechanism
2. Wire `InterpolationAwareSource` into the replay path — each replay unit is lexed through an interpolation-aware source using a resolver backed by the binder's current semantic state
3. Remove the eager `_interpolation.Resolve(text)` call from `TextReplayService.ParseForReplay`
4. Verify the binder's processing loop correctly updates state between replay units
5. Run Tier 3 tests
6. Run the full existing test suite

**Gate:** All Tier 3 tests pass. Full existing test suite passes. The Azure Dreams project compiles without the section overflow errors caused by Bug 2.

### Phase 6: Integration validation

1. Build Azure Dreams with Koh and compare output against RGBDS
2. Verify LSP features (diagnostics, hover, go-to-definition, rename, references) work correctly on the Azure Dreams project
3. Verify macro and constant with the same case-insensitive name can coexist in one workspace without rename/reference cross-contamination in LSP
4. Run RGBDS compatibility test suite

---

## Appendix: Risk summary

| Risk | Severity | Mitigation |
|------|----------|------------|
| RGBDS failure semantics unknown | High | Phase 1 blocker — verify before implementation |
| Numeric interpolation visibility unknown | High | Phase 1 blocker — verify before implementation |
| Raw macro text insufficient for replay-unit splitting | Medium | Phase 4 gate — assess before replay redesign |
| Lazy replay breaks assumptions in existing code | High | Dedicated enumerator type, comprehensive Tier 3 tests |
| LSP features broken by namespace separation | Medium | Tier 2 tests include LSP rename/reference verification |
| Lexer change to ICharSource introduces regressions | Low | All existing lexer tests must pass with StringCharSource |
