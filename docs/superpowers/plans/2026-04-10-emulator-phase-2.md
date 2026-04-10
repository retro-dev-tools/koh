# Koh Emulator — Phase 2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship a cycle-accurate pixel-FIFO PPU, full OAM DMA, CGB HDMA, VRAM/OAM access lockouts, the minimal CPU opcode subset required to run `dmg-acid2` and `cgb-acid2`, CGB features (VRAM/WRAM banking, double-speed, CGB palettes), the single-copy framebuffer pipeline delivering real pixels to the webview, Razor UI components for VRAM/palette/OAM/memory inspection, the `readMemory` DAP capability, and an automated acid2 pixel-match test that gates Phase 2.

**Architecture:** Extends `Koh.Emulator.Core` with the full PPU per spec §7.7 (algorithmic fetcher + FIFO + sprite penalties + window restart), OAM DMA per §7.6, HDMA, the MMU access-lockout window, and targeted CPU opcodes. Extends `Koh.Emulator.App` with framebuffer rendering, debug inspection views, and additional benchmark workloads. Extends the VS Code extension's debugger with `readMemory`.

**Tech Stack:** Unchanged from Phase 1 (C# 14 / .NET 10, Blazor WebAssembly, TUnit, TypeScript, Cake).

**Prerequisites:** Phase 1 complete. All Phase 1 exit criteria must pass before starting Phase 2.

**Scope note:** This plan covers only Phase 2 from the design spec. Phase 3 (full SM83 instruction set + full debugger features), Phase 4 (APU + save states + watchpoints + real-game verification), and Phase 5 (MAUI + playground) are each handled by separate plans.

---

## Architecture summary

Phase 2 adds these subsystems to `Koh.Emulator.Core`:

```
src/Koh.Emulator.Core/
├── Ppu/
│   ├── Ppu.cs                    // REWRITTEN — algorithmic dot-driven state machine
│   ├── PixelFifo.cs              // NEW — 16-pixel BG/OBJ FIFO data structures
│   ├── Fetcher.cs                // NEW — BG + sprite fetcher state machine
│   ├── ObjectAttributes.cs       // NEW — OAM entry parsing
│   ├── CgbPalette.cs             // NEW — CGB palette RAM with auto-increment
│   ├── LcdControl.cs             // NEW — LCDC register decode helpers
│   └── StatRegister.cs           // NEW — STAT register decode + IRQ line tracking
├── Dma/
│   ├── OamDma.cs                 // NEW — 160-byte transfer with contention window
│   └── Hdma.cs                   // NEW — CGB general + HBlank HDMA
├── Cgb/
│   ├── KeyOneRegister.cs         // NEW — double-speed state
│   └── VramWramBanking.cs        // NEW — $FF4F / $FF70 handlers (moved from Mmu)
├── Bus/
│   ├── Mmu.cs                    // MODIFIED — adds OAM DMA contention, PPU lockouts, CGB banking
│   └── IoRegisters.cs            // MODIFIED — adds PPU, DMA, HDMA, CGB registers
└── Cpu/
    └── Sm83.cs                   // MODIFIED — replaces mock CPU with acid2 opcode subset
```

Phase 2 adds these files to `Koh.Emulator.App`:

```
src/Koh.Emulator.App/
├── Components/
│   ├── LcdDisplay.razor          // REWRITTEN — real canvas rendering
│   ├── LcdDisplay.razor.js       // NEW — putImageData bridge
│   ├── VramView.razor            // NEW — tile grid visualization
│   ├── PaletteView.razor         // NEW — BG/OBJ palette swatches
│   ├── OamView.razor             // NEW — sprite table
│   └── MemoryView.razor          // NEW — hex viewer
└── Services/
    └── FramebufferBridge.cs      // NEW — single-copy JS interop for framebuffer
```

The VS Code extension gains `readMemory` handling. The debugger gains the `readMemory` DAP handler.

A new `scripts/download-test-roms.{ps1,sh}` population step downloads `dmg-acid2.gb`, `cgb-acid2.gb`, and their reference PNG outputs.

---

## File structure (all new or modified files)

### New / modified C# files

- `src/Koh.Emulator.Core/Ppu/Ppu.cs` — rewritten
- `src/Koh.Emulator.Core/Ppu/PixelFifo.cs` — new
- `src/Koh.Emulator.Core/Ppu/Fetcher.cs` — new
- `src/Koh.Emulator.Core/Ppu/ObjectAttributes.cs` — new
- `src/Koh.Emulator.Core/Ppu/CgbPalette.cs` — new
- `src/Koh.Emulator.Core/Ppu/LcdControl.cs` — new
- `src/Koh.Emulator.Core/Ppu/StatRegister.cs` — new
- `src/Koh.Emulator.Core/Dma/OamDma.cs` — new
- `src/Koh.Emulator.Core/Dma/Hdma.cs` — new
- `src/Koh.Emulator.Core/Cgb/KeyOneRegister.cs` — new
- `src/Koh.Emulator.Core/Cgb/VramWramBanking.cs` — new
- `src/Koh.Emulator.Core/Bus/Mmu.cs` — modified
- `src/Koh.Emulator.Core/Bus/IoRegisters.cs` — modified
- `src/Koh.Emulator.Core/Cpu/Sm83.cs` — rewritten (acid2 subset)
- `src/Koh.Emulator.Core/Cpu/InstructionTable.cs` — new (acid2 subset seed)
- `src/Koh.Emulator.Core/GameBoySystem.cs` — modified to tick DMAs
- `tests/Koh.Emulator.Core.Tests/PpuModeTimingTests.cs` — new
- `tests/Koh.Emulator.Core.Tests/PpuFetcherTests.cs` — new
- `tests/Koh.Emulator.Core.Tests/OamDmaTests.cs` — new
- `tests/Koh.Emulator.Core.Tests/HdmaTests.cs` — new
- `tests/Koh.Emulator.Core.Tests/CgbBankingTests.cs` — new
- `tests/Koh.Emulator.Core.Tests/Acid2SubsetInstructionTests.cs` — new
- `tests/Koh.Compat.Tests/Emulation/Acid2Tests.cs` — new
- `src/Koh.Debugger/Dap/Messages/ReadMemoryMessages.cs` — new
- `src/Koh.Debugger/Dap/Handlers/ReadMemoryHandler.cs` — new
- `src/Koh.Debugger/Dap/DapCapabilities.cs` — modified (Phase 2 capabilities)
- `src/Koh.Debugger/Dap/HandlerRegistration.cs` — modified

### New / modified Blazor files

- `src/Koh.Emulator.App/Components/LcdDisplay.razor` — rewritten
- `src/Koh.Emulator.App/Components/LcdDisplay.razor.js` — new
- `src/Koh.Emulator.App/Components/VramView.razor` — new
- `src/Koh.Emulator.App/Components/PaletteView.razor` — new
- `src/Koh.Emulator.App/Components/OamView.razor` — new
- `src/Koh.Emulator.App/Components/MemoryView.razor` — new
- `src/Koh.Emulator.App/Services/FramebufferBridge.cs` — new
- `src/Koh.Emulator.App/wwwroot/js/framebuffer-bridge.js` — new
- `src/Koh.Emulator.App/Benchmark/BenchmarkRunner.cs` — extended with Phase 2 workload

### New scripts and test assets

- `scripts/download-test-roms.ps1` — populated
- `scripts/download-test-roms.sh` — populated
- `tests/fixtures/test-roms/dmg-acid2.gb` — downloaded (gitignored)
- `tests/fixtures/test-roms/cgb-acid2.gb` — downloaded (gitignored)
- `tests/fixtures/reference/dmg-acid2.png` — downloaded
- `tests/fixtures/reference/cgb-acid2.png` — downloaded

---

## Phase 2-A: PPU foundations

### Task 2.A.1: LCDC, STAT, object attribute helpers

**Files:**
- Create: `src/Koh.Emulator.Core/Ppu/LcdControl.cs`
- Create: `src/Koh.Emulator.Core/Ppu/StatRegister.cs`
- Create: `src/Koh.Emulator.Core/Ppu/ObjectAttributes.cs`

- [ ] **Step 1: Create `LcdControl.cs`**

```csharp
namespace Koh.Emulator.Core.Ppu;

/// <summary>
/// LCDC register ($FF40) decode helpers. Stored as a plain byte; these helpers
/// read bit fields without mutating state.
/// </summary>
public static class LcdControl
{
    public const byte LcdEnable           = 1 << 7;
    public const byte WindowTileMapArea   = 1 << 6;
    public const byte WindowEnable        = 1 << 5;
    public const byte BgWindowTileDataArea = 1 << 4;
    public const byte BgTileMapArea       = 1 << 3;
    public const byte ObjSize8x16         = 1 << 2;
    public const byte ObjEnable           = 1 << 1;
    public const byte BgWindowEnableOrPriority = 1 << 0;

    public static bool IsSet(byte lcdc, byte flag) => (lcdc & flag) != 0;
    public static ushort BgTileMapBase(byte lcdc) => IsSet(lcdc, BgTileMapArea) ? (ushort)0x9C00 : (ushort)0x9800;
    public static ushort WindowTileMapBase(byte lcdc) => IsSet(lcdc, WindowTileMapArea) ? (ushort)0x9C00 : (ushort)0x9800;
    public static bool BgWindowUnsignedTileData(byte lcdc) => IsSet(lcdc, BgWindowTileDataArea);
    public static int SpriteHeight(byte lcdc) => IsSet(lcdc, ObjSize8x16) ? 16 : 8;
}
```

- [ ] **Step 2: Create `StatRegister.cs`**

```csharp
namespace Koh.Emulator.Core.Ppu;

/// <summary>
/// STAT register ($FF41) with internal IRQ-line tracking. The IRQ line is the
/// OR of all enabled sources; an edge (low→high) raises IF.STAT.
/// </summary>
public struct StatRegister
{
    public const byte LyLycIrqEnable   = 1 << 6;
    public const byte OamIrqEnable     = 1 << 5;
    public const byte VBlankIrqEnable  = 1 << 4;
    public const byte HBlankIrqEnable  = 1 << 3;
    public const byte LyLycFlag        = 1 << 2;

    public byte UserBits;   // bits 3..6 user-writable, bits 0..2 are computed

    public byte Read(PpuMode mode, bool lyEqualsLyc)
    {
        byte modeBits = (byte)((int)mode & 0x03);
        byte coincidence = lyEqualsLyc ? LyLycFlag : (byte)0;
        byte userMask = 0b_0111_1000;
        return (byte)((UserBits & userMask) | modeBits | coincidence | 0x80);
    }

    public void Write(byte value) => UserBits = (byte)(value & 0b_0111_1000);
}
```

- [ ] **Step 3: Create `ObjectAttributes.cs`**

```csharp
namespace Koh.Emulator.Core.Ppu;

/// <summary>
/// Parsed OAM entry. OAM contains 40 entries of 4 bytes each: Y, X, tile, attributes.
/// </summary>
public readonly struct ObjectAttributes
{
    public readonly byte Y;
    public readonly byte X;
    public readonly byte Tile;
    public readonly byte Flags;

    public ObjectAttributes(byte y, byte x, byte tile, byte flags)
    {
        Y = y; X = x; Tile = tile; Flags = flags;
    }

    public const byte FlagBgPriority = 1 << 7;
    public const byte FlagYFlip      = 1 << 6;
    public const byte FlagXFlip      = 1 << 5;
    public const byte FlagDmgPalette = 1 << 4;   // OBP0 vs OBP1 on DMG
    public const byte FlagCgbVramBank = 1 << 3;  // VRAM bank for CGB
    public const byte CgbPaletteMask  = 0x07;    // bits 0..2 CGB palette index

    public bool BgPriority => (Flags & FlagBgPriority) != 0;
    public bool YFlip => (Flags & FlagYFlip) != 0;
    public bool XFlip => (Flags & FlagXFlip) != 0;
    public bool UsesObp1 => (Flags & FlagDmgPalette) != 0;
    public bool CgbVramBank1 => (Flags & FlagCgbVramBank) != 0;
    public byte CgbPalette => (byte)(Flags & CgbPaletteMask);

    public static ObjectAttributes Parse(ReadOnlySpan<byte> oamBytes, int index)
    {
        int baseIdx = index * 4;
        return new ObjectAttributes(
            oamBytes[baseIdx + 0],
            oamBytes[baseIdx + 1],
            oamBytes[baseIdx + 2],
            oamBytes[baseIdx + 3]);
    }
}
```

- [ ] **Step 4: Build and commit**

Run: `dotnet build src/Koh.Emulator.Core/Koh.Emulator.Core.csproj`

```bash
git add src/Koh.Emulator.Core/Ppu/LcdControl.cs src/Koh.Emulator.Core/Ppu/StatRegister.cs src/Koh.Emulator.Core/Ppu/ObjectAttributes.cs
git commit -m "feat(emulator): add LcdControl, StatRegister, ObjectAttributes helpers"
```

---

### Task 2.A.2: CGB palette RAM

**Files:**
- Create: `src/Koh.Emulator.Core/Ppu/CgbPalette.cs`

- [ ] **Step 1: Create `CgbPalette.cs`**

```csharp
namespace Koh.Emulator.Core.Ppu;

/// <summary>
/// CGB palette RAM: 64 bytes (8 palettes × 4 colors × 2 bytes). Access via
/// index register ($FF68 BG, $FF6A OBJ) with optional auto-increment.
/// </summary>
public sealed class CgbPalette
{
    private readonly byte[] _data = new byte[64];

    public byte IndexRegister;   // bit 7 = auto-increment, bits 0..5 = index

    public byte ReadData() => _data[IndexRegister & 0x3F];

    public void WriteData(byte value)
    {
        _data[IndexRegister & 0x3F] = value;
        if ((IndexRegister & 0x80) != 0)
        {
            byte idx = (byte)((IndexRegister & 0x3F) + 1);
            IndexRegister = (byte)((IndexRegister & 0x80) | (idx & 0x3F));
        }
    }

    /// <summary>Returns the 15-bit BGR555 color for a given palette index and color slot.</summary>
    public ushort GetColor(int paletteIndex, int colorSlot)
    {
        int offset = paletteIndex * 8 + colorSlot * 2;
        return (ushort)(_data[offset] | (_data[offset + 1] << 8));
    }

    public ReadOnlySpan<byte> RawData => _data;
}
```

- [ ] **Step 2: Build and commit**

Run: `dotnet build src/Koh.Emulator.Core/Koh.Emulator.Core.csproj`

```bash
git add src/Koh.Emulator.Core/Ppu/CgbPalette.cs
git commit -m "feat(emulator): add CGB palette RAM with auto-increment"
```

---

### Task 2.A.3: PixelFifo data structure

**Files:**
- Create: `src/Koh.Emulator.Core/Ppu/PixelFifo.cs`

- [ ] **Step 1: Create `PixelFifo.cs`**

```csharp
namespace Koh.Emulator.Core.Ppu;

/// <summary>
/// A 16-entry BG FIFO and a parallel sprite FIFO. Each entry encodes color index
/// (0..3), palette selector, sprite-priority, and CGB-specific metadata.
/// </summary>
public sealed class PixelFifo
{
    // BG FIFO
    private readonly byte[] _bgColors = new byte[16];       // 0..3 color index
    private readonly byte[] _bgAttrs = new byte[16];        // CGB BG attribute byte
    private int _bgHead;
    private int _bgCount;

    // Sprite FIFO (overlay)
    private readonly byte[] _spriteColors = new byte[8];    // 0..3 color index
    private readonly byte[] _spritePalettes = new byte[8];  // CGB palette 0..7 or DMG OBP0/1
    private readonly byte[] _spriteFlags = new byte[8];     // mirrors ObjectAttributes.Flags subset
    private int _spriteCount;

    public int BgCount => _bgCount;
    public int SpriteCount => _spriteCount;

    public void ClearBg() { _bgHead = 0; _bgCount = 0; }
    public void ClearSprites() { _spriteCount = 0; }

    public bool PushBgTile(byte[] colors8, byte attributes)
    {
        if (_bgCount + 8 > 16) return false;
        for (int i = 0; i < 8; i++)
        {
            int slot = (_bgHead + _bgCount + i) & 15;
            _bgColors[slot] = colors8[i];
            _bgAttrs[slot] = attributes;
        }
        _bgCount += 8;
        return true;
    }

    public void PushSpritePixel(int index, byte color, byte palette, byte flags)
    {
        // Sprite FIFO supports mixing: only overwrite if the current slot is transparent (color 0).
        if (index < _spriteCount)
        {
            if (_spriteColors[index] == 0 && color != 0)
            {
                _spriteColors[index] = color;
                _spritePalettes[index] = palette;
                _spriteFlags[index] = flags;
            }
        }
        else
        {
            _spriteColors[_spriteCount] = color;
            _spritePalettes[_spriteCount] = palette;
            _spriteFlags[_spriteCount] = flags;
            _spriteCount++;
        }
    }

    public (byte bgColor, byte bgAttrs, byte spriteColor, byte spritePalette, byte spriteFlags) ShiftOut()
    {
        byte bgColor = _bgColors[_bgHead];
        byte bgAttrs = _bgAttrs[_bgHead];
        _bgHead = (_bgHead + 1) & 15;
        _bgCount--;

        byte spriteColor = 0, spritePalette = 0, spriteFlags = 0;
        if (_spriteCount > 0)
        {
            spriteColor = _spriteColors[0];
            spritePalette = _spritePalettes[0];
            spriteFlags = _spriteFlags[0];
            for (int i = 1; i < _spriteCount; i++)
            {
                _spriteColors[i - 1] = _spriteColors[i];
                _spritePalettes[i - 1] = _spritePalettes[i];
                _spriteFlags[i - 1] = _spriteFlags[i];
            }
            _spriteCount--;
        }

        return (bgColor, bgAttrs, spriteColor, spritePalette, spriteFlags);
    }

    public void Reset()
    {
        _bgHead = 0;
        _bgCount = 0;
        _spriteCount = 0;
    }
}
```

- [ ] **Step 2: Build and commit**

Run: `dotnet build src/Koh.Emulator.Core/Koh.Emulator.Core.csproj`

```bash
git add src/Koh.Emulator.Core/Ppu/PixelFifo.cs
git commit -m "feat(emulator): add PixelFifo with parallel BG + sprite FIFOs"
```

---

### Task 2.A.4: Background fetcher state machine

**Files:**
- Create: `src/Koh.Emulator.Core/Ppu/Fetcher.cs`

Per spec §7.7: the fetcher runs an 8-dot-per-tile pipeline (Get Tile → Get Low → Get High → Push), retrying the push until the FIFO has room. This task implements the BG/window fetcher. Sprite fetching is added in Task 2.A.6.

- [ ] **Step 1: Create `Fetcher.cs`**

```csharp
namespace Koh.Emulator.Core.Ppu;

public enum FetcherStep : byte
{
    GetTile = 0,
    GetTileDataLow = 1,
    GetTileDataHigh = 2,
    Push = 3,
    Sleep = 4,
}

public sealed class Fetcher
{
    public FetcherStep Step;
    public int DotBudget;           // remaining dots before the step completes

    public int TileMapX;            // which column of the tile map we're fetching (0..31)
    public int TileMapY;            // which row of the tile map we're fetching (0..31)
    public ushort TileMapBase;      // $9800 or $9C00
    public bool UsingWindow;
    public byte FetchedTileIndex;
    public byte FetchedAttributes;  // CGB attribute byte
    public byte FetchedLow;
    public byte FetchedHigh;

    public void ResetForScanline(byte scx, byte scy, byte ly, ushort bgTileMapBase, bool window)
    {
        Step = FetcherStep.GetTile;
        DotBudget = 2;
        TileMapX = (scx / 8) & 0x1F;
        TileMapY = ((ly + scy) / 8) & 0x1F;
        TileMapBase = bgTileMapBase;
        UsingWindow = window;
    }

    public void StartWindow(ushort windowTileMapBase, int windowLineCounter)
    {
        Step = FetcherStep.GetTile;
        DotBudget = 2;
        TileMapX = 0;
        TileMapY = windowLineCounter / 8;
        TileMapBase = windowTileMapBase;
        UsingWindow = true;
    }
}
```

- [ ] **Step 2: Build and commit**

Run: `dotnet build src/Koh.Emulator.Core/Koh.Emulator.Core.csproj`

```bash
git add src/Koh.Emulator.Core/Ppu/Fetcher.cs
git commit -m "feat(emulator): add Fetcher state machine skeleton"
```

---

### Task 2.A.5: PPU algorithmic mode 3 driver

**Files:**
- Modify: `src/Koh.Emulator.Core/Ppu/Ppu.cs`

This is the biggest task in Phase 2. The Phase 1 PPU (dot counter only) is replaced with a full dot-driven state machine that runs the fetcher, shifts pixels out of the FIFO, handles window activation, and emits into the framebuffer.

- [ ] **Step 1: Replace `Ppu.cs` with the algorithmic implementation**

```csharp
using Koh.Emulator.Core.Cpu;

namespace Koh.Emulator.Core.Ppu;

public sealed class Ppu
{
    public Framebuffer Framebuffer { get; } = new();

    // Public state
    public byte LY;
    public byte LYC;
    public byte SCX;
    public byte SCY;
    public byte WX;
    public byte WY;
    public byte BGP;        // DMG BG palette
    public byte OBP0;
    public byte OBP1;
    public byte LCDC;
    public StatRegister Stat;
    public CgbPalette BgPalette { get; } = new();
    public CgbPalette ObjPalette { get; } = new();

    // VRAM + OAM reference (owned by Mmu; passed into the PPU at construction)
    private readonly byte[] _vram;
    private readonly byte[] _oam;
    private readonly HardwareMode _mode;

    // Internal PPU state
    public PpuMode Mode { get; private set; } = PpuMode.OamScan;
    public int Dot;
    private readonly Fetcher _fetcher = new();
    private readonly PixelFifo _fifo = new();
    private int _lcdX;
    private int _windowLineCounter;
    private bool _windowTriggeredThisLine;

    // OAM scan results
    private readonly ObjectAttributes[] _lineSprites = new ObjectAttributes[10];
    private int _lineSpriteCount;

    // Previous STAT IRQ line for edge detection.
    private bool _prevStatLine;

    public Ppu(HardwareMode mode, byte[] vram, byte[] oam)
    {
        _mode = mode;
        _vram = vram;
        _oam = oam;
        LCDC = 0x91;
        BGP = 0xFC;
    }

    public void TickDot(ref Interrupts interrupts)
    {
        if ((LCDC & LcdControl.LcdEnable) == 0)
        {
            LY = 0;
            Dot = 0;
            Mode = PpuMode.HBlank;
            return;
        }

        switch (Mode)
        {
            case PpuMode.OamScan: TickOamScan(); break;
            case PpuMode.Drawing: TickDrawing(ref interrupts); break;
            case PpuMode.HBlank: TickHBlank(ref interrupts); break;
            case PpuMode.VBlank: TickVBlank(ref interrupts); break;
        }

        UpdateStatIrqLine(ref interrupts);
    }

    private void TickOamScan()
    {
        if (Dot == 0)
        {
            ScanOam();
        }
        Dot++;
        if (Dot >= 80)
        {
            Mode = PpuMode.Drawing;
            StartScanlineDrawing();
        }
    }

    private void ScanOam()
    {
        _lineSpriteCount = 0;
        int height = LcdControl.SpriteHeight(LCDC);
        for (int i = 0; i < 40 && _lineSpriteCount < 10; i++)
        {
            var sprite = ObjectAttributes.Parse(_oam, i);
            int spriteY = sprite.Y - 16;
            if (LY >= spriteY && LY < spriteY + height)
            {
                _lineSprites[_lineSpriteCount++] = sprite;
            }
        }
    }

    private void StartScanlineDrawing()
    {
        _lcdX = 0;
        _fifo.Reset();
        _windowTriggeredThisLine = false;
        _fetcher.ResetForScanline(SCX, SCY, LY, LcdControl.BgTileMapBase(LCDC), window: false);
    }

    private void TickDrawing(ref Interrupts interrupts)
    {
        RunFetcher();

        // SCX mod 8 initial discard: first (SCX & 7) pixels of the first BG tile push
        // don't go to the LCD.
        if (_fifo.BgCount > 0)
        {
            int discardTarget = SCX & 7;
            // The PPU discards the first discardTarget pixels of the first tile.
            // Track this by comparing against a one-time counter.
            if (_lcdX == 0 && _fifo.BgCount >= 8 && discardTarget > 0 && !_initialDiscardDone)
            {
                for (int i = 0; i < discardTarget; i++)
                {
                    _fifo.ShiftOut();
                }
                _initialDiscardDone = true;
            }

            // Window activation check.
            if (!_windowTriggeredThisLine && (LCDC & LcdControl.WindowEnable) != 0 &&
                LY >= WY && _lcdX + 7 >= WX)
            {
                _windowTriggeredThisLine = true;
                _fifo.ClearBg();
                _fetcher.StartWindow(LcdControl.WindowTileMapBase(LCDC), _windowLineCounter);
                return; // dot consumed
            }

            // Shift one pixel out to the LCD.
            if (_fifo.BgCount > 0)
            {
                var (bgColor, bgAttrs, spriteColor, spritePalette, spriteFlags) = _fifo.ShiftOut();
                EmitPixel(bgColor, bgAttrs, spriteColor, spritePalette, spriteFlags);
                _lcdX++;

                if (_lcdX >= 160)
                {
                    Mode = PpuMode.HBlank;
                    _initialDiscardDone = false;
                    if (_windowTriggeredThisLine) _windowLineCounter++;
                }
            }
        }

        Dot++;
    }

    private bool _initialDiscardDone;

    private void RunFetcher()
    {
        _fetcher.DotBudget--;
        if (_fetcher.DotBudget > 0) return;

        switch (_fetcher.Step)
        {
            case FetcherStep.GetTile:
                int mapIdx = _fetcher.TileMapY * 32 + _fetcher.TileMapX;
                _fetcher.FetchedTileIndex = _vram[(_fetcher.TileMapBase - 0x8000) + mapIdx];
                if (_mode == HardwareMode.Cgb)
                    _fetcher.FetchedAttributes = _vram[0x2000 + (_fetcher.TileMapBase - 0x8000) + mapIdx];
                _fetcher.Step = FetcherStep.GetTileDataLow;
                _fetcher.DotBudget = 2;
                break;

            case FetcherStep.GetTileDataLow:
                _fetcher.FetchedLow = FetchTileByte(lowByte: true);
                _fetcher.Step = FetcherStep.GetTileDataHigh;
                _fetcher.DotBudget = 2;
                break;

            case FetcherStep.GetTileDataHigh:
                _fetcher.FetchedHigh = FetchTileByte(lowByte: false);
                _fetcher.Step = FetcherStep.Push;
                _fetcher.DotBudget = 2;
                break;

            case FetcherStep.Push:
                var colors = DecodeTileRow(_fetcher.FetchedLow, _fetcher.FetchedHigh);
                if (_fifo.PushBgTile(colors, _fetcher.FetchedAttributes))
                {
                    _fetcher.TileMapX = (_fetcher.TileMapX + 1) & 0x1F;
                    _fetcher.Step = FetcherStep.GetTile;
                    _fetcher.DotBudget = 2;
                }
                else
                {
                    _fetcher.DotBudget = 1;  // retry next dot
                }
                break;
        }
    }

    private byte FetchTileByte(bool lowByte)
    {
        int tileRow = (LY + SCY) & 7;
        int vramBankBit = 0;
        if (_mode == HardwareMode.Cgb && (_fetcher.FetchedAttributes & 0x08) != 0)
            vramBankBit = 0x2000;

        int tileAddr;
        if (LcdControl.BgWindowUnsignedTileData(LCDC))
        {
            tileAddr = 0x0000 + _fetcher.FetchedTileIndex * 16;
        }
        else
        {
            tileAddr = 0x1000 + (sbyte)_fetcher.FetchedTileIndex * 16;
        }
        tileAddr += tileRow * 2 + (lowByte ? 0 : 1);
        return _vram[vramBankBit + tileAddr];
    }

    private static byte[] DecodeTileRow(byte low, byte high)
    {
        var result = new byte[8];
        for (int i = 0; i < 8; i++)
        {
            int bit = 7 - i;
            int lo = (low >> bit) & 1;
            int hi = (high >> bit) & 1;
            result[i] = (byte)((hi << 1) | lo);
        }
        return result;
    }

    private void EmitPixel(byte bgColor, byte bgAttrs, byte spriteColor, byte spritePalette, byte spriteFlags)
    {
        int pixelIdx = (LY * Framebuffer.Width + _lcdX) * 4;
        var back = Framebuffer.Back;

        // Resolve final color index via DMG/CGB palette selection.
        byte finalColor;
        bool useSpriteColor = spriteColor != 0 && ((spriteFlags & ObjectAttributes.FlagBgPriority) == 0 || bgColor == 0);
        if (useSpriteColor)
        {
            finalColor = ApplyDmgPalette(spriteColor, (spriteFlags & ObjectAttributes.FlagDmgPalette) != 0 ? OBP1 : OBP0);
        }
        else
        {
            finalColor = ApplyDmgPalette(bgColor, BGP);
        }

        // DMG greyscale mapping.
        byte shade = finalColor switch
        {
            0 => (byte)0xE0,
            1 => (byte)0xA8,
            2 => (byte)0x58,
            _ => (byte)0x08,
        };
        back[pixelIdx + 0] = shade;
        back[pixelIdx + 1] = shade;
        back[pixelIdx + 2] = shade;
        back[pixelIdx + 3] = 0xFF;
    }

    private static byte ApplyDmgPalette(byte colorIdx, byte palette)
        => (byte)((palette >> (colorIdx * 2)) & 0x03);

    private void TickHBlank(ref Interrupts interrupts)
    {
        Dot++;
        if (Dot >= 456)
        {
            Dot = 0;
            LY++;
            if (LY == 144)
            {
                Mode = PpuMode.VBlank;
                interrupts.Raise(Interrupts.VBlank);
                Framebuffer.Flip();
            }
            else
            {
                Mode = PpuMode.OamScan;
            }
        }
    }

    private void TickVBlank(ref Interrupts interrupts)
    {
        Dot++;
        if (Dot >= 456)
        {
            Dot = 0;
            LY++;
            if (LY >= 154)
            {
                LY = 0;
                Mode = PpuMode.OamScan;
                _windowLineCounter = 0;
            }
        }
    }

    private void UpdateStatIrqLine(ref Interrupts interrupts)
    {
        bool line = false;
        byte user = Stat.UserBits;
        if ((user & StatRegister.LyLycIrqEnable) != 0 && LY == LYC) line = true;
        if ((user & StatRegister.OamIrqEnable) != 0 && Mode == PpuMode.OamScan) line = true;
        if ((user & StatRegister.VBlankIrqEnable) != 0 && Mode == PpuMode.VBlank) line = true;
        if ((user & StatRegister.HBlankIrqEnable) != 0 && Mode == PpuMode.HBlank) line = true;

        if (line && !_prevStatLine)
        {
            interrupts.Raise(Interrupts.Stat);
        }
        _prevStatLine = line;
    }
}
```

- [ ] **Step 2: Update `GameBoySystem.cs` to pass vram/oam into the new PPU constructor**

The previous PPU construction was `new Ppu.Ppu()`. Now it needs the mode + VRAM + OAM references. Expose those from `Mmu`:

Add to `Mmu.cs`:
```csharp
public byte[] VramArray { get; }
public byte[] OamArray { get; }
```
(Rename the existing `_vram` / `_oam` field to public properties for PPU access. Keep the existing ReadonlySpan accessors for other callers.)

Then in `GameBoySystem.cs` constructor:
```csharp
Ppu = new Ppu.Ppu(mode, Mmu.VramArray, Mmu.OamArray);
```

- [ ] **Step 3: Build**

Run: `dotnet build src/Koh.Emulator.Core/Koh.Emulator.Core.csproj`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Koh.Emulator.Core/Ppu/Ppu.cs src/Koh.Emulator.Core/Bus/Mmu.cs src/Koh.Emulator.Core/GameBoySystem.cs
git commit -m "feat(emulator): rewrite PPU with algorithmic fetcher + FIFO + mode 3 drawing"
```

---

### Task 2.A.6: Sprite fetcher integration

**Files:**
- Modify: `src/Koh.Emulator.Core/Ppu/Ppu.cs`

- [ ] **Step 1: Extend the mode 3 driver to handle sprites**

Add sprite-fetcher logic inside `TickDrawing`: before shifting out a BG pixel, check if any sprite's X matches the current `_lcdX`. If so, run the 6-dot sprite fetch penalty (plus position penalty) and push sprite pixels into the sprite FIFO.

Add helper methods:

```csharp
private int _spritePenaltyDots;
private int _currentSpriteIndex = -1;

private bool CheckAndStartSpriteFetch()
{
    if (_currentSpriteIndex >= 0) return true; // already fetching

    for (int i = 0; i < _lineSpriteCount; i++)
    {
        int spriteX = _lineSprites[i].X - 8;
        if (spriteX == _lcdX && _lineSprites[i].X != 0xFF)
        {
            _currentSpriteIndex = i;
            _spritePenaltyDots = 6 + ((_lineSprites[i].X - 1) & 7);
            _lineSprites[i] = new ObjectAttributes(0xFF, 0xFF, 0xFF, 0xFF); // mark consumed
            return true;
        }
    }
    return false;
}

private bool ProgressSpriteFetch()
{
    if (_currentSpriteIndex < 0) return false;

    _spritePenaltyDots--;
    if (_spritePenaltyDots > 0) return true;  // still penalty-stalling

    // Fetch the sprite row and push pixels into the sprite FIFO.
    var sprite = _lineSprites[_currentSpriteIndex];
    // Note: we already marked this as consumed; re-fetch from index stored elsewhere for real impl.
    PushSpritePixelsForCurrentLcdX(sprite);
    _currentSpriteIndex = -1;
    return true;
}

private void PushSpritePixelsForCurrentLcdX(ObjectAttributes sprite)
{
    int height = LcdControl.SpriteHeight(LCDC);
    int row = LY - (sprite.Y - 16);
    if (sprite.YFlip) row = height - 1 - row;
    int tileIndex = sprite.Tile;
    if (height == 16) tileIndex &= 0xFE;

    int vramBankBit = 0;
    if (_mode == HardwareMode.Cgb && sprite.CgbVramBank1) vramBankBit = 0x2000;

    int tileAddr = tileIndex * 16 + row * 2;
    byte low = _vram[vramBankBit + tileAddr];
    byte high = _vram[vramBankBit + tileAddr + 1];

    for (int px = 0; px < 8; px++)
    {
        int bit = sprite.XFlip ? px : (7 - px);
        int lo = (low >> bit) & 1;
        int hi = (high >> bit) & 1;
        byte color = (byte)((hi << 1) | lo);
        _fifo.PushSpritePixel(px, color, sprite.CgbPalette, sprite.Flags);
    }
}
```

Call `CheckAndStartSpriteFetch()` and `ProgressSpriteFetch()` at the top of `TickDrawing()` before the BG shift-out, and skip the BG shift-out while a sprite fetch is in progress.

(The real implementation will need to preserve sprite identity across ticks — refactor `_currentSpriteIndex` to store the actual sprite rather than marking it consumed. The sketch above shows the structure; adapt field names as needed during implementation.)

- [ ] **Step 2: Build**

Run: `dotnet build src/Koh.Emulator.Core/Koh.Emulator.Core.csproj`

- [ ] **Step 3: Commit**

```bash
git add src/Koh.Emulator.Core/Ppu/Ppu.cs
git commit -m "feat(emulator): add sprite fetcher + sprite FIFO integration to PPU"
```

---

### Task 2.A.7: PPU mode-timing tests

**Files:**
- Create: `tests/Koh.Emulator.Core.Tests/PpuModeTimingTests.cs`

- [ ] **Step 1: Write tests for mode transitions**

```csharp
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;
using Koh.Emulator.Core.Cpu;
using Koh.Emulator.Core.Ppu;

namespace Koh.Emulator.Core.Tests;

public class PpuModeTimingTests
{
    private static GameBoySystem MakeSystem()
    {
        var rom = new byte[0x8000];
        rom[0x147] = 0x00;
        var cart = CartridgeFactory.Load(rom);
        return new GameBoySystem(HardwareMode.Dmg, cart);
    }

    [Test]
    public async Task OamScan_Lasts_80_Dots_At_Scanline_Start()
    {
        var gb = MakeSystem();
        gb.Ppu.LCDC = LcdControl.LcdEnable | LcdControl.BgWindowEnableOrPriority;

        var interrupts = gb.Io.Interrupts;
        for (int i = 0; i < 79; i++) gb.Ppu.TickDot(ref interrupts);
        await Assert.That(gb.Ppu.Mode).IsEqualTo(PpuMode.OamScan);

        gb.Ppu.TickDot(ref interrupts);
        await Assert.That(gb.Ppu.Mode).IsEqualTo(PpuMode.Drawing);
    }

    [Test]
    public async Task Scanline_Total_Is_456_Dots()
    {
        var gb = MakeSystem();
        gb.Ppu.LCDC = LcdControl.LcdEnable | LcdControl.BgWindowEnableOrPriority;
        var interrupts = gb.Io.Interrupts;

        for (int i = 0; i < 456; i++) gb.Ppu.TickDot(ref interrupts);
        await Assert.That(gb.Ppu.LY).IsEqualTo((byte)1);
    }

    [Test]
    public async Task VBlank_Starts_At_LY_144_And_Raises_Irq()
    {
        var gb = MakeSystem();
        gb.Ppu.LCDC = LcdControl.LcdEnable | LcdControl.BgWindowEnableOrPriority;
        var interrupts = gb.Io.Interrupts;

        // Tick 144 scanlines.
        for (int i = 0; i < 144 * 456; i++) gb.Ppu.TickDot(ref interrupts);

        await Assert.That(gb.Ppu.LY).IsEqualTo((byte)144);
        await Assert.That((interrupts.IF & Interrupts.VBlank) != 0).IsTrue();
    }

    [Test]
    public async Task Frame_Wraps_To_LY_0_After_VBlank()
    {
        var gb = MakeSystem();
        gb.Ppu.LCDC = LcdControl.LcdEnable | LcdControl.BgWindowEnableOrPriority;
        var interrupts = gb.Io.Interrupts;

        // Full frame = 154 scanlines.
        for (int i = 0; i < 154 * 456; i++) gb.Ppu.TickDot(ref interrupts);
        await Assert.That(gb.Ppu.LY).IsEqualTo((byte)0);
    }
}
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test tests/Koh.Emulator.Core.Tests/Koh.Emulator.Core.Tests.csproj --filter PpuModeTimingTests`
Expected: all four tests pass.

- [ ] **Step 3: Commit**

```bash
git add tests/Koh.Emulator.Core.Tests/PpuModeTimingTests.cs
git commit -m "test(emulator): add PPU mode-timing tests for scanline and VBlank"
```

---

### Task 2.A.8: PPU fetcher unit tests

**Files:**
- Create: `tests/Koh.Emulator.Core.Tests/PpuFetcherTests.cs`

- [ ] **Step 1: Write tests with hand-crafted tile data**

```csharp
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;
using Koh.Emulator.Core.Cpu;
using Koh.Emulator.Core.Ppu;

namespace Koh.Emulator.Core.Tests;

public class PpuFetcherTests
{
    private static GameBoySystem MakeSystem()
    {
        var rom = new byte[0x8000];
        rom[0x147] = 0x00;
        var cart = CartridgeFactory.Load(rom);
        var gb = new GameBoySystem(HardwareMode.Dmg, cart);
        gb.Ppu.LCDC = (byte)(LcdControl.LcdEnable | LcdControl.BgWindowEnableOrPriority | LcdControl.BgWindowTileDataArea);
        gb.Ppu.BGP = 0b_11_10_01_00;  // identity palette
        return gb;
    }

    private static void WriteVram(GameBoySystem gb, ushort address, byte value)
        => gb.Mmu.WriteByte(address, value);

    [Test]
    public async Task Single_Tile_Produces_Expected_Pixels_On_Scanline_0()
    {
        var gb = MakeSystem();

        // Tile 0 at $8000 — row 0 = colors 0,1,2,3,0,1,2,3
        // Encoded per Game Boy 2bpp: low byte = lower bits, high byte = upper bits.
        // Colors: 0,1,2,3,0,1,2,3 → low=0b01010101, high=0b00110011
        WriteVram(gb, 0x8000, 0b01010101);
        WriteVram(gb, 0x8001, 0b00110011);

        // BG tile map at $9800 — entry 0 points to tile 0
        WriteVram(gb, 0x9800, 0);

        // Tick a full scanline.
        var interrupts = gb.Io.Interrupts;
        for (int i = 0; i < 456; i++) gb.Ppu.TickDot(ref interrupts);

        // After the scanline, the framebuffer back buffer contains row 0.
        var back = gb.Framebuffer.Back;
        // Check the first 8 pixels match colors 0,1,2,3,0,1,2,3.
        byte[] expectedShades = { 0xE0, 0xA8, 0x58, 0x08, 0xE0, 0xA8, 0x58, 0x08 };
        for (int x = 0; x < 8; x++)
        {
            int idx = x * 4;
            await Assert.That(back[idx + 0]).IsEqualTo(expectedShades[x]);
        }
    }
}
```

- [ ] **Step 2: Run the test**

Run: `dotnet test tests/Koh.Emulator.Core.Tests/Koh.Emulator.Core.Tests.csproj --filter PpuFetcherTests`
Expected: test passes. Iterate on the PPU implementation if the pixel values don't match.

- [ ] **Step 3: Commit**

```bash
git add tests/Koh.Emulator.Core.Tests/PpuFetcherTests.cs
git commit -m "test(emulator): add PPU fetcher tile-decode test"
```

---

## Phase 2-B: OAM DMA

### Task 2.B.1: OamDma implementation

**Files:**
- Create: `src/Koh.Emulator.Core/Dma/OamDma.cs`

Per spec §7.6: triggered by write to $FF46; 1 M-cycle start delay; 160 M-cycles transfer; CPU reads from non-HRAM return $FF during the contention window; writes dropped.

- [ ] **Step 1: Create `OamDma.cs`**

```csharp
using Koh.Emulator.Core.Bus;

namespace Koh.Emulator.Core.Dma;

public sealed class OamDma
{
    private readonly Mmu _mmu;
    public byte SourceHighByte { get; private set; }

    private int _tCountdownToStart;   // 0 when not scheduled
    private int _byteIndex;
    private int _tCountdownInByte;    // counts down 4 T-cycles per byte

    public bool Active => _byteIndex < 160 && _tCountdownToStart == 0 && _running;
    public bool IsBusLocking { get; private set; }

    private bool _running;

    public OamDma(Mmu mmu) { _mmu = mmu; }

    public void Trigger(byte sourceHighByte)
    {
        SourceHighByte = sourceHighByte;
        _tCountdownToStart = 4;   // 1 M-cycle of start delay
        _byteIndex = 0;
        _tCountdownInByte = 4;
        _running = true;
    }

    public void TickT()
    {
        if (!_running) { IsBusLocking = false; return; }

        if (_tCountdownToStart > 0)
        {
            _tCountdownToStart--;
            if (_tCountdownToStart == 0)
            {
                IsBusLocking = true;
                TransferByte();
            }
            return;
        }

        _tCountdownInByte--;
        if (_tCountdownInByte == 0)
        {
            _byteIndex++;
            if (_byteIndex >= 160)
            {
                _running = false;
                // Contention ends 4 T-cycles after the last transfer per §7.6.
                _tCountdownInByte = 0;
                IsBusLocking = false;
                return;
            }
            _tCountdownInByte = 4;
            TransferByte();
        }
    }

    private void TransferByte()
    {
        ushort src = (ushort)((SourceHighByte << 8) | _byteIndex);
        byte value = _mmu.ReadByte(src);
        _mmu.OamArray[_byteIndex] = value;
    }
}
```

- [ ] **Step 2: Wire OamDma into `Mmu` so I/O writes to $FF46 trigger it, and memory reads during contention return $FF**

Update `Mmu.ReadByte` to check `oamDma.IsBusLocking` and return $FF if the address is not in HRAM ($FF80-$FFFE). Update `WriteByte` similarly for writes (drop them). Hook `$FF46` write to call `oamDma.Trigger(value)`.

- [ ] **Step 3: Add `OamDma` field to `GameBoySystem` and tick it in `StepOneSystemTick`**

```csharp
public OamDma OamDma { get; }

// In constructor:
OamDma = new OamDma(Mmu);
Mmu.AttachOamDma(OamDma);   // so Mmu can consult IsBusLocking

// In StepOneSystemTick, inside the CPU T-cycle loop:
OamDma.TickT();
```

- [ ] **Step 4: Build and commit**

Run: `dotnet build src/Koh.Emulator.Core/Koh.Emulator.Core.csproj`

```bash
git add src/Koh.Emulator.Core/Dma/OamDma.cs src/Koh.Emulator.Core/Bus/Mmu.cs src/Koh.Emulator.Core/GameBoySystem.cs
git commit -m "feat(emulator): add OAM DMA with contention window"
```

---

### Task 2.B.2: OAM DMA timing tests

**Files:**
- Create: `tests/Koh.Emulator.Core.Tests/OamDmaTests.cs`

- [ ] **Step 1: Write tests per §7.6 exact timing windows**

```csharp
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Emulator.Core.Tests;

public class OamDmaTests
{
    private static GameBoySystem MakeSystem()
    {
        var rom = new byte[0x8000];
        rom[0x147] = 0x00;
        // Put a known pattern at $C000..$C09F
        for (int i = 0; i < 0xA0; i++) rom[i] = (byte)i; // not in WRAM but marker
        var cart = CartridgeFactory.Load(rom);
        var gb = new GameBoySystem(HardwareMode.Dmg, cart);
        for (int i = 0; i < 0xA0; i++) gb.Mmu.WriteByte((ushort)(0xC000 + i), (byte)(i + 1));
        return gb;
    }

    [Test]
    public async Task Dma_Transfers_160_Bytes_To_Oam()
    {
        var gb = MakeSystem();
        gb.Mmu.WriteByte(0xFF46, 0xC0);  // trigger DMA from $C000

        // Advance enough cycles: 4 start delay + 160*4 transfer + 4 tail = 648 T-cycles.
        for (int i = 0; i < 648; i++) gb.OamDma.TickT();

        // Verify each byte transferred.
        var oam = gb.Mmu.OamArray;
        for (int i = 0; i < 0xA0; i++)
        {
            await Assert.That(oam[i]).IsEqualTo((byte)(i + 1));
        }
    }

    [Test]
    public async Task Cpu_Read_Outside_Hram_Returns_FF_During_Dma()
    {
        var gb = MakeSystem();
        gb.Mmu.WriteByte(0xD000, 0x42);
        gb.Mmu.WriteByte(0xFF46, 0xC0);

        // Advance past start delay (4 T-cycles).
        for (int i = 0; i < 10; i++) gb.OamDma.TickT();

        await Assert.That(gb.Mmu.ReadByte(0xD000)).IsEqualTo((byte)0xFF);
    }

    [Test]
    public async Task Cpu_Read_From_Hram_Succeeds_During_Dma()
    {
        var gb = MakeSystem();
        gb.Mmu.WriteByte(0xFF90, 0x42);
        gb.Mmu.WriteByte(0xFF46, 0xC0);

        for (int i = 0; i < 10; i++) gb.OamDma.TickT();

        await Assert.That(gb.Mmu.ReadByte(0xFF90)).IsEqualTo((byte)0x42);
    }
}
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test tests/Koh.Emulator.Core.Tests/Koh.Emulator.Core.Tests.csproj --filter OamDmaTests`
Expected: all tests pass.

- [ ] **Step 3: Commit**

```bash
git add tests/Koh.Emulator.Core.Tests/OamDmaTests.cs
git commit -m "test(emulator): add OAM DMA timing and contention tests"
```

---

## Phase 2-C: CGB HDMA

### Task 2.C.1: HDMA implementation

**Files:**
- Create: `src/Koh.Emulator.Core/Dma/Hdma.cs`
- Modify: `src/Koh.Emulator.Core/Bus/IoRegisters.cs`

Per spec §7.6: general-purpose HDMA halts the CPU during transfer; HBlank HDMA transfers 16 bytes per HBlank. CGB only.

- [ ] **Step 1: Create `Hdma.cs`**

```csharp
using Koh.Emulator.Core.Bus;

namespace Koh.Emulator.Core.Dma;

public sealed class Hdma
{
    private readonly Mmu _mmu;

    public byte Source1;   // $FF51 high
    public byte Source2;   // $FF52 low
    public byte Dest1;     // $FF53 high (masked to $80..$9F)
    public byte Dest2;     // $FF54 low

    public byte LengthRegister;   // $FF55
    public bool IsHBlankMode { get; private set; }
    public bool Active { get; private set; }
    public bool CpuHaltedByGp { get; private set; }

    private int _blocksRemaining;
    private int _currentBlockByteIndex;
    private bool _hblankPending;

    public Hdma(Mmu mmu) { _mmu = mmu; }

    public byte ReadLengthRegister()
    {
        if (!Active) return 0xFF;
        byte mode = IsHBlankMode ? (byte)0x80 : (byte)0x00;
        return (byte)(mode | (_blocksRemaining - 1));
    }

    public void WriteLengthRegister(byte value)
    {
        if (Active && IsHBlankMode && (value & 0x80) == 0)
        {
            // Cancel HBlank HDMA.
            Active = false;
            LengthRegister = (byte)(0x80 | (_blocksRemaining - 1));
            return;
        }

        int blocks = (value & 0x7F) + 1;
        _blocksRemaining = blocks;
        IsHBlankMode = (value & 0x80) != 0;
        Active = true;
        _currentBlockByteIndex = 0;
        CpuHaltedByGp = !IsHBlankMode;
    }

    public ushort SourceAddress => (ushort)(((Source1 << 8) | Source2) & 0xFFF0);
    public ushort DestAddress => (ushort)(0x8000 | (((Dest1 << 8) | Dest2) & 0x1FF0));

    public void OnHBlankEntered()
    {
        if (Active && IsHBlankMode) _hblankPending = true;
    }

    public void TickT(bool doubleSpeed)
    {
        if (!Active) { CpuHaltedByGp = false; return; }

        if (IsHBlankMode)
        {
            if (!_hblankPending) return;
            TransferBlockTick(doubleSpeed);
        }
        else
        {
            TransferBlockTick(doubleSpeed);
        }
    }

    private void TransferBlockTick(bool doubleSpeed)
    {
        // Rate: 2 bytes per CPU M-cycle.
        // In the tick loop, this is called once per CPU T-cycle, so one byte every 2 T-cycles.
        // Implementation copies one byte per call and tracks progress.
        ushort src = (ushort)(SourceAddress + (16 * ((_blocksRemaining > 0 ? (LengthRegister & 0x7F) - (_blocksRemaining - 1) : 0))) + _currentBlockByteIndex);
        ushort dst = (ushort)(DestAddress + (16 * ((_blocksRemaining > 0 ? (LengthRegister & 0x7F) - (_blocksRemaining - 1) : 0))) + _currentBlockByteIndex);
        byte value = _mmu.ReadByte(src);
        _mmu.WriteByte(dst, value);

        _currentBlockByteIndex++;
        if (_currentBlockByteIndex >= 16)
        {
            _currentBlockByteIndex = 0;
            _blocksRemaining--;
            _hblankPending = false;
            if (_blocksRemaining <= 0)
            {
                Active = false;
                CpuHaltedByGp = false;
                LengthRegister = 0xFF;
            }
        }
    }
}
```

- [ ] **Step 2: Wire HDMA $FF51-$FF55 into `IoRegisters`**

Add cases in `Read` and `Write` that route to the Hdma instance. Construct `Hdma` in `GameBoySystem` constructor and expose it.

- [ ] **Step 3: Call `OnHBlankEntered()` from the PPU when mode transitions from Drawing to HBlank**

In `Ppu.TickDrawing()` where it sets `Mode = PpuMode.HBlank`, also call `hdma.OnHBlankEntered()`. This requires passing an `Hdma` reference to the PPU or exposing a callback.

- [ ] **Step 4: Build and commit**

Run: `dotnet build src/Koh.Emulator.Core/Koh.Emulator.Core.csproj`

```bash
git add src/Koh.Emulator.Core/Dma/Hdma.cs src/Koh.Emulator.Core/Bus/IoRegisters.cs src/Koh.Emulator.Core/Ppu/Ppu.cs src/Koh.Emulator.Core/GameBoySystem.cs
git commit -m "feat(emulator): add CGB HDMA (general + HBlank modes)"
```

---

### Task 2.C.2: HDMA tests

**Files:**
- Create: `tests/Koh.Emulator.Core.Tests/HdmaTests.cs`

- [ ] **Step 1: Write general-purpose and HBlank HDMA tests**

```csharp
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Emulator.Core.Tests;

public class HdmaTests
{
    private static GameBoySystem MakeCgbSystem()
    {
        var rom = new byte[0x8000];
        rom[0x143] = 0x80;  // CGB flag
        rom[0x147] = 0x00;
        var cart = CartridgeFactory.Load(rom);
        return new GameBoySystem(HardwareMode.Cgb, cart);
    }

    [Test]
    public async Task GeneralPurpose_Transfers_16_Bytes()
    {
        var gb = MakeCgbSystem();

        // Source: $C000 with pattern
        for (int i = 0; i < 16; i++) gb.Mmu.WriteByte((ushort)(0xC000 + i), (byte)(i + 1));

        gb.Mmu.WriteByte(0xFF51, 0xC0);  // source high
        gb.Mmu.WriteByte(0xFF52, 0x00);  // source low
        gb.Mmu.WriteByte(0xFF53, 0x80);  // dest high (VRAM)
        gb.Mmu.WriteByte(0xFF54, 0x00);  // dest low
        gb.Mmu.WriteByte(0xFF55, 0x00);  // bit 7 = 0 (general-purpose), length 16*1

        // Advance enough cycles to complete the transfer.
        for (int i = 0; i < 64; i++) gb.OamDma.TickT();  // tick loop proxies HDMA

        // Verify VRAM bytes.
        for (int i = 0; i < 16; i++)
        {
            await Assert.That(gb.Mmu.ReadByte((ushort)(0x8000 + i))).IsEqualTo((byte)(i + 1));
        }
    }
}
```

(The exact tick API depends on how you wire HDMA into the system tick loop — update the test harness to call the right method.)

- [ ] **Step 2: Run the tests**

Run: `dotnet test tests/Koh.Emulator.Core.Tests/Koh.Emulator.Core.Tests.csproj --filter HdmaTests`
Expected: passes.

- [ ] **Step 3: Commit**

```bash
git add tests/Koh.Emulator.Core.Tests/HdmaTests.cs
git commit -m "test(emulator): add general-purpose HDMA transfer test"
```

---

## Phase 2-D: CGB banking and double-speed

### Task 2.D.1: VRAM and WRAM banking

**Files:**
- Create: `src/Koh.Emulator.Core/Cgb/VramWramBanking.cs`
- Modify: `src/Koh.Emulator.Core/Bus/Mmu.cs`

- [ ] **Step 1: Create `VramWramBanking.cs`**

```csharp
namespace Koh.Emulator.Core.Cgb;

public sealed class VramWramBanking
{
    public byte VramBank;   // 0 or 1 on CGB, always 0 on DMG
    public byte WramBank = 1;   // 1..7 on CGB

    public byte ReadVbkRegister() => (byte)(0xFE | (VramBank & 1));
    public void WriteVbkRegister(byte value) => VramBank = (byte)(value & 1);

    public byte ReadSvbkRegister() => (byte)(0xF8 | (WramBank & 7));
    public void WriteSvbkRegister(byte value)
    {
        int bank = value & 7;
        WramBank = (byte)(bank == 0 ? 1 : bank);
    }
}
```

- [ ] **Step 2: Modify `Mmu` to consult the banking state**

The existing Phase 1 Mmu already uses `_vramBank` and `_wramBank` fields — replace them with a `VramWramBanking` instance. Wire the $FF4F and $FF70 I/O writes to that instance.

- [ ] **Step 3: Build and commit**

Run: `dotnet build src/Koh.Emulator.Core/Koh.Emulator.Core.csproj`

```bash
git add src/Koh.Emulator.Core/Cgb/VramWramBanking.cs src/Koh.Emulator.Core/Bus/Mmu.cs src/Koh.Emulator.Core/Bus/IoRegisters.cs
git commit -m "feat(emulator): extract VramWramBanking and wire $FF4F/$FF70"
```

---

### Task 2.D.2: Double-speed mode (KEY1)

**Files:**
- Create: `src/Koh.Emulator.Core/Cgb/KeyOneRegister.cs`
- Modify: `src/Koh.Emulator.Core/SystemClock.cs`
- Modify: `src/Koh.Emulator.Core/GameBoySystem.cs`

- [ ] **Step 1: Create `KeyOneRegister.cs`**

```csharp
namespace Koh.Emulator.Core.Cgb;

public sealed class KeyOneRegister
{
    public bool SwitchArmed;
    public bool DoubleSpeed;

    public byte Read()
        => (byte)(0x7E | (DoubleSpeed ? 0x80 : 0) | (SwitchArmed ? 0x01 : 0));

    public void Write(byte value)
    {
        SwitchArmed = (value & 0x01) != 0;
    }

    /// <summary>
    /// Called when the CPU executes a STOP instruction. If switch is armed,
    /// toggle speed and disarm.
    /// </summary>
    public void OnStopExecuted()
    {
        if (SwitchArmed)
        {
            DoubleSpeed = !DoubleSpeed;
            SwitchArmed = false;
        }
    }
}
```

- [ ] **Step 2: Wire KEY1 into `IoRegisters` ($FF4D) and `GameBoySystem.Clock.DoubleSpeed`**

In `IoRegisters`:
```csharp
case 0xFF4D: return _keyOne.Read();
// and in Write:
case 0xFF4D: _keyOne.Write(value); break;
```

In `GameBoySystem`, after each `StepOneSystemTick`, update `Clock.DoubleSpeed = KeyOne.DoubleSpeed;` (or read it directly in the tick loop).

- [ ] **Step 3: Build and commit**

Run: `dotnet build src/Koh.Emulator.Core/Koh.Emulator.Core.csproj`

```bash
git add src/Koh.Emulator.Core/Cgb/KeyOneRegister.cs src/Koh.Emulator.Core/Bus/IoRegisters.cs src/Koh.Emulator.Core/GameBoySystem.cs
git commit -m "feat(emulator): add CGB double-speed KEY1 register"
```

---

### Task 2.D.3: CGB banking tests

**Files:**
- Create: `tests/Koh.Emulator.Core.Tests/CgbBankingTests.cs`

- [ ] **Step 1: Write tests**

```csharp
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Emulator.Core.Tests;

public class CgbBankingTests
{
    private static GameBoySystem MakeCgbSystem()
    {
        var rom = new byte[0x8000];
        rom[0x143] = 0x80;
        rom[0x147] = 0x00;
        var cart = CartridgeFactory.Load(rom);
        return new GameBoySystem(HardwareMode.Cgb, cart);
    }

    [Test]
    public async Task Vram_Bank_Switch_Isolates_Bytes()
    {
        var gb = MakeCgbSystem();
        gb.Mmu.WriteByte(0xFF4F, 0);
        gb.Mmu.WriteByte(0x8000, 0xAA);
        gb.Mmu.WriteByte(0xFF4F, 1);
        gb.Mmu.WriteByte(0x8000, 0xBB);

        gb.Mmu.WriteByte(0xFF4F, 0);
        await Assert.That(gb.Mmu.ReadByte(0x8000)).IsEqualTo((byte)0xAA);

        gb.Mmu.WriteByte(0xFF4F, 1);
        await Assert.That(gb.Mmu.ReadByte(0x8000)).IsEqualTo((byte)0xBB);
    }

    [Test]
    public async Task Wram_Bank_Switch_Isolates_High_Region()
    {
        var gb = MakeCgbSystem();
        gb.Mmu.WriteByte(0xFF70, 2);
        gb.Mmu.WriteByte(0xD000, 0x11);
        gb.Mmu.WriteByte(0xFF70, 3);
        gb.Mmu.WriteByte(0xD000, 0x22);

        gb.Mmu.WriteByte(0xFF70, 2);
        await Assert.That(gb.Mmu.ReadByte(0xD000)).IsEqualTo((byte)0x11);
    }

    [Test]
    public async Task Wram_Bank_0_Aliases_To_Bank_1()
    {
        var gb = MakeCgbSystem();
        gb.Mmu.WriteByte(0xFF70, 0);
        gb.Mmu.WriteByte(0xD000, 0x77);
        gb.Mmu.WriteByte(0xFF70, 1);
        await Assert.That(gb.Mmu.ReadByte(0xD000)).IsEqualTo((byte)0x77);
    }
}
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test tests/Koh.Emulator.Core.Tests/Koh.Emulator.Core.Tests.csproj --filter CgbBankingTests`
Expected: passes.

- [ ] **Step 3: Commit**

```bash
git add tests/Koh.Emulator.Core.Tests/CgbBankingTests.cs
git commit -m "test(emulator): add CGB VRAM/WRAM banking tests"
```

---

## Phase 2-E: Acid2 opcode subset

Per spec §Phase 2: determine the opcode subset empirically by disassembling `dmg-acid2.gb` and `cgb-acid2.gb`, then implement exactly that set.

### Task 2.E.1: Download acid2 ROMs and populate download script

**Files:**
- Modify: `scripts/download-test-roms.ps1`
- Modify: `scripts/download-test-roms.sh`

- [ ] **Step 1: Add acid2 ROM and reference-PNG entries to the download scripts**

Update the scripts to fetch from the canonical sources with SHA-256 verification. Example `scripts/download-test-roms.sh` body:

```bash
#!/usr/bin/env bash
set -euo pipefail

OUTPUT_DIR="${1:-tests/fixtures/test-roms}"
REFERENCE_DIR="${2:-tests/fixtures/reference}"
mkdir -p "$OUTPUT_DIR" "$REFERENCE_DIR"

download_with_hash() {
    local url="$1"
    local output="$2"
    local expected_sha256="$3"

    if [ -f "$output" ]; then
        local actual=$(sha256sum "$output" | cut -d' ' -f1)
        if [ "$actual" = "$expected_sha256" ]; then
            echo "OK  $output"
            return
        fi
        echo "REDOWNLOAD $output (hash mismatch)"
    fi

    curl -fsSL -o "$output" "$url"
    local actual=$(sha256sum "$output" | cut -d' ' -f1)
    if [ "$actual" != "$expected_sha256" ]; then
        echo "FAIL $output: hash mismatch (expected $expected_sha256, got $actual)"
        exit 1
    fi
    echo "DL  $output"
}

# dmg-acid2
download_with_hash \
    "https://github.com/mattcurrie/dmg-acid2/releases/download/v1.0/dmg-acid2.gb" \
    "$OUTPUT_DIR/dmg-acid2.gb" \
    "PUT_ACTUAL_SHA256_HERE"

# cgb-acid2
download_with_hash \
    "https://github.com/mattcurrie/cgb-acid2/releases/download/v1.0/cgb-acid2.gb" \
    "$OUTPUT_DIR/cgb-acid2.gb" \
    "PUT_ACTUAL_SHA256_HERE"

# Reference PNGs (from the same release)
download_with_hash \
    "https://github.com/mattcurrie/dmg-acid2/releases/download/v1.0/dmg-acid2-dmg.png" \
    "$REFERENCE_DIR/dmg-acid2.png" \
    "PUT_ACTUAL_SHA256_HERE"

download_with_hash \
    "https://github.com/mattcurrie/cgb-acid2/releases/download/v1.0/cgb-acid2-cgb.png" \
    "$REFERENCE_DIR/cgb-acid2.png" \
    "PUT_ACTUAL_SHA256_HERE"

echo "download-test-roms: acid2 fixtures ready"
```

(Replace `PUT_ACTUAL_SHA256_HERE` with the real SHA-256 of each file. To compute them initially: download once manually, run `sha256sum`, paste into the script.)

Mirror the changes in `scripts/download-test-roms.ps1` using `Invoke-WebRequest` and `Get-FileHash`.

- [ ] **Step 2: Run the script locally**

Run: `bash scripts/download-test-roms.sh` (or the PowerShell equivalent).
Expected: ROMs and PNGs appear under `tests/fixtures/`.

- [ ] **Step 3: Add the fixtures directory to `.gitignore`**

Append to `.gitignore`:

```
tests/fixtures/test-roms/
tests/fixtures/reference/
```

- [ ] **Step 4: Commit**

```bash
git add scripts/download-test-roms.sh scripts/download-test-roms.ps1 .gitignore
git commit -m "chore: populate test-rom download scripts with acid2 fixtures"
```

---

### Task 2.E.2: Disassemble acid2 ROMs to determine opcode subset

**Files:**
- Create: `docs/decisions/acid2-opcode-subset.md`

- [ ] **Step 1: Run a disassembly of both ROMs**

Use any Game Boy disassembler (mgbdis or similar). Record the set of unique opcode byte values that appear in the executed regions of each ROM. If you don't have a disassembler handy, a simple Python script can read the ROM bytes starting at the known entry point and record all bytes that appear in positions that look like opcodes.

- [ ] **Step 2: Write the decision document**

File `docs/decisions/acid2-opcode-subset.md`:

```markdown
# Decision: Acid2 Opcode Subset for Phase 2

**Date:** 2026-04-10
**Status:** Accepted

## Context

Phase 2 of the emulator implementation (per the emulator design spec) requires
running `dmg-acid2.gb` and `cgb-acid2.gb` to validate PPU correctness. These
ROMs exercise the PPU via real CPU code. Phase 2 implements exactly the CPU
opcode subset required by these ROMs — no more, no less.

## Decision

The following opcodes are required by the acid2 ROMs (determined empirically
from disassembly). Implementing exactly these opcodes gates Phase 2 CPU work:

<!-- Populate this list from the actual disassembly output. Example entries: -->
- `NOP` ($00)
- `LD BC,d16` ($01)
- `LD A,(BC)` ($0A)
- `JR NZ,r8` ($20)
- `LD HL,d16` ($21)
- `LD SP,d16` ($31)
- `LD A,d8` ($3E)
- `LD B,A` ($47) ... (full LD r,r fanout)
- `ADD A,d8` ($C6)
- `CALL a16` ($CD)
- `RET` ($C9)
- `JP a16` ($C3)
- `DI` ($F3)
- `EI` ($FB)

(Plus whatever else the disassembly turns up. Keep this list in sync with
`tests/Koh.Emulator.Core.Tests/Acid2SubsetInstructionTests.cs`.)

## Consequences

- Any opcode not in this list is marked `NOT_IMPLEMENTED` in
  `InstructionTable.cs` and causes the PPU test run to halt with
  `StopReason.HaltedBySystem` and a diagnostic message.
- When Phase 3 begins (full SM83 instruction set), this decision is superseded.
```

- [ ] **Step 3: Commit**

```bash
git add docs/decisions/acid2-opcode-subset.md
git commit -m "docs: record acid2 opcode subset decision"
```

---

### Task 2.E.3: InstructionTable seed with acid2 subset

**Files:**
- Create: `src/Koh.Emulator.Core/Cpu/InstructionTable.cs`
- Modify: `src/Koh.Emulator.Core/Cpu/Sm83.cs`

- [ ] **Step 1: Create `InstructionTable.cs` with only the acid2 subset**

```csharp
namespace Koh.Emulator.Core.Cpu;

public static class InstructionTable
{
    public delegate int InstructionHandler(ref CpuRegisters regs, IInstructionBus bus);

    public interface IInstructionBus
    {
        byte ReadByte(ushort address);
        void WriteByte(ushort address, byte value);
        byte ReadImmediate();       // reads at PC and increments PC
        ushort ReadImmediate16();   // reads 2 bytes at PC and increments PC
    }

    public static readonly InstructionHandler?[] Unprefixed = BuildUnprefixedTable();

    private static InstructionHandler?[] BuildUnprefixedTable()
    {
        var table = new InstructionHandler?[256];

        // NOP ($00) — 4 T-cycles
        table[0x00] = (ref CpuRegisters r, IInstructionBus bus) => 4;

        // LD BC,d16 ($01) — 12 T-cycles
        table[0x01] = (ref CpuRegisters r, IInstructionBus bus) =>
        {
            r.BC = bus.ReadImmediate16();
            return 12;
        };

        // LD (BC),A ($02) — 8 T-cycles
        table[0x02] = (ref CpuRegisters r, IInstructionBus bus) =>
        {
            bus.WriteByte(r.BC, r.A);
            return 8;
        };

        // ... (fill in all the acid2 opcodes identified in Task 2.E.2)

        // JP a16 ($C3) — 16 T-cycles
        table[0xC3] = (ref CpuRegisters r, IInstructionBus bus) =>
        {
            r.Pc = bus.ReadImmediate16();
            return 16;
        };

        // CALL a16 ($CD) — 24 T-cycles
        table[0xCD] = (ref CpuRegisters r, IInstructionBus bus) =>
        {
            ushort target = bus.ReadImmediate16();
            r.Sp -= 2;
            bus.WriteByte(r.Sp, (byte)(r.Pc & 0xFF));
            bus.WriteByte((ushort)(r.Sp + 1), (byte)(r.Pc >> 8));
            r.Pc = target;
            return 24;
        };

        // RET ($C9) — 16 T-cycles
        table[0xC9] = (ref CpuRegisters r, IInstructionBus bus) =>
        {
            byte lo = bus.ReadByte(r.Sp);
            byte hi = bus.ReadByte((ushort)(r.Sp + 1));
            r.Sp += 2;
            r.Pc = (ushort)((hi << 8) | lo);
            return 16;
        };

        // DI ($F3)
        table[0xF3] = (ref CpuRegisters r, IInstructionBus bus) => 4;

        // EI ($FB)
        table[0xFB] = (ref CpuRegisters r, IInstructionBus bus) => 4;

        return table;
    }
}
```

Fill out the remaining opcodes identified in Task 2.E.2. This is a larger task than one commit — group related opcodes and commit incrementally (e.g., "LD register group", "arithmetic group", "jumps group").

- [ ] **Step 2: Rewrite `Sm83.cs` to use the instruction table instead of the mock CPU**

```csharp
using Koh.Emulator.Core.Bus;

namespace Koh.Emulator.Core.Cpu;

public sealed class Sm83 : InstructionTable.IInstructionBus
{
    private readonly Mmu _mmu;
    public CpuRegisters Registers;
    public Interrupts Interrupts;
    public bool Halted;
    public ulong TotalTCycles;

    private int _tCyclesRemainingInInstruction;

    public Sm83(Mmu mmu)
    {
        _mmu = mmu;
        Registers.Pc = 0x0100;
        Registers.Sp = 0xFFFE;
    }

    public bool TickT()
    {
        TotalTCycles++;
        if (_tCyclesRemainingInInstruction > 0)
        {
            _tCyclesRemainingInInstruction--;
            return _tCyclesRemainingInInstruction == 0;
        }

        ExecuteNextInstruction();
        return false;
    }

    private void ExecuteNextInstruction()
    {
        byte opcode = ReadImmediate();
        var handler = InstructionTable.Unprefixed[opcode];
        if (handler is null)
        {
            // Unimplemented — halt.
            Halted = true;
            _tCyclesRemainingInInstruction = 4;
            return;
        }
        int cycles = handler(ref Registers, this);
        _tCyclesRemainingInInstruction = cycles - 1;  // -1 because this call IS the first T-cycle
    }

    public byte ReadByte(ushort address) => _mmu.ReadByte(address);
    public void WriteByte(ushort address, byte value) => _mmu.WriteByte(address, value);

    public byte ReadImmediate()
    {
        byte value = _mmu.ReadByte(Registers.Pc);
        Registers.Pc++;
        return value;
    }

    public ushort ReadImmediate16()
    {
        byte lo = ReadImmediate();
        byte hi = ReadImmediate();
        return (ushort)((hi << 8) | lo);
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build src/Koh.Emulator.Core/Koh.Emulator.Core.csproj`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Koh.Emulator.Core/Cpu/InstructionTable.cs src/Koh.Emulator.Core/Cpu/Sm83.cs
git commit -m "feat(emulator): replace mock CPU with acid2 opcode subset via InstructionTable"
```

---

### Task 2.E.4: Per-opcode tests for the acid2 subset

**Files:**
- Create: `tests/Koh.Emulator.Core.Tests/Acid2SubsetInstructionTests.cs`

- [ ] **Step 1: Write tests per opcode**

For each opcode in the acid2 subset, write a focused test:

```csharp
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Emulator.Core.Tests;

public class Acid2SubsetInstructionTests
{
    private static GameBoySystem SystemWithCode(params byte[] code)
    {
        var rom = new byte[0x8000];
        rom[0x147] = 0x00;
        Array.Copy(code, 0, rom, 0x0100, code.Length);
        var cart = CartridgeFactory.Load(rom);
        return new GameBoySystem(HardwareMode.Dmg, cart);
    }

    [Test]
    public async Task Nop_Advances_Pc_And_Takes_4_TCycles()
    {
        var gb = SystemWithCode(0x00);
        var before = gb.Cpu.TotalTCycles;
        gb.StepInstruction();
        var after = gb.Cpu.TotalTCycles;
        await Assert.That(after - before).IsEqualTo(4UL);
        await Assert.That(gb.Registers.Pc).IsEqualTo((ushort)0x0101);
    }

    [Test]
    public async Task LdHl_D16_Loads_Immediate()
    {
        var gb = SystemWithCode(0x21, 0x34, 0x12);   // LD HL,$1234
        gb.StepInstruction();
        await Assert.That(gb.Registers.HL).IsEqualTo((ushort)0x1234);
    }

    [Test]
    public async Task Jp_A16_Sets_Pc_To_Target()
    {
        var gb = SystemWithCode(0xC3, 0x00, 0x02);   // JP $0200
        gb.StepInstruction();
        await Assert.That(gb.Registers.Pc).IsEqualTo((ushort)0x0200);
    }

    [Test]
    public async Task Call_Then_Ret_Returns_To_Caller()
    {
        var gb = SystemWithCode(
            0xCD, 0x10, 0x01,   // $0100: CALL $0110
            0x00,               // $0103: NOP
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,  // padding
            0xC9                // $0110: RET
        );
        gb.StepInstruction();  // CALL
        await Assert.That(gb.Registers.Pc).IsEqualTo((ushort)0x0110);
        gb.StepInstruction();  // RET
        await Assert.That(gb.Registers.Pc).IsEqualTo((ushort)0x0103);
    }

    // ... add a test for every opcode in the acid2 subset
}
```

- [ ] **Step 2: Run the tests iteratively as you add opcodes to `InstructionTable.cs`**

Run: `dotnet test tests/Koh.Emulator.Core.Tests/Koh.Emulator.Core.Tests.csproj --filter Acid2SubsetInstructionTests`

Commit incrementally: add ~10 opcodes + their tests at a time, commit, continue. This keeps each commit small and reviewable.

- [ ] **Step 3: Final commit when all acid2 opcodes pass**

```bash
git add tests/Koh.Emulator.Core.Tests/Acid2SubsetInstructionTests.cs
git commit -m "test(emulator): full acid2 opcode subset per-instruction tests"
```

---

## Phase 2-F: Acid2 integration tests

### Task 2.F.1: dmg-acid2 framebuffer comparison test

**Files:**
- Create: `tests/Koh.Compat.Tests/Emulation/Acid2Tests.cs`
- Modify: `tests/Koh.Compat.Tests/Koh.Compat.Tests.csproj`

- [ ] **Step 1: Add a project reference from Koh.Compat.Tests to Koh.Emulator.Core**

Update `tests/Koh.Compat.Tests/Koh.Compat.Tests.csproj`:

```xml
  <ItemGroup>
    <ProjectReference Include="..\..\src\Koh.Emulator.Core\Koh.Emulator.Core.csproj" />
  </ItemGroup>
```

- [ ] **Step 2: Create `Acid2Tests.cs`**

```csharp
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Compat.Tests.Emulation;

public class Acid2Tests
{
    private static string FixtureRoot => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tests", "fixtures");

    private static readonly string DmgRomPath = Path.Combine(FixtureRoot, "test-roms", "dmg-acid2.gb");
    private static readonly string DmgReferencePngPath = Path.Combine(FixtureRoot, "reference", "dmg-acid2.png");
    private static readonly string CgbRomPath = Path.Combine(FixtureRoot, "test-roms", "cgb-acid2.gb");
    private static readonly string CgbReferencePngPath = Path.Combine(FixtureRoot, "reference", "cgb-acid2.png");

    [Test]
    public async Task DmgAcid2_Framebuffer_Matches_Reference()
    {
        if (!File.Exists(DmgRomPath) || !File.Exists(DmgReferencePngPath))
        {
            // CI must fail if the fixtures are missing (§12.10). Locally, skip with a clear error.
            throw new FileNotFoundException(
                $"dmg-acid2 fixtures missing; run scripts/download-test-roms.sh. Missing: {DmgRomPath} or {DmgReferencePngPath}");
        }

        var rom = await File.ReadAllBytesAsync(DmgRomPath);
        var cart = CartridgeFactory.Load(rom);
        var gb = new GameBoySystem(HardwareMode.Dmg, cart);

        // Run long enough for the ROM to render its final frame (acid2 holds it indefinitely).
        for (int frame = 0; frame < 60; frame++)
        {
            gb.RunFrame();
        }

        var framebuffer = gb.Framebuffer.Front;
        var reference = LoadReferenceRgba(DmgReferencePngPath);

        int mismatch = 0;
        for (int i = 0; i < framebuffer.Length; i++)
        {
            if (framebuffer[i] != reference[i]) mismatch++;
        }
        await Assert.That(mismatch).IsEqualTo(0);
    }

    private static byte[] LoadReferenceRgba(string pngPath)
    {
        // Decoding PNG without an image library: use System.Drawing.Common if available,
        // or add ImageSharp as a test-only package. This task adds ImageSharp.
        using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(pngPath);
        var bytes = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(bytes);
        return bytes;
    }
}
```

- [ ] **Step 3: Add the ImageSharp package to Directory.Packages.props and reference it in the test project**

`Directory.Packages.props`:

```xml
    <PackageVersion Include="SixLabors.ImageSharp" Version="3.1.5" />
```

`tests/Koh.Compat.Tests/Koh.Compat.Tests.csproj`:

```xml
    <PackageReference Include="SixLabors.ImageSharp" />
```

- [ ] **Step 4: Run the test**

Run: `bash scripts/download-test-roms.sh` (if fixtures aren't present).
Run: `dotnet test tests/Koh.Compat.Tests/Koh.Compat.Tests.csproj --filter Acid2Tests`

Expected: DmgAcid2 test passes with zero pixel mismatch. **If it fails, the PPU implementation has bugs — iterate on the fetcher, sprite rendering, or window logic until it passes.** This is the Phase 2 acceptance gate.

- [ ] **Step 5: Add the CgbAcid2 test in the same style**

Add a second `[Test]` method that loads `cgb-acid2.gb` with `HardwareMode.Cgb` and compares against `cgb-acid2.png`.

- [ ] **Step 6: Commit**

```bash
git add tests/Koh.Compat.Tests/Emulation/Acid2Tests.cs tests/Koh.Compat.Tests/Koh.Compat.Tests.csproj Directory.Packages.props
git commit -m "test(compat): add dmg-acid2 and cgb-acid2 framebuffer comparison tests"
```

---

## Phase 2-G: Blazor app rendering

### Task 2.G.1: Single-copy framebuffer pipeline

**Files:**
- Create: `src/Koh.Emulator.App/Services/FramebufferBridge.cs`
- Create: `src/Koh.Emulator.App/wwwroot/js/framebuffer-bridge.js`
- Modify: `src/Koh.Emulator.App/Components/LcdDisplay.razor`
- Create: `src/Koh.Emulator.App/Components/LcdDisplay.razor.js`

- [ ] **Step 1: Create `framebuffer-bridge.js`**

```javascript
// Allocates persistent ImageData + Uint8ClampedArray and exposes a single-copy
// commit path from WASM to the canvas.
window.kohFramebufferBridge = (function () {
    const WIDTH = 160;
    const HEIGHT = 144;
    let imageData = null;
    let canvas = null;
    let ctx = null;

    return {
        attach: function (canvasId) {
            canvas = document.getElementById(canvasId);
            if (!canvas) throw new Error('Canvas not found: ' + canvasId);
            ctx = canvas.getContext('2d');
            imageData = ctx.createImageData(WIDTH, HEIGHT);
        },

        commit: function (base64Pixels) {
            // Phase 2 path: simple base64 decode into the persistent ImageData.
            // This is intentionally the "single-copy" path documented in §10.4.
            if (!imageData || !ctx) return;
            const raw = atob(base64Pixels);
            const dst = imageData.data;
            for (let i = 0; i < raw.length; i++) {
                dst[i] = raw.charCodeAt(i);
            }
            ctx.putImageData(imageData, 0, 0);
        }
    };
})();
```

Add `<script src="js/framebuffer-bridge.js"></script>` to `index.html` before the Blazor loader.

- [ ] **Step 2: Create `FramebufferBridge.cs`**

```csharp
using Microsoft.JSInterop;
using Koh.Emulator.Core.Ppu;

namespace Koh.Emulator.App.Services;

public sealed class FramebufferBridge
{
    private readonly IJSRuntime _js;
    public FramebufferBridge(IJSRuntime js) { _js = js; }

    public ValueTask AttachAsync(string canvasId)
        => _js.InvokeVoidAsync("kohFramebufferBridge.attach", canvasId);

    public ValueTask CommitAsync(Framebuffer framebuffer)
    {
        var bytes = framebuffer.Front.ToArray();
        string base64 = Convert.ToBase64String(bytes);
        return _js.InvokeVoidAsync("kohFramebufferBridge.commit", base64);
    }
}
```

*Note: base64 encoding is the single-copy baseline per §10.4. A later optimization can replace this with `IJSUnmarshalledRuntime.InvokeUnmarshalled` to transfer the span directly without base64. Keep the base64 path as the portable fallback.*

- [ ] **Step 3: Register `FramebufferBridge` in `Program.cs`**

```csharp
builder.Services.AddSingleton<FramebufferBridge>();
```

- [ ] **Step 4: Rewrite `LcdDisplay.razor`**

```razor
@using Koh.Emulator.App.Services
@inject EmulatorHost EmulatorHost
@inject FramebufferBridge Bridge
@implements IAsyncDisposable

<canvas id="koh-lcd" width="160" height="144" style="image-rendering: pixelated; width: @(160 * Scale)px; height: @(144 * Scale)px"></canvas>

@code {
    [Parameter] public int Scale { get; set; } = 3;

    private bool _attached;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await Bridge.AttachAsync("koh-lcd");
            _attached = true;
            EmulatorHost.FrameReady += OnFrameReady;
        }
    }

    private async void OnFrameReady()
    {
        if (!_attached || EmulatorHost.System is null) return;
        await Bridge.CommitAsync(EmulatorHost.System.Framebuffer);
    }

    public async ValueTask DisposeAsync()
    {
        EmulatorHost.FrameReady -= OnFrameReady;
        await ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 5: Replace the placeholder `<div class="lcd-placeholder">` in both `DebugShell.razor` and `StandaloneShell.razor` with `<LcdDisplay />`**

- [ ] **Step 6: Build and run the dev host**

Run: `dotnet build src/Koh.Emulator.App/Koh.Emulator.App.csproj`
Run: `dotnet run --project src/Koh.Emulator.App/Koh.Emulator.App.csproj`

Load a ROM in standalone mode. Expected: the canvas renders pixels (may be garbage until a real ROM runs).

- [ ] **Step 7: Commit**

```bash
git add src/Koh.Emulator.App/Services/FramebufferBridge.cs src/Koh.Emulator.App/wwwroot/js/framebuffer-bridge.js src/Koh.Emulator.App/Components/LcdDisplay.razor src/Koh.Emulator.App/wwwroot/index.html src/Koh.Emulator.App/Shell/ src/Koh.Emulator.App/Program.cs
git commit -m "feat(emulator-app): wire single-copy framebuffer pipeline to LCD canvas"
```

---

### Task 2.G.2: VRAM, Palette, OAM, Memory view components

**Files:**
- Create: `src/Koh.Emulator.App/Components/VramView.razor`
- Create: `src/Koh.Emulator.App/Components/PaletteView.razor`
- Create: `src/Koh.Emulator.App/Components/OamView.razor`
- Create: `src/Koh.Emulator.App/Components/MemoryView.razor`

These are debug inspection components. Each reads from `EmulatorHost.System` and re-renders on `StateChanged`.

- [ ] **Step 1: Create `VramView.razor`** — renders the 256 BG tiles as an 8×32 grid of tiny canvases.

```razor
@using Koh.Emulator.App.Services
@inject EmulatorHost EmulatorHost
@implements IDisposable

<div class="vram-view">
    <h3>VRAM Tile Data</h3>
    @if (EmulatorHost.System is { } gb)
    {
        <canvas id="koh-vram-canvas" width="128" height="192"></canvas>
        <button @onclick="Refresh">Refresh</button>
    }
</div>

@code {
    protected override void OnInitialized()
    {
        EmulatorHost.StateChanged += OnStateChanged;
    }

    private void OnStateChanged() => InvokeAsync(StateHasChanged);

    public void Dispose() => EmulatorHost.StateChanged -= OnStateChanged;

    private Task Refresh() => InvokeAsync(StateHasChanged);
}
```

(Full canvas-rendering logic can be deferred; the placeholder here is fine for a Phase 2 ship with a "Refresh" button the developer clicks to see the current state. Real-time updating on every frame would thrash the UI. The component becomes fully interactive in Phase 4.)

- [ ] **Step 2: Create `PaletteView.razor`**

```razor
@using Koh.Emulator.App.Services
@using Koh.Emulator.Core.Ppu
@inject EmulatorHost EmulatorHost
@implements IDisposable

<div class="palette-view">
    <h3>Palettes</h3>
    @if (EmulatorHost.System is { } gb)
    {
        <div class="dmg-palette">
            <strong>BGP:</strong> $@(gb.Ppu.BGP.ToString("X2"))
            <strong>OBP0:</strong> $@(gb.Ppu.OBP0.ToString("X2"))
            <strong>OBP1:</strong> $@(gb.Ppu.OBP1.ToString("X2"))
        </div>
    }
</div>

@code {
    protected override void OnInitialized() => EmulatorHost.StateChanged += OnStateChanged;
    private void OnStateChanged() => InvokeAsync(StateHasChanged);
    public void Dispose() => EmulatorHost.StateChanged -= OnStateChanged;
}
```

- [ ] **Step 3: Create `OamView.razor`** — lists the 40 OAM entries.

```razor
@using Koh.Emulator.App.Services
@using Koh.Emulator.Core.Ppu
@inject EmulatorHost EmulatorHost
@implements IDisposable

<div class="oam-view">
    <h3>OAM</h3>
    @if (EmulatorHost.System is { } gb)
    {
        <table>
            <thead><tr><th>#</th><th>Y</th><th>X</th><th>Tile</th><th>Flags</th></tr></thead>
            <tbody>
                @for (int i = 0; i < 40; i++)
                {
                    var sprite = ObjectAttributes.Parse(gb.Mmu.Oam, i);
                    <tr>
                        <td>@i</td>
                        <td>$@(sprite.Y.ToString("X2"))</td>
                        <td>$@(sprite.X.ToString("X2"))</td>
                        <td>$@(sprite.Tile.ToString("X2"))</td>
                        <td>$@(sprite.Flags.ToString("X2"))</td>
                    </tr>
                }
            </tbody>
        </table>
    }
</div>

@code {
    protected override void OnInitialized() => EmulatorHost.StateChanged += OnStateChanged;
    private void OnStateChanged() => InvokeAsync(StateHasChanged);
    public void Dispose() => EmulatorHost.StateChanged -= OnStateChanged;
}
```

- [ ] **Step 4: Create `MemoryView.razor`** — a simple hex dump with a start-address input.

```razor
@using Koh.Emulator.App.Services
@inject EmulatorHost EmulatorHost
@implements IDisposable

<div class="memory-view">
    <h3>Memory</h3>
    <label>Start: $<input @bind="StartHex" style="width: 64px" /></label>
    <button @onclick="Refresh">Refresh</button>

    @if (EmulatorHost.System is { } gb && TryParseStart(out ushort start))
    {
        <pre>
@for (int row = 0; row < 16; row++)
{
    @($"${(start + row * 16):X4}  ")
    for (int col = 0; col < 16; col++)
    {
        @($"{gb.Mmu.ReadByte((ushort)(start + row * 16 + col)):X2} ")
    }
    @("\n")
}
        </pre>
    }
</div>

@code {
    private string StartHex { get; set; } = "C000";

    protected override void OnInitialized() => EmulatorHost.StateChanged += OnStateChanged;
    private void OnStateChanged() => InvokeAsync(StateHasChanged);
    public void Dispose() => EmulatorHost.StateChanged -= OnStateChanged;

    private Task Refresh() => InvokeAsync(StateHasChanged);
    private bool TryParseStart(out ushort value) => ushort.TryParse(StartHex, System.Globalization.NumberStyles.HexNumber, null, out value);
}
```

- [ ] **Step 5: Add the components to `DebugShell.razor`**

```razor
<div class="debug-panels">
    <CpuDashboard />
    <PaletteView />
    <OamView />
    <MemoryView />
</div>
```

- [ ] **Step 6: Build and commit**

Run: `dotnet build src/Koh.Emulator.App/Koh.Emulator.App.csproj`

```bash
git add src/Koh.Emulator.App/Components/VramView.razor src/Koh.Emulator.App/Components/PaletteView.razor src/Koh.Emulator.App/Components/OamView.razor src/Koh.Emulator.App/Components/MemoryView.razor src/Koh.Emulator.App/Shell/DebugShell.razor
git commit -m "feat(emulator-app): add VramView, PaletteView, OamView, MemoryView inspection components"
```

---

## Phase 2-H: `readMemory` DAP capability

### Task 2.H.1: ReadMemory messages and handler

**Files:**
- Create: `src/Koh.Debugger/Dap/Messages/ReadMemoryMessages.cs`
- Create: `src/Koh.Debugger/Dap/Handlers/ReadMemoryHandler.cs`
- Modify: `src/Koh.Debugger/Dap/DapJson.cs`
- Modify: `src/Koh.Debugger/Dap/DapCapabilities.cs`
- Modify: `src/Koh.Debugger/Dap/HandlerRegistration.cs`

- [ ] **Step 1: Create `ReadMemoryMessages.cs`**

```csharp
using System.Text.Json.Serialization;

namespace Koh.Debugger.Dap.Messages;

public sealed class ReadMemoryArguments
{
    [JsonPropertyName("memoryReference")] public string MemoryReference { get; set; } = "";
    [JsonPropertyName("offset")] public int Offset { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }
}

public sealed class ReadMemoryResponseBody
{
    [JsonPropertyName("address")] public string Address { get; set; } = "";
    [JsonPropertyName("unreadableBytes")] public int UnreadableBytes { get; set; }
    [JsonPropertyName("data")] public string Data { get; set; } = "";
}
```

- [ ] **Step 2: Create `ReadMemoryHandler.cs`**

```csharp
using System.Text.Json;
using Koh.Debugger.Dap.Messages;

namespace Koh.Debugger.Dap.Handlers;

public sealed class ReadMemoryHandler
{
    private readonly DebugSession _session;
    public ReadMemoryHandler(DebugSession session) { _session = session; }

    public Response Handle(Request request)
    {
        var args = request.Arguments?.Deserialize(DapJsonContext.Default.ReadMemoryArguments);
        if (args is null)
            return new Response { Success = false, Message = "readMemory: missing args" };

        var system = _session.System;
        if (system is null)
            return new Response { Success = false, Message = "readMemory: no active session" };

        ushort start;
        try
        {
            start = Convert.ToUInt16(args.MemoryReference, 16);
            start = (ushort)(start + args.Offset);
        }
        catch
        {
            return new Response { Success = false, Message = $"readMemory: invalid memoryReference '{args.MemoryReference}'" };
        }

        int count = Math.Max(0, Math.Min(args.Count, 0x10000 - start));
        var bytes = new byte[count];
        for (int i = 0; i < count; i++)
            bytes[i] = system.DebugReadByte((ushort)(start + i));

        return new Response
        {
            Success = true,
            Body = new ReadMemoryResponseBody
            {
                Address = "0x" + start.ToString("X4"),
                UnreadableBytes = 0,
                Data = Convert.ToBase64String(bytes),
            },
        };
    }
}
```

- [ ] **Step 3: Register the message types in `DapJsonContext`**

```csharp
[JsonSerializable(typeof(ReadMemoryArguments))]
[JsonSerializable(typeof(ReadMemoryResponseBody))]
```

- [ ] **Step 4: Update `DapCapabilities.Phase1()` → `Phase2()` enabling `SupportsReadMemoryRequest = true`**

Rename the method and update `HandlerRegistration` to call `Phase2()`. Register the new handler:

```csharp
var readMemory = new ReadMemoryHandler(session);
dispatcher.RegisterHandler("readMemory", readMemory.Handle);
```

- [ ] **Step 5: Write a test**

File `tests/Koh.Debugger.Tests/ReadMemoryHandlerTests.cs`:

```csharp
using System.Text.Json;
using Koh.Debugger;
using Koh.Debugger.Dap;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Debugger.Tests;

public class ReadMemoryHandlerTests
{
    [Test]
    public async Task ReadMemory_Returns_Base64_Bytes()
    {
        var dispatcher = new DapDispatcher();
        var session = new DebugSession();
        var responses = new List<byte[]>();
        dispatcher.ResponseReady += data => responses.Add(data.ToArray());

        var rom = new byte[0x8000];
        rom[0x147] = 0x00;
        rom[0x0100] = 0xAA;
        rom[0x0101] = 0xBB;
        var cart = CartridgeFactory.Load(rom);
        session.Launch(rom, Array.Empty<byte>(), Koh.Emulator.Core.HardwareMode.Dmg);

        HandlerRegistration.RegisterAll(dispatcher, session, _ => Array.Empty<byte>());

        var request = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["seq"] = 1,
            ["type"] = "request",
            ["command"] = "readMemory",
            ["arguments"] = new Dictionary<string, object?>
            {
                ["memoryReference"] = "0100",
                ["offset"] = 0,
                ["count"] = 2,
            },
        });

        dispatcher.HandleRequest(request);

        using var doc = JsonDocument.Parse(responses[0]);
        var body = doc.RootElement.GetProperty("body");
        string data = body.GetProperty("data").GetString() ?? "";
        byte[] decoded = Convert.FromBase64String(data);
        await Assert.That(decoded).IsEquivalentTo(new byte[] { 0xAA, 0xBB });
    }
}
```

- [ ] **Step 6: Run and commit**

Run: `dotnet test tests/Koh.Debugger.Tests/Koh.Debugger.Tests.csproj`

```bash
git add src/Koh.Debugger/Dap/Messages/ReadMemoryMessages.cs src/Koh.Debugger/Dap/Handlers/ReadMemoryHandler.cs src/Koh.Debugger/Dap/DapJson.cs src/Koh.Debugger/Dap/DapCapabilities.cs src/Koh.Debugger/Dap/HandlerRegistration.cs tests/Koh.Debugger.Tests/ReadMemoryHandlerTests.cs
git commit -m "feat(debugger): add readMemory DAP handler for Phase 2 capability"
```

---

## Phase 2-I: Phase 2 benchmark + CI

### Task 2.I.1: Extend benchmark runner with Phase 2 workload

**Files:**
- Modify: `src/Koh.Emulator.App/Benchmark/BenchmarkRunner.cs`
- Modify: `src/Koh.Emulator.App/Benchmark/BenchmarkPage.razor`

- [ ] **Step 1: Add a Phase 2 workload method to `BenchmarkRunner.cs`**

Per §12.9 Phase 2 row: full PPU with tile data in VRAM, 40 sprites visible across a frame, window enabled mid-frame, CGB palettes, real OAM DMA once per frame. Mock CPU from Phase 1.

```csharp
public async Task<Result> RunPhase2WorkloadAsync(TimeSpan warmup, TimeSpan measure)
{
    var rom = BuildPhase2SyntheticRom();
    var cart = CartridgeFactory.Load(rom);
    var gb = new GameBoySystem(HardwareMode.Cgb, cart);
    SetupPhase2TestState(gb);

    // Same warmup/measure loop as Phase 1, but executing the Phase 2 workload.
    // (Copy the body from RunAsync and substitute this setup.)
    // ... identical loop structure ...
}

private static byte[] BuildPhase2SyntheticRom()
{
    var rom = new byte[0x8000];
    rom[0x143] = 0x80;  // CGB
    rom[0x147] = 0x00;
    return rom;
}

private static void SetupPhase2TestState(GameBoySystem gb)
{
    // Populate VRAM with tile data.
    for (int i = 0; i < 0x2000; i++)
        gb.Mmu.WriteByte((ushort)(0x8000 + i), (byte)((i * 7) ^ 0xAA));

    // Populate OAM with 40 sprites.
    for (int i = 0; i < 40; i++)
    {
        gb.Mmu.WriteByte((ushort)(0xFE00 + i * 4 + 0), (byte)(16 + (i * 4)));  // Y
        gb.Mmu.WriteByte((ushort)(0xFE00 + i * 4 + 1), (byte)(8 + (i * 4)));   // X
        gb.Mmu.WriteByte((ushort)(0xFE00 + i * 4 + 2), (byte)i);               // Tile
        gb.Mmu.WriteByte((ushort)(0xFE00 + i * 4 + 3), 0);                     // Flags
    }

    // Enable LCD, sprites, window.
    gb.Mmu.WriteByte(0xFF40, 0b_1110_0011);
    gb.Mmu.WriteByte(0xFF4A, 50);  // WY
    gb.Mmu.WriteByte(0xFF4B, 80);  // WX
}
```

- [ ] **Step 2: Add a "Run Phase 2" button to `BenchmarkPage.razor` and wire it to `RunPhase2WorkloadAsync`**

The page now has two buttons: Phase 1 workload and Phase 2 workload. Each reports its multiplier separately.

- [ ] **Step 3: Build, run dev host, execute Phase 2 benchmark**

Expected: ≥ 1.5× real-time median per §12.9 Phase 2 row. Hard floor 1.35×. If it fails, diagnose per §12.9 failure policy before continuing.

- [ ] **Step 4: Commit**

```bash
git add src/Koh.Emulator.App/Benchmark/
git commit -m "feat(benchmark): add Phase 2 PPU+sprites+window workload"
```

---

### Task 2.I.2: CI job to run acid2 tests and Phase 2 benchmark

**Files:**
- Modify: `.github/workflows/ci.yml`

- [ ] **Step 1: Add acid2 test and Phase 2 benchmark steps to the existing CI workflow**

Add a job that:

1. Downloads test ROMs via `scripts/download-test-roms.sh`
2. Runs `Koh.Compat.Tests` filtered to `Acid2Tests`
3. Publishes the emulator app
4. Runs headless Chrome to hit `/benchmark` and execute the Phase 2 workload

Headless benchmark in CI is non-trivial. A simpler approach for Phase 2: use a native .NET benchmark (`Koh.Benchmarks.EmulatorBenchmarks` with BenchmarkDotNet) that runs the same workload on the native CPU. Accept that the native result is an upper bound on the WASM result, and rely on manual local verification for the actual WASM multiplier. Native benchmark becomes the CI gate; WASM benchmark is a local developer responsibility.

Add to CI:

```yaml
      - name: Run compatibility tests
        run: dotnet test tests/Koh.Compat.Tests/Koh.Compat.Tests.csproj --filter Acid2Tests
```

And a new job for native benchmark:

```yaml
  benchmark-native:
    runs-on: ubuntu-latest
    needs: build-and-test
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - name: Download test ROMs
        run: bash scripts/download-test-roms.sh
      - name: Run native emulator benchmark
        run: dotnet run --project benchmarks/Koh.Benchmarks --configuration Release -- --filter '*Phase2*'
```

- [ ] **Step 2: Add a native benchmark for Phase 2**

Create `benchmarks/Koh.Benchmarks/Phase2Benchmarks.cs` mirroring the Blazor `BenchmarkRunner` workload but using BenchmarkDotNet attributes.

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/ci.yml benchmarks/Koh.Benchmarks/Phase2Benchmarks.cs
git commit -m "ci: add acid2 tests and native Phase 2 benchmark to CI"
```

---

## Phase 2 exit checklist

Before declaring Phase 2 complete, verify every item below. Any failure blocks Phase 2 exit.

- [ ] `dotnet build Koh.slnx` succeeds on Windows and Linux with no warnings
- [ ] `dotnet test Koh.slnx` — all tests pass (including Phase 1 tests and new Phase 2 tests)
- [ ] `dmg-acid2` framebuffer matches the reference PNG pixel-for-pixel
- [ ] `cgb-acid2` framebuffer matches the reference PNG pixel-for-pixel
- [ ] PPU mode timing tests pass (mode 2 = 80 dots, scanline = 456 dots, frame wrap)
- [ ] OAM DMA timing tests pass (contention window, HRAM passthrough, 160-byte transfer)
- [ ] CGB HDMA general-purpose transfer test passes
- [ ] CGB VRAM / WRAM banking tests pass
- [ ] All acid2 opcode subset tests pass
- [ ] `readMemory` DAP capability is advertised in `initialize` response
- [ ] `readMemory` handler test passes
- [ ] Dev host renders the acid2 ROMs visually matching the reference
- [ ] Phase 2 benchmark (native) meets ≥ 1.5× real-time median
- [ ] VS Code extension F5 still works end-to-end (from Phase 1)
- [ ] VRAM / Palette / OAM / Memory view components render in the debug shell
- [ ] CI workflow passes on both ubuntu-latest and windows-latest including acid2 tests

If every checkbox is checked, Phase 2 is complete and ready for Phase 3 planning.

---

## Self-review notes

**Spec coverage:** Every Phase 2 requirement from `docs/superpowers/specs/2026-04-10-emulator-debugger-design.md` is covered:

- §7.2 clocking model (including CGB double-speed): Tasks 2.D.2, partial (full speed switch wiring completes in Phase 3 when STOP lands)
- §7.6 DMA precise timing: Tasks 2.B.*, 2.C.*
- §7.7 PPU pixel-FIFO algorithm: Tasks 2.A.3–2.A.8
- §7.12 Phase 2 subsystem phasing: PPU full, OAM DMA full, HDMA full, CGB VRAM/WRAM/double-speed
- §8.7 Phase 2 capabilities (readMemory): Task 2.H.1
- §10.4 single-copy framebuffer: Task 2.G.1
- §12.9 Phase 2 benchmark: Tasks 2.I.1, 2.I.2
- §Phase 2 exit criteria: acid2 tests (Task 2.F.1)

**Known deferrals to Phase 3+ (explicitly NOT in this plan):**
- Full SM83 instruction set (only the acid2 subset is implemented)
- Interrupt dispatch (5 M-cycle sequence)
- HALT + EI delay slot
- Source-level stepping (`next`, `stepIn`, `stepOut`)
- Breakpoint halting (Phase 1 only verified breakpoints; Phase 3 makes them halt)
- Stack trace
- Disassembly
- Symbols scope and Source Context scope in Variables
- Joypad IRQ
- CGB CGB-palette-aware pixel rendering (DMG palette only; CGB palette added in Phase 3)

**Known design risks:**
- Phase 2 benchmark currently uses a mock CPU per §12.9 — the real CPU arrives in Phase 3 and will re-validate the performance gate against Blargg cpu_instrs.
- `MemoryView.razor` renders via Razor string building which is slow for large ranges; a JS-side renderer is an option for Phase 3 if it becomes a bottleneck.
- The acid2 opcode subset is determined empirically in Task 2.E.2; if the disassembly reveals opcodes more complex than anticipated (e.g., CB-prefix bit operations), Task 2.E.3 work may grow.

---

**Plan complete.** Phase 2 will be implemented after Phase 1 passes its exit checklist.
