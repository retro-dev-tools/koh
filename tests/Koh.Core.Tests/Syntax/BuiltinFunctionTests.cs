using Koh.Core;
using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Core.Syntax;

namespace Koh.Core.Tests.Syntax;

public class BuiltinFunctionTests
{
    private static SyntaxNode GetExpressionFromImmediate(string source)
    {
        var tree = SyntaxTree.Parse(source);
        var instr = tree.Root.ChildNodes().First();
        var immediate = instr.ChildNodes().First(n => n.Kind == SyntaxKind.ImmediateOperand);
        return immediate.ChildNodes().First();
    }

    [Test]
    public async Task High()
    {
        var expr = GetExpressionFromImmediate("ld a, HIGH($AABB)");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.FunctionCallExpression);

        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[0].AsToken!.Kind).IsEqualTo(SyntaxKind.HighKeyword);
        await Assert.That(children[1].AsToken!.Kind).IsEqualTo(SyntaxKind.OpenParenToken);
        // Argument is a LiteralExpression
        await Assert.That(children[2].AsNode!.Kind).IsEqualTo(SyntaxKind.LiteralExpression);
        await Assert.That(children[3].AsToken!.Kind).IsEqualTo(SyntaxKind.CloseParenToken);
    }

    [Test]
    public async Task Low()
    {
        var expr = GetExpressionFromImmediate("ld a, LOW($AABB)");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.FunctionCallExpression);
        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[0].AsToken!.Kind).IsEqualTo(SyntaxKind.LowKeyword);
    }

    [Test]
    public async Task Bank()
    {
        var expr = GetExpressionFromImmediate("ld a, BANK(\"ROM0\")");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.FunctionCallExpression);
        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[0].AsToken!.Kind).IsEqualTo(SyntaxKind.BankKeyword);
        // Argument is a LiteralExpression(StringLiteral)
        await Assert.That(children[2].AsNode!.Kind).IsEqualTo(SyntaxKind.LiteralExpression);
    }

    [Test]
    public async Task Sizeof()
    {
        var expr = GetExpressionFromImmediate("ld hl, SIZEOF(\"ROM0\")");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.FunctionCallExpression);
        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[0].AsToken!.Kind).IsEqualTo(SyntaxKind.SizeofKeyword);
    }

    [Test]
    public async Task Startof()
    {
        var expr = GetExpressionFromImmediate("ld hl, STARTOF(\"ROM0\")");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.FunctionCallExpression);
        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[0].AsToken!.Kind).IsEqualTo(SyntaxKind.StartofKeyword);
    }

    [Test]
    public async Task Def()
    {
        var expr = GetExpressionFromImmediate("ld a, DEF(MySymbol)");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.FunctionCallExpression);
        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[0].AsToken!.Kind).IsEqualTo(SyntaxKind.DefKeyword);
        await Assert.That(children[2].AsNode!.Kind).IsEqualTo(SyntaxKind.NameExpression);
    }

    [Test]
    public async Task Strlen()
    {
        var expr = GetExpressionFromImmediate("ld a, STRLEN(\"hello\")");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.FunctionCallExpression);
        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[0].AsToken!.Kind).IsEqualTo(SyntaxKind.StrlenKeyword);
    }

    [Test]
    public async Task Strcat_MultipleArgs()
    {
        var expr = GetExpressionFromImmediate("ld a, STRCAT(\"a\", \"b\")");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.FunctionCallExpression);

        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[0].AsToken!.Kind).IsEqualTo(SyntaxKind.StrcatKeyword);
        await Assert.That(children[1].AsToken!.Kind).IsEqualTo(SyntaxKind.OpenParenToken);
        await Assert.That(children[2].AsNode!.Kind).IsEqualTo(SyntaxKind.LiteralExpression); // "a"
        await Assert.That(children[3].AsToken!.Kind).IsEqualTo(SyntaxKind.CommaToken);
        await Assert.That(children[4].AsNode!.Kind).IsEqualTo(SyntaxKind.LiteralExpression); // "b"
        await Assert.That(children[5].AsToken!.Kind).IsEqualTo(SyntaxKind.CloseParenToken);
    }

    [Test]
    public async Task Strsub_ThreeArgs()
    {
        var expr = GetExpressionFromImmediate("ld a, STRSUB(\"abc\", 2, 1)");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.FunctionCallExpression);

        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[0].AsToken!.Kind).IsEqualTo(SyntaxKind.StrsubKeyword);
        // 3 args separated by 2 commas: keyword ( arg , arg , arg )
        // children: [0]=keyword [1]=( [2]=arg [3]=, [4]=arg [5]=, [6]=arg [7]=)
        await Assert.That(children).Count().IsEqualTo(8);
    }

    [Test]
    public async Task FunctionInExpression()
    {
        // HIGH($AABB) + 1
        var expr = GetExpressionFromImmediate("ld a, HIGH($AABB) + 1");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.BinaryExpression);

        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[0].AsNode!.Kind).IsEqualTo(SyntaxKind.FunctionCallExpression);
        await Assert.That(children[1].AsToken!.Kind).IsEqualTo(SyntaxKind.PlusToken);
    }

    [Test]
    public async Task NestedFunctions()
    {
        // HIGH(LOW($AABB))
        var expr = GetExpressionFromImmediate("ld a, HIGH(LOW($AABB))");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.FunctionCallExpression);

        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[0].AsToken!.Kind).IsEqualTo(SyntaxKind.HighKeyword);
        // The argument is itself a FunctionCallExpression
        await Assert.That(children[2].AsNode!.Kind).IsEqualTo(SyntaxKind.FunctionCallExpression);
    }

    [Test]
    public async Task CaseInsensitive()
    {
        var expr = GetExpressionFromImmediate("ld a, high($AABB)");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.FunctionCallExpression);
    }

    [Test]
    public async Task NoDiagnostics()
    {
        var tree = SyntaxTree.Parse("ld a, HIGH($AABB)");
        await Assert.That(tree.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task MissingCloseParen_ProducesDiagnostic()
    {
        var tree = SyntaxTree.Parse("ld a, HIGH($AABB");
        await Assert.That(tree.Diagnostics).IsNotEmpty();
        // Tree must still be well-formed
        var instr = tree.Root.ChildNodes().First();
        var ops = instr.ChildNodes().ToList();
        var immediate = ops.First(n => n.Kind == SyntaxKind.ImmediateOperand);
        var func = immediate.ChildNodes().First();
        await Assert.That(func.Kind).IsEqualTo(SyntaxKind.FunctionCallExpression);
    }

    [Test]
    public async Task MissingOpenParen_ProducesDiagnostic()
    {
        var tree = SyntaxTree.Parse("ld a, HIGH $AABB");
        await Assert.That(tree.Diagnostics).IsNotEmpty();
    }

    [Test]
    public async Task TrailingComma_ProducesDiagnostic()
    {
        var tree = SyntaxTree.Parse("ld a, HIGH(1,)");
        await Assert.That(tree.Diagnostics).IsNotEmpty();
        // Tree still has a FunctionCallExpression
        var instr = tree.Root.ChildNodes().First();
        await Assert.That(instr.Kind).IsEqualTo(SyntaxKind.InstructionStatement);
    }

    [Test]
    public async Task EmptyFirstArgument_ProducesDiagnostic()
    {
        var tree = SyntaxTree.Parse("ld a, HIGH(,1)");
        await Assert.That(tree.Diagnostics).IsNotEmpty();
    }

    [Test]
    public async Task UnaryOnFunctionCall()
    {
        var expr = GetExpressionFromImmediate("ld a, -HIGH($FF)");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.UnaryExpression);
        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[0].AsToken!.Kind).IsEqualTo(SyntaxKind.MinusToken);
        await Assert.That(children[1].AsNode!.Kind).IsEqualTo(SyntaxKind.FunctionCallExpression);
    }

    [Test]
    public async Task IsConst()
    {
        var expr = GetExpressionFromImmediate("ld a, ISCONST(MySymbol)");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.FunctionCallExpression);
        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[0].AsToken!.Kind).IsEqualTo(SyntaxKind.IsConstKeyword);
    }

    [Test]
    public async Task ExpressionAsArgument()
    {
        var expr = GetExpressionFromImmediate("ld a, HIGH($FF00 + $10)");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.FunctionCallExpression);
        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[2].AsNode!.Kind).IsEqualTo(SyntaxKind.BinaryExpression);
    }

    // =========================================================================
    // Semantic evaluation tests (builtin function results at bind time)
    // =========================================================================

    private static EmitModel Emit(string source)
    {
        var tree = SyntaxTree.Parse(source);
        return Compilation.Create(tree).Emit();
    }

    private static EmitModel EmitWithOutput(string source, out string output)
    {
        var sw = new System.IO.StringWriter();
        var tree = SyntaxTree.Parse(source);
        var model = Compilation.Create(sw, tree).Emit();
        output = sw.ToString();
        return model;
    }

    // --- RGBDS: math.asm — fixed-point arithmetic functions ---

    [Test]
    public async Task Div_PositiveByPositive()
    {
        // RGBDS: math.asm — DIV(5.0, 2.0) == 2.5
        var model = Emit("assert DIV(5.0, 2.0) == 2.5");
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Div_NegativeByPositive()
    {
        // RGBDS: math.asm — DIV(-5.0, 2.0) == -2.5
        var model = Emit("assert DIV(-5.0, 2.0) == -2.5");
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Div_ByZero_PosInfinity()
    {
        // RGBDS: math.asm — DIV(5.0, 0.0) == $7fffffff (+inf saturates to INT32_MAX)
        var model = Emit("assert DIV(5.0, 0.0) == $7fff_ffff");
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Div_NegByZero_NegInfinity()
    {
        // RGBDS: math.asm — DIV(-5.0, 0.0) == $80000000 (-inf saturates to INT32_MIN)
        var model = Emit("assert DIV(-5.0, 0.0) == $8000_0000");
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Div_ZeroByZero_IsNan()
    {
        // RGBDS: math.asm — DIV(0.0, 0.0) == 0 (nan => 0)
        var model = Emit("assert DIV(0.0, 0.0) == $0000_0000");
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Mul_TenByHalf()
    {
        // RGBDS: math.asm — MUL(10.0, 0.5) == 5.0
        var model = Emit("assert MUL(10.0, 0.5) == 5.0");
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Mul_ByZero_IsZero()
    {
        // RGBDS: math.asm — MUL(10.0, 0.0) == 0.0
        var model = Emit("assert MUL(10.0, 0.0) == 0.0");
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Fmod_PositivePositive()
    {
        // RGBDS: math.asm — FMOD(5.0, 2.0) == 1.0
        var model = Emit("assert FMOD(5.0, 2.0) == 1.0");
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Fmod_NegativePositive()
    {
        // RGBDS: math.asm — FMOD(-5.0, 2.0) == -1.0
        var model = Emit("assert FMOD(-5.0, 2.0) == -1.0");
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Fmod_ByZero_IsNan()
    {
        // RGBDS: math.asm — FMOD(5.0, 0.0) == 0 (nan)
        var model = Emit("assert FMOD(5.0, 0.0) == 0");
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Pow_SquareRoot()
    {
        // RGBDS: math.asm — POW(100.0, 0.5) == 10.0
        var model = Emit("assert POW(100.0, 0.5) == 10.0");
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Pow_TenSquared()
    {
        // RGBDS: math.asm — POW(10.0, 2.0) == 100.0
        var model = Emit("assert POW(10.0, 2.0) == 100.0");
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Log_Base10_Of100()
    {
        // RGBDS: math.asm — LOG(100.0, 10.0) == 2.0
        var model = Emit("assert LOG(100.0, 10.0) == 2.0");
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Log_Base2_Of256()
    {
        // RGBDS: math.asm — LOG(256.0, 2.0) == 8.0
        var model = Emit("assert LOG(256.0, 2.0) == 8.0");
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Round_PositiveHalf_RoundsUp()
    {
        // RGBDS: math.asm — ROUND(1.5) == 2.0
        var model = Emit("assert ROUND(1.5) == 2.0");
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Round_NegativeHalf_RoundsDown()
    {
        // RGBDS: math.asm — ROUND(-1.5) == -2.0
        var model = Emit("assert ROUND(-1.5) == -2.0");
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Ceil_PositiveFraction()
    {
        // RGBDS: math.asm — CEIL(1.5) == 2.0
        var model = Emit("assert CEIL(1.5) == 2.0");
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Ceil_NegativeFraction()
    {
        // RGBDS: math.asm — CEIL(-1.5) == -1.0
        var model = Emit("assert CEIL(-1.5) == -1.0");
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Floor_PositiveFraction()
    {
        // RGBDS: math.asm — FLOOR(1.5) == 1.0
        var model = Emit("assert FLOOR(1.5) == 1.0");
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Floor_NegativeFraction()
    {
        // RGBDS: math.asm — FLOOR(-1.5) == -2.0
        var model = Emit("assert FLOOR(-1.5) == -2.0");
        await Assert.That(model.Success).IsTrue();
    }

    // --- RGBDS: trigonometry.asm ---

    [Test]
    public async Task Sin_QuarterTurn_IsOne()
    {
        // RGBDS: trigonometry.asm — sin(0.25) == 1.0 in Q.16
        var model = Emit("""
            OPT Q.16
            assert sin(0.25) == 1.0
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Sin_Zero_IsZero()
    {
        // RGBDS: trigonometry.asm
        var model = Emit("""
            OPT Q.16
            assert sin(0.0) == 0.0
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Cos_Zero_IsOne()
    {
        // RGBDS: trigonometry.asm
        var model = Emit("""
            OPT Q.16
            assert cos(0.0) == 1.0
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Asin_One_IsQuarterTurn()
    {
        // RGBDS: trigonometry.asm — asin(1.0) == 0.25
        var model = Emit("""
            OPT Q.16
            assert asin(1.0) == 0.25
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Acos_One_IsZero()
    {
        // RGBDS: trigonometry.asm — acos(1.0) == 0.0
        var model = Emit("""
            OPT Q.16
            assert acos(1.0) == 0.0
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Tan_EighthTurn_IsOne()
    {
        // RGBDS: trigonometry.asm — tan(0.125) == 1.0 (needs Q > 2)
        var model = Emit("""
            OPT Q.16
            assert tan(0.125) == 1.0
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Atan_One_IsEighthTurn()
    {
        // RGBDS: trigonometry.asm — atan(1.0) == 0.125
        var model = Emit("""
            OPT Q.16
            assert atan(1.0) == 0.125
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Atan2_OneOne_IsEighthTurn()
    {
        // RGBDS: trigonometry.asm — atan2(1.0, 1.0) == 0.125
        var model = Emit("""
            OPT Q.16
            assert atan2(1.0, 1.0) == 0.125
            """);
        await Assert.That(model.Success).IsTrue();
    }

    // --- RGBDS: strfind-strrfind.asm ---

    [Test]
    public async Task Strfind_FoundOnce_EqualToStrrfind()
    {
        // RGBDS: strfind-strrfind.asm
        var model = Emit("""
            assert STRFIND("foo bar baz", "bar") == STRRFIND("foo bar baz", "bar")
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Strfind_FirstOccurrence()
    {
        // RGBDS: strfind-strrfind.asm — "bar" first appears at index 4
        var model = Emit("""
            assert STRFIND("foo bar bargain", "bar") == 4
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Strrfind_LastOccurrence()
    {
        // RGBDS: strfind-strrfind.asm — "bar" last appears at index 8
        var model = Emit("""
            assert STRRFIND("foo bar bargain", "bar") == 8
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Strfind_NotFound_IsNegOne()
    {
        // RGBDS: strfind-strrfind.asm
        var model = Emit("""
            assert STRFIND("foo bar", "qux") == -1
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Strrfind_NotFound_IsNegOne()
    {
        // RGBDS: strfind-strrfind.asm
        var model = Emit("""
            assert STRRFIND("foo bar", "qux") == -1
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Strfind_HaystackShorterThanNeedle_IsNegOne()
    {
        // RGBDS: strfind-strrfind.asm
        var model = Emit("""
            assert STRFIND("foo", "foobar") == -1
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Strfind_EmptyNeedle_IsZero()
    {
        // RGBDS: strfind-strrfind.asm — empty needle found at 0
        var model = Emit("""
            assert STRFIND("foobar", "") == 0
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Strrfind_EmptyNeedle_IsStrlen()
    {
        // RGBDS: strfind-strrfind.asm — empty needle rfound at STRLEN("foobar")
        var model = Emit("""
            assert STRRFIND("foobar", "") == STRLEN("foobar")
            """);
        await Assert.That(model.Success).IsTrue();
    }

    // --- RGBDS: strupr-strlwr.asm ---

    [Test]
    public async Task Strupr_ConvertsToUppercase()
    {
        // RGBDS: strupr-strlwr.asm
        var model = EmitWithOutput("""
            def foo equs strupr("xii")
            PRINTLN "foo={foo}"
            """, out var output);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(output).Contains("XII");
    }

    [Test]
    public async Task Strlwr_ConvertsToLowercase()
    {
        // RGBDS: strupr-strlwr.asm
        var model = EmitWithOutput("""
            def bar equs strlwr("LOL")
            PRINTLN "bar={bar}"
            """, out var output);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(output).Contains("lol");
    }

    // --- RGBDS: bytelen-strbyte.asm ---

    [Test]
    public async Task Bytelen_EmptyString_IsZero()
    {
        // RGBDS: bytelen-strbyte.asm
        var model = Emit("""
            assert bytelen("") == 0
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Bytelen_AsciiString()
    {
        // RGBDS: bytelen-strbyte.asm — "ABC" is 3 bytes
        var model = Emit("""
            assert bytelen("ABC") == 3
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Strbyte_FirstByte()
    {
        // RGBDS: bytelen-strbyte.asm — strbyte("ABC", 0) == $41 ('A')
        var model = Emit("""
            assert strbyte("ABC", 0) == $41
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Strbyte_NegativeIndex_FromEnd()
    {
        // RGBDS: bytelen-strbyte.asm — strbyte("ABC", -1) == $43 ('C')
        var model = Emit("""
            assert strbyte("ABC", -1) == $43
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Strbyte_OutOfBounds_ReturnsZero()
    {
        // RGBDS: bytelen-strbyte.asm — index past end returns 0 (with warning)
        var model = Emit("""
            assert strbyte("abc", 10) == 0
            """);
        // Should succeed (warning only, not error)
        await Assert.That(model.Success).IsTrue();
    }

    // --- RGBDS: charlen-strchar.asm ---

    [Test]
    public async Task Charlen_WithCustomCharmap()
    {
        // RGBDS: charlen-strchar.asm — charmap maps multi-char sequences
        var model = Emit("""
            opt Wno-unmapped-char
            charmap "Bold", $88
            charmap "A", $10
            charmap "B", $20
            charmap "C", $30
            charmap "<NULL>", $00
            SECTION "test", ROM0
            DEF S EQUS "XBold<NULL>ABC"
            assert CHARLEN("{S}") == 6
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Charlen_AsciiCharmap()
    {
        // RGBDS: charlen-strchar.asm — under ASCII charmap, each byte is one char
        var model = Emit("""
            opt Wno-unmapped-char
            charmap "Bold", $88
            charmap "A", $10
            SECTION "test", ROM0
            DEF S EQUS "XBoldA"
            newcharmap ascii
            assert CHARLEN("{S}") == 6
            """);
        await Assert.That(model.Success).IsTrue();
    }

    // --- RGBDS: incharmap.asm ---

    [Test]
    public async Task Incharmap_MappedEntry_IsTrue()
    {
        // RGBDS: incharmap.asm — charmap "a",1 → incharmap("a") is true
        var model = Emit("""
            charmap "a", 1
            assert incharmap("a")
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Incharmap_UnmappedEntry_IsFalse()
    {
        // RGBDS: incharmap.asm — case sensitive: "A" not mapped
        var model = Emit("""
            charmap "a", 1
            assert !incharmap("A")
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Incharmap_EmptyString_IsFalse()
    {
        // RGBDS: incharmap.asm
        var model = Emit("""
            charmap "a", 1
            assert !incharmap("")
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Incharmap_MulticharEntry()
    {
        // RGBDS: incharmap.asm — charmap "ab",2 → incharmap("ab") is true
        var model = Emit("""
            charmap "ab", 2
            assert incharmap("ab")
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Incharmap_AfterSetcharmap_SeesNewMap()
    {
        // RGBDS: incharmap.asm — setcharmap switches active charmap
        var model = Emit("""
            charmap "a", 1
            newcharmap second
            charmap "d", 4
            setcharmap second
            assert incharmap("d")
            assert !incharmap("a")
            """);
        await Assert.That(model.Success).IsTrue();
    }

    // --- RGBDS: readfile.asm ---

    [Test]
    public async Task Readfile_LoadsFileContent()
    {
        // RGBDS: readfile.asm — readfile() returns file contents as a string
        // Use a VFS to inject the file content
        var vfs = new VirtualFileResolver();
        vfs.AddTextFile("greet.inc", "hello world");
        var sw = new System.IO.StringWriter();
        var tree = SyntaxTree.Parse("""
            def s equs readfile("greet.inc")
            println strupr(#s) ++ "!"
            """);
        var binder = new Binder(fileResolver: vfs, printOutput: sw);
        var model = binder.BindToEmitModel(tree);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(sw.ToString()).Contains("HELLO WORLD!");
    }

    [Test]
    public async Task Readfile_WithLimit_TruncatesContent()
    {
        // RGBDS: readfile.asm — readfile("file", N) reads only first N bytes
        var vfs = new VirtualFileResolver();
        vfs.AddTextFile("greet.inc", "hello world");
        var sw = new System.IO.StringWriter();
        var tree = SyntaxTree.Parse("""
            def s equs readfile("greet.inc", 5)
            println #s ++ "?"
            """);
        var binder = new Binder(fileResolver: vfs, printOutput: sw);
        var model = binder.BindToEmitModel(tree);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(sw.ToString()).Contains("hello?");
    }

    // --- RGBDS: string-compare.asm — === and !== operators ---

    [Test]
    public async Task StringEquals_SameString_IsTrue()
    {
        // RGBDS: string-compare.asm
        var model = Emit("""
            assert "hello" === "hello"
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task StringNotEquals_DifferentStrings_IsTrue()
    {
        // RGBDS: string-compare.asm
        var model = Emit("""
            assert "hello" !== "goodbye"
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task StringConcat_WithPlusPlus()
    {
        // RGBDS: string-compare.asm — "game" ++ "boy" === "gameboy"
        var model = Emit("""
            assert "game" ++ "boy" === "gameboy"
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task StringConcat_MultiPart()
    {
        // RGBDS: string-compare.asm — three-part concatenation
        var model = Emit("""
            assert "fire flower" === "fire" ++ " " ++ "flower"
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task StringCompare_NumericFalse_IsZero()
    {
        // RGBDS: string-compare.asm — "a" === "b" evaluates to 0
        var model = Emit("""
            assert "a" === "b" == 0
            """);
        await Assert.That(model.Success).IsTrue();
    }

    // --- RGBDS: fixed-point-specific.asm ---

    [Test]
    public async Task FixedPoint_MulQ8_Result()
    {
        // RGBDS: fixed-point-specific.asm — MUL(6.0, 7.0) == 42.0 in Q.8
        var model = Emit("""
            OPT Q.8
            assert MUL(6.0, 7.0) == 42.0
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task FixedPoint_MulQ16_Result()
    {
        // RGBDS: fixed-point-specific.asm — MUL(6.0, 7.0) == 42.0 in Q.16
        var model = Emit("""
            OPT Q.16
            assert MUL(6.0, 7.0) == 42.0
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task FixedPoint_DivQ8_Result()
    {
        // RGBDS: fixed-point-specific.asm — DIV(115.625, 9.25) == 12.5 in Q.8
        var model = Emit("""
            OPT Q.8
            assert DIV(115.625, 9.25) == 12.5
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task FixedPoint_SinInQ16()
    {
        // RGBDS: fixed-point-specific.asm — sin(0.25) == 1.0 in Q.16
        var model = Emit("""
            OPT Q.16
            assert sin(0.25) == 1.0
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task FixedPoint_CosQ8_Zero()
    {
        // RGBDS: fixed-point-specific.asm — cos(0.75) == 0.0 in Q.8 (quarter-turn offset)
        var model = Emit("""
            OPT Q.8
            assert cos(0.75) == 0.0
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task FixedPoint_RoundQ8()
    {
        // RGBDS: fixed-point-specific.asm — ROUND(1.75) == 2.0
        var model = Emit("""
            OPT Q.8
            assert ROUND(1.75) == 2.0
            """);
        await Assert.That(model.Success).IsTrue();
    }

    // --- RGBDS: fixed-point-magnitude.asm ---

    [Test]
    public async Task FixedPoint_MaxValueQ16_InRange()
    {
        // RGBDS: fixed-point-magnitude.asm — in Q.16, max representable int is (1<<16)-1 = 65535
        var model = Emit("""
            OPT Q.16
            def maxValue = 65535.0
            assert maxValue != 0
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task FixedPoint_OverflowProducesWarning()
    {
        // RGBDS: fixed-point-magnitude.asm — values past the representable range produce a warning
        // In Q.16, 65536.0 overflows the integer part
        var model = Emit("""
            OPT Q.16
            def minBadValue = 65536.0
            """);
        // Should succeed (warning only, not error)
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Diagnostics.Any(d =>
            d.Severity == DiagnosticSeverity.Warning)).IsTrue();
    }
}
