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
/// The loop bodies are deliberately minimal and shaped as a single-block stride-1 pointer walk (increment
/// form, not indexed) — the shape a parallel loop-optimizer package targets for faster codegen; this
/// package only supplies a correct baseline via the normal compiled pipeline, not the fast path.
/// </summary>
internal static class MemRuntime
{
    public const string Source =
        @"
// ---- Koh Mem.Copy / Mem.Fill runtime --------------------------------------

static void __mem_copy(byte* destination, byte* source, ushort count) {
    while (count != 0) {
        *destination = *source;
        destination++;
        source++;
        count--;
    }
}

static void __mem_fill(byte* destination, byte value, ushort count) {
    while (count != 0) {
        *destination = value;
        destination++;
        count--;
    }
}
";
}
