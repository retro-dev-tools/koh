namespace Koh.Linker.Core;

public static class KdbgFormat
{
    public const uint Magic = 0x4742444B;   // "KDBG" little-endian
    public const ushort Version1 = 1;

    public const ushort FlagExpansionPresent = 1 << 0;
    public const ushort FlagScopeTablePresent = 1 << 1;
    public const ushort FlagPathsAbsolute = 1 << 2;

    public const int HeaderSize = 32;

    public const uint NoExpansion = 0xFFFFFFFF;
}
