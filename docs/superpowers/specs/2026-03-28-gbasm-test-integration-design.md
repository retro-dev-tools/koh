# GB Assembler Test Integration Design

**Date**: 2026-03-28
**Source**: RGBDS test suite (`github.com/gbdev/rgbds/tree/master/test/asm`)
**Goal**: Integrate ~180 test cases (110 should-pass failures + ~72 should-reject false-positives) into existing Koh test classes as pure C# TUnit tests, serving as a development backlog for missing features and bug fixes.

## Approach

- **Pure C# inline**: Each RGBDS `.asm` test becomes a C# test method with the source as a raw string literal. No external `.asm` files.
- **In-process execution**: Tests call `Compilation.Create(tree).Emit()` directly. PRINTLN tests pass a `StringWriter` to capture output.
- **Distribute into existing classes**: Tests go into the existing test file that matches their category. No new test projects or classes.
- **Failing tests as backlog**: Tests for unimplemented features are added as real failing tests (no `Skip.Test`). The test suite will have failures — that's intentional. The failing test count is the work backlog. A subsequent fixing session works through them until they pass.

## Test Distribution

| Existing test file | New tests | Feature categories |
|---|---|---|
| `Syntax/LexerTests.cs` | ~15 | Block comments `/* */`, underscores in numeric literals, `0x`/`0b`/`0o` prefixes, character literals `'X'`, raw strings `#"..."` |
| `Syntax/ExpressionTests.cs` | ~20 | `@` as PC operand, `**` exponentiation, built-in functions (`SIN`, `COS`, `BITWIDTH`, `STRLEN` etc.), `===`/`!==` string comparison |
| `Syntax/BuiltinFunctionTests.cs` | ~15 | `STRFIND`, `STRRFIND`, `STRUPR`, `STRLWR`, `CHARLEN`, `BYTELEN`, `STRBYTE`, `STRCHAR`, `READFILE`, `INCHARMAP` |
| `Binding/BinderSymbolTests.cs` | ~15 | Anonymous labels `:+`/`:-`, EQUS multi-line expansion, scope/period, `def(.)`, raw identifiers `#name`, `Parent.child` scope syntax |
| `Binding/DirectiveTests.cs` | ~15 | `dl` 32-bit data, `ds align[N]`, ASSERT with `@`, INCBIN abort behavior |
| `Binding/MacroTests.cs` | ~12 | Keyword args as text, `\<N>` computed index, quiet `?` suffix, trailing commas, argument limit, escape chars in args |
| `Binding/CharMapTests.cs` | ~8 | `INCHARMAP()`, multivalue charmap, unicode charmap, `equ-charmap` character literal EQU |
| `Binding/RepeatTests.cs` | ~8 | Nested BREAK, FOR loop count with `{d:x}` interpolation, unique ID in nested REPT |
| `Binding/UnionLoadTests.cs` | ~10 | UNION nesting, UNION with PUSHS, LOAD/ENDL edge cases, `ENDL` local label scope |
| `Binding/DirectiveExtensionTests.cs` | ~10 | PRINTLN format strings (`{d:x}`, `{f:n}`), fixed-point formatting, `OPT Q.N`, shift output, `SIZEOF`/`STARTOF` |
| `Binding/ConditionalTests.cs` | ~5 | Nested IF without space `if(1)`, complex conditional + EQUS |
| `Binding/RgbdsCompatTests.cs` | ~8 | `::` as statement separator, negated condition codes `!nz`, preinclude, `__UTC_*` builtins, state features |
| `Binding/IncludeTests.cs` | ~3 | INCBIN missing file abort, INCBIN slice/start abort |
| `Syntax/ParserTests.cs` | ~5 | Multiple instructions per line `::`, `rst` label ambiguity, fragment literals |
| `Syntax/DataDirectiveTests.cs` | ~5 | `dl` directive, empty data directives, `db 'A' + 1` |
| `Integration/ComplexEndToEndTests.cs` (new) | ~8 | Complex end-to-end: `sort-algorithms`, `long-rpn-expression`, `long-string-constant`, `div-negative`, `format-extremes` |

**Subtotal: ~162 should-pass test methods** across 16 existing files.

## Should-Reject Tests (+72 methods)

For the 72 tests where RGBDS expects errors but Koh silently accepts, add tests asserting `model.Success` is `false`. These are real failing tests (no skips) — they fail because Koh doesn't reject invalid input yet. They go into the same files based on category (e.g., `align-large-ofs` into `DirectiveTests`, `invalid-ldh` into `InstructionBindingTests`, `syntax-error` into `ParserTests`).

**Grand total: ~234 new test methods** across 16 existing files (not counting binary output tests below).

## Test Method Pattern

### Should-pass test (feature not yet implemented — will fail until feature is added):
```csharp
// RGBDS: anon-label
[Test]
public async Task AnonymousLabel_ForwardBackwardReferences()
{
    var model = Emit("""
        SECTION "test", ROM0[0]
            ld hl, :++
        :   ld a, [hli]
            ldh [c], a
            dec c
            jr nz, :-
            ret
        :
            dw $7FFF, $1061, $03E0, $58A5
        """);
    await Assert.That(model.Success).IsTrue();
}
```

### Should-pass test with PRINTLN output:
```csharp
// RGBDS: block-comment
[Test]
public async Task BlockComment_PrintlnAroundComments()
{
    var (model, output) = EmitWithOutput("""
        PRINTLN "hi"
        /* block comment */
        PRINTLN "block (/* ... */) comments at ends of line are fine"
        """);
    await Assert.That(model.Success).IsTrue();
    await Assert.That(output.TrimEnd()).IsEqualTo(
        "hi\nblock (/* ... */) comments at ends of line are fine");
}
```

### Should-reject test (missing validation — will fail until validation is added):
```csharp
// RGBDS: align-large-ofs
[Test]
public async Task AlignLargeOffset_RejectsInvalidAlignment()
{
    var model = Emit("""
        SECTION "test", ROM0, ALIGN[4, 16]
        """);
    await Assert.That(model.Success).IsFalse();
}
```

## Naming Convention

Test methods follow the existing pattern: `[Feature]_[Scenario]`. When derived from an RGBDS test, include a comment with the source test name:

```csharp
// RGBDS: anon-label
[Test]
public async Task AnonymousLabel_ForwardBackwardReferences()
```

## Shared Helpers

Each test file already has a private `Emit()` helper. For PRINTLN tests, add an `EmitWithOutput()` variant where needed:

```csharp
private static (EmitModel Model, string Output) EmitWithOutput(string source)
{
    var sw = new StringWriter();
    var tree = SyntaxTree.Parse(source);
    var model = Compilation.Create(sw, tree).Emit();
    return (model, sw.ToString());
}
```

## Implementation Order

The implementation plan (next step) will process test files roughly by dependency order:
1. **Lexer-level** first (block comments, number syntax, character literals) - unblocks many downstream tests
2. **Expression parser** (built-in functions, `@`, `**`) - unblocks directive and binding tests
3. **Directives** (`dl`, `ds align`, UNION) - standalone features
4. **Macro/EQUS** - most complex, depends on lexer/parser being solid
5. **PRINTLN/formatting** - depends on expressions and string functions
6. **Should-reject validations** - can be done independently

## Binary Output Tests (`.out.bin`)

59 RGBDS tests include `.out.bin` files — the expected linked ROM output. For these, tests will assemble with Koh, link with `Koh.Linker`, and compare the output bytes against the expected binary. These go into `Integration/ComplexEndToEndTests.cs` or a dedicated `Integration/BinaryOutputTests.cs` if the count warrants it. Tests that fail due to linker gaps are still added as failing tests (no skips).

## Out of Scope

- **RGBDS CLI flag tests**: Koh has its own CLI with different flags (`-f rgbds` vs `-Weverything`). The RGBDS `cli/*.flags` tests are not portable.
- **Piped stdin tests**: RGBDS runs each test twice (file input + piped stdin). This tests RGBDS's stream handling, not assembler correctness. Koh doesn't support stdin input.
- **RGBDS version-specific tests**: The `version.asm` test checks `__RGBDS_MAJOR__` etc. Koh may define its own version builtins but won't pretend to be RGBDS.
- **`make-deps` / `-M` dependency generation**: RGBDS-specific build system integration feature.
- **`state-file` / `-s` state dump**: RGBDS-specific debugging feature.
