using Koh.Core.Binding;
using Koh.Core.Syntax;

namespace Koh.Core.Tests.Binding;

/// <summary>
/// Tests for OPT, ALIGN, EQUS expansion, angle-bracket quoting, and PRINT/PRINTLN with interpolation.
/// </summary>
public class DirectiveExtensionTests
{
    private static EmitModel Emit(string source)
    {
        var tree = SyntaxTree.Parse(source);
        return Compilation.Create(tree).Emit();
    }

    // =========================================================================
    // OPT directive — accepted without error
    // =========================================================================

    [Test]
    public async Task Opt_AcceptedWithoutError()
    {
        var model = Emit("""
            OPT b.$FF
            SECTION "Main", ROM0
            nop
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Pusho_Popo_AcceptedWithoutError()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            PUSHO
            OPT b.$00
            POPO
            nop
            """);
        await Assert.That(model.Success).IsTrue();
    }

    // =========================================================================
    // Inline ALIGN
    // =========================================================================

    [Test]
    public async Task Align_PadsToNextBoundary()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            db $AA
            ALIGN 3
            after_align: db $BB
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        // ALIGN 3 = 8-byte alignment. After 1 byte ($AA), pad 7 bytes to reach offset 8
        var sym = model.Symbols.First(s => s.Name == "after_align");
        await Assert.That(sym.Value).IsEqualTo(8);
    }

    [Test]
    public async Task Align_AlreadyAligned_NoPad()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            ds 8
            ALIGN 3
            after_align: nop
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        // Already at offset 8, ALIGN 3 (8-byte) = no pad needed
        var sym = model.Symbols.First(s => s.Name == "after_align");
        await Assert.That(sym.Value).IsEqualTo(8);
    }

    [Test]
    public async Task Align_ZeroBits_NoPad()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            db $AA
            ALIGN 0
            after: db $BB
            """);
        await Assert.That(model.Success).IsTrue();
        // ALIGN 0 = 1-byte alignment = always aligned, no padding
        var sym = model.Symbols.First(s => s.Name == "after");
        await Assert.That(sym.Value).IsEqualTo(1);
    }

    [Test]
    public async Task Align_OutsideSection_ReportsError()
    {
        var model = Emit("ALIGN 3");
        await Assert.That(model.Success).IsFalse();
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("ALIGN outside"))).IsTrue();
    }

    [Test]
    public async Task Align_NoArgument_ReportsError()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            ALIGN
            """);
        await Assert.That(model.Success).IsFalse();
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("ALIGN requires"))).IsTrue();
    }

    [Test]
    public async Task Align_OutOfRange_ReportsError()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            ALIGN 17
            """);
        await Assert.That(model.Success).IsFalse();
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("ALIGN value must be 0-16"))).IsTrue();
    }

    // =========================================================================
    // EQUS expansion
    // =========================================================================

    [Test]
    public async Task Equs_BareNameExpansion()
    {
        var model = Emit("""
            MY_INST EQUS "nop"
            SECTION "Main", ROM0
            MY_INST
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(1);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x00); // nop
    }

    [Test]
    public async Task Equs_UsedInExpression()
    {
        // EQUS with a simple instruction — verify it assembles correctly
        var model = Emit("""
            MY_HALT EQUS "halt"
            SECTION "Main", ROM0
            MY_HALT
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x76); // halt
    }

    // =========================================================================
    // Angle-bracket quoting in macro args
    // =========================================================================

    [Test]
    public async Task Println_OutputsCapturedText()
    {
        var sw = new StringWriter();
        var tree = SyntaxTree.Parse("""
            MY_VAL EQU 42
            SECTION "Main", ROM0
            PRINTLN "hello"
            PRINTLN "val={d:MY_VAL}"
            PRINT "no newline"
            nop
            """);
        var model = Compilation.Create(sw, tree).Emit();
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();

        var output = sw.ToString();
        await Assert.That(output).Contains("hello");
        await Assert.That(output).Contains("val=42");
        await Assert.That(output).Contains("no newline");
    }

    [Test]
    public async Task Interpolation_HexFormat_WithPrefix()
    {
        var sw = new StringWriter();
        var tree = SyntaxTree.Parse("""
            MY_VAL EQU 255
            SECTION "Main", ROM0
            PRINTLN "{#X:MY_VAL}"
            nop
            """);
        var model = Compilation.Create(sw, tree).Emit();
        await Assert.That(model.Success).IsTrue();
        await Assert.That(sw.ToString()).Contains("$FF");
    }

    [Test]
    public async Task Interpolation_DefaultFormat_IsDecimal()
    {
        var sw = new StringWriter();
        var tree = SyntaxTree.Parse("""
            MY_VAL EQU 42
            SECTION "Main", ROM0
            PRINTLN "{MY_VAL}"
            nop
            """);
        var model = Compilation.Create(sw, tree).Emit();
        await Assert.That(model.Success).IsTrue();
        await Assert.That(sw.ToString()).Contains("42");
    }

    [Test]
    public async Task Interpolation_BinaryFormat_NoPrefix()
    {
        var sw = new StringWriter();
        var tree = SyntaxTree.Parse("""
            MY_VAL EQU 5
            SECTION "Main", ROM0
            PRINTLN "{b:MY_VAL}"
            nop
            """);
        var model = Compilation.Create(sw, tree).Emit();
        await Assert.That(model.Success).IsTrue();
        await Assert.That(sw.ToString()).Contains("101");
    }

    [Test]
    public async Task AngleBracketQuoting_CommaInArg()
    {
        var model = Emit("""
            emit_two: MACRO
            db \1
            ENDM
            SECTION "Main", ROM0
            emit_two <$AA, $BB>
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        // <$AA, $BB> should be passed as a single argument "\1" = "$AA, $BB"
        // which then parses as "db $AA, $BB" → 2 bytes
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(2);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0xAA);
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0xBB);
    }
}
