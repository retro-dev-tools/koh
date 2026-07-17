namespace Koh.GameBoy;

/// <summary>
/// Marks a <c>static</c> field as needing its storage aligned to a power-of-two byte boundary — e.g. a
/// page-aligned (0xXX00) OAM DMA source buffer, or a 16-byte-aligned HDMA source. The Koh CIL frontend
/// matches this attribute by simple type name (it never references <c>Koh.GameBoy</c>), so it stays a
/// plain, dependency-free marker; the SM83 backend's static-WRAM allocator rounds the field's assigned
/// address up to the next multiple of <see cref="Alignment"/> before placing it.
///
/// Desktop reference build: a no-op marker only — a managed field/array has no Game Boy address to
/// align, and the desktop host never DMAs from it directly (see <c>Gb.DmaOam</c>'s own WRAM-array
/// source, which needs no alignment on the managed side).
/// </summary>
/// <param name="alignment">The required alignment in bytes; must be a power of two (e.g. 16, 256).</param>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public sealed class KohAlignedAttribute(int alignment) : Attribute
{
    public int Alignment { get; } = alignment;
}
