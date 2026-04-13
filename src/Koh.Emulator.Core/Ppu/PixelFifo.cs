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
