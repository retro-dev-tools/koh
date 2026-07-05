namespace Koh.Compiler.Ir;

/// <summary>Integer binary operations. Signedness is explicit where it matters (div/rem/shift).</summary>
public enum IrBinaryOp
{
    Add, Sub, Mul,
    UDiv, SDiv, URem, SRem,
    And, Or, Xor,
    Shl,   // logical shift left
    LShr,  // logical shift right
    AShr,  // arithmetic shift right
}

/// <summary>Integer comparison predicates. u = unsigned, s = signed.</summary>
public enum IrCompareOp
{
    Eq, Ne,
    Ult, Ule, Ugt, Uge,
    Slt, Sle, Sgt, Sge,
}

/// <summary>Integer width conversions.</summary>
public enum IrConvOp
{
    Trunc,  // to a narrower type
    ZExt,   // zero-extend to a wider type
    SExt,   // sign-extend to a wider type
}

/// <summary>
/// Base of all instructions. An instruction is also an <see cref="IrValue"/> — its result.
/// Instructions with no result have <see cref="IrType.Void"/> type and are not used as operands.
/// </summary>
public abstract class IrInstruction : IrValue
{
    /// <summary>The block that owns this instruction; set when appended.</summary>
    public IrBasicBlock? Parent { get; internal set; }

    protected IrInstruction(IrType type) : base(type) { }

    /// <summary>Operand values referenced by this instruction, in textual order.</summary>
    public abstract IEnumerable<IrValue> Operands { get; }

    /// <summary>True for block terminators (ret/br/condbr/switch).</summary>
    public virtual bool IsTerminator => false;

    /// <summary>Successor blocks for a terminator; empty otherwise.</summary>
    public virtual IEnumerable<IrBasicBlock> Successors => [];

    /// <summary>Mnemonic used by the printer (e.g. "add", "load").</summary>
    public abstract string Mnemonic { get; }
}

// ---- Terminators ---------------------------------------------------------

/// <summary><c>ret</c> — optionally returns a value.</summary>
public sealed class RetInstruction : IrInstruction
{
    public IrValue? Value { get; }

    public RetInstruction(IrValue? value) : base(IrType.Void) => Value = value;

    public override IEnumerable<IrValue> Operands => Value is null ? [] : [Value];
    public override bool IsTerminator => true;
    public override string Mnemonic => "ret";
}

/// <summary><c>br</c> — unconditional branch.</summary>
public sealed class BrInstruction : IrInstruction
{
    public IrBasicBlock Target { get; }

    public BrInstruction(IrBasicBlock target) : base(IrType.Void) => Target = target;

    public override IEnumerable<IrValue> Operands => [];
    public override bool IsTerminator => true;
    public override IEnumerable<IrBasicBlock> Successors => [Target];
    public override string Mnemonic => "br";
}

/// <summary><c>condbr</c> — branch on a boolean (i8) condition.</summary>
public sealed class CondBrInstruction : IrInstruction
{
    public IrValue Condition { get; }
    public IrBasicBlock IfTrue { get; }
    public IrBasicBlock IfFalse { get; }

    public CondBrInstruction(IrValue condition, IrBasicBlock ifTrue, IrBasicBlock ifFalse)
        : base(IrType.Void)
    {
        Condition = condition;
        IfTrue = ifTrue;
        IfFalse = ifFalse;
    }

    public override IEnumerable<IrValue> Operands => [Condition];
    public override bool IsTerminator => true;
    public override IEnumerable<IrBasicBlock> Successors => [IfTrue, IfFalse];
    public override string Mnemonic => "condbr";
}

/// <summary><c>switch</c> — multi-way branch on an integer value.</summary>
public sealed class SwitchInstruction : IrInstruction
{
    public IrValue Value { get; }
    public IrBasicBlock Default { get; }
    public IReadOnlyList<(IrConstInt Case, IrBasicBlock Target)> Cases { get; }

    public SwitchInstruction(
        IrValue value,
        IrBasicBlock defaultTarget,
        IReadOnlyList<(IrConstInt, IrBasicBlock)> cases)
        : base(IrType.Void)
    {
        Value = value;
        Default = defaultTarget;
        Cases = cases;
    }

    public override IEnumerable<IrValue> Operands => [Value];
    public override bool IsTerminator => true;
    public override IEnumerable<IrBasicBlock> Successors
    {
        get
        {
            yield return Default;
            foreach (var (_, target) in Cases)
                yield return target;
        }
    }
    public override string Mnemonic => "switch";
}

// ---- Arithmetic / logic --------------------------------------------------

/// <summary>An integer binary operation; both operands and the result share one type.</summary>
public sealed class BinaryInstruction : IrInstruction
{
    public IrBinaryOp Op { get; }
    public IrValue Left { get; }
    public IrValue Right { get; }

    public BinaryInstruction(IrBinaryOp op, IrValue left, IrValue right)
        : base(left.Type)
    {
        Op = op;
        Left = left;
        Right = right;
    }

    public override IEnumerable<IrValue> Operands => [Left, Right];
    public override string Mnemonic => Op.ToString().ToLowerInvariant();
}

/// <summary>An integer comparison; result is a boolean (<see cref="IrType.I8"/>).</summary>
public sealed class CompareInstruction : IrInstruction
{
    public IrCompareOp Op { get; }
    public IrValue Left { get; }
    public IrValue Right { get; }

    public CompareInstruction(IrCompareOp op, IrValue left, IrValue right)
        : base(IrType.I8)
    {
        Op = op;
        Left = left;
        Right = right;
    }

    public override IEnumerable<IrValue> Operands => [Left, Right];
    public override string Mnemonic => "icmp";
}

/// <summary>An integer width conversion (trunc/zext/sext).</summary>
public sealed class ConvInstruction : IrInstruction
{
    public IrConvOp Op { get; }
    public IrValue Operand { get; }

    public ConvInstruction(IrConvOp op, IrValue operand, IrType target)
        : base(target)
    {
        Op = op;
        Operand = operand;
    }

    public override IEnumerable<IrValue> Operands => [Operand];
    public override string Mnemonic => Op.ToString().ToLowerInvariant();
}

// ---- Memory --------------------------------------------------------------

/// <summary><c>alloca</c> — reserve storage for a value; result is a pointer to it.</summary>
public sealed class AllocaInstruction : IrInstruction
{
    public IrType Allocated { get; }

    public AllocaInstruction(IrType allocated)
        : base(IrType.Pointer(allocated)) => Allocated = allocated;

    public override IEnumerable<IrValue> Operands => [];
    public override string Mnemonic => "alloca";
}

/// <summary><c>load</c> — read the value a pointer refers to.</summary>
public sealed class LoadInstruction : IrInstruction
{
    public IrValue Pointer { get; }

    public LoadInstruction(IrValue pointer)
        : base(pointer.Type.Element ?? IrType.Void) => Pointer = pointer;

    public override IEnumerable<IrValue> Operands => [Pointer];
    public override string Mnemonic => "load";
}

/// <summary><c>store</c> — write a value through a pointer; produces no result.</summary>
public sealed class StoreInstruction : IrInstruction
{
    public IrValue Value { get; }
    public IrValue Pointer { get; }

    public StoreInstruction(IrValue value, IrValue pointer) : base(IrType.Void)
    {
        Value = value;
        Pointer = pointer;
    }

    public override IEnumerable<IrValue> Operands => [Value, Pointer];
    public override string Mnemonic => "store";
}

/// <summary><c>gep</c> — compute <c>base + index</c> as a pointer to an element.</summary>
public sealed class GetElementPtrInstruction : IrInstruction
{
    public IrValue BasePointer { get; }
    public IrValue Index { get; }
    public IrType ElementType { get; }

    public GetElementPtrInstruction(IrValue basePointer, IrValue index, IrType elementType)
        : base(IrType.Pointer(elementType, basePointer.Type.AddressSpace))
    {
        BasePointer = basePointer;
        Index = index;
        ElementType = elementType;
    }

    public override IEnumerable<IrValue> Operands => [BasePointer, Index];
    public override string Mnemonic => "gep";
}

// ---- Calls and phis ------------------------------------------------------

/// <summary><c>call</c> — a direct call; result type is the callee's return type.</summary>
public sealed class CallInstruction : IrInstruction
{
    public IrFunction Callee { get; }
    public IReadOnlyList<IrValue> Arguments { get; }

    public CallInstruction(IrFunction callee, IReadOnlyList<IrValue> arguments)
        : base(callee.ReturnType)
    {
        Callee = callee;
        Arguments = arguments;
    }

    public override IEnumerable<IrValue> Operands => Arguments;
    public override string Mnemonic => "call";
}

/// <summary><c>phi</c> — selects a value based on the predecessor block control came from.</summary>
public sealed class PhiInstruction : IrInstruction
{
    private readonly List<(IrValue Value, IrBasicBlock Block)> _incomings;

    public PhiInstruction(IrType type) : base(type) => _incomings = [];

    public IReadOnlyList<(IrValue Value, IrBasicBlock Block)> Incomings => _incomings;

    public void AddIncoming(IrValue value, IrBasicBlock block) => _incomings.Add((value, block));

    public override IEnumerable<IrValue> Operands => _incomings.Select(i => i.Value);
    public override string Mnemonic => "phi";
}
