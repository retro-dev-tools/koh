namespace Koh.Compiler.Ir;

/// <summary>
/// Convenience API for constructing IR programmatically. Instructions are appended to the
/// current insertion block; each helper returns the created instruction (its result value).
/// Used by frontends when lowering and by tests.
/// </summary>
public sealed class IrBuilder
{
    private IrBasicBlock? _block;

    /// <summary>Set the block subsequent instructions are appended to.</summary>
    public void PositionAtEnd(IrBasicBlock block) => _block = block;

    public IrBasicBlock CurrentBlock =>
        _block ?? throw new InvalidOperationException("IrBuilder has no insertion block.");

    /// <summary>Source location stamped onto each appended instruction (for debug line maps).</summary>
    public IrSourceLocation? CurrentSource { get; set; }

    private T Append<T>(T instruction) where T : IrInstruction
    {
        var block = CurrentBlock;
        instruction.Parent = block;
        instruction.Source ??= CurrentSource;
        block.Instructions.Add(instruction);
        return instruction;
    }

    public static IrConstInt ConstInt(IrType type, long value) => new(type, value);
    public static IrGlobalRef GlobalRef(IrGlobal global) => new(global);

    public BinaryInstruction Binary(IrBinaryOp op, IrValue left, IrValue right) =>
        Append(new BinaryInstruction(op, left, right));

    public BinaryInstruction Add(IrValue l, IrValue r) => Binary(IrBinaryOp.Add, l, r);
    public BinaryInstruction Sub(IrValue l, IrValue r) => Binary(IrBinaryOp.Sub, l, r);
    public BinaryInstruction Mul(IrValue l, IrValue r) => Binary(IrBinaryOp.Mul, l, r);

    public CompareInstruction Compare(IrCompareOp op, IrValue left, IrValue right) =>
        Append(new CompareInstruction(op, left, right));

    public ConvInstruction Conv(IrConvOp op, IrValue operand, IrType target) =>
        Append(new ConvInstruction(op, operand, target));

    public AllocaInstruction Alloca(IrType allocated) =>
        Append(new AllocaInstruction(allocated));

    public LoadInstruction Load(IrValue pointer) =>
        Append(new LoadInstruction(pointer));

    public StoreInstruction Store(IrValue value, IrValue pointer) =>
        Append(new StoreInstruction(value, pointer));

    public GetElementPtrInstruction Gep(IrValue basePointer, IrValue index, IrType elementType) =>
        Append(new GetElementPtrInstruction(basePointer, index, elementType));

    public CallInstruction Call(IrFunction callee, IReadOnlyList<IrValue> args) =>
        Append(new CallInstruction(callee, args));

    public PhiInstruction Phi(IrType type) =>
        Append(new PhiInstruction(type));

    public IntrinsicInstruction Intrinsic(string name) =>
        Append(new IntrinsicInstruction(name));

    public RetInstruction Ret(IrValue? value = null) =>
        Append(new RetInstruction(value));

    public BrInstruction Br(IrBasicBlock target) =>
        Append(new BrInstruction(target));

    public CondBrInstruction CondBr(IrValue condition, IrBasicBlock ifTrue, IrBasicBlock ifFalse) =>
        Append(new CondBrInstruction(condition, ifTrue, ifFalse));

    public SwitchInstruction Switch(
        IrValue value,
        IrBasicBlock defaultTarget,
        IReadOnlyList<(IrConstInt, IrBasicBlock)> cases) =>
        Append(new SwitchInstruction(value, defaultTarget, cases));
}
