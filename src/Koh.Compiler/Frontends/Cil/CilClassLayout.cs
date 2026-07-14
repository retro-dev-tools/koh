using Koh.Compiler.Ir;
using Mono.Cecil;

namespace Koh.Compiler.Frontends.Cil;

/// <summary>
/// Byte layout of a reference type's instance fields — the CIL-frontend analogue of
/// <c>Koh.Compiler.Frontends.CSharp.CsStruct</c>/<c>CsClass</c>'s <c>Layout</c>. Built lazily and
/// cached per <see cref="TypeDefinition"/> (see <c>CilMethodLowerer.GetLayout</c>): a class/display-
/// class/delegate-capture object is a heap-allocated blob of these bytes, addressed the same way
/// <c>MethodLowerer.LowerNew</c> addresses a C#-frontend class instance. Fields pack byte-addressable
/// (SM83 has no alignment requirement) but each scalar field still aligns to its own size — matching
/// <c>CSharpFrontend.Declarations.LayoutFields</c> — so a field's offset is always an exact multiple
/// of its own <see cref="IrType.SizeInBytes"/>, which is what lets a field access lower to a single
/// element-scaled <c>gep</c> (offset / size = element index) instead of separate byte arithmetic.
/// </summary>
internal sealed class CilClassLayout
{
    public readonly record struct FieldInfo(int Offset, IrType Type, bool Signed);

    public IReadOnlyDictionary<FieldDefinition, FieldInfo> Fields { get; }
    public int Size { get; }

    private CilClassLayout(Dictionary<FieldDefinition, FieldInfo> fields, int size)
    {
        Fields = fields;
        Size = size;
    }

    /// <summary>Lay out every non-static field of <paramref name="type"/>, in declaration order.
    /// Throws <see cref="CilNotSupportedException"/> (via <see cref="CilTypeMapper.Map"/>) if a
    /// field's type is out of the phase-1+delegates subset — reported by the caller as a diagnostic,
    /// same as every other unsupported-construct path in this frontend.</summary>
    public static CilClassLayout Compute(TypeDefinition type)
    {
        var fields = new Dictionary<FieldDefinition, FieldInfo>();
        int offset = 0,
            maxAlign = 1;
        foreach (var field in type.Fields)
        {
            if (field.IsStatic)
                continue;
            var (irType, signed) = CilTypeMapper.Map(field.FieldType);
            var size = Math.Max(irType.SizeInBytes, 1);
            offset = RoundUp(offset, size);
            fields[field] = new FieldInfo(offset, irType, signed);
            offset += size;
            maxAlign = Math.Max(maxAlign, size);
        }
        return new CilClassLayout(fields, RoundUp(offset, maxAlign));
    }

    private static int RoundUp(int value, int align) =>
        align <= 1 ? value : (value + align - 1) / align * align;
}
