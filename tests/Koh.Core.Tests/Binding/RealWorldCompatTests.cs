using Koh.Core.Binding;
using Koh.Core.Syntax;

namespace Koh.Core.Tests.Binding;

/// <summary>
/// Real-world compatibility tests that assemble non-trivial RGBDS-style
/// programs and verify correct output.
/// </summary>
public class RealWorldCompatTests
{
    private static EmitModel Emit(string source)
    {
        var tree = SyntaxTree.Parse(source);
        return Compilation.Create(tree).Emit();
    }

    [Test]
    public async Task MinimalGameBoyRom_AssemblesCorrectly()
    {
        var model = Emit("""
            ; Minimal Game Boy ROM header and entry point
            SECTION "Header", ROM0[$0100]
            entry_point:
                nop
                nop
                nop
                nop

            SECTION "Main", ROM0[$0150]
            start::
                ld sp, $FFFE
                ld a, 0
                ld hl, $9800
                ld [hl], a
                halt
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        var header = model.Sections.First(s => s.Name == "Header");
        await Assert.That(header.Data[0]).IsEqualTo((byte)0x00); // nop
        await Assert.That(header.Data.Length).IsEqualTo(4);
    }

    [Test]
    public async Task MacroWithEquConstants_AssemblesCorrectly()
    {
        var model = Emit("""
            ; Common RGBDS pattern: constants + macros
            SCREEN_W EQU 160
            SCREEN_H EQU 144
            TILE_SIZE EQU 8

            set_reg: MACRO
                ld \1, \2
            ENDM

            SECTION "Main", ROM0
            start::
                set_reg a, SCREEN_W / TILE_SIZE
                set_reg b, SCREEN_H / TILE_SIZE
                nop
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        // ld a, 20 (SCREEN_W/TILE_SIZE = 160/8 = 20 = $14)
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x3E); // ld a, n8
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)20);   // 160/8
        // ld b, 18 (SCREEN_H/TILE_SIZE = 144/8 = 18 = $12)
        await Assert.That(model.Sections[0].Data[2]).IsEqualTo((byte)0x06); // ld b, n8
        await Assert.That(model.Sections[0].Data[3]).IsEqualTo((byte)18);   // 144/8
    }

    [Test]
    public async Task RsCounterStruct_AssemblesCorrectly()
    {
        var model = Emit("""
            ; RS counter pattern for struct-like layouts
            RSRESET
            ENTITY_X      RB 1
            ENTITY_Y      RB 1
            ENTITY_SPEED  RB 1
            ENTITY_HP     RW 1
            ENTITY_SIZE   RB 0

            SECTION "Main", ROM0
                db ENTITY_X
                db ENTITY_Y
                db ENTITY_SPEED
                db ENTITY_HP
                db ENTITY_SIZE
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0);  // X = 0
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)1);  // Y = 1
        await Assert.That(model.Sections[0].Data[2]).IsEqualTo((byte)2);  // SPEED = 2
        await Assert.That(model.Sections[0].Data[3]).IsEqualTo((byte)3);  // HP = 3
        await Assert.That(model.Sections[0].Data[4]).IsEqualTo((byte)5);  // SIZE = 5
    }

    [Test]
    public async Task ConditionalAssembly_MultiPlatform()
    {
        var model = Emit("""
            ; Conditional assembly for GBC vs DMG
            GBC_MODE EQU 1

            SECTION "Main", ROM0
            IF GBC_MODE
                ld a, $80
            ELSE
                ld a, $00
            ENDC
                nop
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x3E); // ld a, n8
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0x80); // GBC mode
    }

    [Test]
    public async Task ReptDataTable_GeneratesCorrectBytes()
    {
        var model = Emit("""
            SECTION "Data", ROM0
            FOR I, 0, 8
                db I * 2
            ENDR
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(8);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0);
        await Assert.That(model.Sections[0].Data[3]).IsEqualTo((byte)6);
        await Assert.That(model.Sections[0].Data[7]).IsEqualTo((byte)14);
    }

    [Test]
    public async Task CharMapCustomEncoding_AssemblesCorrectly()
    {
        var model = Emit("""
            ; Custom character encoding for dialogue
            CHARMAP "A", $80
            CHARMAP "B", $81
            CHARMAP " ", $00

            SECTION "Strings", ROM0
                db "A B"
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x80); // A
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0x00); // space
        await Assert.That(model.Sections[0].Data[2]).IsEqualTo((byte)0x81); // B
    }

    [Test]
    public async Task EqusExpansion_WithPrintln()
    {
        var model = Emit("""
            MY_INSTR EQUS "nop"
            SECTION "Main", ROM0
            MY_INSTR
            MY_INSTR
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(2);
    }

    [Test]
    public async Task StringInterpolation_InPrintln()
    {
        var model = Emit("""
            MY_VAL EQU 42
            SECTION "Main", ROM0
            PRINTLN "Value is {d:MY_VAL}"
            PRINTLN "Default: {MY_VAL}"
            PRINTLN "Hex: {#X:MY_VAL}"
            nop
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        // No error diagnostics — interpolation resolved successfully
        await Assert.That(model.Diagnostics.All(d =>
            d.Severity != Koh.Core.Diagnostics.DiagnosticSeverity.Error)).IsTrue();
    }
}
