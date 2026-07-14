using Mono.Cecil;

namespace Koh.Compiler.Frontends.Cil;

/// <summary>
/// Indexes every <c>[KohRuntime(key)]</c>-carrying method reachable from a module: its own types, plus
/// the types of every referenced assembly the module's resolver can resolve — the mirror of
/// <see cref="CilIntrinsicIndex"/> (that one indexes "the compiler implements this",
/// <c>MethodDefinition -&gt; (kind, address)</c>; this one indexes "the ROM implements this",
/// <c>key -&gt; MethodDefinition</c>). Matched by the attribute type's SIMPLE NAME
/// ("KohRuntimeAttribute") — <c>Koh.Compiler</c> must never reference <c>Koh.GameBoy</c>, so this
/// cannot check the attribute's declaring assembly/namespace, same reasoning as
/// <see cref="CilIntrinsicIndex"/>.
///
/// Used for float/double IL routing (<c>CilMethodLowerer</c>'s <c>add</c>/<c>sub</c>/<c>mul</c>/
/// <c>div</c>/compare/<c>conv.r4</c>/<c>conv.r8</c> handling on <c>float32</c>/<c>float64</c> operands):
/// each such operation resolves its <c>"f32.*"</c>/<c>"f64.*"</c> key through this index rather than
/// ever hardcoding a routine name, so the vocabulary lives entirely in <c>[KohRuntime]</c> metadata
/// (see <c>Koh.GameBoy.SoftFloat</c>).
/// </summary>
public static class CilRuntimeIndex
{
    /// <summary>
    /// Walks <paramref name="module"/> and every referenced module its own
    /// <see cref="ModuleDefinition.AssemblyResolver"/> can resolve (transitively — a referenced assembly
    /// may itself reference another), returning every <c>[KohRuntime(key)]</c>-tagged method keyed by
    /// its key string. An assembly reference that fails to resolve (not on disk, not needed by the
    /// program) is silently skipped rather than treated as an error here — that is a lowering-time
    /// concern, not an indexing one. Two methods sharing the same key (a metadata authoring mistake —
    /// the vocabulary is meant to be one routine per key) resolve to whichever is indexed last; nothing
    /// in this frontend currently defines more than one.
    /// </summary>
    public static IReadOnlyDictionary<string, MethodDefinition> Build(ModuleDefinition module)
    {
        var result = new Dictionary<string, MethodDefinition>();
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
        Dictionary<string, MethodDefinition> result
    )
    {
        // GetTypes() is already flattened (includes nested types), so no manual recursion is needed.
        foreach (var type in module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (TryGetKey(method.CustomAttributes, out var key))
                    result[key] = method;
            }
        }
    }

    private static bool TryGetKey(
        Mono.Collections.Generic.Collection<CustomAttribute> attributes,
        out string key
    )
    {
        foreach (var attribute in attributes)
        {
            if (attribute.AttributeType.Name != "KohRuntimeAttribute")
                continue;
            key = (string)attribute.ConstructorArguments[0].Value;
            return true;
        }
        key = "";
        return false;
    }
}
