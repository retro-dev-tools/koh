namespace Koh.GameBoy;

/// <summary>Raw WRAM storage. Reset releases raw blocks; managed arrays remain valid.</summary>
public static unsafe class Mem
{
    private const int Capacity = 4096;
    private const ushort ArenaEnd = 0xE000;

    private static int _used;

    /// <summary>Uninitialized bytes in WRAM bank1. Select bank1 when accessing the returned pointer.</summary>
    [KohIntrinsic("alloc")]
    public static byte* Alloc(int size)
    {
        if ((uint)size > Capacity)
            throw new ArgumentOutOfRangeException(nameof(size));
        if (size > Capacity - _used)
            throw new OutOfMemoryException("Raw WRAM arena exhausted");
        _used += size;
        return Gb.Base + ArenaEnd - _used;
    }

    /// <summary>Release raw blocks only; all previous raw pointers become invalid.</summary>
    [KohIntrinsic("heapreset")]
    public static void Reset() => _used = 0;

    /// <summary>Ascending byte copy, including overlap. Zero-count no-op. Caller-owned capacity and PPU timing.</summary>
    [KohIntrinsic("memcopy")]
    public static void Copy(byte* destination, byte* source, ushort count)
    {
        while (count != 0)
        {
            *destination = *source;
            destination++;
            source++;
            count--;
        }
    }

    /// <summary>Ascending fill. Zero-count no-op. Caller-owned capacity and PPU timing.</summary>
    [KohIntrinsic("memfill")]
    public static void Fill(byte* destination, byte value, ushort count)
    {
        while (count != 0)
        {
            *destination = value;
            destination++;
            count--;
        }
    }
}
