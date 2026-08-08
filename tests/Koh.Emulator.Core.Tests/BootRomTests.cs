using Koh.Emulator.Core.Boot;
using Koh.Emulator.Core.Bus;
using Koh.Emulator.Core.Cartridge;

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

    /// <summary>A cartridge whose low bytes are distinguishable from a boot ROM's.</summary>
    private static Cartridge.Cartridge MakeCart()
    {
        var rom = new byte[0x8000];
        // Past $0900 too, so a test can tell "overlay ended" from "nothing there".
        for (int i = 0; i < 0x1000; i++)
            rom[i] = 0xC7; // cartridge marker byte
        // After the fill, not before: the marker would otherwise land in $0147 and be
        // read as an unsupported mapper.
        rom[0x147] = 0x00; // RomOnly
        return CartridgeFactory.Load(rom);
    }

    private static BootRom MakeStub(int size, byte fill)
    {
        var bytes = new byte[size];
        Array.Fill(bytes, fill);
        return BootRom.FromBytes(bytes);
    }

    [Test]
    public async Task Dmg_Overlay_Covers_0000_To_00FF_Only()
    {
        var gb = new GameBoySystem(MakeCart(), HardwareMode.Dmg);
        gb.LoadBootRom(MakeStub(0x100, 0xB0));

        await Assert.That(gb.DebugReadByte(0x0000)).IsEqualTo((byte)0xB0);
        await Assert.That(gb.DebugReadByte(0x00FF)).IsEqualTo((byte)0xB0);
        // $0100 onward is the cartridge even while mapped.
        await Assert.That(gb.DebugReadByte(0x0100)).IsEqualTo((byte)0xC7);
        await Assert.That(gb.DebugReadByte(0x0200)).IsEqualTo((byte)0xC7);
    }

    [Test]
    public async Task Cgb_Overlay_Leaves_The_Cartridge_Header_Hole_Visible()
    {
        // $0100-$01FF must fall through to the cartridge: it is the header the boot ROM
        // is reading the logo, title, and cartridge type out of.
        var gb = new GameBoySystem(MakeCart(), HardwareMode.Cgb);
        gb.LoadBootRom(MakeStub(0x900, 0xB0));

        await Assert.That(gb.DebugReadByte(0x0000)).IsEqualTo((byte)0xB0);
        await Assert.That(gb.DebugReadByte(0x00FF)).IsEqualTo((byte)0xB0);
        await Assert.That(gb.DebugReadByte(0x0100)).IsEqualTo((byte)0xC7);
        await Assert.That(gb.DebugReadByte(0x01FF)).IsEqualTo((byte)0xC7);
        await Assert.That(gb.DebugReadByte(0x0200)).IsEqualTo((byte)0xB0);
        await Assert.That(gb.DebugReadByte(0x08FF)).IsEqualTo((byte)0xB0);
        await Assert.That(gb.DebugReadByte(0x0900)).IsEqualTo((byte)0xC7);
    }

    [Test]
    public async Task Unmapping_Reveals_The_Cartridge_Underneath()
    {
        var gb = new GameBoySystem(MakeCart(), HardwareMode.Dmg);
        gb.LoadBootRom(MakeStub(0x100, 0xB0));
        await Assert.That(gb.DebugReadByte(0x0000)).IsEqualTo((byte)0xB0);

        gb.Mmu.WriteByte(0xFF50, 0x01);

        await Assert.That(gb.DebugReadByte(0x0000)).IsEqualTo((byte)0xC7);
        await Assert.That(gb.Mmu.BootRomMapped).IsFalse();
    }

    [Test]
    public async Task Writes_Below_0900_Always_Reach_The_Cartridge_Not_The_Overlay()
    {
        // Boot ROM reads are intercepted; writes are not. A write to $0000 is an MBC
        // register write on hardware whether or not a boot ROM is mapped.
        var gb = new GameBoySystem(MakeCart(), HardwareMode.Dmg);
        gb.LoadBootRom(MakeStub(0x100, 0xB0));
        gb.Mmu.WriteByte(0x0000, 0x0A);
        await Assert.That(gb.DebugReadByte(0x0000)).IsEqualTo((byte)0xB0);
    }

    [Test]
    public async Task Cgb_Machine_Rejects_A_Dmg_Boot_Rom()
    {
        var gb = new GameBoySystem(MakeCart(), HardwareMode.Cgb);
        var ex = Assert.Throws<ArgumentException>(() => gb.LoadBootRom(MakeStub(0x100, 0xB0)));
        await Assert.That(ex!.Message).Contains("Cgb");
    }

    [Test]
    public async Task Dmg_Machine_Rejects_A_Cgb_Boot_Rom()
    {
        var gb = new GameBoySystem(MakeCart(), HardwareMode.Dmg);
        var ex = Assert.Throws<ArgumentException>(() => gb.LoadBootRom(MakeStub(0x900, 0xB0)));
        await Assert.That(ex!.Message).Contains("Dmg");
    }

    [Test]
    public async Task No_Boot_Rom_Means_The_Cartridge_Is_Visible_From_0000()
    {
        // The PowerOnStateTests contract: without a boot ROM nothing is overlaid.
        var gb = new GameBoySystem(MakeCart(), HardwareMode.Dmg);
        await Assert.That(gb.BootRomMapped).IsFalse();
        await Assert.That(gb.DebugReadByte(0x0000)).IsEqualTo((byte)0xC7);
    }
}
