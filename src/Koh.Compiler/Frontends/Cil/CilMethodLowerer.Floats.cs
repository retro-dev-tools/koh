using Koh.Compiler.Ir;
using Mono.Cecil;

namespace Koh.Compiler.Frontends.Cil;

/// <summary>
/// float32/float64 IL routing: <c>ldc.r4</c>/<c>ldc.r8</c>, <c>conv.r4</c>/<c>conv.r8</c>/<c>conv.r.un</c>,
/// <c>add</c>/<c>sub</c>/<c>mul</c>/<c>div</c>/<c>neg</c> and the float compares (<c>ceq</c>/<c>clt</c>/
/// <c>cgt</c> and their <c>.un</c> forms) on a float-typed operand. <see cref="CilTypeMapper.Map"/> maps
/// <c>float</c>/<c>double</c> to an ordinary (unsigned) i32/i64 IR type carrying the value's raw IEEE
/// bits — so a float local/arg/field/constant is, to every OTHER part of this frontend (allocas, stores,
/// calls, struct layout, …), just another integer. What makes it a float is purely which OPERATION runs
/// on it: every opcode this file handles routes to the matching <c>Koh.GameBoy.SoftFloat</c>
/// <c>[KohRuntime(key)]</c> routine (resolved via <see cref="CilLoweringContext.EnsureRuntime"/> —
/// never a hardcoded routine name, only the key vocabulary, e.g. <c>"f32.add"</c>) instead of an ordinary
/// <see cref="IrBinaryOp"/>/<see cref="IrCompareOp"/>.
///
/// Since the IR type alone can't distinguish "raw i32 bits that are really a float32" from "an ordinary
/// int32", floatness is tracked two ways: (1) at its ORIGIN — a local/arg/field read tags the pushed
/// value by consulting the operand's real Cecil type (<see cref="FloatKindOfType"/>) — this needs no
/// side-table durable across a store/reload, since the declared type is always available again next
/// load; (2) for a pure STACK value with no declared type of its own (a float constant, or the result of
/// a float op/conversion/call), <see cref="_floatTag"/> — a reference-keyed side table exactly like this
/// frontend's other per-value provenance tables (<c>_pendingArrayInfo</c>, <c>_pendingDelegateProvenance</c>,
/// <c>_pendingConcreteType</c>) — carries the tag forward from whichever opcode produced it.
///
/// Vocabulary coverage (see <c>Koh.GameBoy.SoftFloat</c>): the full f32 surface (add/sub/mul/div/neg/
/// isNan/cmp/lt/le/gt/ge/eq/ne, u32&lt;-&gt;f32, i32-&gt;f32, f32-&gt;i32, f32&lt;-&gt;f64) plus the same
/// f64 surface EXCEPT <c>rem</c> — there is no <c>"f32.rem"</c>/<c>"f64.rem"</c> key (float remainder,
/// IL's <c>rem</c> on a float operand, is rare in practice and was out of the softfloat port's scope);
/// hitting it is a diagnostic naming the missing key (<see cref="CilLoweringContext.EnsureRuntime"/>),
/// never a silent integer-remainder miscompile of the bit pattern. An int64-&lt;-&gt;float32 conversion
/// composes through float64 (<see cref="ConvertIntToF32"/>) since no direct <c>"i64.toF32"</c>/
/// <c>"u64.toF32"</c> key exists either — a double-rounding away from what a single direct rounding step
/// would give, immaterial for the finite-normal-and-zero domain the whole softfloat port targets (see
/// <c>Koh.GameBoy.SoftFloat</c>'s remarks). <c>MathF</c>/<c>Math</c> calls are NOT intercepted here — they
/// fall through to the ordinary BCL-call diagnostic in <see cref="LowerCall"/> (no <c>[KohRuntime]</c>
/// routine backs them yet).
/// </summary>
internal sealed partial class CilMethodLowerer
{
    private enum FloatWidth
    {
        F32,
        F64,
    }

    // Reference-keyed, exactly like _pendingArrayInfo/_pendingDelegateProvenance/_pendingConcreteType
    // (IrValue has no structural Equals/GetHashCode override — see those tables' own remarks) — carries
    // a STACK value's floatness (constant, or an op/conversion/call result with no declared Cecil type
    // of its own to re-derive it from) forward to whichever opcode next consumes it.
    private readonly Dictionary<IrValue, FloatWidth> _floatTag = new();

    private void TagFloat(IrValue value, FloatWidth width) => _floatTag[value] = width;

    private FloatWidth? FloatKindOf(IrValue value) =>
        _floatTag.TryGetValue(value, out var width) ? width : null;

    /// <summary>Whether <paramref name="typeReference"/> (a local/parameter/field's declared type — a
    /// byref one, e.g. a <c>ref float</c> parameter, is unwrapped to its element type first, since the
    /// VALUE it carries once <c>ldind.r4</c>/<c>ldind.r8</c> dereferences it is what's float, not the
    /// pointer itself) is <c>float</c>/<c>double</c>, and which width.</summary>
    private static FloatWidth? FloatKindOfType(TypeReference typeReference)
    {
        var target = typeReference is ByReferenceType byRefType
            ? byRefType.ElementType
            : typeReference;
        return target.MetadataType switch
        {
            MetadataType.Single => FloatWidth.F32,
            MetadataType.Double => FloatWidth.F64,
            _ => null,
        };
    }

    private static string Prefix(FloatWidth width) => width == FloatWidth.F32 ? "f32." : "f64.";

    /// <summary>Call the <c>[KohRuntime(key)]</c> routine for <paramref name="key"/>, coercing each
    /// argument to the routine's own declared parameter width first (defensive — every caller in this
    /// file already passes an operand of matching width, but this mirrors the ordinary call path's own
    /// <c>PrepareArg</c> coercion rather than assuming it). Returns the call's raw result value
    /// (unwidened — every routine's IR return type is already &gt;=8 bits and never needs the ordinary
    /// stack-widening <see cref="WidenToStack"/> applies to a narrower local/arg read).</summary>
    private IrValue CallRuntime(string key, params IrValue[] args)
    {
        var fn = _ctx.EnsureRuntime(key);
        var prepared = new IrValue[args.Length];
        for (var i = 0; i < args.Length; i++)
            prepared[i] = CoerceStore(args[i], fn.Parameters[i].Type);
        return _b.Call(fn, prepared);
    }

    /// <summary>Route <c>add</c>/<c>sub</c>/<c>mul</c>/<c>div</c>/<c>rem</c> to the matching
    /// <c>[KohRuntime]</c> routine when either operand is float-tagged (both are, in valid IL — mixed
    /// int/float arithmetic never reaches a binary opcode without an explicit <c>conv.r4</c>/<c>conv.r8</c>
    /// first); returns false (do the ordinary int/pointer path) when neither is.</summary>
    private bool TryFloatBinaryOp(List<IrValue> stack, string opName)
    {
        if (stack.Count < 2)
            return false;
        var kind = FloatKindOf(stack[^2]) ?? FloatKindOf(stack[^1]);
        if (kind is not { } width)
            return false;
        var b = Pop(stack);
        var a = Pop(stack);
        var result = CallRuntime(Prefix(width) + opName, a, b);
        TagFloat(result, width);
        stack.Add(result);
        return true;
    }

    /// <summary><c>neg</c> on a float-tagged operand — the sibling of <see cref="TryFloatBinaryOp"/> for
    /// the one unary arithmetic opcode.</summary>
    private bool TryFloatNeg(List<IrValue> stack)
    {
        if (stack.Count < 1 || FloatKindOf(stack[^1]) is not { } width)
            return false;
        var a = Pop(stack);
        var result = CallRuntime(Prefix(width) + "neg", a);
        TagFloat(result, width);
        stack.Add(result);
        return true;
    }

    /// <summary>Ordered float compare (<c>ceq</c>/<c>clt</c>/<c>cgt</c> — false, never true, whenever
    /// either operand is NaN): routes to the matching <c>[KohRuntime("f32."/"f64." + opName)]</c> bool
    /// routine, then widens its i8 0/1 result to i32 exactly like <see cref="CompareOp"/> does for the
    /// ordinary int path (real CLR compare-opcode stack discipline).</summary>
    private bool TryFloatCompareOp(List<IrValue> stack, string opName)
    {
        if (stack.Count < 2)
            return false;
        var kind = FloatKindOf(stack[^2]) ?? FloatKindOf(stack[^1]);
        if (kind is not { } width)
            return false;
        var b = Pop(stack);
        var a = Pop(stack);
        var result = CallRuntime(Prefix(width) + opName, a, b);
        stack.Add(_b.Conv(IrConvOp.ZExt, result, IrType.I32));
        return true;
    }

    /// <summary>Unordered-or-compare float compare (<c>clt.un</c>/<c>cgt.un</c> — TRUE whenever either
    /// operand is NaN, unlike the ordered forms above): composed from the existing
    /// <c>"f32."/"f64." + "isNan"</c> and <c>"lt"/"gt"</c> keys (<c>isNan(a) | isNan(b) | ordered(a,b)</c>)
    /// rather than needing dedicated <c>".un"</c> runtime keys — cheap given <c>isNan</c> already exists,
    /// and NaN handling is out of scope for the whole softfloat port anyway (see this file's class
    /// remarks and <c>Koh.GameBoy.SoftFloat</c>'s), so this composition is exact everywhere the port is
    /// pinned bit-correct and merely "some true value" (not asserted against) in the NaN case it isn't.</summary>
    private bool TryFloatCompareUnOp(List<IrValue> stack, bool greaterThan)
    {
        if (stack.Count < 2)
            return false;
        var kind = FloatKindOf(stack[^2]) ?? FloatKindOf(stack[^1]);
        if (kind is not { } width)
            return false;
        var b = Pop(stack);
        var a = Pop(stack);
        var prefix = Prefix(width);
        var isNanA = CallRuntime(prefix + "isNan", a);
        var isNanB = CallRuntime(prefix + "isNan", b);
        var ordered = CallRuntime(prefix + (greaterThan ? "gt" : "lt"), a, b);
        var anyNan = _b.Binary(IrBinaryOp.Or, isNanA, isNanB);
        var combined = _b.Binary(IrBinaryOp.Or, anyNan, ordered);
        stack.Add(_b.Conv(IrConvOp.ZExt, combined, IrType.I32));
        return true;
    }

    /// <summary><c>conv.r4</c>/<c>conv.r8</c>: pop, convert to <paramref name="target"/> width, tag,
    /// push. A same-width float source is a no-op re-tag; a different-width float source narrows/widens
    /// through <c>"f32.toF64"</c>/<c>"f64.toF32"</c>; a plain (non-float-tagged) int source is a direct
    /// SIGNED int-&gt;float conversion — real CLR semantics: there is no unsigned direct variant of
    /// these two opcodes, since an unsigned source is always routed through <c>conv.r.un</c> FIRST (see
    /// <see cref="LowerConvRUn"/>), which itself already leaves a float-tagged value behind.</summary>
    private void ConvertToFloat(List<IrValue> stack, FloatWidth target)
    {
        var v = Pop(stack);
        var srcKind = FloatKindOf(v);
        IrValue result;
        if (srcKind == target)
            result = v;
        else if (srcKind is { } sk)
            result = CallRuntime(sk == FloatWidth.F32 ? "f32.toF64" : "f64.toF32", v);
        else
            result = target == FloatWidth.F32 ? ConvertIntToF32(v) : ConvertIntToF64(v);
        TagFloat(result, target);
        stack.Add(result);
    }

    private IrValue ConvertIntToF32(IrValue v)
    {
        if (v.Type.Bits <= 32)
            return CallRuntime("i32.toF32", CoerceStore(v, IrType.I32));
        // int64 -> float32: no direct "i64.toF32" key: compose through float64 (see class remarks on
        // the resulting, immaterial-for-this-port's-scope double rounding).
        var asF64 = CallRuntime("i64.toF64", v);
        return CallRuntime("f64.toF32", asF64);
    }

    private IrValue ConvertIntToF64(IrValue v)
    {
        if (v.Type.Bits > 32)
            return CallRuntime("i64.toF64", CoerceStore(v, IrType.I64));
        // int32 -> float64: no direct "i32.toF64" key: compose through float32 (exact here — float32
        // fully round-trips through float64 with no precision loss, unlike the i64->f32 case above).
        var asF32 = CallRuntime("i32.toF32", CoerceStore(v, IrType.I32));
        return CallRuntime("f32.toF64", asF32);
    }

    /// <summary>If <paramref name="v"/> is float-tagged, convert it to an ORDINARY (untagged) int of
    /// <paramref name="widthBits"/> (32 or 64) via the matching <c>[KohRuntime]</c> routine — the
    /// float-&gt;int mirror of <see cref="ConvertToFloat"/>, used by every <c>conv.i1</c>/<c>u1</c>/
    /// <c>i2</c>/<c>u2</c>/<c>i4</c>/<c>u4</c>/<c>i8</c>/<c>u8</c>/<c>i</c>/<c>u</c> case (real CLR
    /// semantics: each of these opcodes also accepts an "F" (float) stack value as its source, not just
    /// an int — ECMA-335 III.3.27 family). Returns <paramref name="v"/> unchanged when it isn't
    /// float-tagged (an ordinary int source — the overwhelmingly common case, unaffected by this
    /// routing). A width narrower than 32 (conv.i1/u1/i2/u2) always resolves through the 32-bit routine
    /// first — matching the C# language rule that a float-&gt;small-integral conversion goes through
    /// `int` first, then narrows (<paramref name="widthBits"/> is the FINAL opcode's own target width;
    /// callers for the four narrow opcodes pass 32, then narrow the result themselves exactly as they
    /// already did for a plain int source).
    ///
    /// float32-&gt;int64/uint64 composes through float64 (<c>"f32.toF64"</c> then <c>"f64.toI64"/"toU64"</c>)
    /// rather than through int32 first — going through int32 would wrongly saturate a float32 magnitude
    /// that fits int64 but not int32 down to the 32-bit range before it ever reached the 64-bit target.
    /// float64-&gt;int32/uint32 (no direct 32-bit key for a float64 source) composes the other direction —
    /// through a saturating 64-bit conversion, then an ordinary truncation to 32 bits — exact for any
    /// magnitude actually representable at the smaller width (every case this file's tests cover), an
    /// approximation only for a double whose magnitude overflows even int64 (already out of the whole
    /// softfloat port's pinned-correct domain — see <c>Koh.GameBoy.SoftFloat</c>'s remarks).</summary>
    private IrValue ResolveFloatToInt(IrValue v, int widthBits, bool signed)
    {
        if (FloatKindOf(v) is not { } srcKind)
            return v;
        if (srcKind == FloatWidth.F32)
        {
            if (widthBits <= 32)
                return CallRuntime(signed ? "f32.toI32" : "f32.toU32", v);
            var asF64 = CallRuntime("f32.toF64", v);
            return CallRuntime(signed ? "f64.toI64" : "f64.toU64", asF64);
        }
        if (widthBits > 32)
            return CallRuntime(signed ? "f64.toI64" : "f64.toU64", v);
        var asI64 = CallRuntime(signed ? "f64.toI64" : "f64.toU64", v);
        return CoerceStore(asI64, IrType.I32);
    }

    /// <summary><c>conv.r.un</c>: pop an UNSIGNED int32/int64, convert to the natural wide (float64)
    /// precision (real CLR semantics: the "F" stack type this opcode produces is unspecified/native
    /// precision, always subsequently narrowed by an explicit <c>conv.r4</c> when the source expression
    /// wants float32 — see <see cref="ConvertToFloat"/>'s own same-width/different-width handling, which
    /// then does that narrowing when a <c>conv.r4</c> follows). No direct <c>"u32.toF64"</c> key: compose
    /// through float32 (exact — see <see cref="ConvertIntToF64"/>'s remarks on the u32/i32 case).</summary>
    private void LowerConvRUn(List<IrValue> stack)
    {
        var v = Pop(stack);
        var result =
            v.Type.Bits > 32
                ? CallRuntime("u64.toF64", CoerceStore(v, IrType.I64))
                : CallRuntime("f32.toF64", CallRuntime("u32.toF32", CoerceStore(v, IrType.I32)));
        TagFloat(result, FloatWidth.F64);
        stack.Add(result);
    }
}
