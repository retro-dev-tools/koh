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
}
