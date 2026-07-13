namespace Koh.Compiler.Frontends.CSharp;

/// <summary>
/// The <c>Mem.Copy</c>/<c>Mem.Fill</c> runtime, written in the Koh C# subset. The frontend appends this
/// source to a program that calls either one (see <c>CSharpFrontend.UsesMemRuntime</c>), so the calls —
/// which lower to <c>__mem_copy</c>/<c>__mem_fill</c> — resolve to compiled code, exactly like the
/// softfloat runtime (<see cref="SoftFloatRuntime"/>) is appended for <c>float</c>/<c>double</c>. Nothing
/// here is hand-written assembly or hardcoded in the backend: it is ordinary subset source the compiler
/// compiles, and every byte moved goes through the normal register allocator and instruction selection.
///
/// Semantics (mirrored in <c>Mem.cs</c> XML docs and <c>Koh.GameBoy.Mem</c>'s managed implementation):
/// forward copy (ascending address order); overlapping regions are defined only when
/// <c>destination &lt; source</c> (each source byte is read before it could be overwritten); a count of
/// zero is a no-op; NOT vblank-aware (same stance as <c>Cgb.CopyToVram</c>) — the caller is responsible
/// for PPU-mode safety when the destination is VRAM/OAM.
///
/// The loop body is shaped as a stride-1 pointer walk (increment form, not indexed) with a BYTE
/// induction variable — the loop register-residency pass (Wave 1 of the parallel loop-optimizer
/// package) only keeps i8 induction values resident in registers, so a plain `ushort count` counter
/// round-trips through its WRAM slot every iteration. Splitting the `ushort` count into a byte block
/// count (`count &gt;&gt; 8`) plus a byte remainder, and running the copy 256 bytes at a time via a byte
/// inner counter that wraps 0 -&gt; 255 -&gt; 0, keeps the counter i8-resident.
///
/// The block and remainder passes deliberately share ONE inner loop body (one `do`/`while` node in the
/// source), reached through an `if`/`else` that only picks the starting counter value, rather than two
/// separate sequential loops. Two textually distinct stride-1 pointer-walk loops in the same function
/// — even non-overlapping ones over disjoint memory, confirmed with a minimal repro — corrupt each
/// other under the current register allocator/residency pass (a pre-existing Wave 1 bug in loop/pointer
/// register liveness across sibling loops, out of this package's scope: MemRuntime.cs cannot touch the
/// backend). A SINGLE inner loop node executed repeatedly by an outer loop does not trigger it. Do not
/// reintroduce a second sequential loop here without re-verifying against that failure mode first.
/// Measured marginal rate (fixed-overhead-free, see
/// MemRuntimeTests.Copy_MarginalCostPerByte_IsWithinLooseCeiling): ~302 dots/byte, down from
/// 424.6 dots/byte pre-tuning.
/// </summary>
internal static class MemRuntime
{
    public const string Source =
        @"
// ---- Koh Mem.Copy / Mem.Fill runtime --------------------------------------

static void __mem_copy(byte* destination, byte* source, ushort count) {
    byte blocks = (byte)(count >> 8);
    byte remainder = (byte)count;
    while (blocks != 0 || remainder != 0) {
        byte i;
        if (blocks != 0) {
            i = 0;
            blocks--;
        } else {
            i = (byte)(0 - remainder);
            remainder = 0;
        }
        do {
            *destination = *source;
            destination++;
            source++;
            i++;
        } while (i != 0);
    }
}

static void __mem_fill(byte* destination, byte value, ushort count) {
    byte blocks = (byte)(count >> 8);
    byte remainder = (byte)count;
    while (blocks != 0 || remainder != 0) {
        byte i;
        if (blocks != 0) {
            i = 0;
            blocks--;
        } else {
            i = (byte)(0 - remainder);
            remainder = 0;
        }
        do {
            *destination = value;
            destination++;
            i++;
        } while (i != 0);
    }
}
";
}
