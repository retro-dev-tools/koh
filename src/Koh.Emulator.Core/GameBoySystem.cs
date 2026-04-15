using Koh.Emulator.Core.Bus;
using Koh.Emulator.Core.Cgb;
using Koh.Emulator.Core.Cpu;
using Koh.Emulator.Core.Dma;
using Koh.Emulator.Core.Joypad;
using Koh.Emulator.Core.Ppu;

namespace Koh.Emulator.Core;

public sealed class GameBoySystem
{
    public HardwareMode Mode { get; }
    public SystemClock Clock { get; } = new();
    public Cartridge.Cartridge Cartridge { get; }
    public Mmu Mmu { get; }
    public IoRegisters Io { get; }
    public Timer.Timer Timer { get; }
    public Sm83 Cpu { get; }
    public Ppu.Ppu Ppu { get; }
    public OamDma OamDma { get; }
    public Hdma Hdma { get; }
    public Apu.Apu Apu { get; } = new();
    public KeyOneRegister KeyOne { get; } = new();
    public JoypadState Joypad;

    public RunGuard RunGuard { get; } = new();

    /// <summary>
    /// Optional breakpoint predicate called at each instruction boundary.
    /// Returning true halts the run loop with <see cref="StopReason.Breakpoint"/>.
    /// </summary>
    public Func<ushort, bool>? BreakpointChecker;

    private bool _running;

    public GameBoySystem(HardwareMode mode, Cartridge.Cartridge cart)
    {
        Mode = mode;
        Cartridge = cart;
        Timer = new Timer.Timer();
        Io = new IoRegisters(Timer) { HardwareMode = mode };
        Mmu = new Mmu(cart, Io);
        Ppu = new Ppu.Ppu(mode, Mmu.VramArray, Mmu.OamArray);
        OamDma = new OamDma(Mmu);
        Mmu.AttachOamDma(OamDma);
        Hdma = new Hdma(Mmu);
        Ppu.HBlankEntered += Hdma.OnHBlankEntered;
        Io.AttachPpu(Ppu);
        Io.AttachHdma(Hdma);
        Io.AttachKeyOne(KeyOne);
        Io.AttachBanking(Mmu.Banking);
        Io.AttachApu(Apu);

        // Sm83 drives peripheral ticks per memory access: each ReadByte /
        // WriteByte / ReadImmediate / InternalCycle advances one M-cycle.
        Cpu = new Sm83(Mmu, TickForMCycle);
    }

    public ref CpuRegisters Registers => ref Cpu.Registers;
    public Framebuffer Framebuffer => Ppu.Framebuffer;
    public bool IsRunning => _running;

    /// <summary>
    /// Advance peripherals by 1 CPU M-cycle (4 T-cycles). Called by the CPU
    /// during memory accesses and internal cycles.
    /// </summary>
    private void TickForMCycle()
    {
        Clock.DoubleSpeed = KeyOne.DoubleSpeed;

        // Per M-cycle: Timer + OamDma + Hdma always tick 4 T-cycles (these run
        // at CPU clock rate — unchanged in double-speed). PPU and the system
        // clock run at the base rate, so in DS they only tick 2 dots per CPU
        // M-cycle.
        for (int t = 0; t < 4; t++)
        {
            Timer.TickT(ref Io.Interrupts);
            OamDma.TickT();
            if (Hdma.Active) Hdma.TickT();
            Apu.TickT();
        }

        int ppuDots = Clock.DoubleSpeed ? 2 : 4;
        for (int d = 0; d < ppuDots; d++)
        {
            Ppu.TickDot(ref Io.Interrupts);
            Clock.AdvanceOne();
        }
    }

    /// <summary>
    /// Execute one full CPU step (one instruction, or one idle M-cycle when
    /// halted). Peripherals tick internally via the M-cycle callback.
    /// </summary>
    public bool StepOneSystemTick()
    {
        Cpu.TickT();  // now always completes a full instruction or idle cycle
        return true;
    }

    public StepResult RunFrame()
    {
        _running = true;
        RunGuard.Clear();
        Clock.ResetFrameCounter();

        while (Clock.FrameSystemTicks < (ulong)SystemClock.SystemTicksPerFrame)
        {
            Cpu.TickT();

            if (RunGuard.StopRequested)
            {
                _running = false;
                return new StepResult(StopReason.StopRequested, Cpu.TotalTCycles, Cpu.Registers.Pc);
            }
            if (BreakpointChecker is { } check && check(Cpu.Registers.Pc))
            {
                _running = false;
                return new StepResult(StopReason.Breakpoint, Cpu.TotalTCycles, Cpu.Registers.Pc);
            }
        }

        _running = false;
        return new StepResult(StopReason.FrameComplete, Cpu.TotalTCycles, Cpu.Registers.Pc);
    }

    public StepResult StepInstruction()
    {
        _running = true;
        ulong startT = Cpu.TotalTCycles;
        Cpu.TickT();
        _running = false;
        return new StepResult(StopReason.InstructionComplete, Cpu.TotalTCycles - startT, Cpu.Registers.Pc);
    }

    public StepResult StepTCycle()
    {
        // With M-cycle-granular execution we no longer have a true 1-T-cycle
        // step; fall through to StepInstruction and return its cycle count.
        _running = true;
        ulong startT = Cpu.TotalTCycles;
        Cpu.TickT();
        _running = false;
        return new StepResult(StopReason.TCycleComplete, Cpu.TotalTCycles - startT, Cpu.Registers.Pc);
    }

    public StepResult RunUntil(in StopCondition condition)
    {
        _running = true;
        RunGuard.Clear();
        Clock.ResetFrameCounter();
        ulong startT = Cpu.TotalTCycles;
        ulong frameBudget = (ulong)SystemClock.SystemTicksPerFrame;

        while (Clock.FrameSystemTicks < frameBudget)
        {
            Cpu.TickT();

            if (RunGuard.StopRequested)
            {
                _running = false;
                return new StepResult(StopReason.StopRequested, Cpu.TotalTCycles - startT, Cpu.Registers.Pc);
            }

            if (StopConditionMet(in condition))
            {
                _running = false;
                return new StepResult(StopReason.Breakpoint, Cpu.TotalTCycles - startT, Cpu.Registers.Pc);
            }
        }

        _running = false;
        return new StepResult(StopReason.FrameComplete, Cpu.TotalTCycles - startT, Cpu.Registers.Pc);
    }

    private bool StopConditionMet(in StopCondition condition)
    {
        if (condition.Kind == StopConditionKind.None) return false;

        ushort pc = Cpu.Registers.Pc;

        if ((condition.Kind & StopConditionKind.PcEquals) != 0 && pc == condition.PcEquals)
            return true;

        if ((condition.Kind & StopConditionKind.PcInRange) != 0 &&
            pc >= condition.PcRangeStart && pc < condition.PcRangeEnd)
            return true;

        if ((condition.Kind & StopConditionKind.PcLeavesRange) != 0 &&
            (pc < condition.PcRangeStart || pc >= condition.PcRangeEnd))
            return true;

        return false;
    }

    public byte DebugReadByte(ushort address) => Mmu.DebugRead(address);

    public bool DebugWriteByte(ushort address, byte value)
    {
        if (_running) return false;
        return Mmu.DebugWrite(address, value);
    }

    // Test hook only — not part of the public production API.
    internal void SetRunningForTest(bool running) => _running = running;
}
