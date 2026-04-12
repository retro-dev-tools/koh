using Koh.Emulator.Core.Bus;
using Koh.Emulator.Core.Cpu;
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
    public JoypadState Joypad;

    public RunGuard RunGuard { get; } = new();

    private bool _running;

    public GameBoySystem(HardwareMode mode, Cartridge.Cartridge cart)
    {
        Mode = mode;
        Cartridge = cart;
        Timer = new Timer.Timer();
        Io = new IoRegisters(Timer);
        Mmu = new Mmu(cart, Io);
        Cpu = new Sm83(Mmu);
        Ppu = new Ppu.Ppu();
    }

    public ref CpuRegisters Registers => ref Cpu.Registers;
    public Framebuffer Framebuffer => Ppu.Framebuffer;
    public bool IsRunning => _running;

    /// <summary>
    /// Advance one PPU dot. CPU ticks once per system tick in single-speed,
    /// twice in double-speed. See §7.2 for the clocking invariant.
    /// </summary>
    public bool StepOneSystemTick()
    {
        Ppu.TickDot(ref Io.Interrupts);

        int cpuT = Clock.DoubleSpeed ? 2 : 1;
        bool crossedInstructionBoundary = false;
        for (int i = 0; i < cpuT; i++)
        {
            if (Cpu.TickT()) crossedInstructionBoundary = true;
            Timer.TickT(ref Io.Interrupts);
            // OAM DMA and HDMA tick here in Phase 2.
        }

        Clock.AdvanceOne();
        return crossedInstructionBoundary;
    }

    public StepResult RunFrame()
    {
        _running = true;
        RunGuard.Clear();
        Clock.ResetFrameCounter();

        while (Clock.FrameSystemTicks < (ulong)SystemClock.SystemTicksPerFrame)
        {
            bool instrBoundary = StepOneSystemTick();

            if (instrBoundary && RunGuard.StopRequested)
            {
                _running = false;
                return new StepResult(StopReason.StopRequested, Cpu.TotalTCycles, Cpu.Registers.Pc);
            }
        }

        _running = false;
        return new StepResult(StopReason.FrameComplete, Cpu.TotalTCycles, Cpu.Registers.Pc);
    }

    public StepResult StepInstruction()
    {
        _running = true;
        ulong startT = Cpu.TotalTCycles;
        while (true)
        {
            if (StepOneSystemTick())
            {
                _running = false;
                return new StepResult(StopReason.InstructionComplete, Cpu.TotalTCycles - startT, Cpu.Registers.Pc);
            }
        }
    }

    public StepResult StepTCycle()
    {
        _running = true;
        StepOneSystemTick();
        _running = false;
        return new StepResult(StopReason.TCycleComplete, 1, Cpu.Registers.Pc);
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
            bool instrBoundary = StepOneSystemTick();

            if (instrBoundary)
            {
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
