using Koh.Compiler.Ir;
using Mono.Cecil;

namespace Koh.Compiler.Frontends.Cil;

/// <summary>
/// String-literal support for the graphics library's <c>Text.Draw(col, row, "SCORE")</c> (see
/// <c>docs/superpowers/specs/2026-07-15-graphics-library-design.md</c> §8 decision 3, build-plan slice
/// 0). A C# <c>string</c> literal is Game Boy tile-index data, not a .NET-managed object: Roslyn's
/// <c>ldstr</c> pushes ONE BYTE PER CHARACTER (ASCII, not UTF-16 — a Game Boy screen has no use for a
/// 16-bit code unit) into a ROM global, exactly the same "raw pointer + frontend-only element/count
/// provenance" representation <c>CilMethodLowerer.Arrays.cs</c> already uses for every other array (see
/// its class remarks) — <c>ldstr</c>'s result is recorded in the SAME <c>_pendingArrayInfo</c> table a
/// <c>newarr</c>/array-literal result is, so it round-trips through locals/Dup for free and a plain
/// <c>byte[]</c>-shaped consumer (an indexing loop, <c>Text</c>'s eventual implementation) needs no
/// string-specific code at all.
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
/// are compile-time ROM data, readable via <c>.Length</c> and <c>[i]</c>, PROVIDED the access happens in
/// the SAME method that produced the string value from a literal (directly, or through a local/Dup — see
/// <see cref="_pendingArrayInfo"/>'s propagation in <c>LoadLocal</c>/<c>StoreLocal</c>). Exactly like
/// every other array in this frontend (see <c>LowerLdlen</c>'s matching remark), a string value that
/// crosses a real call boundary (received as a parameter, returned from a call, read from a field) has
/// no traceable provenance and reports a diagnostic rather than a wrong answer — a caller passing a
/// string literal into a helper method that reads <c>.Length</c>/indexes it is therefore NOT yet
/// supported by this slice; that helper must do its own indexing inline (or receive the literal inlined)
/// until a later slice, if ever, threads array/string provenance across call boundaries.</para>
/// </summary>
internal sealed partial class CilMethodLowerer
{
    /// <summary><c>ldstr</c>: convert the literal to one ASCII byte per character (a diagnostic, not a
    /// silent truncation/miscompile, for any character above 0x7F — Game Boy text has no representation
    /// for it), get/create its ROM global (deduplicated by content across the whole module — see
    /// <see cref="CilLoweringContext.EnsureStringLiteralGlobal"/>), and push its address tagged with
    /// <see cref="_pendingArrayInfo"/> exactly like a <c>byte[]</c> literal's own base pointer.</summary>
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
        var ptr = IrBuilder.GlobalRef(global);
        _pendingArrayInfo[ptr] = (IrType.I8, false, IrBuilder.ConstInt(IrType.I16, bytes.Length));
        return ptr;
    }

    /// <summary>Intercepts the two <c>System.String</c> instance members a string-walking loop can ever
    /// emit — see class remarks. Returns false (no-op, falls through to the ordinary BCL-call diagnostic
    /// path) for any other member or any receiver <see cref="_pendingArrayInfo"/> can't trace back to an
    /// <c>ldstr</c>/array-shaped provenance in THIS method.</summary>
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
                if (!_pendingArrayInfo.TryGetValue(thisValue, out var info))
                    throw new CilNotSupportedException(
                        $"'{calleeRef.FullName}' in '{_method.FullName}' reads the length of a string "
                            + "this frontend cannot trace back to an 'ldstr' literal in this same "
                            + "method (a string received as a parameter, returned from a call, or read "
                            + "from a field/array element carries no runtime length — see "
                            + "CilMethodLowerer.Strings.cs's class remarks)."
                    );
                stack.Add(WidenToStack(CoerceStore(info.Count, IrType.I32), signed: false));
                return true;
            }
            case "get_Chars" when args.Length == 1:
            {
                if (!_pendingArrayInfo.TryGetValue(thisValue, out var info))
                    throw new CilNotSupportedException(
                        $"'{calleeRef.FullName}' in '{_method.FullName}' indexes a string this frontend "
                            + "cannot trace back to an 'ldstr' literal in this same method (see "
                            + "CilMethodLowerer.Strings.cs's class remarks)."
                    );
                var elementPtr = ElementPointer(thisValue, args[0], info.ElemType);
                stack.Add(WidenToStack(_b.Load(elementPtr), info.Signed));
                return true;
            }
            default:
                return false;
        }
    }
}
