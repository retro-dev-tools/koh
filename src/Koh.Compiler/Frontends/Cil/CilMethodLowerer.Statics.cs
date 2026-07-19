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
    /// <summary>Run once per the GAME's OWN module, before any method body (including a <c>.cctor</c>'s
    /// own) is lowered — see the class remarks for why the ordering matters. Deliberately does NOT walk
    /// referenced assemblies eagerly (a first attempt at this — mirroring <see cref="CilIntrinsicIndex.Build"/>'s
    /// whole-reference-graph traversal — was reverted: unlike an intrinsic-method lookup table, this
    /// pass has a real, permanent side effect per match, <c>ctx.Module.Globals.Add(...)</c>, and the
    /// SM83 backend places EVERY entry in <c>module.Globals</c> unconditionally, with no reachability
    /// filtering (<c>Sm83Backend.Compile</c>'s <c>foreach (var g in module.Globals)</c>). Walking the
    /// whole reference graph eagerly classified (and permanently placed) hundreds of unrelated BCL
    /// statics — confirmed by a real IR dump: <c>System.Globalization.OrdinalCasing.s_basicLatin</c>
    /// (512 ROM bytes), <c>System.Globalization.HebrewNumber.s_numberPassingState</c> (170 ROM bytes),
    /// dozens more — bloating every ROM this frontend compiles regardless of whether the game ever
    /// touches them. See <see cref="CilLoweringContext.EnsureStaticGlobal"/> for the ON-DEMAND
    /// counterpart that actually handles a cross-assembly library static like Koh.GameBoy's
    /// <c>[KohAligned(n)] static byte[] Shadow</c> (the Sprites module's shadow OAM) correctly: it
    /// classifies a field's declaring type's <c>.cctor</c> the first time REACHABLE code actually
    /// references one of that type's fields, exactly mirroring how <c>CilLoweringContext.EnsureLowered</c>
    /// lowers a cross-assembly METHOD body on demand rather than eagerly sweeping every referenced
    /// assembly's methods.</summary>
    public static void Collect(ModuleDefinition module, CilLoweringContext ctx)
    {
        foreach (var type in module.GetTypes())
            CollectType(type, ctx);
    }

    /// <summary>Classifies every static field of <paramref name="type"/> whose <c>.cctor</c> initializer
    /// matches one of the three fixed shapes (see the class remarks) — the per-TYPE unit of work
    /// <see cref="Collect"/>'s eager module-wide sweep and <see cref="CilLoweringContext.EnsureStaticGlobal"/>'s
    /// lazy on-demand fallback both call, so the two paths share one implementation and can never
    /// disagree. Idempotent: a field already decided (by either path, or a duplicate initializer) is
    /// left alone (<see cref="CilLoweringContext.HasStaticFieldDecision"/>).</summary>
    internal static void CollectType(TypeDefinition type, CilLoweringContext ctx)
    {
        // A compiler-generated type (name starting with '<' — no hand-written type can be named that)
        // never declares a static field this frontend's own general Ldsfld/Stsfld path would ever
        // read/write for real (see CilModuleLowerer.Lower's matching Pass-1.5 skip, whose remarks
        // explain why `<>c::.cctor` must never be lowered/called).
        if (type.Name.StartsWith('<'))
            return;
        var cctor = type.Methods.FirstOrDefault(m => m.IsConstructor && m.IsStatic && m.HasBody);
        if (cctor is null)
            return;

        var elided = new HashSet<Instruction>();
        foreach (var instr in cctor.Body.Instructions)
        {
            if (instr.OpCode.Code != Code.Stsfld)
                continue;
            if ((instr.Operand as FieldReference)?.Resolve() is not { } field)
                continue;
            // Only THIS type's own fields are this cctor's business (defensive — Roslyn never emits a
            // cross-type stsfld from a static initializer in valid C#).
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

    /// <summary>Reads <c>[KohAligned(n)]</c> off <paramref name="field"/> (matched by the attribute
    /// type's SIMPLE NAME, "KohAlignedAttribute" — same reasoning as <see cref="CilIntrinsicIndex"/>:
    /// <c>Koh.Compiler</c> must never reference <c>Koh.GameBoy</c>), or null if absent. Consulted by
    /// both a plain mutable static field's holder (<see cref="CilLoweringContext.EnsureStaticGlobal"/>)
    /// and a fixed-size array field with no literal content (<see cref="TryMatchFixedSizeArray"/>) —
    /// the two WRAM-placed shapes the SM83 backend's static-WRAM allocator can actually align.</summary>
    internal static int? ReadAlignment(FieldDefinition field)
    {
        foreach (var attr in field.CustomAttributes)
        {
            if (attr.AttributeType.Name != "KohAlignedAttribute")
                continue;
            return (int)attr.ConstructorArguments[0].Value;
        }
        return null;
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

        // Length-carrying arrays (enabler E4): bake the u16 element count in front of the payload;
        // the field's value is the PAYLOAD address (base+2) — see RegisterFoldedStaticArray.
        var prefixed = new byte[2 + bytes.Length];
        prefixed[0] = (byte)count;
        prefixed[1] = (byte)((int)count >> 8);
        bytes.CopyTo(prefixed, 2);
        var romGlobal = new IrGlobal(
            $"{field.DeclaringType.FullName}.{field.Name}",
            IrType.Array(IrType.I8, prefixed.Length),
            AddressSpace.Rom,
            initializer: prefixed
        );
        ctx.Module.Globals.Add(romGlobal);
        ctx.RegisterFoldedStaticArray(
            field,
            romGlobal,
            elemType,
            elemSigned: false,
            (int)count,
            payloadOffset: 2
        );

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

        // Length-carrying arrays (enabler E4): the u16 element count sits at payload−2. The payload
        // offset is normally 2, but a [KohAligned(A)] field pads it to A so the PAYLOAD — what OAM
        // DMA page math and pointer interop actually see — keeps the declared alignment (the global's
        // own base is aligned to A, so base+A is too; the count occupies the last 2 pad bytes).
        var space = field.IsInitOnly ? AddressSpace.Rom : AddressSpace.Wram;
        var alignment = ReadAlignment(field);
        var payloadOffset = Math.Max(2, alignment ?? 1);
        var elemSize = Math.Max(elemType.SizeInBytes, 1);
        var totalSize = payloadOffset + (int)count * elemSize;
        byte[]? initializer = null;
        if (space == AddressSpace.Rom)
        {
            initializer = new byte[totalSize]; // zeros, plus the count baked at payload−2
            initializer[payloadOffset - 2] = (byte)count;
            initializer[payloadOffset - 1] = (byte)((int)count >> 8);
        }
        var arrayGlobal = new IrGlobal(
            $"{field.DeclaringType.FullName}.{field.Name}",
            IrType.Array(IrType.I8, totalSize),
            space,
            alignment: alignment,
            initializer: initializer
        );
        ctx.Module.Globals.Add(arrayGlobal);
        ctx.RegisterFoldedStaticArray(
            field,
            arrayGlobal,
            elemType,
            elemSigned: false,
            (int)count,
            payloadOffset
        );
        if (space == AddressSpace.Wram)
            ctx.ArrayCountInits.Add((arrayGlobal, payloadOffset - 2, (int)count));

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
    /// holder cell, widened onto the simulated stack exactly like a local/argument read.
    ///
    /// <see cref="CilLoweringContext.EnsureStaticGlobal"/> is called FIRST, unconditionally, before the
    /// <see cref="CilLoweringContext.IsStaticFieldAlias"/> check — not the other way around. For a
    /// cross-assembly field (declared outside the game's own module — see <c>EnsureStaticGlobal</c>'s
    /// own remarks on why it, not the eager module-only <c>CilStaticFieldSupport.Collect</c>, is what
    /// classifies such a field), checking <c>IsStaticFieldAlias</c> BEFORE ever calling
    /// <c>EnsureStaticGlobal</c> would read a not-yet-populated answer on this field's very first
    /// reference anywhere in the program: always false, since nothing has classified the field yet,
    /// sending an aliased array field down the "ordinary scalar holder" branch below — which then
    /// tries to <c>Load</c> a whole array-typed global as if it were a POINTER-holding scalar cell, an
    /// invalid load the IR verifier rejects the moment something (e.g. <c>Ldelema</c>) tries to use that
    /// load's result as a pointer (confirmed by a real repro: <c>Sprites.HideAll</c>, the first function
    /// in a real program to reference the Sprites module's <c>Shadow</c> array, hit exactly this
    /// verifier failure — "'gep' base must be a pointer" — before this reordering).</summary>
    private IrValue LoadStaticField(FieldDefinition field)
    {
        var g = _ctx.EnsureStaticGlobal(field);
        // A user-struct field's blob address IS its value — the frontend's universal struct
        // representation (a struct value is the address of its bytes), so consumers (stloc into a
        // struct local, PrepareArg's byval copy, ldfld off it) need nothing special.
        if (_ctx.TryGetStaticStructLayout(field, out _))
            return IrBuilder.GlobalRef(g);
        if (_ctx.IsStaticFieldAlias(field))
        {
            var ptr = AliasedFieldValue(field, g);
            if (_ctx.TryGetStaticArrayInfo(field, out var info))
                _pendingArrayInfo[ptr] = (
                    info.ElemType,
                    info.Signed,
                    IrBuilder.ConstInt(IrType.I16, info.Count)
                );
            return ptr;
        }
        var (_, signed) = CilTypeMapper.Map(field.FieldType);
        return WidenToStack(_b.Load(IrBuilder.GlobalRef(g)), signed);
    }

    /// <summary>An aliased field's pushed value: the global's PAYLOAD address — past the u16 length
    /// prefix for an array alias (enabler E4; see <c>RegisterFoldedStaticArray</c>), the base itself
    /// for a folded scalar.</summary>
    private IrValue AliasedFieldValue(FieldDefinition field, IrGlobal g)
    {
        var ptr = (IrValue)IrBuilder.GlobalRef(g);
        if (_ctx.TryGetStaticArrayInfo(field, out var info) && info.PayloadOffset != 0)
            ptr = _b.Gep(ptr, IrBuilder.ConstInt(IrType.I16, info.PayloadOffset), IrType.I8);
        return ptr;
    }

    /// <summary><c>ldsflda</c>: the field's global address either way (an aliased field's global IS its
    /// value, so this is the same pointer <see cref="LoadStaticField"/> would push for it — including
    /// the payload offset for a length-prefixed array alias).</summary>
    private IrValue StaticFieldAddress(FieldDefinition field)
    {
        var g = _ctx.EnsureStaticGlobal(field);
        return _ctx.IsStaticFieldAlias(field)
            ? AliasedFieldValue(field, g)
            : IrBuilder.GlobalRef(g);
    }

    /// <summary><c>stsfld</c>: a diagnostic for an aliased field — its identity is fixed at compile
    /// time (a ROM constant, or a fixed-size array whose ONE assignment was already accounted for by
    /// the pre-pass), so any OTHER store to it is a shape this frontend does not support, not a value
    /// to silently miscompile. An ordinary field stores to its holder cell.
    ///
    /// Same ordering fix as <see cref="LoadStaticField"/>: <see cref="CilLoweringContext.EnsureStaticGlobal"/>
    /// runs FIRST, so a cross-assembly field's alias status is settled before
    /// <see cref="CilLoweringContext.IsStaticFieldAlias"/> is consulted.</summary>
    private void StoreStaticField(FieldDefinition field, IrValue value)
    {
        var holder = _ctx.EnsureStaticGlobal(field);
        // Whole-struct assignment ('Assets.Tiles = ...'): the popped value is the source struct's
        // address; byte-copy it into the field's blob, same as any other struct write site.
        if (_ctx.TryGetStaticStructLayout(field, out var structLayout))
        {
            EmitCopy(IrBuilder.GlobalRef(holder), value, structLayout.Size);
            return;
        }
        if (_ctx.IsStaticFieldAlias(field))
            throw new CilNotSupportedException(
                $"'{field.FullName}' is a fixed-identity static field (its storage was folded from "
                    + "its declaring type's static constructor); only that one recognized "
                    + "initialization is supported, not a later reassignment."
            );
        _b.Store(CoerceStore(value, holder.Type), IrBuilder.GlobalRef(holder));
    }
}
