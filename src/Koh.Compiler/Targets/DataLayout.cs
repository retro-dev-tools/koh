namespace Koh.Compiler.Targets;

/// <summary>
/// Per-target description of how IR types are laid out in memory. The IR itself is
/// target-independent (it uses <c>i8/i16/i32</c> freely); the DataLayout plus each
/// backend's legalization stage decide what a given width costs on a given machine.
/// </summary>
/// <param name="PointerBits">Native pointer width. SM83 = 16; ARM7/MIPS = 32.</param>
/// <param name="LittleEndian">True for SM83, ARM (LE), and MIPS R3000 (LE on PSX).</param>
/// <param name="NativeIntBits">
/// Widths the target's ALU handles without expansion. Anything wider is legalized
/// into multi-part sequences by the backend. SM83 = { 8 }; 32-bit targets = { 8, 16, 32 }.
/// </param>
public sealed record DataLayout(
    int PointerBits,
    bool LittleEndian,
    IReadOnlyList<int> NativeIntBits
)
{
    /// <summary>SM83: 16-bit pointers, little-endian, 8-bit-native ALU.</summary>
    public static DataLayout Sm83 { get; } =
        new(PointerBits: 16, LittleEndian: true, NativeIntBits: [8]);

    /// <summary>Bytes required to store a pointer on this target.</summary>
    public int PointerSize => (PointerBits + 7) / 8;

    /// <summary>Whether an integer of the given bit width is handled without legalization.</summary>
    public bool IsNativeInt(int bits) => NativeIntBits.Contains(bits);
}
