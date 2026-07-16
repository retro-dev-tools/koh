using Koh.Compiler.Ir;
using Koh.Compiler.Targets;
using Koh.Core.Diagnostics;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Koh.Compiler.Frontends.Cil;

/// <summary>
/// Raised internally by <see cref="CilTypeMapper"/>/<see cref="CilMethodLowerer"/> for a construct
/// phase 1 doesn't cover (an unsupported CIL opcode, type, or call shape). Always caught within this
/// namespace and converted to a <see cref="Diagnostic"/> — mirrors
/// <c>Koh.Compiler.Frontends.CSharp.CSharpNotSupportedException</c>'s per-method containment, so one
/// bad method reports a diagnostic and is skipped rather than sinking the whole module.
/// </summary>
internal sealed class CilNotSupportedException(string message) : Exception(message);

/// <summary>Maps a Cecil <see cref="TypeReference"/> to a Koh IR type plus its signedness (signedness
/// is not a property of <see cref="IrType"/> itself — see <c>IrType</c>'s remarks — but the CIL frontend
/// needs it once, at the point a narrow local/argument/return is widened onto the simulated evaluation
/// stack, or narrowed back off it).</summary>
internal static class CilTypeMapper
{
    public static (IrType Type, bool Signed) Map(TypeReference typeReference)
    {
        // An unsubstituted generic parameter (a bare '!!0'/'!0', or any pointer/array/byref/generic-
        // instance built from one) reaching here means some caller lowering a generic method's
        // specialized body forgot to run it through CilGenericSubst.Substitute first — see
        // CilMethodLowerer.Generics.cs's remarks. Without this guard the reference-type shortcut just
        // below would silently treat an open 'T' as "pointer to byte" (a GenericParameter's own
        // IsValueType defaults to false), a wrong-type miscompile rather than a loud failure — so this
        // is checked FIRST and unconditionally, not folded into the switch below.
        if (typeReference.ContainsGenericParameter)
            throw new CilNotSupportedException(
                $"'{typeReference.FullName}' still names an open generic parameter during lowering "
                    + "(a missing type-argument substitution site — the CIL frontend monomorphizes "
                    + "generic methods on demand; see CilMethodLowerer.Generics.cs)."
            );

        if (typeReference is PointerType pointerType)
        {
            var (element, _) = Map(pointerType.ElementType);
            return (IrType.Pointer(element), false);
        }

        // A byref (ref/out/in parameter, or a managed pointer produced by ldloca/ldarga/ldflda) is,
        // for lowering purposes, just an address — the CIL frontend never distinguishes a managed
        // pointer from an unmanaged one (see docs/superpowers/specs/2026-07-14-cil-frontend-design.md's
        // struct/pointer task): every ref/out/in access in Roslyn-emitted IL is already explicit
        // (ldarg then ldind/stind), so no extra deref bookkeeping is needed here — see
        // CilMethodLowerer.Structs.cs's remarks on ref/out/in parameters.
        if (typeReference is ByReferenceType byRefType)
        {
            var (element, _) = Map(byRefType.ElementType);
            return (IrType.Pointer(element), false);
        }

        // A reference type (class, delegate, interface) — every instance is heap-allocated and
        // referred to by a raw byte pointer, exactly like CSharpFrontend's class instances (see
        // CSharpFrontend.HeapPointerName / MethodLowerer.LowerNew). `System.Void`'s own IsValueType
        // is not reliable across Cecil readers, so it is excluded explicitly and handled by the
        // switch below rather than by this shortcut.
        if (typeReference.MetadataType != MetadataType.Void && !typeReference.IsValueType)
            return (IrType.Pointer(IrType.I8), false);

        // System.ReadOnlySpan<T>/System.Span<T> — a 'ref struct' whose own internal layout (a managed
        // pointer field this frontend cannot generically lay out; see CilClassLayout's remarks on why
        // a struct's fields must each have a scalar IrType) is never itself modeled. Standard C#'s
        // replacement for Koh's old "string literal as byte[] initializer" idiom (that idiom was
        // Koh-legal-but-C#-illegal, so it cannot exist in a compiled assembly at all — see
        // CilMethodLowerer's TryLowerSpanCall/CilMethodLowerer.Statics.cs's array-literal remarks) is
        // `"..."u8`, which Roslyn compiles to exactly a ReadOnlySpan<byte> over RVA data. Represented,
        // like every array in this frontend (CilMethodLowerer.Arrays.cs), as a raw pointer to its
        // element type — the length is call-site provenance tracked separately (see
        // CilMethodLowerer.Delegates.cs's TryLowerSpanCall), not part of the type itself. Checked here,
        // ahead of the struct-routing MetadataType.ValueType branches below, because a generic instance
        // type's own MetadataType is GenericInstance, not ValueType (confirmed against a real Cecil
        // read of "ReadOnlySpan<byte> x = ...u8;" — see the CIL frontend's design task), so it would
        // otherwise fall through to the "unsupported CIL type" diagnostic at the bottom.
        if (
            typeReference is GenericInstanceType spanGit
            && spanGit.ElementType.Namespace == "System"
            && spanGit.ElementType.Name is "ReadOnlySpan`1" or "Span`1"
        )
        {
            var (spanElement, _) = Map(spanGit.GenericArguments[0]);
            return (IrType.Pointer(spanElement), false);
        }

        // System.Int128/System.UInt128 have no dedicated ECMA-335 element type (confirmed against a
        // real Cecil read: MetadataType.ValueType, same as any user struct) but are, for this
        // frontend's purposes, ordinary 128-bit scalars — CLAUDE.md's "Koh C# subset" lists i128
        // arithmetic as routing through the SM83 backend's existing generic width-N memory routines
        // (already proven for i32/i64), so only the type mapping and the operator-method call-site
        // interception (every Int128/UInt128 arithmetic/comparison/conversion op arrives in IL as a
        // call to a static operator method, never a primitive opcode — see
        // CilMethodLowerer.LowerCall's TryLowerInt128Operator) are new. Checked before
        // CilStructSupport.ResolveStruct would otherwise misclassify it as a user value-type struct —
        // see that method's own matching exclusion.
        if (typeReference.Namespace == "System" && typeReference.Name == "Int128")
            return (IrType.Int(128), true);
        if (typeReference.Namespace == "System" && typeReference.Name == "UInt128")
            return (IrType.Int(128), false);

        // An enum is just its underlying integer in IL (ECMA-335 II.14.3) — every enum-typed local/
        // field/param collapses to that integer type here. A user struct also reaches this branch
        // (MetadataType.ValueType, IsValueType true) but is never mapped through this generic path:
        // every struct-aware call site (CilLoweringContext.EnsureSignature, CilMethodLowerer.Run's
        // param/local setup, CilClassLayout.Compute) checks IsStructType and branches to struct
        // handling BEFORE calling Map — see CilMethodLowerer.Structs.cs.
        if (
            typeReference.IsValueType
            && typeReference.MetadataType == MetadataType.ValueType
            && CilModuleLowerer.ResolveSafe(typeReference) is { IsEnum: true } enumDef
        )
            return Map(CilStructSupport.EnumUnderlyingType(enumDef));

        return typeReference.MetadataType switch
        {
            MetadataType.Void => (IrType.Void, false),
            MetadataType.Boolean => (IrType.I8, false),
            MetadataType.SByte => (IrType.I8, true),
            MetadataType.Byte => (IrType.I8, false),
            MetadataType.Int16 => (IrType.I16, true),
            MetadataType.UInt16 => (IrType.I16, false),
            MetadataType.Char => (IrType.I16, false),
            MetadataType.Int32 => (IrType.I32, true),
            MetadataType.UInt32 => (IrType.I32, false),
            MetadataType.Int64 => (IrType.I64, true),
            MetadataType.UInt64 => (IrType.I64, false),
            // float32/float64 carry their raw IEEE bits as an ordinary (unsigned) 32-/64-bit int — see
            // CilMethodLowerer's float-op routing (CallRuntime/TryFloatBinaryOp/TryFloatCompareOp/
            // ConvertToFloat): every arithmetic/compare/convert opcode on a float-tagged value routes to
            // a Koh.GameBoy.SoftFloat [KohRuntime] routine instead of an ordinary IrBinaryOp/IrCompareOp,
            // so the raw-bits representation here is exactly what those routines already operate on.
            MetadataType.Single => (IrType.I32, false),
            MetadataType.Double => (IrType.I64, false),
            _ => throw new CilNotSupportedException(
                $"unsupported CIL type '{typeReference.FullName}' (phase 1 supports byte/sbyte/"
                    + "short/ushort/char/int/uint/long/ulong/Int128/UInt128/bool/float/double/void, "
                    + "pointers, and ReadOnlySpan<T>/Span<T> only)."
            ),
        };
    }

    /// <summary>Struct-aware sibling of <see cref="Map"/> for a parameter type — the ONE place that
    /// checks "is this a struct" (byval, or byref via <see cref="ByReferenceType"/>) before falling
    /// back to the ordinary scalar/pointer mapping. Used by both
    /// <see cref="CilLoweringContext.EnsureSignature"/> (building the IR function's own parameter list)
    /// and <c>CilMethodLowerer.Structs.cs</c>'s <c>DeclareParam</c> (building that same parameter's
    /// local storage) — the two must agree on shape, so both call this rather than duplicating the
    /// struct/byref detection.</summary>
    public static CilParamShape MapParam(TypeReference typeReference)
    {
        if (typeReference is ByReferenceType byRefType)
        {
            if (CilStructSupport.ResolveStruct(byRefType.ElementType) is { } byRefStruct)
                return new CilParamShape(IrType.Pointer(IrType.I8), false, byRefStruct);
            var (elementType, _) = Map(byRefType.ElementType);
            return new CilParamShape(IrType.Pointer(elementType), false, null);
        }
        if (CilStructSupport.ResolveStruct(typeReference) is { } structDef)
            return new CilParamShape(IrType.Pointer(IrType.I8), false, structDef);
        var (irType, signed) = Map(typeReference);
        return new CilParamShape(irType, signed, null);
    }
}

/// <summary>The IR shape of one parameter: its type/signedness for ordinary lowering, plus
/// <paramref name="StructType"/> when it names a struct (byval or byref) — the one extra bit
/// <see cref="CilTypeMapper.MapParam"/>'s callers need to also copy-in a byval argument (see
/// <c>CilMethodLowerer.Structs.cs</c>'s <c>PrepareArg</c>) or skip the ordinary alloca-wrapping (see
/// its <c>DeclareParam</c>).</summary>
internal readonly record struct CilParamShape(
    IrType IrType,
    bool Signed,
    TypeDefinition? StructType
);

/// <summary>
/// Lowers every eligible static method of a game module's own assembly to Koh IR (see
/// <c>docs/superpowers/specs/2026-07-14-cil-frontend-design.md</c>, phase 1, and the delegates/closures
/// task). Entry-point selection follows <see cref="ModuleDefinition.EntryPoint"/>, falling back to a
/// single static <c>Main</c>. A compiler-generated type's own members (a lambda body, a display-class
/// ctor/field-holder, a no-capture cache singleton's method) are never swept eagerly — only lowered
/// on demand, the first time a resolved call site references them (see
/// <see cref="CilLoweringContext.EnsureLowered"/>), so a construct this frontend can't yet devirtualize
/// stays a diagnostic at its call site rather than a spurious failure on an unrelated dead member.
/// </summary>
internal static class CilModuleLowerer
{
    public static void Lower(
        ModuleDefinition cecilModule,
        IrModule module,
        DiagnosticBag diagnostics
    )
    {
        var intrinsics = CilIntrinsicIndex.Build(cecilModule);
        var runtime = CilRuntimeIndex.Build(cecilModule);
        var ctx = new CilLoweringContext(module, diagnostics, intrinsics, runtime, cecilModule);
        var methods = new List<MethodDefinition>();

        foreach (var type in cecilModule.GetTypes())
        {
            if (type.Name == "<Module>")
                continue;
            foreach (var method in type.Methods)
            {
                // Static, has-a-body methods only, eagerly. A constructor is excluded here: an instance
                // .ctor is reached (when needed at all) via a resolved `newobj`, on demand; a static
                // constructor (.cctor — a static field initializer emits one that is IsStatic &&
                // HasBody) is lowered separately, in its own eager pass below (see
                // CilMethodLowerer.Statics.cs), before this list's own bodies. An open generic method
                // (HasGenericParameters) is a TEMPLATE, never lowered directly — its own body still
                // names its type parameter as '!!0' off raw Cecil metadata, which CilTypeMapper.Map
                // now rejects outright (see its ContainsGenericParameter guard). It is only ever
                // specialized on demand, the first time some call site instantiates it at concrete type
                // arguments — see CilMethodLowerer.Generics.cs's CilLoweringContext.EnsureGenericInstance.
                if (
                    !method.IsStatic
                    || !method.HasBody
                    || method.IsConstructor
                    || method.HasGenericParameters
                )
                    continue;
                methods.Add(method);
            }
        }

        // Pass 0: a pure-metadata pre-pass over every type's '.cctor', deciding each static field's
        // storage (ROM-folded constant, ROM/WRAM array alias, or an ordinary mutable WRAM holder) BEFORE
        // anything lowers a method body — see CilMethodLowerer.Statics.cs's class remarks for why this
        // must run first (a readonly field's classification would otherwise race whichever method
        // happens to reference it first, including another type's own '.cctor').
        CilStaticFieldSupport.Collect(cecilModule, ctx);

        // Pass 1: signatures, so calls resolve regardless of declaration order (mirrors
        // CSharpFrontend). A per-method failure here (an unsupported parameter/return type) reports a
        // diagnostic and leaves that method out of FunctionsByMethod — its body pass is skipped below.
        foreach (var method in methods)
            ctx.EnsureSignature(method);

        var entryMethod = ResolveEntryPoint(cecilModule, methods, diagnostics, module.Name);

        // A non-delegate `newobj` anywhere in the assembly (today: only a display-class capture
        // allocation) needs the shared bump-pointer heap global, seeded at the entry's prologue —
        // exactly CSharpFrontend's Pass-0.6 gate (CSharpFrontend.cs UsesHeap/ObjectCreationExpression
        // check), done here on raw IL instead of syntax since this frontend has no syntax tree. Scans
        // EVERY method's body, including a '.cctor' (Pass 0's idiom-matched instructions are elided at
        // LOWERING time, not here, so an un-elided 'newarr' inside a static constructor — e.g. a
        // non-constant-sized array — still needs the heap this scan provisions).
        if (NeedsHeap(cecilModule, intrinsics))
            ctx.EnsureHeapGlobal();

        // Pass 1.5: every HAND-WRITTEN type's static constructor, eagerly and BEFORE any other body —
        // an ordinary method (including the entry) may reference a static field that only Pass 0
        // classified but a '.cctor' body lowering is what actually EXECUTES the corresponding store;
        // lowering every '.cctor' now means CctorFunctions is complete by the time the entry's own
        // Run() needs to call each of them from its prologue (see CilMethodLowerer.Run's isEntry
        // cctor-call block). A compiler-generated type's own '.cctor' (a name starting with '<' — no
        // hand-written type can be named that: `<>c`'s no-capture-lambda-cache singleton, an iterator/
        // display-class's declaring type, `<PrivateImplementationDetails>`) is skipped: it exists only
        // to build a cache field (`<>9`) that this frontend's own delegate-cache-idiom interception
        // (CilMethodLowerer.Delegates.cs) never actually reads — calling it would resurrect a heap
        // allocation and a write-only global into every lambda-bearing ROM for no observable effect.
        foreach (var type in cecilModule.GetTypes())
        {
            if (type.Name.StartsWith('<'))
                continue;
            var cctor = type.Methods.FirstOrDefault(m =>
                m.IsConstructor && m.IsStatic && m.HasBody
            );
            if (cctor is null)
                continue;
            var cctorFn = ctx.EnsureSignature(cctor);
            if (cctorFn is null)
                continue;
            ctx.LowerBody(cctor, cctorFn, isEntry: false);
            ctx.CctorFunctions.Add(cctorFn);
        }

        // Pass 2: bodies. Report per-method so one bad method doesn't sink the whole compile.
        foreach (var method in methods)
        {
            if (!ctx.FunctionsByMethod.TryGetValue(method, out var fn))
                continue;
            ctx.LowerBody(method, fn, isEntry: ReferenceEquals(method, entryMethod));
        }

        // Mark the entry authoritatively (the backend boots into it by flag, not by name-matching) —
        // only once its body actually lowered (FunctionsByMethod still holds it even on a body
        // failure; IsEntry on a diagnosed-broken function is harmless since compilation already failed).
        if (
            entryMethod is not null
            && ctx.FunctionsByMethod.TryGetValue(entryMethod, out var entryFn)
        )
            entryFn.IsEntry = true;

        // Prune every function unreachable from the entry/an interrupt handler through the call graph —
        // both framework functions lowered on demand (see the referenced-assembly task, docs/
        // superpowers/specs/2026-07-14-cil-frontend-design.md, task 2) AND the game module's own dead
        // code. Pruning must be uniform: Pass 1 eagerly declares (and Pass 2 lowers) every hand-written
        // static method regardless of reachability, so a dead game function can itself hold a `Call` to
        // a framework function; leaving the dead caller in place while pruning only its unreachable
        // callee (module-identity-scoped, as this used to do) strands a `Call` to a function no longer
        // in `Module.Functions`, and `Sm83Backend.ControlFlowEmitter.EmitCall` throws reading
        // `_ctx.Allocations[callee]` for it. Computing the live set first and then dropping whatever
        // isn't in it (the plain, no-`removable` overload) prunes both the dead caller and, transitively,
        // any callee that only the dead caller reached — so nothing can end up calling a pruned function.
        // This call runs unconditionally, before the optimizer, so a game pays only for the framework
        // code it calls even when run without IrOptimizer.Optimize.
        Ir.Optimization.IrOptimizer.RemoveUnreachableFunctions(module);
    }

    /// <summary>True if any method body in the assembly (hand-written or compiler-generated —
    /// a display-class ctor lives on the generated type, not the hand-written one) constructs a
    /// non-delegate reference type, OR calls a <c>[KohIntrinsic("alloc")]</c>/<c>[KohIntrinsic(
    /// "heapreset")]</c> member (<c>Mem.Alloc</c>/<c>Mem.Reset</c>): both bump/reset the SAME shared
    /// heap global <c>new</c> does (see <see cref="CilLoweringContext.EnsureHeapGlobal"/>), so a
    /// program that only calls <c>Mem.Alloc</c> — no <c>new</c>/<c>newarr</c> anywhere — must still
    /// provision and seed that global, or the first allocation reads an unseeded value. A delegate's
    /// own <c>newobj</c> (<c>Func`2::.ctor(object, native int)</c>) is intercepted, never allocated
    /// (see <c>CilMethodLowerer.LowerNewobj</c>), so it must not itself trigger the heap.</summary>
    private static bool NeedsHeap(
        ModuleDefinition cecilModule,
        IReadOnlyDictionary<MethodDefinition, CilIntrinsicIndex.Entry> intrinsics
    )
    {
        foreach (var type in cecilModule.GetTypes())
        {
            if (type.Name == "<Module>")
                continue;
            foreach (var method in type.Methods)
            {
                // A static constructor ('.cctor') IS scanned here (unlike the eager ordinary-method
                // sweep, which lowers it separately — see CilMethodLowerer.Statics.cs): its
                // idiom-matched instructions are elided at LOWERING time, not here, so a 'newarr' that
                // Pass 0 could NOT fold away (e.g. a non-constant-sized array) still needs the heap this
                // scan provisions. A '.cctor' whose every 'newarr' WAS folded away costs nothing extra
                // beyond an unused heap-pointer global — a harmless over-approximation, not a
                // correctness issue.
                if (!method.HasBody)
                    continue;
                foreach (var instr in method.Body.Instructions)
                {
                    // A 'newarr' (see CilMethodLowerer.Arrays.cs) always needs the heap — there is no
                    // delegate-construction-style interception for it the way Newobj has.
                    if (instr.OpCode.Code == Code.Newarr)
                        return true;
                    if (instr.OpCode.Code == Code.Call || instr.OpCode.Code == Code.Callvirt)
                    {
                        MethodDefinition? calleeDef;
                        try
                        {
                            calleeDef = ((MethodReference)instr.Operand).Resolve();
                        }
                        catch (AssemblyResolutionException)
                        {
                            calleeDef = null;
                        }
                        if (
                            calleeDef is not null
                            && intrinsics.TryGetValue(calleeDef, out var entry)
                            && entry.Kind is "alloc" or "heapreset"
                        )
                            return true;
                        continue;
                    }
                    if (instr.OpCode.Code != Code.Newobj)
                        continue;
                    var ctorRef = (MethodReference)instr.Operand;
                    var declaringType = ResolveSafe(ctorRef.DeclaringType);
                    if (declaringType is not null && !IsDelegateType(declaringType))
                        return true;
                }
            }
        }
        return false;
    }

    /// <summary>A delegate type's (<c>Func`2</c>, a user-declared <c>delegate</c>, …) direct base is
    /// always exactly <c>System.MulticastDelegate</c> (never <c>System.Delegate</c> for a real,
    /// instantiable delegate type — that is MulticastDelegate's own base, one level further up).</summary>
    internal static bool IsDelegateType(TypeDefinition type) =>
        type.BaseType?.FullName == "System.MulticastDelegate";

    /// <summary>True when <paramref name="method"/> lives in the Base Class Library — the one category
    /// of referenced code the on-demand lowering task (docs/superpowers/specs/2026-07-14-cil-frontend-
    /// design.md, task 2) explicitly keeps off-limits: a BCL method's IL is not written for this
    /// frontend's opcode subset (the LINQ spike is why LINQ itself is intercepted at the call site
    /// rather than lowered), so attempting to lower one would cascade into an opaque, unpredictable
    /// diagnostic deep in framework internals no user code could ever reach anyway, rather than a clean
    /// one at the actual call site. Identified by assembly name — <c>Koh.Compiler</c> has no reference
    /// to any BCL assembly to check identity against (same constraint as <see cref="CilIntrinsicIndex"/>'s
    /// simple-name attribute match) — covering both the modern (<c>System.Private.CoreLib</c> plus the
    /// <c>System.*</c>/<c>Microsoft.*</c> facade assemblies a multi-assembly BCL splits into) and legacy
    /// (<c>mscorlib</c>, <c>netstandard</c>) naming. <c>Koh.GameBoy</c> and a game's own assembly never
    /// match this.</summary>
    internal static bool IsBclMethod(MethodDefinition method)
    {
        var name = method.Module.Assembly.Name.Name;
        return name is "System.Private.CoreLib" or "mscorlib" or "netstandard"
            || name.StartsWith("System.", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.", StringComparison.Ordinal);
    }

    internal static TypeDefinition? ResolveSafe(TypeReference typeReference)
    {
        try
        {
            return typeReference.Resolve();
        }
        catch (AssemblyResolutionException)
        {
            return null;
        }
    }

    /// <summary><see cref="ModuleDefinition.EntryPoint"/> when the assembly declares one (an EXE-style
    /// build); otherwise the single static <c>Main</c> among the module's own eligible methods. More
    /// than one is a diagnostic — which one the backend would boot into would otherwise be a silent,
    /// order-dependent guess.</summary>
    private static MethodDefinition? ResolveEntryPoint(
        ModuleDefinition cecilModule,
        List<MethodDefinition> methods,
        DiagnosticBag diagnostics,
        string moduleName
    )
    {
        if (cecilModule.EntryPoint is { } declared)
            return declared;

        var mains = methods.Where(m => m.Name == "Main").ToList();
        if (mains.Count == 0)
        {
            diagnostics.Report(
                default,
                "the 'cil' frontend found no entry point (no assembly entry point and no static "
                    + "'Main' method).",
                DiagnosticSeverity.Error,
                moduleName
            );
            return null;
        }
        if (mains.Count > 1)
        {
            diagnostics.Report(
                default,
                "multiple static 'Main' methods found ("
                    + string.Join(", ", mains.Select(m => $"{m.DeclaringType.FullName}.{m.Name}"))
                    + "); the entry point is ambiguous.",
                DiagnosticSeverity.Error,
                moduleName
            );
            return null;
        }
        return mains[0];
    }
}

/// <summary>
/// Lowers one method body: splits it into basic blocks at branch targets and after branches, then
/// simulates the CIL evaluation stack at compile time within each block, mapping locals/arguments to
/// <c>alloca</c>s exactly like <c>CSharpFrontend</c>'s <c>MethodLowerer</c>. Produces no phis —
/// <c>Mem2RegPass</c> (default-on in <see cref="CompilerDriver"/>) does SSA construction.
///
/// Stack-typing discipline follows the real CLR evaluation stack (ECMA-335 III.1.1): the stack only
/// ever holds int32/int64/native-int/float/pointer values — never an 8/16-bit type. So every local or
/// argument read narrower than 32 bits is widened right there (sign- or zero-extended per its declared
/// signedness), arithmetic always operates on same-width operands as a result, and <c>conv.i1/u1/i2/u2</c>
/// truncate then re-widen to 32 bits, exactly mirroring what those opcodes do on real hardware. The
/// redundant zext/trunc pairs this produces are expected — cleaning them up is the (out-of-phase-1)
/// narrowing pass's job, not this lowerer's.
///
/// A block's entry stack is usually empty (Roslyn IL clears the stack at nearly every block boundary).
/// Where a forward branch delivers a non-empty stack (<c>?:</c>, <c>&amp;&amp;</c>, <c>||</c> — see
/// <see cref="Deliver"/>), the pending value(s) are spilled to a dedicated <c>alloca</c> created in the
/// function's entry block (so it dominates every other block regardless of which predecessor reaches
/// the join first) and reloaded at the top of the target block. Because Roslyn never leaves a value on
/// the stack across a backward branch, blocks are processed in program order and a block's entry depth
/// is always known — via a prior forward delivery, or implicitly zero — by the time it's reached.
/// </summary>
internal sealed partial class CilMethodLowerer
{
    private readonly MethodDefinition _method;
    private readonly IrFunction _function;
    private readonly CilLoweringContext _ctx;
    private readonly bool _isEntry;

    private readonly IrBuilder _b = new();
    private readonly Dictionary<
        VariableDefinition,
        (AllocaInstruction Alloca, IrType Type, bool Signed)
    > _locals = new();
    private readonly Dictionary<
        ParameterDefinition,
        (AllocaInstruction Alloca, IrType Type, bool Signed)
    > _params = new();

    // Every basic-block leader (jump target, fallthrough-after-a-branch, or the first instruction) maps
    // to the IrBasicBlock it starts. Built once, up front, so a forward branch can always resolve its
    // target block.
    private readonly Dictionary<Instruction, IrBasicBlock> _blockOf = new();

    // Spill allocas for a block reached with a non-empty CIL stack (see class remarks). Created lazily,
    // in the function's entry block, the first time some predecessor delivers to that target.
    private readonly Dictionary<IrBasicBlock, List<AllocaInstruction>> _spillSlots = new();

    // A spill slot's floatness, recorded once at creation (see Deliver) and consulted on every reload
    // (see EntryStack) — see CilMethodLowerer.Floats.cs's class remarks.
    private readonly Dictionary<AllocaInstruction, FloatWidth> _spillFloatKind = new();

    private IrBasicBlock _entryBlock = null!;

    public CilMethodLowerer(
        MethodDefinition method,
        IrFunction function,
        CilLoweringContext ctx,
        bool isEntry,
        IReadOnlyList<TypeReference>? genericArgs = null
    )
    {
        _method = method;
        _function = function;
        _ctx = ctx;
        _isEntry = isEntry;
        _genericArgs = genericArgs;
    }

    /// <summary>The parameter/local slot for CIL argument index <paramref name="index"/>, accounting
    /// for the implicit <c>this</c> the real CLR evaluation stack (and <c>ldarg.0..3</c>'s macro
    /// encoding) always counts as argument 0 on an instance method — <see cref="MethodDefinition.Parameters"/>
    /// itself never includes it.</summary>
    private ParameterDefinition ArgAt(int index) =>
        _method.HasThis
            ? index == 0
                ? _method.Body.ThisParameter
                : _method.Parameters[index - 1]
            : _method.Parameters[index];

    public void Run()
    {
        var body = _method.Body;
        var instructions = body.Instructions;
        if (instructions.Count == 0)
            throw new CilNotSupportedException(
                $"method '{_method.FullName}' has an empty body (unsupported)."
            );

        DetectDelegateCacheIdioms();
        DetectArrayLiteralIdioms();
        // A static constructor's own idiom-matched instructions (see CilMethodLowerer.Statics.cs's
        // CilStaticFieldSupport.Collect, run once for the whole module before any body lowers) are
        // seeded here — exactly like DetectDelegateCacheIdioms' own idiom, they collapse to nothing at
        // runtime (a no-op on this method for anything that isn't itself a '.cctor').
        foreach (var elided in _ctx.ElidedInstructionsFor(_method))
            _suppressed.Add(elided);
        PrepareExceptionHandlers();

        // Every leader gets its own block, created up front and in program order — the first leader
        // (instructions[0]) becomes the entry block, so parameter/local allocas (added to it next) sit
        // before any of the method's own translated instructions.
        foreach (var instr in ComputeLeaders(instructions))
            _blockOf[instr] = _function.AppendBlock();
        _entryBlock = _blockOf[instructions[0]];

        _b.PositionAtEnd(_entryBlock);

        var paramIndex = 0;
        if (_method.HasThis)
        {
            var thisParam = _method.Body.ThisParameter;
            var (thisType, _) = CilTypeMapper.Map(_method.DeclaringType);
            var thisAlloca = _b.Alloca(thisType);
            _b.Store(_function.Parameters[paramIndex], thisAlloca);
            _params[thisParam] = (thisAlloca, thisType, false);
            paramIndex++;
        }
        for (var i = 0; i < _method.Parameters.Count; i++, paramIndex++)
            DeclareParam(_method.Parameters[i], _function.Parameters[paramIndex]);

        if (body.HasVariables)
            foreach (var v in body.Variables)
                DeclareLocal(v);

        // The entry function alone seeds the shared heap pointer (mirrors CSharpFrontend's
        // MethodLowerer, which does the same for its own `_staticInits` — see CilLoweringContext's
        // remarks). A no-op when the assembly never allocates (HeapGlobal stays null).
        if (_isEntry && _ctx.HeapGlobal is { } heap)
            _b.Store(
                IrBuilder.ConstInt(IrType.I16, CilLoweringContext.HeapTop),
                IrBuilder.GlobalRef(heap)
            );

        // The entry function alone runs every type's static constructor, once, before its own body —
        // mirrors CSharpFrontend's staticInits, emitted at the top of Main, but as a real call (a CIL
        // '.cctor' can contain arbitrary lowerable IL, not just a flat constant-store list) — see
        // CilMethodLowerer.Statics.cs.
        if (_isEntry)
            foreach (var cctorFn in _ctx.CctorFunctions)
                _b.Call(cctorFn, []);

        var stack = new List<IrValue>();

        foreach (var instr in instructions)
        {
            // A 'finally' handler's own instruction range is never part of the normal instruction
            // stream — it is only ever reached via a cloned copy emitted at each 'leave' (see
            // CilMethodLowerer.Iterators.cs's LowerFinallyBody).
            if (_finallyHandlerInstructions.Contains(instr))
                continue;

            // Always resolve "where are we" from the builder itself, never a separately tracked
            // variable — a helper Simulate calls into (EmitZeroFill/EmitZeroFillDynamic,
            // LowerFinallyBody's own clone) can reposition the builder to an ad-hoc block it created
            // (e.g. a zero-fill loop's "done" block) and leave it positioned there for the caller to
            // keep appending to; a stale local snapshot of "the current block" would then rewind to the
            // wrong (empty, since-abandoned) block on the next leader transition, orphaning whatever was
            // actually appended to the ad-hoc block (verified by a real bug this fixed: an iterator's
            // dead-at-runtime-but-still-compiled 'newobj' branch inside GetEnumerator).
            if (
                _blockOf.TryGetValue(instr, out var leaderBlock)
                && !ReferenceEquals(leaderBlock, _b.CurrentBlock)
            )
            {
                if (_b.CurrentBlock.Terminator is null)
                {
                    // Fell through a leader boundary without an explicit branch/ret (e.g. a block that
                    // is someone else's jump target but otherwise just runs into the next instruction).
                    Deliver(stack, leaderBlock);
                    _b.Br(leaderBlock);
                }
                _b.PositionAtEnd(leaderBlock);
                stack = EntryStack(leaderBlock);
            }

            Simulate(instr, stack);
        }
    }

    /// <summary>Leader instructions: the first instruction, every branch target, and the instruction
    /// immediately after any branch or <c>ret</c> (a block boundary even with no label pointing at it —
    /// the next byte is reachable only by falling through, or not at all, but either way it starts a
    /// fresh block per <see cref="Run"/>'s "implicit fallthrough" handling). A branch instruction that
    /// is part of a recognized delegate-cache idiom (<see cref="DetectDelegateCacheIdioms"/>) is
    /// excluded from leader contribution — that guard is resolved entirely at compile time (see
    /// <see cref="_suppressed"/>'s remarks), so it must not fork the control-flow graph.</summary>
    private IEnumerable<Instruction> ComputeLeaders(
        Mono.Collections.Generic.Collection<Instruction> instructions
    )
    {
        var leaders = new HashSet<Instruction>();
        if (instructions.Count > 0)
            leaders.Add(instructions[0]);

        foreach (var instr in instructions)
        {
            // A 'finally' handler's own instructions are never leaders of the normal block graph — see
            // CilMethodLowerer.Iterators.cs's remarks (they're only ever reached via a clone).
            if (_finallyHandlerInstructions.Contains(instr))
                continue;
            if (_suppressed.Contains(instr) || !IsBranchOrReturn(instr.OpCode.Code))
                continue;
            foreach (var target in BranchTargets(instr))
                if (!_finallyHandlerInstructions.Contains(target))
                    leaders.Add(target);
            if (instr.Next is not null && !_finallyHandlerInstructions.Contains(instr.Next))
                leaders.Add(instr.Next);
        }

        // Preserve program order (the caller relies on it to create blocks front-to-back). Defensively
        // exclude handler-range instructions from the returned set too, even though nothing above
        // should have added one.
        return instructions.Where(i =>
            leaders.Contains(i) && !_finallyHandlerInstructions.Contains(i)
        );
    }

    private static bool IsBranchOrReturn(Code code) =>
        code
            is Code.Ret
                or Code.Switch
                or Code.Br
                or Code.Br_S
                or Code.Brtrue
                or Code.Brtrue_S
                or Code.Brfalse
                or Code.Brfalse_S
                or Code.Beq
                or Code.Beq_S
                or Code.Bne_Un
                or Code.Bne_Un_S
                or Code.Blt
                or Code.Blt_S
                or Code.Blt_Un
                or Code.Blt_Un_S
                or Code.Ble
                or Code.Ble_S
                or Code.Ble_Un
                or Code.Ble_Un_S
                or Code.Bgt
                or Code.Bgt_S
                or Code.Bgt_Un
                or Code.Bgt_Un_S
                or Code.Bge
                or Code.Bge_S
                or Code.Bge_Un
                or Code.Bge_Un_S
                or Code.Leave
                or Code.Leave_S;

    private static IEnumerable<Instruction> BranchTargets(Instruction instr) =>
        instr.OpCode.Code switch
        {
            Code.Ret => [],
            // A jump table's operand is every case target; the "next instruction" (added separately by
            // ComputeLeaders' unconditional instr.Next handling, same as every other branch) is the
            // default/fallthrough target (ECMA-335 III.3.66: an out-of-range selector falls through).
            Code.Switch => (Instruction[])instr.Operand,
            _ => [(Instruction)instr.Operand],
        };

    /// <summary>The stack an already-positioned block starts with: reloads from its spill allocas
    /// (see <see cref="Deliver"/>) if any predecessor already delivered a non-empty stack to it, else
    /// empty — the overwhelmingly common case.</summary>
    private List<IrValue> EntryStack(IrBasicBlock block)
    {
        if (!_spillSlots.TryGetValue(block, out var slots))
            return [];
        var stack = new List<IrValue>(slots.Count);
        foreach (var slot in slots)
        {
            var loaded = _b.Load(slot);
            // Re-tag a float value across the spill/reload round-trip — see Deliver's matching remark
            // and CilMethodLowerer.Floats.cs's class remarks (a bare alloca/Load has no floatness of its
            // own; without this, a `cond ? floatA : floatB` join would silently lose its tag and route
            // any FOLLOWING arithmetic on it through the ordinary int path instead of SoftFloat).
            if (_spillFloatKind.TryGetValue(slot, out var floatKind))
                TagFloat(loaded, floatKind);
            stack.Add(loaded);
        }
        return stack;
    }

    /// <summary>Spill whatever remains on the simulated stack across a control-flow edge into
    /// <paramref name="target"/>'s spill allocas, creating them (in the entry block, so they dominate
    /// every block) the first time <paramref name="target"/> is reached with a non-empty stack. A no-op
    /// when <paramref name="remainder"/> is empty — the common case pays nothing.</summary>
    private void Deliver(List<IrValue> remainder, IrBasicBlock target)
    {
        if (remainder.Count == 0)
            return;

        if (!_spillSlots.TryGetValue(target, out var slots))
        {
            slots = [];
            foreach (var v in remainder)
            {
                var alloca = new AllocaInstruction(v.Type) { Parent = _entryBlock };
                _entryBlock.Instructions.Insert(0, alloca);
                slots.Add(alloca);
                // Record this slot's floatness once, at creation — see EntryStack's matching remark.
                if (FloatKindOf(v) is { } floatKind)
                    _spillFloatKind[alloca] = floatKind;
            }
            _spillSlots[target] = slots;
        }

        for (var i = 0; i < remainder.Count; i++)
            _b.Store(CoerceStore(remainder[i], slots[i].Allocated), slots[i]);
    }

    private static IrValue Pop(List<IrValue> stack)
    {
        var v = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        return v;
    }

    /// <summary>Widen a value narrower than 32 bits up to i32, per real CLR stack typing (see class
    /// remarks) — a no-op for anything already 32-bit-or-wider (including pointers).</summary>
    private IrValue WidenToStack(IrValue raw, bool signed)
    {
        if (raw.Type.Kind != IrTypeKind.Int || raw.Type.Bits >= 32)
            return raw;
        return _b.Conv(signed ? IrConvOp.SExt : IrConvOp.ZExt, raw, IrType.I32);
    }

    /// <summary>Narrow (or, best-effort, widen) a stack value down to a storage/return target type —
    /// the mirror of <see cref="WidenToStack"/>, used by stores back to a local/argument/return slot.
    /// Also reinterprets across the pointer/address-width-int boundary in either direction (a raw
    /// pointer cast, which Roslyn emits no opcode for at all — both sides are already "address-shaped"
    /// on the real CLR stack; or a round-trip through <c>conv.i</c>/<c>conv.u</c> — see
    /// <see cref="AddOrSub"/>'s remarks) — never a resize, since a pointer's storage width is always
    /// the target's address width regardless of its pointee's size.</summary>
    private IrValue CoerceStore(IrValue value, IrType target)
    {
        if (value.Type.StructurallyEquals(target))
            return value;
        if (value.Type.Kind == IrTypeKind.Int && target.Kind == IrTypeKind.Int)
        {
            // Fold a compile-time constant directly rather than wrapping it in a runtime 'conv' —
            // keeps a constant a constant (IrOptimizer would fold it right back anyway, but some
            // frontend-local decisions, e.g. LowerLocalloc's compile-time-size requirement, need to see
            // through a stack-typing conv/coercion pair immediately, before any optimizer pass runs).
            if (value is IrConstInt constValue)
                return IrBuilder.ConstInt(
                    target,
                    target.Bits >= 64
                        ? constValue.Value
                        : constValue.Value & ((1L << target.Bits) - 1)
                );
            if (value.Type.Bits > target.Bits)
                return _b.Conv(IrConvOp.Trunc, value, target);
            if (value.Type.Bits < target.Bits)
                return _b.Conv(IrConvOp.ZExt, value, target);
            return value;
        }
        if (target.Kind == IrTypeKind.Pointer)
        {
            var asAddr =
                value.Type.Kind == IrTypeKind.Pointer
                    ? value
                    : CoerceStore(value, IrType.Int(target.SizeInBits));
            return _b.Conv(IrConvOp.Bitcast, asAddr, target);
        }
        if (value.Type.Kind == IrTypeKind.Pointer && target.Kind == IrTypeKind.Int)
        {
            var asInt = _b.Conv(IrConvOp.Bitcast, value, IrType.Int(value.Type.SizeInBits));
            return CoerceStore(asInt, target);
        }
        return value;
    }

    /// <summary>A raw pointer can't feed <c>icmp</c> (<see cref="IrVerifier"/> requires integer
    /// operands); reinterpret it as an address-width integer first. A no-op for anything already
    /// integer-typed.</summary>
    private IrValue AsComparable(IrValue v) =>
        v.Type.Kind == IrTypeKind.Pointer
            ? _b.Conv(IrConvOp.Bitcast, v, IrType.Int(v.Type.SizeInBits))
            : v;

    /// <summary>A pointer can't feed the <c>conv.i1/u1/i2/u2</c> narrowing chain directly (same
    /// "Trunc requires an integer operand" constraint <see cref="AsComparable"/> works around for
    /// <c>icmp</c>): reinterpret it as an address-width integer, then widen/narrow that to i32 so it
    /// matches the i32 contract every caller of this (<see cref="ResolveFloatToInt"/>'s own return
    /// contract) already assumes. A no-op for an already-integer operand.</summary>
    private IrValue ResolvePointerForNarrowConv(IrValue v) =>
        v.Type.Kind == IrTypeKind.Pointer
            ? CoerceStore(_b.Conv(IrConvOp.Bitcast, v, IrType.Int(v.Type.SizeInBits)), IrType.I32)
            : v;

    private IrValue LoadLocal(VariableDefinition v)
    {
        // A struct-typed local's "value" is its address — see CilMethodLowerer.Structs.cs's class
        // remarks; none of the scalar side-table round-trips below apply to it.
        if (TryLoadStructLocal(v, out var structAddr))
            return structAddr;
        var (alloca, _, signed) = _locals[v];
        var loaded = WidenToStack(_b.Load(alloca), signed);
        // A float-typed local carries no separate durable tag (unlike delegate/array/concrete-type
        // provenance below): its declared type (post generic substitution) is always available again
        // right here, every load — see CilMethodLowerer.Floats.cs's class remarks.
        if (FloatKindOfType(Subst(v.VariableType)) is { } floatKind)
            TagFloat(loaded, floatKind);
        // Carry a resolved delegate's target method forward across the load — see
        // CilMethodLowerer.Delegates.cs's remarks on _pendingDelegateProvenance/_localDelegateTarget.
        if (_localDelegateTarget.TryGetValue(v, out var target))
            _pendingDelegateProvenance[loaded] = target;
        // Same round-trip for a 'newarr'-allocated array's element type/signedness/count — see
        // CilMethodLowerer.Arrays.cs's remarks on _pendingArrayInfo/_localArrayInfo.
        if (_localArrayInfo.TryGetValue(v, out var arrayInfo))
            _pendingArrayInfo[loaded] = arrayInfo;
        if (_localConcreteType.TryGetValue(v, out var concreteType))
            _pendingConcreteType[loaded] = concreteType;
        return loaded;
    }

    private void StoreLocal(VariableDefinition v, IrValue value)
    {
        if (TryStoreStructLocal(v, value))
            return;
        var (alloca, type, _) = _locals[v];
        if (_pendingDelegateProvenance.TryGetValue(value, out var target))
            _localDelegateTarget[v] = target;
        else
            _localDelegateTarget.Remove(v);
        if (_pendingArrayInfo.TryGetValue(value, out var arrayInfo))
            _localArrayInfo[v] = arrayInfo;
        else
            _localArrayInfo.Remove(v);
        // Same round-trip for a Newobj/known-return's concrete type — see
        // CilMethodLowerer.Iterators.cs's remarks on _pendingConcreteType/_localConcreteType. NOTE:
        // like the two tables above, this is a simple last-write-wins side table with no phi-aware
        // merge across predecessors — sound only because every join this frontend actually lowers (the
        // iterator kickoff/GetEnumerator reuse-vs-fresh shapes) has every predecessor agree on the
        // concrete type; a real divergence would silently lose provenance (falling back to the
        // ordinary sealed/final devirtualization rule, not a miscompile) rather than being asserted.
        if (_pendingConcreteType.TryGetValue(value, out var concreteType))
            _localConcreteType[v] = concreteType;
        else
            _localConcreteType.Remove(v);
        _b.Store(CoerceStore(value, type), alloca);
    }

    private IrValue LoadArg(ParameterDefinition p)
    {
        if (TryLoadStructArg(p, out var structAddr))
            return structAddr;
        var (alloca, _, signed) = _params[p];
        var loaded = WidenToStack(_b.Load(alloca), signed);
        // See LoadLocal's matching remark: a float-typed parameter's tag is re-derived from its
        // declared type on every load rather than tracked in a separate durable table.
        if (FloatKindOfType(Subst(p.ParameterType)) is { } floatKind)
            TagFloat(loaded, floatKind);
        // A sealed declaring type's own 'this' is always exactly that type (no subclass can exist to
        // make it otherwise) — seeds concrete-type devirtualization for an iterator state machine's own
        // instance methods (GetEnumerator/MoveNext/Dispose/get_Current all read 'this' this way). Not
        // safe to assume for a non-sealed declaring type (a derived instance could be calling through
        // a base method), so restricted to sealed — see CilMethodLowerer.Iterators.cs's remarks.
        if (
            _method.HasThis
            && ReferenceEquals(p, _method.Body.ThisParameter)
            && _method.DeclaringType.IsSealed
        )
            _pendingConcreteType[loaded] = _method.DeclaringType;
        return loaded;
    }

    private void StoreArg(ParameterDefinition p, IrValue value)
    {
        if (TryStoreStructArg(p, value))
            return;
        var (alloca, type, _) = _params[p];
        _b.Store(CoerceStore(value, type), alloca);
    }

    private void Simulate(Instruction instr, List<IrValue> stack)
    {
        // A delegate-cache idiom's interior instructions (see DetectDelegateCacheIdioms) resolve
        // entirely at compile time and emit nothing at runtime — every one of them, including its
        // starting `ldsfld`, is either suppressed outright or replaced below.
        if (_suppressed.Contains(instr))
            return;
        if (_ldsfldDelegateProvenance.TryGetValue(instr, out var cachedTarget))
        {
            var env = IrBuilder.ConstInt(IrType.Pointer(IrType.I8), 0);
            _pendingDelegateProvenance[env] = cachedTarget;
            stack.Add(env);
            return;
        }

        var code = instr.OpCode.Code;
        switch (code)
        {
            case Code.Nop:
                break;

            // ---- Constants ------------------------------------------------------------------
            case Code.Ldc_I4_M1:
                stack.Add(IrBuilder.ConstInt(IrType.I32, -1));
                break;
            case Code.Ldc_I4_0:
            case Code.Ldc_I4_1:
            case Code.Ldc_I4_2:
            case Code.Ldc_I4_3:
            case Code.Ldc_I4_4:
            case Code.Ldc_I4_5:
            case Code.Ldc_I4_6:
            case Code.Ldc_I4_7:
            case Code.Ldc_I4_8:
                stack.Add(IrBuilder.ConstInt(IrType.I32, code - Code.Ldc_I4_0));
                break;
            case Code.Ldc_I4_S:
                stack.Add(IrBuilder.ConstInt(IrType.I32, (sbyte)instr.Operand));
                break;
            case Code.Ldc_I4:
                stack.Add(IrBuilder.ConstInt(IrType.I32, (int)instr.Operand));
                break;
            case Code.Ldc_I8:
                stack.Add(IrBuilder.ConstInt(IrType.I64, (long)instr.Operand));
                break;
            case Code.Ldc_R4:
            {
                var bits = BitConverter.SingleToUInt32Bits((float)instr.Operand);
                var c = IrBuilder.ConstInt(IrType.I32, unchecked((int)bits));
                TagFloat(c, FloatWidth.F32);
                stack.Add(c);
                break;
            }
            case Code.Ldc_R8:
            {
                var bits = BitConverter.DoubleToUInt64Bits((double)instr.Operand);
                var c = IrBuilder.ConstInt(IrType.I64, unchecked((long)bits));
                TagFloat(c, FloatWidth.F64);
                stack.Add(c);
                break;
            }
            // A string literal — see CilMethodLowerer.Strings.cs's class remarks (ASCII-bytes-in-ROM,
            // one byte per char).
            case Code.Ldstr:
                stack.Add(LowerLdstr((string)instr.Operand));
                break;

            // ---- Locals / arguments -----------------------------------------------------------
            case Code.Ldloc_0:
                stack.Add(LoadLocal(_method.Body.Variables[0]));
                break;
            case Code.Ldloc_1:
                stack.Add(LoadLocal(_method.Body.Variables[1]));
                break;
            case Code.Ldloc_2:
                stack.Add(LoadLocal(_method.Body.Variables[2]));
                break;
            case Code.Ldloc_3:
                stack.Add(LoadLocal(_method.Body.Variables[3]));
                break;
            case Code.Ldloc_S:
            case Code.Ldloc:
                stack.Add(LoadLocal((VariableDefinition)instr.Operand));
                break;
            case Code.Ldloca_S:
            case Code.Ldloca:
                stack.Add(AddressOfLocal((VariableDefinition)instr.Operand));
                break;
            case Code.Stloc_0:
                StoreLocal(_method.Body.Variables[0], Pop(stack));
                break;
            case Code.Stloc_1:
                StoreLocal(_method.Body.Variables[1], Pop(stack));
                break;
            case Code.Stloc_2:
                StoreLocal(_method.Body.Variables[2], Pop(stack));
                break;
            case Code.Stloc_3:
                StoreLocal(_method.Body.Variables[3], Pop(stack));
                break;
            case Code.Stloc_S:
            case Code.Stloc:
                StoreLocal((VariableDefinition)instr.Operand, Pop(stack));
                break;
            case Code.Ldarg_0:
                stack.Add(LoadArg(ArgAt(0)));
                break;
            case Code.Ldarg_1:
                stack.Add(LoadArg(ArgAt(1)));
                break;
            case Code.Ldarg_2:
                stack.Add(LoadArg(ArgAt(2)));
                break;
            case Code.Ldarg_3:
                stack.Add(LoadArg(ArgAt(3)));
                break;
            case Code.Ldarg_S:
            case Code.Ldarg:
                stack.Add(LoadArg((ParameterDefinition)instr.Operand));
                break;
            case Code.Starg_S:
            case Code.Starg:
                StoreArg((ParameterDefinition)instr.Operand, Pop(stack));
                break;
            case Code.Ldarga_S:
            case Code.Ldarga:
                stack.Add(AddressOfArg((ParameterDefinition)instr.Operand));
                break;

            case Code.Dup:
                stack.Add(stack[^1]);
                break;
            case Code.Pop:
                Pop(stack);
                break;

            // ---- Arithmetic / logic ------------------------------------------------------------
            // Each of add/sub/mul/div/rem tries the float-tagged path first (see
            // CilMethodLowerer.Floats.cs) — a no-op fall-through to the ordinary int/pointer path when
            // neither operand is float-tagged.
            case Code.Add:
                if (!TryFloatBinaryOp(stack, "add"))
                    AddOrSub(stack, subtract: false);
                break;
            case Code.Sub:
                if (!TryFloatBinaryOp(stack, "sub"))
                    AddOrSub(stack, subtract: true);
                break;
            case Code.Mul:
                if (!TryFloatBinaryOp(stack, "mul"))
                    BinaryOp(stack, IrBinaryOp.Mul);
                break;
            case Code.And:
                BinaryOp(stack, IrBinaryOp.And);
                break;
            case Code.Or:
                BinaryOp(stack, IrBinaryOp.Or);
                break;
            case Code.Xor:
                BinaryOp(stack, IrBinaryOp.Xor);
                break;
            case Code.Shl:
                ShiftOp(stack, IrBinaryOp.Shl);
                break;
            case Code.Shr:
                ShiftOp(stack, IrBinaryOp.AShr);
                break;
            case Code.Shr_Un:
                ShiftOp(stack, IrBinaryOp.LShr);
                break;
            case Code.Div:
                if (!TryFloatBinaryOp(stack, "div"))
                    BinaryOp(stack, IrBinaryOp.SDiv);
                break;
            case Code.Div_Un:
                BinaryOp(stack, IrBinaryOp.UDiv);
                break;
            // "rem" is not in the [KohRuntime] vocabulary (no "f32.rem"/"f64.rem" key — see
            // CilMethodLowerer.Floats.cs's class remarks): a float-tagged operand here throws a
            // diagnostic (via CallRuntime/EnsureRuntime) naming the missing key rather than falling
            // through to an int remainder of the raw bits.
            case Code.Rem:
                if (!TryFloatBinaryOp(stack, "rem"))
                    BinaryOp(stack, IrBinaryOp.SRem);
                break;
            case Code.Rem_Un:
                BinaryOp(stack, IrBinaryOp.URem);
                break;
            case Code.Neg:
                if (!TryFloatNeg(stack))
                {
                    var a = Pop(stack);
                    stack.Add(_b.Sub(IrBuilder.ConstInt(a.Type, 0), a));
                }
                break;
            case Code.Not:
            {
                var a = Pop(stack);
                stack.Add(_b.Binary(IrBinaryOp.Xor, a, IrBuilder.ConstInt(a.Type, -1)));
                break;
            }

            case Code.Ceq:
                if (!TryFloatCompareOp(stack, "eq"))
                    CompareOp(stack, IrCompareOp.Eq);
                break;
            case Code.Clt:
                if (!TryFloatCompareOp(stack, "lt"))
                    CompareOp(stack, IrCompareOp.Slt);
                break;
            case Code.Clt_Un:
                if (!TryFloatCompareUnOp(stack, greaterThan: false))
                    CompareOp(stack, IrCompareOp.Ult);
                break;
            case Code.Cgt:
                if (!TryFloatCompareOp(stack, "gt"))
                    CompareOp(stack, IrCompareOp.Sgt);
                break;
            case Code.Cgt_Un:
                if (!TryFloatCompareUnOp(stack, greaterThan: true))
                    CompareOp(stack, IrCompareOp.Ugt);
                break;

            // ---- Conversions ---------------------------------------------------------------------
            // conv.i1/u1/i2/u2 narrow then re-widen to i32 (real CLR semantics — see class remarks).
            // Every one of these also accepts a float-tagged source (real CLR conv.* accepts an "F"
            // stack value too — ECMA-335 III.3.27 family): ResolveFloatToInt resolves it to an ordinary
            // int32 first (a no-op for an already-ordinary int source) — see CilMethodLowerer.Floats.cs.
            // An explicit pointer-to-narrower-integer cast (e.g. Koh.GameBoy.Cgb.CopyToVram's
            // `(ushort)source` on a `byte*` parameter — a real, shipped call shape, not hypothetical)
            // also reaches here as one of these four opcodes: real CLR conv.u2/i2/u1/i1 accept a
            // native-int-tagged (pointer) stack value (ECMA-335 III.3.27), but this frontend's operand
            // is Pointer-typed, not Int-typed, so it can't feed `Trunc` directly (IrVerifier: "'trunc'
            // requires integer operand and result") — ResolvePointerForNarrowConv reinterprets it as
            // an address-width integer first (bitcast, then widened/narrowed to i32 exactly like
            // ResolveFloatToInt's own int32 contract), a no-op for an already-integer operand.
            case Code.Conv_I1:
                stack.Add(
                    _b.Conv(
                        IrConvOp.SExt,
                        _b.Conv(
                            IrConvOp.Trunc,
                            ResolveFloatToInt(
                                ResolvePointerForNarrowConv(Pop(stack)),
                                32,
                                signed: true
                            ),
                            IrType.I8
                        ),
                        IrType.I32
                    )
                );
                break;
            case Code.Conv_U1:
                stack.Add(
                    _b.Conv(
                        IrConvOp.ZExt,
                        _b.Conv(
                            IrConvOp.Trunc,
                            ResolveFloatToInt(
                                ResolvePointerForNarrowConv(Pop(stack)),
                                32,
                                signed: true
                            ),
                            IrType.I8
                        ),
                        IrType.I32
                    )
                );
                break;
            case Code.Conv_I2:
                stack.Add(
                    _b.Conv(
                        IrConvOp.SExt,
                        _b.Conv(
                            IrConvOp.Trunc,
                            ResolveFloatToInt(
                                ResolvePointerForNarrowConv(Pop(stack)),
                                32,
                                signed: true
                            ),
                            IrType.I16
                        ),
                        IrType.I32
                    )
                );
                break;
            case Code.Conv_U2:
                stack.Add(
                    _b.Conv(
                        IrConvOp.ZExt,
                        _b.Conv(
                            IrConvOp.Trunc,
                            ResolveFloatToInt(
                                ResolvePointerForNarrowConv(Pop(stack)),
                                32,
                                signed: true
                            ),
                            IrType.I16
                        ),
                        IrType.I32
                    )
                );
                break;
            case Code.Conv_I4:
            case Code.Conv_U4:
            {
                var v = ResolveFloatToInt(Pop(stack), 32, signed: code == Code.Conv_I4);
                stack.Add(v.Type.Bits > 32 ? _b.Conv(IrConvOp.Trunc, v, IrType.I32) : v);
                break;
            }
            // conv.i8 sign-extends a narrower stack value to 64 bits (a no-op if already 64-bit);
            // conv.u8 zero-extends. Real CLR semantics (ECMA-335 III.3.27/III.3.29) — unlike
            // conv.i1/u1/i2/u2 above, there is no re-narrowing step, since int64 IS a native stack
            // width (see class remarks), not something later widened back up.
            case Code.Conv_I8:
            {
                var v = ResolveFloatToInt(Pop(stack), 64, signed: true);
                stack.Add(v.Type.Bits >= 64 ? v : _b.Conv(IrConvOp.SExt, v, IrType.I64));
                break;
            }
            case Code.Conv_U8:
            {
                var v = ResolveFloatToInt(Pop(stack), 64, signed: false);
                stack.Add(v.Type.Bits >= 64 ? v : _b.Conv(IrConvOp.ZExt, v, IrType.I64));
                break;
            }
            // conv.i/conv.u: to "native int" — this target's address width (I16). A pointer operand
            // reinterprets (a numeric pointer-value cast, e.g. `(nint)p` or the first step of unsafe
            // pointer arithmetic — see AddOrSub); an integer operand narrows/widens like any other
            // conv.* (real CLR native int is address-width, not fixed at 32/64); a float-tagged operand
            // resolves through ResolveFloatToInt exactly like the other conv.* cases above.
            case Code.Conv_I:
            case Code.Conv_U:
                stack.Add(
                    CoerceStore(
                        ResolveFloatToInt(Pop(stack), 32, signed: code == Code.Conv_I),
                        IrType.I16
                    )
                );
                break;

            // ---- Float conversions (see CilMethodLowerer.Floats.cs) ---------------------------
            case Code.Conv_R4:
                ConvertToFloat(stack, FloatWidth.F32);
                break;
            case Code.Conv_R8:
                ConvertToFloat(stack, FloatWidth.F64);
                break;
            case Code.Conv_R_Un:
                LowerConvRUn(stack);
                break;

            // ---- Pointer dereference (ldind.*/stind.*) -----------------------------------------
            case Code.Ldind_I1:
                LoadIndirect(stack, IrType.I8, signed: true);
                break;
            case Code.Ldind_U1:
                LoadIndirect(stack, IrType.I8, signed: false);
                break;
            case Code.Ldind_I2:
                LoadIndirect(stack, IrType.I16, signed: true);
                break;
            case Code.Ldind_U2:
                LoadIndirect(stack, IrType.I16, signed: false);
                break;
            case Code.Ldind_I4:
                LoadIndirect(stack, IrType.I32, signed: true);
                break;
            case Code.Ldind_U4:
                LoadIndirect(stack, IrType.I32, signed: false);
                break;
            case Code.Ldind_I8:
                LoadIndirect(stack, IrType.I64, signed: true);
                break;
            case Code.Ldind_I:
                LoadIndirect(stack, IrType.I16, signed: false);
                break;
            case Code.Ldind_Ref:
                LoadIndirect(stack, IrType.Pointer(IrType.I8), signed: false);
                break;
            case Code.Ldind_R4:
                LoadIndirect(stack, IrType.I32, signed: false);
                TagFloat(stack[^1], FloatWidth.F32);
                break;
            case Code.Ldind_R8:
                LoadIndirect(stack, IrType.I64, signed: false);
                TagFloat(stack[^1], FloatWidth.F64);
                break;
            case Code.Stind_I1:
                StoreIndirect(stack, IrType.I8);
                break;
            case Code.Stind_I2:
                StoreIndirect(stack, IrType.I16);
                break;
            case Code.Stind_I4:
                StoreIndirect(stack, IrType.I32);
                break;
            case Code.Stind_I8:
                StoreIndirect(stack, IrType.I64);
                break;
            case Code.Stind_I:
                StoreIndirect(stack, IrType.I16);
                break;
            case Code.Stind_Ref:
                StoreIndirect(stack, IrType.Pointer(IrType.I8));
                break;
            case Code.Stind_R4:
                StoreIndirect(stack, IrType.I32);
                break;
            case Code.Stind_R8:
                StoreIndirect(stack, IrType.I64);
                break;

            // ---- localloc (stackalloc) ----------------------------------------------------------
            case Code.Localloc:
                LowerLocalloc(stack);
                break;

            // ---- Control flow ---------------------------------------------------------------------
            case Code.Br:
            case Code.Br_S:
            {
                var target = _blockOf[(Instruction)instr.Operand];
                Deliver(stack, target);
                _b.Br(target);
                break;
            }
            case Code.Brtrue:
            case Code.Brtrue_S:
            case Code.Brfalse:
            case Code.Brfalse_S:
            {
                var v = AsComparable(Pop(stack));
                var isTrue = code is Code.Brtrue or Code.Brtrue_S;
                var cmp = _b.Compare(
                    isTrue ? IrCompareOp.Ne : IrCompareOp.Eq,
                    v,
                    IrBuilder.ConstInt(v.Type, 0)
                );
                var target = _blockOf[(Instruction)instr.Operand];
                var fallThrough = _blockOf[instr.Next!];
                Deliver(stack, target);
                Deliver(stack, fallThrough);
                _b.CondBr(cmp, target, fallThrough);
                break;
            }
            case Code.Beq:
            case Code.Beq_S:
            case Code.Bne_Un:
            case Code.Bne_Un_S:
            case Code.Blt:
            case Code.Blt_S:
            case Code.Blt_Un:
            case Code.Blt_Un_S:
            case Code.Ble:
            case Code.Ble_S:
            case Code.Ble_Un:
            case Code.Ble_Un_S:
            case Code.Bgt:
            case Code.Bgt_S:
            case Code.Bgt_Un:
            case Code.Bgt_Un_S:
            case Code.Bge:
            case Code.Bge_S:
            case Code.Bge_Un:
            case Code.Bge_Un_S:
            {
                // A float-tagged operand routes through SoftFloat instead (see
                // CilMethodLowerer.Floats.cs's TryFloatBranchCondition) — Roslyn compiles a float
                // relational used directly as a branch condition (`if (a < b)`, `a < b ? x : y`, …) to
                // exactly these fused branch-compare opcodes, never to a `clt`/`cgt`/`ceq` followed by
                // `brtrue`/`brfalse`, so this path needs its own float routing distinct from
                // TryFloatCompareOp/TryFloatCompareUnOp's (value-producing) one.
                var cmp = TryFloatBranchCondition(stack, code);
                if (cmp is null)
                {
                    var b = AsComparable(Pop(stack));
                    var a = AsComparable(Pop(stack));
                    if (!a.Type.StructurallyEquals(b.Type))
                        b = CoerceStore(b, a.Type);
                    var predicate = code switch
                    {
                        Code.Beq or Code.Beq_S => IrCompareOp.Eq,
                        Code.Bne_Un or Code.Bne_Un_S => IrCompareOp.Ne,
                        Code.Blt or Code.Blt_S => IrCompareOp.Slt,
                        Code.Blt_Un or Code.Blt_Un_S => IrCompareOp.Ult,
                        Code.Ble or Code.Ble_S => IrCompareOp.Sle,
                        Code.Ble_Un or Code.Ble_Un_S => IrCompareOp.Ule,
                        Code.Bgt or Code.Bgt_S => IrCompareOp.Sgt,
                        Code.Bgt_Un or Code.Bgt_Un_S => IrCompareOp.Ugt,
                        Code.Bge or Code.Bge_S => IrCompareOp.Sge,
                        Code.Bge_Un or Code.Bge_Un_S => IrCompareOp.Uge,
                        _ => throw new InvalidOperationException(),
                    };
                    cmp = _b.Compare(predicate, a, b);
                }
                var target = _blockOf[(Instruction)instr.Operand];
                var fallThrough = _blockOf[instr.Next!];
                Deliver(stack, target);
                Deliver(stack, fallThrough);
                _b.CondBr(cmp, target, fallThrough);
                break;
            }

            case Code.Switch:
            {
                var targets = (Instruction[])instr.Operand;
                var value = AsComparable(Pop(stack));
                var defaultTarget = _blockOf[instr.Next!];
                var cases = new List<(IrConstInt, IrBasicBlock)>(targets.Length);
                for (var i = 0; i < targets.Length; i++)
                    cases.Add((IrBuilder.ConstInt(value.Type, i), _blockOf[targets[i]]));
                Deliver(stack, defaultTarget);
                foreach (var (_, caseTarget) in cases)
                    Deliver(stack, caseTarget);
                _b.Switch(value, defaultTarget, cases);
                break;
            }

            case Code.Call:
                LowerCall((MethodReference)instr.Operand, stack);
                break;
            case Code.Callvirt:
                LowerCallvirt((MethodReference)instr.Operand, stack);
                break;
            case Code.Newobj:
                LowerNewobj((MethodReference)instr.Operand, stack);
                break;
            case Code.Ldftn:
            {
                var target =
                    ((MethodReference)instr.Operand).Resolve()
                    ?? throw new CilNotSupportedException(
                        $"cannot resolve 'ldftn' target in '{_method.FullName}'."
                    );
                // A placeholder value that is never itself emitted as real IR — see LowerNewobj, which
                // consumes it purely to look up the method it stands for in _ldftnProvenance.
                var placeholder = IrBuilder.ConstInt(IrType.I16, 0);
                _ldftnProvenance[placeholder] = target;
                stack.Add(placeholder);
                break;
            }
            case Code.Ldvirtftn:
            {
                // A method-group delegate off a virtual method (`Func<T> f = obj.Method;`): unlike
                // `ldftn`, `ldvirtftn`'s whole point is RUNTIME dispatch through the receiver it also
                // pops here (Roslyn's own emitted shape always `dup`s the env first, so the outer
                // delegate `newobj` still gets its copy — see LowerNewobj's remarks). The instruction's
                // own operand only names the vtable SLOT (e.g. `Base::Get`), not the receiver's actual
                // runtime override — binding that token statically would silently invoke the wrong
                // method whenever the receiver's concrete type differs from the token's declaring
                // type. Exactly the same guard LowerInstanceCall already applies to a `callvirt` that
                // can't be devirtualized: resolvable only when the target is non-virtual, final, or
                // its declaring type is sealed (task 3 adds concrete-type tracking to do better).
                Pop(stack);
                var target =
                    ((MethodReference)instr.Operand).Resolve()
                    ?? throw new CilNotSupportedException(
                        $"cannot resolve 'ldvirtftn' target in '{_method.FullName}'."
                    );
                if (target.IsVirtual && !target.IsFinal && !target.DeclaringType.IsSealed)
                    throw new CilNotSupportedException(
                        $"'ldvirtftn' on '{target.FullName}' in '{_method.FullName}' cannot be "
                            + "resolved to a single target (its vtable-slot operand only identifies "
                            + "the declared method, not the receiver's actual runtime override; "
                            + "devirtualization needs a sealed declaring type or a final method; an "
                            + "indirect-call backend is out of scope)."
                    );
                var placeholder = IrBuilder.ConstInt(IrType.I16, 0);
                _ldftnProvenance[placeholder] = target;
                stack.Add(placeholder);
                break;
            }
            case Code.Ldfld:
            {
                var fieldRef = (FieldReference)instr.Operand;
                var objRef = Pop(stack);
                var (ptr, _, signed, nested) = FieldPointer(fieldRef, objRef);
                // A struct-typed field is never loaded as a scalar — its "value" IS its address (see
                // CilMethodLowerer.Structs.cs's remarks); a real independent copy happens wherever that
                // address is eventually consumed (stloc/stfld/stobj/a byval call argument).
                if (nested is null)
                {
                    var loaded = WidenToStack(_b.Load(ptr), signed);
                    if (FloatKindOfType(fieldRef.FieldType) is { } fk)
                        TagFloat(loaded, fk);
                    stack.Add(loaded);
                }
                else
                {
                    stack.Add(ptr);
                }
                break;
            }
            case Code.Ldflda:
            {
                var fieldRef = (FieldReference)instr.Operand;
                var objRef = Pop(stack);
                var (ptr, _, _, _) = FieldPointer(fieldRef, objRef);
                stack.Add(ptr);
                break;
            }
            case Code.Stfld:
            {
                var fieldRef = (FieldReference)instr.Operand;
                var value = Pop(stack);
                var objRef = Pop(stack);
                var (ptr, fieldType, _, nested) = FieldPointer(fieldRef, objRef);
                if (nested is not null)
                    EmitCopy(ptr, value, nested.Size);
                else
                    _b.Store(CoerceStore(value, fieldType), ptr);
                break;
            }
            case Code.Ldnull:
                stack.Add(IrBuilder.ConstInt(IrType.Pointer(IrType.I8), 0));
                break;

            // ---- Static fields (see CilMethodLowerer.Statics.cs) ------------------------------
            case Code.Ldsfld:
            {
                var field = ResolveStaticField((FieldReference)instr.Operand);
                var value = LoadStaticField(field);
                if (FloatKindOfType(field.FieldType) is { } fk)
                    TagFloat(value, fk);
                stack.Add(value);
                break;
            }
            case Code.Ldsflda:
            {
                var fieldRef = (FieldReference)instr.Operand;
                // A compiler-generated RVA blob field (Roslyn's own storage for a `"..."u8` literal's
                // bytes — see CilMethodLowerer.Delegates.cs's TryLowerSpanCall) is addressed directly by
                // 'ldsflda', never via 'ldtoken' (that's the OTHER Roslyn idiom — an array literal's
                // RuntimeHelpers.InitializeArray call, see CilMethodLowerer.Arrays.cs's
                // DetectArrayLiteralIdioms). It must be intercepted here, before ResolveStaticField/
                // StaticFieldAddress: a blob field has no '.cctor' store for CilStaticFieldSupport.Collect
                // to ever see, so the ordinary static-field machinery would otherwise hand it a
                // zero-initialized WRAM holder with none of its real (compile-time-constant) content —
                // see CilLoweringContext.EnsureRvaBlobGlobal's remarks. Identified structurally (non-empty
                // InitialValue, i.e. Cecil's own HasFieldRVA-backed accessor), not by the
                // '<PrivateImplementationDetails>' name Roslyn happens to use today.
                if (fieldRef.Resolve() is { InitialValue.Length: > 0 } blobField)
                {
                    var blob = _ctx.EnsureRvaBlobGlobal(blobField);
                    var blobPtr = IrBuilder.GlobalRef(blob);
                    _pendingArrayInfo[blobPtr] = (
                        IrType.I8,
                        false,
                        IrBuilder.ConstInt(IrType.I16, blobField.InitialValue.Length)
                    );
                    stack.Add(blobPtr);
                    break;
                }
                var field = ResolveStaticField(fieldRef);
                stack.Add(StaticFieldAddress(field));
                break;
            }
            case Code.Stsfld:
            {
                var field = ResolveStaticField((FieldReference)instr.Operand);
                var value = Pop(stack);
                StoreStaticField(field, value);
                break;
            }

            // ---- Value types (see CilMethodLowerer.Structs.cs) ---------------------------------
            case Code.Ldobj:
                LowerLdobj((TypeReference)instr.Operand, stack);
                break;
            case Code.Stobj:
                LowerStobj((TypeReference)instr.Operand, stack);
                break;
            case Code.Initobj:
                LowerInitobj((TypeReference)instr.Operand, stack);
                break;
            case Code.Cpobj:
                LowerCpobj((TypeReference)instr.Operand, stack);
                break;

            // ---- SZ-arrays (see CilMethodLowerer.Arrays.cs) ------------------------------------
            case Code.Newarr:
                if (_newarrLiteralGlobal.TryGetValue(instr, out var literalArray))
                    LowerNewarrLiteral(literalArray, stack);
                else
                    LowerNewarr((TypeReference)instr.Operand, stack);
                break;
            case Code.Stelem_I1:
                StoreElem(stack, IrType.I8);
                break;
            case Code.Stelem_I2:
                StoreElem(stack, IrType.I16);
                break;
            case Code.Stelem_I4:
                StoreElem(stack, IrType.I32);
                break;
            case Code.Ldelem_I1:
                LoadElem(stack, IrType.I8, signed: true);
                break;
            case Code.Ldelem_U1:
                LoadElem(stack, IrType.I8, signed: false);
                break;
            case Code.Ldelem_I2:
                LoadElem(stack, IrType.I16, signed: true);
                break;
            case Code.Ldelem_U2:
                LoadElem(stack, IrType.I16, signed: false);
                break;
            case Code.Ldelem_I4:
                LoadElem(stack, IrType.I32, signed: true);
                break;
            case Code.Ldelem_U4:
                LoadElem(stack, IrType.I32, signed: false);
                break;
            // The generic (type-operand) variants — Roslyn emits these for a struct element (a
            // primitive element always uses one of the typed opcodes above).
            case Code.Ldelema:
                LowerLdelema((TypeReference)instr.Operand, stack);
                break;
            case Code.Ldelem_Any:
                LowerLdelemAny((TypeReference)instr.Operand, stack);
                break;
            case Code.Stelem_Any:
                LowerStelemAny((TypeReference)instr.Operand, stack);
                break;
            case Code.Ldlen:
                LowerLdlen(stack);
                break;
            // A pure verification hint (suppresses the array-covariance check 'ldelema' would
            // otherwise need on a reference-type array — irrelevant to a struct element array, and to
            // this frontend's own unchecked element addressing either way): a complete no-op.
            case Code.Readonly:
                break;

            // ---- try/finally (see CilMethodLowerer.Iterators.cs) ------------------------------
            case Code.Leave:
            case Code.Leave_S:
            {
                // 'leave' always empties the CIL evaluation stack (ECMA-335 III.3.36) — never a
                // Deliver here, unlike every other branch above.
                var targetBlock = _blockOf[(Instruction)instr.Operand];
                if (FindEnclosingFinally(instr) is { } handler)
                {
                    var finallyEntry = _function.AppendBlock("finally.entry");
                    _b.Br(finallyEntry);
                    _b.PositionAtEnd(finallyEntry);
                    LowerFinallyBody(handler, targetBlock);
                }
                else
                {
                    _b.Br(targetBlock);
                }
                break;
            }

            case Code.Ret:
                if (_function.ReturnType.Kind == IrTypeKind.Void)
                    _b.Ret();
                else
                {
                    var retValue = Pop(stack);
                    // A LINQ chain placeholder (see CilMethodLowerer.Linq.cs) escaping without a
                    // terminal is never valid IR to build (it's a never-materialized I16 constant, not a
                    // real value) — diagnose it here explicitly rather than letting it reach CoerceStore/
                    // IrVerifier as an opaque type-mismatch.
                    if (_pendingLinqPipeline.ContainsKey(retValue))
                        throw new CilNotSupportedException(
                            $"LINQ pipeline in '{_method.FullName}' is returned without a terminal "
                                + "operation (phase supports Where/Select pipelines ending in "
                                + "Sum/Count/Any/All only)."
                        );
                    // Record this ret site's concrete-type provenance (or its absence) for the callee-
                    // side devirtualization the design spike's iterator finding needs — see
                    // CilMethodLowerer.Iterators.cs's remarks on CilLoweringContext.RecordConcreteReturn.
                    _ctx.RecordConcreteReturn(
                        _method,
                        _pendingConcreteType.TryGetValue(retValue, out var retType) ? retType : null
                    );
                    _b.Ret(CoerceStore(retValue, _function.ReturnType));
                }
                break;

            default:
                throw new CilNotSupportedException(
                    $"unsupported CIL opcode '{instr.OpCode.Name}' in '{_method.FullName}' "
                        + "(phase 1 opcode subset)."
                );
        }
    }

    private void BinaryOp(List<IrValue> stack, IrBinaryOp op)
    {
        var b = Pop(stack);
        var a = Pop(stack);
        if (!a.Type.StructurallyEquals(b.Type))
            b = CoerceStore(b, a.Type);
        stack.Add(_b.Binary(op, a, b));
    }

    /// <summary><c>add</c>/<c>sub</c> where either operand is a pointer never reaches
    /// <see cref="IrBuilder.Binary"/> (<see cref="IrVerifier"/> requires integer operands) — real
    /// pointer arithmetic instead. C#'s own compiler already scales a <c>T*</c> offset to bytes ahead
    /// of the CIL <c>add</c>/<c>sub</c> (an explicit <c>sizeof</c>/<c>conv.i</c>/<c>mul</c> sequence for
    /// any <c>T</c> wider than a byte — the raw opcode operates on two already-byte-scaled native
    /// ints), so a byte-granularity <c>gep</c> (element type i8) reproduces it exactly without needing
    /// to know <c>T</c>'s size here at all. <c>ptr - ptr</c> (byte distance; C# then divides by
    /// <c>sizeof(T)</c> as ordinary, separately-lowered int arithmetic) is the one shape that yields a
    /// scalar rather than an address.</summary>
    private void AddOrSub(List<IrValue> stack, bool subtract)
    {
        var b = Pop(stack);
        var a = Pop(stack);
        if (a.Type.Kind == IrTypeKind.Pointer)
        {
            if (b.Type.Kind == IrTypeKind.Pointer)
            {
                var ai = _b.Conv(IrConvOp.Bitcast, a, IrType.Int(a.Type.SizeInBits));
                var bi = _b.Conv(IrConvOp.Bitcast, b, IrType.Int(b.Type.SizeInBits));
                stack.Add(_b.Sub(ai, bi));
                return;
            }
            var offset = CoerceStore(b, IrType.I16);
            if (subtract)
                offset = _b.Sub(IrBuilder.ConstInt(IrType.I16, 0), offset);
            var addr = _b.Gep(a, offset, IrType.I8);
            // Reinterpret back to the original pointee type ONLY when it actually differs (a byte
            // pointer's own gep already comes out as Pointer(I8) == its own type, so wrapping it in a
            // no-op bitcast here would needlessly hide the gep from the backend's single-use fusion
            // (see Sm83Backend.MemoryEmitter's FusedGep) behind an extra materialized value).
            stack.Add(
                addr.Type.StructurallyEquals(a.Type)
                    ? addr
                    : _b.Conv(IrConvOp.Bitcast, addr, a.Type)
            );
            return;
        }
        if (b.Type.Kind == IrTypeKind.Pointer)
        {
            // int/native-int + pointer (commutative form, e.g. `n + p`) — same byte-scaled gep, base
            // and offset swapped.
            var offset = CoerceStore(a, IrType.I16);
            var addr = _b.Gep(b, offset, IrType.I8);
            stack.Add(
                addr.Type.StructurallyEquals(b.Type)
                    ? addr
                    : _b.Conv(IrConvOp.Bitcast, addr, b.Type)
            );
            return;
        }
        if (!a.Type.StructurallyEquals(b.Type))
            b = CoerceStore(b, a.Type);
        stack.Add(_b.Binary(subtract ? IrBinaryOp.Sub : IrBinaryOp.Add, a, b));
    }

    private void ShiftOp(List<IrValue> stack, IrBinaryOp op)
    {
        var count = Pop(stack);
        var value = Pop(stack);
        if (!count.Type.StructurallyEquals(value.Type))
            count = CoerceStore(count, value.Type);
        stack.Add(_b.Binary(op, value, count));
    }

    private void CompareOp(List<IrValue> stack, IrCompareOp op)
    {
        var b = AsComparable(Pop(stack));
        var a = AsComparable(Pop(stack));
        if (!a.Type.StructurallyEquals(b.Type))
            b = CoerceStore(b, a.Type);
        var cmp = _b.Compare(op, a, b);
        stack.Add(_b.Conv(IrConvOp.ZExt, cmp, IrType.I32));
    }

    /// <summary>
    /// System.Int128/UInt128 arithmetic/comparison/conversion: unlike every primitive width, IL never
    /// has a native opcode for these (ECMA-335 predates 128-bit integers) — Roslyn instead emits a
    /// STATIC call to the type's own operator method (<c>op_Addition</c>, <c>op_LessThan</c>,
    /// <c>op_Implicit</c>, …), confirmed against a real Cecil dump of <c>a + b</c>/<c>(long)i128Value</c>/
    /// etc. (see the design task). This intercepts exactly that call shape and lowers it to the SAME
    /// generic <see cref="IrBinaryOp"/>/<see cref="IrCompareOp"/>/<see cref="IrConvOp"/> machinery every
    /// OTHER width already uses (<see cref="AddOrSub"/>/<see cref="BinaryOp"/>/<see cref="ShiftOp"/>/
    /// <see cref="CompareOp"/> above are width-agnostic — they operate on whatever <see cref="IrType"/>
    /// the popped operands already carry) — CLAUDE.md's "Koh C# subset" already routes i32/i64/i128
    /// arithmetic through the SM83 backend's generic width-N memory routines, so 128-bit width itself
    /// needed no NEW backend work, only this call-site interception plus <see cref="CilTypeMapper.Map"/>'s
    /// matching Int128/UInt128 type mapping. Placed ahead of the LINQ/BCL-method checks in
    /// <see cref="LowerCall"/> — an Int128/UInt128 operator method is real BCL IL this frontend could
    /// never lower (its actual implementation is a runtime intrinsic), so it must never reach that path.
    /// </summary>
    private bool TryLowerInt128Operator(MethodReference calleeRef, List<IrValue> stack)
    {
        var declaring = calleeRef.DeclaringType.FullName;
        if (declaring != "System.Int128" && declaring != "System.UInt128")
            return false;
        if (!calleeRef.Name.StartsWith("op_", StringComparison.Ordinal))
            return false;
        var signed = declaring == "System.Int128";

        switch (calleeRef.Name)
        {
            case "op_Addition":
                AddOrSub(stack, subtract: false);
                return true;
            case "op_Subtraction":
                AddOrSub(stack, subtract: true);
                return true;
            case "op_Multiply":
                BinaryOp(stack, IrBinaryOp.Mul);
                return true;
            case "op_Division":
                BinaryOp(stack, signed ? IrBinaryOp.SDiv : IrBinaryOp.UDiv);
                return true;
            case "op_Modulus":
                BinaryOp(stack, signed ? IrBinaryOp.SRem : IrBinaryOp.URem);
                return true;
            case "op_BitwiseAnd":
                BinaryOp(stack, IrBinaryOp.And);
                return true;
            case "op_BitwiseOr":
                BinaryOp(stack, IrBinaryOp.Or);
                return true;
            case "op_ExclusiveOr":
                BinaryOp(stack, IrBinaryOp.Xor);
                return true;
            case "op_LeftShift":
                ShiftOp(stack, IrBinaryOp.Shl);
                return true;
            case "op_RightShift":
            case "op_UnsignedRightShift":
                ShiftOp(
                    stack,
                    signed && calleeRef.Name == "op_RightShift" ? IrBinaryOp.AShr : IrBinaryOp.LShr
                );
                return true;
            case "op_UnaryNegation":
            {
                var a = Pop(stack);
                stack.Add(_b.Sub(IrBuilder.ConstInt(a.Type, 0), a));
                return true;
            }
            case "op_OnesComplement":
            {
                var a = Pop(stack);
                stack.Add(_b.Binary(IrBinaryOp.Xor, a, IrBuilder.ConstInt(a.Type, -1)));
                return true;
            }
            case "op_UnaryPlus":
                return true; // identity — leave the operand exactly as-is on the stack.
            case "op_Equality":
                CompareOp(stack, IrCompareOp.Eq);
                return true;
            case "op_Inequality":
                CompareOp(stack, IrCompareOp.Ne);
                return true;
            case "op_LessThan":
                CompareOp(stack, signed ? IrCompareOp.Slt : IrCompareOp.Ult);
                return true;
            case "op_LessThanOrEqual":
                CompareOp(stack, signed ? IrCompareOp.Sle : IrCompareOp.Ule);
                return true;
            case "op_GreaterThan":
                CompareOp(stack, signed ? IrCompareOp.Sgt : IrCompareOp.Ugt);
                return true;
            case "op_GreaterThanOrEqual":
                CompareOp(stack, signed ? IrCompareOp.Sge : IrCompareOp.Uge);
                return true;
            case "op_Implicit":
            case "op_Explicit":
                LowerInt128Conversion(calleeRef, stack);
                return true;
            default:
                return false; // an unrecognized Int128/UInt128 member (e.g. a named method like
            // 'Parse') falls through to the ordinary BCL-method diagnostic in LowerCall.
        }
    }

    /// <summary>An Int128/UInt128 <c>op_Implicit</c>/<c>op_Explicit</c> conversion: widen/narrow the
    /// popped operand to the operator's declared RETURN type, using the operator's declared PARAMETER
    /// type's own signedness to choose sign- vs zero-extension (never the popped stack value's current
    /// physical width alone — a narrower-than-i32 parameter, e.g. an implicit conversion from <c>byte</c>,
    /// already arrived widened to i32 by the ordinary CIL stack-typing discipline; see
    /// <see cref="WidenToStack"/>'s remarks — so it is the DECLARED parameter type that carries the real
    /// signedness here, exactly as <see cref="IsSignedReturn"/> does for an ordinary call's return).
    /// Bit-identical when source and target are the same width (an Int128&lt;-&gt;UInt128 explicit
    /// conversion, or a round-trip through the same width) — signedness is not itself stored in
    /// <see cref="IrType"/>, so no conversion instruction is needed at all in that case.</summary>
    private void LowerInt128Conversion(MethodReference calleeRef, List<IrValue> stack)
    {
        var (targetType, targetSigned) = CilTypeMapper.Map(calleeRef.ReturnType);
        var (_, paramSigned) = CilTypeMapper.Map(calleeRef.Parameters[0].ParameterType);
        var v = Pop(stack);
        IrValue converted;
        if (v.Type.Bits == targetType.Bits)
            converted = v;
        else if (v.Type.Bits > targetType.Bits)
            converted = _b.Conv(IrConvOp.Trunc, v, targetType);
        else
            converted = _b.Conv(paramSigned ? IrConvOp.SExt : IrConvOp.ZExt, v, targetType);
        stack.Add(WidenToStack(converted, targetSigned));
    }

    /// <summary>A <c>call</c>: an instance call (always non-virtual by CIL definition — a base-ctor
    /// call, a <c>base.Method()</c> call, or a devirtualization-irrelevant direct instance call — see
    /// <see cref="LowerInstanceCall"/>), a <c>[KohIntrinsic]</c>-attributed method (Hardware/Gb — see
    /// <see cref="LowerIntrinsicCall"/>), a call to another static function of this same game module,
    /// or — out of scope — anything else, reported as a diagnostic rather than silently miscompiled.</summary>
    private void LowerCall(MethodReference calleeRef, List<IrValue> stack)
    {
        if (calleeRef.HasThis)
        {
            LowerInstanceCall(calleeRef, stack, isVirtualDispatch: false);
            return;
        }

        if (TryLowerInt128Operator(calleeRef, stack))
            return;

        if (IsLinqEnumerableCall(calleeRef))
        {
            LowerLinqCall(calleeRef, stack);
            return;
        }

        // System.Environment::get_CurrentManagedThreadId() -> a constant. Not a hardware address (see
        // CLAUDE.md's no-hardcoded-hardware-addresses rule for this frontend) — a runtime-semantics
        // substitution for a single-threaded target, so a lowered iterator's GetEnumerator reuse-vs-
        // fresh-copy thread check becomes ordinary, foldable code (see
        // CilMethodLowerer.Iterators.cs's remarks). Intercepted by name before Resolve() — the BCL
        // method's own body (an internal call) is never something this frontend could lower anyway.
        if (
            !calleeRef.HasThis
            && calleeRef.DeclaringType.FullName == "System.Environment"
            && calleeRef.Name == "get_CurrentManagedThreadId"
        )
        {
            stack.Add(IrBuilder.ConstInt(IrType.I32, 1));
            return;
        }

        // A call to a user generic method instantiated at concrete type arguments (Cecil exposes this
        // as a GenericInstanceMethod operand) — monomorphize on demand (see
        // CilMethodLowerer.Generics.cs). Placed AFTER the LINQ check above: Enumerable.Select/Where/…
        // are themselves generic BCL methods, so their own call sites are ALSO GenericInstanceMethod
        // operands and must keep routing through LowerLinqCall, never through generic monomorphization
        // (whose template-body lowering would try — and fail — to lower Enumerable's own unlowerable
        // BCL IL).
        if (calleeRef is GenericInstanceMethod gim)
        {
            LowerGenericCall(gim, stack);
            return;
        }

        MethodDefinition? def;
        try
        {
            def = calleeRef.Resolve();
        }
        catch (AssemblyResolutionException)
        {
            def = null;
        }
        if (def is null)
            throw new CilNotSupportedException($"cannot resolve call to '{calleeRef.FullName}'.");

        var argCount = calleeRef.Parameters.Count;
        var args = new IrValue[argCount];
        for (var i = argCount - 1; i >= 0; i--)
            args[i] = Pop(stack);

        if (_ctx.Intrinsics.TryGetValue(def, out var entry))
        {
            LowerIntrinsicCall(def, entry, args, stack);
            return;
        }

        if (!_ctx.FunctionsByMethod.TryGetValue(def, out var callee))
        {
            // A static method reached for the first time: either a game-module method this eager
            // sweep hasn't seen yet (shouldn't happen — Pass 1 signs every one up front — but handled
            // uniformly rather than assumed), or — the point of the referenced-assembly task (see
            // docs/superpowers/specs/2026-07-14-cil-frontend-design.md, task 2) — a method from a
            // REFERENCED assembly (Koh.GameBoy's Hal, Mem.Copy/Fill, …): lower it on demand,
            // transitively, exactly like CilLoweringContext.EnsureLowered already does for an instance
            // call/delegate target/constructor. The BCL is excluded explicitly (with its own message,
            // rather than falling into EnsureLowered's own diagnostic) so this, the one call shape that
            // newly reaches arbitrary referenced code, never attempts to lower unlowerable framework
            // internals no user code could ever reach anyway (the LINQ spike proved why: a BCL method's
            // IL is not written for this frontend's opcode subset).
            if (CilModuleLowerer.IsBclMethod(def))
                throw new CilNotSupportedException(
                    $"call to unsupported BCL method '{def.FullName}' (the CIL frontend cannot lower "
                        + "Base Class Library IL; only [KohIntrinsic] members, game-module static "
                        + "methods, and code from a referenced non-BCL assembly are supported)."
                );
            callee =
                _ctx.EnsureLowered(def)
                ?? throw new CilNotSupportedException(
                    $"cannot lower referenced method '{def.FullName}'."
                );
        }

        // The stack's own widen-narrow-to-i32 discipline (see class remarks) means an argument
        // narrower than i32 arrives here already widened; narrow it back to the callee's declared
        // parameter type (e.g. i8 for a byte parameter) before the call, matching what stloc/starg
        // do for locals/arguments and what the real CLR calling convention truncates on entry. A
        // byval struct argument additionally gets its own independent copy here — see
        // CilMethodLowerer.Structs.cs's PrepareArg.
        for (var i = 0; i < args.Length; i++)
            args[i] = PrepareArg(args[i], def.Parameters, i, callee.Parameters[i].Type);
        var call = _b.Call(callee, args);
        if (callee.ReturnType.Kind != IrTypeKind.Void)
        {
            var result = WidenToStack(call, IsSignedReturn(def));
            // A float/double-returning callee's result is tagged the same way a local/arg/field read
            // is (see CilMethodLowerer.Floats.cs) — the callee's declared return type, so a caller
            // chaining more float arithmetic onto this call's result routes correctly.
            if (FloatKindOfType(def.ReturnType) is { } floatKind)
                TagFloat(result, floatKind);
            // Thread this static call's callee-inferred concrete return type (see
            // CilLoweringContext.GetConcreteReturnType) onto the pushed result, so a subsequent
            // callvirt through an interface-typed result (the iterator kickoff's own shape) can
            // devirtualize — see CilMethodLowerer.Iterators.cs.
            if (_ctx.GetConcreteReturnType(def) is { } concreteType)
                _pendingConcreteType[result] = concreteType;
            stack.Add(result);
        }
    }

    private static bool IsSignedReturn(MethodDefinition def) =>
        CilTypeMapper.Map(def.ReturnType).Signed;

    private void LowerIntrinsicCall(
        MethodDefinition def,
        CilIntrinsicIndex.Entry entry,
        IReadOnlyList<IrValue> args,
        List<IrValue> stack
    )
    {
        switch (entry.Kind)
        {
            case "register" when def.IsSetter:
                _b.Store(
                    CoerceStore(args[0], IrType.I8),
                    IrBuilder.GlobalRef(RegisterGlobal(entry.Address))
                );
                break;
            case "register" when def.IsGetter:
                stack.Add(
                    _b.Conv(
                        IrConvOp.ZExt,
                        _b.Load(IrBuilder.GlobalRef(RegisterGlobal(entry.Address))),
                        IrType.I32
                    )
                );
                break;
            case "region":
                stack.Add(IrBuilder.GlobalRef(RegionGlobal(entry.Address)));
                break;
            case "ei":
                _b.Intrinsic("ei");
                break;
            case "di":
                _b.Intrinsic("di");
                break;
            case "halt":
                _b.Intrinsic("halt");
                break;
            case "nop":
                _b.Intrinsic("nop");
                break;
            case "stop":
                _b.Intrinsic("stop");
                break;
            // Arena allocator: Mem.Alloc(size) bumps the shared heap pointer (CilLoweringContext.
            // EnsureHeapGlobal — same global/convention `new` uses) down by size and returns the new
            // pointer as a byte*; Mem.Reset() restores it to HeapTop, freeing every allocation at once.
            // The heap global is guaranteed non-null here: NeedsHeap's pre-scan (CilModuleLowerer.Lower)
            // treats any call resolving to an "alloc"/"heapreset" intrinsic the same as a `newobj`/
            // `newarr`, so EnsureHeapGlobal has already run — and been seeded in the entry prologue —
            // before any method body (including this call site) lowers.
            case "alloc":
            {
                var heap = IrBuilder.GlobalRef(_ctx.HeapGlobal!);
                var size = CoerceStore(args[0], IrType.I16);
                var updated = _b.Binary(IrBinaryOp.Sub, _b.Load(heap), size);
                _b.Store(updated, heap);
                stack.Add(_b.Conv(IrConvOp.Bitcast, updated, IrType.Pointer(IrType.I8)));
                break;
            }
            case "heapreset":
                _b.Store(
                    IrBuilder.ConstInt(IrType.I16, CilLoweringContext.HeapTop),
                    IrBuilder.GlobalRef(_ctx.HeapGlobal!)
                );
                break;
            // OAM DMA: OAM DMA locks the bus to everything but HRAM for ~161 M-cycles, so the
            // trigger+wait cannot run from ROM — the backend installs a fixed HRAM trampoline once, at
            // boot, and this call becomes: stage the source page in a dedicated WRAM scratch global (the
            // trampoline reads it back by its known address), then CALL the trampoline (see
            // CilLoweringContext.EnsureOamDmaSourceGlobal's remarks and Sm83Backend's "oamdma" gating).
            case "oamdma":
                _b.Store(
                    CoerceStore(args[0], IrType.I8),
                    IrBuilder.GlobalRef(_ctx.EnsureOamDmaSourceGlobal())
                );
                _b.Intrinsic("oamdma");
                break;
            default:
                throw new CilNotSupportedException(
                    $"unsupported [KohIntrinsic] kind '{entry.Kind}' on '{def.FullName}' (phase 1 "
                        + "supports register/region/ei/di/halt/nop/stop/alloc/heapreset/oamdma only)."
                );
        }
    }

    private IrGlobal RegisterGlobal(int address) => _ctx.RegisterGlobal(address);

    private IrGlobal RegionGlobal(int address) => _ctx.RegionGlobal(address);
}
