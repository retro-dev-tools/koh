namespace Koh.Emit;

/// <summary>
/// Constants for the Koh Object (.kobj) binary format.
/// All multi-byte integers are little-endian (BinaryWriter/BinaryReader guarantee this
/// regardless of host architecture).
/// </summary>
internal static class KobjFormat
{
    public static ReadOnlySpan<byte> Magic => "KOH\0"u8;

    /// <summary>
    /// Bumped to 2 when per-section line maps landed. v1 files have no
    /// line-map payload after each section's patch list; v2 writes one
    /// (possibly empty). Readers accept both.
    /// Bumped to 3 when PatchEntry.SymbolName was added. v2 patches have no
    /// SymbolName field; v3 appends a length-prefixed UTF-8 string after
    /// DiagnosticSpan (empty string = null). Readers accept v1/v2/v3.
    /// Bumped to 4 when PatchEntry.SymbolOffset was added. v3 patches don't
    /// carry an explicit symbol offset; v4 appends a 32-bit signed int after
    /// SymbolName. Readers accept v1..v4 (older versions default offset to 0).
    /// </summary>
    public const byte Version = 4;
    public const byte MinReadableVersion = 1;

    // Top-level block tags
    public const byte TagSections = 0x01;
    public const byte TagSymbols = 0x02;

    // 0x03 is intentionally unassigned — patches are serialized inline per-section, not as a top-level block
    public const byte TagEnd = 0xFF;
}
