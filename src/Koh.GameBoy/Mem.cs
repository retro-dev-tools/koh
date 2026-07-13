namespace Koh.GameBoy;

/// <summary>
/// The WRAM arena allocator, as the managed reference runtime. On a ROM the Koh compiler lowers
/// <c>Mem.Alloc</c>/<c>Mem.Reset</c> to its intrinsic bump allocator (a heap pointer descending from
/// <c>CSharpFrontend.HeapTop</c>, $DE00); this class backs the same names for the desktop build with
/// the same downward-bump semantics over the simulated address space, so pointer arithmetic on an
/// allocation behaves identically in both worlds. Managed-only: it lives outside <c>Hal/</c>, so the
/// SDK never feeds it to the Koh frontend (which has its own <c>Mem</c> intrinsic).
/// </summary>
public static unsafe class Mem
{
    /// <summary>Top of the arena; mirrors the compiler's <c>HeapTop</c> ($DE00).</summary>
    private const ushort HeapTop = 0xDE00;

    private static ushort _heap = HeapTop;

    /// <summary>Bump the heap pointer down by <paramref name="size"/> bytes and return the new
    /// pointer. No individual free — <see cref="Reset"/> releases the whole arena at once.</summary>
    public static byte* Alloc(int size)
    {
        _heap = (ushort)(_heap - size);
        return Gb.Base + _heap;
    }

    /// <summary>Restore the heap pointer to the top of the arena, freeing every allocation.</summary>
    public static void Reset() => _heap = HeapTop;

    /// <summary>Copy <paramref name="count"/> bytes from <paramref name="source"/> to
    /// <paramref name="destination"/>, in ascending address order (a forward copy). Overlapping regions
    /// are only defined when <paramref name="destination"/> &lt; <paramref name="source"/> (each source
    /// byte is read before a lower/equal destination index could reach and overwrite it); a
    /// <paramref name="count"/> of zero is a no-op. NOT vblank-aware — same stance as
    /// <c>Cgb.CopyToVram</c>: the caller is responsible for PPU-mode safety when the destination is
    /// VRAM/OAM. Mirrors the compiler's <c>__mem_copy</c> runtime (<c>MemRuntime.cs</c>) bit-for-bit, so
    /// desktop and ROM builds agree.</summary>
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

    /// <summary>Fill <paramref name="count"/> bytes starting at <paramref name="destination"/> with
    /// <paramref name="value"/>, in ascending address order. A <paramref name="count"/> of zero is a
    /// no-op. NOT vblank-aware — same stance as <c>Cgb.CopyToVram</c>: the caller is responsible for
    /// PPU-mode safety when the destination is VRAM/OAM. Mirrors the compiler's <c>__mem_fill</c> runtime
    /// (<c>MemRuntime.cs</c>) bit-for-bit, so desktop and ROM builds agree.</summary>
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
