using Koh.Compiler.Ir;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Koh.Compiler.Frontends.Cil;

/// <summary>
/// Value-type structs and raw pointers (see
/// <c>docs/superpowers/specs/2026-07-14-cil-frontend-design.md</c>'s struct/pointer task):
/// <c>ldobj</c>/<c>stobj</c>/<c>initobj</c>/<c>cpobj</c>/<c>ldflda</c>, struct locals/params (nested,
/// arrays-of, whole-copy), <c>ldind.*</c>/<c>stind.*</c>, <c>localloc</c>, and byref (ref/out/in)
/// parameters.
///
/// A struct value is ALWAYS represented on the simulated CIL stack by the address of its bytes —
/// never materialized as a scalar <see cref="IrValue"/> (no <see cref="IrTypeKind.Struct"/> is
/// constructible; see <see cref="IrType"/>'s remarks) — exactly the same representation
/// <c>CSharpFrontend</c> uses for its own struct locals/fields. Every instruction that *reads* a
/// struct (<c>ldloc</c>/<c>ldarg</c>/<c>ldfld</c>/<c>ldobj</c>/<c>dup</c> of a struct-typed place) is
/// therefore a no-op that just re-pushes that address; a REAL, independent byte copy (<see
/// cref="EmitCopy"/>) happens only where a struct value is actually consumed into a distinct
/// destination — a local/arg/field write (<c>stloc</c>/<c>starg</c>/<c>stfld</c>/<c>stobj</c>/
/// <c>cpobj</c>), or a byval call argument (see <see cref="PrepareArg"/>). Because every consumer of a
/// struct value already knows the destination's OWN declared type (a local's <see cref="VariableDefinition.VariableType"/>,
/// a field's <see cref="FieldReference"/>, an instruction's explicit type operand), no provenance
/// side-table (unlike the array/delegate/concrete-type tables elsewhere in this frontend) is needed to
/// know how many bytes to copy or where.
///
/// Struct RETURN by value lowers via the static-slot convention (<see cref="CilLoweringContext.EnsureSignature"/>:
/// the callee becomes void and its <c>Ret</c> site copies into a per-function WRAM return slot; the
/// call site immediately copies the slot into a fresh caller-frame buffer and pushes its address —
/// see <c>TryEmitSretCall</c>, including why a hidden result-POINTER parameter was rejected: a
/// recursive callee's frame restore clobbers anything written into the caller's frame). This
/// replaced the original diagnostic as enabler E1 of the ideal-game-API program
/// (<c>docs/superpowers/specs/2026-07-19-ideal-game-api-design.md</c>). A struct parameter,
/// unlike <c>CSharpFrontend</c> (which requires ref/in/out for every struct parameter — a Koh-C#-only
/// restriction), supports genuine byval passing here: real compiled C# routinely passes small structs
/// by value, and this frontend accepts standard C# semantics (see the design doc's whole premise) — a
/// byval struct argument gets a fresh, independent copy at the call site (see
/// <see cref="PrepareArg"/>), matching the real CLR's own by-value argument semantics exactly (not a
/// shortcut).
/// </summary>
internal sealed partial class CilMethodLowerer
{
    // A struct-typed local's/param's storage: the address of its byte buffer (a Gep of an
    // Array(I8,size) alloca, or — for a byref/byval struct parameter — the incoming pointer value
    // itself) plus its layout (offsets + nested shapes, for field access and whole-copy sizing).
    private readonly Dictionary<
        VariableDefinition,
        (IrValue Base, CilClassLayout Layout)
    > _structLocals = new();
    private readonly Dictionary<
        ParameterDefinition,
        (IrValue Base, CilClassLayout Layout)
    > _structParams = new();

    // ---- Local/argument plumbing (wired from CilMethodLowerer.cs's Run/LoadLocal/StoreLocal/LoadArg/
    // StoreArg/Ldloca) --------------------------------------------------------------------------------

    /// <summary>Declare one method-body local: a struct-typed variable gets a byte-buffer <c>alloca</c>
    /// (never auto-zeroed — matching every other local in this frontend, relying on the source
    /// program's own <c>initobj</c>/constructor call before first read, exactly like C#'s definite-
    /// assignment guarantee); anything else falls back to the ordinary scalar path.</summary>
    private void DeclareLocal(VariableDefinition v)
    {
        // Subst is a no-op outside a generic instantiation (_genericArgs null) — see
        // CilMethodLowerer.Generics.cs. A generic template's own local variable types still name its
        // open type parameter ('!!0') directly off Cecil metadata, so this must run BEFORE either
        // struct-resolution or the ordinary scalar Map below.
        var varType = Subst(v.VariableType);
        if (CilStructSupport.ResolveStruct(varType) is { } structDef)
        {
            var layout = _ctx.GetLayout(structDef);
            var storage = _b.Alloca(IrType.Array(IrType.I8, Math.Max(layout.Size, 1)));
            var basePtr = _b.Gep(storage, IrBuilder.ConstInt(IrType.I16, 0), IrType.I8);
            _structLocals[v] = (basePtr, layout);
            return;
        }
        var (irType, signed) = CilTypeMapper.Map(varType);
        var alloca = _b.Alloca(irType);
        _locals[v] = (alloca, irType, signed);
    }

    /// <summary>Declare one parameter's storage: a struct-typed parameter (byval or byref — both are
    /// just an address by the time they reach the callee; see this file's class remarks) is used
    /// directly, with no wrapping <c>alloca</c> (mirrors <c>CSharpFrontend</c>'s own struct-parameter
    /// handling: "the param value is the struct's address"); anything else falls back to the ordinary
    /// scalar path (a byref scalar param's incoming value is itself already an address — see
    /// <see cref="CilTypeMapper.Map(TypeReference)"/>'s <c>ByReferenceType</c> handling — so the
    /// generic <c>alloca</c>+store here just gives it an ordinary mutable local slot to round-trip
    /// through, exactly as for any other parameter; every dereference is explicit <c>ldind</c>/
    /// <c>stind</c> in the IL itself).</summary>
    private void DeclareParam(ParameterDefinition p, IrValue incoming)
    {
        // Subst — see DeclareLocal's remarks just above; same reasoning for a template's own parameter
        // type ('!!0').
        var shape = CilTypeMapper.MapParam(Subst(p.ParameterType));
        if (shape.StructType is { } structDef)
        {
            _structParams[p] = (incoming, _ctx.GetLayout(structDef));
            return;
        }
        var alloca = _b.Alloca(shape.IrType);
        _b.Store(incoming, alloca);
        _params[p] = (alloca, shape.IrType, shape.Signed);
    }

    private bool TryLoadStructLocal(VariableDefinition v, out IrValue address)
    {
        if (_structLocals.TryGetValue(v, out var s))
        {
            address = s.Base;
            return true;
        }
        address = null!;
        return false;
    }

    private bool TryStoreStructLocal(VariableDefinition v, IrValue value)
    {
        if (!_structLocals.TryGetValue(v, out var s))
            return false;
        EmitCopy(s.Base, value, s.Layout.Size);
        return true;
    }

    private bool TryLoadStructArg(ParameterDefinition p, out IrValue address)
    {
        if (_structParams.TryGetValue(p, out var s))
        {
            address = s.Base;
            return true;
        }
        address = null!;
        return false;
    }

    private bool TryStoreStructArg(ParameterDefinition p, IrValue value)
    {
        if (!_structParams.TryGetValue(p, out var s))
            return false;
        EmitCopy(s.Base, value, s.Layout.Size);
        return true;
    }

    /// <summary>The address of local <paramref name="v"/> — its struct byte buffer, or its ordinary
    /// scalar <c>alloca</c> — for <c>ldloca</c>.</summary>
    private IrValue AddressOfLocal(VariableDefinition v) =>
        _structLocals.TryGetValue(v, out var s) ? s.Base : _locals[v].Alloca;

    /// <summary>The address of parameter <paramref name="p"/> — its struct storage, or its ordinary
    /// scalar <c>alloca</c> — for <c>ldarga</c>.</summary>
    private IrValue AddressOfArg(ParameterDefinition p) =>
        _structParams.TryGetValue(p, out var s) ? s.Base : _params[p].Alloca;

    // ---- ldobj/stobj/initobj/cpobj --------------------------------------------------------------

    private void LowerLdobj(TypeReference typeRef, List<IrValue> stack)
    {
        var addr = Pop(stack);
        if (CilStructSupport.ResolveStruct(typeRef) is not null)
        {
            // The struct "value" IS its address on our simulated stack (see class remarks) — ldobj on
            // a struct type is therefore a pure pass-through.
            stack.Add(addr);
            return;
        }
        var (irType, signed) = CilTypeMapper.Map(typeRef);
        stack.Add(WidenToStack(_b.Load(AsPointerTo(addr, irType)), signed));
    }

    private void LowerStobj(TypeReference typeRef, List<IrValue> stack)
    {
        var value = Pop(stack);
        var addr = Pop(stack);
        if (CilStructSupport.ResolveStruct(typeRef) is { } structDef)
        {
            EmitCopy(addr, value, _ctx.GetLayout(structDef).Size);
            return;
        }
        var (irType, _) = CilTypeMapper.Map(typeRef);
        _b.Store(CoerceStore(value, irType), AsPointerTo(addr, irType));
    }

    private void LowerInitobj(TypeReference typeRef, List<IrValue> stack)
    {
        var addr = Pop(stack);
        EmitZeroFill(AsPointerTo(addr, IrType.I8), SizeOfOperandType(typeRef));
    }

    private void LowerCpobj(TypeReference typeRef, List<IrValue> stack)
    {
        var src = Pop(stack);
        var dst = Pop(stack);
        EmitCopy(dst, src, SizeOfOperandType(typeRef));
    }

    /// <summary>Byte size of a type named by an <c>ldobj</c>/<c>stobj</c>/<c>initobj</c>/<c>cpobj</c>
    /// operand — a struct's own laid-out size, or an ordinary scalar's <see cref="IrType.SizeInBytes"/>.</summary>
    private int SizeOfOperandType(TypeReference typeRef)
    {
        if (CilStructSupport.ResolveStruct(typeRef) is { } structDef)
            return Math.Max(_ctx.GetLayout(structDef).Size, 1);
        var (irType, _) = CilTypeMapper.Map(typeRef);
        return Math.Max(irType.SizeInBytes, 1);
    }

    // ---- ldind.*/stind.* --------------------------------------------------------------------------

    private void LoadIndirect(List<IrValue> stack, IrType type, bool signed)
    {
        var addr = Pop(stack);
        stack.Add(WidenToStack(_b.Load(AsPointerTo(addr, type)), signed));
    }

    private void StoreIndirect(List<IrValue> stack, IrType type)
    {
        var value = Pop(stack);
        var addr = Pop(stack);
        _b.Store(CoerceStore(value, type), AsPointerTo(addr, type));
    }

    /// <summary>Reinterpret <paramref name="addr"/> (a pointer of any pointee, or an address-width
    /// integer from <c>conv.i</c>/<c>conv.u</c>) as a pointer to <paramref name="pointee"/> — a no-op
    /// when it already is one.</summary>
    private IrValue AsPointerTo(IrValue addr, IrType pointee)
    {
        var target = IrType.Pointer(pointee);
        return addr.Type.StructurallyEquals(target)
            ? addr
            : _b.Conv(IrConvOp.Bitcast, addr, target);
    }

    // ---- localloc (stackalloc) ---------------------------------------------------------------------

    /// <summary><c>localloc</c>: reserve <c>n</c> bytes in the frame and yield a pointer to the first —
    /// same shape as <c>CSharpFrontend.MethodLowerer.LowerStackAlloc</c>. Only a compile-time constant
    /// size is supported (matches the C# frontend's own <c>stackalloc T[n]</c>, which requires a
    /// constant-foldable length); a dynamically-sized <c>localloc</c> is a diagnostic.</summary>
    private void LowerLocalloc(List<IrValue> stack)
    {
        var size = Pop(stack);
        if (size is not IrConstInt sizeConst || sizeConst.Value < 0)
            throw new CilNotSupportedException(
                $"'localloc' in '{_method.FullName}' needs a compile-time constant size (a "
                    + "dynamically-sized stackalloc is not supported)."
            );
        var storage = _b.Alloca(IrType.Array(IrType.I8, (int)sizeConst.Value));
        stack.Add(_b.Gep(storage, IrBuilder.ConstInt(IrType.I16, 0), IrType.I8));
    }

    // ---- Byte copy -----------------------------------------------------------------------------------

    /// <summary>Copy <paramref name="size"/> bytes from <paramref name="src"/> to <paramref name="dst"/>
    /// — the one mechanism behind every struct write (<c>stloc</c>/<c>starg</c>/<c>stfld</c>/
    /// <c>stobj</c>/<c>cpobj</c> of a struct-typed destination) and every byval struct call argument
    /// (<see cref="PrepareArg"/>). Unrolled at compile time (a fixed number of byte load/store pairs),
    /// exactly <c>CSharpFrontend.MethodLowerer.LowerAssignment</c>'s own whole-struct-copy loop.</summary>
    private void EmitCopy(IrValue dst, IrValue src, int size)
    {
        if (size <= 0)
            return;
        var dstBase = AsPointerTo(dst, IrType.I8);
        var srcBase = AsPointerTo(src, IrType.I8);
        for (var k = 0; k < size; k++)
        {
            var from = _b.Gep(srcBase, IrBuilder.ConstInt(IrType.I16, k), IrType.I8);
            var to = _b.Gep(dstBase, IrBuilder.ConstInt(IrType.I16, k), IrType.I8);
            _b.Store(_b.Load(from), to);
        }
    }

    // ---- Call-site argument preparation (wired from CilMethodLowerer.cs's LowerCall and
    // CilMethodLowerer.Delegates.cs's LowerInstanceCall/LowerNewobj) ----------------------------------

    /// <summary>Prepare one popped stack value as call argument <paramref name="i"/> of
    /// <paramref name="calleeParams"/> (the callee's own Cecil parameter list — user parameters only,
    /// no implicit <c>this</c>): a byval struct parameter gets an independent copy in a fresh temporary
    /// (real CLR by-value semantics — see this file's class remarks); a byref struct parameter, a byref
    /// scalar parameter, or a plain scalar parameter passes/coerces through unchanged.</summary>
    private IrValue PrepareArg(
        IrValue arg,
        Mono.Collections.Generic.Collection<ParameterDefinition> calleeParams,
        int i,
        IrType calleeIrType
    )
    {
        var paramType = calleeParams[i].ParameterType;
        if (
            paramType is not ByReferenceType
            && CilStructSupport.ResolveStruct(paramType) is { } structDef
        )
        {
            var layout = _ctx.GetLayout(structDef);
            var temp = _b.Alloca(IrType.Array(IrType.I8, Math.Max(layout.Size, 1)));
            var tempPtr = _b.Gep(temp, IrBuilder.ConstInt(IrType.I16, 0), IrType.I8);
            EmitCopy(tempPtr, arg, layout.Size);
            return tempPtr;
        }
        return CoerceStore(arg, calleeIrType);
    }
}
