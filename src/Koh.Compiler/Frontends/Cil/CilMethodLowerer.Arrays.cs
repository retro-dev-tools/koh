using Koh.Compiler.Ir;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Koh.Compiler.Frontends.Cil;

/// <summary>
/// SZ-array support: <c>newarr</c>, <c>ldelem.*</c>/<c>stelem.*</c> for the fixed-width primitive
/// element types the LINQ task's fixtures need. An array is a raw heap-allocated element block
/// (bump-pointer, same <c>__heap</c> convention as <see cref="CilMethodLowerer"/>'s object allocation —
/// see <c>LowerNewobj</c>), never a real CLR array object (no length header, no covariance/type
/// checks) — its element type and count are frontend-only provenance carried alongside the base
/// pointer, exactly like a resolved delegate's target method (see
/// <c>CilMethodLowerer.Delegates.cs</c>'s remarks on <c>_pendingDelegateProvenance</c>). Because the
/// count is whatever value the CIL program actually pushed before <c>newarr</c> (typically, but not
/// necessarily, a compile-time constant), it is tracked as a real <see cref="IrValue"/> — the loop
/// bound in <see cref="CilMethodLowerer.Linq"/>'s pipeline lowering reads it at runtime, not by
/// assuming a constant length.
/// </summary>
internal sealed partial class CilMethodLowerer
{
    // A `newarr`'s pushed base pointer (always Pointer(I8), matching FieldPointer's convention) ->
    // its element type/signedness/count. Propagated through locals the same way delegate provenance
    // is (see LoadLocal/StoreLocal) — Dup carries it for free since it pushes the same reference twice.
    private readonly Dictionary<
        IrValue,
        (IrType ElemType, bool Signed, IrValue Count)
    > _pendingArrayInfo = new();
    private readonly Dictionary<
        VariableDefinition,
        (IrType ElemType, bool Signed, IrValue Count)
    > _localArrayInfo = new();

    // A `newarr` instruction this method's DetectArrayLiteralIdioms pre-pass proved is Roslyn's own
    // array-literal idiom ('ldc.i4 N ; newarr T ; dup ; ldtoken <blob> ; call
    // RuntimeHelpers::InitializeArray') -> the ROM global carrying the literal's bytes, its element
    // type, and its element count. Populated once per method body, before Run's main simulation loop
    // — see DetectArrayLiteralIdioms.
    private readonly Dictionary<
        Instruction,
        (IrGlobal Global, IrType ElemType, int Count)
    > _newarrLiteralGlobal = new();

    /// <summary>
    /// Recognizes Roslyn's array-literal idiom for a LOCAL (or any other non-static-field) array —
    /// <c>ldc.i4 N ; newarr T ; dup ; ldtoken &lt;blob&gt; ; call RuntimeHelpers::InitializeArray</c> —
    /// the exact same instruction shape <c>CilMethodLowerer.Statics.cs</c>'s
    /// <c>CilStaticFieldSupport.TryMatchArrayLiteral</c> matches for a <c>static readonly</c> field's
    /// initializer, just ending in whatever consumes the array next (typically <c>stloc</c>, but
    /// equally an inline call argument — see <c>Simulate</c>'s <c>Code.Newarr</c> case, which doesn't
    /// require any particular consumer) instead of always <c>stsfld</c>. Also the exact idiom Roslyn
    /// emits for standard C#'s <c>byte[] x = { 1, 2, 3 };</c> — which, note, is the ONLY way a byte-
    /// array literal can reach this frontend at all: Koh's own former "string literal as byte[]
    /// initializer" support was Koh-legal-but-C#-illegal (no int-promotion Koh C# accepted syntax real
    /// C# rejects), so it has no assembly representation to lower in the first place — see
    /// <c>docs/superpowers/specs/2026-07-14-cil-frontend-design.md</c>'s whole premise. On a match, the
    /// literal's constant bytes get a ROM global (shared across every occurrence of the same content —
    /// see <see cref="CilLoweringContext.EnsureRvaBlobGlobal"/>) instead of being rebuilt on the heap at
    /// startup; the interior <c>dup</c>/<c>ldtoken</c>/<c>call</c> triple is marked
    /// <see cref="_suppressed"/> (matching <see cref="DetectDelegateCacheIdioms"/>'s own technique), and
    /// <c>Code.Newarr</c>'s own handling (not suppressed — it still needs to POP the count and PUSH the
    /// resulting array reference) consults <see cref="_newarrLiteralGlobal"/> to push the ROM global's
    /// address instead of heap-allocating.
    /// </summary>
    private void DetectArrayLiteralIdioms()
    {
        foreach (var instr in _method.Body.Instructions)
        {
            if (instr.OpCode.Code != Code.Newarr)
                continue;
            var dup = instr.Next;
            if (dup?.OpCode.Code != Code.Dup)
                continue;
            var ldtoken = dup.Next;
            if (ldtoken?.OpCode.Code != Code.Ldtoken)
                continue;
            var call = ldtoken.Next;
            if (
                call?.OpCode.Code != Code.Call
                || call.Operand is not MethodReference callee
                || callee.Name != "InitializeArray"
                || callee.DeclaringType.FullName != "System.Runtime.CompilerServices.RuntimeHelpers"
            )
                continue;

            // The count immediately preceding 'newarr' must be the SAME compile-time constant the
            // blob's own byte length implies — the same cross-check
            // CilStaticFieldSupport.TryMatchArrayLiteral makes for the static-field shape, guarding
            // against a hypothetical non-Roslyn IL shape where the two disagree.
            if (
                !CilStaticFieldSupport.TryReadConstLong(
                    CilStaticFieldSupport.Prev(instr),
                    out var count
                )
            )
                continue;

            IrType elemType;
            try
            {
                (elemType, _) = CilTypeMapper.Map((TypeReference)instr.Operand);
            }
            catch (CilNotSupportedException)
            {
                continue;
            }
            var dataField = (ldtoken.Operand as FieldReference)?.Resolve();
            var bytes = dataField?.InitialValue;
            var elemSize = Math.Max(elemType.SizeInBytes, 1);
            if (bytes is null || bytes.Length != count * elemSize)
                continue;

            var romGlobal = _ctx.EnsureRvaBlobGlobal(dataField!);
            _newarrLiteralGlobal[instr] = (romGlobal, elemType, (int)count);
            _suppressed.Add(dup);
            _suppressed.Add(ldtoken);
            _suppressed.Add(call);
        }
    }

    /// <summary>The literal branch of <c>Code.Newarr</c>'s dispatch (see
    /// <see cref="DetectArrayLiteralIdioms"/>): the runtime count already on the stack was already
    /// cross-checked against the blob's own byte length at compile time, so it is simply discarded —
    /// no heap allocation, no zero-fill, just the ROM global's address with the same array-info
    /// provenance an ordinary heap <c>newarr</c> would carry.</summary>
    private void LowerNewarrLiteral(
        (IrGlobal Global, IrType ElemType, int Count) literal,
        List<IrValue> stack
    )
    {
        Pop(stack);
        var ptr = IrBuilder.GlobalRef(literal.Global);
        _pendingArrayInfo[ptr] = (
            literal.ElemType,
            false,
            IrBuilder.ConstInt(IrType.I16, literal.Count)
        );
        stack.Add(ptr);
    }

    /// <summary><c>newarr</c>: bump the shared heap pointer down by <c>count * sizeof(element)</c>,
    /// zero-fill it (a dynamic-size variant of <see cref="EmitZeroFill"/> — the count is a runtime
    /// value, not necessarily a compile-time constant), and record the array's element
    /// type/signedness/count for later LINQ/element-access lowering.</summary>
    private void LowerNewarr(TypeReference elementTypeRef, List<IrValue> stack)
    {
        // A struct element (e.g. `new Point[3]`) has no scalar IrType to report — its bytes are laid
        // out exactly like a struct local (see CilMethodLowerer.Structs.cs). elemType/elemSigned below
        // are placeholders in that case: struct array elements are never consumed as scalars (no LINQ
        // pipeline over a struct array is in scope), only via Ldelema/the generic Ldelem/Stelem (see
        // ElementPointerGeneric), which re-derive the real per-access size from their own type operand.
        IrType elemType;
        bool elemSigned;
        int elemSize;
        if (CilStructSupport.ResolveStruct(elementTypeRef) is { } structDef)
        {
            elemSize = Math.Max(_ctx.GetLayout(structDef).Size, 1);
            elemType = IrType.I8;
            elemSigned = false;
        }
        else
        {
            (elemType, elemSigned) = CilTypeMapper.Map(elementTypeRef);
            elemSize = Math.Max(elemType.SizeInBytes, 1);
        }
        var count = Pop(stack);
        var count16 = CoerceStore(count, IrType.I16);
        var sizeBytes = _b.Mul(count16, IrBuilder.ConstInt(IrType.I16, elemSize));

        var heap =
            _ctx.HeapGlobal
            ?? throw new CilNotSupportedException(
                $"'newarr {elementTypeRef.FullName}' in '{_method.FullName}' needs the heap, but none "
                    + "was provisioned for this module."
            );
        var heapRef = IrBuilder.GlobalRef(heap);
        var raw = _b.Sub(_b.Load(heapRef), sizeBytes);
        _b.Store(raw, heapRef);
        var basePtr = _b.Conv(IrConvOp.Bitcast, raw, IrType.Pointer(IrType.I8));
        EmitZeroFillDynamic(basePtr, sizeBytes);

        _pendingArrayInfo[basePtr] = (elemType, elemSigned, count16);
        stack.Add(basePtr);
    }

    /// <summary>Dynamic-size sibling of <see cref="EmitZeroFill"/> (that one takes a compile-time
    /// <c>int</c> size — a <c>newobj</c> class instance's laid-out size is always known statically;
    /// a <c>newarr</c> count generally is not).</summary>
    private void EmitZeroFillDynamic(IrValue basePtr, IrValue sizeBytes)
    {
        var iSlot = _b.Alloca(IrType.I16);
        _b.Store(IrBuilder.ConstInt(IrType.I16, 0), iSlot);
        var head = _function.AppendBlock("newarr.zero.head");
        var body = _function.AppendBlock("newarr.zero.body");
        var done = _function.AppendBlock("newarr.zero.done");
        _b.Br(head);
        _b.PositionAtEnd(head);
        _b.CondBr(_b.Compare(IrCompareOp.Ult, _b.Load(iSlot), sizeBytes), body, done);
        _b.PositionAtEnd(body);
        _b.Store(IrBuilder.ConstInt(IrType.I8, 0), _b.Gep(basePtr, _b.Load(iSlot), IrType.I8));
        _b.Store(_b.Add(_b.Load(iSlot), IrBuilder.ConstInt(IrType.I16, 1)), iSlot);
        _b.Br(head);
        _b.PositionAtEnd(done);
    }

    /// <summary>The address of element <paramref name="index"/> in an array based at
    /// <paramref name="arrayRef"/> — an element-scaled <c>gep</c>, exactly
    /// <see cref="FieldPointer"/>'s addressing (the base pointer's own element type doesn't matter to
    /// <c>gep</c>; only <paramref name="elementType"/> does).</summary>
    private IrValue ElementPointer(IrValue arrayRef, IrValue index, IrType elementType) =>
        _b.Gep(arrayRef, CoerceStore(index, IrType.I16), elementType);

    private void StoreElem(List<IrValue> stack, IrType elementType)
    {
        var value = Pop(stack);
        var index = Pop(stack);
        var arrayRef = Pop(stack);
        _b.Store(CoerceStore(value, elementType), ElementPointer(arrayRef, index, elementType));
    }

    private void LoadElem(List<IrValue> stack, IrType elementType, bool signed)
    {
        var index = Pop(stack);
        var arrayRef = Pop(stack);
        stack.Add(WidenToStack(_b.Load(ElementPointer(arrayRef, index, elementType)), signed));
    }

    // ---- Generic ldelema/ldelem/stelem (a struct element type — Roslyn always uses the typed
    // ldelem.*/stelem.* opcodes above for a primitive) ------------------------------------------------

    /// <summary>The address of element <paramref name="index"/> for the generic (type-operand-carrying)
    /// <c>ldelema</c>/<c>ldelem</c>/<c>stelem</c> opcodes: a struct element indexes by its whole laid-
    /// out size (an <c>Array(I8,size)</c>-typed <c>gep</c> bitcast down to a plain byte pointer —
    /// exactly <c>CSharpFrontend.CollectStructs</c>'s own struct-array element addressing); a scalar
    /// element falls back to the ordinary typed <see cref="ElementPointer"/>.</summary>
    private IrValue ElementPointerGeneric(IrValue arrayRef, IrValue index, TypeReference typeRef)
    {
        if (CilStructSupport.ResolveStruct(typeRef) is { } structDef)
        {
            var layout = _ctx.GetLayout(structDef);
            var elementPtr = _b.Gep(
                arrayRef,
                CoerceStore(index, IrType.I16),
                IrType.Array(IrType.I8, Math.Max(layout.Size, 1))
            );
            return _b.Conv(IrConvOp.Bitcast, elementPtr, IrType.Pointer(IrType.I8));
        }
        var (irType, _) = CilTypeMapper.Map(typeRef);
        return ElementPointer(arrayRef, index, irType);
    }

    private void LowerLdelema(TypeReference typeRef, List<IrValue> stack)
    {
        var index = Pop(stack);
        var arrayRef = Pop(stack);
        stack.Add(ElementPointerGeneric(arrayRef, index, typeRef));
    }

    private void LowerLdelemAny(TypeReference typeRef, List<IrValue> stack)
    {
        var index = Pop(stack);
        var arrayRef = Pop(stack);
        if (CilStructSupport.ResolveStruct(typeRef) is not null)
        {
            // Same "the value IS its address" representation as every other struct read — see
            // CilMethodLowerer.Structs.cs's class remarks.
            stack.Add(ElementPointerGeneric(arrayRef, index, typeRef));
            return;
        }
        var (irType, signed) = CilTypeMapper.Map(typeRef);
        stack.Add(WidenToStack(_b.Load(ElementPointer(arrayRef, index, irType)), signed));
    }

    /// <summary><c>ldlen</c>: this frontend's arrays carry no runtime length header (see class
    /// remarks) — the element count only exists as the compile-time <see cref="_pendingArrayInfo"/>/
    /// <see cref="_localArrayInfo"/> side channel a <c>newarr</c> seeds and <c>LoadLocal</c>/
    /// <c>StoreLocal</c>/<c>Dup</c> carry forward, exactly the provenance <see cref="ResolvePipeline"/>
    /// (<c>CilMethodLowerer.Linq.cs</c>) already relies on for the same reason. Pushes the tracked
    /// count (already <see cref="IrType.I16"/> — this target's native-int width, matching real CLR
    /// <c>ldlen</c>'s "native unsigned int" result) straight back; Roslyn's usual
    /// <c>ldlen ; conv.i4</c> shape for <c>array.Length</c> then passes it through unchanged (conv.i4
    /// only truncates a wider-than-32-bit value — see <c>Code.Conv_I4</c>'s case). An array that
    /// can't be traced to a <c>newarr</c> in THIS method (received as a parameter, returned from a
    /// call, or read back out of a field/array element) has no tracked count at all — a diagnostic,
    /// not a wrong answer.</summary>
    private void LowerLdlen(List<IrValue> stack)
    {
        var arrayRef = Pop(stack);
        if (!_pendingArrayInfo.TryGetValue(arrayRef, out var info))
            throw new CilNotSupportedException(
                $"'ldlen' in '{_method.FullName}' could not be traced to a 'newarr'-allocated array in "
                    + "this method (this frontend's arrays carry no runtime length header — an array "
                    + "received as a parameter, returned from a call, or read from a field/element is "
                    + "out of scope for 'array.Length')."
            );
        stack.Add(info.Count);
    }

    private void LowerStelemAny(TypeReference typeRef, List<IrValue> stack)
    {
        var value = Pop(stack);
        var index = Pop(stack);
        var arrayRef = Pop(stack);
        if (CilStructSupport.ResolveStruct(typeRef) is { } structDef)
        {
            EmitCopy(
                ElementPointerGeneric(arrayRef, index, typeRef),
                value,
                _ctx.GetLayout(structDef).Size
            );
            return;
        }
        var (irType, _) = CilTypeMapper.Map(typeRef);
        _b.Store(CoerceStore(value, irType), ElementPointer(arrayRef, index, irType));
    }
}
