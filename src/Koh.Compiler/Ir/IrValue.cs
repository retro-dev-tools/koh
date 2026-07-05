namespace Koh.Compiler.Ir;

/// <summary>
/// Base of everything that can appear as an instruction operand: constants, function
/// parameters, global references, and (since instructions produce their result) instructions
/// themselves. Values are typed; SSA "definitions" are the value objects, referenced by
/// identity.
/// </summary>
public abstract class IrValue
{
    public IrType Type { get; }

    /// <summary>
    /// Optional symbolic name (without sigil). When null, the printer assigns a numbered slot.
    /// </summary>
    public string? Name { get; set; }

    protected IrValue(IrType type) => Type = type;
}

/// <summary>An integer constant of a given width.</summary>
public sealed class IrConstInt : IrValue
{
    public long Value { get; }

    public IrConstInt(IrType type, long value) : base(type) => Value = value;
}

/// <summary>A function parameter — an SSA value defined on entry.</summary>
public sealed class IrParameter : IrValue
{
    public IrParameter(string name, IrType type) : base(type) => Name = name;
}

/// <summary>
/// A reference to a module global, yielding its address. The value's type is a pointer to the
/// global's type in the global's address space.
/// </summary>
public sealed class IrGlobalRef : IrValue
{
    public IrGlobal Global { get; }

    public IrGlobalRef(IrGlobal global)
        : base(IrType.Pointer(global.Type, global.AddressSpace)) => Global = global;
}
