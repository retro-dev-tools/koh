using Koh.Compiler.Targets;

namespace Koh.Compiler.Ir;

/// <summary>Category of an <see cref="IrType"/>.</summary>
public enum IrTypeKind
{
    Void,
    Int,
    Pointer,
    Struct,
    Array,
}

/// <summary>
/// A target-independent IR type. Integer signedness is a property of operations, not of
/// the type (LLVM-style), so an <see cref="IrTypeKind.Int"/> carries only a bit width.
/// </summary>
public sealed class IrType
{
    public IrTypeKind Kind { get; }

    /// <summary>Bit width for <see cref="IrTypeKind.Int"/> (8, 16, 32, 64).</summary>
    public int Bits { get; }

    /// <summary>Address space for <see cref="IrTypeKind.Pointer"/>.</summary>
    public AddressSpace AddressSpace { get; }

    /// <summary>Pointee for <see cref="IrTypeKind.Pointer"/>, element type for <see cref="IrTypeKind.Array"/>.</summary>
    public IrType? Element { get; }

    /// <summary>Element count for <see cref="IrTypeKind.Array"/>.</summary>
    public int ArrayLength { get; }

    private IrType(IrTypeKind kind, int bits, AddressSpace addressSpace, IrType? element, int arrayLength)
    {
        Kind = kind;
        Bits = bits;
        AddressSpace = addressSpace;
        Element = element;
        ArrayLength = arrayLength;
    }

    public static readonly IrType Void = new(IrTypeKind.Void, 0, AddressSpace.Default, null, 0);

    public static readonly IrType I8 = new(IrTypeKind.Int, 8, AddressSpace.Default, null, 0);
    public static readonly IrType I16 = new(IrTypeKind.Int, 16, AddressSpace.Default, null, 0);
    public static readonly IrType I32 = new(IrTypeKind.Int, 32, AddressSpace.Default, null, 0);
    public static readonly IrType I64 = new(IrTypeKind.Int, 64, AddressSpace.Default, null, 0);

    public static IrType Int(int bits) => bits switch
    {
        8 => I8,
        16 => I16,
        32 => I32,
        64 => I64,
        _ => new IrType(IrTypeKind.Int, bits, AddressSpace.Default, null, 0),
    };

    public static IrType Pointer(IrType pointee, AddressSpace space = AddressSpace.Default) =>
        new(IrTypeKind.Pointer, 0, space, pointee, 0);

    public static IrType Array(IrType element, int length) =>
        new(IrTypeKind.Array, 0, AddressSpace.Default, element, length);

    public override string ToString() => Kind switch
    {
        IrTypeKind.Void => "void",
        IrTypeKind.Int => $"i{Bits}",
        IrTypeKind.Pointer => AddressSpace == AddressSpace.Default
            ? $"{Element}*"
            : $"{Element} addrspace({AddressSpace.ToString().ToLowerInvariant()})*",
        IrTypeKind.Array => $"[{ArrayLength} x {Element}]",
        IrTypeKind.Struct => "struct",
        _ => "?",
    };
}
