using Koh.Core.Syntax;

namespace Koh.Core.Tests.Syntax;

public class ParserTests
{
    // Walk the tree depth-first, returning the first node with the given kind.
    private static SyntaxNode? FindFirst(SyntaxNode root, SyntaxKind kind)
    {
        if (root.Kind == kind) return root;
        foreach (var child in root.ChildNodes())
        {
            var found = FindFirst(child, kind);
            if (found != null) return found;
        }
        return null;
    }

    [Test]
    public async Task Parser_Nop()
    {
        var tree = SyntaxTree.Parse("nop");
        var root = tree.Root;

        await Assert.That(root.Kind).IsEqualTo(SyntaxKind.CompilationUnit);
        var statements = root.ChildNodes().ToList();
        await Assert.That(statements).HasCount().EqualTo(1);
        await Assert.That(statements[0].Kind).IsEqualTo(SyntaxKind.InstructionStatement);
    }

    [Test]
    public async Task Parser_LdAB()
    {
        var tree = SyntaxTree.Parse("ld a, b");
        var root = tree.Root;
        var stmt = root.ChildNodes().First();

        await Assert.That(stmt.Kind).IsEqualTo(SyntaxKind.InstructionStatement);
        var tokens = stmt.ChildTokens().ToList();
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.LdKeyword);
    }

    [Test]
    public async Task Parser_NoDiagnostics_ForValidInput()
    {
        var tree = SyntaxTree.Parse("nop");
        await Assert.That(tree.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task Parser_ProducesTree_ForInvalidInput()
    {
        var tree = SyntaxTree.Parse("??? invalid");
        await Assert.That(tree.Root).IsNotNull();
        await Assert.That(tree.Diagnostics).IsNotEmpty();
    }

    [Test]
    public async Task Parser_MultipleStatements()
    {
        var tree = SyntaxTree.Parse("nop\nnop");
        var statements = tree.Root.ChildNodes().ToList();
        await Assert.That(statements).HasCount().EqualTo(2);
    }

    [Test]
    public async Task Parser_ArithmeticInstructions()
    {
        foreach (var instr in new[] { "sub a", "adc a", "sbc a", "and a", "or a", "xor a", "cp a", "inc b", "dec b" })
        {
            var tree = SyntaxTree.Parse(instr);
            var stmts = tree.Root.ChildNodes().ToList();
            await Assert.That(stmts).HasCount().EqualTo(1);
            await Assert.That(stmts[0].Kind).IsEqualTo(SyntaxKind.InstructionStatement);
        }
    }

    [Test]
    public async Task Parser_ControlFlowInstructions()
    {
        foreach (var instr in new[] { "jp $1234", "jr .loop", "call $0100", "ret", "reti", "rst $38" })
        {
            var tree = SyntaxTree.Parse(instr);
            var stmts = tree.Root.ChildNodes().ToList();
            await Assert.That(stmts).HasCount().EqualTo(1);
            await Assert.That(stmts[0].Kind).IsEqualTo(SyntaxKind.InstructionStatement);
        }
    }

    [Test]
    public async Task Parser_RotateShiftBitInstructions()
    {
        foreach (var instr in new[] { "rlca", "rla", "rrca", "rra", "rlc a", "rl a", "rrc a", "rr a",
            "sla a", "sra a", "srl a", "swap a", "bit 3, a", "set 3, a", "res 3, a" })
        {
            var tree = SyntaxTree.Parse(instr);
            var stmts = tree.Root.ChildNodes().ToList();
            await Assert.That(stmts).HasCount().EqualTo(1);
            await Assert.That(stmts[0].Kind).IsEqualTo(SyntaxKind.InstructionStatement);
        }
    }

    [Test]
    public async Task Parser_MiscInstructions()
    {
        foreach (var instr in new[] { "daa", "cpl", "di", "ei", "halt", "stop", "ccf", "scf",
            "push hl", "pop bc", "ldi", "ldd" })
        {
            var tree = SyntaxTree.Parse(instr);
            var stmts = tree.Root.ChildNodes().ToList();
            await Assert.That(stmts).HasCount().EqualTo(1);
            await Assert.That(stmts[0].Kind).IsEqualTo(SyntaxKind.InstructionStatement);
        }
    }

    // -------------------------------------------------------------------------
    // Expression precedence
    // -------------------------------------------------------------------------

    // a | b ^ c  must parse as  a | (b ^ c)  not  (a | b) ^ c
    // because ^ (XOR, level 6) binds tighter than | (OR, level 5).
    // The outermost BinaryExpression operator must be |.
    [Test]
    public async Task Expression_PipeXor_XorBindsTighter()
    {
        // ld a, 1 | 2 ^ 3  →  ld a, (1 | (2 ^ 3))
        // The ImmediateOperand's child BinaryExpression should have | as its operator,
        // with the right child being another BinaryExpression for ^.
        var tree = SyntaxTree.Parse("ld a, 1 | 2 ^ 3");
        await Assert.That(tree.Diagnostics).IsEmpty();

        var stmt = tree.Root.ChildNodes().First();
        // Operands are child nodes of InstructionStatement.
        // Children: ld, RegisterOperand(a), comma(token), ImmediateOperand(BinaryExpr)
        var operands = stmt.ChildNodes().ToList();
        // operands[0] = RegisterOperand, operands[1] = ImmediateOperand
        await Assert.That(operands).HasCount().EqualTo(2);
        var immediate = operands[1];
        await Assert.That(immediate.Kind).IsEqualTo(SyntaxKind.ImmediateOperand);

        var outerBinary = immediate.ChildNodes().First();
        await Assert.That(outerBinary.Kind).IsEqualTo(SyntaxKind.BinaryExpression);

        // Outer BinaryExpression children: left(LiteralExpression), op(PipeToken), right(BinaryExpression)
        var outerTokens = outerBinary.ChildTokens().ToList();
        await Assert.That(outerTokens.Any(t => t.Kind == SyntaxKind.PipeToken)).IsTrue();

        // The right-hand child of the outer binary must itself be a BinaryExpression (for ^)
        var innerBinary = outerBinary.ChildNodes().Last();
        await Assert.That(innerBinary.Kind).IsEqualTo(SyntaxKind.BinaryExpression);
        var innerTokens = innerBinary.ChildTokens().ToList();
        await Assert.That(innerTokens.Any(t => t.Kind == SyntaxKind.CaretToken)).IsTrue();
    }

    // a ^ b | c  must parse as  (a ^ b) | c  — XOR consumes its right operand before | sees it.
    [Test]
    public async Task Expression_XorPipe_XorBindsTighter()
    {
        var tree = SyntaxTree.Parse("ld a, 1 ^ 2 | 3");
        await Assert.That(tree.Diagnostics).IsEmpty();

        var stmt = tree.Root.ChildNodes().First();
        var operands = stmt.ChildNodes().ToList();
        var immediate = operands[1];
        var outerBinary = immediate.ChildNodes().First();

        // Outer operator must be |
        var outerTokens = outerBinary.ChildTokens().ToList();
        await Assert.That(outerTokens.Any(t => t.Kind == SyntaxKind.PipeToken)).IsTrue();

        // Left child must be a BinaryExpression for ^
        var leftBinary = outerBinary.ChildNodes().First();
        await Assert.That(leftBinary.Kind).IsEqualTo(SyntaxKind.BinaryExpression);
        var leftTokens = leftBinary.ChildTokens().ToList();
        await Assert.That(leftTokens.Any(t => t.Kind == SyntaxKind.CaretToken)).IsTrue();
    }

    // Left-associativity: 1 - 2 - 3 = (1 - 2) - 3.
    // The outer BinaryExpression must have a BinaryExpression as its LEFT child (not right).
    [Test]
    public async Task Expression_SubtractChain_IsLeftAssociative()
    {
        var tree = SyntaxTree.Parse("ld a, 1 - 2 - 3");
        await Assert.That(tree.Diagnostics).IsEmpty();

        var stmt = tree.Root.ChildNodes().First();
        var immediate = stmt.ChildNodes().ToList()[1];
        var outerBinary = immediate.ChildNodes().First();
        await Assert.That(outerBinary.Kind).IsEqualTo(SyntaxKind.BinaryExpression);

        // Left child should be a BinaryExpression, right child should be a LiteralExpression
        var children = outerBinary.ChildNodes().ToList();
        await Assert.That(children[0].Kind).IsEqualTo(SyntaxKind.BinaryExpression);
        await Assert.That(children[1].Kind).IsEqualTo(SyntaxKind.LiteralExpression);
    }

    // -------------------------------------------------------------------------
    // Lexer: & ambiguity — octal prefix vs bitwise-AND operator
    // -------------------------------------------------------------------------

    [Test]
    public async Task Lexer_AmpersandWithSpace_IsAmpersandToken()
    {
        // "a & 7" — space before 7 means & is the AND operator, not &7 octal
        var tree = SyntaxTree.Parse("ld a, a & 7");
        await Assert.That(tree.Diagnostics).IsEmpty();

        // The expression inside ImmediateOperand must be a BinaryExpression with & as operator
        var stmt = tree.Root.ChildNodes().First();
        var operands = stmt.ChildNodes().ToList();
        var immediate = operands[1];
        var binary = immediate.ChildNodes().First();
        await Assert.That(binary.Kind).IsEqualTo(SyntaxKind.BinaryExpression);
        var tokens = binary.ChildTokens().ToList();
        await Assert.That(tokens.Any(t => t.Kind == SyntaxKind.AmpersandToken)).IsTrue();
    }

    [Test]
    public async Task Lexer_AmpersandNoSpace_IsAmpersandToken()
    {
        // "a &7" — no space; without the fix this would lex as AKeyword + NumberLiteral("&7")
        // With the fix, it must be AKeyword + AmpersandToken + NumberLiteral("7")
        var tree = SyntaxTree.Parse("ld a, a &7");
        await Assert.That(tree.Diagnostics).IsEmpty();

        var stmt = tree.Root.ChildNodes().First();
        var operands = stmt.ChildNodes().ToList();
        var immediate = operands[1];
        var binary = immediate.ChildNodes().First();
        await Assert.That(binary.Kind).IsEqualTo(SyntaxKind.BinaryExpression);
        var tokens = binary.ChildTokens().ToList();
        await Assert.That(tokens.Any(t => t.Kind == SyntaxKind.AmpersandToken)).IsTrue();
    }

    [Test]
    public async Task Lexer_OctalLiteral_AtExpressionStart_IsNumberLiteral()
    {
        // "&17" at the start of an operand — pure octal literal, no preceding expression token
        var tree = SyntaxTree.Parse("ld a, &17");
        await Assert.That(tree.Diagnostics).IsEmpty();

        var stmt = tree.Root.ChildNodes().First();
        var operands = stmt.ChildNodes().ToList();
        var immediate = operands[1];
        // Should be ImmediateOperand → LiteralExpression, with no BinaryExpression
        var expr = immediate.ChildNodes().First();
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.LiteralExpression);
        var litToken = expr.ChildTokens().First();
        await Assert.That(litToken.Kind).IsEqualTo(SyntaxKind.NumberLiteral);
        await Assert.That(litToken.Text).IsEqualTo("&17");
    }

    [Test]
    public async Task Lexer_OctalAfterOpenParen_IsNumberLiteral()
    {
        // "(&7)" — & follows (, so it must still be an octal literal
        var tree = SyntaxTree.Parse("ld a, (&7)");
        await Assert.That(tree.Diagnostics).IsEmpty();

        var stmt = tree.Root.ChildNodes().First();
        var operands = stmt.ChildNodes().ToList();
        var immediate = operands[1];
        var paren = immediate.ChildNodes().First();
        await Assert.That(paren.Kind).IsEqualTo(SyntaxKind.ParenthesizedExpression);
        var inner = paren.ChildNodes().First();
        await Assert.That(inner.Kind).IsEqualTo(SyntaxKind.LiteralExpression);
        var litToken = inner.ChildTokens().First();
        await Assert.That(litToken.Kind).IsEqualTo(SyntaxKind.NumberLiteral);
        await Assert.That(litToken.Text).IsEqualTo("&7");
    }
}
