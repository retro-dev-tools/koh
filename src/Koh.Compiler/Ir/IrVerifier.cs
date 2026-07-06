namespace Koh.Compiler.Ir;

/// <summary>
/// Structural validation of a module. Returns a list of human-readable errors (empty means
/// valid). This is the safety net for hand-written IR and for frontend output — it checks CFG
/// well-formedness (terminators, successor membership) and operand type agreement, not
/// semantics.
/// </summary>
public static class IrVerifier
{
    public static IReadOnlyList<string> Verify(IrModule module)
    {
        var errors = new List<string>();
        foreach (var f in module.Functions)
            VerifyFunction(f, errors);
        return errors;
    }

    private static void VerifyFunction(IrFunction f, List<string> errors)
    {
        void Err(string message) => errors.Add($"function '@{f.Name}': {message}");

        if (f.IsExternal)
        {
            if (f.Blocks.Count > 0)
                Err("external function must have no body");
            return;
        }

        if (f.Blocks.Count == 0)
        {
            Err("non-external function must have at least one block");
            return;
        }

        var blockSet = new HashSet<IrBasicBlock>(f.Blocks, ReferenceEqualityComparer.Instance);

        foreach (var block in f.Blocks)
        {
            var label = block.Name ?? "<anon>";

            if (block.Instructions.Count == 0)
            {
                Err($"block '{label}' is empty");
                continue;
            }

            for (int i = 0; i < block.Instructions.Count; i++)
            {
                var instr = block.Instructions[i];
                bool isLast = i == block.Instructions.Count - 1;

                if (instr.IsTerminator && !isLast)
                    Err($"block '{label}': terminator '{instr.Mnemonic}' is not the last instruction");
                if (!instr.IsTerminator && isLast)
                    Err($"block '{label}': block does not end in a terminator");

                foreach (var succ in instr.Successors)
                    if (!blockSet.Contains(succ))
                        Err($"block '{label}': '{instr.Mnemonic}' targets a block outside this function");

                VerifyInstruction(f, label, instr, blockSet, Err);
            }
        }
    }

    private static void VerifyInstruction(
        IrFunction f,
        string label,
        IrInstruction instr,
        HashSet<IrBasicBlock> blockSet,
        Action<string> err)
    {
        switch (instr)
        {
            case BinaryInstruction b:
                if (!IsInt(b.Left.Type) || !IsInt(b.Right.Type))
                    err($"block '{label}': '{b.Mnemonic}' requires integer operands");
                else if (!b.Left.Type.StructurallyEquals(b.Right.Type))
                    err($"block '{label}': '{b.Mnemonic}' operand types differ ({b.Left.Type} vs {b.Right.Type})");
                break;

            case CompareInstruction c:
                if (!IsInt(c.Left.Type) || !c.Left.Type.StructurallyEquals(c.Right.Type))
                    err($"block '{label}': 'icmp' requires matching integer operands");
                break;

            case ConvInstruction { Op: IrConvOp.Bitcast } bc:
                // A reinterpret: operand and result must occupy the same storage (int<->pointer of
                // the address width, or two pointer types).
                if (bc.Type.SizeInBytes != bc.Operand.Type.SizeInBytes)
                    err($"block '{label}': 'bitcast' requires equal-size operand and result "
                        + $"({bc.Operand.Type} vs {bc.Type})");
                break;

            case ConvInstruction cv:
                if (!IsInt(cv.Operand.Type) || !IsInt(cv.Type))
                    err($"block '{label}': '{cv.Mnemonic}' requires integer operand and result");
                else if (cv.Op == IrConvOp.Trunc && cv.Type.Bits >= cv.Operand.Type.Bits)
                    err($"block '{label}': 'trunc' target must be narrower than source");
                else if (cv.Op != IrConvOp.Trunc && cv.Type.Bits <= cv.Operand.Type.Bits)
                    err($"block '{label}': '{cv.Mnemonic}' target must be wider than source");
                break;

            case LoadInstruction l:
                if (l.Pointer.Type.Kind != IrTypeKind.Pointer)
                    err($"block '{label}': 'load' operand must be a pointer");
                break;

            case StoreInstruction s:
                if (s.Pointer.Type.Kind != IrTypeKind.Pointer)
                    err($"block '{label}': 'store' target must be a pointer");
                else if (s.Pointer.Type.Element is { } pointee && !pointee.StructurallyEquals(s.Value.Type))
                    err($"block '{label}': 'store' value type {s.Value.Type} does not match pointee {pointee}");
                break;

            case GetElementPtrInstruction g:
                if (g.BasePointer.Type.Kind != IrTypeKind.Pointer)
                    err($"block '{label}': 'gep' base must be a pointer");
                if (!IsInt(g.Index.Type))
                    err($"block '{label}': 'gep' index must be an integer");
                break;

            case CallInstruction call:
                if (call.Arguments.Count != call.Callee.Parameters.Count)
                    err($"block '{label}': call to '@{call.Callee.Name}' has {call.Arguments.Count} args, expected {call.Callee.Parameters.Count}");
                else
                    for (int i = 0; i < call.Arguments.Count; i++)
                        if (!call.Arguments[i].Type.StructurallyEquals(call.Callee.Parameters[i].Type))
                            err($"block '{label}': call to '@{call.Callee.Name}' arg {i} type {call.Arguments[i].Type} != {call.Callee.Parameters[i].Type}");
                break;

            case PhiInstruction phi:
                foreach (var (val, blk) in phi.Incomings)
                {
                    if (!val.Type.StructurallyEquals(phi.Type))
                        err($"block '{label}': 'phi' incoming type {val.Type} != {phi.Type}");
                    if (!blockSet.Contains(blk))
                        err($"block '{label}': 'phi' references a block outside this function");
                }
                break;

            case RetInstruction r:
                if (r.Value is null && f.ReturnType.Kind != IrTypeKind.Void)
                    err($"block '{label}': 'ret void' in function returning {f.ReturnType}");
                else if (r.Value is { } rv && !rv.Type.StructurallyEquals(f.ReturnType))
                    err($"block '{label}': 'ret' type {rv.Type} != function return {f.ReturnType}");
                break;

            case SwitchInstruction sw:
                if (!IsInt(sw.Value.Type))
                    err($"block '{label}': 'switch' value must be an integer");
                break;
        }
    }

    private static bool IsInt(IrType t) => t.Kind == IrTypeKind.Int;
}
