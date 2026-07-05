using Koh.Compiler.Targets;

namespace Koh.Compiler.Ir;

/// <summary>
/// A frontend's output and a backend's input: the complete, target-independent program.
/// A module owns its functions and global data. The instruction/SSA-block layer of each
/// function is specified in the design doc and lands in Phase 1 (see
/// <c>docs/superpowers/specs/2026-07-05-csharp-frontend-compiler-platform-design.md</c>);
/// this type establishes the stable module/function/global contract that frontends and
/// backends agree on.
/// </summary>
public sealed class IrModule
{
    public string Name { get; }
    public List<IrFunction> Functions { get; } = [];
    public List<IrGlobal> Globals { get; } = [];

    public IrModule(string name) => Name = name;
}

/// <summary>A named function signature. The SSA body arrives in Phase 1.</summary>
public sealed class IrFunction
{
    public string Name { get; }
    public IrType ReturnType { get; }
    public IReadOnlyList<IrParameter> Parameters { get; }

    /// <summary>ROM bank this function is placed in; null means the linker/backend decides.</summary>
    public int? Bank { get; }

    /// <summary>True for an <c>extern</c> declaration (e.g. an assembly symbol) with no body.</summary>
    public bool IsExternal { get; }

    public IrFunction(
        string name,
        IrType returnType,
        IReadOnlyList<IrParameter> parameters,
        int? bank = null,
        bool isExternal = false)
    {
        Name = name;
        ReturnType = returnType;
        Parameters = parameters;
        Bank = bank;
        IsExternal = isExternal;
    }
}

/// <summary>A function parameter.</summary>
/// <param name="Name">Source name, for diagnostics and debug info.</param>
/// <param name="Type">Parameter type.</param>
public sealed record IrParameter(string Name, IrType Type);

/// <summary>A module-level global variable, placed by address space (and optionally bank/section).</summary>
public sealed class IrGlobal
{
    public string Name { get; }
    public IrType Type { get; }
    public AddressSpace AddressSpace { get; }
    public int? Bank { get; }
    public string? Section { get; }

    /// <summary>Initial bytes for ROM globals; null for uninitialized RAM globals.</summary>
    public byte[]? Initializer { get; }

    public IrGlobal(
        string name,
        IrType type,
        AddressSpace addressSpace,
        int? bank = null,
        string? section = null,
        byte[]? initializer = null)
    {
        Name = name;
        Type = type;
        AddressSpace = addressSpace;
        Bank = bank;
        Section = section;
        Initializer = initializer;
    }
}
