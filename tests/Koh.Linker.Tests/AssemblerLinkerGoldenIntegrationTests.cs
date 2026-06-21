using Koh.Core;
using Koh.Core.Binding;
using Koh.Core.Syntax;
using Koh.Core.Text;
using Koh.Emit;
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

    /// <summary>
    /// Assembles each file, round-trips it through the real per-file .kobj
    /// serialization, then links them — the actual multi-file pipeline users hit.
    /// </summary>
    private static LinkResult AssembleRoundtripLink(params (string path, string source)[] files)
    {
        var inputs = new List<LinkerInput>();
        foreach (var (path, source) in files)
        {
            var tree = SyntaxTree.Parse(SourceText.From(source, path));
            var model = Compilation.Create(tree).Emit();
            if (!model.Success)
                throw new InvalidOperationException(
                    $"assemble {path} failed: {string.Join("; ", model.Diagnostics.Select(d => d.Message))}");

            using var ms = new MemoryStream();
            KobjWriter.Write(ms, model);
            ms.Position = 0;
            inputs.Add(new LinkerInput(path, KobjReader.Read(ms)));
        }

        var result = new Koh.Linker.Core.Linker().Link(inputs);
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

    [Test]
    public async Task FloatingSection_SameSectionRefs_ResolveToPlacedAddress()
    {
        // Reset is pinned at $0100 (4 bytes); Body floats and is placed at $0104.
        // Same-section refs inside a floating section defer to the linker and must
        // resolve against Body's PLACED address, not the section-relative offset:
        //   - `dw .end` → the ABSOLUTE address of the enclosing scope's .end
        //      (regression: data-directive patches carried no SymbolName, so the
        //      linker couldn't resolve them at all — `dw`/`db` pointer tables in a
        //      floating section failed to link).
        //   - `jr .end` → a position-independent relative displacement.
        //   - the two identically-named `.end` locals resolve to their own scopes.
        var result = AssembleAndLink("floating.asm", """
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
            """);

        var rom = result.RomData!;
        // FuncA at $0104: dw(2) + jr(2) + nop(1) → FuncA.end at $0109.
        await Assert.That(rom[0x0104]).IsEqualTo((byte)0x09); // low(FuncA.end) = $0109
        await Assert.That(rom[0x0105]).IsEqualTo((byte)0x01); // high → absolute, not $0005
        await Assert.That(rom[0x0106]).IsEqualTo((byte)0x18); // jr
        await Assert.That(rom[0x0107]).IsEqualTo((byte)0x01); // displacement: skip nop
        // FuncB at $010A: dw(2) + nop(1) → FuncB.end at $010D (distinct scope).
        await Assert.That(rom[0x010A]).IsEqualTo((byte)0x0D); // low(FuncB.end) = $010D
        await Assert.That(rom[0x010B]).IsEqualTo((byte)0x01); // high
    }

    [Test]
    public async Task CrossSection_KobjRoundtrip_ResolvesDataRef()
    {
        // The real .kobj pipeline: assemble → serialize → deserialize → link.
        // Section A references Target in section B from BOTH a `dw` (data) and a
        // `call` (instruction). The data ref is the regression — `db`/`dw`/`dl`
        // patches never carried a SymbolName, so a deferred (cross-section /
        // floating) `dw Target` couldn't link. Round-tripping through KobjWriter/
        // KobjReader also proves SymbolName survives serialization.
        var result = AssembleRoundtripLink(
            ("mod.asm", """
                SECTION "A", ROM0[$0100]
                EntryA:
                    dw Target
                    call Target
                    nop

                SECTION "B", ROM0[$0200]
                Target:
                    ret
                """));

        var rom = result.RomData!;
        await Assert.That(rom[0x0100]).IsEqualTo((byte)0x00); // dw Target → low($0200)
        await Assert.That(rom[0x0101]).IsEqualTo((byte)0x02); // high — cross-section data ref
        await Assert.That(rom[0x0102]).IsEqualTo((byte)0xCD); // call
        await Assert.That(rom[0x0103]).IsEqualTo((byte)0x00); // low(Target)
        await Assert.That(rom[0x0104]).IsEqualTo((byte)0x02); // high
    }
}
