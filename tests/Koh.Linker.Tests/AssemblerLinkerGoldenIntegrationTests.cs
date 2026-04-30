using Koh.Core;
using Koh.Core.Binding;
using Koh.Core.Syntax;
using Koh.Core.Text;
using Koh.Linker.Core;

namespace Koh.Linker.Tests;

public class AssemblerLinkerGoldenIntegrationTests
{
    private static LinkResult AssembleAndLink(string path, string source)
    {
        var tree = SyntaxTree.Parse(SourceText.From(source, path));
        var model = Compilation.Create(tree).Emit();
        if (!model.Success)
            throw new InvalidOperationException(
                $"assemble failed: {string.Join("; ", model.Diagnostics.Select(d => d.Message))}");

        var result = new Koh.Linker.Core.Linker().Link([new LinkerInput(path, model)]);
        if (!result.Success)
            throw new InvalidOperationException(
                $"link failed: {string.Join("; ", result.Diagnostics.Select(d => d.Message))}");

        return result;
    }

    [Test]
    public async Task AssembleLink_GoldenBytes_CoverCharmapInterpolationAndExplicitLocalJump()
    {
        var result = AssembleAndLink("golden.asm", """
            NEWCHARMAP mte
            CHARMAP "One", $80, $00
            CHARMAP "Four", $80, $01
            SETCHARMAP mte

            MACRO entry
                REDEF _TEXT EQUS REVCHAR(\1, \2)
                db STRLEN("{_TEXT}")
            ENDM

            SECTION "Main", ROM0[$0100]
            Start:
                jr Start.child
                nop
            .child:
                entry $80, $00
                entry $80, $01
            """);

        var rom = result.RomData!;
        await Assert.That(rom[0x0100]).IsEqualTo((byte)0x18);
        await Assert.That(rom[0x0101]).IsEqualTo((byte)0x01);
        await Assert.That(rom[0x0102]).IsEqualTo((byte)0x00);
        await Assert.That(rom[0x0103]).IsEqualTo((byte)0x03);
        await Assert.That(rom[0x0104]).IsEqualTo((byte)0x04);
    }

    [Test]
    public async Task CrossSectionAbsoluteRef_EmitsCorrectJpTarget()
    {
        // Reset section pinned at $0100; Boot section floats and is placed at $0104.
        // The `jp Boot` opcode must encode Boot's actual placed address ($0104), not $0000.
        var result = AssembleAndLink("cross.asm", """
            SECTION "Reset", ROM0[$0100]
            EntryPoint:
                nop
                jp Boot

            SECTION "Boot", ROM0
            Boot:
            .halt:
                halt
                jr .halt
            """);

        var rom = result.RomData!;
        await Assert.That(rom[0x0100]).IsEqualTo((byte)0x00); // nop
        await Assert.That(rom[0x0101]).IsEqualTo((byte)0xC3); // jp
        await Assert.That(rom[0x0102]).IsEqualTo((byte)0x04); // low(Boot)
        await Assert.That(rom[0x0103]).IsEqualTo((byte)0x01); // high(Boot) → $0104
    }

    [Test]
    public async Task FixedSectionLabel_SymbolAtFixedAddress()
    {
        // EntryPoint is the first byte of a section pinned at $0100.
        // Its absolute address must be exactly $0100, not double-counted.
        var result = AssembleAndLink("entry.asm", """
            SECTION "Reset", ROM0[$0100]
            EntryPoint:
                nop
            """);

        var entry = result.Symbols.Single(s => s.Name == "EntryPoint");
        await Assert.That(entry.AbsoluteAddress).IsEqualTo(0x0100);
    }

    [Test]
    public async Task WramFixedSectionLabel_SymbolAtFixedAddress()
    {
        // wBoard at the start of WRAM0[$C000] must report $C000, not $8000
        // (the previous double-counted bug result).
        var result = AssembleAndLink("wram.asm", """
            SECTION "Vars", WRAM0[$C000]
            wBoard: ds 16

            SECTION "Stub", ROM0[$0100]
            Start:
                nop
            """);

        var sym = result.Symbols.Single(s => s.Name == "wBoard");
        await Assert.That(sym.AbsoluteAddress).IsEqualTo(0xC000);
    }

    [Test]
    public async Task IntraSectionRelativeJump_StillWorks()
    {
        var result = AssembleAndLink("rel.asm", """
            SECTION "Main", ROM0[$0100]
            Start:
                nop
                jr Start
            """);

        // Start at $0100, after `nop` PC=$0101, after `jr Start` PC=$0103,
        // jr offset = $0100 - $0103 = -3 = $FD
        await Assert.That(result.RomData![0x0100]).IsEqualTo((byte)0x00); // nop
        await Assert.That(result.RomData![0x0101]).IsEqualTo((byte)0x18); // jr
        await Assert.That(result.RomData![0x0102]).IsEqualTo((byte)0xFD); // -3
    }
}
