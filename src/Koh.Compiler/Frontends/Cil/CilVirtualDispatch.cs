using Mono.Cecil;

namespace Koh.Compiler.Frontends.Cil;

/// <summary>
/// Closed-world virtual-dispatch index — compiler enabler E2 of the ideal-game-API program
/// (<c>docs/superpowers/specs/2026-07-19-ideal-game-api-design.md</c>). Scans the game module and
/// every resolvable referenced non-BCL assembly (the same reach as <see cref="CilIntrinsicIndex"/> —
/// a hierarchy ROOT like the framework's <c>Scene</c> lives in Koh.GameBoy while its subclasses live
/// in the game module) and, for every class hierarchy that has virtual/abstract members, assigns
/// each CONCRETE class a dense byte tag. <c>newobj</c> stores the tag at offset 0 of the instance
/// (the hierarchy root's layout reserves the byte — see <see cref="CilClassLayout.Compute"/>), and a
/// <c>callvirt</c> that cannot be devirtualized by receiver tracking lowers to a tag load plus a
/// jump-table <c>switch</c> of DIRECT calls, one arm per implementation
/// (<c>CilMethodLowerer.Delegates.cs</c>'s <c>EmitTagDispatch</c>).
///
/// Chosen over a true indirect-call IR op deliberately: every backend soundness analysis (Tarjan
/// recursion detection, interrupt-reentrancy, dead-function pruning, inlining, banking's far-call
/// thunk routing) is keyed to direct <c>CallInstruction</c>s, and callee arguments are written into
/// callee-specific static WRAM slots — tag+switch keeps all of it working with zero backend changes.
///
/// A hierarchy is INCLUDED when any type in it declares a virtual method (an abstract member is
/// virtual in metadata). A hierarchy whose base chain leaves the closed world (an unresolvable or
/// BCL base other than <c>System.Object</c>) is skipped — a virtual call into it stays the existing
/// diagnostic. Interfaces, value types, enums, and delegate types never participate; a sealed
/// single-class "hierarchy" with no virtuals (display classes, iterator state machines) is not
/// tagged, so the existing <c>_pendingConcreteType</c> fast path and iterator devirtualization are
/// untouched.
/// </summary>
internal sealed class CilVirtualDispatch
{
    private readonly Dictionary<TypeDefinition, byte> _tags = new();
    private readonly HashSet<TypeDefinition> _taggedRoots = new();

    // Every class in a tagged hierarchy -> that hierarchy's root; and root -> concrete members in
    // tag order (index == tag).
    private readonly Dictionary<TypeDefinition, TypeDefinition> _rootOf = new();
    private readonly Dictionary<TypeDefinition, List<TypeDefinition>> _concreteOf = new();

    // The same maps over EVERY closed-world hierarchy, tagged or not — powering the
    // "provably no instance can exist" query (an abstract framework base like Scene that this
    // particular game never derives: a virtual call through it is statically present in shared
    // framework code but dynamically dead, since nothing instantiable is assignable to it).
    private readonly Dictionary<TypeDefinition, TypeDefinition> _rootOfAll = new();
    private readonly Dictionary<TypeDefinition, List<TypeDefinition>> _concreteOfAll = new();

    public static CilVirtualDispatch Build(ModuleDefinition gameModule)
    {
        var index = new CilVirtualDispatch();

        // Enumerate the closed world: the game module plus resolvable non-BCL references,
        // transitively (mirrors CilIntrinsicIndex.Build).
        var classes = new List<TypeDefinition>();
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
            {
                if (
                    type.IsClass
                    && !type.IsValueType
                    && !type.IsInterface
                    && !CilModuleLowerer.IsDelegateType(type)
                )
                    classes.Add(type);
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

        // Group every class under its chain root (the topmost base below System.Object). A chain
        // that leaves the closed world is dropped whole.
        var membersOf = new Dictionary<TypeDefinition, List<TypeDefinition>>();
        foreach (var type in classes)
        {
            var root = ChainRoot(type);
            if (root is null)
                continue;
            if (!membersOf.TryGetValue(root, out var members))
                membersOf[root] = members = new List<TypeDefinition>();
            members.Add(type);
        }

        foreach (var (root, members) in membersOf)
        {
            var allConcretes = members.Where(m => !m.IsAbstract).ToList();
            index._concreteOfAll[root] = allConcretes;
            foreach (var member in members)
                index._rootOfAll[member] = root;

            // Only hierarchies where dispatch can happen: >1 member (or an abstract root someone
            // derives) AND at least one virtual member anywhere in the hierarchy.
            if (members.Count < 2)
                continue;
            if (!members.Any(HasVirtualMember))
                continue;

            var concretes = members
                .Where(m => !m.IsAbstract)
                .OrderBy(m => m.FullName, StringComparer.Ordinal) // deterministic tag order
                .ToList();
            if (concretes.Count == 0 || concretes.Count > byte.MaxValue)
                continue;

            index._taggedRoots.Add(root);
            index._concreteOf[root] = concretes;
            foreach (var member in members)
                index._rootOf[member] = root;
            for (var i = 0; i < concretes.Count; i++)
                index._tags[concretes[i]] = (byte)i;
        }

        return index;
    }

    /// <summary>True when <paramref name="root"/> is a tagged hierarchy's root — its layout must
    /// reserve the tag byte at offset 0 (derived layouts inherit it through the base prefix).</summary>
    public bool NeedsTagByte(TypeDefinition root) => _taggedRoots.Contains(root);

    /// <summary>The tag <c>newobj</c> must store for a concrete class, if it has one.</summary>
    public bool TryGetTag(TypeDefinition concrete, out byte tag) =>
        _tags.TryGetValue(concrete, out tag);

    /// <summary>True when NO instantiable class in the closed world is assignable to
    /// <paramref name="staticReceiverType"/> — a virtual call through such a receiver can never
    /// execute (the receiver is necessarily null), so its call site is dynamically dead code.
    /// Also true for a class the scan never saw as a hierarchy member at all when it is abstract
    /// (an underived abstract framework base).</summary>
    public bool HasNoConcreteImplementations(TypeDefinition staticReceiverType)
    {
        if (_rootOfAll.TryGetValue(staticReceiverType, out var root))
            return !_concreteOfAll[root].Any(c => IsAssignableTo(c, staticReceiverType));
        // Not a member of any multi-class hierarchy: a single standalone abstract class has no
        // possible instances either.
        return staticReceiverType.IsAbstract;
    }

    /// <summary>All (tag, concrete class) pairs a virtual call through a receiver statically typed
    /// as <paramref name="staticReceiverType"/> could reach — the concrete members of the receiver's
    /// hierarchy that are assignable to it. Empty list (false) when the receiver is not in a tagged
    /// hierarchy.</summary>
    public bool TryGetDispatchTargets(
        TypeDefinition staticReceiverType,
        out List<(byte Tag, TypeDefinition Concrete)> targets
    )
    {
        targets = null!;
        if (!_rootOf.TryGetValue(staticReceiverType, out var root))
            return false;
        targets = new List<(byte, TypeDefinition)>();
        var concretes = _concreteOf[root];
        for (var i = 0; i < concretes.Count; i++)
            if (IsAssignableTo(concretes[i], staticReceiverType))
                targets.Add(((byte)i, concretes[i]));
        return true;
    }

    private static bool IsAssignableTo(TypeDefinition type, TypeDefinition baseCandidate)
    {
        for (TypeDefinition? t = type; t is not null; t = ResolveBase(t))
            if (t == baseCandidate)
                return true;
        return false;
    }

    /// <summary>The topmost base of <paramref name="type"/> below <c>System.Object</c>, or the type
    /// itself when it derives straight from Object — null when the chain leaves the closed world
    /// (unresolvable, or a non-Object BCL base).</summary>
    private static TypeDefinition? ChainRoot(TypeDefinition type)
    {
        var current = type;
        while (true)
        {
            var baseRef = current.BaseType;
            if (baseRef is null)
                return null; // System.Object itself, or an interface — never a hierarchy member
            if (baseRef.FullName == "System.Object")
                return current;
            var baseDef = CilModuleLowerer.ResolveSafe(baseRef);
            if (baseDef is null || IsBclAssemblyName(baseDef.Module.Assembly.Name.Name))
                return null;
            current = baseDef;
        }
    }

    private static TypeDefinition? ResolveBase(TypeDefinition type) =>
        type.BaseType is { } baseRef && baseRef.FullName != "System.Object"
            ? CilModuleLowerer.ResolveSafe(baseRef)
            : null;

    private static bool HasVirtualMember(TypeDefinition type) =>
        type.Methods.Any(m => m.IsVirtual && !m.IsConstructor);

    private static bool IsBclAssemblyName(string name) =>
        name is "System.Private.CoreLib" or "mscorlib" or "netstandard"
        || name.StartsWith("System.", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.", StringComparison.Ordinal);
}
