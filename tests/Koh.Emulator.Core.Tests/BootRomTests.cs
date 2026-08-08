using Koh.Emulator.Core.Boot;
using Koh.Emulator.Core.Bus;

namespace Koh.Emulator.Core.Tests;

/// <summary>
/// The BootRom type is the validation boundary: past it, an invalid blob is
/// unrepresentable, so Mmu never has to re-check a size. Family comes from the
/// blob's length rather than a caller-declared mode, so an unrecognised but
/// correctly-sized dump (SGB, AGB, homebrew) works with no hash table to maintain.
/// </summary>
public class BootRomTests
{
    [Test]
    public async Task Dmg_Sized_Blob_Is_Dmg_Family()
    {
        var rom = BootRom.FromBytes(new byte[0x100]);
        await Assert.That(rom.Family).IsEqualTo(BootRomFamily.Dmg);
        await Assert.That(rom.Bytes.Length).IsEqualTo(0x100);
    }

    [Test]
    public async Task Cgb_Sized_Blob_Is_Cgb_Family()
    {
        var rom = BootRom.FromBytes(new byte[0x900]);
        await Assert.That(rom.Family).IsEqualTo(BootRomFamily.Cgb);
        await Assert.That(rom.Bytes.Length).IsEqualTo(0x900);
    }

    [Test]
    [Arguments(0)]
    [Arguments(0xFF)]
    [Arguments(0x101)]
    [Arguments(0x800)]
    [Arguments(0x1000)]
    public async Task Wrong_Size_Is_Rejected_Naming_Both_Valid_Sizes(int size)
    {
        var ex = Assert.Throws<ArgumentException>(() => BootRom.FromBytes(new byte[size]));
        // The diagnostic must be actionable: what was given, and what is accepted.
        await Assert.That(ex!.Message).Contains(size.ToString());
        await Assert.That(ex.Message).Contains("256");
        await Assert.That(ex.Message).Contains("2304");
    }

    [Test]
    public async Task FromBytes_Copies_So_Caller_Cannot_Mutate_A_Loaded_Blob()
    {
        var buffer = new byte[0x100];
        buffer[0x10] = 0xAB;
        var rom = BootRom.FromBytes(buffer);
        buffer[0x10] = 0xCD; // caller reuses its buffer
        await Assert.That(rom.Bytes[0x10]).IsEqualTo((byte)0xAB);
    }

    [Test]
    public async Task Fingerprint_Differs_For_Different_Contents_And_Matches_For_Same()
    {
        var a = new byte[0x100];
        var b = new byte[0x100];
        b[0x42] = 0x01;
        await Assert
            .That(BootRom.FromBytes(a).Fingerprint)
            .IsEqualTo(BootRom.FromBytes(a).Fingerprint);
        await Assert
            .That(BootRom.FromBytes(a).Fingerprint)
            .IsNotEqualTo(BootRom.FromBytes(b).Fingerprint);
    }

    [Test]
    public async Task Fingerprint_Is_Never_Zero_So_Zero_Can_Mean_No_Boot_Rom()
    {
        // Save states use 0 as the "no boot ROM was loaded" sentinel (Task 4), so a
        // real blob must never fingerprint to 0 -- including an all-zero blob.
        await Assert.That(BootRom.FromBytes(new byte[0x100]).Fingerprint).IsNotEqualTo(0UL);
        await Assert.That(BootRom.FromBytes(new byte[0x900]).Fingerprint).IsNotEqualTo(0UL);
    }

    private static IoRegisters MakeIo() =>
        new(new Timer.Timer()) { HardwareMode = HardwareMode.Dmg };

    [Test]
    public async Task Ff50_Starts_Unlatched()
    {
        await Assert.That(MakeIo().BootRomUnmapped).IsFalse();
    }

    [Test]
    public async Task Ff50_Nonzero_Write_Latches_The_Unmap()
    {
        var io = MakeIo();
        io.Write(0xFF50, 0x01);
        await Assert.That(io.BootRomUnmapped).IsTrue();
    }

    [Test]
    public async Task Ff50_Zero_Write_Does_Not_Latch()
    {
        var io = MakeIo();
        io.Write(0xFF50, 0x00);
        await Assert.That(io.BootRomUnmapped).IsFalse();
    }

    [Test]
    public async Task Ff50_Latch_Is_One_Way_And_A_Zero_Write_Cannot_Clear_It()
    {
        // On hardware there is no path back to a mapped boot ROM.
        var io = MakeIo();
        io.Write(0xFF50, 0x01);
        io.Write(0xFF50, 0x00);
        await Assert.That(io.BootRomUnmapped).IsTrue();
    }

    [Test]
    public async Task Ff50_Still_Reads_As_Ff()
    {
        // $FF50 is write-only; the latch must not make it readable.
        var io = MakeIo();
        io.Write(0xFF50, 0x01);
        await Assert.That(io.Read(0xFF50)).IsEqualTo((byte)0xFF);
    }
}
