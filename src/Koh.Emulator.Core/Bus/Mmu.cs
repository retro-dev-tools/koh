using Koh.Emulator.Core.Cartridge;
using Koh.Emulator.Core.Cgb;
using Koh.Emulator.Core.Debug;
using Koh.Emulator.Core.Dma;
using Koh.Emulator.Core.State;

namespace Koh.Emulator.Core.Bus;

/// <summary>
/// Routes memory accesses across the Game Boy address space.
/// Phase 1: basic routing only. PPU mode lockout and DMA contention arrive in Phase 2.
/// </summary>
public sealed class Mmu
{
    private readonly Cartridge.Cartridge _cart;
    private readonly byte[] _vram = new byte[0x2000 * 2]; // 2 banks for CGB
    private readonly byte[] _wram = new byte[0x1000 * 8]; // 8 banks for CGB
    private readonly byte[] _oam = new byte[0xA0];
    private readonly byte[] _hram = new byte[0x7F];
    public IoRegisters Io { get; }

    /// <summary>Direct VRAM array access for the PPU (bypasses mode lockouts).</summary>
    public byte[] VramArray => _vram;

    /// <summary>Direct OAM array access for the PPU (bypasses mode lockouts).</summary>
    public byte[] OamArray => _oam;

    public VramWramBanking Banking { get; } = new();
    private int VramBank => Banking.VramBank;
    private int WramBank => Banking.WramBank;

    private OamDma? _oamDma;
    private Ppu.Ppu? _ppu;

    public MemoryHook? Hook { get; set; }

    public Mmu(Cartridge.Cartridge cart, IoRegisters io)
    {
        _cart = cart;
        Io = io;
    }

    public void AttachOamDma(OamDma oamDma) => _oamDma = oamDma;

    public void AttachPpu(Ppu.Ppu ppu) => _ppu = ppu;

    /// <summary>True when the PPU has VRAM locked (mode 3 with LCD on).</summary>
    private bool VramLocked =>
        _ppu is not null && (_ppu.LCDC & 0x80) != 0 && _ppu.Mode == Ppu.PpuMode.Drawing;

    /// <summary>True when the PPU has OAM locked (modes 2/3 with LCD on).</summary>
    private bool OamLocked =>
        _ppu is not null
        && (_ppu.LCDC & 0x80) != 0
        && (_ppu.Mode == Ppu.PpuMode.Drawing || _ppu.Mode == Ppu.PpuMode.OamScan);

    /// <summary>
    /// Read that bypasses DMA contention — used by the DMA engine itself when
    /// sourcing bytes, and for debug reads.
    /// </summary>
    public byte ReadByteDirect(ushort address) => ReadByteInternal(address);

    /// <summary>
    /// Write that bypasses PPU/DMA lockouts. Used for debug pokes and for DMA
    /// destinations that are genuinely unaffected by PPU mode (e.g. OAM DMA,
    /// which has its own bus).
    /// </summary>
    public void WriteByteDirect(ushort address, byte value)
    {
        switch (address >> 12)
        {
            case 0x8:
            case 0x9:
                _vram[VramBank * 0x2000 + (address - 0x8000)] = value;
                return;
            default:
                // For non-VRAM destinations fall back to the regular path.
                WriteByte(address, value);
                return;
        }
    }

    /// <summary>
    /// VRAM write for an HDMA/GDMA transfer. Unlike <see cref="WriteByteDirect"/>
    /// this RESPECTS the mode-3 VRAM lock: on real CGB hardware the HDMA engine
    /// shares the CPU's bus path to VRAM, so a transfer byte that lands while the
    /// PPU owns VRAM (mode 3) is silently dropped and the destination keeps its
    /// old value (Pan Docs: GDMA "blindly attempts to copy ... even if the LCD
    /// controller is currently accessing VRAM"). Modeling this makes a GDMA that
    /// overruns VBlank into active rendering leave the stale tiles/attributes it
    /// leaves on hardware, instead of an always-succeeding copy hiding the bug.
    /// </summary>
    public void WriteByteHdma(ushort address, byte value)
    {
        if (address >= 0x8000 && address < 0xA000)
        {
            if (!VramLocked)
                _vram[VramBank * 0x2000 + (address - 0x8000)] = value;
            return;
        }
        WriteByteDirect(address, value); // GDMA targets are always VRAM; be safe.
    }

    public byte ReadByte(ushort address)
    {
        // OAM DMA contention: during DMA, the CPU sees $FF on all "external"
        // buses (ROM/VRAM/WRAM/SRAM/OAM). HRAM and I/O registers remain
        // accessible per Mooneye oam_dma/reg_read behaviour.
        byte value;
        if (_oamDma is { IsBusLocking: true } && address < 0xFF00)
            value = 0xFF;
        // PPU mode lockouts: VRAM is unreadable during mode 3, OAM during
        // modes 2 and 3. Reads return $FF (real-hardware behaviour).
        else if (address >= 0x8000 && address < 0xA000 && VramLocked)
            value = 0xFF;
        else if (address >= 0xFE00 && address < 0xFEA0 && OamLocked)
            value = 0xFF;
        else
            value = ReadByteInternal(address);
        Hook?.OnRead(address, value);
        return value;
    }

    private byte ReadByteInternal(ushort address)
    {
        switch (address >> 12)
        {
            case 0x0:
            case 0x1:
            case 0x2:
            case 0x3:
            case 0x4:
            case 0x5:
            case 0x6:
            case 0x7:
                return _cart.ReadRom(address);
            case 0x8:
            case 0x9:
                return _vram[VramBank * 0x2000 + (address - 0x8000)];
            case 0xA:
            case 0xB:
                return _cart.ReadRam(address);
            case 0xC:
                return _wram[address - 0xC000];
            case 0xD:
                return _wram[WramBank * 0x1000 + (address - 0xD000)];
            case 0xE:
                return _wram[address - 0xE000]; // echo RAM $C000-$CFFF
            case 0xF:
                if (address < 0xFE00)
                    return _wram[WramBank * 0x1000 + (address - 0xF000)]; // echo RAM $D000-$DDFF
                if (address < 0xFEA0)
                    return _oam[address - 0xFE00];
                if (address < 0xFF00)
                    return 0x00; // prohibited region
                if (address == 0xFFFF)
                    return Io.ReadIe();
                if (address >= 0xFF80)
                    return _hram[address - 0xFF80];
                return Io.Read(address);
        }
        return 0xFF;
    }

    public void WriteByte(ushort address, byte value)
    {
        Hook?.OnWrite(address, value);

        // OAM DMA contention: CPU writes to external memory (ROM, VRAM, WRAM,
        // SRAM, OAM) are dropped while the bus is locked. HRAM and I/O
        // registers remain writable.
        if (_oamDma is { IsBusLocking: true } && address < 0xFF00)
            return;

        // $FF46 triggers OAM DMA.
        if (address == 0xFF46 && _oamDma is not null)
        {
            _oamDma.Trigger(value);
            // Still store the value so reads return what was written.
        }

        switch (address >> 12)
        {
            case 0x0:
            case 0x1:
            case 0x2:
            case 0x3:
            case 0x4:
            case 0x5:
            case 0x6:
            case 0x7:
                _cart.WriteRom(address, value);
                return;
            case 0x8:
            case 0x9:
                // VRAM writes during PPU mode 3 are silently dropped on real
                // hardware; modeling this lets ROM-side bugs (e.g. board
                // commits that overflow VBlank) show up in verification.
                if (!VramLocked)
                    _vram[VramBank * 0x2000 + (address - 0x8000)] = value;
                return;
            case 0xA:
            case 0xB:
                _cart.WriteRam(address, value);
                return;
            case 0xC:
                _wram[address - 0xC000] = value;
                return;
            case 0xD:
                _wram[WramBank * 0x1000 + (address - 0xD000)] = value;
                return;
            case 0xE:
                _wram[address - 0xE000] = value;
                return;
            case 0xF:
                if (address < 0xFE00)
                {
                    _wram[WramBank * 0x1000 + (address - 0xF000)] = value;
                    return;
                }
                if (address < 0xFEA0)
                {
                    if (!OamLocked)
                        _oam[address - 0xFE00] = value;
                    return;
                }
                if (address < 0xFF00)
                    return;
                if (address == 0xFFFF)
                {
                    Io.WriteIe(value);
                    return;
                }
                if (address >= 0xFF80)
                {
                    _hram[address - 0xFF80] = value;
                    return;
                }
                Io.Write(address, value);
                return;
        }
    }

    /// <summary>
    /// Raw debug read. Bypasses access restrictions (none in Phase 1).
    /// Phase 2 adds bypass of PPU mode lockout.
    /// </summary>
    /// <summary>Debug/inspector read that bypasses OAM-DMA bus contention.
    /// Tools want to see actual memory values regardless of what the CPU
    /// would see at that moment. ReadByte returns $FF for ext-bus reads
    /// during DMA; DebugRead always returns the underlying byte.</summary>
    public byte DebugRead(ushort address) => ReadByteInternal(address);

    /// <summary>
    /// Raw debug write per §7.10. Caller is responsible for enforcing the paused-only rule.
    /// </summary>
    public bool DebugWrite(ushort address, byte value)
    {
        switch (address >> 12)
        {
            case 0x0:
            case 0x1:
            case 0x2:
            case 0x3:
            case 0x4:
            case 0x5:
            case 0x6:
            case 0x7:
                // Live ROM patch per §7.10.
                // Phase 1: patch only bank 0 for MBC1 to keep the contract simple.
                if (_cart.Header.MapperKind == MapperKind.RomOnly)
                {
                    if (address < _cart.Rom.Length)
                        _cart.Rom[address] = value;
                }
                else
                {
                    if (address < 0x4000 && address < _cart.Rom.Length)
                        _cart.Rom[address] = value;
                }
                return true;
            default:
                WriteByte(address, value);
                return true;
        }
    }

    // Convenience accessors used by debugger scopes.
    public ReadOnlySpan<byte> Vram => _vram;
    public ReadOnlySpan<byte> Oam => _oam;
    public ReadOnlySpan<byte> Hram => _hram;

    public void WriteState(StateWriter w)
    {
        w.WriteBytes(_vram);
        w.WriteBytes(_wram);
        w.WriteBytes(_oam);
        w.WriteBytes(_hram);
        Banking.WriteState(w);
    }

    public void ReadState(StateReader r)
    {
        r.ReadBytes(_vram.AsSpan());
        r.ReadBytes(_wram.AsSpan());
        r.ReadBytes(_oam.AsSpan());
        r.ReadBytes(_hram.AsSpan());
        Banking.ReadState(r);
    }
}
