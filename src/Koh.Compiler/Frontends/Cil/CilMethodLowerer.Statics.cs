using Koh.Compiler.Ir;
using Koh.Compiler.Targets;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Koh.Compiler.Frontends.Cil;

/// <summary>
/// Static fields and static initialization (see the statics task in
/// <c>docs/superpowers/specs/2026-07-14-cil-frontend-design.md</c>): <c>ldsfld</c>/<c>ldsflda</c>/
/// <c>stsfld</c> lowering (<see cref="CilMethodLowerer"/>'s instance-side half, below), and
/// <see cref="CilStaticFieldSupport.Collect"/> — the pure-metadata pre-pass over every type's
/// <c>.cctor</c> that decides each static field's storage before any method body lowers.
///
/// A mutable field gets an ordinary WRAM holder cell (default-zero via the backend's unconditional
/// boot-only WRAM clear, exactly CSharpFrontend's own "no store for a default" rule — see
/// CSharpFrontend.Declarations.cs's remarks). A readonly field whose <c>.cctor</c> initializer is one of
/// three fixed, Roslyn-regular shapes is folded at compile time instead:
///
/// <list type="bullet">
/// <item><c>ldc.* ; stsfld F</c> (F readonly, scalar) — F becomes a ROM global carrying the constant's
/// bytes; still real (if read-only) storage, so <c>ldsflda</c> keeps working.</item>
/// <item><c>ldc.i4 N ; newarr T ; dup ; ldtoken &lt;blob&gt; ; call
/// RuntimeHelpers::InitializeArray ; stsfld F</c> (F readonly array) — Roslyn's array-literal idiom;
/// F is ALIASED straight onto a ROM global pre-filled with the blob's bytes (no heap allocation, no
/// runtime copy, ever).</item>
/// <item><c>ldc.i4 N ; newarr T ; stsfld F</c> (F readonly OR mutable array, no literal content) — a
/// fixed-size zero array; F is aliased onto a dedicated Array-typed global (ROM if readonly, else WRAM)
/// instead of a heap allocation, matching CSharpFrontend's own <c>static T[] x = new T[n]</c>
/// placement.</item>
/// </list>
///
/// Every matched instruction (the whole idiom, including the terminal <c>stsfld</c>) is recorded per
/// <c>.cctor</c> in <see cref="CilLoweringContext.ElidedInstructionsFor"/>; <c>CilMethodLowerer.Run</c>
/// seeds <c>_suppressed</c> from it before simulating that constructor's body, so the idiom collapses to
/// nothing at runtime — exactly <see cref="CilMethodLowerer.DetectDelegateCacheIdioms"/>'s own shape. A
/// readonly field whose initializer matches none of these three shapes (a method call, a non-constant
/// expression) is deliberately left unfolded: it falls through to the ordinary mutable-WRAM path, and
/// its <c>.cctor</c> store becomes a real one-time write from the entry's prologue — permissive rather
/// than a diagnostic, since it is still correct (a static constructor runs exactly once), just not
/// ROM-placed.
/// </summary>
internal static class CilStaticFieldSupport
{
    /// <summary>Run once per module, before any method body (including a <c>.cctor</c>'s own) is
    /// lowered — see the class remarks for why the ordering matters.</summary>
    public static void Collect(ModuleDefinition module, CilLoweringContext ctx)
    {
        foreach (var type in module.GetTypes())
        {
            // A compiler-generated type (name starting with '<' — no hand-written type can be named
            // that) never declares a static field this frontend's own general Ldsfld/Stsfld path would
            // ever read/write for real (see CilModuleLowerer.Lower's matching Pass-1.5 skip, whose
            // remarks explain why `<>c::.cctor` must never be lowered/called).
            if (type.Name.StartsWith('<'))
                continue;
            var cctor = type.Methods.FirstOrDefault(m =>
                m.IsConstructor && m.IsStatic && m.HasBody
            );
            if (cctor is null)
                continue;

            var elided = new HashSet<Instruction>();
            foreach (var instr in cctor.Body.Instructions)
            {
                if (instr.OpCode.Code != Code.Stsfld)
                    continue;
                if ((instr.Operand as FieldReference)?.Resolve() is not { } field)
                    continue;
                // Only THIS type's own fields are this cctor's business (defensive — Roslyn never
                // emits a cross-type stsfld from a static initializer in valid C#).
                if (!ReferenceEquals(field.DeclaringType, type))
                    continue;
                if (ctx.HasStaticFieldDecision(field))
                    continue; // already classified (e.g. a field with two initializers is a duplicate).

                if (TryMatchArrayLiteral(instr, field, ctx, elided))
                    continue;
                if (TryMatchFixedSizeArray(instr, field, ctx, elided))
                    continue;
                TryMatchScalarConstant(instr, field, ctx, elided);
            }

            if (elided.Count > 0)
                ctx.RegisterElidedCctorInstructions(cctor, elided);
        }
    }

    /// <summary>The instruction immediately preceding <paramref name="instr"/> in program order,
    /// skipping <c>nop</c>s (Debug IL pads between statements, but never inside a single expression's
    /// own push sequence — confirmed for this exact idiom shape by a real Debug/Release Cecil dump; see
    /// the design task's delegate-cache spike for the same technique). <c>internal</c>, not
    /// <c>private</c>: also reused by <c>CilMethodLowerer.Arrays.cs</c>'s
    /// <c>DetectArrayLiteralIdioms</c> for the same idiom's LOCAL-variable shape (a <c>newarr</c>
    /// inside an ordinary method body rather than a static constructor).</summary>
    internal static Instruction? Prev(Instruction? instr)
    {
        var p = instr?.Previous;
        while (p is not null && p.OpCode.Code == Code.Nop)
            p = p.Previous;
        return p;
    }

    internal static bool TryReadConstLong(Instruction? instr, out long value)
    {
        value = 0;
        if (instr is null)
            return false;
        switch (instr.OpCode.Code)
        {
            case Code.Ldc_I4_M1:
                value = -1;
                return true;
            case Code.Ldc_I4_0:
                value = 0;
                return true;
            case Code.Ldc_I4_1:
                value = 1;
                return true;
            case Code.Ldc_I4_2:
                value = 2;
                return true;
            case Code.Ldc_I4_3:
                value = 3;
                return true;
            case Code.Ldc_I4_4:
                value = 4;
                return true;
            case Code.Ldc_I4_5:
                value = 5;
                return true;
            case Code.Ldc_I4_6:
                value = 6;
                return true;
            case Code.Ldc_I4_7:
                value = 7;
                return true;
            case Code.Ldc_I4_8:
                value = 8;
                return true;
            case Code.Ldc_I4_S:
                value = (sbyte)instr.Operand;
                return true;
            case Code.Ldc_I4:
                value = (int)instr.Operand;
                return true;
            case Code.Ldc_I8:
                value = (long)instr.Operand;
                return true;
            default:
                return false;
        }
    }

    /// <summary><c>ldc.* ; stsfld F</c> — a readonly scalar (int-mapped, non-pointer) field initialized
    /// to a compile-time constant.</summary>
    private static bool TryMatchScalarConstant(
        Instruction stsfld,
        FieldDefinition field,
        CilLoweringContext ctx,
        HashSet<Instruction> elided
    )
    {
        if (!field.IsInitOnly)
            return false;
        var producer = Prev(stsfld);
        if (!TryReadConstLong(producer, out var value))
            return false;

        IrType irType;
        try
        {
            (irType, _) = CilTypeMapper.Map(field.FieldType);
        }
        catch (CilNotSupportedException)
        {
            return false;
        }
        if (irType.Kind != IrTypeKind.Int)
            return false;

        var size = Math.Max(irType.SizeInBytes, 1);
        var bytes = new byte[size];
        for (var i = 0; i < size; i++)
            bytes[i] = (byte)(value >> (8 * i));

        var romGlobal = new IrGlobal(
            $"{field.DeclaringType.FullName}.{field.Name}",
            irType,
            AddressSpace.Rom,
            initializer: bytes
        );
        ctx.Module.Globals.Add(romGlobal);
        ctx.RegisterFoldedStatic(field, romGlobal);

        elided.Add(producer!);
        elided.Add(stsfld);
        return true;
    }

    /// <summary><c>ldc.i4 N ; newarr T ; dup ; ldtoken &lt;blob&gt; ; call
    /// RuntimeHelpers::InitializeArray ; stsfld F</c> — Roslyn's array-literal idiom (&gt;=3 constant
    /// elements) for a readonly array field. Aliases F directly onto a ROM global pre-filled with the
    /// blob field's raw bytes (<see cref="FieldDefinition.InitialValue"/> is already exactly the
    /// element-major little-endian layout <c>InitializeArray</c> itself would have memcopied in).</summary>
    private static bool TryMatchArrayLiteral(
        Instruction stsfld,
        FieldDefinition field,
        CilLoweringContext ctx,
        HashSet<Instruction> elided
    )
    {
        if (!field.IsInitOnly)
            return false;
        var call = Prev(stsfld);
        if (
            call?.OpCode.Code != Code.Call
            || call.Operand is not MethodReference callee
            || callee.Name != "InitializeArray"
            || callee.DeclaringType.FullName != "System.Runtime.CompilerServices.RuntimeHelpers"
        )
            return false;
        var ldtoken = Prev(call);
        if (ldtoken?.OpCode.Code != Code.Ldtoken)
            return false;
        var dup = Prev(ldtoken);
        if (dup?.OpCode.Code != Code.Dup)
            return false;
        var newarr = Prev(dup);
        if (newarr?.OpCode.Code != Code.Newarr)
            return false;
        var countInstr = Prev(newarr);
        if (!TryReadConstLong(countInstr, out var count) || count < 0)
            return false;

        IrType elemType;
        try
        {
            (elemType, _) = CilTypeMapper.Map((TypeReference)newarr.Operand);
        }
        catch (CilNotSupportedException)
        {
            return false;
        }
        var dataField = (ldtoken.Operand as FieldReference)?.Resolve();
        var bytes = dataField?.InitialValue;
        var elemSize = Math.Max(elemType.SizeInBytes, 1);
        if (bytes is null || bytes.Length != count * elemSize)
            return false;

        var romGlobal = new IrGlobal(
            $"{field.DeclaringType.FullName}.{field.Name}",
            IrType.Array(elemType, (int)count),
            AddressSpace.Rom,
            initializer: bytes
        );
        ctx.Module.Globals.Add(romGlobal);
        ctx.RegisterFoldedStaticArray(field, romGlobal, elemType, elemSigned: false, (int)count);

        elided.Add(countInstr!);
        elided.Add(newarr);
        elided.Add(dup);
        elided.Add(ldtoken);
        elided.Add(call);
        elided.Add(stsfld);
        return true;
    }

    /// <summary><c>ldc.i4 N ; newarr T ; stsfld F</c> — a fixed-size array with no literal content
    /// (<c>new T[n]</c>). Aliases F onto a dedicated zero-initialized Array-typed global (ROM if F is
    /// readonly, else WRAM — the backend's unconditional boot-only WRAM clear zeroes it, matching
    /// <c>newarr</c>'s own zero-fill semantics) instead of a heap allocation, mirroring
    /// CSharpFrontend's own static-array placement.</summary>
    private static bool TryMatchFixedSizeArray(
        Instruction stsfld,
        FieldDefinition field,
        CilLoweringContext ctx,
        HashSet<Instruction> elided
    )
    {
        var newarr = Prev(stsfld);
        if (newarr?.OpCode.Code != Code.Newarr)
            return false;
        var countInstr = Prev(newarr);
        if (!TryReadConstLong(countInstr, out var count) || count < 0)
            return false;

        IrType elemType;
        try
        {
            (elemType, _) = CilTypeMapper.Map((TypeReference)newarr.Operand);
        }
        catch (CilNotSupportedException)
        {
            return false;
        }

        var space = field.IsInitOnly ? AddressSpace.Rom : AddressSpace.Wram;
        var arrayGlobal = new IrGlobal(
            $"{field.DeclaringType.FullName}.{field.Name}",
            IrType.Array(elemType, (int)count),
            space
        );
        ctx.Module.Globals.Add(arrayGlobal);
        ctx.RegisterFoldedStaticArray(field, arrayGlobal, elemType, elemSigned: false, (int)count);

        elided.Add(countInstr!);
        elided.Add(newarr);
        elided.Add(stsfld);
        return true;
    }
}

internal sealed partial class CilMethodLowerer
{
    private FieldDefinition ResolveStaticField(FieldReference fieldRef) =>
        fieldRef.Resolve()
        ?? throw new CilNotSupportedException(
            $"cannot resolve static field '{fieldRef.FullName}' in '{_method.FullName}'."
        );

    /// <summary><c>ldsfld</c>: an aliased field (see <see cref="CilStaticFieldSupport"/>) pushes its
    /// backing global's address directly (no separate holder exists); an ordinary field loads its
    /// holder cell, widened onto the simulated stack exactly like a local/argument read.</summary>
    private IrValue LoadStaticField(FieldDefinition field)
    {
        if (_ctx.IsStaticFieldAlias(field))
        {
            var aliasGlobal = _ctx.EnsureStaticGlobal(field);
            var ptr = IrBuilder.GlobalRef(aliasGlobal);
            if (_ctx.TryGetStaticArrayInfo(field, out var info))
                _pendingArrayInfo[ptr] = (
                    info.ElemType,
                    info.Signed,
                    IrBuilder.ConstInt(IrType.I16, info.Count)
                );
            return ptr;
        }
        var holder = _ctx.EnsureStaticGlobal(field);
        var (_, signed) = CilTypeMapper.Map(field.FieldType);
        return WidenToStack(_b.Load(IrBuilder.GlobalRef(holder)), signed);
    }

    /// <summary><c>ldsflda</c>: the field's global address either way (an aliased field's global IS its
    /// value, so this is the same pointer <see cref="LoadStaticField"/> would push for it).</summary>
    private IrValue StaticFieldAddress(FieldDefinition field) =>
        IrBuilder.GlobalRef(_ctx.EnsureStaticGlobal(field));

    /// <summary><c>stsfld</c>: a diagnostic for an aliased field — its identity is fixed at compile
    /// time (a ROM constant, or a fixed-size array whose ONE assignment was already accounted for by
    /// the pre-pass), so any OTHER store to it is a shape this frontend does not support, not a value
    /// to silently miscompile. An ordinary field stores to its holder cell.</summary>
    private void StoreStaticField(FieldDefinition field, IrValue value)
    {
        if (_ctx.IsStaticFieldAlias(field))
            throw new CilNotSupportedException(
                $"'{field.FullName}' is a fixed-identity static field (its storage was folded from "
                    + "its declaring type's static constructor); only that one recognized "
                    + "initialization is supported, not a later reassignment."
            );
        var holder = _ctx.EnsureStaticGlobal(field);
        _b.Store(CoerceStore(value, holder.Type), IrBuilder.GlobalRef(holder));
    }
}
