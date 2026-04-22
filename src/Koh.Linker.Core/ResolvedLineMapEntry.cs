namespace Koh.Linker.Core;

/// <summary>
/// A run of bytes, after linking, that all came from the same source
/// line. Section-relative offsets from <see cref="Koh.Core.Binding.LineMapEntry"/>
/// have been translated into the windowed GB address + bank pair that
/// matches how symbols are emitted into the .kdbg: ROMX addresses are
/// 0x4000–0x7FFF (not flat ROM offsets), ROM0 addresses are 0x0000–0x3FFF,
/// all with <see cref="Bank"/> 0 except ROMX.
///
/// DebugInfoPopulator turns each of these into a .kdbg address-map
/// entry so the debugger can answer "given source file+line, what
/// addresses do I set hardware breakpoints at?".
/// </summary>
public sealed record ResolvedLineMapEntry(
    byte Bank,
    ushort Address,
    int ByteCount,
    string File,
    uint Line);
