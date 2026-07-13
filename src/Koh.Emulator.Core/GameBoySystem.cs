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
        Mmu.AttachPpu(Ppu);
        Hdma = new Hdma(Mmu);
        Ppu.HBlankEntered += Hdma.OnHBlankEntered;
        // Real hardware clocks the APU frame sequencer from the falling edge
        // of a fixed bit of the shared Timer's internal counter (DIV-APU),
        // not an independent counter — so a DIV write (which resets that
        // counter) can force a known frame-sequencer phase.
        Timer.FrameSequencerFallingEdge += Apu.FrameSequencer.Advance;
        Io.AttachPpu(Ppu);
        Io.AttachHdma(Hdma);
        Io.AttachKeyOne(KeyOne);
        Io.AttachBanking(Mmu.Banking);
        Io.AttachApu(Apu);
        Io.AttachJoypad(() => Joypad);

        // Sm83 drives peripheral ticks per memory access: each ReadByte /
        // WriteByte / ReadImmediate / InternalCycle advances one M-cycle.
        Cpu = new Sm83(Mmu, TickForMCycle);

        // Post-boot-ROM CPU state. We skip the boot ROM, so the game must
        // see the canonical register values the boot ROM would have set —
        // in particular A = $11 on CGB is how every CGB-aware game detects
        // color hardware. Without this, CGB-enhanced games (Azure Dreams,
        // Pokémon Gold/Silver, etc.) see A=0 at $0100, take their DMG code
        // path, and never populate VRAM bank 1 attributes.
        ref var r = ref Cpu.Registers;
        if (mode == HardwareMode.Cgb)
        {
            r.A = 0x11;
            r.F = 0x80;
            r.B = 0x00;
            r.C = 0x00;
            r.D = 0xFF;
            r.E = 0x56;
            r.H = 0x00;
            r.L = 0x0D;
        }
        else
        {
            r.A = 0x01;
            r.F = 0xB0;
            r.B = 0x00;
            r.C = 0x13;
            r.D = 0x00;
            r.E = 0xD8;
            r.H = 0x01;
            r.L = 0x4D;
        }

        // Post-boot-ROM VRAM/palette state. We skip the boot ROM, but the
        // real one always clears all of VRAM to $00 before it draws anything
        // (both DMG and CGB boot ROMs open with the same "clear $8000-$9FFF"
        // loop — SameBoy's clean-room dmg_boot.asm/cgb_boot.asm — and the CGB
        // one clears VRAM a second time via HDMA right before hand-off, after
        // fading every BG color palette to white). Mmu poisons VRAM to $FF
        // like the rest of RAM (fc5a251) to catch reads of never-written
        // data; that poison is correct for raw power-on state but wrong for
        // the boot-ROM *hand-off* state modeled here; the two are different
        // layers; this constructor overlays the corrected hand-off state on
        // top. Without this, an all-$FF tilemap byte selects tile $FF, whose
        // (also poisoned) pixel data is solid color id 3 — BGP=$FC maps that
        // to black — while real hardware/mGBA shows white (tile $00 is
        // all-zero pixel data = color id 0 = white under BGP=$FC).
        //
        // We deliberately do NOT model the CGB boot ROM's extra "clear WRAM
        // bank 2" step — that would reintroduce exactly the leniency the
        // $FF WRAM poison exists to catch (a compiled ROM that reads
        // uninitialized WRAM and happens to see 0 here but garbage on real
        // hardware). OAM and HRAM are likewise untouched by any boot ROM and
        // stay poisoned.
        Array.Clear(Mmu.VramArray);
        if (mode == HardwareMode.Cgb)
        {
            // Native/CGB-compatible carts: the CGB boot ROM clears VRAM a
            // SECOND time via HDMA right after fading BG palettes to white,
            // right before hand-off (SameBoy cgb_boot.asm, Preboot: routine),
            // so no logo tile/tilemap remnant survives — unlike DMG below.
            Ppu.BgPalette.FillWhite();
        }
        else
        {
            // The monochrome boot ROM decompresses the cartridge header logo
            // (BootLogo, from the publicly documented bitmap format — not
            // boot ROM code) into tile indices 1-24 at $8010+, and references
            // them from a 12x2 tilemap patch centered on screen columns
            // 4-15, tilemap rows 8-9 — the only two rows it ever touches, so
            // it's never cleared again before hand-off (SameBoy dmg_boot.asm:
            // it jumps straight to reading registers and booting after the
            // "ba-ding!" sound). We deliberately skip the "(R)" trademark
            // glyph (tile $19 in the corner of row 8) since drawing it would
            // mean embedding a Nintendo-specific symbol shape rather than
            // cartridge-supplied data; that one tilemap cell is left at its
            // cleared $00 (blank) instead.
            DrawBootLogoIntoVram();
        }
    }

    /// <summary>Decompress the cartridge-header logo into tiles 1-24 at $8010+ and reference
    /// them from the 12x2 tilemap patch at rows 8-9, cols 4-15 — the picture the monochrome
    /// boot ROM composes before the scroll.</summary>
    private void DrawBootLogoIntoVram()
    {
        var logoTiles = Boot.BootLogo.Decompress(Cartridge.Rom.AsSpan(0x104, 48));
        logoTiles.CopyTo(Mmu.VramArray.AsSpan(0x10));
        const int tilemapBase = 0x1800; // $9800 - $8000
        const int firstCol = 4;
        for (int trow = 0; trow < Boot.BootLogo.TileRows; trow++)
        for (int tcol = 0; tcol < Boot.BootLogo.TileColumns; tcol++)
        {
            int screenRow = 8 + trow;
            int screenCol = firstCol + tcol;
            Mmu.VramArray[tilemapBase + screenRow * 32 + screenCol] = (byte)(
                1 + trow * Boot.BootLogo.TileColumns + tcol
            );
        }
    }

    public ref CpuRegisters Registers => ref Cpu.Registers;
    public Framebuffer Framebuffer => Ppu.Framebuffer;
    public bool IsRunning => _running;

    private int _bootAnimTotalFrames;
    private int _bootAnimFramesRemaining;

    /// <summary>True while an armed <see cref="ArmBootAnimation"/> sequence is still playing.</summary>
    public bool BootAnimationActive => _bootAnimFramesRemaining > 0;

    /// <summary>
    /// Arms the visible HLE boot sequence: the next several <see cref="RunFrame"/>
    /// calls tick peripherals only (no CPU instructions execute yet) while animating
    /// the hand-off state the constructor already put in VRAM, mirroring what the real
    /// (skipped) boot ROM shows on screen before it unmaps itself. Off by default —
    /// callers that never call this see PC=$0100 execute on the very first
    /// <see cref="RunFrame"/>/<see cref="StepInstruction"/>, unchanged from before this
    /// feature existed. Intended for the interactive App only; must be called before the
    /// first <see cref="RunFrame"/>.
    ///
    /// <para>
    /// DMG: scrolls the logo already drawn in VRAM up from below the window to its
    /// resting position (SCY 64 -&gt; 0 over the first ~64 frames, echoing the real boot
    /// ROM's per-frame SCY decrement — SameBoy dmg_boot.asm's <c>.animate</c> loop), then
    /// plays the two-tone "ba-ding!" through APU channel 1 (cheap: a handful of register
    /// pokes at the same NR12/NR13/NR14 addresses the real boot ROM's PlaySound routine
    /// uses) before handing off. This is an approximation of the real timing/exact SCY
    /// steps, not a cycle-accurate reproduction.
    /// </para>
    /// <para>
    /// CGB: the color boot ROM shows the same header logo (statically — no scroll)
    /// before wiping VRAM and fading palettes to white for hand-off. The animation
    /// draws the logo with a temporary black-on-white palette 0, holds it, plays the
    /// ding, then restores the blank hand-off state (VRAM cleared, palettes white)
    /// the constructor established — armed and skipped boots land identically.
    /// </para>
    /// </summary>
    public void ArmBootAnimation()
    {
        if (Mode == HardwareMode.Dmg)
        {
            Ppu.SCY = 64;
            _bootAnimTotalFrames = 90;
        }
        else
        {
            DrawBootLogoIntoVram();
            // Logo pixels use color ids 1-3; hand-off palettes are all white,
            // so give palette 0 a visible black-on-white ramp for the hold.
            for (int slot = 1; slot < 4; slot++)
                Ppu.BgPalette.SetColor(0, slot, 0x0000);
            _bootAnimTotalFrames = 60;
        }
        _bootAnimFramesRemaining = _bootAnimTotalFrames;
    }

    private StepResult RunBootAnimationFrame()
    {
        int elapsed = _bootAnimTotalFrames - _bootAnimFramesRemaining;
        if (Mode == HardwareMode.Dmg)
            Ppu.SCY = (byte)Math.Max(0, 64 - elapsed);

        if (elapsed == 0)
        {
            // Power the APU on the same way the real boot ROM's init does,
            // so the chime below is audible: NR52 on, square-1 envelope,
            // both stereo panning registers open.
            Io.Write(0xFF26, 0x80); // NR52: power on
            Io.Write(0xFF12, 0xF3); // NR12: square-1 volume/envelope
            Io.Write(0xFF25, 0xF3); // NR51: panning
            Io.Write(0xFF24, 0x77); // NR50: master volume
        }
        // Two-tone "ba-ding!": same NR13/NR14 addresses as the real boot
        // ROM's PlaySound routine, fired once each at approximate spacing.
        // Timed from the end of the animation so it plays on both the DMG's
        // 90-frame scroll and the CGB's shorter hold.
        if (elapsed == _bootAnimTotalFrames - 26)
        {
            Io.Write(0xFF13, 0x83);
            Io.Write(0xFF14, 0x87); // bit 7 = trigger
        }
        else if (elapsed == _bootAnimTotalFrames - 21)
        {
            Io.Write(0xFF13, 0xC1);
            Io.Write(0xFF14, 0x87);
        }

        while (Clock.FrameSystemTicks < (ulong)SystemClock.SystemTicksPerFrame)
            TickOneMCycle(); // peripherals only — CPU hasn't started yet

        _bootAnimFramesRemaining--;
        if (_bootAnimFramesRemaining == 0)
        {
            if (Mode == HardwareMode.Dmg)
            {
                Ppu.SCY = 0; // real post-boot SCY
            }
            else
            {
                // Restore the CGB hand-off state the constructor established:
                // the real color boot ROM wipes the logo (second VRAM clear via
                // HDMA) and fades palettes to white right before hand-off, so
                // an armed boot must land exactly where a skipped one does.
                Array.Clear(Mmu.VramArray);
                Ppu.BgPalette.FillWhite();
            }
        }

        _running = false;
        return new StepResult(StopReason.FrameComplete, Cpu.TotalTCycles, Cpu.Registers.Pc);
    }

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

        if (_bootAnimFramesRemaining > 0)
            return RunBootAnimationFrame();

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

    public StepResult RunUntil(in StopCondition condition)
    {
        _running = true;
        RunGuard.Clear();
        Clock.ResetFrameCounter();
        ulong startT = Cpu.TotalTCycles;
        ulong frameBudget = (ulong)SystemClock.SystemTicksPerFrame;

        while (Clock.FrameSystemTicks < frameBudget)
        {
            StepCpu();

            if (RunGuard.StopRequested)
            {
                _running = false;
                return new StepResult(RunGuard.Reason, Cpu.TotalTCycles - startT, Cpu.Registers.Pc);
            }

            if (StopConditionMet(in condition))
            {
                _running = false;
                return new StepResult(
                    StopReason.Breakpoint,
                    Cpu.TotalTCycles - startT,
                    Cpu.Registers.Pc
                );
            }
        }

        _running = false;
        return new StepResult(
            StopReason.FrameComplete,
            Cpu.TotalTCycles - startT,
            Cpu.Registers.Pc
        );
    }

    private bool StopConditionMet(in StopCondition condition)
    {
        if (condition.Kind == StopConditionKind.None)
            return false;

        ushort pc = Cpu.Registers.Pc;

        if ((condition.Kind & StopConditionKind.PcEquals) != 0 && pc == condition.PcEquals)
            return true;

        if (
            (condition.Kind & StopConditionKind.PcInRange) != 0
            && pc >= condition.PcRangeStart
            && pc < condition.PcRangeEnd
        )
            return true;

        if (
            (condition.Kind & StopConditionKind.PcLeavesRange) != 0
            && (pc < condition.PcRangeStart || pc >= condition.PcRangeEnd)
        )
            return true;

        return false;
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
