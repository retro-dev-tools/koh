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
    /// <c>Max&lt;byte&gt;</c> becomes <c>Max__g1_4_byte</c>. Both the synthesized function and its call
    /// sites use it.
    ///
    /// The name must be a legal C# identifier: a later migration phase moves monomorphized instances out
    /// of detached syntax and into a second, constructed compilation unit that Roslyn actually binds, and
    /// a real <c>MethodDeclarationSyntax</c> identifier has to parse as one — a punctuation-bearing name
    /// like the pre-migration <c>Max$1_4_byte</c> never could.
    ///
    /// Scheme: <c>name + "__g" + argCount + ("_" + enc.Length + "_" + enc)*</c>, where <c>enc</c> is
    /// <see cref="EncodeTypeArg"/> applied to each type argument's own source text
    /// (<c>TypeSyntax.ToString()</c>). This is injective:
    /// - Each encoded argument is prefixed by the *encoded* text's own length, so two distinct argument
    ///   lists can never alias by picking a different split point — one argument <c>A_B</c> vs two
    ///   arguments <c>A</c>, <c>B</c> land at different lengths/offsets and can't collide the way a plain
    ///   <c>_</c>-join would.
    /// - <see cref="EncodeTypeArg"/> doubles every literal <c>_</c> in the source text, which disambiguates
    ///   a real underscore from a hex escape: a hex escape is always a single <c>_</c> followed by exactly
    ///   two hex digits, a shape doubled underscores can never produce, so the encoded stream (and hence
    ///   the length prefix computed from it) has one unambiguous reading.
    ///
    /// Collisions with a user-written identifier are possible in principle (nothing stops a user from
    /// literally naming a method <c>Max__g1_4_byte</c>) but never silent: two functions would then share
    /// one mangled name, and Pass 1's existing duplicate-function-name diagnostic fires — the same loud
    /// failure as any other accidental name clash in the program.</summary>
    internal static string MangleGeneric(string name, IEnumerable<TypeSyntax> typeArgs)
    {
        var args = typeArgs.ToList();
        var sb = new System.Text.StringBuilder(name).Append("__g").Append(args.Count);
        foreach (var t in args)
        {
            var enc = EncodeTypeArg(t.ToString());
            sb.Append('_').Append(enc.Length).Append('_').Append(enc);
        }
        return sb.ToString();
    }

    /// <summary>Injectively sanitizes a type argument's source text into a legal fragment of a C#
    /// identifier: <c>[A-Za-z0-9]</c> passes through unchanged, a literal <c>_</c> doubles to <c>__</c>,
    /// and any other character (<c>*</c>, <c>[</c>, <c>]</c>, <c>&lt;</c>, <c>&gt;</c>, <c>.</c>, <c>,</c>,
    /// space, ...) becomes <c>_</c> followed by its char code as lowercase 2-hex (so <c>byte*</c> becomes
    /// <c>byte_2a</c>); a char above <c>0xFF</c> (a non-Latin identifier letter) gets a distinct
    /// <c>_u</c> + 4-hex escape, since letting the 2-hex form grow to 4 digits would be ambiguous — the
    /// digits are legal pass-through characters, so <c>_1234</c> must always mean escape(0x12) then
    /// literal <c>34</c>. See <see cref="MangleGeneric"/> for why this must be injective and how the
    /// underscore-doubling makes hex escapes unambiguous.</summary>
    internal static string EncodeTypeArg(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in s)
        {
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9')
                sb.Append(c);
            else if (c == '_')
                sb.Append("__");
            else if (c <= 0xFF)
                sb.Append('_').Append(((int)c).ToString("x2"));
            else
                sb.Append("_u").Append(((int)c).ToString("x4"));
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
