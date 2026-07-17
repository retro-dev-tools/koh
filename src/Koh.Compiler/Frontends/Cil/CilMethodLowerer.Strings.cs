using Koh.Compiler.Ir;
using Mono.Cecil;

namespace Koh.Compiler.Frontends.Cil;

/// <summary>
/// String-literal support for the graphics library's <c>Text.Draw(col, row, "SCORE")</c> (see
/// <c>docs/superpowers/specs/2026-07-15-graphics-library-design.md</c> §8 decision 3, build-plan slice
/// 0). A C# <c>string</c> literal is Game Boy tile-index data, not a .NET-managed object, and — unlike
/// every OTHER array in this frontend (<c>CilMethodLowerer.Arrays.cs</c>'s <c>_pendingArrayInfo</c>,
/// compile-time-only, lost across a call boundary) — a string's length travels WITH its bytes at
/// runtime: <c>ldstr</c> lowers to a pointer to a LENGTH-PREFIXED ROM blob, <c>[u16 length (little-
/// endian, matching the SM83 target's own <see cref="Koh.Compiler.Targets.DataLayout"/>)][one ASCII
/// byte per character]</c>. Because the length lives in memory at the pointer itself, a <c>string</c>
/// value is representationally just a pointer — it flows through locals, method parameters, returns,
/// and fields exactly like any other pointer (see <c>CilTypeMapper.Map</c>'s reference-type branch,
/// which every non-value-type — <c>System.String</c> included — already falls into), and
/// <c>.Length</c>/the indexer are ordinary runtime loads off that pointer, not a compile-time-provenance
/// lookup. This is what unblocks a string PARAMETER: a library method like
/// <c>Text.Draw(byte col, byte row, string text)</c> can loop on <c>text.Length</c>/<c>text[i]</c> in
/// its OWN body, called from game code with a literal, because the callee's parameter value is a
/// perfectly ordinary pointer to the same length-prefixed blob the caller's <c>ldstr</c> produced.
///
/// <para><c>System.String</c> itself is never modeled beyond this: no heap object, no UTF-16 buffer, no
/// interning, no general BCL surface. Exactly two instance members are intercepted here —
/// <c>get_Length</c> and <c>get_Chars(int32)</c> — because together they are exactly what the C#
/// language specification's <c>foreach</c>-over-<c>string</c> lowering (and any hand-written index loop)
/// needs; per ECMA-334, <c>foreach (char c in someString)</c> is REQUIRED to compile to
/// <c>for (int i = 0; i &lt; someString.Length; i++) { char c = someString[i]; ... }</c> — never an
/// enumerator — so this is the whole surface a string-walking loop can ever emit, not an arbitrarily
/// truncated subset of a larger String API. Any other <c>String</c> member (concatenation, `Substring`,
/// `Equals`, …) falls through to the ordinary BCL-call diagnostic in <see cref="LowerInstanceCall"/>
/// (its <c>calleeRef.Resolve()</c> fails for an extern mscorlib method), never a silent miscompile.</para>
///
/// <para><b>What a consumer (e.g. the Text module) can rely on:</b> a string's length and byte contents
/// are always readable via <c>.Length</c> and <c>[i]</c> off WHATEVER pointer value the receiver holds
/// — a same-method literal, a local/Dup copy of one, a method parameter, a field read, or a call return
/// — because the length-prefix blob is real runtime memory, not frontend-only bookkeeping. There is no
/// "untraceable provenance" diagnostic for <c>String.Length</c>/indexing any more (contrast
/// <c>CilMethodLowerer.Arrays.cs</c>'s <c>LowerLdlen</c>, which still has one — an ordinary array has no
/// runtime length header). The only remaining string diagnostic is a non-ASCII literal character.</para>
/// </summary>
internal sealed partial class CilMethodLowerer
{
    /// <summary><c>ldstr</c>: convert the literal to one ASCII byte per character (a diagnostic, not a
    /// silent truncation/miscompile, for any character above 0x7F — Game Boy text has no representation
    /// for it), get/create its length-prefixed ROM global (deduplicated by value across the whole
    /// module — see <see cref="CilLoweringContext.EnsureStringLiteralGlobal"/>), and push its address.
    /// No <see cref="_pendingArrayInfo"/> tag is needed (unlike a <c>byte[]</c> literal's base pointer):
    /// the blob's own <c>u16</c> length prefix is what <see cref="TryLowerStringCall"/> reads at
    /// runtime, so this pointer is valid string-shaped data at ANY consumer, not just one traced back to
    /// this exact <c>ldstr</c> in this same method.</summary>
    private IrValue LowerLdstr(string value)
    {
        var bytes = new byte[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch > '\x7F')
                throw new CilNotSupportedException(
                    $"string literal in '{_method.FullName}' contains non-ASCII character U+"
                        + $"{(int)ch:X4} (Koh string literals lower to one-byte-per-character ASCII ROM "
                        + "data — Game Boy text has no representation above 0x7F)."
                );
            bytes[i] = (byte)ch;
        }
        var global = _ctx.EnsureStringLiteralGlobal(value, bytes);
        return IrBuilder.GlobalRef(global);
    }

    /// <summary>Intercepts the two <c>System.String</c> instance members a string-walking loop can ever
    /// emit — see class remarks. Both are runtime memory reads off <paramref name="thisValue"/> itself
    /// (the length-prefixed blob's own <c>u16</c> header / ASCII bytes), so they work identically for a
    /// same-method literal, a parameter, a field, or any other pointer value — no provenance-tracing,
    /// no diagnostic for "can't trace this to an ldstr". Returns false (no-op, falls through to the
    /// ordinary BCL-call diagnostic path) for any other <c>System.String</c> member.</summary>
    private bool TryLowerStringCall(
        MethodReference calleeRef,
        IrValue thisValue,
        IrValue[] args,
        List<IrValue> stack
    )
    {
        if (calleeRef.DeclaringType.FullName != "System.String")
            return false;

        switch (calleeRef.Name)
        {
            case "get_Length" when args.Length == 0:
            {
                // The blob's u16 length prefix lives at the pointer itself — bitcast to a u16 pointer
                // (same-size reinterpret; SizeInBytes for ANY pointer is the target's address width,
                // never its pointee's, so this is valid regardless of thisValue's declared pointee type)
                // and load it.
                var lengthPtr = _b.Conv(IrConvOp.Bitcast, thisValue, IrType.Pointer(IrType.I16));
                stack.Add(WidenToStack(CoerceStore(_b.Load(lengthPtr), IrType.I32), signed: false));
                return true;
            }
            case "get_Chars" when args.Length == 1:
            {
                // ASCII data starts 2 bytes past the length prefix; index from there exactly like an
                // ordinary array element (ElementPointer already coerces the index to i16).
                var dataStart = _b.Gep(thisValue, IrBuilder.ConstInt(IrType.I16, 2), IrType.I8);
                var elementPtr = ElementPointer(dataStart, args[0], IrType.I8);
                stack.Add(WidenToStack(_b.Load(elementPtr), signed: false));
                return true;
            }
            default:
                return false;
        }
    }
}
