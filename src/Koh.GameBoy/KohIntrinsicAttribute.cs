namespace Koh.GameBoy;

/// <summary>
/// Marks a <c>Hardware</c>/<c>Gb</c> member as a compiler intrinsic: the property/method body is the
/// desktop implementation, and the attribute is the ROM implementation. The Koh CIL frontend matches
/// this attribute by simple type name (it never references <c>Koh.GameBoy</c>), so it stays a plain,
/// dependency-free marker.
/// </summary>
/// <param name="kind">
/// <c>"register"</c> (a memory-mapped I/O byte, <paramref name="address"/> is its fixed address),
/// <c>"region"</c> (a memory region base pointer, <paramref name="address"/> is its base address), or
/// a control-flow intrinsic with no address: <c>"ei"</c>, <c>"di"</c>, <c>"halt"</c>, <c>"nop"</c>, or
/// <c>"stop"</c>.
/// </param>
/// <param name="address">The fixed address for <c>"register"</c>/<c>"region"</c> kinds; <c>-1</c> for
/// an address-less control intrinsic.</param>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Method, AllowMultiple = false)]
public sealed class KohIntrinsicAttribute(string kind, int address = -1) : Attribute
{
    /// <summary>The intrinsic kind: <c>"register"</c>, <c>"region"</c>, <c>"ei"</c>, <c>"di"</c>,
    /// <c>"halt"</c>, <c>"nop"</c>, or <c>"stop"</c>.</summary>
    public string Kind { get; } = kind;

    /// <summary>The fixed address for <c>"register"</c>/<c>"region"</c> kinds; <c>-1</c> otherwise.</summary>
    public int Address { get; } = address;
}
