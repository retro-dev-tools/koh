using Koh.Core;
using Koh.Core.Binding;
using Koh.Core.Syntax;
using Koh.Linker;

namespace Koh.Linker.Tests;

/// <summary>
/// A boot ROM is not a cartridge: it is an exactly-sized raw image with no header, no
/// checksums, and no power-of-two padding. Two things break if it goes through the
/// normal cartridge path — NextPowerOfTwo rounds the 2304-byte CGB image up to 4096, and
/// the checksum fixups scribble on $014D-$014F, which for a CGB boot ROM lands inside the
/// image (the length guard at $0150 only protects the 256-byte DMG one).
/// </summary>
public class RawImageTests
{
    private static EmitModel Emit(string source) =>
        Compilation.Create(SyntaxTree.Parse(source)).Emit();

    private static LinkResult LinkRaw(string source, int size) =>
        new Koh.Linker.Linker().Link(
            [new LinkerInput("boot.asm", Emit(source))],
            new LinkOptions(PadToPowerOfTwo: false, MinSize: size)
        );

    [Test]
    public async Task Raw_Cgb_Sized_Image_Is_Not_Rounded_To_A_Power_Of_Two()
    {
        var result = LinkRaw("SECTION \"Boot\", ROM0[$0000]\nnop", 0x900);
        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.RomData!.Length).IsEqualTo(0x900);
    }

    [Test]
    public async Task Raw_Dmg_Sized_Image_Is_Exactly_256_Bytes()
    {
        var result = LinkRaw("SECTION \"Boot\", ROM0[$0000]\nnop", 0x100);
        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.RomData!.Length).IsEqualTo(0x100);
    }

    [Test]
    public async Task Raw_Cgb_Sized_Image_Keeps_Its_Own_Bytes_At_014D()
    {
        // The regression that matters: $014D-$014F sit INSIDE a 2304-byte CGB boot ROM,
        // so the cartridge checksum fixups would happily overwrite real boot code there.
        var result = LinkRaw(
            "SECTION \"Boot\", ROM0[$0000]\nnop\nSECTION \"Mid\", ROM0[$014D]\ndb $AA,$BB,$CC",
            0x900
        );
        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.RomData![0x014D]).IsEqualTo((byte)0xAA);
        await Assert.That(result.RomData![0x014E]).IsEqualTo((byte)0xBB);
        await Assert.That(result.RomData![0x014F]).IsEqualTo((byte)0xCC);
    }

    [Test]
    public async Task Raw_Image_Is_Zero_Filled_Past_Its_Sections()
    {
        var result = LinkRaw("SECTION \"Boot\", ROM0[$0000]\nnop", 0x100);
        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.RomData![0]).IsEqualTo((byte)0x00); // nop
        for (int i = 1; i < 0x100; i++)
            await Assert.That(result.RomData![i]).IsEqualTo((byte)0x00);
    }

    [Test]
    public async Task Raw_Size_Is_Exact_Not_A_Floor()
    {
        // A boot ROM's size is fixed by hardware, so --size must be a ceiling as well as
        // a floor. Content well short of the size is padded up to it.
        var result = LinkRaw("SECTION \"Boot\", ROM0[$0000]\nnop", 0x100);
        await Assert.That(result.RomData!.Length).IsEqualTo(0x100);
    }

    [Test]
    public async Task Raw_Content_Overflowing_The_Size_Is_An_Error_Not_A_Bigger_Image()
    {
        // The failure this prevents: a DMG boot ROM that grows past 256 bytes silently
        // linking to 260, whose $FF50 hand-off then no longer sits at $00FC-$00FF and
        // never reaches $0100. Better to fail at link time with the numbers named.
        var result = LinkRaw(
            "SECTION \"Boot\", ROM0[$0000]\nnop\nSECTION \"Over\", ROM0[$014D]\ndb $AA",
            0x100
        );
        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("256"))).IsTrue();
    }

    [Test]
    public async Task Cartridge_Output_Still_Pads_To_A_Power_Of_Two_By_Default()
    {
        var result = new Koh.Linker.Linker().Link([
            new LinkerInput("cart.asm", Emit("SECTION \"Main\", ROM0\nnop")),
        ]);
        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.RomData!.Length).IsEqualTo(0x8000);
    }

    [Test]
    public async Task Cartridge_Output_Still_Gets_Its_Checksums_Fixed()
    {
        // The default path must be untouched: a cartridge still gets $014D patched.
        var result = new Koh.Linker.Linker().Link([
            new LinkerInput(
                "cart.asm",
                Emit("SECTION \"Main\", ROM0\nnop\nSECTION \"Hdr\", ROM0[$014D]\ndb $AA")
            ),
        ]);
        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.RomData![0x014D]).IsNotEqualTo((byte)0xAA);
    }
}
