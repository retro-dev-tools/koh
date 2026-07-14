using Koh.Compiler.Ir;
using Koh.Compiler.Targets;
using Koh.Core.Diagnostics;
using Mono.Cecil;

namespace Koh.Compiler.Frontends.Cil;

/// <summary>
/// Mutable state shared across every <see cref="CilMethodLowerer"/> instance for one module lowering
/// pass: the function/global/layout caches, plus <see cref="EnsureLowered"/> — the on-demand lowering
/// entry point a call site (an instance <c>call</c>/<c>callvirt</c>, a delegate <c>Invoke</c>
/// resolution, or a <c>newobj</c> constructor) uses to lower a compiler-generated type's method
/// (display-class ctor, capturing-lambda body, no-capture-cache singleton method) the first time — and
/// only the first time — something actually references it. See
/// <c>docs/superpowers/specs/2026-07-14-cil-frontend-design.md</c>'s delegates/closures task: nothing
/// eagerly lowers a compiler-generated type's members (unlike phase 1's hand-written statics), so a
/// dead member (e.g. an iterator's boxing accessor, out of THIS task's scope) is never even attempted.
/// </summary>
internal sealed class CilLoweringContext
{
    public IrModule Module { get; }
    public DiagnosticBag Diagnostics { get; }
    public IReadOnlyDictionary<MethodDefinition, CilIntrinsicIndex.Entry> Intrinsics { get; }
    public Dictionary<MethodDefinition, IrFunction> FunctionsByMethod { get; } = new();

    private readonly Dictionary<int, IrGlobal> _registerGlobals = new();
    private readonly Dictionary<int, IrGlobal> _regionGlobals = new();
    private readonly Dictionary<TypeDefinition, CilClassLayout> _classLayouts = new();
    private readonly HashSet<MethodDefinition> _inProgress = new();
    private IrGlobal? _heapGlobal;

    /// <summary>Bump-pointer heap top, WRAM address — identical convention to
    /// <c>Koh.Compiler.Frontends.CSharp.CSharpFrontend.HeapTop</c> (not shared code: the two frontends
    /// compile disjoint modules, so only the convention, not the constant's storage, needs to agree).</summary>
    internal const int HeapTop = 0xDE00;
    internal const string HeapPointerName = "__heap";

    public CilLoweringContext(
        IrModule module,
        DiagnosticBag diagnostics,
        IReadOnlyDictionary<MethodDefinition, CilIntrinsicIndex.Entry> intrinsics
    )
    {
        Module = module;
        Diagnostics = diagnostics;
        Intrinsics = intrinsics;
    }

    public IrGlobal RegisterGlobal(int address)
    {
        if (_registerGlobals.TryGetValue(address, out var g))
            return g;
        g = new IrGlobal(
            $"Hardware.0x{address:X4}",
            IrType.I8,
            AddressSpace.Default,
            fixedAddress: address
        );
        Module.Globals.Add(g);
        _registerGlobals[address] = g;
        return g;
    }

    public IrGlobal RegionGlobal(int address)
    {
        if (_regionGlobals.TryGetValue(address, out var g))
            return g;
        g = new IrGlobal(
            $"Gb.0x{address:X4}",
            IrType.I8,
            AddressSpace.Default,
            fixedAddress: address
        );
        Module.Globals.Add(g);
        _regionGlobals[address] = g;
        return g;
    }

    public CilClassLayout GetLayout(TypeDefinition type)
    {
        if (_classLayouts.TryGetValue(type, out var layout))
            return layout;
        layout = CilClassLayout.Compute(type);
        _classLayouts[type] = layout;
        return layout;
    }

    /// <summary>The shared heap-pointer global, created the first time it's needed (see
    /// <c>CilModuleLowerer.NeedsHeap</c>'s pre-scan) — its <c>HeapTop</c> initializer is emitted once,
    /// in the entry function's prologue, by <see cref="CilMethodLowerer"/> when <c>isEntry</c> and this
    /// property is non-null.</summary>
    public IrGlobal EnsureHeapGlobal()
    {
        if (_heapGlobal is { } existing)
            return existing;
        var heap = new IrGlobal(HeapPointerName, IrType.I16, AddressSpace.Wram);
        Module.Globals.Add(heap);
        _heapGlobal = heap;
        return heap;
    }

    public IrGlobal? HeapGlobal => _heapGlobal;

    /// <summary>Signature only (Pass 1 of the eager, hand-written-static-method sweep) — adds
    /// <paramref name="method"/> to <see cref="FunctionsByMethod"/> so later calls resolve regardless
    /// of declaration order. A per-method failure reports a diagnostic and leaves the method out (its
    /// body pass is then skipped).</summary>
    public IrFunction? EnsureSignature(MethodDefinition method)
    {
        if (FunctionsByMethod.TryGetValue(method, out var existing))
            return existing;
        try
        {
            var parameters = new List<IrParameter>();
            if (method.HasThis)
            {
                var (thisType, _) = CilTypeMapper.Map(method.DeclaringType);
                parameters.Add(new IrParameter("this", thisType));
            }
            for (var i = 0; i < method.Parameters.Count; i++)
            {
                var p = method.Parameters[i];
                var (irType, _) = CilTypeMapper.Map(p.ParameterType);
                parameters.Add(new IrParameter(p.Name ?? $"arg{i}", irType));
            }
            var (returnType, _) = CilTypeMapper.Map(method.ReturnType);
            var fn = new IrFunction(
                $"{method.DeclaringType.Name}.{method.Name}",
                returnType,
                parameters
            );
            Module.Functions.Add(fn);
            FunctionsByMethod[method] = fn;
            return fn;
        }
        catch (CilNotSupportedException ex)
        {
            Diagnostics.Report(default, ex.Message, DiagnosticSeverity.Error, Module.Name);
            return null;
        }
    }

    /// <summary>Lower <paramref name="method"/> now if it hasn't been already: build its signature
    /// (see <see cref="EnsureSignature"/>) and its body. Used both for the eager Pass-2 sweep (hand-
    /// written statics) and, recursively, for on-demand resolution of a compiler-generated type's
    /// member the first time some call site actually references it (a delegate target, a display-
    /// class ctor, an instance method reached through a devirtualized <c>callvirt</c>). A cycle (a
    /// method whose own body's lowering re-enters this for itself, transitively) reports a diagnostic
    /// and returns the signature-only function rather than recursing forever — not reachable from the
    /// current subset (no recursive closures), but guarded rather than assumed away.</summary>
    public IrFunction? EnsureLowered(MethodDefinition method)
    {
        var fn = EnsureSignature(method);
        if (fn is null)
            return null;
        if (_lowered.Contains(method))
            return fn;
        if (!_inProgress.Add(method))
        {
            Diagnostics.Report(
                default,
                $"'{method.FullName}' cannot be lowered (recursive on-demand resolution).",
                DiagnosticSeverity.Error,
                Module.Name
            );
            return fn;
        }
        try
        {
            LowerBody(method, fn, isEntry: false);
        }
        finally
        {
            _inProgress.Remove(method);
        }
        return fn;
    }

    private readonly HashSet<MethodDefinition> _lowered = new();

    // A method's ret-site(s) concrete-type provenance (see CilMethodLowerer's Code.Ret case) — null
    // means "no known single concrete type" (either genuinely unknown, or two ret sites disagreed).
    // Used by iterator-kickoff/GetEnumerator-shaped devirtualization (see
    // CilMethodLowerer.Iterators.cs): a game-module method's declared return may be an interface, but
    // every ret it actually executes might still trace to the SAME concrete allocation.
    private readonly Dictionary<MethodDefinition, TypeDefinition?> _concreteReturnType = new();

    /// <summary>Record one ret site's concrete-type provenance for <paramref name="method"/> (called
    /// once per <c>ret</c> encountered while lowering its body). The first sighting sets it; a later
    /// sighting that disagrees (a different type, or no provenance at all) downgrades it to "unknown"
    /// — sound, not merely optimistic: every path must agree for the method's result to be trusted as
    /// exactly one concrete type.</summary>
    public void RecordConcreteReturn(MethodDefinition method, TypeDefinition? type)
    {
        if (_concreteReturnType.TryGetValue(method, out var existing))
        {
            if (!ReferenceEquals(existing, type))
                _concreteReturnType[method] = null;
        }
        else
        {
            _concreteReturnType[method] = type;
        }
    }

    /// <summary>The single concrete type every <c>ret</c> in <paramref name="method"/> traces to, or
    /// null if unknown/disagreeing — forces <paramref name="method"/> to be lowered first (idempotent
    /// via <see cref="EnsureLowered"/>) if it hasn't been yet, so this works regardless of whether the
    /// caller or the callee happens to lower first in the eager sweep.</summary>
    public TypeDefinition? GetConcreteReturnType(MethodDefinition method)
    {
        EnsureLowered(method);
        return _concreteReturnType.TryGetValue(method, out var type) ? type : null;
    }

    /// <summary>Lower one method body, reporting a diagnostic (not throwing) on an unsupported
    /// construct so one bad method doesn't sink the whole compile — same containment as
    /// <c>CSharpFrontend</c>'s per-method <c>CSharpNotSupportedException</c> handling.</summary>
    public void LowerBody(MethodDefinition method, IrFunction fn, bool isEntry)
    {
        if (!_lowered.Add(method))
            return;
        try
        {
            new CilMethodLowerer(method, fn, this, isEntry).Run();
        }
        catch (CilNotSupportedException ex)
        {
            Diagnostics.Report(default, ex.Message, DiagnosticSeverity.Error, Module.Name);
        }
    }
}
