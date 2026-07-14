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

    /// <summary>Same as <see cref="MakeSystemWithProgram"/> but the program is
    /// placed at an arbitrary ROM address and the CPU starts executing there —
    /// used by the ie_push tests to control the high byte of the return PC
    /// (and hence which byte a corrupting stack push writes into IE).</summary>
    private static GameBoySystem MakeSystemWithProgramAt(ushort address, params byte[] program)
    {
        var rom = new byte[0x8000];
        rom[0x147] = 0x00;
        Array.Copy(program, 0, rom, address, program.Length);
        var cart = CartridgeFactory.Load(rom);
        var gb = new GameBoySystem(HardwareMode.Dmg, cart);
        gb.Registers.Pc = address;
        return gb;
    }

    private static void RunInstructions(GameBoySystem gb, int count)
    {
        int completed = 0;
        for (int t = 0; t < 100000 && completed < count; t++)
        {
            if (gb.Cpu.TickT())
                completed++;
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

        RunInstructions(gb, 1); // EI — at its boundary, IME is STILL false (delayed).
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

        RunInstructions(gb, 1); // LD A,0
        RunInstructions(gb, 1); // HALT (exits immediately, arms HALT bug)
        RunInstructions(gb, 1); // INC A (first time — reads $3C at $0103)
        RunInstructions(gb, 1); // INC A (second time — HALT bug re-reads $3C at $0103)

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

        RunInstructions(gb, 1); // HALT — CPU sleeps (one idle M-cycle).
        await Assert.That(gb.Cpu.Halted).IsTrue();

        gb.Io.Interrupts.Raise(Interrupts.VBlank);
        RunInstructions(gb, 1); // wake + dispatch in the same TickT.

        await Assert.That(gb.Registers.Pc).IsEqualTo((ushort)0x0040);
    }

    [Test]
    public async Task IePush_HighByte_Push_Cancels_Dispatch_To_Zero_Page()
    {
        // Mooneye acceptance/interrupts/ie_push, round 1: SP=$0000 so the
        // high-PC-byte push lands on $FFFF (= IE). The pushed byte is the
        // return PC's high byte ($01), which clears the Timer bit in IE and
        // cancels the dispatch entirely — PC ends up at $0000 (not $0040),
        // and since nothing was ultimately serviced, IF is left untouched.
        var gb = MakeSystemWithProgramAt(0x0100, 0x00); // NOP at $0100
        gb.Registers.Sp = 0x0000;
        gb.Cpu.Ime = true;
        gb.Io.Interrupts.IE = Interrupts.Timer;
        gb.Io.Interrupts.Raise(Interrupts.Timer);

        RunInstructions(gb, 1); // NOP, then dispatch fires at its boundary.

        await Assert.That(gb.Registers.Pc).IsEqualTo((ushort)0x0000);
        await Assert.That(gb.Io.Interrupts.IF).IsEqualTo(Interrupts.Timer);
        await Assert.That(gb.Io.Interrupts.IE).IsEqualTo((byte)0x01); // clobbered by the push
        await Assert.That(gb.Cpu.Ime).IsFalse();
    }

    [Test]
    public async Task IePush_LowByte_Push_Is_Too_Late_To_Cancel_Dispatch()
    {
        // Mooneye acceptance/interrupts/ie_push, round 3: SP=$0001 so the
        // high-PC-byte push lands harmlessly in RAM, but the low-PC-byte
        // push lands on $FFFF. By then the vector and IF-clear decision are
        // already locked in (made right after the high-byte push), so the
        // Serial interrupt is still serviced normally and its IF bit clears.
        var gb = MakeSystemWithProgramAt(0x0100, 0x00); // NOP at $0100
        gb.Registers.Sp = 0x0001;
        gb.Cpu.Ime = true;
        gb.Io.Interrupts.IE = Interrupts.Serial;
        gb.Io.Interrupts.Raise(Interrupts.Serial);

        RunInstructions(gb, 1);

        await Assert.That(gb.Registers.Pc).IsEqualTo((ushort)0x0058); // Serial vector
        await Assert.That(gb.Io.Interrupts.IF).IsEqualTo((byte)0x00);
        await Assert.That(gb.Cpu.Ime).IsFalse();
    }

    [Test]
    public async Task IePush_HighByte_Push_Redirects_To_Lower_Priority_Interrupt()
    {
        // Mooneye acceptance/interrupts/ie_push, round 4: both VBlank and
        // STAT are pending (VBlank has priority). SP=$0000 so the high-byte
        // push (return PC high byte = $02) lands on $FFFF and rewrites IE to
        // $02 — clearing VBlank's IE bit but keeping STAT's. Re-evaluating
        // IE&IF after that push finds STAT still enabled, so STAT is
        // serviced instead (its IF bit clears; VBlank's IF bit is left set
        // since it was never the one actually dispatched).
        var gb = MakeSystemWithProgramAt(0x0201, 0x00); // NOP at $0201
        gb.Registers.Sp = 0x0000;
        gb.Cpu.Ime = true;
        gb.Io.Interrupts.IE = (byte)(Interrupts.VBlank | Interrupts.Stat);
        gb.Io.Interrupts.Raise((byte)(Interrupts.VBlank | Interrupts.Stat));

        RunInstructions(gb, 1);

        await Assert.That(gb.Registers.Pc).IsEqualTo((ushort)0x0048); // STAT vector
        await Assert.That(gb.Io.Interrupts.IF).IsEqualTo(Interrupts.VBlank); // untouched
        await Assert.That(gb.Io.Interrupts.IE).IsEqualTo((byte)0x02); // clobbered by the push
        await Assert.That(gb.Cpu.Ime).IsFalse();
    }
}
