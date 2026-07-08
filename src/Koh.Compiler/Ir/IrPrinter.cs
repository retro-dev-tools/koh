using System.Globalization;
using System.Text;
using Koh.Compiler.Targets;

namespace Koh.Compiler.Ir;

/// <summary>
/// Renders a module to the textual IR form. The grammar is designed to round-trip through
/// <see cref="IrParser"/>: every operand carries enough type context to be parsed without
/// inference beyond a pointer's element type. Value slots and block labels are assigned
/// deterministically per function.
/// </summary>
public static class IrPrinter
{
    public static string Print(IrModule module)
    {
        var sb = new StringBuilder();
        sb.Append("module \"").Append(module.Name).Append("\"\n");

        if (module.Globals.Count > 0)
        {
            sb.Append('\n');
            foreach (var g in module.Globals)
                PrintGlobal(sb, g);
        }

        foreach (var f in module.Functions)
        {
            sb.Append('\n');
            PrintFunction(sb, f);
        }

        return sb.ToString();
    }

    private static void PrintGlobal(StringBuilder sb, IrGlobal g)
    {
        sb.Append("global @").Append(g.Name).Append(" : ").Append(g.Type);
        if (g.AddressSpace != AddressSpace.Default)
            sb.Append(" addrspace(").Append(SpaceName(g.AddressSpace)).Append(')');
        if (g.Bank is int bank)
            sb.Append(" bank(").Append(bank).Append(')');
        if (g.FixedAddress is int fixedAddress)
            sb.Append(" addr(0x")
                .Append(fixedAddress.ToString("x4", CultureInfo.InvariantCulture))
                .Append(')');
        if (g.Section is string section)
            sb.Append(" section(\"").Append(section).Append("\")");
        if (g.Initializer is byte[] init)
        {
            sb.Append(" = ");
            for (int i = 0; i < init.Length; i++)
            {
                if (i > 0)
                    sb.Append(", ");
                sb.Append("0x").Append(init[i].ToString("x2", CultureInfo.InvariantCulture));
            }
        }
        sb.Append('\n');
    }

    private static void PrintFunction(StringBuilder sb, IrFunction f)
    {
        var names = new Naming(f);

        if (f.IsExternal)
            sb.Append("extern ");
        sb.Append("func @").Append(f.Name).Append('(');
        for (int i = 0; i < f.Parameters.Count; i++)
        {
            if (i > 0)
                sb.Append(", ");
            var p = f.Parameters[i];
            sb.Append('%').Append(names.Value(p)).Append(" : ").Append(p.Type);
        }
        sb.Append(") : ").Append(f.ReturnType);
        if (f.Bank is int bank)
            sb.Append(" bank(").Append(bank).Append(')');

        if (f.IsExternal)
        {
            sb.Append('\n');
            return;
        }

        sb.Append(" {\n");
        foreach (var block in f.Blocks)
        {
            sb.Append(names.Block(block)).Append(":\n");
            foreach (var instr in block.Instructions)
            {
                sb.Append("  ");
                PrintInstruction(sb, instr, names);
                sb.Append('\n');
            }
        }
        sb.Append("}\n");
    }

    private static void PrintInstruction(StringBuilder sb, IrInstruction instr, Naming names)
    {
        if (instr.Type.Kind != IrTypeKind.Void)
            sb.Append('%').Append(names.Value(instr)).Append(" = ");

        switch (instr)
        {
            case BinaryInstruction b:
                sb.Append(b.Mnemonic)
                    .Append(' ')
                    .Append(b.Left.Type)
                    .Append(' ')
                    .Append(Op(b.Left, names))
                    .Append(", ")
                    .Append(Op(b.Right, names));
                break;
            case CompareInstruction c:
                sb.Append("icmp ")
                    .Append(c.Op.ToString().ToLowerInvariant())
                    .Append(' ')
                    .Append(c.Left.Type)
                    .Append(' ')
                    .Append(Op(c.Left, names))
                    .Append(", ")
                    .Append(Op(c.Right, names));
                break;
            case ConvInstruction cv:
                sb.Append(cv.Mnemonic)
                    .Append(' ')
                    .Append(cv.Operand.Type)
                    .Append(' ')
                    .Append(Op(cv.Operand, names))
                    .Append(" to ")
                    .Append(cv.Type);
                break;
            case AllocaInstruction a:
                sb.Append("alloca ").Append(a.Allocated);
                break;
            case LoadInstruction l:
                sb.Append("load ").Append(l.Pointer.Type).Append(' ').Append(Op(l.Pointer, names));
                break;
            case StoreInstruction s:
                sb.Append("store ")
                    .Append(s.Value.Type)
                    .Append(' ')
                    .Append(Op(s.Value, names))
                    .Append(", ")
                    .Append(s.Pointer.Type)
                    .Append(' ')
                    .Append(Op(s.Pointer, names));
                break;
            case GetElementPtrInstruction g:
                sb.Append("gep ")
                    .Append(g.ElementType)
                    .Append(", ")
                    .Append(g.BasePointer.Type)
                    .Append(' ')
                    .Append(Op(g.BasePointer, names))
                    .Append(", ")
                    .Append(g.Index.Type)
                    .Append(' ')
                    .Append(Op(g.Index, names));
                break;
            case CallInstruction call:
                sb.Append("call ")
                    .Append(call.Callee.ReturnType)
                    .Append(" @")
                    .Append(call.Callee.Name)
                    .Append('(');
                for (int i = 0; i < call.Arguments.Count; i++)
                {
                    if (i > 0)
                        sb.Append(", ");
                    sb.Append(call.Arguments[i].Type)
                        .Append(' ')
                        .Append(Op(call.Arguments[i], names));
                }
                sb.Append(')');
                break;
            case IntrinsicInstruction intr:
                sb.Append("intrinsic \"").Append(intr.Intrinsic).Append('"');
                break;
            case PhiInstruction phi:
                sb.Append("phi ").Append(phi.Type).Append(' ');
                for (int i = 0; i < phi.Incomings.Count; i++)
                {
                    if (i > 0)
                        sb.Append(", ");
                    var (val, blk) = phi.Incomings[i];
                    sb.Append("[ ")
                        .Append(Op(val, names))
                        .Append(", ")
                        .Append(names.Block(blk))
                        .Append(" ]");
                }
                break;
            case RetInstruction r:
                if (r.Value is null)
                    sb.Append("ret void");
                else
                    sb.Append("ret ").Append(r.Value.Type).Append(' ').Append(Op(r.Value, names));
                break;
            case BrInstruction br:
                sb.Append("br ").Append(names.Block(br.Target));
                break;
            case CondBrInstruction cb:
                sb.Append("condbr ")
                    .Append(Op(cb.Condition, names))
                    .Append(", ")
                    .Append(names.Block(cb.IfTrue))
                    .Append(", ")
                    .Append(names.Block(cb.IfFalse));
                break;
            case SwitchInstruction sw:
                sb.Append("switch ")
                    .Append(sw.Value.Type)
                    .Append(' ')
                    .Append(Op(sw.Value, names))
                    .Append(", ")
                    .Append(names.Block(sw.Default))
                    .Append(" [ ");
                for (int i = 0; i < sw.Cases.Count; i++)
                {
                    if (i > 0)
                        sb.Append(", ");
                    sb.Append(sw.Cases[i].Case.Value)
                        .Append(": ")
                        .Append(names.Block(sw.Cases[i].Target));
                }
                sb.Append(" ]");
                break;
            default:
                sb.Append(instr.Mnemonic).Append(" <unprintable>");
                break;
        }
    }

    private static string Op(IrValue v, Naming names) =>
        v switch
        {
            IrConstInt c => c.Value.ToString(CultureInfo.InvariantCulture),
            IrGlobalRef g => "@" + g.Global.Name,
            _ => "%" + names.Value(v),
        };

    internal static string SpaceName(AddressSpace space) => space.ToString().ToLowerInvariant();

    /// <summary>Deterministic per-function assignment of value slots and block labels.</summary>
    private sealed class Naming
    {
        private readonly Dictionary<IrValue, string> _values = new(
            ReferenceEqualityComparer.Instance
        );
        private readonly Dictionary<IrBasicBlock, string> _blocks = new(
            ReferenceEqualityComparer.Instance
        );

        public Naming(IrFunction f)
        {
            int slot = 0;
            foreach (var p in f.Parameters)
                _values[p] = p.Name ?? (slot++).ToString(CultureInfo.InvariantCulture);

            int bb = 0;
            foreach (var block in f.Blocks)
                _blocks[block] = block.Name ?? "bb" + bb++;

            foreach (var block in f.Blocks)
            foreach (var instr in block.Instructions)
                if (instr.Type.Kind != IrTypeKind.Void)
                    _values[instr] = instr.Name ?? (slot++).ToString(CultureInfo.InvariantCulture);
        }

        public string Value(IrValue v) => _values.TryGetValue(v, out var n) ? n : "<undef>";

        public string Block(IrBasicBlock b) => _blocks.TryGetValue(b, out var n) ? n : "<unknown>";
    }
}
