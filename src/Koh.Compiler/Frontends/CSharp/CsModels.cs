using Koh.Compiler.Ir;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koh.Compiler.Frontends.CSharp;

/// <summary>An enum: its underlying Koh C# type and member values.</summary>
internal sealed record CsEnum(CsType Underlying, IReadOnlyDictionary<string, long> Members);

/// <summary>A value-type struct: scalar fields with byte offsets, and its total size.</summary>
internal sealed record CsStruct(IReadOnlyList<CsField> Fields, int Size);

/// <summary>One struct field: name, type, and byte offset within the struct. A field whose type is
/// itself a struct carries its layout in <paramref name="Struct"/> (its <see cref="Type"/> is unused).</summary>
internal sealed record CsField(string Name, CsType Type, int Offset, CsStruct? Struct = null);

/// <summary>A resolved method: its IR function plus Koh C# signature types (for signedness/coercion).
/// An instance method has a non-null <paramref name="ThisClass"/> and an implicit first parameter
/// (<c>this</c>, a pointer to the instance); its user parameters follow. <paramref name="ParamClasses"/>
/// carries, per parameter, the class it is an instance of (null for non-class parameters) so the body
/// can resolve field/method access on a class-typed argument.</summary>
internal sealed record CsMethod(
    IrFunction Fn,
    CsType? Return,
    IReadOnlyList<CsType> Params,
    IReadOnlyList<bool> RefParams,
    IReadOnlyList<CsStruct?> ParamStructs,
    CsClass? ThisClass = null,
    IReadOnlyList<CsClass?>? ParamClasses = null,
    // The enclosing top-level `static class` for a program-scope static method (so its body can resolve
    // sibling members by simple name); null for legacy top-level functions and instance methods.
    string? DeclaringClass = null
);

/// <summary>A reference type (heap-allocated, `new`): its field layout (like a struct) and the
/// instance methods declared on it. An instance reference is a pointer to <see cref="Layout"/> bytes.</summary>
internal sealed record CsClass(
    string Name,
    CsStruct Layout,
    IReadOnlyDictionary<string, MethodDeclarationSyntax> Methods
);
