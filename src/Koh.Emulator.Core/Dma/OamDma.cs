using Koh.Emulator.Core.Bus;
using Koh.Emulator.Core.State;

namespace Koh.Emulator.Core.Dma;

/// <summary>
/// OAM DMA per spec §7.6: triggered by write to $FF46; 1 M-cycle (4 T-cycle) start delay;
/// 160 M-cycles transfer; CPU reads from non-HRAM return $FF during the contention window.
/// </summary>
public sealed class OamDma
{
    private readonly Mmu _mmu;
    public byte SourceHighByte { get; private set; }

    private int _tCountdownToStart;
    private int _byteIndex;
    private int _tCountdownInByte;
    private bool _running;

    public bool IsBusLocking { get; private set; }

    public OamDma(Mmu mmu)
    {
        _mmu = mmu;
    }

    public void Trigger(byte sourceHighByte)
    {
        SourceHighByte = sourceHighByte;
        _tCountdownToStart = 4;
        _byteIndex = 0;
        _tCountdownInByte = 4;
        _running = true;
    }

    public void TickT()
    {
        if (!_running)
        {
            IsBusLocking = false;
            return;
        }

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

        // Hardware quirk (Mooneye acceptance/oam_dma/sources-GS): the OAM DMA
        // source address decoder doesn't fully decode the top of the memory
        // map. A source in $E000-$FFFF (echo RAM, OAM, unmapped, I/O, HRAM,
        // IE) all alias into WRAM $C000-$DFFF by dropping address bit 13,
        // same as the ordinary $E000-$FDFF echo — DMA just applies it over
        // the whole top quarter instead of stopping at $FDFF. Below $E000 the
        // regular bus decode (ROM/VRAM/cart RAM/WRAM) is already correct.
        if (src >= 0xE000)
            src &= 0xDFFF;

        byte value = _mmu.ReadByteDirect(src);
        _mmu.OamArray[_byteIndex] = value;
    }

    public void WriteState(StateWriter w)
    {
        w.WriteByte(SourceHighByte);
        w.WriteI32(_tCountdownToStart);
        w.WriteI32(_byteIndex);
        w.WriteI32(_tCountdownInByte);
        w.WriteBool(_running);
        w.WriteBool(IsBusLocking);
    }

    public void ReadState(StateReader r)
    {
        SourceHighByte = r.ReadByte();
        _tCountdownToStart = r.ReadI32();
        _byteIndex = r.ReadI32();
        _tCountdownInByte = r.ReadI32();
        _running = r.ReadBool();
        IsBusLocking = r.ReadBool();
    }
}
