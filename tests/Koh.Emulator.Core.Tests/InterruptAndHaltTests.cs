using Koh.Emulator.Core.Cartridge;
using Koh.Emulator.Core.Cpu;

namespace Koh.Emulator.Core.Tests;

public class InterruptAndHaltTests
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
        for (int t = 0; t < 100000 && completed < count; t++)
        {
            if (gb.Cpu.TickT()) completed++;
        }
    }

    [Test]
    public async Task Interrupt_Dispatch_Pushes_Pc_And_Jumps_To_Vector()
    {
        // NOP ; NOP. IE=VBlank, IF pre-raised, IME=1. The first NOP completes,
        // then dispatch fires at its instruction boundary (same TickT call).
        var gb = MakeSystemWithProgram(0x00, 0x00);
        gb.Cpu.Ime = true;
        gb.Io.Interrupts.IE = Interrupts.VBlank;
        gb.Io.Interrupts.Raise(Interrupts.VBlank);

        RunInstructions(gb, 1);

        await Assert.That(gb.Registers.Pc).IsEqualTo((ushort)0x0040);
        await Assert.That(gb.Cpu.Ime).IsFalse();
    }

    [Test]
    public async Task Ei_Has_One_Instruction_Delay_Before_Interrupt()
    {
        // EI ; NOP ; NOP  (IE+IF set to VBlank beforehand, IME off)
        var gb = MakeSystemWithProgram(0xFB, 0x00, 0x00);
        gb.Io.Interrupts.IE = Interrupts.VBlank;
        gb.Io.Interrupts.Raise(Interrupts.VBlank);

        RunInstructions(gb, 1);  // EI — at its boundary, IME is STILL false (delayed).
        bool imeAfterEi = gb.Cpu.Ime;
        ushort pcAfterEi = gb.Registers.Pc;

        // At NOP's boundary, IME becomes true AND dispatch fires in the same
        // boundary step (which clears IME again). Observable: PC jumped to $0040.
        RunInstructions(gb, 1);
        ushort pcAfterNop = gb.Registers.Pc;

        await Assert.That(imeAfterEi).IsFalse();
        await Assert.That(pcAfterEi).IsEqualTo((ushort)0x0101);
        await Assert.That(pcAfterNop).IsEqualTo((ushort)0x0040);
    }

    [Test]
    public async Task Halt_With_Ime_Clear_And_Pending_Irq_Triggers_Halt_Bug()
    {
        // LD A,0 ; HALT ; INC A ; (padding)
        // IE=VBlank, IF pre-raised, IME=0. HALT exits immediately due to pending IRQ;
        // the next fetch re-reads the same byte (INC A) twice.
        var gb = MakeSystemWithProgram(0x3E, 0x00, 0x76, 0x3C, 0x00, 0x00);
        gb.Cpu.Ime = false;
        gb.Io.Interrupts.IE = Interrupts.VBlank;
        gb.Io.Interrupts.Raise(Interrupts.VBlank);

        RunInstructions(gb, 1);  // LD A,0
        RunInstructions(gb, 1);  // HALT (exits immediately, arms HALT bug)
        RunInstructions(gb, 1);  // INC A (first time — reads $3C at $0103)
        RunInstructions(gb, 1);  // INC A (second time — HALT bug re-reads $3C at $0103)

        await Assert.That(gb.Registers.A).IsEqualTo((byte)2);
    }

    [Test]
    public async Task Halt_With_Ime_Set_Wakes_And_Services_Interrupt()
    {
        // HALT waits for IRQ; when it arrives with IME=1, CPU wakes and the
        // next TickT services the ISR.
        var gb = MakeSystemWithProgram(0x76, 0x00);
        gb.Cpu.Ime = true;
        gb.Io.Interrupts.IE = Interrupts.VBlank;

        RunInstructions(gb, 1);  // HALT — CPU sleeps (one idle M-cycle).
        await Assert.That(gb.Cpu.Halted).IsTrue();

        gb.Io.Interrupts.Raise(Interrupts.VBlank);
        RunInstructions(gb, 1);  // wake + dispatch in the same TickT.

        await Assert.That(gb.Registers.Pc).IsEqualTo((ushort)0x0040);
    }
}
