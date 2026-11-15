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
