using Koh.Core.Binding;
using Koh.Core.Syntax;

namespace Koh.Core.Tests.Binding;

public class RsCounterTests
{
    private static EmitModel Emit(string source)
    {
        var tree = SyntaxTree.Parse(source);
        return Compilation.Create(tree).Emit();
    }

    [Test]
    public async Task Rb_DefinesConstantAtRsCounter()
    {
        var model = Emit("""
            RSRESET
            foo RB 1
            bar RB 1
            SECTION "Main", ROM0
            db foo
            db bar
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0); // foo = 0
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)1); // bar = 1
    }

    [Test]
    public async Task Rw_AdvancesByTwoPerUnit()
    {
        var model = Emit("""
            RSRESET
            x RW 1
            y RB 1
            SECTION "Main", ROM0
            db x
            db y
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0); // x = 0
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)2); // y = 2 (RW advances by 2)
    }

    [Test]
    public async Task Rsset_SetsCounterToValue()
    {
        var model = Emit("""
            RSSET $10
            foo RB 1
            SECTION "Main", ROM0
            db foo
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x10);
    }

    [Test]
    public async Task Rsreset_ResetsCounterToZero()
    {
        var model = Emit("""
            RSSET $10
            val1 RB 1
            RSRESET
            val2 RB 1
            SECTION "Main", ROM0
            db val1
            db val2
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x10); // val1 = $10
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0);    // val2 = 0
    }

    [Test]
    public async Task Rb_MultipleBytes()
    {
        var model = Emit("""
            RSRESET
            off1 RB 4
            off2 RB 2
            SECTION "Main", ROM0
            db off1
            db off2
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0); // off1 = 0
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)4); // off2 = 4
    }

    [Test]
    public async Task Rw_MultipleWords()
    {
        var model = Emit("""
            RSRESET
            off1 RW 2
            off2 RB 1
            SECTION "Main", ROM0
            db off1
            db off2
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0); // off1 = 0
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)4); // off2 = 4 (RW 2 = 4 bytes)
    }

    [Test]
    public async Task Rs_UsableInIfCondition()
    {
        var model = Emit("""
            RSRESET
            x RB 1
            IF x == 0
            SECTION "Main", ROM0
            db $AA
            ENDC
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0xAA);
    }

    [Test]
    public async Task Rl_AdvancesByFourPerUnit()
    {
        var model = Emit("""
            RSRESET
            ptr RW 1
            val RL 1
            next RB 1
            SECTION "Main", ROM0
            db ptr
            db val
            db next
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0);  // ptr = 0
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)2);  // val = 2 (RW advances by 2)
        await Assert.That(model.Sections[0].Data[2]).IsEqualTo((byte)6);  // next = 6 (RL advances by 4)
    }

    [Test]
    public async Task Rb_ZeroCount_SameOffset()
    {
        var model = Emit("""
            RSRESET
            first RB 0
            second RB 1
            SECTION "Main", ROM0
            db first
            db second
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0); // first = 0
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0); // second = 0 (no advance)
    }

    [Test]
    public async Task Rw_ZeroCount_SameOffset()
    {
        var model = Emit("""
            RSRESET
            first RW 0
            second RB 1
            SECTION "Main", ROM0
            db first
            db second
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0); // first = 0
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0); // second = 0 (no advance)
    }

    [Test]
    public async Task Rsset_NoValue_ReportsError()
    {
        var model = Emit("""
            RSSET
            SECTION "Main", ROM0
            nop
            """);
        await Assert.That(model.Success).IsFalse();
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("RSSET requires a value"))).IsTrue();
    }

    [Test]
    public async Task Rsset_WithExpression()
    {
        var model = Emit("""
            RSSET $20
            foo RB 3
            bar RB 1
            SECTION "Main", ROM0
            db foo
            db bar
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x20); // foo = $20
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0x23); // bar = $23
    }
}
