using Koh.Compiler.Ir;
using Mono.Cecil;

namespace Koh.Compiler.Frontends.Cil;

/// <summary>Shared struct/enum classification helpers used by <see cref="CilTypeMapper"/>,
/// <see cref="CilClassLayout"/>, and <c>CilMethodLowerer.Structs.cs</c> — every call site that must
/// branch on "is this a user value-type struct" (locals, params, fields, array elements, ldobj/stobj/
/// initobj/cpobj operands) BEFORE handing the type to <see cref="CilTypeMapper.Map"/>, which never
/// itself constructs a struct-shaped <see cref="IrType"/> (see its remarks).</summary>
internal static class CilStructSupport
{
    /// <summary>The user-declared value type <paramref name="typeReference"/> resolves to, if it is a
    /// struct (a value type that is neither a primitive nor an enum) this frontend lays out as a byte
    /// buffer — null for anything else (primitive, enum, reference type, pointer, byref).</summary>
    public static TypeDefinition? ResolveStruct(TypeReference typeReference)
    {
        if (typeReference is PointerType or ByReferenceType || !typeReference.IsValueType)
            return null;
        if (typeReference.MetadataType != MetadataType.ValueType)
            return null; // a primitive (Byte, Int32, ...) — Map handles it directly
        // System.Int128/UInt128 are ValueType-shaped in CIL (ECMA-335 has no primitive element type
        // for a 128-bit integer — confirmed against a real Cecil read) but are, for this frontend's
        // purposes, ordinary 128-bit scalars (see CilTypeMapper.Map's matching remarks), not a
        // user-declared struct laid out as a byte buffer. Must be excluded here — the ONE gate every
        // struct-aware call site checks before Map — or an Int128/UInt128 local/field/param would be
        // misrouted through the byte-buffer struct path instead of CilTypeMapper.Map's scalar mapping.
        if (typeReference.Namespace == "System" && typeReference.Name is "Int128" or "UInt128")
            return null;
        return CilModuleLowerer.ResolveSafe(typeReference) is { IsEnum: false } def ? def : null;
    }

    /// <summary>An enum's storage field (ECMA-335 II.14.3: exactly one non-static instance field,
    /// conventionally named <c>value__</c>, but matched structurally here rather than by name).</summary>
    public static TypeReference EnumUnderlyingType(TypeDefinition enumDef) =>
        enumDef.Fields.First(f => !f.IsStatic).FieldType;
}

/// <summary>
/// Byte layout of a reference type's or value type's instance fields — the CIL-frontend analogue of
/// <c>Koh.Compiler.Frontends.CSharp.CsStruct</c>/<c>CsClass</c>'s <c>Layout</c>. Built lazily and
/// cached per <see cref="TypeDefinition"/> (see <c>CilLoweringContext.GetLayout</c>): a class/display-
/// class/delegate-capture object is a heap-allocated blob of these bytes, addressed the same way
/// <c>MethodLowerer.LowerNew</c> addresses a C#-frontend class instance; a struct is the same shape of
/// byte buffer, just stack/local/field-allocated instead of heap-allocated (see
/// <c>CilMethodLowerer.Structs.cs</c>). Fields pack byte-addressable (SM83 has no alignment
/// requirement) but each scalar field still aligns to its own size — matching
/// <c>CSharpFrontend.Declarations.LayoutFields</c> — so a field's offset is always an exact multiple
/// of its own <see cref="IrType.SizeInBytes"/>, which is what lets a field access lower to a single
/// element-scaled <c>gep</c> (offset / size = element index) instead of separate byte arithmetic. A
/// nested struct-typed field packs byte-aligned instead (same as <c>CSharpFrontend.CollectStructs</c>'s
/// <c>Layout</c>) since its own size is not itself a "natural" power-of-two scalar width.
/// </summary>
internal sealed class CilClassLayout
{
    /// <summary><paramref name="Nested"/> is non-null exactly when this field is itself a struct — its
    /// own <see cref="Type"/>/<see cref="Signed"/> are meaningless in that case (a struct field is
    /// never loaded/stored as a scalar; see <c>CilMethodLowerer.Structs.cs</c>'s field-access remarks).</summary>
    public readonly record struct FieldInfo(
        int Offset,
        IrType Type,
        bool Signed,
        CilClassLayout? Nested = null
    );

    public IReadOnlyDictionary<FieldDefinition, FieldInfo> Fields { get; }
    public int Size { get; }

    private CilClassLayout(Dictionary<FieldDefinition, FieldInfo> fields, int size)
    {
        Fields = fields;
        Size = size;
    }

    /// <summary>Lay out every non-static field of <paramref name="type"/>, in declaration order.
    /// Throws <see cref="CilNotSupportedException"/> (via <see cref="CilTypeMapper.Map"/>) if a
    /// field's type is out of the supported subset — reported by the caller as a diagnostic, same as
    /// every other unsupported-construct path in this frontend. <paramref name="resolveNested"/> lays
    /// out a struct-typed field's own type (always <c>CilLoweringContext.GetLayout</c>, so nested
    /// layouts share the same cache — a struct cannot contain itself, so no cycle guard is needed, the
    /// same non-issue as <c>CSharpFrontend.CollectStructs</c>).
    ///
    /// INHERITANCE is prefix layout: a derived class's own fields start at
    /// <paramref name="baseLayout"/>.<see cref="Size"/>, so every base-declared field keeps the
    /// offset the BASE's own layout gave it and a base-typed pointer to a derived instance reads
    /// base fields correctly. (Before the ideal-game-API program's E2 enabler, this walked only
    /// <c>type.Fields</c> from offset 0 — a derived class's fields silently OVERLAPPED its base's.)
    /// <paramref name="reserveTagByte"/> additionally reserves offset 0 of a tagged dispatch
    /// hierarchy's ROOT for the runtime type tag (<see cref="CilVirtualDispatch"/>); derived
    /// layouts inherit the reservation through the base prefix.</summary>
    public static CilClassLayout Compute(
        TypeDefinition type,
        Func<TypeDefinition, CilClassLayout> resolveNested,
        CilClassLayout? baseLayout = null,
        bool reserveTagByte = false
    )
    {
        var fields = new Dictionary<FieldDefinition, FieldInfo>();
        int offset = baseLayout is not null ? baseLayout.Size : (reserveTagByte ? 1 : 0);
        int maxAlign = 1;
        foreach (var field in type.Fields)
        {
            if (field.IsStatic)
                continue;
            if (CilStructSupport.ResolveStruct(field.FieldType) is { } nestedDef)
            {
                var nested = resolveNested(nestedDef);
                var nsize = Math.Max(nested.Size, 1);
                // Nested aggregates pack byte-aligned (matches CSharpFrontend.CollectStructs).
                fields[field] = new FieldInfo(offset, IrType.I8, false, nested);
                offset += nsize;
                continue;
            }
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
