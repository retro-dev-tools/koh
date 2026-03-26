using Koh.Core.Binding;
using Koh.Core.Symbols;
using Koh.Core.Syntax;

namespace Koh.Core.Tests.Integration;

/// <summary>
/// End-to-end tests: source text → parse → compile → emit → verify bytes.
/// </summary>
public class SingleFileAssemblyTests
{
    [Test]
    public async Task Assemble_SimpleProgram()
    {
        var source = """
            SECTION "Main", ROM0
            main:
                nop
                ld a, $42
                halt
            """;

        var tree = SyntaxTree.Parse(source);
        await Assert.That(tree.Diagnostics).IsEmpty();

        var compilation = Compilation.Create(tree);
        var emit = compilation.Emit();

        await Assert.That(emit.Success).IsTrue();
        await Assert.That(emit.Diagnostics).IsEmpty();
        await Assert.That(emit.Sections.Count).IsEqualTo(1);

        var section = emit.Sections[0];
        await Assert.That(section.Name).IsEqualTo("Main");
        await Assert.That(section.Type).IsEqualTo(SectionType.Rom0);

        // nop=0x00, ld a,$42=0x3E 0x42, halt=0x76
        await Assert.That(section.Data.Length).IsEqualTo(4);
        await Assert.That(section.Data[0]).IsEqualTo((byte)0x00);
        await Assert.That(section.Data[1]).IsEqualTo((byte)0x3E);
        await Assert.That(section.Data[2]).IsEqualTo((byte)0x42);
        await Assert.That(section.Data[3]).IsEqualTo((byte)0x76);

        // main label at PC 0
        var mainSym = emit.Symbols.FirstOrDefault(s => s.Name == "main");
        await Assert.That(mainSym).IsNotNull();
        await Assert.That(mainSym!.Kind).IsEqualTo(SymbolKind.Label);
        await Assert.That(mainSym.Value).IsEqualTo(0);
    }

    [Test]
    public async Task Assemble_WithConstants()
    {
        var source = """
            SCREEN_WIDTH EQU 160
            SCREEN_HEIGHT EQU 144
            SECTION "Main", ROM0
                ld a, SCREEN_WIDTH
                ld b, SCREEN_HEIGHT
            """;

        var compilation = Compilation.Create(SyntaxTree.Parse(source));
        var emit = compilation.Emit();

        await Assert.That(emit.Success).IsTrue();

        var section = emit.Sections[0];
        await Assert.That(section.Data.Length).IsEqualTo(4);
        // ld a, 160 = 0x3E 0xA0
        await Assert.That(section.Data[0]).IsEqualTo((byte)0x3E);
        await Assert.That(section.Data[1]).IsEqualTo((byte)160);
        // ld b, 144 = 0x06 0x90
        await Assert.That(section.Data[2]).IsEqualTo((byte)0x06);
        await Assert.That(section.Data[3]).IsEqualTo((byte)144);
    }

    [Test]
    public async Task Assemble_WithDataSection()
    {
        var source = """
            SECTION "Code", ROM0
                nop
            SECTION "Data", ROM0
            my_data:
                db $01, $02, $03
                dw $1234
            """;

        var compilation = Compilation.Create(SyntaxTree.Parse(source));
        var emit = compilation.Emit();

        await Assert.That(emit.Success).IsTrue();
        await Assert.That(emit.Sections.Count).IsEqualTo(2);

        var dataSec = emit.Sections.First(s => s.Name == "Data");
        // db $01, $02, $03 = 01 02 03
        // dw $1234 = 34 12 (little-endian)
        await Assert.That(dataSec.Data.Length).IsEqualTo(5);
        await Assert.That(dataSec.Data[0]).IsEqualTo((byte)0x01);
        await Assert.That(dataSec.Data[1]).IsEqualTo((byte)0x02);
        await Assert.That(dataSec.Data[2]).IsEqualTo((byte)0x03);
        await Assert.That(dataSec.Data[3]).IsEqualTo((byte)0x34);
        await Assert.That(dataSec.Data[4]).IsEqualTo((byte)0x12);
    }

    [Test]
    public async Task Assemble_ForwardReference_Resolved()
    {
        var source = """
            SECTION "Main", ROM0
                dw end_label
                nop
            end_label:
                halt
            """;

        var compilation = Compilation.Create(SyntaxTree.Parse(source));
        var emit = compilation.Emit();

        await Assert.That(emit.Success).IsTrue();

        var section = emit.Sections[0];
        // dw end_label = 2 bytes, nop = 1 byte, halt = 1 byte
        // end_label is at offset 3 (dw=2 + nop=1)
        await Assert.That(section.Data.Length).IsEqualTo(4);
        await Assert.That(section.Data[0]).IsEqualTo((byte)0x03); // low byte of 3
        await Assert.That(section.Data[1]).IsEqualTo((byte)0x00); // high byte of 3
        await Assert.That(section.Data[2]).IsEqualTo((byte)0x00); // nop
        await Assert.That(section.Data[3]).IsEqualTo((byte)0x76); // halt
        // Patch should be resolved
        await Assert.That(section.Patches.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Assemble_ExportedSymbols()
    {
        var source = """
            SECTION "Main", ROM0
            main::
                nop
            helper:
                halt
            EXPORT helper
            """;

        var compilation = Compilation.Create(SyntaxTree.Parse(source));
        var emit = compilation.Emit();

        await Assert.That(emit.Success).IsTrue();

        var mainSym = emit.Symbols.First(s => s.Name == "main");
        await Assert.That(mainSym.Visibility).IsEqualTo(SymbolVisibility.Exported);

        var helperSym = emit.Symbols.First(s => s.Name == "helper");
        await Assert.That(helperSym.Visibility).IsEqualTo(SymbolVisibility.Exported);
    }

    [Test]
    public async Task Assemble_LocalLabels()
    {
        var source = """
            SECTION "Main", ROM0
            funcA:
                nop
            .loop:
                nop
            funcB:
                nop
            .loop:
                halt
            """;

        var compilation = Compilation.Create(SyntaxTree.Parse(source));
        var emit = compilation.Emit();

        await Assert.That(emit.Success).IsTrue();

        // Both .loop labels should exist with different qualified names
        var loopA = emit.Symbols.FirstOrDefault(s => s.Name == "funcA.loop");
        var loopB = emit.Symbols.FirstOrDefault(s => s.Name == "funcB.loop");
        await Assert.That(loopA).IsNotNull();
        await Assert.That(loopB).IsNotNull();
        // funcA at 0, .loop at 1 (after nop), funcB at 2, .loop at 3
        await Assert.That(loopA!.Value).IsEqualTo(1);
        await Assert.That(loopB!.Value).IsEqualTo(3);
    }

    [Test]
    public async Task Assemble_CbPrefixInstructions()
    {
        var source = """
            SECTION "Main", ROM0
                rlc a
                swap b
                bit 3, a
                set 7, [hl]
                res 0, c
            """;

        var compilation = Compilation.Create(SyntaxTree.Parse(source));
        var emit = compilation.Emit();

        await Assert.That(emit.Success).IsTrue();

        var data = emit.Sections[0].Data;
        await Assert.That(data.Length).IsEqualTo(10);
        // rlc a = CB 07
        await Assert.That(data[0]).IsEqualTo((byte)0xCB);
        await Assert.That(data[1]).IsEqualTo((byte)0x07);
        // swap b = CB 30
        await Assert.That(data[2]).IsEqualTo((byte)0xCB);
        await Assert.That(data[3]).IsEqualTo((byte)0x30);
        // bit 3, a = CB 5F
        await Assert.That(data[4]).IsEqualTo((byte)0xCB);
        await Assert.That(data[5]).IsEqualTo((byte)0x5F);
        // set 7, [hl] = CB FE
        await Assert.That(data[6]).IsEqualTo((byte)0xCB);
        await Assert.That(data[7]).IsEqualTo((byte)0xFE);
        // res 0, c = CB 81
        await Assert.That(data[8]).IsEqualTo((byte)0xCB);
        await Assert.That(data[9]).IsEqualTo((byte)0x81);
    }

    [Test]
    public async Task Assemble_SemanticModel_Available()
    {
        var source = """
            MY_CONST EQU $42
            SECTION "Main", ROM0
            main:
                ld a, MY_CONST
            """;

        var tree = SyntaxTree.Parse(source);
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);

        // GetDeclaredSymbol works
        var label = tree.Root.ChildNodes().First(n => n.Kind == SyntaxKind.LabelDeclaration);
        var sym = model.GetDeclaredSymbol(label);
        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Name).IsEqualTo("main");

        // LookupSymbols includes both
        var allSymbols = model.LookupSymbols(0).ToList();
        await Assert.That(allSymbols.Any(s => s.Name == "MY_CONST")).IsTrue();
        await Assert.That(allSymbols.Any(s => s.Name == "main")).IsTrue();
    }

    [Test]
    public async Task Assemble_JrForwardRef_Resolved()
    {
        var source = """
            SECTION "Main", ROM0
                jr end
                nop
            end:
                halt
            """;

        var compilation = Compilation.Create(SyntaxTree.Parse(source));
        var emit = compilation.Emit();

        await Assert.That(emit.Success).IsTrue();

        var data = emit.Sections[0].Data;
        await Assert.That(data.Length).IsEqualTo(4); // jr(2) + nop(1) + halt(1)
        await Assert.That(data[0]).IsEqualTo((byte)0x18); // JR opcode
        // end at offset 3, PC after JR = 2, offset = 3 - 2 = 1
        await Assert.That(data[1]).IsEqualTo((byte)0x01);
        await Assert.That(data[2]).IsEqualTo((byte)0x00); // nop
        await Assert.That(data[3]).IsEqualTo((byte)0x76); // halt
        await Assert.That(emit.Sections[0].Patches.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Assemble_DbForwardRef_Resolved()
    {
        var source = """
            SECTION "Main", ROM0
                db target
                nop
            target:
                halt
            """;

        var compilation = Compilation.Create(SyntaxTree.Parse(source));
        var emit = compilation.Emit();

        await Assert.That(emit.Success).IsTrue();

        var data = emit.Sections[0].Data;
        await Assert.That(data.Length).IsEqualTo(3); // db(1) + nop(1) + halt(1)
        // target at offset 2
        await Assert.That(data[0]).IsEqualTo((byte)0x02);
        await Assert.That(emit.Sections[0].Patches.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Assemble_InstructionOutsideSection_Diagnostic()
    {
        var source = "nop";
        var compilation = Compilation.Create(SyntaxTree.Parse(source));
        var emit = compilation.Emit();

        await Assert.That(emit.Success).IsFalse();
        await Assert.That(emit.Diagnostics.Any(d => d.Message.Contains("outside"))).IsTrue();
    }

    [Test]
    public async Task Assemble_DsDirective()
    {
        var source = """
            SECTION "Data", ROM0
                ds 4, $FF
            """;

        var compilation = Compilation.Create(SyntaxTree.Parse(source));
        var emit = compilation.Emit();

        await Assert.That(emit.Success).IsTrue();
        var data = emit.Sections[0].Data;
        await Assert.That(data.Length).IsEqualTo(4);
        await Assert.That(data[0]).IsEqualTo((byte)0xFF);
        await Assert.That(data[3]).IsEqualTo((byte)0xFF);
    }

    [Test]
    public async Task Assemble_ArithmeticEqu()
    {
        var source = """
            BASE EQU $C000
            OFFSET EQU $10
            SECTION "Main", ROM0
                ld hl, BASE + OFFSET
            """;

        var compilation = Compilation.Create(SyntaxTree.Parse(source));
        var emit = compilation.Emit();

        await Assert.That(emit.Success).IsTrue();
        var data = emit.Sections[0].Data;
        await Assert.That(data.Length).IsEqualTo(3);
        // ld hl, $C010 = 0x21 0x10 0xC0
        await Assert.That(data[0]).IsEqualTo((byte)0x21);
        await Assert.That(data[1]).IsEqualTo((byte)0x10);
        await Assert.That(data[2]).IsEqualTo((byte)0xC0);
    }
}
