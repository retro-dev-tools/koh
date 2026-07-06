using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koh.Compiler.Frontends.CSharp;

// Monomorphization: generic methods are specialized per concrete instantiation before lowering.
public sealed partial class CSharpFrontend
{
    /// <summary>Rewrites a generic method's syntax, replacing each type-parameter name with the concrete
    /// type it is instantiated at (monomorphization).</summary>
    private sealed class TypeParamRewriter : CSharpSyntaxRewriter
    {
        private readonly IReadOnlyDictionary<string, TypeSyntax> _map;
        public TypeParamRewriter(IReadOnlyDictionary<string, TypeSyntax> map) => _map = map;
        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node) =>
            _map.TryGetValue(node.Identifier.Text, out var t) ? t.WithTriviaFrom(node) : base.VisitIdentifierName(node);
    }

    /// <summary>The mangled name of a generic method instantiated at concrete type arguments, e.g.
    /// <c>Max&lt;int&gt;</c> becomes <c>Max$int</c>. Both the synthesized function and its call sites use it.</summary>
    internal static string MangleGeneric(string name, IEnumerable<TypeSyntax> typeArgs) =>
        name + "$" + string.Join("_", typeArgs.Select(t => t.ToString()));

    private static IEnumerable<(string Name, TypeArgumentListSyntax Args, InvocationExpressionSyntax Node)>
        FindGenericInvocations(SyntaxNode node) =>
        node.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(inv => inv.Expression is GenericNameSyntax)
            .Select(inv => (((GenericNameSyntax)inv.Expression).Identifier.Text,
                            ((GenericNameSyntax)inv.Expression).TypeArgumentList, inv));

    /// <summary>Monomorphize: for every generic method invoked with concrete type arguments, synthesize
    /// a specialized copy (type parameters substituted, mangled name). A work-list handles transitive
    /// instantiation — a specialized body may name further generic instances.</summary>
    private static List<MethodDeclarationSyntax> SynthesizeGenericInstances(
        CompilationUnitSyntax root, IReadOnlyDictionary<(string Name, int Arity), MethodDeclarationSyntax> generics)
    {
        var done = new Dictionary<string, MethodDeclarationSyntax>(StringComparer.Ordinal);
        var work = new Queue<(string Name, TypeArgumentListSyntax Args)>();
        var templates = new HashSet<MethodDeclarationSyntax>(generics.Values);

        // Seed from concrete instantiations only — invocations inside a generic template still name
        // type parameters (e.g. Id<T>), which become concrete once the template is specialized.
        foreach (var (name, args, node) in FindGenericInvocations(root))
            if (generics.ContainsKey((name, args.Arguments.Count))
                && !node.Ancestors().OfType<MethodDeclarationSyntax>().Any(templates.Contains))
                work.Enqueue((name, args));

        while (work.Count > 0)
        {
            var (name, args) = work.Dequeue();
            var mangled = MangleGeneric(name, args.Arguments);
            if (done.ContainsKey(mangled))
                continue;
            var template = generics[(name, args.Arguments.Count)];
            var map = new Dictionary<string, TypeSyntax>(StringComparer.Ordinal);
            var tps = template.TypeParameterList!.Parameters;
            for (int i = 0; i < tps.Count && i < args.Arguments.Count; i++)
                map[tps[i].Identifier.Text] = args.Arguments[i];

            var specialized = (MethodDeclarationSyntax)new TypeParamRewriter(map).Visit(template)!;
            specialized = specialized
                .WithIdentifier(SyntaxFactory.Identifier(mangled))
                .WithTypeParameterList(null);
            done[mangled] = specialized;

            // The specialized body's generic invocations are now concrete; instantiate them too.
            foreach (var (n2, a2, _) in FindGenericInvocations(specialized))
                if (generics.ContainsKey((n2, a2.Arguments.Count))) work.Enqueue((n2, a2));
        }
        return done.Values.ToList();
    }
}
