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

    /// <summary><c>newarr</c>: bump the shared heap pointer down by <c>count * sizeof(element)</c>,
    /// zero-fill it (a dynamic-size variant of <see cref="EmitZeroFill"/> — the count is a runtime
    /// value, not necessarily a compile-time constant), and record the array's element
    /// type/signedness/count for later LINQ/element-access lowering.</summary>
    private void LowerNewarr(TypeReference elementTypeRef, List<IrValue> stack)
    {
        var (elemType, elemSigned) = CilTypeMapper.Map(elementTypeRef);
        var count = Pop(stack);
        var elemSize = Math.Max(elemType.SizeInBytes, 1);
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
}
