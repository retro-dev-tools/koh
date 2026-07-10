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
            _map.TryGetValue(node.Identifier.Text, out var t)
                ? t.WithTriviaFrom(node)
                : base.VisitIdentifierName(node);
    }

    /// <summary>The mangled name of a generic method instantiated at concrete type arguments, e.g.
    /// <c>Max&lt;int&gt;</c> becomes <c>Max$1i3int</c>. Both the synthesized function and its call sites
    /// use it. Each argument is length-prefixed so distinct argument lists can't alias (e.g. one arg
    /// <c>A_B</c> vs two args <c>A</c>,<c>B</c>), which a plain <c>_</c>-join would collide.</summary>
    internal static string MangleGeneric(string name, IEnumerable<TypeSyntax> typeArgs)
    {
        var sb = new System.Text.StringBuilder(name).Append('$');
        var args = typeArgs.ToList();
        sb.Append(args.Count);
        foreach (var t in args)
        {
            var s = t.ToString();
            sb.Append('_').Append(s.Length).Append('_').Append(s);
        }
        return sb.ToString();
    }

    private static IEnumerable<(
        string? Receiver,
        string Name,
        TypeArgumentListSyntax Args,
        InvocationExpressionSyntax Node
    )> FindGenericInvocations(SyntaxNode node) =>
        node.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(inv =>
                inv.Expression switch
                {
                    // A bare `M<...>(...)` call.
                    GenericNameSyntax g => (
                        (string?)null,
                        g.Identifier.Text,
                        g.TypeArgumentList,
                        inv
                    ),
                    // A qualified `Class.M<...>(...)` call.
                    MemberAccessExpressionSyntax
                    {
                        Name: GenericNameSyntax g,
                        Expression: IdentifierNameSyntax r
                    } => (r.Identifier.Text, g.Identifier.Text, g.TypeArgumentList, inv),
                    _ => default,
                }
            )
            .Where(t => t.Item2 is not null);

    /// <summary>The enclosing top-level user <c>static class</c> of a node, or null at the wrapper level.</summary>
    private static string? EnclosingStaticClass(SyntaxNode node) =>
        node.Ancestors()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(IsProgramStaticClass)
            ?.Identifier.Text;

    /// <summary>Monomorphize: for every generic method invoked with concrete type arguments, synthesize
    /// a specialized copy (type parameters substituted, mangled name). A work-list handles transitive
    /// instantiation — a specialized body may name further generic instances. Instances are named per
    /// declaring class, so same-named generics in different static classes stay distinct.</summary>
    private static List<MethodDeclarationSyntax> SynthesizeGenericInstances(
        CompilationUnitSyntax root,
        IReadOnlyDictionary<
            (string? Class, string Name, int Arity),
            MethodDeclarationSyntax
        > generics
    )
    {
        if (generics.Count == 0)
            return []; // no generic templates — skip the invocation scan entirely (the common program)

        var done = new Dictionary<string, MethodDeclarationSyntax>(StringComparer.Ordinal);
        var work = new Queue<(MethodDeclarationSyntax Template, TypeArgumentListSyntax Args)>();
        var templates = new HashSet<MethodDeclarationSyntax>(generics.Values);

        // Resolve an invocation to its template: a qualified `Recv.M<..>` selects Recv's method; a bare
        // `M<..>` prefers the enclosing static class's method, else a top-level one.
        MethodDeclarationSyntax? Resolve(
            string? receiver,
            string? enclosing,
            string name,
            int arity
        ) =>
            receiver is not null
                ? generics.GetValueOrDefault((receiver, name, arity))
                : generics.GetValueOrDefault((enclosing, name, arity))
                    ?? generics.GetValueOrDefault((null, name, arity));

        // Seed from concrete instantiations only — invocations inside a generic template still name
        // type parameters (e.g. Id<T>), which become concrete once the template is specialized.
        foreach (var (receiver, name, args, node) in FindGenericInvocations(root))
            if (
                !node.Ancestors().OfType<MethodDeclarationSyntax>().Any(templates.Contains)
                && Resolve(receiver, EnclosingStaticClass(node), name, args.Arguments.Count)
                    is { } tmpl
            )
                work.Enqueue((tmpl, args));

        while (work.Count > 0)
        {
            var (template, args) = work.Dequeue();
            var owner = ProgramMemberClass(template);
            var mangled = MangleGeneric(template.Identifier.Text, args.Arguments);
            var qualified = owner is { } c ? $"{c}.{mangled}" : mangled;
            if (done.ContainsKey(qualified))
                continue;

            var map = new Dictionary<string, TypeSyntax>(StringComparer.Ordinal);
            var tps = template.TypeParameterList!.Parameters;
            for (int i = 0; i < tps.Count && i < args.Arguments.Count; i++)
                map[tps[i].Identifier.Text] = args.Arguments[i];

            var specialized = (MethodDeclarationSyntax)new TypeParamRewriter(map).Visit(template)!;
            specialized = specialized
                .WithIdentifier(SyntaxFactory.Identifier(mangled))
                .WithTypeParameterList(null);
            // The specialized node is detached from the tree (no Parent), so carry its declaring class on
            // an annotation; ProgramMemberClass reads it to qualify the name and scope sibling calls.
            if (owner is not null)
                specialized = specialized.WithAdditionalAnnotations(
                    new SyntaxAnnotation(DeclaringClassAnnotation, owner)
                );
            done[qualified] = specialized;

            // The specialized body's generic invocations are now concrete; instantiate them too,
            // resolving sibling generics against this template's own class.
            foreach (var (receiver, name, a2, _) in FindGenericInvocations(specialized))
                if (Resolve(receiver, owner, name, a2.Arguments.Count) is { } tmpl2)
                    work.Enqueue((tmpl2, a2));
        }
        return done.Values.ToList();
    }
}
