using Koh.Emulator.Core.Bus;

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

    public OamDma(Mmu mmu) { _mmu = mmu; }

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
        byte value = _mmu.ReadByteDirect(src);
        _mmu.OamArray[_byteIndex] = value;
    }
}
