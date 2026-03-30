# GB Assembler Test Integration Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add ~234 test methods derived from the RGBDS test suite into existing Koh test classes, covering missing features and missing validations as intentionally-failing tests.

**Architecture:** Each RGBDS `.asm` test becomes an inline C# test method using `Compilation.Create(tree).Emit()`. Tests are distributed into existing test files by category. No new test projects — only new methods in existing classes plus one new file (`ComplexEndToEndTests.cs`).

**Tech Stack:** TUnit 1.21.24, Koh.Core (Compilation/SyntaxTree/EmitModel API), .NET 10, C# 14

**Key patterns:**
- `Emit(string source)` → `EmitModel` (exists in most test files already)
- `EmitWithOutput(string source)` → `(EmitModel, string)` (add where needed for PRINTLN tests)
- Every test has `// RGBDS: test-name` comment
- Tests that exercise unimplemented features WILL FAIL — that is intentional, no `Skip.Test`

**Reference:** Read the RGBDS `.asm` test source from `/tmp/rgbds/test/asm/` (already cloned). Read the spec at `docs/superpowers/specs/2026-03-28-gbasm-test-integration-design.md`.

---

### Task 1: Lexer Tests (`Syntax/LexerTests.cs`)

**Files:**
- Modify: `tests/Koh.Core.Tests/Syntax/LexerTests.cs` (currently 377 lines)
- Reference: `/tmp/rgbds/test/asm/block-comment.asm`, `underscore-in-numeric-literal.asm`, `number-prefixes.asm`, `raw-strings.asm`, `raw-string-symbols.asm`, `line-continuation-string.asm`, `weird-comments.asm`, `invalid-underscore.asm`, `line-continuation.asm`, `line-continuation-whitespace.asm`

**Context:** `LexerTests.cs` uses `using Koh.Core.Syntax; using Koh.Core.Text;` and has a helper `Lex(string source)` returning `List<SyntaxToken>`. These new tests need the `Compilation` API, so add `using Koh.Core; using Koh.Core.Binding;` at the top.

- [ ] **Step 1: Add using statements and Emit helper**

Add at the top of the file after existing usings:
```csharp
using Koh.Core;
using Koh.Core.Binding;
```

Add a private helper at the bottom of the class (before the closing `}`):
```csharp
private static EmitModel Emit(string source)
{
    var tree = SyntaxTree.Parse(source);
    return Compilation.Create(tree).Emit();
}

private static (EmitModel Model, string Output) EmitWithOutput(string source)
{
    var sw = new StringWriter();
    var tree = SyntaxTree.Parse(source);
    var model = Compilation.Create(sw, tree).Emit();
    return (model, sw.ToString());
}
```

- [ ] **Step 2: Add block comment tests**

Read `/tmp/rgbds/test/asm/block-comment.asm` and `/tmp/rgbds/test/asm/block-comment.out` for exact source and expected output. Add 3 test methods:

1. `BlockComment_InlineAndMultiline_AssemblesWithOutput` — should-pass with PRINTLN output verification
2. `BlockComment_NestedOpenDelimiter_WarnsButContinues` — from `block-comment-contents-error.asm`
3. `BlockComment_WeirdCommentEdgeCases_AssemblesCorrectly` — from `weird-comments.asm`

Each test uses `EmitWithOutput()` and asserts `model.Success` is true plus output content.

- [ ] **Step 3: Add numeric literal tests**

Read `/tmp/rgbds/test/asm/underscore-in-numeric-literal.asm` and `/tmp/rgbds/test/asm/number-prefixes.asm`. Add ~7 test methods:

1. `NumericLiteral_UnderscoreAsSeparator_AllBases` — underscores in decimal, hex, binary, octal, gfx
2. `NumericLiteral_LeadingUnderscore_IsAccepted` — leading underscore before digits
3. `NumericLiteral_0x_EquivalentToDollar` — C-style hex prefix
4. `NumericLiteral_0o_EquivalentToAmpersand` — C-style octal prefix
5. `NumericLiteral_0b_EquivalentToPercent` — C-style binary prefix
6. `NumericLiteral_PrefixCaseInsensitive` — `0X`, `0O`, `0B` accepted

Should-reject tests (from `invalid-underscore.asm`):
7. `NumericLiteral_DoubleUnderscore_IsRejected`
8. `NumericLiteral_TrailingUnderscore_IsRejected`

- [ ] **Step 4: Add raw string and character literal tests**

Read `/tmp/rgbds/test/asm/raw-strings.asm`, `raw-string-symbols.asm`, `line-continuation-string.asm`. Add ~5 test methods:

1. `RawString_HashPrefix_DisablesEscapes` — `#"\t"` treated literally
2. `RawString_TripleQuote_SpansNewlines` — `#"""..."""` multi-line
3. `RawStringSymbol_HashName_YieldsStringValue` — `#name` prints EQUS name
4. `LineContinuation_InsideString_JoinsLines` — backslash at EOL inside PRINTLN
5. `LineContinuation_NonWhitespaceAfterBackslash_IsRejected` — from `line-continuation.asm`

- [ ] **Step 5: Run tests to verify they compile and the expected ones fail**

Run: `dotnet test tests/Koh.Core.Tests --filter "ClassName~LexerTests" --no-build -v q 2>&1 | tail -20`

Expected: New tests compile. Some pass (if feature already works), most fail (features not implemented). No crashes.

- [ ] **Step 6: Commit**

```bash
git add tests/Koh.Core.Tests/Syntax/LexerTests.cs
git commit -m "test: add RGBDS-derived lexer tests (block comments, number syntax, raw strings)"
```

---

### Task 2: Expression Tests (`Syntax/ExpressionTests.cs`)

**Files:**
- Modify: `tests/Koh.Core.Tests/Syntax/ExpressionTests.cs` (currently 663 lines)
- Reference: `/tmp/rgbds/test/asm/bit-functions.asm`, `exponent.asm`, `ccode.asm`, `pc-operand.asm`, `pc.asm`, `ds-@.asm`, `jr-@.asm`, `assert-const.asm`, `string-compare.asm`

**Context:** Already has `Emit(string source)` helper and `using Koh.Core; using Koh.Core.Binding;`. Add `EmitWithOutput` helper if not present.

- [ ] **Step 1: Add EmitWithOutput helper if missing**

Check if `EmitWithOutput` exists. If not, add it alongside the existing `Emit` helper.

- [ ] **Step 2: Add `@` (PC) as expression operand tests**

Read `/tmp/rgbds/test/asm/pc-operand.asm`, `pc.asm`, `ds-@.asm`, `jr-@.asm`, `assert-const.asm`. Add ~6 test methods testing `@` in various expression contexts:

1. `PcOperand_InLoadInstruction_ResolvesToCurrentAddress`
2. `PcOperand_InJrSelf_EncodesAsJrMinusTwo`
3. `PcOperand_InDsExpression_FillsToTarget`
4. `PcOperand_InAssertConst_EvaluatesAtAssemblyTime`
5. `PcOperand_InPrintln_FormatsCorrectly` — uses `EmitWithOutput`
6. `PcOperand_InRstVector_ResolvesCorrectly`

- [ ] **Step 3: Add exponentiation and bit function tests**

Read `/tmp/rgbds/test/asm/exponent.asm`, `bit-functions.asm`. Add ~8 tests:

1. `Exponentiation_TwoToTheTen_Is1024`
2. `Exponentiation_ZeroethPower_IsOne`
3. `Exponentiation_NegativeBase_OddExp`
4. `BitWidth_Zero_IsZero` — `BITWIDTH(0) == 0`
5. `BitWidth_FortyTwo_IsSix`
6. `BitWidth_NegativeOne_Is32`
7. `TzCount_Zero_Is32` — `TZCOUNT(0) == 32`
8. `TzCount_FortyTwo_IsOne`

- [ ] **Step 4: Add condition code and string comparison tests**

Read `/tmp/rgbds/test/asm/ccode.asm`, `string-compare.asm`. Add ~5 tests:

1. `ConditionCode_LogicalNot_NzBecomesZ`
2. `ConditionCode_DoubleNot_Identity`
3. `StringCompare_TripleEquals_CaseSensitive`
4. `StringCompare_NotDoubleEquals_Inequality`
5. `StringConcat_PlusPlus_ConcatenatesStrings`

- [ ] **Step 5: Run tests, commit**

Run: `dotnet test tests/Koh.Core.Tests --filter "ClassName~ExpressionTests" --no-build -v q 2>&1 | tail -20`

```bash
git add tests/Koh.Core.Tests/Syntax/ExpressionTests.cs
git commit -m "test: add RGBDS-derived expression tests (PC operand, exponentiation, bit functions)"
```

---

### Task 3: Built-in Function Tests (`Syntax/BuiltinFunctionTests.cs`)

**Files:**
- Modify: `tests/Koh.Core.Tests/Syntax/BuiltinFunctionTests.cs` (currently 935 lines)
- Reference: `/tmp/rgbds/test/asm/math.asm`, `trigonometry.asm`, `strfind-strrfind.asm`, `strupr-strlwr.asm`, `bytelen-strbyte.asm`, `charlen-strchar.asm`, `incharmap.asm`, `readfile.asm`, `readfile-binary.asm`, `fixed-point-specific.asm`, `fixed-point-magnitude.asm`

**Context:** Already has `Emit()`, `EmitWithOutput()`, and `GetExpressionFromImmediate()` helpers.

- [ ] **Step 1: Add math function tests**

Read `/tmp/rgbds/test/asm/math.asm`. Add ~12 tests for `MUL`, `DIV`, `FMOD`, `POW`, `LOG`, `ROUND`, `CEIL`, `FLOOR`:

1. `Div_FixedPoint_PositiveByPositive`
2. `Mul_FixedPoint_TwoTimesThree`
3. `Fmod_FixedPoint_Remainder`
4. `Pow_FixedPoint_TwoSquared`
5. `Log_FixedPoint_OfSixteen`
6. `Round_FixedPoint_HalfUp`
7. `Ceil_FixedPoint_RoundsUp`
8. `Floor_FixedPoint_RoundsDown`

- [ ] **Step 2: Add trigonometry tests**

Read `/tmp/rgbds/test/asm/trigonometry.asm`. Add ~8 tests:

1. `Sin_Zero_IsZero`
2. `Sin_QuarterTurn_IsOne`
3. `Cos_Zero_IsOne`
4. `Asin_One_IsQuarterTurn`
5. `Acos_One_IsZero`
6. `Tan_EighthTurn_IsOne`
7. `Atan_One_IsEighthTurn`
8. `Atan2_OneOne_IsEighthTurn`

- [ ] **Step 3: Add string function tests**

Read `/tmp/rgbds/test/asm/strfind-strrfind.asm`, `strupr-strlwr.asm`, `bytelen-strbyte.asm`, `charlen-strchar.asm`, `incharmap.asm`. Add ~15 tests:

1. `Strfind_SubstringFound_ReturnsIndex`
2. `Strfind_NotFound_ReturnsNegative`
3. `Strrfind_LastOccurrence_ReturnsIndex`
4. `Strupr_LowercaseInput_ReturnsUppercase`
5. `Strlwr_UppercaseInput_ReturnsLowercase`
6. `Bytelen_AsciiString_EqualsStrlen`
7. `Bytelen_WithCustomCharmap_CountsBytes`
8. `Strbyte_FirstByte_ReturnsCorrectValue`
9. `Strchar_FirstChar_ReturnsCodepoint`
10. `Charlen_AsciiCharmap_EqualsStrlen`
11. `Charlen_CustomCharmap_CountsMappedChars`
12. `Incharmap_MappedChar_ReturnsTrue`
13. `Incharmap_UnmappedChar_ReturnsFalse`

- [ ] **Step 4: Add READFILE and fixed-point tests**

Read `/tmp/rgbds/test/asm/readfile.asm`, `readfile-binary.asm`, `fixed-point-specific.asm`, `fixed-point-magnitude.asm`. Add ~8 tests:

1. `Readfile_ExistingFile_ReturnsContent` (needs VirtualFileResolver or temp file)
2. `Readfile_WithMaxLength_Truncates`
3. `Readfile_MissingFile_ProducesError` (from `readfile-mg.asm`)
4. `FixedPoint_MulQ16_CorrectResult`
5. `FixedPoint_DivQ8_CorrectResult`
6. `FixedPoint_SinQ16_CorrectResult`
7. `FixedPoint_MaxValueQ16_InRange`

- [ ] **Step 5: Run tests, commit**

Run: `dotnet test tests/Koh.Core.Tests --filter "ClassName~BuiltinFunctionTests" --no-build -v q 2>&1 | tail -20`

```bash
git add tests/Koh.Core.Tests/Syntax/BuiltinFunctionTests.cs
git commit -m "test: add RGBDS-derived built-in function tests (math, trig, string, readfile)"
```

---

### Task 4: Binder Symbol Tests (`Binding/BinderSymbolTests.cs`)

**Files:**
- Modify: `tests/Koh.Core.Tests/Binding/BinderSymbolTests.cs` (currently 865 lines)
- Reference: `/tmp/rgbds/test/asm/anon-label.asm`, `endl-local-scope.asm`, `scope-level.asm`, `period.asm`, `sym-scope.asm`, `remote-local-explicit.asm`, `raw-identifiers.asm`, `symbol-names.asm`, `equs-macrodef.asm`, `equs-nest.asm`, `equs-newline.asm`, `equs-purge.asm`, `label-indent.asm`, `purge-deferred.asm`, plus should-reject: `double-purge.asm`, `empty-local-purged.asm`, `local-purge.asm`, `local-ref-without-parent.asm`, `local-without-parent.asm`, `purge-ref.asm`, `purge-refs.asm`, `reference-undefined-equs.asm`, `ref-override-bad.asm`, `sym-collision.asm`, `warn-truncation.asm`

**Context:** Has `Bind(string source)` and `Emit(string source)` helpers.

- [ ] **Step 1: Add anonymous label tests**

Read `/tmp/rgbds/test/asm/anon-label.asm`. Add:
1. `AnonymousLabel_ForwardBackwardReferences` — `:+`, `:-`, `:++` syntax
2. `AnonymousLabel_MultipleAnonymousLabels_ResolveCorrectly`

- [ ] **Step 2: Add EQUS expansion tests**

Read `equs-macrodef.asm`, `equs-nest.asm`, `equs-newline.asm`, `equs-purge.asm`. Add:
1. `EqusMacroDef_ExpansionDefinesMacro`
2. `EqusNest_SelfRedefViaExpansion`
3. `EqusNewline_EmbeddedNewline_ExpandsToMultipleStatements`
4. `EqusPurge_SelfPurgeDuringExpansion`

- [ ] **Step 3: Add scope and label tests**

Read `scope-level.asm`, `period.asm`, `sym-scope.asm`, `remote-local-explicit.asm`, `raw-identifiers.asm`, `symbol-names.asm`, `label-indent.asm`, `purge-deferred.asm`, `endl-local-scope.asm`. Add:
1. `ScopeLevel_DefDot_DetectsCurrentScope`
2. `Period_DefDotDot_DetectsParentScope`
3. `SymScope_ExplicitLocalInjection`
4. `RemoteLocalExplicit_ExportedDottedLabel`
5. `RawIdentifiers_HashPrefixEscapesKeywords`
6. `SymbolNames_SpecialCharacters_Valid`
7. `LabelIndent_IndentedLabelsAndColons`
8. `PurgeDeferred_BraceInterpolatedName`
9. `EndlLocalScope_LabelAfterEndlAttachesToRom`

- [ ] **Step 4: Add should-reject symbol tests**

Read `.asm` and `.err` for each should-reject test. Add ~11 tests:
1. `DoublePurge_PurgingSameSymbolTwice_RejectsAssembly`
2. `EmptyLocalPurged_PurgedLocalRef_RejectsAssembly`
3. `LocalPurge_PurgeLocalLabel_RejectsAssembly`
4. `LocalRefWithoutParent_RejectsAssembly`
5. `LocalWithoutParent_RejectsAssembly`
6. `PurgeRef_PurgeReferencedSymbol_RejectsAssembly`
7. `PurgeRefs_PurgeMultipleReferenced_RejectsAssembly`
8. `ReferenceUndefinedEqus_RejectsAssembly`
9. `RefOverrideBad_RejectsAssembly`
10. `SymCollision_DuplicateSymbol_RejectsAssembly`
11. `WarnTruncation_ValueOutOfRange_RejectsAssembly`

- [ ] **Step 5: Run tests, commit**

```bash
git add tests/Koh.Core.Tests/Binding/BinderSymbolTests.cs
git commit -m "test: add RGBDS-derived symbol tests (anon labels, EQUS, scope, rejections)"
```

---

### Task 5: Directive Tests (`Binding/DirectiveTests.cs`)

**Files:**
- Modify: `tests/Koh.Core.Tests/Binding/DirectiveTests.cs` (currently 895 lines)
- Reference: `/tmp/rgbds/test/asm/empty-data-directive.asm`, `ds-align.asm`, `ds-align-min.asm`, `ds-align-offset.asm`, `align-increasing.asm`, `align-pc.asm`, `abort-on-missing-incbin*.asm`, plus ~25 should-reject tests

- [ ] **Step 1: Add `dl` and `ds align` tests**

Read the RGBDS sources. Add ~8 should-pass tests:
1. `EmptyDataDirective_Dl_WithoutData_Succeeds`
2. `DsAlign_PadsToAlignmentBoundary`
3. `DsAlign_AlreadyAligned_NoPadding`
4. `DsAlignMin_UsesMinimumAlignment`
5. `DsAlignOffset_PadsToOffsetWithinAlignment`
6. `AlignIncreasing_WithMatchingOffset`
7. `AlignPc_FixedOrgAssertPc`
8. `AbortOnMissingIncbin_ReportsDiagnostic`

- [ ] **Step 2: Add should-reject directive tests**

Read `.asm`/`.err` for each. Add ~25 tests covering: `align-large-ofs`, `align-offset`, `section-align-large-ofs`, `align-unattainable`, `data-in-ram`, `ds-bad`, `duplicate-section`, `fixed-oob`, `fragment-align`, `fragment-align-mismatch`, `fragment-mismatch`, `impossible-bank`, `incompatible-alignment`, `invalid-alignment`, `invalid-bank`, `label-outside-section`, `use-label-outside-section`, `new-pushed-section`, `pushs`, `section-union-data`, `section-union-mismatch`, `section-name-invalid`, `charmap-empty`, `const-and`, `divzero-section-bank`.

Each test follows the pattern:
```csharp
// RGBDS: test-name
[Test]
public async Task TestName_Scenario_RejectsAssembly()
{
    var model = Emit("""
        <minimal reproducer from .asm file>
        """);
    await Assert.That(model.Success).IsFalse();
}
```

- [ ] **Step 3: Run tests, commit**

```bash
git add tests/Koh.Core.Tests/Binding/DirectiveTests.cs
git commit -m "test: add RGBDS-derived directive tests (dl, ds align, section validation)"
```

---

### Task 6: Macro Tests (`Binding/MacroTests.cs`)

**Files:**
- Modify: `tests/Koh.Core.Tests/Binding/MacroTests.cs` (currently 617 lines)
- Reference: `/tmp/rgbds/test/asm/macro-arguments.asm`, `macro-arg-escape-chars.asm`, `macro-argument-limit.asm`, `trimmed-macro-args.asm`, `trailing-commas.asm`, `sort-algorithms.asm`, `operator-associativity.asm`, plus should-reject: `builtin-overwrite.asm`, `code-after-endm-endr-endc.asm`, `dots-macro-arg.asm`, `macro-arg-recursion.asm`, `macro-args-outside-macro.asm`, `macro-syntax.asm`, `shift-outside-macro.asm`, `bracketed-macro-args.asm`, `rept-shift.asm`

- [ ] **Step 1: Add EmitWithOutput helper**

Add alongside existing `Emit`:
```csharp
private static (EmitModel Model, string Output) EmitWithOutput(string source)
{
    var sw = new StringWriter();
    var tree = SyntaxTree.Parse(source);
    var model = Compilation.Create(sw, tree).Emit();
    return (model, sw.ToString());
}
```

- [ ] **Step 2: Add should-pass macro tests**

Read the RGBDS sources. Add ~12 tests:
1. `MacroArguments_EmptyCall_NargIsZero`
2. `MacroArguments_ThreeArgs_NargIsThree`
3. `MacroArguments_KeywordAsArg_TreatedAsText`
4. `MacroArgEscapeChars_TripleQuotedStringArg`
5. `MacroArgumentLimit_NineArgs_AllAccessible`
6. `TrimmedMacroArgs_WhitespaceAroundArgs_Trimmed`
7. `TrailingComma_MacroCall_Ignored`
8. `TrailingComma_DbDirective_Allowed`
9. `SortAlgorithms_SelectionSortViaMacro` — simplified version testing core macro features
10. `OperatorAssociativity_Division_LeftAssociative`
11. `OperatorAssociativity_Exponentiation_RightAssociative`
12. `ComputedMacroArgIndex_AngleBracketSyntax`

- [ ] **Step 3: Add should-reject macro tests**

Read `.asm`/`.err` for each. Add ~10 tests:
1. `BuiltinOverwrite_RedefiningBuiltin_RejectsAssembly`
2. `CodeAfterEndm_RejectsAssembly`
3. `DotsMacroArg_DotsInArgName_RejectsAssembly`
4. `MacroArgRecursion_InfiniteExpansion_RejectsAssembly`
5. `MacroArgsOutsideMacro_BackslashOutsideMacro_RejectsAssembly`
6. `MacroSyntax_InvalidMacroDefinition_RejectsAssembly`
7. `ShiftOutsideMacro_RejectsAssembly`
8. `BracketedMacroArgs_InvalidBracketUsage_RejectsAssembly`
9. `ReptShift_ShiftInsideRept_RejectsAssembly`

- [ ] **Step 4: Run tests, commit**

```bash
git add tests/Koh.Core.Tests/Binding/MacroTests.cs
git commit -m "test: add RGBDS-derived macro tests (arguments, trailing commas, rejections)"
```

---

### Task 7: Repeat, CharMap, Union/Load Tests

**Files:**
- Modify: `tests/Koh.Core.Tests/Binding/RepeatTests.cs` (414 lines)
- Modify: `tests/Koh.Core.Tests/Binding/CharMapTests.cs` (493 lines)
- Modify: `tests/Koh.Core.Tests/Binding/UnionLoadTests.cs` (515 lines)

- [ ] **Step 1: Add repeat tests**

Read `/tmp/rgbds/test/asm/nested-break.asm`, `for-loop-count.asm`, `unique-id-nested.asm`, `rept-trace.asm`. Add `EmitWithOutput` helper if missing. Add ~8 tests:

RepeatTests:
1. `NestedBreak_BreakInnerLoop_ContinuesOuter`
2. `NestedBreak_BreakOuterLoop_Exits`
3. `ForLoopCount_PositiveRange_CorrectIterations` — with PRINTLN output
4. `ForLoopCount_StartEqualsStop_ZeroIterations`
5. `UniqueIdNested_MacroInsideRept_UniqueIds` — `\@` produces unique suffixes
6. `ReptTrace_InvalidInsideRept_RejectsAssembly` (should-reject)

- [ ] **Step 2: Add charmap tests**

Read `charmap-unicode.asm`, `multivalue-charmap.asm`, `equ-charmap.asm`, `null-char-functions.asm`. Add ~8 tests:

CharMapTests:
1. `CharmapUnicode_Utf8Char_CharlenIsOne`
2. `CharmapUnicode_MixedAsciiAndUtf8`
3. `MultivalueCharmap_MultiByteEntry_EmitsAllBytes`
4. `MultivalueCharmap_TruncationWarning`
5. `EquCharmap_CharLiteralViaEqu`
6. `NullCharFunctions_NullInString_StrlenCounts`
7. `NullCharFunctions_PrintlnWithNull`

- [ ] **Step 3: Add union/load tests**

Read `single-union.asm`, `union-in-union.asm`, `union-pushs.asm`, `load-endings.asm`, `load-pushs-load.asm`. Add should-pass + should-reject tests:

UnionLoadTests:
1. `SingleUnion_ExportedLabel_AtBaseAddress`
2. `UnionInUnion_NestedSize_IsMaxOfAll`
3. `UnionPushs_PushsInsideUnion`
4. `LoadEndings_UntermninatedLoad_Warns`
5. `LoadPushsLoad_NestedLoadAndPushs`

Should-reject:
6. `LoadRom_LoadIntoRom_RejectsAssembly`
7. `LoadOverflow_RejectsAssembly`
8. `SectionInLoad_RejectsAssembly`
9. `UnionMismatch_RejectsAssembly`

- [ ] **Step 4: Run tests, commit**

```bash
git add tests/Koh.Core.Tests/Binding/RepeatTests.cs tests/Koh.Core.Tests/Binding/CharMapTests.cs tests/Koh.Core.Tests/Binding/UnionLoadTests.cs
git commit -m "test: add RGBDS-derived repeat, charmap, and union/load tests"
```

---

### Task 8: Directive Extension and Conditional Tests

**Files:**
- Modify: `tests/Koh.Core.Tests/Binding/DirectiveExtensionTests.cs` (764 lines)
- Modify: `tests/Koh.Core.Tests/Binding/ConditionalTests.cs` (488 lines)

- [ ] **Step 1: Add directive extension tests**

Read `/tmp/rgbds/test/asm/div-negative.asm`, `div-mod.asm`, `format-extremes.asm`, `shift.asm`, `opt.asm`, `section-name.asm`, `sizeof-reg.asm`, `flag-Q.asm`. Add `EmitWithOutput` if missing. Add ~15 tests:

1. `DivNegative_NegativeByNegative_PositiveQuotient`
2. `DivNegative_PositiveByNegative_NegativeQuotient`
3. `DivMod_ModuloIdentityLaw`
4. `FormatExtremes_HexFormat_Int32Max`
5. `FormatExtremes_UnsignedDecimal_Uint32Max`
6. `Shift_LeftByOne_Doubles`
7. `Shift_ArithmeticRight_SignExtends`
8. `Opt_PushoPopoRestoresOptions`
9. `SectionName_FunctionReturnsName`
10. `SizeofReg_8BitRegisters_IsOne`
11. `SizeofReg_16BitRegisters_IsTwo`
12. `FlagQ_FixedPointLiteral_Encoded`

Should-reject:
13. `InvalidOpt_UnrecognizedOption_RejectsAssembly`
14. `UndefinedOpt_RejectsAssembly`
15. `MaxErrors_StopsAfterLimit_RejectsAssembly`

- [ ] **Step 2: Add conditional tests**

Read `nested-if.asm`. Add ~4 tests:
1. `NestedIf_WithParentheses_ParsedCorrectly`
2. `NestedIf_DeepNesting_CorrectBranchSelection`
3. `ElifAfterElse_RejectsAssembly` (should-reject)
4. `MultipleElse_RejectsAssembly` (should-reject)
5. `LabelBeforeEndc_RejectsAssembly` (should-reject)
6. `MisleadingIndentation_RejectsAssembly` (should-reject)

- [ ] **Step 3: Run tests, commit**

```bash
git add tests/Koh.Core.Tests/Binding/DirectiveExtensionTests.cs tests/Koh.Core.Tests/Binding/ConditionalTests.cs
git commit -m "test: add RGBDS-derived directive extension and conditional tests"
```

---

### Task 9: RGBDS Compat, Include, Instruction, and Parser Tests

**Files:**
- Modify: `tests/Koh.Core.Tests/Binding/RgbdsCompatTests.cs` (472 lines)
- Modify: `tests/Koh.Core.Tests/Binding/IncludeTests.cs` (285 lines)
- Modify: `tests/Koh.Core.Tests/Binding/InstructionBindingTests.cs` (860 lines)
- Modify: `tests/Koh.Core.Tests/Syntax/ParserTests.cs` (530 lines)
- Modify: `tests/Koh.Core.Tests/Syntax/DataDirectiveTests.cs` (129 lines)

- [ ] **Step 1: Add RGBDS compat tests**

Read `multiple-instructions.asm`, `rst.asm`, `destination-a.asm`, `preinclude.asm`, `utc-time.asm`, `state-features.asm`, `include-unique-id.asm`. Add `EmitWithOutput` if missing. Add ~10 tests:

1. `MultipleInstructions_DoubleColonSeparator_AllEmitted`
2. `Rst_StandardVectors_Encoded`
3. `Rst_LabelForwardRef_Resolved`
4. `DestinationA_ExplicitA_SameAsImplicit`
5. `Preinclude_PreIncludedFile_SymbolsAvailable`
6. `UtcTime_BuiltinDateSymbols_InValidRange`
7. `StateFeatures_MacroAndCharmap_Assembles`
8. `IncludeUniqueId_MacroInvoked_UniqueIdResolved`

- [ ] **Step 2: Add include tests**

Read `abort-on-missing-incbin*.asm`, `include-slash.asm`. Add ~5 should-reject tests:

1. `IncludeSlash_TrailingSlashInPath_RejectsAssembly`
2. `IncbinEmptyBad_NegativeLength_RejectsAssembly`
3. `IncbinEndBad_EndBeforeStart_RejectsAssembly`
4. `IncbinNegativeBad_NegativeOffset_RejectsAssembly`
5. `IncbinStartBad_StartPastEnd_RejectsAssembly`

- [ ] **Step 3: Add instruction binding rejection tests**

Read `ff00+c-bad.asm`, `invalid-ldh.asm`, `divzero-instr.asm`, `modzero-instr.asm`. Add ~4 tests:

1. `Ff00PlusC_InvalidRegister_RejectsAssembly`
2. `InvalidLdh_NonHighMem_RejectsAssembly`
3. `DivzeroInstr_DivisionByZeroInExpr_RejectsAssembly`
4. `ModzeroInstr_ModuloByZeroInExpr_RejectsAssembly`

- [ ] **Step 4: Add parser and data directive tests**

ParserTests — read `syntax-error.asm`. Add:
1. `SyntaxError_InvalidTokenSequence_ProducesDiagnostic`

DataDirectiveTests — read `empty-data-directive.asm`, `db-dw-dl-string.asm`. Add:
1. `Dl_ThirtyTwoBitData_FourBytesEmitted`
2. `EmptyDataDirective_Dl_NoArgs`
3. `DbCharLiteralPlusOne_CharacterArithmetic`

- [ ] **Step 5: Run tests, commit**

```bash
git add tests/Koh.Core.Tests/Binding/RgbdsCompatTests.cs tests/Koh.Core.Tests/Binding/IncludeTests.cs tests/Koh.Core.Tests/Binding/InstructionBindingTests.cs tests/Koh.Core.Tests/Syntax/ParserTests.cs tests/Koh.Core.Tests/Syntax/DataDirectiveTests.cs
git commit -m "test: add RGBDS-derived compat, include, instruction, and parser tests"
```

---

### Task 10: Binary Output and Complex End-to-End Tests

**Files:**
- Create: `tests/Koh.Core.Tests/Integration/ComplexEndToEndTests.cs`
- Reference: RGBDS `.out.bin` files for 59 should-pass tests

**Context:** 59 RGBDS tests include `.out.bin` — the expected linked ROM bytes. Rather than going through the linker, tests compare `EmitModel.Sections[].Data` (concatenated) against the expected bytes. This validates that Koh produces bit-identical output to RGBDS for the assembled sections.

- [ ] **Step 1: `ComplexEndToEndTests.cs` already created**

The file has been created with `Emit()`, `EmitWithOutput()`, `AssertBinaryOutput()`, and `Hex()` helpers, plus ~25 binary output tests covering both currently-passing and currently-failing features.

- [ ] **Step 2: Add remaining binary output tests**

For each RGBDS test with `.out.bin` not yet covered, read the `.asm` and `.out.bin` files and add a test method. The pattern:

```csharp
// RGBDS: test-name
[Test]
public async Task TestName_BinaryMatch()
{
    var model = Emit("""
        <source from .asm file>
        """);
    await AssertBinaryOutput(model, Hex("<hex from .out.bin>"));
}
```

Priority: tests for features that Koh already handles (load-*, incbin, macro-#, etc.).

- [ ] **Step 3: Add complex multi-feature tests**

Tests that exercise multiple features working together:
1. `SortAlgorithms_FullSelectionSort_ProducesCorrectOutput` — FOR, macro args, `\<N>`
2. `LongRpnExpression_DeeplyNestedEqus_Resolves` — EQUS chains
3. `DivNegative_FullMatrix_IdentityLaw` — div/mod with PRINTLN verification
4. `FormatExtremes_AllFormats_CorrectOutput` — hex, octal, binary, unsigned, signed, fixed-point

- [ ] **Step 4: Run all tests to get baseline failure count**

Run: `dotnet test tests/Koh.Core.Tests -v q 2>&1 | tail -30`

This gives us the baseline: how many new tests fail. The failing count is the work backlog.

- [ ] **Step 5: Commit**

```bash
git add tests/Koh.Core.Tests/Integration/ComplexEndToEndTests.cs
git commit -m "test: add binary output and complex end-to-end tests from RGBDS suite"
```

---

### Task 11: Final verification

- [ ] **Step 1: Build and run full test suite**

```bash
dotnet build tests/Koh.Core.Tests -v q
dotnet test tests/Koh.Core.Tests -v q 2>&1 | tail -30
```

Verify:
- All pre-existing tests still pass (no regressions)
- New tests compile and run (some pass, many fail intentionally)
- No crashes or hangs

- [ ] **Step 2: Count and report results**

```bash
dotnet test tests/Koh.Core.Tests -v q 2>&1 | grep -E "(Passed|Failed|Skipped)"
```

Report the total pass/fail counts. The failing tests ARE the backlog for the fixing session.
