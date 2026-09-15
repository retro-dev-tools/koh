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
    /// pointer. No individual free — <see cref="Reset"/> releases the whole arena at once.
    /// <c>[KohIntrinsic("alloc")]</c>: the arena heap is COMPILER-owned (<c>new</c> bumps the same
    /// <c>__heap</c> global — see <c>CilLoweringContext.EnsureHeapGlobal</c>/<c>CSharpFrontend.HeapTop</c>),
    /// and this managed body reaches <see cref="Gb.Base"/>'s <c>Unsafe.AsPointer</c> (BCL, unlowerable),
    /// so a frontend must intercept the call rather than lower this body.</summary>
    [KohIntrinsic("alloc")]
    public static byte* Alloc(int size)
    {
        _heap = (ushort)(_heap - size);
        return Gb.Base + _heap;
    }

    /// <summary>Restore the heap pointer to the top of the arena, freeing every allocation.
    /// <c>[KohIntrinsic("heapreset")]</c> — same compiler-owned-heap reasoning as <see cref="Alloc"/>.</summary>
    [KohIntrinsic("heapreset")]
    public static void Reset() => _heap = HeapTop;

    /// <summary>Copy <paramref name="count"/> bytes from <paramref name="source"/> to
    /// <paramref name="destination"/>, in ascending address order (a forward copy). Overlapping regions
    /// are only defined when <paramref name="destination"/> &lt; <paramref name="source"/> (each source
    /// byte is read before a lower/equal destination index could reach and overwrite it); a
    /// <paramref name="count"/> of zero is a no-op. NOT vblank-aware — same stance as
    /// <c>Cgb.CopyToVram</c>: the caller is responsible for PPU-mode safety when the destination is
    /// VRAM/OAM.
    ///
    /// This body is ordinary compiled Koh C#: the CIL frontend lowers it as referenced code (it is NOT a
    /// <c>[KohIntrinsic]</c>), so the shape below is exactly what runs on a ROM, and both desktop and ROM
    /// builds agree by construction — there is no separate hand-written assembly runtime to keep in sync.
    ///
    /// The loop body is shaped as a stride-1 pointer walk (increment form, not indexed) with a BYTE
    /// induction variable — the SM83 backend's loop register-residency pass only keeps i8 induction
    /// values resident in registers, so a plain <c>ushort count</c> counter round-trips through its WRAM
    /// slot every iteration. Splitting the <c>ushort</c> count into a byte block count
    /// (<c>count &gt;&gt; 8</c>) plus a byte remainder, and running the copy 256 bytes at a time via a
    /// byte inner counter that wraps 0 -&gt; 255 -&gt; 0, keeps the counter i8-resident.
    ///
    /// The block and remainder passes deliberately share ONE inner loop body (one <c>do</c>/<c>while</c>
    /// node in the source), reached through an <c>if</c>/<c>else</c> that only picks the starting counter
    /// value, rather than two separate sequential loops. Two textually distinct stride-1 pointer-walk
    /// loops in the same function — even non-overlapping ones over disjoint memory, confirmed with a
    /// minimal repro — corrupt each other under the SM83 backend's register allocator/residency pass (a
    /// pre-existing loop/pointer register liveness bug across sibling loops). A SINGLE inner loop node
    /// executed repeatedly by an outer loop does not trigger it. Do not reintroduce a second sequential
    /// loop here without re-verifying against that failure mode first. Measured marginal rate
    /// (fixed-overhead-free, see <c>MemRuntimeTests.Copy_MarginalCostPerByte_IsWithinLooseCeiling</c>):
    /// ~302 dots/byte, down from 424.6 dots/byte pre-tuning.</summary>
    public static void Copy(byte* destination, byte* source, ushort count)
    {
        byte blocks = (byte)(count >> 8);
        byte remainder = (byte)count;
        while (blocks != 0 || remainder != 0)
        {
            byte i;
            if (blocks != 0)
            {
                i = 0;
                blocks--;
            }
            else
            {
                i = (byte)(0 - remainder);
                remainder = 0;
            }
            do
            {
                *destination = *source;
                destination++;
                source++;
                i++;
            } while (i != 0);
        }
    }

    /// <summary>Fill <paramref name="count"/> bytes starting at <paramref name="destination"/> with
    /// <paramref name="value"/>, in ascending address order. A <paramref name="count"/> of zero is a
    /// no-op. NOT vblank-aware — same stance as <c>Cgb.CopyToVram</c>: the caller is responsible for
    /// PPU-mode safety when the destination is VRAM/OAM.
    ///
    /// This body is ordinary compiled Koh C# (not a <c>[KohIntrinsic]</c>) — see <see cref="Copy"/>'s
    /// remarks for why the loop is shaped as a byte-induction-variable block/remainder walk sharing one
    /// <c>do</c>/<c>while</c> node instead of two sequential loops; the same reasoning and register-
    /// allocator constraint apply here.</summary>
    public static void Fill(byte* destination, byte value, ushort count)
    {
        byte blocks = (byte)(count >> 8);
        byte remainder = (byte)count;
        while (blocks != 0 || remainder != 0)
        {
            byte i;
            if (blocks != 0)
            {
                i = 0;
                blocks--;
            }
            else
            {
                i = (byte)(0 - remainder);
                remainder = 0;
            }
            do
            {
                *destination = value;
                destination++;
                i++;
            } while (i != 0);
        }
    }
}
