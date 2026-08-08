using Koh.Emulator.Core.Bus;
using Koh.Emulator.Core.Cgb;
using Koh.Emulator.Core.Cpu;
using Koh.Emulator.Core.Dma;
using Koh.Emulator.Core.Joypad;
using Koh.Emulator.Core.Ppu;
using Koh.Emulator.Core.State;

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
    public Apu.Apu Apu { get; }
    public KeyOneRegister KeyOne { get; } = new();
    public JoypadState Joypad;

    public RunGuard RunGuard { get; } = new();

    /// <summary>
    /// Optional breakpoint predicate called at each instruction boundary.
    /// Returning true halts the run loop with <see cref="StopReason.Breakpoint"/>.
    /// </summary>
    public Func<ushort, bool>? BreakpointChecker;

    private bool _running;

    /// <summary>
    /// Constructs the system for <paramref name="cart"/>. <paramref name="mode"/> defaults to
    /// auto-detecting from the cartridge header (<see cref="Cartridge.CartridgeHeader.CgbFlag"/>) —
    /// the same behavior real hardware exhibits when a CGB-capable cartridge is inserted. Pass an
    /// explicit value to force a mode instead (e.g. running a CGB-compatible $80 cartridge in DMG
    /// mode to check its DMG-compatibility path) — mirroring how accurate emulators (SameBoy, mGBA,
    /// BGB) let auto-detection be overridden. Not validated against the header: real DMG hardware has
    /// no concept of the CGB flag to reject in the first place, so an "unsupported" combination (e.g.
    /// forcing DMG on a CGB-only cartridge) is left to behave however the cartridge's own code does.
    /// </summary>
    public GameBoySystem(Cartridge.Cartridge cart, HardwareMode? mode = null)
    {
        var resolvedMode = mode ?? (cart.Header.CgbFlag ? HardwareMode.Cgb : HardwareMode.Dmg);

        Mode = resolvedMode;
        Cartridge = cart;
        Timer = new Timer.Timer();
        Io = new IoRegisters(Timer) { HardwareMode = resolvedMode };
        Mmu = new Mmu(cart, Io);
        Ppu = new Ppu.Ppu(resolvedMode, Mmu.VramArray, Mmu.OamArray);
        Apu = new Apu.Apu(resolvedMode);
        OamDma = new OamDma(Mmu);
        Mmu.AttachOamDma(OamDma);
        Mmu.AttachPpu(Ppu);
        Hdma = new Hdma(Mmu);
        Ppu.HBlankEntered += Hdma.OnHBlankEntered;
        // Real hardware clocks the APU frame sequencer from the falling edge
        // of a fixed bit of the shared Timer's internal counter (DIV-APU),
        // not an independent counter — so a DIV write (which resets that
        // counter) can force a known frame-sequencer phase.
        Timer.FrameSequencerFallingEdge += Apu.FrameSequencer.Advance;
        Apu.DivApuBitHighProvider = () => Timer.DivApuBitHigh;
        Io.AttachPpu(Ppu);
        Io.AttachHdma(Hdma);
        Io.AttachKeyOne(KeyOne);
        Io.AttachBanking(Mmu.Banking);
        Io.AttachApu(Apu);
        Io.AttachJoypad(() => Joypad);

        // Sm83 drives peripheral ticks per memory access: each ReadByte /
        // WriteByte / ReadImmediate / InternalCycle advances one M-cycle.
        Cpu = new Sm83(Mmu, TickForMCycle);
    }

    /// <summary>
    /// Insert a boot ROM, to execute from $0000 before the cartridge sees control.
    /// Deliberately not a constructor parameter: this type has 67 construction sites,
    /// most of them tests that set Pc/Sp directly and must not have a boot ROM run
    /// first. "Won't run without a boot ROM" is an application-layer policy, not an
    /// invariant of the machine — which is also how SameBoy, mGBA, and ares layer it.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The blob's family does not match this machine's mode. A CGB machine running a
    /// 256-byte boot ROM is not a device that exists, and accepting it silently
    /// produces behavior that is very hard to explain later.
    /// </exception>
    /// <exception cref="InvalidOperationException">Execution has already begun.</exception>
    public void LoadBootRom(Boot.BootRom rom)
    {
        var expected = Mode == HardwareMode.Cgb ? Boot.BootRomFamily.Cgb : Boot.BootRomFamily.Dmg;
        if (rom.Family != expected)
        {
            int expectedSize =
                expected == Boot.BootRomFamily.Cgb ? Boot.BootRom.CgbSize : Boot.BootRom.DmgSize;
            throw new ArgumentException(
                $"A {Mode} machine needs a {expected}-family boot ROM ({expectedSize} bytes), "
                    + $"but the supplied boot ROM is {rom.Family}-family "
                    + $"({rom.Bytes.Length} bytes).",
                nameof(rom)
            );
        }

        if (Clock.SystemTicks != 0)
            throw new InvalidOperationException(
                "LoadBootRom must be called before execution begins — a boot ROM inserted "
                    + "mid-run would shadow code the CPU has already executed."
            );

        Mmu.MapBootRom(rom);
    }

    /// <summary>True while boot ROM reads are shadowing the cartridge at $0000.</summary>
    public bool BootRomMapped => Mmu.BootRomMapped;

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
        TickOneMCycle();
    }

    /// <summary>Advance every peripheral and the PPU by one CPU M-cycle.</summary>
    private void TickOneMCycle()
    {
        // Per M-cycle: Timer + OamDma + Hdma tick 4 T-cycles — these are
        // clocked off the CPU clock, so in double-speed mode they tick 2×
        // more per wall-second (same as real hardware: DIV increments twice
        // as fast, HDMA transfers twice as fast, etc.).
        //
        // PPU and APU run at the base 4.19 MHz rate regardless of CPU
        // speed, so in DS they only tick HALF as many times per M-cycle —
        // across 2× as many M-cycles per wall-second that nets out to the
        // same wall-clock rate as normal speed.
        for (int t = 0; t < 4; t++)
        {
            Timer.TickT(ref Io.Interrupts, Clock.DoubleSpeed);
            OamDma.TickT();
            if (Hdma.Active)
                Hdma.TickT();
            if (!Clock.DoubleSpeed || (t & 1) == 0)
                Apu.TickT();
            Io.Serial.TickT(ref Io.Interrupts);
        }

        int ppuDots = Clock.DoubleSpeed ? 2 : 4;
        for (int d = 0; d < ppuDots; d++)
        {
            Ppu.TickDot(ref Io.Interrupts);
            Clock.AdvanceOne();
        }
    }

    /// <summary>
    /// Run one CPU instruction, then drain any general-purpose GDMA it armed.
    /// A GP transfer halts the CPU until it finishes (~8 µs / 16-byte block,
    /// Pan Docs — the same wall-clock cost in single and double speed). We burn
    /// each block's dot cost while ticking the PPU, so a transfer that runs
    /// past VBlank corrupts the scanlines drawn during it, exactly as on
    /// hardware. The CPU is frozen for the whole loop, so it can't race in with
    /// a VBK flip or VRAM write mid-transfer.
    /// </summary>
    private void StepCpu()
    {
        Cpu.TickT();
        while (Hdma.CpuHaltedByGp)
        {
            Hdma.TransferOneGpBlock();
            int blockMCycles = Clock.DoubleSpeed ? 16 : 8; // ×(2 or 4) dots = 32 dots/block
            for (int m = 0; m < blockMCycles; m++)
                TickOneMCycle();
        }
    }

    /// <summary>
    /// Execute one full CPU step (one instruction, or one idle M-cycle when
    /// halted). Peripherals tick internally via the M-cycle callback.
    /// </summary>
    public bool StepOneSystemTick()
    {
        StepCpu(); // now always completes a full instruction or idle cycle
        return true;
    }

    public void WriteState(StateWriter w)
    {
        Clock.WriteState(w);
        Cpu.WriteState(w);
        Timer.WriteState(w);
        Ppu.WriteState(w);
        OamDma.WriteState(w);
        Hdma.WriteState(w);
        Apu.WriteState(w);
        KeyOne.WriteState(w);
        Cartridge.WriteState(w);
        Mmu.WriteState(w);
        Io.WriteState(w);
        Io.Serial.WriteState(w);
    }

    public void ReadState(StateReader r)
    {
        Clock.ReadState(r);
        Cpu.ReadState(r);
        Timer.ReadState(r);
        Ppu.ReadState(r);
        OamDma.ReadState(r);
        Hdma.ReadState(r);
        Apu.ReadState(r);
        KeyOne.ReadState(r);
        Cartridge.ReadState(r);
        Mmu.ReadState(r);
        Io.ReadState(r);
        Io.Serial.ReadState(r);
    }

    public StepResult RunFrame()
    {
        _running = true;
        RunGuard.Clear();
        Clock.ResetFrameCounter();

        while (Clock.FrameSystemTicks < (ulong)SystemClock.SystemTicksPerFrame)
        {
            StepCpu();

            if (RunGuard.StopRequested)
            {
                _running = false;
                return new StepResult(RunGuard.Reason, Cpu.TotalTCycles, Cpu.Registers.Pc);
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
        StepCpu();
        _running = false;
        return new StepResult(
            StopReason.InstructionComplete,
            Cpu.TotalTCycles - startT,
            Cpu.Registers.Pc
        );
    }

    public StepResult StepTCycle()
    {
        // With M-cycle-granular execution we no longer have a true 1-T-cycle
        // step; fall through to StepInstruction and return its cycle count.
        _running = true;
        ulong startT = Cpu.TotalTCycles;
        StepCpu();
        _running = false;
        return new StepResult(
            StopReason.TCycleComplete,
            Cpu.TotalTCycles - startT,
            Cpu.Registers.Pc
        );
    }

    /// <summary>
    /// Runs until <paramref name="condition"/> is met, a <see cref="RunGuard"/> stop is
    /// requested, or (only when the condition includes <see cref="StopConditionKind.Spinning"/>
    /// or <see cref="StopConditionKind.MaxCycles"/>) the run has spun in place or exhausted its
    /// budget.
    ///
    /// <para>
    /// There is no memory-address stop condition here by design: attach a
    /// <c>Koh.Debugger.Session.WatchpointHook</c> (or a
    /// <c>Koh.Emulator.Core.Debug.CompositeMemoryHook</c> to combine it with another hook) to
    /// <see cref="Mmu"/>.Hook before calling <see cref="RunUntil"/> — it calls
    /// <see cref="RunGuard"/>.RequestStop(<see cref="StopReason.Watchpoint"/>) on a matching
    /// read/write, and the <see cref="RunGuard.StopRequested"/> check below already polls for
    /// that every instruction, so no separate mechanism is needed.
    /// </para>
    /// <para>
    /// Only <see cref="StopConditionKind.Spinning"/> and <see cref="StopConditionKind.MaxCycles"/>
    /// self-terminate independent of any particular PC value, so only they license looping past a
    /// single frame — a bare <c>Pc*</c> condition (or no condition at all) keeps the original
    /// single-frame contract (return <see cref="StopReason.FrameComplete"/> after one frame if
    /// never hit), so a caller can never accidentally hang <see cref="RunUntil"/> by omitting a
    /// cap.
    /// </para>
    /// <para>
    /// Spin detection tracks the last up-to-<c>maxSetSize</c> DISTINCT post-instruction PCs seen
    /// (see <see cref="SpinDetected"/>). Every time the current PC is already in that small set,
    /// it counts as "stable"; every time it's a new address, either the set grows (if under the
    /// cap) or is thrown away and restarted from this new address (if at the cap) — either way
    /// the stable counter resets, because seeing a genuinely new address means the program is
    /// still making progress, not spinning. Only when the SAME small set has been fully re-entered
    /// for <c>stableThreshold</c> consecutive instructions (no new address for that whole window)
    /// does this declare spinning. A loop body larger than <c>maxSetSize</c> (e.g. a real counted
    /// copy loop with many instructions per iteration) keeps forcing set resets and so never
    /// accumulates stable steps, no matter how many total iterations it runs — this is what
    /// prevents false positives on a <c>Tilemap.Clear</c>-style 1024x loop.
    /// </para>
    /// <para>
    /// A caller whose real (non-spin) loop body happens to have &lt;=
    /// <see cref="StopCondition.DefaultSpinPcSetSize"/> distinct addresses AND runs more than
    /// <see cref="StopCondition.DefaultSpinStableInstructions"/> iterations will be misreported as
    /// <see cref="StopReason.Spinning"/> — this is an accepted tradeoff of the K/M tuning, which is
    /// exactly why <see cref="StopConditionKind.Spinning"/> is meant to be paired with
    /// <see cref="StopConditionKind.MaxCycles"/> (see <see cref="StopCondition.SpinningOrBudget"/>)
    /// rather than relied on alone.
    /// </para>
    /// </summary>
    public StepResult RunUntil(in StopCondition condition)
    {
        _running = true;
        RunGuard.Clear();
        Clock.ResetFrameCounter();
        ulong startT = Cpu.TotalTCycles;

        // Only Spinning/MaxCycles self-terminate independent of any particular PC value, so only
        // they license looping past a single frame — a bare Pc* condition (or no condition at
        // all) keeps the original single-frame contract (return FrameComplete after one frame if
        // never hit), so a caller can never accidentally hang RunUntil by omitting a cap.
        bool crossFrames =
            (condition.Kind & (StopConditionKind.Spinning | StopConditionKind.MaxCycles)) != 0;

        bool trackSpin = (condition.Kind & StopConditionKind.Spinning) != 0;
        var recentPcs = trackSpin ? new HashSet<ushort>() : null;
        int stableSteps = 0;
        int spinSetSize =
            condition.SpinPcSetSize > 0
                ? condition.SpinPcSetSize
                : StopCondition.DefaultSpinPcSetSize;
        int spinThreshold =
            condition.SpinStableInstructions > 0
                ? condition.SpinStableInstructions
                : StopCondition.DefaultSpinStableInstructions;

        while (true)
        {
            ulong frameBudget = (ulong)SystemClock.SystemTicksPerFrame;
            while (Clock.FrameSystemTicks < frameBudget)
            {
                StepCpu();

                if (RunGuard.StopRequested)
                {
                    _running = false;
                    return new StepResult(
                        RunGuard.Reason,
                        Cpu.TotalTCycles - startT,
                        Cpu.Registers.Pc
                    );
                }

                // Checked before spin detection so a BudgetExceeded hit is never masked by a
                // coincidentally-simultaneous spin declaration — the cap must always be able to
                // report itself distinctly ("loud"), per the anti-footgun goal this primitive
                // exists for.
                if (StopConditionMet(in condition, startT) is { } reason)
                {
                    _running = false;
                    return new StepResult(reason, Cpu.TotalTCycles - startT, Cpu.Registers.Pc);
                }

                if (
                    trackSpin
                    && SpinDetected(
                        recentPcs!,
                        ref stableSteps,
                        Cpu.Registers.Pc,
                        spinSetSize,
                        spinThreshold
                    )
                )
                {
                    _running = false;
                    return new StepResult(
                        StopReason.Spinning,
                        Cpu.TotalTCycles - startT,
                        Cpu.Registers.Pc
                    );
                }
            }

            if (!crossFrames)
            {
                _running = false;
                return new StepResult(
                    StopReason.FrameComplete,
                    Cpu.TotalTCycles - startT,
                    Cpu.Registers.Pc
                );
            }

            Clock.ResetFrameCounter();
        }
    }

    private StopReason? StopConditionMet(in StopCondition condition, ulong startT)
    {
        if (condition.Kind == StopConditionKind.None)
            return null;

        ushort pc = Cpu.Registers.Pc;

        if ((condition.Kind & StopConditionKind.PcEquals) != 0 && pc == condition.PcEquals)
            return StopReason.Breakpoint;

        if (
            (condition.Kind & StopConditionKind.PcInRange) != 0
            && pc >= condition.PcRangeStart
            && pc < condition.PcRangeEnd
        )
            return StopReason.Breakpoint;

        if (
            (condition.Kind & StopConditionKind.PcLeavesRange) != 0
            && (pc < condition.PcRangeStart || pc >= condition.PcRangeEnd)
        )
            return StopReason.Breakpoint;

        if (
            (condition.Kind & StopConditionKind.MaxCycles) != 0
            && (Cpu.TotalTCycles - startT) >= condition.MaxTCycles
        )
            return StopReason.BudgetExceeded;

        return null;
    }

    /// <summary>
    /// Tracks the last up-to-<paramref name="maxSetSize"/> DISTINCT post-instruction PCs seen.
    /// Returns true once the same small set has been fully re-entered for
    /// <paramref name="stableThreshold"/> consecutive instructions with no new address — see the
    /// <see cref="RunUntil"/> doc comment for the full rationale.
    /// </summary>
    private static bool SpinDetected(
        HashSet<ushort> recentPcs,
        ref int stableSteps,
        ushort pc,
        int maxSetSize,
        int stableThreshold
    )
    {
        if (recentPcs.Contains(pc))
        {
            stableSteps++;
        }
        else if (recentPcs.Count < maxSetSize)
        {
            recentPcs.Add(pc);
            stableSteps = 0;
        }
        else
        {
            recentPcs.Clear();
            recentPcs.Add(pc);
            stableSteps = 0;
        }
        return stableSteps >= stableThreshold;
    }

    /// <summary>Press a joypad button and raise the Joypad interrupt on transition.</summary>
    public void JoypadPress(Joypad.JoypadButton button)
    {
        if (Joypad.IsPressed(button))
            return;
        Joypad.Press(button);
        Io.Interrupts.Raise(Interrupts.Joypad);
    }

    public void JoypadRelease(Joypad.JoypadButton button) => Joypad.Release(button);

    public byte DebugReadByte(ushort address) => Mmu.DebugRead(address);

    public bool DebugWriteByte(ushort address, byte value)
    {
        if (_running)
            return false;
        return Mmu.DebugWrite(address, value);
    }

    // Test hook only — not part of the public production API.
    internal void SetRunningForTest(bool running) => _running = running;
}
