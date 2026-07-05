using Koh.Compiler.Ir;
using Koh.Compiler.Targets;
using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Core.Symbols;

namespace Koh.Compiler.Backends.Sm83;

/// <summary>
/// The hand-written SM83 backend. This is the Phase 2 MVP: correctness-first, non-optimizing
/// code generation for single-block <c>i8</c> functions, proving the whole pipeline end to end
/// (IR → machine code → <see cref="EmitModel"/> → linker → ROM → emulator).
///
/// The allocation model is the simplest form of the NESFab-style static allocation the design
/// calls for: every value-producing instruction gets its own fixed WRAM byte, and every
/// operation flows through the accumulator (load operands into <c>A</c>/<c>B</c>, compute,
/// store the result back to its slot). It emits terrible code on purpose — the point of the MVP
/// is a trustworthy, observable pipeline, not tight output. Register allocation, wider integer
/// legalization, control flow, calls, and instruction selection over
/// <see cref="Koh.Core.Encoding.Sm83InstructionTable"/> are later Phase 2 increments.
///
/// Unsupported IR throws <see cref="NotSupportedException"/> so the MVP's boundary is explicit.
/// </summary>
public sealed class Sm83Backend : IBackend
{
    /// <summary>Fixed ROM address the emitted code section is placed at (MVP: one section).</summary>
    public const int CodeBase = 0x0150;

    /// <summary>First WRAM byte used for statically-allocated SSA value slots.</summary>
    private const int WramBase = 0xC000;

    private const string CodeSectionName = "CODE";

    public string Name => "sm83";

    public TargetInfo Target => TargetInfo.Sm83;

    public EmitModel Compile(IrModule module, DiagnosticBag diagnostics)
    {
        var code = new List<byte>();
        var symbols = new List<SymbolData>();

        foreach (var fn in module.Functions)
        {
            if (fn.IsExternal)
                continue;

            int funcStart = CodeBase + code.Count;
            CompileFunction(fn, code);
            symbols.Add(new SymbolData(
                fn.Name, SymbolKind.Label, SymbolVisibility.Exported, CodeSectionName, funcStart));
        }

        var section = new SectionData(
            CodeSectionName, SectionType.Rom0, fixedAddress: CodeBase, bank: 0,
            data: code.ToArray(), patches: Array.Empty<PatchEntry>());

        return new EmitModel([section], symbols, Array.Empty<Diagnostic>());
    }

    private static void CompileFunction(IrFunction fn, List<byte> code)
    {
        if (fn.Blocks.Count != 1)
            throw new NotSupportedException(
                $"MVP SM83 backend supports single-block functions only ('@{fn.Name}' has {fn.Blocks.Count}).");

        var block = fn.Blocks[0];

        // Static allocation: one fixed WRAM byte per value-producing instruction.
        var slots = new Dictionary<IrInstruction, int>(ReferenceEqualityComparer.Instance);
        int next = WramBase;
        foreach (var instr in block.Instructions)
            if (instr.Type.Kind != IrTypeKind.Void)
                slots[instr] = next++;

        foreach (var instr in block.Instructions)
        {
            switch (instr)
            {
                case BinaryInstruction b:
                    EmitBinary(b, code, slots);
                    break;
                case RetInstruction r:
                    if (r.Value is not null)
                        LoadToA(r.Value, code, slots);
                    code.Add(0xC9); // RET
                    break;
                default:
                    throw new NotSupportedException(
                        $"MVP SM83 backend does not support '{instr.Mnemonic}' (in '@{fn.Name}').");
            }
        }
    }

    private static void EmitBinary(
        BinaryInstruction b, List<byte> code, Dictionary<IrInstruction, int> slots)
    {
        if (b.Type.Bits != 8)
            throw new NotSupportedException(
                $"MVP SM83 backend supports i8 arithmetic only (got {b.Type} for '{b.Mnemonic}').");

        if (b.Right is IrConstInt rc)
        {
            LoadToA(b.Left, code, slots);
            code.Add(AluImmOpcode(b.Op));
            code.Add((byte)rc.Value);
        }
        else
        {
            LoadToB(b.Right, code, slots); // B = right
            LoadToA(b.Left, code, slots);  // A = left
            code.Add(AluRegOpcode(b.Op));  // A = A op B
        }

        StoreA(slots[b], code);
    }

    /// <summary>Load a value into <c>A</c>: constants become immediates, results load from their slot.</summary>
    private static void LoadToA(IrValue value, List<byte> code, Dictionary<IrInstruction, int> slots)
    {
        switch (value)
        {
            case IrConstInt c:
                code.Add(0x3E);            // LD A, d8
                code.Add((byte)c.Value);
                break;
            case IrInstruction instr when slots.TryGetValue(instr, out int addr):
                code.Add(0xFA);            // LD A, (a16)
                code.Add((byte)(addr & 0xFF));
                code.Add((byte)(addr >> 8));
                break;
            default:
                throw new NotSupportedException(
                    "MVP SM83 backend operands must be i8 constants or prior instruction results.");
        }
    }

    /// <summary>Load a value into <c>B</c> (via <c>A</c>).</summary>
    private static void LoadToB(IrValue value, List<byte> code, Dictionary<IrInstruction, int> slots)
    {
        LoadToA(value, code, slots);
        code.Add(0x47); // LD B, A
    }

    private static void StoreA(int addr, List<byte> code)
    {
        code.Add(0xEA); // LD (a16), A
        code.Add((byte)(addr & 0xFF));
        code.Add((byte)(addr >> 8));
    }

    private static byte AluImmOpcode(IrBinaryOp op) => op switch
    {
        IrBinaryOp.Add => 0xC6,
        IrBinaryOp.Sub => 0xD6,
        IrBinaryOp.And => 0xE6,
        IrBinaryOp.Or => 0xF6,
        IrBinaryOp.Xor => 0xEE,
        _ => throw new NotSupportedException($"MVP SM83 backend does not support '{op}'."),
    };

    private static byte AluRegOpcode(IrBinaryOp op) => op switch
    {
        IrBinaryOp.Add => 0x80, // ADD A, B
        IrBinaryOp.Sub => 0x90, // SUB B
        IrBinaryOp.And => 0xA0, // AND B
        IrBinaryOp.Or => 0xB0,  // OR B
        IrBinaryOp.Xor => 0xA8, // XOR B
        _ => throw new NotSupportedException($"MVP SM83 backend does not support '{op}'."),
    };
}
