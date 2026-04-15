using Koh.Emulator.Core.Bus;

namespace Koh.Emulator.Core.Dma;

/// <summary>
/// CGB HDMA per spec §7.6. General-purpose mode halts the CPU until the full transfer
/// completes; HBlank mode transfers 16 bytes per HBlank entry until cancelled.
/// </summary>
public sealed class Hdma
{
    private readonly Mmu _mmu;

    public byte Source1;   // $FF51 high
    public byte Source2;   // $FF52 low
    public byte Dest1;     // $FF53 high (masked to $80..$9F)
    public byte Dest2;     // $FF54 low

    public bool IsHBlankMode { get; private set; }
    public bool Active { get; private set; }
    public bool CpuHaltedByGp { get; private set; }

    private ushort _currentSource;
    private ushort _currentDest;
    private int _bytesRemaining;          // total bytes left in the whole transfer
    private int _byteIndexInBlock;        // 0..15 within the current 16-byte block
    private bool _hblockPending;

    public Hdma(Mmu mmu) { _mmu = mmu; }

    public byte ReadLengthRegister()
    {
        if (!Active) return 0xFF;
        int blocksRemaining = _bytesRemaining / 16;
        return (byte)((IsHBlankMode ? 0x80 : 0x00) | ((blocksRemaining - 1) & 0x7F));
    }

    public void WriteLengthRegister(byte value)
    {
        // Cancel HBlank transfer if bit 7 = 0 while an HBlank transfer is active.
        if (Active && IsHBlankMode && (value & 0x80) == 0)
        {
            Active = false;
            CpuHaltedByGp = false;
            return;
        }

        int blocks = (value & 0x7F) + 1;
        _bytesRemaining = blocks * 16;
        _byteIndexInBlock = 0;
        IsHBlankMode = (value & 0x80) != 0;
        Active = true;
        _currentSource = (ushort)(((Source1 << 8) | Source2) & 0xFFF0);
        _currentDest = (ushort)(0x8000 | (((Dest1 << 8) | Dest2) & 0x1FF0));
        CpuHaltedByGp = !IsHBlankMode;
        _hblockPending = false;
    }

    public void OnHBlankEntered()
    {
        if (Active && IsHBlankMode) _hblockPending = true;
    }

    public void TickT()
    {
        if (!Active) { CpuHaltedByGp = false; return; }

        if (IsHBlankMode && !_hblockPending) return;

        // Copy one byte per T-cycle (Phase 2 approximation; real hardware is 1 byte per M-cycle,
        // 2x rate in double-speed). This matches the acid2 gating coarse enough for pixel tests.
        byte value = _mmu.ReadByteDirect(_currentSource);
        _mmu.WriteByte(_currentDest, value);
        _currentSource++;
        _currentDest++;
        _bytesRemaining--;
        _byteIndexInBlock++;

        if (_byteIndexInBlock >= 16)
        {
            _byteIndexInBlock = 0;
            if (IsHBlankMode) _hblockPending = false;
        }

        if (_bytesRemaining <= 0)
        {
            Active = false;
            CpuHaltedByGp = false;
        }
    }
}
