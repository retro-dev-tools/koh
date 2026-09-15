using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Koh.Compiler.Frontends.Cil;

/// <summary>
/// Closed-world delegate-target registry — the stored-delegate half of the ideal-game-API
/// program's dispatch machinery (enabler E3; the virtual-call half is <see cref="CilVirtualDispatch"/>).
///
/// A delegate whose provenance survives to its <c>Invoke</c> stays a zero-cost direct call (the
/// long-standing fast path LINQ lambdas ride). One that CROSSES a boundary — passed as an
/// argument, stored in a field, returned — is MATERIALIZED at the crossing into a 3-byte arena
/// blob <c>[u8 targetId][u16 env]</c>, and an untraceable <c>Invoke</c> loads the id and switches
/// over this registry's targets for that delegate type, one DIRECT call per arm (same
/// closed-world stance as tag dispatch: no indirect calls, every backend analysis keeps working).
///
/// Built by a pure-metadata pre-pass over the game module and every resolvable non-BCL reference
/// (the same reach as <see cref="CilIntrinsicIndex"/>): every <c>ldftn M ; newobj D::.ctor</c>
/// pair registers M under delegate type D with a dense id — BEFORE any body lowers, so an
/// <c>Invoke</c> lowered early still dispatches over targets created only in bodies lowered later.
/// Targets are keyed per DELEGATE TYPE full name (generic instantiations included), which also
/// guarantees every arm shares the invoke's exact signature.
/// </summary>
internal sealed class CilDelegateRegistry
{
    private readonly Dictionary<string, List<(byte Id, MethodDefinition Target)>> _byDelegateType =
        new(StringComparer.Ordinal);
    private readonly Dictionary<(string DelegateType, MethodDefinition Target), byte> _idOf = new();

    public static CilDelegateRegistry Build(ModuleDefinition gameModule)
    {
        var registry = new CilDelegateRegistry();
        var visitedModules = new HashSet<ModuleDefinition>();
        var visitedAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<ModuleDefinition>();
        pending.Enqueue(gameModule);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (!visitedModules.Add(current))
                continue;
            foreach (var type in current.GetTypes())
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                    continue;
                foreach (var instr in method.Body.Instructions)
                {
                    if (instr.OpCode.Code != Code.Ldftn)
                        continue;
                    if (
                        instr.Next is not { OpCode.Code: Code.Newobj } newobj
                        || newobj.Operand is not MethodReference ctor
                        || CilModuleLowerer.ResolveSafe(ctor.DeclaringType) is not { } delegateDef
                        || !CilModuleLowerer.IsDelegateType(delegateDef)
                    )
                        continue;
                    if (
                        instr.Operand is not MethodReference targetRef
                        || targetRef.Resolve() is not { } target
                    )
                        continue;
                    registry.Register(ctor.DeclaringType.FullName, target);
                }
            }
            foreach (var assemblyRef in current.AssemblyReferences)
            {
                if (IsBclAssemblyName(assemblyRef.Name))
                    continue;
                if (!visitedAssemblyNames.Add(assemblyRef.FullName))
                    continue;
                AssemblyDefinition? resolved;
                try
                {
                    resolved = current.AssemblyResolver.Resolve(assemblyRef);
                }
                catch (AssemblyResolutionException)
                {
                    resolved = null;
                }
                if (resolved is null)
                    continue;
                foreach (var referencedModule in resolved.Modules)
                    pending.Enqueue(referencedModule);
            }
        }
        return registry;
    }

    private void Register(string delegateTypeFullName, MethodDefinition target)
    {
        if (_idOf.ContainsKey((delegateTypeFullName, target)))
            return;
        if (!_byDelegateType.TryGetValue(delegateTypeFullName, out var list))
            _byDelegateType[delegateTypeFullName] = list = new List<(byte, MethodDefinition)>();
        if (list.Count > byte.MaxValue)
            return; // beyond a byte tag — untraceable invokes of this type stay diagnostics
        var id = (byte)list.Count;
        list.Add((id, target));
        _idOf[(delegateTypeFullName, target)] = id;
    }

    /// <summary>The blob id a materialization site stores for (delegate type, target).</summary>
    public bool TryGetId(string delegateTypeFullName, MethodDefinition target, out byte id) =>
        _idOf.TryGetValue((delegateTypeFullName, target), out id);

    /// <summary>Every registered target of a delegate type — the arms of an untraceable Invoke.</summary>
    public bool TryGetTargets(
        string delegateTypeFullName,
        out List<(byte Id, MethodDefinition Target)> targets
    ) => _byDelegateType.TryGetValue(delegateTypeFullName, out targets!);

    private static bool IsBclAssemblyName(string name) =>
        name is "System.Private.CoreLib" or "mscorlib" or "netstandard"
        || name.StartsWith("System.", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.", StringComparison.Ordinal);
}
