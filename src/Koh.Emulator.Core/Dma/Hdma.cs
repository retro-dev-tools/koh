using Koh.Emulator.Core.Bus;
using Koh.Emulator.Core.State;

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
        IsHBlankMode = (value & 0x80) != 0;
        ushort src = (ushort)(((Source1 << 8) | Source2) & 0xFFF0);
        ushort dst = (ushort)(0x8000 | (((Dest1 << 8) | Dest2) & 0x1FF0));

        if (!IsHBlankMode)
        {
            // General-purpose HDMA halts the CPU for the full transfer — on
            // hardware ~8 µs per 16-byte block (Pan Docs), the same wall-clock
            // cost in single and double speed. We *arm* it here and let the
            // system drive it block-by-block (GameBoySystem drains it while
            // ticking the PPU), so a transfer that runs past VBlank tears
            // exactly as on hardware. The CPU stays frozen for the WHOLE
            // transfer (drain runs to completion before the CPU resumes), so
            // it still can't race in with a VBK flip mid-transfer — the chained
            // "VBK=N, block, VBK=M, block" pattern stays correct.
            _bytesRemaining = blocks * 16;
            _byteIndexInBlock = 0;
            Active = true;
            CpuHaltedByGp = true;
            _currentSource = src;
            _currentDest = dst;
            _hblockPending = false;
            return;
        }

        // HBlank mode: arm the state machine; transfers happen 16 bytes per
        // HBlank via TickT.
        _bytesRemaining = blocks * 16;
        _byteIndexInBlock = 0;
        Active = true;
        _currentSource = src;
        _currentDest = dst;
        CpuHaltedByGp = false;
        _hblockPending = false;
    }

    /// <summary>
    /// Transfer one 16-byte block of an armed general-purpose transfer. The
    /// caller (GameBoySystem) burns the block's dot cost while ticking the PPU
    /// after each call, so the bytes land interleaved with rendering. Writes go
    /// through WriteByteHdma, which respects the PPU mode-3 VRAM lock and drops
    /// blocks that overran their window — matching real CGB hardware.
    /// Clears <see cref="CpuHaltedByGp"/>/<see cref="Active"/> on the last block.
    /// </summary>
    public void TransferOneGpBlock()
    {
        if (!CpuHaltedByGp) return;

        int bytesThisBlock = Math.Min(16, _bytesRemaining);
        for (int i = 0; i < bytesThisBlock; i++)
        {
            // WriteByteHdma respects the mode-3 VRAM lock: a block that lands
            // while the PPU owns VRAM (a transfer that overran its window) is
            // dropped, matching real CGB hardware. HBlank-mode blocks run in
            // mode 0 (VRAM free), so theirs still land.
            _mmu.WriteByteHdma(_currentDest, _mmu.ReadByteDirect(_currentSource));
            _currentSource++;
            _currentDest++;
            _bytesRemaining--;
        }

        if (_bytesRemaining <= 0)
        {
            Active = false;
            CpuHaltedByGp = false;
        }
    }

    public void OnHBlankEntered()
    {
        if (!Active || !IsHBlankMode) return;

        // Transfer one 16-byte block atomically. On real hardware the CPU
        // stalls for ~8 M-cycles per block, so from software's point of view
        // the block completes as an indivisible operation inside HBlank. By
        // doing the whole block in a single call we guarantee the CPU can't
        // race in and flip VBK or otherwise stomp on the transfer —
        // previously half-written blocks were the cause of garbled BG tiles
        // / attributes in CGB games that stream mid-frame via HBlank HDMA.
        int bytesThisBlock = Math.Min(16, _bytesRemaining);
        for (int i = 0; i < bytesThisBlock; i++)
        {
            // WriteByteHdma respects the mode-3 VRAM lock: a block that lands
            // while the PPU owns VRAM (a transfer that overran its window) is
            // dropped, matching real CGB hardware. HBlank-mode blocks run in
            // mode 0 (VRAM free), so theirs still land.
            _mmu.WriteByteHdma(_currentDest, _mmu.ReadByteDirect(_currentSource));
            _currentSource++;
            _currentDest++;
            _bytesRemaining--;
        }
        _byteIndexInBlock = 0;
        _hblockPending = false;

        if (_bytesRemaining <= 0)
        {
            Active = false;
        }
    }

    public void TickT()
    {
        // HBlank HDMA completes each block atomically in OnHBlankEntered, and
        // general-purpose HDMA is drained block-by-block by GameBoySystem (via
        // TransferOneGpBlock) — it's only armed in WriteLengthRegister. There is
        // nothing for the per-T-cycle path to do — kept as a no-op so existing
        // call sites in GameBoySystem don't need to change and so the
        // CpuHaltedByGp flag stays false when a transfer finishes between polls.
        if (!Active) CpuHaltedByGp = false;
    }

    public void WriteState(StateWriter w)
    {
        w.WriteByte(Source1); w.WriteByte(Source2);
        w.WriteByte(Dest1); w.WriteByte(Dest2);
        w.WriteBool(IsHBlankMode); w.WriteBool(Active); w.WriteBool(CpuHaltedByGp);
        w.WriteU16(_currentSource); w.WriteU16(_currentDest);
        w.WriteI32(_bytesRemaining); w.WriteI32(_byteIndexInBlock);
        w.WriteBool(_hblockPending);
    }

    public void ReadState(StateReader r)
    {
        Source1 = r.ReadByte(); Source2 = r.ReadByte();
        Dest1 = r.ReadByte(); Dest2 = r.ReadByte();
        IsHBlankMode = r.ReadBool(); Active = r.ReadBool(); CpuHaltedByGp = r.ReadBool();
        _currentSource = r.ReadU16(); _currentDest = r.ReadU16();
        _bytesRemaining = r.ReadI32(); _byteIndexInBlock = r.ReadI32();
        _hblockPending = r.ReadBool();
    }
}
