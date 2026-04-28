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
}
