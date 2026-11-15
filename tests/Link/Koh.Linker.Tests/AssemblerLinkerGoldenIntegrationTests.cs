using Koh.Common;
using Koh.Native;
using Koh.Native.Syntax;
using Koh.Objects;

namespace Koh.Linker.Tests;

public class AssemblerLinkerGoldenIntegrationTests
{
    private static LinkResult AssembleAndLink(string path, string source)
    {
        var tree = SyntaxTree.Parse(SourceText.From(source, path));
        var model = Compilation.Create(tree).Emit();
        if (!model.Success)
            throw new InvalidOperationException(
                $"assemble failed: {string.Join("; ", model.Diagnostics.Select(d => d.Message))}"
            );

        var result = new NativeLinker().Link(
            [new LinkerInput(path, model)],
            new LinkOptions(PadToPowerOfTwo: false)
        );
        if (!result.Success)
            throw new InvalidOperationException(
                $"link failed: {string.Join("; ", result.Diagnostics.Select(d => d.Message))}"
            );

        return result;
    }

    private static LinkResult AssembleRoundtripLink(params (string path, string source)[] files)
    {
        var inputs = new List<LinkerInput>();
        foreach (var (path, source) in files)
        {
            var tree = SyntaxTree.Parse(SourceText.From(source, path));
            var model = Compilation.Create(tree).Emit();
            if (!model.Success)
                throw new InvalidOperationException(
                    $"assemble {path} failed: {string.Join("; ", model.Diagnostics.Select(d => d.Message))}"
                );

            if (model.ElfImage is not { Length: > 0 })
                throw new InvalidOperationException("assembly produced no ELF object");
            inputs.Add(new LinkerInput(path, model));
        }

        var result = new NativeLinker().Link(inputs, new LinkOptions(PadToPowerOfTwo: false));
        if (!result.Success)
            throw new InvalidOperationException(
                $"link failed: {string.Join("; ", result.Diagnostics.Select(d => d.Message))}"
            );

        return result;
    }

    [Test]
    public async Task AssembleLink_GoldenBytes_CoverCharmapInterpolationAndExplicitLocalJump()
    {
        var result = AssembleAndLink(
            "golden.asm",
            """
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
            """
        );

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
        var result = AssembleAndLink(
            "cross.asm",
            """
            SECTION "Reset", ROM0[$0100]
            EntryPoint:
                nop
                jp Boot

            SECTION "Boot", ROM0
            Boot:
            .halt:
                halt
                jr .halt
            """
        );

        var rom = result.RomData!;
        await Assert.That(rom[0x0100]).IsEqualTo((byte)0x00);
        await Assert.That(rom[0x0101]).IsEqualTo((byte)0xC3);
        await Assert
            .That(rom[0x0102] | (rom[0x0103] << 8))
            .IsEqualTo((int)result.Symbols.Single(symbol => symbol.Name == "Boot").AbsoluteAddress);
    }

    [Test]
    public async Task FixedSectionLabel_SymbolAtFixedAddress()
    {
        var result = AssembleAndLink(
            "entry.asm",
            """
            SECTION "Reset", ROM0[$0100]
            EntryPoint:
                nop
            """
        );

        var entry = result.Symbols.Single(s => s.Name == "EntryPoint");
        await Assert.That(entry.AbsoluteAddress).IsEqualTo(0x0100);
    }

    [Test]
    public async Task WramFixedSectionLabel_SymbolAtFixedAddress()
    {
        var result = AssembleAndLink(
            "wram.asm",
            """
            SECTION "Vars", WRAM0[$C000]
            wBoard: ds 16

            SECTION "Stub", ROM0[$0100]
            Start:
                nop
            """
        );

        var sym = result.Symbols.Single(s => s.Name == "wBoard");
        await Assert.That(sym.AbsoluteAddress).IsEqualTo(0xC000);
    }

    [Test]
    public async Task IntraSectionRelativeJump_StillWorks()
    {
        var result = AssembleAndLink(
            "rel.asm",
            """
            SECTION "Main", ROM0[$0100]
            Start:
                nop
                jr Start
            """
        );
        await Assert.That(result.RomData![0x0100]).IsEqualTo((byte)0x00);
        await Assert.That(result.RomData![0x0101]).IsEqualTo((byte)0x18);
        await Assert.That(result.RomData![0x0102]).IsEqualTo((byte)0xFD);
    }

    [Test]
    public async Task FloatingSection_SameSectionRefs_ResolveToPlacedAddress()
    {
        var result = AssembleAndLink(
            "floating.asm",
            """
            SECTION "Reset", ROM0[$0100]
                nop
                nop
                nop
                nop

            SECTION "Body", ROM0
            FuncA:
                dw .end
                jr .end
                nop
            .end:
                ret
            FuncB:
                dw .end
                nop
            .end:
                ret
            """
        );

        var rom = result.RomData!;
        int funcA = (int)result.Symbols.Single(symbol => symbol.Name == "FuncA").AbsoluteAddress;
        int funcB = (int)result.Symbols.Single(symbol => symbol.Name == "FuncB").AbsoluteAddress;
        await Assert.That(rom[funcA] | (rom[funcA + 1] << 8)).IsEqualTo(funcA + 5);
        await Assert.That(rom[funcA + 2]).IsEqualTo((byte)0x18);
        await Assert.That(rom[funcA + 3]).IsEqualTo((byte)1);
        await Assert.That(rom[funcB] | (rom[funcB + 1] << 8)).IsEqualTo(funcB + 3);
    }

    [Test]
    public async Task CrossSection_ElfObject_ResolvesDataRef()
    {
        var result = AssembleRoundtripLink(
            (
                "mod.asm",
                """
                SECTION "A", ROM0[$0100]
                EntryA:
                    dw Target
                    call Target
                    nop

                SECTION "B", ROM0[$0200]
                Target:
                    ret
                """
            )
        );

        var rom = result.RomData!;
        await Assert.That(rom[0x0100]).IsEqualTo((byte)0x00);
        await Assert.That(rom[0x0101]).IsEqualTo((byte)0x02);
        await Assert.That(rom[0x0102]).IsEqualTo((byte)0xCD);
        await Assert.That(rom[0x0103]).IsEqualTo((byte)0x00);
        await Assert.That(rom[0x0104]).IsEqualTo((byte)0x02);
    }
}
