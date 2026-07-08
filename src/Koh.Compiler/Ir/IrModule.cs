using Koh.Compiler.Targets;

namespace Koh.Compiler.Ir;

/// <summary>
/// A frontend's output and a backend's input: the complete, target-independent program.
/// A module owns its functions and global data.
/// </summary>
public sealed class IrModule
{
    public string Name { get; }
    public List<IrFunction> Functions { get; } = [];
    public List<IrGlobal> Globals { get; } = [];

    public IrModule(string name) => Name = name;

    public IrFunction? FindFunction(string name) => Functions.FirstOrDefault(f => f.Name == name);

    public IrGlobal? FindGlobal(string name) => Globals.FirstOrDefault(g => g.Name == name);
}

/// <summary>
/// A function: a signature plus, for non-external functions, a body of basic blocks in an
/// SSA control-flow graph. The first block is the entry.
/// </summary>
public sealed class IrFunction
{
    public string Name { get; }
    public IrType ReturnType { get; }
    public IReadOnlyList<IrParameter> Parameters { get; }

    /// <summary>ROM bank this function is placed in; null means the linker/backend decides.</summary>
    public int? Bank { get; }

    /// <summary>True for an <c>extern</c> declaration (e.g. an assembly symbol) with no body.</summary>
    public bool IsExternal { get; }

    /// <summary>If set, this function is an interrupt handler placed at the given vector address.</summary>
    public int? InterruptVector { get; set; }

    /// <summary>Basic blocks in program order; empty for external functions.</summary>
    public List<IrBasicBlock> Blocks { get; } = [];

    public IrFunction(
        string name,
        IrType returnType,
        IReadOnlyList<IrParameter> parameters,
        int? bank = null,
        bool isExternal = false
    )
    {
        Name = name;
        ReturnType = returnType;
        Parameters = parameters;
        Bank = bank;
        IsExternal = isExternal;
    }

    public IrBasicBlock? EntryBlock => Blocks.Count > 0 ? Blocks[0] : null;

    /// <summary>Create a block, append it to this function, and return it.</summary>
    public IrBasicBlock AppendBlock(string? name = null)
    {
        var block = new IrBasicBlock(this) { Name = name };
        Blocks.Add(block);
        return block;
    }
}

/// <summary>A basic block: a straight-line instruction sequence ending in a terminator.</summary>
public sealed class IrBasicBlock
{
    public string? Name { get; set; }
    public IrFunction Parent { get; }
    public List<IrInstruction> Instructions { get; } = [];

    public IrBasicBlock(IrFunction parent) => Parent = parent;

    /// <summary>The block's terminator if the last instruction is one, else null.</summary>
    public IrInstruction? Terminator =>
        Instructions.Count > 0 && Instructions[^1].IsTerminator ? Instructions[^1] : null;
}

/// <summary>A module-level global variable, placed by address space (and optionally bank/section).</summary>
public sealed class IrGlobal
{
    public string Name { get; }
    public IrType Type { get; }
    public AddressSpace AddressSpace { get; }
    public int? Bank { get; }
    public string? Section { get; }

    /// <summary>
    /// A pinned absolute address, overriding address-space placement. Used to bind a global to a
    /// memory-mapped hardware register (e.g. LCDC at 0xFF40), so load/store become MMIO.
    /// </summary>
    public int? FixedAddress { get; }

    /// <summary>Initial bytes for ROM globals; null for uninitialized RAM globals.</summary>
    public byte[]? Initializer { get; }

    public IrGlobal(
        string name,
        IrType type,
        AddressSpace addressSpace,
        int? bank = null,
        string? section = null,
        byte[]? initializer = null,
        int? fixedAddress = null
    )
    {
        Name = name;
        Type = type;
        AddressSpace = addressSpace;
        Bank = bank;
        Section = section;
        Initializer = initializer;
        FixedAddress = fixedAddress;
    }
}
