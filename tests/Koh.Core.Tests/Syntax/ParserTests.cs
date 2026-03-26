using Koh.Core.Syntax;

namespace Koh.Core.Tests.Syntax;

public class ParserTests
{
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
}
