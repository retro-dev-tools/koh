using Mono.Cecil;

namespace Koh.Compiler.Frontends.Cil;

/// <summary>
/// Indexes every <c>[KohIntrinsic(kind, address)]</c>-carrying member reachable from a module: its
/// own types, plus the types of every referenced assembly the module's resolver can resolve. Matched
/// by the attribute type's SIMPLE NAME ("KohIntrinsicAttribute") — <c>Koh.Compiler</c> must never
/// reference <c>Koh.GameBoy</c>, so this cannot check the attribute's declaring assembly/namespace.
///
/// The attribute (see <c>Koh.GameBoy.KohIntrinsicAttribute</c>) targets <c>Property | Method</c>. On
/// a property, Roslyn (and the CLR) place the attribute on the <see cref="PropertyDefinition"/>
/// itself, not on its accessor methods — so both the getter and the setter (when present) are
/// recorded here under the property's one <see cref="Entry"/>, keyed by their own
/// <see cref="MethodDefinition"/> (the unit the backend/lowering actually calls into).
/// </summary>
public static class CilIntrinsicIndex
{
    /// <summary>An intrinsic's (kind, address) pair, as declared on <c>[KohIntrinsic(kind, address)]</c>.</summary>
    public readonly record struct Entry(string Kind, int Address);

    /// <summary>
    /// Walks <paramref name="module"/> and every referenced module its own
    /// <see cref="ModuleDefinition.AssemblyResolver"/> can resolve (transitively — a referenced
    /// assembly may itself reference another), returning every intrinsic-carrying method keyed by
    /// its <see cref="MethodDefinition"/>. An assembly reference that fails to resolve (not on disk,
    /// not needed by the program) is silently skipped rather than treated as an error here — that is
    /// a lowering-time concern, not an indexing one.
    /// </summary>
    public static IReadOnlyDictionary<MethodDefinition, Entry> Build(ModuleDefinition module)
    {
        var result = new Dictionary<MethodDefinition, Entry>();
        var visitedModules = new HashSet<ModuleDefinition>();
        var visitedAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<ModuleDefinition>();
        pending.Enqueue(module);

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (!visitedModules.Add(current))
                continue;

            IndexModule(current, result);

            foreach (var assemblyRef in current.AssemblyReferences)
            {
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

        return result;
    }

    private static void IndexModule(
        ModuleDefinition module,
        Dictionary<MethodDefinition, Entry> result
    )
    {
        // GetTypes() is already flattened (includes nested types), so no manual recursion is needed.
        foreach (var type in module.GetTypes())
        {
            foreach (var property in type.Properties)
            {
                if (!TryGetEntry(property.CustomAttributes, out var entry))
                    continue;
                if (property.GetMethod is { } getter)
                    result[getter] = entry;
                if (property.SetMethod is { } setter)
                    result[setter] = entry;
            }

            foreach (var method in type.Methods)
            {
                // Accessor methods are indexed above, from the owning property's attribute — a plain
                // ordinary [KohIntrinsic] method (Halt, EnableInterrupts, …) is handled here.
                if (method.IsGetter || method.IsSetter)
                    continue;
                if (TryGetEntry(method.CustomAttributes, out var entry))
                    result[method] = entry;
            }
        }
    }

    private static bool TryGetEntry(
        Mono.Collections.Generic.Collection<CustomAttribute> attributes,
        out Entry entry
    )
    {
        foreach (var attribute in attributes)
        {
            if (attribute.AttributeType.Name != "KohIntrinsicAttribute")
                continue;
            var args = attribute.ConstructorArguments;
            var kind = (string)args[0].Value;
            var address = args.Count > 1 ? Convert.ToInt32(args[1].Value) : -1;
            entry = new Entry(kind, address);
            return true;
        }
        entry = default;
        return false;
    }
}
