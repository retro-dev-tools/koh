namespace Koh.GameBoy;

/// <summary>
/// ROM runtime contract. Desktop execution uses the ordinary method body.
/// </summary>
/// <param name="kind">
/// register, region, ei, di, halt, nop, stop, alloc, heapreset, oamdma, memfill.
/// </param>
/// <param name="address">Fixed register/region address; otherwise -1.</param>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Method, AllowMultiple = false)]
public sealed class KohIntrinsicAttribute(string kind, int address = -1) : Attribute
{
    /// <summary>Contract identifier.</summary>
    public string Kind { get; } = kind;

    /// <summary>The fixed address for <c>"register"</c>/<c>"region"</c> kinds; <c>-1</c> otherwise.</summary>
    public int Address { get; } = address;
}
