using Koh.Emulator.Core.Cpu;
using Koh.Emulator.Core.State;

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
    /// <summary>$FF6C OPRI (CGB only). bit 0: 0 = CGB priority (by OAM index),
    /// 1 = DMG-compat priority (by X coord). Default 0 on CGB.</summary>
    public byte OPRI;
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
                PushSpritePixelsForCurrentLcdX(sprite, _spriteFetchIndex);
                _lineSpriteConsumed[_spriteFetchIndex] = true;
                _spriteFetchIndex = -1;
            }
            Dot++;
            return;
        }

        // Sprite trigger: runs independently of BG FIFO state. Real hardware checks
        // every dot; requiring BG pixels here causes misses when the BG FIFO drains
        // momentarily between fetcher pushes.
        if ((LCDC & LcdControl.ObjEnable) != 0)
        {
            for (int i = 0; i < _lineSpriteCount; i++)
            {
                if (_lineSpriteConsumed[i]) continue;
                int oamX = _lineSprites[i].X;
                if (oamX == 0 || oamX >= 168) continue;  // entirely off-screen
                int spriteX = oamX - 8;
                int visibleStart = spriteX < 0 ? 0 : spriteX;
                if (visibleStart == _lcdX)
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
                // CGB BG attribute bit 5 flips the tile horizontally.
                bool xFlip = _hwMode == HardwareMode.Cgb && (_fetcher.FetchedAttributes & 0x20) != 0;
                var colors = DecodeTileRow(_fetcher.FetchedLow, _fetcher.FetchedHigh, xFlip);
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
        // Window uses window-line-counter, BG uses (LY + SCY).
        int tileRow;
        if (_fetcher.UsingWindow)
            tileRow = _windowLineCounter & 7;
        else
            tileRow = (LY + SCY) & 7;

        // CGB BG attribute bit 6 flips the tile vertically.
        if (_hwMode == HardwareMode.Cgb && (_fetcher.FetchedAttributes & 0x40) != 0)
            tileRow = 7 - tileRow;

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

    private static byte[] DecodeTileRow(byte low, byte high, bool xFlip = false)
    {
        var result = new byte[8];
        for (int i = 0; i < 8; i++)
        {
            int bit = xFlip ? i : (7 - i);
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

        if (_hwMode == HardwareMode.Cgb)
        {
            EmitPixelCgb(pixelIdx, back, bgColor, bgAttrs, spriteColor, spritePalette, spriteFlags);
            return;
        }

        // On DMG with BG-enable bit clear, BG always reads as color 0.
        bool bgDisabledDmg = (LCDC & LcdControl.BgWindowEnableOrPriority) == 0;
        if (bgDisabledDmg) bgColor = 0;

        // Resolve final color index via DMG palette selection.
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

    private void EmitPixelCgb(int pixelIdx, Span<byte> back, byte bgColor, byte bgAttrs,
                              byte spriteColor, byte spritePalette, byte spriteFlags)
    {
        // CGB priority rules (per acid2's bg-to-oam-priority test, validated
        // against the reference PNG):
        //
        // - LCDC bit 0 = 0: BG always wins over sprite (for BG colors 1-3).
        //   Sprite only shows through when BG color is 0. OAM/BG priority bits
        //   are ignored.
        // - LCDC bit 0 = 1: honor BG-attr bit 7 and OAM bit 7 — BG wins over
        //   sprite when either is set AND BG color is 1-3.
        bool lcdc0 = (LCDC & LcdControl.BgWindowEnableOrPriority) != 0;
        bool bgHasPriority;
        // Revert to: LCDC.0=0 → sprite always wins; LCDC.0=1 → priority bits honored.
        if (!lcdc0)
        {
            bgHasPriority = false;
        }
        else
        {
            bgHasPriority = (bgAttrs & 0x80) != 0 || (spriteFlags & ObjectAttributes.FlagBgPriority) != 0;
        }

        // Sprite wins when: sprite color is non-zero AND (BG doesn't have priority OR BG is transparent).
        bool useSpriteColor = spriteColor != 0 && (!bgHasPriority || bgColor == 0);

        ushort bgr555;
        if (useSpriteColor)
        {
            int palIndex = spritePalette & 0x07;
            bgr555 = ObjPalette.GetColor(palIndex, spriteColor);
        }
        else
        {
            int palIndex = bgAttrs & 0x07;
            bgr555 = BgPalette.GetColor(palIndex, bgColor);
        }

        (byte r, byte g, byte b) = Bgr555ToRgb8(bgr555);
        back[pixelIdx + 0] = r;
        back[pixelIdx + 1] = g;
        back[pixelIdx + 2] = b;
        back[pixelIdx + 3] = 0xFF;
    }

    private static (byte r, byte g, byte b) Bgr555ToRgb8(ushort bgr555)
    {
        int r5 = bgr555 & 0x1F;
        int g5 = (bgr555 >> 5) & 0x1F;
        int b5 = (bgr555 >> 10) & 0x1F;
        // Scale 5-bit to 8-bit: (v * 255 + 15) / 31 ≈ (v << 3) | (v >> 2).
        byte r = (byte)((r5 << 3) | (r5 >> 2));
        byte g = (byte)((g5 << 3) | (g5 >> 2));
        byte b = (byte)((b5 << 3) | (b5 >> 2));
        return (r, g, b);
    }

    private static byte ApplyDmgPalette(byte colorIdx, byte palette)
        => (byte)((palette >> (colorIdx * 2)) & 0x03);

    private void PushSpritePixelsForCurrentLcdX(ObjectAttributes sprite, int scanIndex)
    {
        // Priority key: lower = higher priority.
        // DMG (or CGB with OPRI=1): lower X coordinate wins. Ties broken by OAM order.
        // CGB with OPRI=0: lower OAM/scan index wins.
        int priorityKey;
        if (_hwMode == HardwareMode.Cgb && (OPRI & 1) == 0)
            priorityKey = scanIndex;
        else
            priorityKey = (sprite.X << 8) | scanIndex;

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

        // Skip N leftmost sprite pixels when OAM.X < 8 (partial off-screen left).
        int skipLeft = sprite.X < 8 ? 8 - sprite.X : 0;

        for (int px = skipLeft; px < 8; px++)
        {
            int bit = sprite.XFlip ? px : (7 - px);
            int lo = (low >> bit) & 1;
            int hi = (high >> bit) & 1;
            byte color = (byte)((hi << 1) | lo);
            _fifo.PushSpritePixel(px - skipLeft, color, sprite.CgbPalette, sprite.Flags, priorityKey);
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

    public void WriteState(StateWriter w)
    {
        w.WriteByte(LY); w.WriteByte(LYC);
        w.WriteByte(SCX); w.WriteByte(SCY);
        w.WriteByte(WX); w.WriteByte(WY);
        w.WriteByte(BGP); w.WriteByte(OBP0); w.WriteByte(OBP1);
        w.WriteByte(LCDC); w.WriteByte(OPRI);
        w.WriteByte(Stat.UserBits);
        w.WriteI32((int)Mode); w.WriteI32(Dot);
        w.WriteI32(_windowLineCounter);
        w.WriteBool(_windowTriggeredThisLine);
        w.WriteBool(_initialDiscardDone);
        w.WriteBool(_prevStatLine);
        BgPalette.WriteState(w);
        ObjPalette.WriteState(w);

        // Intra-scanline state for mid-frame determinism.
        _fetcher.WriteState(w);
        _fifo.WriteState(w);
        w.WriteI32(_lcdX);
        w.WriteI32(_spriteFetchIndex);
        w.WriteI32(_spritePenaltyDots);
        w.WriteI32(_lineSpriteCount);
        for (int i = 0; i < _lineSprites.Length; i++)
        {
            var s = _lineSprites[i];
            w.WriteByte(s.Y); w.WriteByte(s.X); w.WriteByte(s.Tile); w.WriteByte(s.Flags);
        }
        for (int i = 0; i < _lineSpriteConsumed.Length; i++) w.WriteBool(_lineSpriteConsumed[i]);
    }

    public void ReadState(StateReader r)
    {
        LY = r.ReadByte(); LYC = r.ReadByte();
        SCX = r.ReadByte(); SCY = r.ReadByte();
        WX = r.ReadByte(); WY = r.ReadByte();
        BGP = r.ReadByte(); OBP0 = r.ReadByte(); OBP1 = r.ReadByte();
        LCDC = r.ReadByte(); OPRI = r.ReadByte();
        Stat.UserBits = r.ReadByte();
        Mode = (PpuMode)r.ReadI32(); Dot = r.ReadI32();
        _windowLineCounter = r.ReadI32();
        _windowTriggeredThisLine = r.ReadBool();
        _initialDiscardDone = r.ReadBool();
        _prevStatLine = r.ReadBool();
        BgPalette.ReadState(r);
        ObjPalette.ReadState(r);

        _fetcher.ReadState(r);
        _fifo.ReadState(r);
        _lcdX = r.ReadI32();
        _spriteFetchIndex = r.ReadI32();
        _spritePenaltyDots = r.ReadI32();
        _lineSpriteCount = r.ReadI32();
        for (int i = 0; i < _lineSprites.Length; i++)
        {
            byte y = r.ReadByte(), x = r.ReadByte(), tile = r.ReadByte(), flags = r.ReadByte();
            _lineSprites[i] = new ObjectAttributes(y, x, tile, flags);
        }
        for (int i = 0; i < _lineSpriteConsumed.Length; i++) _lineSpriteConsumed[i] = r.ReadBool();
    }
}
