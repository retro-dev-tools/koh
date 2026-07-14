using System.Linq;
using Koh.Compiler.Frontends.CSharp;
using Koh.Compiler.Ir;
using Mono.Cecil;

namespace Koh.Compiler.Frontends.Cil;

/// <summary>
/// Generic-method monomorphization from IL (see
/// <c>docs/superpowers/specs/2026-07-14-cil-frontend-design.md</c>'s "Generic instantiation from IL"
/// hard part): Cecil exposes a generic call site's concrete type arguments directly as a
/// <see cref="GenericInstanceMethod"/> operand — no syntax rewriting is possible the way
/// <c>CSharpFrontend.Generics.cs</c>'s <c>TypeParamRewriter</c> substitutes a <c>TypeSyntax</c> tree, so
/// this frontend instead threads a <c>GenericParameter</c>-&gt;concrete-<c>TypeReference</c> map
/// (<see cref="CilGenericSubst.Substitute"/>) through every type resolution site inside a generic
/// template's own body lowering. Scope: static generic METHODS only (no generic classes — matching
/// <c>CSharpFrontend</c>, which has none either) with scalar/pointer parameters and return (a struct is
/// a diagnostic — see <c>CilLoweringContext.BuildGenericSignature</c>).
/// </summary>
internal static class CilGenericSubst
{
    /// <summary>Substitute every occurrence of a METHOD type parameter (<c>!!0</c>, <c>!!1</c>, ...) in
    /// <paramref name="tr"/> for the corresponding concrete type in <paramref name="methodArgs"/>,
    /// recursing through pointer/byref/array/nested-generic-instance shapes exactly the way Cecil itself
    /// never does automatically (there is no built-in "instantiate this open type" helper on
    /// <see cref="TypeReference"/>). <paramref name="methodArgs"/> null means "not inside a generic
    /// instantiation at all" (the ordinary, non-generic lowering path) — a no-op. A TYPE (not method)
    /// generic parameter reaching here is out of scope (no generic classes) and reported as a
    /// diagnostic, same as any other unsupported construct in this frontend.</summary>
    public static TypeReference Substitute(
        TypeReference tr,
        IReadOnlyList<TypeReference>? methodArgs
    )
    {
        if (methodArgs is null || !tr.ContainsGenericParameter)
            return tr;
        switch (tr)
        {
            case GenericParameter { Type: GenericParameterType.Method } gp:
                if (gp.Position < 0 || gp.Position >= methodArgs.Count)
                    throw new CilNotSupportedException(
                        $"generic method type parameter '{gp.Name}' (position {gp.Position}) has no "
                            + "matching type argument."
                    );
                return methodArgs[gp.Position];
            case GenericParameter gp:
                throw new CilNotSupportedException(
                    $"'{gp.FullName}' is a generic TYPE parameter; the CIL frontend monomorphizes "
                        + "generic methods only (no generic classes)."
                );
            case PointerType pt:
                return new PointerType(Substitute(pt.ElementType, methodArgs));
            case ByReferenceType bt:
                return new ByReferenceType(Substitute(bt.ElementType, methodArgs));
            case ArrayType at:
                return new ArrayType(Substitute(at.ElementType, methodArgs), at.Rank);
            case GenericInstanceType git:
            {
                var substituted = new GenericInstanceType(git.ElementType);
                foreach (var a in git.GenericArguments)
                    substituted.GenericArguments.Add(Substitute(a, methodArgs));
                return substituted;
            }
            default:
                return tr;
        }
    }

    /// <summary>The mangled suffix for a concrete type-argument list — the CIL frontend's own analog of
    /// <c>CSharpFrontend.Generics.cs</c>'s <c>MangleSuffix</c>. Reuses the SAME encoder
    /// (<see cref="CSharpFrontend.EncodeTypeArg"/>, internal, same-assembly) rather than a parallel
    /// implementation, so a maintenance change to the escaping scheme automatically keeps both frontends
    /// in lockstep — only <see cref="SourceLikeName"/> (this file's own inverse of
    /// <see cref="CilTypeMapper.Map"/>'s keyword switch) differs, because the input here is a CLR
    /// <see cref="TypeReference"/> rather than a C# <c>TypeSyntax</c>'s own source spelling.</summary>
    public static string MangledSuffix(IReadOnlyList<TypeReference> concreteArgs)
    {
        var sb = new System.Text.StringBuilder("__g").Append(concreteArgs.Count);
        foreach (var t in concreteArgs)
        {
            var enc = CSharpFrontend.EncodeTypeArg(SourceLikeName(t));
            sb.Append('_').Append(enc.Length).Append('_').Append(enc);
        }
        return sb.ToString();
    }

    /// <summary>A C#-source-like spelling of a concrete CLR type reference (<c>System.Byte</c> -&gt;
    /// <c>"byte"</c>, ...) — the inverse of <see cref="CilTypeMapper.Map"/>'s keyword mapping, so a
    /// generic instantiation mangles to the same suffix <c>CSharpFrontend</c> would compute from the
    /// equivalent source-level <c>TypeSyntax.ToString()</c>. Covers the primitive keywords plus
    /// pointers/arrays/user types by simple name — the shapes this frontend's generic fixtures actually
    /// use. An exotic spelling this can't reproduce only affects cross-frontend name comparability
    /// (the A/B oracle), never correctness — each frontend still mangles injectively within itself.</summary>
    internal static string SourceLikeName(TypeReference tr) =>
        tr switch
        {
            PointerType pt => SourceLikeName(pt.ElementType) + "*",
            ArrayType at => SourceLikeName(at.ElementType) + "[]",
            _ => tr.MetadataType switch
            {
                MetadataType.Boolean => "bool",
                MetadataType.Char => "char",
                MetadataType.SByte => "sbyte",
                MetadataType.Byte => "byte",
                MetadataType.Int16 => "short",
                MetadataType.UInt16 => "ushort",
                MetadataType.Int32 => "int",
                MetadataType.UInt32 => "uint",
                MetadataType.Int64 => "long",
                MetadataType.UInt64 => "ulong",
                MetadataType.Single => "float",
                MetadataType.Double => "double",
                MetadataType.Void => "void",
                _ => tr.Name,
            },
        };
}

internal sealed partial class CilMethodLowerer
{
    /// <summary>This instance's own generic-instantiation map — non-null exactly when this
    /// <see cref="CilMethodLowerer"/> is lowering a monomorphized specialization's body (constructed by
    /// <see cref="CilLoweringContext.EnsureGenericInstance"/>), null for the ordinary, non-generic
    /// path. Every TypeReference this class reads directly off Cecil metadata for a local/param that
    /// could still name the template's own type parameter must be run through <see cref="Subst"/>
    /// before use — see this file's class remarks for the sites that matter to the current fixture set
    /// (signature, locals, nested generic calls); anything else containing an unsubstituted parameter
    /// fails loudly at <see cref="CilTypeMapper.Map"/> rather than silently mistyping (see that
    /// method's own guard).</summary>
    private readonly IReadOnlyList<TypeReference>? _genericArgs;

    private TypeReference Subst(TypeReference tr) => CilGenericSubst.Substitute(tr, _genericArgs);

    /// <summary>A <c>call</c> whose operand is a <see cref="GenericInstanceMethod"/> — i.e. a call to a
    /// generic method instantiated at concrete type arguments. Resolves (or lowers, the first time) the
    /// specialization via <see cref="CilLoweringContext.EnsureGenericInstance"/> and calls it directly,
    /// exactly like an ordinary static call once the specialization exists.
    ///
    /// Transitivity: <paramref name="gim"/>'s own <see cref="GenericInstanceMethod.GenericArguments"/>
    /// may themselves still name THIS method's own type parameter (a nested generic call inside a
    /// generic template's shared IL body, e.g. <c>Wrap&lt;T&gt;</c> calling <c>Identity&lt;T&gt;</c> —
    /// the callee's type argument is literally <c>!!0</c> in Wrap's own IL, not yet concrete) — running
    /// each one through this instance's own <see cref="Subst"/> resolves it to the CURRENT
    /// instantiation's concrete type before computing the nested call's suffix/routing, which is exactly
    /// how transitive instantiation falls out of the on-demand model without a separate work-list (unlike
    /// <c>CSharpFrontend.Generics.cs</c>'s explicit BFS queue): each concrete call site recursively
    /// triggers the next one's own <see cref="CilLoweringContext.EnsureGenericInstance"/> call.</summary>
    private void LowerGenericCall(GenericInstanceMethod gim, List<IrValue> stack)
    {
        var template =
            gim.ElementMethod.Resolve()
            ?? throw new CilNotSupportedException(
                $"cannot resolve generic call to '{gim.FullName}'."
            );

        var concreteArgs = gim.GenericArguments.Select(Subst).ToList();
        var suffix = CilGenericSubst.MangledSuffix(concreteArgs);
        var fn = _ctx.EnsureGenericInstance(template, concreteArgs, suffix);

        var argCount = gim.Parameters.Count;
        var args = new IrValue[argCount];
        for (var i = argCount - 1; i >= 0; i--)
            args[i] = Pop(stack);

        if (fn is null)
            throw new CilNotSupportedException(
                $"call to unsupported generic instantiation '{gim.FullName}'."
            );

        for (var i = 0; i < args.Length; i++)
            args[i] = CoerceStore(args[i], fn.Parameters[i].Type);
        var call = _b.Call(fn, args);
        if (fn.ReturnType.Kind != IrTypeKind.Void)
        {
            var (_, signed) = CilTypeMapper.Map(
                CilGenericSubst.Substitute(template.ReturnType, concreteArgs)
            );
            stack.Add(WidenToStack(call, signed));
        }
    }
}
