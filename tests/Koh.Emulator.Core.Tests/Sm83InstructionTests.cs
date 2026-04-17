using Koh.Emulator.Core.Cartridge;
using Koh.Emulator.Core.Cpu;

namespace Koh.Emulator.Core.Tests;

public class Sm83InstructionTests
{
    private static GameBoySystem MakeSystemWithProgram(params byte[] program)
    {
        var rom = new byte[0x8000];
        rom[0x147] = 0x00;
        Array.Copy(program, 0, rom, 0x0100, program.Length);
        var cart = CartridgeFactory.Load(rom);
        return new GameBoySystem(HardwareMode.Dmg, cart);
    }

    private static void RunInstructions(GameBoySystem gb, int count)
    {
        int completed = 0;
        // Safety limit to avoid infinite loops in tests.
        for (int t = 0; t < 10000 && completed < count; t++)
        {
            if (gb.Cpu.TickT()) completed++;
        }
    }

    [Test]
    public async Task Ld_A_Immediate_Loads_Value()
    {
        var gb = MakeSystemWithProgram(0x3E, 0x42);  // LD A,$42
        RunInstructions(gb, 1);
        await Assert.That(gb.Registers.A).IsEqualTo((byte)0x42);
    }

    [Test]
    public async Task Ld_Bc_Immediate16_Loads_Word()
    {
        var gb = MakeSystemWithProgram(0x01, 0x34, 0x12);  // LD BC,$1234
        RunInstructions(gb, 1);
        await Assert.That(gb.Registers.BC).IsEqualTo((ushort)0x1234);
    }

    [Test]
    public async Task Xor_A_Clears_A_And_Sets_Z()
    {
        var gb = MakeSystemWithProgram(0x3E, 0xFF, 0xAF);  // LD A,$FF; XOR A
        RunInstructions(gb, 2);
        bool zSet = gb.Registers.FlagSet(CpuRegisters.FlagZ);
        await Assert.That(gb.Registers.A).IsEqualTo((byte)0);
        await Assert.That(zSet).IsTrue();
    }

    [Test]
    public async Task Inc_B_From_FF_Wraps_And_Sets_HalfCarry()
    {
        var gb = MakeSystemWithProgram(0x06, 0xFF, 0x04);  // LD B,$FF; INC B
        RunInstructions(gb, 2);
        bool hSet = gb.Registers.FlagSet(CpuRegisters.FlagH);
        bool zSet = gb.Registers.FlagSet(CpuRegisters.FlagZ);
        await Assert.That(gb.Registers.B).IsEqualTo((byte)0);
        await Assert.That(hSet).IsTrue();
        await Assert.That(zSet).IsTrue();
    }

    [Test]
    public async Task Jp_A16_Sets_Pc()
    {
        var gb = MakeSystemWithProgram(0xC3, 0x00, 0x20);  // JP $2000
        RunInstructions(gb, 1);
        await Assert.That(gb.Registers.Pc).IsEqualTo((ushort)0x2000);
    }

    [Test]
    public async Task Jr_Nz_Taken_When_Z_Clear()
    {
        // XOR A (clears Z via result == 0... wait no — XOR A zeros A and SETS Z.
        // Use OR A with A=$FF-ish instead: clobber F first by loading a non-zero value
        // and using a flag-clearing instruction. Simplest: start from post-boot F then
        // explicitly clear Z with "INC D" (D=$FF post-boot, INC D → D=$00 with Z SET —
        // nope that sets Z too). Use "LD A,1; CP A,0" — CP sets Z iff A==arg, so CP 0
        // with A=1 clears Z.
        var gb = MakeSystemWithProgram(0x3E, 0x01, 0xFE, 0x00, 0x20, 0x02);  // LD A,1; CP 0; JR NZ,+2
        RunInstructions(gb, 3);
        // Pc should be 0x0108 (2 bytes LD + 2 bytes CP + 2 bytes JR + 2 offset)
        await Assert.That(gb.Registers.Pc).IsEqualTo((ushort)0x0108);
    }

    [Test]
    public async Task Call_Ret_Roundtrip()
    {
        // LD SP,$FFFE — implicit via reset.
        // CALL $0110; NOP @$0103
        // At $0110: NOP; RET
        var rom = new byte[0x8000];
        rom[0x147] = 0x00;
        rom[0x100] = 0xCD; rom[0x101] = 0x10; rom[0x102] = 0x01;  // CALL $0110
        rom[0x103] = 0x00;                                          // NOP
        rom[0x110] = 0x00;                                          // NOP
        rom[0x111] = 0xC9;                                          // RET
        var cart = CartridgeFactory.Load(rom);
        var gb = new GameBoySystem(HardwareMode.Dmg, cart);

        RunInstructions(gb, 1);  // CALL
        ushort afterCall = gb.Registers.Pc;
        RunInstructions(gb, 2);  // NOP + RET
        ushort afterRet = gb.Registers.Pc;

        await Assert.That(afterCall).IsEqualTo((ushort)0x0110);
        await Assert.That(afterRet).IsEqualTo((ushort)0x0103);
    }

    [Test]
    public async Task Push_Pop_Preserves_Value()
    {
        // LD BC,$1234; PUSH BC; LD BC,0; POP BC
        var gb = MakeSystemWithProgram(0x01, 0x34, 0x12, 0xC5, 0x01, 0x00, 0x00, 0xC1);
        RunInstructions(gb, 4);
        await Assert.That(gb.Registers.BC).IsEqualTo((ushort)0x1234);
    }

    [Test]
    public async Task And_Immediate_Sets_HalfCarry()
    {
        // LD A,$FF; AND $0F
        var gb = MakeSystemWithProgram(0x3E, 0xFF, 0xE6, 0x0F);
        RunInstructions(gb, 2);
        bool hSet = gb.Registers.FlagSet(CpuRegisters.FlagH);
        await Assert.That(gb.Registers.A).IsEqualTo((byte)0x0F);
        await Assert.That(hSet).IsTrue();
    }

    [Test]
    public async Task Cp_Sets_Z_When_Equal()
    {
        // LD A,5; CP 5
        var gb = MakeSystemWithProgram(0x3E, 0x05, 0xFE, 0x05);
        RunInstructions(gb, 2);
        bool zSet = gb.Registers.FlagSet(CpuRegisters.FlagZ);
        await Assert.That(gb.Registers.A).IsEqualTo((byte)0x05);  // A unchanged by CP
        await Assert.That(zSet).IsTrue();
    }

    [Test]
    public async Task Ldh_Writes_To_HighRam_Area()
    {
        // LD A,$77; LDH ($90),A  — writes to $FF90
        var gb = MakeSystemWithProgram(0x3E, 0x77, 0xE0, 0x90);
        RunInstructions(gb, 2);
        byte v = gb.Mmu.ReadByte(0xFF90);
        await Assert.That(v).IsEqualTo((byte)0x77);
    }

    [Test]
    public async Task CbSwap_A_Swaps_Nibbles()
    {
        // LD A,$12; SWAP A (CB $37)
        var gb = MakeSystemWithProgram(0x3E, 0x12, 0xCB, 0x37);
        RunInstructions(gb, 2);
        await Assert.That(gb.Registers.A).IsEqualTo((byte)0x21);
    }

    [Test]
    public async Task CbBit_On_Zero_Sets_Z_Flag()
    {
        // LD A,0; BIT 0,A (CB $47)
        var gb = MakeSystemWithProgram(0x3E, 0x00, 0xCB, 0x47);
        RunInstructions(gb, 2);
        bool zSet = gb.Registers.FlagSet(CpuRegisters.FlagZ);
        await Assert.That(zSet).IsTrue();
    }

    [Test]
    public async Task CbSet_1_On_A_Sets_Bit_1()
    {
        // LD A,0; SET 1,A (CB $CF)
        var gb = MakeSystemWithProgram(0x3E, 0x00, 0xCB, 0xCF);
        RunInstructions(gb, 2);
        await Assert.That(gb.Registers.A).IsEqualTo((byte)0x02);
    }

    [Test]
    public async Task Ldi_Hl_A_Writes_And_Increments_HL()
    {
        // LD HL,$C000; LD A,$42; LD (HL+),A
        var gb = MakeSystemWithProgram(0x21, 0x00, 0xC0, 0x3E, 0x42, 0x22);
        RunInstructions(gb, 3);
        byte v = gb.Mmu.ReadByte(0xC000);
        await Assert.That(v).IsEqualTo((byte)0x42);
        await Assert.That(gb.Registers.HL).IsEqualTo((ushort)0xC001);
    }
}
