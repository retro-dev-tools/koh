namespace Koh.Emit;

/// <summary>
/// Constants for the RGBDS RGB9 object file format (revision 13).
/// All multi-byte integers are little-endian.
/// </summary>
internal static class RgbdsObjectFormat
{
    public static ReadOnlySpan<byte> Magic => "RGB9"u8;
    public const int Revision = 13;

    // Symbol types
    public const byte SymLocal = 0;
    public const byte SymImport = 1;
    public const byte SymExport = 2;

    // Section types
    public const byte SectWram0 = 0;
    public const byte SectVram = 1;
    public const byte SectRomx = 2;
    public const byte SectRom0 = 3;
    public const byte SectHram = 4;
    public const byte SectWramx = 5;
    public const byte SectSram = 6;
    public const byte SectOam = 7;

    // File stack node types
    public const byte NodeRept = 0;
    public const byte NodeFile = 1;
    public const byte NodeMacro = 2;

    // Patch types
    public const byte PatchByte = 0;
    public const byte PatchLe16 = 1;
    public const byte PatchLe32 = 2;
    public const byte PatchJr = 3;

    // RPN opcodes — arithmetic
    public const byte RpnAdd = 0x00;
    public const byte RpnSub = 0x01;
    public const byte RpnMul = 0x02;
    public const byte RpnDiv = 0x03;
    public const byte RpnMod = 0x04;
    public const byte RpnNeg = 0x05;
    public const byte RpnExp = 0x06;

    // RPN opcodes — bitwise
    public const byte RpnOr = 0x10;
    public const byte RpnAnd = 0x11;
    public const byte RpnXor = 0x12;
    public const byte RpnNot = 0x13;

    // RPN opcodes — logical
    public const byte RpnLogAnd = 0x21;
    public const byte RpnLogOr = 0x22;
    public const byte RpnLogNot = 0x23;

    // RPN opcodes — comparison
    public const byte RpnEq = 0x30;
    public const byte RpnNe = 0x31;
    public const byte RpnGt = 0x32;
    public const byte RpnLt = 0x33;
    public const byte RpnGe = 0x34;
    public const byte RpnLe = 0x35;

    // RPN opcodes — shift
    public const byte RpnShl = 0x40;
    public const byte RpnShr = 0x41;
    public const byte RpnUshr = 0x42;

    // RPN opcodes — section/bank functions
    public const byte RpnBankSym = 0x50;
    public const byte RpnBankSect = 0x51;
    public const byte RpnBankSelf = 0x52;
    public const byte RpnSizeofSect = 0x53;
    public const byte RpnStartofSect = 0x54;
    public const byte RpnSizeofSectType = 0x55;
    public const byte RpnStartofSectType = 0x56;

    // RPN opcodes — validation checks
    public const byte RpnHramCheck = 0x60;
    public const byte RpnRstCheck = 0x61;
    public const byte RpnBitIndex = 0x62;

    // RPN opcodes — byte functions
    public const byte RpnHigh = 0x70;
    public const byte RpnLow = 0x71;
    public const byte RpnBitWidth = 0x72;
    public const byte RpnTzCount = 0x73;

    // RPN opcodes — operands
    public const byte RpnLiteral = 0x80;
    public const byte RpnSymbol = 0x81;
}
