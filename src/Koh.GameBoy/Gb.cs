using System.Runtime.CompilerServices;

namespace Koh.GameBoy;

/// <summary>
/// The Game Boy address space, as real memory. On hardware these are fixed addresses the CPU pokes
/// directly; the Koh compiler lowers <c>Gb.Vram</c> and friends to those constant pointers. Under the
/// plain .NET SDK the same names resolve here, to pointers into one pinned 64&#160;KiB buffer, so a
/// game's raw pointer arithmetic runs unchanged on the desktop against <see cref="Koh.GameBoy"/>.
/// </summary>
public static unsafe class Gb
{
    // A single pinned buffer models the whole 64 KiB bus. Pinned so the pointers below stay valid for
    // the process lifetime (the game holds onto them and does its own arithmetic).
    private static readonly byte[] MemoryArray = GC.AllocateArray<byte>(0x1_0000, pinned: true);

    /// <summary>Base of the simulated address space (address 0x0000).</summary>
    internal static byte* Base => (byte*)Unsafe.AsPointer(ref MemoryArray[0]);

    /// <summary>Read a byte at an absolute address (used by the runtime's renderer). Like the raw
    /// pointer bases below, the address must fall inside the 64&#160;KiB space — an out-of-range read
    /// faults here rather than silently wrapping, matching how an out-of-range pointer write faults.</summary>
    internal static byte Peek(int address) => MemoryArray[address];

    /// <summary>Video RAM / background tile data (0x8000).</summary>
    [KohIntrinsic("region", 0x8000)]
    public static byte* Vram => Base + 0x8000;

    /// <summary>Background tile data, alias of <see cref="Vram"/> (0x8000).</summary>
    [KohIntrinsic("region", 0x8000)]
    public static byte* TileData => Base + 0x8000;

    /// <summary>Background tile map 0 (0x9800).</summary>
    [KohIntrinsic("region", 0x9800)]
    public static byte* TileMap => Base + 0x9800;

    /// <summary>Background tile map 1 (0x9C00).</summary>
    [KohIntrinsic("region", 0x9C00)]
    public static byte* TileMap1 => Base + 0x9C00;

    /// <summary>Work RAM (0xC000).</summary>
    [KohIntrinsic("region", 0xC000)]
    public static byte* Wram => Base + 0xC000;

    /// <summary>Object attribute memory / sprites (0xFE00).</summary>
    [KohIntrinsic("region", 0xFE00)]
    public static byte* Oam => Base + 0xFE00;
}
