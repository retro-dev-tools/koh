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
    private readonly HardwareMode _hwMode;

    // Internal PPU state
    public PpuMode Mode { get; private set; } = PpuMode.OamScan;
    public int Dot;
    private readonly Fetcher _fetcher = new();
    private readonly PixelFifo _fifo = new();
    private int _lcdX;
    private int _windowLineCounter;
    private bool _windowTriggeredThisLine;
    private bool _initialDiscardDone;

    // OAM scan results
    private readonly ObjectAttributes[] _lineSprites = new ObjectAttributes[10];
    private readonly bool[] _lineSpriteConsumed = new bool[10];
    private int _lineSpriteCount;

    // Sprite fetcher state
    private int _spriteFetchIndex = -1;   // -1 = no active sprite fetch
    private int _spritePenaltyDots;

    // Previous STAT IRQ line for edge detection.
    private bool _prevStatLine;

    /// <summary>Raised when the PPU transitions from Drawing to HBlank. Used by HDMA.</summary>
    public event Action? HBlankEntered;

    public Ppu(HardwareMode mode, byte[] vram, byte[] oam)
    {
        _hwMode = mode;
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
                _lineSprites[_lineSpriteCount] = sprite;
                _lineSpriteConsumed[_lineSpriteCount] = false;
                _lineSpriteCount++;
            }
        }
    }

    private void StartScanlineDrawing()
    {
        _lcdX = 0;
        _fifo.Reset();
        _windowTriggeredThisLine = false;
        _initialDiscardDone = false;
        _spriteFetchIndex = -1;
        _fetcher.ResetForScanline(SCX, SCY, LY, LcdControl.BgTileMapBase(LCDC), window: false);
    }

    private void TickDrawing(ref Interrupts interrupts)
    {
        // If a sprite fetch is active, stall BG fetch + shift-out until the penalty elapses.
        if (_spriteFetchIndex >= 0)
        {
            _spritePenaltyDots--;
            if (_spritePenaltyDots <= 0)
            {
                var sprite = _lineSprites[_spriteFetchIndex];
                PushSpritePixelsForCurrentLcdX(sprite);
                _lineSpriteConsumed[_spriteFetchIndex] = true;
                _spriteFetchIndex = -1;
            }
            Dot++;
            return;
        }

        // Check whether a sprite should start fetching at the current pixel column.
        if ((LCDC & LcdControl.ObjEnable) != 0 && _fifo.BgCount > 0)
        {
            for (int i = 0; i < _lineSpriteCount; i++)
            {
                if (_lineSpriteConsumed[i]) continue;
                int spriteX = _lineSprites[i].X - 8;
                if (spriteX == _lcdX)
                {
                    _spriteFetchIndex = i;
                    _spritePenaltyDots = 6;  // minimum sprite-fetch penalty per §7.7
                    Dot++;
                    return;
                }
            }
        }

        RunFetcher();

        // SCX mod 8 initial discard: first (SCX & 7) pixels of the first BG tile push
        // don't go to the LCD.
        if (_fifo.BgCount > 0)
        {
            int discardTarget = SCX & 7;
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
                Dot++;
                return;
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
                    HBlankEntered?.Invoke();
                }
            }
        }

        Dot++;
    }

    private void RunFetcher()
    {
        _fetcher.DotBudget--;
        if (_fetcher.DotBudget > 0) return;

        switch (_fetcher.Step)
        {
            case FetcherStep.GetTile:
                int mapIdx = _fetcher.TileMapY * 32 + _fetcher.TileMapX;
                _fetcher.FetchedTileIndex = _vram[(_fetcher.TileMapBase - 0x8000) + mapIdx];
                if (_hwMode == HardwareMode.Cgb)
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
        if (_hwMode == HardwareMode.Cgb && (_fetcher.FetchedAttributes & 0x08) != 0)
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

        // DMG greyscale mapping (matches acid2 reference palette).
        byte shade = finalColor switch
        {
            0 => (byte)0xFF,
            1 => (byte)0xAA,
            2 => (byte)0x55,
            _ => (byte)0x00,
        };
        back[pixelIdx + 0] = shade;
        back[pixelIdx + 1] = shade;
        back[pixelIdx + 2] = shade;
        back[pixelIdx + 3] = 0xFF;
    }

    private static byte ApplyDmgPalette(byte colorIdx, byte palette)
        => (byte)((palette >> (colorIdx * 2)) & 0x03);

    private void PushSpritePixelsForCurrentLcdX(ObjectAttributes sprite)
    {
        int height = LcdControl.SpriteHeight(LCDC);
        int row = LY - (sprite.Y - 16);
        if (sprite.YFlip) row = height - 1 - row;

        int tileIndex = sprite.Tile;
        if (height == 16)
        {
            tileIndex &= 0xFE;
            if (row >= 8) { tileIndex |= 1; row -= 8; }
        }

        int vramBankBit = 0;
        if (_hwMode == HardwareMode.Cgb && sprite.CgbVramBank1) vramBankBit = 0x2000;

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

    public void Reset()
    {
        LY = 0;
        Dot = 0;
        Mode = PpuMode.OamScan;
        _fifo.Reset();
        _windowLineCounter = 0;
        _windowTriggeredThisLine = false;
        _initialDiscardDone = false;
        _prevStatLine = false;
    }
}
